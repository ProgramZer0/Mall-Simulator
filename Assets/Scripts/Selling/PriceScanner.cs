using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// The price scanner. Put it on the player prefab. Press 1 (with empty hands) to take it out, aim at a
/// shelf or a sellable item in your store and click: the price tag window opens. Set a price for that
/// shelf (or item), or for every one of that product in the store. 1 again puts it away.
///
/// Only things with a Sellable can be priced. Shelves are priced per zone; the price stays on the
/// zone while it's empty and goes with the product it was set for.
/// </summary>
public class PriceScanner : NetworkBehaviour
{
    [SerializeField] private KeyCode toggleKey = KeyCode.Alpha1;
    [Tooltip("Optional: what shows in your hand while scanning.")]
    [SerializeField] private GameObject handVisualPrefab;
    [Tooltip("How far you can scan, in meters.")]
    [SerializeField] private float range = 4f;
    [Tooltip("The Default price is cost × this: a safe price with a small profit, but rarely the best one. " +
             "Finding the best price (which moves with events and seasons) is up to the player.")]
    [SerializeField] private float defaultMarkup = 1.2f;

    private bool active;
    private Camera cam;
    private PlayerInventoryNet inventory;
    private StoreEditController storeEdit;
    private GameObject handVisual;

    // What's being aimed at.
    private StockContainer targetContainer;
    private int targetZone = -1;
    private Sellable targetItem;
    private int targetProduct;
    private string hint;

    // The built-in window (when there's no PriceTagUI in the scene).
    private bool fallbackOpen;
    private string fallbackText = "";
    private string fallbackTitle;
    private int fallbackCost;
    private int fallbackDefault;
    private System.Action<int, bool> fallbackSet;

    public bool IsActive => active;

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

    private void Update()
    {
        if (!IsOwner || cam == null) return;

        bool editing = storeEdit != null && storeEdit.IsEditing;
        bool holding = inventory != null && inventory.HeldItemId.Value != 0;
        if (active && (editing || holding)) SetActive(false);

        bool ourWindowOpen = fallbackOpen || (PriceTagUI.Instance != null && PriceTagUI.Instance.IsOpen);
        if (editing || (UIState.IsMenuOpen && !ourWindowOpen)) return;

        if (!ourWindowOpen && Input.GetKeyDown(toggleKey) && !holding) SetActive(!active);
        if (!active || ourWindowOpen) return;

        FindTarget();
        UpdateOutline();
        if (Input.GetMouseButtonDown(0) && targetProduct != 0 && hint == null) OpenWindow();
    }

    [Tooltip("Outline colour for what the scanner would price.")]
    [SerializeField] private Color outlineColor = new(0.4f, 1f, 0.4f, 1f);
    private readonly List<GameObject> outlineModels = new();

    // Box what a click would price: every product in the shelf space (they share a price), or the item.
    private void UpdateOutline()
    {
        var outline = TargetOutline.For(this);
        if (targetItem != null) { outline.ShowObject(targetItem.gameObject, outlineColor); return; }
        if (targetContainer != null && targetZone >= 0)
        {
            outlineModels.Clear();
            foreach (var m in targetContainer.GetZoneModels(targetZone)) if (m != null) outlineModels.Add(m);
            if (outlineModels.Count > 0) { outline.ShowObjects(outlineModels, outlineColor); return; }
            if (targetContainer.TryGetZoneBox(targetZone, out Vector3 c, out Quaternion r, out Vector3 s))
            { outline.ShowBox(c, r, s, outlineColor); return; }
        }
        outline.Hide();
    }

    private void SetActive(bool on)
    {
        active = on;
        if (on && handVisualPrefab != null && handVisual == null && inventory != null && inventory.HandAnchor != null)
        {
            handVisual = Instantiate(handVisualPrefab, inventory.HandAnchor);
            handVisual.transform.localPosition = Vector3.zero;
            handVisual.transform.localRotation = Quaternion.identity;
        }
        if (!on && handVisual != null)
        {
            Destroy(handVisual);
            handVisual = null;
        }
        if (!on)
        {
            ClearTarget();
            TargetOutline.For(this).Hide();
        }
    }

    private void ClearTarget()
    {
        targetContainer = null;
        targetZone = -1;
        targetItem = null;
        targetProduct = 0;
        hint = null;
    }

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

    /* ========================= WINDOW ========================= */

