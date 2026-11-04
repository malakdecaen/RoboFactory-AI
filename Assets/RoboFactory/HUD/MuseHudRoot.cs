using System.Collections;
using System.Collections.Generic;
using RoboFactory.Hackathon.Meta;
using RoboFactory.Hackathon.Orchestration;
using RoboFactory.Logistics;
using UnityEngine;
using UnityEngine.UI;

namespace RoboFactory.HUD
{
    /// <summary>
    /// MUSE WONDER HUD — entry point. Builds the canvas, the responsive layout grid
    /// (central area constrained on ultra-wide and multi-screen), the views,
    /// the CINEMATIC / OPERATIONS / DEBUG modes, and wires the views to HudDataSource.
    /// Contains NO logistics logic: the only actions are calls to the
    /// existing public APIs (SubmitOrder, SubmitUrgent*, VoiceCommandController.Toggle).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MuseHudRoot : MonoBehaviour
    {
        [Header("Modes")]
        public HudMode StartMode = HudMode.Operations;
        public KeyCode CinematicKey = KeyCode.F2;
        public KeyCode OperationsKey = KeyCode.F3;
        public KeyCode DebugKey = KeyCode.F4;
        public KeyCode HideKey = KeyCode.H;
        public KeyCode MicKey = KeyCode.M;
        public KeyCode SoundKey = KeyCode.N;
        public KeyCode CameraKey = KeyCode.C;

        [Header("Sons")]
        [Range(0f, 1f)] public float SoundVolume = .55f;

        [Header("Layout")]
        [Tooltip("Beyond this ratio (e.g. 32:9), the interface stays in a central 21:9 area.")]
        public float MaxSingleScreenAspect = 2.45f;
        [Tooltip("Beyond this ratio (triple screen), the interface stays in a central 16:9 area (3840×2160 in 12288×2160).")]
        public float MultiScreenAspect = 4.2f;
        public int SortingOrder = 50;

        [Header("Robot Vision")]
        public float VisionCompactFps = 10f;
        public float VisionExpandedFps = 20f;

        public HudMode Mode { get; private set; }

        private readonly HudDataSource data = new HudDataSource();
        private readonly MicLevelProbe mic = new MicLevelProbe();
        private readonly HudLogBuffer log = new HudLogBuffer();
        private readonly LegacyUiController legacy = new LegacyUiController();
        private readonly HudAudio sounds = new HudAudio();
        private readonly HudSoundEvents soundEvents = new HudSoundEvents();

        private Canvas canvas;
        private RectTransform canvasRect, frame;
        private MuseCoreView core;
        private OrdersPanelView orders;
        private DecisionPanelView decisions;
        private StatusBarView statusBar;
        private VoiceDockView voice;
        private TruckStripView trucks;
        private RobotVisionView visionR1, visionR2;
        private PackingScanView packingScan;
        private ShippingLabelView shippingLabel;
        /// <summary>SAM 3.1 on the robot cameras (see SamVisionService).</summary>
        private SamVisionService sam;
        private BannerView banner;
        private TopBarView topBar;
        private DebugConsoleView debugConsole;
        private WorldTagsView tags;
        private readonly WorldMarkersView markers = new WorldMarkersView();
        private readonly HudCameraRig cameraRig = new HudCameraRig();

        private sealed class Fader
        {
            public CanvasGroup Group;
            public bool[] Modes; // index = HudMode
        }
        private readonly List<Fader> faders = new List<Fader>();

        private float nextRefresh;
        private bool hidden;
        private int holoLayer;
        private int robotCamMask = ~0;
        private float voiceSearchUntil;
        private bool built;

        // ------------------------------------------------------------------ lifecycle

        private void Start()
        {
            log.Attach();
            sounds.MasterVolume = SoundVolume;
            sounds.Attach(gameObject);
            data.Bind();
            data.DecisionArrived += OnDecision;
            data.UrgentStageChanged += OnUrgentStage;
            BuildUi();
            SetMode(StartMode, true);
            data.Refresh();
            sam = new SamVisionService(this, data.Orchestrator != null ? data.Orchestrator.ProxyUrl : "http://127.0.0.1:8765");
            StartCoroutine(HealthLoop());
            voiceSearchUntil = Time.unscaledTime + 3f;
            Debug.Log("[HUD] Muse Wonder HUD ready (mode " + Mode + ", F2/F3/F4 to change mode, H to hide).");
        }

        private void OnDestroy()
        {
            log.Detach();
            sounds.Dispose();
            data.DecisionArrived -= OnDecision;
            data.UrgentStageChanged -= OnUrgentStage;
            data.Dispose();
            markers.Dispose();
            sam?.Dispose();
            visionR1?.Dispose();
            visionR2?.Dispose();
            if (built) legacy.RestoreAll();
        }

        // ------------------------------------------------------------------ construction

        private void BuildUi()
        {
            Camera main = Camera.main;
            holoLayer = main != null && (main.cullingMask & (1 << 5)) != 0 ? 5 : 0;
            robotCamMask = holoLayer == 5 ? ~(1 << 5) : ~0;
            markers.Layer = holoLayer;
            markers.Build();

            var go = new GameObject("Muse HUD Canvas", typeof(RectTransform));
            go.layer = 5;
            go.transform.SetParent(transform, false);
            canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = SortingOrder;
            canvas.pixelPerfect = false;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 1f;
            scaler.referencePixelsPerUnit = 100f;
            go.AddComponent<GraphicRaycaster>();
            canvasRect = (RectTransform)go.transform;
            HudInput.EnsureEventSystem();

            // world labels: full screen, under the panels
            tags = new WorldTagsView();
            tags.Build(canvasRect);
            Fade(tags.Root, false, true, true);

            // constrained layout area (central 16:9 / 21:9 depending on the screen)
            frame = HudFactory.Rect("Layout Frame", canvasRect);
            frame.anchorMin = frame.anchorMax = new Vector2(.5f, .5f);
            frame.pivot = new Vector2(.5f, .5f);

            topBar = new TopBarView();
            topBar.Build(frame);
            topBar.ModeRequested = m => SetMode(m, false);
            topBar.SoundToggled = ToggleSound;
            topBar.SoundMuted = () => sounds.Muted;
            topBar.CamToggled = ToggleCamera;
            topBar.CamOrbit = () => cameraRig.IsOrbit;
            topBar.CamLabel = () => cameraRig.Label;
            Fade(topBar.Brand, true, true, true);
            Fade(topBar.Switcher, true, true, true);
            Fade(topBar.CamSwitch, true, true, true);
            Fade(topBar.FlankLeft, false, true, true);
            Fade(topBar.FlankRight, false, true, true);
            Fade(topBar.Toast, true, false, false);

            orders = new OrdersPanelView();
            orders.Build(frame);
            orders.SubmitOrder = SubmitOrder;
            orders.SubmitUrgent = SubmitUrgent;
            orders.OrderFocused = id => markers.FocusOrder = id;
            Fade(orders.Root, false, true, true);

            decisions = new DecisionPanelView();
            decisions.Build(frame);
            Fade(decisions.Root, false, true, true);

            visionR1 = new RobotVisionView("R1", false) { CompactFps = VisionCompactFps, ExpandedFps = VisionExpandedFps };
            visionR1.Build(frame);
            Fade(visionR1.Root, false, true, true);
            visionR2 = new RobotVisionView("R2", true) { CompactFps = VisionCompactFps, ExpandedFps = VisionExpandedFps };
            visionR2.Build(frame);
            Fade(visionR2.Root, false, true, true);
            // packing cell scan (Muse Spark): above R1's camera, only when the cell exists (shown by its own Tick)
            packingScan = new PackingScanView();
            packingScan.Build(frame);
            // shipping label of the order going to its truck (Muse Spark + Muse Image): above R2's camera
            shippingLabel = new ShippingLabelView();
            shippingLabel.Build(frame);

            trucks = new TruckStripView();
            trucks.Build(frame);
            Fade(trucks.Root, false, true, true);

            voice = new VoiceDockView();
            voice.Build(frame);
            voice.ToggleMic = () => { if (data.Voice != null) data.Voice.Toggle(); };
            voice.CancelMic = () => { if (data.Voice != null) data.Voice.CancelTalk(); };
            Fade(voice.Root, true, true, true);

            statusBar = new StatusBarView();
            statusBar.Build(frame);
            Fade(statusBar.Root, false, true, true);

            debugConsole = new DebugConsoleView();
            debugConsole.Build(frame);
            Fade(debugConsole.Root, false, false, true);

            // the circle and the banners go above everything
            core = new MuseCoreView();
            core.Build(frame);
            Fade(core.Root, true, true, true);
            banner = new BannerView();
            banner.Build(frame);
            Fade(banner.Root, true, true, true);

            // sub-canvas: elements animated every frame do not force a rebuild of the rest
            Isolate(core.Root);
            Isolate(tags.Root);
            Isolate(banner.Root);

            UpdateFrame();
            built = true;
        }

        private static void Isolate(RectTransform rt)
        {
            if (rt.GetComponent<Canvas>() == null) rt.gameObject.AddComponent<Canvas>();
        }

        private void Fade(RectTransform rt, bool cinematic, bool operations, bool debug)
        {
            CanvasGroup g = rt.GetComponent<CanvasGroup>();
            if (g == null) g = rt.gameObject.AddComponent<CanvasGroup>();
            faders.Add(new Fader { Group = g, Modes = new[] { cinematic, operations, debug } });
        }

        // ------------------------------------------------------------------ modes

        public void SetMode(HudMode mode, bool instant)
        {
            if (!instant && mode != Mode) sounds.Play(HudSound.ModeSwitch);
            Mode = mode;
            legacy.Apply(mode);
            bool ops = mode != HudMode.Cinematic;
            markers.SetVisible(true, ops);
            if (tags != null) tags.SetVisible(ops, ops);
            if (instant)
                foreach (Fader f in faders) ApplyFade(f, 1f, true);
        }

        private void ApplyFade(Fader f, float dt, bool instant)
        {
            bool on = !hidden && f.Modes[(int)Mode];
            float target = on ? 1f : 0f;
            f.Group.alpha = instant ? target : Mathf.MoveTowards(f.Group.alpha, target, dt * HudTheme.FadeSpeed);
            f.Group.interactable = on;
            f.Group.blocksRaycasts = on;
            bool active = on || f.Group.alpha > .001f;
            if (f.Group.gameObject.activeSelf != active) f.Group.gameObject.SetActive(active);
        }

        private bool Visible(RectTransform rt)
        {
            return rt != null && rt.gameObject.activeInHierarchy && !hidden;
        }

        // ------------------------------------------------------------------ loop

        private void Update()
        {
            if (!built) return;
            float dt = Mathf.Min(Time.unscaledDeltaTime, .1f);
            HandleKeys();
            legacy.Tick();
            EnsureVoice();

            if (Time.unscaledTime >= nextRefresh)
            {
                nextRefresh = Time.unscaledTime + 1f / HudTheme.RefreshHz;
                data.Refresh();
                HudModel m = data.Model;
                if (m.Ready) soundEvents.Check(m, sounds);
                if (m.Ready)
                {
                    orders.MaxHeight = OrdersMaxHeight();
                    orders.Refresh(m);
                    decisions.Refresh(m);
                    statusBar.Refresh(m);
                    if (Mode == HudMode.Debug) debugConsole.Refresh(m, log);
                }
            }

            HudModel model = data.Model;
            mic.Sample(data.Voice, dt);
            UpdateFrame();
            foreach (Fader f in faders) ApplyFade(f, dt, false);
            if (!model.Ready) return;

            core.Tick(dt, model, mic.Level);
            banner.Tick(dt, model);
            topBar.Tick(dt, model, Mode);
            orders.Tick(dt);
            decisions.Tick(dt);
            voice.Tick(dt, model, mic);
            trucks.Tick(dt, model);
            debugConsole.Tick(dt);
            bool ops = Mode != HudMode.Cinematic && !hidden;
            visionR1.PixelScale = visionR2.PixelScale = canvas.scaleFactor;
            visionR1.Tick(dt, model.R1, ops, robotCamMask, sam?.R1);
            visionR2.Tick(dt, model.R2, ops, robotCamMask, sam?.R2);
            packingScan.Tick(dt, ops, HudTheme.BottomRowY + visionR1.Root.sizeDelta.y + HudTheme.Gap);
            float decisionsBottom = decisions.Root.gameObject.activeInHierarchy
                ? frame.rect.height - HudTheme.SideTop - decisions.Root.sizeDelta.y : float.MaxValue;
            shippingLabel.Tick(dt, ops, HudTheme.BottomRowY + visionR2.Root.sizeDelta.y + HudTheme.Gap, decisionsBottom,
                               HudTheme.Margin + visionR2.Root.sizeDelta.x + HudTheme.Gap);
            sam?.Tick(data, visionR1.Feed, visionR2.Feed);
            markers.Tick(dt, model);
            if (Visible(tags.Root))
            {
                tags.SafeArea = TagSafeArea();
                Vector2 c0 = LocalEdge(core.Root, tags.Root, 0), c2 = LocalEdge(core.Root, tags.Root, 2);
                // circle + status pill: ±130 around the horizontal center, from the top to the bottom of the pill
                float cx = (c0.x + c2.x) * .5f, cy = (c0.y + c2.y) * .5f;
                tags.AvoidArea = Rect.MinMaxRect(cx - 135f, cy - 185f, cx + 135f, c2.y);
                // hover bubbles: mouse position (outside the HUD panels); DEBUG = show everything
                tags.ShowAll = Mode == HudMode.Debug;
                bool overUi = UnityEngine.EventSystems.EventSystem.current != null &&
                              UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject();
                tags.MouseOk = !overUi && RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    tags.Root, HudInput.MousePosition(), null, out tags.Mouse);
                tags.Tick(dt, model, Camera.main);
            }
        }

