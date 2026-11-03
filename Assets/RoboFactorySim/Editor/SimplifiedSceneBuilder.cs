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
using RoboFactory.Sim.EditorTools;

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
        private static readonly Dictionary<string, Material[]> boxMaterials = new Dictionary<string, Material[]>();
        private static Mesh cargoMesh;
        private const string TextureDir = MaterialDir + "/Textures";
        private const string CargoMeshPath = MaterialDir + "/sim_cargo_box_mesh.asset";

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
            boxMaterials.Clear();
            cargoMesh = null;

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
            BuildPackingCell();
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

        // ------------------------------------------------------------------ textured boxes

        /// <summary>
        /// Materials of a box type, in the submesh order of CargoMesh: 0 = front/back (±Z), 1 = left/right (±X),
        /// 2 = top/bottom. Cardboard (B, C): tape coming down on ±Z, label + hand hole on ±X, taped top.
        /// Wooden crate (A): stencilled sides, plain top.
        /// </summary>
        private static Material[] BoxMaterials(string type, Color color)
        {
            if (boxMaterials.TryGetValue(type, out Material[] cached)) return cached;
            char letter = type.Length > 0 ? type[0] : 'C';
            int seed = 7 + letter;
            Material[] mats;
            if (type == "A")
            {
                Material side = TexMat("sim_box_A_side", BoxTextures.Crate(color.r, color.g, color.b, false, letter, seed), .12f);
                Material top = TexMat("sim_box_A_top", BoxTextures.Crate(color.r, color.g, color.b, true, letter, seed), .12f);
                mats = new[] { side, side, top };
            }
            else
            {
                mats = new[]
                {
                    TexMat("sim_box_" + type + "_tape", BoxTextures.Cardboard(color.r, color.g, color.b, BoxTextures.CardboardFace.SideTape, letter, seed), .1f),
                    TexMat("sim_box_" + type + "_label", BoxTextures.Cardboard(color.r, color.g, color.b, BoxTextures.CardboardFace.SideLabel, letter, seed), .1f),
                    TexMat("sim_box_" + type + "_top", BoxTextures.Cardboard(color.r, color.g, color.b, BoxTextures.CardboardFace.Top, letter, seed), .1f),
                };
            }
            boxMaterials[type] = mats;
            return mats;
        }

        /// <summary>White-tinted material whose base map is a generated texture (saved as a PNG next to the materials).</summary>
        private static Material TexMat(string name, BoxTextures.Image image, float smoothness)
        {
            Directory.CreateDirectory(TextureDir);
            string path = TextureDir + "/" + name + ".png";
            var tex = new Texture2D(image.W, image.H, TextureFormat.RGBA32, false);
            tex.LoadRawTextureData(image.Rgba);
            tex.Apply(false);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            if (AssetImporter.GetAtPath(path) is TextureImporter importer)
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = true;
                importer.mipmapEnabled = true;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.anisoLevel = 4;
                importer.maxTextureSize = BoxTextures.Size;
                importer.SaveAndReimport();
            }
            Texture2D asset = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            Material mat = Mat(name, Color.white, smoothness);
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", asset);
            if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", asset);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>Unit cube (−0.5…0.5) with one UV square per face and three submeshes (±Z, ±X, ±Y).</summary>
        private static Mesh CargoMesh()
        {
            if (cargoMesh != null) return cargoMesh;
            Directory.CreateDirectory(MaterialDir);
            Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(CargoMeshPath);
            bool create = mesh == null;
            if (create) mesh = new Mesh();
            mesh.Clear();
            mesh.name = "sim_cargo_box_mesh";
            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new[] { new List<int>(), new List<int>(), new List<int>() };
            void Face(Vector3 n, Vector3 up, int sub)
            {
                // seen from outside: u = right, v = up (Unity front faces are clockwise)
                Vector3 right = Vector3.Cross(up, -n);
                Vector3 c = n * .5f;
                int i = vertices.Count;
                vertices.Add(c + (-right - up) * .5f); uvs.Add(new Vector2(0f, 0f));
                vertices.Add(c + (-right + up) * .5f); uvs.Add(new Vector2(0f, 1f));
                vertices.Add(c + (right + up) * .5f); uvs.Add(new Vector2(1f, 1f));
                vertices.Add(c + (right - up) * .5f); uvs.Add(new Vector2(1f, 0f));
                tris[sub].AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
            }
            Face(Vector3.forward, Vector3.up, 0);
            Face(Vector3.back, Vector3.up, 0);
            Face(Vector3.right, Vector3.up, 1);
            Face(Vector3.left, Vector3.up, 1);
            Face(Vector3.up, Vector3.forward, 2);
            Face(Vector3.down, Vector3.forward, 2);
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = 3;
            for (int s = 0; s < 3; s++) mesh.SetTriangles(tris[s], s);
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            if (create) AssetDatabase.CreateAsset(mesh, CargoMeshPath);
            else EditorUtility.SetDirty(mesh);
            cargoMesh = mesh;
            return mesh;
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
                        // wooden pallet (runners + deck boards), like the pallets of the packing cell
                        float first = levelHeights.Count > 0 ? levelHeights.Min : bottom + .15f;
                        float h = Mathf.Max(.06f, first - bottom - .25f);
                        Func<Color, float, float, Material> wood = (col, sm, me) =>
                            Mat("sim_pack_" + ColorUtility.ToHtmlStringRGB(col) + "_" + Mathf.RoundToInt(sm * 100f) + "_" + Mathf.RoundToInt(me * 100f), col, sm, me);
                        RoboFactory.Packing.PalletStock.BuildPalletModel(root, new Vector3(c.x, bottom, c.z), size.x, size.z, h, wood);
                    }
                    else if (role == "Bench")
                    {
                        // Bench as deep as its real frame (0.6 m, the robots' collider is 0.5 m): the bounds of the full
                        // scene are 1.75 m deep and the robots, which dock 0.625 m from the boxes, drove into the plate.
                        // Bottom plate under the first level only (the boxes sit on their level points), 4 uprights.
                        bool longX = size.x >= size.z;
                        float length = longX ? size.x : size.z, depth = .6f;
                        Vector3 plateSize = longX ? new Vector3(length, .04f, depth) : new Vector3(depth, .04f, length);
                        foreach (float y in levelHeights.Take(1))
                            Box("shelf_plate", root, new Vector3(c.x, y - .27f, c.z), Quaternion.identity, plateSize, plate);
                        float top = levelHeights.Count > 0 ? levelHeights.Max + .3f : c.y + size.y / 2f;
                        float floor = bottom;
                        foreach (float da in new[] { -.5f, .5f })
                        foreach (float dd in new[] { -.5f, .5f })
                        {
                            Vector3 o = longX ? new Vector3(da * (length - .05f), 0f, dd * (depth - .05f)) : new Vector3(dd * (depth - .05f), 0f, da * (length - .05f));
                            Box("upright", root, new Vector3(c.x + o.x, (floor + top) / 2f, c.z + o.z), Quaternion.identity,
                                new Vector3(.05f, top - floor, .05f), bench);
                        }
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
                // textured cardboard box / wooden crate (procedural textures, see BoxTextures)
                var v = new GameObject("box_" + type);
                v.transform.SetPositionAndRotation(V(b["center"]), root.rotation);
                v.transform.SetParent(root, true);
                v.transform.localScale = local;
                v.AddComponent<MeshFilter>().sharedMesh = CargoMesh();
                var renderer = v.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = BoxMaterials(type, color);
                renderer.shadowCastingMode = ShadowCastingMode.On;
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
            Material pallet = Mat("sim_pallet", new Color(.55f, .4f, .24f), .2f);
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
                if (worker) continue;   // no worker standing in the public scene
                bool onStation = owner.StartsWith("storage:") || owner.StartsWith("truck:");
                // The robots of the full project drive around every solid collider standing on the floor: the
                // colliders of the benches / pallets / trucks, the worker in the aisle, the loaded pallet on R2's way
                // to the trucks, the door frames. They are all kept so the routes (and the timings) stay the same.
                // Not built: what is above the robots (roof spacers) and the big storage rack, whose real shape is a
                // mesh: its bounding box would wrongly close the access to the pallets standing next to it.
                Vector3 c = V(o["center"]), sz = V(o["size"]);
                bool onFloor = c.y - sz.y * .5f < .5f;
                bool compact = sz.x * sz.z < 4f;
                if (!onStation && !(onFloor && compact)) continue;
                string label = path.Substring(path.LastIndexOf('/') + 1);
                // no "obstacle_*" objects any more: the worker keeps his name, the others are navigation colliders
                bool lowPallet = !onStation && !worker && sz.y < .3f;
                GameObject go = Box(worker ? label : "nav_collider_" + (i++) + "_" + label, parent, c, Q(o["rot"]), sz,
                                    worker ? person : lowPallet ? pallet : env, true, false);
                // Visible: the worker and the loaded pallet. The other colliders stay invisible (no useless cubes).
                if (!worker && !lowPallet) UnityEngine.Object.DestroyImmediate(go.GetComponent<MeshRenderer>());
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
                BuildEyes(root, size);

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
                // pick / drop with the same timing as the full project's robot (arm animation per level)
                var interactor = root.gameObject.AddComponent<RobotCargoInteractor>();
                interactor.gridController = grid;
                interactor.cargoAttachPoint = root.Find("cargo_attach");
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

        /// <summary>
        /// Face of the robot like the warehouse robot of the full project: a dark visor on the front with two round
        /// glowing eyes (pupils), which blink (SimRobotEyes).
        /// </summary>
        private static void BuildEyes(Transform root, Vector3 size)
        {
            float front = size.z * .45f, y = size.y * .6f;
            Box("visor", root, root.position + Vector3.up * y + root.forward * (front + .004f), root.rotation,
                new Vector3(size.x * .66f, size.y * .26f, .012f), Mat("sim_robot_visor", new Color(.03f, .04f, .06f), .9f, .3f));
            Material white = Mat("sim_robot_eyes", new Color(.55f, 1f, .97f), .9f);
            Material pupil = Mat("sim_robot_pupil", new Color(.02f, .05f, .08f), .9f);
            var eyes = new List<Transform>();
            foreach (float side in new[] { -1f, 1f })
            {
                Transform eye = new GameObject(side < 0 ? "eye_left" : "eye_right").transform;
                eye.SetParent(root, false);
                eye.localPosition = new Vector3(side * size.x * .15f, y, front + .012f);
                Disc("glow", eye, Vector3.zero, size.x * .17f, .008f, white);
                Disc("pupil", eye, new Vector3(0f, -.004f, .006f), size.x * .07f, .004f, pupil);
                eyes.Add(eye);
            }
            var blink = root.gameObject.AddComponent<SimRobotEyes>();
            blink.eyes = eyes.ToArray();
        }

        /// <summary>Flat disc facing the robot's front (cylinder turned on its axis).</summary>
        private static void Disc(string name, Transform parent, Vector3 local, float diameter, float depth, Material mat)
        {
            GameObject d = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            UnityEngine.Object.DestroyImmediate(d.GetComponent<Collider>());
            d.name = name;
            d.transform.SetParent(parent, false);
            d.transform.localPosition = local;
            d.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            d.transform.localScale = new Vector3(diameter, depth * .5f, diameter);
            d.GetComponent<MeshRenderer>().sharedMaterial = mat;
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

        /// <summary>
        /// Packing cell of the warehouse, with the scene: conveyor loop, black box, trays, reject bin, the arm and
        /// the two pallets on the left where R3 brings the cartons (the cell's behaviour, the scan, R3 and the stock
        /// equipment of the pallets are added when the scene starts, as in the full project).
        /// </summary>
        private static void BuildPackingCell()
        {
            Func<Color, float, float, Material> mat = (c, s, m) =>
                Mat("sim_pack_" + ColorUtility.ToHtmlStringRGB(c) + "_" + Mathf.RoundToInt(s * 100f) + "_" + Mathf.RoundToInt(m * 100f), c, s, m);
            Transform root = Group("Packing Cell Line");
            RoboFactory.Packing.PackingCellBootstrap.Dressing.Build(root, mat);
            RoboFactory.Packing.ProceduralArm.BuildModel(root, RoboFactory.Packing.PackingCellBootstrap.ArmBase, mat);
            for (int i = 0; i < RoboFactory.Packing.PalletStock.Names.Length; i++)
                RoboFactory.Packing.PalletStock.ProceduralPallet(RoboFactory.Packing.PalletStock.Names[i], RoboFactory.Packing.PalletStock.Places[i],
                                                                 RoboFactory.Packing.PalletStock.Yaws[i], mat).transform.SetParent(root, true);
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
