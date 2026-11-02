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
    /// SAM 3.1 on the on-board cameras of R1 and R2.
    ///
    /// The camera image of the robot (the same camera as the ROBOT VISION window) is sent to Meta SAM 3.1 at the
    /// moments where a visual check is meaningful:
    ///   - R1 has just taken a box from a pallet → "box" on the pallet in front of it;
    ///   - R1 has just put a box on a staging bench → "box" on the bench;
    ///   - R2 arrives at a bench to take a box → "box" on the bench;
    ///   - R2 has just put a box in a truck → "box" in the trailer;
    ///   - an employee is in front of a moving robot → "person" (every few seconds at most).
    /// The number of objects SAM segments is shown next to what the simulation knows ("scene"), never
    /// mixed with it: SAM never drives the robots and the emergency stop stays local (a ~2 s answer is far
    /// too slow for safety). Every call is logged ("[SAM] …" → flow journal) and, in LIVE AI recording,
    /// recorded; the RECORDED DEMO replays the real answer recorded for the same robot / place / concept.
    /// </summary>
    public sealed class SamVisionService
    {
        public const string ConceptBox = "box";
        public const string ConceptPerson = "person";

        public sealed class Detection
        {
            /// <summary>Normalized box, origin top-left of the image (0..1).</summary>
            public Rect Box;
            public float Confidence;   // −1 = not returned
            public int Id;
        }

        public sealed class RobotSam
        {
            public string RobotId = "";
            public string Status = "IDLE";            // IDLE · SEGMENTING · OK · FAILED · NOT RECORDED · OFF
            public string Concept = "";
            public string Place = "";                 // P02, B03, T01, "path"
            public string PlaceKind = "";             // pallet, bench, truck, path
            public int Seen = -1;                     // objects segmented by SAM
            public int Scene = -1;                    // objects known to the simulation at that place (−1 = unknown)
            public int LatencyMs;
            public string Model = "";
            public bool Recorded;
            public float At = -999f;                  // unscaled time of the answer
            public float RequestedAt = -999f;
            public readonly List<Detection> Detections = new List<Detection>();
            public bool InFlight;
            public float BackoffUntil;
            public int Calls, Failures;
            internal float LastPersonCheck;
            internal Request Pending;
        }

        internal sealed class Request
        {
            public string Concept, Place, PlaceKind;
            public int Scene;
            public float At;
        }

        [Tooltip("Analysed image size (pixels).")]
        public int Width = 640;
        public int Height = 360;
        public float MinConfidence = .3f;
        public float MinInterval = 2.5f;          // between two calls of the same robot
        public float PersonInterval = 5f;          // between two "person" checks of the same robot
        public float PersonRange = 8f;
        public float PersonHalfAngle = 33f;        // roughly the camera's half field of view
        public float FailureBackoff = 20f;
        public bool Enabled = true;

        public readonly RobotSam R1 = new RobotSam { RobotId = "R1" };
        public readonly RobotSam R2 = new RobotSam { RobotId = "R2" };
        public int TotalCalls => R1.Calls + R2.Calls;

        private readonly MonoBehaviour host;
        private readonly MetaProxyClient proxy;
        private WarehouseRobotAutopilot boundR1;
        private TruckLoaderAgent.R2State lastR2State = TruckLoaderAgent.R2State.Stopped;
        private string lastR1Bench = "", lastR2Truck = "", lastR2Bench = "";
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
        public void Tick(HudDataSource data, RobotVisionFeed feedR1, RobotVisionFeed feedR2)
        {
            if (data == null) return;
            Bind(data.R1);
            HudModel m = data.Model;

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
                    lastR2Truck = Clean(m.R2.Destination);
                if (s != lastR2State)
                {
                    if (s == TruckLoaderAgent.R2State.Picking && lastR2Bench.Length > 0)
                        Queue(R2, ConceptBox, lastR2Bench, "bench", CountOn(data.World != null ? data.World.Bench(lastR2Bench) : null));
                    if (lastR2State == TruckLoaderAgent.R2State.Dropping && lastR2Truck.Length > 0)
                        Queue(R2, ConceptBox, lastR2Truck, "truck", TruckLoaded(m, lastR2Truck));
                    lastR2State = s;
                }
            }

            // employees in front of a moving robot
            CheckPerson(R1, m.R1);
            CheckPerson(R2, m.R2);

            Pump(R1, feedR1);
            Pump(R2, feedR2);
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
            Queue(R1, ConceptBox, leg.PalletId ?? "", "pallet", CountOn(leg.Pallet));
        }

        private void OnR1Staged(CargoBox box)
        {
            WarehouseWorld world = WarehouseWorld.Instance;
            string bench = lastR1Bench;
            Queue(R1, ConceptBox, bench, "bench", world != null && bench.Length > 0 ? CountOn(world.Bench(bench)) : -1);
        }

        private void CheckPerson(RobotSam r, RobotVM vm)
        {
            if (vm == null || vm.Transform == null || !vm.Present || !vm.MissionActive) return;
            float now = Time.unscaledTime;
            if (now - r.LastPersonCheck < PersonInterval || r.InFlight || r.Pending != null) return;
            if (now - r.RequestedAt < MinInterval) return;
            int inView = PeopleInView(vm.Transform);
            if (inView <= 0) return;
            r.LastPersonCheck = now;
            Queue(r, ConceptPerson, "path", "path", inView);
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

        private void Queue(RobotSam r, string concept, string place, string kind, int scene)
        {
            if (!Enabled || string.IsNullOrEmpty(concept)) return;
            // a box check always replaces a pending person check (the place matters more)
            if (r.Pending != null && r.Pending.Concept == ConceptBox && concept == ConceptPerson) return;
            r.Pending = new Request { Concept = concept, Place = place ?? "", PlaceKind = kind, Scene = scene, At = Time.unscaledTime };
        }

        // ------------------------------------------------------------------ calls

        private void Pump(RobotSam r, RobotVisionFeed feed)
        {
            if (r.Pending == null || r.InFlight) return;
            float now = Time.unscaledTime;
            if (now < r.BackoffUntil) { r.Pending = null; return; }
            if (now - r.RequestedAt < MinInterval) return;
            if (now - r.Pending.At > 6f) { r.Pending = null; return; }   // the view has changed: stale request
            if (feed == null || feed.Camera == null) return;

            Request q = r.Pending;
            r.Pending = null;
            string png = Capture(feed);
            if (png == null) { r.Status = "FAILED"; r.Failures++; return; }
            r.InFlight = true;
            r.RequestedAt = now;
            r.Status = "SEGMENTING";
            r.Concept = q.Concept;
            r.Place = q.Place;
            r.PlaceKind = q.PlaceKind;
            r.Scene = q.Scene;
            r.Calls++;
            string trigger = r.RobotId + "|" + q.PlaceKind + "|" + q.Place + "|" + q.Concept;
            string obs = r.RobotId + "-" + (++seq).ToString(CultureInfo.InvariantCulture);
            host.StartCoroutine(Call(r, q, png, trigger, obs));
        }

        private IEnumerator Call(RobotSam r, Request q, string png, string trigger, string obs)
        {
            SamResponse response = null;
            yield return proxy.AskSamFrame(png, "image/png", q.Concept, trigger, obs, MinConfidence, x => response = x);
            r.InFlight = false;
            r.At = Time.unscaledTime;
            r.Detections.Clear();
            bool replay = RunMode.Kind == RunKind.Recorded;
            if (response == null || !response.ok)
            {
                r.Failures++;
                r.Seen = -1;
                r.Recorded = replay;
                r.Status = replay ? "NOT RECORDED" : "FAILED";
                if (!replay) r.BackoffUntil = Time.unscaledTime + FailureBackoff;
                Debug.Log("[SAM] " + r.RobotId + " " + Where(q) + " '" + q.Concept + "': " +
                          (replay ? "no recorded SAM answer for this view" : "failed" + (response != null && !string.IsNullOrEmpty(response.error) ? " (" + response.error + ")" : "")));
                yield break;
            }
            r.Model = response.model ?? "";
            r.LatencyMs = response.latency_ms;
            r.Recorded = response.recorded || response.mode == "recorded";
            float w = response.width > 0 ? response.width : Width;
            float h = response.height > 0 ? response.height : Height;
            var conf = new StringBuilder();
            if (response.objects != null)
                foreach (SamObjectDto o in response.objects)
                {
                    if (o == null) continue;
                    float x1 = Mathf.Clamp01(o.x1 / w), y1 = Mathf.Clamp01(o.y1 / h);
                    float x2 = Mathf.Clamp01((o.x2 + 1f) / w), y2 = Mathf.Clamp01((o.y2 + 1f) / h);
                    if (x2 <= x1 || y2 <= y1) continue;
                    r.Detections.Add(new Detection { Box = Rect.MinMaxRect(x1, y1, x2, y2), Confidence = o.c, Id = o.id });
                    if (o.c >= 0f) conf.Append(conf.Length > 0 ? "/" : "").Append(o.c.ToString("F2", CultureInfo.InvariantCulture));
                }
            r.Seen = r.Detections.Count;
            r.Status = "OK";
            Debug.Log("[SAM] " + r.RobotId + " " + Where(q) + " '" + q.Concept + "': " + r.Seen + " segmented" +
                      (q.Scene >= 0 ? " (scene " + q.Scene + ")" : "") + " · " + (r.Model.Length > 0 ? r.Model : "sam") + " " +
                      (r.LatencyMs / 1000f).ToString("F1", CultureInfo.InvariantCulture) + " s" +
                      (r.Recorded ? " · recorded" : "") + (conf.Length > 0 ? " · conf " + conf : ""));
        }

        private static string Where(Request q)
        {
            return q.PlaceKind == "path" ? "in front (path)" : q.PlaceKind + " " + (q.Place.Length > 0 ? q.Place : "?");
        }

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

        // ------------------------------------------------------------------ simulation side ("scene")

        private static int CountOn(Component storage)
        {
            if (storage == null) return -1;
            int n = 0;
            foreach (CargoBox box in storage.GetComponentsInChildren<CargoBox>(false))
                if (box != null && !box.isCarried) n++;
            return n;
        }

        private static int TruckLoaded(HudModel m, string truckId)
        {
            foreach (TruckVM t in m.Trucks)
                if (t != null && t.Id == truckId) return t.Loaded;
            return -1;
        }

        private static string Clean(string id)
        {
            if (string.IsNullOrEmpty(id) || id == "—" || id == "BENCH" || id == "TRUCK" || id == "HOME") return "";
            return id;
        }
    }
}
