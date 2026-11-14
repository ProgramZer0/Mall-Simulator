using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Netcode;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Development tool for testing performance with a full mall. Put it on any object in the scene
/// (e.g. the Managers object).
///
/// Keys (host only for Fill and Clear):
/// - Fill (F5): fills stores with rows of shelves and racks, every zone stocked full and priced.
///   All Stores, or just the store the host is standing in. Spread over frames.
/// - Clear (F6): removes everything Fill made.
/// - Stats (F4): the overlay: frame time (average, worst, 1% low), memory and garbage collections,
///   what's spawned, and how long the fake shoppers' work takes.
///
/// Fake shoppers (host): Buys Per Second takes products off the filled shelves, as customers would
/// (each one is a stock change sent to everyone, and updates that store's listing and appeal), and
/// Restock puts one back for each one taken, so it can run for as long as you like. Appeal Asks Per
/// Second asks random stores how they look to random customer types (StoreAppeal.For), the way customers
/// deciding where to go will. Raise both until the frame time moves.
///
/// Leave it out of release builds (or switch it off).
/// </summary>
public class MallBenchmark : MonoBehaviour
{
    [Header("Fill")]
    [Tooltip("Furniture to place (catalog item ids of things with a StockContainer). Empty: every floor-standing one in the catalog.")]
    [SerializeField] private int[] shelfIds = new int[0];
    [Tooltip("Products to stock (catalog item ids of stock crates). Empty: every sellable one in the catalog.")]
    [SerializeField] private int[] productIds = new int[0];
    [Tooltip("Fill every store, or only the one the host is standing in.")]
    [SerializeField] private bool allStores = true;
    [SerializeField, Min(1)] private int maxShelvesPerStore = 200;
    [Tooltip("Gap between shelves (meters), so the rows have aisles.")]
    [SerializeField, Min(0f)] private float aisle = 1f;
    [Tooltip("Spawned per frame, so filling doesn't freeze the game (or flood the network) in one go.")]
    [SerializeField, Min(1)] private int spawnsPerFrame = 20;
    [Tooltip("Price = market price × this.")]
    [SerializeField, Min(0.01f)] private float priceMarkup = 1f;
    [Tooltip("Layers a shelf may not overlap where it's placed.")]
    [SerializeField] private LayerMask blockingMask = ~0;
    [Tooltip("Layers a shelf may stand on.")]
    [SerializeField] private LayerMask supportMask = ~0;

    [Header("Fake Shoppers (host)")]
    [SerializeField, Min(0f)] private float buysPerSecond = 0f;
    [Tooltip("Put one back for each one taken, so shelves stay full.")]
    [SerializeField] private bool restock = true;
    [SerializeField, Min(0f)] private float appealAsksPerSecond = 0f;

    [Header("Keys")]
    [SerializeField] private KeyCode fillKey = KeyCode.F5;
    [SerializeField] private KeyCode clearKey = KeyCode.F6;
    [SerializeField] private KeyCode statsKey = KeyCode.F4;
    [SerializeField] private bool showStats = true;

    private readonly List<NetworkObject> spawned = new();
    private readonly List<StockContainer> filled = new();
    private Coroutine job;
    private string status = "";

    // Stats.
    private readonly float[] frames = new float[600]; // the last 600 frame times (ms)
    private int frameIndex, frameCount;
    private float worstThisSecond, worstLastSecond, secondStarted;
    private double shopperMs, appealMs;        // this second
    private double shopperMsShown, appealMsShown; // last second
    private float buyBudget, askBudget;
    private int buysDone, asksDone, buysShown, asksShown;
    private int gcAtStart;
    private float nextCount;
    private int renderers, stockModels;
    private GUIStyle style;

    private static bool IsHost => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;

    private void Start() => gcAtStart = System.GC.CollectionCount(0);

    private void Update()
    {
        RecordFrame();
        if (Input.GetKeyDown(statsKey)) showStats = !showStats;
        if (UIState.IsMenuOpen) return;
        if (IsHost && Input.GetKeyDown(fillKey)) Fill();
        if (IsHost && Input.GetKeyDown(clearKey)) Clear();
        if (IsHost) FakeShoppers();
    }

