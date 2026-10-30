#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using RoboFactory.Logistics;
using UnityEditor;
using UnityEngine;

namespace RoboFactory.HUD.EditorTools
{
    /// <summary>
    /// HUD visual validation tool (editor only):
    /// - sets the Game View resolution (1920×1080, 3840×2160, 12288×2160);
    /// - captures the Game View in PlayMode (ScreenCapture, UI included) into HudCaptures/;
    /// - demo shortcuts that call the SAME public APIs as the old debug HUD
    ///   (normal demo order, manual urgent) and the HUD mode switch.
    /// </summary>
    [InitializeOnLoad]
    public static class MuseHudCaptureTool
    {
        static MuseHudCaptureTool()
        {
            EditorApplication.update += PollCommands;
        }

        private static double nextPoll;

        /// <summary>
        /// File commands (HudCaptures/cmd.txt, one per line) to drive the captures
        /// without a keyboard: play | stop | capture | suite | order | urgent | mode cinematic|operations|debug
        /// | size W H. The file is consumed then deleted; the log goes to HudCaptures/log.txt.
        /// </summary>
        private static void PollCommands()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now < nextPoll) return;
            nextPoll = now + .5;
            string file = Path.Combine(Folder, "cmd.txt");
            if (!File.Exists(file) || queue != null) return;
            string[] lines;
            try { lines = File.ReadAllLines(file); File.Delete(file); }
            catch { return; }
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                Log("cmd: " + line);
                string[] a = line.Split(' ');
                switch (a[0].ToLowerInvariant())
                {
                    case "play":
                        // PlayMode keeps running at full speed even if the editor does not have focus
                        EditorApplication.playModeStateChanged -= KeepRunning;
                        EditorApplication.playModeStateChanged += KeepRunning;
                        EditorApplication.isPlaying = true;
                        break;
                    case "stop": EditorApplication.isPlaying = false; break;
                    case "capture": Run(new[] { 0 }, a.Length > 1 ? "_" + a[1] : ""); break;
                    case "suite": Run(new[] { 0, 1, 2 }, a.Length > 1 ? "_" + a[1] : ""); break;
                    case "refresh": AssetDatabase.Refresh(); break;
                    case "order": DemoOrder(); break;
                    case "urgent": DemoUrgent(); break;
                    case "cam":
                        {
                            MuseHudRoot root = UnityEngine.Object.FindFirstObjectByType<MuseHudRoot>();
                            if (root != null) root.ToggleCamera();
                            break;
                        }
                    case "size": if (a.Length >= 3) Select(FindOrAdd(int.Parse(a[1]), int.Parse(a[2]))); break;
                    case "mode":
                        if (a.Length > 1)
                            SetMode(a[1].StartsWith("c") ? HudMode.Cinematic : a[1].StartsWith("d") ? HudMode.Debug : HudMode.Operations);
                        break;
                }
            }
        }

        private static void KeepRunning(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredPlayMode) Application.runInBackground = true;
        }

        private static void Log(string msg)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(Path.Combine(Folder, "log.txt"), DateTime.Now.ToString("HH:mm:ss ") + msg + "\n");
            }
            catch { }
        }
        private static readonly Vector2Int[] Sizes = { new Vector2Int(1920, 1080), new Vector2Int(3840, 2160), new Vector2Int(12288, 2160) };
        private static int[] queue;
        private static int cursor;
        private static int phase;
        private static double until;
        private static int restoreIndex = -1;
        private static string tag = "";

        private const string Folder = "HudCaptures";

        [MenuItem("RoboFactory/HUD/Capture 1920x1080 %#F9")]
        private static void Capture1080() { Run(new[] { 0 }, ""); }

        [MenuItem("RoboFactory/HUD/Capture 1080p + 4K + triple %#F10")]
        private static void CaptureSuite() { Run(new[] { 0, 1, 2 }, ""); }

        [MenuItem("RoboFactory/HUD/Game View 1920x1080 %#F8")]
        private static void Set1080() { Select(FindOrAdd(1920, 1080)); }

        [MenuItem("RoboFactory/HUD/Demo - normal order (A x2, B x1) %#F11")]
        private static void DemoOrder()
        {
            MuseOrchestrator o = UnityEngine.Object.FindFirstObjectByType<MuseOrchestrator>();
            if (o == null) { Debug.LogWarning("[HUD capture] no orchestrator (PlayMode?)"); return; }
            o.SubmitOrder("A x2, B x1");
        }

        [MenuItem("RoboFactory/HUD/Demo - manual urgent (A x1, B x1) %#F12")]
        private static void DemoUrgent()
        {
            MuseOrchestrator o = UnityEngine.Object.FindFirstObjectByType<MuseOrchestrator>();
            if (o == null) { Debug.LogWarning("[HUD capture] no orchestrator (PlayMode?)"); return; }
            o.SubmitUrgentCounts(1, 1, 0);
        }

        [MenuItem("RoboFactory/HUD/Mode CINEMATIC")] private static void ModeC() { SetMode(HudMode.Cinematic); }
        [MenuItem("RoboFactory/HUD/Mode OPERATIONS")] private static void ModeO() { SetMode(HudMode.Operations); }
        [MenuItem("RoboFactory/HUD/Mode DEBUG")] private static void ModeD() { SetMode(HudMode.Debug); }

        private static void SetMode(HudMode m)
        {
            MuseHudRoot root = UnityEngine.Object.FindFirstObjectByType<MuseHudRoot>();
            if (root != null) root.SetMode(m, false);
        }

        // ------------------------------------------------------------------ sequential capture

        private static void Run(int[] sizes, string label)
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[HUD capture] start PlayMode first.");
                Log("capture refused: not playing");
                return;
            }
            if (queue != null) return;
            Directory.CreateDirectory(Folder);
            restoreIndex = CurrentIndex();
            queue = sizes;
            cursor = 0;
            phase = 0;
            tag = label;
            EditorApplication.update += Tick;
        }

        private static void Tick()
        {
            if (queue == null) { EditorApplication.update -= Tick; return; }
            double now = EditorApplication.timeSinceStartup;
            if (now < until) return;
            if (cursor >= queue.Length)
            {
                if (restoreIndex >= 0) Select(restoreIndex);
                queue = null;
                EditorApplication.update -= Tick;
                Debug.Log("[HUD capture] done → " + Path.GetFullPath(Folder));
                return;
            }
            Vector2Int s = Sizes[queue[cursor]];
            if (phase == 0)
            {
                Select(FindOrAdd(s.x, s.y));
                until = now + 1.6; // layout + animations
                phase = 1;
            }
            else if (phase == 1)
            {
                string mode = "";
                MuseHudRoot root = UnityEngine.Object.FindFirstObjectByType<MuseHudRoot>();
                if (root != null) mode = "_" + root.Mode.ToString().ToLowerInvariant();
                string file = Path.Combine(Folder, "hud_" + s.x + "x" + s.y + mode + tag + "_" + DateTime.Now.ToString("HHmmss") + ".png");
                ScreenCapture.CaptureScreenshot(file);
                Debug.Log("[HUD capture] " + file);
                Log("captured " + file);
                until = now + 1.2;
                phase = 0;
                cursor++;
            }
        }

        // ------------------------------------------------------------------ Game View (reflection)

        private static readonly Type GameViewType = Type.GetType("UnityEditor.GameView,UnityEditor");
        private static readonly Type SizesType = Type.GetType("UnityEditor.GameViewSizes,UnityEditor");
        private static readonly Type SizeType = Type.GetType("UnityEditor.GameViewSize,UnityEditor");
        private static readonly Type SizeKindType = Type.GetType("UnityEditor.GameViewSizeType,UnityEditor");
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static object Group()
        {
            Type single = typeof(ScriptableSingleton<>).MakeGenericType(SizesType);
            object instance = single.GetProperty("instance").GetValue(null);
            object current = SizesType.GetProperty("currentGroupType").GetValue(instance);
            return SizesType.GetMethod("GetGroup").Invoke(instance, new[] { current });
        }

        private static int FindOrAdd(int w, int h)
        {
            object g = Group();
            Type gt = g.GetType();
            int total = (int)gt.GetMethod("GetTotalCount").Invoke(g, null);
            MethodInfo get = gt.GetMethod("GetGameViewSize");
            for (int i = 0; i < total; i++)
            {
                object s = get.Invoke(g, new object[] { i });
                int sw = (int)SizeType.GetProperty("width").GetValue(s);
                int sh = (int)SizeType.GetProperty("height").GetValue(s);
                string kind = SizeType.GetProperty("sizeType").GetValue(s).ToString();
                if (sw == w && sh == h && kind == "FixedResolution") return i;
            }
            ConstructorInfo ctor = SizeType.GetConstructor(new[] { SizeKindType, typeof(int), typeof(int), typeof(string) });
            object size = ctor.Invoke(new object[] { Enum.Parse(SizeKindType, "FixedResolution"), w, h, "HUD " + w + "x" + h });
            gt.GetMethod("AddCustomSize").Invoke(g, new[] { size });
            return total;
        }

        private static EditorWindow GameView()
        {
            return EditorWindow.GetWindow(GameViewType, false, null, false);
        }

        private static int CurrentIndex()
        {
            PropertyInfo p = GameViewType.GetProperty("selectedSizeIndex", Any);
            return p != null ? (int)p.GetValue(GameView()) : -1;
        }

        private static void Select(int index)
        {
            EditorWindow gv = GameView();
            MethodInfo cb = GameViewType.GetMethod("SizeSelectionCallback", Any);
            if (cb != null) cb.Invoke(gv, new object[] { index, null });
            else GameViewType.GetProperty("selectedSizeIndex", Any)?.SetValue(gv, index);
            gv.Repaint();
        }
    }
}
#endif
