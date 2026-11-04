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
    /// The server only answers the game: every call must carry a random session token made at launch (memory only,
    /// never sent to Meta), and calls from web pages are refused. If port 8765 is already taken by another program,
    /// that program is NOT used: LIVE AI reports the error (RECORDED DEMO does not need the server).
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
        public static bool IsReady => State == ServerState.Running;
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

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void HookReload()
        {
            // stop the server and its calls BEFORE Unity reloads the scripts (Play / script changes)
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= StopServer;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += StopServer;
            UnityEditor.EditorApplication.playModeStateChanged -= OnPlayMode;
            UnityEditor.EditorApplication.playModeStateChanged += OnPlayMode;
        }

        private static void OnPlayMode(UnityEditor.PlayModeStateChange change)
        {
            if (change == UnityEditor.PlayModeStateChange.ExitingPlayMode) StopServer();
        }
#endif

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
            var bytes = new byte[32];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            string token = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            RoboFactory.Hackathon.Meta.MetaProxyClient.SessionToken = token;
            server = new MetaLocalServer { Token = token };
            MetaLocalServer own = server;
            // log only while this server runs (never from a worker thread once Play has stopped)
            own.Logger = m => { if (own.Running) Debug.Log("[MetaServer] " + m); };
            if (server.Start())
            {
                State = ServerState.Running;
                StatusText = "built-in Meta server running" + (KeyPresent ? "" : " — no API key: Meta calls impossible");
                if (!KeyPresent) Debug.LogWarning("[MetaServer] " + StatusText);
                return;
            }
            // Port busy: another program is not trusted with the user's key; LIVE AI says so (RECORDED DEMO still runs)
            string error = server.LastError;
            server = null;
            State = ServerState.Failed;
            StatusText = "port 8765 is used by another program, close it and restart (" + error + ")";
            Debug.LogError("[MetaServer] built-in Meta server could not start: " + StatusText);
        }

        public static void StopServer()
        {
            MetaApi.CancelAll();
            if (server == null) return;
            server.Stop();
            server = null;
            if (State == ServerState.Running) State = ServerState.Starting;
        }
    }
}
