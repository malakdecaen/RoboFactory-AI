using System;
using System.Collections.Generic;

namespace RoboFactory.Hackathon.Orchestration
{
    /// <summary>
    /// Validateur local des plans Muse Wonder — logique pure, testable en EditMode.
    /// Un plan refusé renvoie la liste des motifs ; le plan accepté est ensuite
    /// exécuté par l'orchestrateur qui réserve les ressources.
    /// </summary>
    public static class PlanValidator
    {
        public static readonly string[] KnownActions = { "execute_order", "wait", "defer_order", "no_plan" };

        /// <summary>Premier motif de rejet, ou null si le plan est exécutable.</summary>
        public static string FirstError(MusePlan plan, WarehouseSnapshot snap)
        {
            List<string> errors = Validate(plan, snap);
            return errors.Count > 0 ? errors[0] : null;
        }

        /// <summary>Tous les motifs de rejet (liste vide = plan valide).</summary>
        public static List<string> Validate(MusePlan plan, WarehouseSnapshot snap)
        {
            var errors = new List<string>();
            if (plan == null) { errors.Add("plan vide"); return errors; }
            if (snap == null) { errors.Add("état d'entrepôt vide"); return errors; }

            string action = (plan.action ?? "").Trim().ToLowerInvariant();
            if (Array.IndexOf(KnownActions, action) < 0)
                errors.Add("action inconnue: " + plan.action);

            if (action == "defer_order")
            {
                if (FindOrder(snap, plan.order_id) == null)
                    errors.Add("defer_order sur commande inexistante: " + plan.order_id);
                return errors;
            }
            if (action != "execute_order") return errors; // wait / no_plan : rien à valider

            // ---- commande
            WhOrderState order = FindOrder(snap, plan.order_id);
            if (order == null)
            {
                errors.Add("commande inexistante: " + plan.order_id);
                return errors;
            }
            if (order.status != "pending" && order.status != "waiting" && order.status != "planned")
            {
                errors.Add("commande #" + plan.order_id + " non planifiable (statut " + order.status + ")");
                return errors;
            }
            int needA = Remaining(order, "A"), needB = Remaining(order, "B"), needC = Remaining(order, "C");
            int needed = needA + needB + needC;
            int staged = order.staged_a + order.staged_b + order.staged_c;
            if (needed <= 0 && staged <= 0)
            {
                errors.Add("commande #" + plan.order_id + " déjà complète");
                return errors;
            }

            // Une commande déjà préparée garde ses affectations : déplacer son stock
            // vers un autre banc ou ses cartons chargés vers un autre camion est interdit.
            if (!string.IsNullOrEmpty(order.bench_id) && plan.bench_id != order.bench_id)
                errors.Add("commande #" + plan.order_id + " déjà affectée au banc " + order.bench_id);
            if (!string.IsNullOrEmpty(order.truck_id) &&
                order.loaded_a + order.loaded_b + order.loaded_c > 0 &&
                plan.truck_id != order.truck_id)
                errors.Add("commande #" + plan.order_id + " déjà partiellement chargée dans " + order.truck_id);

            // ---- prélèvements
            // Plan "chargement seul" : tout est déjà préparé sur le banc (reprise
            // après suspension) — aucune étape de collecte n'est alors exigée.
            if (plan.picks == null || plan.picks.Length == 0)
            {
                if (needed > 0 || staged <= 0)
                {
                    errors.Add("execute_order sans aucun prélèvement");
                    return errors;
                }
            }
            else
            {
                int sumA = 0, sumB = 0, sumC = 0;
                var seenLegs = new HashSet<string>();
                foreach (MusePlanPick leg in plan.picks)
                {
                    if (leg == null) { errors.Add("étape de prélèvement nulle"); continue; }
                    string palletId = leg.pallet_id ?? "";
                    string type = (leg.type ?? "").ToUpperInvariant();
                    WhPallet pallet = FindPallet(snap, palletId);
                    if (pallet == null)
                    {
                        errors.Add("palette inexistante: " + palletId);
                        continue;
                    }
                    if (pallet.reserved_by != null && pallet.reserved_by != "" &&
                        pallet.reserved_by != plan.order_id.ToString())
                        errors.Add("palette " + palletId + " déjà réservée par " + pallet.reserved_by);
                    if (type != "A" && type != "B" && type != "C")
                    {
                        errors.Add("type de carton invalide sur " + palletId + ": " + leg.type);
                        continue;
                    }
                    if (leg.count <= 0)
                    {
                        errors.Add("quantité <= 0 sur " + palletId);
                        continue;
                    }
                    int stock = pallet.stock != null ? pallet.stock.Of(type) : 0;
                    if (stock < leg.count)
                        errors.Add("palette " + palletId + " : stock " + type + " insuffisant (" + stock + " < " + leg.count + ")");
                    string legKey = palletId + "|" + type;
                    if (!seenLegs.Add(legKey))
                        errors.Add("même source réservée deux fois: " + legKey);
                    if (type == "A") sumA += leg.count;
                    else if (type == "B") sumB += leg.count;
                    else sumC += leg.count;
                }
                if (sumA != needA) errors.Add("couverture A invalide: prévu " + sumA + " pour un besoin de " + needA);
                if (sumB != needB) errors.Add("couverture B invalide: prévu " + sumB + " pour un besoin de " + needB);
                if (sumC != needC) errors.Add("couverture C invalide: prévu " + sumC + " pour un besoin de " + needC);
            }

            // ---- palettes de secours
            if (plan.fallback_pallets != null)
            {
                var declared = new HashSet<string>();
                if (plan.picks != null)
                    foreach (MusePlanPick leg in plan.picks)
                        if (leg != null) declared.Add(leg.pallet_id);
                var seenFb = new HashSet<string>();
                foreach (string fb in plan.fallback_pallets)
                {
                    if (string.IsNullOrEmpty(fb)) continue;
                    if (FindPallet(snap, fb) == null)
                        errors.Add("palette de secours inexistante: " + fb);
                    else
                    {
                        WhPallet p = FindPallet(snap, fb);
                        if (p.reserved_by != null && p.reserved_by != "" && p.reserved_by != plan.order_id.ToString())
                            errors.Add("palette de secours " + fb + " réservée par " + p.reserved_by);
                    }
                    if (declared.Contains(fb))
                        errors.Add("palette de secours " + fb + " déjà dans les prélèvements");
                    if (!seenFb.Add(fb))
                        errors.Add("palette de secours dupliquée: " + fb);
                }
            }

            // ---- banc
            WhBench bench = FindBench(snap, plan.bench_id);
            if (bench == null)
            {
                errors.Add("banc inexistant: " + plan.bench_id);
            }
            else
            {
                if (bench.reserved_by != null && bench.reserved_by != "" &&
                    bench.reserved_by != plan.order_id.ToString())
                    errors.Add("banc " + plan.bench_id + " réservé par la commande " + bench.reserved_by);
                if (bench.free_levels < 1)
                    errors.Add("banc " + plan.bench_id + " indisponible (aucun niveau libre)");
            }

            // ---- camion
            WhTruck truck = FindTruck(snap, plan.truck_id);
            if (truck == null)
            {
                errors.Add("camion inexistant: " + plan.truck_id);
            }
            else
            {
                if (truck.reserved_by != null && truck.reserved_by != "" &&
                    truck.reserved_by != plan.order_id.ToString())
                    errors.Add("camion " + plan.truck_id + " réservé par la commande " + truck.reserved_by);
                // Les cartons préparés doivent AUSSI monter dans le camion : la
                // capacité vérifiée couvre ce qu'il reste à prélever + le banc.
                int truckNeed = needed + staged;
                if (truck.free < truckNeed)
                    errors.Add("camion " + plan.truck_id + " : capacité insuffisante (" + truck.free + " < " + truckNeed + ")");
            }

            // ---- robots
            if (FindRobot(snap, plan.robot_pick) == null)
                errors.Add("robot de collecte inexistant: " + plan.robot_pick);
            else if (!FindRobot(snap, plan.robot_pick).available)
                errors.Add("robot " + plan.robot_pick + " indisponible");
            if (FindRobot(snap, plan.robot_load) == null)
                errors.Add("robot de chargement inexistant: " + plan.robot_load);
            else if (!FindRobot(snap, plan.robot_load).available)
                errors.Add("robot " + plan.robot_load + " indisponible");
            if (!string.IsNullOrEmpty(plan.robot_pick) && plan.robot_pick == plan.robot_load)
                errors.Add("même robot affecté aux deux missions");

            return errors;
        }

