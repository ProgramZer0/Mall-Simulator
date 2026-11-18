using System.Collections.Generic;
using System.Linq;
using System.Text;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Item setup without typing ids. Each item keeps its catalog entry on its own prefab (ItemData); this
/// gives ids and keeps everything in step:
///
/// Tools > Mall Sim > Items > Update Item Catalog (and automatically before Play, see "Update Before Play"):
/// - Finds every prefab with an ItemData.
/// - Gives any without an id (or with one another item already has) the next free id, and writes it to
///   its ItemPickupNet too. Ids already given never change.
/// - Lists them all on the open scene's ItemCatalog (by id), and adds any missing ones to the network
///   prefab list.
/// - Size Class left on Auto: set from the prefab's folder (Small / Med / Lrg / Misc), which picks its
///   default hand model and delivery box (ItemCatalog → Defaults by size).
/// - A new item that's one product on a shelf or rack (a stock crate's Shelf or Hang Prefab) starts out
///   not in the shop (you buy the crate). Change it afterwards if you like; it's only set once.
/// - Empty Defaults by size on the ItemCatalog are filled from prefabs named handSmall / handMed / handLrg /
///   handMisc and shipmentSmall / shipmentMed / shipmentLrg / shipmentMisc (and Default Shipment Prefab
///   from one named shipment).
///
/// - Stock crates for products without one are made first (StockBoxGenerator): product models in a folder
///   per product under the BoxStock box's folder, named (or in folders named) "hang" or "fold".
///
/// Tools > Mall Sim > Items > Move Catalog Items Onto Prefabs: one time, for items set up on the
/// ItemCatalog the old way. Each moves onto its world prefab as an ItemData, keeping its id.
///
/// New item: make the prefab (NetworkObject, ItemPickupNet, and an ItemData with its name, price and
/// the rest), then press Play (or run Update Item Catalog). That's it.
/// </summary>
[InitializeOnLoad]
public static class ItemSetupTool
{
    private const string AutoKey = "MallSim.UpdateItemsBeforePlay";
    private const string MenuRoot = "Tools/Mall Sim/Items/";

