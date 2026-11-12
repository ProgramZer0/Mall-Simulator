using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// The price scanner. Put it on the player prefab. Press 1 (with empty hands) to take it out, aim at a
/// shelf or a sellable item in your store and click: the scanner's display shows its price, ready to
/// type over. No window opens; you can keep walking and looking around while typing.
///
/// Typing (on the display, one row):
/// - It starts at the current price, or the suggested price (cost × Default Markup) if it isn't for sale.
/// - Digits type a new price cash-register style: 1 2 9 9 = $12.99. The first digit replaces what's
///   shown; Backspace takes the last one off.
/// - Enter: price this shelf space / item. Shift+Enter: every one of that product in the store.
///   Entering 0 takes it off sale.
/// - Esc or right click: cancel. Clicking something else starts on that instead.
/// While typing, the number keys go to the scanner (they don't switch tools or open edit mode).
///
/// The display is the first TextMeshPro text in the Hand Visual Prefab (3D or UI), or the one in
/// Display Text Name. Without one, the price shows in the interact prompt instead.
///
/// Only things with a Sellable can be priced. Shelves are priced per zone; the price stays on the
/// zone while it's empty and goes with the product it was set for. 1 again puts the scanner away.
/// </summary>
public class PriceScanner : NetworkBehaviour
{
    [SerializeField] private KeyCode toggleKey = KeyCode.Alpha1;
    [Tooltip("Optional: what shows in your hand while scanning. Put a TextMeshPro text on it for the display.")]
    [SerializeField] private GameObject handVisualPrefab;
    [Tooltip("Optional: the name of the display's TextMeshPro object in the Hand Visual Prefab. Empty = the first one.")]
    [SerializeField] private string displayTextName = "";
    [Tooltip("Where the scanner is held. Empty = the inventory's Hand Anchor (where held items show). Set it to a " +
             "hand bone later to put the scanner in the hand.")]
    [SerializeField] private Transform hand;
    [Tooltip("Where the scanner sits from the hand: right, up, forward (meters). The default keeps it off to the " +
             "lower right, close enough to read.")]
    [SerializeField] private Vector3 handOffset = new(0.2f, -0.16f, 0.3f);
    [Tooltip("How the scanner is turned in the hand (degrees).")]
    [SerializeField] private Vector3 handRotation = new(-90f, 0f, 0f);
    [Tooltip("While typing a price, the scanner moves this much more (right, up, forward) toward the middle so the " +
             "display is easy to read. Zero = it stays put.")]
    [SerializeField] private Vector3 typingShift = new(-0.06f, 0.06f, -0.04f);
    [Tooltip("How fast it moves there and back.")]
    [SerializeField] private float typingShiftSpeed = 8f;
    [Tooltip("How far you can scan, in meters.")]
    [SerializeField] private float range = 4f;
    [Tooltip("The suggested price is cost × this: a safe price with a small profit, but rarely the best one. " +
             "Finding the best price (which moves with events and seasons) is up to the player.")]
    [SerializeField] private float defaultMarkup = 1.2f;
    [Tooltip("Outline colour for what the scanner would price.")]
    [SerializeField] private Color outlineColor = new(0.4f, 1f, 0.4f, 1f);
    [Tooltip("Outline colour for what's being priced while typing.")]
    [SerializeField] private Color typingOutlineColor = new(1f, 0.9f, 0.3f, 1f);

    private const int MaxDigits = 7; // $99,999.99

    /// <summary>Is the local player's scanner out?</summary>
    public static bool LocalActive { get; private set; }
    /// <summary>Is the local player typing a price? Number keys belong to the scanner meanwhile.</summary>
    public static bool LocalTyping { get; private set; }

    private bool active;
    private Camera cam;
    private PlayerInventoryNet inventory;
    private StoreEditController storeEdit;
    private GameObject handVisual;
    private TMP_Text display;

    // What's being aimed at.
    private StockContainer targetContainer;
    private int targetZone = -1;
    private Sellable targetItem;
    private int targetProduct;
    private string hint;

    // What's being priced (kept while typing, wherever the player looks).
    private bool typing;
    private StockContainer entryContainer;
    private int entryZone;
    private Sellable entryItem;
    private int entryProduct;
    private int entryCents;
    private bool entryFresh;  // the first digit replaces what's shown
    private string entryNote; // e.g. a part-used crate's share

    public bool IsActive => active;

    /// <summary>What the scanner is doing (shown by PlayerInteractInput's prompt while it's out).</summary>
    public string Prompt { get; private set; }

    private void Awake()
    {
        inventory = GetComponent<PlayerInventoryNet>();
        storeEdit = GetComponent<StoreEditController>();
    }

