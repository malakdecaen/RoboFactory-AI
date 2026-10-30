using UnityEngine;

namespace RoboFactory.Sim
{
    /// <summary>
    /// Box of the public version (replaces the box of the Warehouse Robot Controller asset).
    /// Logistics type: A = wooden crate, B = big cardboard box, C = medium cardboard box.
    /// </summary>
    [DisallowMultipleComponent]
    public class CargoBox : MonoBehaviour
    {
        [HideInInspector] public bool isCarried;

        [Tooltip("Owning order. -1 = free stock.")]
        public int orderId = -1;

        [Tooltip("Forced type (A, B or C). Empty = deduced from the object's name.")]
        public string boxType = "";

        public string TypeLetter
        {
            get
            {
                if (!string.IsNullOrEmpty(boxType)) return boxType.Trim().ToUpperInvariant();
                string lower = name.ToLowerInvariant();
                if (lower.Contains("wooden") || lower.Contains("crate")) return "A";
                if (lower.Contains("big")) return "B";
                if (lower.Contains("cardboard") || lower.Contains("carton")) return "C";
                return "?";
            }
        }
    }
}
