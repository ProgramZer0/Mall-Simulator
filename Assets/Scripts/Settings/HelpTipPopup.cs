using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// The first-time help alert (see HelpTips): "Learn more about Stocking shelves" in the top left for a
/// few seconds. Bringing up the phone while it's up (or just after) opens Help on that topic. It also
/// watches for the firsts (holding a crate, the first mall event, a lost child...) and triggers their tips.
///
/// Setup: on the HUD canvas. Root: the alert panel (anchor it top left; hidden until needed), Text: its
/// text. Optional Canvas Group on Root to fade it (added if missing). With no Root, tips go to the
/// notifications instead. Purely local.
///
/// The firsts it watches, by Help topic Id (topics without that Id are skipped):
/// controls (on joining), your-store, customers, ordering, stocking, placing, carts, prices, painting,
/// editing, checkout, events, votes, franchises, levels, help-points, lost-children, broken.
/// Anything else: call HelpTips.Trigger("id") where it happens.
/// </summary>
public class HelpTipPopup : MonoBehaviour
{
    [SerializeField] private GameObject root;
    [SerializeField] private TMP_Text text;
    [Tooltip("{0}: the topic's title.")]
    [SerializeField] private string format = "<b>New!</b> Learn more about {0}\n<size=80%>Esc: open the phone</size>";
    [SerializeField, Min(1f)] private float showSeconds = 8f;
    [Tooltip("After it's gone, bringing up the phone still opens the topic for this long (seconds).")]
    [SerializeField, Min(0f)] private float graceSeconds = 6f;
    [SerializeField, Min(0f)] private float fadeSeconds = 0.3f;
    [Tooltip("Wait between two tips in a row (seconds).")]
    [SerializeField, Min(0f)] private float gapSeconds = 1.5f;
    [Tooltip("The welcome tip (controls) comes this long after joining (seconds).")]
    [SerializeField, Min(0f)] private float welcomeDelay = 4f;

    private CanvasGroup fade;
    private float shownAt = -1f, nextAt, nextCheck, joinedAt = -1f;
    private readonly HashSet<string> done = new(); // this play: already triggered (or seen), stop checking

    private void Awake()
    {
        if (root != null)
        {
            if (!root.TryGetComponent(out fade)) fade = root.AddComponent<CanvasGroup>();
            fade.blocksRaycasts = false;
            fade.interactable = false;
            if (root != gameObject) root.SetActive(false); else fade.alpha = 0f;
        }
    }

    private void OnEnable() => HelpTips.Reset += done.Clear;
    private void OnDisable() => HelpTips.Reset -= done.Clear;

    private void Update()
    {
        float now = Time.unscaledTime;
        if (now >= nextCheck) { nextCheck = now + 0.5f; Watch(); }

        // The one up now.
        if (shownAt >= 0f)
        {
            float t = now - shownAt;
            if (t >= showSeconds || PhoneController.IsOpen) { Hide(); nextAt = now + gapSeconds; }
            else if (fade != null && fadeSeconds > 0f) fade.alpha = Mathf.Clamp01(Mathf.Min(t, showSeconds - t) / fadeSeconds);
            return;
        }
        // The next one waiting (not while the phone's up or another menu's open).
        if (now < nextAt || PhoneController.IsOpen || UIState.IsMenuOpen) return;
        if (HelpTips.TryNext(out string id)) Present(id);
    }

    private void Present(string id)
    {
        string title = HelpApp.TitleOf(id) ?? id;
        HelpTips.SetPending(id, showSeconds + graceSeconds);
        if (root == null || text == null)
        {
            Notifications.Post($"Learn more about {title}", "Bring up the phone (Esc) to read about it in Help.");
            nextAt = Time.unscaledTime + gapSeconds;
            return;
        }
        text.text = string.Format(format, title);
        root.SetActive(true);
        if (fade != null) fade.alpha = 0f;
        shownAt = Time.unscaledTime;
    }

