using System;
using System.Collections.Generic;
using RoboFactory.Hackathon.Orchestration;
using RoboFactory.Integration;
using RoboFactory.Logistics;
using UnityEngine;
using RoboFactory.Sim;

namespace RoboFactory.HUD
{
    /// <summary>
    /// Read-only bridge between the existing systems (MuseOrchestrator, robots, trucks,
    /// voice) and the HUD. Makes NO logistics decision: it reads the public states,
    /// turns them into ViewModels and signals changes (new decision, urgent stage).
    /// </summary>
    public sealed class HudDataSource : IDisposable
    {
        public MuseOrchestrator Orchestrator { get; private set; }
        public VoiceCommandController Voice { get; private set; }
        public WarehouseWorld World { get; private set; }
        public WarehouseRobotAutopilot R1 { get; private set; }
        public TruckLoaderAgent R2 { get; private set; }

        public readonly HudModel Model = new HudModel();

        /// <summary>New decision recorded by the orchestrator (accepted or refused).</summary>
        public event Action<DecisionVM> DecisionArrived;
        /// <summary>Urgent pipeline stage change (old, new).</summary>
        public event Action<string, string> UrgentStageChanged;

        // proxy health (GET /health, read-only)
        public bool? HealthOk;
        public string HealthModel = "";

        private readonly Dictionary<MuseDecisionRecord, DecisionVM> decisionCache = new Dictionary<MuseDecisionRecord, DecisionVM>();
        private MuseDecisionRecord lastSeenDecision;
        private string lastStage = "Idle";
        private float stageSince;
        private float nextSafety;
        private float nextLookup;
        private CargoBox r1Carried;
        private WarehouseRobotAutopilot subscribedR1;
        private WarehouseRobotVisionHud r1Vision;

        // ------------------------------------------------------------------ link

        public bool Bind()
        {
            if (Time.unscaledTime < nextLookup && Orchestrator != null) return true;
            nextLookup = Time.unscaledTime + 1f;
            if (Orchestrator == null) Orchestrator = UnityEngine.Object.FindFirstObjectByType<MuseOrchestrator>();
            if (Orchestrator == null) return false;
            if (Voice == null) Voice = UnityEngine.Object.FindFirstObjectByType<VoiceCommandController>();
            if (World == null) World = WarehouseWorld.Instance != null ? WarehouseWorld.Instance
                                                                       : UnityEngine.Object.FindFirstObjectByType<WarehouseWorld>();
            R1 = Orchestrator.Robot1 != null ? Orchestrator.Robot1 : (World != null ? World.R1 : null);
            R2 = Orchestrator.Robot2 != null ? Orchestrator.Robot2 : (World != null ? World.R2 : null);
            if (R1 != subscribedR1)
            {
                Unsubscribe();
                if (R1 != null)
                {
                    R1.BoxPicked += OnR1Picked;
                    R1.BoxStaged += OnR1Staged;
                    subscribedR1 = R1;
                    r1Vision = R1.GetComponent<WarehouseRobotVisionHud>();
                }
            }
            if (r1Vision == null && R1 != null) r1Vision = R1.GetComponent<WarehouseRobotVisionHud>();
            return true;
        }

        private void OnR1Picked(CargoBox box, WarehouseRobotAutopilot.PickLeg leg) { r1Carried = box; }
        private void OnR1Staged(CargoBox box) { if (r1Carried == box) r1Carried = null; }

        private void Unsubscribe()
        {
            if (subscribedR1 != null)
            {
                subscribedR1.BoxPicked -= OnR1Picked;
                subscribedR1.BoxStaged -= OnR1Staged;
            }
            subscribedR1 = null;
        }

        public void Dispose() { Unsubscribe(); }

        // ------------------------------------------------------------------ refresh

        public void Refresh()
        {
            HudModel m = Model;
            m.Ready = Bind();
            if (!m.Ready) return;
            MuseOrchestrator o = Orchestrator;

            ReadOrders(m, o);
            ReadDecisions(m, o);
            ReadUrgent(m, o);
            ReadVoice(m);
            ReadRobots(m, o);
            ReadTrucksAndMarks(m, o);
            ReadStatus(m, o);
            ReadDebug(m, o);
            ResolveVisual(m, o);
        }

