using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using RoboFactory.Launcher;
using RoboFactory.Logistics;
using RoboFactory.Packing;
using UnityEngine;
using UnityEngine.SceneManagement;
using RoboFactory.Sim;

namespace RoboFactory.HUD
{
    /// <summary>
    /// DIRECTOR camera (F6): films the run by itself for the demo video. Every shot is a FIXED camera (no orbit, no
    /// drift): its position is chosen once, among many candidate angles, as the best one that sees the subject
    /// without a wall, shelf or machine in between and without the camera being inside something. Shots that follow a
    /// robot keep a rigid offset and only change angle (smoothly) if something gets in front of the lens. A shot is
    /// started only when its action is really happening (a product at the scan point, the arm working, the robot
    /// moving) and stops when the action stops. Between shots the camera blends smoothly, or cuts when the straight
    /// path would cross a wall. Opening: wide, products coming out of the machine, scan, arm, top view of the line.
    /// Then events: Muse decision (R1), label, truck loading, carton closed (trays + R3), urgent order, final wide.
    /// F7 = SHOT LAB: goes through every shot type once and saves a picture of each one in logs/shots (to check the
    /// framing). During a run, one picture per shot is saved in logs/director_*_shots. Every shot is written to
    /// logs/director_*.log with its time, to find the clips when editing.
    /// </summary>
    [DefaultExecutionOrder(30000)]
    public sealed class CinematicDirector : MonoBehaviour
    {
        /// <summary>Director on: survives the scene reloads of the scenarios (off at each new launch).</summary>
        public static bool Enabled { get; private set; }

        public float Blend = 1.6f;          // s, blend between two shots
        public float MinShot = 4f;          // s before an event may interrupt a shot
        public float EventLife = 8f;        // s an event waits in the queue before it is dropped
        public bool CaptureRunShots = true; // one picture per shot during a run

        // ------------------------------------------------------------------ bootstrap

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLaunch() { Enabled = false; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            Ensure();
        }

        private static void OnSceneLoaded(Scene s, LoadSceneMode m) { Ensure(); }

        private static void Ensure()
        {
            if (FindFirstObjectByType<CinematicDirector>() != null) return;
            new GameObject("Cinematic Director").AddComponent<CinematicDirector>();
        }

        // ------------------------------------------------------------------ shots

        private sealed class Shot
        {
            public string Name, Why = "";
            public Func<Vector3> Look;          // point looked at (world)
            public Transform Subject;           // followed object (ignored by the obstacle checks)
            public Vector3 Dir = Vector3.back;  // preferred direction from the look point towards the camera (horizontal)
            public float Dist = 3f, Height = 1.5f;
            public float Fov = 45f;
            public float Duration = 7f;
            public int Priority;
            public float Damping = 3f;          // follow smoothing of the look point
            public bool Moving;                 // the look point moves: the angle is re-checked while filming
            public Func<bool> Ready;            // the action is happening (null = always)
            // result of the placement
            public Vector3 Offset;
            public float Cost;
            public int Blocked;
        }

        private sealed class Pending
        {
            public Func<Shot> Make;
            public int Priority;
            public float Since;
            public string Kind;
        }

        private sealed class MoveInfo { public Vector3 Last; public float Speed; public bool Init; }

        private Camera cam;
        private readonly List<Behaviour> paused = new List<Behaviour>();
        private Vector3 savedPos;
        private Quaternion savedRot;
        private float savedFov;
        private bool controlling;

        private Shot shot;
        private float shotTime, blendT = 1f, recheck, idleT, tick, scriptWait;
        private Vector3 fromPos, look, offsetNow;
        private Quaternion fromRot;
        private float fromFov, ceiling;
        private bool capturedThisShot;
        private readonly List<Pending> queue = new List<Pending>();
        private readonly Queue<Func<Shot>> script = new Queue<Func<Shot>>();
        private readonly Dictionary<Transform, MoveInfo> moves = new Dictionary<Transform, MoveInfo>();
        private int filler, shotIndex;
        private bool introQueued, outroQueued, readyNow = true;

        // watched counters
        private int seenDecisions, seenLabels, seenCartons, seenTaken;
        private readonly Dictionary<TruckBay, int> seenLoaded = new Dictionary<TruckBay, int>();
        private MuseOrchestrator.UrgentStage seenStage;
        private bool countersReady;

        private MuseOrchestrator orchCache;
        private StreamWriter log;
        private float logStart;
        private string runShotsDir;
        private bool lab;

