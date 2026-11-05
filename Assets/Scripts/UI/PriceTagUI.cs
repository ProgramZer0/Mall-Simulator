using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The price tag window the price scanner opens. Put it on your Canvas and wire up the parts; only
/// Root, Price Input and Set Button are needed, the rest are optional. Without one in the scene, the
/// scanner shows a plain built-in window instead, so pricing works before the UI is made.
///
/// While it's open the player can't move and the cursor is free (it counts as an open menu).
/// </summary>
public class PriceTagUI : MonoBehaviour
{
    public static PriceTagUI Instance { get; private set; }

    [SerializeField] private GameObject root;
    [Tooltip("Product name.")]
    [SerializeField] private TMP_Text titleText;
    [Tooltip("Cost, current price, and so on.")]
    [SerializeField] private TMP_Text infoText;
    [SerializeField] private TMP_InputField priceInput;
    [Tooltip("Price just this shelf (or this item).")]
    [SerializeField] private Button setButton;
    [Tooltip("Price every one of this product in the store.")]
    [SerializeField] private Button setAllButton;
    [Tooltip("Optional: the Set All button's label, filled in with the product name.")]
    [SerializeField] private TMP_Text setAllLabel;
    [Tooltip("Take the price off (not for sale).")]
    [SerializeField] private Button clearButton;
    [SerializeField] private Button closeButton;
    [Tooltip("Fills in the default price: a safe price that makes a small profit, but rarely the best one.")]
    [SerializeField] private Button defaultButton;
    [Tooltip("Optional: the Default button's label, filled in with the price (e.g. \"Default $6.00\").")]
    [SerializeField] private TMP_Text defaultLabel;
    [Tooltip("Shows problems, e.g. a price that isn't a number.")]
    [SerializeField] private TMP_Text messageText;

    public bool IsOpen { get; private set; }

    private Action<int, bool> onSet; // (cents, every one in the store)
    private int defaultCents;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[PriceTagUI] More than one in the scene; using the first.", this);
            return;
        }
        Instance = this;

        if (setButton != null) setButton.onClick.AddListener(() => Submit(false));
        if (setAllButton != null) setAllButton.onClick.AddListener(() => Submit(true));
        if (clearButton != null) clearButton.onClick.AddListener(() => { onSet?.Invoke(0, false); Close(); });
        if (closeButton != null) closeButton.onClick.AddListener(Close);
        if (priceInput != null) priceInput.onSubmit.AddListener(_ => Submit(false));
        if (defaultButton != null)
            defaultButton.onClick.AddListener(() =>
            {
                if (priceInput != null) priceInput.text = (defaultCents / 100f).ToString("0.00");
            });

        if (root != null) root.SetActive(false);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        UIState.CloseMenu(this);
    }

    private void Update()
    {
        if (IsOpen && Input.GetKeyDown(KeyCode.Escape)) Close();
    }

    /// <summary>
    /// Show the window for a product. defaultPrice: the safe default (filled in if it has no price yet).
    /// onSet gets the price in cents (0 = not for sale) and whether to price all of them.
    /// </summary>
    public void Open(string productName, int currentCents, int cost, int defaultPrice, Action<int, bool> onSet)
    {
        this.onSet = onSet;
        defaultCents = defaultPrice;
        IsOpen = true;
        if (root != null) root.SetActive(true);
        UIState.OpenMenu(this);

        if (titleText != null) titleText.text = productName;
        if (infoText != null)
            infoText.text = $"Cost each: {Sellable.FormatPrice(cost)}\nNow: {Sellable.FormatPrice(currentCents)}";
        if (setAllLabel != null) setAllLabel.text = $"Set for all {productName}";
        if (defaultLabel != null) defaultLabel.text = $"Default {Sellable.FormatPrice(defaultPrice)}";
        if (messageText != null) messageText.text = "";
        if (priceInput != null)
        {
            int shown = currentCents > 0 ? currentCents : defaultPrice;
            priceInput.text = shown > 0 ? (shown / 100f).ToString("0.00") : "";
            priceInput.Select();
            priceInput.ActivateInputField();
        }
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        onSet = null;
        if (root != null) root.SetActive(false);
        UIState.CloseMenu(this);
    }

    private void Submit(bool all)
    {
        if (priceInput == null || !Sellable.TryParsePrice(priceInput.text, out int cents))
        {
            if (messageText != null) messageText.text = "Type a price, e.g. 12.50";
            return;
        }
        onSet?.Invoke(cents, all);
        Close();
    }
}
