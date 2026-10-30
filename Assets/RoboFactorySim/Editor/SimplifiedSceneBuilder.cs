using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace RoboFactory.Sim.EditorTools
{
    /// <summary>
    /// Builds the demo scene with simple shapes (cubes, cylinders) from
    /// Assets/RoboFactorySim/warehouse_layout.json: same positions of pallets, benches, trucks,
    /// docking zones, obstacles and robots as the full scene, same settings of the
    /// RoboFactory components. No third-party asset. Menu: RoboFactory/Public/Build simplified scene.
    /// </summary>
    public static class SimplifiedSceneBuilder
    {
        public const string LayoutPath = "Assets/RoboFactorySim/warehouse_layout.json";
        public const string ScenePath = "Assets/RoboFactorySim/Scenes/warehouse_simplified.unity";
        private const string MaterialDir = "Assets/RoboFactorySim/Materials";

        private static readonly Dictionary<string, Transform> built = new Dictionary<string, Transform>();
        private static readonly Dictionary<string, Material> materials = new Dictionary<string, Material>();

        [MenuItem("RoboFactory/Public/Build simplified scene")]
        public static void Build()
        {
            if (!File.Exists(LayoutPath))
            {
                Debug.LogError("[Sim] " + LayoutPath + " not found");
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            JObject layout = JObject.Parse(File.ReadAllText(LayoutPath));
            built.Clear();
            materials.Clear();

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            BuildLighting(layout);
            BuildFloor(layout);
            BuildTrucks(layout);
            BuildStorages(layout);
            BuildZones(layout);
            BuildTruckBays(layout);
            BuildObstacles(layout);
            BuildRobots(layout);
            BuildSceneComponents(layout);
            BuildCamera(layout);

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log("[Sim] simplified scene created: " + ScenePath);
        }

        // ------------------------------------------------------------------ helpers

        private static Vector3 V(JToken t) => t == null || t.Type == JTokenType.Null
            ? Vector3.zero
            : new Vector3(t[0].Value<float>(), t[1].Value<float>(), t[2].Value<float>());

        private static Quaternion Q(JToken t) => t == null || t.Type == JTokenType.Null
            ? Quaternion.identity
            : new Quaternion(t[0].Value<float>(), t[1].Value<float>(), t[2].Value<float>(), t[3].Value<float>());

        private static string S(JToken t) => t == null || t.Type == JTokenType.Null ? null : t.Value<string>();

        private static Material Mat(string name, Color color, float smoothness = .25f, float metallic = 0f)
        {
            if (materials.TryGetValue(name, out Material cached)) return cached;
            Directory.CreateDirectory(MaterialDir);
            string path = MaterialDir + "/" + name + ".mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", smoothness);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metallic);
            EditorUtility.SetDirty(mat);
            materials[name] = mat;
            return mat;
        }

        /// <summary>Empty object placed in world space (scale 1), stored under parent.</summary>
        private static Transform Node(string name, Transform parent, Vector3 pos, Quaternion rot)
        {
            var go = new GameObject(name);
            go.transform.SetPositionAndRotation(pos, rot);
            if (parent != null) go.transform.SetParent(parent, true);
            return go.transform;
        }

        /// <summary>Visual cube (no collider) from a world center / rotation / size.</summary>
        private static GameObject Box(string name, Transform parent, Vector3 center, Quaternion rot, Vector3 size, Material mat,
                                      bool keepCollider = false, bool trigger = false)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetPositionAndRotation(center, rot);
            if (parent != null) go.transform.SetParent(parent, true);
            Vector3 parentScale = parent != null ? parent.lossyScale : Vector3.one;
            go.transform.localScale = new Vector3(Mathf.Max(.001f, size.x) / parentScale.x,
                                                  Mathf.Max(.001f, size.y) / parentScale.y,
                                                  Mathf.Max(.001f, size.z) / parentScale.z);
            var col = go.GetComponent<BoxCollider>();
            if (!keepCollider) UnityEngine.Object.DestroyImmediate(col);
            else col.isTrigger = trigger;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.On;
            return go;
        }

        /// <summary>Closest parent already built in a path « a/b/c ».</summary>
        private static Transform ParentOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            int slash = path.LastIndexOf('/');
            while (slash > 0)
            {
                string prefix = path.Substring(0, slash);
                if (built.TryGetValue(prefix, out Transform t)) return t;
                slash = prefix.LastIndexOf('/');
            }
            return null;
        }

        private static Transform Group(string name)
        {
            if (built.TryGetValue("#" + name, out Transform t)) return t;
            t = new GameObject(name).transform;
            built["#" + name] = t;
            return t;
        }

        /// <summary>Serialised settings of a component, without object references (ids not valid here).</summary>
        private static void ApplySettings(Component target, JToken json)
        {
            if (target == null || !(json is JObject obj)) return;
            JObject clean = (JObject)obj.DeepClone();
            StripReferences(clean);
            EditorJsonUtility.FromJsonOverwrite(clean.ToString(), target);
        }

        private static void StripReferences(JToken token)
        {
            if (token is JObject o)
            {
                foreach (JProperty p in o.Properties().ToList())
                {
                    if (IsReference(p.Value) || (p.Value is JArray a && a.Count > 0 && a.All(IsReference))) { p.Remove(); continue; }
                    StripReferences(p.Value);
                }
            }
            else if (token is JArray arr)
                foreach (JToken item in arr) StripReferences(item);
        }

        private static bool IsReference(JToken t) =>
            t is JObject o && (o.ContainsKey("instanceID") || (o.ContainsKey("fileID") && o.Count <= 3));

        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = asm.GetType(fullName, false);
                if (t != null) return t;
            }
            return null;
        }

        // ------------------------------------------------------------------ construction

        private static void BuildLighting(JObject layout)
        {
            var lights = layout["lights"] as JArray;
            JObject first = lights != null && lights.Count > 0 ? (JObject)lights[0] : null;
            var go = new GameObject("Directional Light");
            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.Soft;
            light.intensity = first != null ? first["intensity"].Value<float>() : 1.2f;
            if (first != null)
            {
                go.transform.rotation = Q(first["rot"]);
                JArray c = (JArray)first["color"];
                light.color = new Color(c[0].Value<float>(), c[1].Value<float>(), c[2].Value<float>());
            }
            else go.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(.62f, .66f, .74f);
            RenderSettings.ambientEquatorColor = new Color(.45f, .47f, .5f);
            RenderSettings.ambientGroundColor = new Color(.25f, .25f, .27f);
        }

        private static void BuildFloor(JObject layout)
        {
            JObject b = layout["sceneBounds"] as JObject;
            Vector3 center = b != null ? V(b["center"]) : Vector3.zero;
            Vector3 size = b != null ? V(b["size"]) : new Vector3(60f, 1f, 60f);
            Box("Floor", Group("Environment"), new Vector3(center.x, -.05f, center.z), Quaternion.identity,
                new Vector3(size.x, .1f, size.z), Mat("sim_floor", new Color(.36f, .38f, .41f), .35f));
        }

        private static void BuildTrucks(JObject layout)
        {
            foreach (JObject t in (JArray)layout["trucks"])
            {
                string path = S(t["path"]);
                Transform root = Node(S(t["name"]), null, V(t["pos"]), Q(t["rot"]));
                built[path] = root;

                // Trailer: floor, two sides, roof and back; the end closest to
                // the warehouse (origin) stays open, as TruckBay computes it.
                JObject bb = (JObject)t["bounds"];
                Vector3 c = V(bb["center"]), s = V(bb["size"]);
                Vector3 min = c - s * .5f, max = c + s * .5f;
                float doorX = Mathf.Abs(min.x) <= Mathf.Abs(max.x) ? min.x : max.x;
                float farX = Mathf.Approximately(doorX, min.x) ? max.x : min.x;
                Material shell = Mat("sim_truck", new Color(.82f, .84f, .86f), .4f, .1f);
                Material frame = Mat("sim_truck_frame", new Color(.2f, .22f, .25f), .3f, .4f);
                const float wall = .06f;
                Box("trailer_floor", root, new Vector3(c.x, min.y + .05f, c.z), Quaternion.identity, new Vector3(s.x, .1f, s.z), frame);
                Box("trailer_side_a", root, new Vector3(c.x, c.y, min.z + wall * .5f), Quaternion.identity, new Vector3(s.x, s.y, wall), shell);
                Box("trailer_side_b", root, new Vector3(c.x, c.y, max.z - wall * .5f), Quaternion.identity, new Vector3(s.x, s.y, wall), shell);
                Box("trailer_roof", root, new Vector3(c.x, max.y - wall * .5f, c.z), Quaternion.identity, new Vector3(s.x, wall, s.z), shell);
                Box("trailer_front", root, new Vector3(farX - Mathf.Sign(farX - doorX) * wall * .5f, c.y, c.z), Quaternion.identity, new Vector3(wall, s.y, s.z), shell);

                // Sliding door (TruckBay finds it by its name).
                if (t["gate"] is JObject g)
                {
                    Transform gateParent = root;
                    if (g["parentPose"] is JObject pp && S(pp["path"]) != path)
                        gateParent = Node(S(pp["name"]), root, V(pp["pos"]), Q(pp["rot"]));
                    var gate = new GameObject(S(g["name"])).transform;
                    gate.SetParent(gateParent, false);
                    gate.localPosition = V(g["localPos"]);
                    gate.localRotation = g["localRot"] != null ? Q(g["localRot"]) : Quaternion.identity;
                    if (g["bounds"] is JObject gb)
                        Box("gate_panel", gate, V(gb["center"]), Quaternion.identity, V(gb["size"]),
                            Mat("sim_gate", new Color(.95f, .62f, .12f), .35f));
                }
            }
        }

        /// <summary>
        /// TruckBay component of the trucks, added after their child storages (« pallet depot »):
        /// it is what makes them trucks for WarehouseWorld (otherwise they would be taken for pallets).
        /// </summary>
        private static void BuildTruckBays(JObject layout)
        {
            foreach (JObject t in (JArray)layout["trucks"])
            {
                if (!built.TryGetValue(S(t["path"]), out Transform root)) continue;
                var bay = root.gameObject.AddComponent<RoboFactory.Logistics.TruckBay>();
                ApplySettings(bay, t["settings"]);
                string custom = S(t["customStorage"]);
                if (custom != null && storageByPath.TryGetValue(custom, out ShelfStorage storage)) bay.CustomStorage = storage;
            }
        }

        private static readonly Dictionary<string, ShelfStorage> storageByPath = new Dictionary<string, ShelfStorage>();

        private static void BuildStorages(JObject layout)
        {
            storageByPath.Clear();
            Material pallet = Mat("sim_pallet", new Color(.55f, .4f, .24f), .2f);
            Material bench = Mat("sim_bench", new Color(.18f, .42f, .72f), .4f, .2f);
            Material plate = Mat("sim_shelf_plate", new Color(.7f, .72f, .75f), .4f, .3f);
            foreach (JObject s in (JArray)layout["storages"])
            {
                string path = S(s["path"]);
                Transform parent = ParentOf(path);
                Transform root = Node(S(s["name"]), parent ?? Group("Stations"), V(s["pos"]), Q(s["rot"]));
                built[path] = root;
                string role = S(s["role"]);

                var columns = new List<ShelfStorage.StorageColumn>();
                var levelHeights = new SortedSet<float>();
                int ci = 0;
                foreach (JArray col in (JArray)s["columns"])
                {
                    Transform colNode = Node("column_" + ci, root, root.position, root.rotation);
                    var levels = new List<Transform>();
                    int li = 0;
                    foreach (JToken lvTok in col)
                    {
                        if (!(lvTok is JObject lv)) { levels.Add(null); li++; continue; }
                        Transform point = Node(S(lv["name"]) ?? "level_" + li, colNode, V(lv["pos"]), Q(lv["rot"]));
                        levels.Add(point);
                        levelHeights.Add(Mathf.Round(V(lv["pos"]).y * 100f) / 100f);
                        if (lv["box"] is JObject box) BuildBox(box, point);
                        li++;
                    }
                    columns.Add(new ShelfStorage.StorageColumn { levels = levels.ToArray() });
                    ci++;
                }
                var storage = root.gameObject.AddComponent<ShelfStorage>();
                storage.columns = columns.ToArray();
                storageByPath[path] = storage;

                // Visual: pallet base, or a tray under each level (bench, truck floor).
                if (s["bounds"] is JObject b)
                {
                    Vector3 c = V(b["center"]), size = V(b["size"]);
                    float bottom = c.y - size.y * .5f;
                    if (role == "Pallet")
                    {
                        float first = levelHeights.Count > 0 ? levelHeights.Min : bottom + .15f;
                        float h = Mathf.Max(.06f, first - bottom - .25f);
                        Box("pallet_base", root, new Vector3(c.x, bottom + h * .5f, c.z), Quaternion.identity, new Vector3(size.x, h, size.z), pallet);
                    }
                    else if (role == "Bench")
                    {
                        foreach (float y in levelHeights)
                            Box("shelf_plate", root, new Vector3(c.x, y - .27f, c.z), Quaternion.identity, new Vector3(size.x, .04f, size.z), plate);
                        foreach (float dx in new[] { -.5f, .5f })
                        foreach (float dz in new[] { -.5f, .5f })
                            Box("upright", root, new Vector3(c.x + dx * (size.x - .05f), c.y, c.z + dz * (size.z - .05f)), Quaternion.identity,
                                new Vector3(.05f, size.y, .05f), bench);
                    }
                    else
                        Box("depot_base", root, new Vector3(c.x, bottom + .05f, c.z), Quaternion.identity, new Vector3(size.x, .1f, size.z), pallet);
                }
                if (s["station"] is JObject st)
                {
                    var marker = root.gameObject.AddComponent<RoboFactory.Logistics.Station>();
                    marker.useOverride = st["useOverride"].Value<bool>();
                    if (Enum.TryParse(S(st["overrideRole"]), out RoboFactory.Logistics.StationRole r)) marker.overrideRole = r;
                }
                root.gameObject.SetActive(s["active"] == null || s["active"].Value<bool>());
            }
        }

        private static void BuildBox(JObject box, Transform point)
        {
            string type = S(box["type"]) ?? "C";
            string name = type == "A" ? "wooden_box_point" : type == "B" ? "cardboard_box_big_point" : "cardboard_box_point";
            Color color = type == "A" ? new Color(.62f, .43f, .22f) : type == "B" ? new Color(.78f, .62f, .38f) : new Color(.86f, .74f, .52f);
            Transform root = Node(name, point, V(box["pos"]), Q(box["rot"]));
            var cargo = root.gameObject.AddComponent<CargoBox>();
            cargo.boxType = type;
            cargo.orderId = box["orderId"] != null ? box["orderId"].Value<int>() : -1;
            if (box["bounds"] is JObject b)
            {
                Vector3 size = V(b["size"]);
                // local size: the bounds are world-aligned, the box may be rotated by 90°
                Vector3 local = root.InverseTransformVector(size);
                local = new Vector3(Mathf.Abs(local.x), Mathf.Abs(local.y), Mathf.Abs(local.z)) * .96f;
                GameObject v = Box("box_" + type, root, V(b["center"]), root.rotation, Vector3.one, Mat("sim_box_" + type, color, .15f));
                v.transform.localScale = local;
                // identification stripes: A one stripe, B two, C none
                Material tape = Mat("sim_box_tape", new Color(.12f, .12f, .12f), .1f);
                int stripes = type == "A" ? 1 : type == "B" ? 2 : 0;
                for (int i = 0; i < stripes; i++)
                {
                    GameObject st = Box("tape", v.transform, v.transform.position + root.up * (i == 0 ? .1f : -.1f) * local.y, root.rotation, Vector3.one, tape);
                    st.transform.localScale = new Vector3(1.01f, .08f, 1.01f);
                }
            }
        }

        private static void BuildZones(JObject layout)
        {
            foreach (JObject z in (JArray)layout["zones"])
            {
                string path = S(z["path"]);
                Transform parent = ParentOf(path) ?? Group("Zones");
                Transform root = Node(S(z["name"]), parent, V(z["pos"]), Q(z["rot"]));
                built[path] = root;
                var zone = root.gameObject.AddComponent<ShelfInteractionZone>();
                string shelf = S(z["shelf"]);
                if (shelf != null && storageByPath.TryGetValue(shelf, out ShelfStorage storage)) zone.shelf = storage;
                zone.columnIndex = z["columnIndex"].Value<int>();
                zone.accessSide = S(z["accessSide"]) == "Back" ? ShelfInteractionZone.AccessSide.Back : ShelfInteractionZone.AccessSide.Front;
                zone.positionTolerance = z["positionTolerance"].Value<float>();
                zone.angleTolerance = z["angleTolerance"].Value<float>();
                if (z["dock"] is JObject d)
                {
                    string dockPath = S(d["path"]);
                    string dockName = dockPath != null && dockPath.Contains("/") ? dockPath.Substring(dockPath.LastIndexOf('/') + 1) : "dock";
                    Transform dockParent = dockPath != null ? ParentOf(dockPath) ?? root : root;
                    zone.dockPoint = Node(dockName, dockParent, V(d["pos"]), Q(d["rot"]));
                }
                foreach (JObject c in (JArray)z["colliders"])
                {
                    var bc = root.gameObject.AddComponent<BoxCollider>();
                    bc.isTrigger = c["trigger"].Value<bool>();
                    bc.center = root.InverseTransformPoint(V(c["center"]));
                    bc.size = V(c["size"]);
                }
                root.gameObject.SetActive(z["active"] == null || z["active"].Value<bool>());
            }
        }

        private static void BuildObstacles(JObject layout)
        {
            Material env = Mat("sim_env", new Color(.58f, .6f, .63f), .3f);
            Material person = Mat("sim_person", new Color(.95f, .55f, .1f), .3f);
            int i = 0;
            foreach (JObject o in (JArray)layout["obstacles"])
            {
                string path = S(o["path"]) ?? "";
                if (path.Contains("/truck_walls/")) continue; // recreated by TruckBay at start
                string owner = S(o["owner"]) ?? "env";
                Transform parent;
                if (owner.StartsWith("truck:")) built.TryGetValue(owner.Substring(6), out parent);
                else if (owner.StartsWith("storage:")) built.TryGetValue(owner.Substring(8), out parent);
                else parent = null;
                if (parent == null) parent = Group("Environment");
                bool worker = path.StartsWith("Worker");
                bool onStation = owner.StartsWith("storage:") || owner.StartsWith("truck:");
                GameObject go = Box("obstacle_" + (i++) + "_" + path.Substring(path.LastIndexOf('/') + 1), parent,
                                    V(o["center"]), Q(o["rot"]), V(o["size"]), worker ? person : env, true, false);
                // The stations' colliders stay invisible: their visual is already built.
                if (onStation) UnityEngine.Object.DestroyImmediate(go.GetComponent<MeshRenderer>());
                string layer = S(o["layer"]);
                int index = string.IsNullOrEmpty(layer) ? -1 : LayerMask.NameToLayer(layer);
                if (index >= 0) go.layer = index;
            }
        }

        private static void BuildRobots(JObject layout)
        {
            foreach (JObject r in (JArray)layout["robots"])
            {
                string role = S(r["role"]);
                Transform root = Node(S(r["name"]), null, V(r["pos"]), Q(r["rot"]));
                built[S(r["path"])] = root;
                Vector3 size = r["bounds"] is JObject b ? V(b["size"]) : new Vector3(.5f, .5f, .5f);
                Color body = role == "R1" ? new Color(.16f, .45f, .95f) : new Color(.98f, .55f, .1f);
                GameObject chassis = Box("chassis", root, root.position + Vector3.up * (size.y * .5f + .04f), root.rotation,
                                         new Vector3(size.x * .82f, size.y * .62f, size.z * .9f), Mat("sim_robot_" + role, body, .5f, .2f));
                Box("top_plate", root, root.position + Vector3.up * (size.y * .86f), root.rotation,
                    new Vector3(size.x * .7f, .03f, size.z * .78f), Mat("sim_robot_top", new Color(.15f, .16f, .18f), .4f, .5f));
                Box("eyes", root, root.position + Vector3.up * (size.y * .6f) + root.forward * (size.z * .455f), root.rotation,
                    new Vector3(size.x * .5f, .06f, .01f), Mat("sim_robot_eyes", new Color(.4f, 1f, .95f), .9f));

                // 2 wheels per side, axis = robot's local X
                Material wheelMat = Mat("sim_wheel", new Color(.08f, .08f, .09f), .2f);
                var left = new List<Transform>();
                var right = new List<Transform>();
                const float radius = .08f;
                foreach (float side in new[] { -1f, 1f })
                foreach (float dz in new[] { -.15f, .15f })
                {
                    GameObject w = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    UnityEngine.Object.DestroyImmediate(w.GetComponent<Collider>());
                    w.name = side < 0 ? "wheel_left" : "wheel_right";
                    w.transform.SetParent(root, false);
                    w.transform.localPosition = new Vector3(side * size.x * .46f, radius, dz);
                    w.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
                    w.transform.localScale = new Vector3(radius * 2f, .03f, radius * 2f);
                    w.GetComponent<MeshRenderer>().sharedMaterial = wheelMat;
                    (side < 0 ? left : right).Add(w.transform);
                }

                // like the original robot: trigger volume + Rigidbody without gravity (docking zones)
                var col = root.gameObject.AddComponent<BoxCollider>();
                col.isTrigger = true;
                col.center = new Vector3(0f, size.y * .5f, 0f);
                col.size = size;
                var rb = root.gameObject.AddComponent<Rigidbody>();
                rb.isKinematic = false;
                rb.useGravity = false;

                if (r["cargoAttachLocal"] != null)
                {
                    var attach = new GameObject("cargo_attach").transform;
                    attach.SetParent(root, false);
                    attach.localPosition = V(r["cargoAttachLocal"]);
                }

                var tracks = root.gameObject.AddComponent<DualProceduralTracksFromWheels>();
                tracks.leftWheels = left.ToArray();
                tracks.rightWheels = right.ToArray();
                tracks.wheelRadius = radius;
                tracks.spinAxis = Vector3.up; // cylinder axis
                var grid = root.gameObject.AddComponent<GridRobotController>();
                JObject g = (JObject)r["grid"];
                grid.cellSize = g["cellSize"].Value<float>();
                grid.gridCellsX = g["gridCellsX"].Value<int>();
                grid.gridCellsZ = g["gridCellsZ"].Value<int>();
                grid.trackWidth = g["trackWidth"].Value<float>();
                grid.tracks = tracks;
                grid.enabled = r["enabled"] == null || r["enabled"].Value<bool>();

                foreach (JProperty p in ((JObject)r["components"]).Properties())
                {
                    if (!(p.Value is JObject)) continue; // third-party component: replaced above
                    Type type = FindType(p.Name);
                    if (type == null) { Debug.LogWarning("[Sim] type not found: " + p.Name); continue; }
                    Component c = root.gameObject.AddComponent(type);
                    ApplySettings(c, p.Value);
                }
            }
        }

        private static void BuildSceneComponents(JObject layout)
        {
            foreach (JObject c in (JArray)layout["components"])
            {
                string path = S(c["path"]);
                if (!built.TryGetValue(path, out Transform host))
                {
                    JObject pose = (JObject)c["pose"];
                    host = Node(S(pose["name"]), ParentOf(path), V(pose["pos"]), Q(pose["rot"]));
                    built[path] = host;
                }
                Type type = FindType(S(c["type"]));
                if (type == null) { Debug.LogWarning("[Sim] type not found: " + S(c["type"])); continue; }
                if (c["colliders"] is JArray cols)
                    foreach (JObject o in cols)
                    {
                        var bc = host.gameObject.AddComponent<BoxCollider>();
                        bc.isTrigger = o["trigger"].Value<bool>();
                        bc.center = host.InverseTransformPoint(V(o["center"]));
                        bc.size = V(o["size"]);
                    }
                Component comp = host.GetComponent(type) ?? host.gameObject.AddComponent(type);
                ApplySettings(comp, c["json"]);
                if (comp is Behaviour behaviour && c["enabled"] != null) behaviour.enabled = c["enabled"].Value<bool>();
            }
        }

        private static void BuildCamera(JObject layout)
        {
            JObject c = layout["camera"] as JObject;
            var go = new GameObject("Main Camera");
            go.tag = "MainCamera";
            var cam = go.AddComponent<Camera>();
            go.AddComponent<AudioListener>();
            if (c != null)
            {
                go.transform.SetPositionAndRotation(V(c["pos"]), Q(c["rot"]));
                cam.fieldOfView = c["fov"].Value<float>();
                cam.nearClipPlane = c["near"].Value<float>();
                cam.farClipPlane = c["far"].Value<float>();
            }
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(.11f, .12f, .14f);
            var ctrl = go.AddComponent<WarehouseCameraController>();
            ctrl.targetCamera = cam;
        }
    }
}
