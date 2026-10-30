using UnityEngine;

namespace RoboFactory.Sim
{
    /// <summary>
    /// Attach point of the carried box. In the public version picking and dropping are done
    /// directly by the RoboFactory scripts (immediate transfer, no arm animation): this component
    /// only exposes the state and the attach point.
    /// </summary>
    public class RobotCargoInteractor : MonoBehaviour
    {
        public Transform cargoAttachPoint;

        public bool HasBox => false;
        public bool IsBusy => false;
    }
}
