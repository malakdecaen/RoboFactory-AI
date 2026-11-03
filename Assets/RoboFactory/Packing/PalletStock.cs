using System.Collections.Generic;
using System.Reflection;
using RoboFactory.Logistics;
using UnityEngine;
using RoboFactory.Sim;

namespace RoboFactory.Packing
{
    /// <summary>
    /// Turns the empty pallets on the left (pallet (7), pallet (8)) into stock pallets for robot R1, like the six
    /// others: storage columns of level points, access points where a robot docks (dock point + trigger), and the
    /// obstacle the robots' planner sees. The layout is copied from the stock pallet with the same orientation
    /// (pallet (3) for pallet (7), pallet (6) for pallet (8)), so the docking, the gestures and the box heights are
    /// exactly those R1 already uses. Done when the scene loads, before R1 and the warehouse model start, then the
    /// warehouse model is refreshed: the pallets appear as P07 / P08. In the public version, where the scene has
    /// no such pallets, they are built by code at the same places first.
    /// </summary>
    public static class PalletStock
    {
        public static readonly string[] Names = { "pallet (7)", "pallet (8)" };
        public static readonly Vector3[] Places = { new Vector3(-5f, 0f, 2.125f), new Vector3(-5f, 0f, 3.625f) };
        public static readonly float[] Yaws = { 270f, 90f };

        public static void Ensure()
        {
            var report = new List<string>();
            for (int i = 0; i < Names.Length; i++)
            {
                GameObject go = GameObject.Find(Names[i]);
                bool built = false;
                if (go == null) { go = ProceduralPallet(Names[i], Places[i], Yaws[i]); built = true; }
                string r = Equip(go.transform);
                report.Add(Names[i] + (built ? " (built)" : "") + ": " + r);
            }
            if (WarehouseWorld.Instance != null) WarehouseWorld.Instance.Refresh();
            Debug.Log("[PACK] stock pallets for R1: " + string.Join(" · ", report));
        }

        /// <summary>Adds the storage, the access points and the obstacle of a stock pallet, copied from a template.</summary>
        private static string Equip(Transform target)
        {
            ShelfStorage existing = target.GetComponent<ShelfStorage>();
            if (existing != null && Usable(existing) && Zones(existing).Count > 0) return "already a stock pallet";

            ShelfStorage template = Template(target);
            if (template == null) return "no stock pallet to copy, left as it is";

            // pose of a template object → same pose relative to the target
            Transform t = template.transform;
            System.Func<Vector3, Vector3> P = p => target.TransformPoint(t.InverseTransformPoint(p));
            System.Func<Quaternion, Quaternion> R = q => target.rotation * (Quaternion.Inverse(t.rotation) * q);

            // 1. columns of level points (empty)
            var columns = new ShelfStorage.StorageColumn[template.columns.Length];
            for (int c = 0; c < template.columns.Length; c++)
            {
                Transform[] src = template.columns[c] != null && template.columns[c].levels != null ? template.columns[c].levels : new Transform[0];
                var levels = new Transform[src.Length];
                for (int l = 0; l < src.Length; l++)
                {
                    if (src[l] == null) continue;
                    var point = new GameObject(src[l].name).transform;
                    point.SetParent(target, true);
                    point.SetPositionAndRotation(P(src[l].position), R(src[l].rotation));
                    levels[l] = point;
                }
                columns[c] = new ShelfStorage.StorageColumn { levels = levels };
            }
            // the storage component reads its columns in Awake: add it while the pallet is inactive
            bool wasActive = target.gameObject.activeSelf;
            target.gameObject.SetActive(false);
            ShelfStorage shelf = existing != null ? existing : target.gameObject.AddComponent<ShelfStorage>();
            shelf.columns = columns;
            target.gameObject.SetActive(wasActive);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            typeof(ShelfStorage).GetMethod("InitializeStorage", flags)?.Invoke(shelf, null);
            typeof(ShelfStorage).GetMethod("AutoFindExistingBoxes", flags)?.Invoke(shelf, null);

            // 2. access points (dock point + trigger), one per column and side, as on the template
            int zones = 0;
            foreach (ShelfInteractionZone z in Zones(template))
            {
                GameObject copy = Object.Instantiate(z.gameObject, target, true);
                copy.name = z.gameObject.name;
                copy.transform.SetPositionAndRotation(P(z.transform.position), R(z.transform.rotation));
                ShelfInteractionZone nz = copy.GetComponent<ShelfInteractionZone>();
                nz.shelf = shelf;
                nz.columnIndex = z.columnIndex;
                // the dock point is a child of the zone (reference remapped by the copy), else copy its pose
                if (nz.dockPoint == null || !nz.dockPoint.IsChildOf(copy.transform))
                {
                    var dock = new GameObject("DockPoint").transform;
                    dock.SetParent(copy.transform, true);
                    dock.SetPositionAndRotation(P(z.dockPoint.position), R(z.dockPoint.rotation));
                    nz.dockPoint = dock;
                }
                foreach (CargoBox b in copy.GetComponentsInChildren<CargoBox>(true)) Object.Destroy(b.gameObject);
                zones++;
            }

            // 3. obstacle for the robots' planner, if the pallet has none
            int colliders = 0;
            if (!HasSolidCollider(target))
                foreach (BoxCollider bc in template.GetComponentsInChildren<BoxCollider>(true))
                {
                    if (bc.isTrigger || bc.GetComponentInParent<CargoBox>() != null || bc.GetComponentInParent<ShelfInteractionZone>() != null) continue;
                    var g = new GameObject(bc.gameObject.name + " (obstacle)");
                    g.layer = bc.gameObject.layer;
                    g.transform.SetParent(target, true);
                    g.transform.SetPositionAndRotation(P(bc.transform.position), R(bc.transform.rotation));
                    g.transform.localScale = Vector3.Scale(bc.transform.lossyScale, Inverse(target.lossyScale));
                    var nb = g.AddComponent<BoxCollider>();
                    nb.center = bc.center;
                    nb.size = bc.size;
                    colliders++;
                }
            int levelsCount = 0;
            foreach (ShelfStorage.StorageColumn c in columns) levelsCount += c.levels.Length;
            return columns.Length + " columns / " + levelsCount + " places, " + zones + " access points" +
                   (colliders > 0 ? ", obstacle added" : "") + " (copied from " + template.name + ")";
        }

