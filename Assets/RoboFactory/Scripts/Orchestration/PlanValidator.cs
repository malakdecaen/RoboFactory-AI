using System;
using System.Collections.Generic;

namespace RoboFactory.Hackathon.Orchestration
{
    /// <summary>
    /// Local validator of Muse Wonder plans — pure logic, testable in EditMode.
    /// A refused plan returns the list of reasons; the accepted plan is then
    /// executed by the orchestrator, which reserves the resources.
    /// </summary>
    public static class PlanValidator
    {
        public static readonly string[] KnownActions = { "execute_order", "wait", "defer_order", "no_plan" };

        /// <summary>First rejection reason, or null if the plan is executable.</summary>
        public static string FirstError(MusePlan plan, WarehouseSnapshot snap)
        {
            List<string> errors = Validate(plan, snap);
            return errors.Count > 0 ? errors[0] : null;
        }

        /// <summary>All rejection reasons (empty list = valid plan).</summary>
        public static List<string> Validate(MusePlan plan, WarehouseSnapshot snap)
        {
            var errors = new List<string>();
            if (plan == null) { errors.Add("empty plan"); return errors; }
            if (snap == null) { errors.Add("empty warehouse state"); return errors; }

            string action = (plan.action ?? "").Trim().ToLowerInvariant();
            if (Array.IndexOf(KnownActions, action) < 0)
                errors.Add("unknown action: " + plan.action);

            if (action == "defer_order")
            {
                if (FindOrder(snap, plan.order_id) == null)
                    errors.Add("defer_order on unknown order: " + plan.order_id);
                return errors;
            }
            if (action != "execute_order") return errors; // wait / no_plan: nothing to validate

            // ---- order
            WhOrderState order = FindOrder(snap, plan.order_id);
            if (order == null)
            {
                errors.Add("unknown order: " + plan.order_id);
                return errors;
            }
            if (order.status != "pending" && order.status != "waiting" && order.status != "planned")
            {
                errors.Add("order #" + plan.order_id + " not plannable (status " + order.status + ")");
                return errors;
            }
            int needA = Remaining(order, "A"), needB = Remaining(order, "B"), needC = Remaining(order, "C");
            int needed = needA + needB + needC;
            int staged = order.staged_a + order.staged_b + order.staged_c;
            if (needed <= 0 && staged <= 0)
            {
                errors.Add("order #" + plan.order_id + " already complete");
                return errors;
            }

            // An order already staged keeps its assignments: moving its stock
            // to another bench or its loaded boxes to another truck is forbidden.
            if (!string.IsNullOrEmpty(order.bench_id) && plan.bench_id != order.bench_id)
                errors.Add("order #" + plan.order_id + " already assigned to bench " + order.bench_id);
            if (!string.IsNullOrEmpty(order.truck_id) &&
                order.loaded_a + order.loaded_b + order.loaded_c > 0 &&
                plan.truck_id != order.truck_id)
                errors.Add("order #" + plan.order_id + " already partially loaded in " + order.truck_id);

            // ---- picks
            // "Load only" plan: everything is already staged on the bench (resume
            // after suspension) — no pick step is required then.
            if (plan.picks == null || plan.picks.Length == 0)
            {
                if (needed > 0 || staged <= 0)
                {
                    errors.Add("execute_order without any pick");
                    return errors;
                }
            }
            else
            {
                int sumA = 0, sumB = 0, sumC = 0;
                var seenLegs = new HashSet<string>();
                foreach (MusePlanPick leg in plan.picks)
                {
                    if (leg == null) { errors.Add("null pick step"); continue; }
                    string palletId = leg.pallet_id ?? "";
                    string type = (leg.type ?? "").ToUpperInvariant();
                    WhPallet pallet = FindPallet(snap, palletId);
                    if (pallet == null)
                    {
                        errors.Add("unknown pallet: " + palletId);
                        continue;
                    }
                    if (pallet.reserved_by != null && pallet.reserved_by != "" &&
                        pallet.reserved_by != plan.order_id.ToString())
                        errors.Add("pallet " + palletId + " already reserved by " + pallet.reserved_by);
                    if (type != "A" && type != "B" && type != "C")
                    {
                        errors.Add("invalid box type on " + palletId + ": " + leg.type);
                        continue;
                    }
                    if (leg.count <= 0)
                    {
                        errors.Add("quantity <= 0 on " + palletId);
                        continue;
                    }
                    int stock = pallet.stock != null ? pallet.stock.Of(type) : 0;
                    if (stock < leg.count)
                        errors.Add("pallet " + palletId + ": insufficient " + type + " stock (" + stock + " < " + leg.count + ")");
                    string legKey = palletId + "|" + type;
                    if (!seenLegs.Add(legKey))
                        errors.Add("same source reserved twice: " + legKey);
                    if (type == "A") sumA += leg.count;
                    else if (type == "B") sumB += leg.count;
                    else sumC += leg.count;
                }
                if (sumA != needA) errors.Add("invalid coverage A: planned " + sumA + " for a need of " + needA);
                if (sumB != needB) errors.Add("invalid coverage B: planned " + sumB + " for a need of " + needB);
                if (sumC != needC) errors.Add("invalid coverage C: planned " + sumC + " for a need of " + needC);
            }

            // ---- fallback pallets
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
                        errors.Add("unknown fallback pallet: " + fb);
                    else
                    {
                        WhPallet p = FindPallet(snap, fb);
                        if (p.reserved_by != null && p.reserved_by != "" && p.reserved_by != plan.order_id.ToString())
                            errors.Add("fallback pallet " + fb + " reserved by " + p.reserved_by);
                    }
                    if (declared.Contains(fb))
                        errors.Add("fallback pallet " + fb + " already in the picks");
                    if (!seenFb.Add(fb))
                        errors.Add("duplicated fallback pallet: " + fb);
                }
            }

