using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using RoboFactory.Sim;

namespace RoboFactory.Packing
{
    /// <summary>
    /// Transport robot R3 of the packing cell. It waits next to the trays; when a carton is closed and labelled it
    /// docks in front of its tray, takes the box (the same box model robot R1 handles), drives to the empty pallets
    /// on the left and stacks it there. A new empty carton of the same type then appears on the tray.
    ///
    /// The pallets become stock for R1: when the pallet has the warehouse's storage component (columns of level
    /// points, as on the other pallets), the box is registered in it exactly like a box R1 would put down; otherwise
    /// the box is only stacked on the pallet (shown, not counted in R1's stock), and the log says so.
    ///
    /// R3 is a copy of the warehouse robot's model with all its behaviours removed (it is not part of R1/R2's
    /// grid planning): it drives on two fixed lanes that stay clear of the other robots (along the trays, then
    /// north along the west side of the pallets).
    /// </summary>
    public sealed class PackingTransport : MonoBehaviour
    {
        public PackingCell Cell;
        public string RobotTemplate = "robot_boxy";
        public string[] PalletNames = { "pallet (7)", "pallet (8)" };
        [Tooltip("Where the pallets are built when the scene has none (public version), same place as in the full project.")]
        public Vector3[] PalletFallback = { new Vector3(-5f, 0f, 2.125f), new Vector3(-5f, 0f, 3.625f) };

        [Header("Lanes (world)")]
        [Tooltip("Robot centre line in front of the trays (z).")]
        public float TrayLaneZ = -3.665f;   // 0.625 m from the tray centre, like a shelf access point
        [Tooltip("Robot centre line along the west side of the pallets (x).")]
        public float PalletLaneX = -6.05f;
        public Vector3 Park = new Vector3(-6.05f, 0f, -2.6f);
        [Tooltip("Distance from a pallet column to the robot centre when it docks.")]
        public float DockOffset = .625f;   // same distance as the access points of the warehouse shelves
        public int LevelsPerColumn = 3;

        public float Speed = .9f;
        public float TurnSpeed = 160f;   // deg/s
        public float TransferSeconds = 1.3f;

        public bool Ready { get; private set; }
        public string Activity { get; private set; } = "idle";
        public int Delivered { get; private set; }
        public Transform Robot => robot;

        private Transform robot;
        private float carryHeight = .5f;
        private Transform carryRoot;          // where the carried box goes (box origin), as the robot's own cargo script does
        private Vector3 carryOffset;          // the cargo script's carriedBoxLocalPosition
        private string attachPath;            // the cargo script's attach point (path under the robot), found on the model
        private bool stableRoot = true;       // createStableCarryRoot of the cargo script
        private Animator anim;
        private PackingRobotEvents events;

        // gesture timing of the warehouse robot (load / unload per level 0/1/2), same table as the public robot
        private static readonly float[] LoadSeconds = { 38f / 30f, 65f / 30f, 67f / 30f }, LoadAttachAt = { .466f, .425f, .488f };
        private static readonly float[] UnloadSeconds = { 38f / 30f, 55f / 30f, 75f / 30f }, UnloadDetachAt = { .565f, .580f, .564f };
        private readonly List<Column> columns = new List<Column>();
        private bool fullLogged;

        private sealed class Column
        {
            public string Pallet;
            public Transform PalletRoot;
            public ShelfStorage Shelf;        // null = stacked only (not in R1's stock)
            public int Index;
            public Vector3 Centre;            // world, floor of the stack (visual mode) or level 0 point (storage mode)
            public float Top;                 // top of the stack (visual mode)
            public int Count;
            public Vector3 Dock;
            public float DockYaw;
        }

