using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>A physical item in the world that can be picked up, placed and edited.</summary>
public class ItemPickupNet : NetworkBehaviour
{
    [SerializeField] public int itemId;

    /// <summary>Every spawned item on this machine (stores use it to find their contents).</summary>
    public static readonly List<ItemPickupNet> All = new();

    // Deliveries are held for the team that ordered them for a while (GDD 13a).
    private readonly NetworkVariable<ulong> reservedTeam =
        new(ulong.MaxValue, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<double> reservedUntil =
        new(0d, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>The team this delivery is held for, if the hold hasn't run out.</summary>
    public bool TryGetReservation(out ulong teamId, out double secondsLeft)
    {
        teamId = reservedTeam.Value;
        secondsLeft = IsSpawned ? reservedUntil.Value - NetworkManager.ServerTime.Time : 0d;
        return teamId != ulong.MaxValue && secondsLeft > 0d;
    }

    /// <summary>True if this is a delivery still held for a different team.</summary>
    public bool IsReservedAgainst(ulong teamId) =>
        TryGetReservation(out ulong holder, out _) && holder != teamId;

    /// <summary>Server: hold this item for a team for some seconds; after that anyone may take it.</summary>
    public void ServerReserve(ulong teamId, float seconds)
    {
        if (!IsServer) return;
        reservedTeam.Value = teamId;
        reservedUntil.Value = NetworkManager.ServerTime.Time + seconds;
    }

    public override void OnNetworkSpawn() => All.Add(this);

    public override void OnNetworkDespawn() => All.Remove(this);
}
