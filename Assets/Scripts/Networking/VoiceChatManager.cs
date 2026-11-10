using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EscapeRoom.Senses;
using Unity.Services.Multiplayer;
using Unity.Services.Vivox;
using Unity.Services.Vivox.AudioTaps;
using UnityEngine;
using UnityEngine.InputSystem;

namespace EscapeRoom.Networking
{
    /// <summary>
    /// Vivox voice chat for the session, with role-based voice access:
    ///  - Blind: talks and hears everyone clearly.
    ///  - Mute:  makes no sound. Their mic is force-muted, and listeners also silence them as a backup.
    ///  - Deaf:  talks normally, but hears every voice scrambled (VoiceScrambler) so they
    ///           can tell someone is talking but not what is said.
    ///
    /// Everyone joins one Vivox channel. Each remote voice is routed through a Vivox
    /// Participant Tap (a Unity AudioSource) on the speaker's avatar, which gives
    /// per-speaker filtering and 3D positional voices.
    /// </summary>
    public class VoiceChatManager : MonoBehaviour
    {
        public static VoiceChatManager Instance { get; private set; }

        [Header("Deaf player's hearing of voices (tune live in Play mode)")]
        [Range(60f, 1000f)] public float deafCutoffHz = 180f;
        [Range(0f, 1f)] public float deafVoiceMix = 0.35f;
        [Range(0f, 1f)] public float deafMumbleMix = 0.6f;
        [Range(50f, 300f)] public float deafMumblePitchHz = 110f;
        [Range(0f, 4f)] public float deafOutputGain = 1.6f;

        [Header("Deaf player's hearing of game sounds")]
        [Range(100f, 5000f)] public float deafGameAudioCutoffHz = 300f;

        [Header("Microphone")]
        [Tooltip("Off = open mic (always talking). On = only transmit while the key is held (handy when testing several players on one PC).")]
        public bool pushToTalk = false;
        public Key pushToTalkKey = Key.V;

        [Header("Positional voice")]
        [Range(0f, 1f)] public float spatialBlend = 0.8f;
        public float minDistance = 2f;
        public float maxDistance = 25f;

        public string Status { get; private set; } = "Voice: off";
        public string ChannelName { get; private set; }
        /// <summary>Remote participants Vivox reports in our channel vs. how many we route through a tap.</summary>
        public int ChannelRemoteCount { get; private set; }
        public int TrackedRemoteCount => remotes.Count;

        // Set BEFORE joining: Vivox fires ParticipantAddedToChannel for people already in
        // the channel during the join, before JoinGroupChannelAsync returns.
        string targetChannel;
        float nextReconcile;
        const float TapRetrySeconds = 3f;
        public bool IsMicMuted => vivoxLoggedIn && VivoxService.Instance.IsInputDeviceMuted;
        public bool IsMicForcedOff => LocalRole == PlayerRole.Mute;
        /// <summary>Manual mic mute from the UI (independent of role and push-to-talk).</summary>
        public bool MicMutedByUser { get; private set; }
        /// <summary>Testing aid: false = this window hears no other players at all (both Unity and Vivox playback).</summary>
        public bool HearOthers { get; private set; } = true;

        static PlayerRole LocalRole => NetworkPlayer.Local != null ? NetworkPlayer.Local.Role.Value : PlayerRole.None;

        readonly Dictionary<string, RemoteVoice> remotes = new();
        bool vivoxInitialized;
        bool vivoxLoggedIn;
        bool busy;

        class RemoteVoice
        {
            public VivoxParticipant Participant;
            public AudioSource Source;
            public VoiceScrambler Scrambler;
            public NetworkPlayer Speaker;
            public float CreatedAt;
            public float NextPulse;
        }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
        }

        void Start()
        {
            if (SessionManager.Instance != null)
                SessionManager.Instance.SessionChanged += OnSessionChanged;
        }

        void OnDestroy()
        {
            if (SessionManager.Instance != null)
                SessionManager.Instance.SessionChanged -= OnSessionChanged;
            UnhookVivoxEvents();
        }

        async void OnSessionChanged(ISession session)
        {
            // Serialise join/leave so rapid host/leave clicks don't overlap.
            while (busy) await Task.Yield();
            busy = true;
            try
            {
                string wanted = session?.Id;
                if (wanted == ChannelName) return;
                if (ChannelName != null) await LeaveChannelAsync();
                if (wanted != null) await JoinChannelAsync(wanted);
            }
            catch (Exception e)
            {
                Status = $"Voice error: {e.Message}";
                Debug.LogException(e);
            }
            finally
            {
                busy = false;
            }
        }