        private IEnumerator Start()
        {
            if (Cell == null) Cell = GetComponent<PackingCell>();
            // the cell builds its trays and cartons first
            while (Cell != null && Cell.Track == null) yield return null;
            if (Cell == null || !Cell.enabled) yield break;
            BuildPallets();
            robot = BuildRobot();
            if (robot == null || columns.Count == 0)
            {
                Debug.Log("[PACK] R3 transport robot not available (" + (robot == null ? "no robot model" : "no pallet") + "): closed cartons leave their tray by themselves");
                yield break;
            }
            robot.SetPositionAndRotation(Park, Quaternion.Euler(0f, 180f, 0f));
            carryHeight = TopOf(robot) - robot.position.y;
            AddObstacle();
            // Same carry anchor as R1 / R2's cargo script: the asset robot carries the box on its ANIMATED attach point
            // (createStableCarryRoot off: the box follows the arms), the public robot on its stable root on its top.
            Transform attach = attachPath != null ? (attachPath.Length == 0 ? robot : robot.Find(attachPath)) : null;
            carryRoot = !stableRoot && attach != null ? attach : FindChild(robot, "StableCargoRoot");
            if (carryRoot == null) carryRoot = attach;
            anim = robot.GetComponentInChildren<Animator>();
            if (anim != null && anim.runtimeAnimatorController == null) anim = null;
            if (anim != null)
            {
                events = anim.GetComponent<PackingRobotEvents>();
                if (events == null) events = anim.gameObject.AddComponent<PackingRobotEvents>();
                SetBool("HasBox", false);
                SetBool("IsMoving", false);
            }
            Cell.TransportConnected = true;
            Ready = true;
            var names = new List<string>();
            foreach (string p in PalletNames)
            {
                int n = 0;
                bool shelf = false;
                foreach (Column c in columns) if (c.Pallet == p) { n++; shelf |= c.Shelf != null; }
                if (n > 0) names.Add(p + " (" + n + " columns, " + (shelf ? "warehouse storage: stock for R1" : "stacked only") + ")");
            }
            Debug.Log("[PACK] R3 transport robot ready (copy of " + RobotTemplate + (anim != null ? ", with its load / unload animations" : "") +
                      "), parked at " + F(Park) + ", carries to " + string.Join(", ", names));
            StartCoroutine(Run());
        }

        // ------------------------------------------------------------------ work loop

        private IEnumerator Run()
        {
            bool parked = true;
            while (true)
            {
                PackingCarton carton = NextCarton();
                Column column = carton != null ? NextColumn() : null;
                if (carton == null || column == null)
                {
                    if (carton != null && !fullLogged) { fullLogged = true; Debug.Log("[PACK] R3: the pallets are full, closed cartons wait on their tray"); }
                    if (!parked)
                    {
                        Activity = "parking";
                        yield return Drive(new Vector3(robot.position.x, 0f, TrayLaneZ));
                        yield return Drive(new Vector3(PalletLaneX, 0f, TrayLaneZ));
                        yield return Drive(Park);
                        yield return Face(180f);
                        parked = true;
                        Activity = "idle";
                    }
                    yield return new WaitForSeconds(.5f);
                    continue;
                }
                parked = false;
                yield return Deliver(carton, column);
            }
        }

        private PackingCarton NextCarton()
        {
            PackingCarton best = null;
            foreach (PackingCarton c in Cell.Trays)
                if (c != null && c.ReadyToShip && c.State != PackingCarton.CartonState.Taken && (best == null || c.Number < best.Number)) best = c;
            return best;
        }

        private Column NextColumn()
        {
            foreach (Column c in columns)
            {
                if (c.Shelf != null)
                {
                    if (c.Shelf.HasFreePlaceToPut(c.Index, out int _)) return c;
                }
                else if (c.Count < LevelsPerColumn) return c;
            }
            return null;
        }