    private void Hide()
    {
        shownAt = -1f;
        if (root == null) return;
        if (root != gameObject) root.SetActive(false);
        else if (fade != null) fade.alpha = 0f;
    }

    /* ========================= THE FIRSTS ========================= */

    private void Watch()
    {
        var nm = Unity.Netcode.NetworkManager.Singleton;
        var me = nm != null && nm.IsListening ? nm.LocalClient?.PlayerObject : null;
        if (me == null) { joinedAt = -1f; return; }
        if (joinedAt < 0f) joinedAt = Time.unscaledTime;
        ulong client = nm.LocalClientId;
        ulong team = MallManager.GetTeamOf(client);

        if (Time.unscaledTime - joinedAt >= welcomeDelay) First("controls", true);
        First("your-store", OwnsAStore(team));
        First("customers", CustomerManager.Instance != null && CustomerManager.Instance.CustomersShown > 0);

        var inventory = me.GetComponent<PlayerInventoryNet>();
        int held = inventory != null ? inventory.HeldItemId.Value : 0;
        bool crate = held != 0 && StockContainer.IsStockCrate(held);
        First("stocking", crate);
        First("placing", held != 0 && !crate);
        First("carts", CartNet.GetPushedBy(client) != null);
        First("prices", PriceScanner.LocalActive);
        First("painting", PaintBrush.LocalActive);
        var edit = me.GetComponent<StoreEditController>();
        First("editing", edit != null && edit.IsEditing);
        First("ordering", ShopUIController.Instance != null && ShopUIController.Instance.IsOpen);
        if (!done.Contains("checkout")) First("checkout", CheckoutBusy(client));

        First("events", AnyEvent());
        First("votes", MallVoteManager.Instance != null && MallVoteManager.Instance.IsOpen);
        if (!done.Contains("franchises")) First("franchises", AnyAuction());
        First("levels", ProgressionManager.LevelOf(team) >= 2);
        First("help-points", HelpPoints.Instance != null && HelpPoints.Instance.LocalPoints > 0);
        First("lost-children", CustomerManager.Instance != null && CustomerManager.Instance.LostCases.Count > 0);
        var breaks = BreakManager.Instance;
        First("broken", breaks != null && breaks.IsSpawned &&
                        breaks.Count(BreakManager.State.Broken) + breaks.Count(BreakManager.State.Reported) > 0);
    }

    private void First(string id, bool happened)
    {
        if (!happened || done.Contains(id)) return;
        done.Add(id);
        HelpTips.Trigger(id); // (does nothing if seen before, or there's no such topic)
    }

    private static bool OwnsAStore(ulong team)
    {
        var mall = MallManager.Instance;
        if (mall == null) return false;
        foreach (var p in mall.Plots) if (p != null && p.HasOwner && p.OwnerTeamId == team) return true;
        return false;
    }

    private static readonly List<MallEventDef> events = new();

    private static bool AnyEvent()
    {
        var em = MallEventManager.Instance;
        if (em == null) return false;
        em.GetActive(events);
        foreach (var e in events) if (e != null && e != em.CurrentSeason) return true; // seasons don't count
        return false;
    }

    private static bool AnyAuction()
    {
        var fm = FranchiseManager.Instance;
        if (fm == null || !fm.IsSpawned) return false;
        foreach (var f in fm.Franchises)
            if (f != null && fm.TryGetState(f.id, out var s) && s.bidding) return true;
        return false;
    }

    private float nextCounterLook;

    // A customer at one of your checkouts (looked for every few seconds, until it happens).
    private bool CheckoutBusy(ulong client)
    {
        if (Time.unscaledTime < nextCounterLook) return false;
        nextCounterLook = Time.unscaledTime + 3f;
        foreach (var c in FindObjectsByType<CheckoutCounter>(FindObjectsSortMode.None))
            if (c != null && c.IsSpawned && c.IsBusy && c.CanUse(client)) return true;
        return false;
    }
}
