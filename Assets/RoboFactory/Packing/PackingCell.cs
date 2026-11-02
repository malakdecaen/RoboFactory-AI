using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using RoboFactory.Hackathon.Meta;
using UnityEngine;
using RoboFactory.Sim;

namespace RoboFactory.Packing
{
    /// <summary>
    /// Packing cell, upstream of the warehouse logistics.
    ///
    /// Small products come out of the black box and ride the conveyor loop. Before the arm, a scan camera looks
    /// at each product and Muse Spark says what it is and whether it has a visible defect (/inspect). From that
    /// answer only, the cell decides:
    ///   - defect          → the arm drops it in the grey reject bin;
    ///   - good and needed → the arm puts it in the carton being filled on a tray;
    ///   - good, not needed (or no answer) → it rides another lap (after a few laps it goes back into the black box).
    /// The belt stops at the pick point while the arm takes a product (indexing conveyor). A full carton is
    /// closed, given a destination and a label, and waits on its tray for the transport robot.
    /// The simulation's own truth (TrueKind / TrueDefect) is only logged afterwards to measure the scan, never
    /// used to decide.
    /// </summary>
    public sealed class PackingCell : MonoBehaviour
    {
        public static PackingCell Instance { get; private set; }

        [Header("Conveyor (world, order of travel)")]
        public Vector3[] Corners = new Vector3[0];
        public float CornerRadius = .38f;
        public float Speed = .7f;
        public Vector3 SpawnPoint;
        public Vector3 ScanPoint;
        public Vector3 PickPoint;

        [Header("Cartons on the trays (one open carton per tray, each with its recipe)")]
        public Vector3[] TraySlots = new Vector3[0];
        public float TrayYaw;
        [Tooltip("Used only when no box of that type exists in the scene to copy.")]
        public Vector3 CartonSize = new Vector3(.32f, .24f, .42f);
        /// <summary>One carton per tray; the type letter is the warehouse box type robot R1 handles (A wooden crate, B big cardboard, C medium cardboard).</summary>
        public CartonRecipe[] TrayRecipes =
        {
            CartonRecipe.Of("A", 4, "4 bottles", ItemKind.Bottle),
            CartonRecipe.Of("B", 4, "4 mugs", ItemKind.Mug),
            CartonRecipe.Of("C", 4, "4 balls", ItemKind.Ball),
        };
        [Tooltip("Until the transport robot takes the closed cartons: a closed carton leaves its tray after a few seconds.")]
        public bool AutoClearClosedCartons = true;
        public float AutoClearDelay = 6f;

        [Header("Reject bin")]
        public Vector3 BinDrop;
        public float BinFloorY = .3f;
        public float BinRadius = .45f;

        [Header("Products")]
        public int Seed = 2026;
        [Range(0f, 1f)] public float DefectRate = .3f;
        public int MaxOnBelt = 7;
        public float SpawnEvery = 2.2f;
        public int MaxLaps = 3;
        public float MinRejectConfidence = .5f;

        [Header("Scan camera")]
        public float ScanHeight = .55f;
        public float ScanFov = 34f;
        public int ScanResolution = 256;

        public string ProxyUrl = "http://127.0.0.1:8765";
        public bool BuildScanStation = true;

        // ---- runtime state (read by the HUD)
        public ConveyorTrack Track { get; private set; }
        public IPackingArm Arm { get; private set; }
        public readonly PackingCarton[] Trays = new PackingCarton[8];
        public bool ArmBusy { get; private set; }
        public string ArmActivity { get; private set; } = "idle";
        public PackingItem LastScanned { get; private set; }
        public Texture LastScanImage => scanTarget;
        public int Spawned, Scans, ScanFailures, Taken, Rejected, Recycled, CartonsClosed;
        public int ScanCorrect, ScanWrong;   // checked afterwards against the simulation (logs only)

        private System.Random itemRng;
        private MetaProxyClient proxy;
        private float spawnS, scanS, pickS, nextSpawn;
        private readonly List<int> reachableTrays = new List<int>();
        private Camera scanCam;
        private RenderTexture scanTarget;
        private Transform itemsRoot, binRoot;
        private int cartonNumber;
        private ConveyorTrack.Rider waiting;

