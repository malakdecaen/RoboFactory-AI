#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace RoboFactory.Launcher.EditorTools
{
    /// <summary>
    /// Promotes the recordings captured in LIVE AI (RecordedDemos_Captured/) to the demos shipped with the
    /// build (Assets/StreamingAssets/RecordedDemos/&lt;scenario&gt;.json). Only COMPLETE recordings of real Meta
    /// calls are promoted; the newest one of each scenario wins. Nothing is invented or edited.
    /// </summary>
    public static class RecordedDemoMenu
    {
        private static string CaptureDir => Path.Combine(Directory.GetParent(Application.dataPath).FullName, "RecordedDemos_Captured");
        private static string ShippedDir => Path.Combine(Application.dataPath, "StreamingAssets", "RecordedDemos");

        [MenuItem("RoboFactory/Recorded Demo/Promote captured recordings")]
        public static void Promote()
        {
            if (!Directory.Exists(CaptureDir))
            {
                EditorUtility.DisplayDialog("Recorded demo", "No capture folder yet.\nRun a scenario in LIVE AI with recording first.", "OK");
                return;
            }
            var plan = new List<(string id, string source, bool replaces)>();
            foreach (ScenarioScript s in ScenarioScripts.All)
            {
                string best = null;
                DateTime bestTime = DateTime.MinValue;
                foreach (string file in Directory.GetFiles(CaptureDir, s.Id + "_*.json"))
                {
                    if (!IsCompleteRecording(file, s.Id)) continue;
                    DateTime t = File.GetLastWriteTimeUtc(file);
                    if (t > bestTime) { bestTime = t; best = file; }
                }
                if (best != null) plan.Add((s.Id, best, File.Exists(Path.Combine(ShippedDir, s.Id + ".json"))));
            }
            if (plan.Count == 0)
            {
                EditorUtility.DisplayDialog("Recorded demo", "No complete recording found in:\n" + CaptureDir, "OK");
                return;
            }
            string list = "";
            foreach (var p in plan) list += "• " + p.id + (p.replaces ? "  (replaces the current demo)" : "") + "\n";
            if (!EditorUtility.DisplayDialog("Promote recordings", "These scenarios will be shipped with the build:\n\n" + list, "Promote", "Cancel")) return;
            Directory.CreateDirectory(ShippedDir);
            foreach (var p in plan)
            {
                File.Copy(p.source, Path.Combine(ShippedDir, p.id + ".json"), true);
                Debug.Log("[RecordedDemo] promoted " + p.id + " <- " + Path.GetFileName(p.source));
            }
            AssetDatabase.Refresh();
        }

        [MenuItem("RoboFactory/Recorded Demo/Open captured recordings folder")]
        public static void OpenCaptured()
        {
            Directory.CreateDirectory(CaptureDir);
            EditorUtility.RevealInFinder(CaptureDir);
        }

        private static bool IsCompleteRecording(string file, string scenarioId)
        {
            try
            {
                JObject root = JObject.Parse(File.ReadAllText(file));
                return (int?)root["format"] == 1 && (string)root["scenario"] == scenarioId &&
                       ((bool?)root["complete"] ?? false) && root["calls"] is JArray calls && calls.Count > 0;
            }
            catch (Exception) { return false; }
        }
    }
}
#endif
