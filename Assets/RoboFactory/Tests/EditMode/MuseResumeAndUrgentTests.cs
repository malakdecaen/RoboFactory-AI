using NUnit.Framework;
using RoboFactory.Hackathon.Orchestration;

namespace RoboFactory.Hackathon.Tests
{
    /// <summary>
    /// Resume after an urgent order: "load only" plans in the validator,
    /// truck capacity including the staged boxes, suspending/paused statuses
    /// not plannable, and reuse of the original plan by ResumePlanner.
    /// </summary>
    public sealed class MuseResumeAndUrgentTests
    {
        private static WarehouseSnapshot Snap()
        {
            return new WarehouseSnapshot
            {
                state_version = 9,
                orders = new[]
                {
                    // Order suspended then back to pending: 1A staged,
                    // 1A loaded, 1A + 1B still to pick.
                    new WhOrderState
                    {
                        id = 1, a = 3, b = 1, c = 0,
                        staged_a = 1, loaded_a = 1,
                        priority = 1, status = "pending",
                        bench_id = "B01", truck_id = "T01"
                    },
                },
                pallets = new[]
                {
                    new WhPallet { id = "P01", stock = new WhStock { a = 4 } },
                    new WhPallet { id = "P02", stock = new WhStock { b = 3 } },
                    new WhPallet { id = "P03", stock = new WhStock { a = 2, b = 2 } },
                },
                benches = new[]
                {
                    new WhBench { id = "B01", capacity = 6, free_levels = 5, reserved_by = "1" },
                    new WhBench { id = "B02", capacity = 6, free_levels = 6 },
                },
                trucks = new[]
                {
                    new WhTruck { id = "T01", capacity = 10, loaded = 1, free = 9,
                                  door = "Closed", reserved_by = "1" },
                    new WhTruck { id = "T02", capacity = 10, loaded = 0, free = 10,
                                  door = "Closed" },
                },
                robots = new[]
                {
                    new WhRobot { id = "R1", available = true },
                    new WhRobot { id = "R2", available = true },
                }
            };
        }

        private static MusePlan OldPlan()
        {
            return new MusePlan
            {
                action = "execute_order",
                order_id = 1,
                picks = new[]
                {
                    new MusePlanPick { pallet_id = "P01", type = "A", count = 2 },
                    new MusePlanPick { pallet_id = "P02", type = "B", count = 1 },
                },
                fallback_pallets = new[] { "P03" },
                bench_id = "B01",
                truck_id = "T01",
                robot_pick = "R1",
                robot_load = "R2",
                start_immediately = true
            };
        }

        // ------------------------------------------------------------------ validateur

        [Test]
        public void LoadOnlyPlan_StagedOrder_Passes()
        {
            var snap = Snap();
            snap.orders[0].a = 2;             // 1 staged + 1 loaded = all covered
            snap.orders[0].b = 0;             // (otherwise B would still be to pick)
            snap.orders[0].staged_a = 1;      // 1A left on the bench to load
            var p = OldPlan();
            p.picks = new MusePlanPick[0];    // plan "chargement seul"
            p.fallback_pallets = new string[0];
            Assert.IsNull(PlanValidator.FirstError(p, snap));
        }

        [Test]
        public void EmptyPicks_NothingStaged_Rejected()
        {
            var snap = Snap();
            snap.orders[0].staged_a = 0;      // nothing staged: picks are needed
            var p = OldPlan();
            p.picks = new MusePlanPick[0];
            StringAssert.Contains("without any pick", PlanValidator.FirstError(p, snap));
        }

        [Test]
        public void TruckCapacity_IncludesStaged()
        {
            var snap = Snap();
            snap.trucks[0].free = 2; // need 2 (1A+1B) + 1 staged = 3 > 2
            var p = OldPlan();
            p.picks = new[]
            {
                new MusePlanPick { pallet_id = "P01", type = "A", count = 1 },
                new MusePlanPick { pallet_id = "P02", type = "B", count = 1 },
            };
            StringAssert.Contains("insufficient capacity", PlanValidator.FirstError(p, snap));
            snap.trucks[0].free = 3; // just enough: 2 to pick + 1 staged
            Assert.IsNull(PlanValidator.FirstError(p, snap));
        }

        [Test]
        public void SuspendingOrder_NotPlanifiable()
        {
            var snap = Snap();
            snap.orders[0].status = "suspending";
            StringAssert.Contains("not plannable",
                PlanValidator.FirstError(OldPlan(), snap));
        }

        [Test]
        public void PausedOrder_NotPlanifiable()
        {
            var snap = Snap();
            snap.orders[0].status = "paused";
            StringAssert.Contains("not plannable",
                PlanValidator.FirstError(OldPlan(), snap));
        }