        /// <summary>Arm provided by the scene (IK arm of the full project) or built by code (public version).</summary>
        public Func<PackingCell, IPackingArm> ArmFactory;

        private void Awake() { Instance = this; }
        private void OnDestroy() { if (Instance == this) Instance = null; if (scanTarget != null) scanTarget.Release(); }

        private IEnumerator Start()
        {
            yield return null;   // let the scene finish its own setup (IK arm, robots)
            itemRng = new System.Random(Seed);
            proxy = new MetaProxyClient { BaseUrl = ProxyUrl };
            Track = new ConveyorTrack(Corners, CornerRadius) { Speed = Speed };
            spawnS = Track.Project(SpawnPoint);
            scanS = Track.Project(ScanPoint);
            pickS = Track.Project(PickPoint);
            itemsRoot = new GameObject("Products").transform;
            itemsRoot.SetParent(transform, false);
            binRoot = new GameObject("Reject bin content").transform;
            binRoot.SetParent(transform, false);
            Arm = ArmFactory != null ? ArmFactory(this) : IkTargetArm.Find();
            if (Arm == null) { Debug.LogWarning("[PACK] no arm found: packing cell disabled"); enabled = false; yield break; }
            if (Arm is IkTargetArm ik) Debug.Log("[PACK] " + ik.Description);
            for (int i = 0; i < TraySlots.Length && i < Trays.Length; i++)
                if (Arm.CanReach(TraySlots[i] + Vector3.up * (CartonSize.y + .25f))) reachableTrays.Add(i);
            if (reachableTrays.Count == 0) for (int i = 0; i < Mathf.Min(TraySlots.Length, Trays.Length); i++) reachableTrays.Add(i);
            BuildScanCamera();
            FindBoxTemplates();
            foreach (int i in reachableTrays) if (i < TrayRecipes.Length) NewCarton(i);
            Debug.Log("[PACK] cell ready: loop " + Track.Length.ToString("F1", CultureInfo.InvariantCulture) + " m, scan at " +
                      Track.Ahead(spawnS, scanS).ToString("F1", CultureInfo.InvariantCulture) + " m, pick at " +
                      Track.Ahead(spawnS, pickS).ToString("F1", CultureInfo.InvariantCulture) + " m, trays reachable " + string.Join(",", reachableTrays) +
                      ", seed " + Seed);
        }

        // ------------------------------------------------------------------ main loop

        private void Update()
        {
            if (Track == null || Arm == null) return;
            float dt = Time.deltaTime;
            Arm.Tick(dt);
            Spawn();

            var before = new Dictionary<PackingItem, float>();
            foreach (ConveyorTrack.Rider r in Track.Riders) before[r.Item] = r.S;
            ConveyorTrack.Rider stopped = Track.Step(dt, pickS, r => r.Item != null && r.Item.Decision != "pass");
            if (stopped != null) waiting = stopped;

            // crossings: scan point (scan / re-decide) and spawn point (lap counter, recycling)
            var recycle = new List<ConveyorTrack.Rider>();
            foreach (ConveyorTrack.Rider r in Track.Riders)
            {
                if (r.Item == null || !before.TryGetValue(r.Item, out float s0)) continue;
                float moved = Track.Ahead(s0, r.S);
                if (moved <= 0f || moved > 1f) continue;
                if (Track.Ahead(s0, scanS) <= moved) OnScanPoint(r.Item);
                if (Track.Ahead(s0, spawnS) <= moved)
                {
                    r.Item.Laps++;
                    if (r.Item.Laps >= MaxLaps && r.Item.Decision == "pass") recycle.Add(r);
                }
                if (Track.Ahead(s0, pickS) <= moved && r.Item.Decision == "pass") r.Item.Decision = "";   // re-decided next lap
            }
            foreach (ConveyorTrack.Rider r in recycle)
            {
                Recycled++;
                Debug.Log("[PACK] product #" + r.Item.Serial + " (" + Seen(r.Item) + ") back into the black box after " + r.Item.Laps + " laps");
                Track.Remove(r.Item);
                Destroy(r.Item.gameObject);
            }

            // product waiting at the pick point
            if (waiting != null && !ArmBusy)
            {
                PackingItem it = waiting.Item;
                // the carton chosen at the scan may have been filled meanwhile: choose again, or let it pass
                if (it != null && it.Decision == "take" && Choose(it) == null)
                {
                    it.Decision = "pass";
                    Debug.Log("[PACK] product #" + it.Serial + " (" + it.ScanKind + ") → PASS · no carton needs it any more");
                }
                if (it == null) { waiting = null; Track.Running = true; }
                else if (it.Decision == "take" || it.Decision == "reject") StartCoroutine(PickCycle(waiting));
                else if (it.Decision == "pass") { waiting = null; Track.Running = true; }
                // "" = the scan answer has not arrived yet: the belt waits
            }
        }

