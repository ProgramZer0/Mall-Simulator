using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// How appealing one store is. Added to every StorePlot automatically (read it with plot.Appeal).
/// Worked out on every machine from things that are already synced, so it costs no network traffic.
/// It's redone at most once a second, and only when stock, prices, events, decor or walls changed.
///
/// What it knows:
/// - Quality (0-1): how well the store's decor covers what its categories want
///   (StoreAppealSettings → Category Needs), weighted by how much of each category it sells.
///   Features lists the decor it has; Hints says what would help ("Clothing: add 2 Mirror").
/// - What's for sale and what of it is SEEN FROM OUTSIDE: a product counts when a passer-by standing
///   in front of one of the store's front windows or doors could see it (nothing solid in between;
///   glass in windows doesn't block). Needs the store's Storefront Walls.
/// - For(personality): how likely that kind of customer is to come in, and how much they'll want what's
///   inside. Customer AI uses this.
///
/// The maths (all tunable, see StoreAppealSettings and the Store Appeal Tuning Guide):
///   item value  = like × price score × rarity × attractiveness × demand × competition
///                 (competition: other open stores selling the same item lower it)
///   draw        = 1 - e^(-total value / saturation), so more on display helps, less and less
///   quality     × (1 + Looks Matter × (Quality - 0.5)): good stores up to +50%, bad ones down to -50%
///   enter       = (Curiosity + (1 - Curiosity) × outside draw) × quality, 0 if the store is closed
///   interest    = inside draw × quality
/// </summary>
public class StoreAppeal : MonoBehaviour
{
    /// <summary>One personality's view of this store.</summary>
    public struct Result
    {
        public float enterChance;  // 0-1: a passer-by of this kind walks in
        public float interest;     // 0-1: once in, how much they want what's here
        public float outsideDraw;  // 0-1: from what's seen from the hallway
        public float insideDraw;   // 0-1: from everything for sale
    }

    // One place something's for sale, with what the store has to say about it.
    private struct Offer
    {
        public ItemDef def;
        public int priceCents;
        public int marketCents;  // for the same amount (a whole crate sold loose is many products)
        public int quantity;
        public bool seenFromOutside;
        public Vector3 position;
        public float competition; // 1 = nobody else sells it
    }

    // One bit of decor (or a window) and where it is.
    private struct DecorPiece
    {
        public string feature;
        public float amount;
        public Vector3 position;
    }

    private static readonly List<StoreAppeal> AllAppeals = new();

    /// <summary>Something changed that affects every store (settings, events): work them all out again.</summary>
    public static void MarkAllDirty()
    {
        foreach (var a in AllAppeals) a.dirty = true;
    }

    private StorePlot plot;
    private readonly List<Offer> offers = new();
    private readonly Dictionary<string, float> features = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> categoryShare = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> hints = new();
    private readonly List<DecorPiece> decor = new();
    private bool dirty = true;
    private float nextAllowed;
    private int seenOpenings = -1, seenWalls = -1, seenDecor = -1;
    private float quality;
    private int forSale, seenOutside;

    public StorePlot Plot => plot;

    /// <summary>0-1: how well the decor suits what the store sells.</summary>
    public float Quality { get { Refresh(); return quality; } }

    /// <summary>Decor features in the store and how much of each.</summary>
    public IReadOnlyDictionary<string, float> Features { get { Refresh(); return features; } }

    /// <summary>What would raise Quality, most useful first.</summary>
    public IReadOnlyList<string> Hints { get { Refresh(); return hints; } }

    /// <summary>How many things are for sale, and how many of them can be seen from the hallway.</summary>
    public int ForSale { get { Refresh(); return forSale; } }
    public int SeenFromOutside { get { Refresh(); return seenOutside; } }

    private void Awake()
    {
        plot = GetComponent<StorePlot>();
        AllAppeals.Add(this);
        StoreListing.StockOrPricesChanged += MarkDirty;
        MallEventManager.Changed += MarkDirty;
        // Opening, closing or changing hands changes who competes with whom: everyone re-checks.
        if (plot != null) plot.Changed += OnPlotChanged;
    }

    private static void OnPlotChanged(StorePlot _)
    {
        Competition.MarkDirty();
        MarkAllDirty();
    }

    private void OnDestroy()
    {
        AllAppeals.Remove(this);
        StoreListing.StockOrPricesChanged -= MarkDirty;
        MallEventManager.Changed -= MarkDirty;
        if (plot != null) plot.Changed -= OnPlotChanged;
    }

