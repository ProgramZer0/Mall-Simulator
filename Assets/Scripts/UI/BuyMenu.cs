using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The shop's buy screen. Lists the ItemCatalog's items (those with Show In Shop ticked) across as
/// many pages as it takes. A search box filters them: type, then press Filter (or Enter); Clear
/// shows everything again.
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
///
/// With a Paint Bar, the color picked there is the color paintable items (those with PaintParts) are
/// bought in, for free. The same item in two colors is two lines in the cart.
/// </summary>
public class BuyMenu : MonoBehaviour, IShopScreen
{
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

    [Header("Search")]
    [Tooltip("Where the player types what to look for.")]
    [SerializeField] private TMP_InputField filterInput;
    [Tooltip("Applies the search (pressing Enter in the box does too).")]
    [SerializeField] private Button filterButton;
    [Tooltip("Empties the box and shows everything.")]
    [SerializeField] private Button clearFilterButton;
    [Tooltip("Also match the item's Category, not just its name.")]
    [SerializeField] private bool searchCategory = true;

    [Header("Cart")]
    [SerializeField] private bool useCart = true;
    [Tooltip("Lists what's in the cart and the total. Click a line to take one of it out (right-click or " +
             "Shift-click: all of it).")]
    [SerializeField] private TMP_Text cartText;
    [Tooltip("Shown at the end of each cart line, to say it can be clicked off.")]
    [SerializeField] private string removeMark = "  <color=#ff8080>[-]</color>";
    [SerializeField] private Button checkoutButton;
    [SerializeField] private Button clearCartButton;
    [Tooltip("Clear Cart asks first: the first press changes its label to this, a second press within Confirm " +
             "Seconds clears. Empty: clears at once.")]
    [SerializeField] private string clearConfirmLabel = "Clear everything?";
    [SerializeField, Min(0.5f)] private float clearConfirmSeconds = 3f;

    [Header("Paint (optional)")]
    [Tooltip("Color swatches: paintable items are bought in the picked color (free).")]
    [SerializeField] private PaintSwatchBar paintBar;
    [Tooltip("Optional: the pop-up that asks which sizes of a sized item (clothing, shoes). Without it, sized " +
             "items are ordered in their middle size.")]
    [SerializeField] private SizePicker sizePicker;

    [Header("Tiles")]
    [Tooltip("Tiles show just the picture (no name, the buy button without words); hovering one swaps the " +
             "picture for Hover Label and shows the tooltip, which has the name. Off: names and \"Buy\" as set up.")]
    [SerializeField] private bool compactTiles = true;
    [SerializeField] private string hoverLabel = "Click to buy";
    [Tooltip("Hold Shift while clicking buy to add this many at once (sizes: Shift on + or - in the size pop-up).")]
    [SerializeField, Min(1)] private int shiftAmount = 5;

    /// <summary>Shift held: Shift Amount, else 1.</summary>
    public int ClickAmount => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? shiftAmount : 1;

    [Header("Tooltip (optional)")]
    [Tooltip("Hover an item: this panel (hidden at start, Raycast Target off on its graphics) shows its name and " +
             "details beside the tile, fading in and out.")]
    [SerializeField] private TooltipView tooltipPrefab;
    [SerializeField] private GameObject tooltipRoot;
    [SerializeField] private TMP_Text tooltipNameText;
    [SerializeField] private TMP_Text tooltipDescriptionText;
    [SerializeField] private Vector2 tooltipOffset = new(8f, 0f);
    [Tooltip("Optional: the tooltip goes beside the tile without covering this (e.g. the item grid's area).")]
    [SerializeField] private RectTransform tooltipKeepClear;

    [Header("Info")]
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
    private readonly List<(int itemId, byte paint, int quantity)> cart = new();
    private int pageIndex;
    private string filterText = "";
    private bool waitingForResult;
    private float statusUntil;
    private EconomyManager subscribedTo;

    /* ========================= SETUP ========================= */

    [Header("Cart reminder")]
    [Tooltip("Closing the shop with things still in the cart (not ordered): a phone notification saying so.")]
    [SerializeField] private bool remindOfCart = true;

    private ShopUIController shop;
    private Note cartNote; // the reminder, cleared when the cart is ordered or emptied

