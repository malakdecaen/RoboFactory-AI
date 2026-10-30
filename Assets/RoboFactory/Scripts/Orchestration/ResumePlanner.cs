using System;
using System.Collections.Generic;

namespace RoboFactory.Hackathon.Orchestration
{
    /// <summary>
    /// Replanification locale après une commande urgente : tente de réutiliser le
    /// plan d'origine d'une commande suspendue au lieu de redemander une décision
    /// à Muse. Ne choisit JAMAIS de nouvelles ressources — il ne fait que réajuster
    /// les quantités des étapes sur les palettes que Muse avait déjà assignées,
    /// et le résultat repasse toujours par PlanValidator avant exécution.
    /// Retourne null quand l'ancienne affectation n'est plus réalisable.
    /// </summary>
    public static class ResumePlanner
    {
        /// <summary>
        /// Construit un plan de reprise pour 'order' à partir de 'oldPlan' sur
        /// l'état FRAIS 'snap'. L'ordre doit déjà être repassé en statut
        /// pending|waiting|planned pour que PlanValidator l'accepte.
        /// </summary>
        public static MusePlan TryReuse(MusePlan oldPlan, WhOrderState order, WarehouseSnapshot snap)
        {
            if (oldPlan == null || order == null || snap == null) return null;
            var plan = new MusePlan
            {
                action = "execute_order",
                order_id = order.id,
                robot_pick = "R1",
                robot_load = "R2",
                start_immediately = true,
                start_delay_s = 0f,
                reason = "reprise après préemption urgente (plan réutilisé)",
                fallback_pallets = oldPlan.fallback_pallets != null
                    ? (string[])oldPlan.fallback_pallets.Clone() : new string[0]
            };

            // ---- banc : s'il contient encore des cartons de la commande, il est
            // resté réservé et le plan doit le garder ; sinon le banc d'origine
            // doit être redevenu libre (ou un autre choix Muse sera nécessaire).
            string benchId = !string.IsNullOrEmpty(order.bench_id) ? order.bench_id : oldPlan.bench_id;
            WhBench bench = PlanValidator.FindBench(snap, benchId);
            if (bench == null) return null;
            bool benchKept = !string.IsNullOrEmpty(order.bench_id);
            if (!string.IsNullOrEmpty(bench.reserved_by) &&
                bench.reserved_by != order.id.ToString()) return null;
            plan.bench_id = benchId;

            // ---- camion : cartons déjà chargés -> même camion imposé (réservation
            // conservée) ; sinon le camion d'origine doit être libre à nouveau.
            string truckId = !string.IsNullOrEmpty(order.truck_id) ? order.truck_id : oldPlan.truck_id;
            WhTruck truck = PlanValidator.FindTruck(snap, truckId);
            if (truck == null) return null;
            bool truckKept = !string.IsNullOrEmpty(order.truck_id);
            if (!string.IsNullOrEmpty(truck.reserved_by) &&
                truck.reserved_by != order.id.ToString()) return null;
            int stillToStage = PlanValidator.RemainingTotal(order);
            int staged = order.staged_a + order.staged_b + order.staged_c;
            int needTruckSpace = stillToStage + staged;
            if (truck.free < needTruckSpace) return null;
            plan.truck_id = truckId;

            // ---- étapes : couvrir le besoin restant avec les palettes du plan
            // d'origine, dans leur ordre, plafonné par le stock réel actuel.
            int needA = PlanValidator.Remaining(order, "A");
            int needB = PlanValidator.Remaining(order, "B");
            int needC = PlanValidator.Remaining(order, "C");
            if (needA + needB + needC <= 0)
            {
                // Plus rien à prélever : plan "chargement seul" des cartons déjà
                // sur le banc (jamais si le banc n'a plus rien -> incohérent).
                if (staged <= 0 || !benchKept) return null;
                plan.picks = new MusePlanPick[0];
                plan.fallback_pallets = new string[0];
                return plan;
            }

            var legs = new List<MusePlanPick>();
            var usedPallets = new HashSet<string>();
            int[] needs = { needA, needB, needC };
            string[] types = { "A", "B", "C" };
            if (oldPlan.picks != null)
                foreach (MusePlanPick old in oldPlan.picks)
                {
                    if (old == null || string.IsNullOrEmpty(old.pallet_id)) continue;
                    int typeIndex = Array.IndexOf(types, (old.type ?? "").ToUpperInvariant());
                    if (typeIndex < 0 || needs[typeIndex] <= 0) continue;
                    WhPallet pallet = PlanValidator.FindPallet(snap, old.pallet_id);
                    if (pallet == null) return null;
                    // Palette réservée par une autre commande ou commande urgente : pas réutilisable.
                    if (!string.IsNullOrEmpty(pallet.reserved_by) &&
                        pallet.reserved_by != order.id.ToString()) return null;
                    int stock = pallet.stock != null ? pallet.stock.Of(types[typeIndex]) : 0;
                    int take = Math.Min(stock, needs[typeIndex]);
                    if (take <= 0) continue;
                    legs.Add(new MusePlanPick
                    {
                        pallet_id = old.pallet_id,
                        type = types[typeIndex],
                        count = take
                    });
                    usedPallets.Add(old.pallet_id);
                    needs[typeIndex] -= take;
                }
            if (needs[0] + needs[1] + needs[2] > 0) return null; // anciennes palettes insuffisantes

            // Garder uniquement les palettes de secours non réservées et hors picks.
            var keptFallback = new List<string>();
            foreach (string fb in plan.fallback_pallets ?? new string[0])
            {
                if (string.IsNullOrEmpty(fb) || usedPallets.Contains(fb)) continue;
                WhPallet p = PlanValidator.FindPallet(snap, fb);
                if (p == null || (!string.IsNullOrEmpty(p.reserved_by) &&
                    p.reserved_by != order.id.ToString())) continue;
                keptFallback.Add(fb);
            }
            plan.fallback_pallets = keptFallback.ToArray();
            plan.picks = legs.ToArray();
            return plan;
        }
    }
}
