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
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using RoboFactory.Sim;

namespace RoboFactory.HUD
{
    /// <summary>
    /// SHOT MODE (F6) for filming the demo video: six ready-made shots, each one a free orbit camera.
    ///   1 belt + scan, 2 arm filling the cartons, 3 R3 carrying cartons to the stock pallets, 4 R1,
    ///   5 top view of the labelled boxes (label bench), 6 R2 loading the truck.
    /// Left / right arrow (or keys 1-6): previous / next shot. Drag the mouse: orbit around the subject. Wheel: zoom.
    /// Up arrow: back to the shot's starting angle. F6 again: gives the camera back.
    /// A shot starts from the best of ~100 candidate angles (clear view of the subject, nothing in between, not inside
    /// a wall); a shot on a robot follows the robot, keeping the angle you chose. Nothing moves by itself: no automatic
    /// orbit. Each change of shot is written to logs/director_*.log with its time.
    /// F7 = SHOT LAB: saves a picture of the six starting angles in logs/shots (to check the framing).
    /// </summary>
    [DefaultExecutionOrder(30000)]
    public sealed class CinematicDirector : MonoBehaviour
    {
        /// <summary>Shot mode on: survives the scene reloads of the scenarios (off at each new launch).</summary>
        public static bool Enabled { get; private set; }

        public float Blend = 1.2f;   // s, blend between two shots

        private const int ShotCount = 6;
        private static readonly string[] Titles =
        {
            "BELT + SCAN", "ARM FILLING THE CARTONS", "R3 CARRYING TO THE STOCK", "R1", "LABELLED BOXES (TOP VIEW)", "R2 LOADING THE TRUCK"
        };

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

        // ------------------------------------------------------------------ state

        private sealed class Shot
        {
            public string Name, Why = "";
            public Func<Vector3> Look;          // point looked at (world)
            public Transform Subject;           // followed object (ignored by the obstacle checks)
            public Vector3 Dir = Vector3.back;  // preferred direction from the look point towards the camera (horizontal)
            public float Dist = 3f, Height = 1.5f;
            public float Fov = 45f;
            public float Damping = 3f;          // follow smoothing of the look point
            // result of the placement
            public Vector3 Offset;
            public float Cost;
            public int Blocked;
        }

        private Camera cam;
        private readonly List<Behaviour> paused = new List<Behaviour>();
        private Vector3 savedPos;
        private Quaternion savedRot;
        private float savedFov;
        private bool controlling, lab;

        private int index = -1;
        private Shot shot;
        private Vector3 look, fromPos;
        private Quaternion fromRot;
        private float fromFov, blendT = 1f, ceiling;
        private float baseYaw, basePitch, radius;
        private float yawT, pitchT, scaleT = 1f, yawC, pitchC, scaleC = 1f;
        private bool dragging, wasHeld;
        private Vector2 lastMouse;
        private float labelUntil;
        private StreamWriter log;
        private float logStart;
        private GUIStyle labelStyle;

        // ------------------------------------------------------------------ input

        private void Update()
        {
            if (HudInput.KeyDown(KeyCode.F6))
            {
                Enabled = !Enabled;
                Debug.Log("[DIRECTOR] " + (Enabled ? "shot mode on: arrows = shot, drag = orbit, wheel = zoom (F6 to give the camera back)" : "shot mode off"));
                if (!Enabled) Release();
            }
            if (HudInput.KeyDown(KeyCode.F7) && !lab) StartCoroutine(ShotLab());
            if (!Enabled || !controlling || lab) return;

            if (HudInput.KeyDown(KeyCode.RightArrow)) Switch(index + 1, 1);
            else if (HudInput.KeyDown(KeyCode.LeftArrow)) Switch(index - 1, -1);
            for (int i = 0; i < ShotCount; i++)
                if (HudInput.KeyDown(KeyCode.Alpha1 + i)) Switch(i, 1);
            if (HudInput.KeyDown(KeyCode.UpArrow)) { yawT = baseYaw; pitchT = basePitch; scaleT = 1f; }

            // orbit: drag; zoom: wheel
            bool held = HudInput.MouseHeld();
            Vector2 mp = HudInput.MousePosition();
            if (held && !wasHeld) dragging = !OverUi();
            if (!held) dragging = false;
            if (held && dragging)
            {
                Vector2 d = mp - lastMouse;
                yawT -= d.x * .25f * Mathf.Deg2Rad;
                pitchT = Mathf.Clamp(pitchT - d.y * .18f * Mathf.Deg2Rad, 4f * Mathf.Deg2Rad, 88f * Mathf.Deg2Rad);
            }
            lastMouse = mp;
            wasHeld = held;
            float sc = HudInput.ScrollY();
            if (Mathf.Abs(sc) > .01f && !OverUi()) scaleT = Mathf.Clamp(scaleT * (1f - Mathf.Sign(sc) * .08f), .2f, 4f);
        }

