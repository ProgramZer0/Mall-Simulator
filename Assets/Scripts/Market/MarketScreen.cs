using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The shop monitor's Market tab (make it a tab's screen on the ShopUIController): the special
/// market's deals (see SpecialMarket). Buy takes one (hold Shift for Shift Amount); it's delivered to
/// this store like any order. Deals your team isn't licensed for aren't shown; ones that need a higher
/// level show locked.
///
/// Setup, no layout groups: Row Parent is a Scroll View's Content (anchored top, stretched across);
/// Row Prefab (a MarketDealRow) is stacked by code (Row Height, Row Spacing, Padding). Optional Empty
/// Text ("No deals right now") and Status Text.
/// </summary>
public class MarketScreen : MonoBehaviour, IShopScreen
{
    [SerializeField] private MarketDealRow rowPrefab;
    [SerializeField] private RectTransform rowParent;
    [SerializeField, Min(1f)] private float rowHeight = 70f;
    [SerializeField, Min(0f)] private float rowSpacing = 6f;
    [SerializeField, Min(0f)] private float padding = 6f;
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
        if (rowParent != null)
        {
            foreach (var g in rowParent.GetComponents<LayoutGroup>()) g.enabled = false;
            foreach (var f in rowParent.GetComponents<ContentSizeFitter>()) f.enabled = false;
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

        if (rowPrefab != null && rowParent != null)
        {
            while (rows.Count < shown.Count)
            {
                var row = Instantiate(rowPrefab, rowParent);
                int index = rows.Count;
                if (row.buyButton != null) row.buyButton.onClick.AddListener(() => Buy(index));
                rows.Add(row);
            }
            int money = EconomyManager.Instance != null ? EconomyManager.Instance.GetTeamMoney(LocalTeam) : int.MaxValue;
            for (int i = 0; i < rows.Count; i++)
            {
                bool used = i < shown.Count;
                rows[i].gameObject.SetActive(used);
                if (!used) continue;
                var d = shown[i];
                catalog.TryGet(d.itemId, out var def);
                var row = rows[i];

                var rt = (RectTransform)row.transform;
                float top = padding + i * (rowHeight + rowSpacing);
                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(1f, 1f);
                rt.pivot = new Vector2(0.5f, 1f);
                rt.offsetMin = new Vector2(padding, -(top + rowHeight));
                rt.offsetMax = new Vector2(-padding, -top);

                if (row.icon != null && def.icon != null) row.icon.sprite = def.icon;
                if (row.nameText != null) row.nameText.text = def.displayName;
                if (row.priceText != null)
                    row.priceText.text = d.MarketOnly ? $"${d.price:N0}  Market only" : $"${d.price:N0}  (was ${d.usualPrice:N0})";
                if (row.leftText != null) row.leftText.text = $"{d.left} of {d.total} left";

                bool levelOk = ProgressionManager.CanOrder(LocalTeam, def);
                bool can = levelOk && money >= d.price;
                if (row.buyButton != null) row.buyButton.interactable = can;
                if (row.buttonText != null) row.buttonText.text = !levelOk ? $"Level {def.minLevel}" : money < d.price ? "Not enough money" : "Buy";
            }
            float height = shown.Count == 0 ? 0f : padding * 2f + shown.Count * rowHeight + (shown.Count - 1) * rowSpacing;
            rowParent.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
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
