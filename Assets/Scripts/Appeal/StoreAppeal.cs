using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// How appealing one store is. Added to every StorePlot automatically (read it with plot.Appeal).
/// Worked out on every machine from things that are already synced, so it costs no network traffic.
/// It's updated at most once a second, and only for what changed: a shelf spot or item changing updates
/// just that one offer (no visibility checks for count or price changes); walls, windows and decor
/// moving redo everything.
///
/// What it knows:
/// - Quality (0-1): how well the store's decor covers what its categories want
///   (StoreAppealSettings → Category Needs), weighted by how much of each category it sells, plus or
///   minus how well what's for sale is placed next to things that go with it (Category Pairs).
///   Features lists the decor it has; Hints says what would help ("Clothing: add 2 Mirror").
/// - What's for sale and what of it is SEEN FROM OUTSIDE: a product counts when a passer-by standing
///   in front of one of the store's front windows or doors could see it (nothing solid in between;
///   glass in windows doesn't block). Needs the store's Storefront Walls.
/// - For(personality): how likely that kind of customer is to come in, and how much they'll want what's
///   inside. Customer AI uses this.
///
/// The maths (all tunable, see StoreAppealSettings and the Store Appeal Tuning Guide):
///   item value  = like × price score × rarity × attractiveness × demand × competition × color
///                 (color: how its color suits the item and the customer, see ColorRules)
///                 (competition: other open stores selling the same item lower it)
///   draw        = 1 - e^(-total value / saturation), so more on display helps, less and less
///   quality     × (1 + Looks Matter × (Quality - 0.5)): good stores up to +50%, bad ones down to -50%
///   enter       = (Curiosity + (1 - Curiosity) × outside draw) × quality, 0 if the store is closed
///   interest    = inside draw × quality
/// </summary>
/// <summary>
/// Where things stand while a store is being edited (see StoreAppeal.CreatePreview): the edit copies in
/// place of the real items.
/// </summary>
public interface IAppealLayout
{
    /// <summary>
    /// The edit copy standing in for this real thing (a shelf, a loose item, decor, a window): true and the
    /// copy; true and null: it's been taken out; false: it isn't being edited (use it where it is).
    /// </summary>
    bool TryGetCopy(Component real, out Transform copy);

    /// <summary>Goes up whenever something was moved, added or taken out (dropped, not while dragged).</summary>
    int Version { get; }

    /// <summary>The real things moved, added or taken out since the last call (and if windows/walls changed).</summary>
    void TakeChanges(List<Component> moved, out bool openings);

    /// <summary>Something's being dragged or placed: hold off working anything out until it's let go.</summary>
    bool Busy { get; }

