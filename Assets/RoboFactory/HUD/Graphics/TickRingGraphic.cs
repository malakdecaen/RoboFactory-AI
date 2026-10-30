using UnityEngine;
using UnityEngine.UI;

namespace RoboFactory.HUD
{
    /// <summary>Couronne de graduations radiales (grandes graduations tous les N traits).</summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class TickRingGraphic : MaskableGraphic
    {
        public float Radius = 60f;
        public int Count = 72;
        public float Length = 4f;
        public float MajorLength = 9f;
        public int MajorEvery = 6;
        public float Width = 1.2f;

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            Color32 c = color;
            var dim = new Color32(c.r, c.g, c.b, (byte)(c.a * .55f));
            for (int i = 0; i < Count; i++)
            {
                bool major = MajorEvery > 0 && i % MajorEvery == 0;
                float len = major ? MajorLength : Length;
                float ang = i * (360f / Count) * Mathf.Deg2Rad;
                var dir = new Vector2(Mathf.Sin(ang), Mathf.Cos(ang));
                var side = new Vector2(dir.y, -dir.x) * (Width * (major ? .75f : .5f));
                Vector2 p0 = dir * Radius, p1 = dir * (Radius - len);
                Color32 col = major ? c : dim;
                int v = vh.currentVertCount;
                vh.AddVert(p0 - side, col, Vector2.zero);
                vh.AddVert(p0 + side, col, Vector2.zero);
                vh.AddVert(p1 + side, col, Vector2.zero);
                vh.AddVert(p1 - side, col, Vector2.zero);
                vh.AddTriangle(v, v + 1, v + 2);
                vh.AddTriangle(v, v + 2, v + 3);
            }
        }
    }
}
