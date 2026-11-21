using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Tools > Mall Sim > Mall Setup: building a mall's stores quickly, and checking the map for mistakes.
///
/// Fit: drop a small BoxCollider anywhere inside a room and Fit grows it out to the room's walls, floor
/// and ceiling (things for sale, other stores' areas, docks, for-sale and renovation walls don't stop it).
/// Put down a store's segment walls (its False Walls) first and each area stops at them, so the base area
/// and every expansion fill their own part. Areas go to the middle of a segment wall (not its face), so once
/// the wall's gone with the expansion bought there's no strip of floor between them that isn't the store's.
/// "Fit Store" refits every area box of the selected stores.
///
/// Find fills in a store's Storefront Walls (CuttableWalls along its edge) and Floor Renderers.
///
/// Prices: sets each store's and segment's price from its floor space, and starter stores' Starting
/// Money from what's left of the starting cash (Keep Price on a store or segment leaves it alone).
///
/// Check: looks over the whole open scene for setup mistakes (stores with no door, overlapping stores,
/// shipment areas outside any dock, docks no store uses, missing managers...) and lists them; click one
/// to select what it's about.
/// </summary>
public class MallSetupWindow : EditorWindow
{
    [MenuItem("Tools/Mall Sim/Mall Setup", priority = 0)]
    private static void Open() => GetWindow<MallSetupWindow>("Mall Setup");

    [MenuItem("Tools/Mall Sim/Check Mall Setup", priority = 1)]
    private static void OpenAndCheck()
    {
        var w = GetWindow<MallSetupWindow>("Mall Setup");
        w.tab = 2;
        w.RunCheck();
    }

    private int tab;
    private Vector2 scroll;

    // Fit settings (kept between sessions).
    private LayerMask wallMask = ~0;
    private float defaultHeight = 3f;
    private float maxReach = 60f;
    private float wallBuffer = 0.1f;
    private float ceilingBuffer = 0.25f;

    // Price settings (kept between sessions).
    private float pricePerSqFt = 10f;
    private float segmentDiscount = 0.25f;
    private int roundTo = 100;
    private int startCash = 50000;
    private int minStartMoney = 1000;
    private Vector2 priceScroll;

    private void OnEnable()
    {
        pricePerSqFt = EditorPrefs.GetFloat("MallSetup.PricePerSqFt", 10f);
        segmentDiscount = EditorPrefs.GetFloat("MallSetup.SegmentDiscount", 0.25f);
        roundTo = EditorPrefs.GetInt("MallSetup.RoundTo", 100);
        startCash = EditorPrefs.GetInt("MallSetup.StartCash", 50000);
        minStartMoney = EditorPrefs.GetInt("MallSetup.MinStartMoney", 1000);
        wallMask = EditorPrefs.GetInt("MallSetup.WallMask", ~0);
        defaultHeight = EditorPrefs.GetFloat("MallSetup.DefaultHeight", 3f);
        maxReach = EditorPrefs.GetFloat("MallSetup.MaxReach", 60f);
        wallBuffer = EditorPrefs.GetFloat("MallSetup.WallBuffer", 0.1f);
        ceilingBuffer = EditorPrefs.GetFloat("MallSetup.CeilingBuffer", 0.25f);
    }

    private void OnGUI()
    {
        tab = GUILayout.Toolbar(tab, new[] { "Fit Stores", "Prices", "Check Mall" });
        EditorGUILayout.Space();
        if (tab == 0) FitGUI(); else if (tab == 1) PricesGUI(); else CheckGUI();
    }

    /* ========================= PRICES ========================= */

    private const float SqFtPerSqM = 10.7639f;

