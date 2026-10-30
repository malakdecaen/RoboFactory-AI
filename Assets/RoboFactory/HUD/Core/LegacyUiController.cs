using System.Collections.Generic;
using RoboFactory.Integration;
using RoboFactory.Logistics;
using UnityEngine;

namespace RoboFactory.HUD
{
    /// <summary>
    /// Remplacement progressif de l'ancienne interface SANS modifier son code :
    /// on ne touche qu'à des drapeaux d'affichage publics (et on les restaure en DEBUG).
    ///  - MuseDebugHud (IMGUI) masqué au démarrage ; F1 le ré-affiche comme avant.
    ///  - Fenêtre vision IMGUI de WarehouseRobotVisionHud masquée, sa caméra (doublon) coupée.
    ///  - Grilles de debug et tracés de chemin d'origine masqués hors DEBUG (le HUD trace les siens).
    ///  - Disques de sélection d'origine (ResourceHighlighter) masqués hors DEBUG.
    /// </summary>
    public sealed class LegacyUiController
    {
        private bool debugHudHidden;
        private GameObject legacyMarks;
        private float nextScan;
        private HudMode mode = HudMode.Operations;

        public void Apply(HudMode newMode)
        {
            mode = newMode;
            nextScan = 0f;
            Scan(true);
        }

        public void Tick()
        {
            if (Time.unscaledTime < nextScan) return;
            nextScan = Time.unscaledTime + 1f;
            Scan(false);
        }

        private void Scan(bool modeChanged)
        {
            bool debug = mode == HudMode.Debug;

            // ancien panneau IMGUI : masqué une seule fois (F1 reste fonctionnel)
            if (!debugHudHidden)
            {
                MuseDebugHud hud = Object.FindFirstObjectByType<MuseDebugHud>();
                if (hud != null) { hud.Visible = false; debugHudHidden = true; }
            }

            foreach (WarehouseRobotVisionHud v in Object.FindObjectsByType<WarehouseRobotVisionHud>(FindObjectsSortMode.None))
            {
                v.ShowVision = false;
                // caméra "Robot eye camera" de l'ancienne fenêtre : remplacée par le flux du HUD
                foreach (Camera c in v.GetComponentsInChildren<Camera>(true))
                    if (c.name == "Robot eye camera" && c.enabled) c.enabled = false;
            }

            foreach (WarehouseRobotAutopilot p in Object.FindObjectsByType<WarehouseRobotAutopilot>(FindObjectsSortMode.None))
            {
                Remember(p, "grid", p.ShowGridDebug);
                Remember(p, "path", p.ShowPathInGame);
                p.ShowGridDebug = debug && Original(p, "grid");
                p.ShowPathInGame = debug && Original(p, "path");
            }
            foreach (GridOverlayViz g in Object.FindObjectsByType<GridOverlayViz>(FindObjectsSortMode.None))
            {
                Remember(g, "grid", g.ShowGridDebug);
                g.ShowGridDebug = debug && Original(g, "grid");
            }
            foreach (GridPathViz g in Object.FindObjectsByType<GridPathViz>(FindObjectsSortMode.None))
            {
                Remember(g, "path", g.ShowPath);
                g.ShowPath = debug && Original(g, "path");
            }

            // volumes rouges translucides des zones interdites (NavBlocker) : purement visuels,
            // la navigation lit le BoxCollider -> masqués hors DEBUG, comportement inchangé
            foreach (NavBlocker nb in NavBlocker.All)
            {
                if (nb == null) continue;
                Transform vol = nb.transform.Find("nav_blocker_volume");
                Renderer r = vol != null ? vol.GetComponent<Renderer>() : null;
                if (r != null && r.enabled != debug) r.enabled = debug;
            }

            if (legacyMarks == null) legacyMarks = GameObject.Find("Muse Selection Marks");
            if (legacyMarks != null && legacyMarks.activeSelf != debug) legacyMarks.SetActive(debug);
        }

        private readonly Dictionary<string, bool> keyed = new Dictionary<string, bool>();

        private void Remember(Object o, string key, bool value)
        {
            string k = o.GetInstanceID() + key;
            if (!keyed.ContainsKey(k)) keyed[k] = value;
        }

        private bool Original(Object o, string key)
        {
            return keyed.TryGetValue(o.GetInstanceID() + key, out bool v) && v;
        }

        /// <summary>Restaure tous les drapeaux d'origine (HUD désactivé / détruit).</summary>
        public void RestoreAll()
        {
            mode = HudMode.Debug;
            Scan(true);
            if (legacyMarks != null) legacyMarks.SetActive(true);
        }
    }
}
