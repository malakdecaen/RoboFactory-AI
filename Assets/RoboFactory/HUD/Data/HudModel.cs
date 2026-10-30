using System.Collections.Generic;
using UnityEngine;

namespace RoboFactory.HUD
{
    /// <summary>Visual states of the Muse Wonder circle (deduced from the system's real states).</summary>
    public enum MuseVisualState { Idle, Listening, Transcribing, Planning, Executing, Urgent, Suspending, Resuming, Error }

    public enum OrderCategory { Urgent, Active, Suspended, Pending, Done }

    public enum SafetyLevel { Safe, Caution, Alert, Unknown }

    public enum HudMode { Cinematic, Operations, Debug }

    // ------------------------------------------------------------------ ViewModels

    public sealed class OrderVM
    {
        public int Id;
        public bool Urgent;
        public int Priority;
        public string Status = "";
        public OrderCategory Category;
        public int A, B, C;
        public int StagedA, StagedB, StagedC;
        public int LoadedA, LoadedB, LoadedC;
        public string Bench = "";
        public string Truck = "";
        /// <summary>FINISH TO FREE: normal order Muse has finished to free the urgent one.</summary>
        public bool FinishingForUrgent;

        public int Total => A + B + C;
        public int Staged => StagedA + StagedB + StagedC;
        public int Loaded => LoadedA + LoadedB + LoadedC;
        public float LoadedFrac => Total > 0 ? Loaded / (float)Total : 0f;
        public float PreparedFrac => Total > 0 ? (Loaded + Staged) / (float)Total : 0f;

        /// <summary>"A×2 · B×1" (types actually requested).</summary>
        public string Composition
        {
            get
            {
                var parts = new List<string>(3);
                if (A > 0) parts.Add("A×" + A);
                if (B > 0) parts.Add("B×" + B);
                if (C > 0) parts.Add("C×" + C);
                return parts.Count == 0 ? "—" : string.Join("  ", parts);
            }
        }
    }

    public sealed class PickVM
    {
        public string Pallet;
        public string Type;
        public int Count;
    }

    /// <summary>Muse decision as recorded by the orchestrator (never invented).</summary>
    public sealed class DecisionVM
    {
        public object Source;            // original MuseDecisionRecord (identity)
        public string RequestId = "";
        public string Trigger = "";
        public string Mode = "";
        public string Model = "";
        public string Action = "";
        public string Reason = "";
        public string RejectReason = "";
        public string Applied = "";
        public string PlanSummary = "";
        public int OrderId;
        public int LatencyMs;
        public bool Accepted;
        public float Time;
        public bool Urgent;
        public readonly List<PickVM> Picks = new List<PickVM>();
        public readonly List<string> Pallets = new List<string>();
        public string Bench = "";
        public string Truck = "";

        public bool IsExecute => (Action ?? "").Trim().ToLowerInvariant() == "execute_order";
        public bool HasPlan => !string.IsNullOrEmpty(Bench) || Pallets.Count > 0;
        public bool IsReplan
        {
            get
            {
                string t = (Trigger ?? "").ToLowerInvariant();
                return t.Contains("replan") || t.Contains("resume") || t.Contains("retry") || t.Contains("shortage");
            }
        }
    }

    public sealed class RobotVM
    {
        public string Id = "";
        public string Role = "";
        public Transform Transform;
        public bool Present;
        public bool Operational;
        public bool MissionActive;
        public int OrderId = -1;
        public string Mission = "";
        public string Destination = "";
        public string State = "";
        public string RawStatus = "";
        public string Load = "";
        public bool Carrying;
        public bool RouteBlocked;
        public bool RouteKnown;
        public IReadOnlyList<Vector3> Path;
        public string LegInfo = "";
        /// <summary>Readable activity deduced from the real state: PREPARING, DELIVERING, LOADING…</summary>
        public string Activity = "";
        /// <summary>Resource targeted by this activity (P02, B03, T03…), "" if none.</summary>
        public string ActivityTarget = "";
    }

    public sealed class TruckVM
    {
        public string Id = "";
        public Transform Transform;
        public string Door = "";
        public int Loaded;
        public int Capacity;
        public int ReservedOrder = -1;
        /// <summary>Truck reserved for urgent orders (the orchestrator's urgent lane).</summary>
        public bool UrgentLane;
        public bool Selected => ReservedOrder >= 0;
    }

