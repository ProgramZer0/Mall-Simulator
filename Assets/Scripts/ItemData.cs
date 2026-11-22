using UnityEngine;

/// <summary>
/// An item's catalog entry, kept on its own world prefab (next to its NetworkObject and ItemPickupNet), so
/// setting up an item is one place: name, price, categories, shop and market settings, sizes...
///
/// You never type its id: Tools > Mall Sim > Items > Update Item Catalog (also run automatically before
/// Play) gives every item prefab a unique id, writes it to the ItemPickupNet too, adds the prefab to the
/// ItemCatalog and to the network prefab list. Its world prefab is this prefab.
/// </summary>
[DisallowMultipleComponent]
public class ItemData : MonoBehaviour
{
    [Tooltip("The item's catalog entry. Id and World Network Prefab are filled in for you.")]
    public ItemDef item = new();

    [Tooltip("Keep the name as you typed it. Off: Update Item Catalog names it from the prefab's name " +
             "(\"lowCuts_Stock\" → \"Low Cuts\"). Typing your own name ticks this for you.")]
    public bool keepName;
    [HideInInspector] public string autoName; // the name it was last given, to tell yours from ours

    [Tooltip("Its shop icon (made from the model when Icon is empty) is shot standing the way it's placed, " +
             "from the front; this turns it more for the photo (degrees: X tips it toward you, Y turns it round, " +
             "Z leans it). Changing it remakes the icon on the next Update Item Catalog.")]
    public Vector3 iconTurn;
    [HideInInspector] public Vector3 iconMadeWith = new(float.NaN, 0f, 0f); // the turn the made icon was shot with

    /// <summary>This item's id (0 until Update Item Catalog gives it one).</summary>
    public int Id => item != null ? item.id : 0;

#if UNITY_EDITOR
    private void OnValidate()
    {
        item ??= new ItemDef();
        if (string.IsNullOrWhiteSpace(item.displayName)) item.displayName = gameObject.name.Replace('_', ' ').Trim();
        item.MigrateCategory();
        item.CategoriesChanged();
        // The id is the one source of truth: keep the ItemPickupNet's in step with it.
        if (item.id > 0 && TryGetComponent<ItemPickupNet>(out var pickup) && pickup.itemId != item.id)
        {
            pickup.itemId = item.id;
            UnityEditor.EditorUtility.SetDirty(pickup);
        }
    }
#endif
}
