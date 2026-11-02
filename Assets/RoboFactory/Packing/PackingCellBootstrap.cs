using UnityEngine;
using UnityEngine.SceneManagement;

namespace RoboFactory.Packing
{
    /// <summary>
    /// Creates the packing cell in the warehouse scenes, at the place of the conveyor loop (positions measured in
    /// the full project with RoboFactory → Packing Cell → Survey). In the full project the arm of the factory
    /// environment is driven through its IK target; in the public version (no store asset) the conveyor, the black
    /// box, the trays, the reject bin and the arm are built by code at the same positions.
    /// </summary>
    public static class PackingCellBootstrap
    {
        public const string FullScene = "warehouse_robot_demo";
        public const string PublicScene = "warehouse_simplified";

        // ---- layout of the warehouse demo (world coordinates, metres)
        private const float Belt = .85f;
        private static readonly Vector3[] Corners =
        {
            new Vector3(-2.561f, Belt, -10.192f),   // south-east (items go east along the south side, out of the black box)
            new Vector3(-2.561f, Belt, -4.873f),    // north-east
            new Vector3(-12.573f, Belt, -4.873f),   // north-west (the arm picks on the north side)
            new Vector3(-12.573f, Belt, -10.192f),  // south-west, then back east through the black box
        };
        private static readonly Vector3 Spawn = new Vector3(-8.75f, Belt, -10.192f);
        // right after the black box exit: Muse Spark has the whole way to the arm (~13 m) to answer
        private static readonly Vector3 Scan = new Vector3(-6.6f, Belt, -10.192f);
        private static readonly Vector3 Pick = new Vector3(-6.389f, Belt, -4.873f);
        private static readonly Vector3[] Trays =
        {
            new Vector3(-6.17f, Belt, -4.29f), new Vector3(-6.91f, Belt, -4.29f), new Vector3(-5.43f, Belt, -4.29f), new Vector3(-7.66f, Belt, -4.29f)
        };
        // Trays lowered so that a closed box (0.5 m, origin at its centre like every warehouse box) has its centre at
        // the height of a storage level 1 point (0.926 m): R3's load animation then grabs it exactly like R1 / R2
        // grab a box on a pallet. Tray top 0.85 m → 0.676 m.
        private const float TrayTop = .676f;
        private static readonly Vector3 BinCentre = new Vector3(-8.345f, 0f, -5.871f);
        private static readonly Vector3 ArmBase = new Vector3(-6.3885f, 0f, -6.2903f);
        private const float ArmScale = 1.25f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Init()
        {
            SceneManager.sceneLoaded -= OnLoaded;
            SceneManager.sceneLoaded += OnLoaded;
            OnLoaded(SceneManager.GetActiveScene(), LoadSceneMode.Single);
        }

