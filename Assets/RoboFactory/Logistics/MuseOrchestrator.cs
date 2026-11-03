using System.Collections;
using System.Collections.Generic;
using System.Text;
using RoboFactory.Hackathon;
using RoboFactory.Hackathon.Meta;
using RoboFactory.Hackathon.Orchestration;
using RoboFactory.Integration;
using RoboFactory.MetaServer;
using UnityEngine;
using RoboFactory.Sim;

namespace RoboFactory.Logistics
{
    /// <summary>
    /// Central orchestrator: logistics decisions come from Muse Wonder through the
    /// Meta proxy (/warehouse). This class NEVER picks pallets/benches/trucks
    /// by itself: it sends the real state, validates the received plan, reserves resources,
    /// drives the doors and the robots' directed missions, then checks the delivered
    /// content before closing the door.
    ///
    /// Only one active order and one open door at a time (version 1).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MuseOrchestrator : MonoBehaviour, IMuseOrchestration
    {
        [Header("Proxy Meta")]
        public string ProxyUrl = "http://127.0.0.1:8765";
        public PerceptionMode Perception = PerceptionMode.Live;

        [Header("Decision")]
        [Tooltip("Chains requests to Muse as soon as an order is waiting.")]
        public bool AutoDecide = true;
        [Tooltip("Minimum pause between two Meta calls.")]
        public float CallCooldown = 3f;
        [Tooltip("Retry delay when Muse answers wait/no_plan.")]
        public float WaitRetryDelay = 8f;
        [Tooltip("Consecutive rejections before a long pause.")]
        public int MaxConsecutiveRejects = 4;
        [Tooltip("defer_order: delay before the order becomes a candidate again.")]
        public float DeferredReactivateDelay = 30f;

        [Header("Execution (optional: found automatically)")]
        public WarehouseRobotAutopilot Robot1;
        public TruckLoaderAgent Robot2;

        [Header("Fixed urgent lane (option)")]
        [Tooltip("Option: one bench and one truck permanently reserved for urgent orders. Off by default: " +
                 "normal orders use all 3 benches and 3 trucks, and an urgent order frees its capacity " +
                 "through the FINISH TO FREE policy.")]
        public bool ReserveUrgentLane = false;
        public string UrgentBenchId = "B01";
        public string UrgentTruckId = "T01";

        [Header("Safety")]
        [Tooltip("Incident when an employee comes closer than this distance to a robot on a mission.")]
        public float WorkerProximityAlert = .9f;

        // ------------------------------------------------------------------ state
        private enum Phase { Idle, Deciding, Preparing, Picking, Loading, Verifying, Closing }

        private MetaProxyClient proxy;
        private readonly List<WhOrderState> orders = new List<WhOrderState>();
        private readonly List<MuseDecisionRecord> history = new List<MuseDecisionRecord>();
        private readonly List<string> incidents = new List<string>();
        private readonly Dictionary<int, float> deferredUntil = new Dictionary<int, float>();
        private int nextOrderId = 1;
        // Several orders can run in parallel: each one reserves ITS OWN
        // resources (bench, truck, pallets); the robots serve them in sequence.
        private readonly HashSet<int> activeOrderIds = new HashSet<int>();
        private readonly Dictionary<int, MusePlan> activePlans = new Dictionary<int, MusePlan>();
        private readonly List<Coroutine> running = new List<Coroutine>();
        private int lastActiveOrderId = -1;
        private int highlightedOrder = -1;
        private bool r1Busy, r2Busy;
        private int r1TicketSeq;
        /// <summary>Picks waiting for R1 (order -> arrival rank).</summary>
        private readonly Dictionary<int, int> r1Tickets = new Dictionary<int, int>();
        /// <summary>Order holding the R2 lock (releasable even if its queue was killed).</summary>
        private int r2BusyOwner = -1;
        private Phase phase = Phase.Idle;

        private int stateVersion;
        private int requestSeq;
        private string latestRequestId;
        private bool pending;
        private float pendingSince;
        /// <summary>A call left unanswered beyond this delay is dropped (anti-deadlock).</summary>
        public float PendingTimeout = 135f;

        // ------------------------------------------------------------------ spoken urgent orders
        [Header("Urgent orders")]
        [Tooltip("Minimum transcription confidence to execute without confirmation.")]
        public float MinUrgentConfidence = .5f;
        [Tooltip("Maximum delay for the robots to reach a safe state on suspension.")]
        public float SuspendTimeout = 30f;

        /// <summary>Visible stages of the urgent pipeline for the HUD and the demo report.</summary>
        /// <summary>WaitingForCapacity = URGENT_WAITING_FOR_CAPACITY: urgent order accepted, no free
        /// bench + empty truck pair; a normal order is finished to free one (FINISH TO FREE).</summary>
        public enum UrgentStage { Idle, Planning, Suspending, UrgentActive, Resuming, Completed, Failed, WaitingForCapacity }
        public UrgentStage Stage { get; private set; } = UrgentStage.Idle;
        /// <summary>Last failure of the urgent pipeline (displayed text, never hidden).</summary>
        public string UrgentError { get; private set; } = "";
        /// <summary>Outcome of the last urgent order: "delivered" / failure reason.</summary>
        public string LastUrgentOutcome { get; private set; } = "";
        /// <summary>/urgent call in flight (transcription -> Muse interpretation).</summary>
        public bool UrgentPending => urgentPending;
        /// <summary>Resumed orders being executed after the urgent one.</summary>
        private readonly HashSet<int> resumedIds = new HashSet<int>();
        /// <summary>Has the current urgent order been physically delivered.</summary>
        private bool urgentDelivered;
        /// <summary>Muse answered "wait": a new voice attempt is scheduled.
        /// While it is armed, no normal decision may steal the resources
        /// being freed — the urgent order keeps priority.</summary>
        private bool urgentWaiting;
        private int urgentVoiceRetries;
        private const int MaxUrgentVoiceRetries = 24; // ~2 min waiting for resources to free up
        /// <summary>Voice generation: invalidates retries armed by an older utterance.</summary>
        private int urgentVoiceGen;

        /// <summary>/urgent call in flight.</summary>
        private bool urgentPending;
        /// <summary>SUSPENDING -> PAUSED transition in progress.</summary>
        private bool suspendInProgress;
        /// <summary>Current urgent order (-1 = none): blocks normal auto-decisions.</summary>
        private int urgentId = -1;
        /// <summary>R1 has dropped the whole urgent order on the bench: it goes back to the
        /// paused orders while R2 finishes loading the urgent one.</summary>
        private bool urgentPickDone;
        private int urgentReplanFails;
        /// <summary>Normal orders being suspended.</summary>
        private readonly HashSet<int> suspendingIds = new HashSet<int>();
        /// <summary>Paused normal orders (state saved, resume planned).</summary>
        private readonly HashSet<int> pausedIds = new HashSet<int>();
        /// <summary>Original Muse plan of each paused order (reused on resume if still feasible).</summary>
        private readonly Dictionary<int, MusePlan> pausedPlans = new Dictionary<int, MusePlan>();
        /// <summary>Request in flight (HUD): "wh-3 (12s)" or empty.</summary>
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

        // display of the most recent mission (debug HUD). Shared reports:
        // a single pick (R1) and a single load (R2) never run at the same time.
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
        public string IncidentsText => incidents.Count == 0 ? "none" : string.Join(" | ", incidents);
        public IReadOnlyList<MuseDecisionRecord> History => history;
        /// <summary>Urgent flow state for the HUD (interpretation, suspension, execution).</summary>
        public string UrgentStatus { get; private set; } = "";
        public int UrgentOrderId => urgentId;
        public int PausedCount => pausedIds.Count;
        public int SuspendingCount => suspendingIds.Count;
        public int ResumedCount => resumedIds.Count;

        private void Awake()
        {
            proxy = new MetaProxyClient { BaseUrl = ProxyUrl };
            capacityClient = new MuseCapacityClient { BaseUrl = ProxyUrl };
            // The robots switch to directed control before their own Start().
            if (Robot1 == null) Robot1 = FindFirstObjectByType<WarehouseRobotAutopilot>();
            if (Robot2 == null) Robot2 = FindFirstObjectByType<TruckLoaderAgent>();
            if (Robot1 != null) Robot1.AutoStart = false;
            if (Robot2 != null) Robot2.AutoStart = false;
        }

        private void Start()
        {
            if (World() == null)
            {
                Debug.LogError("[Muse] WarehouseWorld missing from the scene: orchestration impossible.");
                enabled = false;
                return;
            }
            // Every bay switches to orchestrator management (door closed at start).
            foreach (string truckId in World().TruckIds)
            {
                TruckBay bay = World().Truck(truckId);
                if (bay != null) bay.SetManaged(true);
            }
            if (Robot1 == null) Robot1 = World().R1;
            if (Robot2 == null) Robot2 = World().R2;
            if (Robot1 != null && !Robot1.Running) { Robot1.Stop(); Robot1.BeginDirected(); }
            // After a pick, R1 does not go back to base if work is coming:
            // a Muse decision in flight (e.g. resume after an urgent order) or orders
            // suspended / paused. Otherwise it made the round trip for nothing.
            if (Robot1 != null)
                Robot1.StayOut = () => pending || urgentPending ||
                                       suspendingIds.Count > 0 || pausedIds.Count > 0;
            if (Robot2 != null)
            {
                if (!Robot2.Running) Robot2.BeginDirected();
                // While orders are suspended/paused, R2 does not go back
                // to base between two loads: the resume is imminent.
                Robot2.StayOut = () => suspendingIds.Count > 0 || pausedIds.Count > 0;
            }
            Debug.Log("[Muse] orchestrator ready: " + World().PalletCount + " pallets, " +
                      World().BenchCount + " benches, " + World().TruckCount + " trucks, mode " + Mode);
        }

        private WarehouseWorld world;
        private WarehouseWorld World()
        {
            if (world == null) world = WarehouseWorld.Instance != null
                ? WarehouseWorld.Instance : FindFirstObjectByType<WarehouseWorld>();
            return world;
        }

        // ------------------------------------------------------------------ orders

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
            Debug.Log("[Muse] order #" + order.id + ": " + OrderText.Format(new[] { a, b, c }) +
                      (activeOrderIds.Count > 0 ? " (decided as soon as resources are available)" : ""));
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
            AbortExecution("orders reset");
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

        // ------------------------------------------------------------------ main loop

        private void Update()
        {
            RefreshLiveCounts();
            // Safety net: a call that is never answered must not block the queue.
            if (pending && Time.time - pendingSince > PendingTimeout)
            {
                pending = false;
                nextAllowedCall = Time.time + 5f;
                Incident("call " + latestRequestId + " unanswered after " +
                         PendingTimeout.ToString("F0") + "s: retrying");
            }
            if (World() == null || !AutoDecide) return;
            if (pending || urgentPending || urgentWaiting || suspendInProgress) return;
            // An urgent order takes over the loop: normal orders
            // (suspended or queued) only get decisions again after its delivery.
            // Exception: urgent pick finished -> R1 is free, the resumed orders
            // can get their decisions while R2 loads the urgent one.
            if (urgentId >= 0 && !urgentPickDone) return;
            if (Time.time < nextAllowedCall) return;
            // ---- end of the resume: every resumed order has been delivered
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
            // An order is only decided if it can really run:
            // free bench + free truck + available stock. Otherwise it waits in the queue.
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

        // ------------------------------------------------------------------ Meta call

        /// <summary>Forces a decision request (async — the answer arrives later).</summary>
        public void RequestDecision(string trigger)
        {
            if (pending || urgentPending) return;
            // RECORDED DEMO: Meta is asked at the moment the recording asked it, with the recorded trigger, so the
            // recorded answers come back in their order (an earlier call would get the plan of another order).
            if (RecordedDemoRuntime.Replaying &&
                RecordedDemoRuntime.NextRecorded("/warehouse", out double recordedAt, out string recordedTrigger, out int recordedOrder))
            {
                if (!replayForce && !RecordedCallDue(recordedAt, recordedOrder))
                {
                    if (replayDeferred == null) replayDeferred = StartCoroutine(RequestAtRecordedTime(recordedAt, recordedOrder));
                    return;
                }
                if (recordedTrigger.Length > 0) trigger = recordedTrigger;
            }
            // Urgent order in progress (including waiting for capacity): no new normal
            // order is planned — freed resources go to the urgent one.
            if (urgentId >= 0 && !urgentPickDone && (trigger == "new_order" || trigger == "pending_order"))
            {
                Debug.Log("[Muse] normal decision deferred (" + trigger + "): urgent #" + urgentId + " has priority");
                return;
            }
            if (Mode == "offline")
            {
                lastValidation = "offline: no Meta call";
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
            injectedPlanError = null; // consumed: a single injection per call
            pending = true;
            pendingSince = Time.time;
            Calls++;
            StartCoroutine(proxy.Orchestrate(request, response => OnPlanResponse(request, response)));
            Debug.Log("[Muse] request " + latestRequestId + " (" + trigger + ", state v" + stateVersion + ")");
        }

        private void OnPlanResponse(WarehouseRequest request, WarehouseResponse response)
        {
            pending = false;
            nextAllowedCall = Time.time + CallCooldown;

            // Stale answer: a more recent request was sent in the meantime.
            if (request.request_id != latestRequestId)
            {
                Debug.Log("[Muse] answer ignored (stale): " + request.request_id);
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
                record.RejectReason = "Meta call failed" + (response != null ? ": " + response.error : "");
                record.Mode = Mode;
                Record(record);
                lastValidation = record.RejectReason;
                // Meta did not answer (timeout, silent proxy, network error): we
                // ask again WITHOUT LIMIT, with a short growing delay (2 -> 10 s).
                // This is not a Muse refusal: these retries do not count in the
                // 4 failures that demote an urgent order.
                metaFailStreak++;
                float delay = RetryDelay();
                string trig = request.trigger ?? "";
                bool urgentCall = trig.StartsWith("urgent") && urgentId >= 0 && !urgentPickDone;
                if (urgentCall)
                {
                    record.OrderId = urgentId;
                    UrgentStatus = "URGENT #" + urgentId + ": no answer from Meta (attempt " + metaFailStreak +
                                   ") — new request in " + delay.ToString("F0") + " s";
                }
                Incident("no answer from Meta (" + trig + ", attempt " + metaFailStreak + "): new request in " +
                         delay.ToString("F0") + " s");
                nextAllowedCall = Time.time + delay; // the auto loop (pending_order) retries too
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
                record.RejectReason = "answer without a plan (" + (response.error ?? "invalid json") + ")";
                Reject(record, "answer without a plan");
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
                record.AppliedResult = "waiting: " + plan.reason;
                Record(record);
                lastValidation = "accepted (" + plan.action + ")";
                nextAllowedCall = Time.time + WaitRetryDelay;
                MaybeRetryUrgent(plan.order_id);
                return;
            }
            if (action == "defer_order")
            {
                WhOrderState target = PlanValidator.FindOrder(lastSnapshot, plan.order_id);
                if (target == null) { Reject(record, "defer on unknown order"); return; }
                deferredUntil[plan.order_id] = Time.time + DeferredReactivateDelay;
                WhOrderState local = FindLocal(plan.order_id);
                if (local != null) local.status = "pending";
                record.Accepted = true;
                record.AppliedResult = "order #" + plan.order_id + " deferred";
                Record(record);
                lastValidation = "accepted (defer)";
                stateVersion++;
                MaybeRetryUrgent(plan.order_id);
                return;
            }

            // Validation on a FRESH state — not the one sent several seconds ago.
            WhOrderState planned = FindLocal(plan.order_id);
            var fresh = Snapshot("validate", planned != null && planned.urgent && plan.order_id == urgentId);
            string error = PlanValidator.FirstError(plan, fresh);
            if (error != null) { Reject(record, error); return; }
            error = CheckRobotRoles(plan);
            if (error != null) { Reject(record, error); return; }

            record.Accepted = true;
            record.AppliedResult = "plan accepted: " + record.PlanSummary;
            Record(record);
            lastValidation = "accepted";
            consecutiveRejects = 0;
            lastRejection = null;
            StartExecution(plan);
        }

        // ------------------------------------------------------------------ urgent orders

        /// <summary>
        /// Entry point of spoken urgent orders: the FINAL transcription
        /// (never a partial) is sent to Muse Wonder, which decides whether it is a
        /// real urgent order, extracts the quantities and produces the plan.
        /// </summary>
        public void SubmitUrgentTranscript(string transcript)
        {
            transcript = (transcript ?? "").Trim();
            if (transcript.Length == 0) return;
            if (urgentId >= 0)
            {
                UrgentStatus = "urgent #" + urgentId + " already in progress — new urgent order refused";
                return;
            }
            if (Mode == "offline")
            {
                UrgentStatus = "offline mode: no Meta decision available";
                return;
            }
            urgentVoiceGen++; // invalidates a retry possibly armed by an older utterance
            BeginUrgentStage(UrgentStage.Planning);
            StartCoroutine(UrgentVoiceFlow(transcript));
        }

        /// <summary>Manual urgent button: same pipeline without speech recognition.</summary>
        public int SubmitUrgentCounts(int a, int b, int c)
        {
            if (a + b + c <= 0) return 0;
            if (urgentId >= 0)
            {
                UrgentStatus = "urgent #" + urgentId + " already in progress — new urgent order refused";
                return 0;
            }
            urgentVoiceGen++; // invalidates a voice retry armed by an older utterance
            // Planning: what comes next (direct suspension or waiting for capacity) is decided by UrgentAdmission.
            BeginUrgentStage(UrgentStage.Planning);
            WhOrderState order = CreateOrder(a, b, c, true);
            UrgentStatus = "URGENT #" + order.id + ": " + OrderText.Format(new[] { a, b, c }) +
                           " — suspending normal orders";
            Incident("manual urgent order #" + order.id + ": suspension in progress");
            StartCoroutine(UrgentAdmission(order, null));
            return order.id;
        }

        /// <summary>Prepares a new urgent flow: stage, outputs and markers reset.</summary>
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
        /// Preemption releases the pallets of normal orders (ParkSuspended ->
        /// ReleasePallets): for the urgent decision they are therefore available.
        /// Benches and trucks stay reserved (they hold staged/loaded boxes).
        /// The displayed stock already excludes boxes taken by an order (orderId).
        /// The plan is still revalidated on the fresh state AFTER suspension.
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

        /// <summary>Muse said "wait": wait 5 s then ask again with the fresh state.
        /// The urgentWaiting guard blocks normal decisions during the wait;
        /// the gen token invalidates the retry if a new utterance has arrived.</summary>
        private IEnumerator RetryUrgentVoice(string transcript, int gen, float delay = 5f)
        {
            urgentWaiting = true;
            yield return new WaitForSeconds(delay);
            urgentWaiting = false;
            if (gen == urgentVoiceGen && urgentId < 0)
                StartCoroutine(UrgentVoiceFlow(transcript));
        }

        /// <summary>Suspends the normal orders then asks Muse for a plan (manual urgent order).</summary>
        private IEnumerator SuspendThenPlan(WhOrderState order)
        {
            yield return SuspendActiveOrders();
            yield return RequestDecisionWhenFree("urgent_order");
        }

        /// <summary>Full voice flow: /urgent -> suspension -> fresh validation -> execution.</summary>
        private IEnumerator UrgentVoiceFlow(string transcript)
        {
            float deadline = Time.time + PendingTimeout + 10f;
            while (pending && Time.time < deadline) yield return null; // a /warehouse call may be in flight
            if (pending)
            {
                UrgentStatus = "previous Meta call unanswered: urgent order postponed";
                yield break;
            }

            urgentPending = true;
            stateVersion++;
            int assignedId = nextOrderId; // id the order will get if it is accepted
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
            UrgentStatus = "Muse Wonder interpreting… (" + Mode + ")";
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
                record.RejectReason = "urgent call failed" +
                                      (response != null ? ": " + response.error : "");
                Record(record);
                // Meta did not answer: the same transcription is resent WITHOUT LIMIT
                // (a new utterance invalidates these retries through urgentVoiceGen).
                metaFailStreak++;
                float delay = RetryDelay();
                UrgentStatus = "no answer from Meta (attempt " + metaFailStreak + ") — new interpretation in " +
                               delay.ToString("F0") + " s";
                Stage = UrgentStage.Planning;
                Incident(record.RejectReason + ": retrying in " + delay.ToString("F0") + " s");
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

            // ---- never a mission on an uncertain or non-urgent transcription
            if (result.action == "clarify")
            {
                record.Accepted = true;
                record.AppliedResult = "clarification: " + result.clarification;
                Record(record);
                UrgentStatus = "TO CONFIRM: " +
                    (result.clarification.Length > 0 ? result.clarification : result.reason);
                Stage = UrgentStage.Idle; // back to idle: rephrase and speak again
                yield break;
            }
            // Muse understood the urgency but is waiting for resources: retry with
            // the fresh state instead of failing (spec: wait for the safe release).
            if (result.urgent &&
                (result.action == "wait" || result.action == "no_plan" ||
                 result.action == "defer_order"))
            {
                record.Accepted = true;
                record.AppliedResult = result.action + " : " + result.reason;
                Record(record);
                // Urgency understood (quantities certain) but no free bench + truck:
                // FINISH TO FREE — the urgent order is created and frees its capacity by
                // having a normal order finish, instead of waiting passively.
                if (result.order != null && result.order.Total > 0 && result.confidence >= MinUrgentConfidence &&
                    !HasUrgentCapacity(result.order.Total, out _, out _))
                {
                    WhOrderState waitingUrgent = CreateOrder(result.order.A, result.order.B, result.order.C, true);
                    record.OrderId = waitingUrgent.id;
                    UrgentStatus = "URGENT #" + waitingUrgent.id + ": " +
                                   OrderText.Format(new[] { result.order.A, result.order.B, result.order.C }) +
                                   " — all docks busy";
                    Incident("urgent order #" + waitingUrgent.id + " validated: capacity to free (FINISH TO FREE)");
                    yield return UrgentAdmission(waitingUrgent, null);
                    yield break;
                }
                urgentVoiceRetries++;
                if (urgentVoiceRetries <= MaxUrgentVoiceRetries)
                {
                    UrgentStatus = "Muse is waiting for resources (" + result.reason +
                                   ") — new attempt " + urgentVoiceRetries +
                                   "/" + MaxUrgentVoiceRetries + " in 5 s";
                    Stage = UrgentStage.Planning;
                    StartCoroutine(RetryUrgentVoice(transcript, urgentVoiceGen));
                }
                else
                {
                    UrgentStatus = "urgent order impossible after " + MaxUrgentVoiceRetries +
                                   " attempts: " + result.reason;
                    UrgentError = UrgentStatus;
                    LastUrgentOutcome = UrgentError;
                    // Orders may have parked during the wait: resume them.
                    ResumePaused();
                    Stage = resumedIds.Count > 0 ? UrgentStage.Resuming : UrgentStage.Failed;
                }
                yield break;
            }
            if (!result.urgent || result.action != "execute_order" || result.plan == null ||
                result.order == null || result.order.Total <= 0)
            {
                record.Accepted = true;
                record.AppliedResult = result.action + ": " + result.reason;
                Record(record);
                UrgentStatus = !result.urgent
                    ? "not an urgent order (" + result.reason + ")"
                    : "Muse cannot execute the urgent order: " + result.reason;
                if (!result.urgent)
                {
                    Stage = UrgentStage.Idle; // just a normal sentence: no incident
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
                record.RejectReason = "transcription confidence " + result.confidence.ToString("F2") +
                                      " < " + MinUrgentConfidence;
                Reject(record, record.RejectReason);
                UrgentStatus = "uncertain transcription — please rephrase the urgent order";
                UrgentError = record.RejectReason;
                LastUrgentOutcome = record.RejectReason;
                Stage = UrgentStage.Failed;
                yield break;
            }

            // ---- real urgent order: creation + suspension of the normal orders
            WhOrderState order = CreateOrder(result.order.A, result.order.B, result.order.C, true);
            result.plan.order_id = order.id;
            record.OrderId = order.id;
            record.PlanSummary = Describe(result.plan);
            record.Accepted = true;
            record.AppliedResult = "urgent accepted: " + record.PlanSummary;
            Record(record);
            UrgentStatus = "URGENT #" + order.id + ": " +
                           OrderText.Format(new[] { result.order.A, result.order.B, result.order.C }) +
                           " (confidence " + result.confidence.ToString("F2") + ") — suspending…";
            Incident("urgent order #" + order.id + " validated: suspending normal orders");
            yield return UrgentAdmission(order, result.plan);
        }

        /// <summary>Urgent (voice) plan revalidated on the FRESH post-suspension state, otherwise replanned.</summary>
        private IEnumerator ContinueUrgent(WhOrderState order, MusePlan plan)
        {
            if (urgentId != order.id) yield break;
            if (plan == null)
            {
                UrgentStatus = "URGENT #" + order.id + ": Muse planning";
                yield return RequestDecisionWhenFree("urgent_order");
                yield break;
            }
            plan.order_id = order.id;
            var fresh = Snapshot("urgent_validate", true);
            string error = PlanValidator.FirstError(plan, fresh) ?? CheckRobotRoles(plan);
            if (error != null)
            {
                Incident("urgent plan refused after suspension (" + error + "): replanning");
                urgentReplanFails++;
                if (urgentReplanFails >= 4)
                {
                    DemoteUrgent("not plannable after suspension (" + error + ")");
                    yield break;
                }
                UrgentStatus = "URGENT #" + order.id + ": plan refused (" + error + "), retrying";
                yield return RequestDecisionWhenFree("urgent_replan");
                yield break;
            }
            UrgentStatus = "URGENT #" + order.id + ": executing (" + Describe(plan) + ")";
            StartExecution(plan);
        }

        /// <summary>Real warehouse state + lane rule (urgent bench/truck).</summary>
        private WarehouseSnapshot Snapshot(string trigger, bool urgent)
        {
            WarehouseSnapshot snap = World().BuildSnapshot(orders, stateVersion, trigger, Mode, incidents, lastRejection);
            ApplyLanes(snap, urgent);
            // FINISH TO FREE: the « finish_for_urgent » decision only concerns the order to
            // finish; the other waiting orders are shown as held (copies — the
            // real orders are never modified by this display).
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
        /// Urgent lane: for a normal decision, the urgent bench and truck appear
        /// reserved ("urgent_lane"); for the urgent order, the others do ("normal_lane"). Muse
        /// never uses a reserved resource and the validator refuses any plan that
        /// would — so the rule is enforced on both sides. A resource already reserved
        /// by a real order keeps its real reservation.
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
            notes.Add("rule: bench " + UrgentBenchId + " and truck " + UrgentTruckId +
                      " reserved for urgent orders; normal orders use the others");
            snap.incidents = notes.ToArray();
        }

        /// <summary>Meta calls lost in a row (reset as soon as an answer arrives).</summary>
        private int metaFailStreak;

        /// <summary>Delay before asking again after a lost call: 4, 6, 8 then 10 s.</summary>
        private float RetryDelay() { return Mathf.Min(10f, 2f + metaFailStreak * 2f); }

        /// <summary>
        /// Retries a lost /warehouse call while it still has a purpose:
        /// the urgent order is still waiting for its plan, or an order is waiting for a decision.
        /// </summary>
        private IEnumerator RetryCallAfter(string trigger, float delay)
        {
            yield return new WaitForSeconds(delay);
            if (trigger.StartsWith("urgent"))
            {
                if (urgentId < 0 || urgentPickDone) yield break; // urgent order delivered, demoted or already planned
                if (activeOrderIds.Contains(urgentId)) yield break; // plan received in the meantime
            }
            else if (NextPending() == null && trigger != "stock_shortage") yield break;
            yield return RequestDecisionWhenFree(trigger);
        }

        // ================================================================== FINISH TO FREE

        /// <summary>Normal order Muse chose to finish to free capacity (-1 = none).</summary>
        private int finishingId = -1;
        public int FinishingOrderId => finishingId;
        /// <summary>Bench / truck the finishing order must free (HUD display).</summary>
        public string FreeingBenchId { get; private set; } = "";
        public string FreeingTruckId { get; private set; } = "";
        /// <summary>Visible reason for the wait: docks busy, free bench without an empty truck, etc.</summary>
        public string CapacityReason { get; private set; } = "";
        /// <summary>Current strategy (HUD text): "Finish Order #1", "selecting…", wait reason.</summary>
        public string CapacityStrategy { get; private set; } = "";
        /// <summary>Secondary note (e.g. Muse strategy unavailable) — never shown as a refusal.</summary>
        public string CapacityNote { get; private set; } = "";
        /// <summary>Announced release estimate (s) for the order to finish (-1 unknown).</summary>
        public float CapacityEtaS { get; private set; } = -1f;
        /// <summary>The current urgent order went through the capacity wait (« Resources available » banner).</summary>
        public bool CapacityEpisode { get; private set; }
        /// <summary>Bench + truck actually available found for the urgent order (display).</summary>
        public string CapacityBenchId { get; private set; } = "";
        public string CapacityTruckId { get; private set; } = "";
        /// <summary>Bench + empty truck pair actually available for the urgent order (end of the wait).</summary>
        public bool CapacityReady => !string.IsNullOrEmpty(CapacityBenchId) && !string.IsNullOrEmpty(CapacityTruckId);

        [Header("Urgent: FINISH TO FREE")]
        [Tooltip("If the order to finish makes no progress during this delay (s), it is dropped and Muse picks another one.")]
        public float FinishWatchdog = 150f;
        [Tooltip("Delay (s) between two re-evaluations when no order can free capacity (no call loop).")]
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
        /// Bench + truck pair usable RIGHT NOW by the urgent order: unreserved bench with
        /// free levels; unreserved truck, physically EMPTY (no box from another
        /// order, never a trailer that is still loaded), enough slots, door at rest.
        /// </summary>
        /// <summary>Read-only: is there a free bench AND truck for an urgent order of this size right now?</summary>
        public bool UrgentCapacityFree(int need)
        {
            return HasUrgentCapacity(need, out _, out _);
        }

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
        /// An active order has not committed anything yet (no box staged or loaded, no robot
        /// on a mission for it): pausing it immediately gives back its bench AND its truck.
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
        /// Entry of every accepted urgent order (button or voice). New normal decisions are
        /// already blocked (urgentId). Capacity available (or freeable by pausing an order
        /// that has committed nothing) -> safe suspension then immediate execution. Otherwise FINISH TO FREE.
        /// </summary>
        private IEnumerator UrgentAdmission(WhOrderState order, MusePlan voicePlan)
        {
            CapacityEpisode = false;
            ClearCapacityState();
            int need = order.a + order.b + order.c;
            bool capacity = HasUrgentCapacity(need, out string b0, out string t0);
            if (!capacity && PausingFreesCapacity())
            {
                MuseTrace.Log("URGENT", "urgent #" + order.id + ": an order with no committed box frees a dock when paused");
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
            // safety net: no normal order runs anymore during the urgent one
            if (ActiveNormalCount() > 0) yield return SuspendActiveOrders();
            // the possible voice plan targeted the old state: replanning on the freed capacity
            yield return ContinueUrgent(order, null);
        }

        /// <summary>
        /// URGENT_WAITING_FOR_CAPACITY: Muse chooses THE normal order whose completion frees
        /// a bench AND an empty truck the fastest; the others are suspended (indivisible operations
        /// respected); the chosen one is finished first (checked, closed, shipped). We wait for the
        /// REAL availability of the pair, then hand over to urgent planning.
        /// No box is moved or deleted; no resource is reserved twice.
        /// </summary>
        private IEnumerator FinishToFree(WhOrderState order)
        {
            int need = order.a + order.b + order.c;
            Stage = UrgentStage.WaitingForCapacity;
            CapacityEpisode = true;
            UrgentStatus = "URGENT #" + order.id + ": waiting for capacity (all docks busy)";
            Incident("URGENT #" + order.id + ": all benches/docks busy — FINISH TO FREE");
            MuseTrace.Log("URGENT", "WAITING_FOR_CAPACITY urgent #" + order.id + " (" + need + " box(es))");
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
                    Incident("URGENT #" + order.id + ": capacity freed — bench " + bench + " / empty truck " + truck);
                    MuseTrace.Log("URGENT", "capacity available for #" + order.id + ": " + bench + " / " + truck);
                    finishingId = -1;
                    yield break;
                }
                CapacityReason = MissingReason(need);

                // ---- an order is being finished: watch it
                if (finishingId >= 0)
                {
                    WhOrderState f = FindLocal(finishingId);
                    if (f != null && f.status == "waiting" && !activeOrderIds.Contains(f.id))
                    {
                        // finished but with the wrong content: its truck keeps its boxes (never
                        // reassigned); we set it aside and Muse picks what is still missing.
                        Incident("order #" + f.id + " finished incomplete: truck kept, new strategy");
                        finishExcluded.Add(f.id);
                        if (excludedSince < 0f) excludedSince = Time.time;
                        finishingId = -1;
                        continue;
                    }
                    if (f == null || f.status == "loaded")
                    {
                        Incident("order #" + finishingId + " finished for the urgent order: resources released");
                        MuseTrace.Log("URGENT", "FINISHING_FOR_URGENT #" + finishingId + " finished");
                        finishingId = -1;
                        progressAt = Time.time;
                        // The pair is not there yet (door moving, truck not empty):
                        // the loop checks it again; if only one of the two is missing, Muse will choose again.
                        yield return new WaitForSeconds(.5f);
                        continue;
                    }
                    int progress = StagedTotal(f) + f.loaded_a + f.loaded_b + f.loaded_c + f.status.GetHashCode() % 97;
                    if (progress != lastProgress) { lastProgress = progress; progressAt = Time.time; }
                    if (Time.time - progressAt > FinishWatchdog)
                    {
                        Incident("order #" + f.id + " no progress for " + FinishWatchdog.ToString("F0") +
                                 " s: dropped as the order to finish, paused");
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

                // ---- choose the order to finish (Muse decision)
                if (excludedSince > 0f && Time.time - excludedSince > 120f) { finishExcluded.Clear(); excludedSince = -1f; }
                FtfContext ctx = BuildCapacityContext(order, need);
                int usable = 0;
                foreach (FtfCandidate c in ctx.candidates) if (c.can_finish && Covers(c, ctx)) usable++;
                if (usable == 0)
                {
                    CapacityStrategy = "Waiting for the first free bench + empty dock";
                    CapacityNote = "no normal order can be finished to free a bay";
                    UrgentStatus = "URGENT #" + order.id + ": priority wait — no normal order can free a dock";
                    MuseTrace.Log("URGENT", "no FINISH TO FREE candidate (" + ctx.candidates.Length + " order(s) examined)");
                    yield return WaitOrCapacity(CapacityRecheck, need); // spaced re-evaluation, no looping Meta calls
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
                    // No Muse strategy for now: the urgent order simply WAITS for the first
                    // free slot (normal orders keep running and free their docks). This
                    // is neither a refusal nor a decision: nothing is written in the decision
                    // history, the reason stays visible on the card and in the incidents.
                    string err = r != null && !string.IsNullOrEmpty(r.error) ? r.error : "no answer";
                    bool noRoute = err.Contains("not found");
                    metaFailStreak++;
                    float delay = noRoute ? 30f : RetryDelay();
                    CapacityStrategy = "Waiting for the first free bench + empty dock";
                    CapacityNote = noRoute
                        ? "Muse strategy unavailable: proxy without /capacity — restart meta_proxy.py"
                        : "Muse strategy unavailable (" + err + ") — new request in " + delay.ToString("F0") + " s";
                    UrgentStatus = "URGENT #" + order.id + ": waiting for a slot (bench + empty truck)";
                    if (!noRoute || metaFailStreak == 1)
                        Incident("URGENT #" + order.id + " waiting for a slot — Muse strategy unavailable (" + err + ")");
                    yield return WaitOrCapacity(delay, need);
                    continue;
                }
                CapacityNote = "";
                metaFailStreak = 0;
                modelUsed = r.model;
                lastLatencyMs = r.latency_ms;
                // Meanwhile an order may have finished by itself: capacity is there, the
                // strategy no longer has a purpose (no useless suspension).
                if (HasUrgentCapacity(need, out _, out _))
                {
                    record.Action = r.result.action;
                    record.Reason = r.result.reason;
                    record.Accepted = true;
                    record.AppliedResult = "capacity freed before the strategy was applied";
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
                    record.AppliedResult = "waiting: " + r.result.reason;
                    Record(record);
                    CapacityStrategy = "Muse: wait — " + r.result.reason;
                    yield return WaitOrCapacity(CapacityRecheck, need);
                    continue;
                }
                FtfCandidate chosen = null;
                foreach (FtfCandidate c in ctx.candidates) if (c.order_id == r.result.order_id) chosen = c;
                string why = chosen == null ? "order #" + r.result.order_id + " not among the candidates"
                           : !chosen.can_finish ? "order #" + chosen.order_id + " cannot finish (stock)"
                           : !Covers(chosen, ctx) ? "finishing it does not free the missing bench and truck"
                           : null;
                if (why != null)
                {
                    Reject(record, "FINISH TO FREE choice refused: " + why);
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
                record.PlanSummary = ""; // not a pick plan: the HUD shows it as « FINISH #n »
                record.AppliedResult = "FINISH TO FREE: finish #" + chosen.order_id + " (frees " +
                                       chosen.bench_id + " / " + chosen.truck_id + ")";
                Record(record);

                finishingId = chosen.order_id;
                FreeingBenchId = chosen.bench_id ?? "";
                FreeingTruckId = chosen.truck_id ?? "";
                CapacityEtaS = r.result.expected_free_s > 0f ? r.result.expected_free_s : chosen.eta_free_s;
                CapacityStrategy = "Finish Order #" + finishingId;
                UrgentStatus = "URGENT #" + order.id + ": Muse has #" + finishingId + " finished (frees " +
                               FreeingBenchId + " / " + FreeingTruckId + ")";
                Incident("Muse — FINISH TO FREE: finish #" + finishingId + " to free " + FreeingBenchId + " / " +
                         FreeingTruckId + " (~" + CapacityEtaS.ToString("F0") + " s) — urgent #" + order.id + " waiting");
                MuseTrace.Log("URGENT", "FINISHING_FOR_URGENT #" + finishingId + " chosen by Muse: " + r.result.reason);

                // The other normal orders are suspended; the chosen one continues.
                yield return SuspendActiveOrders(finishingId);
                Stage = UrgentStage.WaitingForCapacity;
                WhOrderState sel = FindLocal(finishingId);
                if (sel != null && pausedIds.Contains(sel.id)) ResumeForFinish(sel);
                progressAt = Time.time;
                lastProgress = -1;
            }
        }

        /// <summary>Wait that stops as soon as a bench + empty truck becomes free.</summary>
        private IEnumerator WaitOrCapacity(float seconds, int need)
        {
            float until = Time.time + seconds;
            while (Time.time < until && !HasUrgentCapacity(need, out _, out _)) yield return new WaitForSeconds(.5f);
        }

        /// <summary>What the urgent order is missing, in plain words for the HUD.</summary>
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

        /// <summary>Finishing c provides what is missing (bench and/or compatible empty truck).</summary>
        private static bool Covers(FtfCandidate c, FtfContext ctx)
        {
            bool needBench = ctx.free_benches == null || ctx.free_benches.Length == 0;
            bool needTruck = ctx.empty_trucks == null || ctx.empty_trucks.Length == 0;
            if (needBench && !c.frees_bench) return false;
            if (needTruck && (!c.frees_truck || c.truck_capacity < ctx.urgent_total)) return false;
            return true;
        }

        /// <summary>
        /// Data sent to Muse: real progress of each normal order holding a bench
        /// and/or a truck (active or paused), what is left to pick / load, released resources, and
        /// an ETA computed on the scene's real distances (robot speed, handling time).
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

                // enough free stock to finish the pick (per type)
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

                // ETA on real distances
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
            urgentPending = true; // no other Meta call during this choice
            Calls++;
            Debug.Log("[Muse] request " + request.request_id + " (urgent_capacity: which order to finish for urgent #" +
                      urgent.id + ", " + ctx.candidates.Length + " candidate(s))");
            CapacityResponse response = null;
            yield return capacityClient.Ask(request, x => response = x);
            urgentPending = false;
            nextAllowedCall = Time.time + CallCooldown;
            done(response);
        }

        /// <summary>
        /// The chosen order was paused: resume IT ALONE (direct shipping if complete,
        /// original plan if still valid, otherwise a targeted Muse decision « finish_for_urgent »).
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
                Incident("FINISH TO FREE #" + order.id + ": already complete in " + order.truck_id + " — shipping");
                running.Add(StartCoroutine(ShipCompleteOrder(order, fullBay)));
                return;
            }
            var snap = Snapshot("finish_for_urgent", false);
            MusePlan reuse = ResumePlanner.TryReuse(oldPlan, order, snap);
            if (reuse != null && (reuse.picks.Length == 0 ||
                                  (PlanValidator.FirstError(reuse, snap) == null && CheckRobotRoles(reuse) == null)))
            {
                Incident("FINISH TO FREE #" + order.id + ": resuming on its original plan");
                StartExecution(reuse);
                return;
            }
            Incident("FINISH TO FREE #" + order.id + ": original plan not feasible — targeted Muse decision");
            StartCoroutine(RequestDecisionWhenFree("finish_for_urgent"));
        }

        private const double ReplayLead = .05;
        private Coroutine replayDeferred;
        private bool replayForce;

        /// <summary>RECORDED DEMO: the recorded moment is reached and the order of the recorded plan exists.</summary>
        private bool RecordedCallDue(double at, int orderId) =>
            RecordedDemoRuntime.Elapsed >= at - ReplayLead && (orderId <= 0 || FindLocal(orderId) != null);

        /// <summary>RECORDED DEMO: a decision asked too early is sent again at the recorded moment.</summary>
        private IEnumerator RequestAtRecordedTime(double at, int orderId)
        {
            while (RecordedDemoRuntime.Replaying && !RecordedCallDue(at, orderId) && RecordedDemoRuntime.Elapsed < at + 20.0) yield return null;
            float deadline = Time.time + PendingTimeout + 5f;
            while ((pending || urgentPending) && Time.time < deadline) yield return null;
            replayDeferred = null;
            // already asked by the game in the meantime: nothing to send
            if (!RecordedDemoRuntime.Replaying || !RecordedDemoRuntime.NextRecorded("/warehouse", out double next, out string trig, out _) ||
                System.Math.Abs(next - at) > 1e-6) yield break;
            replayForce = true;
            try { RequestDecision(trig); }
            finally { replayForce = false; }
        }

        /// <summary>A decision call is sent as soon as no Meta call is in flight.</summary>
        private IEnumerator RequestDecisionWhenFree(string trigger)
        {
            float deadline = Time.time + PendingTimeout + 5f;
            while ((pending || urgentPending) && Time.time < deadline) yield return null;
            RequestDecision(trigger);
        }

        /// <summary>
        /// Safe preemption: each active normal order switches to SUSPENDING.
        /// R1 finishes its indivisible operation (the box in hand goes to the bench),
        /// then R2 finishes its committed load and leaves the dock. The execution
        /// coroutines park themselves through ParkSuspended (boxes, reservations
        /// and counters saved — nothing is teleported or destroyed).
        /// </summary>
        private IEnumerator SuspendActiveOrders(int keepId = -1)
        {
            suspendInProgress = true;
            Stage = UrgentStage.Suspending;
            foreach (WhOrderState o in orders)
            {
                if (o == null || o.urgent || !activeOrderIds.Contains(o.id) || o.status == "loaded")
                    continue;
                // FINISH TO FREE: the order chosen by Muse keeps going until the end.
                if (o.id == keepId) continue;
                // All its boxes are already in the truck: it no longer needs the
                // robots, it closes and leaves — pausing it blocked its door
                // (and the order) until the end of the urgent one.
                if (FullyLoaded(o, out _)) continue;
                o.status = "suspending";
                suspendingIds.Add(o.id);
            }
            stateVersion++;
            if (suspendingIds.Count > 0)
            {
                Incident("SUSPENSION of " + suspendingIds.Count + " order(s): safe stop of the robots");
                // Both robots are suspended IMMEDIATELY: each one finishes its
                // indivisible operation in parallel (R1 drops at the bench, R2 finishes the
                // committed box then leaves the dock). Only wait for R1: the urgent order can
                // start its pick as soon as R1 is safely stopped. R2's return
                // and the closing of the normal doors happen in the background —
                // R2's urgent mission will naturally wait for it (LoadWhenFree).
                // R1 picking for the kept order (FINISH TO FREE) is not stopped.
                bool r1Keep = Robot1 != null && Robot1.LastReport != null && Robot1.LastReport.OrderId == keepId;
                bool r1Suspended = Robot1 != null && Robot1.MissionActive && !r1Keep;
                if (r1Suspended) Robot1.SuspendMission();
                // R2 finishing (leaving the trailer) an order that is already complete is
                // not suspended: its mission closes normally and the order leaves.
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
                    Incident("R1 forced to stop after " + SuspendTimeout.ToString("F0") + "s");
                    deadline = Time.time + 6f;
                    while (Robot1.MissionActive && Time.time < deadline) yield return null;
                }
                // The Execute coroutines involved park through ParkSuspended; final safety net.
                deadline = Time.time + 8f;
                while (suspendingIds.Count > 0 && Time.time < deadline) yield return null;
            }
            suspendInProgress = false;
        }

        /// <summary>
        /// Pauses ONE order (called from its Execute coroutine, or as a
        /// last resort by the orchestrator): saves the real state — staged
        /// boxes, truck content, reservations — then releases the robots.
        /// </summary>
        private IEnumerator ParkSuspended(WhOrderState order, MusePlan plan,
                                          TruckBay bay, ShelfStorage bench, Coroutine loadCo)
        {
            // NEVER kill a running R2 mission: the coroutine holds the
            // robot's physical steering. We only stop the waiting queue
            // (mission not started yet); a live mission ends safely.
            // Closing the door is DEFERRED (CloseNormalDockWhenSafe):
            // waiting here for R2 to come back would delay the start of the urgent order —
            // the order is paused right away, the door will close as soon as
            // R2 has finished the committed box and left the dock.
            bool r2MissionLive = Robot2 != null && Robot2.MissionActive &&
                                 Robot2.DirectedOrderId == order.id;
            if (r2MissionLive)
            {
                Robot2.SuspendMission(); // finish the box in hand then leave the dock
                StartCoroutine(CloseNormalDockWhenSafe(order, bay));
            }
            else if (loadCo != null)
            {
                StopCoroutine(loadCo);
                running.Remove(loadCo);
                // Queue killed between r2Busy=true and the launch: release the lock,
                // and decrement the signalled queue (otherwise R2 would never go back
                // to base again — QueuedLoads is only decremented if the
                // coroutine reaches the end of its wait).
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
                // The deferred closing will recompute and keep the reservation if
                // boxes remain inside; if R2 had not committed anything we can
                // close right away (r2MissionLive false => no box in flight).
                if (!r2MissionLive)
                {
                    int loaded = inTruck[0] + inTruck[1] + inTruck[2];
                    bay.CloseDoor(false, keepReservation: loaded > 0);
                    if (loaded <= 0) order.truck_id = null;
                }
            }
            ReleasePallets(plan); // the stock becomes available again for the urgent order
            if (StagedTotal(order) <= 0 && !string.IsNullOrEmpty(order.bench_id))
            {
                World().ReleaseBench(order.bench_id, order.id);
                order.bench_id = null;
            }
            // EndExecution BEFORE registering the pause: it removes the id from
            // pausedIds (end-of-order cleanup) — called afterwards, it emptied
            // the list and ResumePaused never resumed anything.
            EndExecution(order);
            suspendingIds.Remove(order.id);
            pausedIds.Add(order.id);
            pausedPlans[order.id] = plan;
            order.status = "paused";
            MuseTrace.Log("SUSPEND", "order #" + order.id + " registered as paused (paused=" +
                          pausedIds.Count + ")");
            stateVersion++;
            Incident("order #" + order.id + " paused: staged " + StagedTotal(order) +
                     ", loaded " + (order.loaded_a + order.loaded_b + order.loaded_c));
            yield return null;
        }

        /// <summary>
        /// Deferred closing of the truck of an order suspended while R2
        /// was still finishing a load for it: wait for the safe end of that
        /// mission (committed box done + left the dock), then close the door.
        /// Golden rule: finish the committed load, get R2 out of the dock, THEN close.
        /// </summary>
        private IEnumerator CloseNormalDockWhenSafe(WhOrderState order, TruckBay bay)
        {
            if (bay == null) yield break;
            float deadline = Time.time + SuspendTimeout;
            while (Robot2 != null && Robot2.MissionActive &&
                   Robot2.DirectedOrderId == order.id && Time.time < deadline)
                yield return null;
            MuseTrace.Log("SUSPEND", "order #" + order.id + ": R2 mission finished=" +
                          !(Robot2 != null && Robot2.MissionActive &&
                            Robot2.DirectedOrderId == order.id) + " before closing the door");
            // Meanwhile the order may have been resumed: its new plan handles
            // the door itself — do not close the truck under its nose.
            if (order.status != "paused") yield break;
            // The bay may have gone to the urgent order: do not steal its reservation.
            if (bay.ReservedOrderId != order.id && bay.ReservedOrderId >= 0) yield break;
            int[] inTruck = bay.CountByTypeForOrder(order.id);
            order.loaded_a = inTruck[0];
            order.loaded_b = inTruck[1];
            order.loaded_c = inTruck[2];
            int loaded = inTruck[0] + inTruck[1] + inTruck[2];
            UpdateStaged(order); // the committed box may have been put back on the bench
            // Door closed BEFORE releasing the dock; reservation kept if
            // boxes of the order remain inside (never moved).
            bay.CloseDoor(false, keepReservation: loaded > 0 && bay.ReservedOrderId == order.id);
            if (loaded <= 0 && !string.IsNullOrEmpty(order.truck_id) &&
                World() != null && World().Truck(order.truck_id) == bay)
                order.truck_id = null;
            stateVersion++;
        }

        /// <summary>
        /// Exact resume after the urgent order is delivered: stocks and
        /// reservations revalidated on the FRESH state, original plan reused if still
        /// feasible, otherwise a new plan request to Muse (state set back to pending).
        /// </summary>
        private void ResumePaused()
        {
            if (pausedIds.Count == 0) return;
            // 1) Re-align with the physical truth and switch back to plannable BEFORE
            //    freezing the snapshot (otherwise the validator would see orders
            //    still "paused" and would refuse any resume plan).
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
                order.status = "pending"; // plannable again (validator + queue)
                resumable.Add(order);
                resumedIds.Add(order.id); // resume tracking for the demo stage
            }
            // Resume priority: the most ADVANCED order resumes first
            // (boxes already staged/loaded = committed work to preserve), then
            // the oldest one on a tie. Without this sort, an order never
            // started could overtake a half-loaded order.
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
                    Incident("resume #" + order.id + ": all boxes are already in " + order.truck_id +
                             " — closing and shipping");
                    running.Add(StartCoroutine(ShipCompleteOrder(order, fullBay)));
                    continue;
                }
                MusePlan oldPlan = oldPlans.TryGetValue(order.id, out MusePlan op) ? op : null;
                MusePlan reuse = ResumePlanner.TryReuse(oldPlan, order, snap);
                if (reuse != null && reuse.picks.Length == 0)
                {
                    Incident("resume #" + order.id + ": finishing the load without a new pick");
                    StartExecution(reuse);
                }
                else if (reuse != null && PlanValidator.FirstError(reuse, snap) == null &&
                         CheckRobotRoles(reuse) == null)
                {
                    Incident("resume #" + order.id + ": original plan reused (no new call)");
                    StartExecution(reuse);
                }
                else
                {
                    Incident("resume #" + order.id +
                             ": old plan not feasible, new Muse decision");
                }
            }
            stateVersion++;
            if (NextPending() != null) RequestDecision("resume_after_urgent");
        }

        /// <summary>The order has all its content in its truck (physical count).</summary>
        private bool FullyLoaded(WhOrderState o, out TruckBay bay)
        {
            bay = o != null && !string.IsNullOrEmpty(o.truck_id) && World() != null ? World().Truck(o.truck_id) : null;
            if (bay == null || o.a + o.b + o.c <= 0) return false;
            int[] c = bay.CountByTypeForOrder(o.id);
            return c[0] >= o.a && c[1] >= o.b && c[2] >= o.c;
        }

        /// <summary>
        /// Order already fully loaded but door closed again (paused at 100 %):
        /// reopen, check the real content, close again -> shipping. Without this it
        /// went back to "pending" and Muse could no longer plan anything for it
        /// (validator: « already complete ») — it stayed stuck forever.
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

        /// <summary>Physical constraint: only R1 picks, only R2 loads.</summary>
        private string CheckRobotRoles(MusePlan plan)
        {
            if (!string.IsNullOrEmpty(plan.robot_pick) && plan.robot_pick != "R1")
                return "robot_pick=" + plan.robot_pick + ": only R1 picks from the pallets";
            if (!string.IsNullOrEmpty(plan.robot_load) && plan.robot_load != "R2")
                return "robot_load=" + plan.robot_load + ": only R2 loads the trucks";
            return null;
        }

        private void Reject(MuseDecisionRecord record, string error)
        {
            record.RejectReason = error;
            record.Accepted = false;
            Record(record);
            lastValidation = "refused: " + error;
            lastRejection = error;
            consecutiveRejects++;
            incidents.Add("plan refused: " + error);
            if (incidents.Count > 8) incidents.RemoveAt(0);
            // The cause + a fresh state go back to Muse on the next request.
            nextAllowedCall = Time.time + (consecutiveRejects >= MaxConsecutiveRejects
                ? WaitRetryDelay * 2f : CallCooldown);
            Debug.LogWarning("[Muse] plan refused: " + error);
            MaybeRetryUrgent(record.OrderId);
        }

        /// <summary>
        /// The auto loop is blocked while an urgent order exists: any
        /// non-executable result concerning it (refusal, wait, no_plan, defer) must explicitly
        /// trigger a new decision, otherwise it would stay in the queue forever.
        /// After 4 failures it becomes a normal order again and the paused orders resume.
        /// </summary>
        private void MaybeRetryUrgent(int orderId)
        {
            if (urgentId < 0 || orderId != urgentId) return;
            urgentReplanFails++;
            if (urgentReplanFails >= 4)
            {
                DemoteUrgent("not plannable after 4 attempts");
                return;
            }
            UrgentStatus = "URGENT #" + urgentId + ": new Muse attempt";
            StartCoroutine(RequestDecisionWhenFree("urgent_replan"));
        }

        /// <summary>The urgent order can no longer be planned: it becomes a
        /// normal order again and the paused orders resume.</summary>
        private void DemoteUrgent(string why)
        {
            WhOrderState urgent = FindLocal(urgentId);
            Incident("urgent #" + urgentId + " " + why +
                     ": back to normal, resuming the paused orders");
            if (urgent != null) urgent.urgent = false;
            urgentId = -1;
            urgentDelivered = false;
            urgentWaiting = false;
            urgentPickDone = false;
            UrgentError = "urgent order not delivered: " + why;
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
            Debug.Log("[Muse] " + (record.Accepted ? "ACCEPTED" : "REFUSED") +
                      " [" + record.Model + ", " + record.LatencyMs + " ms] " +
                      (record.Action ?? "-") +
                      (record.Accepted ? " " + record.AppliedResult : " — " + record.RejectReason));
            MuseTrace.Log("MUSE", record.RequestId + " trigger=" + record.Trigger + " mode=" + record.Mode +
                          " model=" + record.Model + " " + record.LatencyMs + " ms action=" + record.Action +
                          " order=#" + record.OrderId + " v" + record.StateVersion +
                          (record.Accepted ? " ACCEPTED" : " REFUSED: " + record.RejectReason) +
                          (string.IsNullOrEmpty(record.PlanSummary) ? "" : " plan=" + record.PlanSummary) +
                          (string.IsNullOrEmpty(record.Reason) ? "" : " reason=\"" + record.Reason + "\""));
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

        // ------------------------------------------------------------------ execution

        private void StartExecution(MusePlan plan)
        {
            // Each accepted order runs in its own coroutine: the
            // executions overlap, only the robots serve in sequence.
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
            // ---- reservations specific to this order
            ShelfStorage bench = World().Bench(plan.bench_id);
            TruckBay bay = World().Truck(plan.truck_id);
            World().ReserveBench(plan.bench_id, plan.order_id);
            // Only the pick pallets are reserved. The fallback pallets
            // are NOT: otherwise one order monopolises the whole stock
            // (Muse often lists 4-5 of them) and blocks the other orders and
            // the urgent one. Boxes stay protected individually (orderId +
            // CargoRegistry) if a fallback is actually used.
            foreach (MusePlanPick p in plan.picks ?? new MusePlanPick[0])
                if (p != null) World().ReservePallet(p.pallet_id, plan.order_id);
            MuseTrace.Log("EXEC", "start " + (order.urgent ? "URGENT " : "") + Describe(plan) +
                          "  fallback=[" + string.Join(",", plan.fallback_pallets ?? new string[0]) + "]");
            order.status = "queued"; // resources reserved, missions not launched yet
            order.bench_id = plan.bench_id;
            order.truck_id = plan.truck_id;
            stateVersion++;
            highlightedOrder = order.id;
            ResourceHighlighter.Show(this, plan);

            // ---- preemption: a normal execution accepted during the suspension
            // OR during the life of the urgent order parks immediately
            // (reservations released by ParkSuspended).
            if ((suspendInProgress || (urgentId >= 0 && !urgentPickDone) || urgentWaiting) &&
                !order.urgent && order.id != finishingId)
            {
                order.status = "suspending";
                suspendingIds.Add(order.id);
                yield return ParkSuspended(order, plan, bay, bench, null);
                yield break;
            }

            // ---- door of the truck chosen by Muse (each truck has its own door)
            if (order.urgent) Stage = UrgentStage.UrgentActive;
            if (bay != null) bay.OpenDoor(plan.order_id);

            if (!plan.start_immediately && plan.start_delay_s > 0f)
                yield return new WaitForSeconds(plan.start_delay_s);

            // ---- R1 pick steps
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

            // "Load only" resume: everything is already on the bench or in the truck,
            // R1 has nothing to pick — no need to require it or wait for it.
            if (bench == null || (legs.Count > 0 && (Robot1 == null || !Robot1.Running)))
            {
                Incident((bench == null ? "bench" : "R1") + " unavailable: mission impossible");
                order.status = "pending";
                if (bay != null) bay.CloseDoor(false);
                yield return FinishOrder(order, bay, plan, false);
                yield break;
            }

            // ---- R2 loads IN PARALLEL: it takes each box of the order as soon as
            // R1 drops it on the bench. If it is still finishing the previous order,
            // its mission starts automatically when it comes back.
            int[] prevLoaded = bay != null ? bay.CountByTypeForOrder(order.id) : new int[3];
            int expected = (order.a - prevLoaded[0]) + (order.b - prevLoaded[1]) + (order.c - prevLoaded[2]);
            if (expected <= 0 || Robot2 == null || !Robot2.Running || bay == null)
            {
                Incident("R2 or truck unavailable for order #" + order.id);
                order.status = "pending";
                if (bay != null) bay.CloseDoor(false);
                yield return FinishOrder(order, bay, plan, false);
                yield break;
            }
            bool loadDone = false;
            Coroutine loadCo = StartCoroutine(LoadWhenFree(bay, bench, order, expected,
                r => { loadReport = r; loadDone = true; }));
            running.Add(loadCo);

            // ---- R1: it first finishes picking for another order if needed.
            // The signalled queue avoids the return to base: R1 chains directly on this one.
            // Without a pick step (load-only resume): no wait.
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
                        // Called BEFORE R1's return-to-base decision: the resume
                        // queues the next pick for it -> it goes there directly.
                        if (order.urgent) OnUrgentPickFinished(order, plan, r);
                    });
                while (!pickDone) yield return null; // safety if the coroutine is interrupted
                Robot1.RouteChanged -= OnRouteChanged;
                Robot1.LegFinished -= OnLegFinished;
                r1Busy = false;

                // ---- suspension requested during the pick: safe pause
                if (suspendingIds.Contains(order.id))
                {
                    yield return ParkSuspended(order, plan, bay, bench, loadCo);
                    yield break;
                }

                // ---- inventory after the pick
                UpdateStaged(order);
                pickPlanText = Summarize(pickReport);
                int missingTotal = pickReport != null
                    ? pickReport.Missing[0] + pickReport.Missing[1] + pickReport.Missing[2] : 0;

                if (missingTotal > 0)
                {
                    // No approved fallback pallet could fill the gap: new decision
                    // from Muse with the updated stocks. R2 empties the bench first: the
                    // boxes actually dropped stay in the truck that KEEPS the
                    // order (never declared complete while the content is wrong).
                    Incident("stock-out on order #" + order.id + ": missing " +
                             MissingText(pickReport.Missing) + " — new decision requested");
                    // R2 loads in parallel: if it has already started THIS order it empties
                    // the bench; otherwise its waiting queue is cancelled (mission never started).
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
                    lastRejection = "insufficient stock: missing " + MissingText(pickReport.Missing);
                    if (bay != null)
                    {
                        int[] inTruck = bay.CountByTypeForOrder(order.id);
                        order.loaded_a = inTruck[0]; order.loaded_b = inTruck[1]; order.loaded_c = inTruck[2];
                        // Boxes already loaded: same truck imposed on the next plan; otherwise released.
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
                // Nothing to pick: the order goes straight to loading.
                UpdateStaged(order);
            }

            // ---- R2 keeps going up to the expected quantity (loading already started).
            phase = Phase.Loading;
            order.status = "loading";
            stateVersion++;
            while (!loadDone && !suspendingIds.Contains(order.id)) yield return null;
            if (suspendingIds.Contains(order.id))
            {
                yield return ParkSuspended(order, plan, bay, bench, loadCo);
                yield break;
            }

            // ---- check of the delivered content then closing
            phase = Phase.Verifying;
            yield return VerifyAndClose(order, bay, plan, prevLoaded, expected, loadReport);
        }

        /// <summary>
        /// Urgent pick finished (all boxes on the bench): R1 does not wait for
        /// R2 to finish loading — the paused orders resume right away.
        /// Their loads are queued behind the urgent one (LoadWhenFree).
        /// </summary>
        private void OnUrgentPickFinished(WhOrderState order, MusePlan plan,
                                          WarehouseRobotAutopilot.PickMissionReport r)
        {
            if (r == null || r.Aborted || urgentId != order.id || pausedIds.Count == 0) return;
            if (r.Missing[0] + r.Missing[1] + r.Missing[2] > 0) return; // stock-out: normal flow
            urgentPickDone = true;
            ReleasePallets(plan); // the urgent order's pallets can be picked again
            stateVersion++;
            UrgentStatus = "URGENT #" + order.id + ": pick finished, R2 loading — " +
                           "R1 resumes the paused orders";
            Incident("urgent #" + order.id + " dropped on the bench: R1 resumes the paused orders");
            MuseTrace.Log("URGENT", "urgent pick #" + order.id + " finished: early R1 resume");
            ResumePaused();
        }

        /// <summary>The load mission waits for R2 to finish the previous one.</summary>
        private IEnumerator LoadWhenFree(TruckBay bay, ShelfStorage bench, WhOrderState order,
            int expected, System.Action<TruckLoaderAgent.LoadMissionReport> done)
        {
            // The signalled queue avoids the return to base: R2 chains directly on the
            // next load (rest only when there is no work left).
            Robot2.QueuedLoads++;
            // Explicit R2 queue: when several loads are waiting, R2 no longer goes to
            // the first coroutine that wakes up (arbitrary Unity order —
            // #2 overtook the half-loaded #1), but to IsR2Turn.
            r2Waiters[order.id] = new R2Waiter { Ticket = ++r2TicketSeq, Bench = bench, Seen = Time.frameCount };
            // A live mission can last several minutes (one box = ~20 s):
            // NEVER force-stop it — that dropped a box on the floor and
            // corrupted the truck contents. The only recoverable case is the
            // dead lock: r2Busy taken while no mission is running.
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
                        Incident("R2 lock without a mission for 15 s: released for #" + order.id);
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
            // Only release if the lock is still ours (a forced stop or a
            // suspension may have reassigned it in the meantime).
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

        /// <summary>Loads waiting for R2 (order -> arrival rank).</summary>
        private readonly Dictionary<int, R2Waiter> r2Waiters = new Dictionary<int, R2Waiter>();
        private int r2TicketSeq;

        /// <summary>
        /// Deterministic R2 arbitration between the waiting loads:
        /// 1) the urgent order; 2) an order whose boxes are already waiting
        /// on its bench (otherwise R2 would stand in front of an empty bench while
        /// another order's ready boxes pile up); 3) the oldest
        /// request (FIFO). An order resumed after the urgent one therefore goes
        /// before an order that arrived later.
        /// </summary>
        /// <summary>R1 serves the urgent order / the order to finish first, then in decision order.</summary>
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
                // orphan entry (stopped coroutine) or order that is parking: ignored
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

        /// <summary>Waits until R2 has loaded all the order's boxes dropped on the bench.</summary>
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
            Incident("bench unloading incomplete for order #" + order.id);
        }

        private IEnumerator VerifyAndClose(WhOrderState order, TruckBay bay, MusePlan plan,
            int[] prevLoaded, int expected, TruckLoaderAgent.LoadMissionReport loadReport)
        {
            int[] byType = bay != null ? bay.CountByTypeForOrder(order.id) : new int[3];
            int delivered = byType[0] - prevLoaded[0] + byType[1] - prevLoaded[1] + byType[2] - prevLoaded[2];
            bool deliveredExact = loadReport != null && !loadReport.Aborted && delivered == expected;
            // Physical truth: the order is only complete if the truck contains
            // exactly the ordered content — never on a partial delivery.
            bool complete = byType[0] == order.a && byType[1] == order.b && byType[2] == order.c;
            if (!deliveredExact || !complete)
                Incident("wrong truck content for order #" + order.id + ": " +
                         byType[0] + "A/" + byType[1] + "B/" + byType[2] + "C in " + order.truck_id +
                         ", expected " + order.a + "A/" + order.b + "B/" + order.c + "C");

            phase = Phase.Closing;
            if (bay != null)
            {
                bay.CloseDoor(complete);
                // The door only goes down once R2 is out (TruckBay.LoaderOutside).
                float deadline = Time.time + 20f;
                while (Time.time < deadline &&
                       bay.Door != TruckBay.TruckDoorState.Loaded &&
                       bay.Door != TruckBay.TruckDoorState.Closed)
                    yield return null;
            }

            // Delivered boxes are counted (accumulated in the truck), complete or not.
            order.loaded_a = byType[0]; order.loaded_b = byType[1]; order.loaded_c = byType[2];
            UpdateStaged(order);
            if (complete)
            {
                order.status = "loaded";
                Debug.Log("[Muse] order #" + order.id + " DELIVERED in " + order.truck_id +
                          " — door closed");
            }
            else
            {
                order.status = "waiting"; // never "loaded" on a partial/wrong delivery
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
                    LastUrgentOutcome = "urgent #" + order.id + " delivered";
                    ClearCapacityState();
                    UrgentStatus = "URGENT #" + order.id +
                                   " delivered — resuming the paused orders";
                    Incident("urgent #" + order.id + " delivered: resuming the orders");
                    ResumePaused();
                    Stage = resumedIds.Count > 0 ? UrgentStage.Resuming : UrgentStage.Completed;
                }
                else
                {
                    // The urgent order keeps priority as long as it is not delivered.
                    urgentPickDone = false;
                    UrgentStatus = "URGENT #" + order.id + " not delivered — retrying";
                    if (order.status == "pending" || order.status == "waiting")
                        yield return RequestDecisionWhenFree("urgent_retry");
                }
            }
            yield return null;
        }

        /// <summary>End of the execution of ONE order (the others keep going).</summary>
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

        /// <summary>Releases the plan's logical reservations (bench, pallets, truck).</summary>
        private void ReleasePlanResources(MusePlan plan)
        {
            if (plan == null) return;
            World().ReleaseBench(plan.bench_id, plan.order_id);
            ReleasePallets(plan);
            TruckBay bay = World().Truck(plan.truck_id);
            if (bay != null) bay.ReleaseReservation();
        }

        /// <summary>Releases only the plan's pallet reservations.</summary>
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

        // ------------------------------------------------------------------ inventory tracking

        /// <summary>
        /// Real-time counters of the active orders: 'staged' (orderId boxes on the
        /// benches) and 'loaded' (orderId boxes in their truck) — the HUD and the
        /// snapshots sent to Muse reflect the physical state at ~1 Hz.
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

        /// <summary>Recounts the order's boxes sitting on ALL benches.</summary>
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

        /// <summary>Per-type counters of the order's boxes on THIS specific bench.</summary>
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
                sb.Append("  MISSING ").Append(MissingText(report.Missing));
            return sb.ToString();
        }

        private void OnLegFinished(WarehouseRobotAutopilot.PickLeg leg)
        {
            UpdatePickPlanText(null);
        }

        private void OnRouteChanged(string change)
        {
            Incident("route changed: " + change);
            UpdatePickPlanText(pickReport != null ? null : new List<WarehouseRobotAutopilot.PickLeg>());
            pickPlanText = pickPlanText + "  -> " + change;
        }

        private void Incident(string message)
        {
            incidents.Add(message);
            if (incidents.Count > 8) incidents.RemoveAt(0);
            Debug.LogWarning("[Muse] incident: " + message);
        }

        // ------------------------------------------------------------------ world reading (HUD/tests)

        private WhOrderState FindLocal(int id)
        {
            foreach (WhOrderState o in orders) if (o.id == id) return o;
            return null;
        }

        public string DoorStateOf(string truckId)
        {
            TruckBay bay = World() != null ? World().Truck(truckId) : null;
            return bay != null ? bay.Door.ToString() : "unknown";
        }

        public string RobotStatusOf(string robotId)
        {
            if (robotId == "R1") return Robot1 != null ? Robot1.Status : "absent";
            if (robotId == "R2") return Robot2 != null ? Robot2.Status : "absent";
            return "unknown";
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
                Incident("employee at " + best.ToString("F2") + " m from " + robotId);
            }
            return best;
        }

        // ------------------------------------------------------------------ driving (tests)

        public void SetMode(string mode)
        {
            switch ((mode ?? "").ToLowerInvariant())
            {
                case "live": Perception = PerceptionMode.Live; break;
                case "mock": Perception = PerceptionMode.Mock; break;
                case "offline": Perception = PerceptionMode.Offline; break;
            }
        }

        /// <summary>Test: the next mock request will return a deliberately invalid plan.</summary>
        public void InjectPlanError(string error) { injectedPlanError = error; }

        /// <summary>Current orchestrator phase (HUD diagnostics).</summary>
        public string CurrentPhase => phase.ToString();
    }
}
