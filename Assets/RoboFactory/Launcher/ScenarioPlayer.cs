using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using RoboFactory.Hackathon.Orchestration;
using RoboFactory.Logistics;
using RoboFactory.MetaServer;
using UnityEngine;

namespace RoboFactory.Launcher
{
    /// <summary>
    /// Runs a demo scenario on the REAL warehouse: it only sends the operator's inputs (orders, spoken
    /// urgent order) through the normal orchestrator entry points, at the recorded times. Muse decisions,
    /// plan validation, robots, doors and trucks are the existing ones.
    ///   - RECORDED DEMO: inputs and Meta answers come from the recording.
    ///   - LIVE AI + record: the scripted inputs are sent, real Meta answers are captured and saved.
    /// A cargo audit follows every box and reports at the end of the scenario.
    /// </summary>
    public sealed class ScenarioPlayer : MonoBehaviour
    {
        public enum PlayerState { Idle, Running, Complete }

        public static ScenarioPlayer Instance { get; private set; }

        public PlayerState State { get; private set; } = PlayerState.Idle;
        public string ScenarioId { get; private set; } = "";
        public string Title { get; private set; } = "";
        public bool Replay { get; private set; }
        public bool Passed { get; private set; }
        public string ResultText { get; private set; } = "";
        public string DetailText { get; private set; } = "";
        public string SavedPath { get; private set; } = "";

        private MuseOrchestrator orch;
        private readonly List<ScriptInput> inputs = new List<ScriptInput>();
        private readonly HashSet<int> knownOrders = new HashSet<int>();
        private readonly CargoAudit audit = new CargoAudit();
        private int next;
        private float doneSince = -1f;
        private bool recording;

        // ------------------------------------------------------------------ files

        public static string RecordingsDir => Path.Combine(Application.streamingAssetsPath, "RecordedDemos");

        public static string CaptureDir
        {
            get
            {
                string root = Application.isEditor
                    ? Directory.GetParent(Application.dataPath).FullName
                    : Application.persistentDataPath;
                return Path.Combine(root, "RecordedDemos_Captured");
            }
        }

        public static string RecordingPath(string id) { return Path.Combine(RecordingsDir, id + ".json"); }

        /// <summary>Is a valid recording shipped for this scenario? (number of recorded Meta calls)</summary>
        public static bool HasRecording(string id, out int calls)
        {
            calls = 0;
            try
            {
                string path = RecordingPath(id);
                if (!File.Exists(path)) return false;
                JObject root = RecordedDemoRuntime.Parse(File.ReadAllText(path), out _);
                if (root == null) return false;
                calls = ((JArray)root["calls"]).Count;
                return calls > 0;
            }
            catch (Exception) { return false; }
        }

        // ------------------------------------------------------------------ start

        private void Awake() { Instance = this; }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            Application.quitting -= OnQuit;
        }

        /// <summary>RECORDED DEMO: replays the recording of the scenario.</summary>
        public bool BeginReplay(string id, out string error)
        {
            error = "";
            string path = RecordingPath(id);
            if (!File.Exists(path))
            {
                error = "No recording for this scenario yet. Run it once in LIVE AI with recording enabled.";
                return false;
            }
            JObject root = RecordedDemoRuntime.Parse(File.ReadAllText(path), out error);
            if (root == null) return false;
            inputs.Clear();
            foreach (JToken tok in (JArray)root["inputs"])
            {
                var o = (JObject)tok;
                inputs.Add(new ScriptInput
                {
                    T = (double?)o["t"] ?? 0,
                    Urgent = (string)o["kind"] == "urgent",
                    A = (int?)o["a"] ?? 0, B = (int?)o["b"] ?? 0, C = (int?)o["c"] ?? 0,
                    Transcript = (string)o["transcript"] ?? ""
                });
            }
            inputs.Sort((x, y) => x.T.CompareTo(y.T));
            RecordedDemoRuntime.StartReplay(root);
            Replay = true; recording = false;
            Launch(id, (string)root["title"] ?? id);
            return true;
        }

        /// <summary>LIVE AI: sends the scripted inputs; the real Meta answers are recorded.</summary>
        public bool BeginRecording(string id, out string error)
        {
            error = "";
            ScenarioScript script = ScenarioScripts.Find(id);
            if (script == null) { error = "unknown scenario"; return false; }
            inputs.Clear();
            inputs.AddRange(script.Inputs);
            RecordedDemoRuntime.StartRecord(script.Id, script.Title);
            Replay = false; recording = true;
            Application.quitting += OnQuit;
            Launch(id, script.Title);
            return true;
        }

