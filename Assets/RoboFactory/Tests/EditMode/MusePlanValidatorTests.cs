using NUnit.Framework;
using RoboFactory.Hackathon.Orchestration;

namespace RoboFactory.Hackathon.Tests
{
    /// <summary>Local validator of the Muse Wonder plans: pure tests, no scene.</summary>
    public sealed class MusePlanValidatorTests
    {
        private static WarehouseSnapshot Snap()
        {
            return new WarehouseSnapshot
            {
                state_version = 1,
                orders = new[]
                {
                    new WhOrderState { id = 1, a = 3, b = 2, c = 0, priority = 1, status = "pending" },
                    new WhOrderState { id = 2, a = 0, b = 0, c = 4, priority = 2, status = "loaded" },
                },
                pallets = new[]
                {
                    new WhPallet { id = "P01", stock = new WhStock { a = 5, b = 0, c = 0 } },
                    new WhPallet { id = "P02", stock = new WhStock { a = 0, b = 4, c = 2 } },
                    new WhPallet { id = "P03", stock = new WhStock { a = 1, b = 1, c = 8 } },
                },
                benches = new[]
                {
                    new WhBench { id = "B01", capacity = 6, free_levels = 6 },
                    new WhBench { id = "B02", capacity = 6, free_levels = 0 },
                    new WhBench { id = "B03", capacity = 6, free_levels = 6, reserved_by = "7" },
                },
                trucks = new[]
                {
                    new WhTruck { id = "T01", capacity = 12, loaded = 0, free = 12, door = "Closed" },
                    new WhTruck { id = "T02", capacity = 4, loaded = 2, free = 2, door = "Closed" },
                },
                robots = new[]
                {
                    new WhRobot { id = "R1", available = true },
                    new WhRobot { id = "R2", available = true },
                }
            };
        }

        private static MusePlan Plan()
        {
            return new MusePlan
            {
                action = "execute_order",
                order_id = 1,
                picks = new[]
                {
                    new MusePlanPick { pallet_id = "P01", type = "A", count = 3 },
                    new MusePlanPick { pallet_id = "P02", type = "B", count = 2 },
                },
                fallback_pallets = new[] { "P03" },
                bench_id = "B01",
                truck_id = "T01",
                robot_pick = "R1",
                robot_load = "R2",
                start_immediately = true
            };
        }

        [Test]
        public void ValidPlan_Passes()
        {
            Assert.IsNull(PlanValidator.FirstError(Plan(), Snap()));
        }

        [Test]
        public void NullPlan_Rejected()
        {
            Assert.IsNotNull(PlanValidator.FirstError(null, Snap()));
        }

        [Test]
        public void UnknownPallet_Rejected()
        {
            MusePlan p = Plan();
            p.picks[0].pallet_id = "P99";
            StringAssert.Contains("P99", PlanValidator.FirstError(p, Snap()));
        }

        [Test]
        public void InsufficientStock_Rejected()
        {
            MusePlan p = Plan();
            p.picks[0].count = 6; // P01 only has 5, and the order only asks for 3
            StringAssert.Contains("insufficient", PlanValidator.FirstError(p, Snap()));
        }

        [Test]
        public void PickSumNotCoveringOrder_Rejected()
        {
            MusePlan p = Plan();
            p.picks[0].count = 2; // besoin 3A — couverture insuffisante
            StringAssert.Contains("coverage A", PlanValidator.FirstError(p, Snap()));
        }

        [Test]
        public void OverPicking_Rejected()
        {
            MusePlan p = Plan();
            p.picks[0].count = 3;
            p.picks[1].count = 3; // besoin 2B seulement
            StringAssert.Contains("coverage B", PlanValidator.FirstError(p, Snap()));
        }

        [Test]
        public void DuplicateSource_Rejected()
        {
            MusePlan p = Plan();
            // Two identical steps P01/A: the same source reserved twice.
            p.picks = new[]
            {
                new MusePlanPick { pallet_id = "P01", type = "A", count = 1 },
                new MusePlanPick { pallet_id = "P01", type = "A", count = 2 },
                new MusePlanPick { pallet_id = "P02", type = "B", count = 2 },
            };
            StringAssert.Contains("reserved twice", PlanValidator.FirstError(p, Snap()));
        }

        [Test]
        public void MultiPalletSplit_IsAccepted()
        {
            var snap = Snap();
            snap.orders[0].a = 6; // besoin 6A : P01(5) + P03(1)
            var p = Plan();
            p.picks = new[]
            {
                new MusePlanPick { pallet_id = "P01", type = "A", count = 5 },
                new MusePlanPick { pallet_id = "P03", type = "A", count = 1 },
                new MusePlanPick { pallet_id = "P02", type = "B", count = 2 },
            };
            p.fallback_pallets = new string[0]; // P03 is in the picks, not a fallback
            Assert.IsNull(PlanValidator.FirstError(p, snap));
        }

