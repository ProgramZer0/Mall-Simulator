using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The shop monitor's Perks tab: the store's perk points and the perk tree (ProgressionManager →
/// Perks), grouped by Branch. Make it a tab's screen on the ShopUIController.
///
/// Setup: Row Prefab (a PerkRow) and Row Parent (with a Vertical Layout Group, inside a Scroll View if
/// there are many perks). Optional: Branch Header Prefab (a TMP text, one per branch), Summary Text
/// (team level, store profit, points), Reset Button (free respec; click twice to confirm), Status Text.
/// </summary>
public class PerksScreen : MonoBehaviour, IShopScreen
{
    [SerializeField] private PerkRow rowPrefab;
    [SerializeField] private RectTransform rowParent;
    [Tooltip("Optional: a heading made above each branch's perks.")]
    [SerializeField] private TMP_Text branchHeaderPrefab;
    [SerializeField] private TMP_Text summaryText;
    [SerializeField] private Button resetButton;
    [SerializeField] private TMP_Text resetButtonText;
    [SerializeField] private TMP_Text statusText;

    private StorePlot plot;
    private readonly List<(PerkDef perk, PerkRow row)> rows = new();
    private readonly List<GameObject> made = new();
    private ProgressionManager subscribedTo;
    private bool dirty;
    private float resetArmedUntil;

    private void Awake()
    {
        if (resetButton != null) resetButton.onClick.AddListener(OnReset);
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
        if (statusText != null) statusText.text = "";
        Subscribe();
        Build();
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
        if (rows.Count == 0) Build(); else Refresh();
    }

    // One row per perk, under its branch's heading, in the Perks list's order.
    private void Build()
    {
        foreach (var go in made) if (go != null) Destroy(go);
        made.Clear();
        rows.Clear();
        var pm = ProgressionManager.Instance;
        if (pm == null || rowPrefab == null || rowParent == null) { Refresh(); return; }

        var branches = new List<string>();
        foreach (var p in pm.Perks) if (p != null && !branches.Contains(p.branch)) branches.Add(p.branch);
        foreach (string branch in branches)
        {
            if (branchHeaderPrefab != null)
            {
                var header = Instantiate(branchHeaderPrefab, rowParent);
                header.text = branch;
                made.Add(header.gameObject);
            }
            foreach (var p in pm.Perks)
            {
                if (p == null || p.branch != branch) continue;
                var row = Instantiate(rowPrefab, rowParent);
                var perk = p;
                if (row.buyButton != null) row.buyButton.onClick.AddListener(() => OnBuy(perk));
                if (row.icon != null)
                {
                    row.icon.sprite = p.icon;
                    row.icon.enabled = p.icon != null;
                }
                rows.Add((p, row));
                made.Add(row.gameObject);
            }
        }
        Refresh();
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

        foreach (var (perk, row) in rows)
        {
            int rank = pm.GetRank(id, perk.id);
            bool can = pm.CanBuyPerk(id, perk, out string why);
            if (row.nameText != null) row.nameText.text = perk.maxRank > 1 ? $"{perk.displayName}  {rank}/{perk.maxRank}" : perk.displayName;
            if (row.descriptionText != null)
            {
                string effect = perk.DescribeEffects();
                string text = string.IsNullOrEmpty(perk.description) ? effect : $"{perk.description}\n{effect}";
                if (perk.maxRank > 1 && effect.Length > 0) text += " per rank";
                row.descriptionText.text = text;
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

    private void OnBuy(PerkDef perk)
    {
        var pm = ProgressionManager.Instance;
        if (pm == null || plot == null) return;
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
