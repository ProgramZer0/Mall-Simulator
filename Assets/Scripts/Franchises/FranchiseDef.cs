using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A franchise: the sole right to sell one set of items (those with its Category) for a while, won at
/// auction (see FranchiseManager). Make them with Create > Mall Sim > Franchise and list them on the
/// FranchiseManager. Only the holding team can order its items; nobody can while no one holds it.
/// </summary>
[CreateAssetMenu(menuName = "Mall Sim/Franchise", fileName = "New Franchise")]
public class FranchiseDef : ScriptableObject
{
    [Tooltip("Unique number for this franchise (not 0).")]
    public int id = 1;
    public string displayName = "Franchise";
    [TextArea] public string description;
    public Sprite icon;

    [Tooltip("The item categories it covers (as on items' Categories; not case sensitive), e.g. \"Brand X\". An " +
             "item in any of them belongs to this franchise.")]
    public List<string> categories = new();
    [Tooltip("Old single category (still counted); use Categories.")]
    public string category = "";
    [Tooltip("Lowest first bid, in dollars.")]
    [Min(1)] public int startingBid = 2000;
    [Tooltip("Each bid must beat the top bid by at least this, in dollars.")]
    [Min(1)] public int minRaise = 100;
    [Tooltip("How long the winner holds it, in seconds, before it goes up for bids again.")]
    [Min(30f)] public float holdSeconds = 1800f;
    [Tooltip("How long bidding lasts, in seconds (late bids add time).")]
    [Min(10f)] public float auctionSeconds = 180f;
    [Tooltip("Team level needed to bid (0 or 1 = from the start).")]
    [Min(0)] public int minLevel = 0;

    /// <summary>Every category it covers (Categories, plus the old single Category if still set).</summary>
    public IEnumerable<string> AllCategories
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(category)) yield return category;
            if (categories == null) yield break;
            foreach (var c in categories)
                if (!string.IsNullOrWhiteSpace(c) && !string.Equals(c, category, System.StringComparison.OrdinalIgnoreCase)) yield return c;
        }
    }

    /// <summary>Does it cover this item (the item has any of its categories)?</summary>
    public bool Covers(ItemDef item)
    {
        if (item == null) return false;
        foreach (var c in AllCategories) if (item.HasCategory(c)) return true;
        return false;
    }

    /// <summary>"Clothing, Shoes" for menus.</summary>
    public string CategoryList => string.Join(", ", AllCategories);
}
