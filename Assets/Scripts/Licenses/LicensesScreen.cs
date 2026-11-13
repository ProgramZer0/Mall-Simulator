using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// The shop monitor's Licenses tab (make it a tab's screen on the ShopUIController): every license
/// (LicenseManager → Licenses) as a card in a grid. Hover a card for its details; click to buy it, or
/// (if your team holds it) click twice to give it up (no money back). Cards that can't be bought are greyed out and
/// the pop-up says why.
///
/// Setup, no layout groups: Card Parent is a Scroll View's Content (anchored top, stretched across);
/// Card Prefab (a LicenseCard) is laid out in a grid (Cell Size, Cell Spacing, Padding; as many columns
/// as fit). Tooltip Root: a panel (hidden at start, last on the screen, Raycast Target off on its
/// graphics) with Tooltip Name and Tooltip Description texts. Optional Summary Text and Status Text.
/// </summary>
public class LicensesScreen : MonoBehaviour, IShopScreen
{
    [SerializeField] private LicenseCard cardPrefab;
    [SerializeField] private RectTransform cardParent;
    [SerializeField] private Vector2 cellSize = new(80f, 80f);
    [SerializeField] private Vector2 cellSpacing = new(8f, 8f);
    [SerializeField, Min(0f)] private float padding = 6f;

    [SerializeField] private GameObject tooltipRoot;
    [SerializeField] private TMP_Text tooltipNameText;
    [SerializeField] private TMP_Text tooltipDescriptionText;
    [SerializeField] private Vector2 tooltipOffset = new(8f, 0f);

    [SerializeField] private TMP_Text summaryText;
    [SerializeField] private TMP_Text statusText;

    private readonly List<LicenseCard> cards = new();
    private readonly List<LicenseDef> shown = new();
    private LicenseManager subscribedTo;
    private LicenseCard hovered;
    private int giveUpArmed;        // license id waiting for its second click to give up (0 = none)
    private float giveUpArmedUntil;
    private bool dirty;

    private void Awake()
    {
        if (tooltipRoot != null) tooltipRoot.SetActive(false);
        if (cardParent != null)
            foreach (var g in cardParent.GetComponents<UnityEngine.UI.LayoutGroup>()) g.enabled = false;
    }

    private void OnEnable()
    {
        Subscribe();
        dirty = true;
    }

    private void OnDisable()
    {
        if (subscribedTo != null)
        {
            subscribedTo.Changed -= MarkDirty;
            subscribedTo.ResultReceived -= OnResult;
        }
        subscribedTo = null;
        HideTooltip();
    }

    public void OnShopOpened(StorePlot plot)
    {
        Status("");
        giveUpArmed = 0;
        Subscribe();
        Build();
    }

    private void Subscribe()
    {
        var lm = LicenseManager.Instance;
        if (lm == subscribedTo) return;
        OnDisable();
        subscribedTo = lm;
        if (lm != null)
        {
            lm.Changed += MarkDirty;
            lm.ResultReceived += OnResult;
        }
    }

    private void MarkDirty() => dirty = true;

    private void Update()
    {
        if (giveUpArmed != 0 && Time.unscaledTime > giveUpArmedUntil) { giveUpArmed = 0; dirty = true; }
        // Money changes what can be bought; cheap to check now and then.
        if (!dirty && Time.frameCount % 30 != 0) return;
        dirty = false;
        if (shown.Count == 0) Build(); else Refresh();
    }

    private static ulong LocalTeam =>
        Unity.Netcode.NetworkManager.Singleton != null ? MallManager.GetTeamOf(Unity.Netcode.NetworkManager.Singleton.LocalClientId) : 0;

