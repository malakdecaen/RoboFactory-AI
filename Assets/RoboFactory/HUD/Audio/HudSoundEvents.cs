using System.Collections.Generic;

namespace RoboFactory.HUD
{
    /// <summary>
    /// Detects, at each model refresh, the REAL transitions that deserve a
    /// sound: microphone opened / closed, final transcription received, Meta request sent, order
    /// delivered, truck door moving, safety alert. Decisions and urgent
    /// stages are sonified directly by MuseHudRoot (HudDataSource events).
    /// </summary>
    public sealed class HudSoundEvents
    {
        private bool primed;
        private bool wasRecording;
        private string lastFinal = "";
        private string lastLink = "";
        private SafetyLevel lastSafety = SafetyLevel.Unknown;
        private readonly Dictionary<int, OrderCategory> orderState = new Dictionary<int, OrderCategory>();
        private readonly Dictionary<string, string> doorState = new Dictionary<string, string>();

        public void Check(HudModel m, HudAudio audio)
        {
            VoiceVM v = m.Voice;
            bool recording = v.Present && (v.Recording || v.Starting);
            string final = v.Final ?? "";
            string link = m.Status.Link ?? "";

            if (primed)
            {
                if (recording && !wasRecording) audio.Play(HudSound.MicOn);
                if (!recording && wasRecording) audio.Play(HudSound.MicOff);
                if (final != lastFinal && final.Length > 0) audio.Play(HudSound.Transcript);
                if (link == "CALLING" && lastLink != "CALLING") audio.Play(HudSound.RequestSent);
                if (m.Status.Safety == SafetyLevel.Alert && lastSafety != SafetyLevel.Alert) audio.Play(HudSound.SafetyAlert);

                foreach (OrderVM o in m.Orders)
                    if (o.Category == OrderCategory.Done && orderState.TryGetValue(o.Id, out OrderCategory before) && before != OrderCategory.Done)
                        audio.Play(HudSound.OrderDelivered);

                foreach (TruckVM t in m.Trucks)
                    if (doorState.TryGetValue(t.Id, out string before) && before != t.Door && (t.Door == "Opening" || t.Door == "Closing"))
                        audio.Play(HudSound.DoorMove);
            }

            wasRecording = recording;
            lastFinal = final;
            lastLink = link;
            lastSafety = m.Status.Safety;
            foreach (OrderVM o in m.Orders) orderState[o.Id] = o.Category;
            foreach (TruckVM t in m.Trucks) doorState[t.Id] = t.Door;
            primed = true; // no sound for the state already present at start
        }
    }
}