        async Task JoinChannelAsync(string channel)
        {
            Status = "Voice: connecting...";
            if (!vivoxInitialized)
            {
                // UnityServices + anonymous auth are already done by SessionManager,
                // so Vivox authenticates through UGS: no Vivox keys needed in the project.
                await VivoxService.Instance.InitializeAsync();
                vivoxInitialized = true;
                HookVivoxEvents();
            }
            if (!VivoxService.Instance.IsLoggedIn)
            {
                await VivoxService.Instance.LoginAsync(new LoginOptions
                {
                    DisplayName = $"Player{UnityEngine.Random.Range(1000, 9999)}",
                });
            }
            vivoxLoggedIn = true;

            targetChannel = channel;
            await VivoxService.Instance.JoinGroupChannelAsync(channel, ChatCapability.AudioOnly);
            ChannelName = channel;
            Reconcile();
            Status = "Voice: connected";
        }

        async Task LeaveChannelAsync()
        {
            targetChannel = null;
            foreach (var r in remotes.Values)
                r.Participant.DestroyVivoxParticipantTap();
            remotes.Clear();

            if (vivoxLoggedIn && ChannelName != null)
                await VivoxService.Instance.LeaveChannelAsync(ChannelName);
            ChannelName = null;
            Status = "Voice: off";
        }

        void HookVivoxEvents()
        {
            VivoxService.Instance.ParticipantAddedToChannel += OnParticipantAdded;
            VivoxService.Instance.ParticipantRemovedFromChannel += OnParticipantRemoved;
        }

        void UnhookVivoxEvents()
        {
            if (!vivoxInitialized) return;
            VivoxService.Instance.ParticipantAddedToChannel -= OnParticipantAdded;
            VivoxService.Instance.ParticipantRemovedFromChannel -= OnParticipantRemoved;
        }

        void OnParticipantAdded(VivoxParticipant p)
        {
            if (p.IsSelf || targetChannel == null || p.ChannelName != targetChannel || remotes.ContainsKey(p.PlayerId)) return;

            // silenceInChannelAudioMix: the voice is ONLY heard through our tap, never unfiltered.
            var tapGo = p.CreateVivoxParticipantTap($"Voice_{p.DisplayName}", silenceInChannelAudioMix: true);
            if (tapGo == null) return;

            var remote = new RemoteVoice
            {
                Participant = p,
                Source = p.ParticipantTapAudioSource,
                Scrambler = tapGo.AddComponent<VoiceScrambler>(),
                CreatedAt = Time.unscaledTime,
            };
            remotes[p.PlayerId] = remote;
            if (!HearOthers) p.MutePlayerLocally();
            Debug.Log($"[Voice] {p.DisplayName} ({p.PlayerId}) joined voice");
        }

        void OnParticipantRemoved(VivoxParticipant p)
        {
            if (!remotes.TryGetValue(p.PlayerId, out var remote)) return;
            remote.Participant.DestroyVivoxParticipantTap();
            remotes.Remove(p.PlayerId);
        }

        /// <summary>
        /// Safety net: compare against Vivox's own participant list so a missed event can't
        /// leave someone untapped (an untapped voice plays straight through Vivox, unfiltered).
        /// Also retries taps that never started.
        /// </summary>
        void Reconcile()
        {
            if (!vivoxLoggedIn || targetChannel == null) return;
            if (!VivoxService.Instance.ActiveChannels.TryGetValue(targetChannel, out var list))
            {
                ChannelRemoteCount = 0;
                return;
            }

            int count = 0;
            var present = new HashSet<string>();
            foreach (var p in list)
            {
                if (p.IsSelf) continue;
                count++;
                present.Add(p.PlayerId);

                if (remotes.TryGetValue(p.PlayerId, out var r))
                {
                    var tap = r.Source != null ? r.Source.GetComponent<VivoxParticipantTap>() : null;
                    bool stuck = tap == null || (!tap.IsRunning && Time.unscaledTime - r.CreatedAt > TapRetrySeconds);
                    if (!stuck) continue;
                    Debug.LogWarning($"[Voice] Tap for {p.DisplayName} not running, recreating");
                    r.Participant.DestroyVivoxParticipantTap();
                    remotes.Remove(p.PlayerId);
                }
                OnParticipantAdded(p);
            }
            ChannelRemoteCount = count;

            // Drop anyone who left without us getting the event.
            var gone = new List<string>();
            foreach (var id in remotes.Keys) if (!present.Contains(id)) gone.Add(id);
            foreach (var id in gone)
            {
                remotes[id].Participant.DestroyVivoxParticipantTap();
                remotes.Remove(id);
            }
        }