        private void ReadOrders(HudModel m, MuseOrchestrator o)
        {
            WhOrderState[] snapshot = o.OrdersSnapshot;
            while (m.Orders.Count < snapshot.Length) m.Orders.Add(new OrderVM());
            if (m.Orders.Count > snapshot.Length) m.Orders.RemoveRange(snapshot.Length, m.Orders.Count - snapshot.Length);
            for (int i = 0; i < snapshot.Length; i++)
            {
                WhOrderState s = snapshot[i];
                OrderVM v = m.Orders[i];
                v.Id = s.id;
                v.Urgent = s.urgent;
                v.Priority = s.priority;
                v.Status = s.status ?? "";
                v.A = s.a; v.B = s.b; v.C = s.c;
                v.StagedA = s.staged_a; v.StagedB = s.staged_b; v.StagedC = s.staged_c;
                v.LoadedA = s.loaded_a; v.LoadedB = s.loaded_b; v.LoadedC = s.loaded_c;
                v.Bench = s.bench_id ?? "";
                v.Truck = s.truck_id ?? "";
                v.FinishingForUrgent = o.FinishingOrderId == s.id;
                v.Category = Categorize(v);
            }
            m.ActiveOrderId = o.ActiveOrderId;
        }

        public static OrderCategory Categorize(OrderVM v)
        {
            string s = v.Status.ToLowerInvariant();
            if (s == "loaded") return OrderCategory.Done;
            if (s == "suspending" || s == "paused") return OrderCategory.Suspended;
            if (v.Urgent) return OrderCategory.Urgent;
            if (s == "pending" || s == "blocked" || s == "") return OrderCategory.Pending;
            return OrderCategory.Active; // planned | queued | picking | staging | loading | waiting
        }

        private void ReadDecisions(HudModel m, MuseOrchestrator o)
        {
            MuseDecisionRecord last = o.LastDecision;
            m.Decision = last != null ? ToVM(last, m) : null;
            m.History.Clear();
            IReadOnlyList<MuseDecisionRecord> history = o.History;
            if (history != null)
                for (int i = history.Count - 1; i >= 0 && m.History.Count < 12; i--)
                    if (history[i] != null) m.History.Add(ToVM(history[i], m));

            if (last != null && last != lastSeenDecision)
            {
                lastSeenDecision = last;
                DecisionArrived?.Invoke(m.Decision);
            }
        }

        private DecisionVM ToVM(MuseDecisionRecord r, HudModel m)
        {
            if (!decisionCache.TryGetValue(r, out DecisionVM v))
            {
                v = new DecisionVM { Source = r };
                v.RequestId = r.RequestId ?? "";
                v.Trigger = r.Trigger ?? "";
                v.Mode = r.Mode ?? "";
                v.Model = r.Model ?? "";
                v.Action = r.Action ?? "";
                v.Reason = r.Reason ?? "";
                v.RejectReason = r.RejectReason ?? "";
                v.Applied = r.AppliedResult ?? "";
                v.PlanSummary = r.PlanSummary ?? "";
                v.OrderId = r.OrderId;
                v.LatencyMs = r.LatencyMs;
                v.Accepted = r.Accepted;
                v.Time = r.Time;
                ParsePlan(v);
                if (decisionCache.Count > 64) decisionCache.Clear();
                decisionCache[r] = v;
            }
            OrderVM order = m.FindOrder(v.OrderId);
            v.Urgent = (v.Trigger ?? "").Contains("urgent") || (order != null && order.Urgent);
            return v;
        }