    private void MarkDirty() => dirty = true;

    /* ========================= PER PERSONALITY ========================= */

    /// <summary>How this kind of customer sees the store right now.</summary>
    public Result For(CustomerPersonality p)
    {
        Refresh();
        var r = new Result();
        if (p == null) return r;
        var settings = StoreAppealSettings.Instance;
        float outSat = settings != null ? settings.outsideSaturation : 10f;
        float inSat = settings != null ? settings.insideSaturation : 30f;

        float outside = 0f, inside = 0f;
        foreach (var o in offers)
        {
            float v = ValueFor(p, o) * o.quantity;
            inside += v;
            if (o.seenFromOutside) outside += v;
        }
        r.outsideDraw = 1f - Mathf.Exp(-outside / outSat);
        r.insideDraw = 1f - Mathf.Exp(-inside / inSat);

        float looks = Mathf.Max(0f, 1f + p.looksMatter * (quality - 0.5f));
        r.enterChance = plot != null && plot.IsOpen
            ? Mathf.Clamp01((p.curiosity + (1f - p.curiosity) * r.outsideDraw) * looks)
            : 0f;
        r.interest = Mathf.Clamp01(r.insideDraw * looks);
        return r;
    }

    /// <summary>How much one of this product, at this price, is worth to this kind of customer.</summary>
    private static float ValueFor(CustomerPersonality p, Offer o)
    {
        float like = p.LikeOf(o.def);
        if (like <= 0f) return 0f;
        var settings = StoreAppealSettings.Instance;

        // Cheaper than it's worth is a plus for everyone, dearer a minus; how much depends on the customer.
        float ratio = o.marketCents > 0 ? (float)o.priceCents / o.marketCents : 1f;
        float priceScore = settings != null
            ? settings.PriceScore(ratio, p.priceSensitivity)
            : Mathf.Clamp(1f - p.priceSensitivity * (ratio - 1f), 0f, 2f);

        float rarity = settings != null ? settings.RarityMultiplier(o.def.rarity, p.exoticLove) : 1f;
        return like * priceScore * rarity * MarketModifiers.Attractiveness(o.def) * MarketModifiers.Demand(o.def) * o.competition;
    }

    /* ========================= COMPETITION ========================= */

    /// <summary>Which items each open store sells, mall-wide (for "everyone sells it, so it's worth less").</summary>
    private static class Competition
    {
        private static readonly Dictionary<int, int> StoresSelling = new();
        private static bool dirty = true;
        private static float builtAt = -10f;

        public static void MarkDirty() => dirty = true;

        /// <summary>How many open stores other than this one sell this item.</summary>
        public static int Others(int itemId, StorePlot self)
        {
            Build();
            StoresSelling.TryGetValue(itemId, out int n);
            if (self != null && self.IsOpen && self.Listing.Sells(itemId)) n--;
            return Mathf.Max(0, n);
        }

        private static void Build()
        {
            if (!dirty && Time.unscaledTime - builtAt < 2f) return;
            if (Time.unscaledTime - builtAt < 0.25f) return; // many stores asking in the same moment
            dirty = false;
            builtAt = Time.unscaledTime;
            StoresSelling.Clear();
            var mall = MallManager.Instance;
            if (mall == null) return;
            var seen = new HashSet<int>();
            foreach (var plot in mall.Plots)
            {
                if (plot == null || !plot.IsOpen) continue;
                seen.Clear();
                foreach (var e in plot.Listing.Entries)
                    if (seen.Add(e.itemId))
                    {
                        StoresSelling.TryGetValue(e.itemId, out int n);
                        StoresSelling[e.itemId] = n + 1;
                    }
            }
        }

        static Competition() => StoreListing.StockOrPricesChanged += MarkDirty;
    }

    /* ========================= WORKING IT OUT ========================= */

    private void Refresh()
    {
        if (plot == null) return;
        if (seenOpenings != WallOpening.Version || seenWalls != CuttableWall.Version || seenDecor != Decor.Version)
        {
            seenOpenings = WallOpening.Version;
            seenWalls = CuttableWall.Version;
            seenDecor = Decor.Version;
            dirty = true;
        }
        if (!dirty || Time.unscaledTime < nextAllowed) return;
        dirty = false;
        nextAllowed = Time.unscaledTime + 1f;

        BuildOffers();
        BuildFeatures();
        BuildQuality();
    }

