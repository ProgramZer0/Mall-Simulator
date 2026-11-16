using UnityEngine;

/// <summary>
/// Settings for making stock crates by themselves (see StockBoxGenerator): one box prefab for every
/// product, and which product models go on rails or shelves, found by name. Made at
/// Assets/Settings/StockBoxSettings.asset the first time Update Item Catalog runs; select it to change them.
/// </summary>
public class StockBoxSettings : ScriptableObject
{
    [Tooltip("The box every crate is made from (each crate is a prefab variant of it: change the box, they all " +
             "change). It needs a NetworkObject and an ItemPickupNet. An ItemData on it gives new crates their " +
             "starting values (price, categories, sizes...); the box itself isn't an item. Empty: a prefab named " +
             "BoxStock. Only product models in its folder (and the folders under it) get crates.")]
    public GameObject template;

    [Tooltip("Make crates for new products on Update Item Catalog (and before Play), and keep their models " +
             "in step with the product folders.")]
    public bool generate = true;

    [Tooltip("A product model whose name, or its folder's name, has one of these goes on rails (the crate's " +
             "Hang Prefabs). Several in one product folder: variants (colors).")]
    public string[] hangWords = { "hang" };

    [Tooltip("...or one of these: on shelves (Shelf Prefabs). (\"self\" is for the shoe boxes named Self.)")]
    public string[] shelfWords = { "fold", "shelf", "self" };

    [Tooltip("Added to the crate's categories, on top of its product's.")]
    public string boxedCategory = "Boxed";

    // Settings made by an older version get its new defaults added once.
    [SerializeField, HideInInspector] private int version;
    private const int Current = 1;

    /// <summary>Editor: bring settings made by an older version up to date. True if anything changed.</summary>
    public bool Upgrade()
    {
        if (version >= Current) return false;
        if (version < 1)
        {
            var words = new System.Collections.Generic.List<string>(shelfWords ?? new string[0]);
            foreach (var w in new[] { "shelf", "self" })
                if (!words.Exists(x => string.Equals(x?.Trim(), w, System.StringComparison.OrdinalIgnoreCase))) words.Add(w);
            shelfWords = words.ToArray();
            if (string.IsNullOrWhiteSpace(boxedCategory)) boxedCategory = "Boxed";
        }
        version = Current;
        return true;
    }

    [Tooltip("The crate is named after its product's folder, plus this (\"Cheap Jeans Stock\").")]
    public string nameSuffix = " Stock";

    [Header("Crate icon")]
    [Tooltip("The crate's icon is its product's (the hanging one's, else the shelf one's) in front of the box's " +
             "icon (an ItemData on the box). Pixels square.")]
    public int iconSize = 256;
    [Tooltip("How big the product is drawn over the box (1 = as big as the box).")]
    [Range(0.1f, 1f)] public float iconProductScale = 0.8f;
    [Tooltip("Where the product sits over the box, as a share of the icon (0, 0 = middle; y up).")]
    public Vector2 iconProductOffset = Vector2.zero;

    [Header("Products per crate")]
    [Tooltip("How many fit: the box's size over one product's (the shelf model if it has one, else the hanging " +
             "one), times this for the gaps. A product folder named with x and a number (\"Cheap Jeans x12\") " +
             "holds exactly that many instead. Set when a crate is made; change it on the crate afterwards, or run " +
             "Tools > Mall Sim > Items > Recalculate Stock Crate Amounts.")]
    [Range(0.1f, 1f)] public float packing = 0.7f;
    [Min(1)] public int minUnits = 1;
    [Min(1)] public int maxUnits = 24;
}
