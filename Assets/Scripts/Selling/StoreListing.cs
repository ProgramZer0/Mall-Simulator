using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One store's "what's for sale": every product it's selling, at what price, and how many. Added to
/// each StorePlot automatically.
///
/// It's worked out on every machine from things that are already synced (shelf zones and sellable
/// items), so it costs no extra network traffic, and customers on any machine can read it. It's
/// rebuilt only when stock or prices change, and only when something asks for it.
///
/// Read it with plot.Listing. Customers will use: Entries, TryGetCheapest, GetSources (where to go)
/// and TotalForSale. How appealing the store is (quality, what's seen from the hallway) is in
/// plot.Appeal (StoreAppeal).
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
        public int priceCents;
        public int quantity;

        public Vector3 Position => item != null ? item.transform.position
                                 : container != null ? container.GetZoneCenter(zone) : Vector3.zero;
    }

    private StorePlot plot;
    private readonly List<Entry> entries = new();
    private readonly List<Source> sources = new();
    private bool dirty = true;
    private int builtFrame = -1;

    /// <summary>
    /// Raised (on this machine) whenever stock or prices change anywhere, so listings may be out of
    /// date; reading a listing rebuilds it.
    /// </summary>
    public static event Action StockOrPricesChanged;

    // Anything that changes stock or prices anywhere marks every store's listing out of date.
    private static readonly List<StoreListing> AllListings = new();
    public static void MarkAllDirty()
    {
        foreach (var l in AllListings) l.dirty = true;
        StockOrPricesChanged?.Invoke();
    }

    public StorePlot Plot => plot;

    /// <summary>Every product for sale, one entry per product per price.</summary>
    public IReadOnlyList<Entry> Entries { get { Refresh(); return entries; } }

    /// <summary>Every place something can be bought, with its price and how many are there.</summary>
    public IReadOnlyList<Source> Sources { get { Refresh(); return sources; } }

    /// <summary>How many things are for sale in total.</summary>
    public int TotalForSale
    {
        get
        {
            Refresh();
            int n = 0;
            foreach (var e in entries) n += e.quantity;
            return n;
        }
    }

    private void Awake()
    {
        plot = GetComponent<StorePlot>();
        AllListings.Add(this);
    }

    private void OnDestroy() => AllListings.Remove(this);

    /// <summary>The cheapest price this product is for sale at here, if it's for sale at all.</summary>
    public bool TryGetCheapest(int itemId, out Entry cheapest)
    {
        Refresh();
        cheapest = default;
        bool found = false;
        foreach (var e in entries)
        {
            if (e.itemId != itemId) continue;
            if (!found || e.priceCents < cheapest.priceCents) { cheapest = e; found = true; }
        }
        return found;
    }

    /// <summary>Is this product for sale here?</summary>
    public bool Sells(int itemId) => TryGetCheapest(itemId, out _);

    /// <summary>The places this product can be bought here, cheapest first.</summary>
    public void GetSources(int itemId, List<Source> into)
    {
        Refresh();
        into.Clear();
        foreach (var s in sources)
            if ((s.item != null ? s.item.ItemId : s.container.GetZone(s.zone).itemId) == itemId) into.Add(s);
        into.Sort((a, b) => a.priceCents.CompareTo(b.priceCents));
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

    private void Refresh()
    {
        if (!dirty || builtFrame == Time.frameCount || plot == null) return;
        dirty = false;
        builtFrame = Time.frameCount;
        entries.Clear();
        sources.Clear();

        foreach (var c in StockContainer.All)
        {
            if (c == null || !c.IsSpawned) continue;
            for (int z = 0; z < c.ZoneCount; z++)
            {
                var stock = c.GetZone(z);
                if (!stock.IsForSale || !plot.Contains(c.GetZoneCenter(z))) continue;
                Add(stock.itemId, stock.price, stock.count, false);
                sources.Add(new Source { container = c, zone = z, priceCents = stock.price, quantity = stock.count });
            }
        }

        foreach (var s in Sellable.All)
        {
            if (s == null || !s.IsSpawned || !s.IsForSale || !plot.Contains(s.transform.position)) continue;
            if (s.TryGetComponent<ItemPickupNet>(out var p) && p.IsOnCart) continue; // riding a cart isn't on display
            Add(s.ItemId, s.Price, 1, true);
            sources.Add(new Source { item = s, priceCents = s.SalePrice, quantity = 1 }); // a part-used crate sells for its share
        }
    }

    private void Add(int itemId, int price, int quantity, bool loose)
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
}