        private static void OnLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name != FullScene && scene.name != PublicScene) return;
            if (Object.FindFirstObjectByType<PackingCell>() != null) return;
            bool full = GameObject.Find("Line_07") != null;
            // pallets (7) and (8) become stock pallets for R1 (before R1 and the warehouse model start)
            PalletStock.Ensure();
            var go = new GameObject("Packing Cell");
            SceneManager.MoveGameObjectToScene(go, scene);
            var cell = go.AddComponent<PackingCell>();
            cell.Corners = Corners;
            cell.SpawnPoint = Spawn;
            cell.ScanPoint = Scan;
            cell.PickPoint = Pick;
            int lowered = full ? LowerTrays(Belt - TrayTop) : 0;
            float trayTop = full && lowered == 0 ? Belt : TrayTop;
            var slots = new Vector3[Trays.Length];
            for (int i = 0; i < Trays.Length; i++) slots[i] = new Vector3(Trays[i].x, trayTop, Trays[i].z);
            cell.TraySlots = slots;
            if (full) Debug.Log("[PACK] " + (lowered > 0 ? lowered + " tray modules lowered to " + trayTop.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + " m (closed box at the height of a storage level)" : "tray modules not found: trays left at " + Belt + " m"));
            cell.TrayYaw = 0f;
            cell.BinDrop = BinCentre + new Vector3(.4f, 1.55f, 0f);
            cell.BinFloorY = .18f;
            if (full)
            {
                // gripping cylinder of the factory arm, measured on the model (wrist frame): end + outward axis.
                // Scaled x1.25: at its original size the arm cannot lift a product over the 0.5-0.6 m cartons on the
                // trays 2 m away (only the nearest tray, with the lowest carton, was in reach).
                cell.ArmFactory = c => IkTargetArm.Find("ArmIK", new Vector3(.278f, -.5615f, -.027f), Vector3.down, ArmScale);
            }
            else
            {
                Dressing.Build(go.transform);
                cell.ArmFactory = c => new ProceduralArm(c.transform, ArmBase, 0f);
            }
            // transport robot R3: closed cartons from the trays to the empty pallets on the left (stock for R1)
            go.AddComponent<PackingTransport>().Cell = cell;
        }

        /// <summary>Lowers the roller modules in front of the arm (the "Line_02" units of the arm's line) by <paramref name="drop"/>.</summary>
        private static int LowerTrays(float drop)
        {
            int n = 0;
            foreach (Transform t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (t.name != "Line_02" || t.parent == null || t.parent.name != "Line_07") continue;
                bool atTray = false;
                foreach (Vector3 tray in Trays)
                    if (new Vector2(t.position.x - tray.x, t.position.z - tray.z).magnitude < .45f) { atTray = true; break; }
                if (!atTray) continue;
                t.position += Vector3.down * drop;
                n++;
            }
            return n;
        }

        /// <summary>Conveyor loop, black box, trays and reject bin built by code (public version).</summary>
        private static class Dressing
        {
            public static void Build(Transform parent)
            {
                var root = new GameObject("Packing Cell Dressing").transform;
                root.SetParent(parent, false);
                Color belt = new Color(.12f, .12f, .13f), frame = new Color(.55f, .57f, .6f), black = new Color(.06f, .06f, .07f);
                for (int i = 0; i < Corners.Length; i++)
                {
                    Vector3 a = Corners[i], b = Corners[(i + 1) % Corners.Length];
                    Vector3 mid = (a + b) / 2f, dir = (b - a);
                    float len = dir.magnitude + .55f;
                    Quaternion rot = Quaternion.LookRotation(dir.normalized, Vector3.up);
                    Box(root, mid + Vector3.down * .025f, rot, new Vector3(.55f, .05f, len), belt, .3f);
                    Box(root, mid + Vector3.down * .09f, rot, new Vector3(.6f, .08f, len), frame, .5f);
                    for (float s = -dir.magnitude / 2f; s <= dir.magnitude / 2f + .01f; s += 1.2f)
                    {
                        Vector3 leg = mid + dir.normalized * s;
                        Box(root, new Vector3(leg.x, (Belt - .13f) / 2f, leg.z), rot, new Vector3(.5f, Belt - .13f, .05f), frame, .5f);
                    }
                }
                // black box the belt runs through (products are made inside)
                Box(root, new Vector3(-8.75f, 1.48f, -9.3f), Quaternion.identity, new Vector3(2.4f, 2.95f, 3.05f), black, .2f);
                Box(root, new Vector3(-8.75f, 2.97f, -9.3f), Quaternion.identity, new Vector3(2.42f, .04f, 3.07f), new Color(.9f, .9f, .9f), .5f);
                // trays (small roller modules on white stands)
                foreach (Vector3 tray in Trays)
                {
                    Vector3 t = new Vector3(tray.x, TrayTop, tray.z);
                    Box(root, t + Vector3.down * .03f, Quaternion.identity, new Vector3(.36f, .06f, .64f), black, .4f);
                    Box(root, new Vector3(t.x, (TrayTop - .06f) / 2f, t.z), Quaternion.identity, new Vector3(.3f, TrayTop - .06f, .5f), new Color(.92f, .88f, .82f), .3f);
                }
                // reject bin (open grey container)
                Color grey = new Color(.42f, .43f, .45f);
                float w = 1.5f, h = 1.3f;
                Box(root, BinCentre + Vector3.up * .06f, Quaternion.identity, new Vector3(w, .12f, w), grey, .3f);
                Box(root, BinCentre + new Vector3(w / 2f, h / 2f, 0f), Quaternion.identity, new Vector3(.05f, h, w), grey, .3f);
                Box(root, BinCentre + new Vector3(-w / 2f, h / 2f, 0f), Quaternion.identity, new Vector3(.05f, h, w), grey, .3f);
                Box(root, BinCentre + new Vector3(0f, h / 2f, w / 2f), Quaternion.identity, new Vector3(w, h, .05f), grey, .3f);
                Box(root, BinCentre + new Vector3(0f, h / 2f, -w / 2f), Quaternion.identity, new Vector3(w, h, .05f), grey, .3f);
            }

            private static void Box(Transform parent, Vector3 pos, Quaternion rot, Vector3 size, Color color, float smooth)
            {
                GameObject g = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Object.Destroy(g.GetComponent<Collider>());
                g.transform.SetParent(parent, false);
                g.transform.SetPositionAndRotation(pos, rot);
                g.transform.localScale = size;
                g.GetComponent<Renderer>().sharedMaterial = PackingItemFactory.Mat(color, smooth, .2f);
            }
        }
    }
}
