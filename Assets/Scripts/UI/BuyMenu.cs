using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The shop's buy screen. Fills your page prefab with items from the ItemCatalog (those with
/// Show In Shop ticked), adds pages automatically when there are more items than tiles, and
/// filters by category.
///
/// Page prefab: any layout of item tiles. Each tile is an Image (the item's picture) with a Button
/// inside it (buy). Add a BuyItemSlot to a tile to also show its name and price, or to point at
/// the parts explicitly.
///
/// With Use Cart on, buy buttons add to a cart and Checkout orders everything in one delivery.
/// With it off, each buy button orders one item straight away.
/// </summary>
public class BuyMenu : MonoBehaviour, IShopScreen
{
    [Serializable]
    public class Filter
    {
        public string label = "All";
        public Button button;
        [Tooltip("Shows items whose Category matches exactly. Empty = everything.")]
        public string category = "";
    }

    [Header("Pages")]
    [Tooltip("Your page of item tiles: a prefab (made under Page Parent) or an object already in the scene.")]
    [SerializeField] private GameObject pagePrefab;
    [Tooltip("Where the page is created, if Page Prefab is a prefab.")]
    [SerializeField] private RectTransform pageParent;
    [SerializeField] private Button nextPageButton;
    [SerializeField] private Button previousPageButton;
    [SerializeField] private TMP_Text pageLabel;

    [Header("Filters")]
    [SerializeField] private Filter[] filters;
    [SerializeField] private Color selectedFilterColor = new(1f, 1f, 1f, 1f);
    [SerializeField] private Color normalFilterColor = new(0.75f, 0.75f, 0.75f, 1f);

    [Header("Cart")]
    [SerializeField] private bool useCart = true;
    [Tooltip("Lists what's in the cart and the total.")]
    [SerializeField] private TMP_Text cartText;
    [SerializeField] private Button checkoutButton;
    [SerializeField] private Button clearCartButton;

    [Header("Info")]
    [SerializeField] private TMP_Text moneyText;
    [Tooltip("Short messages: added to cart, ordered, not enough money, ...")]
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private float statusSeconds = 4f;

    private struct Slot
    {
        public GameObject go;
        public Image icon;
        public Button button;
        public TMP_Text nameText;
        public TMP_Text priceText;
    }

    private StorePlot plot;
    private GameObject page;
    private readonly List<Slot> slots = new();
    private readonly List<ItemDef> shown = new();
    private readonly List<(int itemId, int quantity)> cart = new();
    private int pageIndex;
    private string category = "";
    private bool waitingForResult;
    private float statusUntil;
    private EconomyManager subscribedTo;

    /* ========================= SETUP ========================= */

    private void Awake()
    {
        if (nextPageButton != null) nextPageButton.onClick.AddListener(() => ChangePage(+1));
        if (previousPageButton != null) previousPageButton.onClick.AddListener(() => ChangePage(-1));
        if (checkoutButton != null) checkoutButton.onClick.AddListener(Checkout);
        if (clearCartButton != null) clearCartButton.onClick.AddListener(ClearCart);

        if (filters != null)
            foreach (var f in filters)
            {
                if (f?.button == null) continue;
                string cat = f.category ?? "";
                f.button.onClick.AddListener(() => SetCategory(cat));
            }

        BuildPage();
    }

    private void OnEnable()
    {
        Subscribe();
        RefreshAll();
    }

    private void OnDisable() => Unsubscribe();

    private void Update()
    {
        if (statusText != null && statusUntil > 0f && Time.unscaledTime > statusUntil)
        {
            statusText.text = "";
            statusUntil = 0f;
        }
    }

    /// <summary>Called by ShopUIController when the shop opens on this tab.</summary>
    public void OnShopOpened(StorePlot forPlot)
    {
        if (forPlot != plot) cart.Clear(); // a cart belongs to one store
        plot = forPlot;
        pageIndex = 0;
        Subscribe();
        RefreshAll();
    }

