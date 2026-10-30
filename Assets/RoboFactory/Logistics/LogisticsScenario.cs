using UnityEngine;
using RoboFactory.Sim;

namespace RoboFactory.Logistics
{
    /// <summary>
    /// Scénarios de test du flux logistique, lancés depuis le menu contextuel du composant
    /// (roue dentée dans l'inspecteur, éditeur ou Play).
    ///
    /// Matrice couverte :
    ///   - "Un carton sur le banc"  : test 1 carton (R2 doit le livrer au camion)
    ///   - "Précharger le camion"   : test camion déjà partiellement chargé
    ///   - "Vider les bancs"        : test banc vide (R2 doit rester en AttenteColis)
    ///   - "Vider le camion"        : renvoie les cartons du camion vers les palettes
    ///   - "Rapport"                : état de tous les cartons + remplissage camion
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LogisticsScenario : MonoBehaviour
    {
        [Header("Préchargement")]
        [Tooltip("Nombre de cartons déplacés des palettes vers le camion.")]
        public int PreloadCount = 4;

        [ContextMenu("Rapport logistique")]
        public void Report()
        {
            var registry = CargoRegistry.Instance != null ? CargoRegistry.Instance
                                                          : FindAnyObjectByType<CargoRegistry>();
            if (registry != null) registry.Scan();
            foreach (CargoBox box in FindObjectsByType<CargoBox>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Debug.Log("[Scénario] " + box.name + " : " + CargoStatusNames.Of(CargoRegistry.Classify(box)));
            foreach (TruckBay bay in FindObjectsByType<TruckBay>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Debug.Log("[Scénario] camion " + bay.name + " : " + bay.LoadedCount + "/" + bay.Capacity +
                          (bay.IsFull ? " (PLEIN)" : ""));
        }

        [ContextMenu("Un carton sur le banc")]
        public void OneBoxOnBench()
        {
            CargoBox box = FirstBoxAt(StationRole.Pallet);
            if (box == null) { Debug.LogWarning("[Scénario] aucun carton sur les palettes."); return; }
            ShelfInteractionZone zone = FirstFreeZone(StationRole.Bench);
            if (zone == null) { Debug.LogWarning("[Scénario] aucune place libre sur les bancs."); return; }
            if (PutBox(zone, box))
                Debug.Log("[Scénario] " + box.name + " placé sur le banc " + zone.shelf.name +
                          " (colonne " + zone.columnIndex + ").");
        }

        [ContextMenu("Précharger le camion")]
        public void PreloadTruck()
        {
            TruckBay bay = FindAnyObjectByType<TruckBay>();
            if (bay == null || bay.Storage == null) { Debug.LogWarning("[Scénario] aucun TruckBay."); return; }
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
            Debug.Log("[Scénario] " + moved + " carton(s) préchargé(s) dans le camion (" +
                      bay.LoadedCount + "/" + bay.Capacity + ").");
            var registry = CargoRegistry.Instance;
            if (registry != null) registry.Scan();
        }

        [ContextMenu("Vider les bancs")]
        public void ClearBenches()
        {
            int moved = 0;
            foreach (CargoBox box in FindObjectsByType<CargoBox>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (box == null || CargoRegistry.Classify(box) != CargoStatus.OnBench) continue;
                if (MoveToFreePallet(box)) moved++;
            }
            Debug.Log("[Scénario] " + moved + " carton(s) retiré(s) des bancs -> palettes.");
        }

        [ContextMenu("Vider le camion")]
        public void ClearTruck()
        {
            int moved = 0;
            foreach (CargoBox box in FindObjectsByType<CargoBox>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (box == null || CargoRegistry.Classify(box) != CargoStatus.InTruck) continue;
                if (MoveToFreePallet(box)) moved++;
            }
            Debug.Log("[Scénario] " + moved + " carton(s) sorti(s) du camion -> palettes.");
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

        /// <summary>Première zone avec un niveau libre dans le rôle demandé.</summary>
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

        /// <summary>Retire le carton de la table de SON ShelfStorage actuel (s'il y est enregistré).</summary>
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

        /// <summary>Déplace le carton sur le premier niveau libre d'une palette.</summary>
        private bool MoveToFreePallet(CargoBox box)
        {
            ShelfInteractionZone zone = FirstFreeZone(StationRole.Pallet);
            if (zone == null)
            {
                // Aucune place : on le dépose simplement hors stockage (statut "au sol").
                RemoveFromAnyStorage(box);
                box.transform.SetParent(null, true);
                box.isCarried = false;
                return true;
            }
            return PutBox(zone, box);
        }

        /// <summary>Premier niveau libre d'une colonne de stockage, -1 si pleine.</summary>
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
