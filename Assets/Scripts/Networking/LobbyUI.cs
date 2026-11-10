using Unity.Netcode;
using UnityEngine;

namespace EscapeRoom.Networking
{
    /// <summary>
    /// Temporary IMGUI lobby for testing the network layer. Replace with real UI later.
    /// </summary>
    public class LobbyUI : MonoBehaviour
    {
        string joinCode = "";

        /// <summary>Screen area of the panel (GUI coordinates), so clicks on it don't capture the mouse.</summary>
        public static Rect PanelRect { get; private set; }

        void OnGUI()
        {
            GUI.depth = 0;
            var sm = SessionManager.Instance;
            if (sm == null) return;

            // In game with the cursor captured: show only a small hint so the view stays clear.
            bool inGame = sm.Session != null && Cursor.lockState == CursorLockMode.Locked;
            if (inGame)
            {
                PanelRect = Rect.zero;
                GUI.Label(new Rect(10, 10, 600, 22), "Esc: menu   WASD: walk   Hold LMB / RMB: raise left / right hand   Hold V: talk");
                return;
            }

            PanelRect = new Rect(10, 10, 380, 560);
            GUILayout.BeginArea(PanelRect, GUI.skin.box);
            GUILayout.Label(sm.Status);

            bool inSession = sm.Session != null;
            GUI.enabled = !sm.IsBusy && sm.IsSignedIn;

            if (!inSession)
            {
                if (GUILayout.Button("Host (creates join code)"))
                    _ = sm.HostAsync();

                GUILayout.Space(8);
                GUILayout.Label("Join code:");
                joinCode = GUILayout.TextField(joinCode, 12);
                if (GUILayout.Button("Join") && !string.IsNullOrWhiteSpace(joinCode))
                    _ = sm.JoinAsync(joinCode);
            }
            else
            {
                GUILayout.Label($"Code: {sm.Session.Code}   Players: {sm.Session.PlayerCount}/{sm.Session.MaxPlayers}");
                if (GUILayout.Button("Copy code"))
                    GUIUtility.systemCopyBuffer = sm.Session.Code;

                var nm = NetworkManager.Singleton;
                GUILayout.Label(nm == null ? "No NetworkManager" :
                    nm.IsHost ? "NGO: Host" : nm.IsClient ? "NGO: Client" : "NGO: not running");

                // Lobby -> game
                var flow = GameFlow.Instance;
                if (flow == null || !flow.IsSpawned)
                    GUILayout.Label("No GameFlow in scene (run Escape Room > Setup Player)");
                else if (flow.Phase.Value == GamePhase.Lobby)
                {
                    if (nm != null && nm.IsHost)
                    {
                        GUI.enabled = !flow.IsStarting;
                        string label = flow.IsStarting ? "Starting..." : $"START GAME ({NetworkPlayer.All.Count}/3 players)";
                        if (GUILayout.Button(label, GUILayout.Height(32)))
                            flow.StartGame();
                        GUI.enabled = true;
                    }
                    else
                        GUILayout.Label("Waiting for the host to start...");
                }
                else
                    GUILayout.Label("In game. Click the game view to play.");

                var voice = VoiceChatManager.Instance;
                if (voice != null)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(voice.Status);
                    if (voice.IsMicForcedOff)
                        GUILayout.Label("Mic off (Mute)", GUILayout.Width(110));
                    else if (GUILayout.Button(voice.MicMutedByUser ? "Unmute mic" : "Mute mic", GUILayout.Width(110)))
                        voice.ToggleMic();
                    GUILayout.EndHorizontal();

                    if (!voice.IsMicForcedOff && voice.pushToTalk)
                        GUILayout.Label(voice.IsMicMuted ? $"Hold {voice.pushToTalkKey} to talk" : "TRANSMITTING");

                    bool hear = GUILayout.Toggle(voice.HearOthers, " Hear other players (testing)");
                    if (hear != voice.HearOthers) voice.SetHearOthers(hear);

                    GUILayout.Label($"Incoming voices (Vivox reports {voice.ChannelRemoteCount}, routed {voice.TrackedRemoteCount}):");
                    foreach (var v in voice.GetDebugInfo())
                    {
                        string link = v.Linked ? "" : " [no avatar yet]";
                        string tap = v.TapRunning ? "" : " [TAP NOT RUNNING]";
                        string talk = v.Speaking ? " [talking]" : "";
                        GUILayout.Label($"  {v.Name} ({v.SpeakerRole}): {v.Effect}{link}{tap}{talk}");
                    }
                }

                GUILayout.Space(8);
                GUILayout.Label("Players:");
                foreach (var p in NetworkPlayer.All)
                {
                    string me = p.IsOwner ? " (you)" : "";
                    string talking = voice != null && voice.IsSpeaking(p) ? "  [talking]" : "";
                    GUILayout.Label($"  Client {p.OwnerClientId}: {p.Role.Value}{me}{talking}");
                }

                var local = NetworkPlayer.Local;
                if (local != null)
                {
                    GUILayout.Label("Request role:");
                    GUILayout.BeginHorizontal();
                    foreach (var r in new[] { PlayerRole.Blind, PlayerRole.Deaf, PlayerRole.Mute })
                        if (GUILayout.Button(r.ToString()))
                            local.RequestRoleRpc(r);
                    GUILayout.EndHorizontal();
                }

                GUILayout.Space(8);
                if (GUILayout.Button("Leave"))
                    _ = sm.LeaveAsync();
            }

            GUI.enabled = true;
            GUILayout.EndArea();
        }
    }
}