        private void HandleKeys()
        {
            if (orders != null && orders.InputFocused) return;
            if (RoboFactory.Launcher.LauncherScreen.Active) return; // launcher open: typing the API key must not trigger HUD shortcuts
            if (HudInput.KeyDown(CinematicKey)) SetMode(HudMode.Cinematic, false);
            if (HudInput.KeyDown(OperationsKey)) SetMode(HudMode.Operations, false);
            if (HudInput.KeyDown(DebugKey)) SetMode(HudMode.Debug, false);
            if (HudInput.KeyDown(HideKey))
            {
                hidden = !hidden;
                markers.SetVisible(!hidden, !hidden && Mode != HudMode.Cinematic);
            }
            if (HudInput.KeyDown(MicKey) && data.Voice != null) data.Voice.Toggle();
            if (HudInput.KeyDown(SoundKey)) ToggleSound();
            if (HudInput.KeyDown(CameraKey)) ToggleCamera();
            // arrows: the orbit camera follows R1 (←) or R2 (→); ↑ / ↓: overview
            if (!CinematicDirector.Enabled)   // shot mode (F6) uses the arrows for its own shots
            {
                if (HudInput.KeyDown(KeyCode.LeftArrow)) FocusCamera(HudCameraRig.Focus.R1);
                if (HudInput.KeyDown(KeyCode.RightArrow)) FocusCamera(HudCameraRig.Focus.R2);
                if (HudInput.KeyDown(KeyCode.UpArrow) || HudInput.KeyDown(KeyCode.DownArrow)) FocusCamera(HudCameraRig.Focus.Warehouse);
            }
        }

