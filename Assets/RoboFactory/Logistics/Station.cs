using UnityEngine;

namespace RoboFactory.Logistics
{
    /// <summary>Rôle logistique d'une station de stockage.</summary>
    public enum StationRole
    {
        Pallet,
        Bench,
        Truck
    }

    /// <summary>
    /// Marqueur optionnel posé sur un objet de stockage pour forcer son rôle.
    /// Sans marqueur, le rôle est déduit : TruckBay parent = camion, nom contenant
    /// "shelving"/"bench"/"banc" = banc, sinon palette.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Station : MonoBehaviour
    {
        [Tooltip("Rôle forcé. Laisser décoché pour déduire du nom / du TruckBay parent.")]
        public bool useOverride;
        public StationRole overrideRole = StationRole.Bench;

        public StationRole Role => useOverride ? overrideRole : Guess(gameObject);

        public static StationRole RoleOf(Component storageLike)
        {
            if (storageLike == null) return StationRole.Pallet;
            var marker = storageLike.GetComponentInParent<Station>();
            if (marker != null) return marker.Role;
            if (storageLike.GetComponentInParent<TruckBay>() != null) return StationRole.Truck;
            return Guess(storageLike.gameObject);
        }

        public static StationRole Guess(GameObject go)
        {
            if (go == null) return StationRole.Pallet;
            string n = go.name.ToLowerInvariant();
            if (n.Contains("truck") || n.Contains("camion") || n.Contains("trailer")) return StationRole.Truck;
            if (n.Contains("shelving") || n.Contains("bench") || n.Contains("banc")) return StationRole.Bench;
            return StationRole.Pallet;
        }
    }
}