        private IEnumerator Deliver(PackingCarton carton, Column column)
        {
            string name = carton.Type + "#" + carton.Number;
            int tray = carton.TraySlot;
            Vector3 trayPos = Cell.TraySlots[tray];

            // 1. to the tray: along the tray lane, then face the tray (south)
            Activity = "to tray " + (tray + 1) + " for carton " + name;
            if (Mathf.Abs(robot.position.z - TrayLaneZ) > .05f)
            {
                yield return Drive(new Vector3(PalletLaneX, 0f, robot.position.z));
                yield return Drive(new Vector3(PalletLaneX, 0f, TrayLaneZ));
            }
            yield return Drive(new Vector3(trayPos.x, 0f, TrayLaneZ));
            yield return Face(180f);

            // 2. take the box: load gesture of the robot (tray height = level 1), the box comes onto it at the attach moment
            Activity = "taking carton " + name;
            Transform box = carton.RealBox != null ? carton.RealBox.transform : carton.transform;
            CargoBox cargo = box.GetComponent<CargoBox>();
            if (cargo != null) cargo.isCarried = true;
            Debug.Log("[PACK] R3 takes carton " + name + " from tray " + (tray + 1) + (carton.Destination.Length > 0 ? " (to " + carton.Destination + ")" : ""));
            // gesture of the storage level whose height matches the box (warehouse levels: centre at 0.426 / 0.926 / 1.426 m)
            int trayLevel = Mathf.Clamp(Mathf.RoundToInt((BoxCentreY(box) - .426f) / .5f), 0, 2);
            yield return Gesture(true, trayLevel, () => OntoRobot(box));
            if (box != carton.transform)
            {
                Cell.CartonTaken(carton);
                Destroy(carton.gameObject);
            }
            else Cell.CartonTaken(carton);

            // 3. to the pallet: back on the tray lane, north along the pallet lane, face the column
            Activity = "carrying carton " + name + " to " + column.Pallet;
            yield return Drive(new Vector3(PalletLaneX, 0f, TrayLaneZ));
            yield return Drive(new Vector3(PalletLaneX, 0f, column.Dock.z));
            yield return Drive(column.Dock);
            yield return Face(column.DockYaw);

            // 4. put it down on the pallet: unload gesture for the level, the box leaves the robot at the detach moment
            Activity = "unloading carton " + name;
            string where;
            if (column.Shelf != null && column.Shelf.HasFreePlaceToPut(column.Index, out int level))
            {
                Transform point = column.Shelf.GetPoint(column.Index, level);
                Quaternion rot = box.rotation;
                yield return Gesture(false, level, () => OffRobot(box, () => point.position - (box.position - BottomCentre(box))));
                box.SetPositionAndRotation(point.position, rot);
                column.Shelf.PutBox(column.Index, level, cargo, rot);
                where = column.Pallet + " column " + (column.Index + 1) + " level " + (level + 1) + " (stock for R1)";
            }
            else
            {
                Vector3 at = new Vector3(column.Centre.x, column.Top, column.Centre.z);
                yield return Gesture(false, Mathf.Min(column.Count, 2), () => OffRobot(box, () => at));
                box.SetParent(column.PalletRoot, true);
                column.Top = TopOf(box) + .002f;
                where = column.Pallet + " stack " + (columns.IndexOf(column) + 1) + " level " + (column.Count + 1) + " (stacked, not in R1's stock)";
            }
            column.Count++;
            if (cargo != null) { cargo.isCarried = false; cargo.orderId = -1; }
            Delivered++;
            Debug.Log("[PACK] R3 put carton " + name + " on " + where);
            Activity = "idle";
        }

        // ------------------------------------------------------------------ gestures (animations of the robot model)

        /// <summary>
        /// Load or unload gesture: the robot model's own animation (trigger Load_Level_n / Unload_Level_n, as the
        /// warehouse robots), the box is moved at the moment the animation says (its AttachBoxToRobot /
        /// DetachBoxToShelf events), then the gesture finishes (OnCargoAnimationFinished). Without an animated
        /// model, the same timing as the warehouse robots.
        /// </summary>
        private IEnumerator Gesture(bool load, int level, Func<IEnumerator> atMoment)
        {
            level = Mathf.Clamp(level, 0, 2);
            float duration = load ? LoadSeconds[level] : UnloadSeconds[level];
            float moment = duration * (load ? LoadAttachAt[level] : UnloadDetachAt[level]);
            string trigger = (load ? "Load_Level_" : "Unload_Level_") + level;
            float t0 = Time.time;
            if (anim != null && HasParam(trigger))
            {
                events.Clear();
                anim.SetTrigger(trigger);
                while (!(load ? events.Attached : events.Detached) && Time.time < t0 + moment + 1.5f) yield return null;
                yield return atMoment();
                SetBool("HasBox", load);
                while (!events.Finished && Time.time < t0 + duration + 2f) yield return null;
                anim.ResetTrigger(trigger);
            }
            else
            {
                yield return new WaitForSeconds(moment);
                yield return atMoment();
                float rest = t0 + duration - Time.time;
                if (rest > 0f) yield return new WaitForSeconds(rest);
            }
        }

        /// <summary>Box from the tray onto the robot (origin on its cargo root, like the warehouse robots).</summary>
        /// <summary>
        /// Box from the tray onto the robot, at once at the gesture's attach moment, exactly as the warehouse robots
        /// do it: animated model of the full project → box origin on the robot's cargo root (rotation kept), the
        /// arms of the animation do the visual work; robot of the public version → box resting on the robot's top
        /// (its cargo root), like its own cargo script.
        /// </summary>
        private IEnumerator OntoRobot(Transform box)
        {
            Quaternion rot = box.rotation;
            Transform root = carryRoot != null ? carryRoot : robot;
            box.SetParent(root, true);
            if (anim != null && carryRoot != null) box.SetPositionAndRotation(carryRoot.TransformPoint(carryOffset), rot);
            else
            {
                Vector3 top = carryRoot != null ? carryRoot.position : robot.position + Vector3.up * carryHeight;
                box.position += top - BottomCentre(box);
                box.rotation = rot;
            }
            yield break;
        }