        private static bool OverUi()
        {
            return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
        }

        private void LateUpdate()
        {
            if (!Enabled) { if (controlling) Release(); return; }
            if (!controlling)
            {
                if (!TakeControl()) return;
                Switch(0, 1);
            }
            if (shot == null) return;
            ApplyPose(Time.deltaTime);
        }

        private void Switch(int to, int step)
        {
            for (int tries = 0; tries < ShotCount; tries++)
            {
                int i = ((to % ShotCount) + ShotCount) % ShotCount;
                Shot s = Make(i);
                if (s != null) { Cut(s, i); return; }
                Debug.Log("[DIRECTOR] shot " + (i + 1) + " (" + Titles[i] + ") not available in this scene: skipped");
                to = i + step;
            }
        }

        // ------------------------------------------------------------------ the six shots

        private Shot Make(int i)
        {
            WarehouseWorld world = WarehouseWorld.Instance;
            switch (i)
            {
                case 0: return Belt();
                case 1: return ArmPick();
                case 2: { PackingTransport r3 = PackingTransport.Current; Shot s = r3 != null ? Follow(r3.Robot, "R3", 3.4f, 2.2f) : null; return s; }
                case 3: return Follow(R1(world), "R1", 3.8f, 3.5f);
                case 4: return LabelBench(world);
                default: return Truck(world);
            }
        }

        private static Transform R1(WarehouseWorld w) => w != null && w.R1 != null ? w.R1.transform : null;
        private static Transform R2(WarehouseWorld w) => w != null && w.R2 != null ? w.R2.transform : null;

        private static Vector3 Flat(Vector3 v) { v.y = 0f; return v.sqrMagnitude < 1e-4f ? Vector3.back : v.normalized; }
        private static Vector3 Side(Vector3 f) { return Vector3.Cross(Vector3.up, Flat(f)).normalized; }

        /// <summary>The belt with the products, around the scan arch, seen from inside the belt loop.</summary>
        private Shot Belt()
        {
            PackingCell cell = PackingCell.Instance;
            if (cell == null || cell.Track == null) return null;
            Vector3 p = cell.Track.Evaluate(cell.Track.Project(cell.ScanPoint), out Vector3 f);
            Vector3 dir = Flat(LoopCentre(cell) - p) + Flat(f) * .4f;
            return new Shot
            {
                Name = Titles[0], Look = () => p + Vector3.up * .15f, Dir = dir,
                Dist = 2.3f, Height = 1.2f, Fov = 44f
            };
        }

        private Shot ArmPick()
        {
            PackingCell cell = PackingCell.Instance;
            if (cell == null || cell.Arm == null || cell.TraySlots == null || cell.TraySlots.Length == 0) return null;
            Vector3 trays = TrayCentre(cell);
            Vector3 pick = cell.PickPoint;
            Vector3 mid = Vector3.Lerp(pick, trays, .45f) + Vector3.up * .55f;
            Vector3 along = Flat(pick - trays);
            return new Shot
            {
                Name = Titles[1], Look = () => mid, Dir = along + Side(along) * .7f,
                Dist = 3.4f, Height = 1.8f, Fov = 46f, Damping = 1.5f
            };
        }