    public override void OnNetworkSpawn()
    {
        if (IsOwner) cam = GetComponentInChildren<Camera>(true);
    }

    public override void OnNetworkDespawn() => SetActive(false);

    public override void OnDestroy()
    {
        if (IsOwner) { LocalActive = false; LocalTyping = false; }
        TargetOutline.Release(this);
        base.OnDestroy();
    }

    /// <summary>Put the scanner away (another tool is coming out).</summary>
    public void PutAway()
    {
        if (active) SetActive(false);
    }

    private float shift; // 0 resting .. 1 shifted for reading while typing

    // Slide the scanner toward the middle while typing, and back after.
    private void LateUpdate()
    {
        if (handVisual == null) return;
        float target = typing ? 1f : 0f;
        if (Mathf.Approximately(shift, target)) return;
        shift = Mathf.MoveTowards(shift, target, typingShiftSpeed * Time.deltaTime);
        float eased = shift * shift * (3f - 2f * shift);
        handVisual.transform.localPosition = handOffset + typingShift * eased;
    }

    private void Update()
    {
        if (!IsOwner || cam == null) return;

        bool editing = storeEdit != null && storeEdit.IsEditing;
        bool holding = (inventory != null && inventory.HeldItemId.Value != 0) || CartNet.GetPushedBy(OwnerClientId) != null;
        if (active && (editing || holding)) SetActive(false);
        if (editing || UIState.IsMenuOpen) return;

        if (typing)
        {
            UpdateTyping();
            return;
        }

        if (Input.GetKeyDown(toggleKey) && !holding) SetActive(!active);
        if (!active) return;

        FindTarget();
        UpdateOutline(targetContainer, targetZone, targetItem, outlineColor);
        ShowAiming();
        if (Input.GetMouseButtonDown(0) && targetProduct != 0 && hint == null) StartTyping();
    }

    private void SetActive(bool on)
    {
        if (on && TryGetComponent<PaintBrush>(out var brush)) brush.PutAway(); // one hand tool at a time
        active = on;
        if (IsOwner) LocalActive = on;
        Transform holdAt = hand != null ? hand : inventory != null ? inventory.HandAnchor : null;
        if (on && handVisualPrefab != null && handVisual == null && holdAt != null)
        {
            handVisual = Instantiate(handVisualPrefab, holdAt);
            handVisual.transform.localPosition = handOffset;
            handVisual.transform.localRotation = Quaternion.Euler(handRotation);
            shift = 0f;
            if (IsOwner) HeldItemBlocker.Add(handVisual, transform, inventory.HeldItemBlockedBy);
            display = FindDisplay(handVisual);
        }
        if (!on && handVisual != null)
        {
            Destroy(handVisual);
            handVisual = null;
            display = null;
        }
        if (!on)
        {
            StopTyping();
            ClearTarget();
            Prompt = null;
            TargetOutline.For(this).Hide();
        }
    }

    private TMP_Text FindDisplay(GameObject model)
    {
        foreach (var t in model.GetComponentsInChildren<TMP_Text>(true))
            if (string.IsNullOrEmpty(displayTextName) || t.name == displayTextName) return t;
        return null;
    }

    private void ShowOnDisplay(string text)
    {
        if (display != null && display.text != text) display.text = text;
    }

    private void ClearTarget()
    {
        targetContainer = null;
        targetZone = -1;
        targetItem = null;
        targetProduct = 0;
        hint = null;
    }

    private readonly List<GameObject> outlineModels = new();

    // Box what a click would price: every product in the shelf space (they share a price), or the item.
    private void UpdateOutline(StockContainer container, int zone, Sellable item, Color color)
    {
        var outline = TargetOutline.For(this);
        if (item != null) { outline.ShowObject(item.gameObject, color); return; }
        if (container != null && zone >= 0)
        {
            outlineModels.Clear();
            foreach (var m in container.GetZoneModels(zone)) if (m != null) outlineModels.Add(m);
            if (outlineModels.Count > 0) { outline.ShowObjects(outlineModels, color); return; }
            if (container.TryGetZoneBox(zone, out Vector3 c, out Quaternion r, out Vector3 s))
            { outline.ShowBox(c, r, s, color); return; }
        }
        outline.Hide();
    }

    // Not typing: the display shows the price of what's aimed at.
    private void ShowAiming()
    {
        if (targetProduct != 0 && hint == null)
        {
            int now = targetItem != null ? targetItem.Price : targetContainer.GetZone(targetZone).price;
            ShowOnDisplay(now > 0 ? Money(now) : "- - -");
            Prompt = $"Click: price {NameOf(targetProduct)}" + (display == null ? $"   (now {Sellable.FormatPrice(now)})" : "");
            return;
        }
        ShowOnDisplay("SCAN");
        Prompt = hint ?? $"Scanner: aim at a shelf or item   ({KeyName(toggleKey)} to put away)";
    }

