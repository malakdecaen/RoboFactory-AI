using System.Collections.Generic;
using UnityEngine;
using RoboFactory.Sim;

namespace RoboFactory.Logistics
{
    /// <summary>
    /// Registre logistique central (singleton de scène).
    ///
    /// - Statut de chaque carton : déduit de la hiérarchie à chaque balayage,
    ///   donc toujours synchronisé avec le monde (aucun double bookkeeping).
    /// - Réservations : un carton réservé par un robot ne peut pas être pris par un autre.
    /// - Requêtes : fournit aux agents des "jobs" (carton sur banc -> slot camion).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CargoRegistry : MonoBehaviour
    {
        public static CargoRegistry Instance { get; private set; }

        [Header("Balayage")]
        [Tooltip("Délai entre deux relectures de la scène.")]
        public float ScanInterval = .3f;
        public bool LogTransitions = true;

        [Header("Priorité Robot 1 (déposeur)")]
        [Tooltip("Le robot déposeur a la priorité au banc : tant qu'il se trouve dans ce rayon " +
                 "d'un dock de banc, aucun carton accessible par ce dock n'est attribué aux autres agents.")]
        public bool LoaderHasPriority = true;
        public float BenchBlockRadius = 1.6f;

        /// <summary>box, ancien statut, nouveau statut</summary>
        public event System.Action<CargoBox, CargoStatus, CargoStatus> StatusChanged;

        private readonly Dictionary<CargoBox, CargoStatus> status = new Dictionary<CargoBox, CargoStatus>();
        private readonly Dictionary<CargoBox, object> reservations = new Dictionary<CargoBox, object>();
        private readonly List<ShelfInteractionZone> benchZones = new List<ShelfInteractionZone>();
        private float nextScan;

        /// <summary>Cartons actuellement dans le camion.</summary>
        public int InTruckCount { get; private set; }
        /// <summary>Cartons actuellement sur les bancs.</summary>
        public int OnBenchCount { get; private set; }
        /// <summary>Cartons actuellement sur les palettes.</summary>
        public int OnPalletCount { get; private set; }

        /// <summary>Job de livraison attribué à un robot : carton à prendre + zone du banc + baie.</summary>
        public struct DeliveryJob
        {
            public CargoBox Box;
            public ShelfInteractionZone Zone;
            public TruckBay Bay;
        }

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (Time.time >= nextScan)
            {
                nextScan = Time.time + ScanInterval;
                Scan();
            }
        }

        // ------------------------------------------------------------------ statuts

        /// <summary>Statut logistique d'un carton, déduit de ses ancêtres.</summary>
        public static CargoStatus Classify(CargoBox box)
        {
            if (box == null) return CargoStatus.Unknown;
            for (Transform t = box.transform; t != null; t = t.parent)
            {
                if (t.GetComponent<TruckBay>() != null) return CargoStatus.InTruck;
                if (t.GetComponent<GridRobotController>() != null) return CargoStatus.Carried;
            }
            ShelfStorage shelf = box.GetComponentInParent<ShelfStorage>();
            if (shelf != null)
            {
                switch (Station.RoleOf(shelf))
                {
                    case StationRole.Truck: return CargoStatus.InTruck;
                    case StationRole.Bench: return CargoStatus.OnBench;
                    default: return CargoStatus.OnPallet;
                }
            }
            return CargoStatus.Loose;
        }

        public CargoStatus StatusOf(CargoBox box)
        {
            if (box == null) return CargoStatus.Unknown;
            return status.TryGetValue(box, out CargoStatus s) ? s : Classify(box);
        }

        /// <summary>Relecture de la scène : met à jour les statuts et publie les transitions.</summary>
        public void Scan()
        {
            int truck = 0, bench = 0, pallet = 0;
            CargoBox[] boxes = FindObjectsByType<CargoBox>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (CargoBox box in boxes)
            {
                if (box == null) continue;
                CargoStatus now = Classify(box);
                if (!status.TryGetValue(box, out CargoStatus before)) before = CargoStatus.Unknown;
                status[box] = now;
                switch (now)
                {
                    case CargoStatus.InTruck: truck++; break;
                    case CargoStatus.OnBench: bench++; break;
                    case CargoStatus.OnPallet: pallet++; break;
                }
                if (now != before)
                {
                    StatusChanged?.Invoke(box, before, now);
                    if (LogTransitions && before != CargoStatus.Unknown)
                        Debug.Log("[Logistique] " + box.name + " : " + CargoStatusNames.Of(before) +
                                  " -> " + CargoStatusNames.Of(now));
                }
            }
            InTruckCount = truck;
            OnBenchCount = bench;
            OnPalletCount = pallet;
            RefreshBenchZones();
        }

        // ------------------------------------------------------------------ réservations

        /// <summary>Réserve un carton pour un propriétaire (le robot, le bras...).</summary>
        public bool TryReserve(CargoBox box, object owner)
        {
            if (box == null || owner == null) return false;
            if (reservations.TryGetValue(box, out object current) && current != null && !ReferenceEquals(current, owner))
                return false;
            reservations[box] = owner;
            return true;
        }

        public void Release(CargoBox box, object owner)
        {
            if (box == null) return;
            if (reservations.TryGetValue(box, out object current) && ReferenceEquals(current, owner))
                reservations.Remove(box);
        }

