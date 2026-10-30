using UnityEngine;

namespace RoboFactory.Sim
{
    /// <summary>
    /// Point d'accroche du carton porté. Dans la version publique la prise et la dépose sont faites
    /// directement par les scripts RoboFactory (transfert immédiat, sans animation de bras) : ce composant
    /// n'expose que l'état et le point d'accroche.
    /// </summary>
    public class RobotCargoInteractor : MonoBehaviour
    {
        public Transform cargoAttachPoint;

        public bool HasBox => false;
        public bool IsBusy => false;
    }
}