        /// <summary>Scene view <-> orbit view (camera button, C key).</summary>
        public void ToggleCamera()
        {
            cameraRig.Toggle(data.World);
            sounds.Play(HudSound.ModeSwitch);
        }

        private void FocusCamera(HudCameraRig.Focus focus)
        {
            HudModel m = data.Model;
            cameraRig.FocusOn(focus, data.World, m.R1.Transform, m.R2.Transform);
            sounds.Play(HudSound.ModeSwitch);
        }

        private void LateUpdate()
        {
            if (!built) return;
            cameraRig.LateTick(Mathf.Min(Time.unscaledDeltaTime, .1f));
        }

        /// <summary>Constraint of the interface area according to the real aspect ratio.</summary>
        private void UpdateFrame()
        {
            Rect r = canvasRect.rect;
            float h = r.height, w = r.width;
            if (h <= 1f) return;
            float aspect = w / h;
            float fw = aspect > MultiScreenAspect ? h * 16f / 9f
                     : aspect > MaxSingleScreenAspect ? h * 21f / 9f
                     : w;
            frame.sizeDelta = new Vector2(fw, h);
            frame.anchoredPosition = Vector2.zero;
        }

        private readonly Vector3[] corners = new Vector3[4];

        /// <summary>Rectangle (labels' local space) of the free area between the panels.</summary>
        private Rect TagSafeArea()
        {
            RectTransform t = tags.Root;
            Rect area = t.rect;
            float xMin = area.xMin + 20f, xMax = area.xMax - 20f, yMin = area.yMin + 20f, yMax = area.yMax - 20f;
            if (orders.Root.gameObject.activeInHierarchy) xMin = Mathf.Max(xMin, LocalEdge(orders.Root, t, 2).x + 16f);
            if (decisions.Root.gameObject.activeInHierarchy) xMax = Mathf.Min(xMax, LocalEdge(decisions.Root, t, 0).x - 16f);
            if (trucks.Root.gameObject.activeInHierarchy) yMin = Mathf.Max(yMin, LocalEdge(trucks.Root, t, 1).y + 12f);
            if (topBar.FlankLeft.gameObject.activeInHierarchy) yMax = Mathf.Min(yMax, LocalEdge(topBar.FlankLeft, t, 0).y - 12f);
            return Rect.MinMaxRect(xMin, yMin, Mathf.Max(xMin + 1f, xMax), Mathf.Max(yMin + 1f, yMax));
        }