    /* ========================= TYPING ========================= */

    private void StartTyping()
    {
        bool stock = targetItem == null;
        int current = stock ? targetContainer.GetZone(targetZone).price : targetItem.Price;
        entryContainer = targetContainer;
        entryZone = targetZone;
        entryItem = targetItem;
        entryProduct = targetProduct;
        // Already for sale: its price. Not for sale yet: the suggested price.
        entryCents = current > 0 ? current : DefaultPrice(CostEach(targetProduct, stock));
        entryFresh = true;
        entryNote = null;
        if (!stock && StockContainer.IsStockCrate(targetProduct))
        {
            entryNote = "full crate";
            if (targetItem.TryGetComponent<ItemPickupNet>(out var crate) && crate.Units < crate.UnitsPerCrate)
                entryNote = $"full crate; {crate.Units} of {crate.UnitsPerCrate} left sells for its share";
        }
        typing = true;
        if (IsOwner) LocalTyping = true;
        ShowTyping();
    }

    private void StopTyping()
    {
        typing = false;
        if (IsOwner) LocalTyping = false;
        entryContainer = null;
        entryItem = null;
    }

    private void UpdateTyping()
    {
        // What's being priced went away (sold, picked up, despawned).
        if ((entryItem == null || !entryItem.IsSpawned) && (entryContainer == null || !entryContainer.IsSpawned))
        {
            StopTyping();
            return;
        }

        if (UIState.TakeEscape() || Input.GetMouseButtonDown(1))
        {
            StopTyping();
            return;
        }

        // Clicking something else starts on that instead.
        if (Input.GetMouseButtonDown(0))
        {
            FindTarget();
            if (targetProduct != 0 && hint == null) { StartTyping(); return; }
        }

        for (int d = 0; d <= 9; d++)
            if (Input.GetKeyDown(KeyCode.Alpha0 + d) || Input.GetKeyDown(KeyCode.Keypad0 + d))
            {
                if (entryFresh) { entryCents = 0; entryFresh = false; }
                if (entryCents.ToString().Length < MaxDigits || entryCents == 0) entryCents = entryCents * 10 + d;
            }
        if (Input.GetKeyDown(KeyCode.Backspace))
        {
            entryFresh = false;
            entryCents /= 10;
        }

        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            bool all = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (entryItem != null) SetItemPriceRpc(entryItem.NetworkObject, entryCents, all);
            else if (entryContainer != null) SetZonePriceRpc(entryContainer.NetworkObject, entryZone, entryCents, all);
            StopTyping();
            return;
        }

