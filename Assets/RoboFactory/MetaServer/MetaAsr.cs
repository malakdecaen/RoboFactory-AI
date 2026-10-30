using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace RoboFactory.MetaServer
{
    /// <summary>
    /// One PUSH_TO_TALK transcription linked to Meta's real-time WebSocket (AsrSession of the Python proxy).
    /// Unity sends PCM chunks (s16le mono 24 kHz); the « pacer » re-emits them in 80 ms frames
    /// paced in real time (Meta closes the session on lag or backlog); the reader collects the
    /// transcriptions.
    /// </summary>
    internal sealed class AsrSession
    {
        public const int Rate = 24000;
        public const int FrameMs = 80;
        public const double TtlSeconds = 180;

        public string Id;
        public readonly bool Mock;
        public readonly string MockText;
        public readonly List<string> Keywords;
        public readonly List<string> Languages;
        public readonly DateTime Created = DateTime.UtcNow;

        public string Partial = "";
        public string FinalText = "";
        public string Error;
        public int AudioMs;
        public int StatusReveal;
        public readonly ManualResetEventSlim FinalEvent = new ManualResetEventSlim(false);
        public readonly ManualResetEventSlim SendDone = new ManualResetEventSlim(false);
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        public readonly ManualResetEventSlim FinishRequested = new ManualResetEventSlim(false);

        private double queuedMs;
        private readonly object sync = new object();
        private readonly ConcurrentQueue<byte[]> inbox = new ConcurrentQueue<byte[]>();
        private ClientWebSocket ws;
        private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);

        public double QueuedMs { get { lock (sync) return queuedMs; } }

        public AsrSession(string id, List<string> keywords, List<string> languages, string key, bool mock = false, string mockText = "")
        {
            Id = id;
            Keywords = keywords ?? new List<string>();
            Languages = languages ?? new List<string>();
            Mock = mock;
            MockText = mockText ?? "";
            if (!mock) Open(key);
        }

        private void Open(string key)
        {
            string url = MetaApi.AsrUrl + (MetaApi.AsrUrl.Contains("?") ? "&" : "?") + "sessionId=" + Id;
            ws = new ClientWebSocket();
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                ws.ConnectAsync(new Uri(url), cts.Token).GetAwaiter().GetResult();
            var handshake = new JObject
            {
                ["authorization"] = new JObject { ["accessToken"] = "Bearer " + key },
                ["audioEncoding"] = "PCM_24KHZ",
                ["model"] = MetaApi.VoiceModel,
                ["mode"] = "PUSH_TO_TALK",
                ["partialMode"] = "CUMULATIVE",
                ["emitAudioProgress"] = true
            };
            if (Keywords.Count > 0) handshake["keywords"] = new JArray(Keywords.GetRange(0, Math.Min(64, Keywords.Count)).ToArray());
            if (Languages.Count > 0) handshake["languageBias"] = new JArray(Languages.ToArray());
            if (!SendText(Py.Dumps(handshake))) throw new IOException("ASR handshake: send failed");
            string ack = ReceiveText(TimeSpan.FromSeconds(15), out string kind);
            if (kind != "text") throw new IOException("ASR handshake: no ack (" + kind + ")");
            JObject ackObj = Py.ParseObject(ack);
            if (ackObj == null || !ackObj.ContainsKey("sessionId")) throw new IOException("ASR handshake rejected: " + Py.Cut(ack, 300));
            string sid = Py.StrOr(Py.Get(ackObj, "sessionId"));
            if (sid.Length > 0) Id = sid;
            new Thread(Pacer) { IsBackground = true, Name = "MetaAsrPacer" }.Start();
            new Thread(Reader) { IsBackground = true, Name = "MetaAsrReader" }.Start();
        }

        // ---- sending (Unity -> Meta)

        private bool Send(ArraySegment<byte> data, WebSocketMessageType type)
        {
            if (ws == null) return false;
            sendLock.Wait();
            try
            {
                if (ws.State != WebSocketState.Open && ws.State != WebSocketState.CloseReceived) return false;
                ws.SendAsync(data, type, true, CancellationToken.None).GetAwaiter().GetResult();
                return true;
            }
            catch (Exception) { return false; }
            finally { sendLock.Release(); }
        }

        private bool SendText(string text) => Send(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text);

        public bool Enqueue(byte[] pcm)
        {
            if (FinishRequested.IsSet || Done.IsSet) return false;
            lock (sync) queuedMs += pcm.Length / (double)(Rate * 2) * 1000.0;
            inbox.Enqueue(pcm);
            return true;
        }

        /// <summary>
        /// Clocked emission at exact real time: without available audio, silence is emitted (Meta closes on
        /// « Ingress audio slower than real-time »); real audio is never reordered or sped up.
        /// </summary>
        private void Pacer()
        {
            int frameBytes = Rate * 2 * FrameMs / 1000;
            double frameS = frameBytes / (double)(Rate * 2);
            var silence = new byte[frameBytes];
            var buf = new List<byte>(frameBytes * 4);
            var clock = Stopwatch.StartNew();
            double nextT = 0;
            try
            {
                while (!Done.IsSet)
                {
                    while (inbox.TryDequeue(out byte[] chunk)) buf.AddRange(chunk);
                    byte[] frame;
                    bool real = true;
                    if (buf.Count >= frameBytes)
                    {
                        frame = buf.GetRange(0, frameBytes).ToArray();
                        buf.RemoveRange(0, frameBytes);
                    }
                    else if (buf.Count > 0)
                    {
                        frame = new byte[frameBytes];
                        buf.CopyTo(frame);
                        buf.Clear();
                    }
                    else if (FinishRequested.IsSet) break;
                    else
                    {
                        frame = silence;
                        real = false;
                    }
                    if (real) lock (sync) queuedMs = Math.Max(0.0, queuedMs - frameS * 1000.0);
                    if (!Send(new ArraySegment<byte>(frame), WebSocketMessageType.Binary)) break;
                    nextT += frameS;
                    double delay = nextT - clock.Elapsed.TotalSeconds;
                    if (delay > 0) Thread.Sleep(TimeSpan.FromSeconds(delay));
                    else if (delay < -.5) nextT = clock.Elapsed.TotalSeconds; // big drop-out: resynchronise
                }
                SendText("{\"type\": \"endStream\"}");
            }
            catch (Exception e)
            {
                if (Error == null) Error = "asr send: " + e.Message;
            }
            finally
            {
                // The reader keeps going: Meta still answers (final transcription + close 1000).
                SendDone.Set();
            }
        }

        // ---- reception (Meta -> Unity)

        /// <summary>A complete message; kind = text | binary | close.</summary>
        private string ReceiveText(TimeSpan? timeout, out string kind)
        {
            var buffer = new byte[16384];
            var ms = new MemoryStream();
            CancellationTokenSource cts = timeout.HasValue ? new CancellationTokenSource(timeout.Value) : null;
            try
            {
                while (true)
                {
                    WebSocketReceiveResult r = ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts?.Token ?? CancellationToken.None)
                                                 .GetAwaiter().GetResult();
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        kind = "close";
                        try
                        {
                            sendLock.Wait();
                            try
                            {
                                if (ws.State == WebSocketState.CloseReceived)
                                    ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).GetAwaiter().GetResult();
                            }
                            finally { sendLock.Release(); }
                        }
                        catch (Exception) { }
                        return (int?)ws.CloseStatus + " " + ws.CloseStatusDescription;
                    }
                    ms.Write(buffer, 0, r.Count);
                    if (r.EndOfMessage)
                    {
                        kind = r.MessageType == WebSocketMessageType.Text ? "text" : "binary";
                        return Encoding.UTF8.GetString(ms.ToArray());
                    }
                }
            }
            catch (OperationCanceledException)
            {
                kind = "timeout";
                return null;
            }
            catch (Exception e)
            {
                kind = "close";
                return "1006 " + e.Message;
            }
            finally { cts?.Dispose(); }
        }

        private void Reader()
        {
            try
            {
                while (!Done.IsSet)
                {
                    string payload = ReceiveText(null, out string kind);
                    if (kind == "close")
                    {
                        Done.Set();
                        break;
                    }
                    if (kind != "text") continue;
                    JObject ev = Py.ParseObject(payload);
                    if (ev == null) continue;
                    string etype = Py.Get(ev, "type") is JToken t && t.Type == JTokenType.String ? t.Value<string>() : null;
                    if (etype == "transcript")
                    {
                        Partial = Py.StrOr(Py.Get(ev, "transcript"));
                        AudioMs = ProcessedMs(ev);
                        if (Py.Truthy(Py.Get(ev, "final")))
                        {
                            FinalText = Partial;
                            FinalEvent.Set();
                        }
                    }
                    else if (etype == "audioProgress")
                        AudioMs = ProcessedMs(ev);
                    else if (etype == "error")
                    {
                        JToken msg = Py.Get(ev, "message");
                        Error = msg == null ? "asr error" : Py.Str(msg);
                        Done.Set();
                        FinalEvent.Set();
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                if (Error == null) Error = "asr read: " + e.Message;
            }
            finally
            {
                Done.Set();
                FinalEvent.Set();
            }
        }

        private int ProcessedMs(JObject ev)
        {
            JToken v = Py.Get(ev, "audioProcessedMs") ?? new JValue(AudioMs);
            return Py.TryIntOr0(v, out long ms) ? (int)ms : AudioMs;
        }

        public void Finish() { FinishRequested.Set(); }

        public void Abort()
        {
            FinishRequested.Set();
            Done.Set();
            if (ws == null) return;
            try
            {
                if (sendLock.Wait(500))
                {
                    try
                    {
                        if (ws.State == WebSocketState.Open)
                            ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", new CancellationTokenSource(1000).Token)
                              .GetAwaiter().GetResult();
                    }
                    finally { sendLock.Release(); }
                }
            }
            catch (Exception) { }
            try { ws.Abort(); } catch (Exception) { }
            try { ws.Dispose(); } catch (Exception) { }
        }

        public JObject Status()
        {
            return new JObject
            {
                ["partial"] = Partial,
                ["audio_ms"] = AudioMs,
                ["queued_ms"] = (long)QueuedMs,
                ["error"] = Error == null ? JValue.CreateNull() : new JValue(Error),
                ["done"] = Done.IsSet,
                ["send_done"] = SendDone.IsSet,
                ["final"] = string.IsNullOrEmpty(FinalText) ? JValue.CreateNull() : new JValue(FinalText)
            };
        }
    }
}
