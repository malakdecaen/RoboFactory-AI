using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using RoboFactory.Sim;

namespace RoboFactory.Logistics
{
    /// <summary>
    /// Navigation grille partagée — logique pure, aucune connaissance métier.
    ///
    /// Le robot avance sur une grille de 0,25 m, tourne par pas de 45° et termine
    /// par une approche fine sur le dock. Même moteur que l'autopilot, extrait en
    /// composant réutilisable : les agents (TruckLoaderAgent, futur bras mobile...)
    /// ne font que demander NavigateTo() et lire le résultat.
    ///
    /// Si le trajet est bloqué, le robot attend sur place et réessaie périodiquement :
    /// un obstacle (ouvrier, autre robot, carton) est le plus souvent temporaire.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GridMover : MonoBehaviour
    {
        public enum NavResult { Inconnu, Arrive, Arrete }

        [Header("Robot")]
        public GridRobotController Controller;
        public Animator Animator;

        [Header("Grille (doit correspondre au GridRobotController)")]
        public float Cell = .25f;
        public float RotationStep = 45f;
        [Tooltip("Colliders pris en compte par le calcul d'itinéraire.")]
        public LayerMask ObstacleMask = ~0;
        public Vector3 CheckHalfExtents = new Vector3(.18f, .28f, .18f);
        public Vector3 CheckOffset = new Vector3(0f, .28f, 0f);
        [Tooltip("Demi-portée de la zone navigable en mètres. 0 = automatique (grille du GridRobotController).")]
        public float MapHalfExtent = 0f;

        [Header("Conduite")]
        public float MoveSpeed = 1.8f;
        public float TurnSpeed = 260f;
        public float StepPause = 0f;
        public float ArriveTolerance = .01f;
        [Tooltip("Aucun trajet trouvé : attente sur place et nouvel essai après ce délai.")]
        public float ReplanInterval = .6f;

        /// <summary>Chemin restant, consommé cellule par cellule (pour l'affichage).</summary>
        public IReadOnlyList<Vector3> CurrentPath => displayPath;
        public NavResult LastResult { get; private set; } = NavResult.Inconnu;
        /// <summary>Cible du trajet en cours (dock monde).</summary>
        public Vector3 Target { get; private set; }
        public bool EnRoute { get; private set; }
        /// <summary>Réutilisé par l'agent : faux -> tous les déplacements s'arrêtent.</summary>
        public bool Enabled = true;

        /// <summary>Condition externe d'abandon du trajet en cours (ex. du travail qui apparaît).</summary>
        private System.Func<bool> abortWhen;
        private bool CanDrive => Enabled && (abortWhen == null || !abortWhen());

        private readonly List<Vector3> path = new List<Vector3>();
        private readonly List<Vector3> displayPath = new List<Vector3>();
        private int pathCursor;
        private float halfExtent;
        private float clearance = 1f;
        private bool replanRequested;
        private Vector3 lastPosition;
        private Collider[] overlapHits;

        private void Awake()
        {
            if (Controller == null) Controller = GetComponent<GridRobotController>();
            if (Animator == null) Animator = GetComponent<Animator>();
            if (Controller != null)
            {
                // Le mover possède le mouvement : le clavier ne doit pas le contrer.
                Controller.controlsLocked = true;
                halfExtent = MapHalfExtent > 0f ? MapHalfExtent
                    : Mathf.Max(1f, Mathf.Max(Controller.gridCellsX, Controller.gridCellsZ) * Controller.cellSize * .5f);
            }
            overlapHits = new Collider[24];
        }

        /// <summary>Demande un recalcul d'itinéraire (obstacle détecté sur le trajet).</summary>
        public void RequestReplan() { replanRequested = true; }

        // ------------------------------------------------------------------ trajet complet

        /// <summary>
        /// Conduit le robot sur le point de dock avec le cap demandé.
        /// Attend et réessaie tant qu'aucun itinéraire n'existe (robuste aux blocages).
        /// Se termine sur Arrivé, quand Enabled/la coroutine s'arrête,
        /// ou quand abortWhen (optionnel) devient vrai -> résultat Arrete.
        /// </summary>
        public IEnumerator NavigateTo(Vector3 dock, Vector3 heading, System.Func<bool> abortWhen = null)
        {
            this.abortWhen = abortWhen;
            LastResult = NavResult.Inconnu;
            Target = dock;
            EnRoute = true;
            heading.y = 0f;
            if (heading.sqrMagnitude < .001f) heading = transform.forward;
            heading.Normalize();

            Vector3 goalCell = Snap(dock);
            bool planned = TryPlan(Snap(transform.position), goalCell);
            if (!planned || path.Count == 0)
            {
                Debug.Log("[Mover:" + name + "] aucun itinéraire vers " + goalCell.ToString("F2") +
                          " : attente sur place, essai toutes les " + ReplanInterval + " s.");
                while (CanDrive)
                {
                    yield return new WaitForSeconds(ReplanInterval);
                    if (!CanDrive) break;
                    planned = TryPlan(Snap(transform.position), goalCell);
                    if (planned && path.Count > 0) break;
                }
            }
            if (!planned || path.Count == 0)
            {
                ClearPath();
                EnRoute = false;
                abortWhen = null;
                LastResult = NavResult.Arrete;
                yield break;
            }

            displayPath.Clear();
            displayPath.AddRange(path);
            while (CanDrive)
            {
                replanRequested = false;
                yield return FollowPath();
                if (!replanRequested) break;
                // Replan mid-route : même logique qu'au départ, avec attente si bloqué.
                planned = TryPlan(Snap(transform.position), goalCell);
                while ((!planned || path.Count == 0) && CanDrive)
                {
                    yield return new WaitForSeconds(ReplanInterval);
                    if (!CanDrive) break;
                    planned = TryPlan(Snap(transform.position), goalCell);
                }
                if (!CanDrive) break;
                displayPath.Clear();
                displayPath.AddRange(path);
            }
            if (!CanDrive)
            {
                EnRoute = false;
                abortWhen = null;
                LastResult = NavResult.Arrete;
                yield break;
            }

            // Approche fine uniquement à courte portée : jamais un raccourci à travers la map.
            if (Vector3.Distance(transform.position, dock) > 1.2f)
            {
                Debug.LogWarning("[Mover:" + name + "] arrivée à " +
                                 Vector3.Distance(transform.position, dock).ToString("F2") +
                                 " m du dock : arrimage abandonné.");
                ClearPath();
                EnRoute = false;
                abortWhen = null;
                LastResult = NavResult.Arrete;
                yield break;
            }

            replanRequested = false; // arrimage engagé : les replans ne concernent que le trajet grille
            yield return TurnTo(heading);
            yield return FineApproach(dock, heading);
            yield return new WaitForSeconds(.1f);
            displayPath.Clear();
            EnRoute = false;
            abortWhen = null;
            LastResult = NavResult.Arrive;
        }

        /// <summary>Arrête tout déplacement en cours.</summary>
        public void StopNavigation()
        {
            Enabled = false;
            ClearPath();
            EnRoute = false;
            LastResult = NavResult.Arrete;
        }

        private void ClearPath()
        {
            path.Clear();
            pathCursor = 0;
            displayPath.Clear();
        }

        // ------------------------------------------------------------------ suivi du chemin

        /// <summary>Suit la grille par segments droits continus (un virage par changement de cap).</summary>
        private IEnumerator FollowPath()
        {
            int index = 0;
            while (index < path.Count && CanDrive)
            {
                if (replanRequested) yield break;
                int end = index;
                while (end + 1 < path.Count)
                {
                    Vector3 current = path[end] - transform.position;
                    current.y = 0f;
                    Vector3 next = path[end + 1] - path[end];
                    next.y = 0f;
                    if (current.sqrMagnitude > 1e-6f && next.sqrMagnitude > 1e-6f &&
                        Vector3.Dot(current.normalized, next.normalized) < .999f) break;
                    end++;
                }

                Vector3 head = path[end] - transform.position;
                head.y = 0f;
                if (head.sqrMagnitude > 1e-6f) yield return TurnTo(head.normalized);
                if (replanRequested) yield break;

                for (int i = index; i <= end && CanDrive; i++)
                {
                    // Re-vérifie chaque cellule avant d'y entrer : un collider apparu
                    // sur la route déclenche un replan au lieu d'être traversé.
                    if (!IsFree(path[i]))
                    {
                        RequestReplan();
                        yield break;
                    }
                    yield return MoveTo(path[i]);
                    if (replanRequested) yield break; // cellule interrompue : pas consommée
                    pathCursor = i + 1;
                    if (displayPath.Count > 0) displayPath.RemoveAt(0);
                    if (StepPause > 0f && i < end) yield return new WaitForSeconds(StepPause);
                }

                index = end + 1;
            }
        }

        private IEnumerator MoveTo(Vector3 target)
        {
            target.y = transform.position.y;
            while (Vector3.Distance(transform.position, target) > ArriveTolerance && CanDrive && !replanRequested)
            {
                transform.position = Vector3.MoveTowards(transform.position, target, MoveSpeed * Time.deltaTime);
                yield return null;
            }
            transform.position = target;
        }

        private IEnumerator TurnTo(Vector3 worldDirection)
        {
            worldDirection.y = 0f;
            if (worldDirection.sqrMagnitude < 1e-6f) yield break;
            float wanted = Mathf.Round(Mathf.Atan2(worldDirection.x, worldDirection.z) * Mathf.Rad2Deg / RotationStep) * RotationStep;
            Quaternion target = Quaternion.Euler(0f, wanted, 0f);
            while (Quaternion.Angle(transform.rotation, target) > .5f && CanDrive && !replanRequested)
            {
                transform.rotation = Quaternion.RotateTowards(transform.rotation, target, TurnSpeed * Time.deltaTime);
                yield return null;
            }
            transform.rotation = target;
        }

        /// <summary>Derniers centimètres : position et cap exacts pour l'arrimage.</summary>
        private IEnumerator FineApproach(Vector3 dock, Vector3 heading)
        {
            Quaternion target = Quaternion.LookRotation(heading, Vector3.up);
            while (Quaternion.Angle(transform.rotation, target) > .4f && CanDrive)
            {
                transform.rotation = Quaternion.RotateTowards(transform.rotation, target, TurnSpeed * Time.deltaTime);
                yield return null;
            }
            transform.rotation = target;

            Vector3 flatDock = new Vector3(dock.x, transform.position.y, dock.z);
            while (Vector3.Distance(transform.position, flatDock) > .005f && CanDrive)
            {
                transform.position = Vector3.MoveTowards(transform.position, flatDock, MoveSpeed * .5f * Time.deltaTime);
                yield return null;
            }
            transform.position = flatDock;
        }

        // ------------------------------------------------------------------ grille + recherche

        public Vector3 Snap(Vector3 world)
        {
            return new Vector3(Mathf.Round(world.x / Cell) * Cell, world.y, Mathf.Round(world.z / Cell) * Cell);
        }

        public float HalfExtent => halfExtent;

        /// <summary>Cellule libre pour le debug overlay (clearance pleine).</summary>
        public bool CellFree(Vector3 world)
        {
            return CellFreeAt(world, 1f);
        }

        /// <summary>Cellule traversable pour le planificateur (colliders seulement).</summary>
        public bool IsFree(Vector3 world)
        {
            return CellFreeAt(world, clearance);
        }

        private bool CellFreeAt(Vector3 world, float clearanceScale)
        {
            Vector3 center = new Vector3(world.x, transform.position.y, world.z) + CheckOffset;
            // Volumes logiques interdits (NavBlocker) : bloquants même sans collider physique.
            if (NavBlocker.BlocksCell(center, CheckHalfExtents * clearanceScale)) return false;
            int count = Physics.OverlapBoxNonAlloc(center, CheckHalfExtents * clearanceScale, overlapHits,
                                                   Quaternion.identity, ObstacleMask, QueryTriggerInteraction.Ignore);
            float floorCut = transform.position.y + .06f;
            for (int i = 0; i < count; i++)
            {
                Collider hit = overlapHits[i];
                if (hit == null || hit.isTrigger) continue;
                if (hit.bounds.max.y <= floorCut) continue; // sol / marquages au ras du plancher
                if (hit.transform.IsChildOf(transform)) continue; // soi-même, charge comprise
                return false;
            }
            return true;
        }

        /// <summary>Route vers le but, ou vers une cellule voisine si le dock est occupé.</summary>
        private bool TryPlan(Vector3 start, Vector3 goal)
        {
            if (Plan(start, goal) && path.Count > 0) return true;
            var neighbours = new[]
            {
                goal + new Vector3(Cell, 0f, 0f), goal + new Vector3(-Cell, 0f, 0f),
                goal + new Vector3(0f, 0f, Cell), goal + new Vector3(0f, 0f, -Cell)
            };
            foreach (Vector3 candidate in neighbours)
                if (Plan(start, Snap(candidate)) && path.Count > 0) return true;
            return false;
        }

        private bool Plan(Vector3 start, Vector3 goal)
        {
            // Allées étroites : réessaie avec une marge réduite avant de déclarer impossible.
            float[] clearances = { 1f, .75f, .5f };
            foreach (float scale in clearances)
                if (PlanWith(start, goal, scale)) return true;
            path.Clear();
            pathCursor = 0;
            return false;
        }

        private bool PlanWith(Vector3 start, Vector3 goal, float clearanceScale)
        {
            clearance = clearanceScale;
            path.Clear();
            pathCursor = 0;
            Vector2Int from = ToCell(start);
            Vector2Int to = ToCell(goal);
            if (from == to) return true;
            // La cellule du dock est toujours acceptée : elle touche l'étagère par construction.

            var came = new Dictionary<Vector2Int, Vector2Int> { [from] = from };
            var open = new Queue<Vector2Int>();
            open.Enqueue(from);
            var steps = new[] { Vector2Int.up, Vector2Int.down, Vector2Int.left, Vector2Int.right };
            int guard = 0;
            while (open.Count > 0 && guard++ < 60000)
            {
                Vector2Int node = open.Dequeue();
                if (node == to) break;
                foreach (Vector2Int step in steps)
                {
                    Vector2Int next = node + step;
                    if (came.ContainsKey(next)) continue;
                    Vector3 world = ToWorld(next);
                    if (Mathf.Abs(world.x) > halfExtent || Mathf.Abs(world.z) > halfExtent) continue;
                    if (next != to && !IsFree(world)) continue;
                    came[next] = node;
                    open.Enqueue(next);
                }
            }
            if (!came.ContainsKey(to)) return false;

            for (Vector2Int cell = to; cell != from; cell = came[cell]) path.Add(ToWorld(cell));
            path.Reverse();
            return true;
        }

        private Vector2Int ToCell(Vector3 world)
        {
            return new Vector2Int(Mathf.RoundToInt(world.x / Cell), Mathf.RoundToInt(world.z / Cell));
        }

        private Vector3 ToWorld(Vector2Int cell)
        {
            return new Vector3(cell.x * Cell, transform.position.y, cell.y * Cell);
        }

        // ------------------------------------------------------------------ animation

        private void LateUpdate()
        {
            if (Animator == null) return;
            // GridRobotController force IsMoving=false tant que controlsLocked : le mover le pilote.
            bool moving = Enabled && (path.Count > pathCursor ||
                          Vector3.Distance(transform.position, lastPosition) > .0005f);
            Animator.SetBool("IsMoving", moving);
            lastPosition = transform.position;
        }
    }
}
