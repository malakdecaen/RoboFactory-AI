using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace RoboFactory.Hackathon.Meta
{
    public enum PerceptionMode { Live, Mock, Offline }

    [Serializable]
    public sealed class DetectionDto
    {
        public string label;
        public float[] box;
        public float confidence;
        public string track_id;
    }

    [Serializable]
    public sealed class VisionResponse
    {
        public bool ok;
        public string mode;
        public string model;
        public int latency_ms;
        public string error;
        public string raw_text;
        public string observation_id;
        public DetectionDto[] detections;
    }

    [Serializable]
    public sealed class OrderCounts
    {
        public int heart;
        public int diamond;
        public int club;
        public int spade;

        public int Total => heart + diamond + club + spade;

        public int[] ToArray()
        {
            return new[] { heart, diamond, club, spade };
        }
    }

    [Serializable]
    public sealed class OrderResponse
    {
        public bool ok;
        public string mode;
        public string model;
        public int latency_ms;
        public string error;
        public string raw_text;
        public OrderCounts order;
    }

    [Serializable]
    public sealed class HealthResponse
    {
        public bool ok;
        public bool key_present;
        public string mode;
        public string base_url;
        public string vision_model;
        public string sam_model;
        public string orchestrator_model;
        public string orchestrator_fallback;
    }

    [Serializable]
    internal sealed class VisionRequest
    {
        public string image_b64;
        public string prompt;
        public string observation_id;
    }

    [Serializable]
    internal sealed class OrderRequest
    {
        public string text;
    }

    [Serializable]
    public sealed class SamResponse
    {
        public bool ok;
        public string mode;
        public string model;
        public int latency_ms;
        public string error;
        public string raw;
        public int number_count;
        public string observation_id;
    }

    [Serializable]
    internal sealed class SamRequest
    {
        public string image_b64;
        public string concept;
        public string observation_id;
        public string mode;
    }

    [Serializable]
    public sealed class SupervisionRequest
    {
        public string request_id;
        public int state_version;
        public string mode;
        public string trigger;
        public string state_json;
        public string mock_action;
        public int mock_state_version = -1;
        public int mock_order_id;
        public int mock_priority;
        public string mock_observation_id;
    }

    [Serializable]
    public sealed class SupervisionDecision
    {
        public string action;
        public int order_id;
        public int priority;
        public string observation_id;
        public string reason;
    }

    [Serializable]
    public sealed class SupervisionResponse
    {
        public bool ok;
        public string mode;
        public string model;
        public int latency_ms;
        public string error;
        public string raw_text;
        public string request_id;
        public int state_version;
        public SupervisionDecision decision;
    }

    /// <summary>Urgent orchestration request: voice transcription -> interpretation + plan.</summary>
    [Serializable]
    public sealed class UrgentRequest
    {
        public string request_id;
        public int state_version;
        public string mode;             // live | mock | offline
        public string transcript;
        public int urgent_order_id;     // local id already assigned to the urgent order
        public string state_json;
    }

    [Serializable]
    public sealed class UrgentOrderCounts
    {
        public int A;
        public int B;
        public int C;
        public int Total => A + B + C;
    }

    [Serializable]
    public sealed class UrgentResult
    {
        public string action;           // execute_order | clarify | wait | no_plan
        public bool urgent;
        public UrgentOrderCounts order;
        public float confidence;
        public string clarification;
        public string reason;
        public Orchestration.MusePlan plan;
    }

    [Serializable]
    public sealed class UrgentResponse
    {
        public bool ok;
        public string mode;
        public string model;
        public int latency_ms;
        public string error;
        public string raw_text;
        public string request_id;
        public int state_version;
        public string note;
        public UrgentResult result;
    }

    // ------------------------------------------------------------------ real-time ASR

    [Serializable]
    internal sealed class AsrStartRequest
    {
        public string session_id;
        public string mode;             // live | mock
        public string[] keywords;
        public string[] languages;
        public string mock_text;        // transcription injected in mock mode
    }

    [Serializable]
    public sealed class AsrStartResponse
    {
        public bool ok;
        public string mode;
        public string model;
        public string session_id;
        public string error;
        public int latency_ms;
    }

    [Serializable]
    internal sealed class AsrAudioRequest
    {
        public string session_id;
        public string pcm_b64;          // PCM s16le mono 24 kHz
    }

    [Serializable]
    public sealed class AsrAudioResponse
    {
        public bool ok;
        public string session_id;
        public string partial;          // most recent CUMULATIVE hypothesis
        public string final;
        public int audio_ms;
        public int queued_ms;
        public string error;
        public bool done;
        public bool send_done;
        public int bytes;
        public int latency_ms;
    }

    [Serializable]
    internal sealed class AsrSessionIdRequest
    {
        public string session_id;
    }

    [Serializable]
    public sealed class AsrFinishResponse
    {
        public bool ok;
        public string session_id;
        public string transcript;       // final transcription (or last partial)
        public bool final;
        public int audio_ms;
        public bool timed_out;
        public bool session_closed;
        public string error;
        public int latency_ms;
    }

    /// <summary>Warehouse orchestration request for Muse Wonder (/warehouse route of the gateway).</summary>
    [Serializable]
    public sealed class WarehouseRequest
    {
        public string request_id;
        public int state_version;
        public string mode;             // live | mock | offline
        public string trigger;
        public string state_json;
        public string mock_plan_error;  // test: forces an invalid plan in mock mode
    }

    [Serializable]
    public sealed class WarehouseResponse
    {
        public bool ok;
        public string mode;
        public string model;            // Meta model actually used (muse-wonder-1.0 or fallback)
        public int latency_ms;
        public string error;
        public string raw_text;
        public string request_id;
        public int state_version;
        public string note;             // ex. "orchestrator model unavailable, fell back to ..."
        public Orchestration.MusePlan plan;
    }

    public sealed class MetaProxyClient
    {
        public string BaseUrl = "http://127.0.0.1:8765";
        public int VisionTimeoutSeconds = 120;
        public int OrderTimeoutSeconds = 60;
        public int SupervisionTimeoutSeconds = 120;

        public IEnumerator Health(Action<HealthResponse> done)
        {
            using (var request = UnityWebRequest.Get(BaseUrl + "/health"))
            {
                request.timeout = 10;
                yield return request.SendWebRequest();
                done?.Invoke(request.result == UnityWebRequest.Result.Success
                    ? Parse<HealthResponse>(request.downloadHandler.text)
                    : null);
            }
        }

        public IEnumerator AskVision(string imageBase64, string prompt, Action<VisionResponse> done)
        {
            yield return AskVision(imageBase64, prompt, "", done);
        }

        public IEnumerator AskVision(string imageBase64, string prompt, string observationId, Action<VisionResponse> done)
        {
            var body = new VisionRequest { image_b64 = imageBase64, prompt = prompt, observation_id = observationId };
            yield return Post("/vision", JsonUtility.ToJson(body), VisionTimeoutSeconds, done);
        }

        public IEnumerator AskSam(string imageBase64, string concept, string observationId, Action<SamResponse> done)
        {
            yield return AskSam(imageBase64, concept, observationId, "live", done);
        }

        public IEnumerator AskSam(string imageBase64, string concept, string observationId, string mode, Action<SamResponse> done)
        {
            var body = new SamRequest { image_b64 = imageBase64, concept = concept, observation_id = observationId, mode = mode };
            yield return Post("/sam", JsonUtility.ToJson(body), 150, done);
        }

        public IEnumerator Supervise(SupervisionRequest request, Action<SupervisionResponse> done)
        {
            yield return Post("/supervise", JsonUtility.ToJson(request), SupervisionTimeoutSeconds, done);
        }

        /// <summary>Full Muse Wonder logistics plan (async, never blocking).</summary>
        public IEnumerator Orchestrate(WarehouseRequest request, Action<WarehouseResponse> done)
        {
            yield return Post("/warehouse", JsonUtility.ToJson(request), SupervisionTimeoutSeconds, done);
        }

        public IEnumerator AskOrder(string text, Action<OrderResponse> done)
        {
            var body = new OrderRequest { text = text };
            yield return Post("/order", JsonUtility.ToJson(body), OrderTimeoutSeconds, done);
        }

        /// <summary>Spoken urgent order: interpretation + plan by Muse Wonder.</summary>
        public IEnumerator AskUrgent(UrgentRequest request, Action<UrgentResponse> done)
        {
            yield return Post("/urgent", JsonUtility.ToJson(request), SupervisionTimeoutSeconds, done);
        }

        // ------------------------------------------------------------------ real-time ASR

        public IEnumerator AsrStart(string sessionId, string[] keywords, string[] languages,
                                    string mode, string mockText, Action<AsrStartResponse> done)
        {
            var body = new AsrStartRequest
            {
                session_id = sessionId,
                mode = mode,
                keywords = keywords,
                languages = languages,
                mock_text = mockText
            };
            yield return Post("/asr/start", JsonUtility.ToJson(body), 45, done);
        }

        /// <summary>One microphone chunk: the answer carries the latest partial (live display).</summary>
        public IEnumerator AsrAudio(string sessionId, byte[] pcm, Action<AsrAudioResponse> done)
        {
            var body = new AsrAudioRequest
            {
                session_id = sessionId,
                pcm_b64 = pcm != null && pcm.Length > 0 ? Convert.ToBase64String(pcm) : null
            };
            yield return Post("/asr/audio", JsonUtility.ToJson(body), 30, done);
        }

        public IEnumerator AsrFinish(string sessionId, Action<AsrFinishResponse> done)
        {
            var body = new AsrSessionIdRequest { session_id = sessionId };
            yield return Post("/asr/finish", JsonUtility.ToJson(body), 60, done);
        }

        public IEnumerator AsrCancel(string sessionId, Action<AsrFinishResponse> done)
        {
            var body = new AsrSessionIdRequest { session_id = sessionId };
            yield return Post("/asr/cancel", JsonUtility.ToJson(body), 15, done);
        }

        /// <summary>Answer given by the RECORDED DEMO: no network call is made at all.</summary>
        public sealed class ReplayAnswer
        {
            public string Json;          // recorded answer, null = this route is not available offline
            public float DelaySeconds;   // recorded latency of the real Meta call
        }

        /// <summary>Set while a recording is replayed (RECORDED DEMO): the request never leaves the process.</summary>
        public static Func<string, string, ReplayAnswer> ReplayHandler;

        private IEnumerator Post<T>(string route, string json, int timeout, Action<T> done) where T : class
        {
            if (ReplayHandler != null)
            {
                ReplayAnswer answer = ReplayHandler(route, json);
                if (answer != null)
                {
                    if (answer.DelaySeconds > 0f) yield return new WaitForSeconds(answer.DelaySeconds);
                    done?.Invoke(answer.Json != null ? Parse<T>(answer.Json) : null);
                    yield break;
                }
            }
            using (var request = new UnityWebRequest(BaseUrl + route, "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.timeout = timeout;
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning("[MetaProxy] " + route + " failed: " + request.error + " :: " + request.downloadHandler.text);
                    done?.Invoke(null);
                    yield break;
                }
                done?.Invoke(Parse<T>(request.downloadHandler.text));
            }
        }

        private static T Parse<T>(string text) where T : class
        {
            try { return JsonUtility.FromJson<T>(text); }
            catch (Exception error)
            {
                Debug.LogWarning("[MetaProxy] invalid json: " + error.Message + " :: " + text);
                return null;
            }
        }
    }
}
