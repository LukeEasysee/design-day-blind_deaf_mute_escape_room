using System;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;

namespace EscapeRoom.Networking
{
    /// <summary>
    /// Wraps Unity Gaming Services (Multiplayer Services "Sessions"), which handles
    /// Lobby + Relay for us: the host creates a session and gets a short join code,
    /// friends on any network join with that code. Relay means no port forwarding.
    /// Sessions automatically configures the UnityTransport on the NetworkManager and
    /// starts NGO as host/client.
    /// </summary>
    public class SessionManager : MonoBehaviour
    {
        public const int MaxPlayers = 3;

        public static SessionManager Instance { get; private set; }

        public ISession Session { get; private set; }
        public bool IsBusy { get; private set; }
        public bool IsSignedIn => UnityServices.State == ServicesInitializationState.Initialized
                                  && AuthenticationService.Instance.IsSignedIn;
        public string Status { get; private set; } = "Not connected";

        public event Action<ISession> SessionChanged;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        async void Start()
        {
            await EnsureSignedInAsync();
        }

        async Task EnsureSignedInAsync()
        {
            if (IsSignedIn) return;
            try
            {
                Status = "Signing in...";
                if (UnityServices.State == ServicesInitializationState.Uninitialized)
                {
                    // Each Multiplayer Play Mode clone / build instance needs its own auth
                    // profile, otherwise all of them sign in as the same anonymous player.
                    var options = new InitializationOptions().SetProfile(GetProfileName());
                    await UnityServices.InitializeAsync(options);
                }
                while (UnityServices.State == ServicesInitializationState.Initializing)
                    await Task.Yield();

                if (!AuthenticationService.Instance.IsSignedIn)
                    await AuthenticationService.Instance.SignInAnonymouslyAsync();

                Status = "Signed in";
            }
            catch (Exception e)
            {
                Status = $"Sign-in failed: {e.Message}";
                Debug.LogException(e);
            }
        }

        static string GetProfileName()
        {
#if UNITY_EDITOR
            // Main editor and each MPPM virtual player have different data paths.
            uint hash = (uint)Application.dataPath.GetHashCode();
            return $"editor{hash % 100000000}";
#else
            // Builds: random per launch so two builds on one PC don't collide.
            return $"player{UnityEngine.Random.Range(0, 100000000)}";
#endif
        }

        public Task HostAsync() => Run("Creating session...", async () =>
        {
            var options = new SessionOptions
            {
                MaxPlayers = MaxPlayers,
                IsPrivate = true, // join by code only
            }.WithRelayNetwork();

            Session = await MultiplayerService.Instance.CreateSessionAsync(options);
            Session.RemovedFromSession += OnRemovedFromSession;
            Status = $"Hosting. Join code: {Session.Code}";
        });

        public Task JoinAsync(string code) => Run("Joining session...", async () =>
        {
            Session = await MultiplayerService.Instance.JoinSessionByCodeAsync(code.Trim().ToUpperInvariant());
            Session.RemovedFromSession += OnRemovedFromSession;
            Status = $"Joined session {Session.Code}";
        });

        public Task LeaveAsync() => Run("Leaving...", async () =>
        {
            var leaving = Session;
            Session = null;
            if (leaving != null)
            {
                leaving.RemovedFromSession -= OnRemovedFromSession;
                await leaving.LeaveAsync();
            }
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
                NetworkManager.Singleton.Shutdown();
            Status = "Not connected";
        });

        // Host left / we were kicked: clean up so voice and UI reset too.
        void OnRemovedFromSession()
        {
            if (Session != null) Session.RemovedFromSession -= OnRemovedFromSession;
            Session = null;
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
                NetworkManager.Singleton.Shutdown();
            Status = "Session ended";
            SessionChanged?.Invoke(null);
        }

        async Task Run(string busyStatus, Func<Task> action)
        {
            if (IsBusy) return;
            IsBusy = true;
            Status = busyStatus;
            try
            {
                await EnsureSignedInAsync();
                await action();
            }
            catch (Exception e)
            {
                Status = $"Error: {e.Message}";
                Debug.LogException(e);
            }
            finally
            {
                IsBusy = false;
                SessionChanged?.Invoke(Session);
            }
        }

        async void OnApplicationQuit()
        {
            if (Session != null)
            {
                try { await Session.LeaveAsync(); } catch { /* quitting anyway */ }
            }
        }
    }
}