        UpdateOutline(entryContainer, entryZone, entryItem, typingOutlineColor);
        ShowTyping();
    }

    private void ShowTyping()
    {
        ShowOnDisplay(entryCents > 0 ? Money(entryCents) : "$0.00");
        string name = NameOf(entryProduct);
        string price = display == null ? $"{(entryCents > 0 ? Money(entryCents) : "$0.00 (not for sale)")}   " : "";
        string note = entryNote != null ? $"({entryNote})   " : "";
        Prompt = $"{price}{note}Enter: set   Shift+Enter: all {name}   0: not for sale   Esc: cancel";
    }

    private static string Money(int cents) => $"${cents / 100:N0}.{cents % 100:00}";

    private static string KeyName(KeyCode k) => k.ToString().Replace("Alpha", "");

    /* ========================= AIMING ========================= */


    private void FindTarget()
    {
        ClearTarget();
        if (!Physics.Raycast(cam.transform.position, cam.transform.forward, out RaycastHit hit, range,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            return;

        // A shelf or rack (or the products on it) first: their zones are priced, not the shelf.
        var container = hit.collider.GetComponentInParent<StockContainer>();
        if (container == null)
        {
            var follower = hit.collider.GetComponentInParent<StockVisualFollower>();
            if (follower != null) container = follower.Container;
        }
        int zone = container != null && container.IsSpawned ? container.FindNearestPriceableZone(hit.point) : -1;
        if (zone >= 0)
        {
            targetContainer = container;
            targetZone = zone;
            targetProduct = container.GetZoneProduct(zone);
            if (!Sellable.IsSellable(targetProduct)) hint = "This can't be sold (no Sellable)";
            else if (!InOwnStore(container.GetZoneCenter(zone))) hint = "Not in your store";
            return;
        }

        // A loose sellable item (not stock on a shelf).
        var item = hit.collider.GetComponentInParent<Sellable>();
        if (item != null && item.IsSpawned)
        {
            targetItem = item;
            targetProduct = item.ItemId;
            if (!item.SellWhenLoose) hint = "This is stock: price it on the shelf";
            else if (!InOwnStore(item.transform.position)) hint = "Not in your store";
            // A checkout counter can only go up for sale if another one is left for customers (and
            // nobody's at it). One already for sale can still have its price changed or taken off.
            else if (item.Price == 0 && item.TryGetComponent<CheckoutCounter>(out var counter) &&
                     !CheckoutCounter.CanBePutForSale(counter, out string why)) hint = why;
            return;
        }

        if (container != null) hint = "Nothing on this shelf to price";
    }

    private bool InOwnStore(Vector3 point)
    {
        var plot = MallManager.Instance != null ? MallManager.Instance.GetPlotAt(point) : null;
        return plot != null && plot.IsAuthorized(OwnerClientId);
    }

    // A safe price: a little over cost (always at least a cent of profit).
    private int DefaultPrice(int cost) =>
        cost <= 0 ? 0 : Mathf.Max(cost + 1, Mathf.CeilToInt(cost * Mathf.Max(1f, defaultMarkup)));

    private static string NameOf(int itemId) =>
        ItemCatalog.Instance != null && ItemCatalog.Instance.TryGet(itemId, out var def) ? def.displayName : "Item";

    // What one of these cost to buy in: a crate's price split over its products, or the item's price.
    private static int CostEach(int itemId, bool stock)
    {
        if (ItemCatalog.Instance == null || !ItemCatalog.Instance.TryGet(itemId, out var def)) return 0;
        int units = stock ? Mathf.Max(1, StockContainer.GetUnitsPerCrate(itemId)) : 1;
        return Mathf.RoundToInt(MarketModifiers.OrderCost(def) * 100f / units); // today's cost, events included
    }

    /* ========================= HOST ========================= */

    [Rpc(SendTo.Server)]
    private void SetZonePriceRpc(NetworkObjectReference containerRef, int zone, int cents, bool all, RpcParams rpcParams = default)
    {
        if (rpcParams.Receive.SenderClientId != OwnerClientId) return;
        if (!containerRef.TryGet(out NetworkObject no) || !no.TryGetComponent(out StockContainer container)) return;
        if (zone < 0 || zone >= container.ZoneCount) return;

        Vector3 at = container.GetZoneCenter(zone);
        if (!ServerCanPrice(at, out StorePlot plot)) return;

        int product = container.GetZoneProduct(zone);
        if (!Sellable.IsSellable(product)) return;

        if (all) ServerPriceAllOnShelves(plot, product, cents);
        else container.ServerSetPrice(zone, cents);
    }

    [Rpc(SendTo.Server)]
    private void SetItemPriceRpc(NetworkObjectReference itemRef, int cents, bool all, RpcParams rpcParams = default)
    {
        if (rpcParams.Receive.SenderClientId != OwnerClientId) return;
        if (!itemRef.TryGet(out NetworkObject no) || !no.TryGetComponent(out Sellable item) || !item.SellWhenLoose) return;
        if (!ServerCanPrice(item.transform.position, out StorePlot plot)) return;

        if (all) ServerPriceAllLoose(plot, item.ItemId, cents);
        else item.ServerSetPrice(cents);
    }

    // In range, and in a store this player's team owns.
    private bool ServerCanPrice(Vector3 target, out StorePlot plot)
    {
        plot = MallManager.Instance != null ? MallManager.Instance.GetPlotAt(target) : null;
        if (plot == null || !plot.IsAuthorized(OwnerClientId)) return false;
        return Vector3.Distance(transform.position, target) <= range + 2f;
    }

    // "Set for all" stays on its own side: a shelf price is per product, a loose item's price is for the
    // whole thing (a full crate costs more than one shirt), so one never overwrites the other.

    // Every shelf zone and rack holding (or priced for) this product, in the store.
    private static void ServerPriceAllOnShelves(StorePlot plot, int product, int cents)
    {
        plot.ServerSetStorePrice(product, false, cents); // new stock of it in this store gets this price too
        foreach (var c in StockContainer.All)
        {
            if (c == null) continue;
            for (int z = 0; z < c.ZoneCount; z++)
                if (c.GetZoneProduct(z) == product && plot.Contains(c.GetZoneCenter(z))) c.ServerSetPrice(z, cents);
        }
    }

    // Every loose one of this item (e.g. every whole crate of it), in the store.
    private static void ServerPriceAllLoose(StorePlot plot, int itemId, int cents)
    {
        plot.ServerSetStorePrice(itemId, true, cents); // ones put down in this store later get this price too
        foreach (var s in Sellable.All)
            if (s != null && s.SellWhenLoose && s.ItemId == itemId && plot.Contains(s.transform.position))
                s.ServerSetPrice(cents);
    }
}
