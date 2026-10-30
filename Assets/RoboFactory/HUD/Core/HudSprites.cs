using System.Collections.Generic;
using UnityEngine;

namespace RoboFactory.HUD
{
    /// <summary>
    /// Procedural sprites and textures (generated once, cached): glass panels
    /// with cut corners, thin outlines, halos, discs, soft lines, selection rings.
    /// Edges anti-aliased with a distance field: crisp rendering without any imported asset.
    /// </summary>
    public static class HudSprites
    {
        private static readonly Dictionary<string, Sprite> sprites = new Dictionary<string, Sprite>();
        private static readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>();

        // ------------------------------------------------------------------ panels

        /// <summary>Panel fill with cut corners (top-left and bottom-right), 9-slice.</summary>
        public static Sprite ChamferFill(int cut = 12) { return Chamfer(cut, 0f); }

        /// <summary>Thin outline of the same panel, 9-slice.</summary>
        public static Sprite ChamferStroke(int cut = 12, float stroke = 1.25f) { return Chamfer(cut, stroke); }

        /// <summary>Rounded rectangle (fill or outline), 9-slice.</summary>
        public static Sprite Rounded(int radius = 6, float stroke = 0f)
        {
            string key = "round_" + radius + "_" + stroke;
            if (sprites.TryGetValue(key, out Sprite s)) return s;
            int size = radius * 2 + 8;
            float half = size * .5f;
            var tex = NewTex(size, size);
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float fx = x + .5f - half, fy = y + .5f - half;
                float qx = Mathf.Abs(fx) - (half - 1f - radius);
                float qy = Mathf.Abs(fy) - (half - 1f - radius);
                float outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude;
                float d = outside + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
                px[y * size + x] = White(Alpha(d, stroke));
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            int b = radius + 3;
            s = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(.5f, .5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(b, b, b, b));
            s.name = key;
            sprites[key] = s;
            return s;
        }

        private static Sprite Chamfer(int cut, float stroke)
        {
            string key = "chamfer_" + cut + "_" + stroke;
            if (sprites.TryGetValue(key, out Sprite s)) return s;
            int size = cut * 2 + 24;
            float half = size * .5f - 1f;
            var tex = NewTex(size, size);
            var px = new Color32[size * size];
            const float inv = 0.70710678f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float fx = x + .5f - size * .5f, fy = y + .5f - size * .5f;
                float dBox = Mathf.Max(Mathf.Abs(fx) - half, Mathf.Abs(fy) - half);
                // top-left corner (x<0, y>0) and bottom-right corner (x>0, y<0)
                float dTl = ((-fx + fy) - (2f * half - cut)) * inv;
                float dBr = ((fx - fy) - (2f * half - cut)) * inv;
                float d = Mathf.Max(dBox, Mathf.Max(dTl, dBr));
                px[y * size + x] = White(Alpha(d, stroke));
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            int b = cut + 4;
            s = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(.5f, .5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(b, b, b, b));
            s.name = key;
            sprites[key] = s;
            return s;
        }

        /// <summary>Soft drop shadow (9-slice) to lift the panels off the scene.</summary>
        public static Sprite SoftShadow()
        {
            const string key = "softshadow";
            if (sprites.TryGetValue(key, out Sprite s)) return s;
            const int size = 96;
            const float blur = 24f;
            var tex = NewTex(size, size);
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float qx = Mathf.Abs(x + .5f - 48f) - (48f - blur - 6f);
                float qy = Mathf.Abs(y + .5f - 48f) - (48f - blur - 6f);
                float d = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0f) - 6f;
                float a = Mathf.Clamp01(1f - d / blur);
                a = a * a * (3f - 2f * a);
                px[y * size + x] = White(a);
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            s = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(.5f, .5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(40, 40, 40, 40));
            s.name = key;
            sprites[key] = s;
            return s;
        }

        // ------------------------------------------------------------------ shapes