        private void LateUpdate() { Arm?.LateTick(); }

        private void Spawn()
        {
            if (Time.time < nextSpawn || Track.Riders.Count >= MaxOnBelt || !Track.Running) return;
            if (!Track.IsFree(spawnS, Track.MinGap + .1f)) return;
            nextSpawn = Time.time + SpawnEvery;
            PackingItem item = PackingItemFactory.Create(++Spawned, itemRng, DefectRate, itemsRoot);
            Track.Add(item, spawnS);
        }

        // ------------------------------------------------------------------ scan + decision

        private void OnScanPoint(PackingItem item)
        {
            if (item.Scanning) return;
            if (item.Scanned && !item.ScanFailed) { Decide(item); return; }
            StartCoroutine(Scan(item));
        }

        private IEnumerator Scan(PackingItem item)
        {
            item.Scanning = true;
            item.ScanFailed = false;
            item.Decision = "";
            Scans++;
            scanCam.enabled = true;
            yield return new WaitForEndOfFrame();
            string png = ReadScan();
            scanCam.enabled = false;
            if (png == null) { item.Scanning = false; item.ScanFailed = true; ScanFailures++; Decide(item); yield break; }
            InspectResponse res = null;
            yield return proxy.AskInspect(png, "item#" + item.Serial, "pack-" + item.Serial + "-" + item.Laps, x => res = x);
            if (item == null) yield break;
            item.Scanning = false;
            item.Scanned = true;
            LastScanned = item;
            if (res == null || !res.ok)
            {
                item.ScanFailed = true;
                ScanFailures++;
                Debug.Log("[PACK] scan product #" + item.Serial + ": no answer" + (res != null && !string.IsNullOrEmpty(res.error) ? " (" + res.error + ")" : "") +
                          " · it rides another lap");
            }
            else
            {
                item.ScanKind = res.kind ?? "";
                item.ScanDefect = res.defect ?? "";
                item.ScanConfidence = res.confidence;
                item.ScanModel = res.model ?? "";
                item.ScanReason = res.reason ?? "";
                bool kindOk = item.ScanKind == PackingItem.KindName(item.TrueKind);
                bool defectOk = (item.ScanDefect == "none") == (item.TrueDefect == ItemDefect.None);
                if (kindOk && defectOk) ScanCorrect++; else ScanWrong++;
                Debug.Log("[PACK] scan product #" + item.Serial + ": " + item.ScanKind + ", defect " + item.ScanDefect + " (" +
                          item.ScanConfidence.ToString("F2", CultureInfo.InvariantCulture) + ") · " + item.ScanModel + " " +
                          (res.latency_ms / 1000f).ToString("F1", CultureInfo.InvariantCulture) + " s" + (res.recorded || res.mode == "recorded" ? " · recorded" : "") +
                          " · truth " + PackingItem.KindName(item.TrueKind) + "/" + PackingItem.DefectName(item.TrueDefect) + (kindOk && defectOk ? " OK" : " MISMATCH") +
                          (item.ScanReason.Length > 0 ? " · « " + item.ScanReason + " »" : ""));
            }
            Decide(item);
        }