        /// <summary>Reads the summary produced by MuseOrchestrator.Describe: "#12 P02:Ax2 P05:Bx1 -> B03 -> T02".</summary>
        public static void ParsePlan(DecisionVM v)
        {
            v.Picks.Clear();
            v.Pallets.Clear();
            v.Bench = "";
            v.Truck = "";
            string s = v.PlanSummary;
            if (string.IsNullOrEmpty(s)) return;
            string[] parts = s.Split(new[] { " -> " }, StringSplitOptions.None);
            if (parts.Length >= 2) v.Bench = parts[1].Trim();
            if (parts.Length >= 3) v.Truck = parts[2].Trim();
            string[] tokens = parts[0].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string tok in tokens)
            {
                if (tok.StartsWith("#")) continue;
                int colon = tok.IndexOf(':');
                if (colon <= 0) continue;
                string pallet = tok.Substring(0, colon);
                string rest = tok.Substring(colon + 1);       // "Ax2"
                int x = rest.IndexOf('x');
                var pick = new PickVM { Pallet = pallet, Type = x > 0 ? rest.Substring(0, x) : rest };
                if (x > 0) int.TryParse(rest.Substring(x + 1), out pick.Count);
                v.Picks.Add(pick);
                if (!v.Pallets.Contains(pallet)) v.Pallets.Add(pallet);
            }
            if (v.Bench == "null" || v.Bench == "") v.Bench = "";
            if (v.Truck == "null" || v.Truck == "") v.Truck = "";
        }

        private void ReadUrgent(HudModel m, MuseOrchestrator o)
        {
            UrgentVM u = m.Urgent;
            string stage = o.Stage.ToString();
            if (stage != lastStage)
            {
                string previous = lastStage;
                lastStage = stage;
                stageSince = Time.unscaledTime;
                UrgentStageChanged?.Invoke(previous, stage);
            }
            u.Stage = stage;
            u.OrderId = o.UrgentOrderId;
            u.Status = o.UrgentStatus ?? "";
            u.Error = o.UrgentError ?? "";
            u.Paused = o.PausedCount;
            u.Suspending = o.SuspendingCount;
            u.Resumed = o.ResumedCount;
            OrderVM uo = m.FindOrder(u.OrderId);
            u.Loaded = uo != null ? uo.Loaded : 0;
            u.Total = uo != null ? uo.Total : 0;
            u.WaitingCapacity = o.Stage == MuseOrchestrator.UrgentStage.WaitingForCapacity;
            u.CapacityEpisode = o.CapacityEpisode;
            u.CapacityReady = o.CapacityReady;
            u.FinishingId = o.FinishingOrderId;
            u.FreeingBench = o.FreeingBenchId ?? "";
            u.FreeingTruck = o.FreeingTruckId ?? "";
            u.CapacityReason = o.CapacityReason ?? "";
            u.CapacityStrategy = o.CapacityStrategy ?? "";
            u.CapacityNote = o.CapacityNote ?? "";
            u.CapacityEta = o.CapacityEtaS;
            u.CapacityBench = o.CapacityBenchId ?? "";
            u.CapacityTruck = o.CapacityTruckId ?? "";
        }

        public float StageAge => Time.unscaledTime - stageSince;

        private void ReadVoice(HudModel m)
        {
            VoiceVM v = m.Voice;
            v.Present = Voice != null;
            if (Voice == null) return;
            v.Recording = Voice.IsRecording;
            v.Starting = Voice.State == VoiceCommandController.VoiceState.Starting;
            v.Finishing = Voice.State == VoiceCommandController.VoiceState.Finishing;
            v.Busy = Voice.Busy;
            v.Elapsed = Voice.RecordingElapsed;
            v.Live = Voice.LiveTranscript ?? "";
            v.Final = Voice.FinalTranscript ?? "";
            v.Status = Voice.StatusText ?? "";
            v.Failure = Voice.LastFailure ?? "";
            v.AsrModel = Voice.LastAsrModel ?? "";
            v.AsrLatencyMs = Voice.LastAsrLatencyMs;
            v.AsrMode = Voice.LastAsrMode ?? "";
        }

        // ------------------------------------------------------------------ robots

