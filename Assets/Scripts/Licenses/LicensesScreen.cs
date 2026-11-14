using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// The shop monitor's Licenses tab (make it a tab's screen on the ShopUIController): every license
/// (LicenseManager → Licenses) as a card in a grid. Hover a card for its details; click to buy it, or
/// (if your team holds it) click to give it up (no money back), after a Yes on the confirm pop-up.
/// Cards that can't be bought are greyed out and the hover pop-up says why.
///
/// Setup, no layout groups: Card Parent is a Scroll View's Content (anchored top, stretched across);
/// Card Prefab (a LicenseCard) is laid out in a grid (Cell Size, Cell Spacing, Padding; as many columns
/// as fit). Tooltip Root: a panel (hidden at start, last on the screen, Raycast Target off on its
/// graphics) with Tooltip Name and Tooltip Description texts. Optional Summary Text and Status Text.
/// Giving something up asks first on the shared ConfirmPopup prefab (Resources/ConfirmPopup), or Popup if
/// set. With neither, giving up takes two clicks.
/// </summary>
public class LicensesScreen : MonoBehaviour, IShopScreen
{
    [SerializeField] private LicenseCard cardPrefab;
    [SerializeField] private RectTransform cardParent;
    [Tooltip("Optional: a second Scroll View's Content for your team's licenses. They move there from Card Parent; " +
             "click one to give it up. Empty: they stay in Card Parent, marked owned.")]
    [SerializeField] private RectTransform ownedCardParent;
    [Tooltip("Franchises your team holds also show in the owned list, as cards in this color (on the card's Tint " +
             "Target); click one to give it up.")]
    [SerializeField] private Color franchiseTint = new(1f, 0.8f, 0.3f, 1f);
    [SerializeField] private Vector2 cellSize = new(80f, 80f);
    [SerializeField] private Vector2 cellSpacing = new(8f, 8f);
    [SerializeField, Min(0f)] private float padding = 6f;

    [SerializeField] private GameObject tooltipRoot;
    [SerializeField] private TMP_Text tooltipNameText;
    [SerializeField] private TMP_Text tooltipDescriptionText;
    [SerializeField] private Vector2 tooltipOffset = new(8f, 0f);

    [SerializeField] private TMP_Text summaryText;
    [SerializeField] private TMP_Text statusText;

    [Tooltip("Optional: this screen's own pop-up (one in the scene, or a prefab). Empty: the shared ConfirmPopup prefab.")]
    [SerializeField] private ConfirmPopup popup;
    private ConfirmPopup popupInstance;
    private bool popupLooked;

    // The pop-up to ask with (null: none, so two clicks instead).
    private ConfirmPopup Popup
    {
        get
        {
            if (popupInstance == null && !popupLooked)
            {
                popupLooked = true;
                popupInstance = ConfirmPopup.For(transform, popup);
            }
            return popupInstance;
        }
    }

    private bool PopupOpen => popupInstance != null && popupInstance.IsOpen;

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
        foreach (var parent in new[] { cardParent, ownedCardParent })
            if (parent != null)
                foreach (var g in parent.GetComponents<UnityEngine.UI.LayoutGroup>()) g.enabled = false;
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
        CloseConfirm();
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
        // Two-click give up times out (the pop-up waits for an answer).
        if (giveUpArmed != 0 && !PopupOpen && Time.unscaledTime > giveUpArmedUntil)
        { giveUpArmed = 0; dirty = true; }
        // Money changes what can be bought; cheap to check now and then.
        if (!dirty && Time.frameCount % 30 != 0) return;
        dirty = false;
        Build(); // cheap: cards are reused, and owned ones move to the owned list
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

