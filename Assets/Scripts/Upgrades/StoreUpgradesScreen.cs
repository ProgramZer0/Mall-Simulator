using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The shop monitor's Upgrades tab (make it a tab's screen on the ShopUIController): rename the store,
/// buy its expansions, upgrade its cart, call the cart home. See StoreUpgrades.
///
/// Setup, no layout groups:
/// - Name: a TMP Input Field (Name Input) and a Rename Button. Enter in the field renames too.
/// - Rows: Row Parent is a Scroll View's Content (anchored top, stretched across); Row Prefab (an
///   UpgradeRow) is stacked by code (Row Height, Row Spacing, Padding). One row per expansion, one for
///   the next cart, one to call the cart home.
/// - Optional Status Text.
/// </summary>
public class StoreUpgradesScreen : MonoBehaviour, IShopScreen
{
    [Header("Name")]
    [SerializeField] private TMP_InputField nameInput;
    [SerializeField] private Button renameButton;

    [Header("Rows")]
    [SerializeField] private UpgradeRow rowPrefab;
    [SerializeField] private RectTransform rowParent;
    [SerializeField, Min(1f)] private float rowHeight = 70f;
    [SerializeField, Min(0f)] private float rowSpacing = 6f;
    [SerializeField, Min(0f)] private float padding = 6f;
    [SerializeField] private Sprite expansionIcon;
    [SerializeField] private Sprite recallIcon;

    [SerializeField] private TMP_Text statusText;

    private StorePlot plot;
    private readonly List<UpgradeRow> rows = new();
    private readonly List<Action> clicks = new(); // what each row's button does now
    private bool dirty;

    private void Awake()
    {
        if (renameButton != null) renameButton.onClick.AddListener(Rename);
        if (nameInput != null) nameInput.onSubmit.AddListener(_ => Rename());
        if (rowParent != null)
        {
            foreach (var g in rowParent.GetComponents<LayoutGroup>()) g.enabled = false;
            foreach (var f in rowParent.GetComponents<ContentSizeFitter>()) f.enabled = false;
        }
    }

    private void OnEnable()
    {
        if (StoreUpgrades.Instance != null)
        {
            StoreUpgrades.Instance.Changed += MarkDirty;
            StoreUpgrades.Instance.ResultReceived += OnResult;
        }
        if (EconomyManager.Instance != null) EconomyManager.Instance.MoneyChanged += MarkDirty;
        dirty = true;
    }

    private void OnDisable()
    {
        if (StoreUpgrades.Instance != null)
        {
            StoreUpgrades.Instance.Changed -= MarkDirty;
            StoreUpgrades.Instance.ResultReceived -= OnResult;
        }
        if (EconomyManager.Instance != null) EconomyManager.Instance.MoneyChanged -= MarkDirty;
        Watch(null);
    }

    public void OnShopOpened(StorePlot forPlot)
    {
        Watch(forPlot);
        Status("");
        if (nameInput != null)
        {
            nameInput.text = plot != null ? plot.DisplayName : "";
            if (StoreUpgrades.Instance != null) nameInput.characterLimit = StoreUpgrades.Instance.MaxNameLength;
        }
        dirty = true;
    }

    private void Watch(StorePlot p)
    {
        if (plot != null) plot.Changed -= OnPlotChanged;
        plot = p;
        if (plot != null) plot.Changed += OnPlotChanged;
    }

    private void OnPlotChanged(StorePlot p) => dirty = true;
    private void MarkDirty() => dirty = true;

    private void Update()
    {
        if (!dirty) return;
        dirty = false;
        Rebuild();
    }

    /* ========================= ROWS ========================= */