    private void PricesGUI()
    {
        EditorGUILayout.HelpBox(
            "Prices from floor space (the store's area boxes, seen from above; overlaps count twice):\n" +
            "- A store's Price: its base area x the price per square foot.\n" +
            "- Each segment: its area x the price, less the segment discount.\n" +
            "- A starter store's Starting Money: the starting cash less its Price (never below the minimum), so " +
            "a bigger starter store means less cash.\n" +
            "Everything's rounded. Tick Keep Price on a store or segment to leave a price you've set by hand.", MessageType.Info);

        EditorGUI.BeginChangeCheck();
        pricePerSqFt = Mathf.Max(0f, EditorGUILayout.FloatField(new GUIContent("Price per sq ft ($)"), pricePerSqFt));
        segmentDiscount = EditorGUILayout.Slider(new GUIContent("Segment discount", "Off segments' price (0.25 = 25% off)."), segmentDiscount, 0f, 0.95f);
        roundTo = Mathf.Max(1, EditorGUILayout.IntField(new GUIContent("Round to ($)", "Prices are rounded to the nearest of this."), roundTo));
        startCash = Mathf.Max(0, EditorGUILayout.IntField(new GUIContent("Starting cash ($)",
            "What each team would have before paying for its starter store."), startCash));
        minStartMoney = Mathf.Max(0, EditorGUILayout.IntField(new GUIContent("Min starting money ($)",
            "A starter store never leaves its team with less than this."), minStartMoney));
        if (EditorGUI.EndChangeCheck())
        {
            EditorPrefs.SetFloat("MallSetup.PricePerSqFt", pricePerSqFt);
            EditorPrefs.SetFloat("MallSetup.SegmentDiscount", segmentDiscount);
            EditorPrefs.SetInt("MallSetup.RoundTo", roundTo);
            EditorPrefs.SetInt("MallSetup.StartCash", startCash);
            EditorPrefs.SetInt("MallSetup.MinStartMoney", minStartMoney);
        }
        EditorGUILayout.Space();

        var all = Object.FindObjectsByType<StorePlot>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .OrderBy(p => p.name).ToList();
        var selected = Selection.gameObjects.Select(g => g.GetComponentInParent<StorePlot>()).Where(p => p != null).Distinct().ToList();
        EditorGUILayout.BeginHorizontal();
        using (new EditorGUI.DisabledScope(all.Count == 0))
            if (GUILayout.Button($"Set prices: every store ({all.Count})", GUILayout.Height(26))) SetPrices(all);
        using (new EditorGUI.DisabledScope(selected.Count == 0))
            if (GUILayout.Button($"Selected stores ({selected.Count})", GUILayout.Height(26))) SetPrices(selected);
        EditorGUILayout.EndHorizontal();

        // What it would set (and what's there now).
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Preview (now → new)", EditorStyles.boldLabel);
        priceScroll = EditorGUILayout.BeginScrollView(priceScroll);
        foreach (var plot in all)
        {
            var so = new SerializedObject(plot);
            bool starter = so.FindProperty("isStarterPlot").boolValue, keep = so.FindProperty("keepPrice").boolValue;
            float ft = SqFt(Objects<BoxCollider>(so.FindProperty("baseAreas")));
            int newPrice = Round(ft * pricePerSqFt);
            string line = $"{so.FindProperty("displayName").stringValue} ({plot.name}): {ft:N0} sq ft, price " +
                          (keep ? $"{so.FindProperty("price").intValue:N0} (kept)" : $"{so.FindProperty("price").intValue:N0} → {newPrice:N0}");
            if (starter)
                line += keep ? $", start money {so.FindProperty("startingMoney").intValue:N0} (kept)"
                             : $", start money {so.FindProperty("startingMoney").intValue:N0} → {StartMoney(newPrice):N0}";
            var segs = so.FindProperty("segments");
            for (int i = 0; segs != null && i < segs.arraySize; i++)
            {
                var seg = segs.GetArrayElementAtIndex(i);
                float sft = SegmentSqFt(seg);
                bool skeep = seg.FindPropertyRelative("keepPrice").boolValue;
                int now = seg.FindPropertyRelative("price").intValue;
                line += $"\n    {seg.FindPropertyRelative("name").stringValue}: {sft:N0} sq ft, " +
                        (skeep ? $"{now:N0} (kept)" : $"{now:N0} → {SegmentPrice(sft):N0}");
            }
            if (GUILayout.Button(line, EditorStyles.helpBox)) { Selection.activeObject = plot; EditorGUIUtility.PingObject(plot); }
        }
        EditorGUILayout.EndScrollView();
    }

    private int Round(float dollars) => Mathf.Max(0, Mathf.RoundToInt(dollars / roundTo) * roundTo);
    private int SegmentPrice(float sqFt) => Round(sqFt * pricePerSqFt * (1f - segmentDiscount));
    private int StartMoney(int storePrice) => Mathf.Max(minStartMoney, Round(startCash - storePrice));

    // Floor space of some boxes, seen from above (their own X by Z, scaled), in square feet.
    private static float SqFt(IEnumerable<BoxCollider> boxes)
    {
        float m2 = 0f;
        foreach (var b in boxes)
        {
            if (b == null) continue;
            Vector3 s = b.transform.lossyScale;
            m2 += Mathf.Abs(b.size.x * s.x) * Mathf.Abs(b.size.z * s.z);
        }
        return m2 * SqFtPerSqM;
    }

    private static float SegmentSqFt(SerializedProperty seg)
    {
        var boxes = new List<BoxCollider>(Objects<BoxCollider>(seg.FindPropertyRelative("areas")));
        var old = seg.FindPropertyRelative("area").objectReferenceValue as BoxCollider;
        if (old != null && !boxes.Contains(old)) boxes.Add(old);
        return SqFt(boxes);
    }

    private void SetPrices(List<StorePlot> plots)
    {
        int changed = 0;
        foreach (var plot in plots)
        {
            var so = new SerializedObject(plot);
            if (!so.FindProperty("keepPrice").boolValue)
            {
                int p = Round(SqFt(Objects<BoxCollider>(so.FindProperty("baseAreas"))) * pricePerSqFt);
                so.FindProperty("price").intValue = p;
                if (so.FindProperty("isStarterPlot").boolValue) so.FindProperty("startingMoney").intValue = StartMoney(p);
            }
            var segs = so.FindProperty("segments");
            for (int i = 0; segs != null && i < segs.arraySize; i++)
            {
                var seg = segs.GetArrayElementAtIndex(i);
                if (!seg.FindPropertyRelative("keepPrice").boolValue)
                    seg.FindPropertyRelative("price").intValue = SegmentPrice(SegmentSqFt(seg));
            }
            if (so.ApplyModifiedProperties()) changed++; // (recorded for Undo)
        }
        Debug.Log($"[Mall Setup] Prices set on {changed} of {plots.Count} store(s).");
    }

    /* ========================= FIT ========================= */

