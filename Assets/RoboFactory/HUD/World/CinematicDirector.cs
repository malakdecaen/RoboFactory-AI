using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using RoboFactory.Launcher;
using RoboFactory.Logistics;
using RoboFactory.Packing;
using UnityEngine;
using UnityEngine.SceneManagement;
using RoboFactory.Sim;

namespace RoboFactory.HUD
{
    /// <summary>
    /// DIRECTOR camera (F6): films the run by itself for the demo video. It opens on a wide establishing shot, goes
    /// to the packing line (scan, arm, cartons), then cuts to whatever the warehouse is doing, as it happens: a Muse
    /// decision (R1 leaving for its pallet), a label put on a box at the bench, a box loaded into a truck, a carton
    /// closed and carried by R3, the urgent order (wide shot then R1), and a final wide shot when the scenario is
    /// complete. Between events it cycles through the robots and the packing cell. Every cut is a smooth blend; each
    /// shot moves slowly (orbit, push-in) and follows its subject with damping, never above the ceiling and pulled in
    /// front of walls. The HUD is left as it is (F2 / F3 / H as usual). Every shot is written to logs/director_*.log
    /// with its time, to find the clips when editing. The scene's camera script is only paused, and the camera goes
    /// back to its original view when F6 is pressed again.
    /// </summary>
    [DefaultExecutionOrder(30000)]
    public sealed class CinematicDirector : MonoBehaviour
    {
        /// <summary>Director on: survives the scene reloads of the scenarios (off at each new launch).</summary>
        public static bool Enabled { get; private set; }

        public float Blend = 1.8f;          // s, cut between two shots
        public float MinShot = 4.5f;        // s before an event may interrupt a shot
        public float EventLife = 8f;        // s an event waits in the queue before it is dropped

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
            public string Name, Why;
            public Func<Vector3> Look;          // point looked at (world)
            public Transform Subject;           // followed object (ignored by the wall check)
            public float Yaw;                   // world yaw of the camera around the look point (deg)
            public float YawSpeed;              // slow orbit (deg/s)
            public float Dist, Height;          // horizontal distance, height above the look point
            public float Push = 1f;             // distance multiplier reached at the end of the shot
            public float Fov = 45f;
            public float Duration = 8f;
            public int Priority;
            public float Damping = 3f;          // follow smoothing of the look point
        }

        private sealed class Pending
        {
            public Func<Shot> Make;
            public int Priority;
            public float Since;
            public string Kind;
        }

        private Camera cam;
        private readonly List<Behaviour> paused = new List<Behaviour>();
        private Vector3 savedPos;
        private Quaternion savedRot;
        private float savedFov;
        private bool controlling;

        private Shot shot;
        private float shotTime, blendT = 1f;
        private Vector3 fromPos, look, distSmoothed = Vector3.zero;
        private Quaternion fromRot;
        private float fromFov, wallDist = -1f, ceiling;
        private readonly List<Pending> queue = new List<Pending>();
        private readonly Queue<Func<Shot>> script = new Queue<Func<Shot>>();
        private int filler;
        private bool introQueued, outroQueued;

        // watched counters
        private int seenDecisions, seenLabels, seenCartons, seenTaken;
        private readonly Dictionary<TruckBay, int> seenLoaded = new Dictionary<TruckBay, int>();
        private MuseOrchestrator.UrgentStage seenStage;
        private bool countersReady;

        private MuseOrchestrator orchCache;
        private StreamWriter log;
        private float logStart;

        private void Update()
        {
            if (HudInput.KeyDown(KeyCode.F6))
            {
                Enabled = !Enabled;
                Debug.Log("[DIRECTOR] " + (Enabled ? "on: the camera films the run by itself (F6 to give it back)" : "off"));
                if (!Enabled) Release();
            }
        }

