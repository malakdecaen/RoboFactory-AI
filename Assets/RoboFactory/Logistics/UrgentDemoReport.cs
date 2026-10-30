using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RoboFactory.Hackathon.Orchestration;
using UnityEngine;

namespace RoboFactory.Logistics
{
    /// <summary>
    /// PlayMode validation report for the urgent demo. Watches the orchestrator's
    /// stage, freezes the state of the orders at the PAUSED moment, then at
    /// the end of the flow checks box conservation (no loss, no duplicate),
    /// truck closing and the absence of leftover reservations.
    ///
    /// The report explicitly separates three proofs:
    ///  - voice   : FINAL transcription actually emitted by muse-voice
    ///              (audio_ms = proof of transport ONLY, never presented
    ///              as proof of transcription);
    ///  - muse    : real model decision (mode "live" + model muse-*);
    ///  - exec    : delivered quantities == ordered quantities, for the urgent order
    ///              AND for each resumed order.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UrgentDemoReport : MonoBehaviour
    {
        [Header("References")]
        public MuseOrchestrator Orchestrator;
        public VoiceCommandController Voice;
        [Tooltip("Stage monitoring frequency.")]
        public float PollInterval = .25f;

        // ------------------------------------------------------------------ JSON
        [Serializable] public sealed class VoiceProof
        {
            public string mode;         // live | mock | manual
            public string model;
            public int latency_ms;
            public int audio_ms;        // transport only
            public string transcript;
            public bool final_submitted;
            public bool real;
            public string note;
        }

        [Serializable] public sealed class MuseProof
        {
            public string request_id;
            public string model;
            public string mode;
            public int latency_ms;
            public string action;
            public bool accepted;
            public string plan;
            public string reason;
            public bool real;
        }

        [Serializable] public sealed class OrderCheck
        {
            public int id;
            public bool urgent;
            public string requested;
            public string staged_at_pause;
            public string loaded_at_pause;
            public string delivered;
            public string status;
            public bool ok;
            public string note;
        }

        [Serializable] public sealed class Report
        {
            public string timestamp;
            public string outcome;
            public VoiceProof voice;
            public MuseProof muse;
            public List<OrderCheck> orders = new List<OrderCheck>();
            public List<string> integrity = new List<string>();
            /// <summary>FINISH TO FREE: normal order finished to free the urgent order's capacity.</summary>
            public string finish_to_free = "";
            public string verdict;
        }

        // ------------------------------------------------------------------ HUD
        /// <summary>Multi-line summary shown in the HUD.</summary>
        public string Summary { get; private set; } = "";
        public string LastPath { get; private set; } = "";
        public bool Ready { get; private set; }
        public bool LastVerdictOk { get; private set; }

        private sealed class Snap
        {
            public int id; public bool urgent;
            public int a, b, c;
            public int sa, sb, sc;   // staged at the PAUSED moment
            public int la, lb, lc;   // loaded at the PAUSED moment
        }

        private readonly List<Snap> paused = new List<Snap>();
        private int urgentOrderId = -1;
        private int finishedForUrgent = -1;
        private string freedBench = "", freedTruck = "";
        private MuseOrchestrator.UrgentStage prevStage = MuseOrchestrator.UrgentStage.Idle;
        private float nextPoll;
        // voice state frozen at the start of the flow (avoids reusing old data)
        private string flowTranscript = "";
        private bool flowVoiceFinal;
        private string flowVoiceMode = "";
        private string flowVoiceModel = "";
        private int flowVoiceLatency;
        private int flowAudioMs;
        private string flowVoiceFailure = "";

        private void Start()
        {
            if (Orchestrator == null) Orchestrator = FindFirstObjectByType<MuseOrchestrator>();
            if (Voice == null) Voice = FindFirstObjectByType<VoiceCommandController>();
        }

        private void Update()
        {
            if (Orchestrator == null || Time.time < nextPoll) return;
            nextPoll = Time.time + PollInterval;
            MuseOrchestrator.UrgentStage s = Orchestrator.Stage;

            // Entering planning: capture the voice right away — the flow can
            // die here (wait/clarify/Meta failure) without ever reaching Suspending.
            if (s == MuseOrchestrator.UrgentStage.Planning &&
                prevStage != MuseOrchestrator.UrgentStage.Planning)
            {
                Ready = false;
                CaptureVoice();
            }
            // Entering suspension: new flow -> new measurements.
            if (s == MuseOrchestrator.UrgentStage.Suspending &&
                prevStage != MuseOrchestrator.UrgentStage.Suspending)
            {
                paused.Clear();
                urgentOrderId = Orchestrator.UrgentOrderId;
                if (!Orchestrator.CapacityEpisode) finishedForUrgent = -1;
                Ready = false;
                CaptureVoice(); // catch-up if Planning was not observed
            }
            // Suspension finished: the orders are PAUSED, freeze the state.
            if (prevStage == MuseOrchestrator.UrgentStage.Suspending &&
                s != MuseOrchestrator.UrgentStage.Suspending)
                CapturePaused();
            // Suspension too fast to be observed: freeze at UrgentActive.
            if (s == MuseOrchestrator.UrgentStage.UrgentActive && paused.Count == 0)
                CapturePaused();
            // End of flow: generate the report.
            if ((s == MuseOrchestrator.UrgentStage.Completed ||
                 s == MuseOrchestrator.UrgentStage.Failed) && prevStage != s)
                BuildReport(s);
            // FINISH TO FREE: remember the order Muse had finished for the urgent one
            if (Orchestrator.FinishingOrderId >= 0)
            {
                finishedForUrgent = Orchestrator.FinishingOrderId;
                freedBench = Orchestrator.FreeingBenchId;
                freedTruck = Orchestrator.FreeingTruckId;
            }
            if (urgentOrderId < 0 && Orchestrator.UrgentOrderId >= 0)
                urgentOrderId = Orchestrator.UrgentOrderId; // catch-up
            prevStage = s;
        }

        /// <summary>Freezes the controller's voice fields at the start of the flow.</summary>
        private void CaptureVoice()
        {
            flowTranscript = Voice != null ? Voice.FinalTranscript : "";
            flowVoiceFinal = Voice != null && Voice.LastSubmittedFinal;
            flowVoiceMode = Voice != null ? Voice.LastAsrMode : "";
            flowVoiceModel = Voice != null ? Voice.LastAsrModel : "";
            flowVoiceLatency = Voice != null ? Voice.LastAsrLatencyMs : 0;
            flowAudioMs = Voice != null ? Voice.AudioProcessedMs : 0;
            flowVoiceFailure = Voice != null ? Voice.LastFailure : "";
        }

        /// <summary>Freezes staged/loaded of each PAUSED order (proof of conservation).</summary>
        private void CapturePaused()
        {
            paused.Clear();
            foreach (WhOrderState o in Orchestrator.OrdersSnapshot)
            {
                // paused orders + the urgent order OF THIS FLOW (not an urgent order from a previous wave)
                bool thisUrgent = o.urgent && (o.id == Orchestrator.UrgentOrderId || o.id == urgentOrderId);
                if (o.status != "paused" && !thisUrgent) continue;
                paused.Add(new Snap
                {
                    id = o.id, urgent = o.urgent,
                    a = o.a, b = o.b, c = o.c,
                    sa = o.staged_a, sb = o.staged_b, sc = o.staged_c,
                    la = o.loaded_a, lb = o.loaded_b, lc = o.loaded_c
                });
            }
        }

        private void BuildReport(MuseOrchestrator.UrgentStage s)
        {
            var report = new Report
            {
                timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                outcome = Orchestrator.LastUrgentOutcome
            };

            // ---- 2. MUSE first: its record tells whether the voice was used
            var mp = new MuseProof { mode = "none" };
            MuseDecisionRecord rec = null;
            IReadOnlyList<MuseDecisionRecord> hist = Orchestrator.History;
            for (int i = hist.Count - 1; i >= 0; i--)
                if (hist[i].OrderId == urgentOrderId && urgentOrderId >= 0) { rec = hist[i]; break; }
            if (rec == null) // /urgent call failed before the order was created
                for (int i = hist.Count - 1; i >= 0; i--)
                    if (hist[i].Trigger == "urgent_voice_order") { rec = hist[i]; break; }
            if (rec != null)
            {
                mp.request_id = rec.RequestId;
                mp.model = rec.Model;
                mp.mode = rec.Mode;
                mp.latency_ms = rec.LatencyMs;
                mp.action = rec.Action;
                mp.accepted = rec.Accepted;
                mp.plan = rec.PlanSummary;
                mp.reason = !string.IsNullOrEmpty(rec.Reason) ? rec.Reason : rec.RejectReason;
                mp.real = rec.Mode == "live" && !string.IsNullOrEmpty(rec.Model) &&
                          rec.Model.StartsWith("muse");
            }
            report.muse = mp;

            // ---- 1. VOICE: real = FINAL transcription emitted by muse-voice.
            //          The flow is only "voice" if Muse received a transcription.
            bool voiceUsed = rec != null && rec.Trigger == "urgent_voice_order";
            var vp = new VoiceProof();
            if (voiceUsed)
            {
                vp.mode = flowVoiceMode;
                vp.model = flowVoiceModel;
                vp.latency_ms = flowVoiceLatency;
                vp.audio_ms = flowAudioMs; // transport only — not a proof
                vp.transcript = flowTranscript;
                vp.final_submitted = flowVoiceFinal;
                vp.real = flowVoiceMode == "live" && flowVoiceFinal &&
                          !string.IsNullOrEmpty(flowTranscript);
                vp.note = vp.real ? "real final transcription (Meta muse-voice)"
                    : flowVoiceFinal ? "mock transcription — not a real proof"
                    : "no final transcription: " + flowVoiceFailure;
            }
            else
            {
                vp.mode = "manual";
                vp.note = "urgent order triggered manually (no voice session)";
            }
            report.voice = vp;

            // ---- 3. EXECUTION: box conservation, exact delivery
            WhOrderState[] now = Orchestrator.OrdersSnapshot;
            bool execOk = true;
            bool urgentSeen = false;
            foreach (Snap p in paused)
            {
                WhOrderState cur = Find(now, p.id);
                var oc = new OrderCheck
                {
                    id = p.id, urgent = p.urgent,
                    requested = Cnts(p.a, p.b, p.c),
                    staged_at_pause = Cnts(p.sa, p.sb, p.sc),
                    loaded_at_pause = Cnts(p.la, p.lb, p.lc)
                };
                if (p.urgent) urgentSeen = true;
                if (cur == null)
                {
                    oc.status = "missing";
                    oc.ok = false;
                    oc.note = "order lost";
                }
                else
                {
                    oc.status = cur.status;
                    oc.delivered = Cnts(cur.loaded_a, cur.loaded_b, cur.loaded_c);
                    bool exact = cur.status == "loaded" &&
                                 cur.loaded_a == p.a && cur.loaded_b == p.b && cur.loaded_c == p.c;
                    bool noOverflow = cur.loaded_a <= p.a && cur.loaded_b <= p.b && cur.loaded_c <= p.c;
                    oc.ok = exact && noOverflow;
                    oc.note = oc.ok ? "delivered exactly, no duplicate or loss"
                        : !noOverflow ? "DUPLICATE: delivered > ordered"
                        : cur.status == "loaded" ? "delivered ≠ ordered (loss)"
                        : "not finished: " + cur.status;
                }
                if (!oc.ok) execOk = false;
                report.orders.Add(oc);
            }
            // The urgent order may not be in paused (e.g. created after the freeze).
            if (!urgentSeen && urgentOrderId >= 0)
            {
                WhOrderState cur = Find(now, urgentOrderId);
                var oc = new OrderCheck { id = urgentOrderId, urgent = true };
                if (cur != null)
                {
                    oc.requested = Cnts(cur.a, cur.b, cur.c);
                    oc.delivered = Cnts(cur.loaded_a, cur.loaded_b, cur.loaded_c);
                    oc.status = cur.status;
                    oc.ok = cur.status == "loaded" && cur.loaded_a == cur.a &&
                            cur.loaded_b == cur.b && cur.loaded_c == cur.c;
                    oc.note = oc.ok ? "urgent order delivered exactly" : "urgent order not delivered: " + cur.status;
                }
                else { oc.status = "missing"; oc.note = "urgent order lost"; }
                if (!oc.ok) execOk = false;
                report.orders.Add(oc);
            }

            // ---- FINISH TO FREE: the order finished to free the urgent one must be delivered exactly
            if (finishedForUrgent >= 0)
            {
                WhOrderState f = Find(now, finishedForUrgent);
                bool fOk = f != null && f.status == "loaded" && f.loaded_a == f.a && f.loaded_b == f.b && f.loaded_c == f.c;
                report.finish_to_free = "#" + finishedForUrgent + " finished to free " + freedBench + " / " + freedTruck +
                                        (fOk ? " — delivered exactly" : " — " + (f != null ? f.status : "missing"));
                if (!fOk) execOk = false;
            }

            // ---- world integrity: doors closed, reservations released.
            // A normal order that does not belong to the urgent flow and is STILL running
            // (started after the resume) legitimately holds its resources: listed, not counted.
            WarehouseWorld world = WarehouseWorld.Instance;
            if (world != null)
            {
                var flowIds = new HashSet<int>();
                foreach (OrderCheck oc in report.orders) flowIds.Add(oc.id);
                if (finishedForUrgent >= 0) flowIds.Add(finishedForUrgent);
                var inProgress = new List<string>();
                bool Legit(string reservedBy, string label)
                {
                    if (!int.TryParse(reservedBy, out int oid) || flowIds.Contains(oid)) return false;
                    WhOrderState o = Find(now, oid);
                    if (o == null || o.status == "loaded") return false;
                    inProgress.Add(label + " (#" + oid + ")");
                    return true;
                }
                var stillReserved = new List<string>();
                foreach (string id in world.PalletIds)
                {
                    string by = world.ReservedBy(id);
                    if (!string.IsNullOrEmpty(by) && !Legit(by, id)) stillReserved.Add("pallet " + id);
                }
                foreach (string id in world.BenchIds)
                {
                    string by = world.ReservedBy(id);
                    if (!string.IsNullOrEmpty(by) && !Legit(by, id)) stillReserved.Add("bench " + id);
                }
                foreach (string id in world.TruckIds)
                {
                    string by = world.ReservedBy(id);
                    if (!string.IsNullOrEmpty(by) && !Legit(by, id)) stillReserved.Add("truck " + id);
                }
                report.integrity.Add(stillReserved.Count == 0
                    ? "no leftover reservation"
                    : "RESOURCES STILL RESERVED: " + string.Join(", ", stillReserved));
                if (inProgress.Count > 0)
                    report.integrity.Add("normal order(s) still running, outside the urgent flow: " + string.Join(", ", inProgress));
                var doors = new List<string>();
                foreach (string id in world.TruckIds)
                {
                    string d = Orchestrator.DoorStateOf(id);
                    TruckBay bay = world.Truck(id);
                    WhOrderState holder = bay != null && bay.ReservedOrderId >= 0 ? Find(now, bay.ReservedOrderId) : null;
                    bool legitOpen = holder != null && holder.status != "loaded" && !flowIds.Contains(holder.id);
                    if (d != "Closed" && d != "Loaded" && !legitOpen) doors.Add(id + "=" + d);
                }
                report.integrity.Add(doors.Count == 0
                    ? "all trucks closed"
                    : "DOORS NOT CLOSED: " + string.Join(", ", doors));
            }
            else report.integrity.Add("WarehouseWorld missing — integrity not verifiable");
            bool integrityOk = report.integrity.TrueForAll(x =>
                !x.StartsWith("RESOURCES") && !x.StartsWith("DOORS") &&
                !x.Contains("not verifiable"));

            // ---- overall verdict
            bool urgentDelivered = s == MuseOrchestrator.UrgentStage.Completed;
            bool museOk = mp.real && mp.accepted;
            var fails = new List<string>();
            if (!urgentDelivered) fails.Add("urgent order not delivered");
            if (!museOk) fails.Add("Muse decision not real/refused");
            if (!execOk) fails.Add("boxes lost or duplicated");
            // Integrity only counts if the flow actually touched orders —
            // otherwise active doors/reservations of normal orders would make
            // a report fail that never executed anything.
            if (!integrityOk && report.orders.Count > 0) fails.Add("world integrity");
            if (voiceUsed && !vp.real)
                fails.Add("transcription not final/not real");
            report.verdict = fails.Count == 0
                ? "PASS — real transcription + real Muse decision + verified execution"
                : "FAIL — " + string.Join("; ", fails);
            LastVerdictOk = fails.Count == 0;
            Ready = true;

            // ---- HUD summary + JSON file
            var sb = new StringBuilder();
            sb.AppendLine("VOICE  : " + vp.mode.ToUpperInvariant() +
                          (vp.real ? " — real final \"" + vp.transcript + "\""
                                   : " — " + vp.note) +
                          " (" + vp.latency_ms + " ms, audio transported " + vp.audio_ms + " ms)");
            sb.AppendLine("MUSE   : " + (rec != null
                ? mp.model + " " + mp.mode.ToUpperInvariant() + " " + mp.latency_ms +
                  " ms " + mp.action + (mp.accepted ? " OK" : " REFUSED") +
                  (!string.IsNullOrEmpty(mp.plan) ? " — " + mp.plan : "")
                : "no Muse call recorded"));
            sb.Append("EXEC   : ");
            foreach (OrderCheck oc in report.orders)
                sb.Append("#" + oc.id + (oc.urgent ? "(URG)" : "") + " " +
                          (oc.delivered ?? "—") + " " + (oc.ok ? "✔" : "✘ " + oc.note) + "  ");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(report.finish_to_free))
                sb.AppendLine("FINISH : " + report.finish_to_free);
            sb.AppendLine("VERDICT: " + report.verdict);
            Summary = sb.ToString();
            LastPath = WriteJson(report);
            Debug.Log("[UrgentDemoReport] " + report.verdict + "\n" + Summary +
                      (LastPath.Length > 0 ? "file: " + LastPath : ""));
        }

        private static WhOrderState Find(WhOrderState[] arr, int id)
        {
            foreach (WhOrderState o in arr) if (o.id == id) return o;
            return null;
        }

        private static string Cnts(int a, int b, int c)
        {
            var parts = new List<string>();
            if (a > 0) parts.Add(a + "A");
            if (b > 0) parts.Add(b + "B");
            if (c > 0) parts.Add(c + "C");
            return parts.Count > 0 ? string.Join("+", parts) : "0";
        }

        private string WriteJson(Report report)
        {
            try
            {
                string dir = Path.Combine(
                    Directory.GetParent(Application.dataPath).FullName, "urgent_reports");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir,
                    "urgent_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json");
                File.WriteAllText(path, JsonUtility.ToJson(report, true));
                return path;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[UrgentDemoReport] cannot write JSON: " + e.Message);
                return "";
            }
        }
    }
}