        private void Update()
        {
            if (HudInput.KeyDown(KeyCode.F6))
            {
                Enabled = !Enabled;
                Debug.Log("[DIRECTOR] " + (Enabled ? "on: the camera films the run by itself (F6 to give it back)" : "off"));
                if (!Enabled) Release();
            }
            if (HudInput.KeyDown(KeyCode.F7) && !lab) StartCoroutine(ShotLab());
        }

        private void LateUpdate()
        {
            if (!Enabled) { if (controlling) Release(); return; }
            if (!controlling && !TakeControl()) return;
            float dt = Time.deltaTime;
            TrackMoves(dt);
            if (!lab)
            {
                Watch();
                ChooseShot(dt);
            }
            if (shot == null) return;
            ApplyPose(dt);
        }

        private static float Ease(float x) => x * x * x * (x * (6f * x - 15f) + 10f);   // smootherstep

        // ------------------------------------------------------------------ choosing

        private void ChooseShot(float dt)
        {
            shotTime += dt;
            tick -= dt;
            bool doTick = tick <= 0f;
            if (doTick) { tick = .25f; readyNow = shot == null || shot.Ready == null || shot.Ready(); }
            if (shot != null && shot.Ready != null && shotTime > .5f) idleT = readyNow ? 0f : idleT + dt;
            bool over = shot == null || shotTime >= shot.Duration;
            // the action stopped: do not stay on an empty scene
            if (!over && shot.Ready != null && shotTime > MinShot * .7f && idleT > 2.5f) over = true;

            if (script.Count > 0 && over)
            {
                Shot s = script.Peek()();
                if (s == null) script.Dequeue();
                else if (s.Ready == null || s.Ready()) { script.Dequeue(); scriptWait = 0f; Cut(s, "script"); }
                else if ((scriptWait += dt) > 8f) { script.Dequeue(); scriptWait = 0f; Cut(s, "script (action late)"); }
                return;
            }
            if (!doTick && !over) return;

            Pending next = null; Shot nextShot = null;
            queue.RemoveAll(p => Time.time - p.Since > EventLife);
            foreach (Pending p in queue)
            {
                if (next != null && (p.Priority < next.Priority || (p.Priority == next.Priority && p.Since > next.Since))) continue;
                Shot c = p.Make();
                if (c == null) { p.Since = -1000f; continue; }
                if (c.Ready != null && !c.Ready()) continue;
                next = p; nextShot = c;
            }
            queue.RemoveAll(p => p.Since < -500f);
            if (next != null && (over || (shotTime >= MinShot && next.Priority >= shot.Priority)))
            {
                queue.Remove(next);
                Cut(nextShot, next.Kind);
            }
            else if (over) Cut(Filler(), "filler");
        }

        private void Push(string kind, int priority, Func<Shot> make)
        {
            queue.RemoveAll(p => p.Kind == kind);
            queue.Add(new Pending { Kind = kind, Priority = priority, Make = make, Since = Time.time });
        }

        private Shot Filler()
        {
            WarehouseWorld world = WarehouseWorld.Instance;
            for (int i = 0; i < 9; i++)
            {
                Shot s;
                switch (filler++ % 9)
                {
                    case 0: s = ArmPick(7f); break;
                    case 1: s = Follow(R1(world), "R1 picking", 8f); break;
                    case 2: s = Scan(6f); break;
                    case 3: s = Follow(R2(world), "R2 loading", 8f); break;
                    case 4: s = R3("R3 transport", 8f); break;
                    case 5: s = Spawn(5f); break;
                    case 6: s = TopView(5f); break;
                    case 7: s = Trays("the cartons fill up", 5f); break;
                    default: s = null; break;
                }
                if (s != null && (s.Ready == null || s.Ready())) return s;
            }
            return Wide("overview", 8f);
        }

        private static Transform R1(WarehouseWorld w) => w != null && w.R1 != null ? w.R1.transform : null;
        private static Transform R2(WarehouseWorld w) => w != null && w.R2 != null ? w.R2.transform : null;

        // ------------------------------------------------------------------ events

