using System;
using System.Collections.Generic;
using UnityEngine;

namespace RoboFactory.HUD
{
    public enum HudIcon { Box, Bench, Truck, Robot, Order, Shield, Signal, Check, Camera, Spark, Clock, Speaker, SpeakerOff }

    /// <summary>
    /// Pictogrammes vectoriels cohérents (trait 4 px sur une grille 64, anti-aliasés par champ
    /// de distance), générés une seule fois. Blancs : teintés par Image.color.
    /// </summary>
    public static class HudIcons
    {
        private static readonly Dictionary<HudIcon, Sprite> cache = new Dictionary<HudIcon, Sprite>();
        private const int S = 64;
        private const float W = 4.2f;

        public static Sprite Get(HudIcon icon)
        {
            if (cache.TryGetValue(icon, out Sprite s)) return s;
            Func<Vector2, float> sdf = Shape(icon);
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave
            };
            var px = new Color32[S * S];
            for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float d = sdf(new Vector2(x + .5f, y + .5f));
                byte a = (byte)Mathf.RoundToInt(Mathf.Clamp01(.5f - d) * 255f);
                px[y * S + x] = new Color32(255, 255, 255, a);
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            s = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(.5f, .5f), 100f);
            s.name = "icon_" + icon;
            cache[icon] = s;
            return s;
        }

        // ------------------------------------------------------------------ formes

        private static Func<Vector2, float> Shape(HudIcon icon)
        {
            switch (icon)
            {
                case HudIcon.Box:
                    return p => Min(
                        BoxLine(p, new Vector2(32, 35), new Vector2(42, 32), 3f),
                        Seg(p, new Vector2(11, 41), new Vector2(53, 41)),
                        Seg(p, new Vector2(32, 41), new Vector2(32, 51)),
                        Seg(p, new Vector2(6, 11), new Vector2(58, 11)),
                        Seg(p, new Vector2(12, 11), new Vector2(12, 6)),
                        Seg(p, new Vector2(52, 11), new Vector2(52, 6)));
                case HudIcon.Bench:
                    return p => Min(
                        BoxFill(p, new Vector2(32, 43), new Vector2(54, 7), 2f),
                        Seg(p, new Vector2(12, 40), new Vector2(12, 12)),
                        Seg(p, new Vector2(52, 40), new Vector2(52, 12)),
                        Seg(p, new Vector2(12, 24), new Vector2(52, 24)),
                        Ring(p, new Vector2(20, 52), 3f), Ring(p, new Vector2(32, 52), 3f), Ring(p, new Vector2(44, 52), 3f));
                case HudIcon.Truck:
                    return p => Min(
                        BoxLine(p, new Vector2(22, 36), new Vector2(36, 26), 2f),
                        BoxLine(p, new Vector2(49, 31), new Vector2(16, 16), 3f),
                        Seg(p, new Vector2(42, 34), new Vector2(54, 34)),
                        Ring(p, new Vector2(16, 15), 5f),
                        Ring(p, new Vector2(48, 15), 5f));
                case HudIcon.Robot:
                    return p => Min(
                        BoxLine(p, new Vector2(32, 31), new Vector2(38, 28), 7f),
                        Disc(p, new Vector2(24, 32), 4.5f),
                        Disc(p, new Vector2(40, 32), 4.5f),
                        Seg(p, new Vector2(32, 45), new Vector2(32, 52)),
                        Disc(p, new Vector2(32, 55), 3.5f),
                        Seg(p, new Vector2(16, 9), new Vector2(48, 9)),
                        Seg(p, new Vector2(22, 17), new Vector2(22, 9)),
                        Seg(p, new Vector2(42, 17), new Vector2(42, 9)));
                case HudIcon.Order:
                    return p => Min(
                        BoxLine(p, new Vector2(32, 30), new Vector2(36, 46), 4f),
                        BoxFill(p, new Vector2(32, 54), new Vector2(16, 7), 2f),
                        Seg(p, new Vector2(22, 40), new Vector2(42, 40)),
                        Seg(p, new Vector2(22, 30), new Vector2(42, 30)),
                        Seg(p, new Vector2(22, 20), new Vector2(34, 20)));
                case HudIcon.Shield:
                    return p => Poly(p, new[] { new Vector2(32, 58), new Vector2(51, 50), new Vector2(49, 27), new Vector2(32, 7), new Vector2(15, 27), new Vector2(13, 50) });
                case HudIcon.Signal:
                    return p => Min(
                        Disc(p, new Vector2(32, 14), 4.5f),
                        Arc(p, new Vector2(32, 14), 14f, -50f, 50f),
                        Arc(p, new Vector2(32, 14), 26f, -50f, 50f),
                        Arc(p, new Vector2(32, 14), 38f, -50f, 50f));
                case HudIcon.Check:
                    return p => Min(Seg(p, new Vector2(14, 33), new Vector2(27, 20)), Seg(p, new Vector2(27, 20), new Vector2(51, 46)));
                case HudIcon.Camera:
                    return p => Min(
                        BoxLine(p, new Vector2(28, 32), new Vector2(40, 28), 4f),
                        Ring(p, new Vector2(28, 32), 7f),
                        Poly(p, new[] { new Vector2(48, 38), new Vector2(58, 44), new Vector2(58, 20), new Vector2(48, 26) }));
                case HudIcon.Spark:
                    return p => Min(
                        Ring(p, new Vector2(32, 32), 10f),
                        Seg(p, new Vector2(32, 50), new Vector2(32, 58)), Seg(p, new Vector2(32, 6), new Vector2(32, 14)),
                        Seg(p, new Vector2(6, 32), new Vector2(14, 32)), Seg(p, new Vector2(50, 32), new Vector2(58, 32)),
                        Seg(p, new Vector2(14, 14), new Vector2(19, 19)), Seg(p, new Vector2(45, 45), new Vector2(50, 50)),
                        Seg(p, new Vector2(14, 50), new Vector2(19, 45)), Seg(p, new Vector2(45, 19), new Vector2(50, 14)));
                case HudIcon.Speaker:
                    return p => Min(
                        Poly(p, new[] { new Vector2(8, 24), new Vector2(18, 24), new Vector2(32, 12), new Vector2(32, 52), new Vector2(18, 40), new Vector2(8, 40) }),
                        Arc(p, new Vector2(32, 32), 12f, 50f, 130f),
                        Arc(p, new Vector2(32, 32), 22f, 50f, 130f));
                case HudIcon.SpeakerOff:
                    return p => Min(
                        Poly(p, new[] { new Vector2(8, 24), new Vector2(18, 24), new Vector2(32, 12), new Vector2(32, 52), new Vector2(18, 40), new Vector2(8, 40) }),
                        Seg(p, new Vector2(42, 24), new Vector2(56, 40)),
                        Seg(p, new Vector2(42, 40), new Vector2(56, 24)));
                case HudIcon.Clock:
                    return p => Min(Ring(p, new Vector2(32, 32), 22f), Seg(p, new Vector2(32, 32), new Vector2(32, 46)), Seg(p, new Vector2(32, 32), new Vector2(42, 26)));
                default:
                    return p => Disc(p, new Vector2(32, 32), 10f);
            }
        }

        private static float Min(params float[] v)
        {
            float m = float.MaxValue;
            foreach (float f in v) if (f < m) m = f;
            return m;
        }

        private static float Seg(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 pa = p - a, ba = b - a;
            float h = Mathf.Clamp01(Vector2.Dot(pa, ba) / Vector2.Dot(ba, ba));
            return (pa - ba * h).magnitude - W * .5f;
        }

        private static float BoxSdf(Vector2 p, Vector2 c, Vector2 size, float r)
        {
            Vector2 q = new Vector2(Mathf.Abs(p.x - c.x), Mathf.Abs(p.y - c.y)) - (size * .5f - new Vector2(r, r));
            return new Vector2(Mathf.Max(q.x, 0f), Mathf.Max(q.y, 0f)).magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0f) - r;
        }

        private static float BoxLine(Vector2 p, Vector2 c, Vector2 size, float r) { return Mathf.Abs(BoxSdf(p, c, size, r)) - W * .5f; }
        private static float BoxFill(Vector2 p, Vector2 c, Vector2 size, float r) { return BoxSdf(p, c, size, r); }
        private static float Disc(Vector2 p, Vector2 c, float r) { return (p - c).magnitude - r; }
        private static float Ring(Vector2 p, Vector2 c, float r) { return Mathf.Abs((p - c).magnitude - r) - W * .5f; }

        private static float Arc(Vector2 p, Vector2 c, float r, float fromDeg, float toDeg)
        {
            Vector2 d = p - c;
            float ang = Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg; // 0 = haut
            if (ang < fromDeg || ang > toDeg)
            {
                Vector2 e0 = c + new Vector2(Mathf.Sin(fromDeg * Mathf.Deg2Rad), Mathf.Cos(fromDeg * Mathf.Deg2Rad)) * r;
                Vector2 e1 = c + new Vector2(Mathf.Sin(toDeg * Mathf.Deg2Rad), Mathf.Cos(toDeg * Mathf.Deg2Rad)) * r;
                return Mathf.Min((p - e0).magnitude, (p - e1).magnitude) - W * .5f;
            }
            return Mathf.Abs(d.magnitude - r) - W * .5f;
        }

        private static float Poly(Vector2 p, Vector2[] pts)
        {
            float m = float.MaxValue;
            for (int i = 0; i < pts.Length; i++) m = Mathf.Min(m, Seg(p, pts[i], pts[(i + 1) % pts.Length]));
            return m;
        }
    }
}