        private Shot Follow(Transform t, string name, float dist, float height)
        {
            if (t == null) return null;
            Vector3 dir = -Flat(t.forward) * .6f + Side(t.forward) * .8f;   // behind and to the side, the starting angle
            return new Shot
            {
                Name = name == "R3" ? Titles[2] : name == "R1" ? Titles[3] : Titles[5], Subject = t,
                Look = () => t.position + Vector3.up * .6f, Dir = dir,
                Dist = dist, Height = height, Fov = 48f, Damping = 2.5f
            };
        }

        /// <summary>Top view of the label bench: the boxes that got their shipping label.</summary>
        private Shot LabelBench(WarehouseWorld world)
        {
            if (world == null) return null;
            ShippingLabels labels = ShippingLabels.Instance;
            string id = labels != null && labels.Latest != null ? labels.Latest.Bench : "";
            ShelfStorage bench = world.Bench(id);
            if (bench == null) foreach (string b in world.BenchIds) { bench = world.Bench(b); if (bench != null) break; }
            if (bench == null) return null;
            Vector3 p = bench.transform.position;
            Vector3 d = Flat(WorldCentre(out float _) - p);
            return new Shot
            {
                Name = Titles[4], Look = () => p + Vector3.up * .6f, Dir = d,
                Dist = .8f, Height = 5f, Fov = 50f
            };
        }

        /// <summary>R2 carrying the boxes into the trailer, seen from behind.</summary>
        private Shot Truck(WarehouseWorld world)
        {
            Transform r2 = R2(world);
            if (r2 == null) return null;
            Shot s = Follow(r2, "R2", 4.2f, 2.6f);
            s.Dir = -Flat(r2.forward) + Side(r2.forward) * .25f;
            return s;
        }

        private static Vector3 TrayCentre(PackingCell cell)
        {
            Vector3 c = Vector3.zero;
            foreach (Vector3 t in cell.TraySlots) c += t;
            return c / cell.TraySlots.Length;
        }

