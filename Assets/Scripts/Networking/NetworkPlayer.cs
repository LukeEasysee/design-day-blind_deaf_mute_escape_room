using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Netcode;
using Unity.Services.Authentication;
using UnityEngine;
using UnityEngine.InputSystem;

namespace EscapeRoom.Networking
{
    /// <summary>
    /// The networked player avatar. The server owns role assignment (Blind/Deaf/Mute);
    /// the owning client drives its own movement (NetworkTransform in Owner mode).
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

        public static NetworkPlayer FindByAuthId(string authId)
        {
            if (string.IsNullOrEmpty(authId)) return null;
            foreach (var p in All)
                if (p.AuthPlayerId.Value.ToString() == authId) return p;
            return null;
        }

        [SerializeField] float moveSpeed = 4f;
        [SerializeField] float lookSensitivity = 0.15f;
        [SerializeField] Camera playerCamera;

        float pitch;

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
                if (playerCamera != null)
                {
                    playerCamera.enabled = true;
                    var listener = playerCamera.GetComponent<AudioListener>();
                    if (listener) listener.enabled = true;
                }
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

        void Update()
        {
            if (!IsSpawned || !IsOwner) return;

            var kb = Keyboard.current;
            if (kb != null)
            {
                Vector2 input = Vector2.zero;
                if (kb.wKey.isPressed) input.y += 1;
                if (kb.sKey.isPressed) input.y -= 1;
                if (kb.dKey.isPressed) input.x += 1;
                if (kb.aKey.isPressed) input.x -= 1;
                Vector3 move = (transform.forward * input.y + transform.right * input.x).normalized;
                transform.position += move * (moveSpeed * Time.deltaTime);
            }

            var mouse = Mouse.current;
            if (mouse != null && mouse.rightButton.isPressed)
            {
                Vector2 delta = mouse.delta.ReadValue() * lookSensitivity;
                transform.Rotate(0f, delta.x, 0f);
                if (playerCamera != null)
                {
                    pitch = Mathf.Clamp(pitch - delta.y, -80f, 80f);
                    playerCamera.transform.localEulerAngles = new Vector3(pitch, 0f, 0f);
                }
            }
        }
    }
}
