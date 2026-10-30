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
    /// Journal de FLUX (lecture seule) : raconte chaque action et chaque décision dans l'ordre —
    /// commandes, décisions Muse (déclencheur, latence, plan, raison, refus), robots (qui fait
    /// quoi, pour quelle commande, combien de temps), portes, cartons, urgences, voix — puis
    /// écrit un BILAN : durée de chaque phase par commande, emploi du temps des robots, appels
    /// Muse, et liste des étapes qui ne servent à rien (allers-retours base, attentes, appels
    /// sans effet, portes ouvertes à vide…). Fichier : &lt;projet&gt;/logs/flow_*.log.
    /// Ne modifie rien dans la simulation.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FlowRecorder : MonoBehaviour
    {
        public float Interval = .1f;
        [Tooltip("Bilan écrit automatiquement quand plus rien ne tourne depuis ce délai (s).")]
        public float IdleSummaryDelay = 5f;

        private StreamWriter writer;
        public static string FilePath { get; private set; } = "";
        private MuseOrchestrator orch;
        private VoiceCommandController voice;
        private float next;
        private float lastLineT = -1f;

        // ---------------------------------------------------------------- suivi par acteur
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
        private readonly List<string> incidentLines = new List<string>();
        private string lastStage = "", lastVoiceState = "", lastFinal = "", lastFailure = "";
        private float idleSince = -1f;
        private bool summaryWrittenForIdle;
        private bool anyOrderSeen;

        // ---------------------------------------------------------------- installation

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
            Line("SESSION", "début — mode " + (orch != null ? orch.Mode : "?") +
                            (orch != null && orch.ReserveUrgentLane ? "  ·  voie urgente fixe " + orch.UrgentBenchId + "/" + orch.UrgentTruckId : "") +
                            "  ·  journal d'état brut : " + Path.GetFileName(MuseTrace.FilePath));
        }

        private void Open()
        {
            try
            {
                string dir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "logs");
                Directory.CreateDirectory(dir);
                FilePath = Path.Combine(dir, "flow_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
                writer = new StreamWriter(FilePath, false, new UTF8Encoding(false)) { AutoFlush = true };
                writer.WriteLine("# RoboFactory — journal de flux " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                                 "  scène=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
                writer.WriteLine("# colonnes : heure réelle | temps de jeu | écart depuis la ligne précédente | acteur | action");
                writer.WriteLine("# acteurs : ORDRE, MUSE, R1, R2, PORTE, CARTON, URGENCE, VOIX, INCIDENT ; un BILAN suit chaque fin d'activité");
                writer.WriteLine();
                Debug.Log("[FlowRecorder] journal de flux : " + FilePath);
            }
            catch (Exception e)
            {
                writer = null;
                Debug.LogWarning("[FlowRecorder] journal non ouvert : " + e.Message);
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

        // ---------------------------------------------------------------- messages Unity

        private void OnUnityLog(string message, string stack, LogType type)
        {
            if (string.IsNullOrEmpty(message) || message.StartsWith("[FlowRecorder]") || message.StartsWith("[MuseTrace]")) return;
            if (type == LogType.Exception || type == LogType.Error)
            {
                Line("ERREUR", message.Split('\n')[0]);
                return;
            }
            if (message.StartsWith("[Muse] incident : "))
            {
                string text = message.Substring("[Muse] incident : ".Length);
                incidentsCount++;
                incidentLines.Add(text);
                if (text.StartsWith("itinéraire modifié")) routeChanges++;
                if (text.Contains(" en pause : ")) suspensions++;
                Line("INCIDENT", text);
            }
            else if (message.StartsWith("[Logistique] "))
            {
                // "wooden_box_point : palette -> transporté"
                cartonMoves++;
                string body = message.Substring("[Logistique] ".Length);
                int colon = body.IndexOf(" : ", StringComparison.Ordinal);
                string name = colon > 0 ? body.Substring(0, colon) : body;
                string move = colon > 0 ? body.Substring(colon + 3) : "";
                string kind = name.Contains("wooden") ? "A" : name.Contains("big") ? "B" : "C";
                move = move.Replace("palette -> transporté", "pris sur une palette")
                           .Replace("transporté -> banc", "posé sur un banc")
                           .Replace("banc -> transporté", "pris sur un banc")
                           .Replace("transporté -> camion", "posé dans un camion");
                Line("CARTON", "carton " + kind + " " + move);
            }
            else if (message.StartsWith("[Camion] ") && (message.Contains("expédiée") || message.Contains("livraison validée")))
                Line("CAMION", message.Substring("[Camion] ".Length));
            else if (message.StartsWith("[Muse] décision normale différée"))
                Line("MUSE", message.Substring("[Muse] ".Length));
        }

        // ---------------------------------------------------------------- échantillonnage

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
                ? " — " + (orch.FinishingOrderId >= 0 ? "Muse fait finir #" + orch.FinishingOrderId + " (" + orch.FreeingBenchId + "/" + orch.FreeingTruckId + ")" : orch.CapacityReason)
                : stage == "UrgentActive" ? " — urgente #" + orch.UrgentOrderId + " en exécution"
                : stage == "Resuming" ? " — reprise de " + orch.ResumedCount + " commande(s)"
                : stage == "Suspending" ? " — mise en pause sûre des commandes normales"
                : "";
            Line("URGENCE", prev + " → " + stage + detail);
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
                                !applied.StartsWith("attente") && !applied.Contains("capacité libérée");
                var call = new MuseCall
                {
                    Id = r.RequestId, Trigger = r.Trigger, Action = action, OrderId = r.OrderId,
                    LatencyMs = r.LatencyMs, Accepted = r.Accepted, Useful = executes
                };
                museCalls.Add(call);
                string what = !string.IsNullOrEmpty(r.PlanSummary) ? r.PlanSummary
                            : !string.IsNullOrEmpty(r.AppliedResult) ? r.AppliedResult
                            : action;
                string verdict = r.Accepted ? (executes ? "ACCEPTÉ" : "SANS EFFET (" + action + ")") : "REFUSÉ : " + r.RejectReason;
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
                case "pending": return "attente décision Muse";
                case "queued": return "attente robots";
                case "picking": return "préparation (R1)";
                case "loading": return "chargement (R2)";
                case "suspending": return "mise en pause";
                case "paused": return "en pause (urgente)";
                case "waiting": return "attente (rupture/incomplet)";
                case "loaded": return "livrée";
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
                    Line("ORDRE", "#" + o.id + (o.urgent ? " URGENTE" : "") + " créée : " + t.Composition.Trim() + "  →  " + Phase(o.status));
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
                        ? "  (préparé " + (o.staged_a + o.staged_b + o.staged_c) + ", chargé " + (o.loaded_a + o.loaded_b + o.loaded_c) + ")"
                        : "";
                bool finishing = o.id == orch.FinishingOrderId;
                Line("ORDRE", "#" + o.id + (o.urgent ? " URGENTE" : "") + (finishing ? " [FINISHING_FOR_URGENT]" : "") + " : " +
                              prevPhase + " (" + spent.ToString("F1") + " s) → " + Phase(o.status) + extra);
                if (o.status == "queued") t.QueuedAt = Time.time;
                if (o.status == "picking" && t.QueuedAt >= 0f)
                    foreach (OrderTrack other in orderTracks.Values)
                        if (other != t && other.Status == "queued" && other.QueuedAt >= 0f && other.QueuedAt < t.QueuedAt && !t.Urgent)
                        {
                            string v = "R1 prend #" + t.Id + " avant #" + other.Id + " (décidée " +
                                       (t.QueuedAt - other.QueuedAt).ToString("F0") + " s plus tôt) — ordre de la file R1 non respecté";
                            fifoViolations.Add("t=" + Time.time.ToString("F0") + " s : " + v);
                            Line("ORDRE", "⚠ " + v);
                        }
                t.Status = o.status;
                t.StatusSince = Time.time;
                if (o.status == "loaded" && t.Delivered < 0f)
                {
                    t.Delivered = Time.time;
                    Line("ORDRE", "#" + o.id + " LIVRÉE en " + (t.Delivered - t.Created).ToString("F1") + " s  —  " + PhaseBreakdown(t));
                }
            }
        }

        private static string PhaseBreakdown(OrderTrack t)
        {
            var parts = new List<string>();
            foreach (KeyValuePair<string, float> kv in t.PhaseTime)
                if (kv.Key != "livrée" && kv.Value >= .5f) parts.Add(kv.Key + " " + kv.Value.ToString("F0") + " s");
            return string.Join(" · ", parts);
        }

        // ---- robots : un état lisible + sa catégorie (pour le bilan)

        private string r1LastTarget = "?";
        private bool r1LastPallet, r1LastBench;
        private readonly Dictionary<string, Vector3> lastPos = new Dictionary<string, Vector3>();
        private readonly Dictionary<string, float> stillFor = new Dictionary<string, float>();

        /// <summary>Temps (s) depuis lequel le robot ne bouge plus.</summary>
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
            // pendant la prise / dépose la cible est effacée par le robot : on garde la dernière connue
            string target = ResolveZone(r1.CurrentTarget, world, out bool isPallet, out bool isBench);
            if (target == "?") { target = r1LastTarget; isPallet = r1LastPallet; isBench = r1LastBench; }
            else { r1LastTarget = target; r1LastPallet = isPallet; r1LastBench = isBench; }
            bool still = Still("R1", r1.transform) >= 1f;
            if (s.Contains("sur place")) { label = "attente sur place (décision Muse)"; cat = "attente décision"; return; }
            if (s.StartsWith("retour") && !still) { label = "retour au poste"; cat = "retour base"; return; }
            if (!r1.MissionActive || s.StartsWith("retour")) { label = "inactif (au poste)"; cat = "inactif"; return; }
            if (s.Contains("suspendue")) { label = "mission suspendue (urgente)"; cat = "suspendu"; return; }
            if (s.Contains("banc plein")) { label = "attente : banc plein"; cat = "attente banc"; return; }
            if (s.Contains("occupé") || s.Contains("bloqu") || s.StartsWith("recalcul")) { label = "attente : " + r1.Status; cat = "attente trajet"; return; }
            if (s.Contains(" posé (")) { label = "carton posé sur " + target + " (" + (r1.ActiveLeg != null ? r1.ActiveLeg.Picked + "/" + r1.ActiveLeg.Planned + " depuis " + r1.ActiveLeg.PalletId : "") + ")"; cat = "manutention"; return; }
            if (s.StartsWith("vers ")) { label = "trajet → " + target; cat = isPallet ? "trajet vers palette" : isBench ? "trajet vers banc" : "trajet"; return; }
            if (s.Contains("déchargement")) { label = "dépose carton sur " + target; cat = "manutention"; return; }
            if (s.Contains("chargement") || s.StartsWith("prise")) { label = "prise carton sur " + target; cat = "manutention"; return; }
            label = r1.Status;
            cat = "autre";
        }

        private void DescribeR2(TruckLoaderAgent r2, out string label, out string cat)
        {
            OrderTrack t = orderTracks.TryGetValue(r2.DirectedOrderId, out OrderTrack ot) ? ot : null;
            string bench = t != null && t.Bench != "" ? t.Bench : "banc";
            string truck = t != null && t.Truck != "" ? t.Truck : "camion";
            string s = (r2.Status ?? "").ToLowerInvariant();
            bool still = Still("R2", r2.transform) >= 1f;
            if (r2.State == TruckLoaderAgent.R2State.RetourBase && still) { label = "inactif"; cat = "inactif"; return; }
            if (!r2.MissionActive && r2.State != TruckLoaderAgent.R2State.RetourBase) { label = "inactif"; cat = "inactif"; return; }
            switch (r2.State)
            {
                case TruckLoaderAgent.R2State.AttenteColis: label = "attente cartons sur " + bench; cat = "attente cartons"; return;
                case TruckLoaderAgent.R2State.AttenteCamion:
                    label = s.Contains("plein") ? "attente : camion plein" : "attente porte " + truck; cat = "attente porte"; return;
                case TruckLoaderAgent.R2State.VersBanc:
                    if (s.StartsWith("suspension")) { label = "remise du carton sur " + bench + " (suspension)"; cat = "suspendu"; return; }
                    label = "trajet → " + bench; cat = "trajet vers banc"; return;
                case TruckLoaderAgent.R2State.Prise: label = "prise carton sur " + bench; cat = "manutention"; return;
                case TruckLoaderAgent.R2State.VersCamion:
                    if (s.StartsWith("sortie")) { label = "sortie de la remorque " + truck; cat = "sortie remorque"; return; }
                    label = "trajet → " + truck + (r2.Carried != null ? " (carton en main)" : ""); cat = "trajet vers camion"; return;
                case TruckLoaderAgent.R2State.Depose: label = "dépose carton dans " + truck; cat = "manutention"; return;
                case TruckLoaderAgent.R2State.RetourBase: label = "retour base"; cat = "retour base"; return;
                default: label = r2.Status; cat = "autre"; return;
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
            Line(id, label + (orderId >= 0 ? "  [#" + orderId + "]" : "") + "      (fin : " + a.Label + " — " + spent.ToString("F1") + " s)");
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
                Line("PORTE", id + " " + prev + " → " + d + (bay.ReservedOrderId >= 0 ? "  [#" + bay.ReservedOrderId + "]" : "") +
                              "  (" + bay.LoadedCount + "/" + bay.Capacity + ")");
            }
        }

        private void TrackVoice()
        {
            if (voice == null) return;
            string st = voice.State.ToString();
            if (st != lastVoiceState)
            {
                if (lastVoiceState != "") Line("VOIX", lastVoiceState + " → " + st + (st == "Recording" ? " (micro ouvert)" : ""));
                lastVoiceState = st;
            }
            if (voice.FinalTranscript != lastFinal)
            {
                lastFinal = voice.FinalTranscript;
                if (lastFinal.Length > 0)
                    Line("VOIX", "transcription finale « " + lastFinal + " »  (ASR " + voice.LastAsrLatencyMs + " ms, " +
                                 voice.LastCaptureSeconds.ToString("F1") + " s, crête " + (voice.LastCapturePeak > 0f ? (20f * Mathf.Log10(voice.LastCapturePeak)).ToString("F0") : "-inf") + " dBFS)");
            }
            if (voice.LastFailure != lastFailure)
            {
                lastFailure = voice.LastFailure;
                if (lastFailure.Length > 0) Line("VOIX", "échec : " + lastFailure);
            }
        }

        // ---------------------------------------------------------------- bilan

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
                WriteSummary("toutes les commandes sont livrées");
            }
        }

        private bool finished;

        private void Finish()
        {
            if (finished || writer == null) return;
            finished = true;
            CloseOpenSegments();
            WriteSummary("fin de session");
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
            sb.AppendLine("================================ BILAN (" + why + ", t=" + Time.time.ToString("F0") + " s) ================================");

            // ---- commandes
            sb.AppendLine("COMMANDES — durée totale et temps passé dans chaque phase");
            foreach (OrderTrack t in orderTracks.Values)
            {
                var pt = new Dictionary<string, float>(t.PhaseTime);
                if (t.Delivered < 0f) pt[Phase(t.Status)] = (pt.TryGetValue(Phase(t.Status), out float acc) ? acc : 0f) + (Time.time - t.StatusSince);
                string phases = "";
                foreach (KeyValuePair<string, float> kv in pt)
                    if (kv.Key != "livrée" && kv.Value >= .5f) phases += kv.Key + " " + kv.Value.ToString("F0") + " s · ";
                sb.AppendLine("  #" + t.Id + (t.Urgent ? " URG" : "    ") + "  " + t.Composition.PadRight(10) + "  " +
                              (t.Delivered >= 0f ? "livrée en " + (t.Delivered - t.Created).ToString("F0") + " s" : "NON livrée (" + Phase(t.Status) + ")").PadRight(26) +
                              phases.TrimEnd(' ', '·') +
                              (t.Decisions > 0 ? "   [Muse : " + t.Decisions + " décision(s), " + t.DecisionLatency.ToString("F0") + " s d'attente]" : ""));
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
                    if (kv.Key.StartsWith("trajet") || kv.Key == "manutention") work += kv.Value;
                sb.AppendLine(robot + " — emploi du temps (" + total.ToString("F0") + " s, travail utile " + (100f * work / total).ToString("F0") + " %)");
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
            sb.AppendLine("MUSE — " + museCalls.Count + " appel(s) : " + useful + " utile(s), " + noEffect + " sans effet (wait/no_plan…), " +
                          rejected + " refusé(s)/échoué(s) ; attente cumulée " + latency.ToString("F0") + " s  [" + string.Join(", ", trig) + "]");

            // ---- portes
            foreach (KeyValuePair<string, float> kv in doorOpenTotal)
            {
                float loadingT = doorLoadingTotal.TryGetValue(kv.Key, out float l) ? l : 0f;
                sb.AppendLine("PORTE " + kv.Key + " — ouverte " + kv.Value.ToString("F0") + " s dont chargement actif " + loadingT.ToString("F0") + " s");
            }
            sb.AppendLine("DIVERS — mouvements de cartons " + cartonMoves + ", changements d'itinéraire (rupture) " + routeChanges +
                          ", mises en pause " + suspensions + ", incidents " + incidentsCount);

            // ---- étapes inutiles
            sb.AppendLine("ÉTAPES QUI NE SERVENT À RIEN / À SURVEILLER");
            int found = 0;
            foreach (string robot in new[] { "R1", "R2" })
            {
                for (int i = 0; i < all.Count; i++)
                {
                    Segment s = all[i];
                    if (s.Actor != robot || s.Category != "retour base") continue;
                    // retour base suivi d'une nouvelle mission peu après -> aller-retour inutile
                    float idleAfter = 0f;
                    Segment nextWork = null;
                    for (int j = i + 1; j < all.Count; j++)
                    {
                        if (all[j].Actor != robot) continue;
                        if (all[j].Category == "inactif") { idleAfter += all[j].Duration; continue; }
                        if (all[j].Category == "retour base") break; // même trajet, découpé
                        nextWork = all[j];
                        break;
                    }
                    if (nextWork != null && idleAfter < 25f)
                    {
                        found++;
                        sb.AppendLine("  • " + robot + " t=" + s.Start.ToString("F0") + " s : retour base " + s.Duration.ToString("F0") +
                                      " s puis nouvelle mission " + idleAfter.ToString("F0") + " s après — aller-retour inutile (~" +
                                      (2f * s.Duration + idleAfter).ToString("F0") + " s perdues)");
                    }
                }
            }
            foreach (Segment s in all)
            {
                if (s.Duration < 20f) continue;
                if (s.Category == "attente cartons" || s.Category == "attente porte" || s.Category == "attente décision" ||
                    s.Category == "attente banc" || s.Category == "attente trajet")
                {
                    found++;
                    sb.AppendLine("  • " + s.Actor + " t=" + s.Start.ToString("F0") + " s : " + s.Label + " pendant " + s.Duration.ToString("F0") + " s" +
                                  (s.OrderId >= 0 ? " (#" + s.OrderId + ")" : ""));
                }
            }
            foreach (KeyValuePair<string, float> kv in doorOpenTotal)
            {
                float loadingT = doorLoadingTotal.TryGetValue(kv.Key, out float l) ? l : 0f;
                if (kv.Value - loadingT > 60f)
                    sb.AppendLine("  (info) porte " + kv.Key + " ouverte " + (kv.Value - loadingT).ToString("F0") +
                                  " s sans chargement — ouverte dès la décision, ne retarde aucun robot");
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
                    sb.AppendLine("  • appel Muse " + c.Id + " [" + c.Trigger + "] sans effet : " + c.Result);
                }
            foreach (OrderTrack t in orderTracks.Values)
            {
                float wait = t.PhaseTime.TryGetValue("attente décision Muse", out float w) ? w : 0f;
                if (wait > 60f)
                {
                    found++;
                    sb.AppendLine("  • #" + t.Id + " a attendu " + wait.ToString("F0") + " s une décision (ressources occupées ou latence Muse)");
                }
            }
            if (routeChanges > 0)
            {
                found++;
                sb.AppendLine("  • " + routeChanges + " changement(s) d'itinéraire : une palette prévue n'avait plus le carton accessible (rupture sur le dessus)");
            }
            if (found == 0) sb.AppendLine("  aucune détectée");
            sb.AppendLine("=====================================================================================================");
            try { writer.Write(sb.ToString()); } catch (Exception) { }
        }
    }
}