        private static Vector3 LoopCentre(PackingCell cell)
        {
            Vector3 c = Vector3.zero;
            if (cell.Corners != null && cell.Corners.Length > 2)
            {
                foreach (Vector3 k in cell.Corners) c += k;
                c /= cell.Corners.Length;
            }
            else c = (cell.SpawnPoint + cell.PickPoint) * .5f;
            c.y = 0f;
            return c;
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

        /// <summary>True when something big is between a and b (the last endMargin metres before b do not count).</summary>
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

        /// <summary>Best starting offset: close to the wanted direction, clear view of the subject, not inside anything.</summary>
        private static void Solve(Shot s, Vector3 lookAt)
        {
            float yaw0 = Mathf.Atan2(-s.Dir.x, -s.Dir.z) * Mathf.Rad2Deg;
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
                        Vector3 off = Quaternion.Euler(0f, yaw0 + yawD[yi], 0f) * Vector3.back * (s.Dist * dM[di]) + Vector3.up * (s.Height * hM[hi]);
                        Vector3 pos = lookAt + off;
                        if (pos.y > roof) pos.y = roof;
                        if (pos.y < .4f) pos.y = .4f;
                        off = pos - lookAt;
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

        private void Cut(Shot s, int number)
        {
            fromPos = cam.transform.position;
            fromRot = cam.transform.rotation;
            fromFov = cam.fieldOfView;
            shot = s;
            index = number;
            look = s.Look();
            ceiling = Ceiling(look);
            Solve(s, look);
            Vector3 o = s.Offset;
            baseYaw = Mathf.Atan2(o.x, o.z);
            basePitch = Mathf.Atan2(o.y, new Vector2(o.x, o.z).magnitude);
            radius = o.magnitude;
            yawT = yawC = baseYaw; pitchT = pitchC = basePitch; scaleT = scaleC = 1f;
            Vector3 target = look + o;
            bool crosses = Hidden(fromPos, target, null, 0f) || (fromPos - target).magnitude > 16f;   // clean cut through walls / far away
            blendT = crosses ? 1f : 0f;
            labelUntil = Time.unscaledTime + 2.6f;
            Log((number + 1) + "/" + ShotCount + "  " + s.Name + "  [" + (crosses ? "cut" : "blend") + "]  cam " + V(target) + " look " + V(look) +
                " cost " + s.Cost.ToString("F1", CultureInfo.InvariantCulture) + (s.Blocked > 0 ? " HIDDEN" : ""));
        }

        private void ApplyPose(float dt)
        {
            Vector3 want = shot.Look();
            look = Vector3.Lerp(look, want, 1f - Mathf.Exp(-shot.Damping * dt));
            float k = 1f - Mathf.Exp(-9f * dt);
            yawC = Mathf.Lerp(yawC, yawT, k); pitchC = Mathf.Lerp(pitchC, pitchT, k); scaleC = Mathf.Lerp(scaleC, scaleT, k);
            float r = radius * scaleC;
            Vector3 pos = look + new Vector3(Mathf.Sin(yawC) * Mathf.Cos(pitchC), Mathf.Sin(pitchC), Mathf.Cos(yawC) * Mathf.Cos(pitchC)) * r;
            pos.y = Mathf.Clamp(pos.y, .35f, ceiling - .5f);
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

        private void OnGUI()
        {
            if (!Enabled || !controlling || lab || index < 0 || Time.unscaledTime > labelUntil) return;
            if (labelStyle == null) labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            float a = Mathf.Clamp01((labelUntil - Time.unscaledTime) / .6f);
            var old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, a);
            GUI.Label(new Rect(16, 14, 900, 28), (index + 1) + "/" + ShotCount + "   " + Titles[index] + "      ← → shot   drag = orbit   wheel = zoom   ↑ reset   F6 = off", labelStyle);
            GUI.color = old;
        }

        // ------------------------------------------------------------------ shot lab (F7)

        /// <summary>Saves a picture of the starting angle of each of the six shots (logs/shots/...).</summary>
        private IEnumerator ShotLab()
        {
            lab = true;
            Enabled = true;
            while (!controlling) yield return null;
            string dir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "logs", "shots", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(dir);
            var report = new StringBuilder();
            for (int i = 0; i < ShotCount; i++)
            {
                Shot s = Make(i);
                if (s == null) { report.AppendLine((i + 1).ToString("00") + "  " + Titles[i] + "  (not available in this scene)"); continue; }
                Cut(s, i);
                blendT = 1f;
                for (int f = 0; f < 6; f++) yield return null;
                yield return new WaitForEndOfFrame();
                SavePicture(Path.Combine(dir, (i + 1).ToString("00") + "_" + Safe(Titles[i]) + ".png"));
                report.AppendLine((i + 1).ToString("00") + "  " + Titles[i] + "  cam " + V(cam.transform.position) + "  look " + V(look) + "  fov " + s.Fov.ToString("F0", CultureInfo.InvariantCulture) +
                                  "  cost " + s.Cost.ToString("F1", CultureInfo.InvariantCulture) + (s.Blocked > 0 ? "  HIDDEN(" + s.Blocked + "/3)" : "  clear"));
            }
            File.WriteAllText(Path.Combine(dir, "lab.txt"), report.ToString());
            Debug.Log("[DIRECTOR] shot lab done: " + dir + "\n" + report);
            lab = false;
            Switch(0, 1);
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
            index = -1;
            dragging = false; wasHeld = HudInput.MouseHeld();
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
                string dir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "logs");
                Directory.CreateDirectory(dir);
                log = new StreamWriter(Path.Combine(dir, "director_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log")) { AutoFlush = true };
                log.WriteLine("# RoboFactory — shot mode, one line per change of shot: real time | time since F6 | scenario time | shot");
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
