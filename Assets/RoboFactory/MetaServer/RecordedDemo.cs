using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RoboFactory.MetaServer
{
    public enum DemoRunKind { Off, Replay, Record }

    /// <summary>
    /// Records the real answers of Meta (LIVE AI) and replays them (RECORDED DEMO).
    ///
    /// It sits in the built-in Meta server, in front of the three decision routes
    /// (/warehouse, /urgent, /capacity): the orchestrator, the plan validator, the robots, the
    /// doors and the trucks are the very same in both modes. Nothing is ever invented:
    ///   - a recording only contains answers that Meta really returned ("ok" and mode "live");
    ///   - a replay serves them back unchanged (model, plan, reason and latency as recorded), only
    ///     the request id / state version are rewritten and the mode is flagged "recorded";
    ///   - if the replay diverges and no recorded answer is left, the call fails honestly.
    /// The API key is never part of a recording (a call that would contain it is dropped).
    /// </summary>
    public static class RecordedDemoRuntime
    {
        public const int FormatVersion = 1;

        private static readonly object gate = new object();
        private static readonly Stopwatch clock = new Stopwatch();
        private static readonly ConditionalWeakTable<object, PendingCall> pending = new ConditionalWeakTable<object, PendingCall>();

        private static DemoRunKind kind = DemoRunKind.Off;
        private static string scenarioId = "";
        private static string scenarioTitle = "";
        private static List<JObject> replayCalls = new List<JObject>();
        private static bool[] consumed = new bool[0];
        private static readonly List<JObject> capturedCalls = new List<JObject>();
        private static readonly List<JObject> capturedInputs = new List<JObject>();
        private static int replayServed;

        private sealed class PendingCall
        {
            public string Route;
            public JObject Body;
            public double T;
        }

        public static DemoRunKind Kind { get { lock (gate) return kind; } }
        public static bool Replaying { get { lock (gate) return kind == DemoRunKind.Replay; } }
        public static bool Recording { get { lock (gate) return kind == DemoRunKind.Record; } }
        public static string ScenarioId { get { lock (gate) return scenarioId; } }
        public static string ScenarioTitle { get { lock (gate) return scenarioTitle; } }
        public static double Elapsed => clock.Elapsed.TotalSeconds;
        public static int CapturedCallCount { get { lock (gate) return capturedCalls.Count; } }
        public static int ReplayTotal { get { lock (gate) return replayCalls.Count; } }
        public static int ReplayServed { get { lock (gate) return replayServed; } }
        public static int ReplayRemaining { get { lock (gate) { int n = 0; foreach (bool c in consumed) if (!c) n++; return n; } } }

        public static bool IsDecisionRoute(string route)
        {
            return route == "/warehouse" || route == "/urgent" || route == "/capacity";
        }

        public static string KindName
        {
            get
            {
                switch (Kind)
                {
                    case DemoRunKind.Replay: return "recorded";
                    case DemoRunKind.Record: return "live-recording";
                    default: return "live";
                }
            }
        }

        // ------------------------------------------------------------------ lifecycle

        public static void Stop()
        {
            lock (gate)
            {
                kind = DemoRunKind.Off;
                replayCalls = new List<JObject>();
                consumed = new bool[0];
                capturedCalls.Clear();
                capturedInputs.Clear();
                scenarioId = scenarioTitle = "";
                replayServed = 0;
                clock.Reset();
            }
        }

        /// <summary>Parses a recording. Returns null and an error if the file is not usable.</summary>
        public static JObject Parse(string text, out string error)
        {
            error = "";
            try
            {
                var root = JObject.Parse(text);
                if ((int?)root["format"] != FormatVersion) { error = "unsupported recording format"; return null; }
                if (!(root["calls"] is JArray calls)) { error = "recording has no calls"; return null; }
                if (!(root["inputs"] is JArray)) { error = "recording has no inputs"; return null; }
                foreach (JToken c in calls)
                {
                    var call = c as JObject;
                    if (call == null || call["response"] == null || (call["route"] ?? "").ToString().Length == 0)
                    { error = "recording contains a malformed call"; return null; }
                }
                return root;
            }
            catch (Exception e) { error = "invalid recording: " + e.Message; return null; }
        }

        public static void StartReplay(JObject recording)
        {
            lock (gate)
            {
                kind = DemoRunKind.Replay;
                scenarioId = (string)recording["scenario"] ?? "";
                scenarioTitle = (string)recording["title"] ?? scenarioId;
                replayCalls = new List<JObject>();
                foreach (JToken c in (JArray)recording["calls"]) replayCalls.Add((JObject)c);
                consumed = new bool[replayCalls.Count];
                replayServed = 0;
                capturedCalls.Clear();
                capturedInputs.Clear();
                clock.Restart();
            }
        }

        public static void StartRecord(string id, string title)
        {
            lock (gate)
            {
                kind = DemoRunKind.Record;
                scenarioId = id ?? "";
                scenarioTitle = title ?? id ?? "";
                capturedCalls.Clear();
                capturedInputs.Clear();
                replayCalls = new List<JObject>();
                consumed = new bool[0];
                clock.Restart();
            }
        }

        // ------------------------------------------------------------------ replay

        /// <summary>
        /// Next recorded answer for this call. The trigger (/warehouse) or the transcription (/urgent) is
        /// matched first; otherwise the next unused answer of the same route is served in order.
        /// </summary>
        public static bool TryReplay(string route, JObject body, out JObject response, out int delayMs, out string error)
        {
            response = null; delayMs = 0; error = "";
            lock (gate)
            {
                if (kind != DemoRunKind.Replay) { error = "no recording is being replayed"; return false; }
                string trigger = (string)body["trigger"] ?? "";
                string transcript = Normalize((string)body["transcript"]);
                int firstSameRoute = -1, exact = -1;
                for (int i = 0; i < replayCalls.Count; i++)
                {
                    if (consumed[i]) continue;
                    JObject c = replayCalls[i];
                    if ((string)c["route"] != route) continue;
                    if (firstSameRoute < 0) firstSameRoute = i;
                    bool match = route == "/warehouse" ? (string)c["trigger"] == trigger
                               : route == "/urgent" ? Normalize((string)c["transcript"]) == transcript
                               : true;
                    if (match) { exact = i; break; }
                }
                // An urgent order is only ever served for the very sentence that was recorded (never another one).
                int pick = route == "/urgent" ? exact : (exact >= 0 ? exact : firstSameRoute);
                bool reused = false;
                if (pick < 0 && route == "/urgent")
                {
                    // The orchestrator asks Meta again with the same sentence while it waits (e.g. after "wait"):
                    // the real recorded answer for that sentence is served again, so the demo never fails.
                    for (int i = replayCalls.Count - 1; i >= 0; i--)
                        if ((string)replayCalls[i]["route"] == route && Normalize((string)replayCalls[i]["transcript"]) == transcript)
                        { pick = i; reused = true; break; }
                }
                if (pick < 0)
                {
                    error = "RECORDED DEMO: no recorded Meta answer left for " + route +
                            " (the scenario diverged from its recording)";
                    return false;
                }
                if (!reused) consumed[pick] = true;
                replayServed++;
                JObject call = replayCalls[pick];
                response = (JObject)call["response"].DeepClone();
                string requestId = (string)body["request_id"];
                if (!string.IsNullOrEmpty(requestId) && (route != "/capacity" || response["request_id"] != null))
                    response["request_id"] = requestId;
                if (response["state_version"] != null && body["state_version"] != null)
                    response["state_version"] = body["state_version"];
                response["mode"] = "recorded";
                response["recorded"] = true;
                delayMs = Math.Max(0, Math.Min(120000, (int?)call["latency_ms"] ?? 0));
                return true;
            }
        }

        private static JObject FindRecordedUrgent(string transcript)
        {
            lock (gate)
            {
                string t = Normalize(transcript);
                foreach (JObject c in replayCalls)
                    if ((string)c["route"] == "/urgent" && Normalize((string)c["transcript"]) == t) return c;
                return null;
            }
        }

        /// <summary>True if the recorded Meta answer to this urgent sentence was "wait" / "no_plan" / "defer_order".</summary>
        public static bool RecordedUrgentWaits(string transcript)
        {
            JObject c = FindRecordedUrgent(transcript);
            string action = (string)c?["response"]?["result"]?["action"] ?? "";
            return action == "wait" || action == "no_plan" || action == "defer_order";
        }

        /// <summary>The A/B/C quantities Meta understood for this recorded urgent sentence.</summary>
        public static bool TryRecordedUrgentOrder(string transcript, out int a, out int b, out int c)
        {
            a = b = c = 0;
            JToken o = FindRecordedUrgent(transcript)?["response"]?["result"]?["order"];
            if (o == null || o.Type != JTokenType.Object) return false;
            a = (int?)o["A"] ?? 0; b = (int?)o["B"] ?? 0; c = (int?)o["C"] ?? 0;
            return a + b + c > 0;
        }

        private static string Normalize(string s)
        {
            return (s ?? "").Trim().ToLowerInvariant();
        }

        // ------------------------------------------------------------------ recording

        public static void CaptureRequest(object ctx, string route, JObject body)
        {
            if (ctx == null || !Recording || !IsDecisionRoute(route)) return;
            pending.Remove(ctx);
            pending.Add(ctx, new PendingCall { Route = route, Body = body, T = clock.Elapsed.TotalSeconds });
        }

        public static void CaptureResponse(object ctx, int status, JObject payload)
        {
            if (ctx == null || payload == null) return;
            if (!pending.TryGetValue(ctx, out PendingCall p)) return;
            pending.Remove(ctx);
            bool ok = payload["ok"] != null && payload["ok"].Type == JTokenType.Boolean && (bool)payload["ok"];
            if (status != 200 || !ok) return;
            if (((string)payload["mode"] ?? "") != "live") return;       // never a mock, offline or replayed answer
            string model = (string)payload["model"] ?? "";
            if (model.Length == 0 || model == "mock" || model == "-") return;

            string json = payload.ToString(Formatting.None);
            string key = MetaApi.LoadKey();
            if (!string.IsNullOrEmpty(key) && json.IndexOf(key, StringComparison.Ordinal) >= 0) return; // never a secret

            var call = new JObject
            {
                ["route"] = p.Route,
                ["t"] = Math.Round(p.T, 2),
                ["trigger"] = (string)p.Body["trigger"] ?? "",
                ["transcript"] = (string)p.Body["transcript"] ?? "",
                ["request_id"] = (string)p.Body["request_id"] ?? "",
                ["latency_ms"] = (int?)payload["latency_ms"] ?? 0,
                ["model"] = model,
                ["response"] = payload.DeepClone()
            };
            lock (gate)
            {
                if (kind != DemoRunKind.Record) return;
                call["seq"] = capturedCalls.Count;
                capturedCalls.Add(call);
            }
        }

        /// <summary>Order or urgent order typed/spoken/scripted during the recording (time since the start).</summary>
        public static void AddInput(JObject input)
        {
            lock (gate)
            {
                if (kind != DemoRunKind.Record) return;
                capturedInputs.Add(input);
            }
        }

        /// <summary>Writes the recording. Returns the file path, or "" if nothing could be written.</summary>
        public static string Save(string directory, bool complete, out string error)
        {
            error = "";
            lock (gate)
            {
                if (kind != DemoRunKind.Record) { error = "no recording in progress"; return ""; }
                if (capturedCalls.Count == 0) { error = "no Meta answer was captured"; return ""; }
                // Urgent orders are derived from the real /urgent calls (first occurrence of each transcription:
                // later identical calls are the orchestrator's own "wait" retries, not new orders).
                var inputs = new List<JObject>(capturedInputs);
                var seen = new HashSet<string>();
                foreach (JObject c in capturedCalls)
                {
                    if ((string)c["route"] != "/urgent") continue;
                    string text = ((string)c["transcript"] ?? "").Trim();
                    if (text.Length == 0 || !seen.Add(text.ToLowerInvariant())) continue;
                    inputs.Add(new JObject { ["t"] = c["t"], ["kind"] = "urgent", ["transcript"] = text });
                }
                inputs.Sort((x, y) => ((double)x["t"]).CompareTo((double)y["t"]));
                var root = new JObject
                {
                    ["format"] = FormatVersion,
                    ["scenario"] = scenarioId,
                    ["title"] = scenarioTitle,
                    ["source"] = "live Meta Model API",
                    ["recorded_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    ["complete"] = complete,
                    ["inputs"] = new JArray(inputs),
                    ["calls"] = new JArray(capturedCalls)
                };
                try
                {
                    Directory.CreateDirectory(directory);
                    string path = Path.Combine(directory, scenarioId + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") +
                                               (complete ? "" : "_partial") + ".json");
                    File.WriteAllText(path, root.ToString(Formatting.Indented), new UTF8Encoding(false));
                    return path;
                }
                catch (Exception e) { error = e.Message; return ""; }
            }
        }
    }
}
