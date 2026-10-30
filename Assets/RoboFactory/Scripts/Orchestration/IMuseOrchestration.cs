using UnityEngine;

namespace RoboFactory.Hackathon.Orchestration
{
    /// <summary>
    /// Public facade of the Muse Wonder orchestrator. It lives in the Hackathon assembly
    /// so that the PlayMode tests (which cannot see Assembly-CSharp) drive the warehouse
    /// through the interface, without reflection. The scene implementation is MuseOrchestrator
    /// (RoboFactory.Logistics).
    /// </summary>
    public interface IMuseOrchestration
    {
        // ---- call state
        int StateVersion { get; }
        bool Pending { get; }                 // a Meta call is in flight
        int Calls { get; }
        string Mode { get; }                  // live | mock | offline
        string ModelUsed { get; }             // model actually used by the last answer
        int LastLatencyMs { get; }
        string LastValidation { get; }        // last validator verdict
        MuseDecisionRecord LastDecision { get; }
        WarehouseSnapshot LastSnapshot { get; }

        // ---- orders
        int SubmitOrder(string text);         // "A x3, B x2" -> order id (0 = unreadable)
        int SubmitOrderCounts(int a, int b, int c);
        WhOrderState[] OrdersSnapshot { get; }
        int ActiveOrderId { get; }
        void ClearOrders();

        // ---- urgent orders (safe preemption)
        /// <summary>FINAL Muse Voice transcription -> interpretation + /urgent plan.</summary>
        void SubmitUrgentTranscript(string transcript);
        /// <summary>Manual urgent button: same pipeline without speech recognition.</summary>
        int SubmitUrgentCounts(int a, int b, int c);
        string UrgentStatus { get; }          // urgent flow state for the HUD
        int UrgentOrderId { get; }            // -1 = no urgent order in progress
        int PausedCount { get; }              // paused normal orders
        int SuspendingCount { get; }          // orders being suspended

        // ---- world (read access through stable ids "P01","B02","T03","R1","R2")
        string DoorStateOf(string truckId);
        string RobotStatusOf(string robotId);
        string ActivePickPlan { get; }        // "P03 A x3 | P07 B x2" (then "P03 2/3" during the pick)
        string BenchAssigned { get; }
        string TruckAssigned { get; }
        int BoxesOnBenchForOrder(int orderId);
        int BoxesInTruckForOrder(string truckId, int orderId);
        Vector3 PositionOf(string resourceId);
        float MinRobotWorkerDistance(string robotId);

        // ---- driving
        void SetMode(string mode);            // live | mock | offline
        void RequestDecision(string trigger); // forces a Meta call now
        void InjectPlanError(string error);   // test: forces an invalid plan in mock mode
        string IncidentsText { get; }
    }
}