        public static int Remaining(WhOrderState order, string type)
        {
            switch ((type ?? "").ToUpperInvariant())
            {
                case "A": return Math.Max(0, order.a - order.staged_a - order.loaded_a);
                case "B": return Math.Max(0, order.b - order.staged_b - order.loaded_b);
                case "C": return Math.Max(0, order.c - order.staged_c - order.loaded_c);
                default: return 0;
            }
        }

        public static int RemainingTotal(WhOrderState order)
        {
            return Remaining(order, "A") + Remaining(order, "B") + Remaining(order, "C");
        }

        public static WhOrderState FindOrder(WarehouseSnapshot snap, int id)
        {
            if (snap == null || snap.orders == null) return null;
            foreach (WhOrderState o in snap.orders) if (o != null && o.id == id) return o;
            return null;
        }

        public static WhPallet FindPallet(WarehouseSnapshot snap, string id)
        {
            if (snap == null || snap.pallets == null) return null;
            foreach (WhPallet p in snap.pallets) if (p != null && p.id == id) return p;
            return null;
        }

        public static WhBench FindBench(WarehouseSnapshot snap, string id)
        {
            if (snap == null || snap.benches == null) return null;
            foreach (WhBench b in snap.benches) if (b != null && b.id == id) return b;
            return null;
        }

        public static WhTruck FindTruck(WarehouseSnapshot snap, string id)
        {
            if (snap == null || snap.trucks == null) return null;
            foreach (WhTruck t in snap.trucks) if (t != null && t.id == id) return t;
            return null;
        }

        public static WhRobot FindRobot(WarehouseSnapshot snap, string id)
        {
            if (snap == null || snap.robots == null) return null;
            foreach (WhRobot r in snap.robots) if (r != null && r.id == id) return r;
            return null;
        }
    }
}