        /// <summary>Corner of RectTransform rt expressed in target's space (0 = bottom-left, 1 = top-left, 2 = top-right).</summary>
        private Vector2 LocalEdge(RectTransform rt, RectTransform target, int corner)
        {
            rt.GetWorldCorners(corners);
            return target.InverseTransformPoint(corners[corner]);
        }

        private float OrdersMaxHeight()
        {
            float frameH = frame.rect.height;
            float visionTop = HudTheme.BottomRowY + visionR1.Root.sizeDelta.y;
            if (packingScan != null && packingScan.Root.gameObject.activeSelf) visionTop += packingScan.Root.sizeDelta.y + HudTheme.Gap;
            return Mathf.Max(160f, frameH - HudTheme.SideTop - visionTop - 16f);
        }

        // ------------------------------------------------------------------ data events

        private void OnDecision(DecisionVM d)
        {
            if (d == null || !built) return;
            decisions.PlayReveal();
            sounds.Play(d.Accepted ? HudSound.DecisionAccepted : HudSound.DecisionRejected);
            if (d.HasPlan && d.Accepted && d.Urgent) banner.ShowUrgentPlan(d);
            if (d.HasPlan)
            {
                Vector2 target = Mode == HudMode.Cinematic
                    ? new Vector2(0f, 220f)
                    : (Vector2)core.Root.InverseTransformPoint(decisions.CardWorldCenter);
                core.ShowDecision(d, target);
                if (d.Accepted)
                {
                    var ids = new List<string>(d.Pallets) { d.Bench, d.Truck };
                    markers.Flash(ids);
                }
            }
            if (Mode == HudMode.Cinematic)
            {
                string text = d.Accepted
                    ? (d.HasPlan ? "MUSE  ·  ORDER #" + d.OrderId + "  " + string.Join("+", d.Pallets) + " › " + d.Bench + " › " + d.Truck
                                 : "MUSE  ·  " + (d.Action ?? "").ToUpperInvariant())
                    : "MUSE  ·  PLAN REJECTED — " + d.RejectReason;
                if (d.LatencyMs > 0) text += "   " + DecisionPanelView.FormatMs(d.LatencyMs);
                topBar.ShowToast(text, d.Accepted ? (d.Urgent ? HudTheme.Orange : HudTheme.Cyan) : HudTheme.Red);
            }
        }

