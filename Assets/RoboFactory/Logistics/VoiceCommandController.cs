using System.Collections;
using System.Collections.Generic;
using RoboFactory.Hackathon.Meta;
using UnityEngine;

namespace RoboFactory.Logistics
{
    /// <summary>
    /// Commandes urgentes vocales : push-to-talk sur le micro, conversion en
    /// PCM16 mono 24 kHz, envoi en chunks au proxy Python (/asr/*), transcription
    /// partielle affichée en direct, transcription FINALE envoyée à Muse Wonder
    /// via SubmitUrgentTranscript. La clé Meta ne quitte jamais le proxy —
    /// Unity ne voit que du PCM et du texte.
    ///
    /// Flux audio : le proxy ré-émet l'audio vers Meta par trames de 80 ms cadencées
    /// en temps réel ; une trame incomplète y est complétée par du silence et un
    /// tampon vide y produit une trame de silence. L'envoi Unity est donc :
    ///  - groupé (tout ce qui attend part dans UNE requête : une requête HTTP lente
    ///    ne fait plus prendre de retard au flux) ;
    ///  - aligné sur 80 ms pendant la capture (aucun trou de silence inséré au
    ///    milieu des mots) ;
    ///  - précédé d'un pré-tampon (~400 ms d'avance côté proxy : les à-coups de
    ///    cadence ne vident plus son tampon en pleine phrase).
    /// Si la session temps réel échoue alors que le micro a bien capté de la voix,
    /// l'enregistrement complet est retranscrit une fois dans une session neuve.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VoiceCommandController : MonoBehaviour
    {
        public enum VoiceState { Idle, Starting, Recording, Finishing }

        [Header("Références")]
        public MuseOrchestrator Orchestrator;
        [Tooltip("Vide = reprendre le ProxyUrl de l'orchestrateur.")]
        public string ProxyUrl = "";

        [Header("Capture")]
        [Tooltip("Durée max d'une prise de parole avant envoi automatique (s).")]
        public float MaxRecordSeconds = 20f;
        [Tooltip("Intervalle de lecture du tampon micro (ms).")]
        public int PollMs = 200;
        [Tooltip("Avance d'audio constituée côté proxy avant le premier envoi (ms).")]
        public int PrebufferMs = 400;
        [Tooltip("Échec de la session temps réel avec de la voix captée : retranscrire l'enregistrement complet une fois.")]
        public bool RetryWholeCapture = true;
        [Tooltip("Niveau crête (0-1) sous lequel la capture est considérée muette.")]
        public float SilencePeak = .004f;
        [Tooltip("Écrit la dernière capture dans Logs/voice_last.wav (vérification à l'écoute).")]
        public bool SaveLastCapture = true;
        [Tooltip("Vocabulaire entrepôt transmis à Meta (biais de reconnaissance).")]
        public string[] Keywords =
        {
            "palette", "banc", "camion", "carton", "commande urgente",
            "caisse", "urgent", "livrer", "charger"
        };
        public string[] Languages = { "French", "English" };
        [Tooltip("Mode mock : texte renvoyé comme transcription (test sans micro ni API).")]
        public string MockTranscript = "commande urgente, deux cartons A et un carton B";

        public VoiceState State { get; private set; } = VoiceState.Idle;
        public bool Busy => State != VoiceState.Idle;
        public bool IsRecording => State == VoiceState.Recording;
        /// <summary>Transcription partielle cumulative la plus récente.</summary>
        public string LiveTranscript { get; private set; } = "";
        /// <summary>Dernière transcription finale envoyée à Muse.</summary>
        public string FinalTranscript { get; private set; } = "";
        public string StatusText { get; private set; } = "micro prêt";
        public int LastAsrLatencyMs { get; private set; }
        public string LastAsrModel { get; private set; } = "";
        /// <summary>Mode ASR réel de la dernière session ("live" | "mock").</summary>
        public string LastAsrMode { get; private set; } = "";
        /// <summary>Vrai uniquement quand Meta a renvoyé une transcription FINALE soumise à Muse.</summary>
        public bool LastSubmittedFinal { get; private set; }
        /// <summary>ms d'audio acceptés par Meta — preuve de transport, PAS de transcription.</summary>
        public int AudioProcessedMs { get; private set; }
        /// <summary>Dernier échec ASR lisible (jamais masqué au HUD).</summary>
        public string LastFailure { get; private set; } = "";
        /// <summary>Niveau crête (0-1) de la dernière capture : diagnostic micro muet.</summary>
        public float LastCapturePeak { get; private set; }
        /// <summary>Durée (s) de la dernière capture envoyée.</summary>
        public float LastCaptureSeconds { get; private set; }
        public float RecordingElapsed => State == VoiceState.Recording ? Time.unscaledTime - recordStart : 0f;

        private MetaProxyClient proxy;
        private AudioClip clip;
        private int micRate = 48000;
        private int micChannels = 1;
        private int lastMicPos;
        private float recordStart;
        private string sessionId;
        private int sessionSeq;
        private int generation;
        private bool mockSession;
        private bool sessionReady;   // /asr/start a répondu OK : on peut envoyer l'audio
        private bool sessionFailed;  // /asr/start a échoué : capture abandonnée
        private readonly Queue<byte[]> outbox = new Queue<byte[]>();
        private int consecutiveSendFails;

        // ---- envoi groupé : tampon d'envoi, requête en vol, pré-tampon atteint
        private readonly List<byte> sendBuf = new List<byte>();
        private bool sendInFlight;
        private bool primed;
        /// <summary>Copie intégrale de la capture (PCM 24 kHz) : diagnostic + nouvel essai.</summary>
        private readonly List<byte> captured = new List<byte>();
        private float capturePeak;

        // ---- resampleur : tampon continu + curseur fractionnaire
        private readonly List<float> resIn = new List<float>();
        private double resCursor;
        private const int TargetRate = 24000;
        /// <summary>Trame du cadenceur du proxy (ASR_FRAME_MS = 80 ms en s16le 24 kHz).</summary>
        private const int FrameBytes = TargetRate * 2 * 80 / 1000;
        /// <summary>Plafond d'une requête (2 s, multiple de la trame).</summary>
        private const int MaxSendBytes = FrameBytes * 25;

        private void Start()
        {
            if (Orchestrator == null) Orchestrator = FindFirstObjectByType<MuseOrchestrator>();
            string url = !string.IsNullOrEmpty(ProxyUrl) ? ProxyUrl
                : Orchestrator != null ? Orchestrator.ProxyUrl : "http://127.0.0.1:8765";
            proxy = new MetaProxyClient { BaseUrl = url };
        }

        private void Update()
        {
            if (State != VoiceState.Recording || mockSession) return;
            if (clip != null && Microphone.IsRecording(null))
            {
                if (Time.unscaledTime - recordStart > MaxRecordSeconds)
                {
                    StatusText = "durée max atteinte — envoi";
                    EndTalk();
                }
            }
            else if (clip != null && Time.unscaledTime - recordStart > 1f)
            {
                StatusText = "micro arrêté — envoi de ce qui a été capté";
                EndTalk();
            }
        }

        // ------------------------------------------------------------------ API bouton

        /// <summary>Bascule appuyé/relâché (bouton MICRO du HUD).</summary>
        public void Toggle()
        {
            if (State == VoiceState.Idle) BeginTalk();
            else if (State == VoiceState.Recording) EndTalk();
        }

        /// <summary>Push-to-talk : appui.</summary>
        public void BeginTalk()
        {
            if (State != VoiceState.Idle) return;
            if (Orchestrator != null && Orchestrator.Mode == "offline")
            {
                StatusText = "mode offline : ASR indisponible";
                return;
            }
            bool mock = Orchestrator != null && Orchestrator.Mode == "mock";
            if (!mock && (Microphone.devices == null || Microphone.devices.Length == 0))
            {
                StatusText = "aucun micro détecté (mode mock disponible)";
                return;
            }
            generation++;
            sessionReady = false;
            sessionFailed = false;
            sessionId = "asr-" + (++sessionSeq) + "-" + Time.frameCount;
            outbox.Clear();
            sendBuf.Clear();
            sendInFlight = false;
            primed = false;
            captured.Clear();
            capturePeak = 0f;
            resIn.Clear();
            resCursor = 0;
            consecutiveSendFails = 0;
            mockSession = mock;
            LiveTranscript = "";
            LastSubmittedFinal = false;
            LastFailure = "";
            AudioProcessedMs = 0;
            LastAsrMode = mock ? "mock" : "live";

            // Le micro démarre TOUT DE SUITE : on peut parler pendant que la session
            // Meta s'ouvre (parfois plusieurs secondes à froid). L'audio est mis en
            // tampon dans l'outbox puis envoyé dès que la session est prête.
            if (!mock)
            {
                clip = Microphone.Start(null, true, Mathf.CeilToInt(MaxRecordSeconds) + 2, TargetRate);
                if (clip == null)
                {
                    StatusText = "micro refusé par le système";
                    LastFailure = StatusText;
                    return;
                }
                micRate = clip.frequency > 0 ? clip.frequency : TargetRate;
                micChannels = Mathf.Max(1, clip.channels);
                lastMicPos = 0;
            }
            recordStart = Time.unscaledTime;
            State = VoiceState.Recording;
            StatusText = mock ? "MOCK — relâchez pour envoyer : «" + MockTranscript + "»"
                              : "● REC — parlez (connexion Meta en cours…)";
            int gen = generation;
            StartCoroutine(StartSession(gen));
            StartCoroutine(mock ? MockPump(gen) : MicPump(gen));
            StartCoroutine(Sender(gen));
        }

        /// <summary>Push-to-talk : relâché -> envoi de la transcription finale.</summary>
        public void EndTalk()
        {
            if (State != VoiceState.Recording) return;
            State = VoiceState.Finishing;
            StatusText = "fin de capture…";
            if (!mockSession)
            {
                Harvest();
                LastCaptureSeconds = captured.Count / (TargetRate * 2f);
                LastCapturePeak = capturePeak;
                string device = Microphone.devices != null && Microphone.devices.Length > 0
                    ? Microphone.devices[0] : "?";
                Debug.Log("[Voice] capture " + LastCaptureSeconds.ToString("F1") + " s · crête " +
                          PeakDb(capturePeak) + " dBFS · micro par défaut (1er : «" + device + "») " +
                          micRate + " Hz × " + micChannels + " canal(aux)");
                if (SaveLastCapture) SaveWav(captured);
            }
            StopMic();
            StartCoroutine(FinishSession(generation));
        }

        /// <summary>Annule la capture sans envoyer quoi que ce soit à Muse.</summary>
        public void CancelTalk()
        {
            if (State == VoiceState.Idle) return;
            generation++;
            StopMic();
            outbox.Clear();
            sendBuf.Clear();
            sendInFlight = false;
            resIn.Clear();
            resCursor = 0;
            State = VoiceState.Idle;
            StatusText = "capture annulée";
            if (!string.IsNullOrEmpty(sessionId))
                StartCoroutine(proxy.AsrCancel(sessionId, _ => { }));
            sessionId = null;
        }

        // ------------------------------------------------------------------ session ASR

        private IEnumerator StartSession(int gen)
        {
            AsrStartResponse r = null;
            yield return proxy.AsrStart(sessionId, Keywords, Languages,
                mockSession ? "mock" : "live",
                mockSession ? MockTranscript : null, x => r = x);
            if (gen != generation || State == VoiceState.Idle) yield break;
            if (r == null || !r.ok)
            {
                sessionFailed = true;
                StopMic();
                outbox.Clear();
                sendBuf.Clear();
                StatusText = "ASR indisponible : " + (r != null ? r.error : "proxy muet");
                LastFailure = StatusText;
                State = VoiceState.Idle;
                yield break;
            }
            LastAsrModel = r.model;
            if (!string.IsNullOrEmpty(r.mode)) LastAsrMode = r.mode; // vérité proxy
            LastAsrLatencyMs = r.latency_ms;
            sessionReady = true;
            if (State == VoiceState.Recording && !mockSession)
                StatusText = "● REC — parlez (français / anglais)";
        }

        /// <summary>Prélève régulièrement l'audio du tampon micro en l'envoyant à l'outbox.</summary>
        private IEnumerator MicPump(int gen)
        {
            // temps réel : indépendant d'un éventuel Time.timeScale
            var wait = new WaitForSecondsRealtime(Mathf.Max(50, PollMs) / 1000f);
            while (gen == generation && State == VoiceState.Recording)
            {
                Harvest();
                yield return wait;
            }
        }

        /// <summary>Mode mock : petits chunks de silence pour animer la transcription.</summary>
        private IEnumerator MockPump(int gen)
        {
            var wait = new WaitForSecondsRealtime(.3f);
            var silence = new byte[TargetRate * 2 / 10]; // 100 ms de silence PCM16
            while (gen == generation && State == VoiceState.Recording)
            {
                if (outbox.Count < 4) outbox.Enqueue(silence);
                yield return wait;
            }
        }

        /// <summary>Octets captés pas encore remis au proxy (outbox + tampon d'envoi + requête en vol).</summary>
        private int PendingBytes()
        {
            int n = sendBuf.Count;
            foreach (byte[] b in outbox) n += b.Length;
            return n;
        }

        /// <summary>
        /// Envoie l'audio en attente, groupé en une requête ; la réponse porte le
        /// partial live. Pendant la capture on n'envoie que des multiples de 80 ms
        /// (pas de trame complétée par du silence au milieu d'un mot) ; le reste
        /// part à la fin de la prise.
        /// </summary>
        private IEnumerator Sender(int gen)
        {
            int prebufferBytes = TargetRate * 2 * Mathf.Max(0, PrebufferMs) / 1000;
            while (gen == generation && (State == VoiceState.Recording || State == VoiceState.Finishing))
            {
                if (!sessionReady) { yield return null; continue; }
                while (outbox.Count > 0) sendBuf.AddRange(outbox.Dequeue());
                bool finishing = State == VoiceState.Finishing;
                int n;
                if (mockSession || finishing) n = sendBuf.Count;
                else
                {
                    if (!primed && sendBuf.Count < prebufferBytes) { yield return null; continue; }
                    n = sendBuf.Count - sendBuf.Count % FrameBytes;
                }
                if (n <= 0) { yield return null; continue; }
                n = Mathf.Min(n, MaxSendBytes);
                primed = true;
                byte[] pcm = sendBuf.GetRange(0, n).ToArray();
                sendBuf.RemoveRange(0, n);

                sendInFlight = true;
                AsrAudioResponse r = null;
                yield return proxy.AsrAudio(sessionId, pcm, x => r = x);
                sendInFlight = false;
                if (gen != generation) yield break;
                if (r == null || !r.ok)
                {
                    if (++consecutiveSendFails >= 3 && State == VoiceState.Recording)
                    {
                        StatusText = "flux ASR perdu : " + (r != null ? r.error : "proxy muet");
                        outbox.Clear();
                        sendBuf.Clear();
                        EndTalk();
                        yield break;
                    }
                    continue;
                }
                consecutiveSendFails = 0;
                if (r.latency_ms > LastAsrLatencyMs) LastAsrLatencyMs = r.latency_ms;
                if (r.audio_ms > AudioProcessedMs) AudioProcessedMs = r.audio_ms;
                if (!string.IsNullOrEmpty(r.partial)) LiveTranscript = r.partial;
                if (!string.IsNullOrEmpty(r.final)) LiveTranscript = r.final;
            }
        }

        /// <summary>Vide l'outbox puis demande la transcription finale au proxy.</summary>
        private IEnumerator FinishSession(int gen)
        {
            // Relâché avant que la session Meta soit prête : attendre son ouverture
            // (l'audio capté est en tampon), sinon abandon propre.
            float openDeadline = Time.unscaledTime + 30f;
            while (!sessionReady && !sessionFailed && gen == generation && Time.unscaledTime < openDeadline)
                yield return null;
            if (gen != generation || sessionFailed) yield break;
            if (!sessionReady)
            {
                StatusText = "session ASR non ouverte après 30 s — rien envoyé à Muse";
                LastFailure = StatusText;
                State = VoiceState.Idle;
                outbox.Clear();
                sendBuf.Clear();
                if (!string.IsNullOrEmpty(sessionId)) StartCoroutine(proxy.AsrCancel(sessionId, _ => { }));
                sessionId = null;
                yield break;
            }
            float deadline = Time.unscaledTime + 10f + PendingBytes() / (TargetRate * 2f) * 1.5f;
            while ((PendingBytes() > 0 || sendInFlight) && Time.unscaledTime < deadline && gen == generation)
                yield return null;
            if (gen != generation) yield break;

            AsrFinishResponse r = null;
            yield return proxy.AsrFinish(sessionId, x => r = x);
            sessionId = null;
            if (gen != generation) yield break;

            string text = r != null && r.ok ? (r.transcript ?? "").Trim() : "";
            bool usable = r != null && r.ok && r.final && !r.timed_out && text.Length > 0;
            bool voiceCaptured = mockSession || capturePeak >= SilencePeak;

            // Session temps réel ratée alors que la voix a bien été captée : on
            // retranscrit l'enregistrement complet d'un seul bloc (le proxy le
            // ré-émet en temps réel, sans trou), une seule fois.
            if (!usable && !mockSession && RetryWholeCapture && voiceCaptured && captured.Count > FrameBytes)
            {
                string firstError = r == null ? "proxy muet"
                    : !string.IsNullOrEmpty(r.error) ? r.error : "aucune parole reconnue";
                Debug.LogWarning("[Voice] session temps réel sans transcription (" + firstError +
                                 ") : nouvel essai sur l'enregistrement complet");
                StatusText = "2e essai : retranscription de l'enregistrement complet…";
                AsrFinishResponse r2 = null;
                yield return TranscribeWhole(gen, x => r2 = x);
                if (gen != generation) yield break;
                if (r2 != null)
                {
                    r = r2;
                    text = r.ok ? (r.transcript ?? "").Trim() : "";
                    usable = r.ok && r.final && !r.timed_out && text.Length > 0;
                }
            }
            State = VoiceState.Idle;

            if (!usable)
            {
                FinalTranscript = "";
                LastSubmittedFinal = false;
                StatusText = !voiceCaptured
                    ? "micro silencieux (crête " + PeakDb(capturePeak) +
                      " dBFS) — vérifiez le micro d'entrée par défaut de Windows"
                    : text.Length > 0
                        ? "transcription incomplète — NON envoyée à Muse (reformulez)"
                        : "aucune transcription : " + (r != null && !string.IsNullOrEmpty(r.error)
                            ? r.error : "aucune parole reconnue");
                LastFailure = StatusText;
                yield break;
            }
            if (r.audio_ms > AudioProcessedMs) AudioProcessedMs = r.audio_ms;
            if (r.latency_ms > LastAsrLatencyMs) LastAsrLatencyMs = r.latency_ms;
            FinalTranscript = text;
            LiveTranscript = text;
            LastSubmittedFinal = true;
            StatusText = "→ Muse Wonder : " + text;
            if (Orchestrator != null) Orchestrator.SubmitUrgentTranscript(text);
        }

        /// <summary>Nouvelle session ASR alimentée d'un bloc avec toute la capture.</summary>
        private IEnumerator TranscribeWhole(int gen, System.Action<AsrFinishResponse> done)
        {
            string retryId = "asr-" + (++sessionSeq) + "-retry-" + Time.frameCount;
            AsrStartResponse s = null;
            yield return proxy.AsrStart(retryId, Keywords, Languages, "live", null, x => s = x);
            if (gen != generation) yield break;
            if (s == null || !s.ok) { done(null); yield break; }
            if (!string.IsNullOrEmpty(s.session_id)) retryId = s.session_id;
            byte[] all = captured.ToArray();
            for (int off = 0; off < all.Length && gen == generation; off += MaxSendBytes * 4)
            {
                int len = Mathf.Min(MaxSendBytes * 4, all.Length - off);
                var part = new byte[len];
                System.Array.Copy(all, off, part, 0, len);
                AsrAudioResponse a = null;
                yield return proxy.AsrAudio(retryId, part, x => a = x);
                if (a != null && a.ok && !string.IsNullOrEmpty(a.partial)) LiveTranscript = a.partial;
            }
            if (gen != generation) yield break;
            AsrFinishResponse f = null;
            yield return proxy.AsrFinish(retryId, x => f = x);
            done(f);
        }

        // ------------------------------------------------------------------ audio

        /// <summary>Lit les nouveaux échantillons micro, les passe en PCM16 mono 24 kHz.</summary>
        private void Harvest()
        {
            if (clip == null) return;
            int pos = Microphone.GetPosition(null);
            if (pos < 0) return;
            int total = pos >= lastMicPos ? pos - lastMicPos
                                          : clip.samples - lastMicPos + pos;
            if (total <= 0) return;

            float[] samples;
            if (pos >= lastMicPos)
            {
                samples = new float[total * micChannels];
                clip.GetData(samples, lastMicPos);
            }
            else // boucle circulaire : segment fin + segment début
            {
                var head = new float[(clip.samples - lastMicPos) * micChannels];
                var tail = new float[pos * micChannels];
                clip.GetData(head, lastMicPos);
                clip.GetData(tail, 0);
                samples = new float[head.Length + tail.Length];
                System.Array.Copy(head, samples, head.Length);
                System.Array.Copy(tail, 0, samples, head.Length, tail.Length);
            }
            lastMicPos = pos;

            // mono
            if (micChannels > 1)
            {
                var mono = new float[total];
                for (int i = 0; i < total; i++)
                {
                    float s = 0f;
                    for (int c = 0; c < micChannels; c++) s += samples[i * micChannels + c];
                    mono[i] = s / micChannels;
                }
                samples = mono;
            }
            foreach (float f in samples)
            {
                float a = f < 0f ? -f : f;
                if (a > capturePeak) capturePeak = a;
            }

            byte[] pcm = ToPcm24k(samples);
            if (pcm.Length > 0)
            {
                outbox.Enqueue(pcm);
                captured.AddRange(pcm);
            }
        }

        /// <summary>Rééchantillonnage linéaire continu vers 24 kHz, sortie s16le.</summary>
        private byte[] ToPcm24k(float[] mono)
        {
            foreach (float f in mono) resIn.Add(f);
            double step = micRate / (double)TargetRate;
            var shorts = new List<short>(mono.Length);
            // Interpolation linéaire : il faut l'échantillon suivant, on garde le reste.
            while (resCursor + 1 < resIn.Count)
            {
                int i = (int)resCursor;
                float frac = (float)(resCursor - i);
                float s = resIn[i] + (resIn[i + 1] - resIn[i]) * frac;
                shorts.Add((short)Mathf.Clamp(Mathf.RoundToInt(s * 32767f), -32768, 32767));
                resCursor += step;
            }
            int consumed = (int)resCursor;
            if (consumed > 0)
            {
                resIn.RemoveRange(0, consumed);
                resCursor -= consumed;
            }
            var bytes = new byte[shorts.Count * 2];
            for (int i = 0; i < shorts.Count; i++)
            {
                bytes[i * 2] = (byte)(shorts[i] & 0xFF);
                bytes[i * 2 + 1] = (byte)((shorts[i] >> 8) & 0xFF);
            }
            return bytes;
        }

        private void StopMic()
        {
            if (Microphone.IsRecording(null)) Microphone.End(null);
            clip = null;
        }

        private static string PeakDb(float peak)
        {
            return peak <= 0f ? "-inf" : (20f * Mathf.Log10(peak)).ToString("F0");
        }

        /// <summary>Dernière capture en WAV (24 kHz mono 16 bits) dans le dossier Logs du projet.</summary>
        private static void SaveWav(List<byte> pcm)
        {
            try
            {
                string dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath) ?? ".", "Logs");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, "voice_last.wav");
                using (var w = new System.IO.BinaryWriter(System.IO.File.Create(path)))
                {
                    int data = pcm.Count;
                    w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                    w.Write(36 + data);
                    w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                    w.Write(16);
                    w.Write((short)1);
                    w.Write((short)1);
                    w.Write(TargetRate);
                    w.Write(TargetRate * 2);
                    w.Write((short)2);
                    w.Write((short)16);
                    w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                    w.Write(data);
                    w.Write(pcm.ToArray());
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[Voice] voice_last.wav non écrit : " + e.Message);
            }
        }
    }
}