    // The shop closed (not just another tab): anything left in the cart wasn't ordered.
    private void OnShopClosed()
    {
        if (!remindOfCart || cart.Count == 0) return;
        int things = 0;
        foreach (var c in cart) things += c.quantity;
        string store = plot != null ? plot.DisplayName : "your store";
        if (cartNote != null) Notifications.Clear(cartNote);
        cartNote = Notifications.Post("Cart not ordered",
            $"{things} thing{(things == 1 ? "" : "s")} for {store} still in the cart. Open the shop and press Checkout to order them.");
    }

    private void ForgetCartNote()
    {
        if (cartNote == null) return;
        Notifications.Clear(cartNote);
        cartNote = null;
    }

    private void OnDestroy()
    {
        if (shop != null) shop.Closed -= OnShopClosed;
    }

    private void Awake()
    {
        shop = GetComponentInParent<ShopUIController>(true);
        if (shop == null) shop = FindAnyObjectByType<ShopUIController>(FindObjectsInactive.Include);
        if (shop != null) shop.Closed += OnShopClosed;
        if (cartText != null)
        {
            if (!cartText.TryGetComponent<TextLinkClicks>(out var clicks)) clicks = cartText.gameObject.AddComponent<TextLinkClicks>();
            clicks.Clicked += OnCartLineClicked;
        }
        if (nextPageButton != null) nextPageButton.onClick.AddListener(() => ChangePage(+1));
        if (previousPageButton != null) previousPageButton.onClick.AddListener(() => ChangePage(-1));
        if (checkoutButton != null) checkoutButton.onClick.AddListener(Checkout);
        if (clearCartButton != null) clearCartButton.onClick.AddListener(OnClearPressed);

        if (filterButton != null) filterButton.onClick.AddListener(ApplyFilter);
        if (clearFilterButton != null) clearFilterButton.onClick.AddListener(ClearFilter);
        if (filterInput != null) filterInput.onSubmit.AddListener(_ => ApplyFilter());

        if (itemSlotPrefab == null) BuildPage();
        if (tooltipRoot != null) TooltipFade.Hide(tooltipRoot, instant: true);
    }

    private void OnEnable()
    {
        Subscribe();
        RefreshAll();
    }

    private void OnDisable()
    {
        Unsubscribe();
        DisarmClear();
    }

    private int shownForLevel = -1;