        /// <summary>Anti-aliased filled disc.</summary>
        public static Sprite Circle()
        {
            const string key = "circle";
            if (sprites.TryGetValue(key, out Sprite s)) return s;
            const int size = 64;
            var tex = NewTex(size, size);
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = new Vector2(x + .5f - 32f, y + .5f - 32f).magnitude - 30.5f;
                px[y * size + x] = White(Mathf.Clamp01(.5f - d));
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            s = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(.5f, .5f), 100f);
            s.name = key;
            sprites[key] = s;
            return s;
        }

        /// <summary>Radial halo (soft gaussian), for glows and pulses.</summary>
        public static Sprite Glow()
        {
            const string key = "glow";
            if (sprites.TryGetValue(key, out Sprite s)) return s;
            const int size = 128;
            var tex = NewTex(size, size);
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float r = new Vector2(x + .5f - 64f, y + .5f - 64f).magnitude / 64f;
                float a = Mathf.Clamp01(1f - r);
                a = a * a * (3f - 2f * a);
                px[y * size + x] = White(a * a);
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            s = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(.5f, .5f), 100f);
            s.name = key;
            sprites[key] = s;
            return s;
        }

        /// <summary>Horizontal line that fades out at both ends.</summary>
        public static Sprite FadeLine()
        {
            const string key = "fadeline";
            if (sprites.TryGetValue(key, out Sprite s)) return s;
            const int w = 128, h = 4;
            var tex = NewTex(w, h);
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float t = (x + .5f) / w;
                float a = Mathf.Clamp01(Mathf.Min(t, 1f - t) * 4f);
                px[y * w + x] = White(a);
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            s = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(.5f, .5f), 100f);
            s.name = key;
            sprites[key] = s;
            return s;
        }

        /// <summary>Vertical gradient (opaque at the bottom → transparent at the top) for reflections.</summary>
        public static Sprite VerticalFade()
        {
            const string key = "vfade";
            if (sprites.TryGetValue(key, out Sprite s)) return s;
            const int w = 4, h = 64;
            var tex = NewTex(w, h);
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                px[y * w + x] = White(1f - (y + .5f) / h);
            tex.SetPixels32(px);
            tex.Apply(false, true);
            s = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(.5f, .5f), 100f);
            s.name = key;
            sprites[key] = s;
            return s;
        }

        /// <summary>Diagonal hatching (urgent banner), tileable.</summary>
        public static Sprite Hazard()
        {
            const string key = "hazard";
            if (sprites.TryGetValue(key, out Sprite s)) return s;
            const int size = 32;
            var tex = NewTex(size, size);
            tex.wrapMode = TextureWrapMode.Repeat;
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float v = Mathf.Repeat(x + y + 1f, 16f);
                float a = v < 8f ? Mathf.Clamp01(Mathf.Min(v, 8f - v) * 1.2f) : 0f;
                px[y * size + x] = White(a);
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            s = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(.5f, .5f), 100f);
            s.name = key;
            sprites[key] = s;
            return s;
        }

        // ------------------------------------------------------------------ world textures

        /// <summary>Holographic selection ring on the floor (dashes + halo), white to be tinted.</summary>
        public static Texture2D SelectionRing()
        {
            const string key = "selring";
            if (textures.TryGetValue(key, out Texture2D t)) return t;
            const int size = 256;
            t = NewTex(size, size);
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float fx = x + .5f - 128f, fy = y + .5f - 128f;
                float r = Mathf.Sqrt(fx * fx + fy * fy);
                float ang = Mathf.Atan2(fy, fx) * Mathf.Rad2Deg + 180f;
                // main dashed ring
                float ring = Mathf.Clamp01(1f - Mathf.Abs(r - 118f) / 3.2f);
                float dash = Mathf.Repeat(ang, 30f) < 21f ? 1f : 0f;
                // thin continuous inner ring
                float inner = Mathf.Clamp01(1f - Mathf.Abs(r - 104f) / 1.4f) * .7f;
                // short ticks pointing inwards
                float tick = (Mathf.Repeat(ang, 10f) < 1.6f && r > 88f && r < 98f) ? .55f : 0f;
                // diffuse inner halo
                float halo = r < 118f ? Mathf.Pow(r / 118f, 6f) * .22f : 0f;
                float a = Mathf.Max(ring * dash, Mathf.Max(inner, Mathf.Max(tick, halo)));
                px[y * size + x] = White(a);
            }
            t.SetPixels32(px);
            t.Apply(true, true);
            t.wrapMode = TextureWrapMode.Clamp;
            textures[key] = t;
            return t;
        }

        /// <summary>Soft cross-section profile for the path lines (LineRenderer).</summary>
        public static Texture2D BeamProfile()
        {
            const string key = "beam";
            if (textures.TryGetValue(key, out Texture2D t)) return t;
            const int w = 8, h = 32;
            t = NewTex(w, h);
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                float v = Mathf.Abs((y + .5f) / h - .5f) * 2f;   // 0 at the center, 1 at the edge
                float core = Mathf.Clamp01(1f - v / .28f);
                float glow = Mathf.Pow(Mathf.Clamp01(1f - v), 2.2f) * .45f;
                float a = Mathf.Max(core, glow);
                for (int x = 0; x < w; x++) px[y * w + x] = White(a);
            }
            t.SetPixels32(px);
            t.Apply(false, true);
            t.wrapMode = TextureWrapMode.Clamp;
            textures[key] = t;
            return t;
        }

        // ------------------------------------------------------------------ utilities

        private static Texture2D NewTex(int w, int h)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave
            };
            return tex;
        }

        /// <summary>Anti-aliased coverage: fill (stroke = 0) or inner outline.</summary>
        private static float Alpha(float d, float stroke)
        {
            if (stroke <= 0f) return Mathf.Clamp01(.5f - d);
            float inner = d + stroke;   // distance to the inner edge of the stroke
            return Mathf.Clamp01(.5f - d) * Mathf.Clamp01(.5f + inner);
        }

        private static Color32 White(float a)
        {
            return new Color32(255, 255, 255, (byte)Mathf.RoundToInt(Mathf.Clamp01(a) * 255f));
        }
    }
}
