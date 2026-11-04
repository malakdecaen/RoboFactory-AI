using System.Globalization;
using RoboFactory.Packing;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RoboFactory.HUD
{
    /// <summary>
    /// PACKING SCAN window: the very picture the scan camera of the packing cell sent to Muse Spark, with Spark's
    /// answer (product, defect, confidence, latency) and the cell's verdict: OK → which carton, DEFECT → bin,
    /// UNSURE → rescan, or no carton needs it → next lap. A pulse on the frame for each new answer; while a
    /// picture is being analysed the header says so. Shown only when the packing cell exists (left side, above
    /// R1's camera).
    /// </summary>
    public sealed class PackingScanView
    {
        public RectTransform Root { get; private set; }

        private const float Pic = 164f;
        private const float W = HudTheme.VisionW + 12f;
        private const float H = HudTheme.VisionHeader + Pic + 12f;

        private RawImage picture;
        private BracketsGraphic brackets;
        private Image stroke, scanLine;
        private TextMeshProUGUI title, status, product, kind, defect, confText, latency, waiting;
        private DualBar confBar;
        private Chip verdict;
        private float time, flash;
        private int shownSerial = -1, shownLap = -1;

        public void Build(RectTransform parent)
        {
            Root = HudFactory.GlassPanel(parent, "Packing Scan", HudTheme.GlassStrong, HudTheme.A(HudTheme.Cyan, .75f), 10);
            float y = HudTheme.BottomRowY + HudTheme.VisionH + HudTheme.VisionHeader + 6f + HudTheme.Gap;
            HudFactory.Place(Root, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(HudTheme.Margin, y), new Vector2(W, H));
            stroke = HudFactory.PanelStroke(Root);

            Image cam = HudFactory.Icon(Root, "Icon", HudIcon.Camera, 20f, HudTheme.Cyan);
            HudFactory.Place(cam.rectTransform, new Vector2(0f, 1f), new Vector2(.5f, .5f), new Vector2(20f, -17f), new Vector2(20f, 20f));
            title = HudFactory.Text(Root, "Title", "SCAN  <color=#" + HudTheme.Hex(HudTheme.TextMid) + ">MUSE SPARK</color>", 16f, HudTheme.TextHi, true,
                                    TextAlignmentOptions.Left, 2f);
            HudFactory.Place(title.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(36f, -6f), new Vector2(170f, 22f));
            status = HudFactory.Text(Root, "Status", "", HudTheme.FontMicro, HudTheme.TextMid, true, TextAlignmentOptions.Right, 1f);
            HudFactory.Place(status.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-12f, -8f), new Vector2(150f, 20f));

            // picture sent to Spark
            RectTransform frame = HudFactory.Rect("Picture", Root);
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
            scanLine = HudFactory.Image(frame, "Scan", HudSprites.FadeLine(), HudTheme.A(HudTheme.Cyan, .18f), false);
            scanLine.rectTransform.anchorMin = new Vector2(0f, 0f);
            scanLine.rectTransform.anchorMax = new Vector2(1f, 0f);
            scanLine.rectTransform.sizeDelta = new Vector2(0f, 1.5f);
            waiting = HudFactory.Text(frame, "Waiting", "WAITING FOR\nA PRODUCT", HudTheme.FontMicro, HudTheme.TextLow, true, TextAlignmentOptions.Center, 1f);
            waiting.textWrappingMode = TextWrappingModes.Normal;
            HudFactory.Stretch(waiting.rectTransform);

            // Spark's answer
            float x = 6f + Pic + 10f, cw = W - x - 10f, top = -HudTheme.VisionHeader;
            product = HudFactory.Text(Root, "Product", "—", HudTheme.FontValue, HudTheme.TextHi, true, TextAlignmentOptions.Left, 1f);
            HudFactory.Place(product.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x, top), new Vector2(cw, 24f));
            kind = Field("KIND", x, top - 28f, cw);
            defect = Field("DEFECT", x, top - 52f, cw);
            TextMeshProUGUI confLabel = HudFactory.Label(Root, "Conf Label", "CONFIDENCE");
            HudFactory.Place(confLabel.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x, top - 76f), new Vector2(100f, 18f));
            confText = HudFactory.Text(Root, "Conf", "", HudTheme.FontSmall, HudTheme.TextHi, true, TextAlignmentOptions.Right, 1f);
            HudFactory.Place(confText.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x + cw - 50f, top - 76f), new Vector2(50f, 18f));
            confBar = HudFactory.DualBar(Root, "Conf Bar", HudTheme.Cyan, HudTheme.A(HudTheme.Cyan, .3f), 4f);
            HudFactory.Place(confBar.Root, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x, top - 98f), new Vector2(cw, 4f));
            latency = HudFactory.Text(Root, "Latency", "", HudTheme.FontMicro, HudTheme.TextMid, false, TextAlignmentOptions.Left, 1f);
            HudFactory.Place(latency.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x, top - 108f), new Vector2(cw, 18f));

            // verdict of the cell
            verdict = HudFactory.Chip(Root, "Verdict", "", HudTheme.TextMid, cw, 30f);
            HudFactory.Place(verdict.Root, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x, top - Pic + 30f), new Vector2(cw, 30f));
            verdict.Label.enableAutoSizing = true;
            verdict.Label.fontSizeMin = 9f;
            verdict.Label.fontSizeMax = HudTheme.FontSmall;
            verdict.Label.textWrappingMode = TextWrappingModes.Normal;
            verdict.Root.gameObject.SetActive(false);
        }

        private TextMeshProUGUI Field(string label, float x, float y, float w)
        {
            TextMeshProUGUI l = HudFactory.Label(Root, label + " Label", label);
            HudFactory.Place(l.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x, y), new Vector2(64f, 20f));
            TextMeshProUGUI v = HudFactory.Text(Root, label, "", HudTheme.FontSmall, HudTheme.TextHi, true, TextAlignmentOptions.Right, 1f);
            HudFactory.Place(v.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x + 60f, y), new Vector2(w - 60f, 20f));
            return v;
        }

        /// <param name="bottom">Height where the panel starts (just above R1's camera window, which can be enlarged).</param>
        public void Tick(float dt, bool visible, float bottom)
        {
            PackingCell cell = PackingCell.Instance;
            bool on = visible && cell != null && cell.isActiveAndEnabled;
            if (Root.gameObject.activeSelf != on) Root.gameObject.SetActive(on);
            if (!on) return;
            time += dt;
            Root.anchoredPosition = new Vector2(HudTheme.Margin, bottom);

            // header: picture being analysed, else the totals
            PackingItem pending = cell.ScansPending > 0 ? cell.LastCaptured : null;
            if (pending != null)
            {
                HudFactory.Set(status, "ANALYSING #" + pending.Serial + new string('.', 1 + (int)(time * 3f) % 3));
                HudFactory.SetColor(status, HudTheme.Cyan);
            }
            else
            {
                HudFactory.Set(status, cell.Taken + " PACKED · " + cell.Rejected + " BIN");
                HudFactory.SetColor(status, HudTheme.TextMid);
            }

            PackingItem it = cell.LastScanned;
            // the photo just taken is shown at once under "ANALYSING" (the answer comes seconds later, the product has moved on)
            PackingItem fresh = cell.LastCaptured;
            bool showFresh = fresh != null && fresh.ScanImage != null && !fresh.Scanned && !fresh.ScanFailed && Time.time - cell.CapturedAt < 4f;
            bool has = !showFresh && it != null && (it.Scanned || it.ScanFailed) && !string.IsNullOrEmpty(it.Verdict);
            if (has && (it.Serial != shownSerial || it.Laps != shownLap))
            {
                shownSerial = it.Serial;
                shownLap = it.Laps;
                flash = 1f;
            }
            flash = Mathf.MoveTowards(flash, 0f, dt * 1.5f);

            Texture tex = showFresh ? fresh.ScanImage : has ? it.ScanImage : null;
            if (picture.texture != tex) picture.texture = tex;
            HudFactory.SetColor(picture, tex != null ? Color.white : new Color(.05f, .08f, .12f, 1f));
            if (waiting.gameObject.activeSelf != (tex == null)) waiting.gameObject.SetActive(tex == null);

            Color c = !has ? HudTheme.Cyan
                    : it.VerdictKind == "ok" ? HudTheme.Green
                    : it.VerdictKind == "reject" ? HudTheme.Red
                    : it.VerdictKind == "rescan" ? HudTheme.Amber : HudTheme.Cyan;
            HudFactory.SetColor(brackets, HudTheme.A(Color.Lerp(c, Color.white, flash * .5f), .95f));
            HudFactory.SetColor(stroke, HudTheme.A(c, .55f + .45f * flash));

            if (verdict.Root.gameObject.activeSelf != has) verdict.Root.gameObject.SetActive(has);
            if (!has)
            {
                HudFactory.Set(product, "—");
                HudFactory.Set(kind, "");
                HudFactory.Set(defect, "");
                HudFactory.Set(confText, "");
                HudFactory.Set(latency, "");
                confBar.Set(0f, 0f);
            }
            else
            {
                HudFactory.Set(product, "PRODUCT #" + it.Serial + (it.Laps > 0 ? "  <size=70%><color=#" + HudTheme.Hex(HudTheme.TextMid) + ">LAP " + (it.Laps + 1) + "</color></size>" : ""));
                if (it.ScanFailed)
                {
                    HudFactory.Set(kind, "—");
                    HudFactory.Set(defect, "—");
                    HudFactory.Set(confText, "");
                    confBar.Set(0f, 0f);
                }
                else
                {
                    HudFactory.Set(kind, it.ScanKind.ToUpperInvariant());
                    bool bad = it.ScanDefect != "none" && it.ScanDefect.Length > 0;
                    HudFactory.Set(defect, bad ? it.ScanDefect.ToUpperInvariant() : "NONE");
                    HudFactory.SetColor(defect, bad ? HudTheme.Red : HudTheme.Green);
                    HudFactory.Set(confText, Mathf.RoundToInt(it.ScanConfidence * 100f) + "%");
                    confBar.Set(it.ScanConfidence, it.ScanConfidence);
                    HudFactory.SetColor(confBar.Main, it.ScanConfidence >= cell.MinRejectConfidence ? HudTheme.Cyan : HudTheme.Amber);
                }
                HudFactory.Set(latency, (it.ScanModel.Length > 0 ? it.ScanModel : "muse-spark") + " · " +
                                        (it.ScanLatencyMs / 1000f).ToString("F1", CultureInfo.InvariantCulture) + " s");
                HudFactory.Set(verdict.Label, it.Verdict);
                verdict.SetColor(c);
                verdict.Fill.color = HudTheme.A(c, .18f + .25f * flash);
                HudFactory.SetColor(verdict.Label, Color.Lerp(c, Color.white, .35f));
            }

            float ly = Mathf.Repeat(time * .45f, 1f);
            scanLine.rectTransform.anchorMin = new Vector2(0f, ly);
            scanLine.rectTransform.anchorMax = new Vector2(1f, ly);
            HudFactory.SetColor(scanLine, HudTheme.A(HudTheme.Cyan, pending != null ? .35f : .12f));
        }
    }
}
