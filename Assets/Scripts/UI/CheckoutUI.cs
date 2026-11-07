using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The checkout payment window, on your own Canvas. Opens when a player presses F on a counter whose
/// customer is ready to pay. Without it in the scene, the counter shows a plain built-in window.
///
/// Cash: the customer hands over some money; give back the right change with the denomination
/// buttons (each adds its amount), then Give Change. Too little and they wait; too much is lost.
/// Card: type the total into Card Amount and press Charge. More than the total and they refuse;
/// less is lost.
///
/// Setup (only Root is required; the rest is used if assigned):
/// - Root, Title Text, Total Text, Message Text, Close Button.
/// - Cash Panel: Tendered Text, Change Due Text, Given Text, Denominations (a Button and its value in
///   cents each, e.g. 2000 for $20, 25 for a quarter), Clear Button, Give Button.
/// - Card Panel: Card Amount Input (TMP_InputField), Charge Button.
/// While open, the player can't move or look around (UIState).
/// </summary>
public class CheckoutUI : MonoBehaviour
{
    [Serializable]
    public class Denomination
    {
        public Button button;
        [Tooltip("Its value in cents: 2000 = $20, 100 = $1, 25 = a quarter.")]
        public int cents = 100;
    }

    public static CheckoutUI Instance { get; private set; }

    [SerializeField] private GameObject root;
    [SerializeField] private TMP_Text titleText;
    [SerializeField] private TMP_Text totalText;
    [SerializeField] private TMP_Text messageText;
    [SerializeField] private Button closeButton;

    [Header("Cash")]
    [SerializeField] private GameObject cashPanel;
    [SerializeField] private TMP_Text tenderedText;
    [SerializeField] private TMP_Text changeDueText;
    [SerializeField] private TMP_Text givenText;
    [SerializeField] private List<Denomination> denominations = new();
    [SerializeField] private Button clearButton;
    [SerializeField] private Button giveButton;

    [Header("Card")]
    [SerializeField] private GameObject cardPanel;
    [SerializeField] private TMP_InputField cardAmountInput;
    [SerializeField] private Button chargeButton;

    private int given;

    /// <summary>The counter being paid at (null when closed).</summary>
    public CheckoutCounter Counter { get; private set; }

    public bool IsOpen => root != null && root.activeSelf;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[CheckoutUI] More than one in the scene; using the first.", this);
            return;
        }
        Instance = this;

        foreach (var d in denominations)
        {
            if (d?.button == null) continue;
            int cents = d.cents;
            d.button.onClick.AddListener(() => { given += cents; Refresh(); });
        }
        if (clearButton != null) clearButton.onClick.AddListener(() => { given = 0; Refresh(); });
        if (giveButton != null) giveButton.onClick.AddListener(() => Counter?.RequestPayCashRpc(given));
        if (chargeButton != null) chargeButton.onClick.AddListener(Charge);
        if (closeButton != null) closeButton.onClick.AddListener(Close);
        if (root != null) root.SetActive(false);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        UIState.CloseMenu(this);
    }

    private void Update()
    {
        if (!IsOpen) return;
        // The sale finished or the counter went away (edit mode, someone else took the payment).
        if (Counter == null || !Counter.IsSpawned || Counter.Current.phase != CheckoutCounter.Phase.AwaitingPayment)
        {
            Close();
            return;
        }
        if (Input.GetKeyDown(KeyCode.Escape)) Close();
    }

    public void Open(CheckoutCounter counter)
    {
        Counter = counter;
        given = 0;
        if (cardAmountInput != null) cardAmountInput.text = "";
        if (messageText != null) messageText.text = "";
        if (root != null) root.SetActive(true);
        UIState.OpenMenu(this);
        Refresh();
    }

    public void Close()
    {
        if (root != null) root.SetActive(false);
        Counter = null;
        UIState.CloseMenu(this);
    }

    /// <summary>The host's answer to a payment: closes on success, shows why on failure.</summary>
    public void ShowResult(CheckoutCounter counter, bool ok, string message)
    {
        if (ok && counter == Counter) Close();
        if (messageText != null) messageText.text = ok ? "" : message;
    }

    private void Charge()
    {
        if (Counter == null) return;
        if (cardAmountInput != null && Sellable.TryParsePrice(cardAmountInput.text, out int cents)) Counter.RequestPayCardRpc(cents);
        else if (messageText != null) messageText.text = "Enter an amount like 12.50";
    }

    private void Refresh()
    {
        if (Counter == null) return;
        var s = Counter.Current;
        bool cash = s.method == CheckoutCounter.PayMethod.Cash;
        if (cashPanel != null) cashPanel.SetActive(cash);
        if (cardPanel != null) cardPanel.SetActive(!cash);
        if (titleText != null) titleText.text = cash ? "Cash payment" : "Card payment";
        if (totalText != null) totalText.text = $"Total: {CheckoutCounter.FormatAmount(s.totalCents)}";
        if (tenderedText != null) tenderedText.text = $"Customer gave {CheckoutCounter.FormatAmount(s.tenderedCents)}";
        if (changeDueText != null) changeDueText.text = $"Change due: {CheckoutCounter.FormatAmount(s.ChangeDueCents)}";
        if (givenText != null) givenText.text = $"Change given: {CheckoutCounter.FormatAmount(given)}";
    }
}
