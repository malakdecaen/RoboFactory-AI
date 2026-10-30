using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RoboFactory.HUD
{
    /// <summary>
    /// ROBOT VISION window (V2) in the style of a surveillance feed: crisp 320×180 video (render
    /// supersampled according to the real screen resolution), high-contrast frame, navigation
    /// target KNOWN TO THE CONTROLLER clearly distinguished from a visual detection
    /// (no detector connected → « DETECTION: NOT CONNECTED », no box drawn).
    /// Enlarges on click.
    /// </summary>
    public sealed class RobotVisionView
    {
        public RectTransform Root { get; private set; }
        public bool Expanded { get; private set; }
        public readonly RobotVisionFeed Feed = new RobotVisionFeed();
        public float CompactFps = 10f;
        public float ExpandedFps = 20f;
        /// <summary>Screen pixels / canvas units factor (to size the RenderTexture).</summary>
        public float PixelScale = 1f;

        private readonly bool rightSide;
        private readonly string robotId;
        private RawImage video;
        private TextMeshProUGUI title, fpsText, targetLabel, targetText, routeText;
        private Image liveDot, stroke, scanLine;
        private BracketsGraphic brackets;
        private float time;
        private float size, sizeTarget;

        public RobotVisionView(string robotId, bool rightSide)
        {
            this.robotId = robotId;
            this.rightSide = rightSide;
        }

        public void Build(RectTransform parent)
        {
            Vector2 anchor = rightSide ? new Vector2(1f, 0f) : new Vector2(0f, 0f);
            Root = HudFactory.GlassPanel(parent, "Robot Vision " + robotId, HudTheme.GlassStrong, HudTheme.A(HudTheme.Cyan, .75f), 10);
            HudFactory.Place(Root, anchor, anchor, new Vector2(rightSide ? -HudTheme.Margin : HudTheme.Margin, HudTheme.BottomRowY),
                new Vector2(HudTheme.VisionW + 12f, HudTheme.VisionH + HudTheme.VisionHeader + 6f));
            stroke = HudFactory.PanelStroke(Root);

            Image cam = HudFactory.Icon(Root, "Icon", HudIcon.Camera, 20f, HudTheme.Cyan);
            HudFactory.Place(cam.rectTransform, new Vector2(0f, 1f), new Vector2(.5f, .5f), new Vector2(20f, -17f), new Vector2(20f, 20f));
            title = HudFactory.Text(Root, "Title", "", 16f, HudTheme.TextHi, true, TextAlignmentOptions.Left, 2f);
            HudFactory.Place(title.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(36f, -6f), new Vector2(220f, 22f));
            liveDot = HudFactory.Image(Root, "Live Dot", HudSprites.Circle(), HudTheme.Red, false);
            HudFactory.Place(liveDot.rectTransform, new Vector2(1f, 1f), new Vector2(.5f, .5f), new Vector2(-84f, -17f), new Vector2(9f, 9f));
            fpsText = HudFactory.Text(Root, "Fps", "", HudTheme.FontMicro, HudTheme.TextMid, true, TextAlignmentOptions.Left, 1f);
            HudFactory.Place(fpsText.rectTransform, new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(-74f, -8f), new Vector2(72f, 20f));

            RectTransform frame = HudFactory.Rect("Video Frame", Root);
            HudFactory.Stretch(frame, 6f, HudTheme.VisionHeader, 6f, 6f);
            video = frame.gameObject.AddComponent<RawImage>();
            video.color = new Color(.05f, .08f, .12f, 1f);
            var button = frame.gameObject.AddComponent<Button>();
            button.targetGraphic = video;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            ColorBlock cb = button.colors;
            cb.highlightedColor = new Color(1.06f, 1.06f, 1.06f, 1f);
            cb.pressedColor = new Color(.9f, .9f, .9f, 1f);
            button.colors = cb;
            button.onClick.AddListener(() => SetExpanded(!Expanded));

            Image shade = HudFactory.Image(frame, "Shade", HudSprites.VerticalFade(), new Color(0f, .015f, .04f, .95f), false);
            shade.rectTransform.anchorMin = new Vector2(0f, 0f);
            shade.rectTransform.anchorMax = new Vector2(1f, 0f);
            shade.rectTransform.pivot = new Vector2(.5f, 0f);
            shade.rectTransform.sizeDelta = new Vector2(0f, 72f);
            shade.rectTransform.anchoredPosition = Vector2.zero;

            brackets = HudFactory.Add<BracketsGraphic>(frame, "Brackets");
            HudFactory.Stretch(brackets.rectTransform);
            brackets.Length = 18f;
            brackets.Width = 2f;
            brackets.Inset = 6f;
            scanLine = HudFactory.Image(frame, "Scan", HudSprites.FadeLine(), HudTheme.A(HudTheme.Cyan, .14f), false);
            scanLine.rectTransform.anchorMin = new Vector2(0f, 0f);
            scanLine.rectTransform.anchorMax = new Vector2(1f, 0f);
            scanLine.rectTransform.sizeDelta = new Vector2(0f, 1.5f);

            Chip detect = HudFactory.Chip(frame, "Detection", "DETECTION NOT CONNECTED", HudTheme.Amber, 232f, 22f);
            HudFactory.Place(detect.Root, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(10f, -10f), new Vector2(232f, 22f));
            detect.Label.characterSpacing = .5f;
            detect.Fill.color = new Color(.02f, .03f, .05f, .85f);

            targetLabel = HudFactory.Label(frame, "Target Label", "NAV TARGET", HudTheme.FontMicro, HudTheme.TextMid);
            HudFactory.Place(targetLabel.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(12f, 9f), new Vector2(110f, 20f));
            targetText = HudFactory.Text(frame, "Target", "", HudTheme.FontValue, HudTheme.TextHi, true, TextAlignmentOptions.Left, 1f);
            HudFactory.Place(targetText.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(112f, 5f), new Vector2(150f, 26f));
            routeText = HudFactory.Text(frame, "Route", "", HudTheme.FontMicro, HudTheme.Green, true, TextAlignmentOptions.Right, 1f);
            HudFactory.Place(routeText.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-12f, 9f), new Vector2(140f, 20f));
        }

        public void SetExpanded(bool on)
        {
            Expanded = on;
            sizeTarget = on ? 1f : 0f;
            Root.SetAsLastSibling();
        }

        private void UpdateResolution()
        {
            float w = Expanded ? HudTheme.VisionExpandedW : HudTheme.VisionW;
            // rendered at 1.25× the size actually displayed (sharpness), capped for performance
            int rw = Mathf.Clamp(Mathf.RoundToInt(w * PixelScale * 1.25f / 16f) * 16, 320, 1280);
            int rh = Mathf.RoundToInt(rw * 9f / 16f);
            Feed.SetResolution(rw, rh);
        }

        public void Tick(float dt, RobotVM r, bool visible, int cullingMask)
        {
            time += dt;
            if (r.Transform != null && Feed.Robot != r.Transform) Feed.Attach(r.Transform, cullingMask);
            UpdateResolution();
            Feed.Active = visible && r.Present;
            Feed.Tick(Expanded ? ExpandedFps : CompactFps);
            if (video.texture != Feed.Texture) video.texture = Feed.Texture;
            HudFactory.SetColor(video, Feed.Texture != null ? Color.white : new Color(.05f, .08f, .12f, 1f));

            size = Mathf.Lerp(size, sizeTarget, 1f - Mathf.Exp(-10f * dt));
            float w = Mathf.Lerp(HudTheme.VisionW, HudTheme.VisionExpandedW, size);
            float h = Mathf.Lerp(HudTheme.VisionH, HudTheme.VisionExpandedH, size);
            Root.sizeDelta = new Vector2(w + 12f, h + HudTheme.VisionHeader + 6f);

            bool live = r.Present && Feed.Texture != null && Feed.Active;
            HudFactory.SetColor(liveDot, HudTheme.A(live ? HudTheme.Red : HudTheme.TextLow, live ? .5f + .5f * Mathf.Sin(time * 5f) : .8f));
            HudFactory.Set(fpsText, !r.Present ? "NO ROBOT" : live ? "LIVE " + Mathf.RoundToInt(Feed.MeasuredFps) : "PAUSED");
            HudFactory.Set(title, robotId + "  <color=#" + HudTheme.Hex(HudTheme.TextMid) + ">" + r.Role + "</color>");

            string target = !string.IsNullOrEmpty(r.ActivityTarget) ? r.ActivityTarget
                          : (!string.IsNullOrEmpty(r.Destination) && r.Destination != "—" ? r.Destination : "—");
            HudFactory.Set(targetText, target);
            if (r.RouteKnown)
            {
                HudFactory.Set(routeText, r.RouteBlocked ? "ROUTE BLOCKED" : "ROUTE CLEAR");
                HudFactory.SetColor(routeText, r.RouteBlocked ? HudTheme.Red : HudTheme.Green);
            }
            else
            {
                HudFactory.Set(routeText, r.Activity);
                HudFactory.SetColor(routeText, HudTheme.Cyan);
            }
            Color frameColor = r.RouteKnown && r.RouteBlocked ? HudTheme.Red : HudTheme.Cyan;
            HudFactory.SetColor(brackets, HudTheme.A(frameColor, .95f));
            HudFactory.SetColor(stroke, HudTheme.A(frameColor, .8f));

            float y = Mathf.Repeat(time * .3f, 1f);
            scanLine.rectTransform.anchorMin = new Vector2(0f, y);
            scanLine.rectTransform.anchorMax = new Vector2(1f, y);
        }

        public void Dispose() { Feed.Release(); }
    }
}
