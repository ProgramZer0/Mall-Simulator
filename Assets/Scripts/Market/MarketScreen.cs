using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The shop monitor's Market tab (make it a tab's screen on the ShopUIController): the special
/// market's deals (see SpecialMarket) as cards in a grid (9 deals: 3 × 3). Each deal is a set size and
/// color. Buy takes one (hold Shift for Shift Amount); it's delivered to this store like any order.
/// Deals your team isn't licensed for aren't shown; ones that need a higher level show locked.
///
/// Setup, no layout groups: Card Parent is a Scroll View's Content, or any panel (anchored top);
/// Card Prefab (a MarketDealRow) is laid out in a grid by code (Cell Size, Cell Spacing, Padding;
/// Columns, or as many as fit). Optional Empty Text ("No deals right now") and Status Text.
/// </summary>
public class MarketScreen : MonoBehaviour, IShopScreen
{
    [UnityEngine.Serialization.FormerlySerializedAs("cardPrefab")]
    [SerializeField] private MarketDealRow cardPrefab;
    [UnityEngine.Serialization.FormerlySerializedAs("cardParent")]
    [SerializeField] private RectTransform cardParent;
    [SerializeField] private Vector2 cellSize = new(160f, 200f);
    [SerializeField] private Vector2 cellSpacing = new(8f, 8f);
    [SerializeField, Min(0f)] private float padding = 6f;
    [Tooltip("Columns in the grid (0 = as many as fit).")]
    [SerializeField, Min(0)] private int columns = 3;
    [SerializeField, Min(1)] private int shiftAmount = 5;
    [SerializeField] private GameObject emptyText;
    [SerializeField] private TMP_Text statusText;

    private StorePlot plot;
    private readonly List<MarketDealRow> rows = new();
    private readonly List<SpecialMarket.Deal> shown = new();
    private bool dirty;
    private float nextTick;

    private void Awake()
    {
        if (cardParent != null)
        {
            foreach (var g in cardParent.GetComponents<LayoutGroup>()) g.enabled = false;
            foreach (var f in cardParent.GetComponents<ContentSizeFitter>()) f.enabled = false;
        }
    }

    private void OnEnable()
    {
        var m = SpecialMarket.Instance;
        if (m != null) { m.Changed += MarkDirty; m.ResultReceived += OnResult; }
        if (EconomyManager.Instance != null) EconomyManager.Instance.MoneyChanged += MarkDirty;
        if (LicenseManager.Instance != null) LicenseManager.Instance.Changed += MarkDirty;
        dirty = true;
    }

    private void OnDisable()
    {
        var m = SpecialMarket.Instance;
        if (m != null) { m.Changed -= MarkDirty; m.ResultReceived -= OnResult; }
        if (EconomyManager.Instance != null) EconomyManager.Instance.MoneyChanged -= MarkDirty;
        if (LicenseManager.Instance != null) LicenseManager.Instance.Changed -= MarkDirty;
    }

    public void OnShopOpened(StorePlot forPlot)
    {
        plot = forPlot;
        Status("");
        dirty = true;
    }

    private void MarkDirty() => dirty = true;

    private void Update()
    {
        if (dirty) { dirty = false; Rebuild(); }
        else if (Time.unscaledTime >= nextTick) { nextTick = Time.unscaledTime + 1f; RefreshTimes(); }
    }

    private static ulong LocalTeam =>
        Unity.Netcode.NetworkManager.Singleton != null ? MallManager.GetTeamOf(Unity.Netcode.NetworkManager.Singleton.LocalClientId) : 0;

    private void Rebuild()
    {
        shown.Clear();
        var m = SpecialMarket.Instance;
        var catalog = ItemCatalog.Instance;
        if (m != null && catalog != null)
            foreach (var d in m.DealsNow)
                if (d.left > 0 && catalog.TryGet(d.itemId, out var def) && LicenseManager.CanOrder(LocalTeam, def, out _))
                    shown.Add(d);

        if (cardPrefab != null && cardParent != null)
        {
            while (rows.Count < shown.Count)
            {
                var row = Instantiate(cardPrefab, cardParent);
                int index = rows.Count;
                if (row.buyButton != null) row.buyButton.onClick.AddListener(() => Buy(index));
                rows.Add(row);
            }
            int money = EconomyManager.Instance != null ? EconomyManager.Instance.GetTeamMoney(LocalTeam) : int.MaxValue;
            int cols = columns > 0 ? columns : UILayout.ColumnsThatFit(cardParent, cellSize, cellSpacing, padding);
            for (int i = 0; i < rows.Count; i++)
            {
                bool used = i < shown.Count;
                rows[i].gameObject.SetActive(used);
                if (!used) continue;
                var d = shown[i];
                catalog.TryGet(d.itemId, out var def);
                var row = rows[i];

                UILayout.PlaceInGrid((RectTransform)row.transform, i, cols, cellSize, cellSpacing, padding);

                if (row.icon != null && def.icon != null) row.icon.sprite = def.icon;
                if (row.nameText != null) row.nameText.text = def.displayName;
                if (row.priceText != null)
                    row.priceText.text = d.MarketOnly ? $"${d.price:N0}  Market only" : $"${d.price:N0}  (was ${d.usualPrice:N0})";
                if (row.leftText != null) row.leftText.text = $"{d.left} of {d.total} left";
                // The color it comes in (no picking another).
                if (row.colorSwatch != null)
                {
                    row.colorSwatch.gameObject.SetActive(d.paint != 0);
                    if (d.paint != 0) PaintPalette.StyleSwatch(row.colorSwatch, d.paint, Color.white);
                }
                if (row.colorText != null) row.colorText.text = d.paint != 0 ? PaintPalette.NameOf(d.paint) : "";

                bool levelOk = ProgressionManager.CanOrder(LocalTeam, def);
                bool can = levelOk && money >= d.price;
                if (row.buyButton != null) row.buyButton.interactable = can;
                if (row.buttonText != null) row.buttonText.text = !levelOk ? $"Level {def.minLevel}" : money < d.price ? "Not enough money" : "Buy";
            }
            cardParent.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, UILayout.GridHeight(shown.Count, cols, cellSize, cellSpacing, padding));
        }
        if (emptyText != null) emptyText.SetActive(shown.Count == 0);
        RefreshTimes();
    }

    private void RefreshTimes()
    {
        var m = SpecialMarket.Instance;
        if (m == null) return;
        for (int i = 0; i < shown.Count && i < rows.Count; i++)
        {
            if (rows[i].timeText == null) continue;
            float s = m.SecondsLeft(shown[i]);
            rows[i].timeText.text = $"Ends in {(int)(s / 60f)}:{(int)(s % 60f):00}";
        }
    }

    private void Buy(int index)
    {
        var m = SpecialMarket.Instance;
        if (m == null || plot == null || index >= shown.Count) return;
        var d = shown[index];
        int qty = Mathf.Min(d.left, Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? shiftAmount : 1);
        m.RequestBuy(plot.PlotId, d.dealId, qty);
        Status("Buying...");
    }

    private void OnResult(EconomyManager.OrderResult result) =>
        Status(result == EconomyManager.OrderResult.BadQuantity ? "That deal just ran out or ended." : BuyMenu.Describe(result));

    private void Status(string text)
    {
        if (statusText != null) statusText.text = text;
    }
}
