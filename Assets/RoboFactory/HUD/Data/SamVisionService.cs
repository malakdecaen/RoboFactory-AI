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
    /// The camera image of the robot (the same camera as the ROBOT VISION window) is captured at the exact moment
    /// a check is meaningful and sent to Meta SAM 3.1 with the name of the object to find:
    ///   - R1 has just taken a box from a pallet / put one on a staging bench;
    ///   - R2 arrives at a bench to take a box;
    ///   - R2 has just put a box in a truck → the truck load is checked, and when the order is complete
    ///     the whole order is declared VERIFIED if SAM sees as many boxes as the truck really holds;
    ///   - an employee is in front of a moving robot ("person", every few seconds at most).
    ///
    /// Only the objects inside the work zone are counted: the bench / truck / pallet slots are projected into the
    /// image and the boxes SAM finds elsewhere in the hall (racks in the background…) are ignored. The word sent to
    /// SAM follows the boxes really expected there ("cardboard box", "wooden crate"; one call per word).
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
            public int Ignored;                       // objects SAM found outside the work zone (not counted)
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
            public bool InFlight;
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

        internal sealed class Request
        {
            public string Place, PlaceKind;
            public readonly List<Part> Parts = new List<Part>();
            public int Scene;
            public string Png;
            public Rect Zone;
            public bool HasZone;
            public bool Verify;                       // bench / truck: a verdict is given
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
        public float ZoneMargin = .04f;
        public bool Enabled = true;

        public readonly RobotSam R1 = new RobotSam { RobotId = "R1" };
        public readonly RobotSam R2 = new RobotSam { RobotId = "R2" };

        private readonly MonoBehaviour host;
        private readonly MetaProxyClient proxy;
        private WarehouseRobotAutopilot boundR1;
        private RobotVisionFeed feedR1, feedR2;
        private HudModel model;
        private TruckLoaderAgent.R2State lastR2State = TruckLoaderAgent.R2State.Stopped;
        private string lastR1Bench = "", lastR2Truck = "", lastR2Bench = "";
        private int lastR2Order = -1;
        private Texture2D readback;
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
                }
                if (s != lastR2State)
                {
                    if (s == TruckLoaderAgent.R2State.Picking && lastR2Bench.Length > 0 && world != null)
                        CheckStorage(R2, feedR2, "bench", lastR2Bench, world.Bench(lastR2Bench), true);
                    if (lastR2State == TruckLoaderAgent.R2State.Dropping && lastR2Truck.Length > 0 && world != null)
                        CheckTruck(world.Truck(lastR2Truck), lastR2Truck, lastR2Order);
                    lastR2State = s;
                }
            }

            // employees in front of a moving robot
            CheckPerson(R1, feedR1, m.R1);
            CheckPerson(R2, feedR2, m.R2);

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
            if (readback != null) UnityEngine.Object.Destroy(readback);
            readback = null;
        }

        private void OnR1Picked(CargoBox box, WarehouseRobotAutopilot.PickLeg leg)
        {
            if (leg == null) return;
            CheckStorage(R1, feedR1, "pallet", leg.PalletId ?? "", leg.Pallet, false);
        }

        private void OnR1Staged(CargoBox box)
        {
            WarehouseWorld world = WarehouseWorld.Instance;
            string bench = lastR1Bench;
            if (world == null || bench.Length == 0) return;
            CheckStorage(R1, feedR1, "bench", bench, world.Bench(bench), true);
        }

        /// <summary>Pallet or bench in front of the robot: boxes counted inside its slots.</summary>
        private void CheckStorage(RobotSam r, RobotVisionFeed feed, string kind, string place, ShelfStorage storage, bool verify)
        {
            if (storage == null) return;
            var q = new Request { Place = place, PlaceKind = kind, Verify = verify };
            AddParts(q, storage.GetComponentsInChildren<CargoBox>(false));
            if (!TryZone(feed, SlotBounds(storage, null), out q.Zone)) q.HasZone = false; else q.HasZone = true;
            Submit(r, feed, q);
        }

        /// <summary>R2 has just dropped a box in a truck: the load (and the order when complete) is checked.</summary>
        private void CheckTruck(TruckBay truck, string truckId, int orderId)
        {
            if (truck == null) return;
            var q = new Request { Place = truckId, PlaceKind = "truck", Verify = true, OrderId = orderId };
            CargoBox[] boxes = truck.GetComponentsInChildren<CargoBox>(false);
            AddParts(q, boxes);
            if (orderId >= 0)
            {
                q.OrderInTruck = truck.CountBoxesForOrder(orderId);
                OrderVM o = model != null ? model.FindOrder(orderId) : null;
                q.OrderTotal = o != null ? o.Total : 0;
            }
            q.HasZone = TryZone(feedR2, SlotBounds(truck.Storage, boxes), out q.Zone);
            Submit(R2, feedR2, q);
        }

        private void CheckPerson(RobotSam r, RobotVisionFeed feed, RobotVM vm)
        {
            if (vm == null || vm.Transform == null || !vm.Present || !vm.MissionActive) return;
            float now = Time.unscaledTime;
            if (now - r.LastPersonCheck < PersonInterval || r.InFlight || r.Pending != null) return;
            if (now - r.RequestedAt < MinInterval) return;
            int inView = PeopleInView(vm.Transform);
            if (inView <= 0) return;
            r.LastPersonCheck = now;
            var q = new Request { Place = "path", PlaceKind = "path", Scene = inView };
            q.Parts.Add(new Part { Concept = ConceptPerson, Scene = inView });
            Submit(r, feed, q);
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

        /// <summary>Captures the image now (it must show the place at this very moment) and queues the call.</summary>
        private void Submit(RobotSam r, RobotVisionFeed feed, Request q)
        {
            if (!Enabled || feed == null || feed.Camera == null) return;
            if (r.Pending != null && r.Pending.PlaceKind != "path" && q.PlaceKind == "path") return; // a place check matters more
            q.Png = Capture(feed);
            if (q.Png == null) { r.Failures++; return; }
            q.At = Time.unscaledTime;
            r.Pending = q;
        }

        // ------------------------------------------------------------------ calls

        private void Pump(RobotSam r)
        {
            if (r.Pending == null || r.InFlight) return;
            float now = Time.unscaledTime;
            if (now < r.BackoffUntil) { r.Pending = null; return; }
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
            var found = new List<Detection>();
            int ignored = 0, latency = 0;
            string modelName = "", error = "";
            bool recorded = false, failed = false;
            var conf = new StringBuilder();
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
                    var d = new Detection { Box = Rect.MinMaxRect(x1, y1, x2, y2), Confidence = o.c, Id = o.id, Concept = part.Concept };
                    if (q.HasZone && !q.Zone.Contains(d.Box.center)) { ignored++; continue; }
                    found.Add(d);
                    if (o.c >= 0f) conf.Append(conf.Length > 0 ? "/" : "").Append(o.c.ToString("F2", CultureInfo.InvariantCulture));
                }
            }

            r.InFlight = false;
            r.At = Time.unscaledTime;
            r.Detections.Clear();
            r.Zone = q.Zone;
            r.HasZone = q.HasZone;
            r.Verdict = r.VerdictText = "";
            r.OrderVerified = false;
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
            r.Detections.AddRange(found);
            r.Seen = found.Count;
            r.Ignored = ignored;
            r.Model = modelName;
            r.LatencyMs = latency;
            r.Recorded = recorded;
            r.Status = "OK";

            // ---- verdict: SAM count in the work zone vs what the simulation holds there
            if (q.Verify && q.Scene >= 0)
            {
                bool ok = r.Seen == q.Scene && q.Scene > 0;
                r.Verdict = ok ? "VERIFIED" : "MISMATCH";
                if (ok) r.Verified++; else r.Mismatches++;
                string unit = q.Scene == 1 ? "BOX" : "BOXES";
                if (q.PlaceKind == "truck")
                {
                    bool orderComplete = q.OrderId >= 0 && q.OrderTotal > 0 && q.OrderInTruck >= q.OrderTotal;
                    r.OrderVerified = ok && orderComplete;
                    if (r.OrderVerified) r.OrdersVerified++;
                    if (r.OrderVerified)
                        r.VerdictText = "ORDER #" + q.OrderId + " VERIFIED · " + q.OrderInTruck + "/" + q.OrderTotal + " BOXES IN " + q.Place;
                    else if (ok)
                        r.VerdictText = "LOAD VERIFIED · " + q.Scene + " " + unit + " IN " + q.Place +
                                        (q.OrderId >= 0 && q.OrderTotal > 0 ? " · ORDER #" + q.OrderId + " " + q.OrderInTruck + "/" + q.OrderTotal : "");
                    else
                        r.VerdictText = "CHECK · SAM SEES " + r.Seen + ", TRUCK HOLDS " + q.Scene;
                }
                else
                {
                    r.VerdictText = ok ? q.PlaceKind.ToUpperInvariant() + " " + q.Place + " VERIFIED · " + q.Scene + " " + unit
                                       : "CHECK · SAM SEES " + r.Seen + ", " + q.PlaceKind.ToUpperInvariant() + " HOLDS " + q.Scene;
                }
            }

            Debug.Log("[SAM] " + r.RobotId + " " + Where(q) + " '" + Words(q) + "': " + r.Seen + " segmented" +
                      (q.HasZone ? " in zone" + (ignored > 0 ? ", " + ignored + " outside ignored" : "") : "") +
                      (q.Scene >= 0 ? " (scene " + q.Scene + ")" : "") + " · " + (modelName.Length > 0 ? modelName : "sam") + " " +
                      (latency / 1000f).ToString("F1", CultureInfo.InvariantCulture) + " s" +
                      (recorded ? " · recorded" : "") + (conf.Length > 0 ? " · conf " + conf : "") +
                      (r.Verdict.Length > 0 ? " · " + r.Verdict : "") +
                      (r.OrderVerified ? " · ORDER #" + q.OrderId + " VERIFIED (" + q.OrderInTruck + "/" + q.OrderTotal + ")" : ""));
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

        // ------------------------------------------------------------------ work zone

        /// <summary>World bounds of the slots of a storage (pallet, bench, truck), room for one box on each slot.</summary>
        private static Bounds? SlotBounds(ShelfStorage storage, CargoBox[] fallback)
        {
            bool any = false;
            var b = new Bounds();
            void Add(Vector3 p)
            {
                var slot = new Bounds(p + Vector3.up * .4f, new Vector3(.9f, 1f, .9f));
                if (!any) { b = slot; any = true; } else b.Encapsulate(slot);
            }
            if (storage != null && storage.columns != null)
                foreach (ShelfStorage.StorageColumn column in storage.columns)
                {
                    if (column == null || column.levels == null) continue;
                    foreach (Transform level in column.levels)
                        if (level != null) Add(level.position);
                }
            if (!any && fallback != null)
                foreach (CargoBox box in fallback)
                    if (box != null && !box.isCarried) Add(box.transform.position);
            return any ? b : (Bounds?)null;
        }

        /// <summary>Projects the world zone into the robot camera image (normalized, origin top-left).</summary>
        private bool TryZone(RobotVisionFeed feed, Bounds? bounds, out Rect zone)
        {
            zone = new Rect(0f, 0f, 1f, 1f);
            if (feed == null || feed.Camera == null || !bounds.HasValue) return false;
            Camera cam = feed.Camera;
            Bounds b = bounds.Value;
            float minX = 1f, minY = 1f, maxX = 0f, maxY = 0f;
            int inFront = 0;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = new Vector3(i % 2 == 0 ? b.min.x : b.max.x, (i / 2) % 2 == 0 ? b.min.y : b.max.y, i / 4 == 0 ? b.min.z : b.max.z);
                Vector3 v = cam.WorldToViewportPoint(corner);
                if (v.z <= .05f) continue;
                inFront++;
                float x = v.x, y = 1f - v.y;
                minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x);
                minY = Mathf.Min(minY, y); maxY = Mathf.Max(maxY, y);
            }
            if (inFront == 0) return false;
            minX = Mathf.Clamp01(minX - ZoneMargin); minY = Mathf.Clamp01(minY - ZoneMargin);
            maxX = Mathf.Clamp01(maxX + ZoneMargin); maxY = Mathf.Clamp01(maxY + ZoneMargin);
            if (maxX - minX < .02f || maxY - minY < .02f) return false;
            zone = Rect.MinMaxRect(minX, minY, maxX, maxY);
            return true;
        }

        // ------------------------------------------------------------------ capture

        /// <summary>
        /// Robot camera frame at a fixed size, as base64 PNG: the latest frame of the ROBOT VISION feed when it is
        /// running (OPERATIONS view), otherwise one frame rendered on demand by the same camera.
        /// </summary>
        private string Capture(RobotVisionFeed feed)
        {
            Camera cam = feed.Camera;
            RenderTexture rt = null;
            RenderTexture previousTarget = cam.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            try
            {
                rt = RenderTexture.GetTemporary(Width, Height, 24, RenderTextureFormat.ARGB32);
                if (feed.Active && feed.Texture != null && feed.Texture.IsCreated())
                    Graphics.Blit(feed.Texture, rt);
                else
                {
                    cam.targetTexture = rt;
                    cam.Render();
                }
                RenderTexture.active = rt;
                if (readback == null || readback.width != Width || readback.height != Height)
                {
                    if (readback != null) UnityEngine.Object.Destroy(readback);
                    readback = new Texture2D(Width, Height, TextureFormat.RGB24, false);
                }
                readback.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
                readback.Apply(false);
                return Convert.ToBase64String(readback.EncodeToPNG());
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SAM] camera capture failed: " + e.Message);
                return null;
            }
            finally
            {
                cam.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
            }
        }

        private static string Clean(string id)
        {
            if (string.IsNullOrEmpty(id) || id == "—" || id == "BENCH" || id == "TRUCK" || id == "HOME") return "";
            return id;
        }
    }
}
