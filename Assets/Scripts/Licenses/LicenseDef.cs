using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A license to sell one category of items (see LicenseManager). Make them with Create > Mall Sim >
/// License and list them on the LicenseManager. While a category has a license, only teams holding it
/// can order items in that category (an item in several licensed categories needs all of them).
/// Categories without a license are free for everyone.
/// </summary>
[CreateAssetMenu(menuName = "Mall Sim/License", fileName = "New License")]
public class LicenseDef : ScriptableObject
{
    [Tooltip("Unique number for this license (not 0). Don't change it once players have bought it.")]
    public int id = 1;
    public string displayName = "License";
    [TextArea] public string description;
    public Sprite icon;

    [Tooltip("The item categories it unlocks (as on items' Categories; not case sensitive). An item in any of them " +
             "needs this license.")]
    public List<string> categories = new();
    [Tooltip("Old single category (still counted); use Categories.")]
    public string category = "";
    [Tooltip("Price in dollars.")]
    [Min(0)] public int price = 1000;
    [Tooltip("How many teams can hold it at once in the mall (0 = any number).")]
    [Min(0)] public int perMall = 3;
    [Tooltip("Team level needed to buy it (0 or 1 = from the start).")]
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
