using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One store's "what's for sale": every product it's selling, at what price, and how many. Added to
/// each StorePlot automatically.
///
/// It's worked out on every machine from things that are already synced (shelf zones and sellable
/// items), so it costs no extra network traffic. It's kept up to date one thing at a time: when a shelf
/// spot or an item for sale changes, only that one entry in only its own store changes (Track). Nothing
/// scans the mall.
///
/// Read it with plot.Listing. Customers use: Sources / GetSources (where to go, by product),
/// TryGetCheapest, Sells and TotalForSale. SourceChanged says which one thing changed (StoreAppeal uses
/// it). How appealing the store is lives in plot.Appeal (StoreAppeal).
/// </summary>
public class StoreListing : MonoBehaviour
{
    /// <summary>One product at one price.</summary>
    public struct Entry
    {
        public int itemId;
        public int priceCents;
        public int quantity;
        public bool loose; // true: sold as a whole item (e.g. a crate, a washing machine); false: off a shelf
    }

    /// <summary>Where some of a product can be bought (so a customer can walk there and take one).</summary>
    public struct Source
    {
        public StockContainer container; // a shelf zone...
        public int zone;
        public Sellable item;            // ...or a loose item
        public int itemId;
        public int priceCents;           // what one costs (a part-used crate sells for its share)
        public int listCents;            // the price tag (the same, except for part-used crates)
        public int quantity;

        public Vector3 Position => item != null ? item.transform.position
                                 : container != null ? container.GetZoneCenter(zone) : Vector3.zero;

        public Key Key => item != null ? new Key(item, -1) : new Key(container, zone);

        internal bool SameAs(Source o) =>
            itemId == o.itemId && priceCents == o.priceCents && listCents == o.listCents && quantity == o.quantity;
    }

    /// <summary>Which thing a source is: a shelf and its zone, or a loose item (zone -1).</summary>
    public readonly struct Key : IEquatable<Key>
    {
        public readonly UnityEngine.Object thing;
        public readonly int zone;

        public Key(UnityEngine.Object thing, int zone)
        {
            this.thing = thing;
            this.zone = zone;
        }

        public bool Equals(Key o) => ReferenceEquals(thing, o.thing) && zone == o.zone;
        public override bool Equals(object obj) => obj is Key o && Equals(o);
        public override int GetHashCode() => (thing is null ? 0 : thing.GetHashCode()) * 31 + zone;
    }

    /// <summary>One source here was added, changed or removed (TryGetSource tells which).</summary>
    public event Action<Key> SourceChanged;

    /// <summary>A store started or stopped selling some product (competition between stores).</summary>
    public static event Action ProductsSoldChanged;

    private StorePlot plot;
    private readonly Dictionary<Key, Source> sources = new();
    private readonly Dictionary<int, int> sourcesPerItem = new(); // item id → how many sources here
    private int totalForSale;

    // Read-side copies, rebuilt only when read after a change.
    private bool viewsDirty = true;
    private readonly List<Entry> entries = new();
    private readonly List<Source> sourceList = new();
    private readonly Dictionary<int, List<Source>> byItem = new();
    private readonly Stack<List<Source>> spareLists = new();

    public StorePlot Plot => plot;

    /// <summary>Every product for sale, one entry per product per price.</summary>
    public IReadOnlyList<Entry> Entries { get { BuildViews(); return entries; } }

    /// <summary>Every place something can be bought, with its price and how many are there.</summary>
    public IReadOnlyList<Source> Sources { get { BuildViews(); return sourceList; } }

    /// <summary>How many things are for sale in total.</summary>
    public int TotalForSale { get { FlushRetrack(); return totalForSale; } }

    /// <summary>Products for sale here (item ids), each once.</summary>
    public Dictionary<int, int>.KeyCollection ProductIds { get { FlushRetrack(); return sourcesPerItem.Keys; } }

    private void Awake() => plot = GetComponent<StorePlot>();

    private void OnDestroy()
    {
        // Whatever was listed here isn't anymore.
        var keys = new List<Key>(sources.Keys);
        foreach (var k in keys) Placed.Remove(k);
    }

