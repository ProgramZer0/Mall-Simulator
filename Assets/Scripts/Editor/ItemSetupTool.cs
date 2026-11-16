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
        foreach (var d in needId)
        {
            while (used.Contains(next)) next++;
            if (next >= Sizes.FirstSizedId) { Debug.LogError("[Items] Out of ids (sizes start at " + Sizes.FirstSizedId + ")."); break; }
            d.item ??= new ItemDef();
            d.item.id = next;
            used.Add(next);
            EditorUtility.SetDirty(d);
            report.AppendLine($"'{d.name}' is item {next}.");
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

        AssetDatabase.SaveAssets();
        if (report.Length > 0) Debug.Log($"[Items] Update Item Catalog ({datas.Count} item prefabs):\n{report}");
        else if (!quiet) Debug.Log($"[Items] Update Item Catalog: {datas.Count} item prefabs, all up to date.");
    }

    // Every prefab with an ItemData on its root, in a stable order (by path).
    private static List<ItemData> FindItemPrefabs()
    {
        var found = new List<ItemData>();
        foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go != null && go.TryGetComponent<ItemData>(out var d)) found.Add(d);
        }
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

/// <summary>ItemData in the Inspector: the id shown (not edited), the rest of the entry as usual.</summary>
[CustomEditor(typeof(ItemData))]
public class ItemDataEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        var data = (ItemData)target;
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField("Item Id", data.Id > 0 ? data.Id.ToString() : "none yet (given before Play)");
            if (GUILayout.Button("Update Item Catalog", GUILayout.Width(150))) ItemSetupTool.UpdateCatalog(quiet: false);
        }
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