    private void FitGUI()
    {
        EditorGUILayout.HelpBox(
            "1. Make an empty child of the store with a small BoxCollider inside the room (Add Area Box does it).\n" +
            "2. Put down segment walls (each Segment's False Walls) where expansions start.\n" +
            "3. Fit: each box grows to the walls, floor and ceiling around it.\n" +
            "Things for sale, store areas, docks, for-sale and renovation walls don't stop it.", MessageType.Info);

        EditorGUI.BeginChangeCheck();
        wallMask = LayerMaskField("Wall layers", wallMask);
        defaultHeight = EditorGUILayout.FloatField(new GUIContent("Height if no ceiling", "Meters above the floor."), defaultHeight);
        maxReach = EditorGUILayout.FloatField(new GUIContent("Max reach", "Furthest a side grows (meters)."), maxReach);
        wallBuffer = EditorGUILayout.FloatField(new GUIContent("Wall buffer",
            "How far the box goes past the walls (meters), so things right against a wall are inside. " +
            "Negative: a gap. (Segment walls: always to their middle.)"), wallBuffer);
        ceilingBuffer = EditorGUILayout.FloatField(new GUIContent("Ceiling buffer",
            "How far the box goes past the ceiling (meters), so ceiling lights and signs are inside."), ceilingBuffer);
        if (EditorGUI.EndChangeCheck())
        {
            EditorPrefs.SetInt("MallSetup.WallMask", wallMask);
            EditorPrefs.SetFloat("MallSetup.DefaultHeight", defaultHeight);
            EditorPrefs.SetFloat("MallSetup.MaxReach", maxReach);
            EditorPrefs.SetFloat("MallSetup.WallBuffer", wallBuffer);
            EditorPrefs.SetFloat("MallSetup.CeilingBuffer", ceilingBuffer);
        }
        EditorGUILayout.Space();

        var boxes = Selection.gameObjects.SelectMany(g => g.GetComponents<BoxCollider>()).ToList();
        var plots = Selection.gameObjects.Select(g => g.GetComponentInParent<StorePlot>()).Where(p => p != null).Distinct().ToList();

        using (new EditorGUI.DisabledScope(boxes.Count == 0))
            if (GUILayout.Button($"Fit selected box{(boxes.Count == 1 ? "" : "es")} to walls ({boxes.Count})"))
                Fit(boxes);
        using (new EditorGUI.DisabledScope(plots.Count == 0))
        {
            if (GUILayout.Button($"Fit every area of the selected store{(plots.Count == 1 ? "" : "s")} ({plots.Count})"))
                Fit(plots.SelectMany(AreasOf).ToList());
            if (GUILayout.Button("Add Area Box (in front of the Scene view camera)")) AddAreaBox(plots[0]);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Find Storefront Walls")) foreach (var p in plots) FindStorefrontWalls(p);
            if (GUILayout.Button("Find Floor Renderers")) foreach (var p in plots) FindFloorRenderers(p);
            EditorGUILayout.EndHorizontal();
        }
        if (plots.Count == 0)
            EditorGUILayout.HelpBox("Select a store (or something in it) for the store buttons.", MessageType.None);
    }

    private static LayerMask LayerMaskField(string label, LayerMask mask)
    {
        var names = Enumerable.Range(0, 32).Select(LayerMask.LayerToName).ToArray();
        var shown = names.Where(n => !string.IsNullOrEmpty(n)).ToArray();
        int compact = 0;
        for (int i = 0; i < shown.Length; i++)
            if ((mask & (1 << LayerMask.NameToLayer(shown[i]))) != 0) compact |= 1 << i;
        compact = EditorGUILayout.MaskField(label, compact, shown);
        int full = 0;
        for (int i = 0; i < shown.Length; i++)
            if ((compact & (1 << i)) != 0) full |= 1 << LayerMask.NameToLayer(shown[i]);
        if (compact == -1) full = ~0;
        return full;
    }

    private static IEnumerable<BoxCollider> AreasOf(StorePlot plot)
    {
        var so = new SerializedObject(plot);
        foreach (var b in Objects<BoxCollider>(so.FindProperty("baseAreas"))) yield return b;
        var segs = so.FindProperty("segments");
        for (int i = 0; segs != null && i < segs.arraySize; i++)
        {
            var seg = segs.GetArrayElementAtIndex(i);
            foreach (var b in Objects<BoxCollider>(seg.FindPropertyRelative("areas"))) yield return b;
            if (seg.FindPropertyRelative("area").objectReferenceValue is BoxCollider old) yield return old;
        }
    }

    private static IEnumerable<T> Objects<T>(SerializedProperty array) where T : Object
    {
        if (array == null || !array.isArray) yield break;
        for (int i = 0; i < array.arraySize; i++)
            if (array.GetArrayElementAtIndex(i).objectReferenceValue is T t && t != null) yield return t;
    }