    /// <summary>Is this product for sale here?</summary>
    public bool Sells(int itemId)
    {
        FlushRetrack();
        return sourcesPerItem.ContainsKey(itemId);
    }

    /// <summary>The cheapest price this product is for sale at here, if it's for sale at all.</summary>
    public bool TryGetCheapest(int itemId, out Entry cheapest)
    {
        cheapest = default;
        BuildViews();
        if (!byItem.TryGetValue(itemId, out var list) || list.Count == 0) return false;
        var s = list[0]; // cheapest first
        cheapest = new Entry { itemId = itemId, priceCents = s.listCents, quantity = 0, loose = s.item != null };
        foreach (var o in list)
            if (o.listCents == s.listCents && (o.item != null) == cheapest.loose) cheapest.quantity += o.quantity;
        return true;
    }

    /// <summary>The places this product can be bought here, cheapest first.</summary>
    public void GetSources(int itemId, List<Source> into)
    {
        into.Clear();
        BuildViews();
        if (byItem.TryGetValue(itemId, out var list)) into.AddRange(list);
    }

    /// <summary>The places this product can be bought here, cheapest first (don't change the list).</summary>
    public IReadOnlyList<Source> SourcesOf(int itemId)
    {
        BuildViews();
        return byItem.TryGetValue(itemId, out var list) ? list : (IReadOnlyList<Source>)Array.Empty<Source>();
    }

    /// <summary>The source for this thing, if it's for sale here.</summary>
    public bool TryGetSource(Key key, out Source source)
    {
        FlushRetrack();
        return sources.TryGetValue(key, out source);
    }

    /* ========================= TRACKING ========================= */

    // Where each tracked thing is listed right now (which store, as what).
    private static readonly Dictionary<Key, (StoreListing listing, Source source)> Placed = new();
    private static bool retrackAll;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetOnPlay()
    {
        Placed.Clear();
        retrackAll = false;
    }

    /// <summary>A shelf zone changed (stock, price, or where the shelf is): update its one listing entry.</summary>
    public static void Track(StockContainer c, int zone)
    {
        if (c == null) return;
        FlushRetrack();
        var key = new Key(c, zone);
        StoreListing to = null;
        Source want = default;
        if (c.IsSpawned && zone >= 0 && zone < c.ZoneCount)
        {
            var stock = c.GetZone(zone);
            if (stock.IsForSale)
            {
                to = ListingAt(c.GetZoneCenter(zone));
                want = new Source
                {
                    container = c, zone = zone, itemId = stock.itemId,
                    priceCents = stock.price, listCents = stock.price, quantity = stock.count,
                };
            }
        }
        Place(key, to, want);
    }

    /// <summary>Every zone of a shelf.</summary>
    public static void Track(StockContainer c)
    {
        if (c == null) return;
        for (int z = 0; z < c.ZoneCount; z++) Track(c, z);
    }

    /// <summary>A loose item for sale changed (price, crate units, where it is, on a cart...).</summary>
    public static void Track(Sellable s)
    {
        if (s == null) return;
        FlushRetrack();
        var key = new Key(s, -1);
        StoreListing to = null;
        Source want = default;
        if (s.IsSpawned && s.IsForSale &&
            !(s.TryGetComponent<ItemPickupNet>(out var p) && (p.IsOnCart || !p.IsFrozen))) // riding a cart or still falling isn't on display
        {
            to = ListingAt(s.transform.position);
            want = new Source { item = s, zone = -1, itemId = s.ItemId, priceCents = s.SalePrice, listCents = s.Price, quantity = 1 };
        }
        Place(key, to, want);
    }

    /// <summary>Everything on this object that can be for sale (a shelf's zones, and the thing itself).</summary>
    public static void Track(GameObject go)
    {
        if (go == null) return;
        if (go.TryGetComponent<StockContainer>(out var c)) Track(c);
        if (go.TryGetComponent<Sellable>(out var s)) Track(s);
    }

    /// <summary>Gone (despawned): take it off its store's listing.</summary>
    public static void Untrack(StockContainer c)
    {
        if (c == null) return;
        for (int z = 0; z < c.ZoneCount; z++) Place(new Key(c, z), null, default);
    }

