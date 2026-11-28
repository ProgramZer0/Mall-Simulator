using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// What a store perk changes. Positive is always better (0.1 = 10% better).
/// The first four work now; the rest are read by customer AI once it exists.
/// </summary>
public enum PerkStat
{
    /// <summary>More passers-by walk in (0.1 = 10% more likely).</summary>
    WalkIns,
    /// <summary>Customers inside want more of what's for sale (0.1 = 10% more).</summary>
    Interest,
    /// <summary>Store quality, as if it had better decor (0.05 = +5 quality points).</summary>
    Quality,
    /// <summary>Deliveries to this store arrive sooner (0.1 = 10% less time, at most 75%).</summary>
    FasterDelivery,
    /// <summary>Customer AI: fewer thefts (0.1 = 10% less likely).</summary>
    TheftResistance,
    /// <summary>Customer AI: customers wait longer before giving up (0.1 = 10% longer).</summary>
    Patience,
    /// <summary>Customer AI: customers accept higher prices (0.1 = 10% more).</summary>
    PriceTolerance,
    /// <summary>Customer AI: more customers in the store at once (0.1 = 10% more).</summary>
    Capacity,
}

/// <summary>
/// One perk in the store perk tree. Make them with Create > Mall Sim > Store Perk and list them on
/// the ProgressionManager (Perks). Points come from the store's own profit; respecs are free.
///
/// Branch groups perks on the perks screen (Prestige, Security, Marketing, Logistics...). Requires
/// makes a tree: this perk can only be bought once that perk has Requires Rank ranks.
/// </summary>
[CreateAssetMenu(menuName = "Mall Sim/Store Perk", fileName = "New Perk")]
public class PerkDef : ScriptableObject
{
    [Serializable]
    public struct Effect
    {
        public PerkStat stat;
        [Tooltip("Added per rank (0.05 = +5% per rank).")]
        public float perRank;
    }

    [Tooltip("Unique number for this perk (not 0). Don't change it once players have bought it.")]
    public int id = 1;
    public string displayName = "Perk";
    [TextArea] public string description;
    [Tooltip("Group on the perks screen, e.g. Marketing.")]
    public string branch = "General";
    public Sprite icon;

    [Min(1)] public int maxRank = 1;
    [Tooltip("Perk points per rank.")]
    [Min(1)] public int costPerRank = 1;

    [Tooltip("Optional: a perk that must be bought first.")]
    public PerkDef requires;
    [Min(1)] public int requiresRank = 1;

    public List<Effect> effects = new();

    /// <summary>"+5% walk-ins, +2 quality" for one rank (or more).</summary>
    public string DescribeEffects(int ranks = 1)
    {
        var sb = new StringBuilder();
        foreach (var e in effects)
        {
            if (sb.Length > 0) sb.Append(", ");
            float v = e.perRank * Mathf.Max(1, ranks);
            sb.Append(e.stat == PerkStat.Quality ? $"{v * 100f:+0;-0} quality" : $"{v * 100f:+0;-0}% {StatName(e.stat)}");
        }
        return sb.ToString();
    }

    public static string StatName(PerkStat s) => s switch
    {
        PerkStat.WalkIns => "walk-ins",
        PerkStat.Interest => "customer interest",
        PerkStat.Quality => "quality",
        PerkStat.FasterDelivery => "faster deliveries",
        PerkStat.TheftResistance => "theft resistance",
        PerkStat.Patience => "customer patience",
        PerkStat.PriceTolerance => "price tolerance",
        PerkStat.Capacity => "customer capacity",
        _ => s.ToString(),
    };
}
