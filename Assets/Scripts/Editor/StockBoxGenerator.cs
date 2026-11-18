using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Stock crates without making one per product. Put product models in a folder per product under the
/// BoxStock box's folder; a model named (or in a folder named) with "hang" goes on rails, "fold" / "shelf" /
/// "self" on shelves (StockBoxSettings). Update Item Catalog then makes "&lt;folder&gt; Stock" in that product
/// folder: a variant of the box with those models and a products-per-crate worked out from the sizes, then
/// gives it an id like any item. Add another color to the folder later and it's added to the crate.
///
/// The crate's price, categories (plus Boxed), rarity, colors and color tastes, sizes, delivery time and so on come from its product's
/// ItemData (the shelf model's, else the hanging one's) and are kept in step every run: set them on the product.
/// Its name and amount are its own to change. Crates made by hand are left alone, and so are their products.
/// </summary>
public static class StockBoxGenerator
{
    private const string SettingsPath = "Assets/Settings/StockBoxSettings.asset";
    private static readonly Regex AmountInName = new(@"\s*[xX](\d+)\s*$");

    public static StockBoxSettings Settings(bool create)
    {
        var s = AssetDatabase.LoadAssetAtPath<StockBoxSettings>(SettingsPath);
        if (s == null)
        {
            var guid = AssetDatabase.FindAssets("t:StockBoxSettings").FirstOrDefault();
            if (guid != null) s = AssetDatabase.LoadAssetAtPath<StockBoxSettings>(AssetDatabase.GUIDToAssetPath(guid));
        }
        if (s == null && create)
        {
            if (!AssetDatabase.IsValidFolder("Assets/Settings")) AssetDatabase.CreateFolder("Assets", "Settings");
            s = ScriptableObject.CreateInstance<StockBoxSettings>();
            AssetDatabase.CreateAsset(s, SettingsPath);
        }
        if (s != null && s.Upgrade()) EditorUtility.SetDirty(s);
        if (s != null && s.template == null)
        {
            var box = FindPrefabNamed("BoxStock");
            if (box != null) { s.template = box; EditorUtility.SetDirty(s); }
        }
        return s;
    }

    /// <summary>The box crates are made from (null if there isn't one).</summary>
    public static GameObject Template => Settings(false)?.template;

    /// <summary>Is this prefab a crate made from the box (a variant of it)?</summary>
    public static bool IsMadeCrate(GameObject prefab, GameObject template) =>
        prefab != null && template != null && prefab != template &&
        PrefabUtility.GetPrefabAssetType(prefab) == PrefabAssetType.Variant &&
        PrefabUtility.GetCorrespondingObjectFromSource(prefab) == template;

    [MenuItem("Tools/Mall Sim/Items/Recalculate Stock Crate Amounts", priority = 10)]
    private static void RecalculateMenu()
    {
        var report = new StringBuilder();
        Run(report, recalcUnits: true);
        Debug.Log(report.Length > 0 ? $"[Items] Stock crates:\n{report}" : "[Items] Stock crates: nothing to change.");
    }

