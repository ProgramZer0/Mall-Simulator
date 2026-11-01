using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>A physical item in the world that can be picked up, placed and edited.</summary>
public class ItemPickupNet : NetworkBehaviour
{
    [SerializeField] public int itemId;
    [Tooltip("Can't be picked up in normal play, only moved in store edit mode (e.g. the shop monitor).")]
    [SerializeField] private bool editModeOnly;

    public bool EditModeOnly => editModeOnly;

    /// <summary>Every spawned item on this machine (stores use it to find their contents).</summary>
    public static readonly List<ItemPickupNet> All = new();

    [Tooltip("Let this item run physics (fall, get pushed). Off = it stays exactly where it was put.\n" +
             "Each machine simulates physics on its own and items have no NetworkTransform, so a " +
             "physics item can end up in different places for different players. Leave this off " +
             "unless you add a NetworkTransform too.")]
    [SerializeField] private bool simulatePhysics;

    private void Awake()
    {
        // Placed items hold still on every machine, so everyone sees them in the same spot.
        if (!simulatePhysics && TryGetComponent<Rigidbody>(out var rb)) rb.isKinematic = true;
    }

    public override void OnNetworkSpawn() => All.Add(this);

    public override void OnNetworkDespawn() => All.Remove(this);
}