        void Update()
        {
            ApplyMicState();

            if (Time.unscaledTime >= nextReconcile)
            {
                nextReconcile = Time.unscaledTime + 0.5f;
                Reconcile();
            }

            bool localDeaf = LocalRole == PlayerRole.Deaf;
            foreach (var r in remotes.Values)
            {
                if (r.Source == null) continue;

                // Attach the voice to the speaker's avatar once their NetworkPlayer has spawned.
                if (r.Speaker == null)
                {
                    r.Speaker = NetworkPlayer.FindByAuthId(r.Participant.PlayerId);
                    if (r.Speaker != null)
                    {
                        r.Source.transform.SetParent(r.Speaker.VoiceAnchor, false);
                        r.Source.transform.localPosition = Vector3.zero;
                    }
                }

                // Backup for the mic force-mute: never play a Mute player's voice.
                bool speakerIsMute = r.Speaker != null && r.Speaker.Role.Value == PlayerRole.Mute;
                r.Source.mute = speakerIsMute || !HearOthers;

                // Blind vision: show where talking comes from (Mute players make no sound, so no pulse).
                if (!speakerIsMute && r.Speaker != null && r.Participant.SpeechDetected && Time.time >= r.NextPulse)
                {
                    r.NextPulse = Time.time + 0.35f;
                    float energy = Mathf.Clamp01((float)r.Participant.AudioEnergy);
                    SoundVisualizer.Pulse(r.Speaker.VoiceAnchor.position, 0.7f + energy);
                }

                r.Scrambler.enabled = localDeaf;
                r.Scrambler.cutoffHz = deafCutoffHz;
                r.Scrambler.voiceMix = deafVoiceMix;
                r.Scrambler.mumbleMix = deafMumbleMix;
                r.Scrambler.mumblePitchHz = deafMumblePitchHz;
                r.Scrambler.outputGain = deafOutputGain;

                // In the lobby there are no bodies yet, so voices are flat; in game they come from the avatar.
                r.Source.spatialBlend = r.Speaker != null && GameFlow.IsPlaying ? spatialBlend : 0f;
                r.Source.rolloffMode = AudioRolloffMode.Linear;
                r.Source.minDistance = minDistance;
                r.Source.maxDistance = maxDistance;
            }
        }

        /// <summary>Whether a speaker is currently talking (for UI indicators).</summary>
        public bool IsSpeaking(NetworkPlayer player)
        {
            if (player == null || !vivoxLoggedIn) return false;
            if (player.IsOwner)
            {
                // Our own speech state lives on our self participant.
                if (ChannelName == null || !VivoxService.Instance.ActiveChannels.TryGetValue(ChannelName, out var list))
                    return false;
                foreach (var p in list)
                    if (p.IsSelf) return p.SpeechDetected;
                return false;
            }
            foreach (var r in remotes.Values)
                if (r.Speaker == player) return r.Participant.SpeechDetected;
            return false;
        }

        // One place decides whether we transmit:
        // Mute role -> never; user muted -> no; push-to-talk -> only while the key is held.
        void ApplyMicState()
        {
            if (!vivoxLoggedIn) return;
            bool keyHeld = Keyboard.current != null && Keyboard.current[pushToTalkKey].isPressed;
            bool shouldMute = IsMicForcedOff || MicMutedByUser || (pushToTalk && !keyHeld);
            if (shouldMute != VivoxService.Instance.IsInputDeviceMuted)
            {
                if (shouldMute) VivoxService.Instance.MuteInputDevice();
                else VivoxService.Instance.UnmuteInputDevice();
            }
        }

        public void ToggleMic() => MicMutedByUser = !MicMutedByUser;

        /// <summary>
        /// Testing aid. Mutes every remote voice in this window, on BOTH paths:
        /// the Unity tap AudioSource and Vivox's own playback (MutePlayerLocally).
        /// If you still hear voices with this off, the sound is coming from another window.
        /// </summary>
        public void SetHearOthers(bool hear)
        {
            HearOthers = hear;
            foreach (var r in remotes.Values)
            {
                if (hear) r.Participant.UnmutePlayerLocally();
                else r.Participant.MutePlayerLocally();
            }
        }

        public struct VoiceDebugInfo
        {
            public string Name;
            public PlayerRole SpeakerRole;
            public bool Linked, TapRunning, Speaking;
            public string Effect;
        }

        public IEnumerable<VoiceDebugInfo> GetDebugInfo()
        {
            bool localDeaf = LocalRole == PlayerRole.Deaf;
            foreach (var r in remotes.Values)
            {
                var tap = r.Source != null ? r.Source.GetComponent<VivoxParticipantTap>() : null;
                bool mute = r.Speaker != null && r.Speaker.Role.Value == PlayerRole.Mute;
                yield return new VoiceDebugInfo
                {
                    Name = r.Speaker != null ? $"Client {r.Speaker.OwnerClientId}" : r.Participant.DisplayName,
                    SpeakerRole = r.Speaker != null ? r.Speaker.Role.Value : PlayerRole.None,
                    Linked = r.Speaker != null,
                    TapRunning = tap != null && tap.IsRunning,
                    Speaking = r.Participant.SpeechDetected,
                    Effect = !HearOthers ? "muted (testing)" : mute ? "silenced (Mute)" : localDeaf ? "scrambled (you are Deaf)" : "clear",
                };
            }
        }

        async void OnApplicationQuit()
        {
            if (!vivoxLoggedIn) return;
            try { await VivoxService.Instance.LeaveAllChannelsAsync(); } catch { /* quitting */ }
        }
    }
}