        private int capacityBannerFor = -1;

        private void OnUrgentStage(string previous, string next)
        {
            if (!built) return;
            UrgentVM u = data.Model.Urgent;
            bool wasCalm = previous == "Idle" || previous == "Planning" || previous == "Completed" || previous == "Failed";
            if (next == "WaitingForCapacity" && capacityBannerFor != u.OrderId)
            {
                // FINISH TO FREE: all docks busy, Muse is going to choose an order to finish
                capacityBannerFor = u.OrderId;
                banner.ShowCapacityOverride(u.OrderId);
                sounds.Play(HudSound.PriorityOverride);
            }
            else if (next == "UrgentActive" && u.CapacityEpisode)
            {
                banner.ShowResourcesAvailable(u.OrderId, u.CapacityBench, u.CapacityTruck);
                sounds.Play(HudSound.DecisionAccepted);
            }
            else if ((next == "Suspending" || next == "UrgentActive") && wasCalm)
            {
                banner.ShowOverride(u.OrderId, Mathf.Max(u.Paused, u.Suspending));
                sounds.Play(HudSound.PriorityOverride);
            }
            else if (next == "Resuming")
            {
                banner.ShowResuming(Mathf.Max(u.Resumed, u.Paused));
                sounds.Play(HudSound.Resuming);
            }
            else if (next == "Completed" && previous == "UrgentActive")
            {
                banner.ShowResuming(0);
                sounds.Play(HudSound.Resuming);
            }
            else if (next == "Failed")
            {
                banner.ShowFailure(string.IsNullOrEmpty(u.Error) ? "see AI decisions panel" : u.Error);
                sounds.Play(HudSound.UrgentFailed);
            }
        }