        private void ReadRobots(HudModel m, MuseOrchestrator o)
        {
            RobotVM a = m.R1;
            a.Present = R1 != null;
            if (R1 != null)
            {
                a.Transform = R1.transform;
                a.Operational = R1.isActiveAndEnabled && R1.Running;
                a.MissionActive = R1.MissionActive;
                a.OrderId = R1.MissionActive && R1.LastReport != null ? R1.LastReport.OrderId : -1;
                a.RawStatus = R1.Status ?? "";
                a.State = MapStatus(a.RawStatus, a.Operational);
                a.Mission = a.MissionActive ? "PREPARE #" + a.OrderId
                          : (R1.QueuedPicks > 0 ? "QUEUED (" + R1.QueuedPicks + ")" : "STANDBY");
                a.Destination = ResolveTarget(R1.CurrentTarget);
                WarehouseRobotAutopilot.PickLeg leg = R1.ActiveLeg;
                a.LegInfo = leg != null ? leg.PalletId + "  " + leg.Kind + "  " + leg.Picked + "/" + leg.Planned : "";
                if (r1Carried != null && !r1Carried.isCarried) r1Carried = null;
                if (!R1.MissionActive && r1Carried != null && !r1Carried.isCarried) r1Carried = null;
                a.Carrying = r1Carried != null;
                a.Load = DescribeBox(r1Carried);
                a.Path = R1.CurrentPath;
                a.RouteKnown = r1Vision != null;
                a.RouteBlocked = r1Vision != null && r1Vision.Blocked;
                OrderVM ao = m.FindOrder(a.OrderId);
                if (!a.Operational) { a.Activity = "OFFLINE"; a.ActivityTarget = ""; }
                else if (!a.MissionActive) { a.Activity = a.State == "RETURNING" ? "RETURNING" : R1.QueuedPicks > 0 ? "QUEUED" : "STANDBY"; a.ActivityTarget = ""; }
                else if (a.State == "WAITING") { a.Activity = "WAITING"; a.ActivityTarget = "MUSE"; a.Mission = "WAIT MUSE"; a.OrderId = -1; }
                else if (a.Carrying) { a.Activity = "DELIVERING"; a.ActivityTarget = ao != null ? ao.Bench : a.Destination; }
                else if (a.State == "SUSPENDED") { a.Activity = "SUSPENDED"; a.ActivityTarget = ""; }
                else { a.Activity = "PREPARING"; a.ActivityTarget = leg != null && !string.IsNullOrEmpty(leg.PalletId) ? leg.PalletId : a.Destination; }
                if (a.ActivityTarget == "—") a.ActivityTarget = "";
            }

            RobotVM b = m.R2;
            b.Present = R2 != null;
            if (R2 != null)
            {
                b.Transform = R2.transform;
                b.Operational = R2.isActiveAndEnabled && R2.Running;
                b.MissionActive = R2.MissionActive;
                b.OrderId = R2.MissionActive ? R2.DirectedOrderId : -1;
                b.RawStatus = R2.Status ?? "";
                b.State = MapR2State(R2.State, b.Operational);
                b.Mission = b.MissionActive ? "LOAD #" + b.OrderId
                          : (R2.QueuedLoads > 0 ? "QUEUED (" + R2.QueuedLoads + ")" : "STANDBY");
                OrderVM order = m.FindOrder(b.OrderId);
                switch (R2.State)
                {
                    case TruckLoaderAgent.R2State.ToBench:
                    case TruckLoaderAgent.R2State.Picking:
                    case TruckLoaderAgent.R2State.WaitingForBoxes:
                        b.Destination = order != null && order.Bench != "" ? order.Bench : "BENCH";
                        break;
                    case TruckLoaderAgent.R2State.ToTruck:
                    case TruckLoaderAgent.R2State.Dropping:
                    case TruckLoaderAgent.R2State.WaitingForTruck:
                        b.Destination = order != null && order.Truck != "" ? order.Truck : "TRUCK";
                        break;
                    case TruckLoaderAgent.R2State.ReturningHome:
                        b.Destination = "HOME";
                        break;
                    default:
                        b.Destination = "—";
                        break;
                }
                b.Carrying = R2.Carried != null;
                b.Load = DescribeBox(R2.Carried);
                b.Path = R2.Mover != null ? R2.Mover.CurrentPath : null;
                b.RouteKnown = false;
                b.RouteBlocked = false;
                b.LegInfo = "";
                if (!b.Operational) { b.Activity = "OFFLINE"; b.ActivityTarget = ""; }
                else if (R2.State == TruckLoaderAgent.R2State.ReturningHome) { b.Activity = "RETURNING"; b.ActivityTarget = ""; }
                else if (!b.MissionActive) { b.Activity = R2.QueuedLoads > 0 ? "QUEUED" : "STANDBY"; b.ActivityTarget = ""; }
                else
                {
                    switch (R2.State)
                    {
                        case TruckLoaderAgent.R2State.WaitingForBoxes: b.Activity = "WAITING"; break;
                        case TruckLoaderAgent.R2State.WaitingForTruck: b.Activity = "WAITING"; break;
                        case TruckLoaderAgent.R2State.ToBench:
                        case TruckLoaderAgent.R2State.Picking: b.Activity = "TAKING BOX"; break;
                        default: b.Activity = "LOADING"; break;
                    }
                    b.ActivityTarget = b.Destination == "—" ? "" : b.Destination;
                }
            }
        }

