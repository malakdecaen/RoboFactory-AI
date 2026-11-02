using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RoboFactory.Hackathon;
using RoboFactory.Hackathon.Meta;
using RoboFactory.Integration;
using RoboFactory.Launcher;
using RoboFactory.Logistics;
using UnityEngine;
using RoboFactory.Sim;

namespace RoboFactory.HUD
{
    /// <summary>
    /// SAM 3.1 on the on-board cameras of R1 and R2: visual check of the boxes, and of the order R2 loads.
    ///
    /// Each robot carries an inspection camera (mast-mounted, looking down at the work in front of it, rendered
    /// only when a check is due). Its image is captured at the exact moment a check is meaningful, shown in the
    /// ROBOT VISION window with SAM's boxes on it, and sent to Meta SAM 3.1 with the name of the object to find:
    ///   - R1 has just taken a box from a pallet / put one on a staging bench;
    ///   - R2 arrives at a bench to take a box;
    ///   - R2 has just put a box in a truck → the truck load is checked, and when the order is complete
    ///     the whole order is declared VERIFIED if SAM sees as many boxes as the truck really holds;
    ///   - an employee is in front of a moving robot ("person", every few seconds at most).
    ///
    /// Slot-by-slot check: every slot of the bench / truck / pallet is projected into the image; each object SAM
    /// returns is attached to the slot it overlaps best (a box found twice, or found by two words, lands on the same
    /// slot; objects elsewhere in the hall overlap no slot and are ignored). A slot hidden behind a nearer box is
    /// not judged. The word sent to SAM follows the boxes really expected there ("cardboard box", "wooden crate").
    /// In a truck, the box R2 has just dropped must be seen at its slot; an order is VERIFIED when each of its
    /// boxes has been confirmed by SAM at the moment it was loaded.
    /// The SAM count is compared with what the simulation knows at that place ("scene"), never mixed with it:
    /// SAM never drives the robots and the emergency stop stays local (a ~2 s answer is far too slow for safety).
    /// Every check is logged ("[SAM] …" → flow journal) and, in LIVE AI recording, recorded; the RECORDED DEMO
    /// replays the real answer recorded for the same robot / place / word.
    /// </summary>
    public sealed class SamVisionService
    {
        public const string ConceptCardboard = "cardboard box";
        public const string ConceptCrate = "wooden crate";
        public const string ConceptPerson = "person";

        public sealed class Detection
        {
            /// <summary>Normalized box, origin top-left of the image (0..1).</summary>
            public Rect Box;
            public float Confidence;   // −1 = not returned
            public int Id;
            public string Concept = "";
        }

        public sealed class RobotSam
        {
            public string RobotId = "";
            public string Status = "IDLE";            // IDLE · SEGMENTING · OK · FAILED · NOT RECORDED
            public string Label = "";                 // what was looked for, plural form for the chip ("CARDBOARD BOXES")
            public string Place = "";                 // P02, B03, T01, "path"
            public string PlaceKind = "";             // pallet, bench, truck, path
            public int Seen = -1;                     // objects SAM found inside the work zone
            public int Ignored;                       // objects SAM found on no slot (decor, background: not counted)
            public int Hidden;                        // occupied slots hidden behind a nearer box or out of the image
            public int Scene = -1;                    // objects the simulation knows at that place (−1 = unknown)
            public int LatencyMs;
            public string Model = "";
            public bool Recorded;
            public float At = -999f;                  // unscaled time of the answer
            public float RequestedAt = -999f;
            /// <summary>Work zone in the image (normalized, origin top-left), when it could be projected.</summary>
            public Rect Zone;
            public bool HasZone;
            /// <summary>"" (no verdict), "VERIFIED" or "MISMATCH".</summary>
            public string Verdict = "";
            public string VerdictText = "";
            public bool OrderVerified;
            public readonly List<Detection> Detections = new List<Detection>();
            /// <summary>Image SAM analysed (shown in the ROBOT VISION window while the result is fresh).</summary>
            public Texture2D Snapshot;
            public bool InFlight;
            internal bool Capturing;
            internal Camera Cam;
            internal RenderTexture Target;
            public float BackoffUntil;
            public int Calls, Failures, Verified, Mismatches, OrdersVerified;
            internal float LastPersonCheck;
            internal Request Pending;
        }

        internal sealed class Part
        {
            public string Concept;
            public int Scene;
        }