        public bool IsReserved(CargoBox box)
        {
            return box != null && reservations.TryGetValue(box, out object owner) && owner != null;
        }

        public object ReservedBy(CargoBox box)
        {
            return box != null && reservations.TryGetValue(box, out object owner) ? owner : null;
        }

        // ------------------------------------------------------------------ jobs

        /// <summary>
        /// Cherche le carton le plus proche posé sur un banc et non réservé,
        /// dans une baie camion qui a encore de la place. Réserve le carton si trouvé.
        /// </summary>
        public bool TryAcquireDelivery(object owner, Vector3 from, out DeliveryJob job)
        {
            job = default;
            if (owner == null) return false;
            if (status.Count == 0) Scan();
            if (benchZones.Count == 0) RefreshBenchZones();

            TruckBay bay = NearestBay(from);
            if (bay == null || bay.FreeSlots <= 0) return false;

            ShelfInteractionZone best = null;
            CargoBox bestBox = null;
            float bestDistance = float.PositiveInfinity;
            foreach (ShelfInteractionZone zone in benchZones)
            {
                if (zone == null || zone.dockPoint == null) continue;
                // Dock dans un volume interdit (NavBlocker) : on passe par l'autre côté.
                if (NavBlocker.BlocksPoint(zone.dockPoint.position)) continue;
                // Robot 1 a la priorité au banc : on ne sélectionne rien près de lui.
                if (LoaderBlocks(zone)) continue;
                CargoBox box = TopBox(zone);
                if (box == null || IsReserved(box)) continue;
                float distance = Vector3.Distance(from, zone.dockPoint.position);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = zone;
                    bestBox = box;
                }
            }
            if (bestBox == null) return false;
            if (!TryReserve(bestBox, owner)) return false;

            job = new DeliveryJob { Box = bestBox, Zone = best, Bay = bay };
            return true;
        }

        /// <summary>
        /// Vrai si le robot déposeur (GridRobotController sans TruckLoaderAgent, autopilot actif)
        /// est à portée du dock de la zone — les colis de cette zone restent à R1.
        /// </summary>
        private Transform loaderRobot;
        private float nextLoaderFind;

        private bool LoaderBlocks(ShelfInteractionZone zone)
        {
            if (!LoaderHasPriority || zone.dockPoint == null) return false;
            Transform r1 = FindLoaderRobot();
            if (r1 == null) return false;
            var controller = r1.GetComponent<GridRobotController>();
            if (controller != null && !controller.controlsLocked) return false; // R1 inactif : pas de blocage
            return Vector3.Distance(r1.position, zone.dockPoint.position) <= BenchBlockRadius;
        }

        private Transform FindLoaderRobot()
        {
            if (loaderRobot != null) return loaderRobot;
            if (Time.time < nextLoaderFind) return null;
            nextLoaderFind = Time.time + 2f;
            foreach (GridRobotController controller in
                     FindObjectsByType<GridRobotController>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (controller == null || controller.GetComponent<TruckLoaderAgent>() != null) continue;
                loaderRobot = controller.transform;
                break;
            }
            return loaderRobot;
        }

        private TruckBay NearestBay(Vector3 from)
        {
            TruckBay best = null;
            float bestDistance = float.PositiveInfinity;
            foreach (TruckBay bay in FindObjectsByType<TruckBay>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (bay == null || !bay.isActiveAndEnabled) continue;
                float distance = Vector3.Distance(from, bay.DockPoint != null ? bay.DockPoint.position : bay.transform.position);
                if (distance < bestDistance) { bestDistance = distance; best = bay; }
            }
            return best;
        }

        /// <summary>Zones de banc valides (dock + stockage). Rechargées à chaque balayage.</summary>
        private void RefreshBenchZones()
        {
            benchZones.Clear();
            foreach (ShelfInteractionZone zone in
                     FindObjectsByType<ShelfInteractionZone>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (zone == null || zone.shelf == null || zone.dockPoint == null) continue;
                if (Station.RoleOf(zone.shelf) == StationRole.Bench) benchZones.Add(zone);
            }
        }

        /// <summary>Carton le plus haut de la colonne de la zone (lecture directe de la scène).</summary>
        public static CargoBox TopBox(ShelfInteractionZone zone)
        {
            Transform[] levels = ColumnLevels(zone);
            if (levels == null) return null;
            for (int i = levels.Length - 1; i >= 0; i--)
            {
                if (levels[i] == null) continue;
                CargoBox box = levels[i].GetComponentInChildren<CargoBox>(true);
                if (box != null) return box;
            }
            return null;
        }

        public static Transform[] ColumnLevels(ShelfInteractionZone zone)
        {
            if (zone == null || zone.shelf == null || zone.shelf.columns == null) return null;
            if (zone.columnIndex < 0 || zone.columnIndex >= zone.shelf.columns.Length) return null;
            ShelfStorage.StorageColumn column = zone.shelf.columns[zone.columnIndex];
            return column != null ? column.levels : null;
        }

        /// <summary>Premier niveau libre de la colonne de la zone, -1 si pleine.</summary>
        public static int FreeLevel(ShelfInteractionZone zone)
        {
            Transform[] levels = ColumnLevels(zone);
            if (levels == null) return -1;
            for (int i = 0; i < levels.Length; i++)
                if (levels[i] != null && levels[i].GetComponentInChildren<CargoBox>(true) == null) return i;
            return -1;
        }
    }
}
