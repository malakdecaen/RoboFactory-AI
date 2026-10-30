using UnityEngine;
using RoboFactory.Sim;

namespace RoboFactory.Logistics
{
    /// <summary>
    /// Anime les chenilles d'un robot déplacé par script quand son GridRobotController est
    /// désactivé (cas de R2 dans la scène : le contrôleur clavier y est coupé, donc
    /// DualProceduralTracksFromWheels ne reçoit plus aucune distance et les chenilles restent
    /// figées). Même calcul que GridRobotController.AnimateTracks : avance / recul d'après le
    /// déplacement, rotation sur place d'après l'angle (chenille gauche et droite en sens
    /// opposés). Purement visuel : ne déplace rien et ne touche à aucune logique de robot.
    /// Si le contrôleur est actif (cas de R1), il anime déjà les chenilles : ce composant ne fait rien.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TrackMotionAnimator : MonoBehaviour
    {
        public DualProceduralTracksFromWheels Tracks;
        public GridRobotController Controller;
        [Tooltip("Entraxe des chenilles (m) ; repris du GridRobotController s'il existe.")]
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
            // Contrôleur d'origine actif : il anime déjà les chenilles (R1).
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
