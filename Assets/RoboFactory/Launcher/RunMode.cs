using UnityEngine;
using UnityEngine.SceneManagement;

namespace RoboFactory.Launcher
{
    public enum RunKind { None, Recorded, Live }

    /// <summary>
    /// How this session runs: RECORDED DEMO (default: replays real Meta answers recorded earlier, no key,
    /// no network) or LIVE AI (the user's own Meta Model API key, real calls). Survives the scene
    /// reloads done to reset the warehouse between scenarios; never survives a new Play / launch.
    /// </summary>
    public static class RunMode
    {
        public static RunKind Kind { get; private set; } = RunKind.None;
        /// <summary>Scenario id of the run ("" = free play).</summary>
        public static string ScenarioId { get; private set; } = "";
        /// <summary>LIVE AI only: the Meta answers of the scenario are being recorded.</summary>
        public static bool Recording { get; private set; }
        /// <summary>A mode was chosen for this session (the launcher is skipped after a reload).</summary>
        public static bool Chosen { get; private set; }
        /// <summary>The key typed in this session has been verified by a real request (memory only).</summary>
        public static bool LiveKeyVerified { get; set; }

        public static string Label =>
            Kind == RunKind.Recorded ? "RECORDED DEMO" : Kind == RunKind.Live ? "LIVE AI" : "";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLaunch()
        {
            Kind = RunKind.None;
            ScenarioId = "";
            Recording = false;
            Chosen = false;
            LiveKeyVerified = false;
        }

        public static void Choose(RunKind kind, string scenarioId, bool recording)
        {
            Kind = kind;
            ScenarioId = scenarioId ?? "";
            Recording = recording && kind == RunKind.Live;
            Chosen = true;
        }

        /// <summary>Forgets the run (launcher shown, no reload).</summary>
        public static void Clear()
        {
            Chosen = false;
            Kind = RunKind.None;
            ScenarioId = "";
            Recording = false;
        }

        /// <summary>Reloads the scene (clean warehouse) and starts the same run again.</summary>
        public static void ReloadSame() { Reload(); }

        /// <summary>Reloads the scene and shows the launcher again.</summary>
        public static void ReturnToLauncher()
        {
            Chosen = false;
            Kind = RunKind.None;
            ScenarioId = "";
            Recording = false;
            Reload();
        }

        private static void Reload()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (scene.buildIndex >= 0) SceneManager.LoadScene(scene.buildIndex);
            else SceneManager.LoadScene(scene.path);
        }
    }
}