        private static Vector3 Inverse(Vector3 v) => new Vector3(v.x != 0f ? 1f / v.x : 1f, v.y != 0f ? 1f / v.y : 1f, v.z != 0f ? 1f / v.z : 1f);

        private static bool Usable(ShelfStorage s)
        {
            if (s.columns == null || s.columns.Length == 0) return false;
            foreach (ShelfStorage.StorageColumn c in s.columns)
                if (c != null && c.levels != null) foreach (Transform l in c.levels) if (l != null) return true;
            return false;
        }

        private static List<ShelfInteractionZone> Zones(ShelfStorage s)
        {
            var list = new List<ShelfInteractionZone>();
            foreach (ShelfInteractionZone z in Object.FindObjectsByType<ShelfInteractionZone>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (z != null && z.shelf == s && z.dockPoint != null) list.Add(z);
            return list;
        }

        /// <summary>Stock pallet (not a bench, not a truck) with the same orientation, usable, with access points; the nearest.</summary>
        private static ShelfStorage Template(Transform target)
        {
            ShelfStorage best = null;
            float bestD = float.MaxValue;
            foreach (ShelfStorage s in Object.FindObjectsByType<ShelfStorage>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (s == null || s.transform == target || Station.RoleOf(s) != StationRole.Pallet) continue;
                if (!s.name.ToLowerInvariant().StartsWith("pallet (")) continue;
                if (!Usable(s) || Zones(s).Count == 0) continue;
                float yaw = Mathf.Abs(Mathf.DeltaAngle(s.transform.eulerAngles.y, target.eulerAngles.y));
                float d = Vector3.Distance(s.transform.position, target.position) + (yaw > 1f ? 1000f : 0f);
                if (d < bestD) { bestD = d; best = s; }
            }
            return best;
        }

        private static bool HasSolidCollider(Transform t)
        {
            foreach (Collider c in t.GetComponentsInChildren<Collider>(true))
                if (!c.isTrigger && c.GetComponentInParent<CargoBox>() == null && c.GetComponentInParent<ShelfInteractionZone>() == null) return true;
            return false;
        }

        /// <summary>
        /// Wooden pallet built by code (public version), 1.25 m x 1.45 m like the warehouse pallets: by the scene
        /// builder (materials saved as assets), or at run time when the scene has none.
        /// </summary>
        public static GameObject ProceduralPallet(string name, Vector3 at, float yaw, System.Func<Color, float, float, Material> material = null)
        {
            var root = new GameObject(name);
            root.transform.SetPositionAndRotation(at, Quaternion.Euler(0f, yaw, 0f));
            // same footprint as the warehouse pallets: 2.5 m x 1.48 m, top 0.176 m (long side along world x here)
            BuildPalletModel(root.transform, at, 2.5f, 1.4766f, .176f, material);
            return root;
        }

        /// <summary>
        /// Wooden pallet model, world-aligned: three runners along the long side, deck boards across, deck top at
        /// <paramref name="height"/> above <paramref name="bottomCentre"/>. Used for every stock pallet of the public scene.
        /// </summary>
        public static void BuildPalletModel(Transform parent, Vector3 bottomCentre, float sizeX, float sizeZ, float height,
                                            System.Func<Color, float, float, Material> material = null)
        {
            mat = material ?? PackingItemFactory.Mat;
            Color wood = new Color(.66f, .5f, .3f), dark = new Color(.45f, .33f, .2f);
            bool alongX = sizeX >= sizeZ;
            float length = alongX ? sizeX : sizeZ, width = alongX ? sizeZ : sizeX;
            float deck = Mathf.Min(.03f, height * .3f), runnerH = Mathf.Max(.02f, height - deck);
            System.Func<float, float, Vector3> W = (along, across) => bottomCentre + (alongX ? new Vector3(along, 0f, across) : new Vector3(across, 0f, along));
            System.Func<float, float, Vector3> S = (along, across) => alongX ? new Vector3(along, 1f, across) : new Vector3(across, 1f, along);
            foreach (float k in new[] { -.42f, 0f, .42f })
            {
                Vector3 sz = S(length, .12f); sz.y = runnerH;
                Part(parent, W(0f, k * width) + Vector3.up * (runnerH / 2f), sz, dark, true);
            }
            int boards = Mathf.Max(3, Mathf.RoundToInt(length / .2f));
            float pitch = length / boards;
            for (int i = 0; i < boards; i++)
            {
                Vector3 sz = S(pitch * .72f, width); sz.y = deck;
                Part(parent, W(-length / 2f + pitch * (i + .5f), 0f) + Vector3.up * (runnerH + deck / 2f), sz, wood, true);
            }
        }

        private static System.Func<Color, float, float, Material> mat;

        private static void Part(Transform parent, Vector3 world, Vector3 size, Color color, bool worldSpace)
        {
            GameObject g = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(g.GetComponent<Collider>());
            g.name = "pallet";
            g.transform.SetPositionAndRotation(world, Quaternion.identity);
            g.transform.localScale = size;
            g.transform.SetParent(parent, true);
            g.GetComponent<Renderer>().sharedMaterial = mat(color, .2f, 0f);
        }
    }
}
