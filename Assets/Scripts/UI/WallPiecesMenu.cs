using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Store edit mode's list of things that go IN a wall: windows, doors, and anything else with a
/// WallOpening on its world prefab. N opens it. It fills itself from the ItemCatalog like the buy
/// menu, one tile per item, starting a new page when one is full; new items show up by themselves.
///
/// Setup (on a Canvas with an EventSystem; the shop's pieces can be reused):
/// - Root: the panel shown and hidden.
/// - Page Prefab: an empty page with a Grid Layout Group. Item Slot Prefab: one tile with a
///   BuyItemSlot (Assets/Prefabs/UI/ItemSlotPrefab works). Page Parent: where pages go.
/// - Optional: page buttons and label, All / Windows / Doors filter buttons, a close button, and
///   texts for money, what's due on save and short messages.
///
/// Clicking a tile picks the piece; it then follows the mouse along the store's walls. Without this
/// menu in the scene, edit mode shows a simple built-in list instead.
/// </summary>
public class WallPiecesMenu : MonoBehaviour
{
    public static WallPiecesMenu Instance { get; private set; }

    public enum Filter { All, Windows, Doors }

    [SerializeField] private GameObject root;

    [Header("Pages")]
    [Tooltip("A page: usually an empty object with a Grid Layout Group. Made under Page Parent.")]
    [SerializeField] private GameObject pagePrefab;
    [Tooltip("One tile (with a BuyItemSlot). One is made per piece.")]
    [SerializeField] private BuyItemSlot itemSlotPrefab;
    [SerializeField] private RectTransform pageParent;
    [Tooltip("Tiles per page. 0 = work it out from the page's Grid Layout Group and size.")]
    [SerializeField] private int itemsPerPage;
    [SerializeField] private int fallbackItemsPerPage = 8;
    [SerializeField] private Button nextPageButton;
    [SerializeField] private Button previousPageButton;
    [SerializeField] private TMP_Text pageLabel;

    [Header("Filter (optional)")]
    [SerializeField] private Button allButton;
    [SerializeField] private Button windowsButton;
    [SerializeField] private Button doorsButton;

    [Header("Info (optional)")]
    [SerializeField] private Button closeButton;
    [SerializeField] private TMP_Text moneyText;
    [Tooltip("What the pieces placed this session will cost when edit mode is saved.")]
    [SerializeField] private TMP_Text dueText;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private float statusSeconds = 3f;

    private readonly List<GameObject> pages = new();
    private readonly List<(ItemDef def, Button button)> tiles = new();
    private readonly List<ItemDef> shown = new();
    private int pageIndex;
    private Filter filter;
    private float statusUntil;
    private bool warnedFallback;

    private Action<ItemDef> onPick;
    private Func<int> moneyLeft;
    private Func<int> due;

