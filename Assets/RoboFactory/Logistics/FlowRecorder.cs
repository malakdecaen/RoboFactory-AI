using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RoboFactory.Hackathon.Orchestration;
using RoboFactory.Integration;
using UnityEngine;
using RoboFactory.Sim;

namespace RoboFactory.Logistics
{
    /// <summary>
    /// FLOW journal (read-only): tells every action and every decision in order —
    /// orders, Muse decisions (trigger, latency, plan, reason, refusal), robots (who does
    /// what, for which order, for how long), doors, boxes, urgent orders, voice — then
    /// writes a SUMMARY: duration of each phase per order, robot timetables, Muse
    /// calls, and the list of useless steps (round trips to base, waits, calls
    /// without effect, doors opened for nothing…). File: &lt;project&gt;/logs/flow_*.log.
    /// Changes nothing in the simulation.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FlowRecorder : MonoBehaviour
    {
        public float Interval = .1f;
        [Tooltip("Summary written automatically when nothing has been running for this delay (s).")]
        public float IdleSummaryDelay = 5f;

        private StreamWriter writer;
        public static string FilePath { get; private set; } = "";
        private MuseOrchestrator orch;
        private VoiceCommandController voice;
        private float next;
        private float lastLineT = -1f;

        // ---------------------------------------------------------------- per-actor tracking
        private sealed class Segment
        {
            public string Actor, Label, Category;
            public int OrderId = -1;
            public float Start, End;
            public float Duration => End - Start;
        }

        private sealed class ActorState
        {
            public string Label = "";
            public string Category = "";
            public int OrderId = -1;
            public float Since;
        }

        private readonly Dictionary<string, ActorState> actors = new Dictionary<string, ActorState>();
        private readonly List<Segment> segments = new List<Segment>();

        private sealed class OrderTrack
        {
            public int Id;
            public bool Urgent;
            public string Composition = "";
            public float Created = -1f, Delivered = -1f;
            public string Status = "";
            public float StatusSince;
            public readonly Dictionary<string, float> PhaseTime = new Dictionary<string, float>();
            public int Decisions;
            public float DecisionLatency;
            public string Bench = "", Truck = "";
            public float QueuedAt = -1f;
        }

        private readonly Dictionary<int, OrderTrack> orderTracks = new Dictionary<int, OrderTrack>();

        private sealed class MuseCall
        {
            public string Id, Trigger, Action, Result;
            public int OrderId, LatencyMs;
            public bool Accepted, Useful;
        }

        private readonly List<MuseCall> museCalls = new List<MuseCall>();
        private readonly HashSet<object> seenRecords = new HashSet<object>();

        private readonly Dictionary<string, string> doorState = new Dictionary<string, string>();
        private readonly Dictionary<string, float> doorOpenSince = new Dictionary<string, float>();
        private readonly Dictionary<string, float> doorOpenTotal = new Dictionary<string, float>();
        private readonly Dictionary<string, float> doorLoadingTotal = new Dictionary<string, float>();

        private int routeChanges, suspensions, incidentsCount, cartonMoves;
        private int samCalls, samWithObjects, samFailed, samVerified, samMismatches, samOrdersVerified;
        private int packScans, packScanWrong, packScanFailed, packTaken, packRejected, packCartons, packMissed, packDelivered;
        private readonly List<string> incidentLines = new List<string>();
        private string lastStage = "", lastVoiceState = "", lastFinal = "", lastFailure = "";
        private float idleSince = -1f;
        private bool summaryWrittenForIdle;
        private bool anyOrderSeen;

        // ---------------------------------------------------------------- setup

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoInstall()
        {
            if (FindFirstObjectByType<MuseOrchestrator>() == null) return;
            if (FindFirstObjectByType<FlowRecorder>() != null) return;
            new GameObject("FlowRecorder").AddComponent<FlowRecorder>();
        }

        private void OnEnable() { Application.logMessageReceived += OnUnityLog; }

        private void OnDisable()
        {
            Application.logMessageReceived -= OnUnityLog;
            Finish();
        }

        private void OnApplicationQuit() { Finish(); }

        private void Start()
        {
            orch = FindFirstObjectByType<MuseOrchestrator>();
            Open();
            Line("SESSION", "start — mode " + (orch != null ? orch.Mode : "?") +
                            (orch != null && orch.ReserveUrgentLane ? "  ·  fixed urgent lane " + orch.UrgentBenchId + "/" + orch.UrgentTruckId : "") +
                            "  ·  raw state log: " + Path.GetFileName(MuseTrace.FilePath));
        }

        private void Open()
        {
            try
            {
                string dir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "logs");
                Directory.CreateDirectory(dir);
                FilePath = Path.Combine(dir, "flow_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
                writer = new StreamWriter(FilePath, false, new UTF8Encoding(false)) { AutoFlush = true };
                writer.WriteLine("# RoboFactory — flow journal " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                                 "  scene=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
                writer.WriteLine("# columns: real time | game time | gap since the previous line | actor | action");
                writer.WriteLine("# actors: ORDER, MUSE, SAM, PACK, R1, R2, DOOR, BOX, URGENT, VOICE, INCIDENT; a SUMMARY follows each end of activity");
                writer.WriteLine();
                Debug.Log("[FlowRecorder] flow journal: " + FilePath);
            }
            catch (Exception e)
            {
                writer = null;
                Debug.LogWarning("[FlowRecorder] journal not opened: " + e.Message);
            }
        }

        private void Line(string actor, string message)
        {
            if (writer == null) return;
            try
            {
                float t = Time.time;
                string gap = lastLineT < 0f ? "" : "+" + (t - lastLineT).ToString("F1") + "s";
                lastLineT = t;
                writer.WriteLine(DateTime.Now.ToString("HH:mm:ss.f") + "  t=" + t.ToString("F1").PadLeft(6) +
                                 "  " + gap.PadLeft(7) + "  " + actor.PadRight(8) + " " + message);
            }
            catch (Exception) { }
        }

        // ---------------------------------------------------------------- Unity messages

        private void OnUnityLog(string message, string stack, LogType type)
        {
            if (string.IsNullOrEmpty(message) || message.StartsWith("[FlowRecorder]") || message.StartsWith("[MuseTrace]")) return;
            if (type == LogType.Exception || type == LogType.Error)
            {
                Line("ERROR", message.Split('\n')[0]);
                return;
            }
            if (message.StartsWith("[SAM] "))
            {
                // SAM 3.1 on a robot camera (SamVisionService): "R1 pallet P02 'box': 3 segmented (scene 4) · sam-3.1 1.8 s"
                string text = message.Substring("[SAM] ".Length);
                if (text.Contains(" segmented"))
                {
                    samCalls++;
                    if (!text.Contains(": 0 segmented")) samWithObjects++;
                    if (text.Contains(" · VERIFIED")) samVerified++;
                    if (text.Contains(" · MISMATCH")) samMismatches++;
                    if (text.Contains(" · ORDER #") && text.Contains(" VERIFIED (")) samOrdersVerified++;
                }
                else if (text.Contains("failed") || text.Contains("no recorded")) { samCalls++; samFailed++; }
                Line("SAM", text);
                return;
            }
            if (message.StartsWith("[PACK] "))
            {
                // packing cell: scans (Muse Spark), decisions, arm, cartons
                string text = message.Substring("[PACK] ".Length);
                if (text.StartsWith("scan product")) { packScans++; if (text.Contains(" MISMATCH")) packScanWrong++; if (text.Contains("no answer")) packScanFailed++; }
                else if (text.Contains(" packed in carton")) packTaken++;
                else if (text.Contains("dropped in the reject bin")) packRejected++;
                else if (text.Contains(" complete and closed")) packCartons++;
                else if (text.Contains(" missed by the arm")) packMissed++;
                else if (text.StartsWith("R3 ") && text.Contains(" put carton ")) packDelivered++;
                Line("PACK", text);
                return;
            }
            if (message.StartsWith("[Muse] incident: "))
            {
                string text = message.Substring("[Muse] incident: ".Length);
                incidentsCount++;
                incidentLines.Add(text);
                if (text.StartsWith("route changed")) routeChanges++;
                if (text.Contains(" paused: ")) suspensions++;
                Line("INCIDENT", text);
            }
            else if (message.StartsWith("[Logistics] "))
            {
                // "wooden_box_point: pallet -> carried"
                cartonMoves++;
                string body = message.Substring("[Logistics] ".Length);
                int colon = body.IndexOf(": ", StringComparison.Ordinal);
                string name = colon > 0 ? body.Substring(0, colon) : body;
                string move = colon > 0 ? body.Substring(colon + 2) : "";
                string kind = name.Contains("wooden") ? "A" : name.Contains("big") ? "B" : "C";
                move = move.Replace("pallet -> carried", "taken from a pallet")
                           .Replace("carried -> bench", "placed on a bench")
                           .Replace("bench -> carried", "taken from a bench")
                           .Replace("carried -> truck", "placed in a truck");
                Line("BOX", "box " + kind + " " + move);
            }
            else if (message.StartsWith("[Truck] ") && (message.Contains("shipped") || message.Contains("delivery validated")))
                Line("TRUCK", message.Substring("[Truck] ".Length));
            else if (message.StartsWith("[Muse] normal decision deferred"))
                Line("MUSE", message.Substring("[Muse] ".Length));
        }

        // ---------------------------------------------------------------- sampling

        private void Update()
        {
            if (Time.time < next) return;
            next = Time.time + Interval;
            if (orch == null) orch = FindFirstObjectByType<MuseOrchestrator>();
            if (orch == null) return;
            if (voice == null) voice = FindFirstObjectByType<VoiceCommandController>();
            WarehouseWorld world = WarehouseWorld.Instance;

            TrackStage();
            TrackDecisions();
            TrackOrders();
            TrackRobots(world);
            TrackDoors(world);
            TrackVoice();
            TrackIdle();
        }

        private void TrackStage()
        {
            string stage = orch.Stage.ToString();
            if (stage == lastStage) return;
            string prev = lastStage;
            lastStage = stage;
            if (prev == "") return;
            string detail = stage == "WaitingForCapacity"
                ? " — " + (orch.FinishingOrderId >= 0 ? "Muse has #" + orch.FinishingOrderId + " finished (" + orch.FreeingBenchId + "/" + orch.FreeingTruckId + ")" : orch.CapacityReason)
                : stage == "UrgentActive" ? " — urgent #" + orch.UrgentOrderId + " executing"
                : stage == "Resuming" ? " — resuming " + orch.ResumedCount + " order(s)"
                : stage == "Suspending" ? " — safely pausing the normal orders"
                : "";
            Line("URGENT", prev + " → " + stage + detail);
        }

        private void TrackDecisions()
        {
            IReadOnlyList<MuseDecisionRecord> hist = orch.History;
            for (int i = 0; i < hist.Count; i++)
            {
                MuseDecisionRecord r = hist[i];
                if (r == null || !seenRecords.Add(r)) continue;
                string action = (r.Action ?? "").Trim();
                string bare = action.StartsWith("urgent:") ? action.Substring(7) : action;
                string applied = r.AppliedResult ?? "";
                bool executes = r.Accepted && bare != "wait" && bare != "no_plan" && bare != "" &&
                                !applied.StartsWith("waiting") && !applied.Contains("capacity freed");
                var call = new MuseCall
                {
                    Id = r.RequestId, Trigger = r.Trigger, Action = action, OrderId = r.OrderId,
                    LatencyMs = r.LatencyMs, Accepted = r.Accepted, Useful = executes
                };
                museCalls.Add(call);
                string what = !string.IsNullOrEmpty(r.PlanSummary) ? r.PlanSummary
                            : !string.IsNullOrEmpty(r.AppliedResult) ? r.AppliedResult
                            : action;
                string verdict = r.Accepted ? (executes ? "ACCEPTED" : "NO EFFECT (" + action + ")") : "REFUSED: " + r.RejectReason;
                call.Result = verdict;
                Line("MUSE", (r.RequestId ?? "?") + " [" + r.Trigger + "] " + (string.IsNullOrEmpty(r.Model) ? "" : r.Model + " ") +
                             (r.LatencyMs > 0 ? (r.LatencyMs / 1000f).ToString("F1") + " s  " : "") + "→ " + what + "  —  " + verdict +
                             (string.IsNullOrEmpty(r.Reason) ? "" : "  —  « " + r.Reason + " »"));
                if (r.OrderId > 0 && orderTracks.TryGetValue(r.OrderId, out OrderTrack ot) && executes)
                {
                    ot.Decisions++;
                    ot.DecisionLatency += r.LatencyMs / 1000f;
                }
            }
        }

        private static string Phase(string status)
        {
            switch (status)
            {
                case "pending": return "waiting for Muse decision";
                case "queued": return "waiting for robots";
                case "picking": return "picking (R1)";
                case "loading": return "loading (R2)";
                case "suspending": return "pausing";
                case "paused": return "paused (urgent)";
                case "waiting": return "waiting (stock-out/incomplete)";
                case "loaded": return "delivered";
                default: return status;
            }
        }

        private void TrackOrders()
        {
            foreach (WhOrderState o in orch.OrdersSnapshot)
            {
                anyOrderSeen = true;
                if (!orderTracks.TryGetValue(o.id, out OrderTrack t))
                {
                    t = new OrderTrack
                    {
                        Id = o.id, Urgent = o.urgent, Created = Time.time, Status = o.status, StatusSince = Time.time,
                        Composition = (o.a > 0 ? "A×" + o.a + " " : "") + (o.b > 0 ? "B×" + o.b + " " : "") + (o.c > 0 ? "C×" + o.c : "")
                    };
                    orderTracks[o.id] = t;
                    Line("ORDER", "#" + o.id + (o.urgent ? " URGENT" : "") + " created: " + t.Composition.Trim() + "  →  " + Phase(o.status));
                    continue;
                }
                if (o.bench_id != null && o.bench_id != t.Bench && o.bench_id != "") t.Bench = o.bench_id;
                if (o.truck_id != null && o.truck_id != t.Truck && o.truck_id != "") t.Truck = o.truck_id;
                if (o.status == t.Status) continue;
                float spent = Time.time - t.StatusSince;
                string prevPhase = Phase(t.Status);
                t.PhaseTime[prevPhase] = (t.PhaseTime.TryGetValue(prevPhase, out float acc) ? acc : 0f) + spent;
                string extra = o.status == "queued" || o.status == "picking"
                    ? "  (" + (string.IsNullOrEmpty(o.bench_id) ? "-" : o.bench_id) + " / " + (string.IsNullOrEmpty(o.truck_id) ? "-" : o.truck_id) + ")"
                    : o.status == "paused"
                        ? "  (staged " + (o.staged_a + o.staged_b + o.staged_c) + ", loaded " + (o.loaded_a + o.loaded_b + o.loaded_c) + ")"
                        : "";
                bool finishing = o.id == orch.FinishingOrderId;
                Line("ORDER", "#" + o.id + (o.urgent ? " URGENT" : "") + (finishing ? " [FINISHING_FOR_URGENT]" : "") + ": " +
                              prevPhase + " (" + spent.ToString("F1") + " s) → " + Phase(o.status) + extra);
                if (o.status == "queued") t.QueuedAt = Time.time;
                if (o.status == "picking" && t.QueuedAt >= 0f)
                    foreach (OrderTrack other in orderTracks.Values)
                        if (other != t && other.Status == "queued" && other.QueuedAt >= 0f && other.QueuedAt < t.QueuedAt && !t.Urgent)
                        {
                            string v = "R1 takes #" + t.Id + " before #" + other.Id + " (decided " +
                                       (t.QueuedAt - other.QueuedAt).ToString("F0") + " s earlier) — R1 queue order not respected";
                            fifoViolations.Add("t=" + Time.time.ToString("F0") + " s: " + v);
                            Line("ORDER", "⚠ " + v);
                        }
                t.Status = o.status;
                t.StatusSince = Time.time;
                if (o.status == "loaded" && t.Delivered < 0f)
                {
                    t.Delivered = Time.time;
                    Line("ORDER", "#" + o.id + " DELIVERED in " + (t.Delivered - t.Created).ToString("F1") + " s  —  " + PhaseBreakdown(t));
                }
            }
        }

        private static string PhaseBreakdown(OrderTrack t)
        {
            var parts = new List<string>();
            foreach (KeyValuePair<string, float> kv in t.PhaseTime)
                if (kv.Key != "delivered" && kv.Value >= .5f) parts.Add(kv.Key + " " + kv.Value.ToString("F0") + " s");
            return string.Join(" · ", parts);
        }

        // ---- robots: a readable state + its category (for the summary)

        private string r1LastTarget = "?";
        private bool r1LastPallet, r1LastBench;
        private readonly Dictionary<string, Vector3> lastPos = new Dictionary<string, Vector3>();
        private readonly Dictionary<string, float> stillFor = new Dictionary<string, float>();

        /// <summary>Time (s) since the robot stopped moving.</summary>
        private float Still(string id, Transform t)
        {
            Vector3 p = t.position;
            bool moved = !lastPos.TryGetValue(id, out Vector3 prev) || (p - prev).sqrMagnitude > .0004f;
            lastPos[id] = p;
            float v = moved ? 0f : (stillFor.TryGetValue(id, out float f) ? f : 0f) + Interval;
            stillFor[id] = v;
            return v;
        }

        private readonly List<string> fifoViolations = new List<string>();

        private void TrackRobots(WarehouseWorld world)
        {
            WarehouseRobotAutopilot r1 = orch.Robot1;
            if (r1 != null)
            {
                int oid = r1.MissionActive && r1.LastReport != null ? r1.LastReport.OrderId : -1;
                DescribeR1(r1, world, out string label, out string cat);
                Actor("R1", label, cat, oid);
            }
            TruckLoaderAgent r2 = orch.Robot2;
            if (r2 != null)
            {
                int oid = r2.MissionActive ? r2.DirectedOrderId : -1;
                DescribeR2(r2, out string label, out string cat);
                Actor("R2", label, cat, oid);
            }
        }

        private void DescribeR1(WarehouseRobotAutopilot r1, WarehouseWorld world, out string label, out string cat)
        {
            string s = (r1.Status ?? "").ToLowerInvariant();
            // during the pick / drop the robot clears its target: keep the last known one
            string target = ResolveZone(r1.CurrentTarget, world, out bool isPallet, out bool isBench);
            if (target == "?") { target = r1LastTarget; isPallet = r1LastPallet; isBench = r1LastBench; }
            else { r1LastTarget = target; r1LastPallet = isPallet; r1LastBench = isBench; }
            bool still = Still("R1", r1.transform) >= 1f;
            if (s.Contains("holding position")) { label = "holding position (Muse decision)"; cat = "waiting decision"; return; }
            if (s.StartsWith("returning") && !still) { label = "returning to station"; cat = "return to base"; return; }
            if (!r1.MissionActive || s.StartsWith("returning")) { label = "idle (at station)"; cat = "idle"; return; }
            if (s.Contains("suspended")) { label = "mission suspended (urgent)"; cat = "suspended"; return; }
            if (s.Contains("bench full")) { label = "waiting: bench full"; cat = "waiting bench"; return; }
            if (s.Contains("busy") || s.Contains("blocked") || s.StartsWith("replanning")) { label = "waiting: " + r1.Status; cat = "waiting path"; return; }
            if (s.Contains(" placed (")) { label = "box placed on " + target + " (" + (r1.ActiveLeg != null ? r1.ActiveLeg.Picked + "/" + r1.ActiveLeg.Planned + " from " + r1.ActiveLeg.PalletId : "") + ")"; cat = "handling"; return; }
            if (s.StartsWith("heading to ")) { label = "travel → " + target; cat = isPallet ? "travel to pallet" : isBench ? "travel to bench" : "travel"; return; }
            if (s.Contains("unloading")) { label = "dropping box on " + target; cat = "handling"; return; }
            if (s.Contains("loading") || s.StartsWith("picking")) { label = "taking box from " + target; cat = "handling"; return; }
            label = r1.Status;
            cat = "other";
        }

        private void DescribeR2(TruckLoaderAgent r2, out string label, out string cat)
        {
            OrderTrack t = orderTracks.TryGetValue(r2.DirectedOrderId, out OrderTrack ot) ? ot : null;
            string bench = t != null && t.Bench != "" ? t.Bench : "bench";
            string truck = t != null && t.Truck != "" ? t.Truck : "truck";
            string s = (r2.Status ?? "").ToLowerInvariant();
            bool still = Still("R2", r2.transform) >= 1f;
            if (r2.State == TruckLoaderAgent.R2State.ReturningHome && still) { label = "idle"; cat = "idle"; return; }
            if (!r2.MissionActive && r2.State != TruckLoaderAgent.R2State.ReturningHome) { label = "idle"; cat = "idle"; return; }
            switch (r2.State)
            {
                case TruckLoaderAgent.R2State.WaitingForBoxes: label = "waiting for boxes on " + bench; cat = "waiting boxes"; return;
                case TruckLoaderAgent.R2State.WaitingForTruck:
                    label = s.Contains("full") ? "waiting: truck full" : "waiting for door " + truck; cat = "waiting door"; return;
                case TruckLoaderAgent.R2State.ToBench:
                    if (s.StartsWith("suspension")) { label = "putting the box back on " + bench + " (suspension)"; cat = "suspended"; return; }
                    label = "travel → " + bench; cat = "travel to bench"; return;
                case TruckLoaderAgent.R2State.Picking: label = "taking box from " + bench; cat = "handling"; return;
                case TruckLoaderAgent.R2State.ToTruck:
                    if (s.StartsWith("leaving")) { label = "leaving trailer " + truck; cat = "leaving trailer"; return; }
                    label = "travel → " + truck + (r2.Carried != null ? " (box in hand)" : ""); cat = "travel to truck"; return;
                case TruckLoaderAgent.R2State.Dropping: label = "dropping box in " + truck; cat = "handling"; return;
                case TruckLoaderAgent.R2State.ReturningHome: label = "return to base"; cat = "return to base"; return;
                default: label = r2.Status; cat = "other"; return;
            }
        }

        private static string ResolveZone(ShelfInteractionZone zone, WarehouseWorld world, out bool pallet, out bool bench)
        {
            pallet = bench = false;
            if (zone == null || zone.shelf == null || world == null) return "?";
            string pid = world.PalletIdOf(zone.shelf);
            if (world.Pallet(pid) == zone.shelf) { pallet = true; return pid; }
            string bid = world.BenchIdOf(zone.shelf);
            if (world.Bench(bid) == zone.shelf) { bench = true; return bid; }
            return zone.shelf.name;
        }

        private void Actor(string id, string label, string category, int orderId)
        {
            if (!actors.TryGetValue(id, out ActorState a))
            {
                actors[id] = new ActorState { Label = label, Category = category, OrderId = orderId, Since = Time.time };
                Line(id, label + (orderId >= 0 ? "  [#" + orderId + "]" : ""));
                return;
            }
            if (a.Label == label && a.OrderId == orderId) return;
            float spent = Time.time - a.Since;
            segments.Add(new Segment { Actor = id, Label = a.Label, Category = a.Category, OrderId = a.OrderId, Start = a.Since, End = Time.time });
            Line(id, label + (orderId >= 0 ? "  [#" + orderId + "]" : "") + "      (end: " + a.Label + " — " + spent.ToString("F1") + " s)");
            a.Label = label;
            a.Category = category;
            a.OrderId = orderId;
            a.Since = Time.time;
        }

        private void TrackDoors(WarehouseWorld world)
        {
            if (world == null) return;
            foreach (string id in world.TruckIds)
            {
                TruckBay bay = world.Truck(id);
                if (bay == null) continue;
                string d = bay.Door.ToString();
                bool open = bay.IsDoorOpen;
                bool loading = bay.Door == TruckBay.TruckDoorState.Loading;
                if (open) doorOpenTotal[id] = (doorOpenTotal.TryGetValue(id, out float o) ? o : 0f) + Interval;
                if (loading) doorLoadingTotal[id] = (doorLoadingTotal.TryGetValue(id, out float l) ? l : 0f) + Interval;
                if (doorState.TryGetValue(id, out string prev) && prev == d) continue;
                doorState[id] = d;
                if (prev == null) continue;
                Line("DOOR", id + " " + prev + " → " + d + (bay.ReservedOrderId >= 0 ? "  [#" + bay.ReservedOrderId + "]" : "") +
                              "  (" + bay.LoadedCount + "/" + bay.Capacity + ")");
            }
        }

        private void TrackVoice()
        {
            if (voice == null) return;
            string st = voice.State.ToString();
            if (st != lastVoiceState)
            {
                if (lastVoiceState != "") Line("VOICE", lastVoiceState + " → " + st + (st == "Recording" ? " (mic open)" : ""));
                lastVoiceState = st;
            }
            if (voice.FinalTranscript != lastFinal)
            {
                lastFinal = voice.FinalTranscript;
                if (lastFinal.Length > 0)
                    Line("VOICE", "final transcription « " + lastFinal + " »  (ASR " + voice.LastAsrLatencyMs + " ms, " +
                                 voice.LastCaptureSeconds.ToString("F1") + " s, peak " + (voice.LastCapturePeak > 0f ? (20f * Mathf.Log10(voice.LastCapturePeak)).ToString("F0") : "-inf") + " dBFS)");
            }
            if (voice.LastFailure != lastFailure)
            {
                lastFailure = voice.LastFailure;
                if (lastFailure.Length > 0) Line("VOICE", "failure: " + lastFailure);
            }
        }

        // ---------------------------------------------------------------- summary

        private void TrackIdle()
        {
            bool busy = orch.ActiveCount > 0 || orch.Pending || orch.UrgentPending || orch.UrgentOrderId >= 0;
            foreach (WhOrderState o in orch.OrdersSnapshot) if (o.status != "loaded") { busy = true; break; }
            if (busy) { idleSince = -1f; summaryWrittenForIdle = false; return; }
            if (!anyOrderSeen || summaryWrittenForIdle) return;
            if (idleSince < 0f) idleSince = Time.time;
            if (Time.time - idleSince >= IdleSummaryDelay)
            {
                summaryWrittenForIdle = true;
                WriteSummary("all orders delivered");
            }
        }

        private bool finished;

        private void Finish()
        {
            if (finished || writer == null) return;
            finished = true;
            CloseOpenSegments();
            WriteSummary("end of session");
            try { writer.Dispose(); } catch (Exception) { }
            writer = null;
        }

        private void CloseOpenSegments()
        {
            foreach (KeyValuePair<string, ActorState> kv in actors)
                segments.Add(new Segment { Actor = kv.Key, Label = kv.Value.Label, Category = kv.Value.Category, OrderId = kv.Value.OrderId, Start = kv.Value.Since, End = Time.time });
            actors.Clear();
        }

        private void WriteSummary(string why)
        {
            if (writer == null) return;
            var all = new List<Segment>(segments);
            foreach (KeyValuePair<string, ActorState> kv in actors)
                all.Add(new Segment { Actor = kv.Key, Label = kv.Value.Label, Category = kv.Value.Category, OrderId = kv.Value.OrderId, Start = kv.Value.Since, End = Time.time });
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("================================ SUMMARY (" + why + ", t=" + Time.time.ToString("F0") + " s) ================================");

            // ---- orders
            sb.AppendLine("ORDERS — total duration and time spent in each phase");
            foreach (OrderTrack t in orderTracks.Values)
            {
                var pt = new Dictionary<string, float>(t.PhaseTime);
                if (t.Delivered < 0f) pt[Phase(t.Status)] = (pt.TryGetValue(Phase(t.Status), out float acc) ? acc : 0f) + (Time.time - t.StatusSince);
                string phases = "";
                foreach (KeyValuePair<string, float> kv in pt)
                    if (kv.Key != "delivered" && kv.Value >= .5f) phases += kv.Key + " " + kv.Value.ToString("F0") + " s · ";
                sb.AppendLine("  #" + t.Id + (t.Urgent ? " URG" : "    ") + "  " + t.Composition.PadRight(10) + "  " +
                              (t.Delivered >= 0f ? "delivered in " + (t.Delivered - t.Created).ToString("F0") + " s" : "NOT delivered (" + Phase(t.Status) + ")").PadRight(26) +
                              phases.TrimEnd(' ', '·') +
                              (t.Decisions > 0 ? "   [Muse: " + t.Decisions + " decision(s), " + t.DecisionLatency.ToString("F0") + " s of waiting]" : ""));
            }

            // ---- robots
            float session = Mathf.Max(1f, Time.time);
            foreach (string robot in new[] { "R1", "R2" })
            {
                var byCat = new Dictionary<string, float>();
                float total = 0f;
                foreach (Segment s in all)
                {
                    if (s.Actor != robot) continue;
                    byCat[s.Category] = (byCat.TryGetValue(s.Category, out float v) ? v : 0f) + s.Duration;
                    total += s.Duration;
                }
                if (total <= 0f) continue;
                var cats = new List<KeyValuePair<string, float>>(byCat);
                cats.Sort((a, b) => b.Value.CompareTo(a.Value));
                float work = 0f;
                foreach (KeyValuePair<string, float> kv in cats)
                    if (kv.Key.StartsWith("travel") || kv.Key == "handling") work += kv.Value;
                sb.AppendLine(robot + " — timetable (" + total.ToString("F0") + " s, useful work " + (100f * work / total).ToString("F0") + " %)");
                foreach (KeyValuePair<string, float> kv in cats)
                    sb.AppendLine("  " + kv.Key.PadRight(26) + kv.Value.ToString("F0").PadLeft(5) + " s   " + (100f * kv.Value / total).ToString("F0").PadLeft(3) + " %");
            }

            // ---- Muse
            int useful = 0, noEffect = 0, rejected = 0;
            float latency = 0f;
            var byTrigger = new Dictionary<string, int>();
            foreach (MuseCall c in museCalls)
            {
                if (c.Useful) useful++;
                else if (c.Accepted) noEffect++;
                else rejected++;
                latency += c.LatencyMs / 1000f;
                byTrigger[c.Trigger ?? "?"] = (byTrigger.TryGetValue(c.Trigger ?? "?", out int n) ? n : 0) + 1;
            }
            var trig = new List<string>();
            foreach (KeyValuePair<string, int> kv in byTrigger) trig.Add(kv.Key + "×" + kv.Value);
            sb.AppendLine("MUSE — " + museCalls.Count + " call(s): " + useful + " useful, " + noEffect + " without effect (wait/no_plan…), " +
                          rejected + " refused/failed; total wait " + latency.ToString("F0") + " s  [" + string.Join(", ", trig) + "]");

            // ---- SAM 3.1 (robot cameras)
            sb.AppendLine("SAM 3.1 — " + samCalls + " segmentation(s) of the robot cameras: " + samWithObjects + " with objects, " +
                          (samCalls - samWithObjects - samFailed) + " empty, " + samFailed + " without answer; checks: " +
                          samVerified + " verified, " + samMismatches + " mismatch(es); orders verified by SAM: " + samOrdersVerified);

            // ---- packing cell (Muse Spark inspection)
            sb.AppendLine("PACKING — " + packScans + " scan(s) by Muse Spark (" + packScanFailed + " without answer, " + packScanWrong +
                          " disagreeing with the simulation), " + packTaken + " product(s) packed, " + packRejected + " rejected, " + packMissed + " missed on the moving belt, " + packCartons + " carton(s) closed, " +
                          packDelivered + " carried to the pallets by R3");

            // ---- doors
            foreach (KeyValuePair<string, float> kv in doorOpenTotal)
            {
                float loadingT = doorLoadingTotal.TryGetValue(kv.Key, out float l) ? l : 0f;
                sb.AppendLine("DOOR " + kv.Key + " — open " + kv.Value.ToString("F0") + " s including active loading " + loadingT.ToString("F0") + " s");
            }
            sb.AppendLine("MISC — box moves " + cartonMoves + ", route changes (stock-out) " + routeChanges +
                          ", pauses " + suspensions + ", incidents " + incidentsCount);

            // ---- useless steps
            sb.AppendLine("STEPS THAT ARE USELESS / TO WATCH");
            int found = 0;
            foreach (string robot in new[] { "R1", "R2" })
            {
                for (int i = 0; i < all.Count; i++)
                {
                    Segment s = all[i];
                    if (s.Actor != robot || s.Category != "return to base") continue;
                    // return to base followed by a new mission shortly after -> useless round trip
                    float idleAfter = 0f;
                    Segment nextWork = null;
                    for (int j = i + 1; j < all.Count; j++)
                    {
                        if (all[j].Actor != robot) continue;
                        if (all[j].Category == "idle") { idleAfter += all[j].Duration; continue; }
                        if (all[j].Category == "return to base") break; // same trip, split
                        nextWork = all[j];
                        break;
                    }
                    if (nextWork != null && idleAfter < 25f)
                    {
                        found++;
                        sb.AppendLine("  • " + robot + " t=" + s.Start.ToString("F0") + " s: return to base " + s.Duration.ToString("F0") +
                                      " s then new mission " + idleAfter.ToString("F0") + " s later — useless round trip (~" +
                                      (2f * s.Duration + idleAfter).ToString("F0") + " s lost)");
                    }
                }
            }
            foreach (Segment s in all)
            {
                if (s.Duration < 20f) continue;
                if (s.Category == "waiting boxes" || s.Category == "waiting door" || s.Category == "waiting decision" ||
                    s.Category == "waiting bench" || s.Category == "waiting path")
                {
                    found++;
                    sb.AppendLine("  • " + s.Actor + " t=" + s.Start.ToString("F0") + " s: " + s.Label + " for " + s.Duration.ToString("F0") + " s" +
                                  (s.OrderId >= 0 ? " (#" + s.OrderId + ")" : ""));
                }
            }
            foreach (KeyValuePair<string, float> kv in doorOpenTotal)
            {
                float loadingT = doorLoadingTotal.TryGetValue(kv.Key, out float l) ? l : 0f;
                if (kv.Value - loadingT > 60f)
                    sb.AppendLine("  (info) door " + kv.Key + " open " + (kv.Value - loadingT).ToString("F0") +
                                  " s without loading — opened at decision time, delays no robot");
            }
            foreach (string v in fifoViolations)
            {
                found++;
                sb.AppendLine("  • " + v);
            }
            foreach (MuseCall c in museCalls)
                if (!c.Useful)
                {
                    found++;
                    sb.AppendLine("  • Muse call " + c.Id + " [" + c.Trigger + "] without effect: " + c.Result);
                }
            foreach (OrderTrack t in orderTracks.Values)
            {
                float wait = t.PhaseTime.TryGetValue("waiting for Muse decision", out float w) ? w : 0f;
                if (wait > 60f)
                {
                    found++;
                    sb.AppendLine("  • #" + t.Id + " waited " + wait.ToString("F0") + " s for a decision (resources busy or Muse latency)");
                }
            }
            if (routeChanges > 0)
            {
                found++;
                sb.AppendLine("  • " + routeChanges + " route change(s): a planned pallet no longer had the box reachable (stock-out on top)");
            }
            if (found == 0) sb.AppendLine("  none detected");
            sb.AppendLine("=====================================================================================================");
            try { writer.Write(sb.ToString()); } catch (Exception) { }
        }
    }
}