        internal sealed class Slot
        {
            public Rect Rect;                         // normalized, origin top-left
            public bool InImage, Hidden, Occupied, IsNew, Seen;
            public float Depth;
            public CargoBox Box;
        }

        internal sealed class Request
        {
            public string Place, PlaceKind;
            public readonly List<Part> Parts = new List<Part>();
            public readonly List<Slot> Slots = new List<Slot>();
            public int Scene;
            public string Png;
            public Rect Zone;
            public bool HasZone;
            public bool Verify;                       // bench / truck: a verdict is given
            public CargoBox NewBox;                   // box just put there (must be seen)
            public ShelfStorage Storage;
            public CargoBox[] Boxes;
            public bool UseSlots;
            public Texture2D Snapshot;
            public int OrderId = -1, OrderTotal, OrderInTruck;
            public float At;
        }

        public int Width = 640;
        public int Height = 360;
        public float MinConfidence = .5f;
        public float MinInterval = 2.5f;          // minimum delay before a "person" check after any call of the same robot
        public float PersonInterval = 5f;
        public float PersonRange = 8f;
        public float PersonHalfAngle = 33f;        // roughly the camera's half field of view
        public float FailureBackoff = 20f;
        public float ZoneMargin = .02f;
        public float SlotMatchIoU = .2f;
        public float HiddenOverlap = .6f;
        public float DefaultBoxSize = .6f;
        public float MinSlotInImage = .25f;
        [Header("Inspection camera (box checks)")]
        public float InspectHeight = 1.55f, InspectForward = .05f, InspectPitch = 38f, InspectFov = 75f;
        [Header("Path camera (employees)")]
        public float PathHeight = 1.0f, PathForward = .22f, PathPitch = 8f, PathFov = 70f;
        public bool Enabled = true;

        public readonly RobotSam R1 = new RobotSam { RobotId = "R1" };
        public readonly RobotSam R2 = new RobotSam { RobotId = "R2" };

        private readonly MonoBehaviour host;
        private readonly MetaProxyClient proxy;
        private WarehouseRobotAutopilot boundR1;
        private RobotVisionFeed feedR1, feedR2;
        private Transform robotR1, robotR2;
        private int cullingMask = ~0;
        private HudModel model;
        private TruckLoaderAgent.R2State lastR2State = TruckLoaderAgent.R2State.Stopped;
        private string lastR1Bench = "", lastR2Truck = "", lastR2Bench = "";
        private int lastR2Order = -1;
        private CargoBox lastR2Box;
        /// <summary>Boxes confirmed by SAM at the moment they were loaded, per order.</summary>
        private readonly Dictionary<int, HashSet<CargoBox>> verifiedLoads = new Dictionary<int, HashSet<CargoBox>>();
        private int seq;

        public SamVisionService(MonoBehaviour host, string proxyUrl)
        {
            this.host = host;
            proxy = new MetaProxyClient { BaseUrl = proxyUrl };
        }

        public RobotSam For(string robotId) => robotId == "R2" ? R2 : R1;

        // ------------------------------------------------------------------ triggers

        /// <summary>Called every HUD frame with the current model and the robot cameras.</summary>
        public void Tick(HudDataSource data, RobotVisionFeed cameraR1, RobotVisionFeed cameraR2)
        {
            if (data == null) return;
            feedR1 = cameraR1;
            feedR2 = cameraR2;
            model = data.Model;
            robotR1 = data.R1 != null ? data.R1.transform : null;
            robotR2 = data.R2 != null ? data.R2.transform : null;
            if (cameraR1 != null && cameraR1.Camera != null) cullingMask = cameraR1.Camera.cullingMask;
            Bind(data.R1);
            HudModel m = data.Model;
            WarehouseWorld world = data.World != null ? data.World : WarehouseWorld.Instance;

            // last known bench of R1 (the bench it is delivering to), used when the box is staged
            if (m.R1.Activity == "DELIVERING" && !string.IsNullOrEmpty(m.R1.ActivityTarget)) lastR1Bench = m.R1.ActivityTarget;

            // R2: state transitions
            TruckLoaderAgent r2 = data.R2;
            if (r2 != null)
            {
                TruckLoaderAgent.R2State s = r2.State;
                if (s == TruckLoaderAgent.R2State.ToBench || s == TruckLoaderAgent.R2State.Picking)
                    lastR2Bench = Clean(m.R2.Destination);
                if (s == TruckLoaderAgent.R2State.ToTruck || s == TruckLoaderAgent.R2State.Dropping)
                {
                    lastR2Truck = Clean(m.R2.Destination);
                    if (r2.DirectedOrderId >= 0) lastR2Order = r2.DirectedOrderId;
                    if (r2.Carried != null) lastR2Box = r2.Carried;
                }
                if (s != lastR2State)
                {
                    if (s == TruckLoaderAgent.R2State.Picking && lastR2Bench.Length > 0 && world != null)
                        CheckStorage(R2, robotR2, "bench", lastR2Bench, world.Bench(lastR2Bench), true, null);
                    if (lastR2State == TruckLoaderAgent.R2State.Dropping && lastR2Truck.Length > 0 && world != null)
                        CheckTruck(world.Truck(lastR2Truck), lastR2Truck, lastR2Order, lastR2Box);
                    lastR2State = s;
                }
            }

            // employees in front of a moving robot
            CheckPerson(R1, m.R1);
            CheckPerson(R2, m.R2);

            Pump(R1);
            Pump(R2);
        }

