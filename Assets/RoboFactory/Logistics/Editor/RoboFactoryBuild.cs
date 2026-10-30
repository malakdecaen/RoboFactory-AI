using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace RoboFactory.Logistics.EditorTools
{
    /// <summary>
    /// Build Windows autonome de la démo (scène warehouse_robot_demo, ou la scène en cubes du dépôt public) dans Builds/RoboFactoryAI_&lt;date&gt;/.
    /// Le serveur Meta est intégré au jeu (C#) : rien d'autre à copier. Les Build Settings du projet ne
    /// sont pas modifiés. Résultat détaillé dans Builds/last_build.txt.
    /// </summary>
    public static class RoboFactoryBuild
    {
        public const string Scene = "Assets/Warehouse_Robot_Controller/Scenes/warehouse_robot_demo.unity";
        /// <summary>Scène en cubes du dépôt public (utilisée quand la scène complète n'existe pas).</summary>
        public const string PublicScene = "Assets/RoboFactorySim/Scenes/warehouse_simplified.unity";

        private static string SceneToBuild() => File.Exists(Path.Combine(Directory.GetParent(Application.dataPath).FullName, Scene)) ? Scene : PublicScene;

        [MenuItem("RoboFactory/Build/Build Windows (jury)")]
        public static void BuildWindows()
        {
            string root = Directory.GetParent(Application.dataPath).FullName;
            string dir = Path.Combine(root, "Builds", "RoboFactoryAI_" + DateTime.Now.ToString("yyyyMMdd_HHmm"));
            string exe = Path.Combine(dir, "RoboFactoryAI.exe");
            string summaryFile = Path.Combine(root, "Builds", "last_build.txt");
            Directory.CreateDirectory(dir);
            var options = new BuildPlayerOptions
            {
                scenes = new[] { SceneToBuild() },
                locationPathName = exe,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None
            };
            Debug.Log("[Build] démarrage : " + exe);
            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary s = report.summary;
            string lines = "résultat : " + s.result + "\n" +
                           "sortie   : " + exe + "\n" +
                           "durée    : " + s.totalTime + "\n" +
                           "taille   : " + (s.totalSize / (1024f * 1024f)).ToString("F0") + " Mo\n" +
                           "erreurs  : " + s.totalErrors + "   avertissements : " + s.totalWarnings + "\n";
            foreach (BuildStep step in report.steps)
                foreach (BuildStepMessage m in step.messages.Where(m => m.type == LogType.Error || m.type == LogType.Exception))
                    lines += "ERREUR [" + step.name + "] " + m.content + "\n";
            File.WriteAllText(summaryFile, lines);
            if (s.result == BuildResult.Succeeded)
            {
                Debug.Log("[Build] OK : " + exe + " (" + (s.totalSize / (1024f * 1024f)).ToString("F0") + " Mo, " + s.totalTime + ")");
                EditorUtility.RevealInFinder(exe);
            }
            else Debug.LogError("[Build] " + s.result + " — " + s.totalErrors + " erreur(s), détail dans Builds/last_build.txt");
        }
    }
}
