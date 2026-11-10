using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Netcode;
using Unity.Services.Authentication;
using UnityEngine;

namespace EscapeRoom.Networking
{
    /// <summary>
    /// The networked player identity: server-assigned role (Blind/Deaf/Mute), UGS id for
    /// voice matching, and the local camera. Movement lives in EscapeRoom.Player.PlayerController,
    /// the procedural body in EscapeRoom.Player.CharacterPose.
    /// </summary>
    public class NetworkPlayer : NetworkBehaviour
    {
        public static readonly List<NetworkPlayer> All = new();
        public static NetworkPlayer Local { get; private set; }

        /// <summary>Raised on every client when the local player's role changes.</summary>
        public static event Action<PlayerRole> LocalRoleChanged;

        public readonly NetworkVariable<PlayerRole> Role = new(
            PlayerRole.None,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        /// <summary>UGS player id of the owner. Used to match Vivox voices to avatars.</summary>
        public readonly NetworkVariable<FixedString64Bytes> AuthPlayerId = new(
            default,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        public Camera PlayerCamera => playerCamera;
        /// <summary>Where this player's voice comes from (their head).</summary>
        public Transform VoiceAnchor => voiceAnchor != null ? voiceAnchor : transform;

        public static NetworkPlayer FindByAuthId(string authId)
        {
            if (string.IsNullOrEmpty(authId)) return null;
            foreach (var p in All)
                if (p.AuthPlayerId.Value.ToString() == authId) return p;
            return null;
        }

        [SerializeField] Camera playerCamera;
        [SerializeField] Transform voiceAnchor;

        public override void OnNetworkSpawn()
        {
            All.Add(this);
            Role.OnValueChanged += OnRoleChanged;

            if (IsServer)
                Role.Value = PickFreeRole();

            if (IsOwner)
            {
                Local = this;
                AuthPlayerId.Value = AuthenticationService.Instance.PlayerId ?? string.Empty;
                // Cameras are switched by PlayerController when the game starts (lobby uses the scene camera).
                LocalRoleChanged?.Invoke(Role.Value);
            }
        }

        public override void OnNetworkDespawn()
        {
            All.Remove(this);
            Role.OnValueChanged -= OnRoleChanged;
            if (Local == this) Local = null;
        }

        void OnRoleChanged(PlayerRole previous, PlayerRole current)
        {
            if (IsOwner)
                LocalRoleChanged?.Invoke(current);
        }

        static PlayerRole PickFreeRole()
        {
            var taken = All.Select(p => p.Role.Value).ToHashSet();
            foreach (PlayerRole r in new[] { PlayerRole.Blind, PlayerRole.Deaf, PlayerRole.Mute })
                if (!taken.Contains(r)) return r;
            return PlayerRole.None;
        }

        /// <summary>Lobby role picking. The server only grants roles nobody else holds.</summary>
        [Rpc(SendTo.Server)]
        public void RequestRoleRpc(PlayerRole requested)
        {
            if (requested == PlayerRole.None) return;
            if (All.Any(p => p != this && p.Role.Value == requested)) return;
            Role.Value = requested;
        }
    }
}