        private void Watch()
        {
            if (orchCache == null) orchCache = FindFirstObjectByType<MuseOrchestrator>();
            MuseOrchestrator orch = orchCache;
            PackingCell cell = PackingCell.Instance;
            WarehouseWorld world = WarehouseWorld.Instance;
            ShippingLabels labels = ShippingLabels.Instance;
            ScenarioPlayer player = ScenarioPlayer.Instance;

            int decisions = orch != null ? AcceptedCount(orch) : 0;
            int labelled = labels != null ? labels.BoxesLabelled : 0;
            int cartons = cell != null ? cell.CartonsClosed : 0;
            int taken = cell != null ? cell.Taken : 0;
            MuseOrchestrator.UrgentStage stage = orch != null ? orch.Stage : MuseOrchestrator.UrgentStage.Idle;
            if (!countersReady)
            {
                seenDecisions = decisions; seenLabels = labelled; seenCartons = cartons; seenTaken = taken; seenStage = stage;
                if (world != null) foreach (string id in world.TruckIds) { TruckBay t = world.Truck(id); if (t != null) seenLoaded[t] = t.LoadedCount; }
                countersReady = true;
            }

            // opening, once per run: wide, the machine, the scan, the arm, the line from above
            bool running = player != null && player.State == ScenarioPlayer.PlayerState.Running;
            if (!introQueued && (running || player == null || player.State == ScenarioPlayer.PlayerState.Idle))
            {
                introQueued = true;
                script.Enqueue(() => Wide("establishing shot", 9f));
                script.Enqueue(() => Spawn(5.5f));
                script.Enqueue(() => Scan(6.5f));
                script.Enqueue(() => ArmPick(8f));
                script.Enqueue(() => TopView(5.5f));
            }
            if (!outroQueued && player != null && player.State == ScenarioPlayer.PlayerState.Complete)
            {
                outroQueued = true;
                Push("end", 9, () => Wide("scenario complete", 14f));
            }

            if (stage != seenStage)
            {
                if ((stage == MuseOrchestrator.UrgentStage.Planning || stage == MuseOrchestrator.UrgentStage.Suspending) &&
                    seenStage == MuseOrchestrator.UrgentStage.Idle)
                {
                    Push("urgent", 8, () => Wide("urgent order: safe pause of the orders", 7f));
                    Push("urgent R1", 7, () => Follow(R1(world), "R1 on the urgent order", 9f));
                }
                else if (stage == MuseOrchestrator.UrgentStage.UrgentActive)
                    Push("urgent run", 7, () => Follow(R1(world), "urgent order executing", 9f));
                else if (stage == MuseOrchestrator.UrgentStage.Resuming)
                    Push("resume", 6, () => Wide("orders resumed", 7f));
                seenStage = stage;
            }
            if (decisions > seenDecisions)
            {
                seenDecisions = decisions;
                Push("decision", 5, () => Follow(R1(world), "Muse Wonder decision: R1 leaves for the pallet", 8f));
            }
            if (labelled > seenLabels)
            {
                seenLabels = labelled;
                string bench = labels != null && labels.Latest != null ? labels.Latest.Bench : "";
                Push("label", 4, () => Bench(world, bench, "shipping label on the box", 6f));
            }
            if (world != null)
                foreach (string id in world.TruckIds)
                {
                    TruckBay t = world.Truck(id);
                    if (t == null) continue;
                    seenLoaded.TryGetValue(t, out int was);
                    if (t.LoadedCount > was) { TruckBay bay = t; Push("truck", 4, () => Truck(bay, "box loaded into " + id + " (SAM 3.1 checks it)", 6.5f)); }
                    seenLoaded[t] = t.LoadedCount;
                }
            if (cartons > seenCartons)
            {
                seenCartons = cartons;
                Push("carton", 3, () => Trays("carton closed", 5f));
                Push("R3", 3, () => R3("R3 brings the carton to the pallets", 8f));
            }
            seenTaken = taken;
        }

        private static int AcceptedCount(MuseOrchestrator orch)
        {
            int n = 0;
            IReadOnlyList<RoboFactory.Hackathon.Orchestration.MuseDecisionRecord> h = orch.History;
            if (h != null) for (int i = 0; i < h.Count; i++) if (h[i] != null && h[i].Accepted && h[i].OrderId > 0) n++;
            return n;
        }

        // ------------------------------------------------------------------ what moves

        private void TrackMoves(float dt)
        {
            if (dt <= 0f) return;
            WarehouseWorld w = WarehouseWorld.Instance;
            Track(R1(w), dt);
            Track(R2(w), dt);
            PackingTransport r3 = PackingTransport.Current;
            Track(r3 != null ? r3.Robot : null, dt);
        }

        private void Track(Transform t, float dt)
        {
            if (t == null) return;
            if (!moves.TryGetValue(t, out MoveInfo m)) { m = new MoveInfo(); moves[t] = m; }
            if (!m.Init) { m.Last = t.position; m.Init = true; return; }
            float v = (t.position - m.Last).magnitude / dt;
            m.Speed = Mathf.Lerp(m.Speed, Mathf.Min(v, 6f), 1f - Mathf.Exp(-3f * dt));
            m.Last = t.position;
        }

        private bool IsMoving(Transform t) { return t != null && moves.TryGetValue(t, out MoveInfo m) && m.Speed > .2f; }