    static ItemSetupTool()
    {
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.ExitingEditMode && AutoUpdate) UpdateCatalog(quiet: true);
        };
    }

    private static bool AutoUpdate
    {
        get => EditorPrefs.GetBool(AutoKey, true);
        set => EditorPrefs.SetBool(AutoKey, value);
    }

    [MenuItem(MenuRoot + "Update Item Catalog", priority = 0)]
    private static void UpdateMenu() => UpdateCatalog(quiet: false);

    [MenuItem(MenuRoot + "Update Before Play", priority = 1)]
    private static void ToggleAuto() => AutoUpdate = !AutoUpdate;

    [MenuItem(MenuRoot + "Update Before Play", true)]
    private static bool ToggleAutoCheck()
    {
        Menu.SetChecked(MenuRoot + "Update Before Play", AutoUpdate);
        return true;
    }

    /* ========================= UPDATE ========================= */

    /// <summary>Give ids, fill the catalog and the network prefab list. Quiet: only log changes and problems.</summary>
    public static void UpdateCatalog(bool quiet)
    {
        var report = new StringBuilder();
        allPrefabs = LoadAllPrefabs();
        FillFromFolders(FindItemPrefabs(), report); // first, so made crates copy their products' categories
        int before = report.Length;
        StockBoxGenerator.Run(report); // crates for new products, so they get ids below
        if (report.Length != before) allPrefabs = LoadAllPrefabs(); // (something made or changed)
        var datas = FindItemPrefabs();
        var catalog = FindCatalog();

        // Ids in use: the older catalog entries, then items that already have one (first one found keeps a repeat).
        var used = new HashSet<int>();
        if (catalog != null)
            foreach (var it in LegacyItems(catalog)) if (it.id > 0) used.Add(it.id);
        var needId = new List<ItemData>();
        foreach (var d in datas)
        {
            int id = d.item != null ? d.item.id : 0;
            if (id <= 0) needId.Add(d);
            else if (!used.Add(id))
            {
                report.AppendLine($"'{d.name}' had id {id}, which another item already has: given a new one.");
                needId.Add(d);
            }
        }
        int next = used.Count > 0 ? used.Max() + 1 : 1;
        var products = FindProductPrefabs();
        foreach (var d in needId)
        {
            bool isNew = d.item == null || d.item.id <= 0;
            while (used.Contains(next)) next++;
            if (next >= Sizes.FirstSizedId) { Debug.LogError("[Items] Out of ids (sizes start at " + Sizes.FirstSizedId + ")."); break; }
            d.item ??= new ItemDef();
            d.item.id = next;
            used.Add(next);
            if (isNew && products.Contains(d.gameObject) && d.item.showInShop)
            {
                d.item.showInShop = false; // one product: players buy the crate
                report.AppendLine($"'{d.name}' is one product on shelves/racks: not in the shop.");
            }
            EditorUtility.SetDirty(d);
            report.AppendLine($"'{d.name}' is item {next}.");
        }

        // Size class from the folder, where it's still Auto.
        foreach (var d in datas)
        {
            if (d.item == null || d.item.sizeClass != ItemDef.SizeClass.Auto) continue;
            var size = SizeFromFolder(AssetDatabase.GetAssetPath(d));
            if (size == ItemDef.SizeClass.Auto) continue;
            d.item.sizeClass = size;
            EditorUtility.SetDirty(d);
            report.AppendLine($"'{d.name}' is {size} (from its folder).");
        }

        // The ItemPickupNet's id, and what's needed to be an item.
        foreach (var d in datas)
        {
            if (!d.TryGetComponent<ItemPickupNet>(out var pickup))
                report.AppendLine($"'{d.name}' has an ItemData but no ItemPickupNet: add one.");
            else if (pickup.itemId != d.item.id)
            {
                pickup.itemId = d.item.id;
                EditorUtility.SetDirty(pickup);
            }
            if (!d.TryGetComponent<NetworkObject>(out _))
                report.AppendLine($"'{d.name}' has no NetworkObject: add one.");
        }

        // The catalog lists them by id.
        var sorted = datas.OrderBy(d => d.item.id).ToList();
        if (catalog == null)
            report.AppendLine("No ItemCatalog in the open scene: open the scene with it and run this again.");
        else
        {
            var so = new SerializedObject(catalog);
            FillSizeDefaults(so, report);
            var list = so.FindProperty("itemPrefabs");
            bool same = list.arraySize == sorted.Count;
            for (int i = 0; same && i < sorted.Count; i++)
                same = list.GetArrayElementAtIndex(i).objectReferenceValue == sorted[i];
            if (!same)
            {
                list.arraySize = sorted.Count;
                for (int i = 0; i < sorted.Count; i++) list.GetArrayElementAtIndex(i).objectReferenceValue = sorted[i];
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(catalog);
                if (!Application.isPlaying) EditorSceneManager.MarkSceneDirty(catalog.gameObject.scene);
                report.AppendLine($"ItemCatalog lists {sorted.Count} item prefab(s).");
            }
        }

        // Every item prefab in the network prefab list.
        int added = AddToNetworkPrefabs(datas.Select(d => d.gameObject));
        if (added > 0) report.AppendLine($"Added {added} item prefab(s) to the network prefab list.");

        allPrefabs = null;
        AssetDatabase.SaveAssets();
        if (report.Length > 0) Debug.Log($"[Items] Update Item Catalog ({datas.Count} item prefabs):\n{report}");
        else if (!quiet) Debug.Log($"[Items] Update Item Catalog: {datas.Count} item prefabs, all up to date.");
    }

    // Every prefab in the project, loaded once per update (several passes read it).
    private static List<(string path, GameObject go)> allPrefabs;

    private static List<(string path, GameObject go)> LoadAllPrefabs()
    {
        var list = new List<(string, GameObject)>();
        foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go != null) list.Add((path, go));
        }
        return list;
    }

    /// <summary>A name or folder name as words: underscores are spaces (and no doubled spaces).</summary>
    public static string Words(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string t = text.Replace('_', ' ');
        while (t.Contains("  ")) t = t.Replace("  ", " ");
        return t.Trim();
    }

    // Folders that say where in the project, not what it is: not categories.
    private static readonly HashSet<string> NotCategories = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "assets", "prefabs", "items", "small", "sml", "sm", "med", "medium", "md", "lrg", "large", "lg", "misc",
        "stocks", "stock",
    };

    /// <summary>
    /// Categories from the folders an item's prefab is in: under Items, without the size folders, Stocks, or
    /// the item's own folder (that's just the item: Books/Book1/Book1.prefab is Books). Only its own folder
    /// left: that one. Items/Small/Stocks/Pants/Jeans/Cheap/x.prefab → Pants, Jeans.
    /// All: every folder name (only the size folders left out), for spotting categories that came from folders.
    /// </summary>
    public static List<string> FolderCategories(string assetPath, bool all = false)
    {
        var parts = assetPath.Replace('\\', '/').Split('/');
        int start = 0;
        for (int i = 0; i < parts.Length - 1; i++)
            if (string.Equals(parts[i], "Items", System.StringComparison.OrdinalIgnoreCase)) start = i + 1;
        int own = parts.Length - 2; // the folder the prefab is in
        var cats = new List<string>();
        string ownName = null;
        for (int i = start; i < parts.Length - 1; i++)
        {
            string c = Words(parts[i]);
            if (c.Length == 0 || SizeFolders.Contains(c)) continue;
            if (!all && NotCategories.Contains(c)) continue;
            if (!all && i == own) { ownName = c; continue; }
            if (!cats.Exists(x => string.Equals(x, c, System.StringComparison.OrdinalIgnoreCase))) cats.Add(c);
        }
        if (cats.Count == 0 && ownName != null) cats.Add(ownName);
        return cats;
    }

    private static readonly HashSet<string> SizeFolders = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "assets", "prefabs", "items", "small", "sml", "sm", "med", "medium", "md", "lrg", "large", "lg", "misc",
    };

    // Items with no categories get their folders'; names still the prefab's (underscores and all) get words.
    private static void FillFromFolders(List<ItemData> datas, StringBuilder report)
    {
        foreach (var d in datas)
        {
            if (d.item == null) continue;
            bool changed = false;
            // Given its folders' categories before some folders were left out (Stocks, its own folder): its
            // folders' categories as they are now. (Only when every category it has is one of its folders' names,
            // and it has all of today's: then they came from its folders, not from you.)
            var had = d.item.Categories.ToList();
            if (had.Count > 0)
            {
                string path = AssetDatabase.GetAssetPath(d);
                var now = FolderCategories(path);
                var folders = FolderCategories(path, all: true);
                var cmp = System.StringComparer.OrdinalIgnoreCase;
                bool fromFolders = had.All(c => folders.Contains(c, cmp)) && now.All(c => had.Contains(c, cmp));
                if (fromFolders && had.Count != now.Count)
                {
                    d.item.categories = now;
                    d.item.category = null;
                    d.item.CategoriesChanged();
                    report.AppendLine($"'{d.name}' categories: {string.Join(", ", now)} (left out {string.Join(", ", had.Where(c => !now.Contains(c, cmp)))}).");
                    changed = true;
                }
            }
            if (!d.item.Categories.Any())
            {
                var cats = FolderCategories(AssetDatabase.GetAssetPath(d));
                if (cats.Count > 0)
                {
                    d.item.categories = cats;
                    d.item.CategoriesChanged();
                    report.AppendLine($"'{d.name}' categories from its folders: {string.Join(", ", cats)}.");
                    changed = true;
                }
            }
            string name = d.item.displayName;
            if ((string.IsNullOrWhiteSpace(name) || name == d.gameObject.name) && Words(d.gameObject.name) != name)
            {
                d.item.displayName = Words(d.gameObject.name);
                changed = true;
            }
            if (changed) EditorUtility.SetDirty(d);
        }
    }

    // Small / Med / Lrg / Misc from the folders on the way to the prefab (Auto if none of them).
    private static ItemDef.SizeClass SizeFromFolder(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        for (int i = parts.Length - 2; i >= 0; i--) // nearest folder first
        {
            switch (parts[i].Trim().ToLowerInvariant())
            {
                case "small": case "sml": case "sm": return ItemDef.SizeClass.Small;
                case "med": case "medium": case "md": return ItemDef.SizeClass.Medium;
                case "lrg": case "large": case "lg": return ItemDef.SizeClass.Large;
                case "misc": return ItemDef.SizeClass.Misc;
            }
        }
        return ItemDef.SizeClass.Auto;
    }

    // Prefabs that are one product on a shelf or rack: in some stock crate's Shelf or Hang Prefabs.
    private static HashSet<GameObject> FindProductPrefabs()
    {
        var set = new HashSet<GameObject>();
        foreach (var (_, go) in allPrefabs)
        {
            if (!go.TryGetComponent<ItemPickupNet>(out var pickup) || !pickup.IsStockCrate) continue;
            var so = new SerializedObject(pickup);
            foreach (var listName in new[] { "shelfPrefabs", "hangPrefabs" })
            {
                var list = so.FindProperty(listName);
                if (list == null) continue;
                for (int i = 0; i < list.arraySize; i++)
                    if (list.GetArrayElementAtIndex(i).objectReferenceValue is GameObject p && p != null) set.Add(p);
            }
        }
        return set;
    }

    // Empty catalog defaults, from prefabs named for them (handLrg, shipmentLrg...).
    private static void FillSizeDefaults(SerializedObject catalog, StringBuilder report)
    {
        var byName = new Dictionary<string, GameObject>();
        foreach (var (path, go) in allPrefabs)
        {
            string key = System.IO.Path.GetFileNameWithoutExtension(path).Replace(" ", "").Replace("_", "").ToLowerInvariant();
            if (!byName.ContainsKey(key)) byName[key] = go;
        }
        GameObject Named(string kind, params string[] sizes)
        {
            foreach (var size in sizes)
                if (byName.TryGetValue(kind + size, out var go) && go != null) return go;
            return null;
        }
        var slots = new (string field, string[] names)[]
        {
            ("small", new[] { "small", "sml", "sm" }),
            ("medium", new[] { "med", "medium", "md" }),
            ("large", new[] { "lrg", "large", "lg" }),
            ("misc", new[] { "misc" }),
        };
        bool changed = false;
        foreach (var (field, names) in slots)
        {
            var hand = catalog.FindProperty(field + ".hand");
            if (hand != null && hand.objectReferenceValue == null && Named("hand", names) is GameObject h)
            {
                hand.objectReferenceValue = h;
                report.AppendLine($"Catalog {field} hand: '{h.name}'.");
                changed = true;
            }
            var ship = catalog.FindProperty(field + ".shipment");
            if (ship != null && ship.objectReferenceValue == null && Named("shipment", names) is GameObject s &&
                s.TryGetComponent<NetworkObject>(out var sno))
            {
                ship.objectReferenceValue = sno;
                report.AppendLine($"Catalog {field} shipment: '{s.name}'.");
                changed = true;
            }
        }
        var def = catalog.FindProperty("defaultShipmentPrefab");
        if (def != null && def.objectReferenceValue == null && byName.TryGetValue("shipment", out var box) && box != null &&
            box.TryGetComponent<NetworkObject>(out var bno))
        {
            def.objectReferenceValue = bno;
            report.AppendLine($"Catalog default shipment: '{box.name}'.");
            changed = true;
        }
        if (changed)
        {
            catalog.ApplyModifiedPropertiesWithoutUndo();
            var target = (ItemCatalog)catalog.targetObject;
            EditorUtility.SetDirty(target);
            if (!Application.isPlaying) EditorSceneManager.MarkSceneDirty(target.gameObject.scene);
        }
    }

    // Every prefab with an ItemData on its root, in a stable order (by path).
    private static List<ItemData> FindItemPrefabs()
    {
        var found = new List<ItemData>();
        var box = StockBoxGenerator.Template; // the stock box's ItemData is only the made crates' starting values
        foreach (var (_, go) in allPrefabs)
            if (go != box && go.TryGetComponent<ItemData>(out var d)) found.Add(d);
        return found.OrderBy(d => AssetDatabase.GetAssetPath(d)).ToList();
    }

    private static ItemCatalog FindCatalog() =>
        Object.FindFirstObjectByType<ItemCatalog>(FindObjectsInactive.Include);

    // The older entries listed on the catalog itself.
    private static List<ItemDef> LegacyItems(ItemCatalog catalog)
    {
        var result = new List<ItemDef>();
        var prop = new SerializedObject(catalog).FindProperty("items");
        for (int i = 0; i < prop.arraySize; i++)
            if (prop.GetArrayElementAtIndex(i).boxedValue is ItemDef def) result.Add(def);
        return result;
    }

    private static int AddToNetworkPrefabs(IEnumerable<GameObject> prefabs)
    {
        var lists = AssetDatabase.FindAssets("t:NetworkPrefabsList")
            .Select(g => AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(AssetDatabase.GUIDToAssetPath(g)))
            .Where(l => l != null).ToList();
        if (lists.Count == 0) return 0;
        var target = lists.FirstOrDefault(l => l.name == "DefaultNetworkPrefabs") ?? lists[0];
        int added = 0;
        foreach (var go in prefabs)
        {
            if (go == null || !go.TryGetComponent<NetworkObject>(out _)) continue;
            if (lists.Any(l => l.PrefabList.Any(p => p != null && p.Prefab == go))) continue;
            target.Add(new NetworkPrefab { Prefab = go });
            added++;
        }
        if (added > 0) EditorUtility.SetDirty(target);
        return added;
    }

    /* ========================= MOVING OLD ITEMS ONTO PREFABS ========================= */

    [MenuItem(MenuRoot + "Move Catalog Items Onto Prefabs", priority = 20)]
    private static void MoveOntoPrefabs()
    {
        var catalog = FindCatalog();
        if (catalog == null)
        {
            EditorUtility.DisplayDialog("Move Catalog Items", "No ItemCatalog in the open scene.", "OK");
            return;
        }
        var so = new SerializedObject(catalog);
        var items = so.FindProperty("items");
        if (items.arraySize == 0)
        {
            EditorUtility.DisplayDialog("Move Catalog Items", "The ItemCatalog has no items listed on it: nothing to move.", "OK");
            return;
        }
        if (!EditorUtility.DisplayDialog("Move Catalog Items",
                $"Move the {items.arraySize} item(s) listed on the ItemCatalog onto their world prefabs (as ItemData), " +
                "keeping their ids? Items without a world prefab stay on the catalog.", "Move", "Cancel"))
            return;

        var report = new StringBuilder();
        var done = new HashSet<GameObject>();
        var moved = new List<int>();
        for (int i = 0; i < items.arraySize; i++)
        {
            if (items.GetArrayElementAtIndex(i).boxedValue is not ItemDef def) continue;
            var prefab = def.worldNetworkPrefab != null ? def.worldNetworkPrefab.gameObject : null;
            string label = $"{def.id} '{def.displayName}'";
            if (prefab == null) { report.AppendLine($"{label}: no world prefab, left on the catalog."); continue; }
            if (!done.Add(prefab))
            {
                report.AppendLine($"{label}: its prefab '{prefab.name}' is already another item's, left on the catalog.");
                continue;
            }
            string path = AssetDatabase.GetAssetPath(prefab);
            if (string.IsNullOrEmpty(path)) { report.AppendLine($"{label}: its world prefab isn't a prefab asset, left on the catalog."); continue; }

            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                if (root.TryGetComponent<ItemData>(out var existing) && existing.item != null && existing.item.id > 0 &&
                    existing.item.id != def.id)
                {
                    report.AppendLine($"{label}: '{prefab.name}' already has an ItemData (item {existing.item.id}), left on the catalog.");
                    continue;
                }
                var data = existing != null ? existing : root.AddComponent<ItemData>();
                data.item = def.Copy();
                data.item.worldNetworkPrefab = null; // it's this prefab
                if (root.TryGetComponent<ItemPickupNet>(out var pickup)) pickup.itemId = def.id;
                PrefabUtility.SaveAsPrefabAsset(root, path);
                moved.Add(i);
                report.AppendLine($"{label} → '{path}'.");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        // Off the catalog's own list (from the end, so the indexes stay right).
        for (int k = moved.Count - 1; k >= 0; k--) items.DeleteArrayElementAtIndex(moved[k]);
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(catalog);
        EditorSceneManager.MarkSceneDirty(catalog.gameObject.scene);
        AssetDatabase.SaveAssets();
        Debug.Log($"[Items] Moved {moved.Count} item(s) onto their prefabs. Save the scene to keep the change.\n{report}");
        UpdateCatalog(quiet: false);
    }
}

/// <summary>
/// ItemData in the Inspector: the id shown (not edited; given by Tools > Mall Sim > Items > Update Item
/// Catalog), the rest of the entry as usual. Several selected at once can be edited together.
/// </summary>
[CustomEditor(typeof(ItemData)), CanEditMultipleObjects]
public class ItemDataEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        var id = serializedObject.FindProperty("item.id");
        string shown = id.hasMultipleDifferentValues ? "—" : id.intValue > 0 ? id.intValue.ToString() : "none yet (given before Play)";
        EditorGUILayout.LabelField("Item Id", shown);
        EditorGUILayout.Space(4);

        var prop = serializedObject.FindProperty("item");
        var end = prop.GetEndProperty();
        bool enter = true;
        while (prop.NextVisible(enter) && !SerializedProperty.EqualContents(prop, end))
        {
            enter = false;
            if (prop.name == "id" || prop.name == "worldNetworkPrefab") continue; // filled in for you
            EditorGUILayout.PropertyField(prop, true);
        }
        serializedObject.ApplyModifiedProperties();
    }
}
