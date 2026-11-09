using UnityEngine;

/// <summary>
/// A front wall type players buy in the shop. Put it on the world prefab of a catalog item (with a
/// NetworkObject and ItemPickupNet, Edit Mode Only ticked) and drag in the WallModule prefab it puts
/// up. Like windows and doors, it's delivered in a box and stays in it (give the catalog entry a
/// Shipment Prefab, or the catalog a default one).
///
/// In store edit mode, click the box, then a storefront section: that section becomes this type and
/// the box is used up. The type it replaces goes back in a box (if some kit puts that type up) on
/// the floor inside the store straight away.
///
/// Kits also count as the FrontWallCatalog: a module in a kit doesn't need to be listed there too.
/// </summary>
public class FrontWallKit : MonoBehaviour
{
    [Tooltip("The front wall module prefab (with a WallModule) this kit puts up.")]
    [SerializeField] private WallModule module;

    public WallModule Module => module;

    /// <summary>The module a catalog item's kit puts up, if it's a kit.</summary>
    public static bool TryGetModule(ItemDef def, out WallModule module)
    {
        module = null;
        if (def == null || def.worldNetworkPrefab == null ||
            !def.worldNetworkPrefab.TryGetComponent<FrontWallKit>(out var kit) || kit.module == null) return false;
        module = kit.module;
        return true;
    }

    public static bool IsKit(ItemDef def) => TryGetModule(def, out _);

    /// <summary>The catalog item whose kit puts up this module type, if any.</summary>
    public static bool TryFindKitItem(int moduleId, out ItemDef kitItem)
    {
        kitItem = null;
        var catalog = ItemCatalog.Instance;
        if (catalog == null || moduleId == 0) return false;
        foreach (var def in catalog.Items)
            if (TryGetModule(def, out var m) && m.ModuleId == moduleId) { kitItem = def; return true; }
        return false;
    }
}