            // ---- bench
            WhBench bench = FindBench(snap, plan.bench_id);
            if (bench == null)
            {
                errors.Add("unknown bench: " + plan.bench_id);
            }
            else
            {
                if (bench.reserved_by != null && bench.reserved_by != "" &&
                    bench.reserved_by != plan.order_id.ToString())
                    errors.Add("bench " + plan.bench_id + " reserved by order " + bench.reserved_by);
                if (bench.free_levels < 1)
                    errors.Add("bench " + plan.bench_id + " unavailable (no free level)");
            }

            // ---- truck
            WhTruck truck = FindTruck(snap, plan.truck_id);
            if (truck == null)
            {
                errors.Add("unknown truck: " + plan.truck_id);
            }
            else
            {
                if (truck.reserved_by != null && truck.reserved_by != "" &&
                    truck.reserved_by != plan.order_id.ToString())
                    errors.Add("truck " + plan.truck_id + " reserved by order " + truck.reserved_by);
                // The staged boxes must ALSO go into the truck: the
                // checked capacity covers what is left to pick + the bench.
                int truckNeed = needed + staged;
                if (truck.free < truckNeed)
                    errors.Add("truck " + plan.truck_id + ": insufficient capacity (" + truck.free + " < " + truckNeed + ")");
            }

            // ---- robots
            if (FindRobot(snap, plan.robot_pick) == null)
                errors.Add("unknown pick robot: " + plan.robot_pick);
            else if (!FindRobot(snap, plan.robot_pick).available)
                errors.Add("robot " + plan.robot_pick + " unavailable");
            if (FindRobot(snap, plan.robot_load) == null)
                errors.Add("unknown load robot: " + plan.robot_load);
            else if (!FindRobot(snap, plan.robot_load).available)
                errors.Add("robot " + plan.robot_load + " unavailable");
            if (!string.IsNullOrEmpty(plan.robot_pick) && plan.robot_pick == plan.robot_load)
                errors.Add("same robot assigned to both missions");

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
