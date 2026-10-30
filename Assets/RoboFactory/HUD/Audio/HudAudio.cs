using System.Collections.Generic;
using UnityEngine;

namespace RoboFactory.HUD
{
    /// <summary>
    /// HUD interface sounds: one 2D AudioSource, synthesised clips, per-sound volume,
    /// anti-repetition, remembered global mute (N key or SOUND button).
    /// Plays ONLY on real events forwarded by MuseHudRoot.
    /// </summary>
    public sealed class HudAudio
    {
        public static HudAudio Instance { get; private set; }

        public float MasterVolume = .55f;
        public bool Muted { get; private set; }

        private const string PrefKey = "RoboFactory.MuseHud.Muted";
        private AudioSource source;
        private readonly Dictionary<HudSound, AudioClip> clips = new Dictionary<HudSound, AudioClip>();
        private readonly Dictionary<HudSound, float> lastPlayed = new Dictionary<HudSound, float>();

        private static readonly Dictionary<HudSound, float> Volumes = new Dictionary<HudSound, float>
        {
            { HudSound.Click, .35f }, { HudSound.ModeSwitch, .45f }, { HudSound.MicOn, .6f }, { HudSound.MicOff, .55f },
            { HudSound.Transcript, .4f }, { HudSound.RequestSent, .3f }, { HudSound.DecisionAccepted, .7f },
            { HudSound.DecisionRejected, .6f }, { HudSound.PriorityOverride, .9f }, { HudSound.Resuming, .7f },
            { HudSound.UrgentFailed, .7f }, { HudSound.OrderDelivered, .65f }, { HudSound.DoorMove, .35f },
            { HudSound.SafetyAlert, .7f }
        };

        private static readonly Dictionary<HudSound, float> Cooldowns = new Dictionary<HudSound, float>
        {
            { HudSound.Click, .05f }, { HudSound.RequestSent, 2f }, { HudSound.DoorMove, .8f },
            { HudSound.SafetyAlert, 4f }, { HudSound.Transcript, 1f }
        };

        public void Attach(GameObject host)
        {
            source = host.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0f;
            source.loop = false;
            source.ignoreListenerPause = true;
            source.priority = 32;
            foreach (HudSound s in System.Enum.GetValues(typeof(HudSound)))
                clips[s] = HudSoundSynth.Create(s);
            Muted = PlayerPrefs.GetInt(PrefKey, 0) == 1;
            Instance = this;
        }

        public void Play(HudSound s, float volumeScale = 1f)
        {
            if (Muted || source == null || !clips.TryGetValue(s, out AudioClip clip)) return;
            float now = Time.unscaledTime;
            float cd = Cooldowns.TryGetValue(s, out float c) ? c : .15f;
            if (lastPlayed.TryGetValue(s, out float last) && now - last < cd) return;
            lastPlayed[s] = now;
            float v = Volumes.TryGetValue(s, out float vol) ? vol : .5f;
            source.PlayOneShot(clip, Mathf.Clamp01(v * MasterVolume * volumeScale));
        }

        public void SetMuted(bool muted)
        {
            Muted = muted;
            PlayerPrefs.SetInt(PrefKey, muted ? 1 : 0);
            if (muted && source != null) source.Stop();
        }

        public void Dispose()
        {
            if (Instance == this) Instance = null;
            foreach (AudioClip c in clips.Values) if (c != null) Object.Destroy(c);
            clips.Clear();
        }
    }
}
