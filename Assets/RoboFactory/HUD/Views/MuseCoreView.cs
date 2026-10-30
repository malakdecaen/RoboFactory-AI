using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RoboFactory.HUD
{
    /// <summary>
    /// MUSE WONDER signature circle (V2): graduated frame, rotating glowing arcs, segmented ring,
    /// sweeping comet, animated neural core, readable name, status pill under the circle,
    /// orange shock wave when entering urgent mode and gradual return to blue.
    /// Purely visual: the state arrives resolved in HudModel.Visual (real system states).
    /// </summary>
    public sealed class MuseCoreView
    {
        public RectTransform Root { get; private set; }

        private const float R = 100f;

        private Image halo, disk, innerDisk;
        private ArcGraphic frame, brackets, arcs, arcsGlow, segments, comet, inner, progress, hazard, pulse;
        private readonly ArcGraphic[] shock = new ArcGraphic[3];
        private TickRingGraphic ticks;
        private NeuralCoreGraphic core;
        private TextMeshProUGUI nameText, wonderText, stateText, detailText;
        private Image pillFill, pillStroke, pillDot, detailBack;

        private Color color = HudTheme.Blue;
        private float speed = .3f;
        private float pulseT = 1f, pulseTimer;
        private float shockT = 10f;
        private float segTimer;
        private float time;
        private MuseVisualState lastState = MuseVisualState.Idle;

        private sealed class Popup
        {
            public RectTransform Rt;
            public CanvasGroup Group;
            public TextMeshProUGUI Key, Value;
            public Image Accent;
            public Vector2 Home;
        }
        private readonly Popup[] popups = new Popup[6];
        private float popupT = -1f;
        private Vector2 flyTarget;

        public void Build(RectTransform parent)
        {
            Root = HudFactory.Rect("Muse Wonder Core", parent);
            HudFactory.Place(Root, new Vector2(.5f, 1f), new Vector2(.5f, .5f), new Vector2(0f, -122f), new Vector2(320f, 320f));

            halo = HudFactory.Image(Root, "Halo", HudSprites.Glow(), HudTheme.A(HudTheme.Blue, .12f), false);
            halo.rectTransform.sizeDelta = new Vector2(330f, 330f);
            disk = HudFactory.Image(Root, "Disk", HudSprites.Circle(), new Color(.01f, .035f, .075f, .84f), false);
            disk.rectTransform.sizeDelta = new Vector2(R * 2f - 4f, R * 2f - 4f);
            innerDisk = HudFactory.Image(Root, "Inner Disk", HudSprites.Circle(), new Color(.0f, .02f, .05f, .9f), false);
            innerDisk.rectTransform.sizeDelta = new Vector2(132f, 132f);

            hazard = Arc("Urgent Hazard", R + 18f, 4f, 0f, 360f, 24, .5f);
            brackets = Arc("Frame Brackets", R + 9f, 3f, 20f, 360f, 4, .16f);
            frame = Arc("Frame", R, 1.5f, 0f, 360f, 1, 1f);
            progress = Arc("Urgent Progress", R + 3.5f, 3f, 0f, 0f, 1, 1f);
            ticks = HudFactory.Add<TickRingGraphic>(Root, "Ticks");
            ticks.Radius = R - 4f; ticks.Count = 120; ticks.Length = 3f; ticks.MajorLength = 8f; ticks.MajorEvery = 10; ticks.Width = 1.4f;
            arcsGlow = Arc("Arcs Glow", R - 8f, 14f, 0f, 360f, 2, .42f);
            arcs = Arc("Arcs", R - 11f, 5f, 0f, 360f, 2, .42f);
            segments = Arc("Segments", R - 21f, 3f, 0f, 360f, 60, .5f);
            comet = Arc("Comet", R - 27f, 2.5f, 0f, 150f, 1, 1f);
            comet.TailFade = true;
            inner = Arc("Inner Ring", 67f, 1.2f, 0f, 360f, 1, 1f);
            pulse = Arc("Pulse", 67f, 2f, 0f, 360f, 1, 1f);
            for (int i = 0; i < shock.Length; i++) shock[i] = Arc("Shockwave " + i, R, 3f, 0f, 360f, 1, 1f);

            core = HudFactory.Add<NeuralCoreGraphic>(Root, "Neural Core");
            core.rectTransform.sizeDelta = new Vector2(10f, 10f);
            core.rectTransform.anchoredPosition = new Vector2(0f, 30f);
            core.Radius = 22f;

            nameText = HudFactory.Text(Root, "Name", "MUSE", 30f, HudTheme.TextHi, true, TextAlignmentOptions.Center, 8f);
            HudFactory.Place(nameText.rectTransform, new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(4f, -10f), new Vector2(140f, 34f));
            wonderText = HudFactory.Text(Root, "Wonder", "WONDER", 16f, HudTheme.Cyan, true, TextAlignmentOptions.Center, 9f);
            HudFactory.Place(wonderText.rectTransform, new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(4f, -36f), new Vector2(140f, 20f));

            // status pill
            RectTransform pill = HudFactory.Rect("State Pill", Root);
            HudFactory.Place(pill, new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(0f, -R - 34f), new Vector2(236f, 38f));
            Image pillShadow = HudFactory.Image(pill, "Shadow", HudSprites.SoftShadow(), HudTheme.Shadow);
            HudFactory.Stretch(pillShadow.rectTransform, -18f, -14f, -18f, -22f);
            pillFill = HudFactory.Image(pill, "Fill", HudSprites.Rounded(8), HudTheme.GlassStrong);
            HudFactory.Stretch(pillFill.rectTransform);
            pillStroke = HudFactory.Image(pill, "Stroke", HudSprites.Rounded(8, 1.5f), HudTheme.Cyan);
            HudFactory.Stretch(pillStroke.rectTransform);
            pillDot = HudFactory.Image(pill, "Dot", HudSprites.Circle(), HudTheme.Cyan, false);
            HudFactory.Place(pillDot.rectTransform, new Vector2(0f, .5f), new Vector2(.5f, .5f), new Vector2(22f, 0f), new Vector2(10f, 10f));
            stateText = HudFactory.Text(pill, "State", "IDLE", 20f, HudTheme.TextHi, true, TextAlignmentOptions.Center, 3f);
            HudFactory.Stretch(stateText.rectTransform, 30f, 0f, 12f, 0f);
            detailText = HudFactory.Text(Root, "Detail", "", HudTheme.FontSmall, HudTheme.TextMid, false, TextAlignmentOptions.Center, 1f);
            HudFactory.Place(detailText.rectTransform, new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(0f, -R - 68f), new Vector2(520f, 20f));
            detailBack = HudFactory.Image(Root, "Detail Back", HudSprites.Rounded(6), HudTheme.A(HudTheme.GlassStrong, .75f));
            detailBack.rectTransform.SetSiblingIndex(detailText.rectTransform.GetSiblingIndex());
            HudFactory.Place(detailBack.rectTransform, new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(0f, -R - 68f), new Vector2(380f, 24f));

            BuildPopups();
        }

        private ArcGraphic Arc(string name, float r, float thick, float start, float sweep, int dashes, float fill)
        {
            var a = HudFactory.Add<ArcGraphic>(Root, name);
            a.rectTransform.sizeDelta = new Vector2(10f, 10f);
            a.Set(r, thick, start, sweep, dashes, fill);
            return a;
        }

        public Color CurrentColor => color;

        // ------------------------------------------------------------------ animation

        public void Tick(float dt, HudModel m, float micLevel)
        {
            time += dt;
            MuseVisualState s = m.Visual;
            Color target; float targetSpeed; float pulseEvery; string label; float activity;
            switch (s)
            {
                case MuseVisualState.Listening: target = HudTheme.Cyan; targetSpeed = 1f; pulseEvery = .45f; label = "LISTENING"; activity = .6f; break;
                case MuseVisualState.Transcribing: target = HudTheme.Cyan; targetSpeed = 1.6f; pulseEvery = 1f; label = "TRANSCRIBING"; activity = .8f; break;
                case MuseVisualState.Planning: target = Color.Lerp(HudTheme.Cyan, HudTheme.Blue, .3f); targetSpeed = 3.4f; pulseEvery = .7f; label = "PLANNING"; activity = 1f; break;
                case MuseVisualState.Executing: target = Color.Lerp(HudTheme.Cyan, HudTheme.Blue, .45f); targetSpeed = .9f; pulseEvery = 2.2f; label = "EXECUTING"; activity = .55f; break;
                case MuseVisualState.Urgent: target = HudTheme.Orange; targetSpeed = 2.2f; pulseEvery = .6f; label = "URGENT"; activity = 1f; break;
                case MuseVisualState.Suspending: target = Color.Lerp(HudTheme.Orange, HudTheme.Amber, .25f); targetSpeed = .8f; pulseEvery = 1.2f; label = "SUSPENDING"; activity = .7f; break;
                case MuseVisualState.Resuming: target = HudTheme.Blue; targetSpeed = 1.2f; pulseEvery = 1.2f; label = "RESUMING"; activity = .6f; break;
                case MuseVisualState.Error: target = HudTheme.Red; targetSpeed = .25f; pulseEvery = 2.6f; label = "ERROR"; activity = .15f; break;
                default: target = HudTheme.Blue; targetSpeed = .35f; pulseEvery = 3.4f; label = "IDLE"; activity = .25f; break;
            }

            bool urgentish = s == MuseVisualState.Urgent || s == MuseVisualState.Suspending;
            if (s != lastState)
            {
                bool wasUrgent = lastState == MuseVisualState.Urgent || lastState == MuseVisualState.Suspending;
                if (urgentish && !wasUrgent) shockT = 0f;   // orange shock wave when entering urgent mode
                lastState = s;
            }

            // fast switch to orange, gradual return to blue (RESUMING)
            float rate = urgentish ? 7f : s == MuseVisualState.Resuming ? 1f : 4f;
            color = Color.Lerp(color, target, 1f - Mathf.Exp(-rate * dt));
            speed = Mathf.Lerp(speed, targetSpeed, 1f - Mathf.Exp(-3f * dt));
            float level = s == MuseVisualState.Listening ? Mathf.Clamp01(micLevel) : 0f;
            float breath = .5f + .5f * Mathf.Sin(time * (1.3f + speed));

            Spin(arcs, -speed * 24f, dt);
            arcsGlow.rectTransform.localRotation = arcs.rectTransform.localRotation;
            Spin(brackets, speed * 6f, dt);
            Spin(ticks, -speed * 3f, dt);
            Spin(comet, -speed * 100f, dt);
            Spin(hazard, -speed * 34f, dt);

            if (s == MuseVisualState.Transcribing || s == MuseVisualState.Planning)
            {
                segTimer += dt;
                if (segTimer > .08f) { segTimer = 0f; segments.rectTransform.localRotation *= Quaternion.Euler(0f, 0f, -6f); }
                segments.SetDashFill(.2f + .6f * Mathf.Abs(Mathf.Sin(time * (s == MuseVisualState.Planning ? 5f : 3f))));
            }
            else
            {
                Spin(segments, speed * 8f, dt);
                segments.SetDashFill(Mathf.Lerp(segments.DashFill, .5f, 1f - Mathf.Exp(-4f * dt)));
            }

            Color bright = Color.Lerp(color, Color.white, .3f);
            HudFactory.SetColor(frame, HudTheme.A(color, .8f));
            HudFactory.SetColor(brackets, HudTheme.A(bright, .85f));
            HudFactory.SetColor(ticks, HudTheme.A(color, .55f));
            HudFactory.SetColor(arcs, HudTheme.A(bright, 1f));
            HudFactory.SetColor(arcsGlow, HudTheme.A(color, .2f + .08f * breath));
            HudFactory.SetColor(segments, HudTheme.A(color, .5f));
            HudFactory.SetColor(comet, HudTheme.A(bright, .95f));
            HudFactory.SetColor(inner, HudTheme.A(color, .55f));
            HudFactory.SetColor(halo, HudTheme.A(color, .09f + .05f * breath + level * .2f));
            HudFactory.SetColor(hazard, HudTheme.A(HudTheme.Orange, urgentish ? .55f + .4f * breath : 0f));
            hazard.rectTransform.localScale = Vector3.one * Mathf.Lerp(hazard.rectTransform.localScale.x, urgentish ? 1f : 1.15f, 1f - Mathf.Exp(-6f * dt));
            HudFactory.SetColor(wonderText, Color.Lerp(color, Color.white, .35f));

            core.color = Color.Lerp(color, Color.white, .45f);
            core.Animate(time, Mathf.Max(activity, level));
            core.rectTransform.localScale = Vector3.one * (1f + level * .25f);

            float prog = urgentish && m.Urgent.Total > 0 ? m.Urgent.Loaded / (float)m.Urgent.Total : 0f;
            progress.SetSweep(Mathf.Lerp(progress.SweepDeg, prog * 360f, 1f - Mathf.Exp(-4f * dt)));
            HudFactory.SetColor(progress, HudTheme.A(HudTheme.Amber, 1f));

            // regular pulses (at the rhythm of the voice in LISTENING)
            pulseTimer += dt;
            bool fire = s == MuseVisualState.Listening ? level > .12f && pulseTimer > .3f : pulseTimer > pulseEvery;
            if (fire) { pulseTimer = 0f; pulseT = 0f; }
            if (pulseT < 1f)
            {
                pulseT = Mathf.Min(1f, pulseT + dt / 1.2f);
                float e = 1f - (1f - pulseT) * (1f - pulseT);
                pulse.Radius = Mathf.Lerp(67f, R + 24f, e);
                pulse.Thickness = Mathf.Lerp(2.5f, .8f, e);
                pulse.SetVerticesDirty();
                HudFactory.SetColor(pulse, HudTheme.A(color, (1f - pulseT) * .45f));
            }
            else HudFactory.SetColor(pulse, Color.clear);

            // urgent shock wave: three thick orange rings expanding
            shockT += dt;
            for (int i = 0; i < shock.Length; i++)
            {
                float t = (shockT - i * .22f) / 1.1f;
                if (t < 0f || t > 1f) { HudFactory.SetColor(shock[i], Color.clear); continue; }
                float e = 1f - (1f - t) * (1f - t);
                shock[i].Radius = Mathf.Lerp(70f, R + 70f, e);
                shock[i].Thickness = Mathf.Lerp(8f, 1f, e);
                shock[i].SetVerticesDirty();
                HudFactory.SetColor(shock[i], HudTheme.A(HudTheme.Orange, (1f - t) * .85f));
            }

            // status pill
            HudFactory.Set(stateText, label);
            HudFactory.SetColor(pillFill, Color.Lerp(HudTheme.GlassStrong, HudTheme.A(color, .9f), .22f));
            HudFactory.SetColor(pillStroke, HudTheme.A(color, .85f + .15f * breath));
            HudFactory.SetColor(pillDot, HudTheme.A(bright, .55f + .45f * breath));
            HudFactory.Set(detailText, urgentish ? "" : m.VisualDetail);
            if (detailBack != null && detailBack.gameObject.activeSelf == urgentish) detailBack.gameObject.SetActive(!urgentish);
            HudFactory.SetColor(detailText, s == MuseVisualState.Error ? Color.Lerp(HudTheme.Red, Color.white, .3f) : HudTheme.TextMid);

            TickPopups(dt);
        }

        private static void Spin(Graphic g, float degPerSec, float dt)
        {
            g.rectTransform.localRotation *= Quaternion.Euler(0f, 0f, degPerSec * dt);
        }

        // ------------------------------------------------------------------ decision around the circle

        private void BuildPopups()
        {
            string[] keys = { "ORDER", "PALLETS", "ROBOTS", "BENCH", "TRUCK", "VALIDATOR" };
            Vector2[] homes =
            {
                new Vector2(-262f, -2f), new Vector2(-276f, -58f), new Vector2(-262f, -114f),
                new Vector2(262f, -2f), new Vector2(276f, -58f), new Vector2(262f, -114f)
            };
            for (int i = 0; i < popups.Length; i++)
            {
                RectTransform rt = HudFactory.GlassPanel(Root, "Decision Popup " + keys[i], HudTheme.GlassStrong, null, 8, false, false);
                HudFactory.Place(rt, new Vector2(.5f, .5f), new Vector2(.5f, .5f), homes[i], new Vector2(200f, 50f));
                var p = new Popup { Rt = rt, Home = homes[i], Group = rt.gameObject.AddComponent<CanvasGroup>() };
                p.Group.alpha = 0f;
                p.Group.blocksRaycasts = false;
                p.Accent = HudFactory.Image(rt, "Accent", null, HudTheme.Orange);
                HudFactory.Place(p.Accent.rectTransform, new Vector2(0f, .5f), new Vector2(0f, .5f), Vector2.zero, new Vector2(3f, 34f));
                p.Key = HudFactory.Label(rt, "Key", keys[i], HudTheme.FontMicro, HudTheme.TextLow);
                HudFactory.Place(p.Key.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(14f, -4f), new Vector2(180f, 18f));
                p.Value = HudFactory.Text(rt, "Value", "", 19f, HudTheme.TextHi, true, TextAlignmentOptions.Left, 1f);
                HudFactory.Place(p.Value.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(14f, 4f), new Vector2(180f, 24f));
                popups[i] = p;
            }
        }

        /// <summary>Shows the decision around the circle then sends it to the AI DECISIONS panel.</summary>
        public void ShowDecision(DecisionVM d, Vector2 targetInRoot)
        {
            if (d == null) return;
            flyTarget = targetInRoot;
            Color accent = d.Urgent ? HudTheme.Orange : HudTheme.Cyan;
            string pallets = d.Pallets.Count > 0 ? string.Join(" + ", d.Pallets) : "—";
            string[] values =
            {
                (d.Urgent ? "URGENT #" : "#") + d.OrderId,
                pallets,
                d.HasPlan ? "R1 PREPARE · R2 LOAD" : "—",
                string.IsNullOrEmpty(d.Bench) ? "—" : d.Bench,
                string.IsNullOrEmpty(d.Truck) ? "—" : d.Truck,
                d.Accepted ? "ACCEPTED" : "REJECTED"
            };
            for (int i = 0; i < popups.Length; i++)
            {
                HudFactory.Set(popups[i].Value, values[i]);
                Color c = i == 5 ? (d.Accepted ? HudTheme.Green : HudTheme.Red) : accent;
                HudFactory.SetColor(popups[i].Accent, c);
                HudFactory.SetColor(popups[i].Value, i == 5 ? Color.Lerp(c, Color.white, .25f) : HudTheme.TextHi);
            }
            popupT = 0f;
        }

        private void TickPopups(float dt)
        {
            if (popupT < 0f) return;
            popupT += dt;
            const float hold = 4.6f, fly = .7f;
            for (int i = 0; i < popups.Length; i++)
            {
                Popup p = popups[i];
                float local = popupT - i * .08f;
                float a;
                Vector2 pos = p.Home;
                if (local < 0f) a = 0f;
                else if (local < .3f)
                {
                    float e = local / .3f;
                    a = e;
                    pos = p.Home + new Vector2(Mathf.Sign(p.Home.x) * -24f * (1f - e), 0f);
                }
                else if (local < hold) a = 1f;
                else if (local < hold + fly)
                {
                    float e = (local - hold) / fly;
                    e *= e;
                    a = 1f - e;
                    pos = Vector2.Lerp(p.Home, flyTarget, e);
                }
                else a = 0f;
                p.Group.alpha = a;
                p.Rt.anchoredPosition = pos;
            }
            if (popupT > hold + fly + popups.Length * .08f + .1f) popupT = -1f;
        }
    }
}