    private void BuildOffers()
    {
        offers.Clear();
        forSale = seenOutside = 0;
        var catalog = ItemCatalog.Instance;
        if (catalog == null) return;

        var viewers = new List<Vector3>();
        FindViewers(viewers);

        foreach (var s in plot.Listing.Sources)
        {
            int itemId = s.item != null ? s.item.ItemId : s.container.GetZone(s.zone).itemId;
            if (!catalog.TryGet(itemId, out var def)) continue;

            // A whole crate sold loose holds many products; its market value is theirs together.
            int products = 1;
            if (s.item != null && s.item.TryGetComponent<ItemPickupNet>(out var pickup) && pickup.IsStockCrate)
                products = pickup.Units;

            bool seen = viewers.Count > 0 && IsSeen(s, viewers);
            var settings = StoreAppealSettings.Instance;
            offers.Add(new Offer
            {
                def = def,
                priceCents = s.priceCents,
                marketCents = MarketModifiers.MarketPriceCents(def) * products,
                quantity = s.quantity,
                seenFromOutside = seen,
                position = s.Position,
                competition = settings != null ? settings.CompetitionFactor(Competition.Others(itemId, plot)) : 1f,
            });
            forSale += s.quantity;
            if (seen) seenOutside += s.quantity;
        }
    }

    // Where passers-by look in from: in front of each window and door in the store's front walls.
    private void FindViewers(List<Vector3> into)
    {
        var settings = StoreAppealSettings.Instance;
        float distance = settings != null ? settings.viewerDistance : 2f;
        float height = settings != null ? settings.viewerHeight : 1.6f;
        float floor = plot.FloorHeight;

        foreach (var o in WallOpening.All)
        {
            if (o == null || o.IsEditCopy) continue;
            var host = CuttableWall.HostOf(o);
            if (host == null || host.Plot != plot || !host.IsStorefront) continue;

            var shape = o.Current;
            // The hallway side is the one that isn't in the store.
            Vector3 a = shape.center + shape.forward * distance, b = shape.center - shape.forward * distance;
            Vector3 outside = !plot.Contains(new Vector3(a.x, floor + 0.5f, a.z)) ? a : b;
            outside.y = floor + height;
            into.Add(outside);
        }
    }

    private static readonly RaycastHit[] Hits = new RaycastHit[32];