        [Test]
        public void UrgentPlan_AvoidsReservedResources()
        {
            var snap = Snap();
            // Urgent order #9: same state, the resources of #1 stay taken.
            var urgent = new WhOrderState { id = 9, b = 1, priority = 0, urgent = true, status = "pending" };
            var orders = new WhOrderState[snap.orders.Length + 1];
            snap.orders.CopyTo(orders, 0);
            orders[snap.orders.Length] = urgent;
            snap.orders = orders;
            var p = new MusePlan
            {
                action = "execute_order", order_id = 9,
                picks = new[] { new MusePlanPick { pallet_id = "P02", type = "B", count = 1 } },
                bench_id = "B02", truck_id = "T02",
                robot_pick = "R1", robot_load = "R2", start_immediately = true
            };
            Assert.IsNull(PlanValidator.FirstError(p, snap));
            p.bench_id = "B01"; // reserved by the suspended order #1
            StringAssert.Contains("reserved by", PlanValidator.FirstError(p, snap));
            p.bench_id = "B02";
            p.truck_id = "T01"; // reserved by #1 (boxes already loaded)
            StringAssert.Contains("reserved by", PlanValidator.FirstError(p, snap));
        }

        // ------------------------------------------------------------------ ResumePlanner

        [Test]
        public void TryReuse_ReusesPlan_WithAdjustedCounts()
        {
            var snap = Snap();
            var order = snap.orders[0];
            var reuse = ResumePlanner.TryReuse(OldPlan(), order, snap);
            Assert.IsNotNull(reuse);
            Assert.AreEqual("execute_order", reuse.action);
            Assert.AreEqual(1, reuse.order_id);
            Assert.AreEqual("B01", reuse.bench_id);
            Assert.AreEqual("T01", reuse.truck_id);
            // Real need: 1A + 1B, the steps are capped to the need.
            Assert.AreEqual(2, reuse.picks.Length);
            Assert.AreEqual("P01", reuse.picks[0].pallet_id);
            Assert.AreEqual(1, reuse.picks[0].count);
            Assert.AreEqual("P02", reuse.picks[1].pallet_id);
            Assert.AreEqual(1, reuse.picks[1].count);
            // The reused plan passes the validator again (order back to pending).
            Assert.IsNull(PlanValidator.FirstError(reuse, snap));
        }

        [Test]
        public void TryReuse_BenchTaken_ReturnsNull()
        {
            var snap = Snap();
            snap.benches[0].reserved_by = "9"; // the urgent order took the bench
            Assert.IsNull(ResumePlanner.TryReuse(OldPlan(), snap.orders[0], snap));
        }

        [Test]
        public void TryReuse_TruckTaken_ReturnsNull()
        {
            var snap = Snap();
            snap.trucks[0].reserved_by = "9"; // camion requis par l'urgente
            Assert.IsNull(ResumePlanner.TryReuse(OldPlan(), snap.orders[0], snap));
        }

        [Test]
        public void TryReuse_PalletTaken_ReturnsNull()
        {
            var snap = Snap();
            snap.pallets[0].reserved_by = "9"; // P01 requise par l'urgente
            Assert.IsNull(ResumePlanner.TryReuse(OldPlan(), snap.orders[0], snap));
        }

        [Test]
        public void TryReuse_StockShrunkBelowNeed_ReturnsNull()
        {
            var snap = Snap();
            snap.pallets[1].stock.b = 0; // P02 emptied by the urgent order, need 1B
            Assert.IsNull(ResumePlanner.TryReuse(OldPlan(), snap.orders[0], snap));
        }

        [Test]
        public void TryReuse_AllStaged_LoadOnlyPlan()
        {
            var snap = Snap();
            var order = snap.orders[0];
            order.a = 2; order.b = 0;             // 1 loaded + 1 staged = covered
            order.staged_a = 1; order.loaded_a = 1;
            var reuse = ResumePlanner.TryReuse(OldPlan(), order, snap);
            Assert.IsNotNull(reuse);
            Assert.AreEqual(0, reuse.picks.Length);
            Assert.IsNull(PlanValidator.FirstError(reuse, snap));
        }

        [Test]
        public void TryReuse_BenchReleased_RequiresFreeOriginal()
        {
            var snap = Snap();
            var order = snap.orders[0];
            order.bench_id = null;                 // bench freed at the suspension (empty)
            snap.benches[0].reserved_by = "9";     // l'urgente l'a pris entre-temps
            Assert.IsNull(ResumePlanner.TryReuse(OldPlan(), order, snap));
            // If it stayed free, the resume reuses it.
            snap.benches[0].reserved_by = null;
            Assert.IsNotNull(ResumePlanner.TryReuse(OldPlan(), order, snap));
        }

        [Test]
        public void TryReuse_NullInputs_ReturnNull()
        {
            var snap = Snap();
            Assert.IsNull(ResumePlanner.TryReuse(null, snap.orders[0], snap));
            Assert.IsNull(ResumePlanner.TryReuse(OldPlan(), null, snap));
            Assert.IsNull(ResumePlanner.TryReuse(OldPlan(), snap.orders[0], null));
        }
    }
}
