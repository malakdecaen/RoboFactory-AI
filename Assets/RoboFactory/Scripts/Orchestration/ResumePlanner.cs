using System;
using System.Collections.Generic;

namespace RoboFactory.Hackathon.Orchestration
{
    /// <summary>
    /// Local replanning after an urgent order: tries to reuse the
    /// original plan of a suspended order instead of asking Muse for a new
    /// decision. NEVER chooses new resources — it only readjusts
    /// the step quantities on the pallets Muse had already assigned,
    /// and the result always goes through PlanValidator before execution.
    /// Returns null when the old assignment is no longer feasible.
    /// </summary>
    public static class ResumePlanner
    {
        /// <summary>
        /// Builds a resume plan for 'order' from 'oldPlan' on the
        /// FRESH state 'snap'. The order must already be back in status
        /// pending|waiting|planned for PlanValidator to accept it.
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
                reason = "resume after urgent preemption (plan reused)",
                fallback_pallets = oldPlan.fallback_pallets != null
                    ? (string[])oldPlan.fallback_pallets.Clone() : new string[0]
            };

            // ---- bench: if it still holds boxes of the order, it has
            // stayed reserved and the plan must keep it; otherwise the original bench
            // must be free again (or another Muse choice will be needed).
            string benchId = !string.IsNullOrEmpty(order.bench_id) ? order.bench_id : oldPlan.bench_id;
            WhBench bench = PlanValidator.FindBench(snap, benchId);
            if (bench == null) return null;
            bool benchKept = !string.IsNullOrEmpty(order.bench_id);
            if (!string.IsNullOrEmpty(bench.reserved_by) &&
                bench.reserved_by != order.id.ToString()) return null;
            plan.bench_id = benchId;

            // ---- truck: boxes already loaded -> same truck imposed (reservation
            // kept); otherwise the original truck must be free again.
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

            // ---- steps: cover the remaining need with the pallets of the original
            // plan, in their order, capped by the current real stock.
            int needA = PlanValidator.Remaining(order, "A");
            int needB = PlanValidator.Remaining(order, "B");
            int needC = PlanValidator.Remaining(order, "C");
            if (needA + needB + needC <= 0)
            {
                // Nothing left to pick: "load only" plan for the boxes already
                // on the bench (never if the bench has nothing left -> inconsistent).
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
                    // Pallet reserved by another order or an urgent order: not reusable.
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
            if (needs[0] + needs[1] + needs[2] > 0) return null; // old pallets insufficient

            // Keep only the fallback pallets that are unreserved and not in the picks.
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