        private static bool ProductAtScan(PackingCell cell)
        {
            if (cell == null || cell.Track == null) return false;
            if (cell.ScansPending > 0) return true;
            float s0 = cell.Track.Project(cell.ScanPoint);
            foreach (PackingItem it in FindObjectsByType<PackingItem>(FindObjectsSortMode.None))
            {
                if (it == null) continue;
                float d = Mathf.Abs(cell.Track.Project(it.transform.position) - s0);
                if (d > cell.Track.Length * .5f) d = cell.Track.Length - d;
                if (d < 1f) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ shot library

        private static Vector3 Flat(Vector3 v) { v.y = 0f; return v.sqrMagnitude < 1e-4f ? Vector3.back : v.normalized; }
        private static Vector3 Side(Vector3 f) { return Vector3.Cross(Vector3.up, Flat(f)).normalized; }

        private Shot Wide(string why, float duration)
        {
            Vector3 centre = WorldCentre(out float radius);
            Vector3 dir = cam != null ? Flat(-cam.transform.forward) : Vector3.back;
            return new Shot
            {
                Name = "WIDE", Why = why, Look = () => centre, Dir = dir,
                Dist = Mathf.Clamp(radius * .8f, 8f, 14f), Height = Mathf.Clamp(radius * .55f, 5f, 8f),
                Fov = 52f, Duration = duration, Priority = 1
            };
        }

        /// <summary>Products coming out of the machine at the start of the belt.</summary>
        private Shot Spawn(float duration)
        {
            PackingCell cell = PackingCell.Instance;
            if (cell == null || cell.Track == null) return null;
            // the spawn point is inside the machine: look at the belt just after its exit, from downstream
            Vector3 p = cell.Track.Evaluate(cell.Track.Project(cell.SpawnPoint) + 1.1f, out Vector3 f);
            Vector3 dir = Flat(f) + Side(f) * .7f;
            return new Shot
            {
                Name = "MACHINE", Why = "products come out of the machine", Look = () => p + Vector3.up * .45f, Dir = dir,
                Dist = 3.0f, Height = 1.3f, Fov = 46f, Duration = duration, Priority = 2,
                Ready = () => PackingCell.Instance != null && PackingCell.Instance.Spawned > 0
            };
        }

        private Shot Scan(float duration)
        {
            PackingCell cell = PackingCell.Instance;
            if (cell == null || cell.Track == null) return null;
            Vector3 p = cell.Track.Evaluate(cell.Track.Project(cell.ScanPoint), out Vector3 f);
            Vector3 dir = Side(f) - Flat(f) * .5f;
            return new Shot
            {
                Name = "SCAN", Why = "Muse Spark inspects the product", Look = () => p + Vector3.up * .15f, Dir = dir,
                Dist = 1.25f, Height = .6f, Fov = 38f, Duration = duration, Priority = 2,
                Ready = () => ProductAtScan(PackingCell.Instance)
            };
        }

        private Shot ArmPick(float duration)
        {
            PackingCell cell = PackingCell.Instance;
            if (cell == null || cell.Arm == null || cell.TraySlots == null || cell.TraySlots.Length == 0) return null;
            Vector3 trays = TrayCentre(cell);
            Vector3 pick = cell.PickPoint;
            Vector3 mid = Vector3.Lerp(pick, trays, .45f) + Vector3.up * .55f;
            Vector3 along = Flat(pick - trays);
            return new Shot
            {
                Name = "ARM", Why = "the arm packs on the moving belt", Look = () => mid, Dir = along + Side(along) * .7f,
                Dist = 3.4f, Height = 1.8f, Fov = 46f, Duration = duration, Priority = 2,
                Ready = () => PackingCell.Instance != null && PackingCell.Instance.ArmBusy
            };
        }

        /// <summary>Small view from above of the whole packing line.</summary>
        private Shot TopView(float duration)
        {
            PackingCell cell = PackingCell.Instance;
            if (cell == null || cell.TraySlots == null || cell.TraySlots.Length == 0) return null;
            Vector3 trays = TrayCentre(cell);
            Vector3 c = (cell.SpawnPoint + cell.PickPoint + trays) / 3f;
            if (cell.Corners != null && cell.Corners.Length > 2)
            {
                c = Vector3.zero;
                foreach (Vector3 k in cell.Corners) c += k;
                c /= cell.Corners.Length;
            }
            c.y = 0f;
            Vector3 along = Flat(cell.PickPoint - cell.SpawnPoint);
            return new Shot
            {
                Name = "TOP", Why = "the packing line seen from above", Look = () => c, Dir = along * .3f + Side(along) * .1f + Vector3.back * .05f,
                Dist = .6f, Height = 8.4f, Fov = 62f, Duration = duration, Priority = 1
            };
        }

        private Shot Trays(string why, float duration)
        {
            PackingCell cell = PackingCell.Instance;
            if (cell == null || cell.TraySlots == null || cell.TraySlots.Length == 0) return null;
            Vector3 trays = TrayCentre(cell);
            Vector3 d = Flat(trays - cell.PickPoint);   // from the R3 side of the trays
            return new Shot
            {
                Name = "TRAYS", Why = why, Look = () => trays + Vector3.up * .45f, Dir = d + Side(d) * .5f,
                Dist = 2.8f, Height = 1.5f, Fov = 46f, Duration = duration, Priority = 3
            };
        }

        private Shot R3(string why, float duration)
        {
            PackingTransport r3 = PackingTransport.Current;
            if (r3 == null || r3.Robot == null) return null;
            Shot s = Follow(r3.Robot, why, duration);
            s.Name = "R3";
            s.Dist = 3.0f; s.Height = 1.9f;
            return s;
        }

        private Shot Follow(Transform t, string why, float duration)
        {
            if (t == null) return null;
            Vector3 dir = -Flat(t.forward) * .6f + Side(t.forward) * .8f;   // behind and to the side, fixed for the shot
            return new Shot
            {
                Name = "FOLLOW " + t.name, Why = why, Subject = t, Moving = true,
                Look = () => t.position + Vector3.up * .6f, Dir = dir,
                Dist = 3.8f, Height = 3.5f, Fov = 48f, Duration = duration, Priority = 2, Damping = 2.5f,
                Ready = () => IsMoving(t)
            };
        }

        private Shot Bench(WarehouseWorld world, string benchId, string why, float duration)
        {
            if (world == null) return null;
            ShelfStorage bench = world.Bench(benchId);
            if (bench == null) foreach (string id in world.BenchIds) { bench = world.Bench(id); if (bench != null) break; }
            if (bench == null) return null;
            Vector3 p = bench.transform.position;
            Vector3 c = WorldCentre(out float _);
            Vector3 d = Flat(c - p);   // from the middle of the room, looking at the bench
            return new Shot
            {
                Name = "BENCH " + benchId, Why = why, Look = () => p + Vector3.up * .8f, Dir = d + Side(d) * .4f,
                Dist = 2.8f, Height = 1.4f, Fov = 44f, Duration = duration, Priority = 3
            };
        }

        /// <summary>Box going into a truck: R2 seen from behind as it drives into the trailer.</summary>
        private Shot Truck(TruckBay bay, string why, float duration)
        {
            Transform r2 = R2(WarehouseWorld.Instance);
            if (r2 == null) return null;
            Vector3 dir = -Flat(r2.forward) + Side(r2.forward) * .25f;
            return new Shot
            {
                Name = "TRUCK " + (bay != null ? bay.name : ""), Why = why, Subject = r2, Moving = true,
                Look = () => r2.position + Vector3.up * .6f, Dir = dir,
                Dist = 4.2f, Height = 2.6f, Fov = 50f, Duration = duration, Priority = 3, Damping = 2.5f
            };
        }

        private static Vector3 TrayCentre(PackingCell cell)
        {
            Vector3 c = Vector3.zero;
            foreach (Vector3 t in cell.TraySlots) c += t;
            return c / cell.TraySlots.Length;
        }

        private static Vector3 WorldCentre(out float radius)
        {
            radius = 10f;
            WarehouseWorld world = WarehouseWorld.Instance;
            bool any = false;
            var b = new Bounds();
            void Add(Vector3 p) { if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p); }
            if (world != null)
            {
                foreach (string id in world.PalletIds) { ShelfStorage s = world.Pallet(id); if (s != null) Add(s.transform.position); }
                foreach (string id in world.BenchIds) { ShelfStorage s = world.Bench(id); if (s != null) Add(s.transform.position); }
                foreach (string id in world.TruckIds) { TruckBay t = world.Truck(id); if (t != null) Add(t.transform.position); }
            }
            PackingCell cell = PackingCell.Instance;
            if (cell != null) Add(cell.PickPoint);
            if (!any) return Vector3.zero;
            radius = new Vector2(b.extents.x, b.extents.z).magnitude;
            return new Vector3(b.center.x, 0f, b.center.z);
        }

        // ------------------------------------------------------------------ placing the camera

        /// <summary>Big static thing (wall, shelf, machine, another robot) that hides the view; small or moving things do not count.</summary>
        private static bool IsObstacle(Collider c, Transform subject)
        {
            if (c == null || c.isTrigger) return false;
            Transform t = c.transform;
            if (subject != null && (t == subject || t.IsChildOf(subject) || subject.IsChildOf(t))) return false;
            Rigidbody rb = c.attachedRigidbody;
            if (rb != null && !rb.isKinematic) return false;
            return c.bounds.size.magnitude >= 1f;
        }

        /// <summary>True when something big is between a and b (the last endMargin metres before b do not count: the table or belt under the subject).</summary>
        private static bool Hidden(Vector3 a, Vector3 b, Transform subject, float endMargin)
        {
            Vector3 d = b - a;
            float len = d.magnitude;
            if (len < .05f) return false;
            RaycastHit[] hits = Physics.RaycastAll(a, d / len, len, ~0, QueryTriggerInteraction.Ignore);
            foreach (RaycastHit h in hits)
                if (h.distance > .01f && h.distance < len - endMargin && IsObstacle(h.collider, subject)) return true;
            return false;
        }

        private static bool Inside(Vector3 p, Transform subject)
        {
            foreach (Collider c in Physics.OverlapSphere(p, .3f, ~0, QueryTriggerInteraction.Ignore))
                if (IsObstacle(c, subject)) return true;
            return false;
        }

        /// <summary>Best camera offset for the shot: close to the wanted direction, clear view of the subject, not inside anything.</summary>
        private void Solve(Shot s, Vector3 lookAt, Vector3? keepNear)
        {
            float baseYaw = Mathf.Atan2(-s.Dir.x, -s.Dir.z) * Mathf.Rad2Deg;
            float[] yawD = { 0f, 25f, -25f, 50f, -50f, 80f, -80f, 110f, -110f, 140f, -140f, 180f };
            float[] hM = { 1f, 1.5f, .7f, 2f };
            float[] dM = { 1f, .75f, 1.3f, .55f };
            float bestCost = float.MaxValue; Vector3 best = Vector3.back * s.Dist + Vector3.up * s.Height; int bestBlocked = 9;
            float roof = Ceiling(lookAt) - .5f;
            Vector3[] aim = { lookAt, lookAt + Vector3.up * .5f, lookAt + Vector3.down * .1f };
            for (int hi = 0; hi < hM.Length; hi++)
                for (int di = 0; di < dM.Length; di++)
                    for (int yi = 0; yi < yawD.Length; yi++)
                    {
                        float cost = Mathf.Abs(yawD[yi]) * .02f + Mathf.Abs(hM[hi] - 1f) * 1.2f + Mathf.Abs(dM[di] - 1f) * 1.2f;
                        if (cost >= bestCost) continue;
                        Vector3 off = Quaternion.Euler(0f, baseYaw + yawD[yi], 0f) * Vector3.back * (s.Dist * dM[di]) + Vector3.up * (s.Height * hM[hi]);
                        Vector3 pos = lookAt + off;
                        if (pos.y > roof) pos.y = roof;
                        if (pos.y < .4f) pos.y = .4f;
                        off = pos - lookAt;
                        if (keepNear.HasValue) cost += Vector3.Angle(Flat(off), Flat(keepNear.Value)) * .012f;
                        if (Inside(pos, s.Subject)) cost += 60f;
                        if (cost >= bestCost) continue;
                        int blocked = 0;
                        for (int k = 0; k < aim.Length; k++) if (Hidden(pos, aim[k], s.Subject, .5f)) blocked++;
                        cost += blocked * 25f;
                        if (cost < bestCost) { bestCost = cost; best = off; bestBlocked = blocked; }
                    }
            s.Offset = best; s.Cost = bestCost; s.Blocked = bestBlocked;
        }

        private static float Ceiling(Vector3 at)
        {
            if (Physics.Raycast(at + Vector3.up * 1.5f, Vector3.up, out RaycastHit hit, 40f, ~0, QueryTriggerInteraction.Ignore) &&
                hit.point.y > at.y + 3.5f)
                return Mathf.Min(hit.point.y, at.y + 12f);
            return at.y + 9f;
        }

        private void Cut(Shot s, string kind)
        {
            if (s == null) return;
            fromPos = cam.transform.position;
            fromRot = cam.transform.rotation;
            fromFov = cam.fieldOfView;
            shot = s;
            shotTime = 0f; idleT = 0f; recheck = 0f; capturedThisShot = false; readyNow = true; tick = .25f;
            look = s.Look();
            Solve(s, look, null);
            offsetNow = s.Offset;
            Vector3 target = look + offsetNow;
            // blend, unless the straight path would cross a wall or is very long: then a clean cut
            bool crosses = Hidden(fromPos, target, null, 0f) || (fromPos - target).magnitude > 16f;
            blendT = crosses ? 1f : 0f;
            shotIndex++;
            Log(s.Name + (s.Why.Length > 0 ? " — " + s.Why : "") + "  [" + kind + (crosses ? ", cut" : ", blend") + "]  " +
                "cam " + V(target) + " look " + V(look) + " cost " + s.Cost.ToString("F1", CultureInfo.InvariantCulture) + (s.Blocked > 0 ? " HIDDEN" : ""));
        }

        private void ApplyPose(float dt)
        {
            Vector3 want = shot.Look();
            look = shotTime < dt * 1.5f ? want : Vector3.Lerp(look, want, 1f - Mathf.Exp(-shot.Damping * dt));
            if (shot.Moving)
            {
                // fixed offset; only changes angle (smoothly) when something gets in front of the lens
                recheck -= dt;
                if (recheck <= 0f)
                {
                    recheck = .5f;
                    if (Hidden(look + offsetNow, look + Vector3.up * .2f, shot.Subject, .5f) || Inside(look + offsetNow, shot.Subject))
                    {
                        ceiling = Ceiling(look);
                        Shot probe = new Shot { Dir = shot.Dir, Dist = shot.Dist, Height = shot.Height, Subject = shot.Subject };
                        Solve(probe, look, offsetNow);
                        shot.Offset = probe.Offset; shot.Cost = probe.Cost; shot.Blocked = probe.Blocked;
                    }
                }
                offsetNow = Vector3.Lerp(offsetNow, shot.Offset, 1f - Mathf.Exp(-2.5f * dt));
            }
            else offsetNow = shot.Offset;

            Vector3 pos = look + offsetNow;
            if (pos.y < .4f) pos.y = .4f;
            Quaternion rot = Quaternion.LookRotation(look - pos, Vector3.up);
            float fov = shot.Fov;
            if (blendT < 1f)
            {
                blendT = Mathf.Clamp01(blendT + dt / Blend);
                float b = Ease(blendT);
                pos = Vector3.Lerp(fromPos, pos, b);
                rot = Quaternion.Slerp(fromRot, rot, b);
                fov = Mathf.Lerp(fromFov, fov, b);
            }
            cam.transform.SetPositionAndRotation(pos, rot);
            cam.fieldOfView = fov;

            if (CaptureRunShots && !lab && !capturedThisShot && runShotsDir != null && shotTime >= Mathf.Min(shot.Duration * .55f, 4f) && blendT >= 1f)
            {
                capturedThisShot = true;
                try { ScreenCapture.CaptureScreenshot(Path.Combine(runShotsDir, shotIndex.ToString("00") + "_" + Safe(shot.Name) + ".png")); }
                catch (Exception) { }
            }
        }

        // ------------------------------------------------------------------ shot lab (F7)

        /// <summary>Goes through every shot type once, with its placement, and saves a picture of each (logs/shots/...).</summary>
        private IEnumerator ShotLab()
        {
            lab = true;
            if (!Enabled) { Enabled = true; }
            while (!controlling) yield return null;
            string dir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "logs", "shots", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(dir);
            var report = new StringBuilder();
            WarehouseWorld world = WarehouseWorld.Instance;
            string bench = ""; if (world != null) foreach (string id in world.BenchIds) { bench = id; break; }
            TruckBay truck = null; if (world != null) foreach (string id in world.TruckIds) { truck = world.Truck(id); if (truck != null) break; }
            var list = new List<Func<Shot>>
            {
                () => Wide("lab", 5f), () => Spawn(5f), () => Scan(5f), () => ArmPick(5f), () => TopView(5f), () => Trays("lab", 5f),
                () => R3("lab", 5f), () => Follow(R1(world), "lab", 5f), () => Follow(R2(world), "lab", 5f),
                () => Bench(world, bench, "lab", 5f), () => Truck(truck, "lab", 5f),
            };
            int n = 0;
            foreach (Func<Shot> make in list)
            {
                Shot s = make();
                n++;
                if (s == null) { report.AppendLine(n.ToString("00") + "  (not available in this scene)"); continue; }
                bool ready = s.Ready == null || s.Ready();
                Cut(s, "lab");
                blendT = 1f;
                for (int i = 0; i < 4; i++) { shotTime += Time.deltaTime; yield return null; }
                yield return new WaitForEndOfFrame();
                string file = n.ToString("00") + "_" + Safe(s.Name) + ".png";
                SavePicture(Path.Combine(dir, file));
                report.AppendLine(n.ToString("00") + "  " + s.Name + "  cam " + V(cam.transform.position) + "  look " + V(look) + "  fov " + s.Fov.ToString("F0", CultureInfo.InvariantCulture) +
                                  "  cost " + s.Cost.ToString("F1", CultureInfo.InvariantCulture) + (s.Blocked > 0 ? "  HIDDEN(" + s.Blocked + "/3)" : "  clear") + (ready ? "" : "  (action not happening now)"));
            }
            File.WriteAllText(Path.Combine(dir, "lab.txt"), report.ToString());
            Debug.Log("[DIRECTOR] shot lab done: " + dir + "\n" + report);
            lab = false;
            shot = null;
        }

        private void SavePicture(string path)
        {
            try
            {
                const int W = 1920, H = 1080;
                RenderTexture rt = RenderTexture.GetTemporary(W, H, 24);
                RenderTexture prevTarget = cam.targetTexture, prevActive = RenderTexture.active;
                cam.targetTexture = rt;
                cam.Render();
                cam.targetTexture = prevTarget;
                RenderTexture.active = rt;
                var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                tex.Apply();
                File.WriteAllBytes(path, tex.EncodeToPNG());
                Destroy(tex);
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
            }
            catch (Exception e) { Debug.LogWarning("[DIRECTOR] picture not saved: " + e.Message); }
        }

        private static string Safe(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }

        private static string V(Vector3 v)
        {
            return "(" + v.x.ToString("F1", CultureInfo.InvariantCulture) + "," + v.y.ToString("F1", CultureInfo.InvariantCulture) + "," + v.z.ToString("F1", CultureInfo.InvariantCulture) + ")";
        }

        // ------------------------------------------------------------------ camera

        private bool TakeControl()
        {
            cam = Camera.main;
            if (cam == null) return false;
            savedPos = cam.transform.position;
            savedRot = cam.transform.rotation;
            savedFov = cam.fieldOfView;
            paused.Clear();
            foreach (WarehouseCameraController c in FindObjectsByType<WarehouseCameraController>(FindObjectsSortMode.None))
                if (c.enabled && (c.targetCamera == cam || c.transform == cam.transform || cam.transform.IsChildOf(c.transform)))
                {
                    c.enabled = false;
                    paused.Add(c);
                }
            controlling = true;
            shot = null;
            queue.Clear();
            script.Clear();
            moves.Clear();
            introQueued = outroQueued = false;
            countersReady = false;
            shotIndex = 0;
            OpenLog();
            return true;
        }

        private void Release()
        {
            if (!controlling) return;
            if (cam != null)
            {
                cam.transform.SetPositionAndRotation(savedPos, savedRot);
                cam.fieldOfView = savedFov;
            }
            foreach (Behaviour b in paused) if (b != null) b.enabled = true;
            paused.Clear();
            controlling = false;
            shot = null;
            CloseLog();
        }

        private void OnDisable() { Release(); }

        // ------------------------------------------------------------------ chapter log

        private void OpenLog()
        {
            CloseLog();
            try
            {
                string root = Directory.GetParent(Application.dataPath).FullName;
                string dir = Path.Combine(root, "logs");
                Directory.CreateDirectory(dir);
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                log = new StreamWriter(Path.Combine(dir, "director_" + stamp + ".log")) { AutoFlush = true };
                log.WriteLine("# RoboFactory — DIRECTOR camera, one line per shot: real time | time since the director started | scenario time | shot");
                runShotsDir = Path.Combine(dir, "director_" + stamp + "_shots");
                if (CaptureRunShots) Directory.CreateDirectory(runShotsDir);
            }
            catch (Exception e) { log = null; runShotsDir = null; Debug.LogWarning("[DIRECTOR] no chapter log: " + e.Message); }
            logStart = Time.realtimeSinceStartup;
        }

        private void Log(string line)
        {
            float t = Time.realtimeSinceStartup - logStart;
            string scenario = RoboFactory.MetaServer.RecordedDemoRuntime.Kind != RoboFactory.MetaServer.DemoRunKind.Off
                ? (RoboFactory.MetaServer.RecordedDemoRuntime.Elapsed).ToString("F1", CultureInfo.InvariantCulture) + " s" : "-";
            string text = DateTime.Now.ToString("HH:mm:ss.f") + "  " + ((int)(t / 60f)).ToString("00") + ":" + (t % 60f).ToString("00.0", CultureInfo.InvariantCulture) +
                          "  scenario " + scenario + "  " + line;
            Debug.Log("[DIRECTOR] " + text);
            try { log?.WriteLine(text); } catch (Exception) { }
        }

        private void CloseLog()
        {
            try { log?.Dispose(); } catch (Exception) { }
            log = null;
        }
    }
}