        private void Bind(WarehouseRobotAutopilot r1)
        {
            if (r1 == boundR1) return;
            Unbind();
            boundR1 = r1;
            if (r1 != null)
            {
                r1.BoxPicked += OnR1Picked;
                r1.BoxStaged += OnR1Staged;
            }
        }

        public void Unbind()
        {
            if (boundR1 != null)
            {
                boundR1.BoxPicked -= OnR1Picked;
                boundR1.BoxStaged -= OnR1Staged;
            }
            boundR1 = null;
        }

        public void Dispose()
        {
            Unbind();
            foreach (RobotSam r in new[] { R1, R2 })
            {
                if (r.Cam != null) UnityEngine.Object.Destroy(r.Cam.gameObject);
                if (r.Target != null) { r.Target.Release(); UnityEngine.Object.Destroy(r.Target); }
                if (r.Snapshot != null) UnityEngine.Object.Destroy(r.Snapshot);
                if (r.Pending != null && r.Pending.Snapshot != null) UnityEngine.Object.Destroy(r.Pending.Snapshot);
                r.Cam = null; r.Target = null; r.Snapshot = null; r.Pending = null;
            }
        }

        private void OnR1Picked(CargoBox box, WarehouseRobotAutopilot.PickLeg leg)
        {
            if (leg == null) return;
            CheckStorage(R1, robotR1, "pallet", leg.PalletId ?? "", leg.Pallet, false, null);
        }

        private void OnR1Staged(CargoBox box)
        {
            WarehouseWorld world = WarehouseWorld.Instance;
            string bench = lastR1Bench;
            if (world == null || bench.Length == 0) return;
            CheckStorage(R1, robotR1, "bench", bench, world.Bench(bench), true, box);
        }

        /// <summary>Pallet or bench in front of the robot: slot-by-slot check.</summary>
        private void CheckStorage(RobotSam r, Transform robot, string kind, string place, ShelfStorage storage, bool verify, CargoBox newBox)
        {
            if (storage == null) return;
            var q = new Request { Place = place, PlaceKind = kind, Verify = verify, NewBox = newBox, Storage = storage, UseSlots = true };
            q.Boxes = storage.GetComponentsInChildren<CargoBox>(false);
            AddParts(q, q.Boxes);
            Submit(r, robot, q);
        }

        /// <summary>R2 has just dropped a box in a truck: the load (and the order when complete) is checked.</summary>
        private void CheckTruck(TruckBay truck, string truckId, int orderId, CargoBox newBox)
        {
            if (truck == null) return;
            var q = new Request { Place = truckId, PlaceKind = "truck", Verify = true, OrderId = orderId, NewBox = newBox };
            CargoBox[] boxes = truck.GetComponentsInChildren<CargoBox>(false);
            q.Boxes = boxes;
            q.Storage = truck.Storage;
            q.UseSlots = true;
            AddParts(q, boxes);
            if (orderId >= 0)
            {
                q.OrderInTruck = truck.CountBoxesForOrder(orderId);
                OrderVM o = model != null ? model.FindOrder(orderId) : null;
                q.OrderTotal = o != null ? o.Total : 0;
            }
            Submit(R2, robotR2, q);
        }

