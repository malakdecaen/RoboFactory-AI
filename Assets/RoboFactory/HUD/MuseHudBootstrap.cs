using RoboFactory.Logistics;
using UnityEngine;

namespace RoboFactory.HUD
{
    /// <summary>
    /// Installe le HUD Muse Wonder au lancement de toute scène contenant un MuseOrchestrator
    /// (aucune modification de scène requise). Pour le désactiver : AutoInstall = false,
    /// ou placer soi-même un MuseHudRoot dans la scène pour le configurer dans l'inspecteur.
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
