using System.Globalization;
using RoboFactory.Logistics;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RoboFactory.HUD
{
    /// <summary>
    /// SHIPPING LABEL window: the label of the order being prepared for its truck: the consignee invented by
    /// Muse Spark, the vignette drawn by Muse Image, the order / truck / bench, and how many boxes already carry
    /// it. While Meta works the header says which step is running. Right side, above R2's camera.
    /// </summary>
    public sealed class ShippingLabelView
    {
        public RectTransform Root { get; private set; }

        private const float Pic = 144f;
        /// <summary>Whole card at 90 %: fits between R2's camera and the AI decisions panel.</summary>
        private const float Scale = .9f;
        private const float W = HudTheme.VisionW + 12f;
        private const float H = HudTheme.VisionHeader + Pic + 12f;

        private RawImage picture;
        private BracketsGraphic brackets;
        private Image stroke;
        private TextMeshProUGUI status, shipTo, name, street, address, route, models, waiting;
        private Chip state;
        private float time, flash;
        private int shownOrder = -1, shownApplied = -1;
        private Texture shownTex;

        public void Build(RectTransform parent)
        {
            Root = HudFactory.GlassPanel(parent, "Shipping Label", HudTheme.GlassStrong, HudTheme.A(HudTheme.Cyan, .75f), 10);
            HudFactory.Place(Root, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-HudTheme.Margin, HudTheme.BottomRowY), new Vector2(W, H));
            Root.localScale = new Vector3(Scale, Scale, 1f);
            stroke = HudFactory.PanelStroke(Root);

            Image icon = HudFactory.Icon(Root, "Icon", HudIcon.Box, 20f, HudTheme.Cyan);
            HudFactory.Place(icon.rectTransform, new Vector2(0f, 1f), new Vector2(.5f, .5f), new Vector2(20f, -17f), new Vector2(20f, 20f));
            TextMeshProUGUI title = HudFactory.Text(Root, "Title", "LABEL  <color=#" + HudTheme.Hex(HudTheme.TextMid) + ">SPARK + IMAGE</color>", 16f,
                                                    HudTheme.TextHi, true, TextAlignmentOptions.Left, 2f);
            HudFactory.Place(title.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(36f, -6f), new Vector2(180f, 22f));
            status = HudFactory.Text(Root, "Status", "", HudTheme.FontMicro, HudTheme.TextMid, true, TextAlignmentOptions.Right, 1f);
            HudFactory.Place(status.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-12f, -8f), new Vector2(140f, 20f));

            RectTransform frame = HudFactory.Rect("Vignette", Root);
            HudFactory.Place(frame, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(6f, -HudTheme.VisionHeader), new Vector2(Pic, Pic));
            picture = frame.gameObject.AddComponent<RawImage>();
            picture.color = new Color(.05f, .08f, .12f, 1f);
            picture.raycastTarget = false;
            brackets = HudFactory.Add<BracketsGraphic>(frame, "Brackets");
            HudFactory.Stretch(brackets.rectTransform);
            brackets.Length = 16f;
            brackets.Width = 2f;
            brackets.Inset = 5f;
            brackets.raycastTarget = false;
            waiting = HudFactory.Text(frame, "Waiting", "NO ORDER\nTO SHIP YET", HudTheme.FontMicro, HudTheme.TextLow, true, TextAlignmentOptions.Center, 1f);
            waiting.textWrappingMode = TextWrappingModes.Normal;
            HudFactory.Stretch(waiting.rectTransform);

            float x = 6f + Pic + 10f, cw = W - x - 10f, top = -HudTheme.VisionHeader;
            shipTo = HudFactory.Label(Root, "Ship To", "SHIP TO");
            HudFactory.Place(shipTo.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x, top), new Vector2(cw, 18f));
            name = Line("Name", x, top - 18f, cw, HudTheme.FontValue, true, HudTheme.TextHi);
            street = Line("Street", x, top - 42f, cw, HudTheme.FontMicro, false, HudTheme.TextHi);
            address = Line("Address", x, top - 60f, cw, HudTheme.FontMicro, false, HudTheme.TextHi);
            route = Line("Route", x, top - 80f, cw, HudTheme.FontSmall, true, HudTheme.Cyan);
            models = Line("Models", x, top - 100f, cw, HudTheme.FontMicro, false, HudTheme.TextMid);

            state = HudFactory.Chip(Root, "State", "", HudTheme.TextMid, cw, 26f);
            HudFactory.Place(state.Root, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x, top - Pic + 26f), new Vector2(cw, 26f));
            state.Label.enableAutoSizing = true;
            state.Label.fontSizeMin = 9f;
            state.Label.fontSizeMax = HudTheme.FontMicro;
        }

        private TextMeshProUGUI Line(string n, float x, float y, float w, float size, bool strong, Color c)
        {
            TextMeshProUGUI t = HudFactory.Text(Root, n, "", size, c, strong, TextAlignmentOptions.Left, .5f);
            HudFactory.Place(t.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x, y), new Vector2(w, size + 6f));
            return t;
        }

        /// <param name="bottom">Height where the panel starts (just above R2's camera window, which can be enlarged).</param>
        /// <param name="ceiling">Lowest edge of the panels above (MuseWonder decisions): if the label would reach it, it goes
        /// next to R2's camera instead, <paramref name="besideX"/> from the right edge.</param>
        public void Tick(float dt, bool visible, float bottom, float ceiling, float besideX)
        {
            ShippingLabels labels = ShippingLabels.Instance;
            bool on = visible && labels != null;
            if (Root.gameObject.activeSelf != on) Root.gameObject.SetActive(on);
            if (!on) return;
            time += dt;
            const float gap = 5f;
            Root.anchoredPosition = bottom - HudTheme.Gap + gap + H * Scale + gap <= ceiling
                ? new Vector2(-HudTheme.Margin, bottom - HudTheme.Gap + gap) : new Vector2(-besideX, HudTheme.BottomRowY);

            ShippingLabels.Label l = labels.Latest;
            if (l != null && (l.OrderId != shownOrder || l.Applied != shownApplied || l.Image != shownTex))
            {
                shownOrder = l.OrderId;
                shownApplied = l.Applied;
                shownTex = l.Image;
                flash = 1f;
            }
            flash = Mathf.MoveTowards(flash, 0f, dt * 1.5f);
            string dots = new string('.', 1 + (int)(time * 3f) % 3);

            Texture tex = l != null ? l.Image : null;
            if (picture.texture != tex) picture.texture = tex;
            HudFactory.SetColor(picture, tex != null ? Color.white : new Color(.05f, .08f, .12f, 1f));
            bool wait = tex == null;
            if (waiting.gameObject.activeSelf != wait) waiting.gameObject.SetActive(wait);
            if (wait) HudFactory.Set(waiting, l == null ? "NO ORDER\nTO SHIP YET" : l.Status == "ready" ? "PRINTED LABEL\n(NO VIGNETTE)" : "MUSE IMAGE\nDRAWING" + dots);

            if (l == null)
            {
                HudFactory.Set(status, labels.BoxesLabelled + " BOXES LABELLED");
                foreach (TextMeshProUGUI t in new[] { name, street, address, route, models }) HudFactory.Set(t, "");
                if (state.Root.gameObject.activeSelf) state.Root.gameObject.SetActive(false);
                Colour(HudTheme.Cyan);
                return;
            }

            HudFactory.Set(status, l.Status == "destination" ? "SPARK WRITING" + dots : l.Status == "image" ? "IMAGE DRAWING" + dots : "ORDER #" + l.OrderId);
            HudFactory.SetColor(status, l.Status == "ready" ? HudTheme.TextMid : HudTheme.Cyan);
            HudFactory.Set(name, l.Status == "destination" ? "…" : l.Name);
            HudFactory.Set(street, l.Street);
            HudFactory.Set(address, l.Address);
            HudFactory.Set(route, "#" + l.OrderId + (l.Bench.Length > 0 ? "  " + l.Bench : "") + (l.Truck.Length > 0 ? " → " + l.Truck : ""));
            string m = "";
            if (l.DestModel.Length > 0) m += (l.DestFromMeta ? l.DestModel + " " + S(l.DestMs) : "printed (no Spark answer)");
            if (l.ImageModel.Length > 0) m += " · " + l.ImageModel + " " + S(l.ImageMs);
            HudFactory.Set(models, m);

            if (!state.Root.gameObject.activeSelf) state.Root.gameObject.SetActive(true);
            int total = Mathf.Max(l.Boxes, l.Applied);
            Color c = l.Applied > 0 ? HudTheme.Green : HudTheme.Cyan;
            HudFactory.Set(state.Label, l.Applied > 0 ? "LABELLED " + l.Applied + (total > 0 ? "/" + total : "") + " · READY TO LOAD"
                                       : l.Status == "ready" ? "WAITING FOR THE BOXES ON THE BENCH" : "PREPARING THE LABEL");
            state.SetColor(c);
            state.Fill.color = HudTheme.A(c, .16f + .25f * flash);
            Colour(c);
        }

        private void Colour(Color c)
        {
            HudFactory.SetColor(brackets, HudTheme.A(Color.Lerp(c, Color.white, flash * .5f), .95f));
            HudFactory.SetColor(stroke, HudTheme.A(c, .55f + .45f * flash));
        }

        private static string S(int ms) => (ms / 1000f).ToString("F1", CultureInfo.InvariantCulture) + " s";
    }
}
