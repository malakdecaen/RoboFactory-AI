using UnityEngine;

namespace RoboFactory.HUD
{
    public enum HudSound
    {
        Click, ModeSwitch, MicOn, MicOff, Transcript,
        RequestSent, DecisionAccepted, DecisionRejected,
        PriorityOverride, Resuming, UrgentFailed,
        OrderDelivered, DoorMove, SafetyAlert
    }

    /// <summary>
    /// Procedural synthesis of the HUD sounds (no audio asset to import): soft bells,
    /// beeps, filtered noise sweeps, two-tone alarm, light echo. Mono 44.1 kHz, clips
    /// generated only once at start.
    /// </summary>
    public static class HudSoundSynth
    {
        public const int Rate = 44100;

        public static AudioClip Create(HudSound s)
        {
            float[] b;
            switch (s)
            {
                case HudSound.Click:
                    b = Buf(.06f);
                    Noise(b, 0f, .006f, .25f, 6000f, 6000f, 1);
                    Tone(b, 0f, .05f, 2400f, 2200f, .18f, .001f, .045f, 0f);
                    break;
                case HudSound.ModeSwitch:
                    b = Buf(.45f);
                    Noise(b, 0f, .38f, .22f, 400f, 5000f, 3);
                    Tone(b, .05f, .3f, 520f, 880f, .1f, .03f, .25f, .2f);
                    break;
                case HudSound.MicOn:
                    b = Buf(.2f);
                    Tone(b, 0f, .07f, 660f, 990f, .3f, .004f, .06f, .1f);
                    Tone(b, .07f, .1f, 1320f, 1320f, .22f, .004f, .09f, .1f);
                    break;
                case HudSound.MicOff:
                    b = Buf(.2f);
                    Tone(b, 0f, .07f, 1320f, 1320f, .22f, .004f, .06f, .1f);
                    Tone(b, .07f, .1f, 990f, 660f, .28f, .004f, .09f, .1f);
                    break;
                case HudSound.Transcript:
                    b = Buf(.35f);
                    for (int i = 0; i < 4; i++) Tone(b, i * .05f, .05f, 1800f + i * 220f, 1800f + i * 220f, .12f, .002f, .045f, 0f);
                    Echo(b, .09f, .3f, .3f);
                    break;
                case HudSound.RequestSent:
                    b = Buf(.4f);
                    Bell(b, 0f, 1174.7f, .16f, .3f);
                    Echo(b, .11f, .35f, .35f);
                    break;
                case HudSound.DecisionAccepted:
                    b = Buf(.9f);
                    Bell(b, 0f, 659.3f, .32f, .5f);     // E5
                    Bell(b, .11f, 987.8f, .3f, .6f);    // B5
                    Bell(b, .22f, 1318.5f, .16f, .5f);  // E6
                    Echo(b, .13f, .3f, .35f);
                    break;
                case HudSound.DecisionRejected:
                    b = Buf(.6f);
                    Tone(b, 0f, .16f, 523.3f, 523.3f, .26f, .005f, .15f, .35f);
                    Tone(b, .15f, .26f, 392f, 370f, .28f, .005f, .25f, .35f);
                    break;
                case HudSound.PriorityOverride:
                    b = Buf(1.7f);
                    Noise(b, 0f, .5f, .32f, 2000f, 120f, 2);           // impact descendant
                    Tone(b, 0f, .45f, 110f, 55f, .45f, .002f, .44f, .1f);
                    for (int i = 0; i < 3; i++)
                    {
                        float t = .18f + i * .42f;
                        Tone(b, t, .19f, 740f, 740f, .26f, .01f, .17f, .6f);   // two-tone alarm
                        Tone(b, t + .2f, .19f, 988f, 988f, .26f, .01f, .17f, .6f);
                    }
                    Echo(b, .16f, .25f, .25f);
                    break;
                case HudSound.Resuming:
                    b = Buf(1.1f);
                    float[] notes = { 523.3f, 659.3f, 784f, 1046.5f };
                    for (int i = 0; i < notes.Length; i++) Bell(b, i * .1f, notes[i], .22f, .55f);
                    Echo(b, .14f, .3f, .3f);
                    break;
                case HudSound.UrgentFailed:
                    b = Buf(.7f);
                    Tone(b, 0f, .3f, 220f, 200f, .3f, .01f, .28f, .8f);
                    Tone(b, .32f, .32f, 185f, 165f, .3f, .01f, .3f, .8f);
                    break;
                case HudSound.OrderDelivered:
                    b = Buf(1f);
                    Bell(b, 0f, 784f, .24f, .6f);
                    Bell(b, .08f, 987.8f, .22f, .6f);
                    Bell(b, .16f, 1174.7f, .22f, .7f);
                    Echo(b, .15f, .3f, .3f);
                    break;
                case HudSound.DoorMove:
                    b = Buf(.5f);
                    Noise(b, 0f, .4f, .14f, 300f, 900f, 2);
                    Tone(b, 0f, .4f, 90f, 110f, .18f, .05f, .3f, .3f);
                    break;
                case HudSound.SafetyAlert:
                    b = Buf(.5f);
                    Tone(b, 0f, .12f, 1400f, 1400f, .26f, .003f, .11f, .5f);
                    Tone(b, .2f, .12f, 1400f, 1400f, .26f, .003f, .11f, .5f);
                    break;
                default:
                    b = Buf(.1f);
                    break;
            }
            Normalize(b, .8f);
            var clip = AudioClip.Create("HUD " + s, b.Length, 1, Rate, false);
            clip.SetData(b, 0);
            return clip;
        }

