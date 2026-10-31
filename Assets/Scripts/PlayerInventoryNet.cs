using ProgramZer0.SoundSystem;
using Unity.Netcode;
using UnityEngine;

public class PlayerInventoryNet : NetworkBehaviour
{
    [SerializeField] private Transform handAnchor;

    public NetworkVariable<int> HeldItemId = new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private GameObject currentHandVisual;
    private bool handVisualHidden;
    private PlayerInteractInput interact;

    private void Awake()
    {
        interact = GetComponent<PlayerInteractInput>();
    }

    /// <summary>
    /// Local-only: hides the held item's hand visual on this client (e.g. while
    /// previewing a placement). Other players still see the item in hand.
    /// </summary>
    public void SetHandVisualHidden(bool hidden)
    {
        if (handVisualHidden == hidden) return;
        handVisualHidden = hidden;
        ApplyHandVisibility();
    }

    private void ApplyHandVisibility()
    {
        if (currentHandVisual) currentHandVisual.SetActive(!handVisualHidden);
    }

    private void OnEnable()
    {
        HeldItemId.OnValueChanged += OnHeldItemChanged;
    }
    private void OnDisable()
    {
        HeldItemId.OnValueChanged -= OnHeldItemChanged;
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        OnHeldItemChanged(0, HeldItemId.Value);
    }

    private void OnHeldItemChanged(int prev, int next)
    {
        if (currentHandVisual) Destroy(currentHandVisual);

        // A new held item always starts visible; an active preview re-hides it next frame.
        handVisualHidden = false;

        if (next == 0) return;
        if (ItemCatalog.Instance != null && ItemCatalog.Instance.TryGet(next, out var def) && def.handVisualPrefab != null)
        {
            currentHandVisual = Instantiate(def.handVisualPrefab, handAnchor);
            currentHandVisual.transform.localPosition = Vector3.zero;
            currentHandVisual.transform.localRotation = Quaternion.identity;
        }
    }


    [ServerRpc]
    public void RequestPickupServerRpc(NetworkObjectReference pickupRef, ServerRpcParams rpcParams = default)
    {
        if (!pickupRef.TryGet(out NetworkObject pickupNO)) return;
        var pickup = pickupNO.GetComponent<ItemPickupNet>();
        if (pickup == null) return;

        var actor = NetworkManager.Singleton.ConnectedClients[rpcParams.Receive.SenderClientId].PlayerObject;
        if (Vector3.Distance(actor.transform.position, pickup.transform.position) > 3.0f) return;

        // Store rules: only from inside your own store, only items inside your own store.
        if (!MallManager.CanPickupOrPlace(rpcParams.Receive.SenderClientId,
                actor.transform.position, pickup.transform.position)) return;

        // Deliveries are held for the team that ordered them for a while.
        if (pickup.IsReservedAgainst(MallManager.GetTeamOf(rpcParams.Receive.SenderClientId))) return;

        if (HeldItemId.Value != 0) return;

        int itemId = pickup.itemId;
        HeldItemId.Value = itemId;

        Vector3 soundPos = pickup.transform.position;
        pickupNO.Despawn(true);

        PlayItemSoundClientRpc(itemId, soundPos, false);
    }

    [ServerRpc]
    public void RequestPlaceServerRpc(Vector3 position, Quaternion rotation, ServerRpcParams rpcParams = default)
    {
        if (HeldItemId.Value == 0) return;
        int id = HeldItemId.Value;

        var actor = NetworkManager.Singleton.ConnectedClients[rpcParams.Receive.SenderClientId].PlayerObject;
        if (Vector3.Distance(actor.transform.position, position) > 5.0f) return;

        // Store rules: only from inside your own store, only onto a spot inside your own store.
        if (!MallManager.CanPickupOrPlace(rpcParams.Receive.SenderClientId,
                actor.transform.position, position)) return;

        if (!ItemCatalog.Instance.TryGet(id, out var def) || def.worldNetworkPrefab == null) return;

        // Server re-runs the same fit test the client used, so a client can't place into
        // walls/other items, and two players can't drop things into the same spot at once.
        if (interact == null || !interact.FitsAt(id, position, rotation)) return;

        var spawned = Instantiate(def.worldNetworkPrefab, position, rotation);
        spawned.Spawn(true);

        HeldItemId.Value = 0;

        PlayItemSoundClientRpc(id, position, true);
    }

    /// <summary>
    /// Sent by the server only after a pickup/place actually succeeds, so every player
    /// (including the one acting) hears it at the item's spot, and rejected attempts are silent.
    /// Only the item id travels over the network; each client looks up the item's own clip.
    /// </summary>
    [ClientRpc]
    private void PlayItemSoundClientRpc(int itemId, Vector3 position, bool placed)
    {
        if (ItemCatalog.Instance == null || !ItemCatalog.Instance.TryGet(itemId, out var def)) return;
        WorldAudio.PlayOneShot(placed ? def.PlaceClip : def.PickupClip, position);
    }

    [ServerRpc]
    public void RequestUseOnServerRpc(NetworkObjectReference targetRef, ServerRpcParams rpcParams = default)
    {
        if (HeldItemId.Value == 0) return;
        if (!targetRef.TryGet(out NetworkObject targetNO)) return;
        var interactable = targetNO.GetComponent<InteractableNet>();
        if (interactable == null) return;

        var actor = NetworkManager.Singleton.ConnectedClients[rpcParams.Receive.SenderClientId].PlayerObject;
        if (Vector3.Distance(actor.transform.position, targetNO.transform.position) > 4.0f) return;

        // Store rules: never inside another player's store; carts etc. only in your own store or a dock.
        if (!MallManager.CanInteract(rpcParams.Receive.SenderClientId, targetNO.transform.position,
                interactable.OnlyInOwnZones)) return;

        bool consumed = interactable.ServerTryInteract(HeldItemId.Value, rpcParams.Receive.SenderClientId);

        if (consumed)
        {
            HeldItemId.Value = 0;
        }
    }
}