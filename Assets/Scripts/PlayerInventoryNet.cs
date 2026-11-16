using System.Collections.Generic;
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
    /// <summary>
    /// The team whose delivery the held thing came out of (ulong.MaxValue: none, or your own). It can be
    /// dropped off in their store (MallManager.IsDeliveringTo).
    /// </summary>
    public NetworkVariable<ulong> HeldFromTeam = new(ulong.MaxValue, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>
    /// What's held came out of a delivery box. Set down at a dock or a shipment area, or dropped off in
    /// the store that ordered it, it goes back in its box (so a box picked up by mistake can be put
    /// back, and deliveries carried for someone arrive still boxed). Anywhere else it comes out.
    /// </summary>
    public NetworkVariable<bool> HeldBoxed = new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private ulong heldOrderedBy = ulong.MaxValue; // server: the team that ordered the box it came from

    private GameObject currentHandVisual;
    private bool handVisualHidden;
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

    [Tooltip("How see-through the held item is while placing it (0 = invisible, 1 = solid). While stocking it's hidden.")]
    [SerializeField, Range(0f, 1f)] private float handFadeAlpha = 0.25f;
    private bool handFaded;
    private readonly List<(Renderer r, Material[] solid)> fadedRenderers = new();
    private static readonly Dictionary<(Material, float), Material> FadedCopies = new();

    /// <summary>
    /// Local-only: the held item turns see-through (Hand Fade Alpha) so you can see where you're
    /// placing or stocking. Other players still see it solid.
    /// </summary>
    public void SetHandVisualFaded(bool faded)
    {
        if (handFaded == faded) return;
        handFaded = faded;
        ApplyHandFade();
    }

    private void ApplyHandFade()
    {
        // Back to solid first (also drops renderers of a hand model that's gone).
        foreach (var (r, solid) in fadedRenderers)
            if (r != null) r.sharedMaterials = solid;
        fadedRenderers.Clear();
        if (!handFaded || currentHandVisual == null) return;

        foreach (var r in currentHandVisual.GetComponentsInChildren<Renderer>(true))
        {
            var solid = r.sharedMaterials;
            var see = new Material[solid.Length];
            for (int i = 0; i < solid.Length; i++)
            {
                if (solid[i] == null) continue;
                // One see-through copy per material, shared by everything that uses it.
                if (!FadedCopies.TryGetValue((solid[i], handFadeAlpha), out var copy) || copy == null)
                    FadedCopies[(solid[i], handFadeAlpha)] = copy = GhostBuilder.MakeTransparent(solid[i], handFadeAlpha);
                see[i] = copy;
            }
            fadedRenderers.Add((r, solid));
            r.sharedMaterials = see;
        }
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

    // The hand model shows the held item's paint, if it has PaintParts of its own (not a stock crate's:
    // its paint is its products' color).
    private void OnHeldPaintChanged(uint before, uint now) => PaintHandVisual();

    private void PaintHandVisual()
    {
        if (currentHandVisual != null)
            PaintParts.ApplySet(currentHandVisual, StockContainer.IsStockCrate(HeldItemId.Value) ? 0u : HeldPaint.Value);
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        OnHeldItemChanged(0, HeldItemId.Value);
    }

    private void OnHeldItemChanged(int prev, int next)
    {
        if (IsServer && next == 0 && HeldFromTeam.Value != ulong.MaxValue) HeldFromTeam.Value = ulong.MaxValue;
        if (IsServer && next == 0 && HeldBoxed.Value) HeldBoxed.Value = false;
        if (currentHandVisual) Destroy(currentHandVisual);

        // A new held item always starts visible; an active preview re-hides it next frame.
        handVisualHidden = false;

        if (next == 0) return;
        if (ItemCatalog.Instance != null && ItemCatalog.Instance.TryGet(next, out var def) && def.HandVisualPrefab != null)
        {
            currentHandVisual = Instantiate(def.HandVisualPrefab, handAnchor);
            currentHandVisual.transform.localPosition = Vector3.zero;
            currentHandVisual.transform.localRotation = Quaternion.identity;
            PaintHandVisual();
            if (handFaded) ApplyHandFade(); // still placing / stocking: the new one starts see-through too
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

        // Already holding a crate of this with room: it tops that up instead.
        if (HeldItemId.Value != 0)
        {
            ServerTopUp(pickup, isBox ? box : null, rpcParams.Receive.SenderClientId);
            return;
        }

        // A delivery box gives you the item inside it.
        int itemId = isBox ? box.ContainedItemId : pickup.ItemId;
        ItemDebug.Log($"Pick up by client {rpcParams.Receive.SenderClientId}: '{pickup.name}' " +
                      $"{(isBox ? "(a box holding " : "(")}{ItemDebug.Describe(itemId)}).", pickup);
        if (itemId == 0) return;
        HeldItemId.Value = itemId;
        HeldPaint.Value = isBox ? box.ContainedPaint : pickup.Paint;
        ulong pickerTeam = MallManager.GetTeamOf(rpcParams.Receive.SenderClientId);
        HeldFromTeam.Value = isBox && box.OrderedBy != pickerTeam ? box.OrderedBy : ulong.MaxValue;
        HeldBoxed.Value = isBox;
        heldOrderedBy = isBox ? box.OrderedBy : ulong.MaxValue;
        // A crate on the floor remembers how much is left; one from a delivery box is full.
        int perCrate = StockContainer.GetUnitsPerCrate(itemId);
        HeldUnits.Value = perCrate == 0 ? 0
            : isBox ? (box.ContainedUnits > 0 ? Mathf.Min(box.ContainedUnits, perCrate) : perCrate)
            : pickup.Units;

        Vector3 soundPos = pickup.transform.position;
        pickupNO.Despawn(true);

        PlayItemSoundClientRpc(itemId, soundPos, false);
    }

    /// <summary>Holding a stock crate that isn't full (and isn't someone else's delivery)?</summary>
    public bool CanTopUpHeldCrate
    {
        get
        {
            int held = HeldItemId.Value;
            if (held == 0 || HeldFromTeam.Value != ulong.MaxValue) return false;
            int per = StockContainer.GetUnitsPerCrate(held);
            return per > 0 && HeldUnits.Value < per;
        }
    }

    /// <summary>
    /// How many products this could give the held crate: a crate of the same product (same colors) on the
    /// floor, or a delivery box of it (not another team's). 0: it can't.
    /// </summary>
    public int UnitsToTopUpFrom(ItemPickupNet pickup)
    {
        if (pickup == null || !CanTopUpHeldCrate) return 0;
        int held = HeldItemId.Value, per = StockContainer.GetUnitsPerCrate(held);
        if (pickup.TryGetComponent<ShipmentPackage>(out var box))
        {
            if (box.ContainedItemId != held || box.ContainedPaint != HeldPaint.Value) return 0;
            if (box.OrderedBy != ulong.MaxValue && MallManager.Instance != null &&
                box.OrderedBy != MallManager.GetTeamOf(OwnerClientId)) return 0;
            return box.ContainedUnits > 0 ? Mathf.Min(box.ContainedUnits, per) : per;
        }
        if (!pickup.IsStockCrate || pickup.ItemId != held || pickup.Paint != HeldPaint.Value) return 0;
        return pickup.Units;
    }

    /// <summary>
    /// Server: put up to this many products into the held crate (same product, plain colors, with room).
    /// How many went in (0: none fit, or it's not the same thing).
    /// </summary>
    public int ServerTopUpWith(int itemId, int units)
    {
        if (!IsServer || itemId == 0 || HeldItemId.Value != itemId || HeldPaint.Value != 0 || !CanTopUpHeldCrate) return 0;
        int moved = Mathf.Min(StockContainer.GetUnitsPerCrate(itemId) - HeldUnits.Value, Mathf.Max(1, units));
        if (moved <= 0) return 0;
        HeldUnits.Value += moved;
        PlayItemSoundClientRpc(itemId, transform.position, false);
        return moved;
    }

    // Server: move what fits from a crate or delivery box into the held crate; it goes away once empty.
    private void ServerTopUp(ItemPickupNet pickup, ShipmentPackage box, ulong sender)
    {
        int units = UnitsToTopUpFrom(pickup);
        if (units <= 0) return;
        int itemId = HeldItemId.Value;
        int moved = Mathf.Min(StockContainer.GetUnitsPerCrate(itemId) - HeldUnits.Value, units);
        if (moved <= 0) return;
        HeldUnits.Value += moved;
        ItemDebug.Log($"Top up by client {sender}: {moved} from '{pickup.name}' ({units - moved} left in it).", pickup);
        Vector3 at = pickup.transform.position;
        if (moved >= units) pickup.NetworkObject.Despawn(true);
        else if (box != null) box.ServerSetUnits(units - moved);
        else pickup.ServerSetUnits(units - moved);
        PlayItemSoundClientRpc(itemId, at, false);
    }

    /// <summary>Put one product from the held stock crate into a container's zone.</summary>
    [ServerRpc]
    public void RequestStockServerRpc(NetworkObjectReference containerRef, int zone, int orient, ServerRpcParams rpcParams = default)
    {
        int id = HeldItemId.Value;
        ItemDebug.Log($"Stock {ItemDebug.Describe(id)} into zone {zone} (orient {orient}).");
        if (id == 0 || !StockContainer.IsStockCrate(id)) { ItemDebug.Log("Stock refused: not holding a stock crate (or it has no Shelf/Hang Prefab)."); return; }
        if (!TryGetContainerZone(containerRef, zone, rpcParams.Receive.SenderClientId, out var container, out var at))
        { ItemDebug.Log("Stock refused: shelf missing, too far, bad zone, or not your store."); return; }

        // A shelf still settling after being put down stops where it is first, so its stock can't send it flying.
        if (container.TryGetComponent<ItemPickupNet>(out var shelf)) shelf.ServerFreezeNow();

        if (!container.ServerAddOne(zone, id, orient, HeldPaint.Value)) // full, holds something else (or another color), or wrong kind of zone
        { ItemDebug.Log($"Stock refused by '{container.name}': {container.WhyCantStock(zone, id, orient)}.", container); return; }

        // The empty crate goes away.
        int left = Mathf.Max(1, HeldUnits.Value) - 1;
        HeldUnits.Value = left;
        if (left == 0) { HeldItemId.Value = 0; HeldPaint.Value = 0; }
        PlayItemSoundClientRpc(id, at, true);
    }

    /* ========================= BUYING FROM OTHER PLAYERS ========================= */

    /// <summary>
    /// What a for-sale thing in someone else's store costs this player (cents), or 0 if they can't buy
    /// it: the store is open and not theirs, and it's priced. Zone -1: a loose item; otherwise one
    /// product off a shelf zone. Works on every machine (for the prompt) and on the host (to buy).
    /// </summary>
    public static int BuyPrice(ulong buyer, NetworkObject target, int zone, out StorePlot plot)
    {
        plot = null;
        var mall = MallManager.Instance;
        if (target == null || mall == null) return 0;
        plot = mall.GetPlotAt(target.transform.position);
        if (plot == null || !plot.IsOpen || plot.IsAuthorized(buyer)) return 0;

        if (zone >= 0)
        {
            if (!target.TryGetComponent<StockContainer>(out var shelf) || zone >= shelf.ZoneCount) return 0;
            var z = shelf.GetZone(zone);
            return z.itemId != 0 && z.count > 0 && z.price > 0 && z.pricedItem == z.itemId ? z.price : 0;
        }

        if (!target.TryGetComponent<Sellable>(out var sellable) || !sellable.IsForSale) return 0;
        if (target.TryGetComponent<ItemPickupNet>(out var pickup) && (pickup.EditModeOnly || pickup.IsOnCart)) return 0;
        if (target.TryGetComponent<CheckoutCounter>(out var counter) && counter.IsBusy) return 0;
        return sellable.SalePrice;
    }

    /// <summary>
    /// Buy something for sale in another player's store: a loose item into empty hands, or one product
    /// off a shelf zone (into empty hands, or a held crate of it with room). Paid straight to the
    /// store's team at its price tag.
    /// </summary>
    [ServerRpc]
    public void RequestBuyServerRpc(NetworkObjectReference targetRef, int zone, ServerRpcParams rpcParams = default)
    {
        ulong buyer = rpcParams.Receive.SenderClientId;
        if (!targetRef.TryGet(out NetworkObject target)) return;
        var actor = NetworkManager.Singleton.ConnectedClients[buyer].PlayerObject;
        if (DistanceTo(target.gameObject, actor.transform.position) > 3.0f) return;

        int price = BuyPrice(buyer, target, zone, out var plot);
        if (price <= 0) return;

        var eco = EconomyManager.Instance;
        int id;
        if (zone >= 0)
        {
            var shelf = target.GetComponent<StockContainer>();
            var stock = shelf.GetZone(zone);
            id = stock.itemId;
            bool emptyHands = HeldItemId.Value == 0;
            bool roomInCrate = HeldItemId.Value == id && HeldPaint.Value == stock.paint &&
                               HeldUnits.Value < StockContainer.GetUnitsPerCrate(id);
            if (!emptyHands && !roomInCrate) return;
            if (eco != null && eco.IsSpawned && !eco.ServerTrySpendCents(MallManager.GetTeamOf(buyer), price)) return;
            if (shelf.ServerRemoveOne(zone) != id) return; // (can't happen: checked above)
            if (emptyHands)
            {
                HeldItemId.Value = id;
                HeldPaint.Value = stock.paint; // the products' color
                HeldUnits.Value = 1;
            }
            else HeldUnits.Value++;
        }
        else
        {
            if (HeldItemId.Value != 0 || !target.TryGetComponent<ItemPickupNet>(out var item) || item.ItemId == 0) return;
            if (item.HasItemOnTopOnCart()) return;
            if (eco != null && eco.IsSpawned && !eco.ServerTrySpendCents(MallManager.GetTeamOf(buyer), price)) return;
            id = item.ItemId;
            HeldItemId.Value = id;
            HeldPaint.Value = item.Paint;
            HeldUnits.Value = StockContainer.GetUnitsPerCrate(id) == 0 ? 0 : item.Units;
            target.Despawn(true);
        }

        if (eco != null && eco.IsSpawned) eco.ServerAddCents(plot.OwnerTeamId, price);
        var progression = ProgressionManager.Instance;
        if (progression != null)
        {
            // The seller's profit, and what the buyer paid (for their own sales).
            int products = zone >= 0 ? 1 : Mathf.Max(1, HeldUnits.Value);
            progression.ServerRecordSale(plot, id, products, price);
            progression.ServerRecordPurchase(MallManager.GetTeamOf(buyer), id, products, price);
        }
        PlayItemSoundClientRpc(id, actor.transform.position, false);
    }

    /// <summary>
    /// Server: put something into this player's hands (e.g. left behind at a checkout). Whole: a loose
    /// item (empty hands only, with its units if it's a crate). Otherwise one product: into empty hands
    /// as a crate holding one, or into a held crate of the same product that has room. False if their
    /// hands are full.
    /// </summary>
    public bool ServerTryTake(int itemId, int units, bool whole)
    {
        if (!IsServer || itemId == 0) return false;
        if (whole)
        {
            if (HeldItemId.Value != 0) return false;
            HeldItemId.Value = itemId;
            HeldPaint.Value = 0;
            HeldUnits.Value = StockContainer.GetUnitsPerCrate(itemId) == 0 ? 0 : Mathf.Max(1, units);
        }
        else if (HeldItemId.Value == 0)
        {
            HeldItemId.Value = itemId;
            HeldPaint.Value = 0;
            HeldUnits.Value = 1;
        }
        else if (HeldItemId.Value == itemId && HeldUnits.Value < StockContainer.GetUnitsPerCrate(itemId)) HeldUnits.Value++;
        else return false;
        PlayItemSoundClientRpc(itemId, transform.position, false);
        return true;
    }

    /// <summary>
    /// Take one product out of a container's zone: into empty hands (as a crate holding one),
    /// or back into a held crate of the same product that has room.
    /// </summary>
    [ServerRpc]
    public void RequestUnstockServerRpc(NetworkObjectReference containerRef, int zone, ServerRpcParams rpcParams = default)
    {
        if (!TryGetContainerZone(containerRef, zone, rpcParams.Receive.SenderClientId, out var container, out var at)) return;

        var stock = container.GetZone(zone);
        int id = stock.itemId;
        if (id == 0) return;

        bool emptyHands = HeldItemId.Value == 0;
        bool roomInCrate = HeldItemId.Value == id && HeldPaint.Value == stock.paint &&
                           HeldUnits.Value < StockContainer.GetUnitsPerCrate(id);
        if (!emptyHands && !roomInCrate) return;
        if (container.ServerRemoveOne(zone) != id) return;

        if (emptyHands)
        {
            HeldItemId.Value = id;
            HeldPaint.Value = stock.paint; // the products' color
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
        if (HeldItemId.Value == 0) { ItemDebug.Log("Place refused: holding nothing."); return; }
        int id = HeldItemId.Value;
        ItemDebug.Log($"Place {ItemDebug.Describe(id)} at {position} (wall {onWall}, cart {onCart}).");

        var actor = NetworkManager.Singleton.ConnectedClients[rpcParams.Receive.SenderClientId].PlayerObject;
        if (Vector3.Distance(actor.transform.position, position) > 5.0f) { ItemDebug.Log("Place refused: too far."); return; }

        // Store rules: only from inside your own store, only onto a spot inside your own store. Except
        // dropping another team's delivery off in their store.
        ulong sender = rpcParams.Receive.SenderClientId;
        bool dropOff = MallManager.IsDeliveringTo(sender, HeldFromTeam.Value, actor.transform.position, position);
        if (!dropOff && !MallManager.CanPickupOrPlace(sender,
                actor.transform.position, position)) { ItemDebug.Log("Place refused: store rules (not your store, or you're outside it)."); return; }

        if (!ItemCatalog.Instance.TryGet(id, out var def) || def.worldNetworkPrefab == null) { ItemDebug.Log("Place refused: not in the catalog or no world prefab."); return; }

        // Out of a delivery box and set down where deliveries go (or dropped off for its store): back in
        // its box. That's allowed at the docks too, so a box picked up by mistake can go back.
        bool backInBox = HeldBoxed.Value && PlacementCheck.CartBoxOf(def) != null &&
                         (dropOff || MallManager.IsDeliveryArea(position));

        // Can't park things on another store's shipment spots, or anywhere at the docks (except on a cart).
        if (!dropOff && MallManager.IsOthersShipmentArea(sender, position)) { ItemDebug.Log("Place refused: another store's shipment area."); return; }
        if (!backInBox && MallManager.IsNoPlaceZone(position)) { ItemDebug.Log("Place refused: no-place zone (docks)."); return; }
        // A store being cleared for edit mode: nothing goes down in it until everyone's out.
        if (MallManager.Instance != null && MallManager.Instance.IsClearingAt(position)) { ItemDebug.Log("Place refused: the store is being cleared for edit mode."); return; }

        // Set down on a cart: it goes on packed in its shipment box, to save room. Wall pieces always
        // go down in their box (they come out in edit mode).
        var boxPrefab = onCart || backInBox || PlacementCheck.StaysBoxed(def) ? PlacementCheck.CartBoxOf(def) : null;
        if (boxPrefab != null)
        {
            ItemDebug.Log($"Placing it in its box '{boxPrefab.name}' ({(onCart ? "on a cart" : "stays boxed")}).");
            ServerPlaceBox(def, boxPrefab, position, rotation, rpcParams.Receive.SenderClientId, onCart);
            if (dropOff && HeldItemId.Value == 0) ServerDroppedOff(sender, def, position);
            return;
        }

        // Whether it fits was checked by the placing player's machine (the green preview); the host takes
        // its word rather than re-running the test (cheaper, and no refusals from the two machines seeing
        // it a hair differently). Only wall items go on walls, and wall-only items only on walls.
        var mounting = PlacementCheck.GetMounting(def);
        if (onWall ? mounting == ItemPickupNet.Mounting.Floor : mounting == ItemPickupNet.Mounting.Wall && !onCart)
        { ItemDebug.Log($"Place refused: mounting {mounting} doesn't allow {(onWall ? "walls" : "floors")}."); return; }

        // Set down on a cart: it rides along from the start (no falling, so it can't be left behind).
        CartNet cart = null;
        if (!onWall && PlacementCheck.GetTestBox(def, position, rotation, out Vector3 center, out _))
            cart = CartNet.FindCartAt(center);

        // Set down on something that's still settling (a shelf just put down)? It stops where it is first,
        // so the two can't knock each other flying.
        if (!onWall && cart == null) FreezeWhatsBelow(def, position, rotation);

        var spawned = Instantiate(def.worldNetworkPrefab, position, rotation);
        if (def.IsSize && spawned.TryGetComponent<ItemPickupNet>(out var sized))
        {
            sized.ServerSetItemId(id);
            ItemDebug.Log($"Placed size: spawned '{spawned.name}' now reads {ItemDebug.Describe(sized.ItemId)}.", spawned);
        }
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

        ulong fromTeam = HeldFromTeam.Value;
        HeldItemId.Value = 0;
        HeldPaint.Value = 0;
        HeldUnits.Value = 0;
        HeldFromTeam.Value = ulong.MaxValue;
        if (dropOff) ServerDroppedOff(sender, def, position, fromTeam);

        PlayItemSoundClientRpc(id, position, true);
    }

    // Another team's delivery set down in their store: a help point, and they're told.
    private void ServerDroppedOff(ulong helper, ItemDef def, Vector3 position, ulong team = ulong.MaxValue)
    {
        if (team == ulong.MaxValue) team = HeldFromTeam.Value;
        HeldFromTeam.Value = ulong.MaxValue;
        var plot = MallManager.Instance != null ? MallManager.Instance.GetPlotAt(position) : null;
        if (plot == null || !plot.HasOwner) return;
        if (HelpPoints.Instance != null) HelpPoints.Instance.ServerAward(helper, HelpPoints.HelpAction.DeliveredForOthers);
        if (NotificationManager.Instance != null)
            NotificationManager.Instance.ServerNotifyTeamText(plot.OwnerTeamId,
                $"Delivery dropped off: {def.displayName}", $"Another player brought it to {plot.DisplayName}.");
        ItemDebug.Log($"Drop-off: client {helper} brought {def.displayName} to '{plot.DisplayName}'.", plot);
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

    private void ServerPlaceBox(ItemDef def, NetworkObject boxPrefab, Vector3 position, Quaternion rotation, ulong sender,
                                bool onCart)
    {
        var cart = CartNet.FindCartAt(PlacementCheck.PrefabCenter(boxPrefab.gameObject, position, rotation));
        if (cart != null && !cart.CanUse(sender)) return;
        if (onCart && cart == null) return;
        // (Whether the box fits was checked by the placing player's machine.)

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
        if (cart != null && spawned.TryGetComponent<ItemPickupNet>(out var pickup)) pickup.ServerPrepareFrozen(carried: true);
        spawned.Spawn(true);
        if (cart != null) cart.ServerLoad(spawned); // on the floor it settles like anything else
        // Still the delivery of the team that ordered it (not held for them any more, just marked theirs).
        if (box != null && heldOrderedBy != ulong.MaxValue) box.ServerReserve(heldOrderedBy, 0f);
        heldOrderedBy = ulong.MaxValue;

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