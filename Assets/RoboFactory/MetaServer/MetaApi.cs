using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace RoboFactory.MetaServer
{
    /// <summary>
    /// Configuration, API key and HTTPS calls to the Meta API (equivalent of http_json /
    /// call_with_retry / call_orchestrator / log of the Python proxy). Thread-safe, no Unity.
    /// </summary>
    public static class MetaApi
    {
        private static string Env(string name, string fallback)
        {
            string v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(v) ? fallback : v;
        }

        private static double EnvF(string name, double fallback)
        {
            return double.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : fallback;
        }

        public static readonly string BaseUrl = Env("META_BASE_URL", "https://api.meta.ai/v1");
        public static readonly string AsrUrl = Env("META_ASR_URL", "wss://api.meta.ai/v1/asr/realtime");
        public static readonly string VisionModel = Env("META_VISION_MODEL", "muse-spark-1.3");
        public static readonly string SamModel = Env("META_SAM_MODEL", "sam-3.1");
        public static readonly string OrchestratorModel = Env("META_ORCHESTRATOR_MODEL", "muse-wonder-1.0");
        public static readonly string OrchestratorFallback = Env("META_ORCHESTRATOR_FALLBACK", "muse-spark-1.3");
        public static readonly double WonderTimeout = EnvF("META_WONDER_TIMEOUT", 45);
        public static readonly double FallbackTimeout = EnvF("META_FALLBACK_TIMEOUT", 45);
        public static readonly string VoiceModel = Env("META_VOICE_MODEL", "muse-voice-transcribe-1.0");

        // ------------------------------------------------------------------ API key

        private static string sessionKey;
        private static readonly object keyLock = new object();

        /// <summary>Key entered by the user (LIVE AI window): memory only, never written.</summary>
        public static void SetSessionKey(string key)
        {
            lock (keyLock) sessionKey = Sanitize(key);
        }

        /// <summary>Tolerates paste artefacts (BOM, « Bearer », quotes) without ever logging the value.</summary>
        public static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            value = value.Replace("﻿", "").Trim();
            if (value.Length >= 7 && value.Substring(0, 7).ToLowerInvariant() == "bearer ") value = value.Substring(7).Trim();
            if (value.Length >= 2 && value[0] == value[value.Length - 1] && (value[0] == '"' || value[0] == '\''))
                value = value.Substring(1, value.Length - 2).Trim();
            return value;
        }

        /// <summary>Session key, otherwise MODEL_API_KEY, otherwise ~/.meta/model_api_key (like the Python proxy).</summary>
        public static string LoadKey()
        {
            lock (keyLock)
                if (!string.IsNullOrEmpty(sessionKey) && sessionKey != MetaPrompts.PLACEHOLDER) return sessionKey;
            string env = Sanitize(Environment.GetEnvironmentVariable("MODEL_API_KEY"));
            if (env.Length > 0 && env != MetaPrompts.PLACEHOLDER) return env;
            try
            {
                string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".meta", "model_api_key");
                if (File.Exists(file))
                {
                    string v = Sanitize(File.ReadAllText(file, Encoding.UTF8));
                    if (v.Length > 0 && v != MetaPrompts.PLACEHOLDER) return v;
                }
            }
            catch (Exception) { }
            return null;
        }

        // ------------------------------------------------------------------ journal calls.jsonl

        public static string LogFile = "";
        private static readonly object logLock = new object();

        public static void Log(JObject entry)
        {
            entry["ts"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            if (string.IsNullOrEmpty(LogFile)) return;
            lock (logLock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogFile));
                    File.AppendAllText(LogFile, Py.Compact(entry) + "\n", new UTF8Encoding(false));
                }
                catch (Exception) { }
            }
        }

        // ------------------------------------------------------------------ HTTPS

        private static readonly HttpClient http = CreateClient();

        private static HttpClient CreateClient()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch (Exception) { }
            return new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        }

        public struct Result
        {
            public int Status;
            public string Raw;
            public Result(int status, string raw) { Status = status; Raw = raw ?? ""; }
        }

        /// <summary>Exception = network / DNS / timeout (status 0 on the Python side).</summary>
        private static Result HttpJson(string url, JObject payload, string key, double timeoutS, bool stream, out bool httpError)
        {
            httpError = false;
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutS)))
            using (var req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
                if (stream) req.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
                req.Content = new StringContent(Py.Dumps(payload), Encoding.UTF8, "application/json");
                using (HttpResponseMessage resp = http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).GetAwaiter().GetResult())
                {
                    byte[] bytes = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                    string text = Encoding.UTF8.GetString(bytes);
                    int code = (int)resp.StatusCode;
                    if (code >= 400)
                    {
                        httpError = true;
                        return new Result(code, Py.Cut(text, 600));
                    }
                    return new Result(code, text);
                }
            }
        }

        /// <summary>call_with_retry: 429/5xx and network errors retried (1.5 s, 3 s…), otherwise returned as is.</summary>
        public static Result CallWithRetry(string url, JObject payload, string key, double timeoutS, bool stream = false, int attempts = 2)
        {
            Result last = new Result(0, "");
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                try
                {
                    Result r = HttpJson(url, payload, key, timeoutS, stream, out bool httpError);
                    if (!httpError) return r;
                    last = r;
                    if ((r.Status == 429 || r.Status == 500 || r.Status == 502 || r.Status == 503 || r.Status == 504) && attempt + 1 < attempts)
                    {
                        Thread.Sleep((int)(1500 * (attempt + 1)));
                        continue;
                    }
                    return r;
                }
                catch (Exception e)
                {
                    last = new Result(0, ErrorText(e, timeoutS));
                    if (attempt + 1 < attempts)
                    {
                        Thread.Sleep((int)(1500 * (attempt + 1)));
                        continue;
                    }
                }
            }
            return last;
        }

        private static string ErrorText(Exception e, double timeoutS)
        {
            if (e is OperationCanceledException) return "timed out after " + (int)timeoutS + "s";
            Exception inner = e;
            while (inner.InnerException != null) inner = inner.InnerException;
            return inner.Message;
        }

        public struct OrchestratorResult
        {
            public int Status;
            public string Raw, Model, Note;
        }

        /// <summary>
        /// Muse Wonder first; if it does not answer in time or fails (timeout, network, 404 / 408 / 429 / 5xx),
        /// the same request goes to Muse Spark. The model actually used is always reported as is.
        /// </summary>
        public static OrchestratorResult CallOrchestrator(JObject payload, string key)
        {
            string model = OrchestratorModel;
            payload["model"] = model;
            Result r = CallWithRetry(BaseUrl + "/responses", payload, key, WonderTimeout, attempts: 1);
            if (r.Status == 200 || OrchestratorFallback == OrchestratorModel)
                return new OrchestratorResult { Status = r.Status, Raw = r.Raw, Model = model, Note = "" };
            int s = r.Status;
            if (!(s == 0 || s == 404 || s == 408 || s == 429 || s == 500 || s == 502 || s == 503 || s == 504))
                return new OrchestratorResult { Status = r.Status, Raw = r.Raw, Model = model, Note = "" };
            string reason = s == 0 ? "no answer within " + (int)WonderTimeout + "s" : "status " + s;
            model = OrchestratorFallback;
            payload["model"] = model;
            r = CallWithRetry(BaseUrl + "/responses", payload, key, FallbackTimeout, attempts: 1);
            return new OrchestratorResult
            {
                Status = r.Status, Raw = r.Raw, Model = model,
                Note = OrchestratorModel + " unavailable (" + reason + "), fell back to " + model
            };
        }

        /// <summary>Body of a Responses request with a single user message.</summary>
        public static JObject UserMessage(string model, params JObject[] content)
        {
            var payload = new JObject();
            if (model != null) payload["model"] = model;
            payload["input"] = new JArray(new JObject
            {
                ["type"] = "message",
                ["role"] = "user",
                ["content"] = new JArray(content)
            });
            return payload;
        }

        public static JObject Text(string text) => new JObject { ["type"] = "input_text", ["text"] = text };

        public static JObject Image(string dataUrl) => new JObject { ["type"] = "input_image", ["image_url"] = dataUrl };
    }
}