        private void CheckPerson(RobotSam r, RobotVM vm)
        {
            if (vm == null || vm.Transform == null || !vm.Present || !vm.MissionActive) return;
            float now = Time.unscaledTime;
            if (now - r.LastPersonCheck < PersonInterval || r.InFlight || r.Capturing || r.Pending != null) return;
            if (now - r.RequestedAt < MinInterval) return;
            int inView = PeopleInView(vm.Transform);
            if (inView <= 0) return;
            r.LastPersonCheck = now;
            var q = new Request { Place = "path", PlaceKind = "path", Scene = inView };
            q.Parts.Add(new Part { Concept = ConceptPerson, Scene = inView });
            Submit(r, vm.Transform, q);
        }

        private int PeopleInView(Transform robot)
        {
            int n = 0;
            Vector3 p = robot.position, f = robot.forward;
            f.y = 0f;
            foreach (IPedestrianSource crew in PedestrianSources.All)
            {
                if (crew == null || crew.Agents == null) continue;
                foreach (PedestrianAgent agent in crew.Agents)
                {
                    if (agent == null || agent.Root == null) continue;
                    Vector3 d = agent.Root.position - p;
                    d.y = 0f;
                    if (d.magnitude > PersonRange || d.sqrMagnitude < 1e-4f) continue;
                    if (Vector3.Angle(f, d) <= PersonHalfAngle) n++;
                }
            }
            return n;
        }

        /// <summary>One SAM word per kind of box really present (A = wooden crate, B/C = cardboard box).</summary>
        private static void AddParts(Request q, CargoBox[] boxes)
        {
            int crates = 0, cardboard = 0;
            if (boxes != null)
                foreach (CargoBox b in boxes)
                {
                    if (b == null || b.isCarried) continue;
                    if (b.TypeLetter == "A") crates++; else cardboard++;
                }
            q.Scene = crates + cardboard;
            if (cardboard > 0 || crates == 0) q.Parts.Add(new Part { Concept = ConceptCardboard, Scene = cardboard });
            if (crates > 0) q.Parts.Add(new Part { Concept = ConceptCrate, Scene = crates });
        }

        /// <summary>Photographs the place now with the inspection camera (it must show this very moment) and queues the call.</summary>
        private void Submit(RobotSam r, Transform robot, Request q)
        {
            if (!Enabled || robot == null || r.Capturing) return;
            if (r.Pending != null && r.Pending.PlaceKind != "path" && q.PlaceKind == "path") return; // a place check matters more
            host.StartCoroutine(CaptureThenQueue(r, robot, q));
        }

