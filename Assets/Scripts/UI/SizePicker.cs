using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The buy menu's size pop-up (see BuyMenu → Size Picker): clicking a sized item (clothing, shoes)
/// opens it. Pick how many of each size with − / +, then Add.
///
/// Setup: a panel (Root, hidden at start) on the shop screen. Option Prefab (a SizeOption) is made once
/// per size into Option Parent, placed in a grid by code (Columns, Cell Size, Spacing): no layout group.
/// Optional: Title Text, Total Text, Add Button (+ its TMP text), Cancel Button.
/// </summary>
public class SizePicker : MonoBehaviour
{
    [SerializeField] private GameObject root;
    [SerializeField] private TMP_Text titleText;
    [SerializeField] private SizeOption optionPrefab;
    [SerializeField] private RectTransform optionParent;
    [SerializeField, Min(1)] private int columns = 3;
    [SerializeField] private Vector2 cellSize = new(120f, 70f);
    [SerializeField] private Vector2 spacing = new(6f, 6f);
    [SerializeField] private TMP_Text totalText;
    [SerializeField] private Button addButton;
    [SerializeField] private TMP_Text addButtonText;
    [SerializeField] private Button cancelButton;
    [SerializeField, Min(1)] private int maxPerSize = 99;

    private readonly List<SizeOption> options = new();
    private readonly List<ItemDef> sizes = new();
    private readonly List<int> counts = new();
    private Action<IReadOnlyList<(ItemDef def, int quantity)>> onAdd;

    public bool IsOpen => root != null && root.activeSelf;

    private void Awake()
    {
        if (addButton != null) addButton.onClick.AddListener(Add);
        if (cancelButton != null) cancelButton.onClick.AddListener(Close);
        if (optionParent != null)
            foreach (var g in optionParent.GetComponents<LayoutGroup>()) g.enabled = false; // we place them
        if (root != null) root.SetActive(false);
    }

    /// <summary>Ask which sizes of an item; Add calls back with each size and how many (only those above 0).</summary>
    public void Open(ItemDef item, IReadOnlyList<ItemDef> itemSizes, Action<IReadOnlyList<(ItemDef def, int quantity)>> added)
    {
        if (root == null || itemSizes == null || itemSizes.Count == 0) return;
        onAdd = added;
        sizes.Clear();
        counts.Clear();
        foreach (var s in itemSizes) { sizes.Add(s); counts.Add(0); }
        if (titleText != null) titleText.text = item.displayName;

        while (options.Count < sizes.Count && optionPrefab != null && optionParent != null)
        {
            var o = Instantiate(optionPrefab, optionParent);
            int index = options.Count;
            if (o.minusButton != null) o.minusButton.onClick.AddListener(() => Change(index, -1));
            if (o.plusButton != null) o.plusButton.onClick.AddListener(() => Change(index, +1));
            options.Add(o);
        }
        for (int i = 0; i < options.Count; i++)
        {
            bool used = i < sizes.Count;
            options[i].gameObject.SetActive(used);
            if (!used) continue;
            Place((RectTransform)options[i].transform, i);
            if (options[i].sizeText != null) options[i].sizeText.text = sizes[i].SizeName;
            if (options[i].priceText != null) options[i].priceText.text = $"${CostOf(sizes[i]):N0}";
        }
        if (optionParent != null)
        {
            int rowsUsed = (sizes.Count + columns - 1) / columns;
            optionParent.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, rowsUsed * cellSize.y + Mathf.Max(0, rowsUsed - 1) * spacing.y);
        }
        root.SetActive(true);
        root.transform.SetAsLastSibling();
        Refresh();
    }

    public void Close()
    {
        if (root != null) root.SetActive(false);
        onAdd = null;
    }

    private void Place(RectTransform rt, int i)
    {
        int col = i % columns, row = i / columns;
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = cellSize;
        rt.anchoredPosition = new Vector2(col * (cellSize.x + spacing.x), -row * (cellSize.y + spacing.y));
    }

    private static int CostOf(ItemDef def) => MarketModifiers.OrderCost(def) + MarketModifiers.DeliveryFee(def);

    private void Change(int index, int by)
    {
        if (index < 0 || index >= counts.Count) return;
        counts[index] = Mathf.Clamp(counts[index] + by, 0, maxPerSize);
        Refresh();
    }

    private void Refresh()
    {
        int total = 0, items = 0;
        for (int i = 0; i < sizes.Count && i < options.Count; i++)
        {
            total += counts[i] * CostOf(sizes[i]);
            items += counts[i];
            if (options[i].quantityText != null) options[i].quantityText.text = counts[i].ToString();
            if (options[i].minusButton != null) options[i].minusButton.interactable = counts[i] > 0;
        }
        if (totalText != null) totalText.text = items > 0 ? $"{items} item{(items == 1 ? "" : "s")}   ${total:N0}" : "Pick sizes";
        if (addButton != null) addButton.interactable = items > 0;
        if (addButtonText != null) addButtonText.text = items > 0 ? $"Add {items}" : "Add";
    }

    private void Add()
    {
        var picked = new List<(ItemDef, int)>();
        for (int i = 0; i < sizes.Count; i++) if (counts[i] > 0) picked.Add((sizes[i], counts[i]));
        var callback = onAdd;
        Close();
        if (picked.Count > 0) callback?.Invoke(picked);
    }

    private void OnDisable() => Close();
}