    private void Update()
    {
        if (clearArmedUntil > 0f && Time.unscaledTime >= clearArmedUntil) DisarmClear(); // not pressed again: back to normal
        if (statusText != null && statusUntil > 0f && Time.unscaledTime > statusUntil)
        {
            statusText.text = "";
            statusUntil = 0f;
        }
        // Leveled up while the shop is open: what's newly unlocked shows up.
        int level = ProgressionManager.LevelOf(LocalTeam);
        if (shownForLevel != level)
        {
            bool first = shownForLevel < 0;
            shownForLevel = level;
            if (!first) RefreshItems();
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

    /// <summary>Page, cart and search buttons aren't item tiles, even if they sit inside the page.</summary>
    private bool IsControlButton(Button b) =>
        b == nextPageButton || b == previousPageButton || b == checkoutButton || b == clearCartButton ||
        b == filterButton || b == clearFilterButton;

    private void Subscribe()
    {
        var eco = EconomyManager.Instance;
        if (eco == subscribedTo) return;
        Unsubscribe();
        if (eco == null) return;

        eco.MoneyChanged += RefreshMoney;
        eco.OrderResultReceived += OnOrderResult;
        subscribedTo = eco;
        if (ProgressionManager.Instance != null) ProgressionManager.Instance.LevelChanged += RefreshItems;
        if (LicenseManager.Instance != null) LicenseManager.Instance.Changed += RefreshItems;
        if (FranchiseManager.Instance != null) FranchiseManager.Instance.Changed += RefreshItems;
    }

    private void Unsubscribe()
    {
        if (subscribedTo == null) return;
        subscribedTo.MoneyChanged -= RefreshMoney;
        subscribedTo.OrderResultReceived -= OnOrderResult;
        subscribedTo = null;
        if (ProgressionManager.Instance != null) ProgressionManager.Instance.LevelChanged -= RefreshItems;
        if (LicenseManager.Instance != null) LicenseManager.Instance.Changed -= RefreshItems;
        if (FranchiseManager.Instance != null) FranchiseManager.Instance.Changed -= RefreshItems;
    }

    /* ========================= ITEMS & PAGES ========================= */

    /// <summary>Show only items whose name (or category) contains what's in the search box.</summary>
    public void ApplyFilter()
    {
        filterText = filterInput != null ? filterInput.text.Trim() : "";
        pageIndex = 0;
        RefreshItems();
    }

    /// <summary>Empty the search box and show everything.</summary>
    public void ClearFilter()
    {
        if (filterInput != null) filterInput.text = "";
        filterText = "";
        pageIndex = 0;
        RefreshItems();
    }

    private bool MatchesFilter(ItemDef def)
    {
        if (filterText.Length == 0) return true;
        const StringComparison ignoreCase = StringComparison.OrdinalIgnoreCase;
        if (!string.IsNullOrEmpty(def.displayName) && def.displayName.IndexOf(filterText, ignoreCase) >= 0) return true;
        if (!searchCategory) return false;
        foreach (var c in def.Categories)
            if (c.IndexOf(filterText, ignoreCase) >= 0) return true;
        return false;
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
                if (!MatchesFilter(def)) continue;
                // Only what the team can order: not before its level, nor needing a license it doesn't hold.
                if (!ProgressionManager.CanOrder(LocalTeam, def)) continue;
                if (!LicenseManager.CanOrder(LocalTeam, def, out _)) continue;
                // Franchise items: only for the team holding the franchise.
                if (!FranchiseManager.CanOrder(LocalTeam, def, out _)) continue;
                shown.Add(def);
            }
        }

        if (itemSlotPrefab != null) BuildPagesFromSlots();
        else FillFixedTiles();

        ShowCurrentPage();
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
        if (icon != null) { icon.enabled = true; icon.canvasRenderer.SetAlpha(1f); }
        if (nameText != null)
        {
            nameText.text = def.displayName;
            nameText.gameObject.SetActive(!compactTiles); // compact: the name's in the tooltip
        }
        if (compactTiles && button != null) SetButtonLabel(button, "");
        // Locked until the team's level reaches the item's level (its Min Level, or its categories' unlock level).
        bool levelLocked = !ProgressionManager.CanOrder(LocalTeam, def);
        bool licenseLocked = !LicenseManager.CanOrder(LocalTeam, def, out var license);
        bool locked = levelLocked || licenseLocked;
        if (priceText != null)
            priceText.text = levelLocked ? $"Level {ProgressionManager.LevelNeeded(def, LocalTeam)}"
                : licenseLocked ? $"Needs {license.displayName}"
                : $"${MarketModifiers.OrderCost(def) + MarketModifiers.DeliveryFee(def):N0}";
        if (button != null)
        {
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(() => OnBuyClicked(def));
            button.interactable = !locked;
        }
        if (go != null)
        {
            if (!go.TryGetComponent<BuyItemSlot>(out var slot)) slot = go.AddComponent<BuyItemSlot>(); // (for the tooltip)
            slot.Def = def;
            slot.Menu = this;
            ShowColors(slot.colors, def);
            if (hoveredTile == slot) ShowTooltip(slot); // refilled under the mouse (a new page)
        }
    }

    /* ========================= TOOLTIP ========================= */

    private BuyItemSlot hoveredTile;

    /// <summary>The mouse went onto (or off) an item tile.</summary>
    public void OnTileHover(BuyItemSlot tile, bool over)
    {
        if (over && sizePicker != null && sizePicker.IsOpen) over = false; // picking a size: no tooltips under it
        if (over) { hoveredTile = tile; ShowTooltip(tile); }
        else if (tile == hoveredTile) { hoveredTile = null; if (tooltipRoot != null) TooltipFade.Hide(tooltipRoot); }
        if (compactTiles && tile != null) ShowHoverLabel(tile, over);
    }

    // Compact tiles: hovered, the picture gives way to "Click to buy" (on a tile that can be bought).
    private void ShowHoverLabel(BuyItemSlot tile, bool over)
    {
        var button = tile.buyButton != null ? tile.buyButton : tile.GetComponentInChildren<Button>(true);
        var icon = tile.icon != null ? tile.icon : TileIcon(tile.gameObject, button);
        bool show = over && button != null && button.interactable;
        // Faded out, not switched off: a switched-off picture stops catching the mouse, which reads as the
        // mouse leaving the tile (the tooltip and label would flicker off and on, or not show at all).
        if (icon != null) icon.canvasRenderer.SetAlpha(show ? 0f : 1f);
        if (button != null) SetButtonLabel(button, show ? hoverLabel : "");
    }

