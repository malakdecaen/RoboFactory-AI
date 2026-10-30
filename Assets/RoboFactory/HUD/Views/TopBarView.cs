using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RoboFactory.HUD
{
    /// <summary>
    /// Top strip (V2): identity + time, mode selector, LIVE/MOCK badge, and two
    /// context cards around the circle — CURRENT ORDER and TRUCK IN USE (real data)
    /// — to understand the situation in three seconds. Short notification of the cinema mode.
    /// </summary>
    public sealed class TopBarView
    {
        public RectTransform Brand { get; private set; }
        public RectTransform Switcher { get; private set; }
        public RectTransform FlankLeft { get; private set; }
        public RectTransform FlankRight { get; private set; }
        public RectTransform Toast { get; private set; }
        public Action<HudMode> ModeRequested;
        public Action SoundToggled;
        public Func<bool> SoundMuted;
        public Action CamToggled;
        public Func<bool> CamOrbit;
        public Func<string> CamLabel;

        private TextMeshProUGUI clock, toastText;
        private Chip liveChip;
        private Image soundIcon;
        private Image soundBg;
        private Image camBg, camIcon;
        private TextMeshProUGUI camHint, camLabel;
        /// <summary>Camera button (right of the brand block), visible in every mode.</summary>
        public RectTransform CamSwitch { get; private set; }
        private readonly Button[] modeButtons = new Button[3];
        private readonly TextMeshProUGUI[] modeLabels = new TextMeshProUGUI[3];
        private Flank orderCard, truckCard;
        private CanvasGroup toastGroup;
        private float toastT = -1f;
        private float time;

        private sealed class Flank
        {
            public RectTransform Rt;
            public Image Icon, Accent;
            public TextMeshProUGUI Value, Sub;
            public string Last = "";
            public float FlashT = 10f;
            public Image Stroke;
        }

        public void Build(RectTransform parent)
        {
            Brand = HudFactory.Rect("Brand", parent);
            HudFactory.Place(Brand, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(HudTheme.Margin, -14f), new Vector2(HudTheme.OrdersWidth, 58f));
            RectTransform brandBack = HudFactory.GlassPanel(Brand, "Back", HudTheme.Glass, HudTheme.StrokeSoft, 10, false);
            HudFactory.Stretch(brandBack, -2f, -2f, 0f, 0f);
            Image mark = HudFactory.Image(Brand, "Mark", HudSprites.Rounded(3, 2f), HudTheme.Cyan);
            HudFactory.Place(mark.rectTransform, new Vector2(0f, 1f), new Vector2(.5f, .5f), new Vector2(22f, -20f), new Vector2(18f, 18f));
            mark.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 45f);
            Image dot = HudFactory.Image(Brand, "Mark Core", HudSprites.Circle(), HudTheme.Cyan, false);
            HudFactory.Place(dot.rectTransform, new Vector2(0f, 1f), new Vector2(.5f, .5f), new Vector2(22f, -20f), new Vector2(6f, 6f));
            TextMeshProUGUI name = HudFactory.Text(Brand, "Name", "ROBOFACTORY AI", 21f, HudTheme.TextHi, true, TextAlignmentOptions.Left, 4f);
            HudFactory.Place(name.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(42f, -6f), new Vector2(270f, 28f));
            clock = HudFactory.Text(Brand, "Clock", "", HudTheme.FontSmall, HudTheme.TextMid, false, TextAlignmentOptions.Left, 1.5f);
            HudFactory.Place(clock.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(43f, -33f), new Vector2(270f, 20f));

            Switcher = HudFactory.Rect("Mode Switcher", parent);
            HudFactory.Place(Switcher, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-HudTheme.Margin, -20f), new Vector2(480f, 34f));
            string[] names = { "CINEMATIC", "OPERATIONS", "DEBUG" };
            float[] widths = { 122f, 136f, 92f };
            float x = 0f;
            for (int i = 2; i >= 0; i--)
            {
                HudMode mode = (HudMode)i;
                Button b = HudFactory.Button(Switcher, names[i], names[i], HudTheme.Cyan, () => ModeRequested?.Invoke(mode), widths[i], 32f, HudTheme.FontMicro);
                HudFactory.Place((RectTransform)b.transform, new Vector2(1f, .5f), new Vector2(1f, .5f), new Vector2(-x, 0f), new Vector2(widths[i], 32f));
                x += widths[i] + 4f;
                modeButtons[i] = b;
                modeLabels[i] = b.GetComponentInChildren<TextMeshProUGUI>();
                modeLabels[i].characterSpacing = 1f;
            }
            liveChip = HudFactory.Chip(Switcher, "Live", "LIVE", HudTheme.Green, 64f, 32f);
            HudFactory.Place(liveChip.Root, new Vector2(1f, .5f), new Vector2(1f, .5f), new Vector2(-x - 6f, 0f), new Vector2(64f, 32f));
            Button sound = HudFactory.Button(Switcher, "Sound", "", HudTheme.Cyan, () => SoundToggled?.Invoke(), 40f, 32f);
            HudFactory.Place((RectTransform)sound.transform, new Vector2(1f, .5f), new Vector2(1f, .5f), new Vector2(-x - 76f, 0f), new Vector2(40f, 32f));
            soundBg = (Image)sound.targetGraphic;
            soundIcon = HudFactory.Icon(sound.transform, "Icon", HudIcon.Speaker, 22f, HudTheme.TextHi);

            // camera button: scene view <-> orbit view (C key), between the brand and the order card
            CamSwitch = HudFactory.Rect("Camera Switch", parent);
            HudFactory.Place(CamSwitch, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(HudTheme.Margin + HudTheme.OrdersWidth + 10f, -14f), new Vector2(118f, 58f));
            Button camButton = HudFactory.Button(CamSwitch, "Camera View", "", HudTheme.Cyan, () => CamToggled?.Invoke(), 104f, 32f);
            HudFactory.Place((RectTransform)camButton.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(104f, 32f));
            camBg = (Image)camButton.targetGraphic;
            camIcon = HudFactory.Icon(camButton.transform, "Icon", HudIcon.Camera, 20f, HudTheme.TextHi);
            HudFactory.Place(camIcon.rectTransform, new Vector2(0f, .5f), new Vector2(.5f, .5f), new Vector2(20f, 0f), new Vector2(20f, 20f));
            camLabel = HudFactory.Text(camButton.transform, "Label", "SCENE", HudTheme.FontMicro, HudTheme.TextHi, true, TextAlignmentOptions.Left, 1.5f);
            HudFactory.Place(camLabel.rectTransform, new Vector2(0f, .5f), new Vector2(0f, .5f), new Vector2(38f, 0f), new Vector2(62f, 20f));
            camHint = HudFactory.Text(CamSwitch, "Camera Hint", "drag · scroll · arrows", HudTheme.FontMicro, HudTheme.TextMid, false, TextAlignmentOptions.Left);
            HudFactory.Place(camHint.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(2f, -38f), new Vector2(170f, 18f));

            orderCard = BuildFlank(parent, "Active Order Card", -1, HudIcon.Order, "ORDER IN PROGRESS");
            truckCard = BuildFlank(parent, "Truck Card", 1, HudIcon.Truck, "TRUCK IN USE");
            FlankLeft = orderCard.Rt;
            FlankRight = truckCard.Rt;

            Toast = HudFactory.Rect("Cinematic Toast Holder", parent);
            HudFactory.Place(Toast, new Vector2(.5f, 1f), new Vector2(.5f, 1f), new Vector2(0f, -330f), new Vector2(640f, 44f));
            RectTransform toastPanel = HudFactory.GlassPanel(Toast, "Cinematic Toast", HudTheme.GlassStrong, null, 10, false);
            HudFactory.Stretch(toastPanel);
            toastGroup = toastPanel.gameObject.AddComponent<CanvasGroup>();
            toastGroup.alpha = 0f;
            toastGroup.blocksRaycasts = false;
            toastText = HudFactory.Text(toastPanel, "Text", "", HudTheme.FontBody, HudTheme.TextHi, true, TextAlignmentOptions.Center, 1f);
            HudFactory.Stretch(toastText.rectTransform, 14f, 0f, 14f, 0f);
        }

        private static Flank BuildFlank(RectTransform parent, string name, int side, HudIcon icon, string label)
        {
            var f = new Flank();
            f.Rt = HudFactory.GlassPanel(parent, name, HudTheme.Glass, HudTheme.Stroke, 10, false);
            HudFactory.Place(f.Rt, new Vector2(.5f, 1f), new Vector2(.5f, 1f), new Vector2(side * 304f, -14f), new Vector2(276f, 74f));
            f.Stroke = HudFactory.PanelStroke(f.Rt);
            f.Accent = HudFactory.Image(f.Rt, "Accent", null, HudTheme.Cyan);
            HudFactory.Place(f.Accent.rectTransform, new Vector2(0f, .5f), new Vector2(0f, .5f), new Vector2(0f, 0f), new Vector2(3f, 46f));
            f.Icon = HudFactory.Icon(f.Rt, "Icon", icon, 30f, HudTheme.Cyan);
            HudFactory.Place(f.Icon.rectTransform, new Vector2(0f, .5f), new Vector2(.5f, .5f), new Vector2(30f, 0f), new Vector2(30f, 30f));
            TextMeshProUGUI l = HudFactory.Label(f.Rt, "Label", label, HudTheme.FontMicro);
            HudFactory.Place(l.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(56f, -6f), new Vector2(210f, 18f));
            f.Value = HudFactory.Text(f.Rt, "Value", "—", HudTheme.FontKey, HudTheme.TextHi, true, TextAlignmentOptions.Left, 1f);
            HudFactory.Place(f.Value.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(56f, -22f), new Vector2(212f, 30f));
            f.Sub = HudFactory.Text(f.Rt, "Sub", "", HudTheme.FontSmall, HudTheme.TextMid);
            HudFactory.Place(f.Sub.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(56f, 5f), new Vector2(212f, 20f));
            return f;
        }

        public void ShowToast(string text, Color c)
        {
            HudFactory.Set(toastText, text);
            HudFactory.SetColor(toastText, Color.Lerp(c, Color.white, .35f));
            toastT = 0f;
        }

        public void Tick(float dt, HudModel m, HudMode mode)
        {
            time += dt;
            HudFactory.Set(clock, DateTime.Now.ToString("HH:mm:ss") + "  ·  AUTONOMOUS WAREHOUSE");
            for (int i = 0; i < 3; i++)
            {
                bool on = (int)mode == i;
                HudFactory.SetColor((Image)modeButtons[i].targetGraphic, HudTheme.A(HudTheme.Cyan, on ? .38f : .08f));
                HudFactory.SetColor(modeLabels[i], on ? HudTheme.TextHi : HudTheme.TextMid);
            }
            string md = (m.Status.Mode ?? "").ToUpperInvariant();
            Color lc = md == "LIVE" ? HudTheme.Green : md == "MOCK" ? HudTheme.Amber : HudTheme.Red;
            liveChip.Set(md == "" ? "—" : md, lc);
            bool muted = SoundMuted != null && SoundMuted();
            Sprite want = HudIcons.Get(muted ? HudIcon.SpeakerOff : HudIcon.Speaker);
            if (soundIcon.sprite != want) soundIcon.sprite = want;
            HudFactory.SetColor(soundIcon, muted ? HudTheme.TextLow : HudTheme.TextHi);
            HudFactory.SetColor(soundBg, HudTheme.A(HudTheme.Cyan, muted ? .04f : .16f));
            bool orbit = CamOrbit != null && CamOrbit();
            HudFactory.SetColor(camBg, HudTheme.A(HudTheme.Cyan, orbit ? .38f : .08f));
            HudFactory.SetColor(camIcon, orbit ? HudTheme.TextHi : HudTheme.TextMid);
            HudFactory.Set(camLabel, CamLabel != null ? CamLabel() : orbit ? "ORBIT" : "SCENE");
            if (camHint.gameObject.activeSelf != orbit) camHint.gameObject.SetActive(orbit);

            // ---- current order (the most recently started, otherwise the urgent one / the first active one)
            OrderVM active = m.FindOrder(m.Urgent.OrderId);
            if (active == null || active.Category == OrderCategory.Done) active = m.FindOrder(m.ActiveOrderId);
            if (active == null || active.Category == OrderCategory.Done)
                foreach (OrderVM o in m.Orders)
                    if (o.Category == OrderCategory.Active || o.Category == OrderCategory.Urgent) { active = o; break; }
            if (active != null && active.Category != OrderCategory.Done)
            {
                Color c = OrdersPanelView.CategoryColor(active.Category);
                SetFlank(orderCard, "#" + active.Id + "   " + active.Composition,
                         (active.Urgent ? "URGENT · " : "") + OrdersPanelView.StatusLabel(active.Status) + "  ·  " + active.Loaded + "/" + active.Total + (active.Urgent ? "" : " loaded"), c);
            }
            else SetFlank(orderCard, "—", "no order in progress", HudTheme.TextLow);

            // ---- truck in use: the current order's truck, otherwise an open truck
            TruckVM truck = null;
            if (active != null && active.Truck != "")
                foreach (TruckVM t in m.Trucks) if (t.Id == active.Truck) truck = t;
            if (truck == null) foreach (TruckVM t in m.Trucks) if (t.Selected || t.Door != "Closed") { truck = t; break; }
            if (truck != null)
            {
                Color dc = TruckStripView.DoorColor(truck.Door);
                SetFlank(truckCard, truck.Id + "   <size=18><color=#" + HudTheme.Hex(dc) + ">" + truck.Door.ToUpperInvariant() + "</color></size>",
                         (truck.ReservedOrder >= 0 ? "order #" + truck.ReservedOrder + "  ·  " : "") + truck.Loaded + "/" + truck.Capacity + " slots", truck.Selected ? HudTheme.Orange : HudTheme.Cyan);
            }
            else SetFlank(truckCard, "—", "all doors closed", HudTheme.TextLow);

            TickFlank(orderCard, dt);
            TickFlank(truckCard, dt);

            if (toastT >= 0f)
            {
                toastT += dt;
                toastGroup.alpha = Mathf.Clamp01(toastT / .3f) * (1f - Mathf.Clamp01((toastT - 4f) / .5f));
                if (toastT > 4.6f) { toastT = -1f; toastGroup.alpha = 0f; }
            }
        }

        private static void SetFlank(Flank f, string value, string sub, Color c)
        {
            if (f.Last != value) { f.Last = value; f.FlashT = 0f; }
            HudFactory.Set(f.Value, value);
            HudFactory.Set(f.Sub, sub);
            HudFactory.SetColor(f.Icon, c);
            HudFactory.SetColor(f.Accent, c);
        }

        private static void TickFlank(Flank f, float dt)
        {
            f.FlashT += dt;
            float k = Mathf.Clamp01(1f - f.FlashT / 1.5f);
            HudFactory.SetColor(f.Stroke, HudTheme.A(Color.Lerp(HudTheme.Cyan, f.Accent.color, k), .5f + .5f * k));
        }
    }
}
