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

    [Tooltip("The item category it unlocks (as on items' Categories; not case sensitive).")]
    public string category = "Electronics";
    [Tooltip("Price in dollars.")]
    [Min(0)] public int price = 1000;
    [Tooltip("How many teams can hold it at once in the mall (0 = any number).")]
    [Min(0)] public int perMall = 3;
    [Tooltip("Team level needed to buy it (0 or 1 = from the start).")]
    [Min(0)] public int minLevel = 0;
}
