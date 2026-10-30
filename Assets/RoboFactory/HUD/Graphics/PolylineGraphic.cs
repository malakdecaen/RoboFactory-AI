using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace RoboFactory.HUD
{
    /// <summary>
    /// Polyligne anti-aliasée dans le rectangle du RectTransform (points normalisés 0..1).
    /// Sert à l'onde audio réelle du micro et à l'historique des latences Meta.
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class PolylineGraphic : MaskableGraphic
    {
        public float Thickness = 1.6f;
        public float Feather = 1f;
        public bool FillBelow;
        [Range(0f, 1f)] public float FillAlpha = .18f;
        private readonly List<Vector2> points = new List<Vector2>();

        public void SetPoints(IList<Vector2> normalized)
        {
            points.Clear();
            if (normalized != null) points.AddRange(normalized);
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (points.Count < 2) return;
            Rect r = rectTransform.rect;
            Color32 c = color;
            var clear = new Color32(c.r, c.g, c.b, 0);
            var fillTop = new Color32(c.r, c.g, c.b, (byte)(c.a * FillAlpha));
            float half = Thickness * .5f;

            var pts = new Vector2[points.Count];
            for (int i = 0; i < points.Count; i++)
                pts[i] = new Vector2(r.xMin + points[i].x * r.width, r.yMin + points[i].y * r.height);

            if (FillBelow)
            {
                for (int i = 0; i < pts.Length - 1; i++)
                {
                    int v = vh.currentVertCount;
                    vh.AddVert(new Vector2(pts[i].x, r.yMin), clear, Vector2.zero);
                    vh.AddVert(pts[i], fillTop, Vector2.zero);
                    vh.AddVert(pts[i + 1], fillTop, Vector2.zero);
                    vh.AddVert(new Vector2(pts[i + 1].x, r.yMin), clear, Vector2.zero);
                    vh.AddTriangle(v, v + 1, v + 2);
                    vh.AddTriangle(v, v + 2, v + 3);
                }
            }

            for (int i = 0; i < pts.Length; i++)
            {
                Vector2 prev = pts[Mathf.Max(0, i - 1)];
                Vector2 next = pts[Mathf.Min(pts.Length - 1, i + 1)];
                Vector2 dir = next - prev;
                if (dir.sqrMagnitude < 1e-6f) dir = Vector2.right;
                dir.Normalize();
                var n = new Vector2(-dir.y, dir.x);
                vh.AddVert(pts[i] - n * (half + Feather), clear, Vector2.zero);
                vh.AddVert(pts[i] - n * half, c, Vector2.zero);
                vh.AddVert(pts[i] + n * half, c, Vector2.zero);
                vh.AddVert(pts[i] + n * (half + Feather), clear, Vector2.zero);
            }
            int start = FillBelow ? (pts.Length - 1) * 4 : 0;
            for (int i = 0; i < pts.Length - 1; i++)
            {
                int v = start + i * 4;
                for (int k = 0; k < 3; k++)
                {
                    vh.AddTriangle(v + k, v + 4 + k, v + 5 + k);
                    vh.AddTriangle(v + k, v + 5 + k, v + 1 + k);
                }
            }
        }
    }
}