    public enum MarkKind { Pallet, Bench, Truck }

    /// <summary>Resource actually reserved by an order (WarehouseWorld.ReservedBy).</summary>
    public sealed class MarkVM
    {
        public string Id = "";
        public MarkKind Kind;
        public int OrderId;
        public bool Urgent;
        public Transform Transform;
    }

    /// <summary>Scene pallet / bench / truck, for the hover info bubble (real values).</summary>
    public sealed class ResourceVM
    {
        public string Id = "";
        public MarkKind Kind;
        public Transform Transform;
        public int OrderId = -1;        // order reserving it (-1 = free)
        public string Detail = "";      // stock / content / door
    }

    public sealed class VoiceVM
    {
        public bool Present;
        public bool Recording;
        public bool Starting;
        public bool Finishing;
        public bool Busy;
        public float Elapsed;
        public string Live = "";
        public string Final = "";
        public string Status = "";
        public string Failure = "";
        public string AsrModel = "";
        public int AsrLatencyMs;
        public string AsrMode = "";
    }

    public sealed class StatusVM
    {
        public int RobotsOnline;
        public int RobotsTotal;
        public int ActiveOrders;
        public int UrgentOrders;
        public int Delivered;
        public int Requested;
        public SafetyLevel Safety = SafetyLevel.Unknown;
        public float NearestWorker = float.PositiveInfinity;
        public string TruckSummary = "";
        public string Model = "-";
        public int LatencyMs;
        public string Link = "—";       // ONLINE / CALLING / UNREACHABLE / OFFLINE MODE / ERROR
        public bool LinkOk;
        public string Mode = "";        // live | mock | offline
        public string HealthModel = "";
    }

    public sealed class UrgentVM
    {
        public string Stage = "Idle";
        public int OrderId = -1;
        public string Status = "";
        public string Error = "";
        public int Paused;
        public int Suspending;
        public int Resumed;
        public int Loaded;
        public int Total;
        // ---- FINISH TO FREE (real orchestrator state)
        public bool WaitingCapacity;       // Stage == WaitingForCapacity
        public bool CapacityEpisode;       // the current urgent order went through the capacity wait
        public bool CapacityReady;         // bench + empty truck pair actually available
        public int FinishingId = -1;
        public string FreeingBench = "", FreeingTruck = "";
        public string CapacityReason = "", CapacityStrategy = "", CapacityNote = "";
        public float CapacityEta = -1f;
        public string CapacityBench = "", CapacityTruck = "";
    }

    public sealed class DebugVM
    {
        public string Phase = "";
        public int StateVersion;
        public string PendingInfo = "";
        public int Calls;
        public string Validation = "";
        public readonly List<string> Incidents = new List<string>();
        public readonly List<string> Reservations = new List<string>();
        public string TraceFile = "";
    }

    /// <summary>Full snapshot of the displayed state, rebuilt at each refresh.</summary>
    public sealed class HudModel
    {
        public bool Ready;
        public MuseVisualState Visual = MuseVisualState.Idle;
        public string VisualDetail = "";
        public string ErrorText = "";
        public readonly List<OrderVM> Orders = new List<OrderVM>();
        public DecisionVM Decision;
        public readonly List<DecisionVM> History = new List<DecisionVM>();
        public readonly RobotVM R1 = new RobotVM { Id = "R1", Role = "PREPARER" };
        public readonly RobotVM R2 = new RobotVM { Id = "R2", Role = "TRANSPORTER" };
        public readonly List<TruckVM> Trucks = new List<TruckVM>();
        public readonly List<MarkVM> Marks = new List<MarkVM>();
        public readonly List<ResourceVM> Resources = new List<ResourceVM>();
        public readonly VoiceVM Voice = new VoiceVM();
        public readonly StatusVM Status = new StatusVM();
        public readonly UrgentVM Urgent = new UrgentVM();
        public readonly DebugVM Debug = new DebugVM();
        public string LastIncident = "";
        public int ActiveOrderId = -1;

        public OrderVM FindOrder(int id)
        {
            foreach (OrderVM o in Orders) if (o.Id == id) return o;
            return null;
        }
    }
}
