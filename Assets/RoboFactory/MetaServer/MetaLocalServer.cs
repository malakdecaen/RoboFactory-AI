using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace RoboFactory.MetaServer
{
    /// <summary>
    /// Serveur Meta intégré au jeu : remplace Tools/MetaProxy/meta_proxy.py, mêmes routes et mêmes
    /// réponses sur http://127.0.0.1:8765, donc aucun client Unity ne change. Aucune dépendance Unity :
    /// démarré par MetaServerHost. La clé API ne sort jamais de ce processus.
    /// Routes : GET /health ; POST /order /vision /sam /supervise /warehouse /urgent /capacity
    /// /asr/start /asr/audio /asr/finish /asr/cancel.
    /// </summary>
    public sealed class MetaLocalServer
    {
        public const int DefaultPort = 8765;

        public int Port { get; }
        public bool Running { get; private set; }
        public string LastError { get; private set; } = "";
        public Action<string> Logger;

        private HttpListener listener;
        private Thread acceptThread;
        private readonly Dictionary<string, AsrSession> sessions = new Dictionary<string, AsrSession>();
        private readonly object sessionsLock = new object();

        public MetaLocalServer(int port = DefaultPort) { Port = port; }

        public int AsrSessionCount { get { lock (sessionsLock) return sessions.Count; } }

        // ------------------------------------------------------------------ cycle de vie

        public bool Start()
        {
            if (Running) return true;
            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
                listener.Start();
            }
            catch (Exception e)
            {
                LastError = e.Message;
                try { listener?.Close(); } catch (Exception) { }
                listener = null;
                return false;
            }
            Running = true;
            acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "MetaLocalServer" };
            acceptThread.Start();
            Logger?.Invoke("serveur Meta intégré sur http://127.0.0.1:" + Port + "  key_present=" + (MetaApi.LoadKey() != null));
            return true;
        }

        public void Stop()
        {
            if (!Running) return;
            Running = false;
            try { listener?.Stop(); } catch (Exception) { }
            try { listener?.Close(); } catch (Exception) { }
            listener = null;
            List<AsrSession> open;
            lock (sessionsLock)
            {
                open = sessions.Values.ToList();
                sessions.Clear();
            }
            foreach (AsrSession s in open) s.Abort();
        }

        private void AcceptLoop()
        {
            while (Running)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch (Exception)
                {
                    if (!Running) break;
                    continue;
                }
                // une requête = une tâche (équivalent ThreadingHTTPServer)
                Task.Run(() => Handle(ctx));
            }
        }

        // ------------------------------------------------------------------ HTTP

        private static void SendJson(HttpListenerContext ctx, int status, JObject payload)
        {
            try
            {
                byte[] body = Encoding.UTF8.GetBytes(Py.Compact(payload));
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = body.Length;
                ctx.Response.OutputStream.Write(body, 0, body.Length);
            }
            catch (Exception) { }
            finally
            {
                try { ctx.Response.OutputStream.Close(); } catch (Exception) { }
                try { ctx.Response.Close(); } catch (Exception) { }
            }
        }

        private static JObject ReadBody(HttpListenerContext ctx)
        {
            long length = ctx.Request.ContentLength64;
            if (length <= 0 || length > 64L * 1024 * 1024) return null;
            try
            {
                using (var ms = new MemoryStream())
                {
                    ctx.Request.InputStream.CopyTo(ms);
                    return Py.Parse(Encoding.UTF8.GetString(ms.ToArray())) as JObject;
                }
            }
            catch (Exception) { return null; }
        }

        private void Handle(HttpListenerContext ctx)
        {
            try
            {
                string route = ctx.Request.Url.AbsolutePath;
                if (ctx.Request.HttpMethod == "GET")
                {
                    if (route != "/health") { SendJson(ctx, 404, new JObject { ["ok"] = false, ["error"] = "not found" }); return; }
                    string k = MetaApi.LoadKey();
                    SendJson(ctx, 200, new JObject
                    {
                        ["ok"] = true,
                        ["key_present"] = k != null,
                        ["mode"] = k != null ? "live" : "no-key",
                        ["base_url"] = MetaApi.BaseUrl,
                        ["vision_model"] = MetaApi.VisionModel,
                        ["sam_model"] = MetaApi.SamModel,
                        ["orchestrator_model"] = MetaApi.OrchestratorModel,
                        ["orchestrator_fallback"] = MetaApi.OrchestratorFallback,
                        ["voice_model"] = MetaApi.VoiceModel,
                        ["asr_sessions"] = AsrSessionCount,
                        ["server"] = "unity"
                    });
                    return;
                }
                if (ctx.Request.HttpMethod != "POST") { SendJson(ctx, 404, new JObject { ["ok"] = false, ["error"] = "not found" }); return; }
                JObject body = ReadBody(ctx);
                if (body == null) { SendJson(ctx, 400, new JObject { ["ok"] = false, ["error"] = "invalid json body" }); return; }
                string key = MetaApi.LoadKey();
                var started = Stopwatch.StartNew();
                switch (route)
                {
                    case "/order": HandleOrder(ctx, body, key, started); break;
                    case "/vision": HandleVision(ctx, body, key, started); break;
                    case "/sam": HandleSam(ctx, body, key, started); break;
                    case "/supervise": HandleSupervise(ctx, body, key, started); break;
                    case "/warehouse": HandleWarehouse(ctx, body, key, started); break;
                    case "/urgent": HandleUrgent(ctx, body, key, started); break;
                    case "/capacity": HandleCapacity(ctx, body, key, started); break;
                    case "/asr/start": HandleAsrStart(ctx, body, key, started); break;
                    case "/asr/audio": HandleAsrAudio(ctx, body, started); break;
                    case "/asr/finish": HandleAsrFinish(ctx, body, started); break;
                    case "/asr/cancel": HandleAsrCancel(ctx, body, started); break;
                    default: SendJson(ctx, 404, new JObject { ["ok"] = false, ["error"] = "not found" }); break;
                }
            }
            catch (Exception e)
            {
                Logger?.Invoke("erreur interne : " + e.GetType().Name + " " + e.Message);
                SendJson(ctx, 500, new JObject { ["ok"] = false, ["error"] = "internal error: " + e.Message });
            }
        }

        private static int Ms(Stopwatch sw) => (int)sw.Elapsed.TotalMilliseconds;

        private void Fail(HttpListenerContext ctx, int status, string route, Stopwatch started, string message, JObject extra = null)
        {
            var payload = new JObject
            {
                ["ok"] = false,
                ["error"] = message,
                ["mode"] = MetaApi.LoadKey() != null ? "live" : "no-key"
            };
            if (extra != null) foreach (JProperty p in extra.Properties()) payload[p.Name] = p.Value;
            MetaApi.Log(new JObject
            {
                ["endpoint"] = route, ["ok"] = false, ["status"] = status, ["error"] = message, ["latency_ms"] = Ms(started)
            });
            SendJson(ctx, status, payload);
        }

        private const string NoKey = "MODEL_API_KEY is not configured on the proxy";

        private static string Mode(JObject body) => Py.StrOr(Py.Get(body, "mode"), "live").ToLowerInvariant();

        private static string StateText(JObject body)
        {
            JToken s = Py.Get(body, "state_json") ?? new JValue("{}");
            return Py.Str(s);
        }

        private static long IntOr0(JObject body, string key) => Py.TryIntOr0(Py.Get(body, key), out long v) ? v : 0;

        // ------------------------------------------------------------------ /order /vision /sam /supervise

        private void HandleOrder(HttpListenerContext ctx, JObject body, string key, Stopwatch started)
        {
            string text = Py.Strip(Py.GetStr(body, "text"));
            if (text.Length == 0) { Fail(ctx, 400, "/order", started, "text is required"); return; }
            if (key == null) { Fail(ctx, 503, "/order", started, NoKey); return; }
            JObject payload = MetaApi.UserMessage(MetaApi.VisionModel, MetaApi.Text(MetaPrompts.ORDER_PROMPT + "\n\nOrder: " + text));
            MetaApi.Result r = MetaApi.CallWithRetry(MetaApi.BaseUrl + "/responses", payload, key, 60);
            if (r.Status != 200) { Fail(ctx, 502, "/order", started, "upstream status " + r.Status, new JObject { ["upstream"] = Py.Cut(r.Raw, 400) }); return; }
            string output = MetaLogic.ExtractOutputText(r.Raw);
            JObject parsed = MetaLogic.FirstJsonObject(output);
            string error = "model did not return json";
            JObject counts = MetaLogic.HasJson(parsed) ? MetaLogic.ValidateOrder(parsed, out error) : null;
            if (counts == null) { Fail(ctx, 422, "/order", started, error, new JObject { ["raw_text"] = Py.Cut(output, 800) }); return; }
            int latency = Ms(started);
            MetaApi.Log(new JObject
            {
                ["endpoint"] = "/order", ["ok"] = true, ["status"] = 200, ["model"] = MetaApi.VisionModel, ["latency_ms"] = latency, ["order"] = counts
            });
            SendJson(ctx, 200, new JObject
            {
                ["ok"] = true, ["mode"] = "live", ["model"] = MetaApi.VisionModel, ["order"] = counts,
                ["latency_ms"] = latency, ["raw_text"] = Py.Cut(output, 800)
            });
        }

        private void HandleVision(HttpListenerContext ctx, JObject body, string key, Stopwatch started)
        {
            JToken image = Py.Get(body, "image_b64");
            string observationId = Py.StrOr(Py.Get(body, "observation_id"));
            if (image == null || image.Type != JTokenType.String || image.Value<string>().Length < 100)
            {
                Fail(ctx, 400, "/vision", started, "image_b64 is required");
                return;
            }
            if (key == null) { Fail(ctx, 503, "/vision", started, NoKey); return; }
            string img = image.Value<string>();
            string prompt = Py.StrOr(Py.Get(body, "prompt"), MetaPrompts.VISION_PROMPT);
            JObject payload = MetaApi.UserMessage(MetaApi.VisionModel, MetaApi.Text(prompt), MetaApi.Image("data:image/png;base64," + img));
            MetaApi.Result r = MetaApi.CallWithRetry(MetaApi.BaseUrl + "/responses", payload, key, 90);
            if (r.Status != 200) { Fail(ctx, 502, "/vision", started, "upstream status " + r.Status, new JObject { ["upstream"] = Py.Cut(r.Raw, 400) }); return; }
            string output = MetaLogic.ExtractOutputText(r.Raw);
            JObject parsed = MetaLogic.FirstJsonObject(output);
            string error = "model did not return json";
            JArray detections = MetaLogic.HasJson(parsed) ? MetaLogic.ValidateDetections(parsed, out error) : null;
            if (detections == null) { Fail(ctx, 422, "/vision", started, error, new JObject { ["raw_text"] = Py.Cut(output, 800) }); return; }
            int latency = Ms(started);
            MetaApi.Log(new JObject
            {
                ["endpoint"] = "/vision", ["ok"] = true, ["status"] = 200, ["model"] = MetaApi.VisionModel, ["latency_ms"] = latency,
                ["image_bytes"] = img.Length, ["detections"] = detections.Count, ["observation_id"] = observationId
            });
            SendJson(ctx, 200, new JObject
            {
                ["ok"] = true, ["mode"] = "live", ["model"] = MetaApi.VisionModel, ["detections"] = detections,
                ["latency_ms"] = latency, ["raw_text"] = Py.Cut(output, 800), ["observation_id"] = observationId
            });
        }

        private void HandleSam(HttpListenerContext ctx, JObject body, string key, Stopwatch started)
        {
            JToken image = Py.Get(body, "image_b64");
            string observationId = Py.StrOr(Py.Get(body, "observation_id"));
            string mode = Mode(body);
            string concept = Py.StrOr(Py.Get(body, "concept"), "playing card suit symbol");
            if (image == null || image.Type != JTokenType.String || image.Value<string>().Length < 100)
            {
                Fail(ctx, 400, "/sam", started, "image_b64 is required");
                return;
            }
            string img = image.Value<string>();
            if (mode == "mock")
            {
                int lat = Ms(started);
                MetaApi.Log(new JObject
                {
                    ["endpoint"] = "/sam", ["ok"] = true, ["status"] = 200, ["mode"] = "mock", ["model"] = "mock",
                    ["latency_ms"] = lat, ["observation_id"] = observationId
                });
                SendJson(ctx, 200, new JObject
                {
                    ["ok"] = true, ["mode"] = "mock", ["model"] = "mock", ["latency_ms"] = lat,
                    ["raw"] = "mock SAM 3.1 inspection: unknown object segmented", ["number_count"] = 0,
                    ["observation_id"] = observationId
                });
                return;
            }
            if (key == null) { Fail(ctx, 503, "/sam", started, NoKey); return; }
            JObject payload = MetaApi.UserMessage(MetaApi.SamModel, MetaApi.Text(concept), MetaApi.Image("data:image/png;base64," + img));
            payload["stream"] = true;
            MetaApi.Result r = MetaApi.CallWithRetry(MetaApi.BaseUrl + "/responses", payload, key, 120, stream: true);
            int latency = Ms(started);
            if (r.Status != 200) { Fail(ctx, 502, "/sam", started, "upstream status " + r.Status, new JObject { ["upstream"] = Py.Cut(r.Raw, 400) }); return; }
            string output = MetaLogic.ExtractOutputText(r.Raw);
            int numbers = MetaLogic.CountNumbers(output);
            MetaApi.Log(new JObject
            {
                ["endpoint"] = "/sam", ["ok"] = true, ["status"] = 200, ["model"] = MetaApi.SamModel, ["latency_ms"] = latency,
                ["image_bytes"] = img.Length, ["output_chars"] = output.Length, ["numbers"] = numbers,
                ["observation_id"] = observationId
            });
            SendJson(ctx, 200, new JObject
            {
                ["ok"] = true, ["mode"] = "live", ["model"] = MetaApi.SamModel, ["latency_ms"] = latency,
                ["raw"] = Py.Cut(output, 4000), ["number_count"] = numbers, ["observation_id"] = observationId
            });
        }

        private void HandleSupervise(HttpListenerContext ctx, JObject body, string key, Stopwatch started)
        {
            string requestId = Py.StrOr(Py.Get(body, "request_id"));
            long stateVersion = IntOr0(body, "state_version");
            string mode = Mode(body);
            JToken stateJson = Py.Get(body, "state_json") ?? new JValue("{}");
            string trigger = Py.StrOr(Py.Get(body, "trigger"));
            if (mode == "offline")
            {
                var decision = new JObject
                {
                    ["action"] = "no_action", ["order_id"] = 0, ["priority"] = 0, ["observation_id"] = "",
                    ["reason"] = "offline mode: local safety only"
                };
                MetaApi.Log(new JObject
                {
                    ["endpoint"] = "/supervise", ["ok"] = true, ["status"] = 200, ["mode"] = "offline",
                    ["request_id"] = requestId, ["state_version"] = stateVersion, ["action"] = "no_action", ["latency_ms"] = 0
                });
                SendJson(ctx, 200, new JObject
                {
                    ["ok"] = true, ["mode"] = "offline", ["model"] = "-", ["request_id"] = requestId,
                    ["state_version"] = stateVersion, ["decision"] = decision, ["latency_ms"] = 0
                });
                return;
            }
            if (mode == "mock")
            {
                JToken mockVersion = Py.Get(body, "mock_state_version");
                if (!Py.IsNone(mockVersion) && Py.TryInt(mockVersion, out long mv) && mv >= 0) stateVersion = mv;
                JObject decision = MetaLogic.MockSupervision(stateJson, trigger, body);
                int lat = Ms(started);
                MetaApi.Log(new JObject
                {
                    ["endpoint"] = "/supervise", ["ok"] = true, ["status"] = 200, ["mode"] = "mock", ["model"] = "mock",
                    ["latency_ms"] = lat, ["request_id"] = requestId, ["state_version"] = stateVersion,
                    ["action"] = decision["action"], ["order_id"] = decision["order_id"], ["priority"] = decision["priority"]
                });
                SendJson(ctx, 200, new JObject
                {
                    ["ok"] = true, ["mode"] = "mock", ["model"] = "mock", ["request_id"] = requestId,
                    ["state_version"] = stateVersion, ["decision"] = decision, ["latency_ms"] = lat
                });
                return;
            }
            if (key == null) { Fail(ctx, 503, "/supervise", started, NoKey); return; }
            List<string> actions = MetaPrompts.SUPERVISE_ACTIONS.ToList();
            if (stateJson.Type == JTokenType.String)
            {
                JObject state = Py.ParseObject(stateJson.Value<string>());
                if (state != null && Py.Get(state, "available_actions") is JArray aa && aa.Count > 0)
                    actions = aa.Select(Py.Str).ToList();
            }
            string prompt = MetaPrompts.SUPERVISE_PROMPT.Replace("{actions}", string.Join(", ", actions)) +
                            "\n\nTrigger: " + trigger + "\nFactory state:\n" + Py.Cut(Py.Str(stateJson), 12000);
            JObject payload = MetaApi.UserMessage(MetaApi.VisionModel, MetaApi.Text(prompt));
            MetaApi.Result r = MetaApi.CallWithRetry(MetaApi.BaseUrl + "/responses", payload, key, 60);
            if (r.Status != 200) { Fail(ctx, 502, "/supervise", started, "upstream status " + r.Status, new JObject { ["upstream"] = Py.Cut(r.Raw, 400) }); return; }
            string output = MetaLogic.ExtractOutputText(r.Raw);
            JObject parsed = MetaLogic.FirstJsonObject(output);
            string error = "model did not return json";
            JObject dec = MetaLogic.HasJson(parsed) ? MetaLogic.ValidateSupervision(parsed, out error) : null;
            if (dec == null) { Fail(ctx, 422, "/supervise", started, error, new JObject { ["raw_text"] = Py.Cut(output, 800) }); return; }
            int latency = Ms(started);
            MetaApi.Log(new JObject
            {
                ["endpoint"] = "/supervise", ["ok"] = true, ["status"] = 200, ["model"] = MetaApi.VisionModel, ["latency_ms"] = latency,
                ["request_id"] = requestId, ["state_version"] = stateVersion, ["action"] = dec["action"],
                ["order_id"] = dec["order_id"], ["priority"] = dec["priority"]
            });
            SendJson(ctx, 200, new JObject
            {
                ["ok"] = true, ["mode"] = "live", ["model"] = MetaApi.VisionModel, ["request_id"] = requestId,
                ["state_version"] = stateVersion, ["decision"] = dec, ["latency_ms"] = latency, ["raw_text"] = Py.Cut(output, 800)
            });
        }

        // ------------------------------------------------------------------ /warehouse /urgent /capacity

        private void HandleWarehouse(HttpListenerContext ctx, JObject body, string key, Stopwatch started)
        {
            string requestId = Py.StrOr(Py.Get(body, "request_id"));
            long stateVersion = IntOr0(body, "state_version");
            string mode = Mode(body);
            JToken stateJson = Py.Get(body, "state_json") ?? new JValue("{}");
            string trigger = Py.StrOr(Py.Get(body, "trigger"));
            if (mode == "offline")
            {
                var plan = new JObject
                {
                    ["action"] = "no_plan", ["order_id"] = 0, ["picks"] = new JArray(), ["fallback_pallets"] = new JArray(),
                    ["bench_id"] = "", ["truck_id"] = "", ["robot_pick"] = "R1", ["robot_load"] = "R2",
                    ["start_immediately"] = true, ["start_delay_s"] = 0, ["reason"] = "offline mode: no Meta decision requested"
                };
                MetaApi.Log(new JObject
                {
                    ["endpoint"] = "/warehouse", ["ok"] = true, ["status"] = 200, ["mode"] = "offline",
                    ["request_id"] = requestId, ["state_version"] = stateVersion, ["latency_ms"] = 0
                });
                SendJson(ctx, 200, new JObject
                {
                    ["ok"] = true, ["mode"] = "offline", ["model"] = "-", ["request_id"] = requestId,
                    ["state_version"] = stateVersion, ["plan"] = plan, ["latency_ms"] = 0
                });
                return;
            }
            if (mode == "mock")
            {
                JObject plan = MetaLogic.MockWarehousePlan(stateJson, body);
                int lat = Ms(started);
                MetaApi.Log(new JObject
                {
                    ["endpoint"] = "/warehouse", ["ok"] = true, ["status"] = 200, ["mode"] = "mock", ["model"] = "mock",
                    ["latency_ms"] = lat, ["request_id"] = requestId, ["state_version"] = stateVersion,
                    ["action"] = plan["action"], ["order_id"] = plan["order_id"]
                });
                SendJson(ctx, 200, new JObject
                {
                    ["ok"] = true, ["mode"] = "mock", ["model"] = "mock", ["request_id"] = requestId,
                    ["state_version"] = stateVersion, ["plan"] = plan, ["latency_ms"] = lat
                });
                return;
            }
            if (key == null) { Fail(ctx, 503, "/warehouse", started, NoKey); return; }
            string prompt = MetaPrompts.WAREHOUSE_PROMPT + "\n\nTrigger: " + trigger + "\nWarehouse state:\n" + Py.Cut(Py.Str(stateJson), 16000);
            JObject payload = MetaApi.UserMessage(MetaApi.OrchestratorModel, MetaApi.Text(prompt));
            MetaApi.OrchestratorResult r = MetaApi.CallOrchestrator(payload, key);
            if (r.Status != 200)
            {
                Fail(ctx, 502, "/warehouse", started, "upstream status " + r.Status, new JObject { ["upstream"] = Py.Cut(r.Raw, 400), ["model"] = r.Model });
                return;
            }
            string output = MetaLogic.ExtractOutputText(r.Raw);
            JObject parsed = MetaLogic.FirstJsonObject(output);
            string error = "model did not return json";
            JObject valid = MetaLogic.HasJson(parsed) ? MetaLogic.ValidateWarehousePlan(parsed, out error) : null;
            if (valid == null)
            {
                Fail(ctx, 422, "/warehouse", started, error, new JObject { ["raw_text"] = Py.Cut(output, 800), ["model"] = r.Model });
                return;
            }
            int latency = Ms(started);
            MetaApi.Log(new JObject
            {
                ["endpoint"] = "/warehouse", ["ok"] = true, ["status"] = 200, ["model"] = r.Model,
                ["latency_ms"] = latency, ["request_id"] = requestId, ["state_version"] = stateVersion,
                ["action"] = valid["action"], ["order_id"] = valid["order_id"],
                ["picks"] = ((JArray)valid["picks"]).Count, ["bench"] = valid["bench_id"], ["truck"] = valid["truck_id"],
                ["note"] = r.Note
            });
            var response = new JObject
            {
                ["ok"] = true, ["mode"] = "live", ["model"] = r.Model, ["request_id"] = requestId,
                ["state_version"] = stateVersion, ["plan"] = valid, ["latency_ms"] = latency, ["raw_text"] = Py.Cut(output, 800)
            };
            if (!string.IsNullOrEmpty(r.Note)) response["note"] = r.Note;
            SendJson(ctx, 200, response);
        }

        private void HandleUrgent(HttpListenerContext ctx, JObject body, string key, Stopwatch started)
        {
            string requestId = Py.StrOr(Py.Get(body, "request_id"));
            long stateVersion = IntOr0(body, "state_version");
            long urgentOrderId = IntOr0(body, "urgent_order_id");
            string mode = Mode(body);
            string transcript = Py.Strip(Py.StrOr(Py.Get(body, "transcript")));
            JToken stateJson = Py.Get(body, "state_json") ?? new JValue("{}");
            if (mode == "offline")
            {
                MetaApi.Log(new JObject
                {
                    ["endpoint"] = "/urgent", ["ok"] = true, ["status"] = 200, ["mode"] = "offline",
                    ["request_id"] = requestId, ["state_version"] = stateVersion, ["latency_ms"] = 0
                });
                SendJson(ctx, 200, new JObject
                {
                    ["ok"] = true, ["mode"] = "offline", ["model"] = "-", ["request_id"] = requestId, ["state_version"] = stateVersion,
                    ["result"] = new JObject
                    {
                        ["action"] = "no_plan", ["urgent"] = false, ["order"] = new JObject { ["A"] = 0, ["B"] = 0, ["C"] = 0 },
                        ["confidence"] = 0.0, ["clarification"] = "", ["plan"] = JValue.CreateNull(),
                        ["reason"] = "offline mode: no Meta decision requested"
                    },
                    ["latency_ms"] = 0
                });
                return;
            }
            if (mode == "mock")
            {
                JObject result = MetaLogic.MockUrgent(body);
                int lat = Ms(started);
                MetaApi.Log(new JObject
                {
                    ["endpoint"] = "/urgent", ["ok"] = true, ["status"] = 200, ["mode"] = "mock", ["model"] = "mock",
                    ["latency_ms"] = lat, ["request_id"] = requestId, ["state_version"] = stateVersion,
                    ["action"] = result["action"], ["urgent"] = result["urgent"]
                });
                SendJson(ctx, 200, new JObject
                {
                    ["ok"] = true, ["mode"] = "mock", ["model"] = "mock", ["request_id"] = requestId,
                    ["state_version"] = stateVersion, ["result"] = result, ["latency_ms"] = lat
                });
                return;
            }
            if (transcript.Length == 0) { Fail(ctx, 400, "/urgent", started, "transcript is required"); return; }
            if (urgentOrderId <= 0) { Fail(ctx, 400, "/urgent", started, "urgent_order_id is required"); return; }
            if (key == null) { Fail(ctx, 503, "/urgent", started, NoKey); return; }
            string prompt = MetaPrompts.URGENT_PROMPT.Replace("{transcript}", transcript).Replace("{order_id}", urgentOrderId.ToString());
            prompt += "\n\nWarehouse state:\n" + Py.Cut(Py.Str(stateJson), 16000);
            JObject payload = MetaApi.UserMessage(MetaApi.OrchestratorModel, MetaApi.Text(prompt));
            MetaApi.OrchestratorResult r = MetaApi.CallOrchestrator(payload, key);
            if (r.Status != 200)
            {
                Fail(ctx, 502, "/urgent", started, "upstream status " + r.Status, new JObject { ["upstream"] = Py.Cut(r.Raw, 400), ["model"] = r.Model });
                return;
            }
            string output = MetaLogic.ExtractOutputText(r.Raw);
            JObject parsed = MetaLogic.FirstJsonObject(output);
            string error = "model did not return json";
            JObject valid = MetaLogic.HasJson(parsed) ? MetaLogic.ValidateUrgent(parsed, urgentOrderId, out error) : null;
            if (valid == null)
            {
                Fail(ctx, 422, "/urgent", started, error, new JObject { ["raw_text"] = Py.Cut(output, 800), ["model"] = r.Model });
                return;
            }
            int latency = Ms(started);
            MetaApi.Log(new JObject
            {
                ["endpoint"] = "/urgent", ["ok"] = true, ["status"] = 200, ["model"] = r.Model,
                ["latency_ms"] = latency, ["request_id"] = requestId, ["state_version"] = stateVersion,
                ["action"] = valid["action"], ["urgent"] = valid["urgent"], ["confidence"] = valid["confidence"],
                ["order"] = valid["order"], ["transcript_len"] = transcript.Length, ["note"] = r.Note
            });
            var response = new JObject
            {
                ["ok"] = true, ["mode"] = "live", ["model"] = r.Model, ["request_id"] = requestId,
                ["state_version"] = stateVersion, ["result"] = valid, ["latency_ms"] = latency, ["raw_text"] = Py.Cut(output, 800)
            };
            if (!string.IsNullOrEmpty(r.Note)) response["note"] = r.Note;
            SendJson(ctx, 200, response);
        }

        private void HandleCapacity(HttpListenerContext ctx, JObject body, string key, Stopwatch started)
        {
            string requestId = Py.StrOr(Py.Get(body, "request_id"));
            string mode = Mode(body);
            JToken capRaw = Py.Get(body, "capacity_json");
            string capText = Py.Truthy(capRaw) ? Py.Str(capRaw) : "{}";
            JObject capCtx;
            try { capCtx = Py.Parse(capText) as JObject; }
            catch (Exception) { Fail(ctx, 400, "/capacity", started, "capacity_json is not valid json"); return; }
            if (capCtx == null) capCtx = new JObject();
            if (mode == "mock" || mode == "offline")
            {
                JObject result = MetaLogic.MockCapacity(capCtx);
                int lat = Ms(started);
                MetaApi.Log(new JObject
                {
                    ["endpoint"] = "/capacity", ["ok"] = true, ["status"] = 200, ["mode"] = mode, ["model"] = "mock",
                    ["latency_ms"] = lat, ["request_id"] = requestId, ["action"] = result["action"], ["order_id"] = result["order_id"]
                });
                SendJson(ctx, 200, new JObject
                {
                    ["ok"] = true, ["mode"] = mode, ["model"] = "mock", ["request_id"] = requestId, ["result"] = result, ["latency_ms"] = lat
                });
                return;
            }
            if (key == null) { Fail(ctx, 503, "/capacity", started, NoKey); return; }
            string prompt = MetaPrompts.CAPACITY_PROMPT + "\n\nFINISH TO FREE candidates and urgent need:\n" + Py.Cut(Py.Dumps(capCtx), 12000) +
                            "\n\nWarehouse state (context):\n" + Py.Cut(Py.Str(Py.Get(body, "state_json") ?? new JValue("{}")), 10000);
            JObject payload = MetaApi.UserMessage(null, MetaApi.Text(prompt));
            MetaApi.OrchestratorResult r = MetaApi.CallOrchestrator(payload, key);
            if (r.Status != 200)
            {
                Fail(ctx, 502, "/capacity", started, "upstream status " + r.Status, new JObject { ["upstream"] = Py.Cut(r.Raw, 400), ["model"] = r.Model });
                return;
            }
            string output = MetaLogic.ExtractOutputText(r.Raw);
            JObject parsed = MetaLogic.FirstJsonObject(output);
            string error = "model did not return json";
            JObject valid = MetaLogic.HasJson(parsed) ? MetaLogic.ValidateCapacity(parsed, capCtx, out error) : null;
            if (valid == null)
            {
                Fail(ctx, 422, "/capacity", started, error, new JObject { ["raw_text"] = Py.Cut(output, 800), ["model"] = r.Model });
                return;
            }
            int latency = Ms(started);
            int candidates = Py.Get(capCtx, "candidates") is JArray ca ? ca.Count : 0;
            MetaApi.Log(new JObject
            {
                ["endpoint"] = "/capacity", ["ok"] = true, ["status"] = 200, ["model"] = r.Model, ["latency_ms"] = latency,
                ["request_id"] = requestId, ["action"] = valid["action"], ["order_id"] = valid["order_id"],
                ["candidates"] = candidates, ["note"] = r.Note
            });
            var response = new JObject
            {
                ["ok"] = true, ["mode"] = "live", ["model"] = r.Model, ["request_id"] = requestId, ["result"] = valid, ["latency_ms"] = latency
            };
            if (!string.IsNullOrEmpty(r.Note)) response["note"] = r.Note;
            SendJson(ctx, 200, response);
        }

        // ------------------------------------------------------------------ ASR temps réel

        private AsrSession GetSession(string id)
        {
            lock (sessionsLock)
            {
                if (!sessions.TryGetValue(id, out AsrSession s)) return null;
                if ((DateTime.UtcNow - s.Created).TotalSeconds > AsrSession.TtlSeconds)
                {
                    s.Abort();
                    sessions.Remove(id);
                    return null;
                }
                return s;
            }
        }

        private void DropSession(string id)
        {
            AsrSession s;
            lock (sessionsLock)
            {
                if (!sessions.TryGetValue(id, out s)) return;
                sessions.Remove(id);
            }
            s.Abort();
        }

        private void HandleAsrStart(HttpListenerContext ctx, JObject body, string key, Stopwatch started)
        {
            string mode = Mode(body);
            string sessionId = Py.StrOr(Py.Get(body, "session_id"));
            if (sessionId.Length == 0) sessionId = "asr-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            JArray keywords = Py.Get(body, "keywords") as JArray ?? new JArray();
            JArray langTok = Py.Get(body, "languages") as JArray ?? new JArray();
            List<string> languages = langTok.Select(Py.Str).ToList();
            if (languages.Count == 0) languages = new List<string> { "French", "English" };
            var merged = new List<string>();
            foreach (string k in new[] { "palette", "banc", "camion", "carton", "commande urgente", "caisse bois", "gros carton", "carton moyen" }
                         .Concat(keywords.Select(Py.Str)))
                if (Py.Strip(k).Length > 0 && !merged.Contains(k)) merged.Add(k);
            AsrSession session;
            if (mode == "mock")
                session = new AsrSession(sessionId, merged, languages, null, true, Py.StrOr(Py.Get(body, "mock_text")));
            else
            {
                if (key == null) { Fail(ctx, 503, "/asr/start", started, NoKey); return; }
                try { session = new AsrSession(sessionId, merged, languages, key); }
                catch (Exception e)
                {
                    Exception inner = e;
                    while (inner.InnerException != null) inner = inner.InnerException;
                    Fail(ctx, 502, "/asr/start", started, "asr connect failed: " + Py.Cut(inner.Message, 300));
                    return;
                }
            }
            lock (sessionsLock) sessions[session.Id] = session;
            int latency = Ms(started);
            string model = mode == "mock" ? "mock" : MetaApi.VoiceModel;
            MetaApi.Log(new JObject
            {
                ["endpoint"] = "/asr/start", ["ok"] = true, ["status"] = 200, ["mode"] = mode, ["model"] = model,
                ["latency_ms"] = latency, ["session_id"] = session.Id, ["keywords"] = merged.Count,
                ["languages"] = new JArray(languages.ToArray())
            });
            SendJson(ctx, 200, new JObject
            {
                ["ok"] = true, ["mode"] = mode, ["model"] = model, ["session_id"] = session.Id, ["latency_ms"] = latency
            });
        }

        private void HandleAsrAudio(HttpListenerContext ctx, JObject body, Stopwatch started)
        {
            string sessionId = Py.StrOr(Py.Get(body, "session_id"));
            AsrSession session = GetSession(sessionId);
            if (session == null) { SendJson(ctx, 404, new JObject { ["ok"] = false, ["error"] = "unknown or expired session" }); return; }
            JToken pcmTok = Py.Get(body, "pcm_b64");
            int queued = 0;
            if (pcmTok != null && pcmTok.Type == JTokenType.String && pcmTok.Value<string>().Length > 0)
            {
                byte[] pcm;
                // base64.b64decode (non strict) : caractères hors alphabet ignorés, comme en Python
                var clean = new StringBuilder();
                foreach (char ch in pcmTok.Value<string>())
                    if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '+' || ch == '/' || ch == '=')
                        clean.Append(ch);
                try { pcm = Convert.FromBase64String(clean.ToString()); }
                catch (FormatException)
                {
                    SendJson(ctx, 400, new JObject { ["ok"] = false, ["error"] = "pcm_b64 is not valid base64" });
                    return;
                }
                queued = pcm.Length;
                if (session.Mock)
                {
                    // Démo sans micro : le partiel révèle le texte progressivement.
                    if (session.MockText.Length > 0)
                    {
                        session.StatusReveal = Math.Min(session.MockText.Length, session.StatusReveal + 14);
                        session.Partial = session.MockText.Substring(0, session.StatusReveal);
                    }
                }
                else session.Enqueue(pcm); // session en fermeture : accepté mais ignoré, l'état le signale
            }
            int latency = Ms(started);
            JObject status = session.Status();
            status["ok"] = true;
            status["session_id"] = session.Id;
            status["bytes"] = queued;
            status["latency_ms"] = latency;
            SendJson(ctx, 200, status);
        }

        private void HandleAsrFinish(HttpListenerContext ctx, JObject body, Stopwatch started)
        {
            string sessionId = Py.StrOr(Py.Get(body, "session_id"));
            AsrSession session = GetSession(sessionId);
            if (session == null) { SendJson(ctx, 404, new JObject { ["ok"] = false, ["error"] = "unknown or expired session" }); return; }
            if (session.Mock)
            {
                int lat = Ms(started);
                string mockTranscript = session.MockText.Length > 0 ? session.MockText : session.Partial;
                DropSession(session.Id);
                MetaApi.Log(new JObject
                {
                    ["endpoint"] = "/asr/finish", ["ok"] = true, ["status"] = 200, ["mode"] = "mock",
                    ["model"] = "mock", ["latency_ms"] = lat, ["session_id"] = sessionId
                });
                SendJson(ctx, 200, new JObject
                {
                    ["ok"] = true, ["mode"] = "mock", ["model"] = "mock", ["session_id"] = sessionId,
                    ["transcript"] = mockTranscript, ["final"] = mockTranscript.Length > 0, ["timed_out"] = false,
                    ["session_closed"] = true, ["audio_ms"] = 0, ["latency_ms"] = lat
                });
                return;
            }
            session.Finish();
            // Attendre la transcription finale : durée de l'audio en file + marge de vidage du modèle.
            double waitS = Math.Min(45.0, session.QueuedMs / 1000.0 + 15.0);
            bool gotFinal = session.FinalEvent.Wait(TimeSpan.FromSeconds(waitS));
            string transcript = session.FinalText.Length > 0 ? session.FinalText : session.Error == null ? session.Partial : "";
            int latency = Ms(started);
            string error = session.Error;
            bool done = session.Done.IsSet;
            DropSession(session.Id);
            MetaApi.Log(new JObject
            {
                ["endpoint"] = "/asr/finish", ["ok"] = error == null, ["status"] = 200,
                ["model"] = MetaApi.VoiceModel, ["latency_ms"] = latency, ["session_id"] = sessionId,
                ["final"] = session.FinalText.Length > 0, ["audio_ms"] = session.AudioMs,
                ["transcript_len"] = transcript.Length, ["error"] = error == null ? JValue.CreateNull() : new JValue(error)
            });
            SendJson(ctx, 200, new JObject
            {
                ["ok"] = error == null, ["session_id"] = sessionId, ["transcript"] = transcript,
                ["final"] = session.FinalText.Length > 0, ["audio_ms"] = session.AudioMs, ["timed_out"] = !gotFinal,
                ["session_closed"] = done, ["error"] = error == null ? JValue.CreateNull() : new JValue(error),
                ["latency_ms"] = latency
            });
        }

        private void HandleAsrCancel(HttpListenerContext ctx, JObject body, Stopwatch started)
        {
            string sessionId = Py.StrOr(Py.Get(body, "session_id"));
            DropSession(sessionId);
            int latency = Ms(started);
            MetaApi.Log(new JObject
            {
                ["endpoint"] = "/asr/cancel", ["ok"] = true, ["status"] = 200, ["latency_ms"] = latency, ["session_id"] = sessionId
            });
            SendJson(ctx, 200, new JObject { ["ok"] = true, ["session_id"] = sessionId, ["latency_ms"] = latency });
        }
    }
}