    private void Rebuild()
    {
        clicks.Clear();
        if (plot == null || rowPrefab == null || rowParent == null) return;
        int n = 0;
        var up = StoreUpgrades.Instance;
        int money = EconomyManager.Instance != null ? EconomyManager.Instance.GetTeamMoney(plot.OwnerTeamId) : int.MaxValue;

        // Expansions.
        for (int i = 0; i < plot.SegmentCount; i++)
        {
            int index = i;
            bool owned = plot.IsSegmentPurchased(i);
            int price = plot.GetSegmentPrice(i);
            bool canBuy = !owned && money >= price;
            Row(n++, plot.GetSegmentName(i), "More floor space for the store.", owned ? "" : $"${price:N0}",
                owned ? "Owned" : canBuy ? "Buy" : "Not enough money", canBuy, expansionIcon,
                () =>
                {
                    MallManager.Instance?.RequestBuySegmentServerRpc(plot.PlotId, index);
                    Status($"Buying {plot.GetSegmentName(index)}...");
                });
        }

        // Cart.
        if (up != null)
        {
            int tier = up.GetCartTier(plot.PlotId);
            var next = up.NextCartTier(plot.PlotId);
            if (next != null)
            {
                bool can = up.CanBuyCartTier(plot, out string why);
                Row(n++, $"{up.CartTierName(tier)} → {next.name}", next.description, $"${next.price:N0}",
                    can ? "Upgrade" : why, can, next.icon,
                    () => { up.RequestBuyCartTier(plot.PlotId); Status($"Upgrading to {next.name}..."); });
            }
            else
                Row(n++, up.CartTierName(tier), "The biggest cart there is.", "", "Max", false, null, null);

            Row(n++, "Call cart home", "Sends your store's carts back to their spot, with whatever's on them.", "Free",
                "Call", true, recallIcon,
                () => { up.RequestRecallCarts(plot.PlotId); Status("Cart called home."); });
        }

        for (int i = n; i < rows.Count; i++) rows[i].gameObject.SetActive(false);
        float height = n == 0 ? 0f : padding * 2f + n * rowHeight + (n - 1) * rowSpacing;
        rowParent.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
    }

    private void Row(int i, string name, string description, string price, string buttonLabel, bool interactable,
                     Sprite icon, Action onClick)
    {
        while (rows.Count <= i)
        {
            var made = Instantiate(rowPrefab, rowParent);
            int index = rows.Count;
            if (made.button != null) made.button.onClick.AddListener(() => { if (index < clicks.Count) clicks[index]?.Invoke(); });
            rows.Add(made);
        }
        while (clicks.Count <= i) clicks.Add(null);
        clicks[i] = onClick;

        var row = rows[i];
        row.gameObject.SetActive(true);
        var rt = (RectTransform)row.transform;
        float top = padding + i * (rowHeight + rowSpacing);
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.offsetMin = new Vector2(padding, -(top + rowHeight));
        rt.offsetMax = new Vector2(-padding, -top);

        if (row.nameText != null) row.nameText.text = name;
        if (row.descriptionText != null) row.descriptionText.text = description;
        if (row.priceText != null) row.priceText.text = price;
        if (row.buttonText != null) row.buttonText.text = buttonLabel;
        if (row.button != null) row.button.interactable = interactable && onClick != null;
        if (row.icon != null && icon != null) row.icon.sprite = icon;
    }

    /* ========================= NAME ========================= */

    private void Rename()
    {
        var up = StoreUpgrades.Instance;
        if (up == null || plot == null || nameInput == null) return;
        string name = up.CleanName(nameInput.text);
        if (name == plot.DisplayName) return;
        up.RequestRename(plot.PlotId, name);
        Status(name.Length == 0 ? $"Back to {plot.DefaultName}..." : $"Renaming to {name}...");
    }

    private void OnResult(string result)
    {
        Status(string.IsNullOrEmpty(result) ? "Done." : result);
        if (nameInput != null && plot != null && !nameInput.isFocused) nameInput.text = plot.DisplayName;
    }

    private void Status(string text)
    {
        if (statusText != null) statusText.text = text;
    }
}
