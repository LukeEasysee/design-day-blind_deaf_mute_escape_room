using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EscapeRoom.Networking
{
    public enum GamePhase : byte
    {
        Lobby = 0,
        Playing = 1,
        /// <summary>The level is being reloaded (timer ran out / Blind player fell). Players are frozen.</summary>
        Restarting = 2,
    }

    /// <summary>
    /// Shared game state, on an in-scene "GameFlow" NetworkObject in the bootstrap scene.
    ///  Lobby      : players connected (roles + voice), no bodies.
    ///  Start      : host locks the session and loads the level scene additively.
    ///  Playing    : a round of <see cref="roundSeconds"/>; when the timer hits 0 the level restarts.
    ///  Restarting : popup shown to everyone, level unloaded + reloaded, everyone back at their spawn.
    /// </summary>
    public class GameFlow : NetworkBehaviour
    {
        public static GameFlow Instance { get; private set; }

        /// <summary>The round is running: players can move.</summary>
        public static bool IsPlaying => Instance != null && Instance.IsSpawned && Instance.Phase.Value == GamePhase.Playing;
        /// <summary>Past the lobby (playing or restarting): bodies and player cameras are shown.</summary>
        public static bool IsInGame => Instance != null && Instance.IsSpawned && Instance.Phase.Value != GamePhase.Lobby;

        [Tooltip("Level scene to load when the host presses Start (must be in the build list). Empty = play in the current scene.")]
        public string levelSceneName = "Level";
        [Tooltip("Round length in seconds. When it runs out the level restarts.")]
        public float roundSeconds = 120f;
        [Tooltip("How long the restart popup stays up before the level reloads.")]
        public float restartDelay = 3f;

        public readonly NetworkVariable<GamePhase> Phase = new(GamePhase.Lobby,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        /// <summary>Increments every time a (re)loaded level starts. Players respawn when it changes.</summary>
        public readonly NetworkVariable<int> Round = new(0,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        /// <summary>Server time (seconds) at which the current round ends.</summary>
        public readonly NetworkVariable<double> RoundEndsAt = new(0,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public static event Action<GamePhase> PhaseChanged;
        public static event Action<int> RoundStarted;
        /// <summary>Popup text + how long to show it (seconds). Raised on every client.</summary>
        public static event Action<string, float> MessageShown;

        public bool IsStarting { get; private set; }

        /// <summary>Seconds left in the round (0 outside of Playing).</summary>
        public float SecondsLeft => IsPlaying ? Mathf.Max(0f, (float)(RoundEndsAt.Value - NetworkManager.ServerTime.Time)) : 0f;

        readonly List<Light> disabledBootstrapLights = new();
        float restartAt = -1f;

        void Awake() => Instance = this;

        public override void OnNetworkSpawn()
        {
            Instance = this;
            if (IsServer)
            {
                Phase.Value = GamePhase.Lobby;
                Round.Value = 0;
                IsStarting = false;
            }
            Phase.OnValueChanged += OnPhaseChanged;
            Round.OnValueChanged += OnRoundChanged;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            PhaseChanged?.Invoke(Phase.Value);
        }

        public override void OnNetworkDespawn()
        {
            Phase.OnValueChanged -= OnPhaseChanged;
            Round.OnValueChanged -= OnRoundChanged;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            IsStarting = false;
            restartAt = -1f;

            // Left the session: drop the level locally so the lobby is clean next time.
            var level = SceneManager.GetSceneByName(levelSceneName);
            if (level.IsValid() && level.isLoaded && SceneManager.sceneCount > 1)
                SceneManager.UnloadSceneAsync(level);
            RestoreBootstrapLights();
            PhaseChanged?.Invoke(GamePhase.Lobby);
        }

        void OnPhaseChanged(GamePhase previous, GamePhase current) => PhaseChanged?.Invoke(current);
        void OnRoundChanged(int previous, int current) => RoundStarted?.Invoke(current);

        // ---------------- Server: start / timer / restart ----------------

        /// <summary>Host only: lock the session and load the level.</summary>
        public void StartGame()
        {
            if (!IsServer || Phase.Value != GamePhase.Lobby || IsStarting) return;
            IsStarting = true;
            LockSession();
            LoadLevel();
        }

        void Update()
        {
            if (!IsServer || !IsSpawned) return;

            if (Phase.Value == GamePhase.Playing && NetworkManager.ServerTime.Time >= RoundEndsAt.Value)
                RestartLevel("Time's up! Restarting the level...");

            if (restartAt > 0f && Time.unscaledTime >= restartAt)
            {
                restartAt = -1f;
                ReloadLevel();
            }
        }

        /// <summary>Server: show a popup to everyone, then reload the level.</summary>
        public void RestartLevel(string reason)
        {
            if (!IsServer || Phase.Value != GamePhase.Playing) return;
            Phase.Value = GamePhase.Restarting;
            ShowMessageRpc(reason, restartDelay + 1f);
            restartAt = Time.unscaledTime + restartDelay;
        }

        /// <summary>Any client: ask the server to restart (e.g. the Blind player fell).</summary>
        [Rpc(SendTo.Server)]
        public void RequestRestartRpc(string reason) => RestartLevel(reason);

        [Rpc(SendTo.Everyone)]
        void ShowMessageRpc(string message, float seconds) => MessageShown?.Invoke(message, seconds);

        void LoadLevel()
        {
            if (string.IsNullOrWhiteSpace(levelSceneName))
            {
                BeginRound();
                return;
            }
            NetworkManager.SceneManager.OnLoadEventCompleted += OnLevelLoaded;
            var status = NetworkManager.SceneManager.LoadScene(levelSceneName, LoadSceneMode.Additive);
            if (status != SceneEventProgressStatus.Started)
            {
                Debug.LogError($"[GameFlow] Could not load level '{levelSceneName}': {status}. Is it in the build list?");
                NetworkManager.SceneManager.OnLoadEventCompleted -= OnLevelLoaded;
                IsStarting = false;
            }
        }

        void ReloadLevel()
        {
            var level = SceneManager.GetSceneByName(levelSceneName);
            if (string.IsNullOrWhiteSpace(levelSceneName) || !level.IsValid() || !level.isLoaded)
            {
                LoadLevel();
                return;
            }
            NetworkManager.SceneManager.OnUnloadEventCompleted += OnLevelUnloaded;
            var status = NetworkManager.SceneManager.UnloadScene(level);
            if (status != SceneEventProgressStatus.Started)
            {
                Debug.LogError($"[GameFlow] Could not unload level: {status}");
                NetworkManager.SceneManager.OnUnloadEventCompleted -= OnLevelUnloaded;
                BeginRound(); // at least un-freeze everyone
            }
        }

        void OnLevelUnloaded(string sceneName, LoadSceneMode mode, List<ulong> completed, List<ulong> timedOut)
        {
            if (sceneName != levelSceneName) return;
            NetworkManager.SceneManager.OnUnloadEventCompleted -= OnLevelUnloaded;
            LoadLevel();
        }

        void OnLevelLoaded(string sceneName, LoadSceneMode mode, List<ulong> completed, List<ulong> timedOut)
        {
            if (sceneName != levelSceneName) return;
            NetworkManager.SceneManager.OnLoadEventCompleted -= OnLevelLoaded;
            BeginRound();
        }

        void BeginRound()
        {
            RoundEndsAt.Value = NetworkManager.ServerTime.Time + roundSeconds;
            Round.Value++;
            Phase.Value = GamePhase.Playing;
            IsStarting = false;
        }

        // Nobody can join once the game is running.
        static async void LockSession()
        {
            var session = SessionManager.Instance != null ? SessionManager.Instance.Session : null;
            if (session == null || !session.IsHost) return;
            try
            {
                var host = session.AsHost();
                host.IsLocked = true;
                await host.SavePropertiesAsync();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GameFlow] Could not lock session: {e.Message}");
            }
        }

        // ---------------- All clients: level scene housekeeping ----------------

        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name != levelSceneName) return;
            SceneManager.SetActiveScene(scene);

            // The level brings its own lighting; switch off the bootstrap scene's lights to avoid double lighting.
            if (disabledBootstrapLights.Count == 0)
                foreach (var l in FindObjectsByType<Light>())
                    if (l.gameObject.scene != scene && l.enabled)
                    {
                        l.enabled = false;
                        disabledBootstrapLights.Add(l);
                    }
        }

        void OnSceneUnloaded(Scene scene)
        {
            if (scene.name == levelSceneName && Phase.Value == GamePhase.Lobby) RestoreBootstrapLights();
        }

        void RestoreBootstrapLights()
        {
            foreach (var l in disabledBootstrapLights) if (l != null) l.enabled = true;
            disabledBootstrapLights.Clear();
        }
    }
}