    /// <summary>Decor only on copies (things bought this edit, which have no real object yet).</summary>
    void GetExtraDecor(List<(string feature, float amount, Vector3 position)> into);
}

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
        public int products;     // products in one (a whole crate: its units)
        public bool seenFromOutside;
        public Vector3 position;
        public float competition; // 1 = nobody else sells it
        public byte paint;        // its main color (ColorRules taste)
    }

    // One bit of decor (or a window) and where it is.
    private struct DecorPiece
    {
        public string feature;
        public float amount;
        public Vector3 position;
    }

    private static readonly List<StoreAppeal> AllAppeals = new();

    // Edit preview (CreatePreview): the store as it's being edited, not as it is.
    private IAppealLayout layout;
    private int seenLayout = -1;
    private readonly List<Component> movedBuffer = new();
    private readonly List<(string feature, float amount, Vector3 position)> extraDecor = new();

    /// <summary>A preview of the store while it's being edited (see CreatePreview).</summary>
    public bool IsPreview => layout != null;

    /// <summary>
    /// A separate, local appeal for a store being edited, worked out from the edit copies (the layout) as
    /// they're moved, so the editor sees what a change does before saving. Destroy its GameObject when done.
    /// Customers never use it.
    /// </summary>
    public static StoreAppeal CreatePreview(StorePlot plot, IAppealLayout layout)
    {
        var go = new GameObject($"Appeal Preview ({(plot != null ? plot.DisplayName : "?")})") { hideFlags = HideFlags.HideAndDontSave };
        var a = go.AddComponent<StoreAppeal>();
        a.plot = plot;
        a.listing = plot != null ? plot.GetComponent<StoreListing>() : null;
        a.layout = layout;
        if (a.listing != null) a.listing.SourceChanged += a.OnSourceChanged;
        return a;
    }

    /// <summary>A shelf zone or loose item for sale, for drawing (see GetOffers).</summary>
    public struct OfferView
    {
        public StoreListing.Key key;  // key.thing: the StockContainer (with key.zone) or the Sellable
        public ItemDef def;
        public Vector3 position;      // where it is (in a preview: where its copy is)
        public bool seenFromOutside;
    }

    /// <summary>What's for sale and where, as worked out last.</summary>
    public void GetOffers(List<OfferView> into)
    {
        Refresh();
        into.Clear();
        foreach (var kv in offers)
            into.Add(new OfferView { key = kv.Key, def = kv.Value.def, position = kv.Value.position, seenFromOutside = kv.Value.seenFromOutside });
    }

    /// <summary>The decor counted, and where (windows included).</summary>
    public void GetDecor(List<(string feature, float amount, Vector3 position)> into)
    {
        Refresh();
        into.Clear();
        foreach (var d in decor) into.Add((d.feature, d.amount, d.position));
    }

    /// <summary>Delivery boxes counted in the store (for the "Shipment" pair category), as worked out last.</summary>
    public IReadOnlyList<Vector3> Boxes { get { Refresh(); return boxes; } }

    /// <summary>Where passers-by look in from (in front of the store's windows and doors).</summary>
    public IReadOnlyList<Vector3> Viewers { get { Refresh(); return viewers; } }

    /// <summary>Goes up each time it's worked out again (overlays redraw then).</summary>
    public int Revision { get; private set; }

    /// <summary>
    /// Something changed that affects every store's values (settings, events, a store opening or
    /// closing): work out prices and competition again. Cheap: no visibility checks.
    /// </summary>
    public static void MarkAllDirty()
    {
        foreach (var a in AllAppeals) a.revalue = true;
    }

    private StorePlot plot;
    private StoreListing listing;
    private readonly Dictionary<StoreListing.Key, Offer> offers = new();
    private readonly Dictionary<string, float> features = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> categoryShare = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> hints = new();
    private readonly List<DecorPiece> decor = new();
    private float quality;
    private int forSale, seenOutside;

    // What needs working out again (see Refresh). Only what changed is redone.
    private bool rebuildAll = true;  // walls, windows or decor changed: every offer, seen from outside again
    private bool revalue;            // events, settings or competition changed: values again, no raycasts
    private readonly HashSet<StoreListing.Key> pending = new(); // these sources changed
    private float nextAllowed;
    private int seenOpenings = -1, seenWalls = -1, seenDecor = -1, seenCompetition = -1;

    // Kept between refreshes.
    private readonly List<Vector3> viewers = new();
    private readonly Dictionary<StoreListing.Key, (Vector3 at, bool seen)> seenCache = new();
    private readonly Dictionary<CustomerPersonality, (float outside, float inside)> sums = new();
    private readonly Dictionary<(string feature, string category), float> nearCache = new();
    private readonly Dictionary<string, float> pairCache = new(StringComparer.OrdinalIgnoreCase); // category → pair score
    private readonly Dictionary<(string category, string other, int wanted), float> pairPartCache = new();
    private readonly List<Vector3> boxes = new(); // delivery boxes in the store (the "Shipment" pair category)
    private int seenItemLayout = -1;
    private bool boxesDirty = true;
    private readonly List<StoreListing.Key> keyBuffer = new();
    private readonly List<StoreListing.Source> sourceBuffer = new();

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
        listing = GetComponent<StoreListing>();
        AllAppeals.Add(this);
        if (listing != null) listing.SourceChanged += OnSourceChanged;
        MallEventManager.Changed += OnEventsChanged;
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
        if (listing != null) listing.SourceChanged -= OnSourceChanged;
        MallEventManager.Changed -= OnEventsChanged;
        if (plot != null) plot.Changed -= OnPlotChanged;
    }

    private void OnSourceChanged(StoreListing.Key key) => pending.Add(key);
    private void OnEventsChanged() => revalue = true;

    /* ========================= PER PERSONALITY ========================= */

    /// <summary>How this kind of customer sees the store right now.</summary>
    public Result For(CustomerPersonality p) => For(p, false);

    /// <summary>Even If Closed: as if it were open (an edit preview: the store's shut while it's edited).</summary>
    public Result For(CustomerPersonality p, bool evenIfClosed)
    {
        Refresh();
        var r = new Result();
        if (p == null) return r;
        var settings = StoreAppealSettings.Instance;
        float outSat = settings != null ? settings.outsideSaturation : 10f;
        float inSat = settings != null ? settings.insideSaturation : 30f;

        // The sum over everything for sale is kept per personality until something changes, so many
        // customers asking costs one pass per change, not one per customer.
        if (!sums.TryGetValue(p, out var sum))
        {
            foreach (var o in offers.Values)
            {
                float v = ValueFor(p, o) * o.quantity;
                sum.inside += v;
                if (o.seenFromOutside) sum.outside += v;
            }
            sums[p] = sum;
        }
        r.outsideDraw = 1f - Mathf.Exp(-sum.outside / outSat);
        r.insideDraw = 1f - Mathf.Exp(-sum.inside / inSat);

        // Store perks: quality points, more walk-ins, more interest.
        float q = Mathf.Clamp01(quality + ProgressionManager.StoreStat(plot, PerkStat.Quality));
        float looks = Mathf.Max(0f, 1f + p.looksMatter * (q - 0.5f));
        float walkIns = 1f + ProgressionManager.StoreStat(plot, PerkStat.WalkIns);
        float interest = 1f + ProgressionManager.StoreStat(plot, PerkStat.Interest);
        r.enterChance = plot != null && (plot.IsOpen || evenIfClosed)
            ? Mathf.Clamp01((p.curiosity + (1f - p.curiosity) * r.outsideDraw) * looks * walkIns)
            : 0f;
        r.interest = Mathf.Clamp01(r.insideDraw * looks * interest);
        return r;
    }

    /// <summary>
    /// The everyday customer: every kind of shopper (not bystanders), averaged by how often they come (Spawn
    /// Weight). Even If Closed: as if it were open.
    /// </summary>
    public Result ForAverage(bool evenIfClosed = false)
    {
        var settings = StoreAppealSettings.Instance;
        var r = new Result();
        if (settings == null) return r;
        float weights = 0f;
        foreach (var p in settings.personalities)
        {
            if (p == null || p.bystander || p.spawnWeight <= 0f) continue;
            var one = For(p, evenIfClosed);
            r.enterChance += one.enterChance * p.spawnWeight;
            r.interest += one.interest * p.spawnWeight;
            r.outsideDraw += one.outsideDraw * p.spawnWeight;
            r.insideDraw += one.insideDraw * p.spawnWeight;
            weights += p.spawnWeight;
        }
        if (weights <= 0f) return r;
        r.enterChance /= weights;
        r.interest /= weights;
        r.outsideDraw /= weights;
        r.insideDraw /= weights;
        return r;
    }

    /// <summary>
    /// How much this kind of customer wants one of what's at this source (0 = not at all). For a
    /// customer browsing the store: pick what's worth the most to them.
    /// </summary>
    public float WorthTo(CustomerPersonality p, StoreListing.Source s)
    {
        Refresh();
        return p != null && offers.TryGetValue(s.Key, out var o) ? ValueFor(p, o) : 0f;
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
        return like * priceScore * rarity * MarketModifiers.Attractiveness(o.def) * MarketModifiers.Demand(o.def) * o.competition *
               ColorRules.Factor(o.def, p, o.paint);
    }

    /* ========================= COMPETITION ========================= */

    /// <summary>Which items each open store sells, mall-wide (for "everyone sells it, so it's worth less").</summary>
    private static class Competition
    {
        private static readonly Dictionary<int, int> StoresSelling = new();
        private static bool dirty = true;

        /// <summary>Goes up each time the table changes (stores compare it to know when to revalue).</summary>
        public static int Version { get; private set; }

        public static void MarkDirty() => dirty = true;

        /// <summary>How many open stores other than this one sell this item.</summary>
        public static int Others(int itemId, StorePlot self)
        {
            Build();
            StoresSelling.TryGetValue(itemId, out int n);
            if (self != null && self.IsOpen && self.Listing.Sells(itemId)) n--;
            return Mathf.Max(0, n);
        }

        // Only rebuilt when a store starts or stops selling something, or opens or closes; counts
        // going up and down don't matter here.
        public static void Build()
        {
            if (!dirty) return;
            dirty = false;
            Version++;
            StoresSelling.Clear();
            var mall = MallManager.Instance;
            if (mall == null) return;
            foreach (var plot in mall.Plots)
            {
                if (plot == null || !plot.IsOpen) continue;
                foreach (int itemId in plot.Listing.ProductIds)
                {
                    StoresSelling.TryGetValue(itemId, out int n);
                    StoresSelling[itemId] = n + 1;
                }
            }
        }

        static Competition() => StoreListing.ProductsSoldChanged += MarkDirty;
    }

    /* ========================= WORKING IT OUT ========================= */

    // At most once a second, and only what changed:
    // - walls, windows or decor moved: everything again, including what's seen from outside
    // - a shelf spot or item changed: just that offer (a new spot is checked for being seen; counts and
    //   prices aren't)
    // - events, settings, competition: values again, no visibility checks
    private void Refresh()
    {
        if (plot == null || listing == null) return;
        if (seenOpenings != WallOpening.Version || seenWalls != CuttableWall.Version || seenDecor != Decor.Version)
        {
            // Only changes in or around this store (its hallway too) redo it.
            var near = StoreArea();
            if (WallOpening.Changes.ChangedNear(seenOpenings, near) || CuttableWall.Changes.ChangedNear(seenWalls, near) ||
                Decor.Changes.ChangedNear(seenDecor, near))
                rebuildAll = true;
            seenOpenings = WallOpening.Version;
            seenWalls = CuttableWall.Version;
            seenDecor = Decor.Version;
        }
        Competition.Build();
        if (seenCompetition != Competition.Version)
        {
            seenCompetition = Competition.Version;
            revalue = true;
        }
        // Something placed or picked up somewhere: delivery boxes may have come or gone (only matters with pairs).
        if (seenItemLayout != ItemPickupNet.LayoutVersion)
        {
            seenItemLayout = ItemPickupNet.LayoutVersion;
            var st = StoreAppealSettings.Instance;
            if (st != null && st.categoryPairs.Count > 0) boxesDirty = true;
        }
        bool decorMoved = false;
        if (layout != null)
        {
            if (layout.Busy) return; // nothing's worked out while something's being dragged
            if (seenLayout != layout.Version)
            {
                seenLayout = layout.Version;
                layout.TakeChanges(movedBuffer, out bool openingsChanged);
                if (openingsChanged) rebuildAll = true;
                else if (movedBuffer.Count > 0) MarkMoved(movedBuffer, ref decorMoved);
            }
        }
        if (!rebuildAll && !revalue && pending.Count == 0 && !boxesDirty && !decorMoved) return;
        if (Time.unscaledTime < nextAllowed) return;
        nextAllowed = Time.unscaledTime + (layout != null ? 0.25f : 1f);
        Revision++;

        bool layoutChanged = false; // offers added, removed or moved (decor-near-it needs redoing)
        bool settingsChanged = revalue || rebuildAll;
        if (rebuildAll)
        {
            rebuildAll = false;
            revalue = false;
            pending.Clear();
            offers.Clear();
            seenCache.Clear();
            viewers.Clear();
            FindViewers(viewers);
            sourceBuffer.Clear();
            sourceBuffer.AddRange(listing.Sources);
            foreach (var s in sourceBuffer) SetOffer(s.Key, s);
            BuildFeatures();
            layoutChanged = true;
        }
        else
        {
            keyBuffer.Clear();
            keyBuffer.AddRange(pending); // (a copy: working one out can't change the set mid-loop)
            pending.Clear();
            foreach (var key in keyBuffer)
            {
                if (listing.TryGetSource(key, out var s)) layoutChanged |= SetOffer(key, s);
                else layoutChanged |= offers.Remove(key);
            }

            if (revalue)
            {
                revalue = false;
                keyBuffer.Clear();
                keyBuffer.AddRange(offers.Keys);
                foreach (var key in keyBuffer)
                {
                    var o = offers[key];
                    o.marketCents = MarketModifiers.MarketPriceCents(o.def) * o.products;
                    o.competition = CompetitionFor(o.def.id);
                    offers[key] = o;
                }
            }
        }

        forSale = seenOutside = 0;
        foreach (var o in offers.Values)
        {
            forSale += o.quantity;
            if (o.seenFromOutside) seenOutside += o.quantity;
        }
        sums.Clear();
        if (decorMoved && !layoutChanged) { BuildFeatures(); layoutChanged = true; }
        if (layoutChanged || settingsChanged) { nearCache.Clear(); wallCache.Clear(); }
        if (boxesDirty)
        {
            boxesDirty = false;
            GatherBoxes();
            layoutChanged = true;
        }
        if (layoutChanged || settingsChanged) { pairCache.Clear(); pairPartCache.Clear(); }
        BuildQuality();
    }

    // Preview: things that moved. Their offers are worked out again; decor among them redoes the decor.
    private void MarkMoved(List<Component> moved, ref bool decorMoved)
    {
        foreach (var c in moved)
        {
            if (c == null) continue;
            if (c.GetComponentInChildren<Decor>(true) != null) decorMoved = true;
            if (c.TryGetComponent<WallOpening>(out _)) { rebuildAll = true; return; }
        }
        foreach (var s in listing.Sources)
        {
            Component thing = s.item != null ? s.item : s.container;
            if (thing == null) continue;
            foreach (var c in moved)
                if (c != null && (c == thing || c.gameObject == thing.gameObject)) { pending.Add(s.Key); break; }
        }
    }

    // Where a source is: in a preview, where its edit copy is (Skip: the copy, which doesn't block its own
    // view). False: taken out in the edit.
    private bool Locate(StoreListing.Source s, out Vector3 at, out Transform skip)
    {
        at = s.Position;
        skip = null;
        if (layout == null) return true;
        Component thing = s.item != null ? s.item : s.container;
        if (thing == null || !layout.TryGetCopy(thing, out var copy)) return true;
        if (copy == null) return false;
        at = copy.TransformPoint(thing.transform.InverseTransformPoint(at));
        skip = copy;
        return true;
    }

    /// <summary>In a preview, where a real thing's edit copy has this point (else the point itself).</summary>
    public Vector3 MapPoint(Component real, Vector3 point)
    {
        if (layout == null || real == null || !layout.TryGetCopy(real, out var copy) || copy == null) return point;
        return copy.TransformPoint(real.transform.InverseTransformPoint(point));
    }

    /// <summary>In a preview, a real thing's rotation as its edit copy has it.</summary>
    public Quaternion MapRotation(Component real, Quaternion rotation)
    {
        if (layout == null || real == null || !layout.TryGetCopy(real, out var copy) || copy == null) return rotation;
        return copy.rotation * Quaternion.Inverse(real.transform.rotation) * rotation;
    }

    private readonly List<Bounds> areaBuffer = new();

    // The store's space, grown to take in the hallway in front of it (where passers-by look in from).
    private Bounds StoreArea()
    {
        areaBuffer.Clear();
        plot.GetAreaBounds(areaBuffer);
        if (areaBuffer.Count == 0) return new Bounds(plot.transform.position, Vector3.one * 1000f);
        var b = areaBuffer[0];
        for (int i = 1; i < areaBuffer.Count; i++) b.Encapsulate(areaBuffer[i]);
        var settings = StoreAppealSettings.Instance;
        b.Expand(2f * ((settings != null ? settings.viewerDistance : 2f) + 1f));
        return b;
    }

    private float CompetitionFor(int itemId)
    {
        var settings = StoreAppealSettings.Instance;
        return settings != null ? settings.CompetitionFactor(Competition.Others(itemId, plot)) : 1f;
    }

    // Add or update one offer. True if it's new, gone or moved, or now a different product.
    private bool SetOffer(StoreListing.Key key, StoreListing.Source s)
    {
        var catalog = ItemCatalog.Instance;
        if (catalog == null || !catalog.TryGet(s.itemId, out var def)) return offers.Remove(key);

        // A whole crate sold loose holds many products; its market value is theirs together.
        int products = 1;
        if (s.item != null && s.item.TryGetComponent<ItemPickupNet>(out var pickup) && pickup.IsStockCrate)
            products = pickup.Units;

        // Seen from outside: only checked for something new or moved (not when a count or price changes).
        if (!Locate(s, out Vector3 at, out Transform skip)) return offers.Remove(key); // taken out (edit preview)
        if (!seenCache.TryGetValue(key, out var seen) || (seen.at - at).sqrMagnitude > 0.0025f)
        {
            seen = (at, viewers.Count > 0 && IsSeen(s, at, skip, viewers));
            seenCache[key] = seen;
        }

        bool had = offers.TryGetValue(key, out var before);
        offers[key] = new Offer
        {
            def = def,
            products = products,
            priceCents = s.priceCents,
            marketCents = MarketModifiers.MarketPriceCents(def) * products,
            quantity = s.quantity,
            seenFromOutside = seen.seen,
            position = at,
            competition = CompetitionFor(s.itemId),
            paint = s.paint,
        };
        return !had || before.def != def || (before.position - at).sqrMagnitude > 0.0025f;
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
            if (!Counts(o)) continue;
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
    private bool IsSeen(StoreListing.Source s, Vector3 at, Transform skip, List<Vector3> viewers) =>
        Visible(at + Vector3.up * 0.2f, s.container, s.item != null ? s.item.transform : null, skip, viewers);

    /// <summary>
    /// Could a passer-by see this point (from in front of any of the store's windows and doors)? For an
    /// edit preview's "seen from the hallway" shading.
    /// </summary>
    public bool IsPointSeen(Vector3 point)
    {
        Refresh();
        return viewers.Count > 0 && Visible(point, null, null, null, viewers);
    }

    // A clear line from the point to one of the viewer spots. Glass (window items), the shelf holding it,
    // the thing itself and product models don't block; walls, other shelves and other things do. In an
    // edit preview the renovation wall doesn't either (it's only up while editing).
    private bool Visible(Vector3 from, StockContainer container, Transform self, Transform skip, List<Vector3> viewers)
    {
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
                if (container != null && c.GetComponentInParent<StockContainer>() == container) continue;
                if (self != null && c.transform.IsChildOf(self)) continue;
                if (c.GetComponentInParent<CharacterController>() != null) continue;   // people
                if (skip != null && c.transform.IsChildOf(skip)) continue;             // its own edit copy
                if (layout != null && plot.IsStorefrontCover(c.transform)) continue;   // the renovation wall
                blocked = true;
            }
            if (!blocked) return true;
        }
        return false;
    }

    // A window or door that counts: the real ones (edit copies don't); in a preview the copies instead of
    // the real ones they stand in for.
    private bool Counts(WallOpening o)
    {
        if (o == null) return false;
        if (layout == null) return !o.IsEditCopy;
        if (o.IsEditCopy) return true;
        return !layout.TryGetCopy(o, out _);
    }

    // Has its own Decor (an edit copy's is on its prefab).
    private static bool HasDecor(WallOpening o)
    {
        if (o.GetComponent<Decor>() != null) return true;
        var copy = o.GetComponent<EditLocalItem>();
        return copy != null && copy.Def != null && copy.Def.worldNetworkPrefab != null &&
               copy.Def.worldNetworkPrefab.GetComponent<Decor>() != null;
    }

    private void BuildFeatures()
    {
        features.Clear();
        decor.Clear();
        var settings = StoreAppealSettings.Instance;
        string window = settings != null ? settings.windowFeature : "Window";

        foreach (var d in Decor.All)
        {
            if (d == null) continue;
            Vector3 at = d.transform.position;
            if (layout != null && layout.TryGetCopy(d, out var copy))
            {
                if (copy == null) continue; // taken out
                at = copy.TransformPoint(d.transform.InverseTransformPoint(at));
                if (!plot.Contains(at + Vector3.up * 0.05f) && d.GetComponent<WallOpening>() == null) continue;
            }
            else if (!InThisStore(d.gameObject)) continue;
            foreach (var f in d.features)
                if (f != null && !string.IsNullOrWhiteSpace(f.name))
                    AddDecor(f.name.Trim(), f.amount, at);
        }
        if (layout != null)
        {
            layout.GetExtraDecor(extraDecor);
            foreach (var (feature, amount, position) in extraDecor) AddDecor(feature, amount, position);
        }

        // Windows in the store's walls (unless they say something else with their own Decor).
        foreach (var o in WallOpening.All)
        {
            if (!Counts(o) || o.PieceKind != WallOpening.Kind.Window || HasDecor(o)) continue;
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
    // Kept until decor or what's where in the store changes (counts and prices don't matter here).
    private float FeatureNear(string feature, string category, float reach)
    {
        if (nearCache.TryGetValue((feature, category), out float cached)) return cached;
        float have = 0f, reach2 = reach * reach;
        foreach (var d in decor)
        {
            if (!string.Equals(d.feature, feature, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var o in offers.Values)
            {
                if (!o.def.HasCategory(category)) continue;
                float dx = d.position.x - o.position.x, dz = d.position.z - o.position.z;
                if (dx * dx + dz * dz > reach2 || WallBetween(d.position, o.position)) continue;
                have += d.amount;
                break;
            }
        }
        nearCache[(feature, category)] = have;
        return have;
    }

    // A category's pair score (-1..1 per pair, summed): for each of its items, each pair's effect times how
    // much of the other category is near it (up to Wanted spots), averaged over its items. Cached until the
    // layout changes.
    private float PairScore(string category, List<(string other, float effect, int wanted)> pairs, float reach)
    {
        if (pairCache.TryGetValue(category, out float cached)) return cached;
        float sum = 0f;
        foreach (var (other, effect, wanted) in pairs) sum += effect * PairPart(category, other, wanted, reach);
        pairCache[category] = sum;
        return sum;
    }

    // Averaged over this category's shelf spots (and loose items): how much of the other category is within
    // reach of each (0-1, full at Wanted spots). A spot doesn't count itself. Cached until the layout changes.
    private float PairPart(string category, string other, int wanted, float reach)
    {
        if (pairPartCache.TryGetValue((category, other, wanted), out float cached)) return cached;
        float reach2 = reach * reach, total = 0f, weight = 0f;
        foreach (var kv in offers)
        {
            var o = kv.Value;
            if (!IsIn(o.def, category)) continue;
            int near = 0;
            if (string.Equals(other, StoreAppealSettings.ShipmentCategory, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var b in boxes)
                {
                    float dx = b.x - o.position.x, dz = b.z - o.position.z;
                    if (dx * dx + dz * dz > reach2 || Mathf.Abs(b.y - o.position.y) > 3f || WallBetween(b, o.position)) continue;
                    if (++near >= wanted) break;
                }
            }
            else
                foreach (var kv2 in offers)
                {
                    if (kv2.Key.Equals(kv.Key)) continue;
                    var n = kv2.Value;
                    if (!IsIn(n.def, other)) continue;
                    float dx = n.position.x - o.position.x, dz = n.position.z - o.position.z;
                    if (dx * dx + dz * dz > reach2 || Mathf.Abs(n.position.y - o.position.y) > 3f || WallBetween(n.position, o.position)) continue;
                    if (++near >= wanted) break;
                }
            total += Mathf.Clamp01(near / (float)wanted);
            weight += 1f;
        }
        float part = weight > 0f ? total / weight : 0f;
        pairPartCache[(category, other, wanted)] = part;
        return part;
    }

    /* ========================= WALLS BETWEEN THINGS ========================= */

    private readonly Dictionary<(Vector3Int, Vector3Int), bool> wallCache = new();
    private static readonly RaycastHit[] WallHits = new RaycastHit[16];

    /// <summary>
    /// Is a wall between two spots (so they don't go together: things on the far side of a wall don't help
    /// or hurt each other)? Checked at eye height (Floor + 1.6 m): store walls and walls built inside the
    /// store count; low walls customers see over, windows and doorways don't. Cached until things move.
    /// </summary>
    public bool WallBetween(Vector3 a, Vector3 b)
    {
        var ka = Vector3Int.RoundToInt(a * 10f);
        var kb = Vector3Int.RoundToInt(b * 10f);
        var key = ka.x < kb.x || (ka.x == kb.x && ka.z <= kb.z) ? (ka, kb) : (kb, ka);
        if (wallCache.TryGetValue(key, out bool blocked)) return blocked;

        float eye = (plot != null ? plot.FloorHeight : Mathf.Min(a.y, b.y)) + 1.6f;
        Vector3 from = new(a.x, eye, a.z), to = new(b.x, eye, b.z);
        Vector3 d = to - from;
        float length = d.magnitude;
        blocked = false;
        if (length > 0.05f)
        {
            int n = Physics.RaycastNonAlloc(from, d / length, WallHits, length, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n && !blocked; i++)
            {
                var c = WallHits[i].collider;
                if (c.GetComponentInParent<WallOpening>() != null) continue;              // windows, door frames
                if (layout != null && plot != null && plot.IsStorefrontCover(c.transform)) continue; // the renovation wall
                if (c.GetComponentInParent<CuttableWall>() != null || c.GetComponentInParent<BuiltWallPiece>() != null) blocked = true;
            }
        }
        wallCache[key] = blocked;
        return blocked;
    }

    // In a pair category: "Any" is every item for sale.
    private static bool IsIn(ItemDef def, string category) =>
        string.Equals(category, StoreAppealSettings.AnyCategory, StringComparison.OrdinalIgnoreCase) || def.HasCategory(category);

    // Delivery boxes sitting in the store (not on a cart, not being carried).
    private void GatherBoxes()
    {
        boxes.Clear();
        if (plot == null) return;
        foreach (var item in ItemPickupNet.All)
        {
            if (item == null || !item.IsSpawned || item.IsOnCart || !item.TryGetComponent<ShipmentPackage>(out _)) continue;
            Vector3 p = item.transform.position;
            if (plot.Contains(p + Vector3.up * 0.05f)) boxes.Add(p);
        }
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
        foreach (var o in offers.Values)
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
            float met = covered / importance;
            // Missing decor only holds it back from adding (unless Missing Decor Hurts): none at all is neutral.
            sum += share * (settings.missingDecorHurts ? met : neutral + (1f - neutral) * met);
            weights += share;
        }

        quality = weights > 0f ? sum / weights : neutral;

        // Things that go together (or don't), placed near each other.
        float pairSum = 0f, pairWeights = 0f;
        var anyPairs = settings.PairsFor(StoreAppealSettings.AnyCategory);
        var both = new List<(string other, float effect, int wanted)>();
        foreach (var kv in categoryShare)
        {
            both.Clear();
            var own = settings.PairsFor(kv.Key);
            if (own != null) both.AddRange(own);
            if (anyPairs != null) both.AddRange(anyPairs); // pairs with "Any" apply to every category
            var pairs = both;
            if (pairs.Count == 0) continue;
            float share = kv.Value / total;
            float score = PairScore(kv.Key, pairs, settings.pairReach);
            string Named(string other) =>
                string.Equals(other, StoreAppealSettings.ShipmentCategory, StringComparison.OrdinalIgnoreCase) ? "delivery boxes"
                : string.Equals(other, StoreAppealSettings.AnyCategory, StringComparison.OrdinalIgnoreCase) ? "other things" : other;
            pairSum += share * score;
            pairWeights += share;
            foreach (var (other, effect, wanted) in pairs)
            {
                float part = PairPart(kv.Key, other, wanted, settings.pairReach);
                if (effect > 0f && part < 1f)
                    gaps.Add((share * effect * (1f - part) * settings.pairStrength, $"{kv.Key}: put more {Named(other)} near it"));
                else if (effect < 0f && part > 0f)
                    gaps.Add((share * -effect * part * settings.pairStrength, $"{kv.Key}: move it away from {Named(other)}"));
            }
        }
        if (pairWeights > 0f) quality = Mathf.Clamp01(quality + settings.pairStrength * pairSum / pairWeights);

        gaps.Sort((x, y) => y.worth.CompareTo(x.worth));
        for (int i = 0; i < gaps.Count && i < 5; i++) hints.Add(gaps[i].text);
    }

    /// <summary>For testing: right-click the component in the Inspector (Play mode) → Log Appeal.</summary>
    [ContextMenu("Log Appeal")]
    private void LogAppeal()
    {
        rebuildAll = true;
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