        // ------------------------------------------------------------------ primitives

        private static float[] Buf(float seconds) { return new float[Mathf.CeilToInt(seconds * Rate)]; }

        /// <summary>Tone with glissando, attack/exponential decay envelope, part triangle (grain).</summary>
        private static void Tone(float[] b, float start, float dur, float f0, float f1, float amp, float attack, float decay, float grit)
        {
            int s0 = Mathf.FloorToInt(start * Rate), n = Mathf.FloorToInt(dur * Rate);
            double phase = 0;
            for (int i = 0; i < n && s0 + i < b.Length; i++)
            {
                float t = i / (float)Rate;
                float k = n > 1 ? i / (float)(n - 1) : 0f;
                float f = Mathf.Lerp(f0, f1, k);
                phase += 2 * Mathf.PI * f / Rate;
                float sine = Mathf.Sin((float)phase);
                float tri = 2f / Mathf.PI * Mathf.Asin(sine);
                float env = (t < attack ? t / Mathf.Max(attack, 1e-4f) : Mathf.Exp(-(t - attack) / Mathf.Max(decay * .35f, 1e-4f)));
                env *= Mathf.Clamp01((n - i) / (Rate * .004f)); // click-free ending
                b[s0 + i] += amp * env * Mathf.Lerp(sine, tri, grit);
            }
        }

        /// <summary>Soft bell: fundamental + inharmonic partials with faster decay.</summary>
        private static void Bell(float[] b, float start, float f, float amp, float length)
        {
            Tone(b, start, length, f, f, amp, .003f, length, 0f);
            Tone(b, start, length * .6f, f * 2.01f, f * 2.01f, amp * .35f, .002f, length * .45f, 0f);
            Tone(b, start, length * .35f, f * 3.98f, f * 3.98f, amp * .12f, .001f, length * .25f, 0f);
        }

        /// <summary>Low-pass filtered noise with a swept cutoff (breath, impact, mechanical).</summary>
        private static void Noise(float[] b, float start, float dur, float amp, float cut0, float cut1, int seed)
        {
            var rng = new System.Random(seed);
            int s0 = Mathf.FloorToInt(start * Rate), n = Mathf.FloorToInt(dur * Rate);
            float lp = 0f, lp2 = 0f;
            for (int i = 0; i < n && s0 + i < b.Length; i++)
            {
                float k = i / (float)Mathf.Max(1, n - 1);
                float cut = Mathf.Lerp(cut0, cut1, k);
                float a = 1f - Mathf.Exp(-2f * Mathf.PI * cut / Rate);
                float w = (float)(rng.NextDouble() * 2 - 1);
                lp += a * (w - lp);
                lp2 += a * (lp - lp2);
                float env = Mathf.Sin(k * Mathf.PI) * (1f - k * .3f);
                b[s0 + i] += amp * lp2 * 3f * env;
            }
        }

        private static void Echo(float[] b, float delay, float feedback, float mix)
        {
            int d = Mathf.FloorToInt(delay * Rate);
            for (int i = d; i < b.Length; i++) b[i] += b[i - d] * feedback * mix * 2f;
        }

        private static void Normalize(float[] b, float peak)
        {
            float m = 1e-5f;
            foreach (float v in b) m = Mathf.Max(m, Mathf.Abs(v));
            float k = peak / m;
            for (int i = 0; i < b.Length; i++) b[i] *= k;
        }
    }
}
