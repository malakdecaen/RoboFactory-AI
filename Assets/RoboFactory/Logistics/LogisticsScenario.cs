using UnityEngine;
using RoboFactory.Sim;

namespace RoboFactory.Logistics
{
    /// <summary>
    /// Logistics flow test scenarios, launched from the component's context menu
    /// (gear icon in the inspector, editor or Play).
    ///
    /// Covered matrix:
    ///   - "One box on the bench"  : 1-box test (R2 must deliver it to the truck)
    ///   - "Preload the truck"     : truck already partially loaded
    ///   - "Empty the benches"     : empty bench test (R2 must stay in WaitingForBoxes)
    ///   - "Empty the truck"       : sends the truck's boxes back to the pallets
    ///   - "Report"                : state of every box + truck fill level
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LogisticsScenario : MonoBehaviour
    {
        [Header("Preloading")]
        [Tooltip("Number of boxes moved from the pallets to the truck.")]
        public int PreloadCount = 4;

        [ContextMenu("Logistics report")]
        public void Report()
        {
            var registry = CargoRegistry.Instance != null ? CargoRegistry.Instance
                                                          : FindAnyObjectByType<CargoRegistry>();
            if (registry != null) registry.Scan();
            foreach (CargoBox box in FindObjectsByType<CargoBox>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Debug.Log("[Scenario] " + box.name + ": " + CargoStatusNames.Of(CargoRegistry.Classify(box)));
            foreach (TruckBay bay in FindObjectsByType<TruckBay>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Debug.Log("[Scenario] truck " + bay.name + ": " + bay.LoadedCount + "/" + bay.Capacity +
                          (bay.IsFull ? " (FULL)" : ""));
        }

        [ContextMenu("One box on the bench")]
        public void OneBoxOnBench()
        {
            CargoBox box = FirstBoxAt(StationRole.Pallet);
            if (box == null) { Debug.LogWarning("[Scenario] no box on the pallets."); return; }
            ShelfInteractionZone zone = FirstFreeZone(StationRole.Bench);
            if (zone == null) { Debug.LogWarning("[Scenario] no free slot on the benches."); return; }
            if (PutBox(zone, box))
                Debug.Log("[Scenario] " + box.name + " placed on bench " + zone.shelf.name +
                          " (column " + zone.columnIndex + ").");
        }

        [ContextMenu("Preload the truck")]
        public void PreloadTruck()
        {
            TruckBay bay = FindAnyObjectByType<TruckBay>();
            if (bay == null || bay.Storage == null) { Debug.LogWarning("[Scenario] no TruckBay."); return; }
            int moved = 0;
            foreach (CargoBox box in FindObjectsByType<CargoBox>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (moved >= PreloadCount) break;
                if (box == null || CargoRegistry.Classify(box) != CargoStatus.OnPallet) continue;
                int level = FreeStorageLevel(bay.Storage, 0);
                if (level < 0) break;
                RemoveFromAnyStorage(box);
                bay.Storage.PutBox(0, level, box);
                moved++;
            }
            Debug.Log("[Scenario] " + moved + " box(es) preloaded into the truck (" +
                      bay.LoadedCount + "/" + bay.Capacity + ").");
            var registry = CargoRegistry.Instance;
            if (registry != null) registry.Scan();
        }

        [ContextMenu("Empty the benches")]
        public void ClearBenches()
        {
            int moved = 0;
            foreach (CargoBox box in FindObjectsByType<CargoBox>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (box == null || CargoRegistry.Classify(box) != CargoStatus.OnBench) continue;
                if (MoveToFreePallet(box)) moved++;
            }
            Debug.Log("[Scenario] " + moved + " box(es) removed from the benches -> pallets.");
        }

        [ContextMenu("Empty the truck")]
        public void ClearTruck()
        {
            int moved = 0;
            foreach (CargoBox box in FindObjectsByType<CargoBox>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (box == null || CargoRegistry.Classify(box) != CargoStatus.InTruck) continue;
                if (MoveToFreePallet(box)) moved++;
            }
            Debug.Log("[Scenario] " + moved + " box(es) taken out of the truck -> pallets.");
        }

        // ------------------------------------------------------------------ helpers

        private CargoBox FirstBoxAt(StationRole role)
        {
            foreach (CargoBox box in FindObjectsByType<CargoBox>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (box == null) continue;
                CargoStatus status = CargoRegistry.Classify(box);
                if (role == StationRole.Pallet && status == CargoStatus.OnPallet) return box;
                if (role == StationRole.Bench && status == CargoStatus.OnBench) return box;
            }
            return null;
        }

        /// <summary>First zone with a free level in the requested role.</summary>
        private ShelfInteractionZone FirstFreeZone(StationRole role)
        {
            foreach (ShelfInteractionZone zone in
                     FindObjectsByType<ShelfInteractionZone>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (zone == null || zone.shelf == null) continue;
                if (Station.RoleOf(zone.shelf) != role) continue;
                if (CargoRegistry.FreeLevel(zone) >= 0) return zone;
            }
            return null;
        }

        private bool PutBox(ShelfInteractionZone zone, CargoBox box)
        {
            int level = CargoRegistry.FreeLevel(zone);
            if (level < 0) return false;
            RemoveFromAnyStorage(box);
            zone.shelf.PutBox(zone.columnIndex, level, box);
            return true;
        }

        /// <summary>Removes the box from ITS current ShelfStorage's table (if registered there).</summary>
        private void RemoveFromAnyStorage(CargoBox box)
        {
            ShelfStorage shelf = box.GetComponentInParent<ShelfStorage>();
            if (shelf == null || shelf.columns == null) return;
            for (int c = 0; c < shelf.columns.Length; c++)
            {
                Transform[] levels = shelf.columns[c] != null ? shelf.columns[c].levels : null;
                if (levels == null) continue;
                for (int l = 0; l < levels.Length; l++)
                    if (shelf.GetBox(c, l) == box) { shelf.RemoveBox(c, l); return; }
            }
        }

        /// <summary>Moves the box to the first free level of a pallet.</summary>
        private bool MoveToFreePallet(CargoBox box)
        {
            ShelfInteractionZone zone = FirstFreeZone(StationRole.Pallet);
            if (zone == null)
            {
                // No room: simply drop it outside any storage (status "on the floor").
                RemoveFromAnyStorage(box);
                box.transform.SetParent(null, true);
                box.isCarried = false;
                return true;
            }
            return PutBox(zone, box);
        }

        /// <summary>First free level of a storage column, -1 if full.</summary>
        private static int FreeStorageLevel(ShelfStorage shelf, int column)
        {
            if (shelf == null || shelf.columns == null || column < 0 || column >= shelf.columns.Length) return -1;
            Transform[] levels = shelf.columns[column] != null ? shelf.columns[column].levels : null;
            if (levels == null) return -1;
            for (int i = 0; i < levels.Length; i++)
                if (levels[i] != null && levels[i].GetComponentInChildren<CargoBox>(true) == null) return i;
            return -1;
        }
    }
}