        /// <summary>Box from the robot to its place on the pallet, at once at the gesture's detach moment.</summary>
        private IEnumerator OffRobot(Transform box, Func<Vector3> bottomTarget)
        {
            box.SetParent(null, true);
            box.position = bottomTarget() + (box.position - BottomCentre(box));
            yield break;
        }

        private bool HasParam(string name)
        {
            foreach (AnimatorControllerParameter p in anim.parameters) if (p.name == name) return true;
            return false;
        }

        private void SetBool(string name, bool value)
        {
            if (anim == null) return;
            foreach (AnimatorControllerParameter p in anim.parameters)
                if (p.name == name && p.type == AnimatorControllerParameterType.Bool) { anim.SetBool(name, value); return; }
        }

        private static Transform FindChild(Transform t, string name)
        {
            foreach (Transform c in t.GetComponentsInChildren<Transform>(true)) if (c.name == name) return c;
            return null;
        }

        // ------------------------------------------------------------------ motion

        private IEnumerator Drive(Vector3 to)
        {
            to.y = 0f;
            Vector3 d = to - robot.position;
            d.y = 0f;
            if (d.magnitude < .01f) yield break;
            yield return Face(Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg);
            SetBool("IsMoving", true);
            while (true)
            {
                Vector3 p = Vector3.MoveTowards(robot.position, to, Speed * Time.deltaTime);
                robot.position = p;
                if ((p - to).sqrMagnitude < 1e-6f) break;
                yield return null;
            }
            SetBool("IsMoving", false);
        }

        private IEnumerator Face(float yaw)
        {
            Quaternion want = Quaternion.Euler(0f, yaw, 0f);
            if (Quaternion.Angle(robot.rotation, want) > .5f) SetBool("IsMoving", true);
            while (Quaternion.Angle(robot.rotation, want) > .5f)
            {
                robot.rotation = Quaternion.RotateTowards(robot.rotation, want, TurnSpeed * Time.deltaTime);
                yield return null;
            }
            robot.rotation = want;
            SetBool("IsMoving", false);
        }

        /// <summary>Moves a box (by the centre of its bottom face) to a point: up, across, down.</summary>
        private IEnumerator Transfer(Transform box, Func<Vector3> bottomTarget, float seconds)
        {
            Vector3 offset = box.position - BottomCentre(box);
            Vector3 from = box.position - offset;
            float lift = Mathf.Max(from.y, bottomTarget().y) + .08f;
            float t = 0f;
            while (t < 1f)
            {
                t = Mathf.Min(1f, t + Time.deltaTime / Mathf.Max(.05f, seconds));
                Vector3 to = bottomTarget();
                Vector3 p;
                if (t < .25f) p = new Vector3(from.x, Mathf.Lerp(from.y, lift, Mathf.SmoothStep(0f, 1f, t / .25f)), from.z);
                else if (t < .75f)
                {
                    float k = Mathf.SmoothStep(0f, 1f, (t - .25f) / .5f);
                    p = new Vector3(Mathf.Lerp(from.x, to.x, k), lift, Mathf.Lerp(from.z, to.z, k));
                }
                else p = new Vector3(to.x, Mathf.Lerp(lift, to.y, Mathf.SmoothStep(0f, 1f, (t - .75f) / .25f)), to.z);
                box.position = p + offset;
                yield return null;
            }
        }

        // ------------------------------------------------------------------ setup