        private void LateUpdate()
        {
            if (!Enabled) { if (controlling) Release(); return; }
            if (!controlling && !TakeControl()) return;
            float dt = Time.deltaTime;
            Watch();

            // next shot: the scripted opening, then the events, then the filler cycle
            shotTime += dt;
            bool expired = shot == null || shotTime >= shot.Duration;
            Pending next = BestPending();
            if (script.Count > 0 && expired) Cut(script.Dequeue()(), "script");
            else if (next != null && (expired || (shotTime >= MinShot && next.Priority >= shot.Priority)))
            {
                queue.Remove(next);
                Shot s = next.Make();
                if (s != null) Cut(s, next.Kind); else if (expired) Cut(Filler(), "filler");
            }
            else if (expired) Cut(Filler(), "filler");
            if (shot == null) return;

            // pose of the shot
            float k = Mathf.Clamp01(shotTime / Mathf.Max(.1f, shot.Duration));
            Vector3 want = shot.Look();
            look = shotTime < dt * 1.5f ? want : Vector3.Lerp(look, want, 1f - Mathf.Exp(-shot.Damping * dt));
            float yaw = shot.Yaw + shot.YawSpeed * shotTime;
            float dist = shot.Dist * Mathf.Lerp(1f, shot.Push, Ease(k));
            Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * Vector3.back;
            Vector3 offset = dir * dist + Vector3.up * shot.Height;
            // in front of walls and shelves (not the subject itself), never above the ceiling
            float free = FreeDistance(look, offset, shot.Subject);
            wallDist = wallDist < 0f ? free : Mathf.Lerp(wallDist, free, 1f - Mathf.Exp(-4f * dt));
            Vector3 pos = look + offset.normalized * Mathf.Min(offset.magnitude, wallDist);
            pos.y = Mathf.Min(pos.y, ceiling - .5f);
            pos.y = Mathf.Max(pos.y, .35f);
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
        }

        private static float Ease(float x) => x * x * x * (x * (6f * x - 15f) + 10f);   // smootherstep

        private void Cut(Shot s, string kind)
        {
            if (s == null) return;
            fromPos = cam.transform.position;
            fromRot = cam.transform.rotation;
            fromFov = cam.fieldOfView;
            blendT = 0f;
            shot = s;
            shotTime = 0f;
            wallDist = -1f;
            look = s.Look();
            ceiling = Ceiling(look);
            Log(s.Name + (s.Why.Length > 0 ? " — " + s.Why : "") + "  [" + kind + "]");
        }

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