    /* ========================= FILL ========================= */

    [ContextMenu("Fill")]
    public void Fill()
    {
        if (!IsHost || job != null) return;
        job = StartCoroutine(FillRoutine());
    }

    [ContextMenu("Clear")]
    public void Clear()
    {
        if (!IsHost || job != null) return;
        job = StartCoroutine(ClearRoutine());
    }

    private IEnumerator FillRoutine()
    {
        var catalog = ItemCatalog.Instance;
        var mall = MallManager.Instance;
        if (catalog == null || mall == null) { status = "No catalog or mall."; job = null; yield break; }

        var shelves = PickShelves(catalog);
        var products = PickProducts(catalog);
        if (shelves.Count == 0 || products.Count == 0)
        {
            status = $"Nothing to place: {shelves.Count} shelf types, {products.Count} product types.";
            job = null;
            yield break;
        }

        var stores = new List<StorePlot>();
        if (allStores) stores.AddRange(mall.Plots);
        else
        {
            var me = NetworkManager.Singleton.LocalClient?.PlayerObject;
            var here = me != null ? mall.GetPlotAt(me.transform.position) : null;
            if (here != null) stores.Add(here);
        }
        if (stores.Count == 0) { status = "No store to fill (stand in one, or tick All Stores)."; job = null; yield break; }

        var sw = Stopwatch.StartNew();
        int placed = 0, thisFrame = 0;
        var areas = new List<Bounds>();
        foreach (var plot in stores)
        {
            if (plot == null) continue;
            areas.Clear();
            plot.GetAreaBounds(areas);
            float floor = plot.FloorHeight;
            int inStore = 0, shelfTurn = 0;

            foreach (var area in areas)
            {
                // Rows across the area, each row one shelf type (taking turns), with an aisle after it.
                float z = area.min.z + 0.2f;
                while (inStore < maxShelvesPerStore)
                {
                    var def = shelves[shelfTurn++ % shelves.Count];
                    var rot = PlacementCheck.Upright(def, Quaternion.identity);
                    if (def.LocalBounds.size == Vector3.zero) break;
                    // Its footprint in the world (the model may be turned to stand up).
                    PlacementCheck.TurnedBox(def.LocalBounds, def.worldNetworkPrefab.transform.localScale, rot, out _, out Vector3 half);
                    float rowZ = z + half.z;
                    if (rowZ > area.max.z - half.z - 0.2f) break;

                    for (float x = area.min.x + half.x + 0.2f; x <= area.max.x - half.x - 0.2f && inStore < maxShelvesPerStore; x += half.x * 2f + 0.1f)
                    {
                        var pos = new Vector3(x, floor, rowZ);
                        pos.y += PlacementCheck.PivotHeightAboveBase(def, rot);
                        if (!plot.ContainsItem(def, pos, rot) || !PlacementCheck.Fits(def, pos, rot, blockingMask, supportMask)) continue;

                        if (Spawn(def, pos, rot, products)) { placed++; inStore++; }
                        if (++thisFrame >= spawnsPerFrame)
                        {
                            thisFrame = 0;
                            status = $"Filling... {placed} placed";
                            yield return null;
                            Physics.SyncTransforms(); // so the next fit checks see what was just placed
                        }
                    }
                    z = rowZ + half.z + aisle;
                }
            }
        }
        status = $"Filled: {placed} shelves in {stores.Count} store(s), {sw.ElapsedMilliseconds} ms.";
        Debug.Log($"[MallBenchmark] {status}", this);
        job = null;
    }