        private void BuildPallets()
        {
            for (int i = 0; i < PalletNames.Length; i++)
            {
                GameObject go = GameObject.Find(PalletNames[i]);
                if (go == null && i < PalletFallback.Length) go = ProceduralPallet(PalletNames[i], PalletFallback[i]);
                if (go == null) continue;
                Transform root = go.transform;
                ShelfStorage shelf = go.GetComponentInParent<ShelfStorage>();
                if (shelf == null) shelf = go.GetComponentInChildren<ShelfStorage>();
                int added = 0;
                if (shelf != null && shelf.columns != null)
                {
                    for (int c = 0; c < shelf.columns.Length; c++)
                    {
                        Transform p0 = shelf.GetPoint(c, 0);
                        if (p0 == null || p0.position.x > root.position.x) continue;   // west half only (our lane)
                        columns.Add(new Column
                        {
                            Pallet = PalletNames[i], PalletRoot = root, Shelf = shelf, Index = c, Centre = p0.position,
                            Dock = new Vector3(p0.position.x - DockOffset, 0f, p0.position.z), DockYaw = 90f
                        });
                        added++;
                    }
                }
                if (added > 0) continue;
                // no usable storage: two stacks on the west half of the pallet, like the 2 x 2 columns of the others
                float top = TopOf(root);
                if (top < root.position.y + .05f) top = root.position.y + .15f;
                foreach (float dz in new[] { -.375f, .375f })
                {
                    Vector3 centre = new Vector3(root.position.x - .375f, top, root.position.z + dz);
                    columns.Add(new Column
                    {
                        Pallet = PalletNames[i], PalletRoot = root, Shelf = null, Index = columns.Count, Centre = centre, Top = top,
                        Dock = new Vector3(centre.x - DockOffset, 0f, centre.z), DockYaw = 90f
                    });
                }
            }
            columns.Sort((a, b) => a.Dock.z.CompareTo(b.Dock.z));
        }

        /// <summary>Wooden pallet built by code (public version), 1.25 m x 1.45 m like the warehouse pallets.</summary>
        private GameObject ProceduralPallet(string name, Vector3 at)
        {
            var root = new GameObject(name);
            root.transform.position = at;
            Color wood = new Color(.66f, .5f, .3f), dark = new Color(.45f, .33f, .2f);
            for (int i = 0; i < 3; i++)
                Part(root.transform, new Vector3(-.5f + .5f * i, .05f, 0f), new Vector3(.12f, .1f, 1.45f), dark);
            for (int i = 0; i < 7; i++)
                Part(root.transform, new Vector3(0f, .125f, -.66f + .22f * i), new Vector3(1.25f, .03f, .14f), wood);
            return root;
        }

        private static void Part(Transform parent, Vector3 local, Vector3 size, Color color)
        {
            GameObject g = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(g.GetComponent<Collider>());
            g.name = "pallet";
            g.transform.SetParent(parent, false);
            g.transform.localPosition = local;
            g.transform.localScale = size;
            g.GetComponent<Renderer>().sharedMaterial = PackingItemFactory.Mat(color, .2f, 0f);
        }

        /// <summary>Copy of the warehouse robot's model, every behaviour, collider and camera removed.</summary>
        private Transform BuildRobot()
        {
            GameObject src = GameObject.Find(RobotTemplate);
            GameObject copy;
            if (src != null)
            {
                var holder = new GameObject("R3 holder");
                holder.SetActive(false);   // the copy stays inactive while it is stripped: none of its scripts wakes up
                ReadCargoSettings(src);
                copy = Instantiate(src, holder.transform);
                foreach (CargoBox b in copy.GetComponentsInChildren<CargoBox>(true)) DestroyImmediate(b.gameObject);
                // labels, path lines and overlays attached to R1 at run time would keep showing R1's data
                foreach (Component k in copy.GetComponentsInChildren<Component>(true))
                    if (k != null && k.gameObject != copy && (k is TMPro.TMP_Text || k is Canvas || k is LineRenderer || k is TrailRenderer))
                        DestroyImmediate(k.gameObject);
                Strip(copy);
                foreach (Animator a in copy.GetComponentsInChildren<Animator>(true)) a.applyRootMotion = false;
                copy.name = RobotTemplate + " (R3)";
                copy.transform.SetParent(transform, true);
                Destroy(holder);
            }
            else
            {
                copy = new GameObject("robot R3");
                copy.transform.SetParent(transform, false);
                var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Destroy(body.GetComponent<Collider>());
                body.transform.SetParent(copy.transform, false);
                body.transform.localPosition = new Vector3(0f, .25f, 0f);
                body.transform.localScale = new Vector3(.5f, .4f, .5f);
                body.GetComponent<Renderer>().sharedMaterial = PackingItemFactory.Mat(new Color(.95f, .55f, .1f), .4f, .2f);
            }
            copy.SetActive(true);
            return copy.transform;
        }