        /// <summary>Decision from the scan answer only.</summary>
        private void Decide(PackingItem item)
        {
            string why;
            if (item.ScanFailed) { item.Decision = "pass"; item.Scanned = false; why = "no scan answer, rescan next lap"; }
            else if (item.ScanDefect != "none" && item.ScanConfidence >= MinRejectConfidence) { item.Decision = "reject"; why = "defect " + item.ScanDefect; }
            else if ((item.ScanKind == "unknown" || item.ScanConfidence < MinRejectConfidence) && item.Laps < 1)
            { item.Decision = "pass"; item.Scanned = false; why = "uncertain (" + item.ScanKind + ", " + item.ScanConfidence.ToString("F2", CultureInfo.InvariantCulture) + "), rescan next lap"; }
            else if (item.ScanKind == "unknown") { item.Decision = "reject"; why = "still unknown after a second scan"; }
            else if (item.ScanDefect != "none") { item.Decision = "pass"; item.Scanned = false; why = "uncertain defect, rescan next lap"; }
            else if (Choose(item) is PackingCarton target)
            { item.Decision = "take"; why = "for carton " + target.Type + "#" + target.Number + " (" + target.Recipe.Description + ", " + target.Contents.Count + "/" + target.Capacity + ")"; }
            else { item.Decision = "pass"; why = "no open carton needs a " + item.ScanKind + " now"; }
            Debug.Log("[PACK] product #" + item.Serial + " (" + Seen(item) + ") → " + item.Decision.ToUpperInvariant() + " · " + why);
        }

        private static string Seen(PackingItem i) => i.Scanned && !i.ScanFailed ? i.ScanKind + (i.ScanDefect != "none" && i.ScanDefect.Length > 0 ? ", " + i.ScanDefect : "") : "not scanned";

        // ------------------------------------------------------------------ arm cycle

        private IEnumerator PickCycle(ConveyorTrack.Rider rider)
        {
            ArmBusy = true;
            PackingItem it = rider.Item;
            string decision = it.Decision;
            float h = Height(it);
            Vector3 p = it.transform.position;
            ArmActivity = decision == "take" ? "picking #" + it.Serial : "removing #" + it.Serial;
            yield return Move(p + Vector3.up * (h + .22f));
            yield return Move(p + Vector3.up * (h + .004f));
            it.transform.SetParent(Arm.Grip, true);
            Track.Remove(it);
            waiting = null;
            yield return Move(p + Vector3.up * (h + .28f));
            Track.Running = true;

            PackingCarton carton = decision == "take" ? Choose(it) : null;
            bool take = carton != null;
            if (take)
            {
                carton.Incoming++;
                ArmActivity = "packing #" + it.Serial + " in carton " + carton.Type + "#" + carton.Number;
                Vector3 slot = carton.SlotWorld(carton.Contents.Count + carton.Incoming - 1);
                yield return Move(slot + Vector3.up * (h + carton.Size.y + .12f));
                yield return Move(slot + Vector3.up * (h + .015f));
                carton.Incoming--;
                carton.Put(it);
                Taken++;
                Debug.Log("[PACK] product #" + it.Serial + " (" + it.ScanKind + ") packed in carton " + carton.Type + "#" + carton.Number +
                          " on tray " + (carton.TraySlot + 1) + " (" + carton.Contents.Count + "/" + carton.Capacity + ")");
                yield return Move(slot + Vector3.up * (h + carton.Size.y + .12f));
                if (carton.Full) StartCoroutine(CompleteCarton(carton));
            }
            else
            {
                ArmActivity = "rejecting #" + it.Serial;
                yield return Move(BinDrop + Vector3.up * (h + .05f));
                it.transform.SetParent(binRoot, true);
                Rejected++;
                Debug.Log("[PACK] product #" + it.Serial + " (" + Seen(it) + ") dropped in the reject bin" +
                          (decision == "take" ? " (carton no longer needs it)" : ""));
                StartCoroutine(Fall(it.transform));
            }
            ArmActivity = "returning";
            yield return Move(Arm.Home);
            ArmActivity = "idle";
            ArmBusy = false;
        }

