using UnityEngine;

namespace RoboFactory.Sim
{
    /// <summary>
    /// Base mobile sur grille de la version publique. Les déplacements sont pilotés par les scripts
    /// RoboFactory (GridMover / autopilot) : ce composant porte la description de la grille, recale le
    /// robot au démarrage et fait tourner ses roues d'après le mouvement réel.
    /// </summary>
    [DisallowMultipleComponent]
    public class GridRobotController : MonoBehaviour
    {
        [Header("Grille")]
        public float cellSize = .5f;
        public int gridCellsX = 30;
        public int gridCellsZ = 30;
        [Tooltip("Pas de recalage de la position au démarrage (m).")]
        public float snapStep = .25f;
        [Tooltip("Pas de recalage de l'orientation au démarrage (degrés).")]
        public float snapAngle = 45f;

        [Header("Chenilles / roues")]
        [Tooltip("Entraxe gauche / droite (m).")]
        public float trackWidth = .45f;
        public DualProceduralTracksFromWheels tracks;

        [Tooltip("Vrai quand un script pilote le robot (toujours le cas dans la démo).")]
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