        public void ToggleSound()
        {
            sounds.SetMuted(!sounds.Muted);
            if (!sounds.Muted) sounds.Play(HudSound.Click);
        }

        public bool SoundMuted => sounds.Muted;

        // ------------------------------------------------------------------ actions (existing APIs)

        private int SubmitOrder(string text)
        {
            MuseOrchestrator o = data.Orchestrator;
            return o != null ? o.SubmitOrder(text) : 0;
        }

        /// <summary>Same handling as the « Manual urgent » button of the old HUD.</summary>
        private void SubmitUrgent(string text)
        {
            MuseOrchestrator o = data.Orchestrator;
            if (o == null) return;
            int[] counts = OrderText.Parse(text);
            if (OrderText.Total(counts) <= 0) o.SubmitUrgentTranscript(text);
            else o.SubmitUrgentCounts(counts[0], counts[1], counts[2]);
        }

        /// <summary>Like the old HUD: the voice controller is installed if it is missing from the scene.</summary>
        private void EnsureVoice()
        {
            if (data.Voice != null || data.Orchestrator == null || Time.unscaledTime < voiceSearchUntil) return;
            if (FindFirstObjectByType<VoiceCommandController>() == null)
            {
                gameObject.AddComponent<VoiceCommandController>().Orchestrator = data.Orchestrator;
                Debug.Log("[HUD] VoiceCommandController missing: installed on the HUD.");
            }
            voiceSearchUntil = Time.unscaledTime + 5f;
        }

        private IEnumerator HealthLoop()
        {
            var wait = new WaitForSecondsRealtime(10f);
            while (true)
            {
                MuseOrchestrator o = data.Orchestrator;
                if (o != null && o.Mode != "offline")
                {
                    var client = new MetaProxyClient { BaseUrl = o.ProxyUrl };
                    yield return client.Health(r =>
                    {
                        data.HealthOk = r != null && r.ok;
                        data.HealthModel = r != null ? (r.orchestrator_model ?? "") : "";
                    });
                }
                yield return wait;
            }
        }
    }
}
