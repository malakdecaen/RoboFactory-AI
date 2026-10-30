using System.Collections.Generic;
using RoboFactory.Integration;
using RoboFactory.Logistics;
using UnityEngine;

namespace RoboFactory.HUD
{
    /// <summary>
    /// Progressive replacement of the old interface WITHOUT modifying its code:
    /// only public display flags are touched (and they are restored in DEBUG).
    ///  - MuseDebugHud (IMGUI) hidden at start; F1 shows it again as before.
    ///  - IMGUI vision window of WarehouseRobotVisionHud hidden, its (duplicate) camera turned off.
    ///  - Original debug grids and path traces hidden outside DEBUG (the HUD draws its own).
    ///  - Original selection discs (ResourceHighlighter) hidden outside DEBUG.
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

            // old IMGUI panel: hidden only once (F1 still works)
            if (!debugHudHidden)
            {
                MuseDebugHud hud = Object.FindFirstObjectByType<MuseDebugHud>();
                if (hud != null) { hud.Visible = false; debugHudHidden = true; }
            }

            foreach (WarehouseRobotVisionHud v in Object.FindObjectsByType<WarehouseRobotVisionHud>(FindObjectsSortMode.None))
            {
                v.ShowVision = false;
                // "Robot eye camera" of the old window: replaced by the HUD feed
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

            // translucent red volumes of the no-go zones (NavBlocker): purely visual,
            // navigation reads the BoxCollider -> hidden outside DEBUG, behaviour unchanged
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

        /// <summary>Restores every original flag (HUD disabled / destroyed).</summary>
        public void RestoreAll()
        {
            mode = HudMode.Debug;
            Scan(true);
            if (legacyMarks != null) legacyMarks.SetActive(true);
        }
    }
}
