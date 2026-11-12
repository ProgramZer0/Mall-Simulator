using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The shop monitor's Perks tab: the store's perk points and the perk tree (ProgressionManager →
/// Perks). One tab per Branch; the chosen branch's perks are listed below it. Make it a tab's screen on
/// the ShopUIController.
///
/// No layout groups needed: the screen places everything itself.
/// - Tab Bar: an empty RectTransform. Tab Prefab (a Button with a TMP label) is made once per branch,
///   side by side, filling the bar. The chosen tab's button is turned off (it shows its Disabled color).
/// - Row Parent: a Scroll View's Content (anchored top, stretched across). Row Prefab (a PerkRow) is
///   stacked top to bottom, Row Height tall with Row Spacing between; Content grows to fit, so it
///   scrolls. A perk that needs another comes after it, indented by Indent.
/// Optional: Summary Text (team level, store profit, points), Reset Button (free respec; click twice
/// to confirm) with its TMP text, Status Text.
/// </summary>
public class PerksScreen : MonoBehaviour, IShopScreen
{
    [Header("Tabs")]
    [SerializeField] private Button tabPrefab;
    [SerializeField] private RectTransform tabBar;
    [SerializeField, Min(0f)] private float tabSpacing = 4f;
    [Tooltip("Added to a tab's name when something in it can be bought.")]
    [SerializeField] private string canBuyMark = " •";

    [Header("Perk List")]
    [SerializeField] private PerkRow rowPrefab;
    [SerializeField] private RectTransform rowParent;
    [SerializeField, Min(1f)] private float rowHeight = 70f;
    [SerializeField, Min(0f)] private float rowSpacing = 6f;
    [SerializeField, Min(0f)] private float padding = 6f;
    [Tooltip("How far a perk that needs another is moved in, per step.")]
    [SerializeField, Min(0f)] private float indent = 24f;

    [Header("Other")]
    [SerializeField] private TMP_Text summaryText;
    [SerializeField] private Button resetButton;
    [SerializeField] private TMP_Text resetButtonText;
    [SerializeField] private TMP_Text statusText;

    private StorePlot plot;
    private readonly List<string> branches = new();
    private readonly List<(Button button, TMP_Text label)> tabs = new();
    private readonly List<PerkRow> rows = new();          // reused across tabs
    private readonly List<(PerkDef perk, int depth)> shown = new();
    private string branch;                                 // the chosen one (kept while the screen lives)
    private ProgressionManager subscribedTo;
    private bool dirty;
    private float resetArmedUntil;

    private void Awake()
    {
        if (resetButton != null) resetButton.onClick.AddListener(OnReset);
        // Our own placing would fight a layout group left on these.
        DisableLayout(rowParent);
        DisableLayout(tabBar);
    }

    private static void DisableLayout(RectTransform rt)
    {
        if (rt == null) return;
        foreach (var g in rt.GetComponents<LayoutGroup>()) g.enabled = false;
        foreach (var f in rt.GetComponents<ContentSizeFitter>()) f.enabled = false;
    }

    private void OnEnable()
    {
        Subscribe();
        dirty = true;
    }

    private void OnDisable()
    {
        if (subscribedTo != null) subscribedTo.Changed -= MarkDirty;
        subscribedTo = null;
    }

    public void OnShopOpened(StorePlot forPlot)
    {
        plot = forPlot;
        resetArmedUntil = 0f;
        Status("");
        Subscribe();
        BuildTabs();
        ShowBranch(branch);
    }

    private void Subscribe()
    {
        var pm = ProgressionManager.Instance;
        if (pm == subscribedTo) return;
        if (subscribedTo != null) subscribedTo.Changed -= MarkDirty;
        subscribedTo = pm;
        if (pm != null) pm.Changed += MarkDirty;
    }

    private void MarkDirty() => dirty = true;

    private void Update()
    {
        if (resetArmedUntil > 0f && Time.unscaledTime > resetArmedUntil)
        {
            resetArmedUntil = 0f;
            dirty = true;
        }
        if (!dirty) return;
        dirty = false;
        if (tabs.Count == 0) { BuildTabs(); ShowBranch(branch); }
        else Refresh();
    }

    /* ========================= TABS ========================= */

    private void BuildTabs()
    {
        var pm = ProgressionManager.Instance;
        if (pm == null || tabPrefab == null || tabBar == null || tabs.Count > 0) return;

        branches.Clear();
        foreach (var p in pm.Perks)
            if (p != null && !branches.Contains(p.branch)) branches.Add(p.branch);

        int n = branches.Count;
        for (int i = 0; i < n; i++)
        {
            var button = Instantiate(tabPrefab, tabBar);
            // Equal shares of the bar, side by side.
            var rt = (RectTransform)button.transform;
            rt.anchorMin = new Vector2((float)i / n, 0f);
            rt.anchorMax = new Vector2((float)(i + 1) / n, 1f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(i == 0 ? 0f : tabSpacing * 0.5f, 0f);
            rt.offsetMax = new Vector2(i == n - 1 ? 0f : -tabSpacing * 0.5f, 0f);

            string b = branches[i];
            button.onClick.AddListener(() => ShowBranch(b));
            tabs.Add((button, button.GetComponentInChildren<TMP_Text>()));
        }
    }

    private void ShowBranch(string name)
    {
        if (branches.Count == 0) { Refresh(); return; }
        branch = branches.Contains(name) ? name : branches[0];
        Status("");

        // This branch's perks, each after the one it needs (indented one more step).
        shown.Clear();
        var pm = ProgressionManager.Instance;
        var inBranch = new List<PerkDef>();
        foreach (var p in pm.Perks) if (p != null && p.branch == branch) inBranch.Add(p);
        foreach (var p in inBranch)
            if (p.requires == null || !inBranch.Contains(p.requires)) AddWithChildren(p, 0, inBranch);
        foreach (var p in inBranch) // anything left (a loop of requirements): at the end
            if (!Contains(p)) shown.Add((p, 0));

        PlaceRows();
        Refresh();
    }

    private void AddWithChildren(PerkDef perk, int depth, List<PerkDef> inBranch)
    {
        if (Contains(perk)) return;
        shown.Add((perk, depth));
        foreach (var p in inBranch)
            if (p.requires == perk) AddWithChildren(p, depth + 1, inBranch);
    }

    private bool Contains(PerkDef perk)
    {
        foreach (var (p, _) in shown) if (p == perk) return true;
        return false;
    }

    /* ========================= ROWS ========================= */

    // Stack the shown perks top to bottom and size Content to fit.
    private void PlaceRows()
    {
        if (rowPrefab == null || rowParent == null) return;
        while (rows.Count < shown.Count)
        {
            var row = Instantiate(rowPrefab, rowParent);
            int index = rows.Count;
            if (row.buyButton != null) row.buyButton.onClick.AddListener(() => OnBuy(index));
            rows.Add(row);
        }

        for (int i = 0; i < rows.Count; i++)
        {
            bool used = i < shown.Count;
            rows[i].gameObject.SetActive(used);
            if (!used) continue;
            var (perk, depth) = shown[i];
            var rt = (RectTransform)rows[i].transform;
            float top = padding + i * (rowHeight + rowSpacing);
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(padding + depth * indent, -(top + rowHeight));
            rt.offsetMax = new Vector2(-padding, -top);
            if (rows[i].icon != null)
            {
                rows[i].icon.sprite = perk.icon;
                rows[i].icon.enabled = perk.icon != null;
            }
        }

        float height = shown.Count == 0 ? 0f : padding * 2f + shown.Count * rowHeight + (shown.Count - 1) * rowSpacing;
        rowParent.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
        rowParent.anchoredPosition = new Vector2(rowParent.anchoredPosition.x, 0f); // back to the top
    }

    private void Refresh()
    {
        var pm = ProgressionManager.Instance;
        if (pm == null || !pm.IsSpawned || plot == null)
        {
            if (summaryText != null) summaryText.text = "";
            return;
        }
        int id = plot.PlotId;

        if (summaryText != null)
        {
            ulong team = plot.OwnerTeamId;
            int level = pm.GetLevel(team), xp = pm.GetXp(team);
            int from = pm.XpForLevel(level), to = pm.XpForLevel(level + 1);
            string levelLine = level >= pm.MaxLevel ? $"Team level {level} (max)" : $"Team level {level}   {xp - from:N0} / {to - from:N0} XP";
            int earned = pm.GetPointsEarned(id);
            long next = pm.ProfitForPoints(earned + 1);
            summaryText.text = $"{levelLine}\n{plot.DisplayName} profit: ${pm.GetStoreProfitCents(id) / 100:N0}   " +
                               $"(next point at ${next / 100:N0})\nPerk points: {pm.GetPointsAvailable(id)} of {earned}";
        }

        for (int i = 0; i < tabs.Count; i++)
        {
            var (button, label) = tabs[i];
            button.interactable = branches[i] != branch;
            if (label != null) label.text = branches[i] + (AnyBuyable(pm, id, branches[i]) ? canBuyMark : "");
        }

        for (int i = 0; i < shown.Count && i < rows.Count; i++)
        {
            var perk = shown[i].perk;
            var row = rows[i];
            int rank = pm.GetRank(id, perk.id);
            bool can = pm.CanBuyPerk(id, perk, out string why);
            if (row.nameText != null) row.nameText.text = perk.maxRank > 1 ? $"{perk.displayName}  {rank}/{perk.maxRank}" : perk.displayName;
            if (row.descriptionText != null)
            {
                string effect = perk.DescribeEffects();
                if (perk.maxRank > 1 && effect.Length > 0) effect += " per rank";
                row.descriptionText.text = string.IsNullOrEmpty(perk.description) ? effect : $"{perk.description}\n{effect}";
            }
            if (row.buyButton != null) row.buyButton.interactable = can;
            if (row.buttonText != null)
                row.buttonText.text = can || why == "Not enough points"
                    ? $"Buy ({perk.costPerRank} pt{(perk.costPerRank == 1 ? "" : "s")})"
                    : why;
        }

        if (resetButton != null) resetButton.interactable = pm.GetPointsSpent(id) > 0;
        if (resetButtonText != null) resetButtonText.text = resetArmedUntil > 0f ? "Click again to reset" : "Reset perks";
    }

    private static bool AnyBuyable(ProgressionManager pm, int plotId, string branchName)
    {
        foreach (var p in pm.Perks)
            if (p != null && p.branch == branchName && pm.CanBuyPerk(plotId, p, out _)) return true;
        return false;
    }

    /* ========================= BUTTONS ========================= */

    private void OnBuy(int index)
    {
        var pm = ProgressionManager.Instance;
        if (pm == null || plot == null || index >= shown.Count) return;
        var perk = shown[index].perk;
        if (!pm.CanBuyPerk(plot.PlotId, perk, out string why)) { Status(why); return; }
        pm.RequestBuyPerk(plot.PlotId, perk.id);
        Status($"{perk.displayName} bought.");
    }

    // Free, but takes everything back: click twice.
    private void OnReset()
    {
        var pm = ProgressionManager.Instance;
        if (pm == null || plot == null) return;
        if (resetArmedUntil <= 0f)
        {
            resetArmedUntil = Time.unscaledTime + 3f;
            dirty = true;
            return;
        }
        resetArmedUntil = 0f;
        pm.RequestResetPerks(plot.PlotId);
        Status("Perks reset: every point is back to spend.");
    }

    private void Status(string text)
    {
        if (statusText != null) statusText.text = text;
    }
}