        private IEnumerator Move(Vector3 tip)
        {
            Arm.Goal = tip;
            float timeout = Time.time + 6f;
            while (!Arm.AtGoal && Time.time < timeout) yield return null;
        }

        private IEnumerator Fall(Transform t)
        {
            Vector2 r = UnityEngine.Random.insideUnitCircle * BinRadius * .6f;
            Vector3 to = new Vector3(BinDrop.x + r.x, BinFloorY, BinDrop.z + r.y);
            Vector3 from = t.position;
            float v = 0f, y = from.y;
            while (y > to.y)
            {
                v += 9.81f * Time.deltaTime;
                y = Mathf.Max(to.y, y - v * Time.deltaTime);
                float k = Mathf.InverseLerp(from.y, to.y, y);
                t.position = new Vector3(Mathf.Lerp(from.x, to.x, k), y, Mathf.Lerp(from.z, to.z, k));
                yield return null;
            }
            t.rotation = Quaternion.Euler(UnityEngine.Random.Range(-25f, 25f), UnityEngine.Random.Range(0f, 360f), UnityEngine.Random.Range(-25f, 25f));
            // the bin keeps the last 20 rejects
            if (binRoot.childCount > 20) Destroy(binRoot.GetChild(0).gameObject);
        }

        private static float Height(PackingItem it)
        {
            float top = it.transform.position.y;
            foreach (Renderer r in it.GetComponentsInChildren<Renderer>()) top = Mathf.Max(top, r.bounds.max.y);
            return Mathf.Max(.03f, top - it.transform.position.y);
        }

        // ------------------------------------------------------------------ cartons

        /// <summary>
        /// The arm's choice for a scanned product: among the open cartons that accept its kind and still have room,
        /// the one closest to completion (it ships sooner); null = none needs it.
        /// </summary>
        public PackingCarton Choose(PackingItem item)
        {
            if (item == null || !PackingItem.TryParseKind(item.ScanKind, out ItemKind kind)) return null;
            PackingCarton best = null;
            foreach (int i in reachableTrays)
            {
                PackingCarton c = Trays[i];
                if (c == null || !c.Needs(kind)) continue;
                if (best == null || c.Contents.Count + c.Incoming > best.Contents.Count + best.Incoming) best = c;
            }
            return best;
        }

        public IEnumerable<PackingCarton> OpenCartons()
        {
            foreach (int i in reachableTrays)
                if (Trays[i] != null && Trays[i].State == PackingCarton.CartonState.Open) yield return Trays[i];
        }

        /// <summary>Puts a new empty carton of the tray's recipe on tray <paramref name="tray"/>.</summary>
        public PackingCarton NewCarton(int tray)
        {
            if (tray < 0 || tray >= TraySlots.Length || tray >= Trays.Length || tray >= TrayRecipes.Length) return null;
            if (Trays[tray] != null && Trays[tray].State != PackingCarton.CartonState.Taken) return null;
            CartonRecipe recipe = TrayRecipes[tray];
            Vector3 size = CartonSize;
            Quaternion boxRot = Quaternion.identity;
            Vector3 boxCentre = Vector3.zero;
            bool real = templates.TryGetValue(recipe.Type, out CargoBox template) && template != null;
            if (real)
            {
                // open carton with the footprint of R1's box (long side along the tray)
                Vector3 s = templateSize[recipe.Type];
                boxRot = s.x > s.z ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.identity;
                size = new Vector3(Mathf.Min(s.x, s.z), s.y, Mathf.Max(s.x, s.z));
                boxCentre = templateCentre[recipe.Type];
            }
            Quaternion trayRot = Quaternion.Euler(0f, TrayYaw, 0f);
            PackingCarton c = PackingCarton.Create(++cartonNumber, TraySlots[tray], trayRot, size, recipe, transform);
            c.TraySlot = tray;
            if (real)
            {
                GameObject box = Instantiate(template.gameObject);
                box.name = template.gameObject.name + " (packed " + recipe.Type + "#" + c.Number + ")";
                box.transform.localScale = template.transform.lossyScale;
                Quaternion rot = trayRot * boxRot;
                // bottom centre of the box on the tray (offsets in the box axes, metres)
                Vector3 bottom = new Vector3(boxCentre.x, boxCentre.y - templateSize[recipe.Type].y / 2f, boxCentre.z);
                box.transform.SetPositionAndRotation(TraySlots[tray] - rot * bottom, rot);
                box.transform.SetParent(c.transform, true);
                foreach (Rigidbody rb in box.GetComponentsInChildren<Rigidbody>()) rb.isKinematic = true;
                CargoBox cargo = box.GetComponent<CargoBox>();
                if (cargo != null) { cargo.isCarried = false; cargo.orderId = -1; }
                box.SetActive(false);
                c.RealBox = box;
            }
            Trays[tray] = c;
            Debug.Log("[PACK] carton " + recipe.Type + "#" + c.Number + " opened on tray " + (tray + 1) + ": " + recipe.Description +
                      (real ? " (closes into a copy of " + template.gameObject.name + ")" : ""));
            return c;
        }

