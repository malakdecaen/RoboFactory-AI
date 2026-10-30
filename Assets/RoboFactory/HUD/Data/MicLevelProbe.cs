using System.Reflection;
using RoboFactory.Logistics;
using UnityEngine;

namespace RoboFactory.HUD
{
    /// <summary>
    /// Lit, en lecture seule, le vrai signal du micro capturé par VoiceCommandController
    /// (son AudioClip en boucle) pour dessiner l'onde et faire pulser le cercle.
    /// Aucune donnée simulée : sans clip (mode mock, pas de micro), le niveau reste à 0.
    /// </summary>
    public sealed class MicLevelProbe
    {
        public const int Bins = 48;
        public readonly float[] Envelope = new float[Bins];
        public float Level { get; private set; }
        public bool HasSignal { get; private set; }

        private static readonly FieldInfo ClipField =
            typeof(VoiceCommandController).GetField("clip", BindingFlags.Instance | BindingFlags.NonPublic);
        private readonly float[] buffer = new float[2048];

        public void Sample(VoiceCommandController voice, float dt)
        {
            AudioClip clip = null;
            if (voice != null && voice.IsRecording && ClipField != null)
                clip = ClipField.GetValue(voice) as AudioClip;

            if (clip == null || !Microphone.IsRecording(null))
            {
                HasSignal = false;
                Level = Mathf.MoveTowards(Level, 0f, dt * 3f);
                for (int i = 0; i < Bins; i++) Envelope[i] = Mathf.MoveTowards(Envelope[i], 0f, dt * 3f);
                return;
            }

            int pos = Microphone.GetPosition(null);
            int samples = clip.samples;
            if (samples <= buffer.Length || pos < 0) { HasSignal = false; return; }
            int start = pos - buffer.Length;
            if (start < 0) start += samples;
            clip.GetData(buffer, start); // lecture circulaire : AudioClip.GetData reboucle sur un clip en boucle

            int per = buffer.Length / Bins;
            float total = 0f;
            for (int b = 0; b < Bins; b++)
            {
                float sum = 0f;
                for (int i = 0; i < per; i++)
                {
                    float v = buffer[b * per + i];
                    sum += v * v;
                }
                float rms = Mathf.Sqrt(sum / per);
                float shaped = Mathf.Clamp01(rms * 9f);
                Envelope[b] = Mathf.Lerp(Envelope[b], shaped, .6f);
                total += rms;
            }
            HasSignal = true;
            float level = Mathf.Clamp01(total / Bins * 10f);
            Level = Mathf.Lerp(Level, level, 1f - Mathf.Exp(-14f * dt));
        }
    }
}
