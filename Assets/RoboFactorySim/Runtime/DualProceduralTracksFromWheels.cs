using UnityEngine;

namespace RoboFactory.Sim
{
    /// <summary>
    /// Left / right wheels of the public version: each side turns by the distance travelled
    /// by its track (forward, backward, rotation in place in opposite directions).
    /// </summary>
    public class DualProceduralTracksFromWheels : MonoBehaviour
    {
        public Transform[] leftWheels = new Transform[0];
        public Transform[] rightWheels = new Transform[0];
        public float wheelRadius = .08f;
        [Tooltip("Local rotation axis of the wheels.")]
        public Vector3 spinAxis = Vector3.right;

        public void AnimateTracksByDistance(float leftDistance, float rightDistance)
        {
            Spin(leftWheels, leftDistance);
            Spin(rightWheels, rightDistance);
        }

        private void Spin(Transform[] wheels, float distance)
        {
            if (wheels == null || wheelRadius <= 0f) return;
            float degrees = distance / wheelRadius * Mathf.Rad2Deg;
            foreach (Transform w in wheels)
                if (w != null) w.Rotate(spinAxis, degrees, Space.Self);
        }
    }
}
