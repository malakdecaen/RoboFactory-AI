using UnityEngine;

namespace RoboFactory.Sim
{
    /// <summary>
    /// Grid mobile base of the public version. Movements are driven by the
    /// RoboFactory scripts (GridMover / autopilot): this component carries the grid description, snaps the
    /// robot at start and turns its wheels according to the real movement.
    /// </summary>
    [DisallowMultipleComponent]
    public class GridRobotController : MonoBehaviour
    {
        [Header("Grid")]
        public float cellSize = .5f;
        public int gridCellsX = 30;
        public int gridCellsZ = 30;
        [Tooltip("Position snapping step at start (m).")]
        public float snapStep = .25f;
        [Tooltip("Orientation snapping step at start (degrees).")]
        public float snapAngle = 45f;

        [Header("Tracks / wheels")]
        [Tooltip("Left / right wheel spacing (m).")]
        public float trackWidth = .45f;
        public DualProceduralTracksFromWheels tracks;

        [Tooltip("True when a script drives the robot (always the case in the demo).")]
        public bool controlsLocked = true;

        private Vector3 lastPosition;
        private Quaternion lastRotation;

        private void Start()
        {
            if (snapStep > 0f)
            {
                Vector3 p = transform.position;
                p.x = Mathf.Round(p.x / snapStep) * snapStep;
                p.z = Mathf.Round(p.z / snapStep) * snapStep;
                transform.position = p;
            }
            if (snapAngle > 0f)
            {
                float yaw = Mathf.Round(transform.eulerAngles.y / snapAngle) * snapAngle;
                transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            }
            lastPosition = transform.position;
            lastRotation = transform.rotation;
        }

        private void LateUpdate()
        {
            if (tracks == null) { Remember(); return; }
            Vector3 delta = transform.position - lastPosition;
            delta.y = 0f;
            float forward = Vector3.Dot(delta, lastRotation * Vector3.forward);
            float yaw = Mathf.DeltaAngle(lastRotation.eulerAngles.y, transform.eulerAngles.y) * Mathf.Deg2Rad;
            float turn = yaw * trackWidth * .5f;
            if (Mathf.Abs(forward) > 1e-5f || Mathf.Abs(turn) > 1e-5f)
                tracks.AnimateTracksByDistance(forward + turn, forward - turn);
            Remember();
        }

        private void Remember()
        {
            lastPosition = transform.position;
            lastRotation = transform.rotation;
        }
    }
}