        private void Launch(string id, string title)
        {
            ScenarioId = id; Title = title;
            orch = FindFirstObjectByType<MuseOrchestrator>();
            next = 0; doneSince = -1f; Passed = false; ResultText = DetailText = SavedPath = "";
            knownOrders.Clear();
            audit.Begin();
            State = PlayerState.Running;
            Debug.Log("[Scenario] " + title + " started (" + (Replay ? "RECORDED DEMO" : "LIVE AI, recording") + ", " +
                      inputs.Count + " inputs)");
        }

        // ------------------------------------------------------------------ run

        private void Update()
        {
            if (State != PlayerState.Running || orch == null) return;
            audit.Tick();
            double now = RecordedDemoRuntime.Elapsed;
            while (next < inputs.Count && now >= inputs[next].T)
            {
                Fire(inputs[next], now);
                next++;
            }
            if (recording) NoteManualOrders(now);
            if (next < inputs.Count) return;

            // done: every input sent and every order loaded, nothing pending, stable for a few seconds
            WhOrderState[] orders = orch.OrdersSnapshot;
            bool done = orders.Length > 0 && !orch.Pending && !orch.UrgentPending &&
                        (orch.Stage == MuseOrchestrator.UrgentStage.Idle || orch.Stage == MuseOrchestrator.UrgentStage.Completed);
            foreach (WhOrderState o in orders) if (o.status != "loaded") done = false;
            if (!done) { doneSince = -1f; return; }
            if (doneSince < 0f) doneSince = Time.time;
            if (Time.time - doneSince >= 4f) Finish(orders);
        }

        private void Fire(ScriptInput input, double now)
        {
            if (input.Urgent)
            {
                orch.SubmitUrgentTranscript(input.Transcript);
                Debug.Log("[Scenario] t=" + now.ToString("F0") + " s: urgent voice order: " + input.Transcript);
                return;
            }
            int id = orch.SubmitOrderCounts(input.A, input.B, input.C);
            if (id > 0) knownOrders.Add(id);
            if (recording && id > 0) RecordOrder(now, input.A, input.B, input.C);
            Debug.Log("[Scenario] t=" + now.ToString("F0") + " s: order #" + id + " A" + input.A + " B" + input.B + " C" + input.C);
        }

        /// <summary>Orders typed by hand while recording are part of the recording too.</summary>
        private void NoteManualOrders(double now)
        {
            foreach (WhOrderState o in orch.OrdersSnapshot)
            {
                if (o.urgent || !knownOrders.Add(o.id)) continue;
                RecordOrder(now, o.a, o.b, o.c);
            }
        }

        private static void RecordOrder(double t, int a, int b, int c)
        {
            RecordedDemoRuntime.AddInput(new JObject
            {
                ["t"] = Math.Round(t, 2), ["kind"] = "order", ["a"] = a, ["b"] = b, ["c"] = c
            });
        }

        private void Finish(WhOrderState[] orders)
        {
            int ordered = 0;
            foreach (WhOrderState o in orders) ordered += o.a + o.b + o.c;
            audit.Tick();
            Passed = audit.Passed && audit.Delivered == ordered;
            ResultText = (Passed ? "CARGO AUDIT PASSED" : "CARGO AUDIT: CHECK NEEDED");
            DetailText = audit.Summary(ordered);
            foreach (string issue in audit.Issues) DetailText += "\n" + issue;
            if (recording)
            {
                string path = RecordedDemoRuntime.Save(CaptureDir, true, out string error);
                SavedPath = path;
                DetailText += path.Length > 0 ? "\nRecording saved: " + path : "\nRecording NOT saved: " + error;
            }
            State = PlayerState.Complete;
            Debug.Log("[Scenario] " + Title + " complete. " + ResultText + " — " + DetailText.Replace("\n", " | "));
            WriteAuditLog();
        }

        private void WriteAuditLog()
        {
            try
            {
                string dir = Path.Combine(Application.isEditor ? Directory.GetParent(Application.dataPath).FullName
                                                               : Application.persistentDataPath, "logs");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "scenario_audit.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + (Replay ? "RECORDED DEMO" : "LIVE AI") + "  " +
                    ScenarioId + "  " + ResultText + "  " + DetailText.Replace("\n", " | ") + "\n");
            }
            catch (Exception) { }
        }

        private void OnQuit()
        {
            if (!recording || State == PlayerState.Complete) return;
            RecordedDemoRuntime.Save(CaptureDir, false, out _);   // partial: never promoted as a demo
        }
    }
}