    public static void Untrack(Sellable s)
    {
        if (s != null) Place(new Key(s, -1), null, default);
    }

    /// <summary>
    /// Store spaces changed (a segment bought, a store appeared): work out again which store everything
    /// is in. Done once, the next time anything is tracked or read.
    /// </summary>
    public static void RetrackAll() => retrackAll = true;

    private static void FlushRetrack()
    {
        if (!retrackAll) return;
        retrackAll = false;
        foreach (var c in StockContainer.All) Track(c);
        foreach (var s in Sellable.All) Track(s);
    }

    private static StoreListing ListingAt(Vector3 point)
    {
        var mall = MallManager.Instance;
        var plot = mall != null ? mall.GetPlotAt(point) : null;
        return plot != null ? plot.Listing : null;
    }

    private static void Place(Key key, StoreListing to, Source want)
    {
        bool had = Placed.TryGetValue(key, out var was);
        if (had && was.listing == to && was.source.SameAs(want)) return;
        if (!had && to == null) return;

        if (had && was.listing != null) was.listing.Remove(key, was.source, notify: was.listing != to);
        if (to != null)
        {
            Placed[key] = (to, want);
            to.Add(key, want);
        }
        else Placed.Remove(key);
    }

    private void Add(Key key, Source s)
    {
        sources[key] = s;
        totalForSale += s.quantity;
        sourcesPerItem.TryGetValue(s.itemId, out int n);
        sourcesPerItem[s.itemId] = n + 1;
        viewsDirty = true;
        if (n == 0) ProductsSoldChanged?.Invoke();
        SourceChanged?.Invoke(key);
    }

    private void Remove(Key key, Source s, bool notify)
    {
        if (!sources.Remove(key)) return;
        totalForSale -= s.quantity;
        if (sourcesPerItem.TryGetValue(s.itemId, out int n))
        {
            if (n <= 1)
            {
                sourcesPerItem.Remove(s.itemId);
                ProductsSoldChanged?.Invoke();
            }
            else sourcesPerItem[s.itemId] = n - 1;
        }
        viewsDirty = true;
        if (notify) SourceChanged?.Invoke(key); // (moving to another place here notifies once, from Add)
    }

    // The lists readers see, rebuilt from this store's own sources only when read after a change.
    private void BuildViews()
    {
        FlushRetrack();
        if (!viewsDirty) return;
        viewsDirty = false;

        sourceList.Clear();
        entries.Clear();
        foreach (var list in byItem.Values) { list.Clear(); spareLists.Push(list); }
        byItem.Clear();

        foreach (var s in sources.Values)
        {
            sourceList.Add(s);
            if (!byItem.TryGetValue(s.itemId, out var list))
            {
                list = spareLists.Count > 0 ? spareLists.Pop() : new List<Source>();
                byItem[s.itemId] = list;
            }
            list.Add(s);
            AddEntry(s.itemId, s.listCents, s.quantity, s.item != null);
        }
        foreach (var list in byItem.Values)
            if (list.Count > 1) list.Sort((a, b) => a.priceCents.CompareTo(b.priceCents));
    }

    private void AddEntry(int itemId, int price, int quantity, bool loose)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.itemId != itemId || e.priceCents != price || e.loose != loose) continue;
            e.quantity += quantity;
            entries[i] = e;
            return;
        }
        entries.Add(new Entry { itemId = itemId, priceCents = price, quantity = quantity, loose = loose });
    }

    /// <summary>For testing: right-click the component in the Inspector (Play mode) → Log Listing.</summary>
    [ContextMenu("Log Listing")]
    private void LogListing()
    {
        var sb = new System.Text.StringBuilder($"[StoreListing] '{(plot != null ? plot.DisplayName : name)}': {TotalForSale} for sale");
        foreach (var e in Entries)
        {
            string itemName = ItemCatalog.Instance != null && ItemCatalog.Instance.TryGet(e.itemId, out var def) ? def.displayName : $"Item {e.itemId}";
            sb.Append($"\n  {itemName}{(e.loose ? " (whole)" : "")} x{e.quantity} at {Sellable.FormatPrice(e.priceCents)}");
        }
        Debug.Log(sb.ToString(), this);
    }
}