        private readonly Dictionary<string, CargoBox> templates = new Dictionary<string, CargoBox>();
        private readonly Dictionary<string, Vector3> templateSize = new Dictionary<string, Vector3>();
        private readonly Dictionary<string, Vector3> templateCentre = new Dictionary<string, Vector3>();

        /// <summary>One box of each type A/B/C already in the warehouse (the boxes R1 handles), measured in its own axes.</summary>
        private void FindBoxTemplates()
        {
            foreach (CargoBox b in FindObjectsByType<CargoBox>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (b == null || b.isCarried || b.transform.IsChildOf(transform)) continue;
                string type = b.TypeLetter;
                if (string.IsNullOrEmpty(type) || templates.ContainsKey(type)) continue;
                bool any = false;
                Vector3 min = Vector3.zero, max = Vector3.zero;
                foreach (Renderer r in b.GetComponentsInChildren<Renderer>())
                {
                    Bounds wb = r.bounds;
                    for (int k = 0; k < 8; k++)
                    {
                        Vector3 corner = new Vector3(k % 2 == 0 ? wb.min.x : wb.max.x, (k / 2) % 2 == 0 ? wb.min.y : wb.max.y, k / 4 == 0 ? wb.min.z : wb.max.z);
                        Vector3 l = Vector3.Scale(b.transform.InverseTransformPoint(corner), b.transform.lossyScale);
                        if (!any) { min = max = l; any = true; } else { min = Vector3.Min(min, l); max = Vector3.Max(max, l); }
                    }
                }
                if (!any) continue;
                templates[type] = b;
                templateSize[type] = max - min;
                templateCentre[type] = (min + max) / 2f;   // box axes, metres (scale applied)
                Debug.Log("[PACK] box type " + type + " = " + b.gameObject.name + " (" + (max - min).ToString("F2") + " m)");
            }
        }

        /// <summary>Called by the transport robot when it takes a carton away: an empty carton of the same recipe replaces it.</summary>
        public void CartonTaken(PackingCarton c)
        {
            if (c == null) return;
            c.State = PackingCarton.CartonState.Taken;
            int tray = c.TraySlot;
            if (tray >= 0 && Trays[tray] == c) Trays[tray] = null;
            NewCarton(tray);
        }

        /// <summary>Hook for the destination + label step (Muse Spark + Muse Image).</summary>
        public Func<PackingCarton, IEnumerator> Labeller;

        private IEnumerator CompleteCarton(PackingCarton c)
        {
            yield return c.CloseRoutine();
            CartonsClosed++;
            Debug.Log("[PACK] carton " + c.Type + "#" + c.Number + " complete and closed (" + c.Recipe.Description + ")");
            if (Labeller != null) yield return Labeller(c);
            if (AutoClearClosedCartons)
            {
                yield return new WaitForSeconds(AutoClearDelay);
                Debug.Log("[PACK] carton " + c.Type + "#" + c.Number + " leaves tray " + (c.TraySlot + 1) + " (transport robot not connected yet)");
                CartonTaken(c);
                c.gameObject.SetActive(false);
                Destroy(c.gameObject, .1f);
            }
        }

