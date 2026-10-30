using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RoboFactory.Hackathon.Orchestration;
using UnityEngine;

namespace RoboFactory.Logistics
{
    /// <summary>
    /// Rapport de validation PlayMode pour la démo urgente. Surveille le stage
    /// de l'orchestrateur, fige l'état des commandes au moment PAUSED, puis à
    /// la fin du flux vérifie la conservation des cartons (ni perte ni doublon),
    /// la fermeture des camions et l'absence de réservation résiduelle.
    ///
    /// Le rapport distingue explicitement trois preuves :
    ///  - voice   : transcription FINALE réellement émise par muse-voice
    ///              (audio_ms = preuve de transport SEULEMENT, jamais présenté
    ///              comme preuve de transcription) ;
    ///  - muse    : décision réelle du modèle (mode "live" + modèle muse-*) ;
    ///  - exec    : quantités livrées == quantités commandées, pour l'urgente
    ///              ET pour chaque commande reprise.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UrgentDemoReport : MonoBehaviour
    {
        [Header("Références")]
        public MuseOrchestrator Orchestrator;
        public VoiceCommandController Voice;
        [Tooltip("Fréquence de surveillance du stage.")]
        public float PollInterval = .25f;

        // ------------------------------------------------------------------ JSON
        [Serializable] public sealed class VoiceProof
        {
            public string mode;         // live | mock | manual
            public string model;
            public int latency_ms;
            public int audio_ms;        // transport seulement
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
            /// <summary>FINISH TO FREE : commande normale finie pour libérer la capacité de l'urgente.</summary>
            public string finish_to_free = "";
            public string verdict;
        }

        // ------------------------------------------------------------------ HUD
        /// <summary>Résumé multi-lignes affiché dans le HUD.</summary>
        public string Summary { get; private set; } = "";
        public string LastPath { get; private set; } = "";
        public bool Ready { get; private set; }
        public bool LastVerdictOk { get; private set; }

        private sealed class Snap
        {
            public int id; public bool urgent;
            public int a, b, c;
            public int sa, sb, sc;   // staged au moment PAUSED
            public int la, lb, lc;   // loaded au moment PAUSED
        }

        private readonly List<Snap> paused = new List<Snap>();
        private int urgentOrderId = -1;
        private int finishedForUrgent = -1;
        private string freedBench = "", freedTruck = "";
        private MuseOrchestrator.UrgentStage prevStage = MuseOrchestrator.UrgentStage.Idle;
        private float nextPoll;
        // état voix figé à l'entrée du flux (évite de réutiliser d'anciennes données)
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

            // Entrée en planning : capturer la voix immédiatement — le flux peut
            // mourir ici (wait/clarify/échec Meta) sans jamais atteindre Suspending.
            if (s == MuseOrchestrator.UrgentStage.Planning &&
                prevStage != MuseOrchestrator.UrgentStage.Planning)
            {
                Ready = false;
                CaptureVoice();
            }
            // Entrée en suspension : nouveau flux -> nouvelles mesures.
            if (s == MuseOrchestrator.UrgentStage.Suspending &&
                prevStage != MuseOrchestrator.UrgentStage.Suspending)
            {
                paused.Clear();
                urgentOrderId = Orchestrator.UrgentOrderId;
                if (!Orchestrator.CapacityEpisode) finishedForUrgent = -1;
                Ready = false;
                CaptureVoice(); // rattrapage si Planning n'a pas été observé
            }
            // Suspension terminée : les commandes sont PAUSED, figer l'état.
            if (prevStage == MuseOrchestrator.UrgentStage.Suspending &&
                s != MuseOrchestrator.UrgentStage.Suspending)
                CapturePaused();
            // Suspension trop rapide pour être observée : figer à UrgentActive.
            if (s == MuseOrchestrator.UrgentStage.UrgentActive && paused.Count == 0)
                CapturePaused();
            // Fin de flux : générer le rapport.
            if ((s == MuseOrchestrator.UrgentStage.Completed ||
                 s == MuseOrchestrator.UrgentStage.Failed) && prevStage != s)
                BuildReport(s);
            // FINISH TO FREE : mémoriser la commande que Muse a fait finir pour l'urgente
            if (Orchestrator.FinishingOrderId >= 0)
            {
                finishedForUrgent = Orchestrator.FinishingOrderId;
                freedBench = Orchestrator.FreeingBenchId;
                freedTruck = Orchestrator.FreeingTruckId;
            }
            if (urgentOrderId < 0 && Orchestrator.UrgentOrderId >= 0)
                urgentOrderId = Orchestrator.UrgentOrderId; // rattrapage
            prevStage = s;
        }