        private string ResolveTarget(ShelfInteractionZone zone)
        {
            if (zone == null) return "—";
            ShelfStorage s = zone.shelf;
            if (s == null || World == null) return zone.name;
            string pid = World.PalletIdOf(s);
            if (World.Pallet(pid) == s) return pid;
            string bid = World.BenchIdOf(s);
            if (World.Bench(bid) == s) return bid;
            foreach (string tid in World.TruckIds)
            {
                TruckBay bay = World.Truck(tid);
                if (bay != null && bay.Storage == s) return tid;
            }
            return s.name;
        }

        private static string DescribeBox(CargoBox box)
        {
            if (box == null) return "EMPTY";
            return "1× " + box.TypeLetter;
        }

        /// <summary>Readable translation of the R1 driver's text statuses.</summary>
        public static string MapStatus(string raw, bool operational)
        {
            if (!operational) return "OFFLINE";
            string s = (raw ?? "").ToLowerInvariant();
            if (s.StartsWith("heading to ")) return "MOVING";
            if (s.Contains("replanning")) return "REPLANNING";
            if (s.Contains("blocked")) return "PATH BLOCKED";
            if (s.Contains("suspended")) return "SUSPENDED";
            if (s.Contains("aborted")) return "ABORTED";
            if (s.StartsWith("picking")) return "TAKING BOX";
            if (s.StartsWith("dropping") || s.Contains("placed")) return "DROPPING";
            if (s.Contains("bench full")) return "BENCH FULL";
            if (s.Contains("busy")) return "DOCK BUSY";
            if (s.Contains("holding position")) return "WAITING";
            if (s.StartsWith("returning")) return "RETURNING";
            if (s.Contains("loading")) return "HANDLING";
            if (s.Contains("waiting")) return "IDLE";
            if (s.Contains("stopped")) return "STOPPED";
            return string.IsNullOrEmpty(raw) ? "—" : raw.ToUpperInvariant();
        }

        public static string MapR2State(TruckLoaderAgent.R2State s, bool operational)
        {
            if (!operational) return "OFFLINE";
            switch (s)
            {
                case TruckLoaderAgent.R2State.Stopped: return "STOPPED";
                case TruckLoaderAgent.R2State.WaitingForBoxes: return "WAITING";
                case TruckLoaderAgent.R2State.ToBench: return "MOVING";
                case TruckLoaderAgent.R2State.Picking: return "TAKING BOX";
                case TruckLoaderAgent.R2State.ToTruck: return "MOVING";
                case TruckLoaderAgent.R2State.Dropping: return "LOADING";
                case TruckLoaderAgent.R2State.WaitingForTruck: return "WAITING";
                case TruckLoaderAgent.R2State.ReturningHome: return "RETURNING";
                default: return s.ToString().ToUpperInvariant();
            }
        }

