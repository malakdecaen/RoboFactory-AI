using System;
using System.Collections.Generic;
using UnityEngine;

namespace RoboFactory.Hackathon.Orchestration
{
    /// <summary>
    /// Types purs pour l'orchestration Muse Wonder : aucun lien avec les assets Warcool,
    /// donc testables en EditMode. Le snapshot est construit côté scène (RoboFactory.Logistics)
    /// puis envoyé au proxy Meta ; le plan reçu est validé par PlanValidator avant exécution.
    /// </summary>

    // ------------------------------------------------------------------ plan (réponse Muse)

    [Serializable]
    public sealed class MusePlanPick
    {
        public string pallet_id;
        public string type;     // "A" | "B" | "C"
        public int count;
    }

    [Serializable]
    public sealed class MusePlan
    {
        public string action;            // execute_order | wait | defer_order | no_plan
        public int order_id;
        public MusePlanPick[] picks;
        public string[] fallback_pallets;
        public string bench_id;
        public string truck_id;
        public string robot_pick;
        public string robot_load;
        public bool start_immediately;
        public float start_delay_s;
        public string reason;
    }

    // ------------------------------------------------------------------ snapshot (envoyé à Muse)

    [Serializable]
    public sealed class WhOrderState
    {
        public int id;
        public int a, b, c;                    // demandé par type
        public int staged_a, staged_b, staged_c; // préparé sur le banc
        public int loaded_a, loaded_b, loaded_c; // déjà dans le camion
        public int priority;
        public bool urgent;                    // commande urgente (voix ou manuelle)
        public string status;                  // pending|planned|picking|staging|loading|loaded|waiting|blocked|suspending|paused
        public string bench_id;                // affectation courante (null/"" sinon)
        public string truck_id;
    }

    [Serializable]
    public sealed class WhStock
    {
        public int a, b, c;

        public int Of(string type)
        {
            switch ((type ?? "").ToUpperInvariant())
            {
                case "A": return a;
                case "B": return b;
                case "C": return c;
                default: return 0;
            }
        }
    }

    [Serializable]
    public sealed class WhPallet
    {
        public string id;
        public float x, z;
        public WhStock stock;   // contenu total réel par type
        public WhStock top;     // cartons directement prenables (dessus des colonnes)
        public string reserved_by;
        public float dist_r1;
    }

    [Serializable]
    public sealed class WhBench
    {
        public string id;
        public float x, z;
        public int capacity;
        public int free_levels;
        public int occupied;
        public string reserved_by; // id de commande (string) ou null
    }

    [Serializable]
    public sealed class WhTruck
    {
        public string id;
        public float x, z;
        public int capacity;
        public int loaded;
        public int free;
        public string door;        // Closed|Opening|Open|Ready|Loading|Closing|Loaded
        public string reserved_by;
    }

    [Serializable]
    public sealed class WhRobot
    {
        public string id;          // R1 | R2
        public float x, z;
        public string state;
        public bool carrying;
        public bool available;   // le robot fonctionne (peut recevoir une mission en file)
        public bool busy;        // mission dirigée en cours : la prochaine attendra son tour
        public string mission;
    }

    [Serializable]
    public sealed class WhDistance
    {
        public string from;
        public string to;
        public float meters;
        public float eta_s;
    }

    [Serializable]
    public sealed class WarehouseSnapshot
    {
        public int state_version;
        public string trigger;
        public string mode;
        public WhOrderState[] orders;
        public WhPallet[] pallets;
        public WhBench[] benches;
        public WhTruck[] trucks;
        public WhRobot[] robots;
        public WhDistance[] distances;
        public string[] incidents;
        public string last_rejection;
        public string box_type_legend; // "A=caisse bois, B=gros carton, C=carton moyen"
    }

    // ------------------------------------------------------------------ enregistrements

    [Serializable]
    public sealed class MuseDecisionRecord
    {
        public string RequestId;
        public string Trigger;
        public string Mode;
        public string Model;
        public string Action;
        public string Reason;
        public string RejectReason;
        public string AppliedResult;
        public int OrderId;
        public int StateVersion;
        public int LatencyMs;
        public bool Accepted;
        public string PlanSummary;
        public float Time;
    }
}
