using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The franchise list, on the Licenses tab (put it on a panel of that screen; it works on its own).
/// Each franchise shows who holds it and for how long, or the live auction: top bid, who has it, time
/// left. Bid offers the lowest bid that wins (hold Shift: Shift Raises times the minimum raise more).
/// The money is taken now and given back if someone outbids you.
///
/// Setup, no layout groups: Row Parent is a Scroll View's Content (anchored top, stretched across);
/// Row Prefab (a FranchiseRow) is stacked by code (Row Height, Row Spacing, Padding). Optional Status Text.
/// </summary>
public class FranchisesPanel : MonoBehaviour
{
    [SerializeField] private FranchiseRow rowPrefab;
    [SerializeField] private RectTransform rowParent;
    [SerializeField, Min(1f)] private float rowHeight = 80f;
    [SerializeField, Min(0f)] private float rowSpacing = 6f;
    [SerializeField, Min(0f)] private float padding = 6f;
    [SerializeField, Min(1)] private int shiftRaises = 5;
    [SerializeField] private TMP_Text statusText;

    private readonly List<FranchiseRow> rows = new();
    private float nextTick;
    private bool built;

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
        var fm = FranchiseManager.Instance;
        if (fm != null) fm.ResultReceived += OnResult;
        Status("");
        nextTick = 0f;
    }

    private void OnDisable()
    {
        var fm = FranchiseManager.Instance;
        if (fm != null) fm.ResultReceived -= OnResult;
    }

    // Times tick, so it simply refreshes a few times a second while shown.
    private void Update()
    {
        if (Time.unscaledTime < nextTick) return;
        nextTick = Time.unscaledTime + 0.25f;
        if (!built) Build();
        Refresh();
    }

    private static bool ShiftHeld => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
    private static ulong LocalTeam =>
        Unity.Netcode.NetworkManager.Singleton != null ? MallManager.GetTeamOf(Unity.Netcode.NetworkManager.Singleton.LocalClientId) : 0;

    private void Build()
    {
        var fm = FranchiseManager.Instance;
        if (fm == null || !fm.IsSpawned || rowPrefab == null || rowParent == null) return;
        built = true;
        int n = 0;
        foreach (var f in fm.Franchises)
        {
            if (f == null) continue;
            var row = Instantiate(rowPrefab, rowParent);
            var def = f;
            if (row.bidButton != null) row.bidButton.onClick.AddListener(() => Bid(def));
            if (row.icon != null && f.icon != null) row.icon.sprite = f.icon;
            if (row.nameText != null) row.nameText.text = f.displayName;

            var rt = (RectTransform)row.transform;
            float top = padding + n * (rowHeight + rowSpacing);
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(padding, -(top + rowHeight));
            rt.offsetMax = new Vector2(-padding, -top);
            rows.Add(row);
            n++;
        }
        rowParent.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, n == 0 ? 0f : padding * 2f + n * rowHeight + (n - 1) * rowSpacing);
    }

    private void Refresh()
    {
        var fm = FranchiseManager.Instance;
        if (fm == null || !fm.IsSpawned) return;
        ulong me = LocalTeam;
        int i = 0;
        foreach (var f in fm.Franchises)
        {
            if (f == null) continue;
            if (i >= rows.Count) break;
            var row = rows[i++];
            if (!fm.TryGetState(f.id, out var s)) continue;

            string holder = s.holder == FranchiseManager.Nobody ? "Not held" : $"Held by {FranchiseManager.TeamLabel(s.holder)}";
            string text;
            if (s.bidding)
            {
                string top = s.topTeam == FranchiseManager.Nobody ? $"starts at ${f.startingBid:N0}" : $"top ${s.topBid:N0} ({FranchiseManager.TeamLabel(s.topTeam)})";
                text = $"{holder}\nBidding: {top}, {Clock(s.auctionEnds - fm.Now)} left";
            }
            else
            {
                double opens = s.heldUntil - f.auctionSeconds - fm.Now;
                text = s.holder == FranchiseManager.Nobody
                    ? $"Not held\nBidding opens in {Clock(opens)}"
                    : $"{holder}: {Clock(s.heldUntil - fm.Now)} left\nBidding opens in {Clock(opens)}";
            }
            if (row.statusText != null) row.statusText.text = text;
            if (row.heldMark != null) row.heldMark.SetActive(s.holder == me);

            int amount = BidAmount(fm, f, s);
            bool can = fm.CanBid(me, f, amount, out string why);
            if (row.bidButton != null) row.bidButton.interactable = can;
            if (row.buttonText != null) row.buttonText.text = can ? $"Bid ${amount:N0}" : why;
        }
    }

    private int BidAmount(FranchiseManager fm, FranchiseDef f, FranchiseManager.State s) =>
        fm.MinimumBid(f, s) + (ShiftHeld ? f.minRaise * shiftRaises : 0);

    private void Bid(FranchiseDef f)
    {
        var fm = FranchiseManager.Instance;
        if (fm == null || !fm.TryGetState(f.id, out var s)) return;
        int amount = BidAmount(fm, f, s);
        if (!fm.CanBid(LocalTeam, f, amount, out string why)) { Status(why); return; }
        fm.RequestBid(f.id, amount);
        Status($"Bidding ${amount:N0} on {f.displayName}...");
    }

    private void OnResult(string result) => Status(string.IsNullOrEmpty(result) ? "You have the top bid." : result);

    private static string Clock(double seconds)
    {
        if (seconds < 0) seconds = 0;
        int s = (int)seconds;
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }

    private void Status(string text)
    {
        if (statusText != null) statusText.text = text;
    }
}
