using System.Collections;
using System.Reflection;
using UnityEngine;
using RoboFactory.Sim;

namespace RoboFactory.Logistics
{
    /// <summary>
    /// Robot 2: truck loader.
    ///
    /// Explicit state machine:
    ///   WaitingForBoxes -> ToBench -> Picking -> ToTruck -> Dropping -> (loops)
    ///                                        (WaitingForTruck if the bay is full)
    ///
    /// The robot asks the CargoRegistry for a job (box on a bench + free truck slot),
    /// reserves the box, drives with GridMover (built-in wait/retry) and uses
    /// RobotCargoInteractor for the pick/drop animations.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TruckLoaderAgent : MonoBehaviour
    {
        public enum R2State
        {
            Stopped,
            WaitingForBoxes,
            ToBench,
            Picking,
            ToTruck,
            Dropping,
            WaitingForTruck,
            ReturningHome
        }

        [Header("Robot")]
        public GridRobotController Controller;
        public Animator Animator;
        public RobotCargoInteractor Interactor;
        public GridMover Mover;

        [Header("Mission")]
        public bool AutoStart = true;
        [Tooltip("Rescan delay when no box is waiting on the benches.")]
        public float PollInterval = 1.5f;
        [Tooltip("Retry delay when the truck bay is full.")]
        public float FullRetryInterval = 2f;
        [Tooltip("When there is nothing left to do, the robot parks back at its initial position.")]
        public bool ReturnHomeWhenIdle = true;
        [Tooltip("Parking point (optional). Empty = robot position at start.")]
        public Transform HomeAnchor;
        [Tooltip("Distance to the parking point considered as 'at base'.")]
        public float HomeTolerance = .35f;
        public bool LogActions = true;

        public R2State State { get; private set; } = R2State.Stopped;
        public string Status { get; private set; } = "stopped";
        public int Delivered { get; private set; }
        public bool Running { get; private set; }
        /// <summary>Box currently carried by this robot (read after picking).</summary>
        public CargoBox Carried { get; private set; }

        private CargoRegistry registry;
        private Transform carryRoot;
        private CargoRegistry.DeliveryJob job;
        private bool hasJob;
        private Vector3 homePos;
        private Vector3 homeForward;

        private void Awake()
        {
            if (Controller == null) Controller = GetComponent<GridRobotController>();
            if (Animator == null) Animator = GetComponent<Animator>();
            if (Interactor == null) Interactor = GetComponent<RobotCargoInteractor>();
            if (Mover == null) Mover = GetComponent<GridMover>();
        }

        private void Start()
        {
            if (AutoStart) Begin();
        }

        public void Begin()
        {
            if (Running) return;
            if (Mover == null)
            {
                Debug.LogError("[Loader:" + name + "] GridMover not found.");
                return;
            }
            if (Controller != null) Controller.controlsLocked = true;
            // Initial position = parking point (unless an explicit anchor is given).
            homePos = HomeAnchor != null ? HomeAnchor.position : transform.position;
            homeForward = HomeAnchor != null ? HomeAnchor.forward : transform.forward;
            Running = true;
            StartCoroutine(Run());
            Log("started");
        }

        public void Stop()
        {
            Running = false;
            if (Mover != null) Mover.StopNavigation();
            ReleaseJob();
            StopAllCoroutines();
            SetState(R2State.Stopped, "stopped");
        }

        // ------------------------------------------------------------------ FSM loop

        private IEnumerator Run()
        {
            yield return null; // let the controller snap onto the grid
            while (Running)
            {
                registry = CargoRegistry.Instance != null ? CargoRegistry.Instance
                                                          : FindAnyObjectByType<CargoRegistry>();
                if (registry == null)
                {
                    SetState(R2State.WaitingForBoxes, "waiting for the cargo registry");
                    yield return new WaitForSeconds(1f);
                    continue;
                }

                if (!registry.TryAcquireDelivery(this, transform.position, out job))
                {
                    // Nothing to do: the robot parks back at its initial position.
                    // The trip stops as soon as a box appears on a bench.
                    if (ReturnHomeWhenIdle && !NearHome())
                    {
                        SetState(R2State.ReturningHome, "no more work: returning to base");
                        yield return Mover.NavigateTo(homePos, homeForward,
                            () => registry != null && registry.OnBenchCount > 0);
                    }
                    SetState(R2State.WaitingForBoxes, NearHome()
                        ? "at base: no box on the benches"
                        : "no box on the benches (or truck full)");
                    yield return new WaitForSeconds(PollInterval);
                    continue;
                }
                hasJob = true;
                Log("job: " + job.Box.name + " on " + job.Zone.name + " -> truck");

                // ------ go to the bench
                SetState(R2State.ToBench, "heading to " + job.Zone.name);
                yield return Mover.NavigateTo(job.Zone.dockPoint.position, job.Zone.dockPoint.forward);
                if (Mover.LastResult != GridMover.NavResult.Arrived)
                {
                    FailJob("did not reach the bench");
                    continue;
                }

                // The box may have disappeared meanwhile (moved by hand, another agent).
                if (CargoRegistry.Classify(job.Box) != CargoStatus.OnBench)
                {
                    FailJob("the box is no longer on the bench");
                    continue;
                }

                // Stack safety: always take the top box. If the reserved box got buried
                // meanwhile, the reservation moves to the actual top box.
                CargoBox top = CargoRegistry.TopBox(job.Zone);
                if (top != null && top != job.Box && registry.TryReserve(top, this))
                {
                    Log("reserved " + job.Box.name + " is under " + top.name + ": taking the top one");
                    registry.Release(job.Box, this);
                    job.Box = top;
                }

                // ------ pick
                SetState(R2State.Picking, "picking " + job.Box.name);
                yield return Interact(true, job.Box, job.Zone);
                if (Carried != job.Box)
                {
                    FailJob("pick failed");
                    yield return new WaitForSeconds(.3f);
                    continue;
                }

                // ------ go to the truck (the robot is carrying: it waits as long as needed)
                // Dock = zone of the least filled column: spreads the load on the pallet.
                ShelfInteractionZone dockZone = job.Bay.ChooseDockForLoad(transform.position);
                Transform dockPoint = dockZone != null && dockZone.dockPoint != null
                    ? dockZone.dockPoint : job.Bay.DockPoint;
                if (dockPoint == null)
                {
                    FailJob("no truck dock");
                    continue;
                }
                SetState(R2State.ToTruck, "heading to truck (" + dockPoint.name + ")");
                yield return Mover.NavigateTo(dockPoint.position, dockPoint.forward);
                if (Mover.LastResult != GridMover.NavResult.Arrived)
                {
                    // Enabled=false only when stopped: should not happen during a mission.
                    FailJob("truck trip interrupted");
                    continue;
                }

                // ------ drop (wait if the bay filled up meanwhile)
                while (Running && job.Bay.FreeSlots <= 0)
                {
                    SetState(R2State.WaitingForTruck, "truck full: waiting for a slot");
                    yield return new WaitForSeconds(FullRetryInterval);
                }
                if (!Running) yield break;

                SetState(R2State.Dropping, "dropping in the truck");
                yield return Interact(false, Carried, job.Zone, job.Bay);
                if (Carried == null)
                {
                    Delivered++;
                    registry.Release(job.Box, this);
                    hasJob = false;
                    if (Animator != null) Animator.SetBool("HasBox", false);
                    Status = Delivered + " box(es) delivered to the truck";
                }
                else
                {
                    // Dropping failed while the box is still in hand: try a free slot,
                    // otherwise put it on the floor; never leave a robot frozen with
                    // a load it cannot drop.
                    if (job.Bay == null || !job.Bay.PlaceBox(Carried)) DropAtFeet(Carried);
                    Carried = null;
                    if (Animator != null) Animator.SetBool("HasBox", false);
                    FailJob("drop failed (box released)");
                }
            }
        }

        private bool NearHome()
        {
            return Vector3.Distance(transform.position, homePos) <= HomeTolerance;
        }

        /// <summary>Detaches the box and puts it on the floor in front of the robot (Loose status).</summary>
        private void DropAtFeet(CargoBox box)
        {
            if (box == null) return;
            box.isCarried = false;
            box.transform.SetParent(null, true);
            Vector3 pos = transform.position + transform.forward * .45f;
            box.transform.position = new Vector3(pos.x, transform.position.y, pos.z);
            Log("box put on the floor (fallback)");
        }

        private void FailJob(string reason)
        {
            Log("job aborted: " + reason);
            ReleaseJob();
        }

        private void ReleaseJob()
        {
            if (hasJob && registry != null && job.Box != null) registry.Release(job.Box, this);
            hasJob = false;
        }

        private void SetState(R2State state, string status)
        {
            if (State != state)
            {
                State = state;
                Log("state: " + state);
            }
            Status = status;
        }

        // ------------------------------------------------------------------ interaction

        /// <summary>
        /// Pick/drop through the robot's cargo interactor (trigger zones + animations).
        /// Manual fallback if it does not respond, like the autopilot.
        /// </summary>
        private IEnumerator Interact(bool load, CargoBox box, ShelfInteractionZone zone, TruckBay bay = null)
        {
            if (Interactor != null)
            {
                Interactor.SendMessage("TryInteract", SendMessageOptions.DontRequireReceiver);
                float deadline = Time.time + 3f;
                while (Time.time < deadline && Interactor.IsBusy) yield return null;
                while (Time.time < deadline && Interactor.HasBox != load) yield return null;
                if (Interactor.HasBox == load)
                {
                    Carried = load ? box : null;
                    if (!load && bay != null && box != null) bay.ValidateStored(box);
                    yield break;
                }
                Debug.LogWarning("[Loader:" + name + "] the cargo interactor did not respond, manual transfer.");
            }

            if (load)
            {
                int level = Mathf.Clamp(LevelOf(zone, box), 0, 2);
                PlayTrigger("Load_Level_" + level);
                yield return new WaitForSeconds(.45f);
                Attach(box, zone);
                yield return new WaitForSeconds(.25f);
            }
            else
            {
                PlayTrigger("Unload_Level_0");
                yield return new WaitForSeconds(.55f);
                bool placed = bay != null && bay.PlaceBox(box);
                if (placed) Carried = null;
                else Debug.LogWarning("[Loader:" + name + "] no free slot in the truck.");
                yield return new WaitForSeconds(.25f);
            }
        }

        private void Attach(CargoBox box, ShelfInteractionZone zone)
        {
            if (box == null) return;
            Transform anchor = AssetCarryRoot();
            if (anchor == null)
            {
                if (carryRoot == null)
                {
                    var root = new GameObject("Loader Carry Root");
                    carryRoot = root.transform;
                    carryRoot.SetParent(transform, false);
                    carryRoot.localPosition = Interactor != null && Interactor.cargoAttachPoint != null
                        ? transform.InverseTransformPoint(Interactor.cargoAttachPoint.position)
                        : new Vector3(0f, .75f, 0f);
                }
                anchor = carryRoot;
            }
            // Remove the box from the ShelfStorage table (consistency for the other agents).
            RemoveFromStorage(zone, box);
            Quaternion rotation = box.transform.rotation;
            box.transform.SetParent(anchor, true);
            box.transform.position = anchor.position;
            box.transform.rotation = rotation;
            box.isCarried = true;
            Carried = box;
            if (Animator != null) Animator.SetBool("HasBox", true);
        }

        private static void RemoveFromStorage(ShelfInteractionZone zone, CargoBox box)
        {
            if (zone == null || zone.shelf == null || zone.shelf.columns == null) return;
            for (int c = 0; c < zone.shelf.columns.Length; c++)
            {
                Transform[] levels = zone.shelf.columns[c] != null ? zone.shelf.columns[c].levels : null;
                if (levels == null) continue;
                for (int l = 0; l < levels.Length; l++)
                    if (zone.shelf.GetBox(c, l) == box) { zone.shelf.RemoveBox(c, l); return; }
            }
        }

        private Transform AssetCarryRoot()
        {
            if (Interactor == null) return null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            return typeof(RobotCargoInteractor).GetField("activeCarryRoot", flags)?.GetValue(Interactor) as Transform;
        }

        private static int LevelOf(ShelfInteractionZone zone, CargoBox box)
        {
            Transform[] levels = CargoRegistry.ColumnLevels(zone);
            if (levels == null) return 0;
            for (int i = 0; i < levels.Length; i++)
                if (levels[i] != null && levels[i].GetComponentInChildren<CargoBox>(true) == box) return i;
            return 0;
        }

        private void PlayTrigger(string triggerName)
        {
            if (Animator == null) return;
            foreach (AnimatorControllerParameter parameter in Animator.parameters)
                if (parameter.name == triggerName && parameter.type == AnimatorControllerParameterType.Trigger)
                {
                    Animator.SetTrigger(triggerName);
                    return;
                }
        }

        private void Log(string message)
        {
            if (LogActions) Debug.Log("[Loader:" + name + "] " + message);
        }

        // ------------------------------------------------------------------ directed mission (Muse Wonder)
        //
        // The orchestrator assigns the truck and the order: the robot ONLY takes the
        // boxes tagged with orderId, on the assigned bench, and only loads them into
        // the assigned bay, waiting until its door is actually open.

        public sealed class LoadMissionReport
        {
            public int OrderId;
            public int Loaded;
            public bool Aborted;
            public string AbortReason;
        }

        /// <summary>Directed mission in progress.</summary>
        /// <summary>Loading missions already queued: returning to base is skipped
        /// to chain directly (rest only when there is nothing left to do).</summary>
        public int QueuedLoads;
        /// <summary>The orchestrator can ask R2 to stay out after a mission
        /// (resumed loadings will arrive within seconds): returns true while
        /// paused work is waiting for the truck.</summary>
        public System.Func<bool> StayOut;
        public bool MissionActive { get; private set; }
        /// <summary>Order loaded by the current mission (-1 = none).</summary>
        public int DirectedOrderId { get; private set; } = -1;
        public LoadMissionReport LastReport { get; private set; }
        /// <summary>Each box actually loaded into the truck.</summary>
        public event System.Action<CargoBox> BoxLoaded;
        /// <summary>Seconds without finding a box before giving up (0 = wait forever).</summary>
        public float IdleAbortDelay = 0f;

        private bool missionAbort;
        private bool missionSuspend;

        /// <summary>Starts in directed mode: controls locked but NO free loop.</summary>
        public void BeginDirected()
        {
            if (Running) return;
            if (Mover == null)
            {
                Debug.LogError("[Loader:" + name + "] GridMover not found.");
                return;
            }
            if (Controller != null) Controller.controlsLocked = true;
            homePos = HomeAnchor != null ? HomeAnchor.position : transform.position;
            homeForward = HomeAnchor != null ? HomeAnchor.forward : transform.forward;
            Running = true;
            SetState(R2State.WaitingForBoxes, "waiting for a directed mission");
            Log("started (directed mode)");
        }

        /// <summary>Cancels the current mission (the carried box is put on the floor).</summary>
        public void AbortMission() { missionAbort = true; }

        /// <summary>
        /// Safe suspension (urgent order): a box in hand is ALWAYS loaded into the
        /// truck (indivisible operation), then the mission ends and the robot leaves
        /// the dock; no new cycle is started.
        /// </summary>
        public void SuspendMission() { missionSuspend = true; }

        /// <summary>
        /// Loads into bay every box of orderId found on bench, until expectedCount
        /// is reached or nothing is left (R1 finished its pick).
        /// Never enters a truck whose door is not fully open.
        /// </summary>
        public IEnumerator LoadMission(TruckBay bay, ShelfStorage bench, int orderId,
                                       int expectedCount, System.Action<LoadMissionReport> done)
        {
            var report = new LoadMissionReport { OrderId = orderId };
            LastReport = report;
            MissionActive = true;
            missionAbort = false;
            missionSuspend = false;
            DirectedOrderId = orderId;
            float idleSince = Time.time;

            // Carried is always null at the top of the loop: suspension can stop
            // the cycle there without leaving a box in the air.
            while (Running && !missionAbort && !missionSuspend && report.Loaded < expectedCount)
            {
                // ---- door: never approach a closed truck
                while (Running && !missionAbort && !missionSuspend && !bay.IsDoorOpen)
                {
                    SetState(R2State.WaitingForTruck, "waiting for door to open " + bay.name);
                    yield return new WaitForSeconds(.4f);
                }
                if (!Running || missionAbort || missionSuspend) break;

                // ---- order box on the assigned bench (always the top one)
                CargoBox box = FindOrderBox(bench, orderId, out ShelfInteractionZone zone);
                if (box == null)
                {
                    SetState(R2State.WaitingForBoxes, "waiting for boxes of order #" + orderId +
                           " (" + report.Loaded + "/" + expectedCount + ")");
                    if (IdleAbortDelay > 0f && Time.time - idleSince > IdleAbortDelay)
                    {
                        report.AbortReason = "no more boxes of the order on the bench";
                        break;
                    }
                    yield return new WaitForSeconds(PollInterval);
                    continue;
                }
                idleSince = Time.time;

                if (registry == null) registry = CargoRegistry.Instance;
                if (registry != null && !registry.TryReserve(box, this))
                {
                    yield return new WaitForSeconds(.3f);
                    continue;
                }

                // ---- go to the bench
                SetState(R2State.ToBench, "heading to " + zone.name);
                yield return Mover.NavigateTo(zone.dockPoint.position, zone.dockPoint.forward);
                if (Mover.LastResult != GridMover.NavResult.Arrived)
                {
                    if (registry != null) registry.Release(box, this);
                    FailJob("did not reach the bench");
                    yield return new WaitForSeconds(.5f);
                    continue;
                }

                // The top box may have changed: take the actual top one if it
                // belongs to the order, otherwise search again.
                CargoBox top = CargoRegistry.TopBox(zone);
                if (top != box)
                {
                    if (registry != null) registry.Release(box, this);
                    if (top != null && top.orderId == orderId &&
                        (registry == null || registry.TryReserve(top, this)))
                        box = top;
                    else continue;
                }

                // Suspension: no new cycle, the next pick does not start.
                if (missionSuspend)
                {
                    if (registry != null) registry.Release(box, this);
                    break;
                }

                SetState(R2State.Picking, "picking " + box.name + " (order #" + orderId + ")");
                yield return Interact(true, box, zone);
                if (Carried != box)
                {
                    if (registry != null) registry.Release(box, this);
                    yield return new WaitForSeconds(.3f);
                    continue;
                }

                // ---- go to the truck chosen by Muse (open door required)
                while (Running && !missionAbort && !missionSuspend && !bay.IsDoorOpen)
                {
                    SetState(R2State.WaitingForTruck, "door closed: waiting " + bay.name);
                    yield return new WaitForSeconds(.4f);
                }
                if (!Running || missionAbort) break;
                // Suspension with a box in hand and the door closed: put back on the
                // order's bench, never on the floor and never mixed.
                if (missionSuspend && !bay.IsDoorOpen)
                {
                    yield return PutBackOnBench(Carried, bench, zone);
                    break;
                }
                bay.BeginLoading();

                ShelfInteractionZone dockZone = bay.ChooseDockForLoad(transform.position);
                Transform dockPoint = dockZone != null && dockZone.dockPoint != null
                    ? dockZone.dockPoint : bay.DockPoint;
                if (dockPoint == null)
                {
                    FailJob("no truck dock");
                    break;
                }
                SetState(R2State.ToTruck, "heading to " + bay.name + " (order #" + orderId + ")");
                yield return Mover.NavigateTo(dockPoint.position, dockPoint.forward);
                if (Mover.LastResult != GridMover.NavResult.Arrived)
                {
                    FailJob("truck trip interrupted");
                    yield return new WaitForSeconds(.5f);
                    continue;
                }

                while (Running && !missionAbort && !missionSuspend && bay.FreeSlots <= 0)
                {
                    SetState(R2State.WaitingForTruck, "truck full: waiting for a slot");
                    yield return new WaitForSeconds(FullRetryInterval);
                }
                if (!Running || missionAbort) break;
                // Suspension with a box in hand and the truck full: put back on the bench.
                if (missionSuspend && bay.FreeSlots <= 0)
                {
                    yield return PutBackOnBench(Carried, bench, zone);
                    break;
                }

                SetState(R2State.Dropping, "dropping in " + bay.name);
                yield return Interact(false, Carried, zone, bay);
                if (Carried == null)
                {
                    Delivered++;
                    report.Loaded++;
                    if (registry != null) registry.Release(box, this);
                    if (Animator != null) Animator.SetBool("HasBox", false);
                    Status = report.Loaded + "/" + expectedCount + " boxes of order #" + orderId;
                    BoxLoaded?.Invoke(box);
                }
                else
                {
                    if (bay == null || !bay.PlaceBox(Carried)) DropAtFeet(Carried);
                    Carried = null;
                    if (Animator != null) Animator.SetBool("HasBox", false);
                    FailJob("drop failed (box released)");
                }
            }

            if (missionAbort || missionSuspend)
            {
                report.Aborted = true;
                report.AbortReason = missionSuspend ? "suspended for an urgent order"
                                                    : "cancelled by the orchestrator";
            }
            if (Carried != null) DropAtFeet(Carried);
            // Leave the truck and return to base BEFORE handing back the report:
            // the door only closes once the robot is physically outside, and the
            // mission stays active until parking to avoid any overlap.
            // Rest only when there is really no work left: queued loadings,
            // suspended mission or paused orders about to resume. In ALL cases R2
            // first leaves the trailer: the door waits for that to close
            // (LoaderOutside) and a robot parked in the truck would block it forever.
            bool stayOut = QueuedLoads > 0 || missionSuspend ||
                           (StayOut != null && StayOut());
            if (stayOut)
            {
                yield return ExitTrailer(bay);
            }
            else if (ReturnHomeWhenIdle && Running && !NearHome())
            {
                SetState(R2State.ReturningHome, "mission complete: returning to base");
                yield return Mover.NavigateTo(homePos, homeForward);
            }
            MissionActive = false;
            DirectedOrderId = -1;
            done?.Invoke(report);
        }

        /// <summary>
        /// Gets R2 out of the trailer / doorway without returning to base:
        /// TruckBay.IsInside tells exactly where the inner volume ends; both sides
        /// of the door are probed and the robot drives just beyond.
        /// </summary>
        private IEnumerator ExitTrailer(TruckBay bay)
        {
            if (bay == null || !bay.IsInside(transform.position)) yield break;
            Vector3 p = transform.position;
            float dir = 0f;
            for (float d = 1f; d <= 14f && dir == 0f; d += 1f)
            {
                if (!bay.IsInside(new Vector3(p.x - d, p.y, p.z))) dir = -1f;
                else if (!bay.IsInside(new Vector3(p.x + d, p.y, p.z))) dir = +1f;
            }
            if (dir == 0f) yield break; // unknown geometry: the door protects itself
            Vector3 target = p;
            for (int i = 0; i < 18 && bay.IsInside(target); i++)
                target.x += dir * 1f;
            target.x += dir * .8f; // margin beyond the inner volume
            SetState(R2State.ToTruck, "leaving " + bay.name);
            yield return Mover.NavigateTo(target, transform.forward);
        }

        /// <summary>
        /// Suspension with a box in hand that cannot be loaded (door closed or
        /// truck full): the box is PUT BACK on its order's bench, never on the
        /// floor, never mixed with another order.
        /// </summary>
        private IEnumerator PutBackOnBench(CargoBox box, ShelfStorage bench,
                                           ShelfInteractionZone zone)
        {
            if (box == null) yield break;
            if (bench == null || zone == null || zone.shelf != bench || zone.dockPoint == null)
            {
                DropAtFeet(box);
                Carried = null;
                if (Animator != null) Animator.SetBool("HasBox", false);
                yield break;
            }
            SetState(R2State.ToBench, "suspension: returning " + box.name + " to the bench");
            yield return Mover.NavigateTo(zone.dockPoint.position, zone.dockPoint.forward);
            int column = zone.columnIndex;
            int level = -1;
            if (!bench.HasFreePlaceToPut(column, out level))
            {
                level = -1; // column full: any other free level of the bench will do
                for (int c = 0; c < bench.columns.Length; c++)
                {
                    if (bench.HasFreePlaceToPut(c, out level)) { column = c; break; }
                }
            }
            if (level >= 0)
            {
                bench.PutBox(column, level, box);
                Carried = null;
                if (Animator != null) Animator.SetBool("HasBox", false);
                Status = "box put back on the bench (suspension)";
            }
            else
            {
                DropAtFeet(box);
                Carried = null;
                if (Animator != null) Animator.SetBool("HasBox", false);
            }
            if (registry != null && box != null) registry.Release(box, this);
        }

        /// <summary>Top box of a bench column, belonging to the order.</summary>
        private CargoBox FindOrderBox(ShelfStorage bench, int orderId, out ShelfInteractionZone zone)
        {
            zone = null;
            if (bench == null) return null;
            CargoBox best = null;
            ShelfInteractionZone bestZone = null;
            float bestDistance = float.PositiveInfinity;
            foreach (ShelfInteractionZone z in bench.GetComponentsInChildren<ShelfInteractionZone>(true))
            {
                if (z == null || z.dockPoint == null) continue;
                if (NavBlocker.BlocksPoint(z.dockPoint.position)) continue;
                CargoBox top = CargoRegistry.TopBox(z);
                if (top == null || top.orderId != orderId || top.isCarried) continue;
                if (!ShippingLabels.IsReady(top)) continue;   // the parcel gets its shipping label first (R2 waits on the bench)
                if (registry != null && registry.IsReserved(top) &&
                    !ReferenceEquals(registry.ReservedBy(top), this)) continue;
                float d = Vector3.Distance(transform.position, z.dockPoint.position);
                if (d < bestDistance) { bestDistance = d; best = top; bestZone = z; }
            }
            zone = bestZone;
            return best;
        }
    }
}
