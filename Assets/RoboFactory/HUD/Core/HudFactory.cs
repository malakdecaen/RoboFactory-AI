using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace RoboFactory.HUD
{
    /// <summary>
    /// Factory of the design system's visual components: glass panels, texts,
    /// chips, buttons, progress bars. Presentation only.
    /// </summary>
    public static class HudFactory
    {
        // ------------------------------------------------------------------ transforms

        public static RectTransform Rect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = 5; // UI
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            return rt;
        }

        /// <summary>Anchors at a point (anchor = pivot) with position and size.</summary>
        public static RectTransform Place(RectTransform rt, Vector2 anchor, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = anchor;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return rt;
        }

        public static RectTransform Place(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return rt;
        }

        /// <summary>Stretches inside the parent with margins (left, top, right, bottom).</summary>
        public static RectTransform Stretch(RectTransform rt, float left = 0f, float top = 0f, float right = 0f, float bottom = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(.5f, .5f);
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
            return rt;
        }

        /// <summary>Full-width strip stuck to the top of the parent.</summary>
        public static RectTransform TopStrip(RectTransform rt, float y, float height, float left = 0f, float right = 0f)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(.5f, 1f);
            rt.offsetMin = new Vector2(left, -y - height);
            rt.offsetMax = new Vector2(-right, -y);
            return rt;
        }

        // ------------------------------------------------------------------ elements

        public static Image Image(Transform parent, string name, Sprite sprite, Color color, bool sliced = true)
        {
            RectTransform rt = Rect(name, parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = sprite;
            img.color = color;
            img.type = sprite != null && sliced && sprite.border != Vector4.zero ? UnityEngine.UI.Image.Type.Sliced : UnityEngine.UI.Image.Type.Simple;
            img.raycastTarget = false;
            return img;
        }

        /// <summary>
        /// Tech panel: soft drop shadow, ~86 % night-blue background with cut corners,
        /// light glowing gradient at the top, thin outline and top reflection.
        /// The returned RectTransform carries no Image: the background is the "Fill" child.
        /// </summary>
        public static RectTransform GlassPanel(Transform parent, string name, Color? fill = null, Color? stroke = null,
                                               int cut = 12, bool raycast = true, bool shadow = true)
        {
            RectTransform rt = Rect(name, parent);
            if (shadow)
            {
                Image sh = Image(rt, "Shadow", HudSprites.SoftShadow(), HudTheme.Shadow);
                Stretch(sh.rectTransform, -22f, -16f, -22f, -28f);
            }
            Image bg = Image(rt, "Fill", HudSprites.ChamferFill(cut), fill ?? HudTheme.Glass);
            Stretch(bg.rectTransform);
            bg.raycastTarget = raycast; // panels block clicks towards the scene

            Image grad = Image(rt, "Gradient", HudSprites.VerticalFade(), HudTheme.A(HudTheme.Blue, .09f), false);
            grad.rectTransform.anchorMin = new Vector2(0f, .45f);
            grad.rectTransform.anchorMax = new Vector2(1f, 1f);
            grad.rectTransform.offsetMin = new Vector2(2f, 0f);
            grad.rectTransform.offsetMax = new Vector2(-2f, -2f);
            grad.rectTransform.localScale = new Vector3(1f, -1f, 1f); // light at the top

            Image line = Image(rt, "Stroke", HudSprites.ChamferStroke(cut), stroke ?? HudTheme.Stroke);
            Stretch(line.rectTransform);

            Image sheen = Image(rt, "Sheen", HudSprites.FadeLine(), HudTheme.A(HudTheme.Cyan, .75f), false);
            sheen.rectTransform.anchorMin = new Vector2(0f, 1f);
            sheen.rectTransform.anchorMax = new Vector2(1f, 1f);
            sheen.rectTransform.pivot = new Vector2(.5f, 1f);
            sheen.rectTransform.offsetMin = new Vector2(cut + 8f, -1.5f);
            sheen.rectTransform.offsetMax = new Vector2(-28f, 0f);
            return rt;
        }

        public static Image PanelFill(RectTransform panel) { Transform t = panel.Find("Fill"); return t != null ? t.GetComponent<Image>() : null; }
        public static Image PanelStroke(RectTransform panel) { Transform t = panel.Find("Stroke"); return t != null ? t.GetComponent<Image>() : null; }

        /// <summary>Pictogram from the HUD icon set.</summary>
        public static Image Icon(Transform parent, string name, HudIcon icon, float size, Color color)
        {
            Image img = Image(parent, name, HudIcons.Get(icon), color, false);
            img.rectTransform.sizeDelta = new Vector2(size, size);
            img.preserveAspect = true;
            return img;
        }

        /// <summary>Panel header: icon, title, glowing rule.</summary>
        public static TextMeshProUGUI Header(RectTransform panel, HudIcon icon, string title, float height = 48f)
        {
            RectTransform header = TopStrip(Rect("Header", panel), 0f, height);
            Image band = Image(header, "Band", null, HudTheme.A(HudTheme.Cyan, .06f));
            Stretch(band.rectTransform, 2f, 2f, 2f, 0f);
            Image ic = Icon(header, "Icon", icon, 24f, HudTheme.Cyan);
            Place(ic.rectTransform, new Vector2(0f, .5f), new Vector2(.5f, .5f), new Vector2(28f, 0f), new Vector2(24f, 24f));
            TextMeshProUGUI t = Text(header, "Title", title, HudTheme.FontTitle, HudTheme.TextHi, true, TextAlignmentOptions.Left, HudTheme.TitleSpacing);
            Place(t.rectTransform, new Vector2(0f, .5f), new Vector2(0f, .5f), new Vector2(50f, 0f), new Vector2(220f, 28f));
            Image div = Image(header, "Divider", HudSprites.FadeLine(), HudTheme.A(HudTheme.Cyan, .55f), false);
            TopStrip(div.rectTransform, height - 1f, 1f, 8f, 8f);
            return t;
        }

        public static TextMeshProUGUI Text(Transform parent, string name, string text, float size, Color color,
                                           bool strong = false, TextAlignmentOptions align = TextAlignmentOptions.Left,
                                           float spacing = 0f)
        {
            RectTransform rt = Rect(name, parent);
            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            t.font = strong ? HudFonts.Strong : HudFonts.Regular;
            if (strong && !HudFonts.StrongIsNative) t.fontStyle = FontStyles.Bold;
            t.fontSize = size;
            t.color = color;
            t.alignment = align;
            t.characterSpacing = spacing;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Ellipsis;
            t.raycastTarget = false;
            t.richText = true;
            t.text = text;
            return t;
        }

        /// <summary>Small spaced uppercase label (field titles).</summary>
        public static TextMeshProUGUI Label(Transform parent, string name, string text, float size = HudTheme.FontMicro,
                                            Color? color = null, TextAlignmentOptions align = TextAlignmentOptions.Left)
        {
            return Text(parent, name, text, size, color ?? HudTheme.TextLow, false, align, HudTheme.LabelSpacing);
        }

        /// <summary>Coloured chip (status badge): tinted background + outline + text.</summary>
        public static Chip Chip(Transform parent, string name, string text, Color color, float width = 72f, float height = 18f)
        {
            RectTransform rt = Rect(name, parent);
            rt.sizeDelta = new Vector2(width, height);
            var chip = new Chip
            {
                Root = rt,
                Fill = rt.gameObject.AddComponent<Image>(),
            };
            chip.Fill.sprite = HudSprites.Rounded(4);
            chip.Fill.type = UnityEngine.UI.Image.Type.Sliced;
            chip.Fill.raycastTarget = false;
            chip.Stroke = Image(rt, "Stroke", HudSprites.Rounded(4, 1f), color);
            Stretch(chip.Stroke.rectTransform);
            chip.Label = Text(rt, "Text", text, HudTheme.FontMicro, color, true, TextAlignmentOptions.Center, 1.5f);
            Stretch(chip.Label.rectTransform, 4f, 0f, 4f, 0f);
            chip.SetColor(color);
            return chip;
        }

        public static Button Button(Transform parent, string name, string text, Color accent, UnityAction onClick,
                                    float width = 80f, float height = 26f, float fontSize = HudTheme.FontSmall)
        {
            RectTransform rt = Rect(name, parent);
            rt.sizeDelta = new Vector2(width, height);
            // opaque dark background (readable in front of any scenery) + accent tint animated by the button
            var baseImg = rt.gameObject.AddComponent<Image>();
            baseImg.sprite = HudSprites.Rounded(4);
            baseImg.type = UnityEngine.UI.Image.Type.Sliced;
            baseImg.color = HudTheme.GlassStrong;
            Image bg = Image(rt, "Tint", HudSprites.Rounded(4), HudTheme.A(accent, .14f));
            Stretch(bg.rectTransform);
            Image stroke = Image(rt, "Stroke", HudSprites.Rounded(4, 1f), HudTheme.A(accent, .75f));
            Stretch(stroke.rectTransform);
            TextMeshProUGUI label = Text(rt, "Text", text, fontSize, HudTheme.TextHi, true, TextAlignmentOptions.Center, 2f);
            Stretch(label.rectTransform, 4f, 0f, 4f, 0f);

            var button = rt.gameObject.AddComponent<Button>();
            button.targetGraphic = bg;
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.6f, 1.6f, 1.6f, 1f);
            colors.pressedColor = new Color(2.2f, 2.2f, 2.2f, 1f);
            colors.selectedColor = Color.white;
            colors.colorMultiplier = 2f;
            colors.fadeDuration = .08f;
            button.colors = colors;
            var nav = new Navigation { mode = Navigation.Mode.None };
            button.navigation = nav;
            button.onClick.AddListener(() => HudAudio.Instance?.Play(HudSound.Click));
            if (onClick != null) button.onClick.AddListener(onClick);
            return button;
        }

        /// <summary>Two-segment progress bar (e.g. loaded + staged).</summary>
        public static DualBar DualBar(Transform parent, string name, Color main, Color second, float height = 3f)
        {
            RectTransform rt = Rect(name, parent);
            rt.sizeDelta = new Vector2(100f, height);
            var bar = new DualBar { Root = rt };
            Image track = Image(rt, "Track", null, HudTheme.A(HudTheme.Cyan, .12f));
            Stretch(track.rectTransform);
            bar.Second = Image(rt, "Second", null, second);
            bar.Main = Image(rt, "Main", null, main);
            foreach (Image img in new[] { bar.Second, bar.Main })
            {
                RectTransform r = img.rectTransform;
                r.anchorMin = new Vector2(0f, 0f);
                r.anchorMax = new Vector2(0f, 1f);
                r.pivot = new Vector2(0f, .5f);
                r.offsetMin = Vector2.zero;
                r.offsetMax = Vector2.zero;
            }
            return bar;
        }

        /// <summary>Faded horizontal separator.</summary>
        public static Image Divider(Transform parent, string name, float alpha = .35f)
        {
            Image img = Image(parent, name, HudSprites.FadeLine(), HudTheme.A(HudTheme.Cyan, alpha), false);
            img.rectTransform.sizeDelta = new Vector2(100f, 1f);
            return img;
        }

        public static T Add<T>(Transform parent, string name) where T : Graphic
        {
            RectTransform rt = Rect(name, parent);
            var g = rt.gameObject.AddComponent<T>();
            g.raycastTarget = false;
            return g;
        }

        /// <summary>Sets a text only if it changed (avoids TMP rebuilds).</summary>
        public static void Set(TMP_Text t, string value)
        {
            if (t == null) return;
            value = value ?? "";
            if (t.text != value) t.text = value;
        }

        public static void SetColor(Graphic g, Color c)
        {
            if (g != null && g.color != c) g.color = c;
        }
    }

    /// <summary>Reusable status badge.</summary>
    public sealed class Chip
    {
        public RectTransform Root;
        public Image Fill;
        public Image Stroke;
        public TextMeshProUGUI Label;

        public void SetColor(Color c)
        {
            HudFactory.SetColor(Fill, HudTheme.A(c, .16f));
            HudFactory.SetColor(Stroke, HudTheme.A(c, .8f));
            HudFactory.SetColor(Label, Color.Lerp(c, Color.white, .25f));
        }

        public void Set(string text, Color c)
        {
            HudFactory.Set(Label, text);
            SetColor(c);
        }

        public void SetActive(bool on)
        {
            if (Root.gameObject.activeSelf != on) Root.gameObject.SetActive(on);
        }
    }

    /// <summary>Two-segment bar, values 0..1 (second segment = cumulative value shown behind).</summary>
    public sealed class DualBar
    {
        public RectTransform Root;
        public Image Main;
        public Image Second;
        private float shownMain, shownSecond;

        public void Set(float main, float secondCumulative, bool instant = false)
        {
            main = Mathf.Clamp01(main);
            secondCumulative = Mathf.Clamp01(Mathf.Max(main, secondCumulative));
            if (instant) { shownMain = main; shownSecond = secondCumulative; }
            else
            {
                float k = 1f - Mathf.Exp(-8f * Time.unscaledDeltaTime);
                shownMain = Mathf.Lerp(shownMain, main, k);
                shownSecond = Mathf.Lerp(shownSecond, secondCumulative, k);
            }
            Main.rectTransform.anchorMax = new Vector2(shownMain, 1f);
            Second.rectTransform.anchorMax = new Vector2(shownSecond, 1f);
        }
    }
}
