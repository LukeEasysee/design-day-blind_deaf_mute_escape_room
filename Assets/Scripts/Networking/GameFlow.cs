using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EscapeRoom.Networking
{
    public enum GamePhase : byte
    {
        Lobby = 0,
        Playing = 1,
    }

    /// <summary>
    /// Shared game state. Players connect into the Lobby (roles + voice, no bodies yet);
    /// the host presses Start, which locks the session, optionally loads the level scene
    /// (additively, so the network/bootstrap scene stays), then switches everyone to Playing.
    /// Lives on an in-scene "GameFlow" object with a NetworkObject (created by Escape Room > Setup Player).
    /// </summary>
    public class GameFlow : NetworkBehaviour
    {
        public static GameFlow Instance { get; private set; }
        public static bool IsPlaying => Instance != null && Instance.IsSpawned && Instance.Phase.Value == GamePhase.Playing;

        [Tooltip("Level scene to load when the host presses Start (must be in the build list). Leave empty to play in the current scene.")]
        public string levelSceneName = "";

        public readonly NetworkVariable<GamePhase> Phase = new(GamePhase.Lobby,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        /// <summary>Raised on every client when the phase changes.</summary>
        public static event Action<GamePhase> PhaseChanged;

        public bool IsStarting { get; private set; }

        void Awake() => Instance = this;

        public override void OnNetworkSpawn()
        {
            Instance = this;
            if (IsServer)
            {
                Phase.Value = GamePhase.Lobby;
                IsStarting = false;
            }
            Phase.OnValueChanged += OnPhaseChanged;
            PhaseChanged?.Invoke(Phase.Value);
        }

        public override void OnNetworkDespawn()
        {
            Phase.OnValueChanged -= OnPhaseChanged;
            IsStarting = false;
            PhaseChanged?.Invoke(GamePhase.Lobby);
        }

        void OnPhaseChanged(GamePhase previous, GamePhase current) => PhaseChanged?.Invoke(current);

        /// <summary>Host only.</summary>
        public void StartGame()
        {
            if (!IsServer || Phase.Value != GamePhase.Lobby || IsStarting) return;
            IsStarting = true;
            LockSession();

            if (string.IsNullOrWhiteSpace(levelSceneName))
            {
                Phase.Value = GamePhase.Playing;
                IsStarting = false;
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

        void OnLevelLoaded(string sceneName, LoadSceneMode mode, System.Collections.Generic.List<ulong> completed, System.Collections.Generic.List<ulong> timedOut)
        {
            if (sceneName != levelSceneName) return;
            NetworkManager.SceneManager.OnLoadEventCompleted -= OnLevelLoaded;
            var level = SceneManager.GetSceneByName(sceneName);
            if (level.IsValid()) SceneManager.SetActiveScene(level);
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
    }
}
