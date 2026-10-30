using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace RoboFactory.HUD
{
    /// <summary>
    /// HUD fonts. Bahnschrift (industrial DIN font shipped with Windows 10/11) is
    /// loaded dynamically; otherwise Segoe UI; otherwise the project's default TMP font.
    /// No asset to import: nothing breaks if the system font is missing.
    /// </summary>
    public static class HudFonts
    {
        private static bool loaded;
        private static TMP_FontAsset regular;
        private static TMP_FontAsset strong;

        public static TMP_FontAsset Regular { get { Load(); return regular; } }
        /// <summary>Strong weight (SemiBold if available, otherwise Regular + TMP bold style).</summary>
        public static TMP_FontAsset Strong { get { Load(); return strong; } }
        /// <summary>True if Strong is a real weight (no need for synthetic bold).</summary>
        public static bool StrongIsNative { get; private set; }

        private static void Load()
        {
            if (loaded) return;
            loaded = true;
            TMP_FontAsset fallback = TMP_Settings.defaultFontAsset;

            // same family for both weights (no mixing of Bahnschrift / Segoe UI)
            regular = TryCreate("Bahnschrift", "Regular");
            if (regular != null)
                strong = TryCreate("Bahnschrift", "SemiBold") ?? TryCreate("Bahnschrift", "Bold");
            else
            {
                regular = TryCreate("Segoe UI", "Regular") ?? fallback;
                strong = regular != fallback ? TryCreate("Segoe UI", "Semibold") : null;
            }
            StrongIsNative = strong != null;
            if (strong == null) strong = regular;

            AddFallback(regular, fallback);
            AddFallback(strong, fallback);
        }

        private static TMP_FontAsset TryCreate(string family, string style)
        {
            try
            {
                TMP_FontAsset asset = TMP_FontAsset.CreateFontAsset(family, style, 72);
                if (asset != null)
                {
                    asset.name = "HUD " + family + " " + style;
                    return asset;
                }
            }
            catch (System.Exception e)
            {
                Debug.Log("[HUD] font " + family + " " + style + " unavailable: " + e.Message);
            }
            return null;
        }

        private static void AddFallback(TMP_FontAsset asset, TMP_FontAsset fallback)
        {
            if (asset == null || fallback == null || asset == fallback) return;
            if (asset.fallbackFontAssetTable == null)
                asset.fallbackFontAssetTable = new List<TMP_FontAsset>();
            if (!asset.fallbackFontAssetTable.Contains(fallback))
                asset.fallbackFontAssetTable.Add(fallback);
        }
    }
}
