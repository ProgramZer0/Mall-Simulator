using Unity.Netcode;
using UnityEngine;

/// <summary>
/// A delivery box. Put this on a shipment prefab together with a NetworkObject and ItemPickupNet
/// (and add the prefab to the NetworkManager's prefab list).
///
/// The box knows which item is inside. Picking it up puts that item in your hand; placing it puts
/// down the real item, so the box unpacks itself when you set it down in your store.
/// </summary>
public class ShipmentPackage : NetworkBehaviour
{
    private readonly NetworkVariable<int> containedItemId =
        new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // Products left, if what's inside is a part-used stock crate packed onto a cart (0 = full).
    private readonly NetworkVariable<int> containedUnits =
        new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>The item inside this box (0 = empty).</summary>
    public int ContainedItemId => containedItemId.Value;

    /// <summary>Products left in the stock crate inside (0 = a full crate).</summary>
    public int ContainedUnits => containedUnits.Value;

    /// <summary>Server: how many products are left in the crate inside. Call before Spawn.</summary>
    public void ServerSetUnits(int units) => containedUnits.Value = Mathf.Max(0, units);

    // The paint color of the item inside (PaintPalette id, 0 = its own colors).
    private readonly NetworkVariable<byte> containedPaint =
        new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>The paint color of the item inside (0 = its own colors).</summary>
    public byte ContainedPaint => containedPaint.Value;

    /// <summary>Server: the paint color of the item inside. Call before Spawn.</summary>
    public void ServerSetPaint(byte id)
    {
        if (PaintPalette.IsValid(id)) containedPaint.Value = id;
    }

    // Deliveries are held for the team that ordered them for a while (GDD 13a). Only boxes carry
    // this, so ordinary items don't pay for two extra synced values each.
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

    /// <summary>True if this delivery is still held for a different team.</summary>
    public bool IsReservedAgainst(ulong teamId) =>
        TryGetReservation(out ulong holder, out _) && holder != teamId;

    /// <summary>Server: hold this box for a team for some seconds; after that anyone may take it.</summary>
    public void ServerReserve(ulong teamId, float seconds)
    {
        if (!IsServer) return;
        reservedTeam.Value = teamId;
        reservedUntil.Value = NetworkManager.ServerTime.Time + seconds;
    }

    /// <summary>Server: set what's inside. Call before Spawn so it arrives with the box.</summary>
    public void ServerSetContents(int itemId)
    {
        containedItemId.Value = itemId;
    }
}