        /// <summary>
        /// A solid (kinematic) body the size of the robot, so that R1's planner, which looks for colliders, sees R3
        /// and drives around it like around any obstacle.
        /// </summary>
        private void AddObstacle()
        {
            bool any = false;
            Bounds b = new Bounds(robot.position, Vector3.zero);
            foreach (Renderer r in robot.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || r.GetComponentInParent<CargoBox>() != null) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            if (!any) return;
            var body = robot.gameObject.AddComponent<BoxCollider>();
            Vector3 s = robot.lossyScale;
            body.center = robot.InverseTransformPoint(b.center);
            body.size = new Vector3(b.size.x / Mathf.Max(1e-4f, s.x), b.size.y / Mathf.Max(1e-4f, s.y), b.size.z / Mathf.Max(1e-4f, s.z));
            var rb = robot.gameObject.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
        }

        /// <summary>Carry settings of the template robot's cargo script (read by name: asset or public version).</summary>
        private void ReadCargoSettings(GameObject src)
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            foreach (MonoBehaviour mb in src.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                System.Type t = mb.GetType();
                System.Reflection.FieldInfo ap = t.GetField("cargoAttachPoint", flags);
                if (ap == null || !(ap.GetValue(mb) is Transform point)) continue;
                attachPath = PathFrom(src.transform, point);
                System.Reflection.FieldInfo st = t.GetField("createStableCarryRoot", flags);
                stableRoot = st == null || (bool)st.GetValue(mb);
                System.Reflection.FieldInfo off = t.GetField("carriedBoxLocalPosition", flags);
                if (off != null) carryOffset = (Vector3)off.GetValue(mb);
                return;
            }
        }

        private static string PathFrom(Transform root, Transform t)
        {
            if (t == root) return "";
            var parts = new List<string>();
            for (Transform c = t; c != null && c != root; c = c.parent) parts.Insert(0, c.name);
            return t.IsChildOf(root) ? string.Join("/", parts) : null;
        }

        private static void Strip(GameObject go)
        {
            for (int pass = 0; pass < 8; pass++)
            {
                bool removed = false;
                Component[] all = go.GetComponentsInChildren<Component>(true);
                for (int i = all.Length - 1; i >= 0; i--)
                {
                    Component k = all[i];
                    if (k == null) continue;
                    bool drop = k is MonoBehaviour || k is Collider || k is Rigidbody || k is Joint || k is Camera ||
                                k is AudioListener || k is AudioSource || k is CharacterController;
                    if (!drop || RequiredByOther(k, all)) continue;
                    DestroyImmediate(k);
                    removed = true;
                }
                if (!removed) break;
            }
        }

        private static bool RequiredByOther(Component k, Component[] all)
        {
            foreach (Component o in all)
            {
                if (o == null || o == k || o.gameObject != k.gameObject) continue;
                foreach (object a in o.GetType().GetCustomAttributes(typeof(RequireComponent), true))
                {
                    var rc = (RequireComponent)a;
                    if ((rc.m_Type0 != null && rc.m_Type0.IsInstanceOfType(k)) || (rc.m_Type1 != null && rc.m_Type1.IsInstanceOfType(k)) ||
                        (rc.m_Type2 != null && rc.m_Type2.IsInstanceOfType(k))) return true;
                }
            }
            return false;
        }

        private static float TopOf(Transform t)
        {
            float top = float.NegativeInfinity;
            bool isBox = t.GetComponent<CargoBox>() != null;
            foreach (Renderer r in t.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (!isBox && r.GetComponentInParent<CargoBox>() != null) continue;   // a carried box is not part of the robot
                top = Mathf.Max(top, r.bounds.max.y);
            }
            return float.IsInfinity(top) ? t.position.y : top;
        }

        private static float BoxCentreY(Transform t)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (Renderer r in t.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                lo = Mathf.Min(lo, r.bounds.min.y);
                hi = Mathf.Max(hi, r.bounds.max.y);
            }
            return lo <= hi ? (lo + hi) / 2f : t.position.y;
        }

        private static Vector3 BottomCentre(Transform t)
        {
            bool any = false;
            Bounds b = new Bounds(t.position, Vector3.zero);
            foreach (Renderer r in t.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            return new Vector3(b.center.x, b.min.y, b.center.z);
        }

        private static string F(Vector3 v) => "(" + v.x.ToString("F2", CultureInfo.InvariantCulture) + ", " + v.z.ToString("F2", CultureInfo.InvariantCulture) + ")";
    }
}
