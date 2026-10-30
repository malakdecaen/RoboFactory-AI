using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RoboFactory.Hackathon.Orchestration;
using RoboFactory.Integration;
using UnityEngine;

namespace RoboFactory.Logistics
{
    /// <summary>
    /// Detailed journal of a PlayMode session: one file per Play in
    /// &lt;project&gt;/logs/muse_trace_*.log. Each line = real timestamp, game
    /// time, frame, category, message. Written immediately (flush) so that the
    /// file stays usable even if Play is stopped abruptly.
    /// </summary>
    public static class MuseTrace
    {
        private static StreamWriter writer;
        public static string FilePath { get; private set; } = "";

        public static void Log(string category, string message)
        {
            try
            {
                if (writer == null) Open();
                if (writer == null) return;
                float t = Application.isPlaying ? Time.time : 0f;
                writer.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  t=" +
                                 t.ToString("F2").PadLeft(7) + "  f=" +
                                 Time.frameCount.ToString().PadLeft(6) + "  [" +
                                 category.PadRight(8) + "] " + message);
            }
            catch (Exception)
            {
                // the journal must never break the simulation
            }
        }

        private static void Open()
        {
            string dir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "logs");
            Directory.CreateDirectory(dir);
            FilePath = Path.Combine(dir, "muse_trace_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
            writer = new StreamWriter(FilePath, false, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine("# RoboFactory — trace PlayMode " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                             "  scene=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
            Debug.Log("[MuseTrace] journal : " + FilePath);
        }

        public static void Close()
        {
            if (writer == null) return;
            Log("TRACE", "end of session");
            writer.Dispose();
            writer = null;
        }
    }

    /// <summary>
    /// Differential recorder: compares the full simulation state
    /// every 0.1 s and logs each change. Also captures the
    /// tagged Unity messages ([Muse], [Truck], ...), warnings and exceptions.
    /// Installed automatically as soon as a MuseOrchestrator is present.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MuseTraceRecorder : MonoBehaviour
    {
        public float Interval = .1f;

        private MuseOrchestrator orch;
        private VoiceCommandController voice;
        private readonly Dictionary<string, string> last = new Dictionary<string, string>();
        private float next;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoInstall()
        {
            if (FindFirstObjectByType<MuseOrchestrator>() == null) return;
            if (FindFirstObjectByType<MuseTraceRecorder>() != null) return;
            new GameObject("MuseTraceRecorder").AddComponent<MuseTraceRecorder>();
        }

        private void OnEnable() { Application.logMessageReceived += OnUnityLog; }

        private void OnDisable() { Application.logMessageReceived -= OnUnityLog; }

        private void OnApplicationQuit() { MuseTrace.Close(); }

        private void Start()
        {
            orch = FindFirstObjectByType<MuseOrchestrator>();
            MuseTrace.Log("TRACE", "start — mode " + (orch != null ? orch.Mode : "?") +
                          ", model " + (orch != null ? orch.ModelUsed : "?"));
        }

        private void OnUnityLog(string message, string stack, LogType type)
        {
            if (string.IsNullOrEmpty(message) || message.StartsWith("[MuseTrace]")) return;
            bool tagged = message.StartsWith("[");
            if (type == LogType.Exception || type == LogType.Error)
                MuseTrace.Log("ERROR", message + "\n" + stack);
            else if (type == LogType.Warning && tagged)
                MuseTrace.Log("WARN", message);
            else if (tagged)
                MuseTrace.Log("UNITY", message);
        }

        private void Update()
        {
            if (Time.time < next) return;
            next = Time.time + Interval;
            if (orch == null) orch = FindFirstObjectByType<MuseOrchestrator>();
            if (orch == null) return;
            if (voice == null) voice = FindFirstObjectByType<VoiceCommandController>();

            // ---- pipeline
            Diff("STAGE", "stage", orch.Stage.ToString());
            Diff("STAGE", "phase", orch.CurrentPhase.ToString());
            Diff("URGENT", "status", orch.UrgentStatus);
            Diff("URGENT", "error", orch.UrgentError);
            Diff("URGENT", "counts", "urgent=#" + orch.UrgentOrderId + " suspending=" +
                 orch.SuspendingCount + " paused=" + orch.PausedCount + " resumed=" + orch.ResumedCount);

            // ---- orders
            foreach (WhOrderState o in orch.OrdersSnapshot)
                Diff("ORDER", "order" + o.id,
                     "#" + o.id + (o.urgent ? " URGENT" : "") + " [" + o.status + "] need " +
                     o.a + "A/" + o.b + "B/" + o.c + "C  bench " + Cnt(o.staged_a, o.staged_b, o.staged_c) +
                     "  truck " + Cnt(o.loaded_a, o.loaded_b, o.loaded_c) +
                     "  -> " + (string.IsNullOrEmpty(o.bench_id) ? "-" : o.bench_id) + "/" +
                     (string.IsNullOrEmpty(o.truck_id) ? "-" : o.truck_id));

            // ---- resources
            WarehouseWorld world = WarehouseWorld.Instance;
            TruckLoaderAgent r2 = orch.Robot2;
            if (world != null)
            {
                foreach (string id in world.PalletIds)
                    Diff("RESERV", "res" + id, id + " reserved by " + (world.ReservedBy(id) ?? "nobody"));
                foreach (string id in world.BenchIds)
                    Diff("RESERV", "res" + id, id + " reserved by " + (world.ReservedBy(id) ?? "nobody"));
                foreach (string id in world.TruckIds)
                {
                    TruckBay bay = world.Truck(id);
                    if (bay == null) continue;
                    bool r2In = r2 != null && bay.IsInside(r2.transform.position);
                    Diff("TRUCK", "truck" + id, id + " door=" + bay.Door + " reserved=#" +
                         bay.ReservedOrderId + " loaded=" + bay.LoadedCount + " R2inside=" + r2In);
                    // Safety alert: R2 inside a trailer whose door is not open.
                    bool danger = r2In && !bay.IsDoorOpen;
                    Diff("ALERT", "trap" + id, danger
                        ? "!!! R2 INSIDE " + id + " WITH DOOR " + bay.Door + " — risk of being locked in"
                        : "", logEmpty: false);
                }
            }

            // ---- robots
            WarehouseRobotAutopilot r1 = orch.Robot1;
            if (r1 != null)
                Diff("R1", "r1", r1.Status + "  mission=" + r1.MissionActive,
                     suffix: "  pos=" + Pos(r1.transform));
            if (r2 != null)
                Diff("R2", "r2", r2.Status + "  mission=" + r2.MissionActive + " order=#" +
                     r2.DirectedOrderId + "  carrying=" + (r2.Carried != null ? r2.Carried.name : "nothing"),
                     suffix: "  pos=" + Pos(r2.transform));

            // ---- voice
            if (voice != null)
            {
                Diff("VOICE", "vstate", voice.State + " (" + voice.LastAsrMode + ")");
                Diff("VOICE", "vstatus", voice.StatusText);
                Diff("VOICE", "vlive", voice.LiveTranscript.Length > 0 ? "partial: " + voice.LiveTranscript : "");
                Diff("VOICE", "vfinal", voice.FinalTranscript.Length > 0
                    ? "FINAL : " + voice.FinalTranscript + "  (ASR " + voice.LastAsrLatencyMs +
                      " ms, audio " + voice.AudioProcessedMs + " ms)" : "");
                Diff("VOICE", "verr", voice.LastFailure);
            }
        }

        /// <summary>Logs the key only if its value changed.</summary>
        private void Diff(string category, string key, string value, bool logEmpty = true,
                          string suffix = "")
        {
            value = value ?? "";
            if (last.TryGetValue(key, out string prev) && prev == value) return;
            last[key] = value;
            if (value.Length == 0 && (!logEmpty || prev == null)) return;
            MuseTrace.Log(category, value.Length > 0 ? value + suffix : key + " cleared");
        }

        private static string Cnt(int a, int b, int c) => a + "A/" + b + "B/" + c + "C";

        private static string Pos(Transform t) =>
            "(" + t.position.x.ToString("F1") + "," + t.position.z.ToString("F1") + ")";
    }
}