        // ------------------------------------------------------------------ trucks & reservations

        private void ReadTrucksAndMarks(HudModel m, MuseOrchestrator o)
        {
            m.Trucks.Clear();
            m.Marks.Clear();
            m.Debug.Reservations.Clear();
            if (World == null) return;
            foreach (string id in World.TruckIds)
            {
                TruckBay bay = World.Truck(id);
                if (bay == null) continue;
                var t = new TruckVM
                {
                    Id = id,
                    Transform = bay.transform,
                    Door = bay.Door.ToString(),
                    Loaded = bay.LoadedCount,
                    Capacity = bay.Capacity,
                    ReservedOrder = bay.ReservedOrderId,
                    UrgentLane = o != null && o.ReserveUrgentLane && id == o.UrgentTruckId
                };
                m.Trucks.Add(t);
                if (bay.ReservedOrderId >= 0) AddMark(m, id, MarkKind.Truck, bay.ReservedOrderId, bay.transform);
            }
            m.Trucks.Sort((x, y) => string.CompareOrdinal(x.Id, y.Id));

            foreach (string id in World.PalletIds)
            {
                if (!int.TryParse(World.ReservedBy(id), out int orderId)) continue;
                ShelfStorage s = World.Pallet(id);
                AddMark(m, id, MarkKind.Pallet, orderId, s != null ? s.transform : null);
            }
            foreach (string id in World.BenchIds)
            {
                if (!int.TryParse(World.ReservedBy(id), out int orderId)) continue;
                ShelfStorage s = World.Bench(id);
                AddMark(m, id, MarkKind.Bench, orderId, s != null ? s.transform : null);
            }
            ReadResources(m);
        }

        private float nextResourceScan;

        /// <summary>
        /// All pallets, benches and trucks with real detail (free stock per type,
        /// boxes sitting there, door and fill level) — read from the scene twice per second.
        /// </summary>
        private void ReadResources(HudModel m)
        {
            if (Time.unscaledTime < nextResourceScan && m.Resources.Count > 0) return;
            nextResourceScan = Time.unscaledTime + .5f;
            m.Resources.Clear();
            foreach (string id in World.PalletIds)
            {
                ShelfStorage s = World.Pallet(id);
                if (s == null) continue;
                int.TryParse(World.ReservedBy(id), out int by);
                string detail = "A " + World.StockOnPallet(id, "A") + "  ·  B " + World.StockOnPallet(id, "B") +
                                "  ·  C " + World.StockOnPallet(id, "C");
                m.Resources.Add(new ResourceVM { Id = id, Kind = MarkKind.Pallet, Transform = s.transform,
                                                 OrderId = World.ReservedBy(id) != null ? by : -1, Detail = detail });
            }
            foreach (string id in World.BenchIds)
            {
                ShelfStorage s = World.Bench(id);
                if (s == null) continue;
                int boxes = s.GetComponentsInChildren<CargoBox>(true).Length;
                int reserved = World.BenchReservedBy(id);
                string detail = (reserved >= 0 ? "#" + reserved + "  ·  " : "") +
                                (boxes > 0 ? boxes + " box(es)" : reserved >= 0 ? "empty" : "free");
                m.Resources.Add(new ResourceVM { Id = id, Kind = MarkKind.Bench, Transform = s.transform,
                                                 OrderId = reserved, Detail = detail });
            }
            foreach (TruckVM t in m.Trucks)
            {
                if (t.Transform == null) continue;
                string detail = t.Door.ToUpperInvariant() + "  ·  " + t.Loaded + "/" + t.Capacity +
                                (t.ReservedOrder >= 0 ? "  ·  #" + t.ReservedOrder : "");
                m.Resources.Add(new ResourceVM { Id = t.Id, Kind = MarkKind.Truck, Transform = t.Transform,
                                                 OrderId = t.ReservedOrder, Detail = detail });
            }
        }

