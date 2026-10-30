using UnityEngine;

namespace RoboFactory.Sim
{
    /// <summary>
    /// Roues gauche / droite de la version publique : chaque côté tourne de la distance parcourue
    /// par sa chenille (avance, recul, rotation sur place en sens opposés).
    /// </summary>
    public class DualProceduralTracksFromWheels : MonoBehaviour
    {
        public Transform[] leftWheels = new Transform[0];
        public Transform[] rightWheels = new Transform[0];
        public float wheelRadius = .08f;
        [Tooltip("Axe local de rotation des roues.")]
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