        private IEnumerator CaptureThenQueue(RobotSam r, Transform robot, Request q)
        {
            r.Capturing = true;
            Camera cam = InspectionCamera(r, robot);
            bool path = q.PlaceKind == "path";
            cam.transform.localPosition = new Vector3(0f, path ? PathHeight : InspectHeight, path ? PathForward : InspectForward);
            cam.transform.localRotation = Quaternion.Euler(path ? PathPitch : InspectPitch, 0f, 0f);
            cam.fieldOfView = path ? PathFov : InspectFov;
            cam.cullingMask = cullingMask;
            if (q.UseSlots) BuildSlots(cam, q, q.Storage, q.Boxes);
            // rendered by the pipeline this frame (like the ROBOT VISION feed), read back at the end of the frame
            cam.enabled = true;
            yield return new WaitForEndOfFrame();
            Texture2D snap = null;
            string png = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = r.Target;
                snap = new Texture2D(Width, Height, TextureFormat.RGB24, false) { name = "SAM " + r.RobotId };
                snap.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
                snap.Apply(false);
                png = Convert.ToBase64String(snap.EncodeToPNG());
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SAM] camera capture failed: " + e.Message);
            }
            finally
            {
                RenderTexture.active = previous;
                if (cam != null) cam.enabled = false;
                r.Capturing = false;
            }
            if (png == null)
            {
                r.Failures++;
                if (snap != null) UnityEngine.Object.Destroy(snap);
                yield break;
            }
            q.Png = png;
            q.Snapshot = snap;
            q.At = Time.unscaledTime;
            if (r.Pending != null && r.Pending.Snapshot != null) UnityEngine.Object.Destroy(r.Pending.Snapshot);
            r.Pending = q;
        }

        private Camera InspectionCamera(RobotSam r, Transform robot)
        {
            if (r.Cam != null && r.Cam.transform.parent == robot) return r.Cam;
            if (r.Cam != null) UnityEngine.Object.Destroy(r.Cam.gameObject);
            if (r.Target == null)
            {
                r.Target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { name = "SAM Inspection " + r.RobotId, antiAliasing = 1 };
                r.Target.Create();
            }
            var go = new GameObject("SAM Inspection Camera " + r.RobotId);
            go.transform.SetParent(robot, false);
            Camera cam = go.AddComponent<Camera>();
            cam.nearClipPlane = .05f;
            cam.farClipPlane = 40f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(.03f, .05f, .08f);
            cam.useOcclusionCulling = false;
            cam.allowMSAA = false;
            cam.allowHDR = false;
            cam.depth = -60f;
            cam.targetTexture = r.Target;
            cam.enabled = false;
            r.Cam = cam;
            return cam;
        }

        // ------------------------------------------------------------------ calls

        private void Pump(RobotSam r)
        {
            if (r.Pending == null || r.InFlight) return;
            float now = Time.unscaledTime;
            if (now < r.BackoffUntil)
            {
                if (r.Pending.Snapshot != null) UnityEngine.Object.Destroy(r.Pending.Snapshot);
                r.Pending = null;
                return;
            }
            Request q = r.Pending;
            r.Pending = null;
            r.InFlight = true;
            r.RequestedAt = now;
            r.Status = "SEGMENTING";
            r.Place = q.Place;
            r.PlaceKind = q.PlaceKind;
            r.Scene = q.Scene;
            r.Label = LabelOf(q);
            r.Calls++;
            host.StartCoroutine(Call(r, q));
        }

        private IEnumerator Call(RobotSam r, Request q)
        {
            var raw = new List<Detection>();
            int latency = 0;
            string modelName = "", error = "";
            bool recorded = false, failed = false;
            foreach (Part part in q.Parts)
            {
                SamResponse response = null;
                string trigger = r.RobotId + "|" + q.PlaceKind + "|" + q.Place + "|" + part.Concept;
                string obs = r.RobotId + "-" + (++seq).ToString(CultureInfo.InvariantCulture);
                yield return proxy.AskSamFrame(q.Png, "image/png", part.Concept, trigger, obs, MinConfidence, x => response = x);
                if (response == null || !response.ok)
                {
                    failed = true;
                    if (response != null && !string.IsNullOrEmpty(response.error)) error = response.error;
                    break;
                }
                latency += response.latency_ms;
                if (!string.IsNullOrEmpty(response.model)) modelName = response.model;
                recorded |= response.recorded || response.mode == "recorded";
                float w = response.width > 0 ? response.width : Width;
                float h = response.height > 0 ? response.height : Height;
                if (response.objects == null) continue;
                foreach (SamObjectDto o in response.objects)
                {
                    if (o == null || (o.c >= 0f && o.c < MinConfidence)) continue;
                    float x1 = Mathf.Clamp01(o.x1 / w), y1 = Mathf.Clamp01(o.y1 / h);
                    float x2 = Mathf.Clamp01((o.x2 + 1f) / w), y2 = Mathf.Clamp01((o.y2 + 1f) / h);
                    if (x2 <= x1 || y2 <= y1) continue;
                    raw.Add(new Detection { Box = Rect.MinMaxRect(x1, y1, x2, y2), Confidence = o.c, Id = o.id, Concept = part.Concept });
                }
            }

            r.InFlight = false;
            r.At = Time.unscaledTime;
            if (r.Snapshot != null && r.Snapshot != q.Snapshot) UnityEngine.Object.Destroy(r.Snapshot);
            r.Snapshot = q.Snapshot;
            r.Detections.Clear();
            r.Zone = q.Zone;
            r.HasZone = q.HasZone;
            r.Verdict = r.VerdictText = "";
            r.OrderVerified = false;
            r.Hidden = 0;
            bool replay = RunMode.Kind == RunKind.Recorded;
            if (failed)
            {
                r.Failures++;
                r.Seen = -1;
                r.Ignored = 0;
                r.Recorded = replay;
                r.Status = replay ? "NOT RECORDED" : "FAILED";
                if (!replay) r.BackoffUntil = Time.unscaledTime + FailureBackoff;
                Debug.Log("[SAM] " + r.RobotId + " " + Where(q) + " '" + Words(q) + "': " +
                          (replay ? "no recorded SAM answer for this view" : "failed" + (error.Length > 0 ? " (" + error + ")" : "")));
                yield break;
            }
            r.Model = modelName;
            r.LatencyMs = latency;
            r.Recorded = recorded;
            r.Status = "OK";

            var conf = new StringBuilder();
            int ignored = 0;
            if (q.Slots.Count == 0)
            {
                // no slot to check against (employees on the path): every object counts
                foreach (Detection d in raw) { r.Detections.Add(d); AppendConf(conf, d); }
                r.Seen = r.Detections.Count;
                r.Ignored = 0;
                r.Scene = q.Scene;
                LogResult(r, q, conf, 0, 0, 0, 0, false);
                yield break;
            }

            // ---- each SAM object goes to the visible slot it overlaps best (one slot = one box at most)
            foreach (Detection d in raw)
            {
                Slot best = null;
                float bestIoU = SlotMatchIoU;
                foreach (Slot sl in q.Slots)
                {
                    if (!sl.InImage || sl.Hidden) continue;
                    float iou = IoU(d.Box, sl.Rect);
                    if (iou >= bestIoU) { bestIoU = iou; best = sl; }
                }
                if (best == null) { ignored++; continue; }
                if (best.Seen) continue;            // same box found again (other word, part of the box)
                best.Seen = true;
                r.Detections.Add(d);
                AppendConf(conf, d);
            }

            int visible = 0, seenOk = 0, onEmpty = 0, hidden = 0;
            bool newVisible = false, newSeen = false;
            foreach (Slot sl in q.Slots)
            {
                bool judged = sl.InImage && !sl.Hidden;
                if (sl.Occupied && !judged) hidden++;
                if (!judged) continue;
                if (sl.Occupied) { visible++; if (sl.Seen) seenOk++; }
                else if (sl.Seen) onEmpty++;
                if (sl.IsNew) { newVisible = true; newSeen = sl.Seen; }
            }
            r.Seen = seenOk + onEmpty;
            r.Ignored = ignored;
            r.Hidden = hidden;
            r.Scene = visible;

            // ---- verdict
            if (q.Verify)
            {
                string place = q.PlaceKind == "truck" ? q.Place : q.PlaceKind.ToUpperInvariant() + " " + q.Place;
                bool allVisibleSeen = seenOk == visible && onEmpty == 0;
                if (q.NewBox != null && !newVisible)
                {
                    r.Verdict = "UNSEEN";
                    r.VerdictText = "NEW BOX NOT VISIBLE FROM HERE · " + place;
                }
                else if (visible == 0)
                {
                    r.Verdict = "UNSEEN";
                    r.VerdictText = "NO BOX VISIBLE FROM HERE · " + place;
                }
                else
                {
                    bool ok = allVisibleSeen && (q.NewBox == null || newSeen);
                    r.Verdict = ok ? "VERIFIED" : "MISMATCH";
                    if (ok) r.Verified++; else r.Mismatches++;
                    string hiddenText = hidden > 0 ? " (+" + hidden + " HIDDEN)" : "";
                    if (q.PlaceKind == "truck")
                    {
                        string order = q.OrderId >= 0 && q.OrderTotal > 0 ? " · ORDER #" + q.OrderId + " " + q.OrderInTruck + "/" + q.OrderTotal : "";
                        if (ok && q.OrderId >= 0 && q.NewBox != null)
                        {
                            if (!verifiedLoads.TryGetValue(q.OrderId, out HashSet<CargoBox> set))
                                verifiedLoads[q.OrderId] = set = new HashSet<CargoBox>();
                            set.Add(q.NewBox);
                            bool complete = q.OrderTotal > 0 && q.OrderInTruck >= q.OrderTotal;
                            r.OrderVerified = complete && set.Count >= q.OrderTotal;
                        }
                        if (r.OrderVerified)
                        {
                            r.OrdersVerified++;
                            r.VerdictText = "ORDER #" + q.OrderId + " VERIFIED · " + q.OrderTotal + "/" + q.OrderTotal + " BOXES CHECKED IN " + q.Place;
                        }
                        else if (ok)
                            r.VerdictText = "BOX VERIFIED IN " + q.Place + " · " + seenOk + "/" + visible + " VISIBLE" + hiddenText + order;
                        else
                            r.VerdictText = "CHECK · SAM SEES " + seenOk + " OF " + visible + " VISIBLE BOXES" +
                                            (onEmpty > 0 ? " + " + onEmpty + " ON EMPTY SLOTS" : "") +
                                            (q.NewBox != null && !newSeen ? " · NEW BOX NOT SEEN" : "");
                    }
                    else
                    {
                        r.VerdictText = ok ? place + " VERIFIED · " + seenOk + "/" + visible + " VISIBLE" + hiddenText
                                           : "CHECK · SAM SEES " + seenOk + " OF " + visible + " VISIBLE BOXES" +
                                             (onEmpty > 0 ? " + " + onEmpty + " ON EMPTY SLOTS" : "");
                    }
                }
            }
            LogResult(r, q, conf, visible, hidden, onEmpty, ignored, true);
        }

        private static void AppendConf(StringBuilder conf, Detection d)
        {
            if (d.Confidence >= 0f) conf.Append(conf.Length > 0 ? "/" : "").Append(d.Confidence.ToString("F2", CultureInfo.InvariantCulture));
        }

        private static void LogResult(RobotSam r, Request q, StringBuilder conf, int visible, int hidden, int onEmpty, int ignored, bool slots)
        {
            Debug.Log("[SAM] " + r.RobotId + " " + Where(q) + " '" + Words(q) + "': " + r.Seen + " segmented" +
                      (slots ? " on slots (" + visible + " occupied visible" + (hidden > 0 ? ", " + hidden + " hidden" : "") +
                               (onEmpty > 0 ? ", " + onEmpty + " on empty slots" : "") + (ignored > 0 ? ", " + ignored + " off-slot ignored" : "") + ")"
                             : (q.Scene >= 0 ? " (scene " + q.Scene + ")" : "")) +
                      " · " + (r.Model.Length > 0 ? r.Model : "sam") + " " +
                      (r.LatencyMs / 1000f).ToString("F1", CultureInfo.InvariantCulture) + " s" +
                      (r.Recorded ? " · recorded" : "") + (conf.Length > 0 ? " · conf " + conf : "") +
                      (r.Verdict.Length > 0 ? " · " + r.Verdict : "") +
                      (r.OrderVerified ? " · ORDER #" + q.OrderId + " VERIFIED (" + q.OrderTotal + "/" + q.OrderTotal + ")" : ""));
        }

        private static string Where(Request q)
        {
            return q.PlaceKind == "path" ? "in front (path)" : q.PlaceKind + " " + (q.Place.Length > 0 ? q.Place : "?");
        }

        private static string Words(Request q)
        {
            var parts = new List<string>();
            foreach (Part p in q.Parts) parts.Add(p.Concept);
            return string.Join("' + '", parts);
        }

        private static string LabelOf(Request q)
        {
            if (q.Parts.Count == 1)
            {
                string c = q.Parts[0].Concept;
                return c == ConceptCardboard ? "CARDBOARD BOX" : c == ConceptCrate ? "WOODEN CRATE" : c == ConceptPerson ? "PERSON" : c.ToUpperInvariant();
            }
            return "BOX";
        }

        /// <summary>Plural used by the chip ("3 CARDBOARD BOXES", "1 PERSON", "2 PEOPLE").</summary>
        public static string Plural(string label, int n)
        {
            if (n == 1 || string.IsNullOrEmpty(label)) return label;
            if (label == "PERSON") return "PEOPLE";
            return label.EndsWith("X") || label.EndsWith("S") ? label + "ES" : label + "S";
        }

        // ------------------------------------------------------------------ slots

        /// <summary>
        /// Every slot of the storage (pallet, bench, truck) projected into the robot camera image: rectangle,
        /// depth, box present or not; slots covered by a nearer box are marked hidden (not judged).
        /// </summary>
        private void BuildSlots(Camera cam, Request q, ShelfStorage storage, CargoBox[] boxes)
        {
            q.Slots.Clear();
            q.HasZone = false;
            if (cam == null) return;
            Vector3 typical = Vector3.one * DefaultBoxSize;
            if (boxes != null)
                foreach (CargoBox b in boxes)
                    if (b != null && !b.isCarried && TryBounds(b, out Bounds bb)) { typical = bb.size; break; }

            var points = new List<Transform>();
            if (storage != null && storage.columns != null)
                foreach (ShelfStorage.StorageColumn column in storage.columns)
                {
                    if (column == null || column.levels == null) continue;
                    foreach (Transform level in column.levels)
                        if (level != null) points.Add(level);
                }
            if (points.Count > 0)
                foreach (Transform point in points)
                {
                    CargoBox box = null;
                    foreach (CargoBox b in point.GetComponentsInChildren<CargoBox>(false))
                        if (b != null && !b.isCarried) { box = b; break; }
                    Bounds bounds = box != null && TryBounds(box, out Bounds bb) ? bb
                                  : new Bounds(point.position + Vector3.up * typical.y * .5f, typical);
                    AddSlot(cam, q, bounds, box);
                }
            else if (boxes != null)
                foreach (CargoBox b in boxes)
                    if (b != null && !b.isCarried && TryBounds(b, out Bounds bb)) AddSlot(cam, q, bb, b);

            // a slot more than HiddenOverlap covered by a nearer occupied slot cannot be judged
            foreach (Slot s in q.Slots)
            {
                if (!s.InImage) continue;
                float area = s.Rect.width * s.Rect.height;
                foreach (Slot o in q.Slots)
                {
                    if (o == s || !o.Occupied || !o.InImage || o.Depth >= s.Depth - .1f) continue;
                    if (Intersection(s.Rect, o.Rect) > HiddenOverlap * area) { s.Hidden = true; break; }
                }
            }

            // zone shown in the HUD: union of the judged slots
            foreach (Slot s in q.Slots)
            {
                if (!s.InImage || s.Hidden) continue;
                q.Zone = q.HasZone ? Rect.MinMaxRect(Mathf.Min(q.Zone.xMin, s.Rect.xMin), Mathf.Min(q.Zone.yMin, s.Rect.yMin),
                                                     Mathf.Max(q.Zone.xMax, s.Rect.xMax), Mathf.Max(q.Zone.yMax, s.Rect.yMax))
                                   : s.Rect;
                q.HasZone = true;
            }
        }

        private void AddSlot(Camera cam, Request q, Bounds bounds, CargoBox box)
        {
            var slot = new Slot { Box = box, Occupied = box != null, IsNew = box != null && box == q.NewBox };
            slot.InImage = Project(cam, bounds, out slot.Rect, out slot.Depth);
            q.Slots.Add(slot);
        }

        private static bool TryBounds(CargoBox box, out Bounds bounds)
        {
            bounds = new Bounds();
            bool any = false;
            foreach (Renderer rd in box.GetComponentsInChildren<Renderer>(false))
            {
                if (rd == null || !rd.enabled) continue;
                if (!any) { bounds = rd.bounds; any = true; } else bounds.Encapsulate(rd.bounds);
            }
            return any;
        }

        /// <summary>World box → normalized image rectangle (origin top-left). False if behind or mostly off-screen.</summary>
        private bool Project(Camera cam, Bounds b, out Rect rect, out float depth)
        {
            rect = new Rect();
            depth = cam.WorldToViewportPoint(b.center).z;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = new Vector3(i % 2 == 0 ? b.min.x : b.max.x, (i / 2) % 2 == 0 ? b.min.y : b.max.y, i / 4 == 0 ? b.min.z : b.max.z);
                Vector3 v = cam.WorldToViewportPoint(corner);
                if (v.z <= .05f) return false;
                float x = v.x, y = 1f - v.y;
                minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x);
                minY = Mathf.Min(minY, y); maxY = Mathf.Max(maxY, y);
            }
            Rect full = Rect.MinMaxRect(minX - ZoneMargin, minY - ZoneMargin, maxX + ZoneMargin, maxY + ZoneMargin);
            Rect clipped = Rect.MinMaxRect(Mathf.Clamp01(full.xMin), Mathf.Clamp01(full.yMin), Mathf.Clamp01(full.xMax), Mathf.Clamp01(full.yMax));
            float fullArea = full.width * full.height, clippedArea = clipped.width * clipped.height;
            rect = clipped;
            // enough of the slot must be in the image to be judged
            return fullArea > 1e-5f && clippedArea >= MinSlotInImage * fullArea;
        }

        private static float Intersection(Rect a, Rect b)
        {
            float w = Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin);
            float h = Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin);
            return w > 0f && h > 0f ? w * h : 0f;
        }

        private static float IoU(Rect a, Rect b)
        {
            float i = Intersection(a, b);
            float u = a.width * a.height + b.width * b.height - i;
            return u > 1e-6f ? i / u : 0f;
        }

        private static string Clean(string id)
        {
            if (string.IsNullOrEmpty(id) || id == "—" || id == "BENCH" || id == "TRUCK" || id == "HOME") return "";
            return id;
        }
    }
}