            // opening: wide, scan, arm (the plan of the video), once per run
            bool running = player != null && player.State == ScenarioPlayer.PlayerState.Running;
            if (!introQueued && (running || player == null || player.State == ScenarioPlayer.PlayerState.Idle))
            {
                introQueued = true;
                script.Enqueue(() => Wide("establishing shot", 10f));
                script.Enqueue(() => Scan(7f));
                script.Enqueue(() => ArmPick(9f));
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
                    Push("urgent", 8, () => Wide("urgent order: safe pause of the orders", 7f, 11f));
                    Push("urgent R1", 7, () => Follow(world != null && world.R1 != null ? world.R1.transform : null, "R1 on the urgent order", 10f));
                }
                else if (stage == MuseOrchestrator.UrgentStage.UrgentActive)
                    Push("urgent run", 7, () => Follow(world != null && world.R1 != null ? world.R1.transform : null, "urgent order executing", 9f));
                else if (stage == MuseOrchestrator.UrgentStage.Resuming)
                    Push("resume", 6, () => Wide("orders resumed", 8f));
                seenStage = stage;
            }
            if (decisions > seenDecisions)
            {
                seenDecisions = decisions;
                Push("decision", 5, () => Follow(world != null && world.R1 != null ? world.R1.transform : null, "Muse Wonder decision: R1 leaves for the pallet", 9f));
            }
            if (labelled > seenLabels)
            {
                seenLabels = labelled;
                string bench = labels != null && labels.Latest != null ? labels.Latest.Bench : "";
                Push("label", 4, () => Bench(world, bench, "shipping label on the box", 6.5f));
            }
            if (world != null)
                foreach (string id in world.TruckIds)
                {
                    TruckBay t = world.Truck(id);
                    if (t == null) continue;
                    seenLoaded.TryGetValue(t, out int was);
                    if (t.LoadedCount > was) { TruckBay bay = t; Push("truck", 4, () => Truck(bay, "box loaded into " + id + " (SAM 3.1 checks it)", 7f)); }
                    seenLoaded[t] = t.LoadedCount;
                }
            if (cartons > seenCartons)
            {
                seenCartons = cartons;
                Push("carton", 3, () => Trays("carton closed", 5f));
                Push("R3", 3, () => R3("R3 brings the carton to the pallets", 9f));
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

        private void Push(string kind, int priority, Func<Shot> make)
        {
            queue.RemoveAll(p => p.Kind == kind);
            queue.Add(new Pending { Kind = kind, Priority = priority, Make = make, Since = Time.time });
        }

        private Pending BestPending()
        {
            queue.RemoveAll(p => Time.time - p.Since > EventLife);
            Pending best = null;
            foreach (Pending p in queue) if (best == null || p.Priority > best.Priority || (p.Priority == best.Priority && p.Since < best.Since)) best = p;
            return best;
        }

        private Shot Filler()
        {
            WarehouseWorld world = WarehouseWorld.Instance;
            for (int i = 0; i < 6; i++)
            {
                Shot s;
                switch (filler++ % 6)
                {
                    case 0: s = ArmPick(9f); break;
                    case 1: s = Follow(world != null && world.R1 != null ? world.R1.transform : null, "R1 picking", 9f); break;
                    case 2: s = Scan(6f); break;
                    case 3: s = Follow(world != null && world.R2 != null ? world.R2.transform : null, "R2 loading", 9f); break;
                    case 4: s = R3("R3 transport", 8f); break;
                    default: s = Wide("overview", 9f); break;
                }
                if (s != null) return s;
            }
            return Wide("overview", 9f);
        }

        // ------------------------------------------------------------------ shot library

        private Shot Wide(string why, float duration, float height = 0f)
        {
            Vector3 centre = WorldCentre(out float radius);
            float yaw0 = cam != null ? cam.transform.eulerAngles.y : 0f;
            return new Shot
            {
                Name = "WIDE", Why = why, Look = () => centre, Yaw = yaw0, YawSpeed = 3.5f,
                Dist = Mathf.Clamp(radius * .85f, 8f, 15f), Height = height > 0f ? height : Mathf.Clamp(radius * .55f, 5f, 8f),
                Push = .9f, Fov = 50f, Duration = duration, Priority = 1, Damping = 2f
            };
        }

        private Shot Scan(float duration)
        {
            PackingCell cell = PackingCell.Instance;
            if (cell == null || cell.Track == null) return null;
            Vector3 p = cell.Track.Evaluate(cell.Track.Project(cell.ScanPoint), out Vector3 f);
            float yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg - 70f;   // side and a little ahead of the product
            return new Shot
            {
                Name = "SCAN", Why = "Muse Spark inspects the product", Look = () => p + Vector3.up * .15f, Yaw = yaw, YawSpeed = 2f,
                Dist = 1.7f, Height = .8f, Push = .8f, Fov = 40f, Duration = duration, Priority = 2
            };
        }

        private Shot ArmPick(float duration)
        {
            PackingCell cell = PackingCell.Instance;
            if (cell == null || cell.Arm == null || cell.TraySlots == null || cell.TraySlots.Length == 0) return null;
            Vector3 trays = TrayCentre(cell);
            Vector3 pick = cell.PickPoint;
            Vector3 d = trays - pick; d.y = 0f;
            float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg + 35f;
            Transform grip = cell.Arm.Grip;
            return new Shot
            {
                Name = "ARM", Why = "the arm packs on the moving belt",
                Look = () => Vector3.Lerp(Vector3.Lerp(pick, trays, .4f) + Vector3.up * .6f, grip != null ? grip.position : pick, .35f),
                Yaw = yaw, YawSpeed = -2.5f, Dist = 3.6f, Height = 1.7f, Push = .85f, Fov = 45f, Duration = duration, Priority = 2, Damping = 1.5f
            };
        }

        private Shot Trays(string why, float duration)
        {
            PackingCell cell = PackingCell.Instance;
            if (cell == null || cell.TraySlots == null || cell.TraySlots.Length == 0) return null;
            Vector3 trays = TrayCentre(cell);
            Vector3 d = trays - cell.PickPoint; d.y = 0f;
            float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg + 180f + 25f;   // from the R3 side of the trays
            return new Shot
            {
                Name = "TRAYS", Why = why, Look = () => trays + Vector3.up * .6f, Yaw = yaw, YawSpeed = 2f,
                Dist = 2.8f, Height = 1.4f, Push = .85f, Fov = 45f, Duration = duration, Priority = 3
            };
        }

        private Shot R3(string why, float duration)
        {
            PackingTransport r3 = PackingTransport.Current;
            if (r3 == null || r3.Robot == null) return null;
            Shot s = Follow(r3.Robot, why, duration);
            s.Name = "R3";
            s.Dist = 3f; s.Height = 1.6f;
            return s;
        }

        private Shot Follow(Transform t, string why, float duration)
        {
            if (t == null) return null;
            float yaw = t.eulerAngles.y + 145f;   // behind and to the side of the robot, fixed for the shot
            return new Shot
            {
                Name = "FOLLOW " + t.name, Why = why, Subject = t, Look = () => t.position + Vector3.up * .6f, Yaw = yaw, YawSpeed = 4f,
                Dist = 3.6f, Height = 2f, Push = .9f, Fov = 48f, Duration = duration, Priority = 2, Damping = 2.5f
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
            Vector3 d = p - c; d.y = 0f;
            float yaw = (d.sqrMagnitude > .01f ? Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg : bench.transform.eulerAngles.y) + 200f;
            return new Shot
            {
                Name = "BENCH " + benchId, Why = why, Look = () => p + Vector3.up * .8f, Yaw = yaw, YawSpeed = 2.5f,
                Dist = 2.6f, Height = 1.3f, Push = .8f, Fov = 42f, Duration = duration, Priority = 3
            };
        }

        private Shot Truck(TruckBay bay, string why, float duration)
        {
            if (bay == null) return null;
            Vector3 dock = bay.DockPoint != null ? bay.DockPoint.position : bay.transform.position;
            Vector3 into = bay.transform.position - dock; into.y = 0f;
            if (into.sqrMagnitude < .01f) into = bay.transform.forward;
            float yaw = Mathf.Atan2(into.x, into.z) * Mathf.Rad2Deg + 25f;   // from the dock, looking into the trailer
            return new Shot
            {
                Name = "TRUCK " + bay.name, Why = why, Look = () => dock + into.normalized * 1.2f + Vector3.up * 1f, Yaw = yaw, YawSpeed = 1.5f,
                Dist = 3.4f, Height = 1.3f, Push = .85f, Fov = 46f, Duration = duration, Priority = 3
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
            introQueued = outroQueued = false;
            countersReady = false;
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

        /// <summary>Distance free of obstacles from the look point towards the camera (the subject does not count).</summary>
        private static float FreeDistance(Vector3 from, Vector3 offset, Transform subject)
        {
            float max = offset.magnitude;
            if (max < .01f) return max;
            RaycastHit[] hits = Physics.SphereCastAll(from, .22f, offset / max, max, ~0, QueryTriggerInteraction.Ignore);
            float best = max;
            foreach (RaycastHit h in hits)
            {
                if (h.collider == null || h.distance <= 0f) continue;
                Transform t = h.collider.transform;
                if (subject != null && (t == subject || t.IsChildOf(subject) || subject.IsChildOf(t))) continue;
                // small moving things (boxes, products, robots) do not push the camera: only big static ones
                if (h.collider.attachedRigidbody != null && !h.collider.attachedRigidbody.isKinematic) continue;
                if (h.collider.bounds.size.magnitude < 1.2f) continue;
                best = Mathf.Min(best, Mathf.Max(.8f, h.distance - .25f));
            }
            return best;
        }

        private static float Ceiling(Vector3 at)
        {
            if (Physics.Raycast(at + Vector3.up * 1.5f, Vector3.up, out RaycastHit hit, 40f, ~0, QueryTriggerInteraction.Ignore) &&
                hit.point.y > at.y + 3.5f)
                return Mathf.Min(hit.point.y, at.y + 12f);
            return at.y + 9f;
        }

        // ------------------------------------------------------------------ chapter log

        private void OpenLog()
        {
            CloseLog();
            try
            {
                string dir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "logs");
                Directory.CreateDirectory(dir);
                log = new StreamWriter(Path.Combine(dir, "director_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log")) { AutoFlush = true };
                log.WriteLine("# RoboFactory — DIRECTOR camera, one line per shot: real time | time since the director started | scenario time | shot");
            }
            catch (Exception e) { log = null; Debug.LogWarning("[DIRECTOR] no chapter log: " + e.Message); }
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