    private static Image TileIcon(GameObject tile, Button button)
    {
        foreach (var img in tile.GetComponentsInChildren<Image>(true))
            if (img.gameObject != tile && (button == null || img.gameObject != button.gameObject) && img.sprite != null) return img;
        return null;
    }

    private static void SetButtonLabel(Button button, string text)
    {
        var label = button.GetComponentInChildren<TMP_Text>(true);
        if (label != null) label.text = text;
    }

    /// <summary>The size picker's up: hide the tooltip (it opened over the tiles).</summary>
    private void HideTileTooltip()
    {
        if (hoveredTile != null && compactTiles) ShowHoverLabel(hoveredTile, false);
        hoveredTile = null;
        if (tooltipRoot != null) TooltipFade.Hide(tooltipRoot);
    }

    private void ShowTooltip(BuyItemSlot tile)
    {
        var def = tile != null ? tile.Def : null;
        TooltipView.Fill(transform, tooltipPrefab, ref tooltipRoot, ref tooltipNameText, ref tooltipDescriptionText);
        if (tooltipRoot == null || def == null || !tile.gameObject.activeInHierarchy) return;
        if (tooltipNameText != null) tooltipNameText.text = def.displayName;
        if (tooltipDescriptionText != null) tooltipDescriptionText.text = Describe(def);
        TooltipFade.Show(tooltipRoot);
        tooltipRoot.transform.SetAsLastSibling();
        var cell = (RectTransform)tile.transform;
        if (tooltipKeepClear != null) UILayout.PlaceBeside(tooltipRoot.transform as RectTransform, cell, tooltipOffset, tooltipKeepClear);
        else UILayout.PlaceBeside(tooltipRoot.transform as RectTransform, cell, tooltipOffset);
    }

    // What's worth knowing before buying one.
    private string Describe(ItemDef def)
    {
        var sb = new System.Text.StringBuilder();
        bool levelLocked = !ProgressionManager.CanOrder(LocalTeam, def);
        bool licenseLocked = !LicenseManager.CanOrder(LocalTeam, def, out var license);
        if (levelLocked) sb.AppendLine($"<color=#ff8080>Unlocks at level {ProgressionManager.LevelNeeded(def, LocalTeam)}</color>");
        else if (licenseLocked) sb.AppendLine($"<color=#ff8080>Needs the {license.displayName} license</color>");

        int cost = MarketModifiers.OrderCost(def), fee = MarketModifiers.DeliveryFee(def);
        sb.AppendLine(fee > 0 ? $"${cost:N0} + ${fee:N0} delivery" : $"${cost:N0}");
        int perCrate = StockContainer.GetUnitsPerCrate(def.id);
        if (perCrate > 0) sb.AppendLine($"Stock: {perCrate} to a crate, for shelves");
        if (def.sizes != ItemDef.SizeSet.None) sb.AppendLine($"Comes in sizes ({(def.sizes == ItemDef.SizeSet.Clothing ? "XS to XXL" : "4 to 12")})");
        if (def.rarity != ItemDef.Rarity.Common) sb.AppendLine($"{def.rarity}");
        var cats = new List<string>(def.Categories);
        if (cats.Count > 0) sb.AppendLine(string.Join(", ", cats));
        return sb.ToString().TrimEnd();
    }

    private static readonly List<byte> tileColors = new();

    // The colors an item is limited to (its own or its categories', with its franchise's), or just its
    // franchise's extra colors, as little swatches: only colors the team has unlocked. Nothing shown when
    // it can be any color.
    private static void ShowColors(RectTransform into, ItemDef def)
    {
        if (into == null) return;
        tileColors.Clear();
        if (PaintParts.IsPaintable(def))
            foreach (var c in ColorRules.ShownFor(def))
                if (!PaintPalette.IsLockedHere(c)) tileColors.Add(c);
        var limit = tileColors;
        into.gameObject.SetActive(limit.Count > 0);
        if (limit.Count == 0) return;
        float size = into.rect.height > 1f ? into.rect.height : 14f;
        int i = 0;
        for (; i < limit.Count; i++)
        {
            Image dot;
            if (i < into.childCount) dot = into.GetChild(i).GetComponent<Image>();
            else
            {
                var go = new GameObject("Color", typeof(RectTransform), typeof(Image));
                go.transform.SetParent(into, false);
                dot = go.GetComponent<Image>();
                dot.raycastTarget = false;
                var rt = (RectTransform)go.transform;
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 0.5f);
                rt.sizeDelta = new Vector2(size, size);
            }
            if (dot == null) continue;
            dot.gameObject.SetActive(true);
            PaintPalette.StyleSwatch(dot, limit[i], Color.white, turn: false);
            if (into.GetComponent<LayoutGroup>() == null)
                ((RectTransform)dot.transform).anchoredPosition = new Vector2(i * (size + 2f), 0f);
        }
        for (; i < into.childCount; i++) into.GetChild(i).gameObject.SetActive(false);
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