    // What doesn't stop a box growing: shapes only (store areas, segments, docks, shipment areas), things
    // for sale and placed items, for-sale and renovation walls.
    private static HashSet<Collider> Ignored()
    {
        var set = new HashSet<Collider>();
        foreach (var plot in Object.FindObjectsByType<StorePlot>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var so = new SerializedObject(plot);
            foreach (var b in AreasOf(plot)) set.Add(b);
            foreach (var b in Objects<BoxCollider>(so.FindProperty("shipmentZones"))) set.Add(b);
            foreach (var g in Objects<GameObject>(so.FindProperty("forSaleWalls"))) foreach (var c in g.GetComponentsInChildren<Collider>(true)) set.Add(c);
            foreach (var g in Objects<GameObject>(so.FindProperty("renovationWalls"))) foreach (var c in g.GetComponentsInChildren<Collider>(true)) set.Add(c);
        }
        foreach (var dock in Object.FindObjectsByType<LoadingDockZone>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            foreach (var c in dock.GetComponentsInChildren<Collider>(true)) set.Add(c);
        foreach (var item in Object.FindObjectsByType<ItemPickupNet>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (item.GetComponent<WallOpening>() != null) continue; // windows and doors are part of the wall
            foreach (var c in item.GetComponentsInChildren<Collider>(true)) set.Add(c);
        }
        foreach (var n in Object.FindObjectsByType<NoPlaceArea>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            foreach (var c in n.GetComponentsInChildren<Collider>(true)) set.Add(c);
        return set;
    }

    // Segment walls (every store's False Walls): areas go to their middle.
    private HashSet<Collider> segmentWalls = new();

    private static HashSet<Collider> SegmentWalls()
    {
        var set = new HashSet<Collider>();
        foreach (var plot in Object.FindObjectsByType<StorePlot>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var segs = new SerializedObject(plot).FindProperty("segments");
            for (int i = 0; segs != null && i < segs.arraySize; i++)
                foreach (var g in Objects<GameObject>(segs.GetArrayElementAtIndex(i).FindPropertyRelative("falseWalls")))
                    foreach (var c in g.GetComponentsInChildren<Collider>(true)) set.Add(c);
        }
        return set;
    }

    private void Fit(List<BoxCollider> boxes)
    {
        Physics.SyncTransforms();
        var ignored = Ignored();
        segmentWalls = SegmentWalls();
        int fitted = 0;
        foreach (var box in boxes.Distinct())
        {
            if (box == null) continue;
            Undo.RecordObject(box, "Fit area to walls");
            if (FitBox(box, ignored, out string why)) { fitted++; EditorUtility.SetDirty(box); }
            else Debug.LogWarning($"[Mall Setup] Couldn't fit '{box.name}': {why}", box);
        }
        Debug.Log($"[Mall Setup] Fitted {fitted} of {boxes.Count} area box(es) to the walls.");
    }

    // The first thing (not ignored, not the box itself) along a ray, or Max.
    private float Cast(Vector3 from, Vector3 dir, float max, BoxCollider self, HashSet<Collider> ignored) =>
        Cast(from, dir, max, self, ignored, out _);

    private float Cast(Vector3 from, Vector3 dir, float max, BoxCollider self, HashSet<Collider> ignored, out Collider hit)
    {
        var hits = Physics.RaycastAll(from, dir, max, wallMask, QueryTriggerInteraction.Ignore);
        float best = max;
        hit = null;
        foreach (var h in hits)
            if (h.collider != self && !ignored.Contains(h.collider) && h.distance < best) { best = h.distance; hit = h.collider; }
        return best;
    }

    // How thick a wall is along a ray that hit it Distance along (from its far side back), 0 if unknown.
    private static float Thickness(Collider wall, Vector3 from, Vector3 dir, float distance)
    {
        const float beyond = 3f;
        Vector3 far = from + dir * (distance + beyond);
        if (!wall.Raycast(new Ray(far, -dir), out RaycastHit back, beyond + 0.01f)) return 0f;
        return Mathf.Clamp(beyond - back.distance, 0f, 2f);
    }

    private bool FitBox(BoxCollider box, HashSet<Collider> ignored, out string why)
    {
        why = null;
        var t = box.transform;
        Vector3 seed = t.TransformPoint(box.center);
        Vector3 right = t.right, forward = t.forward;
        right.y = 0f; forward.y = 0f;
        if (right.sqrMagnitude < 0.01f || forward.sqrMagnitude < 0.01f) { why = "it's tipped over (it must stand upright)"; return false; }
        right.Normalize(); forward.Normalize();

        // Floor and ceiling.
        float down = Cast(seed, Vector3.down, 20f, box, ignored);
        if (down >= 20f) { why = "there's no floor under it"; return false; }
        float floor = seed.y - down;
        Vector3 low = new(seed.x, floor + 0.3f, seed.z);
        float up = Cast(low, Vector3.up, 30f, box, ignored);
        float ceiling = up >= 30f ? floor + defaultHeight : low.y + up;

        // The four sides: first from the middle, then across each side's whole width (and a few heights),
        // keeping the nearest wall, a few times so the sides settle on each other (a first ray out through a
        // doorway is pulled back once the side is checked across its width).
        float h = ceiling - floor;
        float[] heights = { Mathf.Min(0.3f, h * 0.2f), h * 0.45f, Mathf.Max(h - 0.3f, h * 0.8f) };
        float pr = Cast(low, right, maxReach, box, ignored), nr = Cast(low, -right, maxReach, box, ignored);
        float pf = Cast(low, forward, maxReach, box, ignored), nf = Cast(low, -forward, maxReach, box, ignored);
        var walls = new SideWall[4];
        for (int pass = 0; pass < 3; pass++)
        {
            pr = Side(seed, floor, heights, right, forward, nf, pf, pr, box, ignored, out walls[0]);
            nr = Side(seed, floor, heights, -right, forward, nf, pf, nr, box, ignored, out walls[1]);
            pf = Side(seed, floor, heights, forward, right, nr, pr, pf, box, ignored, out walls[2]);
            nf = Side(seed, floor, heights, -forward, right, nr, pr, nf, box, ignored, out walls[3]);
        }
        if (pr >= maxReach || nr >= maxReach || pf >= maxReach || nf >= maxReach)
        {
            why = $"a side found no wall within {maxReach} m (an opening in the room?)";
            return false;
        }

        // Walls: Wall Buffer past them. A segment wall: to its middle (the next area comes to it from the other side).
        pr = Edge(pr, walls[0], right); nr = Edge(nr, walls[1], -right);
        pf = Edge(pf, walls[2], forward); nf = Edge(nf, walls[3], -forward);
        ceiling += ceilingBuffer;
        Vector3 centerWorld = seed + right * (pr - nr) * 0.5f + forward * (pf - nf) * 0.5f;
        centerWorld.y = (floor + ceiling) * 0.5f;
        Vector3 scale = t.lossyScale;
        box.center = t.InverseTransformPoint(centerWorld);
        box.size = new Vector3((pr + nr) / Mathf.Max(1e-4f, Mathf.Abs(scale.x)), (ceiling - floor) / Mathf.Max(1e-4f, Mathf.Abs(scale.y)),
                               (pf + nf) / Mathf.Max(1e-4f, Mathf.Abs(scale.z)));
        return true;
    }

    // The wall a side stopped at, and the ray that found it.
    private struct SideWall
    {
        public Collider wall;
        public Vector3 from;
    }

    // A side's distance, to the middle of a segment wall or Wall Buffer past anything else.
    private float Edge(float distance, SideWall w, Vector3 outward)
    {
        if (w.wall == null || !segmentWalls.Contains(w.wall)) return distance + wallBuffer;
        return distance + Thickness(w.wall, w.from, outward, distance) * 0.5f;
    }

    // How far a side can go: rays along Out from points spread across the side (Across from -AcrossNeg to
    // +AcrossPos, inset a little) at each height; the nearest wall wins.
    private float Side(Vector3 seed, float floor, float[] heights, Vector3 outward, Vector3 across, float acrossNeg,
                       float acrossPos, float current, BoxCollider box, HashSet<Collider> ignored, out SideWall wall)
    {
        float width = acrossNeg + acrossPos;
        int steps = Mathf.Clamp(Mathf.CeilToInt(width / 0.5f), 2, 80);
        float best = maxReach;
        wall = default;
        for (int i = 0; i <= steps; i++)
        {
            float a = Mathf.Lerp(-acrossNeg + 0.1f, acrossPos - 0.1f, i / (float)steps);
            foreach (float y in heights)
            {
                Vector3 from = seed + across * a;
                from.y = floor + y;
                float d = Cast(from, outward, maxReach, box, ignored, out Collider hit);
                if (d < best) { best = d; wall = new SideWall { wall = hit, from = from }; }
            }
        }
        return best;
    }

    private static void AddAreaBox(StorePlot plot)
    {
        var view = SceneView.lastActiveSceneView;
        Vector3 at = view != null ? view.camera.transform.position + view.camera.transform.forward * 5f : plot.transform.position;
        var go = new GameObject("Area");
        Undo.RegisterCreatedObjectUndo(go, "Add area box");
        go.transform.SetParent(plot.transform, true);
        go.transform.position = at;
        go.transform.rotation = plot.transform.rotation;
        var box = go.AddComponent<BoxCollider>();
        box.size = Vector3.one * 0.5f;
        var so = new SerializedObject(plot);
        var areas = so.FindProperty("baseAreas");
        areas.arraySize++;
        areas.GetArrayElementAtIndex(areas.arraySize - 1).objectReferenceValue = box;
        so.ApplyModifiedProperties();
        Selection.activeGameObject = go;
        Debug.Log($"[Mall Setup] Added an area box to '{plot.name}' (Base Areas). Move it into the room and press Fit. " +
                  "For an expansion, drag it into a Segment's Areas instead.", go);
    }

    private static Bounds StoreBounds(StorePlot plot)
    {
        bool any = false;
        var b = new Bounds(plot.transform.position, Vector3.zero);
        foreach (var box in AreasOf(plot))
        {
            var wb = WorldBounds(box);
            if (!any) { b = wb; any = true; } else b.Encapsulate(wb);
        }
        return b;
    }

    private static Bounds WorldBounds(BoxCollider box)
    {
        var t = box.transform;
        Vector3 h = box.size * 0.5f;
        var b = new Bounds(t.TransformPoint(box.center), Vector3.zero);
        for (int i = 0; i < 8; i++)
            b.Encapsulate(t.TransformPoint(box.center + new Vector3((i & 1) == 0 ? -h.x : h.x, (i & 2) == 0 ? -h.y : h.y, (i & 4) == 0 ? -h.z : h.z)));
        return b;
    }

    // CuttableWalls along the store's edge (within half a meter of its areas), not another store's.
    private static void FindStorefrontWalls(StorePlot plot)
    {
        var bounds = StoreBounds(plot);
        bounds.Expand(1f);
        var taken = new HashSet<CuttableWall>();
        foreach (var other in Object.FindObjectsByType<StorePlot>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (other != plot)
                foreach (var w in Objects<CuttableWall>(new SerializedObject(other).FindProperty("storefrontWalls"))) taken.Add(w);
        var found = new List<CuttableWall>();
        foreach (var w in Object.FindObjectsByType<CuttableWall>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (taken.Contains(w) || w.GetComponentInParent<ItemPickupNet>() != null) continue;
            var col = w.GetComponent<Collider>();
            var wb = col != null ? col.bounds : new Bounds(w.transform.position, Vector3.one * 0.1f);
            if (!bounds.Intersects(wb)) continue;
            if (NearAreas(plot, wb, 0.5f)) found.Add(w);
        }
        var so = new SerializedObject(plot);
        var prop = so.FindProperty("storefrontWalls");
        prop.arraySize = found.Count;
        for (int i = 0; i < found.Count; i++) prop.GetArrayElementAtIndex(i).objectReferenceValue = found[i];
        so.ApplyModifiedProperties();
        Debug.Log($"[Mall Setup] '{plot.name}': {found.Count} storefront wall(s) found" +
                  (found.Count == 0 ? " (add CuttableWall to the store's front wall boxes first)." : ": " + string.Join(", ", found.Select(w => w.name))), plot);
    }

    private static bool NearAreas(StorePlot plot, Bounds b, float reach)
    {
        foreach (var box in AreasOf(plot))
        {
            var wb = WorldBounds(box);
            wb.Expand(reach * 2f);
            if (wb.Intersects(b)) return true;
        }
        return false;
    }

    // Renderers lying flat on the store's floor, under its areas.
    private static void FindFloorRenderers(StorePlot plot)
    {
        var found = new List<Renderer>();
        var areas = AreasOf(plot).ToList();
        foreach (var r in Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (r.GetComponentInParent<ItemPickupNet>() != null) continue;
            var b = r.bounds;
            if (b.size.y > 0.6f) continue; // flat
            foreach (var box in areas)
            {
                var wb = WorldBounds(box);
                Vector3 c = b.center;
                if (Mathf.Abs(b.max.y - wb.min.y) > 0.25f) continue;
                if (c.x < wb.min.x || c.x > wb.max.x || c.z < wb.min.z || c.z > wb.max.z) continue;
                found.Add(r);
                break;
            }
        }
        var so = new SerializedObject(plot);
        var prop = so.FindProperty("floorRenderers");
        prop.arraySize = found.Count;
        for (int i = 0; i < found.Count; i++) prop.GetArrayElementAtIndex(i).objectReferenceValue = found[i];
        so.ApplyModifiedProperties();
        Debug.Log($"[Mall Setup] '{plot.name}': {found.Count} floor renderer(s) found.", plot);
    }

    /* ========================= CHECK ========================= */

    private enum Level { Error, Warning, Note }

    private struct Issue
    {
        public Level level;
        public string text;
        public Object target;
    }

    private readonly List<Issue> issues = new();
    private bool showNotes = true;
    private bool checkedOnce;

    private void CheckGUI()
    {
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Check the open scene", GUILayout.Height(26))) RunCheck();
        showNotes = GUILayout.Toggle(showNotes, "Notes", GUILayout.Width(60));
        EditorGUILayout.EndHorizontal();
        if (!checkedOnce) { EditorGUILayout.HelpBox("Looks over the stores, docks and managers for setup mistakes.", MessageType.Info); return; }

        int errors = issues.Count(i => i.level == Level.Error), warnings = issues.Count(i => i.level == Level.Warning);
        EditorGUILayout.LabelField(errors + warnings == 0 ? "No problems found." : $"{errors} error(s), {warnings} warning(s)", EditorStyles.boldLabel);
        scroll = EditorGUILayout.BeginScrollView(scroll);
        foreach (var i in issues)
        {
            if (i.level == Level.Note && !showNotes) continue;
            EditorGUILayout.BeginHorizontal();
            var type = i.level == Level.Error ? MessageType.Error : i.level == Level.Warning ? MessageType.Warning : MessageType.Info;
            EditorGUILayout.HelpBox(i.text, type);
            using (new EditorGUI.DisabledScope(i.target == null))
                if (GUILayout.Button("Select", GUILayout.Width(52), GUILayout.Height(38)))
                {
                    Selection.activeObject = i.target;
                    EditorGUIUtility.PingObject(i.target);
                    if (SceneView.lastActiveSceneView != null) SceneView.lastActiveSceneView.FrameSelected();
                }
            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.EndScrollView();
    }

    private void Add(Level level, string text, Object target) => issues.Add(new Issue { level = level, text = text, target = target });

    private static T[] All<T>() where T : Object => Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);

    private void RunCheck()
    {
        issues.Clear();
        checkedOnce = true;
        shapeFrame = -1;
        Physics.SyncTransforms();
        var plots = All<StorePlot>();
        var docks = All<LoadingDockZone>();

        // Managers.
        void Need<T>(string what) where T : Object
        {
            if (All<T>().Length == 0) Add(Level.Error, $"No {typeof(T).Name} in the scene: {what}.", null);
        }
        Need<MallManager>("stores don't work");
        Need<EconomyManager>("no money or deliveries");
        if (All<Unity.Netcode.NetworkManager>().Length == 0) Add(Level.Error, "No NetworkManager in the scene.", null);
        if (All<CustomerManager>().Length > 0)
        {
            if (All<CustomerSpawnPoint>().Length == 0) Add(Level.Error, "No CustomerSpawnPoint: customers have nowhere to come in.", null);
            if (All<CustomerNavMesh>().Length == 0) Add(Level.Error, "No CustomerNavMesh: customers can't find their way.", null);
        }
        else Add(Level.Warning, "No CustomerManager: no customers.", null);
        if (All<Breakable>().Length > 0 && All<BreakManager>().Length == 0) Add(Level.Error, "There are Breakables but no BreakManager: nothing will ever break.", null);
        if (All<VoteKiosk>().Length > 0 && All<MallVoteManager>().Length == 0) Add(Level.Error, "There are vote kiosks but no MallVoteManager.", null);
        if (All<MallVoteManager>().Length > 0 && All<VoteKiosk>().Length == 0) Add(Level.Warning, "MallVoteManager but no VoteKiosk: nobody can vote.", null);
        if (plots.Length == 0) { Add(Level.Error, "No stores (StorePlot) in the scene.", null); return; }

        int starters = plots.Count(p => p.IsStarterPlot);
        if (starters == 0) Add(Level.Error, "No starter stores: players joining get no store.", null);
        else if (starters < 8) Add(Level.Note, $"{starters} starter store(s): players beyond that are put in a store with someone.", null);

        // Ids and names.
        foreach (var g in plots.GroupBy(p => p.PlotId).Where(g => g.Count() > 1))
            Add(Level.Error, $"{g.Count()} stores share Plot Id {g.Key}: {string.Join(", ", g.Select(p => p.name))}. Each needs its own.", g.First());
        foreach (var g in plots.GroupBy(p => new SerializedObject(p).FindProperty("displayName").stringValue).Where(g => g.Count() > 1))
            Add(Level.Warning, $"{g.Count()} stores are called '{g.Key}': {string.Join(", ", g.Select(p => p.name))}.", g.First());

        var areaBounds = plots.ToDictionary(p => p, p => AreasOf(p).Select(WorldBounds).ToList());
        var allShipment = new List<(StorePlot plot, BoxCollider zone)>();

        foreach (var plot in plots) CheckPlot(plot, plots, areaBounds, docks, allShipment);

        // Docks.
        foreach (var dock in docks)
        {
            var so = new SerializedObject(dock);
            var areas = Objects<BoxCollider>(so.FindProperty("areas")).ToList();
            if (areas.Count == 0) { Add(Level.Error, $"Dock '{dock.name}' has no Areas.", dock); continue; }
            bool used = allShipment.Any(s => areas.Any(a => WorldBounds(a).Intersects(WorldBounds(s.zone))));
            if (!used) Add(Level.Warning, $"Dock '{dock.name}' has no store's shipment area in it: no deliveries come here.", dock);
            foreach (var a in areas) CheckOnFloor(a, $"Dock '{dock.name}' area '{a.name}'");
        }

        // Carts.
        foreach (var cart in All<CartNet>())
            if (new SerializedObject(cart).FindProperty("homeStore").objectReferenceValue == null)
                Add(Level.Warning, $"Cart '{cart.name}' has no Home Store: anyone can push it and Recall doesn't know where it belongs.", cart);

        issues.Sort((a, b) => a.level.CompareTo(b.level));
        Repaint();
    }

    private void CheckPlot(StorePlot plot, StorePlot[] plots, Dictionary<StorePlot, List<Bounds>> areaBounds,
                           LoadingDockZone[] docks, List<(StorePlot, BoxCollider)> allShipment)
    {
        var so = new SerializedObject(plot);
        string n = $"'{plot.name}'";
        if (plot.PlotId < 0) Add(Level.Error, $"{n}: Plot Id is below 0.", plot);

        // Space.
        var baseAreas = so.FindProperty("baseAreas");
        if (baseAreas == null || baseAreas.arraySize == 0) Add(Level.Error, $"{n} has no Base Areas: it has no floor space.", plot);
        for (int i = 0; baseAreas != null && i < baseAreas.arraySize; i++)
            if (baseAreas.GetArrayElementAtIndex(i).objectReferenceValue == null) Add(Level.Error, $"{n}: Base Areas has an empty slot.", plot);
        foreach (var box in AreasOf(plot))
        {
            var wb = WorldBounds(box);
            if (wb.size.x < 1f || wb.size.z < 1f || wb.size.y < 1.5f)
                Add(Level.Warning, $"{n}: area '{box.name}' is tiny ({wb.size.x:0.0} × {wb.size.y:0.0} × {wb.size.z:0.0} m). Not fitted yet? (Fit Stores)", box);
            CheckOnFloor(box, $"{n} area '{box.name}'");
        }
        foreach (var other in plots)
        {
            if (other == plot || other.GetInstanceID() < plot.GetInstanceID()) continue;
            foreach (var a in areaBounds[plot])
                foreach (var b in areaBounds[other])
                    if (Overlap(a, b) > 0.25f)
                    {
                        Add(Level.Error, $"{n} and '{other.name}' overlap: their areas share space (a spot would belong to both).", plot);
                        goto nextOther;
                    }
            nextOther:;
        }

        // Segments.
        var segs = so.FindProperty("segments");
        for (int i = 0; segs != null && i < segs.arraySize; i++)
        {
            var seg = segs.GetArrayElementAtIndex(i);
            string sn = $"{n} segment {i + 1} '{seg.FindPropertyRelative("name").stringValue}'";
            var areas = Objects<BoxCollider>(seg.FindPropertyRelative("areas")).ToList();
            if (seg.FindPropertyRelative("area").objectReferenceValue is BoxCollider old) areas.Add(old);
            if (areas.Count == 0) Add(Level.Error, $"{sn} has no Areas: buying it adds nothing.", plot);
            var walls = Objects<GameObject>(seg.FindPropertyRelative("falseWalls")).ToList();
            if (walls.Count == 0) Add(Level.Warning, $"{sn} has no False Walls: nothing separates it before it's bought.", plot);
            foreach (var w in walls)
                if (w.GetComponentInChildren<Collider>(true) == null) Add(Level.Warning, $"{sn}: false wall '{w.name}' has no collider (players walk through it).", w);
            bool touches = areas.Any(a => areaBounds[plot].Any(b => b != WorldBounds(a) && Gap(WorldBounds(a), b) < 0.5f));
            if (areas.Count > 0 && !touches) Add(Level.Warning, $"{sn} doesn't touch the rest of the store.", areas[0]);
        }

        // Storefront, door.
        var fronts = Objects<CuttableWall>(so.FindProperty("storefrontWalls")).ToList();
        if (fronts.Count == 0)
            Add(Level.Error, $"{n} has no Storefront Walls: no windows or doors can go in, and customers can't see in (Find Storefront Walls).", plot);
        foreach (var w in fronts)
        {
            var col = w.GetComponent<Collider>();
            if (col != null && !NearAreas(plot, col.bounds, 1f)) Add(Level.Warning, $"{n}: storefront wall '{w.name}' is far from the store.", w);
        }
        bool door = false;
        foreach (var o in All<WallOpening>())
        {
            if (o.PieceKind != WallOpening.Kind.Door) continue;
            var r = o.GetComponentInChildren<Renderer>();
            var ob = r != null ? r.bounds : new Bounds(o.transform.position, Vector3.one * 0.5f);
            if (fronts.Any(w => w.GetComponent<Collider>() is Collider c && Gap(c.bounds, ob) < 0.3f)) { door = true; break; }
        }
        foreach (var m in Objects<WallModule>(so.FindProperty("frontWalls"))) if (m.IsEntrance) door = true;
        if (!door) Add(Level.Error, $"{n} has no door in its storefront: customers (and players) can't get in.", plot);

        // Deliveries.
        var zones = Objects<BoxCollider>(so.FindProperty("shipmentZones")).ToList();
        if (zones.Count == 0) Add(Level.Error, $"{n} has no Shipment Zones: its deliveries have nowhere to go.", plot);
        foreach (var z in zones)
        {
            allShipment.Add((plot, z));
            var zb = WorldBounds(z);
            bool inDock = docks.Any(d => Objects<BoxCollider>(new SerializedObject(d).FindProperty("areas")).Any(a => WorldBounds(a).Intersects(zb)));
            bool inStore = areaBounds[plot].Any(b => b.Intersects(zb));
            if (!inDock && !inStore) Add(Level.Warning, $"{n}: shipment zone '{z.name}' is in no dock and not in the store: boxes land where players can't pick them up.", z);
            CheckOnFloor(z, $"{n} shipment zone '{z.name}'");
        }

        // The rest.
        if (!Objects<Renderer>(so.FindProperty("floorRenderers")).Any()) Add(Level.Note, $"{n} has no Floor Renderers: its floor can't be painted (Find Floor Renderers).", plot);
        if (!Objects<GameObject>(so.FindProperty("renovationWalls")).Any()) Add(Level.Warning, $"{n} has no Renovation Walls: nothing covers the storefront in edit mode.", plot);
        var forSale = Objects<GameObject>(so.FindProperty("forSaleWalls")).ToList();
        int price = so.FindProperty("price").intValue;
        if (!plot.IsStarterPlot)
        {
            if (forSale.Count == 0) Add(Level.Error, $"{n} is buy-only but has no For Sale Walls: nobody can buy it.", plot);
            if (price <= 0) Add(Level.Warning, $"{n} is buy-only but its Price is 0.", plot);
        }
        else if (so.FindProperty("startingMoney").intValue <= 0) Add(Level.Warning, $"{n} is a starter store with no Starting Money.", plot);
        foreach (var w in forSale)
            if (w.GetComponentInChildren<Collider>(true) == null) Add(Level.Error, $"{n}: for-sale wall '{w.name}' has no collider: it can't be looked at to buy.", w);
        if (so.FindProperty("spawnPoint").objectReferenceValue is Transform spawn)
        {
            var b = StoreBounds(plot);
            b.Expand(16f);
            if (!b.Contains(spawn.position)) Add(Level.Warning, $"{n}: its Spawn Point is far from the store.", spawn);
        }
        var doorsProp = so.FindProperty("doors");
        for (int i = 0; doorsProp != null && i < doorsProp.arraySize; i++)
            if (doorsProp.GetArrayElementAtIndex(i).objectReferenceValue == null) Add(Level.Warning, $"{n}: Doors has an empty slot.", plot);

        // Things a starter store needs inside.
        if (plot.IsStarterPlot)
        {
            bool In(Component c) => c != null && areaBounds[plot].Any(b => b.Contains(c.transform.position + Vector3.up * 0.1f));
            if (!All<ShopMonitor>().Any(In)) Add(Level.Warning, $"{n} (starter) has no shop monitor in it: its owner can't order.", plot);
            if (!All<CheckoutCounter>().Any(In)) Add(Level.Warning, $"{n} (starter) has no checkout counter in it: customers can't pay.", plot);
            if (!All<CartNet>().Any(c => new SerializedObject(c).FindProperty("homeStore").objectReferenceValue == plot))
                Add(Level.Note, $"{n} (starter) has no cart of its own.", plot);
        }

        // In reach of the customers' navmesh.
        foreach (var nav in All<CustomerNavMesh>())
        {
            var area = new SerializedObject(nav).FindProperty("area").objectReferenceValue as BoxCollider;
            if (area == null) continue;
            var nb = WorldBounds(area);
            if (!nb.Intersects(StoreBounds(plot))) Add(Level.Warning, $"{n} is outside the CustomerNavMesh area: customers can't walk there.", plot);
        }
    }

    private void CheckOnFloor(BoxCollider box, string what)
    {
        var b = WorldBounds(box);
        Vector3 from = new(b.center.x, b.min.y + 0.3f, b.center.z);
        var hits = Physics.RaycastAll(from, Vector3.down, 3f, ~0, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        foreach (var h in hits)
            if (h.collider != box && !(h.collider is BoxCollider bc && IsShapeBox(bc)) && h.distance < best) best = h.distance;
        if (best == float.MaxValue) { Add(Level.Warning, $"{what} has no floor under it.", box); return; }
        float gap = best - 0.3f; // + : the box starts above the floor; - : below it
        if (gap > 0.25f) Add(Level.Warning, $"{what} floats {gap:0.00} m above the floor.", box);
        else if (gap < -0.25f) Add(Level.Warning, $"{what} starts {-gap:0.00} m under the floor.", box);
    }

    private static readonly HashSet<BoxCollider> shapeBoxes = new();
    private static int shapeFrame = -1;

    // Area-like boxes (store areas, segments, shipment zones, docks): shapes, not floors.
    private static bool IsShapeBox(BoxCollider box)
    {
        if (shapeFrame != Time.frameCount)
        {
            shapeFrame = Time.frameCount;
            shapeBoxes.Clear();
            foreach (var p in All<StorePlot>())
            {
                foreach (var b in AreasOf(p)) shapeBoxes.Add(b);
                foreach (var b in Objects<BoxCollider>(new SerializedObject(p).FindProperty("shipmentZones"))) shapeBoxes.Add(b);
            }
            foreach (var d in All<LoadingDockZone>())
                foreach (var b in Objects<BoxCollider>(new SerializedObject(d).FindProperty("areas"))) shapeBoxes.Add(b);
        }
        return shapeBoxes.Contains(box);
    }

    // Shared volume (cubic meters).
    private static float Overlap(Bounds a, Bounds b)
    {
        float x = Mathf.Min(a.max.x, b.max.x) - Mathf.Max(a.min.x, b.min.x);
        float y = Mathf.Min(a.max.y, b.max.y) - Mathf.Max(a.min.y, b.min.y);
        float z = Mathf.Min(a.max.z, b.max.z) - Mathf.Max(a.min.z, b.min.z);
        return x > 0f && y > 0f && z > 0f ? x * y * z : 0f;
    }

    // How far apart (0: touching or overlapping).
    private static float Gap(Bounds a, Bounds b)
    {
        float x = Mathf.Max(0f, Mathf.Max(a.min.x - b.max.x, b.min.x - a.max.x));
        float y = Mathf.Max(0f, Mathf.Max(a.min.y - b.max.y, b.min.y - a.max.y));
        float z = Mathf.Max(0f, Mathf.Max(a.min.z - b.max.z, b.min.z - a.max.z));
        return Mathf.Sqrt(x * x + y * y + z * z);
    }
}