        [Test]
        public void UnknownBench_Rejected()
        {
            MusePlan p = Plan();
            p.bench_id = "B42";
            StringAssert.Contains("unknown bench", PlanValidator.FirstError(p, Snap()));
        }

        [Test]
        public void FullBench_Rejected()
        {
            MusePlan p = Plan();
            p.bench_id = "B02"; // 0 niveau libre
            StringAssert.Contains("unavailable", PlanValidator.FirstError(p, Snap()));
        }

        [Test]
        public void ReservedBench_Rejected()
        {
            MusePlan p = Plan();
            p.bench_id = "B03"; // reserved by order 7
            StringAssert.Contains("reserved by", PlanValidator.FirstError(p, Snap()));
        }

        [Test]
        public void ReservedBench_SameOrder_Passes()
        {
            var snap = Snap();
            snap.orders[0].status = "waiting";
            snap.orders[0].bench_id = "B03";
            snap.benches[2].reserved_by = "1"; // reserved by THIS order
            var p = Plan();
            p.bench_id = "B03";
            Assert.IsNull(PlanValidator.FirstError(p, snap));
        }

        [Test]
        public void UnknownTruck_Rejected()
        {
            MusePlan p = Plan();
            p.truck_id = "T42";
            StringAssert.Contains("unknown truck", PlanValidator.FirstError(p, Snap()));
        }

        [Test]
        public void TruckTooSmall_Rejected()
        {
            MusePlan p = Plan();
            p.truck_id = "T02"; // 2 libres < 5 cartons
            StringAssert.Contains("insufficient capacity", PlanValidator.FirstError(p, Snap()));
        }

        [Test]
        public void UnknownRobot_Rejected()
        {
            MusePlan p = Plan();
            p.robot_pick = "R9";
            StringAssert.Contains("unknown pick robot", PlanValidator.FirstError(p, Snap()));
        }

        [Test]
        public void BusyRobot_Rejected()
        {
            var snap = Snap();
            snap.robots[0].available = false;
            StringAssert.Contains("unavailable", PlanValidator.FirstError(Plan(), snap));
        }

        [Test]
        public void SameRobotTwice_Rejected()
        {
            MusePlan p = Plan();
            p.robot_load = "R1";
            StringAssert.Contains("both missions", PlanValidator.FirstError(p, Snap()));
        }

        [Test]
        public void CompletedOrder_Rejected()
        {
            MusePlan p = Plan();
            p.order_id = 2; // statut loaded
            StringAssert.Contains("not plannable", PlanValidator.FirstError(p, Snap()));
        }

        [Test]
        public void AssignedBenchConflict_Rejected()
        {
            var snap = Snap();
            snap.orders[0].status = "waiting";
            snap.orders[0].bench_id = "B01"; // stock already staged on B01
            var p = Plan();
            p.bench_id = "B01"; // same bench = OK...
            Assert.IsNull(PlanValidator.FirstError(p, snap));
            p.bench_id = null;   // but another bench = refused
            StringAssert.Contains("already assigned", PlanValidator.FirstError(p, snap));
        }

        [Test]
        public void UnknownAction_Rejected()
        {
            MusePlan p = Plan();
            p.action = "teleport";
            StringAssert.Contains("unknown action", PlanValidator.FirstError(p, Snap()));
        }

        [Test]
        public void WaitAction_Passes()
        {
            var p = new MusePlan { action = "wait", reason = "incident" };
            Assert.IsNull(PlanValidator.FirstError(p, Snap()));
        }

        [Test]
        public void UnknownFallback_Rejected()
        {
            MusePlan p = Plan();
            p.fallback_pallets = new[] { "P77" };
            StringAssert.Contains("unknown fallback pallet", PlanValidator.FirstError(p, Snap()));
        }

        // ----------------------------------------------------------- OrderText

        [Test]
        public void ParseOrder_StandardForm()
        {
            int[] c = OrderText.Parse("A x3, B x2, C x1");
            Assert.AreEqual(new[] { 3, 2, 1 }, c);
        }

        [Test]
        public void ParseOrder_CompactAndPrefixed()
        {
            Assert.AreEqual(new[] { 4, 1, 0 }, OrderText.Parse("A4 + 1B"));
            Assert.AreEqual(new[] { 0, 5, 2 }, OrderText.Parse("5B; C:2"));
        }

        [Test]
        public void ParseOrder_Garbage()
        {
            Assert.AreEqual(0, OrderText.Total(OrderText.Parse("hello world")));
        }

        [Test]
        public void FormatOrder()
        {
            Assert.AreEqual("A x3, C x1", OrderText.Format(new[] { 3, 0, 1 }));
        }
    }
}
