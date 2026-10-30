using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RoboFactory.HUD
{
    /// <summary>
    /// Dock vocal compact : bouton micro, onde audio réelle, transcription progressive,
    /// badge LIVE / MOCK / OFFLINE. Les actions appellent VoiceCommandController (Toggle / Cancel).
    /// </summary>
    public sealed class VoiceDockView
    {
        public RectTransform Root { get; private set; }
        public Action ToggleMic;
        public Action CancelMic;

        private ArcGraphic ring, ringPulse;
        private Image micDisk, recDot;
        private Image[] micIcon;
        private ArcGraphic micCup;
        private Chip modeChip;
        private TextMeshProUGUI statusText, transcriptText, asrText;
        private PolylineGraphic waveTop, waveBottom;
        private Button cancel;
        private readonly List<Vector2> top = new List<Vector2>();
        private readonly List<Vector2> bottom = new List<Vector2>();
        private string targetTranscript = "";
        private float shownChars;
        private float time;

        public void Build(RectTransform parent)
        {
            Root = HudFactory.GlassPanel(parent, "Voice Dock", HudTheme.GlassStrong, null, 10);
            HudFactory.Place(Root, new Vector2(.5f, 0f), new Vector2(.5f, 0f), new Vector2(0f, HudTheme.BottomRowY), new Vector2(HudTheme.VoiceW, HudTheme.VoiceH));

            // ---- bouton micro (60 px)
            RectTransform mic = HudFactory.Rect("Mic Button", Root);
            HudFactory.Place(mic, new Vector2(0f, .5f), new Vector2(.5f, .5f), new Vector2(46f, 0f), new Vector2(60f, 60f));
            micDisk = mic.gameObject.AddComponent<Image>();
            micDisk.sprite = HudSprites.Circle();
            micDisk.color = HudTheme.A(HudTheme.Cyan, .14f);
            var button = mic.gameObject.AddComponent<Button>();
            button.targetGraphic = micDisk;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            button.onClick.AddListener(() => ToggleMic?.Invoke());
            ring = HudFactory.Add<ArcGraphic>(mic, "Ring");
            ring.Set(30f, 2f, 0f, 360f, 1, 1f);
            ringPulse = HudFactory.Add<ArcGraphic>(mic, "Pulse");
            ringPulse.Set(30f, 1.5f, 0f, 360f, 1, 1f);
            Image capsule = HudFactory.Image(mic, "Capsule", HudSprites.Rounded(6), HudTheme.TextHi);
            HudFactory.Place(capsule.rectTransform, new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(0f, 5f), new Vector2(12f, 20f));
            micCup = HudFactory.Add<ArcGraphic>(mic, "Cup");
            micCup.Set(11f, 2f, 90f, 180f, 1, 1f);
            micCup.rectTransform.anchoredPosition = new Vector2(0f, 4f);
            Image stem = HudFactory.Image(mic, "Stem", null, HudTheme.TextHi);
            HudFactory.Place(stem.rectTransform, new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(0f, -11f), new Vector2(2f, 6f));
            Image foot = HudFactory.Image(mic, "Foot", null, HudTheme.TextHi);
            HudFactory.Place(foot.rectTransform, new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(0f, -14f), new Vector2(11f, 2f));
            micIcon = new[] { capsule, stem, foot };

            // ---- état
            TextMeshProUGUI label = HudFactory.Text(Root, "Label", "MUSE VOICE", 17f, HudTheme.TextHi, true, TextAlignmentOptions.Left, 2f);
            HudFactory.Place(label.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(88f, -9f), new Vector2(120f, 22f));
            modeChip = HudFactory.Chip(Root, "Mode", "LIVE", HudTheme.Green, 62f, 22f);
            HudFactory.Place(modeChip.Root, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(206f, -9f), new Vector2(62f, 22f));
            recDot = HudFactory.Image(Root, "Rec Dot", HudSprites.Circle(), HudTheme.Red, false);
            HudFactory.Place(recDot.rectTransform, new Vector2(0f, 1f), new Vector2(.5f, .5f), new Vector2(282f, -20f), new Vector2(10f, 10f));
            statusText = HudFactory.Text(Root, "Status", "", HudTheme.FontMicro, HudTheme.TextMid);
            HudFactory.Place(statusText.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(88f, 30f), new Vector2(200f, 18f));
            waveTop = HudFactory.Add<PolylineGraphic>(Root, "Wave Top");
            waveBottom = HudFactory.Add<PolylineGraphic>(Root, "Wave Bottom");
            foreach (PolylineGraphic w in new[] { waveTop, waveBottom })
            {
                w.Thickness = 1.6f;
                HudFactory.Place(w.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(88f, 6f), new Vector2(196f, 22f));
            }

            // ---- transcription (18 px)
            Image sep = HudFactory.Image(Root, "Separator", null, HudTheme.A(HudTheme.Cyan, .2f));
            HudFactory.Place(sep.rectTransform, new Vector2(0f, .5f), new Vector2(.5f, .5f), new Vector2(298f, 0f), new Vector2(1f, 54f));
            transcriptText = HudFactory.Text(Root, "Transcript", "", HudTheme.FontBody, HudTheme.TextHi);
            transcriptText.textWrappingMode = TextWrappingModes.Normal;
            transcriptText.maxVisibleLines = 2;
            transcriptText.lineSpacing = -8f;
            transcriptText.alignment = TextAlignmentOptions.MidlineLeft;
            HudFactory.Place(transcriptText.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(310f, -6f), new Vector2(232f, 50f));
            asrText = HudFactory.Text(Root, "ASR", "", HudTheme.FontMicro, HudTheme.TextLow, false, TextAlignmentOptions.Right);
            HudFactory.Place(asrText.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-12f, 3f), new Vector2(232f, 18f));
            cancel = HudFactory.Button(Root, "Cancel", "×", HudTheme.Red, () => CancelMic?.Invoke(), 26f, 26f, 18f);
            HudFactory.Place((RectTransform)cancel.transform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-8f, -8f), new Vector2(26f, 26f));
        }

        public void Tick(float dt, HudModel m, MicLevelProbe probe)
        {
            time += dt;
            VoiceVM v = m.Voice;
            string mode = (m.Status.Mode ?? "").ToUpperInvariant();
            Color modeColor = mode == "LIVE" ? HudTheme.Green : mode == "MOCK" ? HudTheme.Amber : HudTheme.Red;
            modeChip.Set(mode == "" ? "—" : mode, modeColor);

            bool rec = v.Recording || v.Starting;
            float level = probe != null ? probe.Level : 0f;
            Color accent = rec ? HudTheme.Cyan : v.Finishing ? HudTheme.Blue : HudTheme.A(HudTheme.Cyan, .8f);
            float breath = .5f + .5f * Mathf.Sin(time * (rec ? 7f : 2f));
            HudFactory.SetColor(ring, HudTheme.A(accent, rec ? .7f + .3f * breath : .65f));
            HudFactory.SetColor(micDisk, HudTheme.A(accent, rec ? .22f + level * .4f : .1f));
            ringPulse.Radius = 30f + (rec ? 4f + level * 16f : 0f) + breath * (rec ? 2f : 0f);
            ringPulse.SetVerticesDirty();
            HudFactory.SetColor(ringPulse, HudTheme.A(accent, rec ? .45f * (1f - level * .3f) : 0f));
            Color iconColor = rec ? Color.white : HudTheme.TextHi;
            foreach (Image i in micIcon) HudFactory.SetColor(i, iconColor);
            HudFactory.SetColor(micCup, iconColor);
            HudFactory.SetColor(recDot, HudTheme.A(HudTheme.Red, rec ? .4f + .6f * breath : 0f));

            string status;
            if (!v.Present) status = "voice controller not found";
            else if (rec) status = "REC  " + v.Elapsed.ToString("F1") + " s  ·  click to send";
            else if (v.Finishing) status = "TRANSCRIBING…";
            else status = "click or press M";
            if (!string.IsNullOrEmpty(v.Failure)) status = v.Failure;
            HudFactory.Set(statusText, status);
            HudFactory.SetColor(statusText, !string.IsNullOrEmpty(v.Failure) ? HudTheme.Red : rec ? HudTheme.Cyan : HudTheme.TextMid);

            // onde : enveloppe réelle du micro, sinon ligne plate
            top.Clear();
            bottom.Clear();
            int bins = MicLevelProbe.Bins;
            for (int i = 0; i < bins; i++)
            {
                float e = probe != null && probe.HasSignal ? probe.Envelope[i] : 0f;
                float x = i / (float)(bins - 1);
                float taper = Mathf.Sin(x * Mathf.PI);
                top.Add(new Vector2(x, .5f + .48f * e * taper));
                bottom.Add(new Vector2(x, .5f - .48f * e * taper));
            }
            waveTop.SetPoints(top);
            waveBottom.SetPoints(bottom);
            Color waveColor = probe != null && probe.HasSignal ? HudTheme.Cyan : HudTheme.A(HudTheme.Cyan, .25f);
            HudFactory.SetColor(waveTop, waveColor);
            HudFactory.SetColor(waveBottom, HudTheme.A(waveColor, waveColor.a * .6f));

            // transcription progressive (machine à écrire sur le texte réel)
            string target = !string.IsNullOrEmpty(v.Live) && (rec || v.Finishing) ? v.Live
                          : !string.IsNullOrEmpty(v.Final) ? v.Final : v.Live;
            if (target != targetTranscript)
            {
                if (string.IsNullOrEmpty(target) || !target.StartsWith(targetTranscript ?? "")) shownChars = 0f;
                targetTranscript = target ?? "";
            }
            shownChars = Mathf.Min(targetTranscript.Length, shownChars + dt * 45f);
            bool isFinal = !string.IsNullOrEmpty(v.Final) && targetTranscript == v.Final;
            string shown = targetTranscript.Substring(0, Mathf.FloorToInt(shownChars));
            if (string.IsNullOrEmpty(targetTranscript))
            {
                HudFactory.Set(transcriptText, "<color=#" + HudTheme.Hex(HudTheme.TextLow) + "><i>“Urgent: two cartons A, one carton B”</i></color>");
            }
            else
            {
                bool typing = shownChars < targetTranscript.Length || rec;
                HudFactory.Set(transcriptText, (isFinal ? "“" : "") + shown + (isFinal && !typing ? "”" : "") +
                                                (typing && Mathf.Repeat(time, .8f) < .4f ? "<color=#" + HudTheme.Hex(HudTheme.Cyan) + ">|</color>" : ""));
            }
            HudFactory.SetColor(transcriptText, isFinal ? HudTheme.TextHi : HudTheme.TextMid);

            HudFactory.Set(asrText, v.AsrLatencyMs > 0 || !string.IsNullOrEmpty(v.AsrModel)
                ? (string.IsNullOrEmpty(v.AsrModel) ? "ASR" : v.AsrModel) + (v.AsrLatencyMs > 0 ? "  ·  " + v.AsrLatencyMs + " ms" : "")
                : "");
            bool showCancel = v.Busy;
            if (cancel.gameObject.activeSelf != showCancel) cancel.gameObject.SetActive(showCancel);
        }
    }
}
