using UnityEngine;

namespace RoboFactory.Sim
{
    /// <summary>
    /// Carton de la version publique (remplace le carton de l'asset Warehouse Robot Controller).
    /// Type logistique : A = caisse bois, B = gros carton, C = carton moyen.
    /// </summary>
    [DisallowMultipleComponent]
    public class CargoBox : MonoBehaviour
    {
        [HideInInspector] public bool isCarried;

        [Tooltip("Commande propriétaire. -1 = stock libre.")]
        public int orderId = -1;

        [Tooltip("Type forcé (A, B ou C). Vide = déduit du nom de l'objet.")]
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
