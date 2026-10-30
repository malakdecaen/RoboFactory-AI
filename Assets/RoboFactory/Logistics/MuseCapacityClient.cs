using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace RoboFactory.Logistics
{
    /// <summary>
    /// FINISH TO FREE : une commande normale candidate à finir avant l'urgente.
    /// Toutes les valeurs viennent de l'état réel (cartons préparés / chargés, réservations,
    /// distances mesurées dans la scène) ; l'ETA est une estimation calculée, signalée comme telle.
    /// </summary>
    [Serializable]
    public sealed class FtfCandidate
    {
        public int order_id;
        public string status;           // picking / loading / queued / paused ...
        public bool paused;
        public string bench_id;
        public string truck_id;
        public int total;
        public int staged;
        public int loaded;
        public int remaining_pick;      // cartons encore à collecter par R1
        public int remaining_load;      // cartons encore à charger par R2
        public float eta_free_s;        // estimation : délai avant libération banc + camion
        public bool frees_bench;
        public bool frees_truck;
        public int truck_capacity;      // places du camion une fois expédié (vide)
        public bool can_finish;         // stock libre suffisant pour le reste à collecter
        public string note;
    }

    [Serializable]
    public sealed class FtfContext
    {
        public int urgent_order_id;
        public int urgent_total;
        public string urgent_needs;     // "A x2, B x1"
        public string missing;          // "bench+truck" | "truck" | "bench"
        public string[] free_benches;
        public string[] empty_trucks;
        public FtfCandidate[] candidates;
        public string eta_method;
    }

    [Serializable]
    public sealed class CapacityRequest
    {
        public string request_id;
        public string mode;
        public string capacity_json;    // FtfContext sérialisé
        public string state_json;       // état complet de l'entrepôt (contexte)
    }

    [Serializable]
    public sealed class CapacityResult
    {
        public string action;           // finish_order | wait
        public int order_id;
        public string bench_id;
        public string truck_id;
        public float expected_free_s;
        public string reason;
    }

    [Serializable]
    public sealed class CapacityResponse
    {
        public bool ok;
        public string mode;
        public string model;
        public int latency_ms;
        public string error;
        public string note;
        public string request_id;
        public CapacityResult result;
    }

    /// <summary>Route /capacity du proxy Meta (même proxy, même clé — jamais côté Unity).</summary>
    public sealed class MuseCapacityClient
    {
        public string BaseUrl = "http://127.0.0.1:8765";
        public int TimeoutSeconds = 120;

        public IEnumerator Ask(CapacityRequest body, Action<CapacityResponse> done)
        {
            using (var request = new UnityWebRequest(BaseUrl + "/capacity", "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonUtility.ToJson(body)));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.timeout = TimeoutSeconds;
                yield return request.SendWebRequest();
                string text = request.downloadHandler != null ? request.downloadHandler.text : "";
                CapacityResponse parsed = null;
                if (!string.IsNullOrEmpty(text))
                {
                    try { parsed = JsonUtility.FromJson<CapacityResponse>(text); }
                    catch (Exception) { parsed = null; }
                }
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning("[MetaProxy] /capacity failed: " + request.error + " :: " + text);
                    if (parsed != null) parsed.ok = false;
                    done?.Invoke(parsed);
                    yield break;
                }
                done?.Invoke(parsed);
            }
        }
    }
}
