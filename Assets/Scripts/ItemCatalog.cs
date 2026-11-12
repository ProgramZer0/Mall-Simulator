using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public class ItemCatalog : MonoBehaviour
{
    public static ItemCatalog Instance { get; private set; }
    [SerializeField] private List<ItemDef> items = new();

    [Header("Default Item Sounds")]
    [Tooltip("Used for any item that doesn't set its own Pickup Clip.")]
    [SerializeField] private AudioClip defaultPickupClip;
    [Tooltip("Used for any item that doesn't set its own Place Clip.")]
    [SerializeField] private AudioClip defaultPlaceClip;

    public AudioClip DefaultPickupClip => defaultPickupClip;

    [Header("Default Shipment Box")]
    [Tooltip("Box delivered for items that don't set their own Shipment Prefab. Needs a NetworkObject, " +
             "ItemPickupNet and ShipmentPackage, and must be in the NetworkManager's prefab list.")]
    [SerializeField] private NetworkObject defaultShipmentPrefab;

    public NetworkObject DefaultShipmentPrefab => defaultShipmentPrefab;

    [Header("Debug")]
    [Tooltip("Log picking up, placing, stocking and sizes to the Console (see ItemDebug).")]
    [SerializeField] private bool debugItemLogs = true;
    public AudioClip DefaultPlaceClip => defaultPlaceClip;

    private Dictionary<int, ItemDef> map;
    private readonly Dictionary<int, List<ItemDef>> sizesOf = new();

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        ItemDebug.On = debugItemLogs;
        map = new Dictionary<int, ItemDef>();
        foreach (var it in items)
        {
            if (it == null) continue;
            if (it.id >= Sizes.FirstSizedId)
                Debug.LogWarning($"[ItemCatalog] '{it.displayName}' has id {it.id}: ids from {Sizes.FirstSizedId} up are " +
                                 "used for sizes. Give it a smaller one.");
            map[it.id] = it;
        }
        // Each size of a sized item is its own item (own id, price, stock, price tag), made here.
        foreach (var it in items)
        {
            if (it == null || it.sizes == ItemDef.SizeSet.None) continue;
            var list = new List<ItemDef>();
            for (int i = 0; i < Sizes.Count(it.sizes); i++)
            {
                var v = it.MakeSize(i);
                map[v.id] = v;
                list.Add(v);
            }
            sizesOf[it.id] = list;
            if (ItemDebug.On)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var v in list) sb.Append($"  {v.id} {v.SizeName} ${v.price} (market ${v.marketPrice:0.00})\n");
                ItemDebug.Log($"Sizes for '{it.displayName}' ({it.id}, {it.sizes}, step {it.sizePriceStep:P0}):\n{sb}");
            }
        }
    }

    public bool TryGet(int id, out ItemDef def) => map.TryGetValue(id, out def);

    /// <summary>A sized item's sizes, smallest first (null if it has none).</summary>
    public IReadOnlyList<ItemDef> GetSizes(int itemId)
    {
        int baseId = TryGet(itemId, out var def) ? def.BaseId : itemId;
        return sizesOf.TryGetValue(baseId, out var list) ? list : null;
    }

#if UNITY_EDITOR
    // Items used to have one Category; move it into Categories so nothing set up is lost.
    private void OnValidate()
    {
        if (items == null) return;
        bool changed = false;
        foreach (var it in items)
        {
            if (it == null) continue;
            if (it.MigrateCategory()) changed = true;
            it.CategoriesChanged();
        }
        if (changed) UnityEditor.EditorUtility.SetDirty(this);
    }
#endif

    /// <summary>Every item in the catalog, in the order listed (the shop shows them in this order).</summary>
    public IReadOnlyList<ItemDef> Items => items;
}

[System.Serializable]
public class ItemDef
{
    public int id;
    public string displayName;
    public GameObject handVisualPrefab;
    public NetworkObject worldNetworkPrefab;

    [Header("Buying")]
    [Tooltip("List this item in the shop monitor's buy menu.")]
    public bool showInShop = true;
    [Tooltip("Picture shown in the buy menu.")]
    public Sprite icon;
    [Tooltip("Groups like Shelves, Products, Decor, Clothing. An item can be in several. Events target them, " +
             "and the buy menu search matches them as well as the name.")]
    public List<string> categories = new();
    [Tooltip("Old single category; it's moved into Categories automatically.")]
    [HideInInspector] public string category;

    [System.NonSerialized] private HashSet<string> categorySet;

