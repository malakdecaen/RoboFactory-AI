using System.Collections.Generic;
using RoboFactory.Hackathon.Orchestration;
using RoboFactory.Integration;
using UnityEngine;
using RoboFactory.Sim;

namespace RoboFactory.Logistics
{
    /// <summary>
    /// World catalogue: assigns stable identifiers (P01, B01, T01, R1, R2)
    /// to the scene's resources and builds the snapshot sent to Muse Wonder.
    ///
    /// Ids are sorted by name then position: deterministic between scans,
    /// so a Muse decision is interpreted the same way for the whole mission.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WarehouseWorld : MonoBehaviour
    {
        public static WarehouseWorld Instance { get; private set; }

        [Tooltip("Travel speed used for the ETAs (m/s).")]
        public float RobotSpeed = 1.8f;

        private readonly Dictionary<string, ShelfStorage> pallets = new Dictionary<string, ShelfStorage>();
        private readonly Dictionary<string, ShelfStorage> benches = new Dictionary<string, ShelfStorage>();
        private readonly Dictionary<string, TruckBay> trucks = new Dictionary<string, TruckBay>();
        private readonly Dictionary<ShelfStorage, string> palletIds = new Dictionary<ShelfStorage, string>();
        private readonly Dictionary<ShelfStorage, string> benchIds = new Dictionary<ShelfStorage, string>();
        private readonly Dictionary<TruckBay, string> truckIds = new Dictionary<TruckBay, string>();

        /// <summary>Logical reservations of benches/pallets: resource id -> order id.</summary>
        private readonly Dictionary<string, int> benchReservations = new Dictionary<string, int>();
        private readonly Dictionary<string, int> palletReservations = new Dictionary<string, int>();

        public WarehouseRobotAutopilot R1 { get; private set; }
        public TruckLoaderAgent R2 { get; private set; }
        public int PalletCount => pallets.Count;
        public int BenchCount => benches.Count;
        public int TruckCount => trucks.Count;

        private void Awake()
        {
            Instance = this;
            Refresh();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ------------------------------------------------------------------ access by id

        public ShelfStorage Pallet(string id) => pallets.TryGetValue(id ?? "", out ShelfStorage s) ? s : null;
        public ShelfStorage Bench(string id) => benches.TryGetValue(id ?? "", out ShelfStorage s) ? s : null;
        public TruckBay Truck(string id) => trucks.TryGetValue(id ?? "", out TruckBay t) ? t : null;
        public string PalletIdOf(ShelfStorage s) => s != null && palletIds.TryGetValue(s, out string id) ? id : s != null ? s.name : null;
        public string BenchIdOf(ShelfStorage s) => s != null && benchIds.TryGetValue(s, out string id) ? id : s != null ? s.name : null;
        public string TruckIdOf(TruckBay t) => t != null && truckIds.TryGetValue(t, out string id) ? id : t != null ? t.name : null;

        public IEnumerable<string> PalletIds => pallets.Keys;
        public IEnumerable<string> BenchIds => benches.Keys;
        public IEnumerable<string> TruckIds => trucks.Keys;

        /// <summary>Dock position of a resource (or its transform).</summary>
        public bool TryPositionOf(string resourceId, out Vector3 position)
        {
            position = default;
            if (string.IsNullOrEmpty(resourceId)) return false;
            if (resourceId == "R1" && R1 != null) { position = R1.transform.position; return true; }
            if (resourceId == "R2" && R2 != null) { position = R2.transform.position; return true; }
            ShelfStorage s = Pallet(resourceId);
            if (s == null) s = Bench(resourceId);
            if (s != null)
            {
                ShelfInteractionZone zone = NearestZone(s, s.transform.position);
                position = zone != null && zone.dockPoint != null ? zone.dockPoint.position : s.transform.position;
                return true;
            }
            TruckBay bay = Truck(resourceId);
            if (bay != null)
            {
                position = bay.DockPoint != null ? bay.DockPoint.position : bay.transform.position;
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ logical reservations

        public bool ReserveBench(string benchId, int orderId)
        {
            if (benchReservations.TryGetValue(benchId, out int current) && current != orderId) return false;
            benchReservations[benchId] = orderId;
            return true;
        }

        public bool ReservePallet(string palletId, int orderId)
        {
            if (palletReservations.TryGetValue(palletId, out int current) && current != orderId) return false;
            palletReservations[palletId] = orderId;
            return true;
        }

        public void ReleaseBench(string benchId, int orderId)
        {
            if (benchReservations.TryGetValue(benchId, out int current) && current == orderId)
                benchReservations.Remove(benchId);
        }

        public void ReleasePallet(string palletId, int orderId)
        {
            if (palletReservations.TryGetValue(palletId, out int current) && current == orderId)
                palletReservations.Remove(palletId);
        }

        public string ReservedBy(string resourceId)
        {
            if (benchReservations.TryGetValue(resourceId, out int b)) return b.ToString();
            if (palletReservations.TryGetValue(resourceId, out int p)) return p.ToString();
            TruckBay bay = Truck(resourceId);
            if (bay != null && bay.ReservedOrderId >= 0) return bay.ReservedOrderId.ToString();
            return null;
        }

        // ------------------------------------------------------------------ scanning

        /// <summary>Renumbers the resources (stable order: name, then position).</summary>
        public void Refresh()
        {
            pallets.Clear(); benches.Clear(); trucks.Clear();
            palletIds.Clear(); benchIds.Clear(); truckIds.Clear();

            var palletList = new List<ShelfStorage>();
            var benchList = new List<ShelfStorage>();
            foreach (ShelfStorage shelf in FindObjectsByType<ShelfStorage>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (shelf == null) continue;
                switch (Station.RoleOf(shelf))
                {
                    case StationRole.Bench: benchList.Add(shelf); break;
                    case StationRole.Truck: break; // via TruckBay
                    default: palletList.Add(shelf); break;
                }
            }
            var bayList = new List<TruckBay>(
                FindObjectsByType<TruckBay>(FindObjectsInactive.Exclude, FindObjectsSortMode.None));

            palletList.Sort(CompareStorage); benchList.Sort(CompareStorage);
            bayList.Sort((a, b) => CompareTransforms(a.transform, b.transform));

            for (int i = 0; i < palletList.Count; i++)
            {
                string id = "P" + (i + 1).ToString("00");
                pallets[id] = palletList[i];
                palletIds[palletList[i]] = id;
            }
            for (int i = 0; i < benchList.Count; i++)
            {
                string id = "B" + (i + 1).ToString("00");
                benches[id] = benchList[i];
                benchIds[benchList[i]] = id;
            }
            for (int i = 0; i < bayList.Count; i++)
            {
                string id = "T" + (i + 1).ToString("00");
                trucks[id] = bayList[i];
                truckIds[bayList[i]] = id;
            }

            if (R1 == null)
                R1 = FindFirstObjectByType<WarehouseRobotAutopilot>();
            if (R2 == null)
                R2 = FindFirstObjectByType<TruckLoaderAgent>();
        }

        private static int CompareStorage(ShelfStorage a, ShelfStorage b)
        {
            int byName = NaturalIndex(a.name).CompareTo(NaturalIndex(b.name));
            if (byName != 0) return byName;
            byName = string.CompareOrdinal(a.name, b.name);
            if (byName != 0) return byName;
            return CompareTransforms(a.transform, b.transform);
        }

        private static int CompareTransforms(Transform a, Transform b)
        {
            int byX = a.position.x.CompareTo(b.position.x);
            return byX != 0 ? byX : a.position.z.CompareTo(b.position.z);
        }

        private static int NaturalIndex(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            int open = name.LastIndexOf('(');
            if (open >= 0 && name.EndsWith(")"))
            {
                string inner = name.Substring(open + 1, name.Length - open - 2);
                if (int.TryParse(inner, out int number)) return number;
            }
            return 0;
        }

        // ------------------------------------------------------------------ snapshot

        /// <summary>Builds the real warehouse state for Muse Wonder.</summary>
        public WarehouseSnapshot BuildSnapshot(List<WhOrderState> orders, int stateVersion,
                                               string trigger, string mode, List<string> incidents,
                                               string lastRejection)
        {
            var snap = new WarehouseSnapshot
            {
                state_version = stateVersion,
                trigger = trigger,
                mode = mode,
                orders = orders != null ? orders.ToArray() : new WhOrderState[0],
                incidents = incidents != null ? incidents.ToArray() : new string[0],
                last_rejection = lastRejection,
                box_type_legend = "A=wooden crate (wooden_crate), B=big cardboard box (big_cardboard), C=medium cardboard box (medium_cardboard)"
            };

            Vector3 r1Pos = R1 != null ? R1.transform.position : Vector3.zero;
            Vector3 r2Pos = R2 != null ? R2.transform.position : Vector3.zero;

            var palletList = new List<WhPallet>();
            foreach (KeyValuePair<string, ShelfStorage> kv in pallets)
            {
                ShelfStorage s = kv.Value;
                var stock = new WhStock();
                var top = new WhStock();
                foreach (CargoBox box in s.GetComponentsInChildren<CargoBox>(true))
                {
                    if (box == null || box.isCarried || box.orderId >= 0) continue; // already assigned
                    switch (box.TypeLetter)
                    {
                        case "A": stock.a++; break;
                        case "B": stock.b++; break;
                        case "C": stock.c++; break;
                    }
                }
                // Box directly takeable per type: top of each column.
                foreach (ShelfStorage.StorageColumn column in s.columns ?? new ShelfStorage.StorageColumn[0])
                {
                    if (column == null || column.levels == null) continue;
                    for (int l = column.levels.Length - 1; l >= 0; l--)
                    {
                        Transform level = column.levels[l];
                        if (level == null) continue;
                        CargoBox box = level.GetComponentInChildren<CargoBox>(true);
                        if (box == null || box.isCarried || box.orderId >= 0) continue;
                        switch (box.TypeLetter)
                        {
                            case "A": top.a++; break;
                            case "B": top.b++; break;
                            case "C": top.c++; break;
                        }
                        break; // only the top counts
                    }
                }
                palletList.Add(new WhPallet
                {
                    id = kv.Key,
                    x = s.transform.position.x,
                    z = s.transform.position.z,
                    stock = stock,
                    top = top,
                    reserved_by = palletReservations.TryGetValue(kv.Key, out int rp) ? rp.ToString() : null,
                    dist_r1 = DockDistance(r1Pos, s)
                });
            }
            snap.pallets = palletList.ToArray();

            var benchList = new List<WhBench>();
            foreach (KeyValuePair<string, ShelfStorage> kv in benches)
            {
                ShelfStorage s = kv.Value;
                int capacity = 0, occupied = 0;
                if (s.columns != null)
                    foreach (ShelfStorage.StorageColumn column in s.columns)
                    {
                        if (column == null || column.levels == null) continue;
                        foreach (Transform level in column.levels)
                        {
                            if (level == null) continue;
                            capacity++;
                            if (level.GetComponentInChildren<CargoBox>(true) != null) occupied++;
                        }
                    }
                benchList.Add(new WhBench
                {
                    id = kv.Key,
                    x = s.transform.position.x,
                    z = s.transform.position.z,
                    capacity = capacity,
                    occupied = occupied,
                    free_levels = capacity - occupied,
                    reserved_by = benchReservations.TryGetValue(kv.Key, out int rb) ? rb.ToString() : null
                });
            }
            snap.benches = benchList.ToArray();

            var truckList = new List<WhTruck>();
            foreach (KeyValuePair<string, TruckBay> kv in trucks)
            {
                TruckBay bay = kv.Value;
                truckList.Add(new WhTruck
                {
                    id = kv.Key,
                    x = bay.transform.position.x,
                    z = bay.transform.position.z,
                    capacity = bay.Capacity,
                    loaded = bay.LoadedCount,
                    free = bay.FreeSlots,
                    door = bay.Door.ToString(),
                    reserved_by = bay.ReservedOrderId >= 0 ? bay.ReservedOrderId.ToString() : null
                });
            }
            snap.trucks = truckList.ToArray();

            var robotList = new List<WhRobot>();
            if (R1 != null)
                robotList.Add(new WhRobot
                {
                    id = "R1",
                    x = R1.transform.position.x,
                    z = R1.transform.position.z,
                    state = R1.Status,
                    carrying = R1.MissionActive,
                    available = R1.Running,
                    busy = R1.MissionActive,
                    mission = R1.MissionActive ? "directed pick" : null
                });
            if (R2 != null)
                robotList.Add(new WhRobot
                {
                    id = "R2",
                    x = R2.transform.position.x,
                    z = R2.transform.position.z,
                    state = R2.Status,
                    carrying = R2.Carried != null,
                    available = R2.Running,
                    busy = R2.MissionActive,
                    mission = R2.MissionActive ? "directed load" : null
                });
            snap.robots = robotList.ToArray();

            // ---- useful distances: R1->pallets, pallets->benches, benches->trucks
            var distances = new List<WhDistance>();
            foreach (KeyValuePair<string, ShelfStorage> kv in pallets)
            {
                if (R1 == null) break;
                float d = DockDistance(r1Pos, kv.Value);
                distances.Add(new WhDistance { from = "R1", to = kv.Key, meters = d, eta_s = d / Mathf.Max(.1f, RobotSpeed) });
            }
            foreach (KeyValuePair<string, ShelfStorage> pb in pallets)
                foreach (KeyValuePair<string, ShelfStorage> bb in benches)
                {
                    float d = Vector3.Distance(pb.Value.transform.position, bb.Value.transform.position);
                    distances.Add(new WhDistance { from = pb.Key, to = bb.Key, meters = d, eta_s = d / Mathf.Max(.1f, RobotSpeed) });
                }
            foreach (KeyValuePair<string, ShelfStorage> bb in benches)
                foreach (KeyValuePair<string, TruckBay> tb in trucks)
                {
                    Vector3 truckPos = tb.Value.DockPoint != null ? tb.Value.DockPoint.position : tb.Value.transform.position;
                    float d = Vector3.Distance(bb.Value.transform.position, truckPos);
                    distances.Add(new WhDistance { from = bb.Key, to = tb.Key, meters = d, eta_s = d / Mathf.Max(.1f, RobotSpeed) });
                }
            if (R2 != null)
                foreach (KeyValuePair<string, ShelfStorage> bb in benches)
                {
                    float d = DockDistance(r2Pos, bb.Value);
                    distances.Add(new WhDistance { from = "R2", to = bb.Key, meters = d, eta_s = d / Mathf.Max(.1f, RobotSpeed) });
                }
            snap.distances = distances.ToArray();

            return snap;
        }

        private static float DockDistance(Vector3 from, ShelfStorage shelf)
        {
            ShelfInteractionZone zone = NearestZone(shelf, from);
            return zone != null && zone.dockPoint != null
                ? Vector3.Distance(from, zone.dockPoint.position)
                : Vector3.Distance(from, shelf.transform.position);
        }

        private static ShelfInteractionZone NearestZone(ShelfStorage shelf, Vector3 from)
        {
            ShelfInteractionZone best = null;
            float bestDistance = float.PositiveInfinity;
            foreach (ShelfInteractionZone zone in shelf.GetComponentsInChildren<ShelfInteractionZone>(true))
            {
                if (zone == null || zone.dockPoint == null) continue;
                float d = Vector3.Distance(from, zone.dockPoint.position);
                if (d < bestDistance) { bestDistance = d; best = zone; }
            }
            return best;
        }

        /// <summary>At least one unreserved bench with a free level (except: excluded bench, e.g. urgent lane).</summary>
        public bool HasFreeBench(string except = null)
        {
            foreach (KeyValuePair<string, ShelfStorage> kv in benches)
            {
                if (benchReservations.ContainsKey(kv.Key) || kv.Key == except) continue;
                if (FreeLevels(kv.Value) > 0) return true;
            }
            return false;
        }

        /// <summary>At least one unreserved truck with a free slot (except: excluded truck, e.g. urgent lane).</summary>
        public bool HasFreeTruck(string except = null)
        {
            foreach (KeyValuePair<string, TruckBay> kv in trucks)
                if (kv.Key != except && kv.Value.ReservedOrderId < 0 && kv.Value.FreeSlots > 0) return true;
            return false;
        }

        /// <summary>At least one free box (unreserved, unassigned) on an unreserved pallet.</summary>
        public bool HasAnyFreeStock()
        {
            foreach (KeyValuePair<string, ShelfStorage> kv in pallets)
            {
                if (palletReservations.ContainsKey(kv.Key)) continue;
                foreach (CargoBox box in kv.Value.GetComponentsInChildren<CargoBox>(true))
                    if (box != null && !box.isCarried && box.orderId < 0) return true;
            }
            return false;
        }

        /// <summary>Free levels of a bench (0 if unknown).</summary>
        public int BenchFreeLevels(string benchId)
        {
            ShelfStorage s = Bench(benchId);
            return s != null ? FreeLevels(s) : 0;
        }

        /// <summary>Bench reserved by an order (-1 = free).</summary>
        public int BenchReservedBy(string benchId)
        {
            return benchId != null && benchReservations.TryGetValue(benchId, out int id) ? id : -1;
        }

        /// <summary>Free stock (boxes not assigned to an order) of one type, across all pallets.</summary>
        public int FreeStock(string type)
        {
            int n = 0;
            foreach (KeyValuePair<string, ShelfStorage> kv in pallets)
                n += StockOnPallet(kv.Key, type);
            return n;
        }

        /// <summary>Number of levels without a box on this storage.</summary>
        private static int FreeLevels(ShelfStorage s)
        {
            if (s == null || s.columns == null) return 0;
            int free = 0;
            foreach (ShelfStorage.StorageColumn column in s.columns)
            {
                if (column == null || column.levels == null) continue;
                foreach (Transform level in column.levels)
                    if (level != null && level.GetComponentInChildren<CargoBox>(true) == null) free++;
            }
            return free;
        }

        /// <summary>Real stock of one type on a pallet (direct scene read).</summary>
        public int StockOnPallet(string palletId, string type)
        {
            ShelfStorage s = Pallet(palletId);
            if (s == null) return 0;
            int count = 0;
            foreach (CargoBox box in s.GetComponentsInChildren<CargoBox>(true))
                if (box != null && !box.isCarried && box.orderId < 0 && box.TypeLetter == type) count++;
            return count;
        }
    }
}