    private void BuildPage()
    {
        if (page != null || pagePrefab == null) return;

        // A prefab gets a copy; an object already in the scene is used as it is.
        page = pagePrefab.scene.IsValid() ? pagePrefab
             : Instantiate(pagePrefab, pageParent != null ? pageParent : (RectTransform)transform);

        slots.Clear();
        var marked = page.GetComponentsInChildren<BuyItemSlot>(true);
        if (marked.Length > 0)
        {
            foreach (var m in marked)
                slots.Add(new Slot { go = m.gameObject, icon = m.icon, button = m.buyButton, nameText = m.nameText, priceText = m.priceText });
        }
        else
        {
            // No BuyItemSlot: every button in the page is a buy button, and the Image it sits in is the picture.
            foreach (var b in page.GetComponentsInChildren<Button>(true))
            {
                if (IsControlButton(b)) continue;
                Transform tile = b.transform.parent != null ? b.transform.parent : b.transform;
                slots.Add(new Slot { go = tile.gameObject, icon = tile.GetComponent<Image>(), button = b });
            }
        }

        if (slots.Count == 0)
            Debug.LogWarning("[BuyMenu] The page has no item tiles (no BuyItemSlot and no Buttons).", this);
    }

    /// <summary>Page, cart and filter buttons aren't item tiles, even if they sit inside the page.</summary>
    private bool IsControlButton(Button b)
    {
        if (b == nextPageButton || b == previousPageButton || b == checkoutButton || b == clearCartButton) return true;
        if (filters != null)
            foreach (var f in filters)
                if (f != null && f.button == b) return true;
        return false;
    }

    private void Subscribe()
    {
        var eco = EconomyManager.Instance;
        if (eco == subscribedTo) return;
        Unsubscribe();
        if (eco == null) return;

        eco.MoneyChanged += RefreshMoney;
        eco.OrderResultReceived += OnOrderResult;
        subscribedTo = eco;
    }

    private void Unsubscribe()
    {
        if (subscribedTo == null) return;
        subscribedTo.MoneyChanged -= RefreshMoney;
        subscribedTo.OrderResultReceived -= OnOrderResult;
        subscribedTo = null;
    }

    /* ========================= ITEMS & PAGES ========================= */

    private void SetCategory(string cat)
    {
        category = cat ?? "";
        pageIndex = 0;
        RefreshItems();
    }

    private void ChangePage(int delta)
    {
        pageIndex += delta;
        RefreshItems();
    }

    private int PageCount => slots.Count == 0 ? 1 : Mathf.Max(1, Mathf.CeilToInt(shown.Count / (float)slots.Count));

    private void RefreshAll()
    {
        RefreshItems();
        RefreshCart();
        RefreshMoney();
        RefreshFilterLook();
    }

    private void RefreshItems()
    {
        BuildPage();

        shown.Clear();
        var catalog = ItemCatalog.Instance;
        if (catalog != null)
        {
            foreach (var def in catalog.Items)
            {
                if (def == null || !def.showInShop || def.worldNetworkPrefab == null) continue;
                if (category.Length > 0 && !string.Equals(def.category, category, StringComparison.Ordinal)) continue;
                shown.Add(def);
            }
        }

        pageIndex = Mathf.Clamp(pageIndex, 0, PageCount - 1);
        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            int index = pageIndex * slots.Count + i;
            bool has = index < shown.Count;
            if (slot.go != null) slot.go.SetActive(has);
            if (!has) continue;

            var def = shown[index];
            if (slot.icon != null && def.icon != null) slot.icon.sprite = def.icon;
            if (slot.nameText != null) slot.nameText.text = def.displayName;
            if (slot.priceText != null) slot.priceText.text = $"${def.price:N0}";
            if (slot.button != null)
            {
                slot.button.onClick.RemoveAllListeners();
                slot.button.onClick.AddListener(() => OnBuyClicked(def));
            }
        }

