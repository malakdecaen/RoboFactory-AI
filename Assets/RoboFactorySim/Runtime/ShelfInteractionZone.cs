using UnityEngine;

namespace RoboFactory.Sim
{
    /// <summary>
    /// A robot's access point to a storage column: the robot docks on dockPoint
    /// (position and orientation) before taking or placing a box.
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

        /// <summary>True if the robot is on the docking point, within tolerance, and correctly oriented.</summary>
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