        /// <summary>Fige les champs voix du contrôleur au début du flux.</summary>
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

        /// <summary>Fige staged/loaded de chaque commande PAUSED (preuve de conservation).</summary>
        private void CapturePaused()
        {
            paused.Clear();
            foreach (WhOrderState o in Orchestrator.OrdersSnapshot)
            {
                // commandes en pause + l'urgente DE CE FLUX (pas une urgente d'une vague précédente)
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

            // ---- 2. MUSE d'abord : son record dit si la voix a été utilisée
            var mp = new MuseProof { mode = "none" };
            MuseDecisionRecord rec = null;
            IReadOnlyList<MuseDecisionRecord> hist = Orchestrator.History;
            for (int i = hist.Count - 1; i >= 0; i--)
                if (hist[i].OrderId == urgentOrderId && urgentOrderId >= 0) { rec = hist[i]; break; }
            if (rec == null) // appel /urgent échoué avant création de commande
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

            // ---- 1. VOIX : réel = transcription FINALE émise par muse-voice.
            //          Le flux n'est "vocal" que si Muse a reçu une transcription.
            bool voiceUsed = rec != null && rec.Trigger == "urgent_voice_order";
            var vp = new VoiceProof();
            if (voiceUsed)
            {
                vp.mode = flowVoiceMode;
                vp.model = flowVoiceModel;
                vp.latency_ms = flowVoiceLatency;
                vp.audio_ms = flowAudioMs; // transport seulement — pas une preuve
                vp.transcript = flowTranscript;
                vp.final_submitted = flowVoiceFinal;
                vp.real = flowVoiceMode == "live" && flowVoiceFinal &&
                          !string.IsNullOrEmpty(flowTranscript);
                vp.note = vp.real ? "transcription finale réelle (Meta muse-voice)"
                    : flowVoiceFinal ? "transcription mock — pas une preuve réelle"
                    : "pas de transcription finale : " + flowVoiceFailure;
            }
            else
            {
                vp.mode = "manual";
                vp.note = "urgence déclenchée manuellement (pas de session vocale)";
            }
            report.voice = vp;

            // ---- 3. EXÉCUTION : conservation des cartons, livraison exacte
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
                    oc.status = "disparue";
                    oc.ok = false;
                    oc.note = "commande perdue";
                }
                else
                {
                    oc.status = cur.status;
                    oc.delivered = Cnts(cur.loaded_a, cur.loaded_b, cur.loaded_c);
                    bool exact = cur.status == "loaded" &&
                                 cur.loaded_a == p.a && cur.loaded_b == p.b && cur.loaded_c == p.c;
                    bool noOverflow = cur.loaded_a <= p.a && cur.loaded_b <= p.b && cur.loaded_c <= p.c;
                    oc.ok = exact && noOverflow;
                    oc.note = oc.ok ? "livré exact, sans doublon ni perte"
                        : !noOverflow ? "DOUBLON : livré > commandé"
                        : cur.status == "loaded" ? "livré ≠ commandé (perte)"
                        : "non terminée : " + cur.status;
                }
                if (!oc.ok) execOk = false;
                report.orders.Add(oc);
            }
            // L'urgente peut ne pas figurer dans paused (ex. créée après figeage).
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
                    oc.note = oc.ok ? "urgente livrée exacte" : "urgente non livrée : " + cur.status;
                }
                else { oc.status = "disparue"; oc.note = "urgente perdue"; }
                if (!oc.ok) execOk = false;
                report.orders.Add(oc);
            }

            // ---- FINISH TO FREE : la commande finie pour libérer l'urgente doit être livrée exacte
            if (finishedForUrgent >= 0)
            {
                WhOrderState f = Find(now, finishedForUrgent);
                bool fOk = f != null && f.status == "loaded" && f.loaded_a == f.a && f.loaded_b == f.b && f.loaded_c == f.c;
                report.finish_to_free = "#" + finishedForUrgent + " finie pour libérer " + freedBench + " / " + freedTruck +
                                        (fOk ? " — livrée exacte" : " — " + (f != null ? f.status : "disparue"));
                if (!fOk) execOk = false;
            }

            // ---- intégrité monde : portes fermées, réservations libérées.
            // Une commande normale qui n'appartient pas au flux urgent et qui est ENCORE en cours
            // (démarrée après la reprise) détient légitimement ses ressources : citée, pas comptée.
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
                    if (!string.IsNullOrEmpty(by) && !Legit(by, id)) stillReserved.Add("palette " + id);
                }
                foreach (string id in world.BenchIds)
                {
                    string by = world.ReservedBy(id);
                    if (!string.IsNullOrEmpty(by) && !Legit(by, id)) stillReserved.Add("banc " + id);
                }
                foreach (string id in world.TruckIds)
                {
                    string by = world.ReservedBy(id);
                    if (!string.IsNullOrEmpty(by) && !Legit(by, id)) stillReserved.Add("camion " + id);
                }
                report.integrity.Add(stillReserved.Count == 0
                    ? "aucune réservation résiduelle"
                    : "RESSOURCES ENCORE RÉSERVÉES : " + string.Join(", ", stillReserved));
                if (inProgress.Count > 0)
                    report.integrity.Add("commande(s) normale(s) encore en cours, hors flux urgent : " + string.Join(", ", inProgress));
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
                    ? "tous les camions fermés"
                    : "PORTES NON FERMÉES : " + string.Join(", ", doors));
            }
            else report.integrity.Add("WarehouseWorld absent — intégrité non vérifiable");
            bool integrityOk = report.integrity.TrueForAll(x =>
                !x.StartsWith("RESSOURCES") && !x.StartsWith("PORTES") &&
                !x.Contains("non vérifiable"));

            // ---- verdict global
            bool urgentDelivered = s == MuseOrchestrator.UrgentStage.Completed;
            bool museOk = mp.real && mp.accepted;
            var fails = new List<string>();
            if (!urgentDelivered) fails.Add("urgente non livrée");
            if (!museOk) fails.Add("décision Muse non réelle/refusée");
            if (!execOk) fails.Add("cartons perdus ou dupliqués");
            // L'intégrité ne compte que si le flux a réellement touché des commandes —
            // sinon des portes/réservations actives des commandes normales feraient
            // échouer un rapport qui n'a jamais exécuté quoi que ce soit.
            if (!integrityOk && report.orders.Count > 0) fails.Add("intégrité monde");
            if (voiceUsed && !vp.real)
                fails.Add("transcription non finale/non réelle");
            report.verdict = fails.Count == 0
                ? "PASS — transcription réelle + décision Muse réelle + exécution vérifiée"
                : "FAIL — " + string.Join(" ; ", fails);
            LastVerdictOk = fails.Count == 0;
            Ready = true;

            // ---- résumé HUD + fichier JSON
            var sb = new StringBuilder();
            sb.AppendLine("VOIX   : " + vp.mode.ToUpperInvariant() +
                          (vp.real ? " — finale réelle \"" + vp.transcript + "\""
                                   : " — " + vp.note) +
                          " (" + vp.latency_ms + " ms, audio transporté " + vp.audio_ms + " ms)");
            sb.AppendLine("MUSE   : " + (rec != null
                ? mp.model + " " + mp.mode.ToUpperInvariant() + " " + mp.latency_ms +
                  " ms " + mp.action + (mp.accepted ? " OK" : " REFUSÉ") +
                  (!string.IsNullOrEmpty(mp.plan) ? " — " + mp.plan : "")
                : "aucun appel Muse enregistré"));
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
                      (LastPath.Length > 0 ? "fichier : " + LastPath : ""));
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
                Debug.LogWarning("[UrgentDemoReport] écriture JSON impossible : " + e.Message);
                return "";
            }
        }
    }
}
