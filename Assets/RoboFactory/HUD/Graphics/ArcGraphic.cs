using UnityEngine;
using UnityEngine.UI;

namespace RoboFactory.HUD
{
    /// <summary>
    /// Anti-aliased vector arc / ring (transparency fringes) for the Muse circle,
    /// the gauges and the microphone button. 0° = noon, clockwise. Animated rotations are done
    /// through the RectTransform (no mesh rebuild).
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class ArcGraphic : MaskableGraphic
    {
        public float Radius = 50f;
        public float Thickness = 2f;
        public float StartDeg;
        public float SweepDeg = 360f;
        [Tooltip("Number of segments (dashes) spread over the arc.")]
        public int Dashes = 1;
        [Range(0.02f, 1f)] public float DashFill = 1f;
        public float Feather = 1f;
        [Tooltip("Opacity gradient from the start (0) to the end (1) of each segment: comet effect.")]
        public bool TailFade;
        public int Resolution = 160;

        public void Set(float radius, float thickness, float startDeg, float sweepDeg, int dashes, float dashFill)
        {
            Radius = radius;
            Thickness = thickness;
            StartDeg = startDeg;
            SweepDeg = sweepDeg;
            Dashes = Mathf.Max(1, dashes);
            DashFill = Mathf.Clamp(dashFill, .02f, 1f);
            SetVerticesDirty();
        }

        public void SetSweep(float sweepDeg)
        {
            if (Mathf.Abs(sweepDeg - SweepDeg) < .05f) return;
            SweepDeg = sweepDeg;
            SetVerticesDirty();
        }

        public void SetDashFill(float fill)
        {
            fill = Mathf.Clamp(fill, .02f, 1f);
            if (Mathf.Abs(fill - DashFill) < .002f) return;
            DashFill = fill;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (SweepDeg <= .01f || Thickness <= 0f) return;
            int dashes = Mathf.Max(1, Dashes);
            float span = SweepDeg / dashes;
            float rOut = Radius;
            float rIn = Mathf.Max(0f, Radius - Thickness);
            float f = Mathf.Max(.01f, Feather);
            Color32 c = color;
            for (int d = 0; d < dashes; d++)
            {
                float a0 = StartDeg + d * span;
                float a1 = a0 + span * (dashes == 1 && DashFill >= 1f ? 1f : DashFill);
                int steps = Mathf.Max(2, Mathf.CeilToInt(Mathf.Abs(a1 - a0) / 360f * Resolution));
                int baseIndex = vh.currentVertCount;
                for (int i = 0; i <= steps; i++)
                {
                    float t = i / (float)steps;
                    float ang = Mathf.Lerp(a0, a1, t) * Mathf.Deg2Rad;
                    var dir = new Vector2(Mathf.Sin(ang), Mathf.Cos(ang));
                    byte alpha = (byte)(c.a * (TailFade ? t * t : 1f));
                    var solid = new Color32(c.r, c.g, c.b, alpha);
                    var clear = new Color32(c.r, c.g, c.b, 0);
                    vh.AddVert(dir * Mathf.Max(0f, rIn - f), clear, Vector2.zero);
                    vh.AddVert(dir * rIn, solid, Vector2.zero);
                    vh.AddVert(dir * rOut, solid, Vector2.zero);
                    vh.AddVert(dir * (rOut + f), clear, Vector2.zero);
                }
                for (int i = 0; i < steps; i++)
                {
                    int v = baseIndex + i * 4;
                    for (int k = 0; k < 3; k++)
                    {
                        vh.AddTriangle(v + k, v + 4 + k, v + 5 + k);
                        vh.AddTriangle(v + k, v + 5 + k, v + 1 + k);
                    }
                }
            }
        }
    }
}
