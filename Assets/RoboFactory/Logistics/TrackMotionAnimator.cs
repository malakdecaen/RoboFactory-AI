using UnityEngine;
using RoboFactory.Sim;

namespace RoboFactory.Logistics
{
    /// <summary>
    /// Animates the tracks of a robot moved by script when its GridRobotController is
    /// disabled (the case of R2 in the scene: its keyboard controller is turned off, so
    /// DualProceduralTracksFromWheels no longer receives any distance and the tracks stay
    /// frozen). Same computation as GridRobotController.AnimateTracks: forward / backward from the
    /// displacement, rotation in place from the angle (left and right tracks in opposite
    /// directions). Purely visual: moves nothing and touches no robot logic.
    /// If the controller is active (the case of R1), it already animates the tracks: this component does nothing.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TrackMotionAnimator : MonoBehaviour
    {
        public DualProceduralTracksFromWheels Tracks;
        public GridRobotController Controller;
        [Tooltip("Track spacing (m); taken from the GridRobotController if there is one.")]
        public float TrackWidth = .45f;

        private Vector3 previousPosition;
        private Quaternion previousRotation;
        private bool primed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoInstall()
        {
            foreach (DualProceduralTracksFromWheels tracks in
                     FindObjectsByType<DualProceduralTracksFromWheels>(FindObjectsSortMode.None))
            {
                if (tracks == null || tracks.GetComponent<TrackMotionAnimator>() != null) continue;
                var animator = tracks.gameObject.AddComponent<TrackMotionAnimator>();
                animator.Tracks = tracks;
                animator.Controller = tracks.GetComponentInParent<GridRobotController>();
                if (animator.Controller != null) animator.TrackWidth = animator.Controller.trackWidth;
            }
        }

        private void OnEnable() { primed = false; }

        private void LateUpdate()
        {
            if (Tracks == null) return;
            // Original controller active: it already animates the tracks (R1).
            if (Controller != null && Controller.enabled && Controller.gameObject.activeInHierarchy)
            {
                primed = false;
                return;
            }
            Transform t = transform;
            if (!primed)
            {
                previousPosition = t.position;
                previousRotation = t.rotation;
                primed = true;
                return;
            }

            float left = 0f, right = 0f;
            Vector3 move = t.position - previousPosition;
            move.y = 0f;
            float distance = move.magnitude;
            if (distance > Mathf.Epsilon)
            {
                float sign = Vector3.Dot(previousRotation * Vector3.forward, move / distance) >= 0f ? 1f : -1f;
                left += distance * sign;
                right += distance * sign;
            }
            float angle = Mathf.DeltaAngle(previousRotation.eulerAngles.y, t.rotation.eulerAngles.y);
            if (Mathf.Abs(angle) > Mathf.Epsilon)
            {
                float turn = angle * Mathf.Deg2Rad * (TrackWidth * .5f);
                left += turn;
                right -= turn;
            }
            if (left != 0f || right != 0f) Tracks.AnimateTracksByDistance(left, right);

            previousPosition = t.position;
            previousRotation = t.rotation;
        }
    }
}