    private bool Spawn(ItemDef def, Vector3 pos, Quaternion rot, List<ItemDef> products)
    {
        var no = Instantiate(def.worldNetworkPrefab, pos, rot);
        if (!no.TryGetComponent<StockContainer>(out var shelf)) { Destroy(no.gameObject); return false; }
        if (no.TryGetComponent<ItemPickupNet>(out var pickup)) pickup.ServerPrepareFrozen();

        // Every zone full of some product that fits it, priced.
        var stock = new StockContainer.ZoneStock[shelf.ZoneCount];
        for (int z = 0; z < stock.Length; z++)
        {
            int start = Random.Range(0, products.Count);
            for (int k = 0; k < products.Count; k++)
            {
                var p = products[(start + k) % products.Count];
                int cap = shelf.CapacityAt(z, p.id, 0);
                if (cap <= 0) continue;
                int price = Mathf.Max(1, Mathf.RoundToInt(MarketModifiers.MarketPriceCents(p) * priceMarkup));
                stock[z] = new StockContainer.ZoneStock
                {
                    itemId = p.id, count = cap, orient = 0, seed = Random.Range(1, int.MaxValue),
                    price = price, pricedItem = p.id,
                };
                break;
            }
        }
        shelf.ServerPrepareStock(stock);
        no.Spawn(true);
        spawned.Add(no);
        filled.Add(shelf);
        return true;
    }

    private List<ItemDef> PickShelves(ItemCatalog catalog)
    {
        var list = new List<ItemDef>();
        if (shelfIds.Length > 0)
        {
            foreach (int id in shelfIds) if (catalog.TryGet(id, out var d) && IsShelf(d)) list.Add(d);
            return list;
        }
        foreach (var d in catalog.Items) if (IsShelf(d)) list.Add(d);
        return list;
    }

    private static bool IsShelf(ItemDef d) =>
        d != null && d.worldNetworkPrefab != null &&
        d.worldNetworkPrefab.TryGetComponent<StockContainer>(out var c) && c.ZoneCount > 0 &&
        !d.worldNetworkPrefab.TryGetComponent<WallOpening>(out _) &&
        PlacementCheck.GetMounting(d) != ItemPickupNet.Mounting.Wall;

    private List<ItemDef> PickProducts(ItemCatalog catalog)
    {
        var list = new List<ItemDef>();
        if (productIds.Length > 0)
        {
            foreach (int id in productIds) if (catalog.TryGet(id, out var d) && IsProduct(d)) list.Add(d);
            return list;
        }
        foreach (var d in catalog.Items) if (IsProduct(d)) list.Add(d);
        return list;
    }

    private static bool IsProduct(ItemDef d) =>
        d != null && d.worldNetworkPrefab != null && !d.IsSize &&
        d.worldNetworkPrefab.TryGetComponent<ItemPickupNet>(out var p) && p.IsStockCrate &&
        (p.ShelfPrefab != null || p.HangPrefab != null) && Sellable.IsSellable(d.id);

    private IEnumerator ClearRoutine()
    {
        int n = 0;
        for (int i = spawned.Count - 1; i >= 0; i--)
        {
            var no = spawned[i];
            if (no != null && no.IsSpawned) no.Despawn(true);
            if (++n % spawnsPerFrame == 0)
            {
                status = $"Clearing... {spawned.Count - i} of {spawned.Count}";
                yield return null;
            }
        }
        spawned.Clear();
        filled.Clear();
        status = $"Cleared {n}.";
        job = null;
    }

    /* ========================= FAKE SHOPPERS ========================= */

    private void FakeShoppers()
    {
        if (filled.Count > 0 && buysPerSecond > 0f)
        {
            var sw = Stopwatch.StartNew();
            buyBudget += buysPerSecond * Time.deltaTime;
            int tries = 0;
            while (buyBudget >= 1f && tries++ < 1000)
            {
                var shelf = filled[Random.Range(0, filled.Count)];
                if (shelf == null || !shelf.IsSpawned || shelf.ZoneCount == 0) { buyBudget -= 1f; continue; }
                int zone = Random.Range(0, shelf.ZoneCount);
                int item = shelf.ServerRemoveOne(zone);
                if (item != 0 && restock) shelf.ServerAddOne(zone, item, shelf.GetZone(zone).orient);
                buyBudget -= 1f;
                buysDone++;
            }
            shopperMs += sw.Elapsed.TotalMilliseconds;
        }

        var settings = StoreAppealSettings.Instance;
        var mall = MallManager.Instance;
        if (appealAsksPerSecond > 0f && settings != null && settings.personalities.Count > 0 && mall != null && mall.Plots.Count > 0)
        {
            var sw = Stopwatch.StartNew();
            askBudget += appealAsksPerSecond * Time.deltaTime;
            int stores = mall.Plots.Count;
            while (askBudget >= 1f)
            {
                askBudget -= 1f;
                int pick = Random.Range(0, stores), i = 0;
                foreach (var plot in mall.Plots)
                {
                    if (i++ != pick) continue;
                    var p = settings.personalities[Random.Range(0, settings.personalities.Count)];
                    if (plot != null && p != null) plot.Appeal.For(p);
                    break;
                }
                asksDone++;
            }
            appealMs += sw.Elapsed.TotalMilliseconds;
        }
    }

