using RoboFactory.Hackathon.Orchestration;
using UnityEngine;

namespace RoboFactory.Logistics
{
    /// <summary>
    /// Interface de debug provisoire (pas le HUD JARVIS définitif) :
    /// modèle Meta réel, commande choisie, palettes/banc/camion, portes, robots,
    /// latence, verdict du validateur, incidents — zone de saisie pour envoyer
    /// une commande ("A x3, B x2"), bouton MICRO push-to-talk des commandes
    /// urgentes vocales et bouton manuel d'urgence.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MuseDebugHud : MonoBehaviour
    {
        public MuseOrchestrator Orchestrator;
        public VoiceCommandController Voice;
        public bool Visible = true;
        public KeyCode ToggleKey = KeyCode.F1;

        private string orderInput = "A x3, B x2";
        private string urgentInput = "deux cartons A et un carton B";
        private Vector2 scroll;
        private UrgentDemoReport report;
        private int maxStage = -1;

        private static readonly string[] StageNames =
        {
            "Listening", "Transcribing", "Planning", "Suspending",
            "Urgent Active", "Resuming", "Completed"
        };

        private void Start()
        {
            if (Orchestrator == null) Orchestrator = FindFirstObjectByType<MuseOrchestrator>();
            if (Voice == null) Voice = FindFirstObjectByType<VoiceCommandController>();
            // Le contrôleur voix s'auto-installe sur le même GameObject si absent
            // de la scène (il retrouve l'orchestrateur dans son propre Start).
            if (Voice == null) Voice = gameObject.AddComponent<VoiceCommandController>();
            report = GetComponent<UrgentDemoReport>();
            if (report == null) report = gameObject.AddComponent<UrgentDemoReport>();
            report.Orchestrator = Orchestrator;
            report.Voice = Voice;
        }

        private void Update()
        {
            if (Input.GetKeyDown(ToggleKey)) Visible = !Visible;
        }

        private void OnGUI()
        {
            if (!Visible || Orchestrator == null) return;
            const int W = 390;
            var area = new Rect(8, 8, W, Screen.height - 16);
            GUILayout.BeginArea(area, GUI.skin.box);
            scroll = GUILayout.BeginScrollView(scroll);

            GUILayout.Label("<b>MUSE WONDER — ORCHESTRATION</b>", Rich());
            GUILayout.Label("Modèle : " + Orchestrator.ModelUsed +
                            "   mode " + Orchestrator.Mode +
                            "   appels " + Orchestrator.Calls +
                            "   latence " + Orchestrator.LastLatencyMs + " ms");
            GUILayout.Label("État v" + Orchestrator.StateVersion +
                            "   phase " + Orchestrator.CurrentPhase +
                            (Orchestrator.Pending ? "   [appel " + Orchestrator.PendingInfo + "]" : ""));
            GUILayout.Label("Validation : " + Orchestrator.LastValidation);
            GUILayout.Space(4);

            DrawPipeline();
            DrawDemoChecklist();

            // ---- bandeau suspension / reprise
            if (Orchestrator.SuspendingCount > 0 || Orchestrator.PausedCount > 0 ||
                Orchestrator.UrgentOrderId >= 0)
            {
                string banner = "<b>⚠ URGENCE #" + Orchestrator.UrgentOrderId + "</b>  ";
                if (Orchestrator.SuspendingCount > 0)
                    banner += Orchestrator.SuspendingCount + " commande(s) en SUSPENDING";
                if (Orchestrator.PausedCount > 0)
                    banner += "  |  " + Orchestrator.PausedCount + " en PAUSE";
                GUILayout.Label(banner, Rich());
                if (!string.IsNullOrEmpty(Orchestrator.UrgentStatus))
                    GUILayout.Label("  " + Orchestrator.UrgentStatus);
                GUILayout.Space(4);
            }

            var last = Orchestrator.LastDecision;
            if (last != null)
            {
                GUILayout.Label("<b>Dernière décision</b> (" + last.RequestId + ")", Rich());
                GUILayout.Label("  action " + last.Action + "  commande #" + last.OrderId +
                                (last.Accepted ? "  ✔" : "  ✘ " + last.RejectReason));
                if (!string.IsNullOrEmpty(last.PlanSummary)) GUILayout.Label("  " + last.PlanSummary);
                if (!string.IsNullOrEmpty(last.Reason)) GUILayout.Label("  raison : " + last.Reason);
            }
            GUILayout.Space(4);

            GUILayout.Label("<b>Sélection</b>", Rich());
            GUILayout.Label("  commande : " + (Orchestrator.ActiveOrderId >= 0
                ? "#" + Orchestrator.ActiveOrderId : "aucune"));
            if (!string.IsNullOrEmpty(Orchestrator.ActivePickPlan))
                GUILayout.Label("  palettes : " + Orchestrator.ActivePickPlan);
            GUILayout.Label("  banc : " + (Orchestrator.BenchAssigned ?? "-") +
                            "   camion : " + (Orchestrator.TruckAssigned ?? "-"));
            GUILayout.Space(4);

            if (WarehouseWorld.Instance != null)
            {
                GUILayout.Label("<b>Portes</b>", Rich());
                foreach (string truckId in WarehouseWorld.Instance.TruckIds)
                    GUILayout.Label("  " + truckId + " : " + Orchestrator.DoorStateOf(truckId));
                GUILayout.Label("<b>Robots</b>", Rich());
                GUILayout.Label("  R1 : " + Orchestrator.RobotStatusOf("R1"));
                GUILayout.Label("  R2 : " + Orchestrator.RobotStatusOf("R2"));
                GUILayout.Label("  employé le plus proche de R1 : " +
                                Dist(Orchestrator.MinRobotWorkerDistance("R1")) + " m" +
                                "   R2 : " + Dist(Orchestrator.MinRobotWorkerDistance("R2")) + " m");
            }
            GUILayout.Space(4);

            GUILayout.Label("<b>Commandes</b>", Rich());
            foreach (WhOrderState o in Orchestrator.OrdersSnapshot)
            {
                int restA = o.a - o.staged_a - o.loaded_a;
                int restB = o.b - o.staged_b - o.loaded_b;
                int restC = o.c - o.staged_c - o.loaded_c;
                GUILayout.Label("  #" + o.id + " [" + o.status + "]  besoin A" + o.a + " B" + o.b + " C" + o.c +
                                "  préparé " + (o.staged_a + o.staged_b + o.staged_c) +
                                "  chargé " + (o.loaded_a + o.loaded_b + o.loaded_c) +
                                "  reste A" + restA + " B" + restB + " C" + restC);
            }
            GUILayout.Space(4);

            GUILayout.Label("<b>Incidents</b> : " + Orchestrator.IncidentsText, Rich());
            GUILayout.Space(8);

            // ---- commandes urgentes vocales
            GUILayout.Label("<b>MUSE VOICE — COMMANDE URGENTE</b>" +
                            (Voice != null && !string.IsNullOrEmpty(Voice.LastAsrMode)
                                ? "   [" + Voice.LastAsrMode.ToUpperInvariant() + "]" : ""), Rich());
            if (Voice != null)
            {
                string mic = Voice.IsRecording
                    ? "■ STOP  (" + Voice.RecordingElapsed.ToString("F1") + " s)"
                    : Voice.State == VoiceCommandController.VoiceState.Finishing
                        ? "… transcription…"
                        : "● MICRO (appuyer = parler)";
                GUI.backgroundColor = Voice.IsRecording
                    ? new Color(1f, .35f, .35f) : new Color(.55f, .9f, .55f);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(mic, GUILayout.Height(34), GUILayout.Width(190)))
                    Voice.Toggle();
                GUI.backgroundColor = Color.white;
                if (Voice.Busy && GUILayout.Button("Annuler", GUILayout.Height(34),
                        GUILayout.Width(80)))
                    Voice.CancelTalk();
                GUILayout.EndHorizontal();
                GUILayout.Label("  " + Voice.StatusText);
                if (!string.IsNullOrEmpty(Voice.LastFailure))
                    GUILayout.Label("  <color=#ff8080>" + Voice.LastFailure + "</color>", Rich());
                if (!string.IsNullOrEmpty(Voice.LiveTranscript))
                    GUILayout.Label("  live : <i>" + Voice.LiveTranscript + "</i>", Rich());
                if (!string.IsNullOrEmpty(Voice.FinalTranscript))
                    GUILayout.Label("  final : " + Voice.FinalTranscript);
                if (Voice.LastAsrLatencyMs > 0 || Voice.AudioProcessedMs > 0)
                    GUILayout.Label("  ASR " + (string.IsNullOrEmpty(Voice.LastAsrModel)
                        ? "" : Voice.LastAsrModel + " — ") + Voice.LastAsrLatencyMs +
                        " ms   audio accepté " + Voice.AudioProcessedMs +
                        " ms (transport seul)");
            }
            else GUILayout.Label("  VoiceCommandController absent de la scène");

            GUILayout.BeginHorizontal();
            urgentInput = GUILayout.TextField(urgentInput, GUILayout.Width(220));
            if (GUILayout.Button("Urgence manuelle", GUILayout.Width(120)))
            {
                int[] counts = OrderText.Parse(urgentInput);
                if (OrderText.Total(counts) <= 0)
                    Orchestrator.SubmitUrgentTranscript(urgentInput); // texte libre -> Muse
                else
                    Orchestrator.SubmitUrgentCounts(counts[0], counts[1], counts[2]);
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("(urgence manuelle : \"A x2, B x1\" ou phrase libre)");
            if (!string.IsNullOrEmpty(Orchestrator.UrgentError))
                GUILayout.Label("  <color=#ff8080>ERREUR : " + Orchestrator.UrgentError +
                                "</color>", Rich());
            GUILayout.Space(8);

            // ---- rapport de validation PlayMode
            if (report != null && report.Ready)
            {
                GUILayout.Label("<b>RAPPORT DE VALIDATION</b>", Rich());
                GUILayout.Label(report.Summary,
                    new GUIStyle(GUI.skin.label) { richText = false, wordWrap = true });
                if (!string.IsNullOrEmpty(report.LastPath))
                    GUILayout.Label("  fichier : " + report.LastPath);
                GUILayout.Space(4);
            }

            GUILayout.Label("<b>Nouvelle commande</b> (ex: A x3, B x2)", Rich());
            GUILayout.BeginHorizontal();
            orderInput = GUILayout.TextField(orderInput, GUILayout.Width(220));
            if (GUILayout.Button("Envoyer à Muse", GUILayout.Width(120)))
            {
                int id = Orchestrator.SubmitOrder(orderInput);
                if (id <= 0) Debug.LogWarning("[HUD] commande illisible : " + orderInput);
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("(F1 : masquer l'interface)");
            if (!string.IsNullOrEmpty(MuseTrace.FilePath))
                GUILayout.Label("Journal : " + MuseTrace.FilePath);

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        /// <summary>Stepper visible du pipeline urgent : voix -> Muse -> physique.</summary>
        private void DrawPipeline()
        {
            int stageIdx = -1;
            if (Voice != null)
            {
                if (Voice.State == VoiceCommandController.VoiceState.Starting ||
                    Voice.IsRecording) stageIdx = 0;
                else if (Voice.State == VoiceCommandController.VoiceState.Finishing)
                    stageIdx = 1;
            }
            MuseOrchestrator.UrgentStage us = Orchestrator.Stage;
            bool failed = us == MuseOrchestrator.UrgentStage.Failed;
            if (us != MuseOrchestrator.UrgentStage.Idle && !failed)
                stageIdx = 1 + (int)us; // Planning=1 -> affichage 2 ... Completed=5 -> 6
            if (failed && stageIdx < 0) stageIdx = Mathf.Max(2, maxStage);
            if (stageIdx <= 0) maxStage = stageIdx;
            else maxStage = Mathf.Max(maxStage, stageIdx);

            var sb = new System.Text.StringBuilder("<b>PIPELINE </b> ");
            for (int i = 0; i < StageNames.Length; i++)
            {
                if (i > 0) sb.Append(" <color=#666>→</color> ");
                string name = StageNames[i];
                if (failed && i == stageIdx)
                    sb.Append("<color=#ff8080><b>✘ ").Append(name).Append("</b></color>");
                else if (i < stageIdx ||
                         (i == stageIdx && us == MuseOrchestrator.UrgentStage.Completed))
                    sb.Append("<color=#7fff9f>✔ ").Append(name).Append("</color>");
                else if (i == stageIdx)
                    sb.Append("<color=#7fd4ff><b>▶ ").Append(name).Append("</b></color>");
                else sb.Append("<color=#888>· ").Append(name).Append("</color>");
            }
            GUILayout.Label(sb.ToString(), Rich());
        }

        /// <summary>Checklist guidée du scénario jury (commande normale en un clic).</summary>
        private void DrawDemoChecklist()
        {
            GUILayout.Label("<b>DÉMO JURY — scénario guidé</b>", Rich());
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("1. Commande normale démo (A x2, B x1)",
                    GUILayout.Width(240)))
                Orchestrator.SubmitOrder("A x2, B x1");
            GUILayout.EndHorizontal();
            GUILayout.Label("  2. Laisser R1 poser ≥ 2 cartons sur le banc\n" +
                            "  3. MICRO → annoncer l'urgente en français\n" +
                            "  4. Observer Suspension → Urgent → Resuming → Completed\n" +
                            "  5. Lire le RAPPORT : voix réelle / Muse réel / exécution");
        }

        private static string Dist(float d)
        {
            return float.IsPositiveInfinity(d) ? "—" : d.ToString("F1");
        }

        private static GUIStyle rich;
        private static GUIStyle Rich()
        {
            if (rich == null) rich = new GUIStyle(GUI.skin.label) { richText = true };
            return rich;
        }
    }
}
