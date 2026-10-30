using UnityEngine;

namespace RoboFactory.Hackathon.Orchestration
{
    /// <summary>
    /// Façade publique de l'orchestrateur Muse Wonder. Elle vit dans l'assembly Hackathon
    /// pour que les tests PlayMode (qui ne voient pas Assembly-CSharp) pilotent l'entrepôt
    /// via l'interface, sans réflexion. L'implémentation scène est MuseOrchestrator
    /// (RoboFactory.Logistics).
    /// </summary>
    public interface IMuseOrchestration
    {
        // ---- état des appels
        int StateVersion { get; }
        bool Pending { get; }                 // un appel Meta est en vol
        int Calls { get; }
        string Mode { get; }                  // live | mock | offline
        string ModelUsed { get; }             // modèle réellement utilisé par la dernière réponse
        int LastLatencyMs { get; }
        string LastValidation { get; }        // dernier verdict du validateur
        MuseDecisionRecord LastDecision { get; }
        WarehouseSnapshot LastSnapshot { get; }

        // ---- commandes
        int SubmitOrder(string text);         // "A x3, B x2" -> id de commande (0 = illisible)
        int SubmitOrderCounts(int a, int b, int c);
        WhOrderState[] OrdersSnapshot { get; }
        int ActiveOrderId { get; }
        void ClearOrders();

        // ---- commandes urgentes (préemption sécurisée)
        /// <summary>Transcription FINALE de Muse Voice -> interprétation + plan /urgent.</summary>
        void SubmitUrgentTranscript(string transcript);
        /// <summary>Bouton manuel d'urgence : même pipeline sans reconnaissance vocale.</summary>
        int SubmitUrgentCounts(int a, int b, int c);
        string UrgentStatus { get; }          // état du flux urgent pour le HUD
        int UrgentOrderId { get; }            // -1 = aucune urgente en cours
        int PausedCount { get; }              // commandes normales en pause
        int SuspendingCount { get; }          // commandes en cours de suspension

        // ---- monde (accès lecture via ids stables "P01","B02","T03","R1","R2")
        string DoorStateOf(string truckId);
        string RobotStatusOf(string robotId);
        string ActivePickPlan { get; }        // "P03 A x3 (reste 2) | P07 B x2"
        string BenchAssigned { get; }
        string TruckAssigned { get; }
        int BoxesOnBenchForOrder(int orderId);
        int BoxesInTruckForOrder(string truckId, int orderId);
        Vector3 PositionOf(string resourceId);
        float MinRobotWorkerDistance(string robotId);

        // ---- pilotage
        void SetMode(string mode);            // live | mock | offline
        void RequestDecision(string trigger); // force un appel Meta maintenant
        void InjectPlanError(string error);   // test : force un plan invalide en mode mock
        string IncidentsText { get; }
    }
}
