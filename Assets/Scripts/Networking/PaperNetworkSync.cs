using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Multiplayer for the paper (next to DrawingSurface, with a NetworkObject):
///  - every stroke drawn locally is sent to the other players and applied on their copy;
///  - the "sent through the wall" state is a server value, and each client animates the move
///    (SendPaperButton listens to <see cref="SentChanged"/>), so the paper is in the same place for everyone.
/// Without a network session the paper works exactly as before (local only).
/// </summary>
[RequireComponent(typeof(DrawingSurface))]
public class PaperNetworkSync : NetworkBehaviour
{
    public readonly NetworkVariable<bool> Sent = new(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>Raised on every client when the paper should move (true = sent to the other side).</summary>
    public event Action<bool> SentChanged;

    DrawingSurface surface;

    void Awake() => surface = GetComponent<DrawingSurface>();

    public override void OnNetworkSpawn()
    {
        surface.Drawn += OnLocalStroke;
        Sent.OnValueChanged += OnSentChanged;
    }

    public override void OnNetworkDespawn()
    {
        surface.Drawn -= OnLocalStroke;
        Sent.OnValueChanged -= OnSentChanged;
    }

    void OnSentChanged(bool previous, bool current) => SentChanged?.Invoke(current);

    // ---------- Paper position ----------
    public void RequestToggle() => ToggleRpc();

    [Rpc(SendTo.Server)]
    void ToggleRpc() => Sent.Value = !Sent.Value;

    // ---------- Drawing ----------
    void OnLocalStroke(Vector2 from, Vector2 to, int radius, Color32 color, bool isDot)
    {
        if (IsSpawned) StrokeRpc(from, to, radius, color, isDot);
    }

    [Rpc(SendTo.NotMe)]
    void StrokeRpc(Vector2 from, Vector2 to, int radius, Color32 color, bool isDot)
        => surface.ApplyRemoteStroke(from, to, radius, color, isDot);
}
