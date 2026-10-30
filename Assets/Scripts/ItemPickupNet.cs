using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>A physical item in the world that can be picked up, placed and edited.</summary>
public class ItemPickupNet : NetworkBehaviour
{
    [SerializeField] public int itemId;

    /// <summary>Every spawned item on this machine (stores use it to find their contents).</summary>
    public static readonly List<ItemPickupNet> All = new();

    public override void OnNetworkSpawn() => All.Add(this);

    public override void OnNetworkDespawn() => All.Remove(this);
}
