using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using RoboFactory.Sim;

namespace RoboFactory.Logistics
{
    /// <summary>
    /// Shared grid navigation — pure logic, no business knowledge.
    ///
    /// The robot moves on a 0.25 m grid, turns in 45° steps and ends
    /// with a fine approach on the dock. Same engine as the autopilot, extracted as a
    /// reusable component: the agents (TruckLoaderAgent, future mobile arm...)
    /// only call NavigateTo() and read the result.
    ///
    /// If the path is blocked, the robot waits in place and retries periodically:
    /// an obstacle (worker, other robot, box) is most often temporary.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GridMover : MonoBehaviour
    {
        public enum NavResult { Unknown, Arrived, Stopped }

        [Header("Robot")]
        public GridRobotController Controller;
        public Animator Animator;

        [Header("Grid (must match the GridRobotController)")]
        public float Cell = .25f;
        public float RotationStep = 45f;
        [Tooltip("Colliders taken into account by route planning.")]
        public LayerMask ObstacleMask = ~0;
        public Vector3 CheckHalfExtents = new Vector3(.18f, .28f, .18f);
        public Vector3 CheckOffset = new Vector3(0f, .28f, 0f);
        [Tooltip("Half-extent of the navigable area in metres. 0 = automatic (GridRobotController grid).")]
        public float MapHalfExtent = 0f;

        [Header("Driving")]
        public float MoveSpeed = 1.8f;
        public float TurnSpeed = 260f;
        public float StepPause = 0f;
        public float ArriveTolerance = .01f;
        [Tooltip("No path found: wait in place and retry after this delay.")]
        public float ReplanInterval = .6f;

        /// <summary>Remaining path, consumed cell by cell (for display).</summary>
        public IReadOnlyList<Vector3> CurrentPath => displayPath;
        public NavResult LastResult { get; private set; } = NavResult.Unknown;
        /// <summary>Target of the current trip (world dock).</summary>
        public Vector3 Target { get; private set; }
        public bool EnRoute { get; private set; }
        /// <summary>Reused by the agent: false -> every movement stops.</summary>
        public bool Enabled = true;

        /// <summary>External condition to abort the current trip (e.g. work showing up).</summary>
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
                // The mover owns the movement: the keyboard must not fight it.
                Controller.controlsLocked = true;
                halfExtent = MapHalfExtent > 0f ? MapHalfExtent
                    : Mathf.Max(1f, Mathf.Max(Controller.gridCellsX, Controller.gridCellsZ) * Controller.cellSize * .5f);
            }
            overlapHits = new Collider[24];
        }

        /// <summary>Requests a route replan (obstacle detected on the path).</summary>
        public void RequestReplan() { replanRequested = true; }

        // ------------------------------------------------------------------ full trip

        /// <summary>
        /// Drives the robot onto the dock point with the requested heading.
        /// Waits and retries as long as no route exists (robust to blockages).
        /// Ends on Arrived, when Enabled/the coroutine stops,
        /// or when abortWhen (optional) becomes true -> result Stopped.
        /// </summary>
        public IEnumerator NavigateTo(Vector3 dock, Vector3 heading, System.Func<bool> abortWhen = null)
        {
            this.abortWhen = abortWhen;
            LastResult = NavResult.Unknown;
            Target = dock;
            EnRoute = true;
            heading.y = 0f;
            if (heading.sqrMagnitude < .001f) heading = transform.forward;
            heading.Normalize();

            Vector3 goalCell = Snap(dock);
            bool planned = TryPlan(Snap(transform.position), goalCell);
            if (!planned || path.Count == 0)
            {
                Debug.Log("[Mover:" + name + "] no route to " + goalCell.ToString("F2") +
                          ": waiting in place, retrying every " + ReplanInterval + " s.");
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
                LastResult = NavResult.Stopped;
                yield break;
            }

            displayPath.Clear();
            displayPath.AddRange(path);
            while (CanDrive)
            {
                replanRequested = false;
                yield return FollowPath();
                if (!replanRequested) break;
                // Mid-route replan: same logic as at the start, with a wait if blocked.
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
                LastResult = NavResult.Stopped;
                yield break;
            }

            // Fine approach only at short range: never a shortcut across the map.
            if (Vector3.Distance(transform.position, dock) > 1.2f)
            {
                Debug.LogWarning("[Mover:" + name + "] arrived at " +
                                 Vector3.Distance(transform.position, dock).ToString("F2") +
                                 " m from the dock: docking aborted.");
                ClearPath();
                EnRoute = false;
                abortWhen = null;
                LastResult = NavResult.Stopped;
                yield break;
            }

            replanRequested = false; // docking engaged: replans only concern the grid path
            yield return TurnTo(heading);
            yield return FineApproach(dock, heading);
            yield return new WaitForSeconds(.1f);
            displayPath.Clear();
            EnRoute = false;
            abortWhen = null;
            LastResult = NavResult.Arrived;
        }

        /// <summary>Stops any movement in progress.</summary>
        public void StopNavigation()
        {
            Enabled = false;
            ClearPath();
            EnRoute = false;
            LastResult = NavResult.Stopped;
        }

        private void ClearPath()
        {
            path.Clear();
            pathCursor = 0;
            displayPath.Clear();
        }

        // ------------------------------------------------------------------ path following

        /// <summary>Follows the grid in continuous straight segments (one turn per heading change).</summary>
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
                    // Re-checks each cell before entering it: a collider that appeared
                    // on the route triggers a replan instead of being crossed.
                    if (!IsFree(path[i]))
                    {
                        RequestReplan();
                        yield break;
                    }
                    yield return MoveTo(path[i]);
                    if (replanRequested) yield break; // cell interrupted: not consumed
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

        /// <summary>Last centimetres: exact position and heading for docking.</summary>
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

        // ------------------------------------------------------------------ grid + search

        public Vector3 Snap(Vector3 world)
        {
            return new Vector3(Mathf.Round(world.x / Cell) * Cell, world.y, Mathf.Round(world.z / Cell) * Cell);
        }

        public float HalfExtent => halfExtent;

        /// <summary>Free cell for the debug overlay (full clearance).</summary>
        public bool CellFree(Vector3 world)
        {
            return CellFreeAt(world, 1f);
        }

        /// <summary>Traversable cell for the planner (colliders only).</summary>
        public bool IsFree(Vector3 world)
        {
            return CellFreeAt(world, clearance);
        }

        private bool CellFreeAt(Vector3 world, float clearanceScale)
        {
            Vector3 center = new Vector3(world.x, transform.position.y, world.z) + CheckOffset;
            // Forbidden logical volumes (NavBlocker): blocking even without a physical collider.
            if (NavBlocker.BlocksCell(center, CheckHalfExtents * clearanceScale)) return false;
            int count = Physics.OverlapBoxNonAlloc(center, CheckHalfExtents * clearanceScale, overlapHits,
                                                   Quaternion.identity, ObstacleMask, QueryTriggerInteraction.Ignore);
            float floorCut = transform.position.y + .06f;
            for (int i = 0; i < count; i++)
            {
                Collider hit = overlapHits[i];
                if (hit == null || hit.isTrigger) continue;
                if (hit.bounds.max.y <= floorCut) continue; // floor / markings flush with the floor
                if (hit.transform.IsChildOf(transform)) continue; // itself, load included
                return false;
            }
            return true;
        }

        /// <summary>Route to the goal, or to a neighbouring cell if the dock is occupied.</summary>
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
            // Narrow aisles: retry with a smaller margin before declaring it impossible.
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
            // The dock cell is always accepted: it touches the shelf by construction.

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
            // GridRobotController forces IsMoving=false while controlsLocked: the mover drives it.
            bool moving = Enabled && (path.Count > pathCursor ||
                          Vector3.Distance(transform.position, lastPosition) > .0005f);
            Animator.SetBool("IsMoving", moving);
            lastPosition = transform.position;
        }
    }
}