    /* ========================= BUYING ========================= */

    private static ulong LocalTeam =>
        Unity.Netcode.NetworkManager.Singleton != null ? MallManager.GetTeamOf(Unity.Netcode.NetworkManager.Singleton.LocalClientId) : 0;

    private void OnBuyClicked(ItemDef def)
    {
        if (plot == null) return;
        if (!ProgressionManager.CanOrder(LocalTeam, def)) { ShowStatus($"Reach level {ProgressionManager.LevelNeeded(def, LocalTeam)} to order {def.displayName}."); return; }
        if (!LicenseManager.CanOrder(LocalTeam, def, out var needs)) { ShowStatus($"{def.displayName} needs the {needs.displayName} (Licenses tab)."); return; }
        if (!FranchiseManager.CanOrder(LocalTeam, def, out var franchise)) { ShowStatus($"{def.displayName} belongs to the {franchise.displayName} franchise."); return; }

        // Paintable items come in the picked color; the rest ignore it.
        byte paint = paintBar != null && PaintParts.IsPaintable(def) ? paintBar.Selected : (byte)0;
        // ...if it comes in that color (and it's unlocked).
        if (!ColorRules.Allows(def, paint, LocalTeam, out string why)) { ShowStatus($"{why}."); return; }

        // Sized: which sizes, and how many of each.
        var sizes = ItemCatalog.Instance != null ? ItemCatalog.Instance.GetSizes(def.id) : null;
        if (sizes != null && sizes.Count > 0)
        {
            if (sizePicker != null) { HideTileTooltip(); sizePicker.Open(def, sizes, picked => Buy(picked, paint)); }
            else Buy(new[] { (sizes[Sizes.Middle(def.sizes)], ClickAmount) }, paint);
            return;
        }
        Buy(new[] { (def, ClickAmount) }, paint); // Shift: Shift Amount at once
    }

    // Order these now, or add them to the cart.
    private void Buy(IReadOnlyList<(ItemDef def, int quantity)> what, byte paint)
    {
        if (!useCart)
        {
            var ids = new int[what.Count];
            var qty = new int[what.Count];
            var paints = new byte[what.Count];
            for (int k = 0; k < what.Count; k++) { ids[k] = what[k].def.id; qty[k] = what[k].quantity; paints[k] = paint; }
            PlaceOrder(ids, qty, paints);
            return;
        }

        int count = 0;
        foreach (var (def, quantity) in what)
        {
            int i = cart.FindIndex(c => c.itemId == def.id && c.paint == paint);
            if (i >= 0) cart[i] = (def.id, paint, cart[i].quantity + quantity);
            else cart.Add((def.id, paint, quantity));
            count += quantity;
        }
        string name = what.Count == 1 ? what[0].def.displayName : what[0].def.BaseDef.displayName;
        if (count > 1) name = $"{count} × {name}";
        ShowStatus(paint != 0 ? $"Added {name} ({PaintPalette.NameOf(paint)})" : $"Added {name}");
        RefreshCart();
    }

    private void Checkout()
    {
        if (cart.Count == 0 || plot == null) return;
        var ids = new int[cart.Count];
        var qty = new int[cart.Count];
        var paints = new byte[cart.Count];
        for (int i = 0; i < cart.Count; i++) { ids[i] = cart[i].itemId; qty[i] = cart[i].quantity; paints[i] = cart[i].paint; }
        PlaceOrder(ids, qty, paints);
    }