    /// <summary>Is the item in this category (not case sensitive)? Quick: a set built once.</summary>
    public bool HasCategory(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (categorySet == null)
        {
            categorySet = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var c in Categories) categorySet.Add(c);
        }
        return categorySet.Contains(name.Trim());
    }

    /// <summary>Every category (including an old single Category not moved over yet).</summary>
    public IEnumerable<string> Categories
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(category)) yield return category.Trim();
            if (categories == null) yield break;
            foreach (var c in categories)
                if (!string.IsNullOrWhiteSpace(c)) yield return c.Trim();
        }
    }

    /// <summary>Forget the cached category set (after editing Categories).</summary>
    public void CategoriesChanged() => categorySet = null;

    /// <summary>Editor: move an old single Category into Categories. True if anything changed.</summary>
    public bool MigrateCategory()
    {
        if (string.IsNullOrWhiteSpace(category)) return false;
        categories ??= new List<string>();
        bool have = false;
        foreach (var c in categories)
            if (string.Equals(c?.Trim(), category.Trim(), System.StringComparison.OrdinalIgnoreCase)) have = true;
        if (!have) categories.Insert(0, category.Trim());
        category = null;
        categorySet = null;
        return true;
    }
    [Tooltip("Cost of one when ordered.")]
    public int price = 10;

    public enum SizeSet { None, Clothing, Shoes }
    [Tooltip("Clothing: XS to XXL. Shoes: 4 to 12. Each size becomes its own item (own stock and price tag); " +
             "the shop shows this item once and asks which sizes.")]
    public SizeSet sizes = SizeSet.None;
    [Tooltip("Each size up from the middle (M, or 8) costs this much more, each size down this much less " +
             "(0.03 = 3%). Market price too.")]
    [Range(0f, 0.25f)] public float sizePriceStep = 0.03f;

    [System.NonSerialized] private ItemDef sizeOf;   // a size: the item it's a size of
    [System.NonSerialized] private int sizeIndex = -1;

    /// <summary>The item this is a size of (itself if it isn't a size).</summary>
    public ItemDef BaseDef => sizeOf ?? this;
    public int BaseId => BaseDef.id;
    public bool IsSize => sizeOf != null;
    /// <summary>Which size (0 = smallest), -1 if it isn't a size.</summary>
    public int SizeIndex => sizeIndex;
    public string SizeName => IsSize ? Sizes.Name(sizeOf.sizes, sizeIndex) : "";

    /// <summary>One size of this item (ItemCatalog makes them).</summary>
    public ItemDef MakeSize(int index)
    {
        var v = (ItemDef)MemberwiseClone();
        v.sizeOf = this;
        v.sizeIndex = index;
        v.id = Sizes.SizedId(id, index);
        v.displayName = $"{displayName} ({Sizes.Name(sizes, index)})";
        v.showInShop = false; // the shop lists the item once
        float scale = 1f + sizePriceStep * (index - Sizes.Middle(sizes));
        v.price = Mathf.Max(1, Mathf.RoundToInt(price * scale));
        v.marketPrice = marketPrice * scale;
        v.categorySet = null;
        v.ghostTemplate = null;
        return v;
    }
    [Tooltip("Team level needed to order it (0 or 1 = from the start). Shown locked in the shop until then.")]
    [Min(0)] public int minLevel = 0;
    [Tooltip("How rare it is. Customers who love exotic things are drawn to rarer items.")]
    public Rarity rarity = Rarity.Common;

    public enum Rarity { Common, Uncommon, Rare, Exotic }
    [Tooltip("What one is really worth to customers, in dollars (cents allowed, e.g. 24.99). Not shown to the " +
             "player: their prices are judged against it (and it'll move with events and seasons). For stock " +
             "crates this is per product (one shirt), not the whole crate.")]
    public float marketPrice = 0f;
    [Tooltip("Default seconds from ordering until it arrives (used when the order doesn't give its own time).")]
    public float deliverySeconds = 30f;
    [Tooltip("What arrives at the shipment area: a box (NetworkObject + ItemPickupNet + ShipmentPackage). " +
             "Picking it up gives you this item; placing it puts down the real item. Empty = the catalog's " +
             "default box (or, if that's empty too, the item itself).")]
    public NetworkObject shipmentPrefab;

    /// <summary>Market value of one (one product, for stock crates), in cents. 0 = not set.</summary>
    public int MarketPriceCents => Mathf.Max(0, Mathf.RoundToInt(marketPrice * 100f));

    /// <summary>The object to deliver for this item.</summary>
    public NetworkObject GetShipmentPrefab()
    {
        if (shipmentPrefab != null) return shipmentPrefab;
        if (ItemCatalog.Instance != null && ItemCatalog.Instance.DefaultShipmentPrefab != null)
            return ItemCatalog.Instance.DefaultShipmentPrefab;
        return worldNetworkPrefab;
    }

    [Header("Sounds (leave empty to use the catalog defaults)")]
    public AudioClip pickupClip;
    public AudioClip placeClip;

    public AudioClip PickupClip => pickupClip != null ? pickupClip
        : ItemCatalog.Instance != null ? ItemCatalog.Instance.DefaultPickupClip : null;
    public AudioClip PlaceClip => placeClip != null ? placeClip
        : ItemCatalog.Instance != null ? ItemCatalog.Instance.DefaultPlaceClip : null;

    [System.NonSerialized] private GameObject ghostTemplate;
    [System.NonSerialized] private bool localBoundsComputed;
    [System.NonSerialized] private Bounds localBounds;

    /// <summary>Physical size of the world prefab in its own local space (cached).</summary>
    public Bounds LocalBounds
    {
        get
        {
            if (sizeOf != null) return sizeOf.LocalBounds; // same model
            if (!localBoundsComputed && worldNetworkPrefab != null)
            {
                localBounds = PlacementCheck.ComputeLocalBounds(worldNetworkPrefab.gameObject);
                localBoundsComputed = true;
            }
            return localBounds;
        }
    }

    /// <summary>
    /// Inactive, transparent, component-stripped copy of the world prefab.
    /// Built once per item and reused; Instantiate it to show a preview.
    /// </summary>
    public GameObject GetGhostPrefab()
    {
        if (sizeOf != null) return sizeOf.GetGhostPrefab(); // one per model, not per size
        if (ghostTemplate != null) return ghostTemplate;
        if (worldNetworkPrefab == null) return null;

        // Starting opacity only; PlayerInteractInput sets the real per-ghost opacity.
        ghostTemplate = GhostBuilder.BuildTemplate(worldNetworkPrefab.gameObject, 0.45f);
        return ghostTemplate;
    }
}
/// <summary>
/// Clothing and shoe sizes. A size of an item is its own item: id FirstSizedId + item id * 16 + size.
/// </summary>
public static class Sizes
{
    public const int FirstSizedId = 1_000_000;
    private static readonly string[] Clothing = { "XS", "S", "M", "L", "XL", "XXL" };
    private static readonly string[] Shoes = { "4", "5", "6", "7", "8", "9", "10", "11", "12" };

