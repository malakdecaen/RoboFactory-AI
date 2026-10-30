using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace RoboFactory.HUD
{
    /// <summary>
    /// Polices du HUD. Bahnschrift (police DIN industrielle livrée avec Windows 10/11) est
    /// chargée dynamiquement ; sinon Segoe UI ; sinon la police TMP par défaut du projet.
    /// Aucun asset à importer : rien ne casse si la police système est absente.
    /// </summary>
    public static class HudFonts
    {
        private static bool loaded;
        private static TMP_FontAsset regular;
        private static TMP_FontAsset strong;

        public static TMP_FontAsset Regular { get { Load(); return regular; } }
        /// <summary>Graisse forte (SemiBold si disponible, sinon Regular + style gras TMP).</summary>
        public static TMP_FontAsset Strong { get { Load(); return strong; } }
        /// <summary>Vrai si Strong est une vraie graisse (pas besoin du gras synthétique).</summary>
        public static bool StrongIsNative { get; private set; }

        private static void Load()
        {
            if (loaded) return;
            loaded = true;
            TMP_FontAsset fallback = TMP_Settings.defaultFontAsset;

            // même famille pour les deux graisses (pas de mélange Bahnschrift / Segoe UI)
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
                Debug.Log("[HUD] police " + family + " " + style + " indisponible : " + e.Message);
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
