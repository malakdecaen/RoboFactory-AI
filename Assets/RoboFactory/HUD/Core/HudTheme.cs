using UnityEngine;

namespace RoboFactory.HUD
{
    /// <summary>
    /// Design system V2 of the Muse Wonder HUD: palette, type scale READABLE from a distance,
    /// spacing grid. Canvas units = pixels at 1920×1080 (CanvasScaler, match height).
    /// Rules: text in white/light grey, cyan reserved for accents, orange = selection/urgent,
    /// red = real danger/errors only.
    /// </summary>
    public static class HudTheme
    {
        // ------------------------------------------------------------------ palette
        public static readonly Color Cyan = new Color(0.20f, 0.88f, 1f);
        public static readonly Color Blue = new Color(0.24f, 0.58f, 1f);
        public static readonly Color DeepBlue = new Color(0.10f, 0.30f, 0.72f);
        public static readonly Color Orange = new Color(1f, 0.56f, 0.14f);
        public static readonly Color Amber = new Color(1f, 0.77f, 0.30f);
        public static readonly Color Green = new Color(0.30f, 0.87f, 0.58f);
        public static readonly Color Red = new Color(1f, 0.32f, 0.38f);
        public static readonly Color Violet = new Color(0.62f, 0.58f, 1f);

        public static readonly Color TextHi = new Color(0.93f, 0.97f, 1f);
        public static readonly Color TextMid = new Color(0.68f, 0.78f, 0.87f);
        public static readonly Color TextLow = new Color(0.50f, 0.61f, 0.71f);

        /// <summary>Night-blue panel background ~86 % (text always readable in front of the scene).</summary>
        public static readonly Color Glass = new Color(0.020f, 0.055f, 0.105f, 0.86f);
        public static readonly Color GlassStrong = new Color(0.015f, 0.040f, 0.080f, 0.92f);
        public static readonly Color GlassLight = new Color(0.05f, 0.11f, 0.19f, 0.88f);
        public static readonly Color GlassRow = new Color(0.07f, 0.15f, 0.25f, 0.92f);
        public static readonly Color GlassRowHi = new Color(0.10f, 0.22f, 0.36f, 0.95f);
        public static readonly Color Stroke = new Color(0.27f, 0.82f, 1f, 0.55f);
        public static readonly Color StrokeSoft = new Color(0.27f, 0.82f, 1f, 0.22f);
        public static readonly Color Shadow = new Color(0f, 0.01f, 0.03f, 0.55f);

        // ------------------------------------------------------------------ typography (px @1080p)
        public const float FontHero = 30f;      // major identifiers (ORDER #1)
        public const float FontKey = 24f;       // key values (P02, B03, T03)
        public const float FontTitle = 22f;     // panel titles
        public const float FontValue = 20f;     // regular values
        public const float FontBody = 18f;      // main text
        public const float FontSmall = 15f;     // secondary metadata
        public const float FontMicro = 14f;     // absolute minimum (labels)
        public const float TitleSpacing = 4f;
        public const float LabelSpacing = 2f;

        // ------------------------------------------------------------------ grid (base 4)
        public const float Margin = 20f;
        public const float Pad = 14f;
        public const float Gap = 10f;
        public const float OrdersWidth = 320f;
        public const float DecisionsWidth = 350f;
        public const float SideTop = 84f;
        public const float StatusBarH = 52f;
        public const float BottomGap = 12f;
        public const float VisionW = 320f;
        public const float VisionH = 180f;
        public const float VisionHeader = 34f;
        public const float VisionExpandedW = 640f;
        public const float VisionExpandedH = 360f;
        public const float VoiceW = 560f;
        public const float VoiceH = 78f;

        /// <summary>Bottom of the vision windows / voice dock (above the status bar).</summary>
        public static float BottomRowY => Margin + StatusBarH + BottomGap;

        // ------------------------------------------------------------------ animation
        public const float FadeSpeed = 6f;
        public const float RefreshHz = 8f;

        public static Color A(Color c, float a)
        {
            c.a = a;
            return c;
        }

        public static Color Mul(Color c, float k)
        {
            return new Color(c.r * k, c.g * k, c.b * k, c.a);
        }

        public static string Hex(Color c)
        {
            return ColorUtility.ToHtmlStringRGB(c);
        }
    }
}
