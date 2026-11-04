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
    /// The belt never stops: the arm catches the product on the fly (it follows it while it comes down on it),
    /// and a product it misses rides another lap. A full carton is closed, given a destination and a label,
    /// and the transport robot R3 carries it to the pallets.
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
        [Tooltip("Without the transport robot: a closed carton leaves its tray after a few seconds.")]
        public bool AutoClearClosedCartons = true;
        public float AutoClearDelay = 6f;
        /// <summary>Set by the transport robot R3 when it is working: it takes the closed cartons.</summary>
        public bool TransportConnected { get; set; }

        [Header("Picking on the moving belt")]
        [Tooltip("The arm starts following a product this far before the pick point (m).")]
        public float PickLead = 1.4f;
        [Tooltip("It can still grab it this far after the pick point (m).")]
        public float PickWindow = .8f;
        public float HoverHeight = .15f;
        public float DescentSpeed = .7f;

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
        public float ScanHeight = .5f;
        [Tooltip("The camera looks at the product from upstream at this angle from the vertical (shape of the neck / handle visible).")]
        public float ScanTilt = 35f;
        public float ScanLight = 1.1f;
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
        /// <summary>Last product Muse Spark answered for (its picture and verdict are on the product).</summary>
        public PackingItem LastScanned { get; private set; }
        /// <summary>Products photographed whose answer has not arrived yet.</summary>
        public int ScansPending { get; private set; }
        public PackingItem LastCaptured { get; private set; }
        /// <summary>Time.time of the last scan picture (the HUD shows it at once, before the answer).</summary>
        public float CapturedAt { get; private set; }
        public Texture LastScanImage => scanTarget;
        public int Spawned, Scans, ScanFailures, Taken, Rejected, Recycled, CartonsClosed;
        public int ScanCorrect, ScanWrong;   // checked afterwards against the simulation (logs only)

        private System.Random itemRng;
        private MetaProxyClient proxy;
        private float spawnS, scanS, pickS, nextSpawn;
        private readonly List<int> reachableTrays = new List<int>();
        private Vector3 readyPose;
        private Camera scanCam;
        private RenderTexture scanTarget;
        private Transform itemsRoot, binRoot;
        private int cartonNumber;

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
            BeltFlow.Apply(Track, Speed);   // the belt texture runs along the products (full project)
            itemsRoot = new GameObject("Products").transform;
            itemsRoot.SetParent(transform, false);
            binRoot = new GameObject("Reject bin content").transform;
            binRoot.SetParent(transform, false);
            Arm = ArmFactory != null ? ArmFactory(this) : IkTargetArm.Find();
            if (Arm == null) { Debug.LogWarning("[PACK] no arm found: packing cell disabled"); enabled = false; yield break; }
            if (Arm is IkTargetArm ik) Debug.Log("[PACK] " + ik.Description);
            BuildScanCamera();
            FindBoxTemplates();
            var unreachable = new List<string>();
            for (int i = 0; i < TraySlots.Length && i < Trays.Length && i < TrayRecipes.Length; i++)
            {
                if (CanServe(i)) reachableTrays.Add(i);
                else unreachable.Add((i + 1) + " (" + TrayRecipes[i].Type + ")");
            }
            if (unreachable.Count > 0) Debug.Log("[PACK] the arm cannot reach over the carton on tray " + string.Join(", ", unreachable) + ": tray not used");
            if (reachableTrays.Count == 0) for (int i = 0; i < Mathf.Min(TraySlots.Length, Trays.Length); i++) reachableTrays.Add(i);
            readyPose = Track.Evaluate(Track.Wrap(pickS - .35f), out Vector3 _) + Vector3.up * .32f;
            Arm.Goal = readyPose;
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
            Track.Step(dt, -1f, null);   // the belt never stops

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

            if (!ArmBusy)
            {
                ConveyorTrack.Rider next = NextPick();
                if (next != null) StartCoroutine(PickCycle(next));
                else Arm.Goal = readyPose;
            }
        }

        /// <summary>
        /// Next product for the arm: decided TAKE or REJECT, coming to the pick point (or just past it, still
        /// within reach); the one that arrives first.
        /// </summary>
        private ConveyorTrack.Rider NextPick()
        {
            ConveyorTrack.Rider best = null;
            float bestD = float.MaxValue;
            foreach (ConveyorTrack.Rider r in Track.Riders)
            {
                PackingItem it = r.Item;
                if (it == null || it.Scanning || (it.Decision != "take" && it.Decision != "reject")) continue;
                float toPick = Track.Ahead(r.S, pickS), past = Track.Ahead(pickS, r.S);
                float d;
                if (toPick <= PickLead) d = toPick;
                else if (past <= PickWindow * .5f) d = -past;
                else continue;
                if (it.Decision == "take" && Choose(it) == null)
                {
                    it.Decision = "pass";
                    SetVerdict(it);
                    Debug.Log("[PACK] product #" + it.Serial + " (" + it.ScanKind + ") → PASS · no carton needs it any more");
                    continue;
                }
                if (d < bestD) { bestD = d; best = r; }
            }
            return best;
        }

        /// <summary>The product is past the pick zone: the arm can no longer catch it.</summary>
        private bool Gone(ConveyorTrack.Rider r)
        {
            if (r.Item == null || !Track.Riders.Contains(r)) return true;
            float past = Track.Ahead(pickS, r.S);
            return past > PickWindow && past < Track.Length * .5f;
        }

        /// <summary>The product is close enough to the pick point for the arm to come down on it.</summary>
        private bool InZone(ConveyorTrack.Rider r)
        {
            float toPick = Track.Ahead(r.S, pickS), past = Track.Ahead(pickS, r.S);
            return toPick <= .6f || past <= PickWindow;
        }

        /// <summary>Position of the product a moment ahead (the belt moves while the arm reacts).</summary>
        private Vector3 Ahead(ConveyorTrack.Rider r, float seconds)
        {
            Vector3 p = Track.Evaluate(Track.Wrap(r.S + Track.Speed * seconds), out Vector3 _);
            return new Vector3(p.x, r.Item.transform.position.y, p.z);
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

        /// <summary>A box made by the packing cell (a closed shipping carton), not one of the warehouse's initial boxes.</summary>
        public static bool IsCellBox(Component box) => box != null && box.name.Contains(" (packed ");

        /// <summary>The box is still in the cell's hands: in its carton on a tray, or carried by R3.</summary>
        public static bool HeldByCell(Component box)
        {
            if (box == null) return false;
            if (box.GetComponentInParent<PackingCarton>(true) != null) return true;
            PackingTransport r3 = PackingTransport.Current;
            return r3 != null && r3.Robot != null && box.transform.IsChildOf(r3.Robot);
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
            string png = ReadScan(out Texture2D picture);
            scanCam.enabled = false;
            if (item.ScanImage != null) Destroy(item.ScanImage);
            item.ScanImage = picture;
            LastCaptured = item;
            CapturedAt = Time.time;
            if (png == null) { item.Scanning = false; item.ScanFailed = true; ScanFailures++; Decide(item); yield break; }
            InspectResponse res = null;
            ScansPending++;
            RoboFactory.MetaServer.RecordedDemoRuntime.HintInspect("item#" + item.Serial, PackingItem.KindName(item.TrueKind), PackingItem.DefectName(item.TrueDefect));
            yield return proxy.AskInspect(png, "item#" + item.Serial, "pack-" + item.Serial + "-" + item.Laps, x => res = x);
            ScansPending--;
            if (item == null) yield break;
            item.ScanLatencyMs = res != null ? (int)res.latency_ms : 0;
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
            SetVerdict(item);
            Debug.Log("[PACK] product #" + item.Serial + " (" + Seen(item) + ") → " + item.Decision.ToUpperInvariant() + " · " + why);
        }

        /// <summary>Short verdict shown in the HUD next to the picture the scan sent.</summary>
        private void SetVerdict(PackingItem item)
        {
            string defect = (item.ScanDefect ?? "").ToUpperInvariant();
            switch (item.Decision)
            {
                case "take":
                    PackingCarton c = Choose(item);
                    item.Verdict = "OK → CARTON " + (c != null ? c.Type : "?");
                    item.VerdictKind = "ok";
                    break;
                case "reject":
                    item.Verdict = item.ScanKind == "unknown" ? "UNKNOWN → BIN" : "DEFECT: " + defect + " → BIN";
                    item.VerdictKind = "reject";
                    break;
                default:
                    if (item.ScanFailed) { item.Verdict = "NO ANSWER → RESCAN"; item.VerdictKind = "rescan"; }
                    else if (!item.Scanned) { item.Verdict = "UNSURE → RESCAN NEXT LAP"; item.VerdictKind = "rescan"; }
                    else { item.Verdict = "OK · NO CARTON NEEDS IT → NEXT LAP"; item.VerdictKind = "pass"; }
                    break;
            }
        }

        private static string Seen(PackingItem i) => i.Scanned && !i.ScanFailed ? i.ScanKind + (i.ScanDefect != "none" && i.ScanDefect.Length > 0 ? ", " + i.ScanDefect : "") : "not scanned";

        // ------------------------------------------------------------------ arm cycle

        private IEnumerator PickCycle(ConveyorTrack.Rider rider)
        {
            ArmBusy = true;
            PackingItem it = rider.Item;
            string decision = it.Decision;
            float h = Height(it);
            ArmActivity = (decision == "take" ? "catching #" : "catching (reject) #") + it.Serial;

            // 1. come above the product and follow it (the belt keeps running)
            bool above = false;
            float limit = Time.time + 6f;
            while (Time.time < limit && !Gone(rider))
            {
                Arm.Goal = Ahead(rider, Time.deltaTime) + Vector3.up * (h + HoverHeight);
                Vector3 e = Arm.ToolTip - Arm.Goal;
                // the product moves while the arm follows it: allow one frame of belt travel on top of the 3 cm
                float slack = Track.Speed * Time.deltaTime;
                if (InZone(rider) && new Vector2(e.x, e.z).magnitude < .03f + slack && Mathf.Abs(e.y) < .05f) { above = true; break; }
                yield return null;
            }
            // 2. come down on it while following it, grab it by the top
            bool caught = false;
            if (above)
            {
                float y = HoverHeight;
                limit = Time.time + 2f;
                while (Time.time < limit && !Gone(rider))
                {
                    y = Mathf.MoveTowards(y, .004f, DescentSpeed * Time.deltaTime);
                    Arm.Goal = Ahead(rider, Time.deltaTime) + Vector3.up * (h + y);
                    if (y <= .0045f && Vector3.Distance(Arm.ToolTip, Arm.Goal) < .03f + Track.Speed * Time.deltaTime) { caught = true; break; }
                    yield return null;
                }
            }
            if (!caught || it == null || !Track.Riders.Contains(rider))
            {
                if (it != null)
                {
                    Vector3 err = Arm.ToolTip - Arm.Goal;
                    Debug.Log("[PACK] product #" + it.Serial + " (" + Seen(it) + ") missed by the arm on the moving belt · next lap (" +
                              (above ? "lost it while coming down" : InZone(rider) ? "never above it" : "left the pick zone") +
                              ": tool tip " + (new Vector2(err.x, err.z).magnitude * 100f).ToString("F0", CultureInfo.InvariantCulture) + " cm across, " +
                              (err.y * 100f).ToString("F0", CultureInfo.InvariantCulture) + " cm high from its goal, " +
                              (1f / Mathf.Max(Time.smoothDeltaTime, 1e-4f)).ToString("F0", CultureInfo.InvariantCulture) + " fps)");
                }
                ArmActivity = "idle";
                ArmBusy = false;
                yield break;
            }
            it.transform.SetParent(Arm.Grip, true);
            Track.Remove(it);
            Vector3 tip = Arm.ToolTip;

            PackingCarton carton = decision == "take" ? Choose(it) : null;
            if (carton != null)
            {
                carton.Incoming++;
                ArmActivity = "packing #" + it.Serial + " in carton " + carton.Type + "#" + carton.Number;
                Vector3 slot = carton.SlotWorld(carton.Contents.Count + carton.Incoming - 1);
                // above the carton's rim with the product hanging under the gripper, in one move each way
                float safe = Mathf.Max(tip.y + .1f, carton.transform.position.y + carton.Size.y + h + .05f);
                yield return Move(new Vector3(tip.x, safe, tip.z), "lift");
                yield return Move(new Vector3(slot.x, safe, slot.z), "over carton " + carton.Type);
                yield return Move(slot + Vector3.up * (h + .015f), "into carton " + carton.Type);
                carton.Incoming--;
                carton.Put(it);
                Taken++;
                Debug.Log("[PACK] product #" + it.Serial + " (" + it.ScanKind + ") packed in carton " + carton.Type + "#" + carton.Number +
                          " on tray " + (carton.TraySlot + 1) + " (" + carton.Contents.Count + "/" + carton.Capacity + ")");
                if (carton.Full) StartCoroutine(CompleteCarton(carton));
                yield return Move(new Vector3(slot.x, safe, slot.z), "out of carton " + carton.Type);
            }
            else
            {
                ArmActivity = "rejecting #" + it.Serial;
                yield return Move(new Vector3(tip.x, Mathf.Max(tip.y + .1f, BinDrop.y + h + .05f), tip.z), "lift");
                yield return Move(BinDrop + Vector3.up * (h + .05f), "over the reject bin");
                it.transform.SetParent(binRoot, true);
                Rejected++;
                Debug.Log("[PACK] product #" + it.Serial + " (" + Seen(it) + ") dropped in the reject bin" +
                          (decision == "take" ? " (carton no longer needs it)" : ""));
                StartCoroutine(Fall(it.transform));
            }
            // straight to the next product (or the ready pose above the belt)
            ArmActivity = "idle";
            ArmBusy = false;
        }

        private IEnumerator Move(Vector3 tip, string what)
        {
            Arm.Goal = tip;
            float timeout = Time.time + 3f;
            while (!Arm.AtGoal && Time.time < timeout) yield return null;
            if (!Arm.AtGoal)
                Debug.Log("[PACK] arm: " + what + " not reached in 3 s (tool tip " + Vector3.Distance(Arm.ToolTip, tip).ToString("F2", CultureInfo.InvariantCulture) + " m away)");
        }

        /// <summary>True if the arm can bring a product over the rim of this tray's carton and down to its bottom, at every place inside.</summary>
        private bool CanServe(int tray)
        {
            CartonRecipe recipe = TrayRecipes[tray];
            Vector3 size = CartonSize;
            if (templateSize.TryGetValue(recipe.Type, out Vector3 s))
                size = new Vector3(Mathf.Min(s.x, s.z), s.y, Mathf.Max(s.x, s.z));
            const float item = .22f;   // tallest product (bottle)
            foreach (float dx in new[] { -.23f, .23f })
                foreach (float dz in new[] { -.3f, .3f })
                {
                    Vector3 p = TraySlots[tray] + Quaternion.Euler(0f, TrayYaw, 0f) * new Vector3(size.x * dx, 0f, size.z * dz);
                    if (!Arm.CanReach(p + Vector3.up * (size.y + item + .05f)) || !Arm.CanReach(p + Vector3.up * (item + .03f))) return false;
                }
            return true;
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
            c.ReadyToShip = true;
            if (AutoClearClosedCartons && !TransportConnected)
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
            // from upstream, tilted: a top-down view only shows discs, the tilt shows the neck of a bottle and the handle of a mug
            float tilt = ScanTilt * Mathf.Deg2Rad;
            Vector3 aim = p + Vector3.up * .08f;
            go.transform.position = aim - f * (Mathf.Sin(tilt) * ScanHeight) + Vector3.up * (Mathf.Cos(tilt) * ScanHeight);
            go.transform.rotation = Quaternion.LookRotation(aim - go.transform.position, f);
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
            // light from the side, not along the camera axis: no mirror reflection that whitens the top of the products
            var light = new GameObject("Scan Light").AddComponent<Light>();
            light.transform.SetParent(transform, false);
            Vector3 side = Vector3.Cross(Vector3.up, f).normalized;
            light.transform.position = aim + side * .35f + Vector3.up * .45f;
            light.transform.rotation = Quaternion.LookRotation(aim - light.transform.position, Vector3.up);
            light.type = LightType.Spot;
            light.spotAngle = 55f;
            light.range = 1.6f;
            light.intensity = ScanLight;
            if (BuildScanStation) ScanStation.Build(transform, go.transform.position - Vector3.up * go.transform.position.y + Vector3.up * p.y, f, go.transform.position.y - p.y);
        }

        /// <summary>Captures the scan camera: PNG (base64) for Muse Spark, and the same picture kept for the HUD.</summary>
        private string ReadScan(out Texture2D picture)
        {
            RenderTexture previous = RenderTexture.active;
            Texture2D tex = null;
            picture = null;
            try
            {
                RenderTexture.active = scanTarget;
                tex = new Texture2D(scanTarget.width, scanTarget.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, scanTarget.width, scanTarget.height), 0, 0, false);
                tex.Apply(false);
                string png = Convert.ToBase64String(tex.EncodeToPNG());
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.name = "scan";
                picture = tex;
                tex = null;
                return png;
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
            // as wide as the belt (measured on the belt modules when there are some), not taller than the camera head
            float half = BeltHalfWidth(beltPoint, forward) + .15f, top = height + .12f, post = .06f;
            float postY = top / 2f - beltPoint.y / 2f, postH = top + beltPoint.y;
            Box(root, new Vector3(-half, postY, 0f), new Vector3(post, postH, post), frame);
            Box(root, new Vector3(half, postY, 0f), new Vector3(post, postH, post), frame);
            Box(root, new Vector3(0f, top, 0f), new Vector3(2f * half + post, .06f, .08f), frame);
            Box(root, new Vector3(0f, height + .03f, 0f), new Vector3(.1f, .06f, .1f), new Color(.08f, .08f, .09f));
            Box(root, new Vector3(0f, top - .035f, .05f), new Vector3(2f * half - .1f, .012f, .012f), accent, .9f);
        }

        /// <summary>Half width of the belt at that point: the belt module of the factory scene (shader Line_Belt) under it, else .3 m.</summary>
        private static float BeltHalfWidth(Vector3 at, Vector3 forward)
        {
            Vector3 side = Vector3.Cross(Vector3.up, forward).normalized;
            float best = 0f;
            foreach (Renderer r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                Material m = r.sharedMaterial;
                if (m == null || m.shader == null || !m.shader.name.Contains("Line_Belt")) continue;
                Bounds b = r.bounds;
                if (!b.Contains(new Vector3(at.x, b.center.y, at.z))) continue;
                best = Mathf.Max(best, Mathf.Abs(side.x) * b.extents.x + Mathf.Abs(side.z) * b.extents.z);
            }
            return best > .1f ? best : .3f;
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