    /// <summary>Make crates for new products and keep made ones in step. Called by Update Item Catalog.</summary>
    public static void Run(StringBuilder report, bool recalcUnits = false)
    {
        var s = Settings(create: true);
        if (s == null || !s.generate) return;
        var template = s.template;
        if (template == null) return; // no box yet: nothing to make
        if (!template.TryGetComponent<ItemPickupNet>(out _) || !template.TryGetComponent<NetworkObject>(out _))
        {
            report.AppendLine($"Stock box '{template.name}' needs a NetworkObject and an ItemPickupNet: no crates made.");
            return;
        }
        string root = Path.GetDirectoryName(AssetDatabase.GetAssetPath(template)).Replace('\\', '/');

        // Every prefab under the box's folder; which models crates made by hand already use.
        var prefabs = new List<(string path, GameObject go)>();
        foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { root }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go != null) prefabs.Add((path, go));
        }
        var handMade = new HashSet<GameObject>();
        var made = new Dictionary<string, (string path, GameObject go)>(); // product folder → its made crate
        foreach (var (path, go) in prefabs)
        {
            if (!go.TryGetComponent<ItemPickupNet>(out var crate) || !crate.IsStockCrate) continue;
            if (IsMadeCrate(go, template)) { made[Folder(path)] = (path, go); continue; }
            if (go == template) continue;
            var so = new SerializedObject(crate);
            foreach (var list in new[] { "shelfPrefabs", "hangPrefabs" })
            {
                var p = so.FindProperty(list);
                for (int i = 0; p != null && i < p.arraySize; i++)
                    if (p.GetArrayElementAtIndex(i).objectReferenceValue is GameObject m && m != null) handMade.Add(m);
            }
        }

        // Product models, by product folder.
        var products = new SortedDictionary<string, (List<GameObject> hang, List<GameObject> shelf)>();
        foreach (var (path, go) in prefabs.OrderBy(p => p.path))
        {
            if (go == template || handMade.Contains(go) || IsMadeCrate(go, template)) continue;
            if (go.TryGetComponent<ItemPickupNet>(out var pickup) && pickup.IsStockCrate) continue;
            string folder = Folder(path);
            string name = Path.GetFileNameWithoutExtension(path);
            string folderName = Path.GetFileName(folder);
            bool hang = Has(name, s.hangWords) || Has(folderName, s.hangWords);
            bool shelf = !hang && (Has(name, s.shelfWords) || Has(folderName, s.shelfWords));
            if (!hang && !shelf) continue;
            // In a "Hang" / "Folded" folder of its own: the product is the folder above.
            string product = Has(folderName, hang ? s.hangWords : s.shelfWords) ? Folder(folder) : folder;
            if (product == root || string.IsNullOrEmpty(product)) continue; // loose in the box's own folder: no product folder
            if (!products.TryGetValue(product, out var lists)) products[product] = lists = (new List<GameObject>(), new List<GameObject>());
            (hang ? lists.hang : lists.shelf).Add(go);
        }

        foreach (var kv in products)
        {
            string folder = kv.Key;
            var (hang, shelf) = kv.Value;
            string folderName = Path.GetFileName(folder);
            int? fixedUnits = AmountInName.Match(folderName) is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;
            string productName = ItemSetupTool.Words(AmountInName.Replace(folderName, ""));
            if (made.TryGetValue(folder, out var existing))
            {
                // Keep Units on the crate: its amount is yours.
                bool keep = existing.go.TryGetComponent<ItemPickupNet>(out var crate) && crate.KeepUnits;
                bool work = !keep && (recalcUnits || fixedUnits.HasValue);
                Update(s, existing.path, hang, shelf, work ? Units(s, template, hang, shelf, fixedUnits) : (int?)null, report);
            }
            else
                Make(s, template, folder, productName, hang, shelf, Units(s, template, hang, shelf, fixedUnits), report);
        }
    }

    // A new crate: a variant of the box with the product's models, its own name and no id yet.
    private static void Make(StockBoxSettings s, GameObject template, string folder, string productName,
                             List<GameObject> hang, List<GameObject> shelf, int units, StringBuilder report)
    {
        string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{productName}{s.nameSuffix}.prefab");
        var scene = EditorSceneManager.NewPreviewScene(); // so the open scene isn't touched
        try
        {
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(template, scene);
            inst.name = Path.GetFileNameWithoutExtension(path);
            SetModels(inst.GetComponent<ItemPickupNet>(), hang, shelf, units);

            var data = inst.GetComponent<ItemData>();
            if (data == null) data = inst.AddComponent<ItemData>();
            data.item = data.item != null ? data.item.Copy() : new ItemDef(); // the box's values to start from
            data.item.id = 0;
            data.item.displayName = productName;
            data.item.worldNetworkPrefab = null;
            data.item.showInShop = true;
            FromProduct(s, inst.GetComponent<ItemPickupNet>(), data, Product(hang, shelf), units, CrateIcon(s, template, path, hang, shelf));
            PrefabUtility.RecordPrefabInstancePropertyModifications(data);

            PrefabUtility.SaveAsPrefabAsset(inst, path);
        }
        finally { EditorSceneManager.ClosePreviewScene(scene); }
        FreshNetworkId(path);
        report.AppendLine($"Made stock crate '{path}': {Describe(hang, shelf)}, {units} per crate.");
    }

    // A made crate: its models in step with its folder (and its amount, when given).
    private static void Update(StockBoxSettings s, string path, List<GameObject> hang, List<GameObject> shelf, int? units,
                               StringBuilder report)
    {
        // Looked at as it is first (cheap): only opened and saved when something's different.
        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (asset == null || !asset.TryGetComponent<ItemPickupNet>(out var current)) return;
        var product = Product(hang, shelf);
        var cur = new SerializedObject(current);
        int count = units ?? cur.FindProperty("unitsPerCrate").intValue;
        bool same = SameList(cur.FindProperty("hangPrefabs"), hang) && SameList(cur.FindProperty("shelfPrefabs"), shelf) &&
                    cur.FindProperty("isStockCrate").boolValue && cur.FindProperty("unitsPerCrate").intValue == count &&
                    (PoseOf(product) is not int pose || cur.FindProperty("shelfPose").enumValueIndex == pose);
        if (same && asset.TryGetComponent<ItemData>(out var curData) && curData.item != null)
        {
            var want = curData.item.Copy();
            ApplyProduct(s, want, product, count, CrateIcon(s, s.template, path, hang, shelf));
            same = JsonUtility.ToJson(want) == JsonUtility.ToJson(curData.item);
        }
        if (same) return;

        var root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            var pickup = root.GetComponent<ItemPickupNet>();
            if (pickup == null) return;
            var data = root.GetComponent<ItemData>();
            SetModels(pickup, hang, shelf, count);
            if (data != null) FromProduct(s, pickup, data, product, count, CrateIcon(s, s.template, path, hang, shelf));
            PrefabUtility.SaveAsPrefabAsset(root, path);
            report.AppendLine($"Updated stock crate '{path}': {Describe(hang, shelf)}, {count} per crate.");
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }

    // The product a crate is priced and described from: its shelf model, else its hanging one.
    private static GameObject Product(List<GameObject> hang, List<GameObject> shelf) =>
        shelf.Count > 0 ? shelf[0] : hang.Count > 0 ? hang[0] : null;

    /// <summary>
    /// What a crate takes from its product's ItemData (kept in step every run; set it on the product):
    /// price (one product's times how many are in a crate), what one's worth, categories (plus Boxed),
    /// rarity, sizes and size step, delivery time, level, special market. Its icon (see CrateIcon). Always in the shop. And how
    /// its products may sit on shelves, from the product's ItemPickupNet.
    /// </summary>
    private static void FromProduct(StockBoxSettings s, ItemPickupNet crate, ItemData data, GameObject product, int units,
                                    Sprite icon)
    {
        if (data.item == null) data.item = new ItemDef();
        ApplyProduct(s, data.item, product, units, icon);

        // Shelf Pose is on the crate but says how its products sit: the product's own setting, if it has one.
        if (crate != null && PoseOf(product) is int pose)
        {
            var so = new SerializedObject(crate);
            var to = so.FindProperty("shelfPose");
            if (to != null && to.enumValueIndex != pose)
            {
                to.enumValueIndex = pose;
                so.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.RecordPrefabInstancePropertyModifications(crate);
            }
        }
    }

    /* ========================= ICONS ========================= */

    // A product model's own icon (its ItemData's), or null.
    private static Sprite IconOf(GameObject model) =>
        model != null && model.TryGetComponent<ItemData>(out var d) && d.item != null ? d.item.icon : null;

    /// <summary>
    /// The crate's icon: its hanging product's icon (else its shelf product's) in front of the box's own icon
    /// (BoxStock's ItemData). Saved as "&lt;crate&gt; Icon.png" next to the crate, and only drawn again when one
    /// of the two pictures (or the settings) changes. No product icon: the box's. No box icon: the product's.
    /// </summary>
    private static Sprite CrateIcon(StockBoxSettings s, GameObject box, string cratePath, List<GameObject> hang, List<GameObject> shelf)
    {
        Sprite product = hang.Select(IconOf).FirstOrDefault(i => i != null) ?? shelf.Select(IconOf).FirstOrDefault(i => i != null);
        Sprite back = IconOf(box);
        if (product == null || back == null) return product ?? back;

        string iconPath = Path.ChangeExtension(cratePath, null) + " Icon.png";
        string key = $"{SourceKey(product)}|{SourceKey(back)}|{s.iconSize}|{s.iconProductScale:0.###}|{s.iconProductOffset}";
        var importer = AssetImporter.GetAtPath(iconPath) as TextureImporter;
        if (importer == null || importer.userData != key)
        {
            int size = Mathf.Clamp(s.iconSize, 32, 1024);
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var was = RenderTexture.active;
            try
            {
                RenderTexture.active = rt;
                GL.Clear(true, true, Color.clear);
                GL.PushMatrix();
                GL.LoadPixelMatrix(0, size, size, 0);
                Draw(back, new Rect(0, 0, size, size));
                float w = size * Mathf.Clamp(s.iconProductScale, 0.1f, 1f);
                Vector2 at = new Vector2((size - w) * 0.5f, (size - w) * 0.5f) +
                             new Vector2(s.iconProductOffset.x, -s.iconProductOffset.y) * size;
                Draw(product, new Rect(at.x, at.y, w, w));
                GL.PopMatrix();
                tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                tex.Apply();
            }
            finally
            {
                RenderTexture.active = was;
                RenderTexture.ReleaseTemporary(rt);
            }
            File.WriteAllBytes(iconPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(iconPath, ImportAssetOptions.ForceUpdate);
            importer = (TextureImporter)AssetImporter.GetAtPath(iconPath);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.userData = key;
            importer.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Sprite>(iconPath);
    }

    // A sprite drawn into a square, keeping its shape (centered).
    private static void Draw(Sprite sprite, Rect into)
    {
        Rect r = sprite.textureRect;
        var t = sprite.texture;
        float aspect = r.width / Mathf.Max(1f, r.height);
        Rect dest = into;
        if (aspect > 1f) { dest.height = into.width / aspect; dest.y += (into.height - dest.height) * 0.5f; }
        else { dest.width = into.height * aspect; dest.x += (into.width - dest.width) * 0.5f; }
        var source = new Rect(r.x / t.width, r.y / t.height, r.width / t.width, r.height / t.height);
        Graphics.DrawTexture(dest, t, source, 0, 0, 0, 0);
    }

    // Which picture, and which version of it (so an edited icon is drawn again).
    private static string SourceKey(Sprite sprite)
    {
        string path = AssetDatabase.GetAssetPath(sprite);
        return AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out string guid, out long id)
            ? $"{guid}:{id}:{AssetDatabase.GetAssetDependencyHash(path)}" : sprite.name;
    }

    // The product's Shelf Pose (on its ItemPickupNet), if it has one.
    private static int? PoseOf(GameObject product)
    {
        if (product == null || !product.TryGetComponent<ItemPickupNet>(out var pp)) return null;
        var from = new SerializedObject(pp).FindProperty("shelfPose");
        return from != null ? from.enumValueIndex : (int?)null;
    }

    private static void ApplyProduct(StockBoxSettings s, ItemDef item, GameObject product, int units, Sprite icon)
    {
        item.showInShop = true;
        if (icon != null) item.icon = icon;
        if (product == null) return;

        if (product.TryGetComponent<ItemData>(out var pd) && pd.item != null)
        {
            var p = pd.item;
            item.price = Mathf.Max(0, p.price) * Mathf.Max(1, units);
            item.marketPrice = p.marketPrice; // worth to customers is per product (counted per product sold)
            var cats = new List<string>();
            foreach (var c in p.Categories)
                if (!cats.Exists(x => string.Equals(x, c, System.StringComparison.OrdinalIgnoreCase))) cats.Add(c);
            if (!string.IsNullOrWhiteSpace(s.boxedCategory) &&
                !cats.Exists(x => string.Equals(x, s.boxedCategory.Trim(), System.StringComparison.OrdinalIgnoreCase)))
                cats.Add(s.boxedCategory.Trim());
            item.categories = cats;
            item.category = null;
            item.CategoriesChanged();
            item.rarity = p.rarity;
            item.colors = p.colors != null ? new List<string>(p.colors) : new List<string>();
            item.colorTastes = p.colorTastes != null
                ? p.colorTastes.Where(t => t != null).Select(t => new ColorTaste { color = t.color, weight = t.weight }).ToList()
                : new List<ColorTaste>();
            item.sizes = p.sizes;
            item.sizePriceStep = p.sizePriceStep;
            item.deliverySeconds = p.deliverySeconds;
            item.minLevel = p.minLevel;
            item.inSpecialMarket = p.inSpecialMarket;
            item.specialMarketWeight = p.specialMarketWeight;
        }
    }

    private static void SetModels(ItemPickupNet pickup, List<GameObject> hang, List<GameObject> shelf, int units)
    {
        var so = new SerializedObject(pickup);
        so.FindProperty("isStockCrate").boolValue = true;
        SetList(so.FindProperty("hangPrefabs"), hang);
        SetList(so.FindProperty("shelfPrefabs"), shelf);
        so.FindProperty("unitsPerCrate").intValue = Mathf.Max(1, units);
        so.ApplyModifiedPropertiesWithoutUndo();
        PrefabUtility.RecordPrefabInstancePropertyModifications(pickup);
    }

    // How many go in a crate: the folder's xN, or the box's size over one product's (with gaps).
    private static int Units(StockBoxSettings s, GameObject box, List<GameObject> hang, List<GameObject> shelf, int? fixedUnits)
    {
        if (fixedUnits.HasValue) return Mathf.Max(1, fixedUnits.Value);
        var product = shelf.Count > 0 ? shelf[0] : hang.Count > 0 ? hang[0] : null;
        float one = Volume(product), room = Volume(box);
        if (one <= 1e-7f || room <= 0f) return s.minUnits;
        return Mathf.Clamp(Mathf.FloorToInt(room * s.packing / one), s.minUnits, Mathf.Max(s.minUnits, s.maxUnits));
    }

    private static float Volume(GameObject prefab)
    {
        if (prefab == null) return 0f;
        Bounds b = PlacementCheck.ComputeLocalBounds(prefab);
        Vector3 sc = prefab.transform.localScale;
        Vector3 size = Vector3.Scale(b.size, new Vector3(Mathf.Abs(sc.x), Mathf.Abs(sc.y), Mathf.Abs(sc.z)));
        return size.x * size.y * size.z;
    }

    // A new prefab asset's NetworkObject works out its own id (otherwise it may keep the box's).
    private static void FreshNetworkId(string path)
    {
        AssetDatabase.ImportAsset(path);
        var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (go == null || !go.TryGetComponent<NetworkObject>(out var no)) return;
        var validate = typeof(NetworkObject).GetMethod("OnValidate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        validate?.Invoke(no, null);
        EditorUtility.SetDirty(no);
    }

    private static string Describe(List<GameObject> hang, List<GameObject> shelf) =>
        string.Join(", ", new[]
        {
            hang.Count > 0 ? $"hangs {string.Join(" / ", hang.Select(g => g.name))}" : null,
            shelf.Count > 0 ? $"on shelves {string.Join(" / ", shelf.Select(g => g.name))}" : null,
        }.Where(t => t != null));

    private static bool SameList(SerializedProperty p, List<GameObject> want)
    {
        if (p == null || p.arraySize != want.Count) return false;
        for (int i = 0; i < want.Count; i++)
            if (p.GetArrayElementAtIndex(i).objectReferenceValue != want[i]) return false;
        return true;
    }

    private static void SetList(SerializedProperty p, List<GameObject> items)
    {
        if (p == null) return;
        p.arraySize = items.Count;
        for (int i = 0; i < items.Count; i++) p.GetArrayElementAtIndex(i).objectReferenceValue = items[i];
    }

    private static bool Has(string text, string[] words)
    {
        if (string.IsNullOrEmpty(text) || words == null) return false;
        foreach (var w in words)
            if (!string.IsNullOrWhiteSpace(w) && text.IndexOf(w.Trim(), System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    private static string Folder(string path) => Path.GetDirectoryName(path)?.Replace('\\', '/');

    private static GameObject FindPrefabNamed(string name)
    {
        foreach (var guid in AssetDatabase.FindAssets($"{name} t:Prefab"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (Path.GetFileNameWithoutExtension(path) == name) return AssetDatabase.LoadAssetAtPath<GameObject>(path);
        }
        return null;
    }
}