        private static void AddMark(HudModel m, string id, MarkKind kind, int orderId, Transform t)
        {
            OrderVM order = m.FindOrder(orderId);
            m.Marks.Add(new MarkVM { Id = id, Kind = kind, OrderId = orderId, Urgent = order != null && order.Urgent, Transform = t });
            m.Debug.Reservations.Add(id + "›#" + orderId);
        }

        // ------------------------------------------------------------------ status bar

        private void ReadStatus(HudModel m, MuseOrchestrator o)
        {
            StatusVM s = m.Status;
            s.RobotsTotal = (m.R1.Present ? 1 : 0) + (m.R2.Present ? 1 : 0);
            s.RobotsOnline = (m.R1.Present && m.R1.Operational ? 1 : 0) + (m.R2.Present && m.R2.Operational ? 1 : 0);
            s.ActiveOrders = 0;
            s.UrgentOrders = 0;
            s.Delivered = 0;
            s.Requested = 0;
            foreach (OrderVM ord in m.Orders)
            {
                if (ord.Category == OrderCategory.Active || ord.Category == OrderCategory.Urgent) s.ActiveOrders++;
                if (ord.Category == OrderCategory.Urgent) s.UrgentOrders++;
                s.Delivered += ord.Loaded;
                s.Requested += ord.Total;
            }

            if (Time.unscaledTime >= nextSafety)
            {
                nextSafety = Time.unscaledTime + .5f;
                // Same measure as the old debug HUD (creates an incident if an employee is too close).
                float d = Mathf.Min(o.MinRobotWorkerDistance("R1"), o.MinRobotWorkerDistance("R2"));
                s.NearestWorker = d;
                float alert = o.WorkerProximityAlert;
                s.Safety = float.IsPositiveInfinity(d) ? SafetyLevel.Safe
                         : d < alert ? SafetyLevel.Alert
                         : d < alert * 2.5f ? SafetyLevel.Caution : SafetyLevel.Safe;
            }

            TruckVM busy = null;
            int open = 0;
            foreach (TruckVM t in m.Trucks)
                if (t.Door != "Closed")
                {
                    open++;
                    if (busy == null || t.Door == "Loading") busy = t;
                }
            s.TruckSummary = busy != null
                ? busy.Id + " " + busy.Door.ToUpperInvariant() + (open > 1 ? "  +" + (open - 1) : "")
                : (m.Trucks.Count > 0 ? "ALL CLOSED (" + m.Trucks.Count + ")" : "—");

            s.Model = o.ModelUsed;
            s.LatencyMs = o.LastLatencyMs;
            s.Mode = o.Mode;
            s.HealthModel = HealthModel;
            MuseDecisionRecord last = o.LastDecision;
            bool lastFailed = last != null && !last.Accepted && (last.RejectReason ?? "").StartsWith("Meta call failed");
            if (o.Mode == "offline") { s.Link = "OFFLINE MODE"; s.LinkOk = false; }
            else if (o.Pending || o.UrgentPending) { s.Link = "CALLING"; s.LinkOk = true; }
            else if (lastFailed) { s.Link = "CALL FAILED"; s.LinkOk = false; }
            else if (HealthOk.HasValue) { s.Link = HealthOk.Value ? "ONLINE" : "UNREACHABLE"; s.LinkOk = HealthOk.Value; }
            else if (o.Calls > 0 && o.ModelUsed != "-") { s.Link = "ONLINE"; s.LinkOk = true; }
            else { s.Link = "—"; s.LinkOk = false; }
        }

        private void ReadDebug(HudModel m, MuseOrchestrator o)
        {
            DebugVM d = m.Debug;
            d.Phase = o.CurrentPhase;
            d.StateVersion = o.StateVersion;
            d.PendingInfo = o.PendingInfo;
            d.Calls = o.Calls;
            d.Validation = o.LastValidation;
            d.TraceFile = MuseTrace.FilePath;
            d.Incidents.Clear();
            string inc = o.IncidentsText;
            if (!string.IsNullOrEmpty(inc) && inc != "none")
                d.Incidents.AddRange(inc.Split(new[] { " | " }, StringSplitOptions.RemoveEmptyEntries));
            m.LastIncident = d.Incidents.Count > 0 ? d.Incidents[d.Incidents.Count - 1] : "";
        }

