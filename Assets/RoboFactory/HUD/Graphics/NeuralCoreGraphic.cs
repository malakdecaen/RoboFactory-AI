using UnityEngine;
using UnityEngine.UI;

namespace RoboFactory.HUD
{
    /// <summary>
    /// Noyau « réseau neuronal » du cercle Muse : nœuds reliés, influx lumineux qui parcourt
    /// le réseau. Intensity (0..1) accélère et densifie l'activité selon l'état réel de Muse.
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class NeuralCoreGraphic : MaskableGraphic
    {
        public float Radius = 24f;
        public float Phase;
        [Range(0f, 1f)] public float Intensity = .3f;

        private static readonly int[,] Links =
        {
            { 0, 1 }, { 0, 2 }, { 0, 3 }, { 0, 4 }, { 0, 5 }, { 0, 6 },
            { 1, 2 }, { 2, 3 }, { 3, 4 }, { 4, 5 }, { 5, 6 }, { 6, 1 },
            { 1, 7 }, { 2, 8 }, { 3, 9 }, { 4, 10 }, { 5, 11 }, { 6, 12 },
            { 7, 2 }, { 8, 3 }, { 9, 4 }, { 10, 5 }, { 11, 6 }, { 12, 1 }
        };

        private readonly Vector2[] nodes = new Vector2[13];

        public void Animate(float phase, float intensity)
        {
            Phase = phase;
            Intensity = intensity;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            float r1 = Radius * .52f, r2 = Radius;
            nodes[0] = Vector2.zero;
            for (int i = 0; i < 6; i++)
            {
                float a = (i * 60f + 90f) * Mathf.Deg2Rad;
                float wob = 1f + .06f * Mathf.Sin(Phase * 1.7f + i * 1.3f);
                nodes[1 + i] = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r1 * wob;
                float b = (i * 60f + 120f) * Mathf.Deg2Rad;
                nodes[7 + i] = new Vector2(Mathf.Cos(b), Mathf.Sin(b)) * r2 * (1f + .05f * Mathf.Sin(Phase * 1.3f + i));
            }
            Color32 c = color;
            int linkCount = Links.GetLength(0);
            float travel = Phase * (1.5f + Intensity * 4f);
            for (int i = 0; i < linkCount; i++)
            {
                Vector2 a = nodes[Links[i, 0]], b = nodes[Links[i, 1]];
                // influx : un « paquet » lumineux circule sur chaque liaison, décalé dans le temps
                float t = Mathf.Repeat(travel + i * .37f, 1f);
                float lit = Mathf.Pow(1f - Mathf.Abs(t - .5f) * 2f, 3f) * (.35f + .65f * Intensity);
                byte alpha = (byte)(c.a * Mathf.Clamp01(.22f + lit * .6f));
                Line(vh, a, b, .9f, new Color32(c.r, c.g, c.b, alpha));
                if (lit > .05f)
                {
                    Vector2 p = Vector2.Lerp(a, b, t);
                    Dot(vh, p, 1.4f + lit * 1.2f, new Color32(255, 255, 255, (byte)(c.a * lit)));
                }
            }
            for (int i = 0; i < nodes.Length; i++)
            {
                float pulse = .5f + .5f * Mathf.Sin(Phase * (2f + Intensity * 3f) + i * .9f);
                float size = i == 0 ? 3.4f + pulse * 1.2f : (i < 7 ? 2.2f : 1.7f) + pulse * .6f;
                Dot(vh, nodes[i], size, new Color32(c.r, c.g, c.b, (byte)(c.a * (.65f + .35f * pulse))));
            }
        }

        private static void Line(VertexHelper vh, Vector2 a, Vector2 b, float w, Color32 c)
        {
            Vector2 d = b - a;
            if (d.sqrMagnitude < 1e-4f) return;
            Vector2 n = new Vector2(-d.y, d.x).normalized * (w * .5f);
            int v = vh.currentVertCount;
            vh.AddVert(a - n, c, Vector2.zero);
            vh.AddVert(a + n, c, Vector2.zero);
            vh.AddVert(b + n, c, Vector2.zero);
            vh.AddVert(b - n, c, Vector2.zero);
            vh.AddTriangle(v, v + 1, v + 2);
            vh.AddTriangle(v, v + 2, v + 3);
        }

        private static void Dot(VertexHelper vh, Vector2 p, float r, Color32 c)
        {
            const int seg = 10;
            int center = vh.currentVertCount;
            vh.AddVert(p, c, Vector2.zero);
            var edge = new Color32(c.r, c.g, c.b, 0);
            for (int i = 0; i <= seg; i++)
            {
                float a = i * Mathf.PI * 2f / seg;
                var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                vh.AddVert(p + dir * r, c, Vector2.zero);
                vh.AddVert(p + dir * (r + 1f), edge, Vector2.zero);
            }
            for (int i = 0; i < seg; i++)
            {
                int a0 = center + 1 + i * 2, a1 = a0 + 2;
                vh.AddTriangle(center, a0, a1);
                vh.AddTriangle(a0, a0 + 1, a1 + 1);
                vh.AddTriangle(a0, a1 + 1, a1);
            }
        }
    }
}
