#if UNITY_EDITOR
using RoboFactory.Hackathon.Orchestration;
using RoboFactory.Logistics;
using UnityEditor;
using UnityEngine;

namespace RoboFactory.HUD.EditorTools
{
    /// <summary>
    /// FINISH TO FREE demonstration (launched BY HAND, in PlayMode):
    ///  1. 5 normal orders « A x2, B x1 » (real Muse Live decisions);
    ///  2. wait until the 3 trucks are reserved by orders that ALREADY have boxes
    ///     committed (staged or loaded) and none is complete — no freeable pair left;
    ///  3. urgent order « A x2, B x1 »: the orchestrator switches to WAITING_FOR_CAPACITY and Muse
    ///     chooses the order to finish. Nothing is simulated: it is the real pipeline.
    /// Menu: RoboFactory/Demo/…  (the urgent order can also be dictated to the microphone instead of the button).
    /// </summary>
    public static class FinishToFreeDemo
    {
        private static bool armed;
        private static double armedAt;
        private const double MaxWaitSeconds = 420;

        [MenuItem("RoboFactory/Demo/FINISH TO FREE: 5 normal orders then automatic urgent")]
        private static void Run()
        {
            if (!EditorApplication.isPlaying) { Debug.LogWarning("[Demo FTF] start PlayMode first."); return; }
            MuseOrchestrator o = Object.FindFirstObjectByType<MuseOrchestrator>();
            if (o == null) { Debug.LogWarning("[Demo FTF] MuseOrchestrator not found."); return; }
            for (int i = 0; i < 5; i++) o.SubmitOrder("A x2, B x1");
            Debug.Log("[Demo FTF] 5 normal orders sent — urgent order triggered as soon as the 3 docks are busy " +
                      "with orders that have committed boxes.");
            armed = true;
            armedAt = EditorApplication.timeSinceStartup;
            EditorApplication.update -= Watch;
            EditorApplication.update += Watch;
        }

        [MenuItem("RoboFactory/Demo/FINISH TO FREE: urgent now")]
        private static void UrgentNow()
        {
            if (!EditorApplication.isPlaying) return;
            Fire("manual trigger");
        }

        private static void Watch()
        {
            if (!armed) { EditorApplication.update -= Watch; return; }
            if (!EditorApplication.isPlaying) { armed = false; return; }
            MuseOrchestrator o = Object.FindFirstObjectByType<MuseOrchestrator>();
            WarehouseWorld w = WarehouseWorld.Instance != null ? WarehouseWorld.Instance : Object.FindFirstObjectByType<WarehouseWorld>();
            if (o == null || w == null) return;
            if (EditorApplication.timeSinceStartup - armedAt > MaxWaitSeconds)
            {
                Fire("wait timeout exceeded — urgent order sent in the current state");
                return;
            }
            int committed = 0, trucks = 0;
            foreach (string id in w.TruckIds)
            {
                TruckBay bay = w.Truck(id);
                if (bay == null) continue;
                trucks++;
                if (bay.ReservedOrderId < 0) return;                       // a free truck: not the studied case yet
                WhOrderState order = null;
                foreach (WhOrderState s in o.OrdersSnapshot) if (s.id == bay.ReservedOrderId) order = s;
                if (order == null) return;
                int[] inTruck = bay.CountByTypeForOrder(order.id);
                int loaded = inTruck[0] + inTruck[1] + inTruck[2];
                int staged = order.staged_a + order.staged_b + order.staged_c;
                if (loaded >= order.a + order.b + order.c) return;        // complete: it leaves by itself
                if (loaded + staged > 0) committed++;
            }
            if (trucks > 0 && committed == trucks) Fire("3 benches / 3 docks busy with committed orders");
        }

        private static void Fire(string why)
        {
            armed = false;
            EditorApplication.update -= Watch;
            MuseOrchestrator o = Object.FindFirstObjectByType<MuseOrchestrator>();
            if (o == null) return;
            int id = o.SubmitUrgentCounts(2, 1, 0);
            Debug.Log("[Demo FTF] urgent #" + id + " sent (" + why + ")");
        }
    }
}
#endif