    public static int Count(ItemDef.SizeSet set) => Names(set).Length;
    /// <summary>The size priced as the item itself (M, 8).</summary>
    public static int Middle(ItemDef.SizeSet set) => set == ItemDef.SizeSet.Clothing ? 2 : set == ItemDef.SizeSet.Shoes ? 4 : 0;
    public static string Name(ItemDef.SizeSet set, int index)
    {
        var names = Names(set);
        return index >= 0 && index < names.Length ? names[index] : "";
    }

    public static int SizedId(int itemId, int index) => FirstSizedId + itemId * 16 + index;

    /// <summary>Is this a size's id? Gives the item it's a size of and which size.</summary>
    public static bool TrySplit(int id, out int itemId, out int index)
    {
        itemId = id;
        index = -1;
        if (id < FirstSizedId) return false;
        itemId = (id - FirstSizedId) / 16;
        index = (id - FirstSizedId) % 16;
        return true;
    }

    private static string[] Names(ItemDef.SizeSet set) =>
        set == ItemDef.SizeSet.Clothing ? Clothing : set == ItemDef.SizeSet.Shoes ? Shoes : System.Array.Empty<string>();
}

/// <summary>
/// Console logs for picking up, placing, stocking and sizes ("[Items] ..."). Turn off with the
/// ItemCatalog's Debug Item Logs.
/// </summary>
public static class ItemDebug
{
    public static bool On = true;

    public static void Log(string message, Object context = null)
    {
        if (On) Debug.Log("[Items] " + message, context);
    }

    /// <summary>"7000123 'Sneakers (8)' (size 8 of 'Sneakers' 77, crate 12, Floor)" for logs.</summary>
    public static string Describe(int id)
    {
        if (id == 0) return "nothing (0)";
        if (ItemCatalog.Instance == null) return $"{id} (no ItemCatalog)";
        if (!ItemCatalog.Instance.TryGet(id, out var def))
        {
            string split = Sizes.TrySplit(id, out int baseId, out int index) ? $", looks like size {index} of item {baseId}" : "";
            return $"{id} (NOT IN THE CATALOG{split})";
        }
        var text = $"{id} '{def.displayName}'";
        var parts = new List<string>();
        if (def.IsSize) parts.Add($"size {def.SizeName} of '{def.BaseDef.displayName}' {def.BaseId}");
        if (def.worldNetworkPrefab == null) parts.Add("NO WORLD PREFAB");
        else
        {
            var p = def.worldNetworkPrefab.GetComponent<ItemPickupNet>();
            if (p == null) parts.Add("prefab has NO ItemPickupNet");
            else
            {
                if (p.itemId != def.BaseId) parts.Add($"PREFAB ItemPickupNet id is {p.itemId}, not {def.BaseId}");
                parts.Add(p.MountingType.ToString());
                if (p.IsStockCrate) parts.Add($"stock crate x{p.UnitsPerCrate}" +
                                              (p.ShelfPrefab == null && p.HangPrefab == null ? " (NO Shelf/Hang Prefab)" : ""));
            }
        }
        if (PlacementCheck.StaysBoxed(def)) parts.Add("stays boxed");
        return parts.Count > 0 ? $"{text} ({string.Join(", ", parts)})" : text;
    }
}