    // Can someone at one of the viewer spots see this? Glass (window items), the shelf holding it and the
    // product models don't block; walls, other shelves and other things do.
    private bool IsSeen(StoreListing.Source s, List<Vector3> viewers)
    {
        Vector3 from = s.Position + Vector3.up * 0.2f;
        foreach (var eye in viewers)
        {
            Vector3 d = eye - from;
            float length = d.magnitude;
            if (length < 0.01f) return true;
            int n = Physics.RaycastNonAlloc(from, d / length, Hits, length, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            bool blocked = false;
            for (int i = 0; i < n && !blocked; i++)
            {
                var c = Hits[i].collider;
                if (c.GetComponentInParent<WallOpening>() != null) continue;           // glass, door frames
                if (c.GetComponentInParent<StockVisualFollower>() != null) continue;   // product models
                if (s.container != null && c.GetComponentInParent<StockContainer>() == s.container) continue;
                if (s.item != null && c.transform.IsChildOf(s.item.transform)) continue;
                if (c.GetComponentInParent<CharacterController>() != null) continue;   // people
                blocked = true;
            }
            if (!blocked) return true;
        }
        return false;
    }

    private void BuildFeatures()
    {
        features.Clear();
        decor.Clear();
        var settings = StoreAppealSettings.Instance;
        string window = settings != null ? settings.windowFeature : "Window";

        foreach (var d in Decor.All)
        {
            if (d == null || !InThisStore(d.gameObject)) continue;
            foreach (var f in d.features)
                if (f != null && !string.IsNullOrWhiteSpace(f.name))
                    AddDecor(f.name.Trim(), f.amount, d.transform.position);
        }

        // Windows in the store's walls (unless they say something else with their own Decor).
        foreach (var o in WallOpening.All)
        {
            if (o == null || o.IsEditCopy || o.PieceKind != WallOpening.Kind.Window || o.GetComponent<Decor>() != null) continue;
            var host = CuttableWall.HostOf(o);
            if (host != null && host.Plot == plot) AddDecor(window, 1f, o.Current.center);
        }
    }

    private void AddDecor(string feature, float amount, Vector3 position)
    {
        decor.Add(new DecorPiece { feature = feature, amount = amount, position = position });
        AddFeature(feature, amount);
    }

    // How much of a feature is close enough to help a category: within Decor Reach (across the floor) of
    // something for sale in that category.
    private float FeatureNear(string feature, string category, float reach)
    {
        float have = 0f, reach2 = reach * reach;
        foreach (var d in decor)
        {
            if (!string.Equals(d.feature, feature, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var o in offers)
            {
                if (!o.def.HasCategory(category)) continue;
                float dx = d.position.x - o.position.x, dz = d.position.z - o.position.z;
                if (dx * dx + dz * dz > reach2) continue;
                have += d.amount;
                break;
            }
        }
        return have;
    }

    private bool InThisStore(GameObject go)
    {
        if (go.TryGetComponent<WallOpening>(out var o))
        {
            var host = CuttableWall.HostOf(o);
            return host != null && host.Plot == plot;
        }
        return plot.Contains(go.transform.position + Vector3.up * 0.05f);
    }

    private void AddFeature(string name, float amount)
    {
        features.TryGetValue(name, out float have);
        features[name] = have + amount;
    }

    private void BuildQuality()
    {
        hints.Clear();
        var settings = StoreAppealSettings.Instance;
        float neutral = settings != null ? settings.neutralQuality : 0.5f;

        // How much of what's for sale is in each category.
        categoryShare.Clear();
        float total = 0f;
        foreach (var o in offers)
            foreach (var c in o.def.Categories)
            {
                categoryShare.TryGetValue(c, out float n);
                categoryShare[c] = n + o.quantity;
                total += o.quantity;
            }

        if (settings == null || total <= 0f) { quality = neutral; return; }

        float sum = 0f, weights = 0f;
        var gaps = new List<(float worth, string text)>();
        foreach (var kv in categoryShare)
        {
            var needs = settings.NeedsFor(kv.Key);
            if (needs == null || needs.needs.Count == 0) continue;
            float share = kv.Value / total;

            float covered = 0f, importance = 0f;
            foreach (var need in needs.needs)
            {
                if (need == null || string.IsNullOrWhiteSpace(need.feature)) continue;
                float have = FeatureNear(need.feature.Trim(), kv.Key, settings.decorReach);
                float part = Mathf.Clamp01(have / need.wanted);
                covered += part * need.importance;
                importance += need.importance;
                if (part < 1f)
                {
                    int missing = Mathf.CeilToInt(need.wanted - have);
                    gaps.Add((share * need.importance * (1f - part), $"{needs.category}: add {missing} {need.feature} near it"));
                }
            }
            if (importance <= 0f) continue;
            sum += share * covered / importance;
            weights += share;
        }

        quality = weights > 0f ? sum / weights : neutral;
        gaps.Sort((x, y) => y.worth.CompareTo(x.worth));
        for (int i = 0; i < gaps.Count && i < 5; i++) hints.Add(gaps[i].text);
    }

    /// <summary>For testing: right-click the component in the Inspector (Play mode) → Log Appeal.</summary>
    [ContextMenu("Log Appeal")]
    private void LogAppeal()
    {
        dirty = true;
        nextAllowed = 0f;
        Debug.Log(Describe(), this);
    }

    /// <summary>A readable summary (what the store checker shows).</summary>
    public string Describe(bool richText = false)
    {
        Refresh();
        string B(string s) => richText ? $"<b>{s}</b>" : s;
        var sb = new System.Text.StringBuilder();
        sb.Append($"{B(plot != null ? plot.DisplayName : name)}   {(plot != null && plot.IsOpen ? "open" : "closed")}\n");
        sb.Append($"Quality: {quality * 100f:0}%   For sale: {forSale} ({seenOutside} seen from outside)\n");

        if (features.Count > 0)
        {
            sb.Append("Decor: ");
            bool first = true;
            foreach (var kv in features) { sb.Append(first ? "" : ", ").Append($"{kv.Key} {kv.Value:0.#}"); first = false; }
            sb.Append('\n');
        }
        foreach (var h in hints) sb.Append($"Tip: {h}\n");

        var settings = StoreAppealSettings.Instance;
        if (settings != null)
            foreach (var p in settings.personalities)
            {
                if (p == null) continue;
                var r = For(p);
                sb.Append($"{p.Name}: comes in {r.enterChance * 100f:0}%, wants {r.interest * 100f:0}%\n");
            }
        return sb.ToString().TrimEnd();
    }
}
