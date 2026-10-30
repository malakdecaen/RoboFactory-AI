using UnityEngine;
using UnityEngine.UI;

namespace RoboFactory.HUD
{
    /// <summary>Four corner brackets (viewfinder frame) drawn in the rectangle.</summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class BracketsGraphic : MaskableGraphic
    {
        public float Length = 14f;
        public float Width = 1.5f;
        public float Inset = 4f;

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            Rect r = rectTransform.rect;
            float x0 = r.xMin + Inset, x1 = r.xMax - Inset, y0 = r.yMin + Inset, y1 = r.yMax - Inset;
            Color32 c = color;
            Bar(vh, c, x0, y1 - Width, x0 + Length, y1);
            Bar(vh, c, x0, y1 - Length, x0 + Width, y1);
            Bar(vh, c, x1 - Length, y1 - Width, x1, y1);
            Bar(vh, c, x1 - Width, y1 - Length, x1, y1);
            Bar(vh, c, x0, y0, x0 + Length, y0 + Width);
            Bar(vh, c, x0, y0, x0 + Width, y0 + Length);
            Bar(vh, c, x1 - Length, y0, x1, y0 + Width);
            Bar(vh, c, x1 - Width, y0, x1, y0 + Length);
        }

        private static void Bar(VertexHelper vh, Color32 c, float xa, float ya, float xb, float yb)
        {
            int v = vh.currentVertCount;
            vh.AddVert(new Vector2(xa, ya), c, Vector2.zero);
            vh.AddVert(new Vector2(xa, yb), c, Vector2.zero);
            vh.AddVert(new Vector2(xb, yb), c, Vector2.zero);
            vh.AddVert(new Vector2(xb, ya), c, Vector2.zero);
            vh.AddTriangle(v, v + 1, v + 2);
            vh.AddTriangle(v, v + 2, v + 3);
        }
    }
}