    private void OpenWindow()
    {
        bool stock = targetItem == null;
        int current = stock ? targetContainer.GetZone(targetZone).price : targetItem.Price;
        int cost = CostEach(targetProduct, stock);
        string title = NameOf(targetProduct);
        int suggested = DefaultPrice(cost);
        if (!stock && StockContainer.IsStockCrate(targetProduct))
        {
            title += " (full crate price)"; // priced apart from one on the shelf
            // A part-used one sells for its share.
            if (targetItem.TryGetComponent<ItemPickupNet>(out var crate) && crate.Units < crate.UnitsPerCrate)
                title += $": {crate.Units} of {crate.UnitsPerCrate} left, sells for {Sellable.FormatPrice(targetItem.SalePrice)}";
        }

        // Remember the target now; the player can look away while typing.
        var container = targetContainer;
        int zone = targetZone;
        var item = targetItem;
        System.Action<int, bool> set = (cents, all) =>
        {
            if (item != null) SetItemPriceRpc(item.NetworkObject, cents, all);
            else if (container != null) SetZonePriceRpc(container.NetworkObject, zone, cents, all);
        };

        if (PriceTagUI.Instance != null)
        {
            PriceTagUI.Instance.Open(title, current, cost, suggested, set);
            return;
        }

        fallbackOpen = true;
        fallbackTitle = title;
        fallbackCost = cost;
        fallbackDefault = suggested;
        int shown = current > 0 ? current : suggested;
        fallbackText = shown > 0 ? (shown / 100f).ToString("0.00") : "";
        fallbackSet = set;
        UIState.OpenMenu(this);
    }

    private void CloseFallback()
    {
        fallbackOpen = false;
        fallbackSet = null;
        UIState.CloseMenu(this);
    }

    public override void OnDestroy()
    {
        UIState.CloseMenu(this);
        TargetOutline.Release(this);
        base.OnDestroy();
    }

    private static GUIStyle hintStyle;

    private void OnGUI()
    {
        if (!IsOwner) return;

        if (fallbackOpen)
        {
            var r = new Rect(Screen.width * 0.5f - 170f, Screen.height * 0.5f - 110f, 340f, 220f);
            GUI.Box(r, $"Price: {fallbackTitle}");
            GUI.Label(new Rect(r.x + 15, r.y + 28, 310, 20), $"Cost each: {Sellable.FormatPrice(fallbackCost)}");
            GUI.SetNextControlName("PriceField");
            fallbackText = GUI.TextField(new Rect(r.x + 15, r.y + 52, 310, 24), fallbackText);
            GUI.FocusControl("PriceField");

            if (GUI.Button(new Rect(r.x + 15, r.y + 84, 310, 24), $"Default {Sellable.FormatPrice(fallbackDefault)}"))
                fallbackText = (fallbackDefault / 100f).ToString("0.00");

            bool enter = Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return;
            if (GUI.Button(new Rect(r.x + 15, r.y + 118, 150, 28), "Set this one") || enter) SubmitFallback(false);
            if (GUI.Button(new Rect(r.x + 175, r.y + 118, 150, 28), $"Set all {fallbackTitle}")) SubmitFallback(true);
            if (GUI.Button(new Rect(r.x + 15, r.y + 152, 150, 28), "Not for sale")) { fallbackSet?.Invoke(0, false); CloseFallback(); }
            if (GUI.Button(new Rect(r.x + 175, r.y + 152, 150, 28), "Close") ||
                (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)) CloseFallback();
            return;
        }

        if (!active || UIState.IsMenuOpen) return;
        hintStyle ??= new GUIStyle(GUI.skin.box) { fontSize = 15, alignment = TextAnchor.MiddleCenter };
        string text = hint ?? (targetProduct != 0
            ? $"Click: price {NameOf(targetProduct)}   (now {Sellable.FormatPrice(targetItem != null ? targetItem.Price : targetContainer.GetZone(targetZone).price)})"
            : $"Scanner: aim at a shelf or item   ({toggleKey} to put away)");
        GUI.Box(new Rect(Screen.width * 0.5f - 220f, Screen.height * 0.68f, 440f, 28f), text, hintStyle);
    }

    private void SubmitFallback(bool all)
    {
        if (!Sellable.TryParsePrice(fallbackText, out int cents)) return;
        fallbackSet?.Invoke(cents, all);
        CloseFallback();
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
