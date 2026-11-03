using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using RoboFactory.Hackathon.Meta;
using RoboFactory.Hackathon.Orchestration;
using RoboFactory.Packing;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using RoboFactory.Sim;

namespace RoboFactory.Logistics
{
    /// <summary>
    /// Shipping labels, the last step before the trucks.
    ///
    /// As soon as an order is planned (bench and truck chosen), Muse Spark invents its consignee (fictional name,
    /// street, town, and a short scene evoking the place), then Muse Image draws the label's vignette from that
    /// scene. The generation runs while R1 picks, so the label is ready when the boxes arrive. Each box robot R1
    /// puts on the bench gets the label on its top face ("SHIP TO", consignee, order, truck, parcel n/N, a
    /// barcode); robot R2 then loads labelled boxes. If Meta does not answer, a printed label (text only, from a
    /// small local list) is used and the line goes on. Logged as [LABEL] lines; shown in the HUD.
    /// </summary>
    public sealed class ShippingLabels : MonoBehaviour
    {
        public static ShippingLabels Instance { get; private set; }

        public string ProxyUrl = "http://127.0.0.1:8765";
        [Tooltip("A box waits this long on its bench for the Muse Image vignette, then gets the label without it (added later).")]
        public float WaitForImage = 25f;

        public sealed class Label
        {
            public int OrderId;
            public string Truck = "", Bench = "", Contents = "";
            public int Boxes;
            public string Name = "", Street = "", Postcode = "", City = "", Country = "", Scene = "";
            public string DestModel = "", ImageModel = "";
            public int DestMs, ImageMs;
            public bool DestFromMeta, ImageFromMeta;
            public Texture2D Image;
            /// <summary>destination → image → ready (or ready without image when Muse Image failed).</summary>
            public string Status = "destination";
            public float RequestedAt;
            public int Applied;
            public readonly List<Renderer> Vignettes = new List<Renderer>();
            public string Address => (Postcode.Length > 0 ? Postcode + " " : "") + City + (Country.Length > 0 ? ", " + Country : "");
        }

        public readonly Dictionary<int, Label> Labels = new Dictionary<int, Label>();
        /// <summary>Label shown in the HUD: the one last applied to a box, else the last one generated.</summary>
        public Label Latest { get; private set; }
        public int Consignees, Images, BoxesLabelled;

        private MetaProxyClient proxy;
        private MuseOrchestrator orchestrator;
        private float nextScan;
        private readonly HashSet<CargoBox> labelled = new HashSet<CargoBox>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Init()
        {
            SceneManager.sceneLoaded -= OnLoaded;
            SceneManager.sceneLoaded += OnLoaded;
            OnLoaded(SceneManager.GetActiveScene(), LoadSceneMode.Single);
        }

