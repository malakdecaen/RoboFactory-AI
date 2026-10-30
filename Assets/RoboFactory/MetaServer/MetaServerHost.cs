using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace RoboFactory.MetaServer
{
    /// <summary>
    /// Démarre le serveur Meta intégré (<see cref="MetaLocalServer"/>) au lancement du jeu et l'arrête à
    /// la fermeture — dans l'éditeur comme dans le build, sans Python ni exécutable externe.
    /// Si un autre serveur occupe déjà le port 8765 (proxy Python lancé à la main), il est utilisé tel quel.
    /// Clé API : <see cref="SetApiKey"/> (fenêtre LIVE AI, mémoire seulement), sinon MODEL_API_KEY ou
    /// ~/.meta/model_api_key. Journal des appels : logs/meta_calls.jsonl.
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

        /// <summary>Clé saisie par l'utilisateur : gardée en mémoire pour cette session, jamais écrite sur disque.</summary>
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
                StatusText = "serveur Meta intégré actif" + (KeyPresent ? "" : " — aucune clé API : appels Meta impossibles");
                if (!KeyPresent) Debug.LogWarning("[MetaServer] " + StatusText);
                return;
            }
            // Port occupé : un serveur répond-il déjà (proxy Python lancé à la main) ?
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
                    StatusText = "un autre serveur Meta occupe déjà le port 8765 : il est utilisé";
                    Debug.LogWarning("[MetaServer] " + StatusText);
                }
                else
                {
                    State = ServerState.Failed;
                    StatusText = "serveur Meta intégré impossible à démarrer : " + bindError;
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