        if (pageLabel != null) pageLabel.text = $"Page {pageIndex + 1} / {PageCount}";
        if (previousPageButton != null) previousPageButton.interactable = pageIndex > 0;
        if (nextPageButton != null) nextPageButton.interactable = pageIndex < PageCount - 1;
        RefreshFilterLook();
    }

    private void RefreshFilterLook()
    {
        if (filters == null) return;
        foreach (var f in filters)
        {
            if (f?.button == null || f.button.targetGraphic == null) continue;
            bool selected = string.Equals(f.category ?? "", category, StringComparison.Ordinal);
            f.button.targetGraphic.color = selected ? selectedFilterColor : normalFilterColor;
        }
    }

    /* ========================= BUYING ========================= */

    private void OnBuyClicked(ItemDef def)
    {
        if (plot == null) return;

        if (!useCart)
        {
            PlaceOrder(new[] { def.id }, new[] { 1 });
            return;
        }

        int i = cart.FindIndex(c => c.itemId == def.id);
        if (i >= 0) cart[i] = (def.id, cart[i].quantity + 1);
        else cart.Add((def.id, 1));
        ShowStatus($"Added {def.displayName}");
        RefreshCart();
    }

    private void Checkout()
    {
        if (cart.Count == 0 || plot == null) return;
        var ids = new int[cart.Count];
        var qty = new int[cart.Count];
        for (int i = 0; i < cart.Count; i++) { ids[i] = cart[i].itemId; qty[i] = cart[i].quantity; }
        PlaceOrder(ids, qty);
    }

    private void PlaceOrder(int[] itemIds, int[] quantities)
    {
        var eco = EconomyManager.Instance;
        if (eco == null || plot == null)
        {
            ShowStatus("The shop isn't available right now.");
            return;
        }
        waitingForResult = true;
        if (checkoutButton != null) checkoutButton.interactable = false;
        ShowStatus("Ordering...");
        eco.Order(plot.PlotId, itemIds, quantities, GetDeliverySeconds(itemIds, quantities));
    }

    /// <summary>
    /// Delivery time for an order. -1 = the items' own Delivery Seconds. Override (or edit) this
    /// when skills and levels shorten deliveries.
    /// </summary>
    protected virtual float GetDeliverySeconds(int[] itemIds, int[] quantities) => -1f;

    private void ClearCart()
    {
        cart.Clear();
        RefreshCart();
    }

    private void OnOrderResult(EconomyManager.OrderResult result)
    {
        if (!waitingForResult) return; // someone else's order on this machine? (only ours arrive, but be safe)
        waitingForResult = false;

        if (result == EconomyManager.OrderResult.Ordered && useCart) cart.Clear();
        ShowStatus(Describe(result));
        RefreshCart();
    }

    private static string Describe(EconomyManager.OrderResult r) => r switch
    {
        EconomyManager.OrderResult.Ordered => "Ordered! It'll arrive at your shipment area.",
        EconomyManager.OrderResult.NotEnoughMoney => "Not enough money.",
        EconomyManager.OrderResult.NoShipmentAreas => "This store has no shipment area set up.",
        EconomyManager.OrderResult.TooBig => "That order won't fit in your shipment area.",
        EconomyManager.OrderResult.NotYourStore => "This isn't your store.",
        EconomyManager.OrderResult.UnknownItem => "One of those items can't be ordered.",
        EconomyManager.OrderResult.BadQuantity => "Too many items in one order.",
        _ => r.ToString(),
    };

    /* ========================= DISPLAY ========================= */

    private void RefreshCart()
    {
        int total = 0;
        var catalog = ItemCatalog.Instance;
        var lines = new System.Text.StringBuilder();
        foreach (var (itemId, quantity) in cart)
        {
            if (catalog == null || !catalog.TryGet(itemId, out var def)) continue;
            int sub = def.price * quantity;
            total += sub;
            lines.AppendLine($"{def.displayName} x{quantity}   ${sub:N0}");
        }

        int money = EconomyManager.Instance != null ? EconomyManager.Instance.LocalMoney : 0;
        if (cartText != null)
        {
            if (cart.Count == 0) cartText.text = "Cart is empty";
            else
            {
                lines.Append($"Total: ${total:N0}");
                if (total > money) lines.Append("  (not enough money)");
                cartText.text = lines.ToString();
            }
        }

        if (checkoutButton != null) checkoutButton.interactable = cart.Count > 0 && !waitingForResult;
        if (clearCartButton != null) clearCartButton.interactable = cart.Count > 0;
    }

    private void RefreshMoney()
    {
        if (moneyText != null)
            moneyText.text = EconomyManager.Instance != null ? $"${EconomyManager.Instance.LocalMoney:N0}" : "";
        if (cart.Count > 0) RefreshCart(); // the "not enough money" note may have changed
    }

    private void ShowStatus(string message)
    {
        if (statusText == null) return;
        statusText.text = message;
        statusUntil = Time.unscaledTime + statusSeconds;
    }
}
