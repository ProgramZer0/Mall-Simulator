using ProgramZer0.SoundSystem;
using Unity.Netcode;
using UnityEngine;

public class PlayerInventoryNet : NetworkBehaviour
{
    [SerializeField] private Transform handAnchor;
    [Tooltip("What pushes back on the item in your hands, so it can't poke through (nothing ticked = the \"walls\" layer). " +
             "Leave shelves and counters out, or they'll shove you away while stocking.")]
    [SerializeField] private LayerMask heldItemBlockedBy;

    /// <summary>Layers that push back on held things (the price scanner uses it too).</summary>
    public int HeldItemBlockedBy => heldItemBlockedBy.value;

    /// <summary>Where held things show in the hand (the price scanner uses it too).</summary>
    public Transform HandAnchor => handAnchor;

    public NetworkVariable<int> HeldItemId = new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>Products left in the held stock crate (0 when not holding one).</summary>
    public NetworkVariable<int> HeldUnits = new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>Paint colors of the held item (PaintSet, 0 = its own colors); it keeps them when put down.</summary>
    public NetworkVariable<uint> HeldPaint = new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

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
        HeldPaint.OnValueChanged += OnHeldPaintChanged;
    }
    private void OnDisable()
    {
        HeldItemId.OnValueChanged -= OnHeldItemChanged;
        HeldPaint.OnValueChanged -= OnHeldPaintChanged;
    }

    // The hand model shows the held item's paint, if it has PaintParts of its own.
    private void OnHeldPaintChanged(uint before, uint now) => PaintHandVisual();

    private void PaintHandVisual()
    {
        if (currentHandVisual != null) PaintParts.ApplySet(currentHandVisual, HeldPaint.Value);
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
            PaintHandVisual();
            // Your own held item gets a body, so walls push you back instead of it poking through.
            if (IsOwner) HeldItemBlocker.Add(currentHandVisual, transform, heldItemBlockedBy.value);
        }
    }


    [ServerRpc]
    public void RequestPickupServerRpc(NetworkObjectReference pickupRef, ServerRpcParams rpcParams = default)
    {
        if (!pickupRef.TryGet(out NetworkObject pickupNO)) return;
        var pickup = pickupNO.GetComponent<ItemPickupNet>();
        if (pickup == null || pickup.EditModeOnly) return; // e.g. the shop monitor: edit mode only

        var actor = NetworkManager.Singleton.ConnectedClients[rpcParams.Receive.SenderClientId].PlayerObject;
        if (DistanceTo(pickupNO.gameObject, actor.transform.position) > 3.0f) return;

        // Store rules: only from inside your own store, only items inside your own store.
        if (!MallManager.CanPickupOrPlace(rpcParams.Receive.SenderClientId,
                actor.transform.position, pickup.transform.position)) return;

        // Deliveries are held for the team that ordered them for a while.
        // Stacked on a cart: the things on top come off first (nothing is left floating).
        if (pickup.HasItemOnTopOnCart()) return;

        // A shelf with stock on it has to be emptied first.
        if (pickupNO.TryGetComponent<StockContainer>(out var container) && container.HasStock) return;

        // A checkout counter with a customer at it waits until they're served.
        if (pickupNO.TryGetComponent<CheckoutCounter>(out var counter) && counter.IsBusy) return;

        bool isBox = pickupNO.TryGetComponent<ShipmentPackage>(out var box);
        if (isBox && box.IsReservedAgainst(MallManager.GetTeamOf(rpcParams.Receive.SenderClientId))) return;

        if (HeldItemId.Value != 0) return;

        // A delivery box gives you the item inside it.
        int itemId = isBox ? box.ContainedItemId : pickup.itemId;
        if (itemId == 0) return;
        HeldItemId.Value = itemId;
        HeldPaint.Value = isBox ? box.ContainedPaint : pickup.Paint;
        // A crate on the floor remembers how much is left; one from a delivery box is full.
        int perCrate = StockContainer.GetUnitsPerCrate(itemId);
        HeldUnits.Value = perCrate == 0 ? 0
            : isBox ? (box.ContainedUnits > 0 ? Mathf.Min(box.ContainedUnits, perCrate) : perCrate)
            : pickup.Units;

        Vector3 soundPos = pickup.transform.position;
        pickupNO.Despawn(true);

        PlayItemSoundClientRpc(itemId, soundPos, false);
    }

    /// <summary>Put one product from the held stock crate into a container's zone.</summary>
    [ServerRpc]
    public void RequestStockServerRpc(NetworkObjectReference containerRef, int zone, int orient, ServerRpcParams rpcParams = default)
    {
        int id = HeldItemId.Value;
        if (id == 0 || !StockContainer.IsStockCrate(id)) return;
        if (!TryGetContainerZone(containerRef, zone, rpcParams.Receive.SenderClientId, out var container, out var at)) return;

        // A shelf still settling after being put down stops where it is first, so its stock can't send it flying.
        if (container.TryGetComponent<ItemPickupNet>(out var shelf)) shelf.ServerFreezeNow();

        if (!container.ServerAddOne(zone, id, orient)) return; // full, holds something else, or wrong kind of zone

        // The empty crate goes away.
        int left = Mathf.Max(1, HeldUnits.Value) - 1;
        HeldUnits.Value = left;
        if (left == 0) { HeldItemId.Value = 0; HeldPaint.Value = 0; }
        PlayItemSoundClientRpc(id, at, true);
    }

    /// <summary>
    /// Take one product out of a container's zone: into empty hands (as a crate holding one),
    /// or back into a held crate of the same product that has room.
    /// </summary>
    [ServerRpc]
    public void RequestUnstockServerRpc(NetworkObjectReference containerRef, int zone, ServerRpcParams rpcParams = default)
    {
        if (!TryGetContainerZone(containerRef, zone, rpcParams.Receive.SenderClientId, out var container, out var at)) return;

        int id = container.GetZone(zone).itemId;
        if (id == 0) return;

        bool emptyHands = HeldItemId.Value == 0;
        bool roomInCrate = HeldItemId.Value == id && HeldUnits.Value < StockContainer.GetUnitsPerCrate(id);
        if (!emptyHands && !roomInCrate) return;
        if (container.ServerRemoveOne(zone) != id) return;

        if (emptyHands)
        {
            HeldItemId.Value = id;
            HeldPaint.Value = 0;
            HeldUnits.Value = 1;
        }
        else HeldUnits.Value++;

        PlayItemSoundClientRpc(id, at, false);
    }

    /// <summary>Change which way a zone's products face / whether they lie down (if they all still fit).</summary>
    [ServerRpc]
    public void RequestOrientZoneServerRpc(NetworkObjectReference containerRef, int zone, int orient, ServerRpcParams rpcParams = default)
    {
        if (!TryGetContainerZone(containerRef, zone, rpcParams.Receive.SenderClientId, out var container, out _)) return;
        container.ServerSetOrientation(zone, orient);
    }

    // Same rules as picking up and placing: close enough, standing in your own store, container in it too.
    private static bool TryGetContainerZone(NetworkObjectReference containerRef, int zone, ulong sender,
                                            out StockContainer container, out Vector3 at)
    {
        at = default;
        container = null;
        if (!containerRef.TryGet(out NetworkObject no) || !no.TryGetComponent(out container)) return false;
        if (zone < 0 || zone >= container.ZoneCount) return false;

        at = container.GetZoneCenter(zone);
        var actor = NetworkManager.Singleton.ConnectedClients[sender].PlayerObject;
        if (actor == null || Vector3.Distance(actor.transform.position, at) > 4.5f) return false;
        return MallManager.CanPickupOrPlace(sender, actor.transform.position, at);
    }

    [ServerRpc]
    public void RequestPlaceServerRpc(Vector3 position, Quaternion rotation, bool onWall, bool onCart,
                                      ServerRpcParams rpcParams = default)
    {
        if (HeldItemId.Value == 0) return;
        int id = HeldItemId.Value;

        var actor = NetworkManager.Singleton.ConnectedClients[rpcParams.Receive.SenderClientId].PlayerObject;
        if (Vector3.Distance(actor.transform.position, position) > 5.0f) return;

        // Store rules: only from inside your own store, only onto a spot inside your own store.
        if (!MallManager.CanPickupOrPlace(rpcParams.Receive.SenderClientId,
                actor.transform.position, position)) return;

        // Can't park things on another store's shipment spots, or anywhere at the docks (except on a cart).
        if (MallManager.IsOthersShipmentArea(rpcParams.Receive.SenderClientId, position)) return;
        if (MallManager.IsNoPlaceZone(position)) return;

        if (!ItemCatalog.Instance.TryGet(id, out var def) || def.worldNetworkPrefab == null) return;

        // Set down on a cart: it goes on packed in its shipment box, to save room.
        var cartBox = onCart ? PlacementCheck.CartBoxOf(def) : null;
        if (cartBox != null)
        {
            ServerPlaceBoxOnCart(def, cartBox, position, rotation, rpcParams.Receive.SenderClientId);
            return;
        }

        // Server re-runs the same fit test the client used, so a client can't place into
        // walls/other items, and two players can't drop things into the same spot at once.
        // Only wall items go on walls, and wall-only items only on walls.
        var mounting = PlacementCheck.GetMounting(def);
        if (onWall ? mounting == ItemPickupNet.Mounting.Floor : mounting == ItemPickupNet.Mounting.Wall && !onCart) return;

        if (interact == null || !interact.FitsAt(id, position, rotation, onWall)) return;

        // Set down on a cart: it rides along from the start (no falling, so it can't be left behind).
        CartNet cart = null;
        if (!onWall && PlacementCheck.GetTestBox(def, position, rotation, out Vector3 center, out _))
            cart = CartNet.FindCartAt(center);

        // Set down on something that's still settling (a shelf just put down)? It stops where it is first,
        // so the two can't knock each other flying.
        if (!onWall && cart == null) FreezeWhatsBelow(def, position, rotation);

        var spawned = Instantiate(def.worldNetworkPrefab, position, rotation);
        if (onWall && spawned.TryGetComponent<ItemPickupNet>(out var hung))
            hung.ServerPrepareFrozen(onWall: true); // hangs there; never falls
        else if (cart != null && spawned.TryGetComponent<ItemPickupNet>(out var load))
            load.ServerPrepareFrozen(carried: true);
        if (HeldUnits.Value > 0 && spawned.TryGetComponent<ItemPickupNet>(out var crate))
            crate.ServerSetUnits(HeldUnits.Value); // a part-used crate stays part-used
        if (HeldPaint.Value != 0 && spawned.TryGetComponent<ItemPickupNet>(out var painted))
            painted.ServerSetPaint(HeldPaint.Value);
        // Put down in a store that priced all of these: it gets that price.
        if (cart == null && spawned.TryGetComponent<Sellable>(out var sellable) && sellable.SellWhenLoose &&
            StorePlot.TryGetStorePriceAt(position, id, true, out int storePrice))
            sellable.ServerSetPrice(storePrice);
        spawned.Spawn(true);
        if (cart != null) cart.ServerLoad(spawned);

        HeldItemId.Value = 0;
        HeldPaint.Value = 0;
        HeldUnits.Value = 0;

        PlayItemSoundClientRpc(id, position, true);
    }

    /// <summary>
    /// How far a player is from the nearest part of a thing (its colliders), not from its pivot: a long
    /// counter can be reached from either end.
    /// </summary>
    private static float DistanceTo(GameObject target, Vector3 from)
    {
        float best = Vector3.Distance(from, target.transform.position);
        foreach (var col in target.GetComponentsInChildren<Collider>())
        {
            if (!col.enabled || col.isTrigger) continue;
            // ClosestPoint only works on convex shapes; a plain mesh collider uses its bounds.
            Vector3 p = col is MeshCollider mesh && !mesh.convex ? col.bounds.ClosestPoint(from) : col.ClosestPoint(from);
            best = Mathf.Min(best, Vector3.Distance(from, p));
        }
        return best;
    }

    private static readonly Collider[] BelowHits = new Collider[16];

    // Server: freeze any still-settling items just under where this one is being put.
    private static void FreezeWhatsBelow(ItemDef def, Vector3 position, Quaternion rotation)
    {
        if (!PlacementCheck.GetTestBox(def, position, rotation, out Vector3 center, out Vector3 half)) return;
        // The item's footprint, reaching a little below it.
        Vector3 probeHalf = new(half.x, 0.06f, half.z);
        Vector3 probeCenter = center - rotation * new Vector3(0f, half.y, 0f);
        int n = Physics.OverlapBoxNonAlloc(probeCenter, probeHalf, BelowHits, rotation, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var below = BelowHits[i].GetComponentInParent<ItemPickupNet>();
            if (below != null) below.ServerFreezeNow();
        }
    }

    private void ServerPlaceBoxOnCart(ItemDef def, NetworkObject boxPrefab, Vector3 position, Quaternion rotation, ulong sender)
    {
        var cart = CartNet.FindCartAt(PlacementCheck.PrefabCenter(boxPrefab.gameObject, position, rotation));
        if (cart == null || !cart.CanUse(sender)) return;
        if (interact == null || !interact.FitsBoxAt(boxPrefab.gameObject, position, rotation)) return;

        int id = def.id;
        var spawned = Instantiate(boxPrefab, position, rotation);
        if (spawned.TryGetComponent<ShipmentPackage>(out var box))
        {
            box.ServerSetContents(id);
            if (HeldUnits.Value > 0) box.ServerSetUnits(HeldUnits.Value); // a part-used crate stays part-used
            box.ServerSetPaint(HeldPaint.Value);
        }
        else Debug.LogWarning($"[PlayerInventoryNet] Shipment prefab '{boxPrefab.name}' has no ShipmentPackage, so " +
                              "picking it up won't give the item inside.", boxPrefab);
        if (spawned.TryGetComponent<ItemPickupNet>(out var pickup)) pickup.ServerPrepareFrozen(carried: true);
        spawned.Spawn(true);
        cart.ServerLoad(spawned);

        HeldItemId.Value = 0;
        HeldPaint.Value = 0;
        HeldUnits.Value = 0;
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
        if (DistanceTo(targetNO.gameObject, actor.transform.position) > 4.0f) return;

        // Store rules: never inside another player's store; carts etc. only in your own store or a dock.
        if (!MallManager.CanInteract(rpcParams.Receive.SenderClientId, targetNO.transform.position,
                interactable.OnlyInOwnZones)) return;

        bool consumed = interactable.ServerTryInteract(HeldItemId.Value, rpcParams.Receive.SenderClientId);

        if (consumed)
        {
            HeldItemId.Value = 0;
            HeldPaint.Value = 0;
            HeldUnits.Value = 0;
        }
    }
}