        // ------------------------------------------------------------------ scan camera

        private void BuildScanCamera()
        {
            Vector3 f;
            Vector3 p = Track.Evaluate(scanS, out f);
            var go = new GameObject("Scan Camera");
            go.transform.SetParent(transform, false);
            go.transform.position = p + Vector3.up * ScanHeight;
            go.transform.rotation = Quaternion.LookRotation(Vector3.down, f);
            scanCam = go.AddComponent<Camera>();
            scanCam.fieldOfView = ScanFov;
            scanCam.nearClipPlane = .05f;
            scanCam.farClipPlane = 5f;
            scanCam.clearFlags = CameraClearFlags.SolidColor;
            scanCam.backgroundColor = new Color(.05f, .05f, .06f);
            scanCam.allowMSAA = false;
            scanCam.depth = -70f;
            scanTarget = new RenderTexture(ScanResolution, ScanResolution, 24, RenderTextureFormat.ARGB32) { name = "Packing Scan" };
            scanTarget.Create();
            scanCam.targetTexture = scanTarget;
            scanCam.enabled = false;
            var light = new GameObject("Scan Light").AddComponent<Light>();
            light.transform.SetParent(go.transform, false);
            light.type = LightType.Spot;
            light.spotAngle = 50f;
            light.range = 2f;
            light.intensity = 3f;
            if (BuildScanStation) ScanStation.Build(transform, p, f, ScanHeight);
        }

        private string ReadScan()
        {
            RenderTexture previous = RenderTexture.active;
            Texture2D tex = null;
            try
            {
                RenderTexture.active = scanTarget;
                tex = new Texture2D(scanTarget.width, scanTarget.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, scanTarget.width, scanTarget.height), 0, 0, false);
                tex.Apply(false);
                return Convert.ToBase64String(tex.EncodeToPNG());
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PACK] scan capture failed: " + e.Message);
                return null;
            }
            finally
            {
                RenderTexture.active = previous;
                if (tex != null) Destroy(tex);
            }
        }
    }

    /// <summary>Scan gate drawn by code: two posts, a beam with the camera head and a light bar.</summary>
    public static class ScanStation
    {
        public static void Build(Transform parent, Vector3 beltPoint, Vector3 forward, float height)
        {
            var root = new GameObject("Scan Station").transform;
            root.SetParent(parent, false);
            root.position = beltPoint;
            root.rotation = Quaternion.LookRotation(forward, Vector3.up);
            Color frame = new Color(.18f, .2f, .23f), accent = new Color(.15f, .75f, .95f);
            float half = .42f, top = height + .12f;
            Box(root, new Vector3(-half, top / 2f - beltPoint.y * 0f, 0f) + Vector3.down * beltPoint.y / 2f, new Vector3(.05f, top + beltPoint.y, .05f), frame);
            Box(root, new Vector3(half, top / 2f, 0f) + Vector3.down * beltPoint.y / 2f, new Vector3(.05f, top + beltPoint.y, .05f), frame);
            Box(root, new Vector3(0f, top, 0f), new Vector3(2f * half + .05f, .06f, .08f), frame);
            Box(root, new Vector3(0f, height + .03f, 0f), new Vector3(.1f, .06f, .1f), new Color(.08f, .08f, .09f));
            Box(root, new Vector3(0f, top - .035f, .05f), new Vector3(2f * half - .1f, .012f, .012f), accent, .9f);
        }

        private static void Box(Transform parent, Vector3 local, Vector3 size, Color color, float smooth = .4f)
        {
            GameObject g = GameObject.CreatePrimitive(PrimitiveType.Cube);
            UnityEngine.Object.Destroy(g.GetComponent<Collider>());
            g.transform.SetParent(parent, false);
            g.transform.localPosition = local;
            g.transform.localScale = size;
            g.GetComponent<Renderer>().sharedMaterial = PackingItemFactory.Mat(color, smooth, .3f);
        }
    }
}
