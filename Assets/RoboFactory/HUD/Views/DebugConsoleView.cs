using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RoboFactory.HUD
{
    /// <summary>Circular buffer of Unity logs (read-only through Application.logMessageReceived).</summary>
    public sealed class HudLogBuffer
    {
        public struct Entry
        {
            public float Time;
            public LogType Type;
            public string Text;
        }

        public readonly List<Entry> Entries = new List<Entry>();
        public int Capacity = 300;
        public int Version { get; private set; }

        public void Attach() { Application.logMessageReceived += OnLog; }
        public void Detach() { Application.logMessageReceived -= OnLog; }

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            if (string.IsNullOrEmpty(condition)) return;
            bool relevant = type != LogType.Log || condition.StartsWith("[");
            if (!relevant) return;
            string line = condition;
            int nl = line.IndexOf('\n');
            if (nl > 0) line = line.Substring(0, nl);
            if (line.Length > 220) line = line.Substring(0, 220) + "…";
            Entries.Add(new Entry { Time = Time.realtimeSinceStartup, Type = type, Text = line });
            if (Entries.Count > Capacity) Entries.RemoveAt(0);
            Version++;
        }
    }

    /// <summary>
    /// DEBUG mode: technical journal, internal orchestrator states, reservations,
    /// incidents, raw robot statuses. Hidden in CINEMATIC and OPERATIONS.
    /// </summary>
    public sealed class DebugConsoleView
    {
        public RectTransform Root { get; private set; }
        private TextMeshProUGUI logText, stateText;
        private int shownVersion = -1;
        private readonly StringBuilder sb = new StringBuilder(4096);
        private float fps, fpsTimer;
        private int fpsFrames;

        public void Build(RectTransform parent)
        {
            Root = HudFactory.GlassPanel(parent, "Debug Console", HudTheme.A(HudTheme.GlassStrong, .9f));
            HudFactory.Place(Root, new Vector2(.5f, 0f), new Vector2(.5f, 0f), new Vector2(0f, HudTheme.BottomRowY + HudTheme.VoiceH + 66f), new Vector2(1060f, 330f));

            TextMeshProUGUI t = HudFactory.Text(Root, "Title", "DEBUG  ·  DIAGNOSTICS", HudTheme.FontTitle, HudTheme.Amber, true, TextAlignmentOptions.Left, HudTheme.TitleSpacing);
            HudFactory.Place(t.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f, -9f), new Vector2(400f, 22f));
            TextMeshProUGUI hint = HudFactory.Text(Root, "Hint", "F1  legacy debug panel", HudTheme.FontMicro, HudTheme.TextLow, false, TextAlignmentOptions.Right);
            HudFactory.Place(hint.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-16f, -12f), new Vector2(420f, 14f));

            TextMeshProUGUI ll = HudFactory.Label(Root, "Log Label", "EVENT LOG", HudTheme.FontMicro);
            HudFactory.Place(ll.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f, -46f), new Vector2(200f, 18f));
            RectTransform logMask = HudFactory.Rect("Log Mask", Root);
            HudFactory.Place(logMask, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(16f, 12f), new Vector2(640f, 250f));
            logMask.gameObject.AddComponent<RectMask2D>();
            logText = HudFactory.Text(logMask, "Log", "", HudTheme.FontMicro, HudTheme.TextMid);
            logText.textWrappingMode = TextWrappingModes.NoWrap;
            logText.overflowMode = TextOverflowModes.Overflow;
            logText.alignment = TextAlignmentOptions.BottomLeft;
            logText.lineSpacing = 0f;
            HudFactory.Stretch(logText.rectTransform);

            TextMeshProUGUI sl = HudFactory.Label(Root, "State Label", "INTERNAL STATE", HudTheme.FontMicro);
            HudFactory.Place(sl.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(676f, -46f), new Vector2(200f, 18f));
            stateText = HudFactory.Text(Root, "State", "", HudTheme.FontMicro, HudTheme.TextMid);
            stateText.textWrappingMode = TextWrappingModes.NoWrap;
            stateText.overflowMode = TextOverflowModes.Truncate;
            stateText.alignment = TextAlignmentOptions.TopLeft;
            stateText.lineSpacing = 0f;
            HudFactory.Place(stateText.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(676f, -66f), new Vector2(370f, 252f));
        }

        public void Tick(float dt)
        {
            fpsFrames++;
            fpsTimer += dt;
            if (fpsTimer >= .5f)
            {
                fps = fpsFrames / fpsTimer;
                fpsFrames = 0;
                fpsTimer = 0f;
            }
        }

        public void Refresh(HudModel m, HudLogBuffer log)
        {
            if (log != null && log.Version != shownVersion)
            {
                shownVersion = log.Version;
                sb.Length = 0;
                int start = Mathf.Max(0, log.Entries.Count - 12);
                for (int i = start; i < log.Entries.Count; i++)
                {
                    HudLogBuffer.Entry e = log.Entries[i];
                    Color c = e.Type == LogType.Error || e.Type == LogType.Exception || e.Type == LogType.Assert ? HudTheme.Red
                            : e.Type == LogType.Warning ? HudTheme.Amber : HudTheme.TextMid;
                    sb.Append("<color=#").Append(HudTheme.Hex(HudTheme.TextLow)).Append('>')
                      .Append(e.Time.ToString("0000.0")).Append("</color>  <color=#").Append(HudTheme.Hex(c)).Append('>')
                      .Append(Escape(e.Text)).Append("</color>\n");
                }
                HudFactory.Set(logText, sb.ToString());
            }

            DebugVM d = m.Debug;
            UrgentVM u = m.Urgent;
            sb.Length = 0;
            Kv("phase", d.Phase);
            Kv("state version", "v" + d.StateVersion + "   calls " + d.Calls);
            Kv("pending", string.IsNullOrEmpty(d.PendingInfo) ? "—" : d.PendingInfo);
            Kv("validator", d.Validation);
            Kv("urgent stage", u.Stage + (u.OrderId >= 0 ? "  #" + u.OrderId : ""));
            Kv("suspend/pause", u.Suspending + " / " + u.Paused + "   resumed " + u.Resumed);
            if (!string.IsNullOrEmpty(u.Status)) Kv("urgent status", u.Status);
            Kv("R1 raw", m.R1.RawStatus + (m.R1.LegInfo != "" ? "  [" + m.R1.LegInfo + "]" : ""));
            Kv("R2 raw", m.R2.RawStatus);
            Kv("reservations", d.Reservations.Count == 0 ? "none" : string.Join("  ", d.Reservations));
            int n = d.Incidents.Count;
            Kv("incidents", n == 0 ? "none" : n + " recorded");
            for (int i = Mathf.Max(0, n - 3); i < n; i++) sb.Append("   <color=#").Append(HudTheme.Hex(HudTheme.Amber)).Append('>').Append(Escape(d.Incidents[i])).Append("</color>\n");
            Kv("trace file", string.IsNullOrEmpty(d.TraceFile) ? "—" : System.IO.Path.GetFileName(d.TraceFile));
            Kv("render", Mathf.RoundToInt(fps) + " fps");
            HudFactory.Set(stateText, sb.ToString());
        }

        private void Kv(string k, string v)
        {
            sb.Append("<color=#").Append(HudTheme.Hex(HudTheme.TextLow)).Append('>').Append(k.ToUpperInvariant())
              .Append("</color><pos=120>").Append(Escape(v ?? "")).Append('\n');
        }

        private static string Escape(string s)
        {
            return s.Replace("<", "‹").Replace(">", "›");
        }
    }
}
