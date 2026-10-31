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

    /// <summary>The item inside this box (0 = empty).</summary>
    public int ContainedItemId => containedItemId.Value;

    /// <summary>Server: set what's inside. Call before Spawn so it arrives with the box.</summary>
    public void ServerSetContents(int itemId)
    {
        containedItemId.Value = itemId;
    }
}
