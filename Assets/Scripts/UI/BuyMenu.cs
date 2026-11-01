using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The shop's buy screen. Lists the ItemCatalog's items (those with Show In Shop ticked), filtered
/// by category, across as many pages as it takes.
///
/// Usual setup: Page Prefab is an empty page with a Grid Layout Group, and Item Slot Prefab is one
/// item tile (with a BuyItemSlot). One tile is made per item; when a page is full a new page is
/// started. How many fit on a page is worked out from the Grid Layout Group (cell size, spacing,
/// padding and the page's size), unless Items Per Page is set.
///
/// Without an Item Slot Prefab, the page prefab's own tiles are reused instead (each tile an Image
/// with a buy Button inside, or a BuyItemSlot), refilled for each page.
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
    [Tooltip("A page: usually an empty object with a Grid Layout Group. Made under Page Parent.")]
    [SerializeField] private GameObject pagePrefab;
    [Tooltip("One item tile (with a BuyItemSlot). One is made per item, inside the pages.")]
    [SerializeField] private BuyItemSlot itemSlotPrefab;
    [Tooltip("Where pages are created.")]
    [SerializeField] private RectTransform pageParent;
    [Tooltip("Tiles per page. 0 = work it out from the page's Grid Layout Group and size.")]
    [SerializeField] private int itemsPerPage;
    [Tooltip("Used when it can't be worked out (no Grid Layout Group, or the page has no size yet).")]
    [SerializeField] private int fallbackItemsPerPage = 8;
    [SerializeField] private Button nextPageButton;
    [SerializeField] private Button previousPageButton;
    [SerializeField] private TMP_Text pageLabel;

    [Header("Debug (read only)")]
    [Tooltip("How many tiles fit on a page the last time the list was built.")]
    [SerializeField] private int tilesPerPageInUse;
    [SerializeField] private int pagesInUse;

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
    private GameObject page;                       // fixed-tiles mode: the one reused page
    private readonly List<Slot> slots = new();     // fixed-tiles mode: its tiles
    private readonly List<GameObject> pages = new(); // slot-prefab mode: one per page
    private bool warnedFallback;
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

        if (itemSlotPrefab == null) BuildPage();
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

    /// <summary>Fixed-tiles mode: make (or use) the one page and find its tiles.</summary>
    private void BuildPage()
    {
        if (page != null || pagePrefab == null || itemSlotPrefab != null) return;

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
        if (itemSlotPrefab != null) ShowCurrentPage();
        else
        {
            FillFixedTiles();
            ShowCurrentPage();
        }
    }

    private int PageCount
    {
        get
        {
            if (itemSlotPrefab != null) return Mathf.Max(1, pages.Count);
            return slots.Count == 0 ? 1 : Mathf.Max(1, Mathf.CeilToInt(shown.Count / (float)slots.Count));
        }
    }

    private void RefreshAll()
    {
        RefreshItems();
        RefreshCart();
        RefreshMoney();
        RefreshFilterLook();
    }

    /// <summary>Re-reads the catalog for the current filter and rebuilds the pages.</summary>
    private void RefreshItems()
    {
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

        if (itemSlotPrefab != null) BuildPagesFromSlots();
        else FillFixedTiles();

        ShowCurrentPage();
        RefreshFilterLook();
    }

    /* ---------- Slot-prefab mode: one tile per item, new page when full ---------- */

    private void BuildPagesFromSlots()
    {
        foreach (var old in pages)
        {
            if (old == null) continue;
            old.SetActive(false); // Destroy happens at the end of the frame; hide it now
            Destroy(old);
        }
        pages.Clear();

        if (pagePrefab == null) return;
        Transform parent = pageParent != null ? pageParent : transform;

        GameObject current = null;
        int perPage = 1, onPage = 0;

        foreach (var def in shown)
        {
            if (current == null || onPage >= perPage)
            {
                current = Instantiate(pagePrefab, parent);
                current.name = $"{pagePrefab.name} {pages.Count + 1}";
                pages.Add(current);
                if (pages.Count == 1) perPage = TilesPerPage((RectTransform)current.transform);
                onPage = 0;
            }

            var tile = Instantiate(itemSlotPrefab, current.transform);
            tile.name = $"{itemSlotPrefab.name} ({def.displayName})";
            FillTile(tile.gameObject, tile.icon, tile.buyButton, tile.nameText, tile.priceText, def);
            onPage++;
        }

        // Always show at least one (empty) page.
        if (pages.Count == 0)
        {
            current = Instantiate(pagePrefab, parent);
            current.name = $"{pagePrefab.name} 1";
            pages.Add(current);
            perPage = TilesPerPage((RectTransform)current.transform);
        }

        tilesPerPageInUse = perPage;
        pagesInUse = pages.Count;
    }

    /// <summary>
    /// How many tiles fit on a page: Items Per Page if set, otherwise columns x rows from the
    /// page's Grid Layout Group (a fixed column/row count is used as-is; the rest comes from
    /// the page size, cell size, spacing and padding).
    /// </summary>
    private int TilesPerPage(RectTransform pageRect)
    {
        if (itemsPerPage > 0) return itemsPerPage;

        var grid = pageRect.GetComponent<GridLayoutGroup>();
        if (grid == null) return Fallback("the page has no Grid Layout Group");

        Canvas.ForceUpdateCanvases(); // make sure the page has its real size
        Rect r = pageRect.rect;
        float w = r.width - grid.padding.horizontal;
        float h = r.height - grid.padding.vertical;

        int cols = grid.constraint == GridLayoutGroup.Constraint.FixedColumnCount
            ? grid.constraintCount
            : Mathf.FloorToInt((w + grid.spacing.x) / Mathf.Max(1f, grid.cellSize.x + grid.spacing.x));
        int rows = grid.constraint == GridLayoutGroup.Constraint.FixedRowCount
            ? grid.constraintCount
            : Mathf.FloorToInt((h + grid.spacing.y) / Mathf.Max(1f, grid.cellSize.y + grid.spacing.y));

        if (cols < 1 || rows < 1) return Fallback($"the page is only {r.width:0}x{r.height:0} (too small for one cell)");
        return cols * rows;
    }

    private int Fallback(string why)
    {
        if (!warnedFallback)
        {
            warnedFallback = true;
            Debug.LogWarning($"[BuyMenu] Can't work out tiles per page ({why}); using Fallback Items Per Page " +
                             $"({fallbackItemsPerPage}). Set Items Per Page to choose it yourself.", this);
        }
        return Mathf.Max(1, fallbackItemsPerPage);
    }

    /* ---------- Fixed-tiles mode: refill the page's own tiles for each page ---------- */

    private void FillFixedTiles()
    {
        BuildPage();
        pageIndex = Mathf.Clamp(pageIndex, 0, PageCount - 1);
        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            int index = pageIndex * slots.Count + i;
            if (index < shown.Count) FillTile(slot.go, slot.icon, slot.button, slot.nameText, slot.priceText, shown[index]);
            else if (slot.go != null) slot.go.SetActive(false);
        }
        tilesPerPageInUse = slots.Count;
        pagesInUse = PageCount;
    }

    private void FillTile(GameObject go, Image icon, Button button, TMP_Text nameText, TMP_Text priceText, ItemDef def)
    {
        if (go != null) go.SetActive(true);
        if (icon != null && def.icon != null) icon.sprite = def.icon;
        if (nameText != null) nameText.text = def.displayName;
        if (priceText != null) priceText.text = $"${def.price:N0}";
        if (button != null)
        {
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(() => OnBuyClicked(def));
        }
    }

    /// <summary>Show only the current page and update the page buttons and label.</summary>
    private void ShowCurrentPage()
    {
        pageIndex = Mathf.Clamp(pageIndex, 0, PageCount - 1);

        if (itemSlotPrefab != null)
            for (int i = 0; i < pages.Count; i++)
                if (pages[i] != null) pages[i].SetActive(i == pageIndex);

        if (pageLabel != null) pageLabel.text = $"Page {pageIndex + 1} / {PageCount}";
        if (previousPageButton != null) previousPageButton.interactable = pageIndex > 0;
        if (nextPageButton != null) nextPageButton.interactable = pageIndex < PageCount - 1;
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
