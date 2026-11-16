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

    // Set before spawning (Netcode warns about writing a NetworkVariable then): kept here and written in
    // OnNetworkSpawn, before anyone is sent the box. The getters read these until then (also while other
    // scripts on the box spawn first).
    private int? pendingItem, pendingUnits;
    private uint? pendingPaint;

    /// <summary>The item inside this box (0 = empty).</summary>
    public int ContainedItemId => pendingItem ?? containedItemId.Value;

    /// <summary>Products left in the stock crate inside (0 = a full crate).</summary>
    public int ContainedUnits => pendingUnits ?? containedUnits.Value;

    /// <summary>Server: how many products are left in the crate inside (before or after Spawn).</summary>
    public void ServerSetUnits(int units)
    {
        units = Mathf.Max(0, units);
        if (IsSpawned) containedUnits.Value = units;
        else pendingUnits = units;
    }

    // The paint colors of the item inside (PaintSet, 0 = its own colors).
    private readonly NetworkVariable<uint> containedPaint =
        new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>The paint colors of the item inside (see PaintSet).</summary>
    public uint ContainedPaint => pendingPaint ?? containedPaint.Value;

    /// <summary>Server: the paint colors of the item inside (before or after Spawn).</summary>
    public void ServerSetPaint(uint set)
    {
        if (!PaintSet.IsValid(set)) return;
        if (IsSpawned) containedPaint.Value = set;
        else pendingPaint = set;
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

    /// <summary>The team that ordered this delivery (ulong.MaxValue if none), even after its hold runs out.</summary>
    public ulong OrderedBy => reservedTeam.Value;

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

    /// <summary>Server: set what's inside (before Spawn, it arrives with the box).</summary>
    public void ServerSetContents(int itemId)
    {
        if (IsSpawned) containedItemId.Value = itemId;
        else pendingItem = itemId;
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        if (!IsServer) return;
        if (pendingItem.HasValue) containedItemId.Value = pendingItem.Value;
        if (pendingUnits.HasValue) containedUnits.Value = pendingUnits.Value;
        if (pendingPaint.HasValue) containedPaint.Value = pendingPaint.Value;
        pendingItem = pendingUnits = null;
        pendingPaint = null;
    }
}
