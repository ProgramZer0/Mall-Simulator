using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// A physical item in the world that can be picked up, placed and edited.
///
/// Edit mode: while the store an item sits in is being edited, the host freezes the item's
/// Rigidbody (no gravity, nothing can knock it around) unless the player switched gravity on
/// for it. Leaving edit mode gives every item its gravity back.
///
/// Prefab needs: NetworkObject, NetworkTransform and NetworkRigidbody (so the host's physics
/// and edit-mode moves reach every player), plus a Rigidbody and colliders.
/// </summary>
public class ItemPickupNet : NetworkBehaviour
{
    [SerializeField] public int itemId;

    /// <summary>Every spawned item on this machine.</summary>
    public static readonly List<ItemPickupNet> All = new();

    // Player chose "gravity on" for this item during the current edit session.
    private readonly NetworkVariable<bool> gravityInEdit =
        new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private Rigidbody rb;
    private bool baseKinematic;

    public bool GravityInEdit => gravityInEdit.Value;
    public Rigidbody Body => rb;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        if (rb != null) baseKinematic = rb.isKinematic;
    }

    public override void OnNetworkSpawn()
    {
        All.Add(this);
        // Placed straight into a store that's mid-edit? Start frozen.
        if (IsServer) ServerRefreshPhysics();
    }

    public override void OnNetworkDespawn()
    {
        All.Remove(this);
    }

    /* ========================= SERVER ========================= */

    public void ServerSetGravityInEdit(bool on)
    {
        if (!IsServer) return;
        gravityInEdit.Value = on;
        ServerRefreshPhysics();
    }

    /// <summary>Server: freeze or unfreeze this item based on its store's edit state.</summary>
    public void ServerRefreshPhysics()
    {
        if (!IsServer || rb == null) return;

        var plot = MallManager.Instance != null ? MallManager.Instance.GetPlotAt(transform.position) : null;
        bool frozen = plot != null && plot.IsRenovating && !gravityInEdit.Value;
        bool kinematic = baseKinematic || frozen;

        if (kinematic && !rb.isKinematic)
        {
            // Stop it dead before freezing, so it doesn't resume its old motion later.
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
        rb.isKinematic = kinematic;
        if (!kinematic) rb.WakeUp();
    }

    /// <summary>Server: move the item to an edit-mode pose (already validated by the caller).</summary>
    public void ServerApplyEditPose(Vector3 position, Quaternion rotation)
    {
        if (!IsServer) return;

        transform.SetPositionAndRotation(position, rotation);
        if (rb != null)
        {
            rb.position = position;
            rb.rotation = rotation;
            if (!rb.isKinematic)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }
    }
}
