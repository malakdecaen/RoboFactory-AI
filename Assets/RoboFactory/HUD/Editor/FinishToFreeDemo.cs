#if UNITY_EDITOR
using RoboFactory.Hackathon.Orchestration;
using RoboFactory.Logistics;
using UnityEditor;
using UnityEngine;

namespace RoboFactory.HUD.EditorTools
{
    /// <summary>
    /// Démonstration FINISH TO FREE (lancée À LA MAIN, en PlayMode) :
    ///  1. 5 commandes normales « A x2, B x1 » (décisions Muse Live réelles) ;
    ///  2. attente que les 3 camions soient réservés par des commandes qui ont DÉJÀ des cartons
    ///     engagés (préparés ou chargés) et qu'aucune ne soit complète — plus aucun couple libérable ;
    ///  3. commande urgente « A x2, B x1 » : l'orchestrateur passe en WAITING_FOR_CAPACITY et Muse
    ///     choisit la commande à finir. Rien n'est simulé : c'est le vrai pipeline.
    /// Menu : RoboFactory/Demo/…  (l'urgente peut aussi être dictée au micro au lieu du bouton).
    /// </summary>
    public static class FinishToFreeDemo
    {
        private static bool armed;
        private static double armedAt;
        private const double MaxWaitSeconds = 420;

        [MenuItem("RoboFactory/Demo/FINISH TO FREE : 5 normales puis urgente auto")]
        private static void Run()
        {
            if (!EditorApplication.isPlaying) { Debug.LogWarning("[Demo FTF] lancer le PlayMode d'abord."); return; }
            MuseOrchestrator o = Object.FindFirstObjectByType<MuseOrchestrator>();
            if (o == null) { Debug.LogWarning("[Demo FTF] MuseOrchestrator introuvable."); return; }
            for (int i = 0; i < 5; i++) o.SubmitOrder("A x2, B x1");
            Debug.Log("[Demo FTF] 5 commandes normales envoyées — urgente déclenchée dès que les 3 quais sont occupés " +
                      "par des commandes ayant des cartons engagés.");
            armed = true;
            armedAt = EditorApplication.timeSinceStartup;
            EditorApplication.update -= Watch;
            EditorApplication.update += Watch;
        }

        [MenuItem("RoboFactory/Demo/FINISH TO FREE : urgente maintenant")]
        private static void UrgentNow()
        {
            if (!EditorApplication.isPlaying) return;
            Fire("déclenchement manuel");
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
                Fire("délai d'attente dépassé — urgente envoyée dans l'état courant");
                return;
            }
            int committed = 0, trucks = 0;
            foreach (string id in w.TruckIds)
            {
                TruckBay bay = w.Truck(id);
                if (bay == null) continue;
                trucks++;
                if (bay.ReservedOrderId < 0) return;                       // un camion libre : pas encore le cas étudié
                WhOrderState order = null;
                foreach (WhOrderState s in o.OrdersSnapshot) if (s.id == bay.ReservedOrderId) order = s;
                if (order == null) return;
                int[] inTruck = bay.CountByTypeForOrder(order.id);
                int loaded = inTruck[0] + inTruck[1] + inTruck[2];
                int staged = order.staged_a + order.staged_b + order.staged_c;
                if (loaded >= order.a + order.b + order.c) return;        // complète : elle part d'elle-même
                if (loaded + staged > 0) committed++;
            }
            if (trucks > 0 && committed == trucks) Fire("3 bancs / 3 quais occupés par des commandes engagées");
        }

        private static void Fire(string why)
        {
            armed = false;
            EditorApplication.update -= Watch;
            MuseOrchestrator o = Object.FindFirstObjectByType<MuseOrchestrator>();
            if (o == null) return;
            int id = o.SubmitUrgentCounts(2, 1, 0);
            Debug.Log("[Demo FTF] urgente #" + id + " envoyée (" + why + ")");
        }
    }
}
#endif
