using UnityEngine;
using UnityEngine.UI;

namespace RoboFactory.HUD
{
    /// <summary>Segment de droite entre deux points du canvas (traits de rappel des étiquettes).</summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class SegmentGraphic : MaskableGraphic
    {
        public Vector2 From;
        public Vector2 To;
        public float Width = 1.2f;

        public void SetEnds(Vector2 from, Vector2 to)
        {
            if ((from - From).sqrMagnitude < .01f && (to - To).sqrMagnitude < .01f) return;
            From = from;
            To = to;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            Vector2 d = To - From;
            if (d.sqrMagnitude < 1e-4f) return;
            Vector2 n = new Vector2(-d.y, d.x).normalized;
            Color32 c = color;
            var clear = new Color32(c.r, c.g, c.b, 0);
            float h = Width * .5f;
            vh.AddVert(From - n * (h + 1f), clear, Vector2.zero);
            vh.AddVert(From - n * h, new Color32(c.r, c.g, c.b, (byte)(c.a * .25f)), Vector2.zero);
            vh.AddVert(From + n * h, new Color32(c.r, c.g, c.b, (byte)(c.a * .25f)), Vector2.zero);
            vh.AddVert(From + n * (h + 1f), clear, Vector2.zero);
            vh.AddVert(To - n * (h + 1f), clear, Vector2.zero);
            vh.AddVert(To - n * h, c, Vector2.zero);
            vh.AddVert(To + n * h, c, Vector2.zero);
            vh.AddVert(To + n * (h + 1f), clear, Vector2.zero);
            for (int k = 0; k < 3; k++)
            {
                vh.AddTriangle(k, 4 + k, 5 + k);
                vh.AddTriangle(k, 5 + k, 1 + k);
            }
        }
    }
}