    private void PlaceOrder(int[] itemIds, int[] quantities, byte[] paints)
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
        eco.Order(plot.PlotId, itemIds, quantities, paints, GetDeliverySeconds(itemIds, quantities));
    }

    /// <summary>
    /// Delivery time for an order. -1 = the items' own Delivery Seconds. Override (or edit) this
    /// when skills and levels shorten deliveries.
    /// </summary>
    protected virtual float GetDeliverySeconds(int[] itemIds, int[] quantities) => -1f;

    // A cart line clicked: one fewer of it (right-click or Shift: all of it).
    private void OnCartLineClicked(string id, UnityEngine.EventSystems.PointerEventData e)
    {
        if (waitingForResult || !int.TryParse(id, out int i) || i < 0 || i >= cart.Count) return;
        bool all = e.button == UnityEngine.EventSystems.PointerEventData.InputButton.Right ||
                   Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        var (itemId, paint, quantity) = cart[i];
        if (all || quantity <= 1) cart.RemoveAt(i);
        else cart[i] = (itemId, paint, quantity - 1);
        RefreshCart();
    }

    // Clear Cart: the first press asks (the button says Clear Confirm Label), the second clears.
    private float clearArmedUntil;
    private string clearLabel;

    private void OnClearPressed()
    {
        if (string.IsNullOrEmpty(clearConfirmLabel) || Time.unscaledTime < clearArmedUntil)
        {
            DisarmClear();
            ClearCart();
            return;
        }
        clearArmedUntil = Time.unscaledTime + clearConfirmSeconds;
        var label = clearCartButton.GetComponentInChildren<TMP_Text>(true);
        if (label != null)
        {
            clearLabel ??= label.text;
            label.text = clearConfirmLabel;
        }
        ShowStatus("Press again to empty the cart.");
    }

    private void DisarmClear()
    {
        clearArmedUntil = 0f;
        if (clearLabel == null || clearCartButton == null) return;
        var label = clearCartButton.GetComponentInChildren<TMP_Text>(true);
        if (label != null) label.text = clearLabel;
    }

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

    public static string Describe(EconomyManager.OrderResult r) => r switch
    {
        EconomyManager.OrderResult.Ordered => "Ordered! It'll arrive at your shipment area.",
        EconomyManager.OrderResult.NotEnoughMoney => "Not enough money.",
        EconomyManager.OrderResult.NoShipmentAreas => "This store has no shipment area set up.",
        EconomyManager.OrderResult.TooBig => "That order won't fit in your shipment area.",
        EconomyManager.OrderResult.NotYourStore => "This isn't your store.",
        EconomyManager.OrderResult.UnknownItem => "One of those items can't be ordered.",
        EconomyManager.OrderResult.BadQuantity => "Too many items in one order.",
        EconomyManager.OrderResult.LevelTooLow => "Your team's level is too low for one of those items.",
        EconomyManager.OrderResult.NeedsLicense => "One of those items needs a license (Licenses tab).",
        EconomyManager.OrderResult.NeedsFranchise => "One of those items belongs to a franchise your team doesn't hold.",
        _ => r.ToString(),
    };

    /* ========================= DISPLAY ========================= */

    private void RefreshCart()
    {
        if (cart.Count == 0) ForgetCartNote(); // ordered or emptied: the reminder's done
        int total = 0;
        var catalog = ItemCatalog.Instance;
        var lines = new System.Text.StringBuilder();
        for (int i = 0; i < cart.Count; i++)
        {
            var (itemId, paint, quantity) = cart[i];
            if (catalog == null || !catalog.TryGet(itemId, out var def)) continue;
            int sub = (MarketModifiers.OrderCost(def) + MarketModifiers.DeliveryFee(def)) * quantity; // with events
            total += sub;
            string color = paint != 0 ? $" ({PaintPalette.NameOf(paint)})" : "";
            // A link per line: clicking it takes one out (see OnCartLineClicked).
            lines.AppendLine($"<link=\"{i}\">{def.displayName}{color} x{quantity}   ${sub:N0}{removeMark}</link>");
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

    // Money itself is shown by MoneyDisplay; here it only matters for the cart's "not enough money" note.
    private void RefreshMoney()
    {
        if (cart.Count > 0) RefreshCart();
    }

    private void ShowStatus(string message)
    {
        if (statusText == null) return;
        statusText.text = message;
        statusUntil = Time.unscaledTime + statusSeconds;
    }
}
