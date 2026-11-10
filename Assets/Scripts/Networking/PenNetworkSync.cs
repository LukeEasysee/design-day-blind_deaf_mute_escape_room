using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Multiplayer for a pen/eraser (next to PenMouseController, with a NetworkObject + NetworkTransform
/// in Owner mode). Picking it up asks the server for ownership, so the holder's movement is synced
/// to everyone; only one player can hold a tool at a time.
/// </summary>
public class PenNetworkSync : NetworkBehaviour
{
    public const ulong Nobody = ulong.MaxValue;

    /// <summary>Client id of the player holding this tool, or <see cref="Nobody"/>.</summary>
    public readonly NetworkVariable<ulong> HeldBy = new(Nobody,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public bool IsHeldByOther => HeldBy.Value != Nobody && HeldBy.Value != NetworkManager.LocalClientId;

    public void RequestHold() => HoldRpc();
    public void Release() => ReleaseRpc();

    [Rpc(SendTo.Server)]
    void HoldRpc(RpcParams rpc = default)
    {
        ulong sender = rpc.Receive.SenderClientId;
        if (HeldBy.Value != Nobody && HeldBy.Value != sender) return;
        if (OwnerClientId != sender) NetworkObject.ChangeOwnership(sender);
        HeldBy.Value = sender;
    }

    [Rpc(SendTo.Server)]
    void ReleaseRpc(RpcParams rpc = default)
    {
        if (HeldBy.Value == rpc.Receive.SenderClientId) HeldBy.Value = Nobody;
    }
}