        private static void OnLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name != PackingCellBootstrap.FullScene && scene.name != PackingCellBootstrap.PublicScene) return;
            if (FindFirstObjectByType<ShippingLabels>() != null) return;
            var go = new GameObject("Shipping Labels");
            SceneManager.MoveGameObjectToScene(go, scene);
            go.AddComponent<ShippingLabels>();
        }

        private void Awake() { Instance = this; proxy = new MetaProxyClient { BaseUrl = ProxyUrl }; }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            foreach (Label l in Labels.Values) if (l.Image != null) Destroy(l.Image);
        }

        private void Update()
        {
            if (Time.time < nextScan) return;
            nextScan = Time.time + .4f;
            if (orchestrator == null) orchestrator = FindFirstObjectByType<MuseOrchestrator>();

            // 1. a label is prepared for every order that has a bench or a truck (generated during the picking)
            if (orchestrator != null)
                foreach (WhOrderState o in orchestrator.OrdersSnapshot)
                {
                    if (o == null || o.status == "pending" || o.status == "loaded") continue;
                    if (string.IsNullOrEmpty(o.bench_id) && string.IsNullOrEmpty(o.truck_id)) continue;
                    if (Labels.TryGetValue(o.id, out Label known))
                    {
                        if (!string.IsNullOrEmpty(o.truck_id)) known.Truck = o.truck_id;
                        if (!string.IsNullOrEmpty(o.bench_id)) known.Bench = o.bench_id;
                        continue;
                    }
                    Request(o.id, o.truck_id, o.bench_id, o.a + o.b + o.c, Contents(o));
                }

            // 2. every box put on a bench for an order gets its label
            WarehouseWorld world = WarehouseWorld.Instance;
            if (world == null) return;
            foreach (string benchId in world.BenchIds)
            {
                ShelfStorage bench = world.Bench(benchId);
                if (bench == null) continue;
                foreach (CargoBox box in bench.GetComponentsInChildren<CargoBox>())
                {
                    if (box == null || box.isCarried || box.orderId < 0 || labelled.Contains(box)) continue;
                    if (!Labels.TryGetValue(box.orderId, out Label label)) label = Request(box.orderId, "", benchId, 0, "");
                    if (label.Status == "destination") continue;   // consignee not known yet
                    if (label.Status == "image" && Time.time - label.RequestedAt < WaitForImage) continue;
                    if (label.Bench.Length == 0) label.Bench = benchId;
                    Apply(box, label);
                }
            }
            labelled.RemoveWhere(b => b == null);
        }

        private static string Contents(WhOrderState o)
        {
            var parts = new List<string>();
            if (o.a > 0) parts.Add(o.a + " wooden crate" + (o.a > 1 ? "s" : ""));
            if (o.b > 0) parts.Add(o.b + " big carton" + (o.b > 1 ? "s" : ""));
            if (o.c > 0) parts.Add(o.c + " carton" + (o.c > 1 ? "s" : ""));
            return string.Join(", ", parts);
        }

        // ------------------------------------------------------------------ generation (Muse Spark + Muse Image)

        private Label Request(int orderId, string truck, string bench, int boxes, string contents)
        {
            var label = new Label
            {
                OrderId = orderId, Truck = truck ?? "", Bench = bench ?? "", Boxes = boxes, Contents = contents ?? "",
                RequestedAt = Time.time
            };
            Labels[orderId] = label;
            Latest = label;
            StartCoroutine(Generate(label));
            return label;
        }

        private IEnumerator Generate(Label label)
        {
            string trigger = "order#" + label.OrderId;
            DestinationResponse dest = null;
            yield return proxy.AskDestination(trigger, "order #" + label.OrderId + (label.Contents.Length > 0 ? ": " + label.Contents : ""),
                                              label.Truck.Length > 0 ? label.Truck : "T01", x => dest = x);
            if (dest != null && dest.ok && !string.IsNullOrEmpty(dest.name) && !string.IsNullOrEmpty(dest.city))
            {
                label.Name = dest.name; label.Street = dest.street ?? ""; label.Postcode = dest.postcode ?? "";
                label.City = dest.city; label.Country = dest.country ?? ""; label.Scene = dest.scene ?? "";
                label.DestModel = dest.model ?? ""; label.DestMs = dest.latency_ms; label.DestFromMeta = true;
                Consignees++;
                Debug.Log("[LABEL] order #" + label.OrderId + ": consignee by " + label.DestModel + " (" + Sec(label.DestMs) + ") · " +
                          label.Name + ", " + label.Street + ", " + label.Address + (Replay(dest.mode, dest.recorded) ? " · recorded" : ""));
            }
            else
            {
                Offline(label);
                Debug.Log("[LABEL] order #" + label.OrderId + ": no consignee from Muse Spark" +
                          (dest != null && !string.IsNullOrEmpty(dest.error) ? " (" + dest.error + ")" : "") + " · printed label " + label.Name + ", " + label.City);
            }

            label.Status = "image";
            LabelResponse img = null;
            if (label.Scene.Length > 0) yield return proxy.AskLabel(trigger, label.Scene, x => img = x);
            Texture2D tex = null;
            if (img != null && img.ok && !string.IsNullOrEmpty(img.image_b64))
            {
                try
                {
                    tex = new Texture2D(2, 2, TextureFormat.RGB24, false) { name = "label order " + label.OrderId, wrapMode = TextureWrapMode.Clamp };
                    if (!tex.LoadImage(Convert.FromBase64String(img.image_b64))) { Destroy(tex); tex = null; }
                }
                catch (Exception) { if (tex != null) Destroy(tex); tex = null; }
            }
            if (tex != null)
            {
                label.Image = tex;
                label.ImageModel = img.model ?? ""; label.ImageMs = img.latency_ms; label.ImageFromMeta = true;
                Images++;
                foreach (Renderer r in label.Vignettes) if (r != null) SetTexture(r, tex);
                Debug.Log("[LABEL] order #" + label.OrderId + ": label vignette by " + label.ImageModel + " (" + Sec(label.ImageMs) + ", " +
                          tex.width + "x" + tex.height + ")" + (Replay(img.mode, img.recorded) ? " · recorded" : ""));
            }
            else
                Debug.Log("[LABEL] order #" + label.OrderId + ": no vignette from Muse Image" +
                          (img != null && !string.IsNullOrEmpty(img.error) ? " (" + img.error + ")" : "") + " · text-only label");
            label.Status = "ready";
        }

        private static bool Replay(string mode, bool recorded) => recorded || mode == "recorded";

        private static readonly string[] OfflineNames = { "Harbor & Pine Supply", "Marlow Tool Co.", "Juniper Lane Bakery", "Orchard Row Studio", "Northgate Workshop" };
        private static readonly string[] OfflineTowns = { "Northvale", "Port Ellery", "Brookmere", "Ashford Hollow", "Cedar Bay" };

        /// <summary>Printed label when Meta does not answer: invented names from a fixed list (deterministic per order).</summary>
        private static void Offline(Label l)
        {
            int i = Mathf.Abs(l.OrderId) % OfflineNames.Length;
            l.Name = OfflineNames[i];
            l.Street = (12 + 7 * l.OrderId % 80) + " Market Street";
            l.Postcode = (10000 + 1373 * l.OrderId % 89999).ToString(CultureInfo.InvariantCulture);
            l.City = OfflineTowns[(i + 2) % OfflineTowns.Length];
            l.Country = "";
            l.Scene = "";
            l.DestModel = "printed";
        }

        private static string Sec(int ms) => (ms / 1000f).ToString("F1", CultureInfo.InvariantCulture) + " s";

        // ------------------------------------------------------------------ the label on the box

        private void Apply(CargoBox box, Label label)
        {
            labelled.Add(box);
            if (box.transform.Find("shipping_label") != null) return;
            // top face of the box (its own renderers only)
            bool any = false;
            Bounds b = new Bounds(box.transform.position, Vector3.zero);
            foreach (Renderer r in box.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            if (!any) return;
            label.Applied++;
            int index = label.Applied;
            int total = Mathf.Max(label.Boxes, index);
            BoxesLabelled++;
            Latest = label;

            // footprint in the box's own axes (yaw only)
            float yaw = box.transform.eulerAngles.y;
            Quaternion flat = Quaternion.Euler(0f, yaw, 0f);
            Vector3 ex = flat * Vector3.right, ez = flat * Vector3.forward;
            float sx = Mathf.Abs(Vector3.Dot(b.size, new Vector3(Mathf.Abs(ex.x), 0f, Mathf.Abs(ex.z))));
            float sz = Mathf.Abs(Vector3.Dot(b.size, new Vector3(Mathf.Abs(ez.x), 0f, Mathf.Abs(ez.z))));
            bool along = sx >= sz;   // label along the longer side
            float w = Mathf.Min(.42f, (along ? sx : sz) * .86f), h = Mathf.Min(w * .62f, (along ? sz : sx) * .86f);
            w = Mathf.Min(w, h / .62f);
            w *= .9f;
            h *= .9f;

            var root = new GameObject("shipping_label").transform;
            root.SetParent(box.transform, true);
            root.position = new Vector3(b.center.x, b.max.y + .003f, b.center.z);
            root.rotation = Quaternion.Euler(90f, yaw + (along ? 0f : 90f), 0f);   // lying on the top, readable from above

            Quad(root, "paper", Vector3.zero, new Vector2(w, h), new Color(.97f, .96f, .92f), null);
            Quad(root, "frame", new Vector3(0f, 0f, .0004f), new Vector2(w + .008f, h + .008f), new Color(.1f, .1f, .12f), null);
            float pic = h * .62f;
            Renderer vignette = Quad(root, "vignette", new Vector3(-w / 2f + pic / 2f + h * .06f, h * .12f, -.0006f), new Vector2(pic, pic), Color.white, label.Image);
            label.Vignettes.Add(vignette);
            if (label.Image == null) SetColor(vignette, new Color(.82f, .86f, .9f));

            float tx = -w / 2f + pic + h * .12f, tw = w / 2f - tx - h * .05f;
            Text(root, "SHIP TO", new Vector2(tx, h * .40f), tw, h * .07f, true, new Color(.45f, .1f, .1f));
            Text(root, label.Name, new Vector2(tx, h * .29f), tw, h * .11f, true, Color.black);
            Text(root, label.Street, new Vector2(tx, h * .16f), tw, h * .085f, false, Color.black);
            Text(root, label.Address, new Vector2(tx, h * .05f), tw, h * .085f, false, Color.black);
            Text(root, "ORDER #" + label.OrderId + "   " + (label.Truck.Length > 0 ? "TRUCK " + label.Truck : ""), new Vector2(tx, -h * .08f), tw, h * .08f, true, new Color(.1f, .2f, .45f));
            Text(root, "PARCEL " + index + "/" + total, new Vector2(tx, -h * .19f), tw, h * .08f, true, new Color(.1f, .2f, .45f));
            // barcode along the bottom
            var rng = new System.Random(label.OrderId * 31 + index);
            float bx = -w / 2f + h * .08f, end = w / 2f - h * .08f, by = -h * .38f, bh = h * .14f;
            while (bx < end)
            {
                float bw = (.002f + .004f * (float)rng.NextDouble()) * (w / .4f);
                Quad(root, "bar", new Vector3(bx + bw / 2f, by, -.0006f), new Vector2(bw, bh), Color.black, null);
                bx += bw + (.002f + .003f * (float)rng.NextDouble()) * (w / .4f);
            }
            Debug.Log("[LABEL] order #" + label.OrderId + " parcel " + index + "/" + total + " (" + box.gameObject.name + ") labelled on bench " +
                      (label.Bench.Length > 0 ? label.Bench : "?") + " → truck " + (label.Truck.Length > 0 ? label.Truck : "?") +
                      " · " + label.Name + ", " + label.City + (label.Image != null ? "" : " · without vignette (yet)"));
        }

        private static Renderer Quad(Transform parent, string name, Vector3 local, Vector2 size, Color color, Texture tex)
        {
            GameObject q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            DestroyImmediate(q.GetComponent<Collider>());
            q.name = name;
            q.transform.SetParent(parent, false);
            q.transform.localPosition = local;
            q.transform.localScale = new Vector3(size.x, size.y, 1f);
            Renderer r = q.GetComponent<Renderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            if (tex != null) SetTexture(r, tex);
            else r.sharedMaterial = PackingItemFactory.Mat(color, .1f, 0f);
            return r;
        }

        private static void SetTexture(Renderer r, Texture tex)
        {
            var m = new Material(PackingItemFactory.Mat(Color.white, .1f, 0f));
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", tex);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
            r.sharedMaterial = m;
        }

        private static void SetColor(Renderer r, Color c) { r.sharedMaterial = PackingItemFactory.Mat(c, .1f, 0f); }

        private static void Text(Transform parent, string text, Vector2 at, float width, float height, bool bold, Color color)
        {
            var go = new GameObject("text");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(at.x + width / 2f, at.y, -.0008f);
            var t = go.AddComponent<TextMeshPro>();
            t.text = text ?? "";
            t.alignment = TextAlignmentOptions.Left;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Ellipsis;
            t.enableAutoSizing = true;
            t.fontSizeMin = .05f;
            t.fontSizeMax = height * 10f;
            t.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
            t.color = color;
            t.rectTransform.sizeDelta = new Vector2(width, height * 1.25f);
        }
    }
}
