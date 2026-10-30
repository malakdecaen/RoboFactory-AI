using RoboFactory.Logistics;
using UnityEngine;

namespace RoboFactory.HUD
{
    /// <summary>
    /// Installs the Muse Wonder HUD when any scene containing a MuseOrchestrator starts
    /// (no scene change required). To disable it: AutoInstall = false,
    /// or place a MuseHudRoot in the scene yourself to configure it in the inspector.
    /// </summary>
    public static class MuseHudBootstrap
    {
        public static bool AutoInstall = true;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (!AutoInstall) return;
            if (Object.FindFirstObjectByType<MuseOrchestrator>() == null) return;
            if (Object.FindFirstObjectByType<MuseHudRoot>() != null) return;
            new GameObject("MUSE WONDER HUD").AddComponent<MuseHudRoot>();
        }
    }
}
