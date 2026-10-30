using UnityEngine;

namespace RoboFactory.Sim
{
    /// <summary>
    /// Point d'accès d'un robot à une colonne de stockage : le robot s'arrime sur dockPoint
    /// (position et orientation) avant de prendre ou de poser un carton.
    /// </summary>
    public class ShelfInteractionZone : MonoBehaviour
    {
        public enum AccessSide { Front, Back }

        public ShelfStorage shelf;
        [Min(0)] public int columnIndex;
        public AccessSide accessSide = AccessSide.Front;
        public Transform dockPoint;
        public float positionTolerance = .05f;
        public float angleTolerance = 5f;

        /// <summary>Vrai si le robot est sur le point d'arrimage, à la tolérance près, et bien orienté.</summary>
        public bool IsRobotCorrectlyDocked(Transform robot)
        {
            if (robot == null || dockPoint == null) return false;
            Vector2 a = new Vector2(robot.position.x, robot.position.z);
            Vector2 b = new Vector2(dockPoint.position.x, dockPoint.position.z);
            if ((a - b).magnitude > positionTolerance) return false;
            Vector3 fa = Vector3.ProjectOnPlane(robot.forward, Vector3.up);
            Vector3 fb = Vector3.ProjectOnPlane(dockPoint.forward, Vector3.up);
            return Vector3.Angle(fa, fb) <= angleTolerance;
        }

        private void OnDrawGizmosSelected()
        {
            if (dockPoint == null) return;
            Gizmos.color = new Color(.2f, 1f, .4f);
            Gizmos.DrawWireSphere(dockPoint.position, Mathf.Max(.05f, positionTolerance));
            Gizmos.color = new Color(.3f, .6f, 1f);
            Gizmos.DrawLine(dockPoint.position, dockPoint.position + dockPoint.forward * .5f);
        }
    }
}
