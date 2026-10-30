using UnityEngine;

namespace RoboFactory.Logistics
{
    /// <summary>Logistics role of a storage station.</summary>
    public enum StationRole
    {
        Pallet,
        Bench,
        Truck
    }

    /// <summary>
    /// Optional marker placed on a storage object to force its role.
    /// Without a marker, the role is deduced: parent TruckBay = truck, name containing
    /// "shelving"/"bench"/"banc" = bench, otherwise pallet (French object names are matched too).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Station : MonoBehaviour
    {
        [Tooltip("Forced role. Leave unchecked to deduce it from the name / the parent TruckBay.")]
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