    /* ========================= STATS ========================= */

    private void RecordFrame()
    {
        float ms = Time.unscaledDeltaTime * 1000f;
        frames[frameIndex] = ms;
        frameIndex = (frameIndex + 1) % frames.Length;
        frameCount = Mathf.Min(frameCount + 1, frames.Length);
        worstThisSecond = Mathf.Max(worstThisSecond, ms);

        if (Time.unscaledTime - secondStarted >= 1f)
        {
            secondStarted = Time.unscaledTime;
            worstLastSecond = worstThisSecond;
            worstThisSecond = 0f;
            shopperMsShown = shopperMs; appealMsShown = appealMs;
            shopperMs = appealMs = 0;
            buysShown = buysDone; asksShown = asksDone;
            buysDone = asksDone = 0;
        }

        // Counting renderers searches the scene: only now and then, and only while the overlay is up.
        if (showStats && Time.unscaledTime >= nextCount)
        {
            nextCount = Time.unscaledTime + 5f;
            renderers = FindObjectsByType<Renderer>(FindObjectsSortMode.None).Length;
            stockModels = 0;
            foreach (var c in StockContainer.All)
                for (int z = 0; c != null && z < c.ZoneCount; z++) stockModels += c.GetZone(z).count;
        }
    }

    // Average, and the average of the slowest 1% (the stutters).
    private void FrameStats(out float avg, out float low1)
    {
        avg = low1 = 0f;
        if (frameCount == 0) return;
        var sorted = new float[frameCount];
        System.Array.Copy(frames, sorted, frameCount);
        System.Array.Sort(sorted);
        float sum = 0f;
        foreach (var f in sorted) sum += f;
        avg = sum / frameCount;
        int n = Mathf.Max(1, frameCount / 100);
        float slow = 0f;
        for (int i = frameCount - n; i < frameCount; i++) slow += sorted[i];
        low1 = slow / n;
    }

    private void OnGUI()
    {
        if (!showStats) return;
        style ??= new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 13, richText = true };
        FrameStats(out float avg, out float low1);
        var nm = NetworkManager.Singleton;
        int netObjects = nm != null && nm.SpawnManager != null ? nm.SpawnManager.SpawnedObjectsList.Count : 0;

        string text =
            $"<b>Benchmark</b>  {(IsHost ? $"{fillKey}: fill  {clearKey}: clear  " : "")}{statsKey}: hide\n" +
            $"Frame: {avg:0.0} ms avg ({(avg > 0f ? 1000f / avg : 0f):0} fps), worst {worstLastSecond:0.0} ms, 1% low {low1:0.0} ms\n" +
            $"Memory: {System.GC.GetTotalMemory(false) / (1024f * 1024f):0} MB managed, " +
            $"{UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / (1024f * 1024f):0} MB total, " +
            $"{System.GC.CollectionCount(0) - gcAtStart} GCs\n" +
            $"Spawned: {netObjects} network objects, {StockContainer.All.Count} shelves, {stockModels} products, {renderers} renderers\n" +
            (IsHost ? $"Shoppers: {buysShown} buys/s ({shopperMsShown:0.00} ms), {asksShown} appeal asks/s ({appealMsShown:0.00} ms)\n" : "") +
            status;
        GUI.Box(new Rect(10, 10, 560, IsHost ? 118 : 100), text, style);
    }
}
