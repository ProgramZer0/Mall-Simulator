using Unity.Netcode;
using UnityEngine;

public class PlayerInventoryNet : NetworkBehaviour
{
    [SerializeField] private Transform handAnchor;

    public NetworkVariable<int> HeldItemId = new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private GameObject currentHandVisual;

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

        if (HeldItemId.Value != 0) return;

        HeldItemId.Value = pickup.itemId;

        pickupNO.Despawn(true);
    }

    [ServerRpc]
    public void RequestPlaceServerRpc(Vector3 position, Quaternion rotation, ServerRpcParams rpcParams = default)
    {
        if (HeldItemId.Value == 0) return;
        int id = HeldItemId.Value;

        var actor = NetworkManager.Singleton.ConnectedClients[rpcParams.Receive.SenderClientId].PlayerObject;
        if (Vector3.Distance(actor.transform.position, position) > 5.0f) return;

        if (!ItemCatalog.Instance.TryGet(id, out var def) || def.worldNetworkPrefab == null) return;

        var spawned = Instantiate(def.worldNetworkPrefab, position, rotation);
        spawned.Spawn(true);

        HeldItemId.Value = 0;
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

        bool consumed = interactable.ServerTryInteract(HeldItemId.Value, rpcParams.Receive.SenderClientId);

        if (consumed)
        {
            HeldItemId.Value = 0;
        }
    }
}