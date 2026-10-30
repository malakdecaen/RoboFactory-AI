using System.Collections;
using System.Collections.Generic;
using System.Text;
using RoboFactory.Hackathon;
using RoboFactory.Hackathon.Meta;
using RoboFactory.Hackathon.Orchestration;
using RoboFactory.Integration;
using UnityEngine;
using RoboFactory.Sim;

namespace RoboFactory.Logistics
{
    /// <summary>
    /// Orchestrateur central : les décisions logistiques viennent de Muse Wonder via le
    /// proxy Meta (/warehouse). Cette classe ne choisit JAMAIS les palettes/bancs/camions
    /// elle-même : elle envoie l'état réel, valide le plan reçu, réserve les ressources,
    /// pilote les portes et les missions dirigées des robots, puis vérifie le contenu
    /// livré avant de refermer la porte.
    ///
    /// Une seule commande active et une seule porte ouverte à la fois (version 1).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MuseOrchestrator : MonoBehaviour, IMuseOrchestration
    {
        [Header("Proxy Meta")]
        public string ProxyUrl = "http://127.0.0.1:8765";
        public PerceptionMode Perception = PerceptionMode.Live;

        [Header("Décision")]
        [Tooltip("Enchaîne les demandes à Muse dès qu'une commande attend.")]
        public bool AutoDecide = true;
        [Tooltip("Pause minimale entre deux appels Meta.")]
        public float CallCooldown = 3f;
        [Tooltip("Nouvel essai quand Muse répond wait/no_plan.")]
        public float WaitRetryDelay = 8f;
        [Tooltip("Rejets successifs avant pause longue.")]
        public int MaxConsecutiveRejects = 4;
        [Tooltip("Commande defer_order : délai avant de redevenir candidate.")]
        public float DeferredReactivateDelay = 30f;

        [Header("Exécution (optionnel : trouvé automatiquement)")]
        public WarehouseRobotAutopilot Robot1;
        public TruckLoaderAgent Robot2;

        [Header("Voie urgente fixe (option)")]
        [Tooltip("Option : un banc et un camion réservés en permanence aux urgentes. Désactivée par défaut : " +
                 "les commandes normales utilisent les 3 bancs et 3 camions, et l'urgente libère sa capacité " +
                 "par la politique FINISH TO FREE.")]
        public bool ReserveUrgentLane = false;
        public string UrgentBenchId = "B01";
        public string UrgentTruckId = "T01";

        [Header("Sécurité")]
        [Tooltip("Incident si un employé passe à moins de cette distance d'un robot en mission.")]
        public float WorkerProximityAlert = .9f;

        // ------------------------------------------------------------------ état
        private enum Phase { Idle, Deciding, Preparing, Picking, Loading, Verifying, Closing }

        private MetaProxyClient proxy;
        private readonly List<WhOrderState> orders = new List<WhOrderState>();
        private readonly List<MuseDecisionRecord> history = new List<MuseDecisionRecord>();
        private readonly List<string> incidents = new List<string>();
        private readonly Dictionary<int, float> deferredUntil = new Dictionary<int, float>();
        private int nextOrderId = 1;
        // Plusieurs commandes peuvent s'exécuter en parallèle : chacune réserve SES
        // ressources (banc, camion, palettes) ; les robots servent en séquence.
        private readonly HashSet<int> activeOrderIds = new HashSet<int>();
        private readonly Dictionary<int, MusePlan> activePlans = new Dictionary<int, MusePlan>();
        private readonly List<Coroutine> running = new List<Coroutine>();
        private int lastActiveOrderId = -1;
        private int highlightedOrder = -1;
        private bool r1Busy, r2Busy;
        private int r1TicketSeq;
        /// <summary>Collectes en attente de R1 (commande -> ordre d'arrivée).</summary>
        private readonly Dictionary<int, int> r1Tickets = new Dictionary<int, int>();
        /// <summary>Commande détenant le verrou R2 (libérable même si sa file est tuée).</summary>
        private int r2BusyOwner = -1;
        private Phase phase = Phase.Idle;

        private int stateVersion;
        private int requestSeq;
        private string latestRequestId;
        private bool pending;
        private float pendingSince;
        /// <summary>Un appel sans réponse au-delà de ce délai est abandonné (anti-blocage).</summary>
        public float PendingTimeout = 135f;

        // ------------------------------------------------------------------ urgences vocales
        [Header("Commandes urgentes")]
        [Tooltip("Confiance minimale de la transcription pour exécuter sans confirmation.")]
        public float MinUrgentConfidence = .5f;
        [Tooltip("Délai max pour que les robots se mettent en état sûr à la suspension.")]
        public float SuspendTimeout = 30f;

        /// <summary>Étapes visibles du pipeline urgent pour le HUD et le rapport de démo.</summary>
        /// <summary>WaitingForCapacity = URGENT_WAITING_FOR_CAPACITY : urgente acceptée, aucun couple
        /// banc + camion vide disponible ; une commande normale est finie pour le libérer (FINISH TO FREE).</summary>
        public enum UrgentStage { Idle, Planning, Suspending, UrgentActive, Resuming, Completed, Failed, WaitingForCapacity }
        public UrgentStage Stage { get; private set; } = UrgentStage.Idle;
        /// <summary>Dernier échec du pipeline urgent (texte affiché, jamais masqué).</summary>
        public string UrgentError { get; private set; } = "";
        /// <summary>Issue de la dernière urgente : "livrée" / motif d'échec.</summary>
        public string LastUrgentOutcome { get; private set; } = "";
        /// <summary>Appel /urgent en vol (transcription -> interprétation Muse).</summary>
        public bool UrgentPending => urgentPending;
        /// <summary>Commandes reprises en cours d'exécution après l'urgente.</summary>
        private readonly HashSet<int> resumedIds = new HashSet<int>();
        /// <summary>L'urgente courante a-t-elle été physiquement livrée.</summary>
        private bool urgentDelivered;
        /// <summary>Muse a répondu "wait" : une nouvelle tentative vocale est planifiée.
        /// Tant qu'elle est armée, aucune décision normale ne doit voler les ressources
        /// qui se libèrent — l'urgente garde la priorité.</summary>
        private bool urgentWaiting;
        private int urgentVoiceRetries;
        private const int MaxUrgentVoiceRetries = 24; // ~2 min d'attente de libération
        /// <summary>Génération vocale : invalide les retries armés par une ancienne prise.</summary>
        private int urgentVoiceGen;

        /// <summary>Appel /urgent en vol.</summary>
        private bool urgentPending;
        /// <summary>Transition SUSPENDING -> PAUSED en cours.</summary>
        private bool suspendInProgress;
        /// <summary>Commande urgente courante (-1 = aucune) : bloque l'auto-décision normale.</summary>
        private int urgentId = -1;
        /// <summary>R1 a déposé toute l'urgente au banc : il est rendu aux commandes
        /// en pause pendant que R2 finit le chargement de l'urgente.</summary>
        private bool urgentPickDone;
        private int urgentReplanFails;
        /// <summary>Commandes normales en cours de suspension.</summary>
        private readonly HashSet<int> suspendingIds = new HashSet<int>();
        /// <summary>Commandes normales en pause (état sauvegardé, reprise prévue).</summary>
        private readonly HashSet<int> pausedIds = new HashSet<int>();
        /// <summary>Plan Muse d'origine de chaque commande en pause (reprise si réalisable).</summary>
        private readonly Dictionary<int, MusePlan> pausedPlans = new Dictionary<int, MusePlan>();
        /// <summary>Requête en vol (HUD) : "wh-3 (12s)" ou vide.</summary>
        public string PendingInfo => pending && !string.IsNullOrEmpty(latestRequestId)
            ? latestRequestId + " (" + (Time.time - pendingSince).ToString("F0") + "s)" : "";
        private float nextAllowedCall;
        private int consecutiveRejects;
        private string lastRejection;
        private string lastValidation = "-";
        private string modelUsed = "-";
        private int lastLatencyMs;
        private MuseDecisionRecord lastDecision;
        private WarehouseSnapshot lastSnapshot;
        private string injectedPlanError;

        // affichage de la mission la plus récente (HUD de debug). Rapports partagés :
        // un seul pick (R1) et un seul load (R2) ne tournent jamais simultanément.
        private string activeBenchId;
        private string activeTruckId;
        private readonly List<string> activePalletIds = new List<string>();
        private WarehouseRobotAutopilot.PickMissionReport pickReport;
        private TruckLoaderAgent.LoadMissionReport loadReport;
        private string pickPlanText = "";
        private float workerAlertCooldown;

        // ------------------------------------------------------------------ IMuseOrchestration
        public int StateVersion => stateVersion;
        public bool Pending => pending;
        public int Calls { get; private set; }
        public string Mode => Perception.ToString().ToLowerInvariant();
        public string ModelUsed => modelUsed;
        public int LastLatencyMs => lastLatencyMs;
        public string LastValidation => lastValidation;
        public MuseDecisionRecord LastDecision => lastDecision;
        public WarehouseSnapshot LastSnapshot => lastSnapshot;
        public WhOrderState[] OrdersSnapshot => orders.ToArray();
        public int ActiveOrderId => lastActiveOrderId;
        public int ActiveCount => activeOrderIds.Count;
        public string ActivePickPlan => pickPlanText;
        public string BenchAssigned => activeBenchId;
        public string TruckAssigned => activeTruckId;
        public string IncidentsText => incidents.Count == 0 ? "aucun" : string.Join(" | ", incidents);
        public IReadOnlyList<MuseDecisionRecord> History => history;
        /// <summary>État du flux urgent pour le HUD (interprétation, suspension, exécution).</summary>
        public string UrgentStatus { get; private set; } = "";
        public int UrgentOrderId => urgentId;
        public int PausedCount => pausedIds.Count;
        public int SuspendingCount => suspendingIds.Count;
        public int ResumedCount => resumedIds.Count;

        private void Awake()
        {
            proxy = new MetaProxyClient { BaseUrl = ProxyUrl };
            capacityClient = new MuseCapacityClient { BaseUrl = ProxyUrl };
            // Les robots passent sous contrôle dirigé avant leur propre Start().
            if (Robot1 == null) Robot1 = FindFirstObjectByType<WarehouseRobotAutopilot>();
            if (Robot2 == null) Robot2 = FindFirstObjectByType<TruckLoaderAgent>();
            if (Robot1 != null) Robot1.AutoStart = false;
            if (Robot2 != null) Robot2.AutoStart = false;
        }

        private void Start()
        {
            if (World() == null)
            {
                Debug.LogError("[Muse] WarehouseWorld absent de la scène : orchestration impossible.");
                enabled = false;
                return;
            }
            // Toutes les baies passent sous gestion orchestrateur (porte fermée au départ).
            foreach (string truckId in World().TruckIds)
            {
                TruckBay bay = World().Truck(truckId);
                if (bay != null) bay.SetManaged(true);
            }
            if (Robot1 == null) Robot1 = World().R1;
            if (Robot2 == null) Robot2 = World().R2;
            if (Robot1 != null && !Robot1.Running) { Robot1.Stop(); Robot1.BeginDirected(); }
            // Après une collecte, R1 ne rentre pas à la base si du travail arrive :
            // décision Muse en vol (ex. reprise après urgente) ou commandes
            // suspendues / en pause. Sinon il faisait l'aller-retour pour rien.
            if (Robot1 != null)
                Robot1.StayOut = () => pending || urgentPending ||
                                       suspendingIds.Count > 0 || pausedIds.Count > 0;
            if (Robot2 != null)
            {
                if (!Robot2.Running) Robot2.BeginDirected();
                // Tant que des commandes sont suspendues/en pause, R2 ne repart pas
                // à la base entre deux chargements : la reprise est imminente.
                Robot2.StayOut = () => suspendingIds.Count > 0 || pausedIds.Count > 0;
            }
            Debug.Log("[Muse] orchestrateur prêt : " + World().PalletCount + " palettes, " +
                      World().BenchCount + " bancs, " + World().TruckCount + " camions, mode " + Mode);
        }

        private WarehouseWorld world;
        private WarehouseWorld World()
        {
            if (world == null) world = WarehouseWorld.Instance != null
                ? WarehouseWorld.Instance : FindFirstObjectByType<WarehouseWorld>();
            return world;
        }

        // ------------------------------------------------------------------ commandes

        public int SubmitOrder(string text)
        {
            int[] counts = OrderText.Parse(text);
            if (OrderText.Total(counts) <= 0) return 0;
            return SubmitOrderCounts(counts[0], counts[1], counts[2]);
        }

        public int SubmitOrderCounts(int a, int b, int c)
        {
            if (a + b + c <= 0) return 0;
            WhOrderState order = CreateOrder(a, b, c, false);
            Debug.Log("[Muse] commande #" + order.id + " : " + OrderText.Format(new[] { a, b, c }) +
                      (activeOrderIds.Count > 0 ? " (décidée dès que ressources disponibles)" : ""));
            if (AutoDecide) RequestDecision("new_order");
            return order.id;
        }

        private WhOrderState CreateOrder(int a, int b, int c, bool urgent)
        {
            var order = new WhOrderState
            {
                id = nextOrderId++,
                a = a, b = b, c = c,
                urgent = urgent,
                priority = urgent ? 0 : nextOrderId,
                status = "pending"
            };
            orders.Add(order);
            if (urgent)
            {
                urgentId = order.id;
                urgentReplanFails = 0;
            }
            stateVersion++;
            return order;
        }

        public void ClearOrders()
        {
            AbortExecution("commandes réinitialisées");
            orders.Clear();
            deferredUntil.Clear();
            suspendingIds.Clear();
            pausedIds.Clear();
            pausedPlans.Clear();
            urgentId = -1;
            urgentReplanFails = 0;
            UrgentStatus = "";
            Stage = UrgentStage.Idle;
            UrgentError = "";
            LastUrgentOutcome = "";
            urgentDelivered = false;
            urgentWaiting = false;
            urgentPickDone = false;
            resumedIds.Clear();
            ClearCapacityState();
            CapacityEpisode = false;
            phase = Phase.Idle;
            stateVersion++;
        }

        // ------------------------------------------------------------------ boucle principale

        private void Update()
        {
            RefreshLiveCounts();
            // Filet de sécurité : un appel jamais répondu ne doit pas bloquer la file.
            if (pending && Time.time - pendingSince > PendingTimeout)
            {
                pending = false;
                nextAllowedCall = Time.time + 5f;
                Incident("appel " + latestRequestId + " sans réponse après " +
                         PendingTimeout.ToString("F0") + "s : relance");
            }
            if (World() == null || !AutoDecide) return;
            if (pending || urgentPending || urgentWaiting || suspendInProgress) return;
            // Une commande urgente monopolise la boucle : les commandes normales
            // (suspendues ou en file) ne reprennent des décisions qu'après sa livraison.
            // Exception : collecte urgente terminée -> R1 est libre, les commandes
            // reprises peuvent obtenir leurs décisions pendant que R2 charge l'urgente.
            if (urgentId >= 0 && !urgentPickDone) return;
            if (Time.time < nextAllowedCall) return;
            // ---- fin de la reprise : toutes les commandes reprises sont livrées
            if (Stage == UrgentStage.Resuming && urgentId < 0)
            {
                bool anyLeft = false;
                foreach (int id in resumedIds)
                {
                    WhOrderState o = FindLocal(id);
                    if (o != null && o.status != "loaded") { anyLeft = true; break; }
                }
                if (!anyLeft) Stage = urgentDelivered ? UrgentStage.Completed : UrgentStage.Failed;
            }
            if (NextPending() == null) return;
            // Une commande n'est décidée que si elle peut réellement s'exécuter :
            // banc libre + camion libre + stock disponible. Sinon elle attend en file.
            if (!World().HasFreeBench(ReserveUrgentLane ? UrgentBenchId : null) ||
                !World().HasFreeTruck(ReserveUrgentLane ? UrgentTruckId : null) || !World().HasAnyFreeStock()) return;
            RequestDecision("pending_order");
        }

        private WhOrderState NextPending()
        {
            WhOrderState best = null;
            foreach (WhOrderState o in orders)
            {
                if (o.status != "pending") continue;
                if (deferredUntil.TryGetValue(o.id, out float until) && Time.time < until) continue;
                if (best == null || o.priority < best.priority) best = o;
            }
            return best;
        }

        // ------------------------------------------------------------------ appel Meta

        /// <summary>Force une demande de décision (async — la réponse arrive plus tard).</summary>
        public void RequestDecision(string trigger)
        {
            if (pending || urgentPending) return;
            // Urgente en cours (y compris en attente de capacité) : aucune nouvelle commande
            // normale n'est planifiée — les ressources libérées vont à l'urgente.
            if (urgentId >= 0 && !urgentPickDone && (trigger == "new_order" || trigger == "pending_order"))
            {
                Debug.Log("[Muse] décision normale différée (" + trigger + ") : urgente #" + urgentId + " prioritaire");
                return;
            }
            if (Mode == "offline")
            {
                lastValidation = "offline : pas d'appel Meta";
                lastRejection = "offline";
                return;
            }
            stateVersion++;
            requestSeq++;
            latestRequestId = "wh-" + requestSeq;
            lastSnapshot = Snapshot(trigger, (trigger ?? "").StartsWith("urgent"));
            var request = new WarehouseRequest
            {
                request_id = latestRequestId,
                state_version = stateVersion,
                mode = Mode,
                trigger = trigger,
                state_json = JsonUtility.ToJson(lastSnapshot),
                mock_plan_error = injectedPlanError
            };
            injectedPlanError = null; // consommé : une seule injection par appel
            pending = true;
            pendingSince = Time.time;
            Calls++;
            StartCoroutine(proxy.Orchestrate(request, response => OnPlanResponse(request, response)));
            Debug.Log("[Muse] demande " + latestRequestId + " (" + trigger + ", état v" + stateVersion + ")");
        }

        private void OnPlanResponse(WarehouseRequest request, WarehouseResponse response)
        {
            pending = false;
            nextAllowedCall = Time.time + CallCooldown;

            // Réponse périmée : une demande plus récente a été émise entre-temps.
            if (request.request_id != latestRequestId)
            {
                Debug.Log("[Muse] réponse ignorée (périmée) : " + request.request_id);
                return;
            }

            var record = new MuseDecisionRecord
            {
                RequestId = request.request_id,
                Trigger = request.trigger,
                StateVersion = request.state_version,
                Time = Time.time
            };

            if (response == null || !response.ok)
            {
                record.RejectReason = "appel Meta échoué" + (response != null ? " : " + response.error : "");
                record.Mode = Mode;
                Record(record);
                lastValidation = record.RejectReason;
                // Meta n'a pas répondu (timeout, proxy muet, erreur réseau) : on
                // redemande SANS LIMITE, avec un court délai croissant (2 -> 10 s).
                // Ce n'est pas un refus de Muse : ces essais ne comptent pas dans les
                // 4 échecs qui rétrogradent une urgente.
                metaFailStreak++;
                float delay = RetryDelay();
                string trig = request.trigger ?? "";
                bool urgentCall = trig.StartsWith("urgent") && urgentId >= 0 && !urgentPickDone;
                if (urgentCall)
                {
                    record.OrderId = urgentId;
                    UrgentStatus = "URGENTE #" + urgentId + " : Meta sans réponse (essai " + metaFailStreak +
                                   ") — nouvelle demande dans " + delay.ToString("F0") + " s";
                }
                Incident("Meta sans réponse (" + trig + ", essai " + metaFailStreak + ") : nouvelle demande dans " +
                         delay.ToString("F0") + " s");
                nextAllowedCall = Time.time + delay; // la boucle auto (pending_order) relance aussi
                if (urgentCall || (trig != "pending_order" && trig != "new_order"))
                    StartCoroutine(RetryCallAfter(trig, delay));
                return;
            }
            metaFailStreak = 0;

            record.Mode = response.mode;
            record.Model = response.model;
            record.LatencyMs = response.latency_ms;
            modelUsed = response.model;
            lastLatencyMs = response.latency_ms;
            if (!string.IsNullOrEmpty(response.note))
                Debug.Log("[Muse] note proxy : " + response.note);

            MusePlan plan = response.plan;
            if (plan == null)
            {
                record.RejectReason = "réponse sans plan (" + (response.error ?? "json invalide") + ")";
                Reject(record, "réponse sans plan");
                return;
            }
            record.Action = plan.action;
            record.Reason = plan.reason;
            record.OrderId = plan.order_id;
            record.PlanSummary = Describe(plan);

            string action = (plan.action ?? "").Trim().ToLowerInvariant();
            if (action == "wait" || action == "no_plan")
            {
                record.Accepted = true;
                record.AppliedResult = "attente : " + plan.reason;
                Record(record);
                lastValidation = "accepté (" + plan.action + ")";
                nextAllowedCall = Time.time + WaitRetryDelay;
                MaybeRetryUrgent(plan.order_id);
                return;
            }
            if (action == "defer_order")
            {
                WhOrderState target = PlanValidator.FindOrder(lastSnapshot, plan.order_id);
                if (target == null) { Reject(record, "defer sur commande inconnue"); return; }
                deferredUntil[plan.order_id] = Time.time + DeferredReactivateDelay;
                WhOrderState local = FindLocal(plan.order_id);
                if (local != null) local.status = "pending";
                record.Accepted = true;
                record.AppliedResult = "commande #" + plan.order_id + " différée";
                Record(record);
                lastValidation = "accepté (defer)";
                stateVersion++;
                MaybeRetryUrgent(plan.order_id);
                return;
            }

            // Validation sur un état FRAIS — pas celui envoyé il y a plusieurs secondes.
            WhOrderState planned = FindLocal(plan.order_id);
            var fresh = Snapshot("validate", planned != null && planned.urgent && plan.order_id == urgentId);
            string error = PlanValidator.FirstError(plan, fresh);
            if (error != null) { Reject(record, error); return; }
            error = CheckRobotRoles(plan);
            if (error != null) { Reject(record, error); return; }

            record.Accepted = true;
            record.AppliedResult = "plan accepté : " + record.PlanSummary;
            Record(record);
            lastValidation = "accepté";
            consecutiveRejects = 0;
            lastRejection = null;
            StartExecution(plan);
        }

        // ------------------------------------------------------------------ commandes urgentes

        /// <summary>
        /// Point d'entrée des commandes urgentes vocales : la transcription FINALE
        /// (jamais un partial) est envoyée à Muse Wonder qui décide s'il s'agit d'une
        /// vraie commande urgente, en extrait les quantités et produit le plan.
        /// </summary>
        public void SubmitUrgentTranscript(string transcript)
        {
            transcript = (transcript ?? "").Trim();
            if (transcript.Length == 0) return;
            if (urgentId >= 0)
            {
                UrgentStatus = "urgente #" + urgentId + " déjà en cours — nouvelle urgente refusée";
                return;
            }
            if (Mode == "offline")
            {
                UrgentStatus = "mode offline : aucune décision Meta disponible";
                return;
            }
            urgentVoiceGen++; // invalide un retry éventuellement armé par une ancienne prise
            BeginUrgentStage(UrgentStage.Planning);
            StartCoroutine(UrgentVoiceFlow(transcript));
        }

        /// <summary>Bouton manuel d'urgence : même pipeline sans reconnaissance vocale.</summary>
        public int SubmitUrgentCounts(int a, int b, int c)
        {
            if (a + b + c <= 0) return 0;
            if (urgentId >= 0)
            {
                UrgentStatus = "urgente #" + urgentId + " déjà en cours — nouvelle urgente refusée";
                return 0;
            }
            urgentVoiceGen++; // invalide un retry vocal armé par une ancienne prise
            // Planning : la suite (suspension directe ou attente de capacité) est décidée par UrgentAdmission.
            BeginUrgentStage(UrgentStage.Planning);
            WhOrderState order = CreateOrder(a, b, c, true);
            UrgentStatus = "URGENTE #" + order.id + " : " + OrderText.Format(new[] { a, b, c }) +
                           " — suspension des commandes normales";
            Incident("commande urgente manuelle #" + order.id + " : suspension en cours");
            StartCoroutine(UrgentAdmission(order, null));
            return order.id;
        }

        /// <summary>Prépare un nouveau flux urgent : stage, sorties et marqueurs remis à zéro.</summary>
        private void BeginUrgentStage(UrgentStage stage)
        {
            Stage = stage;
            UrgentError = "";
            LastUrgentOutcome = "";
            urgentDelivered = false;
            urgentWaiting = false;
            urgentVoiceRetries = 0;
            urgentPickDone = false;
            resumedIds.Clear();
        }

        /// <summary>
        /// La préemption libère les palettes des commandes normales (ParkSuspended ->
        /// ReleasePallets) : pour la décision urgente, elles sont donc disponibles.
        /// Bancs et camions restent réservés (ils portent des cartons préparés/chargés).
        /// Le stock affiché exclut déjà les cartons pris par une commande (orderId).
        /// Le plan reste revalidé sur l'état frais APRÈS suspension.
        /// </summary>
        private void ExposePreemptablePallets(WarehouseSnapshot snap)
        {
            if (snap == null || snap.pallets == null) return;
            foreach (WhPallet p in snap.pallets)
            {
                if (p == null || string.IsNullOrEmpty(p.reserved_by)) continue;
                if (!int.TryParse(p.reserved_by, out int owner)) continue;
                WhOrderState o = FindLocal(owner);
                if (o != null && !o.urgent && activeOrderIds.Contains(owner))
                    p.reserved_by = null;
            }
        }

        /// <summary>Muse a dit "wait" : attendre 5 s puis redemander avec l'état frais.
        /// Le garde urgentWaiting bloque les décisions normales pendant l'attente ;
        /// le jeton gen invalide le retry si une nouvelle prise de parole est arrivée.</summary>
        private IEnumerator RetryUrgentVoice(string transcript, int gen, float delay = 5f)
        {
            urgentWaiting = true;
            yield return new WaitForSeconds(delay);
            urgentWaiting = false;
            if (gen == urgentVoiceGen && urgentId < 0)
                StartCoroutine(UrgentVoiceFlow(transcript));
        }

        /// <summary>Suspend les normales puis demande un plan à Muse (urgence manuelle).</summary>
        private IEnumerator SuspendThenPlan(WhOrderState order)
        {
            yield return SuspendActiveOrders();
            yield return RequestDecisionWhenFree("urgent_order");
        }

        /// <summary>Flux voix complet : /urgent -> suspension -> validation fraîche -> exécution.</summary>
        private IEnumerator UrgentVoiceFlow(string transcript)
        {
            float deadline = Time.time + PendingTimeout + 10f;
            while (pending && Time.time < deadline) yield return null; // un appel /warehouse peut être en vol
            if (pending)
            {
                UrgentStatus = "appel Meta précédent sans réponse : urgente reportée";
                yield break;
            }

            urgentPending = true;
            stateVersion++;
            int assignedId = nextOrderId; // id que la commande recevra si elle est acceptée
            var snap = Snapshot("urgent_voice_order", true);
            ExposePreemptablePallets(snap);
            var request = new UrgentRequest
            {
                request_id = "urg-" + (++requestSeq),
                state_version = stateVersion,
                mode = Mode,
                transcript = transcript,
                urgent_order_id = assignedId,
                state_json = JsonUtility.ToJson(snap)
            };
            UrgentStatus = "interprétation Muse Wonder… (" + Mode + ")";
            UrgentResponse response = null;
            yield return proxy.AskUrgent(request, r => response = r);
            urgentPending = false;
            nextAllowedCall = Time.time + CallCooldown;

            var record = new MuseDecisionRecord
            {
                RequestId = request.request_id,
                Trigger = "urgent_voice_order",
                StateVersion = stateVersion,
                Time = Time.time
            };
            if (response == null || !response.ok || response.result == null)
            {
                record.Mode = Mode;
                record.RejectReason = "appel urgent échoué" +
                                      (response != null ? " : " + response.error : "");
                Record(record);
                // Meta n'a pas répondu : la même transcription est renvoyée SANS LIMITE
                // (une nouvelle prise de parole invalide ces essais via urgentVoiceGen).
                metaFailStreak++;
                float delay = RetryDelay();
                UrgentStatus = "Meta sans réponse (essai " + metaFailStreak + ") — nouvelle interprétation dans " +
                               delay.ToString("F0") + " s";
                Stage = UrgentStage.Planning;
                Incident(record.RejectReason + " : nouvel essai dans " + delay.ToString("F0") + " s");
                StartCoroutine(RetryUrgentVoice(transcript, urgentVoiceGen, delay));
                yield break;
            }
            metaFailStreak = 0;

            record.Mode = response.mode;
            record.Model = response.model;
            record.LatencyMs = response.latency_ms;
            modelUsed = response.model;
            lastLatencyMs = response.latency_ms;
            UrgentResult result = response.result;
            record.Action = "urgent:" + result.action;
            record.Reason = result.reason;

            // ---- jamais de mission sur une transcription incertaine ou non urgente
            if (result.action == "clarify")
            {
                record.Accepted = true;
                record.AppliedResult = "clarification : " + result.clarification;
                Record(record);
                UrgentStatus = "À CONFIRMER : " +
                    (result.clarification.Length > 0 ? result.clarification : result.reason);
                Stage = UrgentStage.Idle; // retour au repos : reformuler et reparler
                yield break;
            }
            // Muse a compris l'urgence mais attend des ressources : retenter avec
            // l'état frais au lieu d'échouer (spec : attendre la libération sûre).
            if (result.urgent &&
                (result.action == "wait" || result.action == "no_plan" ||
                 result.action == "defer_order"))
            {
                record.Accepted = true;
                record.AppliedResult = result.action + " : " + result.reason;
                Record(record);
                // Urgence comprise (quantités sûres) mais aucun banc + camion libre :
                // FINISH TO FREE — l'urgente est créée et libère sa capacité en faisant
                // finir une commande normale, au lieu d'attendre passivement.
                if (result.order != null && result.order.Total > 0 && result.confidence >= MinUrgentConfidence &&
                    !HasUrgentCapacity(result.order.Total, out _, out _))
                {
                    WhOrderState waitingUrgent = CreateOrder(result.order.A, result.order.B, result.order.C, true);
                    record.OrderId = waitingUrgent.id;
                    UrgentStatus = "URGENTE #" + waitingUrgent.id + " : " +
                                   OrderText.Format(new[] { result.order.A, result.order.B, result.order.C }) +
                                   " — tous les quais occupés";
                    Incident("commande urgente #" + waitingUrgent.id + " validée : capacité à libérer (FINISH TO FREE)");
                    yield return UrgentAdmission(waitingUrgent, null);
                    yield break;
                }
                urgentVoiceRetries++;
                if (urgentVoiceRetries <= MaxUrgentVoiceRetries)
                {
                    UrgentStatus = "Muse attend des ressources (" + result.reason +
                                   ") — nouvelle tentative " + urgentVoiceRetries +
                                   "/" + MaxUrgentVoiceRetries + " dans 5 s";
                    Stage = UrgentStage.Planning;
                    StartCoroutine(RetryUrgentVoice(transcript, urgentVoiceGen));
                }
                else
                {
                    UrgentStatus = "urgente impossible après " + MaxUrgentVoiceRetries +
                                   " tentatives : " + result.reason;
                    UrgentError = UrgentStatus;
                    LastUrgentOutcome = UrgentError;
                    // Des commandes ont pu se garer pendant l'attente : les reprendre.
                    ResumePaused();
                    Stage = resumedIds.Count > 0 ? UrgentStage.Resuming : UrgentStage.Failed;
                }
                yield break;
            }
            if (!result.urgent || result.action != "execute_order" || result.plan == null ||
                result.order == null || result.order.Total <= 0)
            {
                record.Accepted = true;
                record.AppliedResult = result.action + " : " + result.reason;
                Record(record);
                UrgentStatus = !result.urgent
                    ? "pas une commande urgente (" + result.reason + ")"
                    : "Muse ne peut pas exécuter l'urgente : " + result.reason;
                if (!result.urgent)
                {
                    Stage = UrgentStage.Idle; // simple phrase normale : pas d'incident
                }
                else
                {
                    UrgentError = UrgentStatus;
                    LastUrgentOutcome = UrgentStatus;
                    Stage = UrgentStage.Failed;
                }
                yield break;
            }
            if (result.confidence < MinUrgentConfidence)
            {
                record.RejectReason = "confiance transcription " + result.confidence.ToString("F2") +
                                      " < " + MinUrgentConfidence;
                Reject(record, record.RejectReason);
                UrgentStatus = "transcription incertaine — reformulez la commande urgente";
                UrgentError = record.RejectReason;
                LastUrgentOutcome = record.RejectReason;
                Stage = UrgentStage.Failed;
                yield break;
            }

            // ---- commande urgente réelle : création + suspension des normales
            WhOrderState order = CreateOrder(result.order.A, result.order.B, result.order.C, true);
            result.plan.order_id = order.id;
            record.OrderId = order.id;
            record.PlanSummary = Describe(result.plan);
            record.Accepted = true;
            record.AppliedResult = "urgente acceptée : " + record.PlanSummary;
            Record(record);
            UrgentStatus = "URGENTE #" + order.id + " : " +
                           OrderText.Format(new[] { result.order.A, result.order.B, result.order.C }) +
                           " (confiance " + result.confidence.ToString("F2") + ") — suspension…";
            Incident("commande urgente #" + order.id + " validée : suspension des commandes normales");
            yield return UrgentAdmission(order, result.plan);
        }

        /// <summary>Plan urgent (vocal) revalidé sur l'état FRAIS post-suspension, sinon replanifié.</summary>
        private IEnumerator ContinueUrgent(WhOrderState order, MusePlan plan)
        {
            if (urgentId != order.id) yield break;
            if (plan == null)
            {
                UrgentStatus = "URGENTE #" + order.id + " : planification Muse";
                yield return RequestDecisionWhenFree("urgent_order");
                yield break;
            }
            plan.order_id = order.id;
            var fresh = Snapshot("urgent_validate", true);
            string error = PlanValidator.FirstError(plan, fresh) ?? CheckRobotRoles(plan);
            if (error != null)
            {
                Incident("plan urgent refusé après suspension (" + error + ") : replanification");
                urgentReplanFails++;
                if (urgentReplanFails >= 4)
                {
                    DemoteUrgent("non planifiable après suspension (" + error + ")");
                    yield break;
                }
                UrgentStatus = "URGENTE #" + order.id + " : plan refusé (" + error + "), nouvel essai";
                yield return RequestDecisionWhenFree("urgent_replan");
                yield break;
            }
            UrgentStatus = "URGENTE #" + order.id + " : exécution (" + Describe(plan) + ")";
            StartExecution(plan);
        }

        /// <summary>État réel de l'entrepôt + règle de voie (banc/camion urgents).</summary>
        private WarehouseSnapshot Snapshot(string trigger, bool urgent)
        {
            WarehouseSnapshot snap = World().BuildSnapshot(orders, stateVersion, trigger, Mode, incidents, lastRejection);
            ApplyLanes(snap, urgent);
            // FINISH TO FREE : la décision « finish_for_urgent » ne concerne QUE la commande à
            // finir ; les autres commandes en attente sont présentées retenues (copies — les
            // commandes réelles ne sont jamais modifiées par cet affichage).
            if (trigger == "finish_for_urgent" && finishingId >= 0 && snap.orders != null)
                for (int i = 0; i < snap.orders.Length; i++)
                {
                    WhOrderState o = snap.orders[i];
                    if (o == null || o.id == finishingId || o.status != "pending") continue;
                    WhOrderState copy = JsonUtility.FromJson<WhOrderState>(JsonUtility.ToJson(o));
                    copy.status = "held_for_urgent";
                    snap.orders[i] = copy;
                }
            return snap;
        }

        /// <summary>
        /// Voie urgente : pour une décision normale, le banc et le camion urgents apparaissent
        /// réservés ("urgent_lane") ; pour l'urgente, ce sont les autres ("normal_lane"). Muse
        /// n'utilise jamais une ressource réservée et le validateur refuse tout plan qui le
        /// ferait — la règle est donc respectée des deux côtés. Une ressource déjà réservée
        /// par une vraie commande garde sa réservation réelle.
        /// </summary>
        private void ApplyLanes(WarehouseSnapshot snap, bool urgent)
        {
            if (!ReserveUrgentLane || snap == null) return;
            string tag = urgent ? "normal_lane" : "urgent_lane";
            if (snap.benches != null)
                foreach (WhBench b in snap.benches)
                    if (b != null && string.IsNullOrEmpty(b.reserved_by) && (b.id == UrgentBenchId) != urgent)
                        b.reserved_by = tag;
            if (snap.trucks != null)
                foreach (WhTruck t in snap.trucks)
                    if (t != null && string.IsNullOrEmpty(t.reserved_by) && (t.id == UrgentTruckId) != urgent)
                        t.reserved_by = tag;
            var notes = new List<string>(snap.incidents ?? new string[0]);
            notes.Add("règle : banc " + UrgentBenchId + " et camion " + UrgentTruckId +
                      " réservés aux commandes urgentes ; les commandes normales utilisent les autres");
            snap.incidents = notes.ToArray();
        }

        /// <summary>Appels Meta perdus d'affilée (remis à zéro dès qu'une réponse arrive).</summary>
        private int metaFailStreak;

        /// <summary>Délai avant de redemander après un appel perdu : 4, 6, 8 puis 10 s.</summary>
        private float RetryDelay() { return Mathf.Min(10f, 2f + metaFailStreak * 2f); }

        /// <summary>
        /// Nouvel essai d'un appel /warehouse perdu, tant qu'il a encore un objet :
        /// l'urgente est toujours en attente de son plan, ou une commande attend une décision.
        /// </summary>
        private IEnumerator RetryCallAfter(string trigger, float delay)
        {
            yield return new WaitForSeconds(delay);
            if (trigger.StartsWith("urgent"))
            {
                if (urgentId < 0 || urgentPickDone) yield break; // urgente livrée, rétrogradée ou déjà planifiée
                if (activeOrderIds.Contains(urgentId)) yield break; // plan reçu entre-temps
            }
            else if (NextPending() == null && trigger != "stock_shortage") yield break;
            yield return RequestDecisionWhenFree(trigger);
        }

        // ================================================================== FINISH TO FREE

        /// <summary>Commande normale que Muse a choisi de finir pour libérer la capacité (-1 = aucune).</summary>
        private int finishingId = -1;
        public int FinishingOrderId => finishingId;
        /// <summary>Banc / camion que la finalisation doit libérer (affichage HUD).</summary>
        public string FreeingBenchId { get; private set; } = "";
        public string FreeingTruckId { get; private set; } = "";
        /// <summary>Motif visible de l'attente : quais occupés, banc libre sans camion vide, etc.</summary>
        public string CapacityReason { get; private set; } = "";
        /// <summary>Stratégie en cours (texte HUD) : "Finish Order #1", "selecting…", motif d'attente.</summary>
        public string CapacityStrategy { get; private set; } = "";
        /// <summary>Note secondaire (ex. stratégie Muse indisponible) — jamais présentée comme un refus.</summary>
        public string CapacityNote { get; private set; } = "";
        /// <summary>Estimation de libération (s) annoncée pour la commande à finir (-1 inconnue).</summary>
        public float CapacityEtaS { get; private set; } = -1f;
        /// <summary>L'urgente courante est passée par l'attente de capacité (bandeau « Resources available »).</summary>
        public bool CapacityEpisode { get; private set; }
        /// <summary>Banc + camion réellement disponibles trouvés pour l'urgente (affichage).</summary>
        public string CapacityBenchId { get; private set; } = "";
        public string CapacityTruckId { get; private set; } = "";
        /// <summary>Couple banc + camion vide réellement disponible pour l'urgente (fin de l'attente).</summary>
        public bool CapacityReady => !string.IsNullOrEmpty(CapacityBenchId) && !string.IsNullOrEmpty(CapacityTruckId);

        [Header("Urgence : FINISH TO FREE")]
        [Tooltip("Sans progression de la commande à finir pendant ce délai (s), elle est abandonnée et Muse en choisit une autre.")]
        public float FinishWatchdog = 150f;
        [Tooltip("Délai (s) entre deux réévaluations quand aucune commande ne peut libérer de capacité (pas de boucle d'appels).")]
        public float CapacityRecheck = 15f;

        private MuseCapacityClient capacityClient;
        private readonly HashSet<int> finishExcluded = new HashSet<int>();

        private void ClearCapacityState()
        {
            finishingId = -1;
            FreeingBenchId = "";
            FreeingTruckId = "";
            CapacityReason = "";
            CapacityStrategy = "";
            CapacityNote = "";
            CapacityEtaS = -1f;
            CapacityBenchId = "";
            CapacityTruckId = "";
            finishExcluded.Clear();
        }

        /// <summary>
        /// Couple banc + camion utilisable TOUT DE SUITE par l'urgente : banc non réservé avec
        /// des niveaux libres ; camion non réservé, physiquement VIDE (aucun carton d'une autre
        /// commande, jamais une remorque encore chargée), assez de places, porte au repos.
        /// </summary>
        private bool HasUrgentCapacity(int need, out string bench, out string truck)
        {
            bench = null;
            truck = null;
            if (World() == null) return false;
            foreach (string id in World().BenchIds)
            {
                if (ReserveUrgentLane && id != UrgentBenchId) continue;
                if (World().BenchReservedBy(id) < 0 && World().BenchFreeLevels(id) > 0) { bench = id; break; }
            }
            foreach (string id in World().TruckIds)
            {
                if (ReserveUrgentLane && id != UrgentTruckId) continue;
                TruckBay bay = World().Truck(id);
                if (bay == null || bay.ReservedOrderId >= 0 || bay.LoadedCount > 0 || bay.FreeSlots < need) continue;
                if (bay.Door != TruckBay.TruckDoorState.Closed && bay.Door != TruckBay.TruckDoorState.Loaded) continue;
                truck = id;
                break;
            }
            return bench != null && truck != null;
        }

        /// <summary>
        /// Une commande active n'a encore rien engagé (aucun carton préparé ni chargé, aucun robot
        /// en mission pour elle) : sa mise en pause rend immédiatement son banc ET son camion.
        /// </summary>
        private bool PausingFreesCapacity()
        {
            foreach (WhOrderState o in orders)
            {
                if (o == null || o.urgent || !activeOrderIds.Contains(o.id)) continue;
                if (string.IsNullOrEmpty(o.bench_id) || string.IsNullOrEmpty(o.truck_id)) continue;
                if (StagedTotal(o) > 0 || o.loaded_a + o.loaded_b + o.loaded_c > 0) continue;
                if (Robot1 != null && Robot1.MissionActive && Robot1.LastReport != null && Robot1.LastReport.OrderId == o.id) continue;
                if (Robot2 != null && Robot2.MissionActive && Robot2.DirectedOrderId == o.id) continue;
                TruckBay bay = World().Truck(o.truck_id);
                if (bay != null && bay.LoadedCount > 0) continue;
                return true;
            }
            return false;
        }

        private int ActiveNormalCount()
        {
            int n = 0;
            foreach (int id in activeOrderIds)
            {
                WhOrderState o = FindLocal(id);
                if (o != null && !o.urgent) n++;
            }
            return n;
        }

        /// <summary>
        /// Entrée de toute urgente acceptée (bouton ou voix). Les nouvelles décisions normales sont
        /// déjà bloquées (urgentId). Capacité disponible (ou libérable en mettant en pause une commande
        /// qui n'a rien engagé) -> suspension sûre puis exécution immédiate. Sinon FINISH TO FREE.
        /// </summary>
        private IEnumerator UrgentAdmission(WhOrderState order, MusePlan voicePlan)
        {
            CapacityEpisode = false;
            ClearCapacityState();
            int need = order.a + order.b + order.c;
            bool capacity = HasUrgentCapacity(need, out string b0, out string t0);
            if (!capacity && PausingFreesCapacity())
            {
                MuseTrace.Log("URGENT", "urgente #" + order.id + " : une commande sans carton engagé libère un quai à la pause");
                yield return SuspendActiveOrders();
                capacity = HasUrgentCapacity(need, out b0, out t0);
            }
            if (capacity)
            {
                if (ActiveNormalCount() > 0) yield return SuspendActiveOrders();
                CapacityBenchId = b0;
                CapacityTruckId = t0;
                yield return ContinueUrgent(order, voicePlan);
                yield break;
            }
            yield return FinishToFree(order);
            if (urgentId != order.id) yield break;
            // filet : plus aucune commande normale ne tourne pendant l'urgente
            if (ActiveNormalCount() > 0) yield return SuspendActiveOrders();
            // le plan vocal éventuel visait l'ancien état : replanification sur la capacité libérée
            yield return ContinueUrgent(order, null);
        }

        /// <summary>
        /// URGENT_WAITING_FOR_CAPACITY : Muse choisit LA commande normale dont la finalisation libère
        /// le plus vite un banc ET un camion vide ; les autres sont suspendues (opérations indivisibles
        /// respectées) ; la choisie est finie en priorité (vérifiée, fermée, expédiée). On attend la
        /// disponibilité RÉELLE du couple, puis on rend la main à la planification urgente.
        /// Aucun carton n'est déplacé ni supprimé ; aucune ressource n'est réservée deux fois.
        /// </summary>
        private IEnumerator FinishToFree(WhOrderState order)
        {
            int need = order.a + order.b + order.c;
            Stage = UrgentStage.WaitingForCapacity;
            CapacityEpisode = true;
            UrgentStatus = "URGENTE #" + order.id + " : en attente de capacité (tous les quais occupés)";
            Incident("URGENTE #" + order.id + " : tous les bancs/quais occupés — FINISH TO FREE");
            MuseTrace.Log("URGENT", "WAITING_FOR_CAPACITY urgente #" + order.id + " (" + need + " carton(s))");
            float progressAt = Time.time;
            int lastProgress = -1;
            int invalidChoices = 0;
            float excludedSince = -1f;

            while (urgentId == order.id)
            {
                if (HasUrgentCapacity(need, out string bench, out string truck))
                {
                    CapacityBenchId = bench;
                    CapacityTruckId = truck;
                    CapacityReason = "Resources available: bench " + bench + " / dock " + truck;
                    CapacityStrategy = finishingId >= 0 ? "Finish Order #" + finishingId + " — done" : CapacityStrategy;
                    Incident("URGENTE #" + order.id + " : capacité libérée — banc " + bench + " / camion vide " + truck);
                    MuseTrace.Log("URGENT", "capacité disponible pour #" + order.id + " : " + bench + " / " + truck);
                    finishingId = -1;
                    yield break;
                }
                CapacityReason = MissingReason(need);

                // ---- une commande est en cours de finalisation : la surveiller
                if (finishingId >= 0)
                {
                    WhOrderState f = FindLocal(finishingId);
                    if (f != null && f.status == "waiting" && !activeOrderIds.Contains(f.id))
                    {
                        // terminée mais contenu inexact : son camion garde ses cartons (jamais
                        // réattribué) ; on l'écarte et Muse choisit ce qui manque encore.
                        Incident("commande #" + f.id + " finie incomplète : camion conservé, nouvelle stratégie");
                        finishExcluded.Add(f.id);
                        if (excludedSince < 0f) excludedSince = Time.time;
                        finishingId = -1;
                        continue;
                    }
                    if (f == null || f.status == "loaded")
                    {
                        Incident("commande #" + finishingId + " terminée pour l'urgente : ressources rendues");
                        MuseTrace.Log("URGENT", "FINISHING_FOR_URGENT #" + finishingId + " terminée");
                        finishingId = -1;
                        progressAt = Time.time;
                        // Le couple n'est pas encore là (porte en mouvement, camion pas vide) :
                        // la boucle le revérifie ; si un seul des deux manque, Muse rechoisira.
                        yield return new WaitForSeconds(.5f);
                        continue;
                    }
                    int progress = StagedTotal(f) + f.loaded_a + f.loaded_b + f.loaded_c + f.status.GetHashCode() % 97;
                    if (progress != lastProgress) { lastProgress = progress; progressAt = Time.time; }
                    if (Time.time - progressAt > FinishWatchdog)
                    {
                        Incident("commande #" + f.id + " sans progression depuis " + FinishWatchdog.ToString("F0") +
                                 " s : abandonnée comme commande à finir, mise en pause");
                        finishExcluded.Add(f.id);
                        if (excludedSince < 0f) excludedSince = Time.time;
                        finishingId = -1;
                        yield return SuspendActiveOrders();
                        Stage = UrgentStage.WaitingForCapacity;
                        continue;
                    }
                    yield return new WaitForSeconds(.5f);
                    continue;
                }

                // ---- choisir la commande à finir (décision Muse)
                if (excludedSince > 0f && Time.time - excludedSince > 120f) { finishExcluded.Clear(); excludedSince = -1f; }
                FtfContext ctx = BuildCapacityContext(order, need);
                int usable = 0;
                foreach (FtfCandidate c in ctx.candidates) if (c.can_finish && Covers(c, ctx)) usable++;
                if (usable == 0)
                {
                    CapacityStrategy = "Waiting for the first free bench + empty dock";
                    CapacityNote = "no normal order can be finished to free a bay";
                    UrgentStatus = "URGENTE #" + order.id + " : en attente prioritaire — aucune commande normale ne peut libérer de quai";
                    MuseTrace.Log("URGENT", "aucune candidate FINISH TO FREE (" + ctx.candidates.Length + " commande(s) examinée(s))");
                    yield return WaitOrCapacity(CapacityRecheck, need); // réévaluation espacée, sans appel Meta en boucle
                    continue;
                }

                CapacityStrategy = "Muse selecting the order to finish…";
                if (metaFailStreak == 0) CapacityNote = "";
                CapacityResponse r = null;
                yield return AskCapacity(order, ctx, x => r = x);
                if (urgentId != order.id) yield break;
                var record = new MuseDecisionRecord
                {
                    RequestId = "cap-" + requestSeq,
                    Trigger = "urgent_capacity",
                    StateVersion = stateVersion,
                    Time = Time.time,
                    Mode = r != null ? r.mode : Mode,
                    Model = r != null ? r.model : "",
                    LatencyMs = r != null ? r.latency_ms : 0
                };
                if (r == null || !r.ok || r.result == null)
                {
                    // Pas de stratégie Muse pour l'instant : l'urgente ATTEND simplement la première
                    // place libre (les commandes normales continuent et libèrent leurs quais). Ce
                    // n'est ni un refus ni une décision : rien n'est inscrit dans l'historique des
                    // décisions, le motif reste visible sur la carte et dans les incidents.
                    string err = r != null && !string.IsNullOrEmpty(r.error) ? r.error : "pas de réponse";
                    bool noRoute = err.Contains("not found");
                    metaFailStreak++;
                    float delay = noRoute ? 30f : RetryDelay();
                    CapacityStrategy = "Waiting for the first free bench + empty dock";
                    CapacityNote = noRoute
                        ? "Muse strategy unavailable: proxy without /capacity — restart meta_proxy.py"
                        : "Muse strategy unavailable (" + err + ") — new request in " + delay.ToString("F0") + " s";
                    UrgentStatus = "URGENTE #" + order.id + " : en attente d'une place (banc + camion vide)";
                    if (!noRoute || metaFailStreak == 1)
                        Incident("URGENTE #" + order.id + " en attente d'une place — stratégie Muse indisponible (" + err + ")");
                    yield return WaitOrCapacity(delay, need);
                    continue;
                }
                CapacityNote = "";
                metaFailStreak = 0;
                modelUsed = r.model;
                lastLatencyMs = r.latency_ms;
                // Entre-temps une commande a pu finir d'elle-même : la capacité est là, la
                // stratégie n'a plus d'objet (aucune suspension inutile).
                if (HasUrgentCapacity(need, out _, out _))
                {
                    record.Action = r.result.action;
                    record.Reason = r.result.reason;
                    record.Accepted = true;
                    record.AppliedResult = "capacité libérée avant application de la stratégie";
                    Record(record);
                    continue;
                }
                record.Action = r.result.action;
                record.Reason = r.result.reason;
                record.OrderId = r.result.order_id;
                string action = (r.result.action ?? "").Trim().ToLowerInvariant();
                if (action != "finish_order")
                {
                    record.Accepted = true;
                    record.AppliedResult = "attente : " + r.result.reason;
                    Record(record);
                    CapacityStrategy = "Muse: wait — " + r.result.reason;
                    yield return WaitOrCapacity(CapacityRecheck, need);
                    continue;
                }
                FtfCandidate chosen = null;
                foreach (FtfCandidate c in ctx.candidates) if (c.order_id == r.result.order_id) chosen = c;
                string why = chosen == null ? "commande #" + r.result.order_id + " absente des candidates"
                           : !chosen.can_finish ? "commande #" + chosen.order_id + " ne peut pas finir (stock)"
                           : !Covers(chosen, ctx) ? "la finir ne libère pas le banc et le camion manquants"
                           : null;
                if (why != null)
                {
                    Reject(record, "choix FINISH TO FREE refusé : " + why);
                    if (++invalidChoices >= 3)
                    {
                        CapacityStrategy = "Muse choices rejected — re-evaluating in " + CapacityRecheck.ToString("F0") + " s";
                        invalidChoices = 0;
                        yield return WaitOrCapacity(CapacityRecheck, need);
                    }
                    else yield return new WaitForSeconds(2f);
                    continue;
                }
                invalidChoices = 0;
                record.Accepted = true;
                record.PlanSummary = ""; // pas un plan de collecte : le HUD l'affiche « FINISH #n »
                record.AppliedResult = "FINISH TO FREE : finir #" + chosen.order_id + " (libère " +
                                       chosen.bench_id + " / " + chosen.truck_id + ")";
                Record(record);

                finishingId = chosen.order_id;
                FreeingBenchId = chosen.bench_id ?? "";
                FreeingTruckId = chosen.truck_id ?? "";
                CapacityEtaS = r.result.expected_free_s > 0f ? r.result.expected_free_s : chosen.eta_free_s;
                CapacityStrategy = "Finish Order #" + finishingId;
                UrgentStatus = "URGENTE #" + order.id + " : Muse fait finir #" + finishingId + " (libère " +
                               FreeingBenchId + " / " + FreeingTruckId + ")";
                Incident("Muse — FINISH TO FREE : finir #" + finishingId + " pour libérer " + FreeingBenchId + " / " +
                         FreeingTruckId + " (~" + CapacityEtaS.ToString("F0") + " s) — urgente #" + order.id + " en attente");
                MuseTrace.Log("URGENT", "FINISHING_FOR_URGENT #" + finishingId + " choisie par Muse : " + r.result.reason);

                // Les autres commandes normales sont suspendues ; la choisie continue.
                yield return SuspendActiveOrders(finishingId);
                Stage = UrgentStage.WaitingForCapacity;
                WhOrderState sel = FindLocal(finishingId);
                if (sel != null && pausedIds.Contains(sel.id)) ResumeForFinish(sel);
                progressAt = Time.time;
                lastProgress = -1;
            }
        }

        /// <summary>Attente qui s'interrompt dès qu'un banc + camion vide se libère.</summary>
        private IEnumerator WaitOrCapacity(float seconds, int need)
        {
            float until = Time.time + seconds;
            while (Time.time < until && !HasUrgentCapacity(need, out _, out _)) yield return new WaitForSeconds(.5f);
        }

        /// <summary>Ce qui manque à l'urgente, en clair pour le HUD.</summary>
        private string MissingReason(int need)
        {
            bool bench = false, truck = false;
            string benchId = "", truckId = "";
            foreach (string id in World().BenchIds)
                if (World().BenchReservedBy(id) < 0 && World().BenchFreeLevels(id) > 0) { bench = true; benchId = id; break; }
            foreach (string id in World().TruckIds)
            {
                TruckBay bay = World().Truck(id);
                if (bay != null && bay.ReservedOrderId < 0 && bay.LoadedCount == 0 && bay.FreeSlots >= need) { truck = true; truckId = id; break; }
            }
            if (!bench && !truck) return "All loading bays occupied";
            if (bench) return "Bench " + benchId + " free — no empty truck";
            return "Truck " + truckId + " empty — no free bench";
        }

        /// <summary>La finalisation de c apporte ce qui manque (banc et/ou camion vide compatible).</summary>
        private static bool Covers(FtfCandidate c, FtfContext ctx)
        {
            bool needBench = ctx.free_benches == null || ctx.free_benches.Length == 0;
            bool needTruck = ctx.empty_trucks == null || ctx.empty_trucks.Length == 0;
            if (needBench && !c.frees_bench) return false;
            if (needTruck && (!c.frees_truck || c.truck_capacity < ctx.urgent_total)) return false;
            return true;
        }

        /// <summary>
        /// Données envoyées à Muse : progression réelle de chaque commande normale qui tient un banc
        /// et/ou un camion (active ou en pause), reste à collecter / charger, ressources rendues, et
        /// une ETA calculée sur les distances réelles de la scène (vitesse robot, temps de manutention).
        /// </summary>
        private FtfContext BuildCapacityContext(WhOrderState urgent, int need)
        {
            var ctx = new FtfContext
            {
                urgent_order_id = urgent.id,
                urgent_total = need,
                urgent_needs = OrderText.Format(new[] { urgent.a, urgent.b, urgent.c }),
                eta_method = "estimate: R1 cycle = 2 x pallet->bench / speed + 6 s, R2 cycle = 2 x bench->truck / speed + 8 s, " +
                             "close+ship 10 s, +12 s if R1/R2 must first finish another indivisible operation"
            };
            var freeB = new List<string>();
            var emptyT = new List<string>();
            foreach (string id in World().BenchIds)
                if (World().BenchReservedBy(id) < 0 && World().BenchFreeLevels(id) > 0) freeB.Add(id);
            foreach (string id in World().TruckIds)
            {
                TruckBay bay = World().Truck(id);
                if (bay != null && bay.ReservedOrderId < 0 && bay.LoadedCount == 0 && bay.FreeSlots >= need) emptyT.Add(id);
            }
            ctx.free_benches = freeB.ToArray();
            ctx.empty_trucks = emptyT.ToArray();
            ctx.missing = freeB.Count == 0 && emptyT.Count == 0 ? "bench+truck" : freeB.Count == 0 ? "bench" : "truck";

            var list = new List<FtfCandidate>();
            float speed = Mathf.Max(.1f, World().RobotSpeed);
            foreach (WhOrderState o in orders)
            {
                if (o == null || o.urgent || o.status == "loaded" || finishExcluded.Contains(o.id)) continue;
                bool active = activeOrderIds.Contains(o.id);
                bool paused = pausedIds.Contains(o.id);
                if (!active && !paused) continue;
                bool holdsBench = !string.IsNullOrEmpty(o.bench_id) && World().BenchReservedBy(o.bench_id) == o.id;
                TruckBay bay = !string.IsNullOrEmpty(o.truck_id) ? World().Truck(o.truck_id) : null;
                bool holdsTruck = bay != null && bay.ReservedOrderId == o.id;
                if (!holdsBench && !holdsTruck) continue;

                int[] inTruck = bay != null ? bay.CountByTypeForOrder(o.id) : new int[3];
                int loaded = inTruck[0] + inTruck[1] + inTruck[2];
                int staged = StagedTotal(o);
                int total = o.a + o.b + o.c;
                int remPick = Mathf.Max(0, total - loaded - staged);
                int remLoad = Mathf.Max(0, total - loaded);

                // stock libre suffisant pour finir la collecte (par type)
                bool canFinish = true;
                int[] remByType =
                {
                    Mathf.Max(0, o.a - inTruck[0] - o.staged_a),
                    Mathf.Max(0, o.b - inTruck[1] - o.staged_b),
                    Mathf.Max(0, o.c - inTruck[2] - o.staged_c)
                };
                string[] types = { "A", "B", "C" };
                for (int i = 0; i < 3; i++)
                    if (remByType[i] > 0 && World().FreeStock(types[i]) < remByType[i]) canFinish = false;

                // ETA sur distances réelles
                MusePlan plan = activePlans.TryGetValue(o.id, out MusePlan ap) ? ap
                              : pausedPlans.TryGetValue(o.id, out MusePlan pp) ? pp : null;
                Vector3 benchPos = Vector3.zero;
                bool hasBench = !string.IsNullOrEmpty(o.bench_id) && World().TryPositionOf(o.bench_id, out benchPos);
                float pickLeg = 6f;
                if (hasBench)
                {
                    float d = 0f;
                    int n = 0;
                    if (plan != null && plan.picks != null)
                        foreach (MusePlanPick p in plan.picks)
                            if (p != null && World().TryPositionOf(p.pallet_id, out Vector3 pal)) { d += Vector3.Distance(pal, benchPos); n++; }
                    if (n == 0)
                        foreach (string pid in World().PalletIds)
                            if (World().TryPositionOf(pid, out Vector3 pal)) { d += Vector3.Distance(pal, benchPos); n++; }
                    pickLeg = (n > 0 ? 2f * (d / n) / speed : 10f) + 6f;
                }
                float loadLeg = 8f + (hasBench && bay != null
                    ? 2f * Vector3.Distance(benchPos, bay.DockPoint != null ? bay.DockPoint.position : bay.transform.position) / speed
                    : 12f);
                bool r1Elsewhere = Robot1 != null && Robot1.MissionActive && (Robot1.LastReport == null || Robot1.LastReport.OrderId != o.id);
                bool r2Elsewhere = Robot2 != null && Robot2.MissionActive && Robot2.DirectedOrderId != o.id;
                float pickEnd = remPick > 0 ? remPick * pickLeg + (r1Elsewhere ? 12f : 0f) : 0f;
                float loadEnd = remLoad > 0 ? Mathf.Max(remLoad * loadLeg + (r2Elsewhere ? 12f : 0f), pickEnd + loadLeg) : 0f;
                float eta = Mathf.Max(pickEnd, loadEnd) + 10f + (paused ? 5f : 0f);

                list.Add(new FtfCandidate
                {
                    order_id = o.id,
                    status = paused ? "paused" : o.status,
                    paused = paused,
                    bench_id = o.bench_id ?? "",
                    truck_id = o.truck_id ?? "",
                    total = total,
                    staged = staged,
                    loaded = loaded,
                    remaining_pick = remPick,
                    remaining_load = remLoad,
                    eta_free_s = Mathf.Round(eta),
                    frees_bench = holdsBench,
                    frees_truck = holdsTruck,
                    truck_capacity = bay != null ? bay.Capacity : 0,
                    can_finish = canFinish,
                    note = canFinish ? "" : "free stock insufficient for the remaining picks"
                });
            }
            ctx.candidates = list.ToArray();
            return ctx;
        }

        private IEnumerator AskCapacity(WhOrderState urgent, FtfContext ctx, System.Action<CapacityResponse> done)
        {
            if (capacityClient == null) capacityClient = new MuseCapacityClient { BaseUrl = ProxyUrl };
            requestSeq++;
            stateVersion++;
            var request = new CapacityRequest
            {
                request_id = "cap-" + requestSeq,
                mode = Mode,
                capacity_json = JsonUtility.ToJson(ctx),
                state_json = JsonUtility.ToJson(Snapshot("urgent_capacity", false))
            };
            urgentPending = true; // aucun autre appel Meta pendant ce choix
            Calls++;
            Debug.Log("[Muse] demande " + request.request_id + " (urgent_capacity : quelle commande finir pour l'urgente #" +
                      urgent.id + ", " + ctx.candidates.Length + " candidate(s))");
            CapacityResponse response = null;
            yield return capacityClient.Ask(request, x => response = x);
            urgentPending = false;
            nextAllowedCall = Time.time + CallCooldown;
            done(response);
        }

        /// <summary>
        /// La commande choisie était en pause : reprise d'ELLE SEULE (expédition directe si complète,
        /// plan d'origine si encore valable, sinon décision Muse ciblée « finish_for_urgent »).
        /// </summary>
        private void ResumeForFinish(WhOrderState order)
        {
            pausedIds.Remove(order.id);
            MusePlan oldPlan = pausedPlans.TryGetValue(order.id, out MusePlan op) ? op : null;
            pausedPlans.Remove(order.id);
            UpdateStaged(order);
            TruckBay bay = !string.IsNullOrEmpty(order.truck_id) ? World().Truck(order.truck_id) : null;
            if (bay != null)
            {
                int[] inTruck = bay.CountByTypeForOrder(order.id);
                order.loaded_a = inTruck[0];
                order.loaded_b = inTruck[1];
                order.loaded_c = inTruck[2];
            }
            order.status = "pending";
            stateVersion++;
            if (FullyLoaded(order, out TruckBay fullBay))
            {
                Incident("FINISH TO FREE #" + order.id + " : déjà complète dans " + order.truck_id + " — expédition");
                running.Add(StartCoroutine(ShipCompleteOrder(order, fullBay)));
                return;
            }
            var snap = Snapshot("finish_for_urgent", false);
            MusePlan reuse = ResumePlanner.TryReuse(oldPlan, order, snap);
            if (reuse != null && (reuse.picks.Length == 0 ||
                                  (PlanValidator.FirstError(reuse, snap) == null && CheckRobotRoles(reuse) == null)))
            {
                Incident("FINISH TO FREE #" + order.id + " : reprise sur son plan d'origine");
                StartExecution(reuse);
                return;
            }
            Incident("FINISH TO FREE #" + order.id + " : plan d'origine irréalisable — décision Muse ciblée");
            StartCoroutine(RequestDecisionWhenFree("finish_for_urgent"));
        }

        /// <summary>Un appel de décision est envoyé dès qu'aucun appel Meta n'est en vol.</summary>
        private IEnumerator RequestDecisionWhenFree(string trigger)
        {
            float deadline = Time.time + PendingTimeout + 5f;
            while ((pending || urgentPending) && Time.time < deadline) yield return null;
            RequestDecision(trigger);
        }

        /// <summary>
        /// Préemption sécurisée : chaque commande normale active passe en SUSPENDING.
        /// R1 termine son opération indivisible (le carton en main rejoint le banc),
        /// puis R2 finit son chargement engagé et sort du quai. Les coroutines
        /// d'exécution se garent elles-mêmes via ParkSuspended (cartons, réservations
        /// et compteurs sauvegardés — rien n'est téléporté ni détruit).
        /// </summary>
        private IEnumerator SuspendActiveOrders(int keepId = -1)
        {
            suspendInProgress = true;
            Stage = UrgentStage.Suspending;
            foreach (WhOrderState o in orders)
            {
                if (o == null || o.urgent || !activeOrderIds.Contains(o.id) || o.status == "loaded")
                    continue;
                // FINISH TO FREE : la commande choisie par Muse continue jusqu'au bout.
                if (o.id == keepId) continue;
                // Tous ses cartons sont déjà dans le camion : elle n'a plus besoin des
                // robots, elle se ferme et part — la mettre en pause bloquait sa porte
                // (et la commande) jusqu'à la fin de l'urgente.
                if (FullyLoaded(o, out _)) continue;
                o.status = "suspending";
                suspendingIds.Add(o.id);
            }
            stateVersion++;
            if (suspendingIds.Count > 0)
            {
                Incident("SUSPENSION de " + suspendingIds.Count + " commande(s) : arrêt sûr des robots");
                // Les deux robots sont suspendus IMMEDIATEMENT : chacun finit son
                // opération indivisible en parallèle (R1 dépose au banc, R2 finit le
                // carton engagé puis sort du quai). N'attendre que R1 : l'urgente peut
                // démarrer sa collecte dès qu'il est à l'arrêt sûr. Le retour de R2
                // et la fermeture des portes normales se font en tâche de fond —
                // la mission urgente de R2 l'attendra naturellement (LoadWhenFree).
                // R1 qui collecte pour la commande gardée (FINISH TO FREE) n'est pas arrêté.
                bool r1Keep = Robot1 != null && Robot1.LastReport != null && Robot1.LastReport.OrderId == keepId;
                bool r1Suspended = Robot1 != null && Robot1.MissionActive && !r1Keep;
                if (r1Suspended) Robot1.SuspendMission();
                // R2 qui termine (sortie de remorque) une commande déjà complète n'est
                // pas suspendu : sa mission se clôt normalement et la commande part.
                if (Robot2 != null && Robot2.MissionActive && Robot2.DirectedOrderId != keepId)
                {
                    WhOrderState r2Order = FindLocal(Robot2.DirectedOrderId);
                    if (r2Order == null || !FullyLoaded(r2Order, out _)) Robot2.SuspendMission();
                }
                float deadline = Time.time + SuspendTimeout;
                while (r1Suspended && Robot1.MissionActive && Time.time < deadline) yield return null;
                if (r1Suspended && Robot1.MissionActive)
                {
                    Robot1.AbortMission();
                    Incident("R1 forcé à l'arrêt après " + SuspendTimeout.ToString("F0") + "s");
                    deadline = Time.time + 6f;
                    while (Robot1.MissionActive && Time.time < deadline) yield return null;
                }
                // Les Execute concernés se garent via ParkSuspended ; filet final.
                deadline = Time.time + 8f;
                while (suspendingIds.Count > 0 && Time.time < deadline) yield return null;
            }
            suspendInProgress = false;
        }

        /// <summary>
        /// Mise en pause d'UNE commande (appelée depuis sa coroutine Execute, ou en
        /// dernier recours par l'orchestrateur) : sauvegarde l'état réel — cartons
        /// préparés, contenu camion, réservations — puis libère les robots.
        /// </summary>
        private IEnumerator ParkSuspended(WhOrderState order, MusePlan plan,
                                          TruckBay bay, ShelfStorage bench, Coroutine loadCo)
        {
            // Ne JAMAIS tuer une mission R2 en cours : la coroutine contient le
            // volant physique du robot. On stoppe seulement la file d'attente
            // (mission pas encore démarrée) ; une mission live se termine sûrement.
            // La fermeture de la porte est DIFFÉRÉE (CloseNormalDockWhenSafe) :
            // attendre ici le retour de R2 retarderait le lancement de l'urgente —
            // la commande passe en pause tout de suite, la porte fermera dès que
            // R2 aura fini le carton engagé et quitté le quai.
            bool r2MissionLive = Robot2 != null && Robot2.MissionActive &&
                                 Robot2.DirectedOrderId == order.id;
            if (r2MissionLive)
            {
                Robot2.SuspendMission(); // finir le carton en main puis sortir du quai
                StartCoroutine(CloseNormalDockWhenSafe(order, bay));
            }
            else if (loadCo != null)
            {
                StopCoroutine(loadCo);
                running.Remove(loadCo);
                // File tuée entre r2Busy=true et le lancement : libérer le verrou,
                // et décrémenter la file signalée (sinon R2 ne rentrerait plus
                // jamais à la base — QueuedLoads ne se décrémente que si la
                // coroutine atteint la fin de son attente).
                if (r2BusyOwner == order.id) { r2Busy = false; r2BusyOwner = -1; }
                if (Robot2 != null && Robot2.QueuedLoads > 0) Robot2.QueuedLoads--;
            }
            UpdateStaged(order);
            if (bay != null)
            {
                int[] inTruck = bay.CountByTypeForOrder(order.id);
                order.loaded_a = inTruck[0];
                order.loaded_b = inTruck[1];
                order.loaded_c = inTruck[2];
                // La fermeture différée recalculera et gardera la réservation si
                // des cartons restent dedans ; si R2 n'avait rien engagé on peut
                // fermer tout de suite (r2MissionLive false => aucun carton en vol).
                if (!r2MissionLive)
                {
                    int loaded = inTruck[0] + inTruck[1] + inTruck[2];
                    bay.CloseDoor(false, keepReservation: loaded > 0);
                    if (loaded <= 0) order.truck_id = null;
                }
            }
            ReleasePallets(plan); // le stock redevient disponible pour l'urgente
            if (StagedTotal(order) <= 0 && !string.IsNullOrEmpty(order.bench_id))
            {
                World().ReleaseBench(order.bench_id, order.id);
                order.bench_id = null;
            }
            // EndExecution AVANT l'inscription en pause : il retire l'id de
            // pausedIds (nettoyage de fin de commande) — appelé après, il vidait
            // la liste et ResumePaused ne reprenait jamais rien.
            EndExecution(order);
            suspendingIds.Remove(order.id);
            pausedIds.Add(order.id);
            pausedPlans[order.id] = plan;
            order.status = "paused";
            MuseTrace.Log("SUSPEND", "commande #" + order.id + " inscrite en pause (paused=" +
                          pausedIds.Count + ")");
            stateVersion++;
            Incident("commande #" + order.id + " en pause : préparé " + StagedTotal(order) +
                     ", chargé " + (order.loaded_a + order.loaded_b + order.loaded_c));
            yield return null;
        }

        /// <summary>
        /// Fermeture différée du camion d'une commande suspendue pendant que R2
        /// finissait encore un chargement pour elle : attendre la fin sûre de cette
        /// mission (carton engagé terminé + sortie du quai), puis fermer la porte.
        /// Règle d'or : finir le chargement engagé, sortir R2 du quai, PUIS fermer.
        /// </summary>
        private IEnumerator CloseNormalDockWhenSafe(WhOrderState order, TruckBay bay)
        {
            if (bay == null) yield break;
            float deadline = Time.time + SuspendTimeout;
            while (Robot2 != null && Robot2.MissionActive &&
                   Robot2.DirectedOrderId == order.id && Time.time < deadline)
                yield return null;
            MuseTrace.Log("SUSPEND", "commande #" + order.id + " : mission R2 terminée=" +
                          !(Robot2 != null && Robot2.MissionActive &&
                            Robot2.DirectedOrderId == order.id) + " avant fermeture porte");
            // Entre-temps la commande a pu être reprise : son nouveau plan gère
            // lui-même la porte — ne pas lui fermer le camion sous le nez.
            if (order.status != "paused") yield break;
            // La baie a pu passer à l'urgente : ne pas lui voler sa réservation.
            if (bay.ReservedOrderId != order.id && bay.ReservedOrderId >= 0) yield break;
            int[] inTruck = bay.CountByTypeForOrder(order.id);
            order.loaded_a = inTruck[0];
            order.loaded_b = inTruck[1];
            order.loaded_c = inTruck[2];
            int loaded = inTruck[0] + inTruck[1] + inTruck[2];
            UpdateStaged(order); // le carton engagé a pu être remis au banc
            // Porte fermée AVANT de libérer le quai ; réservation conservée si des
            // cartons de la commande restent dedans (jamais déplacés).
            bay.CloseDoor(false, keepReservation: loaded > 0 && bay.ReservedOrderId == order.id);
            if (loaded <= 0 && !string.IsNullOrEmpty(order.truck_id) &&
                World() != null && World().Truck(order.truck_id) == bay)
                order.truck_id = null;
            stateVersion++;
        }

        /// <summary>
        /// Reprise exacte après livraison de l'urgente : revalidation des stocks et
        /// réservations sur l'état FRAIS, réutilisation du plan d'origine si encore
        /// réalisable, sinon nouvelle demande de plan à Muse (état repassé pending).
        /// </summary>
        private void ResumePaused()
        {
            if (pausedIds.Count == 0) return;
            // 1) Recollage à la vérité physique et repassage en planifiable AVANT
            //    de figer le snapshot (sinon le validateur verrait des commandes
            //    encore "paused" et refuserait tout plan de reprise).
            var resumable = new List<WhOrderState>();
            var oldPlans = new Dictionary<int, MusePlan>();
            foreach (int id in new List<int>(pausedIds))
            {
                WhOrderState order = FindLocal(id);
                pausedIds.Remove(id);
                if (pausedPlans.TryGetValue(id, out MusePlan oldPlan)) oldPlans[id] = oldPlan;
                pausedPlans.Remove(id);
                if (order == null) continue;
                UpdateStaged(order);
                TruckBay bay = !string.IsNullOrEmpty(order.truck_id) ? World().Truck(order.truck_id) : null;
                if (bay != null)
                {
                    int[] inTruck = bay.CountByTypeForOrder(order.id);
                    order.loaded_a = inTruck[0];
                    order.loaded_b = inTruck[1];
                    order.loaded_c = inTruck[2];
                }
                order.status = "pending"; // redevient planifiable (validateur + file)
                resumable.Add(order);
                resumedIds.Add(order.id); // suivi de la reprise pour le stage démo
            }
            // Priorité de reprise : la commande la plus AVANCÉE reprend d'abord
            // (cartons déjà préparés/chargés = travail engagé à préserver), puis
            // la plus ancienne à égalité. Sans ce tri, une commande jamais
            // commencée pouvait passer devant une commande à moitié chargée.
            resumable.Sort((x, y) =>
            {
                int px = x.staged_a + x.staged_b + x.staged_c +
                         x.loaded_a + x.loaded_b + x.loaded_c;
                int py = y.staged_a + y.staged_b + y.staged_c +
                         y.loaded_a + y.loaded_b + y.loaded_c;
                return px != py ? py - px : x.id - y.id;
            });
            stateVersion++;
            var snap = Snapshot("resume_after_urgent", false);
            foreach (WhOrderState order in resumable)
            {
                if (FullyLoaded(order, out TruckBay fullBay))
                {
                    Incident("reprise #" + order.id + " : tous les cartons sont déjà dans " + order.truck_id +
                             " — fermeture et expédition");
                    running.Add(StartCoroutine(ShipCompleteOrder(order, fullBay)));
                    continue;
                }
                MusePlan oldPlan = oldPlans.TryGetValue(order.id, out MusePlan op) ? op : null;
                MusePlan reuse = ResumePlanner.TryReuse(oldPlan, order, snap);
                if (reuse != null && reuse.picks.Length == 0)
                {
                    Incident("reprise #" + order.id + " : fin du chargement sans nouveau prélèvement");
                    StartExecution(reuse);
                }
                else if (reuse != null && PlanValidator.FirstError(reuse, snap) == null &&
                         CheckRobotRoles(reuse) == null)
                {
                    Incident("reprise #" + order.id + " : plan d'origine réutilisé (pas de nouvel appel)");
                    StartExecution(reuse);
                }
                else
                {
                    Incident("reprise #" + order.id +
                             " : ancien plan irréalisable, nouvelle décision Muse");
                }
            }
            stateVersion++;
            if (NextPending() != null) RequestDecision("resume_after_urgent");
        }

        /// <summary>La commande a tout son contenu dans son camion (comptage physique).</summary>
        private bool FullyLoaded(WhOrderState o, out TruckBay bay)
        {
            bay = o != null && !string.IsNullOrEmpty(o.truck_id) && World() != null ? World().Truck(o.truck_id) : null;
            if (bay == null || o.a + o.b + o.c <= 0) return false;
            int[] c = bay.CountByTypeForOrder(o.id);
            return c[0] >= o.a && c[1] >= o.b && c[2] >= o.c;
        }

        /// <summary>
        /// Commande déjà entièrement chargée mais porte refermée (mise en pause à 100 %) :
        /// rouvrir, vérifier le contenu réel, refermer -> expédition. Sans cela elle
        /// redevenait "pending" et Muse ne pouvait plus rien planifier pour elle
        /// (validateur : « déjà complète ») — elle restait bloquée à jamais.
        /// </summary>
        private IEnumerator ShipCompleteOrder(WhOrderState order, TruckBay bay)
        {
            if (!activeOrderIds.Add(order.id)) yield break;
            var plan = new MusePlan
            {
                action = "execute_order",
                order_id = order.id,
                bench_id = order.bench_id ?? "",
                truck_id = order.truck_id,
                picks = new MusePlanPick[0],
                fallback_pallets = new string[0]
            };
            activePlans[order.id] = plan;
            order.status = "loading";
            stateVersion++;
            bay.OpenDoor(order.id);
            float deadline = Time.time + 15f;
            while (!bay.IsDoorOpen && Time.time < deadline) yield return null;
            int[] inTruck = bay.CountByTypeForOrder(order.id);
            yield return VerifyAndClose(order, bay, plan, inTruck, 0,
                new TruckLoaderAgent.LoadMissionReport { OrderId = order.id });
        }

        /// <summary>Contrainte physique : seul R1 collecte, seul R2 charge.</summary>
        private string CheckRobotRoles(MusePlan plan)
        {
            if (!string.IsNullOrEmpty(plan.robot_pick) && plan.robot_pick != "R1")
                return "robot_pick=" + plan.robot_pick + " : seul R1 collecte les palettes";
            if (!string.IsNullOrEmpty(plan.robot_load) && plan.robot_load != "R2")
                return "robot_load=" + plan.robot_load + " : seul R2 charge les camions";
            return null;
        }

        private void Reject(MuseDecisionRecord record, string error)
        {
            record.RejectReason = error;
            record.Accepted = false;
            Record(record);
            lastValidation = "refusé : " + error;
            lastRejection = error;
            consecutiveRejects++;
            incidents.Add("plan refusé: " + error);
            if (incidents.Count > 8) incidents.RemoveAt(0);
            // La cause + un état neuf repartent à Muse à la prochaine demande.
            nextAllowedCall = Time.time + (consecutiveRejects >= MaxConsecutiveRejects
                ? WaitRetryDelay * 2f : CallCooldown);
            Debug.LogWarning("[Muse] plan refusé : " + error);
            MaybeRetryUrgent(record.OrderId);
        }

        /// <summary>
        /// La boucle auto est bloquée tant qu'une urgente existe : tout résultat
        /// non exécutable la concernant (refus, wait, no_plan, defer) doit relancer
        /// explicitement une décision, sinon elle resterait en file à jamais.
        /// Après 4 échecs elle redevient une commande normale et les pauses reprennent.
        /// </summary>
        private void MaybeRetryUrgent(int orderId)
        {
            if (urgentId < 0 || orderId != urgentId) return;
            urgentReplanFails++;
            if (urgentReplanFails >= 4)
            {
                DemoteUrgent("non planifiable après 4 essais");
                return;
            }
            UrgentStatus = "URGENTE #" + urgentId + " : nouvelle tentative Muse";
            StartCoroutine(RequestDecisionWhenFree("urgent_replan"));
        }

        /// <summary>L'urgente ne peut plus être planifiée : elle redevient une
        /// commande normale et les commandes en pause reprennent.</summary>
        private void DemoteUrgent(string why)
        {
            WhOrderState urgent = FindLocal(urgentId);
            Incident("urgente #" + urgentId + " " + why +
                     " : redevient normale, reprise des commandes en pause");
            if (urgent != null) urgent.urgent = false;
            urgentId = -1;
            urgentDelivered = false;
            urgentWaiting = false;
            urgentPickDone = false;
            UrgentError = "urgente non livrée : " + why;
            LastUrgentOutcome = UrgentError;
            UrgentStatus = "";
            ClearCapacityState();
            ResumePaused();
            Stage = resumedIds.Count > 0 ? UrgentStage.Resuming : UrgentStage.Failed;
        }

        private void Record(MuseDecisionRecord record)
        {
            lastDecision = record;
            history.Add(record);
            if (history.Count > 20) history.RemoveAt(0);
            Debug.Log("[Muse] " + (record.Accepted ? "ACCEPTE" : "REFUSE") +
                      " [" + record.Model + ", " + record.LatencyMs + " ms] " +
                      (record.Action ?? "-") +
                      (record.Accepted ? " " + record.AppliedResult : " — " + record.RejectReason));
            MuseTrace.Log("MUSE", record.RequestId + " trigger=" + record.Trigger + " mode=" + record.Mode +
                          " modèle=" + record.Model + " " + record.LatencyMs + " ms action=" + record.Action +
                          " commande=#" + record.OrderId + " v" + record.StateVersion +
                          (record.Accepted ? " ACCEPTÉ" : " REFUSÉ: " + record.RejectReason) +
                          (string.IsNullOrEmpty(record.PlanSummary) ? "" : " plan=" + record.PlanSummary) +
                          (string.IsNullOrEmpty(record.Reason) ? "" : " raison=\"" + record.Reason + "\""));
        }

        private static string Describe(MusePlan plan)
        {
            var sb = new StringBuilder();
            sb.Append("#").Append(plan.order_id).Append(" ");
            if (plan.picks != null)
                foreach (MusePlanPick p in plan.picks)
                    if (p != null) sb.Append(p.pallet_id).Append(":").Append(p.type).Append("x").Append(p.count).Append(" ");
            sb.Append("-> ").Append(plan.bench_id).Append(" -> ").Append(plan.truck_id);
            return sb.ToString().TrimEnd();
        }

        // ------------------------------------------------------------------ exécution

        private void StartExecution(MusePlan plan)
        {
            // Chaque commande acceptée tourne dans sa propre coroutine : les
            // exécutions se chevauchent, seuls les robots servent en séquence.
            running.Add(StartCoroutine(Execute(plan)));
        }

        private IEnumerator Execute(MusePlan plan)
        {
            WhOrderState order = FindLocal(plan.order_id);
            if (order == null || !activeOrderIds.Add(plan.order_id)) yield break;
            lastActiveOrderId = plan.order_id;
            activePlans[plan.order_id] = plan;
            phase = Phase.Preparing;
            activeBenchId = plan.bench_id;
            activeTruckId = plan.truck_id;
            // ---- réservations propres à cette commande
            ShelfStorage bench = World().Bench(plan.bench_id);
            TruckBay bay = World().Truck(plan.truck_id);
            World().ReserveBench(plan.bench_id, plan.order_id);
            // Seules les palettes de prélèvement sont réservées. Les palettes de
            // secours ne le sont PAS : sinon une commande monopolise tout le stock
            // (Muse en liste souvent 4-5) et bloque les autres commandes et
            // l'urgente. Les cartons restent protégés individuellement (orderId +
            // CargoRegistry) si un secours est réellement utilisé.
            foreach (MusePlanPick p in plan.picks ?? new MusePlanPick[0])
                if (p != null) World().ReservePallet(p.pallet_id, plan.order_id);
            MuseTrace.Log("EXEC", "démarrage " + (order.urgent ? "URGENTE " : "") + Describe(plan) +
                          "  secours=[" + string.Join(",", plan.fallback_pallets ?? new string[0]) + "]");
            order.status = "queued"; // ressources réservées, missions pas encore lancées
            order.bench_id = plan.bench_id;
            order.truck_id = plan.truck_id;
            stateVersion++;
            highlightedOrder = order.id;
            ResourceHighlighter.Show(this, plan);

            // ---- préemption : une exécution normale acceptée pendant la suspension
            // OU pendant la vie de la commande urgente se gare immédiatement
            // (réservations relâchées par ParkSuspended).
            if ((suspendInProgress || (urgentId >= 0 && !urgentPickDone) || urgentWaiting) &&
                !order.urgent && order.id != finishingId)
            {
                order.status = "suspending";
                suspendingIds.Add(order.id);
                yield return ParkSuspended(order, plan, bay, bench, null);
                yield break;
            }

            // ---- porte du camion choisi par Muse (chaque camion a sa porte)
            if (order.urgent) Stage = UrgentStage.UrgentActive;
            if (bay != null) bay.OpenDoor(plan.order_id);

            if (!plan.start_immediately && plan.start_delay_s > 0f)
                yield return new WaitForSeconds(plan.start_delay_s);

            // ---- étapes de prélèvement R1
            var legs = new List<WarehouseRobotAutopilot.PickLeg>();
            foreach (MusePlanPick p in plan.picks ?? new MusePlanPick[0])
            {
                if (p == null || p.count <= 0) continue;
                ShelfStorage pallet = World().Pallet(p.pallet_id);
                if (pallet == null) continue;
                legs.Add(new WarehouseRobotAutopilot.PickLeg
                {
                    Pallet = pallet,
                    PalletId = p.pallet_id,
                    Kind = KindOf(p.type),
                    Planned = p.count
                });
            }
            var fallbacks = new List<ShelfStorage>();
            foreach (string fb in plan.fallback_pallets ?? new string[0])
            {
                ShelfStorage s = World().Pallet(fb);
                if (s != null) fallbacks.Add(s);
            }
            UpdatePickPlanText(legs);

            // Reprise "chargement seul" : tout est déjà au banc ou dans le camion,
            // R1 n'a rien à collecter — inutile de l'exiger ni de l'attendre.
            if (bench == null || (legs.Count > 0 && (Robot1 == null || !Robot1.Running)))
            {
                Incident((bench == null ? "banc" : "R1") + " indisponible : mission impossible");
                order.status = "pending";
                if (bay != null) bay.CloseDoor(false);
                yield return FinishOrder(order, bay, plan, false);
                yield break;
            }

            // ---- R2 charge EN PARALLELE : il prend chaque carton de la commande dès
            // que R1 le dépose sur le banc. S'il finit encore la commande précédente,
            // sa mission démarre automatiquement à son retour.
            int[] prevLoaded = bay != null ? bay.CountByTypeForOrder(order.id) : new int[3];
            int expected = (order.a - prevLoaded[0]) + (order.b - prevLoaded[1]) + (order.c - prevLoaded[2]);
            if (expected <= 0 || Robot2 == null || !Robot2.Running || bay == null)
            {
                Incident("R2 ou camion indisponible pour commande #" + order.id);
                order.status = "pending";
                if (bay != null) bay.CloseDoor(false);
                yield return FinishOrder(order, bay, plan, false);
                yield break;
            }
            bool loadDone = false;
            Coroutine loadCo = StartCoroutine(LoadWhenFree(bay, bench, order, expected,
                r => { loadReport = r; loadDone = true; }));
            running.Add(loadCo);

            // ---- R1 : il finit d'abord la collecte d'une autre commande le cas échéant.
            // La file signalée évite le retour base : R1 enchaîne directement sur celle-ci.
            // Sans étape de prélèvement (reprise chargement seul) : aucune attente.
            if (legs.Count > 0)
            {
                Robot1.QueuedPicks++;
                r1Tickets[order.id] = ++r1TicketSeq;
                while ((Robot1.MissionActive || r1Busy || !IsR1Turn(order.id)) && !suspendingIds.Contains(order.id))
                    yield return null;
                r1Tickets.Remove(order.id);
                if (suspendingIds.Contains(order.id))
                {
                    Robot1.QueuedPicks--;
                    yield return ParkSuspended(order, plan, bay, bench, loadCo);
                    yield break;
                }
                r1Busy = true;
                Robot1.QueuedPicks--;
                activePalletIds.Clear();
                foreach (WarehouseRobotAutopilot.PickLeg leg in legs)
                    if (!activePalletIds.Contains(leg.PalletId)) activePalletIds.Add(leg.PalletId);
                phase = Phase.Picking;
                order.status = "picking";
                stateVersion++;
                bool pickDone = false;
                Robot1.RouteChanged += OnRouteChanged;
                Robot1.LegFinished += OnLegFinished;
                yield return Robot1.PickMission(legs, fallbacks, bench, plan.order_id,
                    r =>
                    {
                        pickReport = r;
                        pickDone = true;
                        // Appelé AVANT la décision de retour base de R1 : la reprise
                        // lui met la collecte suivante en file -> il y va directement.
                        if (order.urgent) OnUrgentPickFinished(order, plan, r);
                    });
                while (!pickDone) yield return null; // sécurité si la coroutine est interrompue
                Robot1.RouteChanged -= OnRouteChanged;
                Robot1.LegFinished -= OnLegFinished;
                r1Busy = false;

                // ---- suspension demandée pendant la collecte : mise en pause sûre
                if (suspendingIds.Contains(order.id))
                {
                    yield return ParkSuspended(order, plan, bay, bench, loadCo);
                    yield break;
                }

                // ---- inventaire après collecte
                UpdateStaged(order);
                pickPlanText = Summarize(pickReport);
                int missingTotal = pickReport != null
                    ? pickReport.Missing[0] + pickReport.Missing[1] + pickReport.Missing[2] : 0;

                if (missingTotal > 0)
                {
                    // Aucune palette de secours approuvée n'a pu combler : nouvelle décision
                    // de Muse avec les stocks actualisés. R2 vide d'abord le banc : les
                    // cartons réellement déposés restent dans le camion qui GARDE la
                    // commande (jamais déclarée complète tant que le contenu est inexact).
                    Incident("rupture sur commande #" + order.id + " : manque " +
                             MissingText(pickReport.Missing) + " — nouvelle décision demandée");
                    // R2 charge en parallèle : s'il a déjà commencé CETTE commande il vide
                    // le banc ; sinon on annule sa file d'attente (mission jamais démarrée).
                    if (Robot2.MissionActive && Robot2.DirectedOrderId == order.id)
                    {
                        yield return DrainBench(order, bench);
                        Robot2.AbortMission();
                        while (!loadDone) yield return null;
                    }
                    else if (loadCo != null && !loadDone)
                    {
                        StopCoroutine(loadCo);
                        loadDone = true;
                        if (r2BusyOwner == order.id) { r2Busy = false; r2BusyOwner = -1; }
                        if (Robot2.QueuedLoads > 0) Robot2.QueuedLoads--;
                    }

                    order.status = "waiting";
                    lastRejection = "stock insuffisant: manque " + MissingText(pickReport.Missing);
                    if (bay != null)
                    {
                        int[] inTruck = bay.CountByTypeForOrder(order.id);
                        order.loaded_a = inTruck[0]; order.loaded_b = inTruck[1]; order.loaded_c = inTruck[2];
                        // Cartons déjà chargés : même camion imposé au prochain plan ; sinon libéré.
                        order.truck_id = inTruck[0] + inTruck[1] + inTruck[2] > 0 ? plan.truck_id : null;
                        bay.CloseDoor(false);
                    }
                    UpdateStaged(order);
                    ReleasePallets(plan);
                    if (StagedTotal(order) <= 0)
                    {
                        World().ReleaseBench(plan.bench_id, plan.order_id);
                        order.bench_id = null;
                    }
                    stateVersion++;
                    EndExecution(order);
                    RequestDecision("stock_shortage");
                    yield break;
                }
            }
            else
            {
                // Rien à prélever : la commande passe directement au chargement.
                UpdateStaged(order);
            }

            // ---- R2 poursuit jusqu'à la quantité attendue (chargement déjà commencé).
            phase = Phase.Loading;
            order.status = "loading";
            stateVersion++;
            while (!loadDone && !suspendingIds.Contains(order.id)) yield return null;
            if (suspendingIds.Contains(order.id))
            {
                yield return ParkSuspended(order, plan, bay, bench, loadCo);
                yield break;
            }

            // ---- vérification du contenu livré puis fermeture
            phase = Phase.Verifying;
            yield return VerifyAndClose(order, bay, plan, prevLoaded, expected, loadReport);
        }

        /// <summary>
        /// Collecte urgente terminée (tous les cartons au banc) : R1 n'attend pas la
        /// fin du chargement par R2 — les commandes en pause reprennent tout de suite.
        /// Leurs chargements passent en file derrière celui de l'urgente (LoadWhenFree).
        /// </summary>
        private void OnUrgentPickFinished(WhOrderState order, MusePlan plan,
                                          WarehouseRobotAutopilot.PickMissionReport r)
        {
            if (r == null || r.Aborted || urgentId != order.id || pausedIds.Count == 0) return;
            if (r.Missing[0] + r.Missing[1] + r.Missing[2] > 0) return; // rupture : flux normal
            urgentPickDone = true;
            ReleasePallets(plan); // les palettes de l'urgente redeviennent prélevables
            stateVersion++;
            UrgentStatus = "URGENTE #" + order.id + " : collecte terminée, R2 charge — " +
                           "R1 reprend les commandes en pause";
            Incident("urgente #" + order.id + " déposée au banc : R1 reprend les commandes en pause");
            MuseTrace.Log("URGENT", "collecte urgente #" + order.id + " terminée : reprise anticipée R1");
            ResumePaused();
        }

        /// <summary>La mission de chargement attend que R2 ait terminé la précédente.</summary>
        private IEnumerator LoadWhenFree(TruckBay bay, ShelfStorage bench, WhOrderState order,
            int expected, System.Action<TruckLoaderAgent.LoadMissionReport> done)
        {
            // La file signalée évite le retour base : R2 enchaîne directement sur le
            // chargement suivant (repos seulement quand plus aucun travail).
            Robot2.QueuedLoads++;
            // File R2 explicite : quand plusieurs chargements attendent, ce n'est plus
            // la première coroutine réveillée qui prend R2 (ordre arbitraire de Unity —
            // la #2 passait devant la #1 à moitié chargée), mais IsR2Turn.
            r2Waiters[order.id] = new R2Waiter { Ticket = ++r2TicketSeq, Bench = bench, Seen = Time.frameCount };
            // Une mission live peut durer plusieurs minutes (un carton = ~20 s) :
            // JAMAIS d'arrêt forcé dessus — cela lâchait un carton au sol et
            // faussait le contenu des camions. Le seul cas récupérable est le
            // verrou mort : r2Busy pris alors qu'aucune mission ne tourne.
            float staleSince = -1f;
            while (Robot2.MissionActive || r2Busy || !IsR2Turn(order.id))
            {
                if (r2Waiters.TryGetValue(order.id, out R2Waiter self)) self.Seen = Time.frameCount;
                if (Robot2.MissionActive) staleSince = -1f;
                else if (r2Busy)
                {
                    if (staleSince < 0f) staleSince = Time.time;
                    else if (Time.time - staleSince > 15f)
                    {
                        Incident("verrou R2 sans mission depuis 15 s : libéré pour #" + order.id);
                        r2Busy = false;
                        r2BusyOwner = -1;
                    }
                }
                yield return null;
            }
            r2Waiters.Remove(order.id);
            Robot2.QueuedLoads--;
            r2Busy = true;
            r2BusyOwner = order.id;
            yield return Robot2.LoadMission(bay, bench, order.id, expected, done);
            // Libérer seulement si le verrou est encore à nous (un arrêt forcé ou une
            // suspension a pu le réattribuer entre-temps).
            if (r2BusyOwner == order.id)
            {
                r2Busy = false;
                r2BusyOwner = -1;
            }
        }

        private sealed class R2Waiter
        {
            public int Ticket;
            public ShelfStorage Bench;
            public int Seen;
        }

        /// <summary>Chargements en attente de R2 (commande -> ordre d'arrivée).</summary>
        private readonly Dictionary<int, R2Waiter> r2Waiters = new Dictionary<int, R2Waiter>();
        private int r2TicketSeq;

        /// <summary>
        /// Arbitrage déterministe de R2 entre les chargements en attente :
        /// 1) la commande urgente ; 2) une commande dont des cartons attendent déjà
        /// sur son banc (sinon R2 resterait planté devant un banc vide pendant que
        /// les cartons prêts d'une autre commande s'accumulent) ; 3) la plus
        /// ancienne demande (FIFO). Une commande reprise après l'urgente passe donc
        /// avant une commande arrivée plus tard.
        /// </summary>
        /// <summary>R1 sert l'urgente / la commande à finir d'abord, puis dans l'ordre des décisions.</summary>
        private bool IsR1Turn(int orderId)
        {
            int best = -1, bestRank = int.MaxValue, bestTicket = int.MaxValue;
            foreach (KeyValuePair<int, int> kv in r1Tickets)
            {
                int id = kv.Key;
                if (id != orderId && (!activeOrderIds.Contains(id) || suspendingIds.Contains(id))) continue;
                int rank = id == urgentId || id == finishingId ? 0 : 1;
                if (rank < bestRank || (rank == bestRank && kv.Value < bestTicket))
                {
                    best = id;
                    bestRank = rank;
                    bestTicket = kv.Value;
                }
            }
            return best < 0 || best == orderId;
        }

        private bool IsR2Turn(int orderId)
        {
            int best = -1, bestRank = int.MaxValue, bestTicket = int.MaxValue;
            foreach (KeyValuePair<int, R2Waiter> kv in r2Waiters)
            {
                int id = kv.Key;
                R2Waiter w = kv.Value;
                // entrée orpheline (coroutine arrêtée) ou commande qui se gare : ignorée
                if (id != orderId && (w.Seen < Time.frameCount - 2 || !activeOrderIds.Contains(id) ||
                                      suspendingIds.Contains(id)))
                    continue;
                WhOrderState o = FindLocal(id);
                int rank;
                if ((o != null && o.urgent && id == urgentId) || id == finishingId) rank = 0;
                else
                {
                    int[] onBench = w.Bench != null ? CountByTypeOnBench(w.Bench, id) : new int[3];
                    rank = onBench[0] + onBench[1] + onBench[2] > 0 ? 1 : 2;
                }
                if (rank < bestRank || (rank == bestRank && w.Ticket < bestTicket))
                {
                    best = id;
                    bestRank = rank;
                    bestTicket = w.Ticket;
                }
            }
            return best < 0 || best == orderId;
        }

        /// <summary>Attend que R2 ait chargé tous les cartons de la commande déposés sur le banc.</summary>
        private IEnumerator DrainBench(WhOrderState order, ShelfStorage bench)
        {
            float deadline = Time.time + 120f;
            while (Time.time < deadline)
            {
                int[] left = CountByTypeOnBench(bench, order.id);
                bool benchEmpty = left[0] + left[1] + left[2] == 0;
                bool handsFree = Robot2 == null || Robot2.Carried == null;
                if (benchEmpty && handsFree) yield break;
                yield return new WaitForSeconds(.25f);
            }
            Incident("vidage du banc incomplet pour commande #" + order.id);
        }

        private IEnumerator VerifyAndClose(WhOrderState order, TruckBay bay, MusePlan plan,
            int[] prevLoaded, int expected, TruckLoaderAgent.LoadMissionReport loadReport)
        {
            int[] byType = bay != null ? bay.CountByTypeForOrder(order.id) : new int[3];
            int delivered = byType[0] - prevLoaded[0] + byType[1] - prevLoaded[1] + byType[2] - prevLoaded[2];
            bool deliveredExact = loadReport != null && !loadReport.Aborted && delivered == expected;
            // Vérité physique : la commande n'est complète que si le camion contient
            // exactement le contenu commandé — jamais sur une livraison partielle.
            bool complete = byType[0] == order.a && byType[1] == order.b && byType[2] == order.c;
            if (!deliveredExact || !complete)
                Incident("contenu camion inexact commande #" + order.id + " : " +
                         byType[0] + "A/" + byType[1] + "B/" + byType[2] + "C dans " + order.truck_id +
                         ", attendu " + order.a + "A/" + order.b + "B/" + order.c + "C");

            phase = Phase.Closing;
            if (bay != null)
            {
                bay.CloseDoor(complete);
                // La porte descend seulement quand R2 est sorti (TruckBay.LoaderOutside).
                float deadline = Time.time + 20f;
                while (Time.time < deadline &&
                       bay.Door != TruckBay.TruckDoorState.Loaded &&
                       bay.Door != TruckBay.TruckDoorState.Closed)
                    yield return null;
            }

            // Les cartons livrés sont comptabilisés (cumul dans le camion), complets ou non.
            order.loaded_a = byType[0]; order.loaded_b = byType[1]; order.loaded_c = byType[2];
            UpdateStaged(order);
            if (complete)
            {
                order.status = "loaded";
                Debug.Log("[Muse] commande #" + order.id + " LIVREE dans " + order.truck_id +
                          " — porte refermée");
            }
            else
            {
                order.status = "waiting"; // jamais "loaded" sur une livraison partielle/fausse
                order.truck_id = bay != null && delivered > 0 ? order.truck_id : null;
            }
            yield return FinishOrder(order, bay, plan, complete);
        }

        private IEnumerator FinishOrder(WhOrderState order, TruckBay bay, MusePlan plan, bool delivered)
        {
            ReleasePlanResources(plan);
            EndExecution(order);
            stateVersion++;
            if (order.urgent)
            {
                if (delivered)
                {
                    urgentId = -1;
                    urgentDelivered = true;
                    urgentPickDone = false;
                    LastUrgentOutcome = "urgente #" + order.id + " livrée";
                    ClearCapacityState();
                    UrgentStatus = "URGENTE #" + order.id +
                                   " livrée — reprise des commandes en pause";
                    Incident("urgente #" + order.id + " livrée : reprise des commandes");
                    ResumePaused();
                    Stage = resumedIds.Count > 0 ? UrgentStage.Resuming : UrgentStage.Completed;
                }
                else
                {
                    // L'urgente garde la priorité tant qu'elle n'est pas livrée.
                    urgentPickDone = false;
                    UrgentStatus = "URGENTE #" + order.id + " non livrée — nouvelle tentative";
                    if (order.status == "pending" || order.status == "waiting")
                        yield return RequestDecisionWhenFree("urgent_retry");
                }
            }
            yield return null;
        }

        /// <summary>Fin de l'exécution d'UNE commande (les autres continuent).</summary>
        private void EndExecution(WhOrderState order)
        {
            activeOrderIds.Remove(order.id);
            activePlans.Remove(order.id);
            r2Waiters.Remove(order.id);
            suspendingIds.Remove(order.id);
            pausedIds.Remove(order.id);
            if (highlightedOrder == order.id)
            {
                highlightedOrder = -1;
                ResourceHighlighter.Clear();
            }
            if (lastActiveOrderId == order.id)
                lastActiveOrderId = activeOrderIds.Count > 0 ? FirstActive() : -1;
            if (activeOrderIds.Count == 0)
            {
                activeBenchId = null;
                activeTruckId = null;
                activePalletIds.Clear();
                pickPlanText = "";
                phase = Phase.Idle;
            }
        }

        private int FirstActive()
        {
            foreach (int id in activeOrderIds) return id;
            return -1;
        }

        /// <summary>Libère les réservations logiques du plan (banc, palettes, camion).</summary>
        private void ReleasePlanResources(MusePlan plan)
        {
            if (plan == null) return;
            World().ReleaseBench(plan.bench_id, plan.order_id);
            ReleasePallets(plan);
            TruckBay bay = World().Truck(plan.truck_id);
            if (bay != null) bay.ReleaseReservation();
        }

        /// <summary>Libère uniquement les réservations de palettes du plan.</summary>
        private void ReleasePallets(MusePlan plan)
        {
            if (plan == null) return;
            foreach (MusePlanPick p in plan.picks ?? new MusePlanPick[0])
                if (p != null) World().ReleasePallet(p.pallet_id, plan.order_id);
            foreach (string fb in plan.fallback_pallets ?? new string[0])
                World().ReleasePallet(fb, plan.order_id);
        }

        private void AbortExecution(string reason)
        {
            if (Robot1 != null && Robot1.MissionActive) Robot1.AbortMission();
            if (Robot2 != null && Robot2.MissionActive) Robot2.AbortMission();
            foreach (Coroutine c in running) if (c != null) StopCoroutine(c);
            running.Clear();
            r1Busy = r2Busy = false;
            r2BusyOwner = -1;
            r2Waiters.Clear();
            r1Tickets.Clear();
            if (Robot1 != null) Robot1.QueuedPicks = 0;
            if (Robot2 != null) Robot2.QueuedLoads = 0;
            if (World() != null)
                foreach (KeyValuePair<int, MusePlan> kv in activePlans)
                {
                    TruckBay bay = World().Truck(kv.Value.truck_id);
                    if (bay != null && bay.IsDoorOpen) bay.CloseDoor(false);
                    ReleasePlanResources(kv.Value);
                }
            activePlans.Clear();
            activeOrderIds.Clear();
            lastActiveOrderId = -1;
            highlightedOrder = -1;
            activeBenchId = null;
            activeTruckId = null;
            activePalletIds.Clear();
            pickPlanText = "";
            suspendingIds.Clear();
            pausedIds.Clear();
            pausedPlans.Clear();
            suspendInProgress = false;
            urgentId = -1;
            urgentReplanFails = 0;
            UrgentStatus = "";
            Stage = UrgentStage.Idle;
            UrgentError = "";
            urgentDelivered = false;
            urgentWaiting = false;
            urgentPickDone = false;
            resumedIds.Clear();
            ClearCapacityState();
            CapacityEpisode = false;
            phase = Phase.Idle;
            ResourceHighlighter.Clear();
            if (!string.IsNullOrEmpty(reason)) Incident(reason);
        }

        // ------------------------------------------------------------------ suivi d'inventaire

        /// <summary>
        /// Compteurs temps réel des commandes actives : 'preparé' (cartons orderId sur
        /// les bancs) et 'chargé' (cartons orderId dans leur camion) — le HUD et les
        /// instantanés envoyés à Muse reflètent l'état physique à ~1 Hz.
        /// </summary>
        private float invNext;
        private void RefreshLiveCounts()
        {
            if (activeOrderIds.Count == 0 || World() == null || Time.time < invNext) return;
            invNext = Time.time + .75f;
            foreach (int id in activeOrderIds)
            {
                WhOrderState o = FindLocal(id);
                if (o == null) continue;
                UpdateStaged(o);
                TruckBay bay = !string.IsNullOrEmpty(o.truck_id) ? World().Truck(o.truck_id) : null;
                if (bay == null) continue;
                int[] inTruck = bay.CountByTypeForOrder(o.id);
                o.loaded_a = inTruck[0]; o.loaded_b = inTruck[1]; o.loaded_c = inTruck[2];
            }
            stateVersion++;
        }

        /// <summary>Recompte les cartons de la commande posés sur TOUS les bancs.</summary>
        private void UpdateStaged(WhOrderState order)
        {
            order.staged_a = order.staged_b = order.staged_c = 0;
            if (World() == null) return;
            foreach (string benchId in World().BenchIds)
            {
                ShelfStorage bench = World().Bench(benchId);
                if (bench == null) continue;
                foreach (CargoBox box in bench.GetComponentsInChildren<CargoBox>(true))
                {
                    if (box == null || box.isCarried || box.orderId != order.id) continue;
                    switch (box.TypeLetter)
                    {
                        case "A": order.staged_a++; break;
                        case "B": order.staged_b++; break;
                        case "C": order.staged_c++; break;
                    }
                }
            }
        }

        /// <summary>Compteurs par type des cartons de la commande sur CE banc précis.</summary>
        private static int[] CountByTypeOnBench(ShelfStorage bench, int orderId)
        {
            var counts = new int[3];
            if (bench == null) return counts;
            foreach (CargoBox box in bench.GetComponentsInChildren<CargoBox>(true))
            {
                if (box == null || box.isCarried || box.orderId != orderId) continue;
                switch (box.TypeLetter)
                {
                    case "A": counts[0]++; break;
                    case "B": counts[1]++; break;
                    case "C": counts[2]++; break;
                }
            }
            return counts;
        }

        private static int StagedTotal(WhOrderState o)
        {
            return o.staged_a + o.staged_b + o.staged_c;
        }

        private static string MissingText(int[] missing)
        {
            var sb = new StringBuilder();
            string[] names = { "A", "B", "C" };
            for (int i = 0; i < 3; i++)
                if (missing[i] > 0)
                {
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(names[i]).Append("x").Append(missing[i]);
                }
            return sb.ToString();
        }

        private static WarehouseRobotAutopilot.CargoKind KindOf(string type)
        {
            switch ((type ?? "").ToUpperInvariant())
            {
                case "A": return WarehouseRobotAutopilot.CargoKind.WoodenCrate;
                case "B": return WarehouseRobotAutopilot.CargoKind.BigCardboard;
                case "C": return WarehouseRobotAutopilot.CargoKind.MediumCardboard;
                default: return WarehouseRobotAutopilot.CargoKind.Unknown;
            }
        }

        private void UpdatePickPlanText(List<WarehouseRobotAutopilot.PickLeg> legs)
        {
            var sb = new StringBuilder();
            if (legs != null)
                foreach (var leg in legs)
                {
                    if (sb.Length > 0) sb.Append(" | ");
                    sb.Append(leg.PalletId).Append(" ").Append(leg.Kind).Append(" x").Append(leg.Planned);
                }
            else if (pickReport != null)
                foreach (var leg in pickReport.Legs)
                {
                    if (sb.Length > 0) sb.Append(" | ");
                    sb.Append(leg.PalletId).Append(" ").Append(leg.Picked).Append("/").Append(leg.Planned);
                }
            pickPlanText = sb.ToString();
        }

        private static string Summarize(WarehouseRobotAutopilot.PickMissionReport report)
        {
            if (report == null) return "";
            var sb = new StringBuilder();
            foreach (var leg in report.Legs)
            {
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(leg.PalletId).Append(" ").Append(leg.Picked).Append("/").Append(leg.Planned);
            }
            if (report.Missing[0] + report.Missing[1] + report.Missing[2] > 0)
                sb.Append("  MANQUE ").Append(MissingText(report.Missing));
            return sb.ToString();
        }

        private void OnLegFinished(WarehouseRobotAutopilot.PickLeg leg)
        {
            UpdatePickPlanText(null);
        }

        private void OnRouteChanged(string change)
        {
            Incident("itinéraire modifié: " + change);
            UpdatePickPlanText(pickReport != null ? null : new List<WarehouseRobotAutopilot.PickLeg>());
            pickPlanText = pickPlanText + "  -> " + change;
        }

        private void Incident(string message)
        {
            incidents.Add(message);
            if (incidents.Count > 8) incidents.RemoveAt(0);
            Debug.LogWarning("[Muse] incident : " + message);
        }

        // ------------------------------------------------------------------ lecture monde (HUD/tests)

        private WhOrderState FindLocal(int id)
        {
            foreach (WhOrderState o in orders) if (o.id == id) return o;
            return null;
        }

        public string DoorStateOf(string truckId)
        {
            TruckBay bay = World() != null ? World().Truck(truckId) : null;
            return bay != null ? bay.Door.ToString() : "inconnu";
        }

        public string RobotStatusOf(string robotId)
        {
            if (robotId == "R1") return Robot1 != null ? Robot1.Status : "absent";
            if (robotId == "R2") return Robot2 != null ? Robot2.Status : "absent";
            return "inconnu";
        }

        public int BoxesOnBenchForOrder(int orderId)
        {
            WhOrderState o = FindLocal(orderId);
            if (o == null || string.IsNullOrEmpty(o.bench_id) || World() == null) return 0;
            ShelfStorage bench = World().Bench(o.bench_id);
            if (bench == null) return 0;
            int count = 0;
            foreach (CargoBox box in bench.GetComponentsInChildren<CargoBox>(true))
                if (box != null && box.orderId == orderId) count++;
            return count;
        }

        public int BoxesInTruckForOrder(string truckId, int orderId)
        {
            TruckBay bay = World() != null ? World().Truck(truckId) : null;
            return bay != null ? bay.CountBoxesForOrder(orderId) : 0;
        }

        public Vector3 PositionOf(string resourceId)
        {
            return World() != null && World().TryPositionOf(resourceId, out Vector3 p) ? p : Vector3.zero;
        }

        public float MinRobotWorkerDistance(string robotId)
        {
            Transform robot = robotId == "R2" ? (Robot2 != null ? Robot2.transform : null)
                                            : (Robot1 != null ? Robot1.transform : null);
            if (robot == null) return float.PositiveInfinity;
            float best = float.PositiveInfinity;
            foreach (IPedestrianSource crew in PedestrianSources.All)
                foreach (PedestrianAgent agent in crew.Agents)
                {
                    if (agent == null || agent.Root == null) continue;
                    float d = Vector3.Distance(robot.position, agent.Root.position);
                    if (d < best) best = d;
                }
            if (best < WorkerProximityAlert && Time.time > workerAlertCooldown)
            {
                workerAlertCooldown = Time.time + 5f;
                Incident("employé à " + best.ToString("F2") + " m de " + robotId);
            }
            return best;
        }

        // ------------------------------------------------------------------ pilotage (tests)

        public void SetMode(string mode)
        {
            switch ((mode ?? "").ToLowerInvariant())
            {
                case "live": Perception = PerceptionMode.Live; break;
                case "mock": Perception = PerceptionMode.Mock; break;
                case "offline": Perception = PerceptionMode.Offline; break;
            }
        }

        /// <summary>Test : la prochaine demande mock renverra un plan volontairement invalide.</summary>
        public void InjectPlanError(string error) { injectedPlanError = error; }

        /// <summary>Phase courante de l'orchestrateur (diagnostic HUD).</summary>
        public string CurrentPhase => phase.ToString();
    }
}
