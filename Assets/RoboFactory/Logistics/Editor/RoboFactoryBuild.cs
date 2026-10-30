using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace RoboFactory.Logistics.EditorTools
{
    /// <summary>
    /// Standalone Windows build of the demo (warehouse_robot_demo scene, or the public repository's cube scene) into Builds/RoboFactoryAI_&lt;date&gt;/.
    /// The Meta server is built into the game (C#): nothing else to copy. The project's Build Settings are
    /// not modified. Detailed result in Builds/last_build.txt.
    /// </summary>
    public static class RoboFactoryBuild
    {
        public const string Scene = "Assets/Warehouse_Robot_Controller/Scenes/warehouse_robot_demo.unity";
        /// <summary>Cube scene of the public repository (used when the full scene does not exist).</summary>
        public const string PublicScene = "Assets/RoboFactorySim/Scenes/warehouse_simplified.unity";

        private static string SceneToBuild() => File.Exists(Path.Combine(Directory.GetParent(Application.dataPath).FullName, Scene)) ? Scene : PublicScene;

        [MenuItem("RoboFactory/Build/Build Windows")]
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
            Debug.Log("[Build] starting: " + exe);
            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary s = report.summary;
            string lines = "result   : " + s.result + "\n" +
                           "output   : " + exe + "\n" +
                           "duration : " + s.totalTime + "\n" +
                           "size     : " + (s.totalSize / (1024f * 1024f)).ToString("F0") + " MB\n" +
                           "errors   : " + s.totalErrors + "   warnings : " + s.totalWarnings + "\n";
            foreach (BuildStep step in report.steps)
                foreach (BuildStepMessage m in step.messages.Where(m => m.type == LogType.Error || m.type == LogType.Exception))
                    lines += "ERROR [" + step.name + "] " + m.content + "\n";
            File.WriteAllText(summaryFile, lines);
            if (s.result == BuildResult.Succeeded)
            {
                Debug.Log("[Build] OK: " + exe + " (" + (s.totalSize / (1024f * 1024f)).ToString("F0") + " MB, " + s.totalTime + ")");
                EditorUtility.RevealInFinder(exe);
            }
            else Debug.LogError("[Build] " + s.result + " — " + s.totalErrors + " error(s), details in Builds/last_build.txt");
        }
    }
}
