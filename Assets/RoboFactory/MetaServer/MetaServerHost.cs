using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace RoboFactory.MetaServer
{
    /// <summary>
    /// Starts the built-in Meta server (<see cref="MetaLocalServer"/>) when the game launches and stops it
    /// on exit — in the editor as in the build, without Python or an external executable.
    /// If another server already uses port 8765 (Python proxy started by hand), it is used as is.
    /// API key: <see cref="SetApiKey"/> (LIVE AI window, memory only), otherwise MODEL_API_KEY or
    /// ~/.meta/model_api_key. Call log: logs/meta_calls.jsonl.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MetaServerHost : MonoBehaviour
    {
        public const string Url = "http://127.0.0.1:8765";
        public enum ServerState { Starting, Running, External, Failed }

        public static ServerState State { get; private set; } = ServerState.Starting;
        public static string StatusText { get; private set; } = "";
        public static bool IsReady => State == ServerState.Running || State == ServerState.External;
        public static bool KeyPresent => MetaApi.LoadKey() != null;

        private static MetaServerHost instance;
        private static MetaLocalServer server;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Boot()
        {
            if (instance != null) return;
            var go = new GameObject("MetaServerHost");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<MetaServerHost>();
        }

        /// <summary>Key entered by the user: kept in memory for this session, never written to disk.</summary>
        public static void SetApiKey(string key) { MetaApi.SetSessionKey(key); }

        private void Awake()
        {
            Application.quitting += StopServer;
            AppDomain.CurrentDomain.DomainUnload += OnDomainUnload;
            string root = Application.isEditor
                ? Directory.GetParent(Application.dataPath).FullName
                : Application.persistentDataPath;
            MetaApi.LogFile = Path.Combine(root, "logs", "meta_calls.jsonl");
            StartServer();
        }

        private void OnDestroy()
        {
            Application.quitting -= StopServer;
            AppDomain.CurrentDomain.DomainUnload -= OnDomainUnload;
            StopServer();
            if (instance == this) instance = null;
        }

        private void OnApplicationQuit() { StopServer(); }

        private static void OnDomainUnload(object sender, EventArgs e) { StopServer(); }

        private void StartServer()
        {
            server = new MetaLocalServer { Logger = m => Debug.Log("[MetaServer] " + m) };
            if (server.Start())
            {
                State = ServerState.Running;
                StatusText = "built-in Meta server running" + (KeyPresent ? "" : " — no API key: Meta calls impossible");
                if (!KeyPresent) Debug.LogWarning("[MetaServer] " + StatusText);
                return;
            }
            // Port busy: is a server already answering (Python proxy started by hand)?
            string error = server.LastError;
            server = null;
            StartCoroutine(CheckExternal(error));
        }

        private IEnumerator CheckExternal(string bindError)
        {
            using (UnityWebRequest req = UnityWebRequest.Get(Url + "/health"))
            {
                req.timeout = 2;
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                {
                    State = ServerState.External;
                    StatusText = "another Meta server already uses port 8765: it is used";
                    Debug.LogWarning("[MetaServer] " + StatusText);
                }
                else
                {
                    State = ServerState.Failed;
                    StatusText = "built-in Meta server could not start: " + bindError;
                    Debug.LogError("[MetaServer] " + StatusText);
                }
            }
        }

        public static void StopServer()
        {
            if (server == null) return;
            server.Stop();
            server = null;
            if (State == ServerState.Running) State = ServerState.Starting;
        }
    }
}