        // Each card in its list: yours in the owned list (if there is one), the rest in the main one.
        ulong team = LocalTeam;
        int colsMain = UILayout.ColumnsThatFit(cardParent, cellSize, cellSpacing, padding);
        int colsOwned = ownedCardParent != null ? UILayout.ColumnsThatFit(ownedCardParent, cellSize, cellSpacing, padding) : colsMain;
        int nMain = 0, nOwned = 0;
        for (int i = 0; i < cards.Count; i++)
        {
            bool used = i < shown.Count;
            cards[i].gameObject.SetActive(used);
            if (!used) continue;
            bool owned = ownedCardParent != null && lm != null && lm.IsSpawned && lm.Holds(team, shown[i].id);
            var parent = owned ? ownedCardParent : cardParent;
            if (cards[i].transform.parent != parent)
            {
                cards[i].transform.SetParent(parent, false);
                if (hovered == cards[i]) HideTooltip();
            }
            int slot = owned ? nOwned++ : nMain++;
            UILayout.PlaceInGrid((RectTransform)cards[i].transform, slot, owned ? colsOwned : colsMain, cellSize, cellSpacing, padding);
            if (cards[i].icon != null && shown[i].icon != null) cards[i].icon.sprite = shown[i].icon;
            if (cards[i].nameText != null) cards[i].nameText.text = shown[i].displayName;
            if (cards[i].franchiseMark != null) cards[i].franchiseMark.SetActive(false);
        }
        PlaceFranchiseCards(team, colsOwned, ref nOwned);
        cardParent.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, UILayout.GridHeight(nMain, colsMain, cellSize, cellSpacing, padding));
        if (ownedCardParent != null)
            ownedCardParent.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, UILayout.GridHeight(nOwned, colsOwned, cellSize, cellSpacing, padding));
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
        if (hovered != null)
        {
            if (franchiseCards.Contains(hovered)) OnFranchiseHovered(hovered, true);
            else ShowTooltip(hovered);
        }
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
            if (Popup != null)
            {
                // Ask first.
                int id = l.id;
                HideTooltip();
                Popup.Show($"Give up the {l.displayName}?\nNo money back, and no ordering " +
                           $"{l.CategoryList} items until you buy one again.", () =>
                {
                    LicenseManager.Instance?.RequestGiveUp(id);
                    Status("Giving up the license...");
                });
                return;
            }
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

    private void CloseConfirm()
    {
        giveUpArmed = 0;
        franchiseArmed = 0;
        if (PopupOpen) popupInstance.Close();
    }

    /* ========================= FRANCHISES (OWNED LIST) ========================= */

    private readonly List<LicenseCard> franchiseCards = new();
    private readonly List<FranchiseDef> franchisesShown = new();
    private int franchiseArmed;       // franchise id waiting for Yes (or a second click)
    private float franchiseArmedUntil;

    // Your team's franchises, after its licenses in the owned list, in the Franchise Tint.
    private void PlaceFranchiseCards(ulong team, int cols, ref int slot)
    {
        franchisesShown.Clear();
        var fm = FranchiseManager.Instance;
        if (ownedCardParent != null && fm != null && fm.IsSpawned)
            foreach (var f in fm.Franchises)
                if (f != null && fm.TryGetState(f.id, out var s) && s.holder == team) franchisesShown.Add(f);

        while (franchiseCards.Count < franchisesShown.Count && cardPrefab != null)
        {
            var card = Instantiate(cardPrefab, ownedCardParent);
            int index = franchiseCards.Count;
            if (card.button != null) card.button.onClick.AddListener(() => OnFranchiseClick(index));
            card.Hovered += OnFranchiseHovered;
            if (card.tintTarget != null) card.tintTarget.color = franchiseTint;
            if (card.franchiseMark != null) card.franchiseMark.SetActive(true);
            if (card.ownedMark != null) card.ownedMark.SetActive(true);
            if (card.countText != null) card.countText.text = "";
            franchiseCards.Add(card);
        }
        for (int i = 0; i < franchiseCards.Count; i++)
        {
            bool used = i < franchisesShown.Count;
            franchiseCards[i].gameObject.SetActive(used);
            if (!used) continue;
            var f = franchisesShown[i];
            UILayout.PlaceInGrid((RectTransform)franchiseCards[i].transform, slot++, cols, cellSize, cellSpacing, padding);
            if (franchiseCards[i].icon != null && f.icon != null) franchiseCards[i].icon.sprite = f.icon;
            if (franchiseCards[i].nameText != null) franchiseCards[i].nameText.text = f.displayName;
            if (franchiseCards[i].button != null) franchiseCards[i].button.interactable = true;
        }
        if (franchiseArmed != 0 && !PopupOpen && Time.unscaledTime > franchiseArmedUntil)
            franchiseArmed = 0;
    }

    private void OnFranchiseClick(int index)
    {
        if (index >= franchisesShown.Count) return;
        var f = franchisesShown[index];
        if (Popup != null)
        {
            int id = f.id;
            HideTooltip();
            Popup.Show($"Give up the {f.displayName} franchise?\nNo money back, bidding for it opens right away, " +
                       "and your team can't bid in that auction.", () =>
            {
                FranchiseManager.Instance?.RequestGiveUp(id);
                Status("Giving up the franchise...");
            });
            return;
        }
        if (franchiseArmed != f.id)
        {
            franchiseArmed = f.id;
            franchiseArmedUntil = Time.unscaledTime + 3f;
            Status($"Click again to give up the {f.displayName} franchise.");
            return;
        }
        franchiseArmed = 0;
        FranchiseManager.Instance?.RequestGiveUp(f.id);
        Status($"Giving up {f.displayName}...");
    }

    private void OnFranchiseHovered(LicenseCard card, bool over)
    {
        if (!over) { if (card == hovered) HideTooltip(); return; }
        int index = franchiseCards.IndexOf(card);
        var fm = FranchiseManager.Instance;
        if (tooltipRoot == null || fm == null || index < 0 || index >= franchisesShown.Count) return;
        hovered = card;
        var f = franchisesShown[index];
        fm.TryGetState(f.id, out var s);
        if (tooltipNameText != null) tooltipNameText.text = $"{f.displayName} (franchise)";
        if (tooltipDescriptionText != null)
        {
            string text = string.IsNullOrEmpty(f.description) ? "" : f.description + "\n";
            int left = Mathf.Max(0, (int)(s.heldUntil - fm.Now));
            text += $"Only your team can order: {f.CategoryList} items\nYours for {left / 60}:{left % 60:00} more";
            text += "\n\nClick to give it up (no money back)";
            tooltipDescriptionText.text = text;
        }
        tooltipRoot.SetActive(true);
        tooltipRoot.transform.SetAsLastSibling();
        UILayout.PlaceBeside(tooltipRoot.transform as RectTransform, (RectTransform)card.transform, tooltipOffset);
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
            text += $"Unlocks: {l.CategoryList} items\nPrice: ${l.price:N0}";
            if (l.perMall > 0) text += $"\nHeld: {lm.HolderCount(l.id)} of {l.perMall} in the mall";
            if (l.minLevel > 1) text += $"\nNeeds level {l.minLevel}";
            if (mine) text += giveUpArmed == l.id
                ? "\n\nClick again to give it up (no money back)"
                : $"\n\nYours. Click{(Popup != null ? "" : " twice")} to give it up (no money back; a help point if you haven't used it in a while)";
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