        // ------------------------------------------------------------------ Muse visual state

        private void ResolveVisual(HudModel m, MuseOrchestrator o)
        {
            string stage = m.Urgent.Stage;
            float age = StageAge;
            VoiceVM v = m.Voice;
            DecisionVM d = m.Decision;
            bool freshReject = d != null && !d.Accepted && Time.time - d.Time < 4f;
            m.ErrorText = "";

            if (stage == "Failed" && age < 10f)
            {
                m.Visual = MuseVisualState.Error;
                m.ErrorText = string.IsNullOrEmpty(m.Urgent.Error) ? "URGENT PIPELINE FAILED" : m.Urgent.Error;
                m.VisualDetail = m.ErrorText;
            }
            else if (v.Present && (v.Recording || v.Starting))
            {
                m.Visual = MuseVisualState.Listening;
                m.VisualDetail = "VOICE CAPTURE  " + v.Elapsed.ToString("F1") + " s";
            }
            else if (v.Present && v.Finishing)
            {
                m.Visual = MuseVisualState.Transcribing;
                m.VisualDetail = "TRANSCRIBING VOICE COMMAND";
            }
            else if (stage == "Suspending")
            {
                m.Visual = MuseVisualState.Suspending;
                m.VisualDetail = m.Urgent.Suspending > 0 ? "SUSPENDING " + m.Urgent.Suspending + " ORDER(S) SAFELY" : m.Urgent.Paused + " ORDER(S) PAUSED SAFELY";
            }
            else if (stage == "WaitingForCapacity")
            {
                m.Visual = MuseVisualState.Urgent;
                UrgentVM u = m.Urgent;
                m.VisualDetail = u.CapacityReady ? "RESOURCES AVAILABLE  ·  " + u.CapacityBench + " / " + u.CapacityTruck
                               : u.FinishingId >= 0 ? "FINISHING #" + u.FinishingId + " TO FREE CAPACITY"
                               : "WAITING FOR CAPACITY";
            }
            else if (stage == "UrgentActive")
            {
                m.Visual = MuseVisualState.Urgent;
                m.VisualDetail = "URGENT #" + m.Urgent.OrderId + "  ·  LOADED " + m.Urgent.Loaded + "/" + m.Urgent.Total;
            }
            else if (stage == "Resuming" || (stage == "Completed" && age < 5f))
            {
                m.Visual = MuseVisualState.Resuming;
                m.VisualDetail = "RESUMING " + Mathf.Max(m.Urgent.Resumed, m.Urgent.Paused) + " ORDER(S)";
            }
            else if (stage == "Planning" || o.UrgentPending)
            {
                m.Visual = MuseVisualState.Planning;
                m.VisualDetail = "INTERPRETING URGENT REQUEST";
            }
            else if (o.Pending)
            {
                m.Visual = MuseVisualState.Planning;
                m.VisualDetail = "REQUEST " + o.PendingInfo;
            }
            else if (freshReject)
            {
                m.Visual = MuseVisualState.Error;
                m.ErrorText = "PLAN REJECTED — " + d.RejectReason;
                m.VisualDetail = m.ErrorText;
            }
            else if (o.ActiveCount > 0)
            {
                m.Visual = MuseVisualState.Executing;
                m.VisualDetail = o.ActiveCount + " ORDER(S) IN EXECUTION";
            }
            else
            {
                int waiting = 0;
                foreach (OrderVM ord in m.Orders) if (ord.Category == OrderCategory.Pending) waiting++;
                m.Visual = MuseVisualState.Idle;
                m.VisualDetail = waiting > 0 ? waiting + " ORDER(S) WAITING" : "AWAITING ORDERS";
            }
        }
    }
}