    public bool IsOpen => root != null && root.activeSelf;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[WallPiecesMenu] More than one in the scene; using the first.", this);
            return;
        }
        Instance = this;

        if (nextPageButton != null) nextPageButton.onClick.AddListener(() => ChangePage(+1));
        if (previousPageButton != null) previousPageButton.onClick.AddListener(() => ChangePage(-1));
        if (allButton != null) allButton.onClick.AddListener(() => SetFilter(Filter.All));
        if (windowsButton != null) windowsButton.onClick.AddListener(() => SetFilter(Filter.Windows));
        if (doorsButton != null) doorsButton.onClick.AddListener(() => SetFilter(Filter.Doors));
        if (closeButton != null) closeButton.onClick.AddListener(Close);
        if (root != null) root.SetActive(false);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Update()
    {
        if (!IsOpen) return;
        if (statusText != null && statusUntil > 0f && Time.unscaledTime > statusUntil)
        {
            statusText.text = "";
            statusUntil = 0f;
        }
        RefreshMoney(); // money and prices can change while it's open (other players, events)
    }

    /* ========================= OPEN / CLOSE ========================= */

    /// <summary>
    /// Show the menu. On Pick is called with the piece clicked; Money Left says what can still be
    /// spent (money minus what's due); Due is what's owed on save.
    /// </summary>
    public void Open(Action<ItemDef> onPick, Func<int> moneyLeft, Func<int> due)
    {
        this.onPick = onPick;
        this.moneyLeft = moneyLeft;
        this.due = due;
        if (root != null) root.SetActive(true);
        Rebuild();
    }

    public void Close()
    {
        if (root != null) root.SetActive(false);
        onPick = null;
    }

    public void ShowStatus(string message)
    {
        if (statusText == null) return;
        statusText.text = message;
        statusUntil = Time.unscaledTime + statusSeconds;
    }

    /// <summary>Every catalog item that goes in a wall (whatever Show In Shop says).</summary>
    public static void FindPieces(List<ItemDef> into, Filter filter = Filter.All)
    {
        into.Clear();
        var catalog = ItemCatalog.Instance;
        if (catalog == null) return;
        foreach (var def in catalog.Items)
        {
            if (def == null || !WallOpening.TryGet(def, out var o)) continue;
            if (filter == Filter.Doors && !o.isDoor) continue;
            if (filter == Filter.Windows && o.isDoor) continue;
            into.Add(def);
        }
    }

    private void SetFilter(Filter f)
    {
        filter = f;
        pageIndex = 0;
        Rebuild();
    }

    /* ========================= PAGES ========================= */

    private void Rebuild()
    {
        FindPieces(shown, filter);

        foreach (var old in pages)
        {
            if (old == null) continue;
            old.SetActive(false);
            Destroy(old);
        }
        pages.Clear();
        tiles.Clear();

        if (pagePrefab == null || itemSlotPrefab == null)
        {
            Debug.LogWarning("[WallPiecesMenu] Needs a Page Prefab and an Item Slot Prefab.", this);
            return;
        }
        Transform parent = pageParent != null ? pageParent : transform;

        GameObject current = null;
        int perPage = 1, onPage = 0;
        foreach (var def in shown)
        {
            if (current == null || onPage >= perPage)
            {
                current = NewPage(parent);
                if (pages.Count == 1) perPage = TilesPerPage((RectTransform)current.transform);
                onPage = 0;
            }

            var tile = Instantiate(itemSlotPrefab, current.transform);
            tile.name = $"{itemSlotPrefab.name} ({def.displayName})";
            if (tile.icon != null && def.icon != null) tile.icon.sprite = def.icon;
            if (tile.nameText != null)
                tile.nameText.text = WallOpening.TryGet(def, out var o) && o.isDoor ? $"{def.displayName} (door)" : def.displayName;
            if (tile.buyButton != null)
            {
                var picked = def;
                tile.buyButton.onClick.AddListener(() => Pick(picked));
                tiles.Add((def, tile.buyButton));
            }
            if (tile.priceText != null) tile.priceText.text = $"${MarketModifiers.OrderCost(def):N0}";
            onPage++;
        }
        if (pages.Count == 0) NewPage(parent);

        ShowCurrentPage();
        RefreshMoney();
        if (shown.Count == 0)
            ShowStatus(filter == Filter.All ? "Nothing in the catalog goes in a wall yet." : "None of these yet.");
    }

    private GameObject NewPage(Transform parent)
    {
        var page = Instantiate(pagePrefab, parent);
        page.name = $"{pagePrefab.name} {pages.Count + 1}";
        pages.Add(page);
        return page;
    }

    private int TilesPerPage(RectTransform pageRect)
    {
        if (itemsPerPage > 0) return itemsPerPage;
        var grid = pageRect.GetComponent<GridLayoutGroup>();
        if (grid != null)
        {
            Canvas.ForceUpdateCanvases();
            Rect r = pageRect.rect;
            int cols = grid.constraint == GridLayoutGroup.Constraint.FixedColumnCount ? grid.constraintCount
                : Mathf.FloorToInt((r.width - grid.padding.horizontal + grid.spacing.x) / Mathf.Max(1f, grid.cellSize.x + grid.spacing.x));
            int rows = grid.constraint == GridLayoutGroup.Constraint.FixedRowCount ? grid.constraintCount
                : Mathf.FloorToInt((r.height - grid.padding.vertical + grid.spacing.y) / Mathf.Max(1f, grid.cellSize.y + grid.spacing.y));
            if (cols >= 1 && rows >= 1) return cols * rows;
        }
        if (!warnedFallback)
        {
            warnedFallback = true;
            Debug.LogWarning($"[WallPiecesMenu] Can't work out tiles per page; using {fallbackItemsPerPage}.", this);
        }
        return Mathf.Max(1, fallbackItemsPerPage);
    }

    private void ChangePage(int delta)
    {
        pageIndex += delta;
        ShowCurrentPage();
    }

    private void ShowCurrentPage()
    {
        int count = Mathf.Max(1, pages.Count);
        pageIndex = Mathf.Clamp(pageIndex, 0, count - 1);
        for (int i = 0; i < pages.Count; i++)
            if (pages[i] != null) pages[i].SetActive(i == pageIndex);
        if (pageLabel != null) pageLabel.text = $"Page {pageIndex + 1} / {count}";
        if (previousPageButton != null) previousPageButton.interactable = pageIndex > 0;
        if (nextPageButton != null) nextPageButton.interactable = pageIndex < count - 1;
    }

    /* ========================= MONEY ========================= */

    private void RefreshMoney()
    {
        int left = moneyLeft != null ? moneyLeft() : int.MaxValue;
        foreach (var (def, button) in tiles)
            if (button != null) button.interactable = MarketModifiers.OrderCost(def) <= left;

        var econ = EconomyManager.Instance;
        if (moneyText != null) moneyText.text = econ != null && econ.IsSpawned ? $"${econ.LocalMoney:N0}" : "";
        if (dueText != null) dueText.text = due != null ? $"Paid when you save: ${due():N0}" : "";
    }

    private void Pick(ItemDef def)
    {
        int left = moneyLeft != null ? moneyLeft() : int.MaxValue;
        if (MarketModifiers.OrderCost(def) > left)
        {
            ShowStatus("Not enough money");
            return;
        }
        onPick?.Invoke(def);
    }
}
