using System.Collections;
using System.Collections.Generic;
using RoboFactory.Hackathon.Meta;
using UnityEngine;

namespace RoboFactory.Logistics
{
    /// <summary>
    /// Spoken urgent orders: push-to-talk on the microphone, conversion to
    /// PCM16 mono 24 kHz, sent in chunks to the Meta gateway (/asr/*), partial
    /// transcription shown live, FINAL transcription sent to Muse Wonder
    /// through SubmitUrgentTranscript. The Meta key never leaves the gateway —
    /// Unity only sees PCM and text.
    ///
    /// Audio flow: the gateway re-emits the audio to Meta in 80 ms frames paced
    /// in real time; an incomplete frame is padded with silence there and an
    /// empty buffer produces a frame of silence. Sending from Unity is therefore:
    ///  - batched (everything waiting goes in ONE request: a slow HTTP request
    ///    no longer makes the stream fall behind);
    ///  - aligned on 80 ms during capture (no gap of silence inserted in the
    ///    middle of words);
    ///  - preceded by a pre-buffer (~400 ms ahead on the gateway side: pacing
    ///    hiccups no longer empty its buffer in the middle of a sentence).
    /// If the real-time session fails while the microphone did capture a voice,
    /// the full recording is transcribed once more in a new session.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VoiceCommandController : MonoBehaviour
    {
        public enum VoiceState { Idle, Starting, Recording, Finishing }

        [Header("References")]
        public MuseOrchestrator Orchestrator;
        [Tooltip("Empty = use the orchestrator's ProxyUrl.")]
        public string ProxyUrl = "";

        [Header("Capture")]
        [Tooltip("Maximum length of an utterance before automatic sending (s).")]
        public float MaxRecordSeconds = 20f;
        [Tooltip("Microphone buffer read interval (ms).")]
        public int PollMs = 200;
        [Tooltip("Audio lead built up on the gateway side before the first send (ms).")]
        public int PrebufferMs = 400;
        [Tooltip("Real-time session failure with captured voice: transcribe the full recording once more.")]
        public bool RetryWholeCapture = true;
        [Tooltip("Peak level (0-1) below which the capture is considered silent.")]
        public float SilencePeak = .004f;
        [Tooltip("Writes the last capture to Logs/voice_last.wav (to check by listening).")]
        public bool SaveLastCapture = true;
        [Tooltip("Warehouse vocabulary sent to Meta (recognition bias; French terms, the speech model also understands English).")]
        public string[] Keywords =
        {
            "palette", "banc", "camion", "carton", "commande urgente",
            "caisse", "urgent", "livrer", "charger"
        };
        public string[] Languages = { "French", "English" };
        [Tooltip("Mock mode: text returned as the transcription (test without a microphone or API).")]
        public string MockTranscript = "urgent order, two boxes A and one box B";

        public VoiceState State { get; private set; } = VoiceState.Idle;
        public bool Busy => State != VoiceState.Idle;
        public bool IsRecording => State == VoiceState.Recording;
        /// <summary>Most recent cumulative partial transcription.</summary>
        public string LiveTranscript { get; private set; } = "";
        /// <summary>Last final transcription sent to Muse.</summary>
        public string FinalTranscript { get; private set; } = "";
        public string StatusText { get; private set; } = "mic ready";
        public int LastAsrLatencyMs { get; private set; }
        public string LastAsrModel { get; private set; } = "";
        /// <summary>Real ASR mode of the last session ("live" | "mock").</summary>
        public string LastAsrMode { get; private set; } = "";
        /// <summary>True only when Meta returned a FINAL transcription submitted to Muse.</summary>
        public bool LastSubmittedFinal { get; private set; }
        /// <summary>ms of audio accepted by Meta — proof of transport, NOT of transcription.</summary>
        public int AudioProcessedMs { get; private set; }
        /// <summary>Last readable ASR failure (never hidden in the HUD).</summary>
        public string LastFailure { get; private set; } = "";
        /// <summary>Peak level (0-1) of the last capture: silent-microphone diagnostics.</summary>
        public float LastCapturePeak { get; private set; }
        /// <summary>Duration (s) of the last capture sent.</summary>
        public float LastCaptureSeconds { get; private set; }
        public float RecordingElapsed => State == VoiceState.Recording ? Time.unscaledTime - recordStart : 0f;

        private MetaProxyClient proxy;
        private AudioClip clip;
        private int micRate = 48000;
        private int micChannels = 1;
        private int lastMicPos;
        private float recordStart;
        private string sessionId;
        private int sessionSeq;
        private int generation;
        private bool mockSession;
        private bool sessionReady;   // /asr/start answered OK: audio can be sent
        private bool sessionFailed;  // /asr/start failed: capture abandoned
        private readonly Queue<byte[]> outbox = new Queue<byte[]>();
        private int consecutiveSendFails;

        // ---- batched sending: send buffer, request in flight, pre-buffer reached
        private readonly List<byte> sendBuf = new List<byte>();
        private bool sendInFlight;
        private bool primed;
        /// <summary>Full copy of the capture (PCM 24 kHz): diagnostics + retry.</summary>
        private readonly List<byte> captured = new List<byte>();
        private float capturePeak;

        // ---- resampler: continuous buffer + fractional cursor
        private readonly List<float> resIn = new List<float>();
        private double resCursor;
        private const int TargetRate = 24000;
        /// <summary>Gateway pacer frame (ASR_FRAME_MS = 80 ms in s16le 24 kHz).</summary>
        private const int FrameBytes = TargetRate * 2 * 80 / 1000;
        /// <summary>Maximum size of a request (2 s, a multiple of the frame).</summary>
        private const int MaxSendBytes = FrameBytes * 25;

        private void Start()
        {
            if (Orchestrator == null) Orchestrator = FindFirstObjectByType<MuseOrchestrator>();
            string url = !string.IsNullOrEmpty(ProxyUrl) ? ProxyUrl
                : Orchestrator != null ? Orchestrator.ProxyUrl : "http://127.0.0.1:8765";
            proxy = new MetaProxyClient { BaseUrl = url };
        }

        private void Update()
        {
            if (State != VoiceState.Recording || mockSession) return;
            if (clip != null && Microphone.IsRecording(null))
            {
                if (Time.unscaledTime - recordStart > MaxRecordSeconds)
                {
                    StatusText = "maximum duration reached — sending";
                    EndTalk();
                }
            }
            else if (clip != null && Time.unscaledTime - recordStart > 1f)
            {
                StatusText = "mic stopped — sending what was captured";
                EndTalk();
            }
        }

        // ------------------------------------------------------------------ button API

        /// <summary>Toggles pressed/released (HUD MIC button).</summary>
        public void Toggle()
        {
            if (State == VoiceState.Idle) BeginTalk();
            else if (State == VoiceState.Recording) EndTalk();
        }

        /// <summary>Push-to-talk: press.</summary>
        public void BeginTalk()
        {
            if (State != VoiceState.Idle) return;
            if (Orchestrator != null && Orchestrator.Mode == "offline")
            {
                StatusText = "offline mode: ASR unavailable";
                return;
            }
            bool mock = Orchestrator != null && Orchestrator.Mode == "mock";
            if (!mock && (Microphone.devices == null || Microphone.devices.Length == 0))
            {
                StatusText = "no microphone detected (mock mode available)";
                return;
            }
            generation++;
            sessionReady = false;
            sessionFailed = false;
            sessionId = "asr-" + (++sessionSeq) + "-" + Time.frameCount;
            outbox.Clear();
            sendBuf.Clear();
            sendInFlight = false;
            primed = false;
            captured.Clear();
            capturePeak = 0f;
            resIn.Clear();
            resCursor = 0;
            consecutiveSendFails = 0;
            mockSession = mock;
            LiveTranscript = "";
            LastSubmittedFinal = false;
            LastFailure = "";
            AudioProcessedMs = 0;
            LastAsrMode = mock ? "mock" : "live";

            // The microphone starts RIGHT AWAY: you can speak while the Meta session
            // opens (sometimes several seconds when cold). The audio is
            // buffered in the outbox then sent as soon as the session is ready.
            if (!mock)
            {
                clip = Microphone.Start(null, true, Mathf.CeilToInt(MaxRecordSeconds) + 2, TargetRate);
                if (clip == null)
                {
                    StatusText = "microphone refused by the system";
                    LastFailure = StatusText;
                    return;
                }
                micRate = clip.frequency > 0 ? clip.frequency : TargetRate;
                micChannels = Mathf.Max(1, clip.channels);
                lastMicPos = 0;
            }
            recordStart = Time.unscaledTime;
            State = VoiceState.Recording;
            StatusText = mock ? "MOCK — release to send: «" + MockTranscript + "»"
                              : "● REC — speak (connecting to Meta…)";
            int gen = generation;
            StartCoroutine(StartSession(gen));
            StartCoroutine(mock ? MockPump(gen) : MicPump(gen));
            StartCoroutine(Sender(gen));
        }

        /// <summary>Push-to-talk: released -> the final transcription is sent.</summary>
        public void EndTalk()
        {
            if (State != VoiceState.Recording) return;
            State = VoiceState.Finishing;
            StatusText = "ending capture…";
            if (!mockSession)
            {
                Harvest();
                LastCaptureSeconds = captured.Count / (TargetRate * 2f);
                LastCapturePeak = capturePeak;
                string device = Microphone.devices != null && Microphone.devices.Length > 0
                    ? Microphone.devices[0] : "?";
                Debug.Log("[Voice] capture " + LastCaptureSeconds.ToString("F1") + " s · peak " +
                          PeakDb(capturePeak) + " dBFS · default microphone (1st: «" + device + "») " +
                          micRate + " Hz × " + micChannels + " channel(s)");
                if (SaveLastCapture) SaveWav(captured);
            }
            StopMic();
            StartCoroutine(FinishSession(generation));
        }

        /// <summary>Cancels the capture without sending anything to Muse.</summary>
        public void CancelTalk()
        {
            if (State == VoiceState.Idle) return;
            generation++;
            StopMic();
            outbox.Clear();
            sendBuf.Clear();
            sendInFlight = false;
            resIn.Clear();
            resCursor = 0;
            State = VoiceState.Idle;
            StatusText = "capture cancelled";
            if (!string.IsNullOrEmpty(sessionId))
                StartCoroutine(proxy.AsrCancel(sessionId, _ => { }));
            sessionId = null;
        }

        // ------------------------------------------------------------------ session ASR

        private IEnumerator StartSession(int gen)
        {
            AsrStartResponse r = null;
            yield return proxy.AsrStart(sessionId, Keywords, Languages,
                mockSession ? "mock" : "live",
                mockSession ? MockTranscript : null, x => r = x);
            if (gen != generation || State == VoiceState.Idle) yield break;
            if (r == null || !r.ok)
            {
                sessionFailed = true;
                StopMic();
                outbox.Clear();
                sendBuf.Clear();
                StatusText = "ASR unavailable: " + (r != null ? r.error : "gateway silent");
                LastFailure = StatusText;
                State = VoiceState.Idle;
                yield break;
            }
            LastAsrModel = r.model;
            if (!string.IsNullOrEmpty(r.mode)) LastAsrMode = r.mode; // gateway truth
            LastAsrLatencyMs = r.latency_ms;
            sessionReady = true;
            if (State == VoiceState.Recording && !mockSession)
                StatusText = "● REC — speak (French / English)";
        }

        /// <summary>Regularly takes the audio from the microphone buffer and sends it to the outbox.</summary>
        private IEnumerator MicPump(int gen)
        {
            // real time: independent of any Time.timeScale
            var wait = new WaitForSecondsRealtime(Mathf.Max(50, PollMs) / 1000f);
            while (gen == generation && State == VoiceState.Recording)
            {
                Harvest();
                yield return wait;
            }
        }

        /// <summary>Mock mode: small chunks of silence to animate the transcription.</summary>
        private IEnumerator MockPump(int gen)
        {
            var wait = new WaitForSecondsRealtime(.3f);
            var silence = new byte[TargetRate * 2 / 10]; // 100 ms of PCM16 silence
            while (gen == generation && State == VoiceState.Recording)
            {
                if (outbox.Count < 4) outbox.Enqueue(silence);
                yield return wait;
            }
        }

        /// <summary>Captured bytes not yet handed to the gateway (outbox + send buffer + request in flight).</summary>
        private int PendingBytes()
        {
            int n = sendBuf.Count;
            foreach (byte[] b in outbox) n += b.Length;
            return n;
        }

        /// <summary>
        /// Sends the pending audio, batched into one request; the answer carries the
        /// live partial. During capture only multiples of 80 ms are sent
        /// (no frame padded with silence in the middle of a word); the rest
        /// leaves at the end of the utterance.
        /// </summary>
        private IEnumerator Sender(int gen)
        {
            int prebufferBytes = TargetRate * 2 * Mathf.Max(0, PrebufferMs) / 1000;
            while (gen == generation && (State == VoiceState.Recording || State == VoiceState.Finishing))
            {
                if (!sessionReady) { yield return null; continue; }
                while (outbox.Count > 0) sendBuf.AddRange(outbox.Dequeue());
                bool finishing = State == VoiceState.Finishing;
                int n;
                if (mockSession || finishing) n = sendBuf.Count;
                else
                {
                    if (!primed && sendBuf.Count < prebufferBytes) { yield return null; continue; }
                    n = sendBuf.Count - sendBuf.Count % FrameBytes;
                }
                if (n <= 0) { yield return null; continue; }
                n = Mathf.Min(n, MaxSendBytes);
                primed = true;
                byte[] pcm = sendBuf.GetRange(0, n).ToArray();
                sendBuf.RemoveRange(0, n);

                sendInFlight = true;
                AsrAudioResponse r = null;
                yield return proxy.AsrAudio(sessionId, pcm, x => r = x);
                sendInFlight = false;
                if (gen != generation) yield break;
                if (r == null || !r.ok)
                {
                    if (++consecutiveSendFails >= 3 && State == VoiceState.Recording)
                    {
                        StatusText = "ASR stream lost: " + (r != null ? r.error : "gateway silent");
                        outbox.Clear();
                        sendBuf.Clear();
                        EndTalk();
                        yield break;
                    }
                    continue;
                }
                consecutiveSendFails = 0;
                if (r.latency_ms > LastAsrLatencyMs) LastAsrLatencyMs = r.latency_ms;
                if (r.audio_ms > AudioProcessedMs) AudioProcessedMs = r.audio_ms;
                if (!string.IsNullOrEmpty(r.partial)) LiveTranscript = r.partial;
                if (!string.IsNullOrEmpty(r.final)) LiveTranscript = r.final;
            }
        }

        /// <summary>Empties the outbox then asks the gateway for the final transcription.</summary>
        private IEnumerator FinishSession(int gen)
        {
            // Released before the Meta session was ready: wait for it to open
            // (the captured audio is buffered), otherwise abort cleanly.
            float openDeadline = Time.unscaledTime + 30f;
            while (!sessionReady && !sessionFailed && gen == generation && Time.unscaledTime < openDeadline)
                yield return null;
            if (gen != generation || sessionFailed) yield break;
            if (!sessionReady)
            {
                StatusText = "ASR session not opened after 30 s — nothing sent to Muse";
                LastFailure = StatusText;
                State = VoiceState.Idle;
                outbox.Clear();
                sendBuf.Clear();
                if (!string.IsNullOrEmpty(sessionId)) StartCoroutine(proxy.AsrCancel(sessionId, _ => { }));
                sessionId = null;
                yield break;
            }
            float deadline = Time.unscaledTime + 10f + PendingBytes() / (TargetRate * 2f) * 1.5f;
            while ((PendingBytes() > 0 || sendInFlight) && Time.unscaledTime < deadline && gen == generation)
                yield return null;
            if (gen != generation) yield break;

            AsrFinishResponse r = null;
            yield return proxy.AsrFinish(sessionId, x => r = x);
            sessionId = null;
            if (gen != generation) yield break;

            string text = r != null && r.ok ? (r.transcript ?? "").Trim() : "";
            bool usable = r != null && r.ok && r.final && !r.timed_out && text.Length > 0;
            bool voiceCaptured = mockSession || capturePeak >= SilencePeak;

            // Real-time session missed while the voice was captured: we
            // transcribe the full recording in a single block (the gateway
            // re-emits it in real time, without gaps), only once.
            if (!usable && !mockSession && RetryWholeCapture && voiceCaptured && captured.Count > FrameBytes)
            {
                string firstError = r == null ? "gateway silent"
                    : !string.IsNullOrEmpty(r.error) ? r.error : "no speech recognised";
                Debug.LogWarning("[Voice] real-time session without transcription (" + firstError +
                                 "): retrying on the full recording");
                StatusText = "2nd attempt: transcribing the full recording…";
                AsrFinishResponse r2 = null;
                yield return TranscribeWhole(gen, x => r2 = x);
                if (gen != generation) yield break;
                if (r2 != null)
                {
                    r = r2;
                    text = r.ok ? (r.transcript ?? "").Trim() : "";
                    usable = r.ok && r.final && !r.timed_out && text.Length > 0;
                }
            }
            State = VoiceState.Idle;

            if (!usable)
            {
                FinalTranscript = "";
                LastSubmittedFinal = false;
                StatusText = !voiceCaptured
                    ? "silent microphone (peak " + PeakDb(capturePeak) +
                      " dBFS) — check the Windows default input microphone"
                    : text.Length > 0
                        ? "incomplete transcription — NOT sent to Muse (please rephrase)"
                        : "no transcription: " + (r != null && !string.IsNullOrEmpty(r.error)
                            ? r.error : "no speech recognised");
                LastFailure = StatusText;
                yield break;
            }
            if (r.audio_ms > AudioProcessedMs) AudioProcessedMs = r.audio_ms;
            if (r.latency_ms > LastAsrLatencyMs) LastAsrLatencyMs = r.latency_ms;
            FinalTranscript = text;
            LiveTranscript = text;
            LastSubmittedFinal = true;
            StatusText = "→ Muse Wonder : " + text;
            if (Orchestrator != null) Orchestrator.SubmitUrgentTranscript(text);
        }

        /// <summary>New ASR session fed in one block with the whole capture.</summary>
        private IEnumerator TranscribeWhole(int gen, System.Action<AsrFinishResponse> done)
        {
            string retryId = "asr-" + (++sessionSeq) + "-retry-" + Time.frameCount;
            AsrStartResponse s = null;
            yield return proxy.AsrStart(retryId, Keywords, Languages, "live", null, x => s = x);
            if (gen != generation) yield break;
            if (s == null || !s.ok) { done(null); yield break; }
            if (!string.IsNullOrEmpty(s.session_id)) retryId = s.session_id;
            byte[] all = captured.ToArray();
            for (int off = 0; off < all.Length && gen == generation; off += MaxSendBytes * 4)
            {
                int len = Mathf.Min(MaxSendBytes * 4, all.Length - off);
                var part = new byte[len];
                System.Array.Copy(all, off, part, 0, len);
                AsrAudioResponse a = null;
                yield return proxy.AsrAudio(retryId, part, x => a = x);
                if (a != null && a.ok && !string.IsNullOrEmpty(a.partial)) LiveTranscript = a.partial;
            }
            if (gen != generation) yield break;
            AsrFinishResponse f = null;
            yield return proxy.AsrFinish(retryId, x => f = x);
            done(f);
        }

        // ------------------------------------------------------------------ audio

        /// <summary>Reads the new microphone samples, converts them to PCM16 mono 24 kHz.</summary>
        private void Harvest()
        {
            if (clip == null) return;
            int pos = Microphone.GetPosition(null);
            if (pos < 0) return;
            int total = pos >= lastMicPos ? pos - lastMicPos
                                          : clip.samples - lastMicPos + pos;
            if (total <= 0) return;

            float[] samples;
            if (pos >= lastMicPos)
            {
                samples = new float[total * micChannels];
                clip.GetData(samples, lastMicPos);
            }
            else // circular buffer: end segment + start segment
            {
                var head = new float[(clip.samples - lastMicPos) * micChannels];
                var tail = new float[pos * micChannels];
                clip.GetData(head, lastMicPos);
                clip.GetData(tail, 0);
                samples = new float[head.Length + tail.Length];
                System.Array.Copy(head, samples, head.Length);
                System.Array.Copy(tail, 0, samples, head.Length, tail.Length);
            }
            lastMicPos = pos;

            // mono
            if (micChannels > 1)
            {
                var mono = new float[total];
                for (int i = 0; i < total; i++)
                {
                    float s = 0f;
                    for (int c = 0; c < micChannels; c++) s += samples[i * micChannels + c];
                    mono[i] = s / micChannels;
                }
                samples = mono;
            }
            foreach (float f in samples)
            {
                float a = f < 0f ? -f : f;
                if (a > capturePeak) capturePeak = a;
            }

            byte[] pcm = ToPcm24k(samples);
            if (pcm.Length > 0)
            {
                outbox.Enqueue(pcm);
                captured.AddRange(pcm);
            }
        }

        /// <summary>Continuous linear resampling to 24 kHz, s16le output.</summary>
        private byte[] ToPcm24k(float[] mono)
        {
            foreach (float f in mono) resIn.Add(f);
            double step = micRate / (double)TargetRate;
            var shorts = new List<short>(mono.Length);
            // Linear interpolation: the next sample is needed, the rest is kept.
            while (resCursor + 1 < resIn.Count)
            {
                int i = (int)resCursor;
                float frac = (float)(resCursor - i);
                float s = resIn[i] + (resIn[i + 1] - resIn[i]) * frac;
                shorts.Add((short)Mathf.Clamp(Mathf.RoundToInt(s * 32767f), -32768, 32767));
                resCursor += step;
            }
            int consumed = (int)resCursor;
            if (consumed > 0)
            {
                resIn.RemoveRange(0, consumed);
                resCursor -= consumed;
            }
            var bytes = new byte[shorts.Count * 2];
            for (int i = 0; i < shorts.Count; i++)
            {
                bytes[i * 2] = (byte)(shorts[i] & 0xFF);
                bytes[i * 2 + 1] = (byte)((shorts[i] >> 8) & 0xFF);
            }
            return bytes;
        }

        private void StopMic()
        {
            if (Microphone.IsRecording(null)) Microphone.End(null);
            clip = null;
        }

        private static string PeakDb(float peak)
        {
            return peak <= 0f ? "-inf" : (20f * Mathf.Log10(peak)).ToString("F0");
        }

        /// <summary>Last capture as WAV (24 kHz mono 16 bits) in the project's Logs folder.</summary>
        private static void SaveWav(List<byte> pcm)
        {
            try
            {
                string dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath) ?? ".", "Logs");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, "voice_last.wav");
                using (var w = new System.IO.BinaryWriter(System.IO.File.Create(path)))
                {
                    int data = pcm.Count;
                    w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                    w.Write(36 + data);
                    w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                    w.Write(16);
                    w.Write((short)1);
                    w.Write((short)1);
                    w.Write(TargetRate);
                    w.Write(TargetRate * 2);
                    w.Write((short)2);
                    w.Write((short)16);
                    w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                    w.Write(data);
                    w.Write(pcm.ToArray());
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[Voice] voice_last.wav not written: " + e.Message);
            }
        }
    }
}