    private void Build()
    {
        shown.Clear();
        var lm = LicenseManager.Instance;
        if (lm != null) foreach (var l in lm.Licenses) if (l != null) shown.Add(l);
        if (cardPrefab == null || cardParent == null) { Refresh(); return; }

        while (cards.Count < shown.Count)
        {
            var card = Instantiate(cardPrefab, cardParent);
            int index = cards.Count;
            if (card.button != null) card.button.onClick.AddListener(() => OnClick(index));
            card.Hovered += OnHovered;
            cards.Add(card);
        }
        int cols = UILayout.ColumnsThatFit(cardParent, cellSize, cellSpacing, padding);
        for (int i = 0; i < cards.Count; i++)
        {
            bool used = i < shown.Count;
            cards[i].gameObject.SetActive(used);
            if (!used) continue;
            UILayout.PlaceInGrid((RectTransform)cards[i].transform, i, cols, cellSize, cellSpacing, padding);
            if (cards[i].icon != null && shown[i].icon != null) cards[i].icon.sprite = shown[i].icon;
            if (cards[i].nameText != null) cards[i].nameText.text = shown[i].displayName;
        }
        cardParent.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, UILayout.GridHeight(shown.Count, cols, cellSize, cellSpacing, padding));
        Refresh();
    }

    private void Refresh()
    {
        var lm = LicenseManager.Instance;
        if (lm == null || !lm.IsSpawned) return;
        ulong team = LocalTeam;

        if (summaryText != null)
            summaryText.text = $"Your team holds {lm.HeldBy(team)} of {lm.AllowedFor(team)} licenses";

        for (int i = 0; i < shown.Count && i < cards.Count; i++)
        {
            var l = shown[i];
            bool mine = lm.Holds(team, l.id);
            if (cards[i].button != null) cards[i].button.interactable = mine || lm.CanBuy(team, l, out _);
            if (cards[i].ownedMark != null) cards[i].ownedMark.SetActive(mine);
            if (cards[i].countText != null)
                cards[i].countText.text = l.perMall > 0 ? $"{Mathf.Max(0, l.perMall - lm.HolderCount(l.id))} left" : "";
        }
        if (hovered != null) ShowTooltip(hovered);
    }

    /* ========================= CLICKS ========================= */

    private void OnClick(int index)
    {
        var lm = LicenseManager.Instance;
        if (lm == null || index >= shown.Count) return;
        var l = shown[index];
        ulong team = LocalTeam;

        if (lm.Holds(team, l.id))
        {
            if (giveUpArmed != l.id)
            {
                giveUpArmed = l.id;
                giveUpArmedUntil = Time.unscaledTime + 3f;
                Status($"Click again to give up {l.displayName} (no money back).");
                dirty = true;
                return;
            }
            giveUpArmed = 0;
            lm.RequestGiveUp(l.id);
            Status($"Giving up {l.displayName}...");
            return;
        }
        if (!lm.CanBuy(team, l, out string why)) { Status(why); return; }
        lm.RequestBuy(l.id);
        Status($"Buying {l.displayName}...");
    }

    private void OnResult(string result) => Status(string.IsNullOrEmpty(result) ? "Done." : result);

    /* ========================= TOOLTIP ========================= */

    private void OnHovered(LicenseCard card, bool over)
    {
        if (over) ShowTooltip(card);
        else if (card == hovered) HideTooltip();
    }

    private void ShowTooltip(LicenseCard card)
    {
        hovered = card;
        int index = cards.IndexOf(card);
        var lm = LicenseManager.Instance;
        if (tooltipRoot == null || lm == null || index < 0 || index >= shown.Count) { HideTooltip(); return; }
        var l = shown[index];
        ulong team = LocalTeam;
        bool mine = lm.Holds(team, l.id);

        if (tooltipNameText != null) tooltipNameText.text = l.displayName;
        if (tooltipDescriptionText != null)
        {
            string text = string.IsNullOrEmpty(l.description) ? "" : l.description + "\n";
            text += $"Unlocks: {l.category} items\nPrice: ${l.price:N0}";
            if (l.perMall > 0) text += $"\nHeld: {lm.HolderCount(l.id)} of {l.perMall} in the mall";
            if (l.minLevel > 1) text += $"\nNeeds level {l.minLevel}";
            if (mine) text += giveUpArmed == l.id
                ? "\n\nClick again to give it up (no money back)"
                : "\n\nYours. Click twice to give it up (no money back; a help point if you never used it)";
            else text += lm.CanBuy(team, l, out string why) ? "\n\nClick to buy" : $"\n\nCan't buy: {why}";
            tooltipDescriptionText.text = text;
        }
        tooltipRoot.SetActive(true);
        tooltipRoot.transform.SetAsLastSibling();
        UILayout.PlaceBeside(tooltipRoot.transform as RectTransform, (RectTransform)card.transform, tooltipOffset);
    }

    private void HideTooltip()
    {
        hovered = null;
        if (tooltipRoot != null) tooltipRoot.SetActive(false);
    }

    private void Status(string text)
    {
        if (statusText != null) statusText.text = text;
    }
}
