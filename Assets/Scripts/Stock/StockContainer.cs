using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Something that holds stock: a shelf, clothes rack, table, fridge... Put it on the furniture's
/// world prefab (next to its NetworkObject and ItemPickupNet). It has two kinds of zone, both
/// BoxColliders on child objects (add as many as you like, side by side or apart):
///
/// Stock Zones (shelves): products are lined up in a grid inside the box, stacked if it's tall
/// enough, filling from the front row (the zone's blue arrow) back.
///
/// Hang Rails (clothes rails): the rail runs along the TOP of the box, down its longer side (red X
/// or blue Z, whichever is longer). Products use the crate's Hang Prefab and hang with the top of
/// their model (or a child named "HangPoint") on the rail, one after another along it. By default a
/// product's front (blue arrow) points along the rail, like clothes on a real rack; R turns it round
/// (half turns only: clothes always hang across the rail, never along it), T tips it 90 degrees (for a
/// model built lying flat). Only the rail's length matters; clothes can hang
/// below the box. Set Hang Spacing to squeeze hangers closer than their model's thickness.
///
/// Each zone holds one kind of product, in one orientation (which way it faces, and on shelves
/// whether it stands up or lies down). The player picks it with R / T before stocking an empty
/// zone, and can change it later if everything still fits. How many fit is worked out from the
/// product's size in that orientation.
///
/// A zone can hold several products side by side: stocking a different product into a zone with room
/// left after what's there starts a new PART of it, from where the last product's stock ends (in fill
/// order: Fill From Right). The part before keeps to the width it uses at that moment, so restocking it
/// never pushes into the next one, and nothing moves. A part that empties keeps its place for a new
/// product; empty parts next to each other join back up (all empty: the whole zone again). Clothes
/// rails split the same way, along the rail. To everything else a part is just a zone (its own number,
/// product, count, price): the first part keeps the zone's number, later ones are added after.
///
/// Over the network each zone (part) is just (item id, count, orientation, which part); every machine
/// lays out the models itself. Zone colliders are made triggers and switched off at runtime (authoring only).
/// </summary>
public class StockContainer : NetworkBehaviour
{
    /// <summary>What's in one zone.</summary>
    public struct ZoneStock : INetworkSerializeByMemcpy, IEquatable<ZoneStock>
    {
        public int itemId; // 0 = empty
        public int count;
        public int orient; // see Orientation
        public int seed;   // picks the variant (colour) of each product; new each time the zone is restocked from empty
        public int price;      // sale price in cents (0 = not for sale); kept while the zone is empty
        public int pricedItem; // the product the price was set for (restocking with something else clears it)
        public uint paint;     // the products' colors (PaintSet, 0 = their own): one color per zone
        public ushort from, to; // a part of a zone: the share of its width it covers, in fill order (0-65535; both 0: all)
        public byte box;        // a part added after the zones: which zone it's part of, +1 (0: the zone with its own number)
        public byte flags;      // 1: joined back into another part (unused, but keeps its number); 2: split front to back

        /// <summary>Is what's in this zone for sale? (Stocked, priced, and the product has a Sellable.)</summary>
        public bool IsForSale => count > 0 && price > 0 && pricedItem == itemId && Sellable.IsSellable(itemId);

        /// <summary>A part that joined back into another (nothing there any more).</summary>
        public bool Dead => (flags & 1) != 0;
        /// <summary>Its zone is split front to back (parts one behind another) rather than side by side.</summary>
        public bool AcrossDepth => (flags & 2) != 0;
        public void SetAcrossDepth(bool on) => flags = (byte)(on ? flags | 2 : flags & ~2);
        /// <summary>The whole zone (not split).</summary>
        public bool Whole => from == 0 && (to == 0 || to == ushort.MaxValue);
        /// <summary>Where it starts and ends across the zone's width, 0-1 in fill order.</summary>
        public float From => Whole ? 0f : from / 65535f;
        public float To => Whole ? 1f : to / 65535f;

        /// <summary>Cover this share of the zone's width (0-1, fill order).</summary>
        public void SetSpan(float start, float end)
        {
            if (start <= 0.0001f && end >= 0.9999f) { from = 0; to = 0; return; }
            from = (ushort)Mathf.Clamp(Mathf.RoundToInt(start * 65535f), 0, 65535);
            to = (ushort)Mathf.Clamp(Mathf.RoundToInt(end * 65535f), 0, 65535);
            if (to == 0) to = 1;
        }

        /// <summary>Emptied: the product gone, the price tag and where it is kept.</summary>
        public ZoneStock Emptied() =>
            new() { price = price, pricedItem = pricedItem, from = from, to = to, box = box, flags = flags };

        public bool Equals(ZoneStock other) =>
            itemId == other.itemId && count == other.count && orient == other.orient && seed == other.seed &&
            price == other.price && pricedItem == other.pricedItem && paint == other.paint &&
            from == other.from && to == other.to && box == other.box && flags == other.flags;
        public override bool Equals(object obj) => obj is ZoneStock other && Equals(other);
        public override int GetHashCode() => (((itemId * 397 ^ count) * 31 + orient) * 31 + seed) * 31 + price;
    }

    /// <summary>
    /// A product's orientation in a zone, packed in one int: bits 0-1 = quarter turns from facing the
    /// zone's blue arrow (on rails: from facing along the rail), bit 2 = tipped 90 degrees (on shelves:
    /// lying down with the front facing up; on rails: a model built lying flat stood upright).
    /// </summary>
    public static class Orientation
    {
        public static int Turns(int orient) => orient & 3;
        public static bool Lying(int orient) => (orient & 4) != 0;
        public static int Make(int turns, bool lying) => (turns & 3) | (lying ? 4 : 0);
        public static int Turn(int orient) => Make(Turns(orient) + 1, Lying(orient));
        public static int ToggleLying(int orient) => Make(Turns(orient), !Lying(orient));

        public static Quaternion Rotation(int orient, bool hang)
        {
            var r = Quaternion.Euler(0f, Turns(orient) * 90f, 0f);
            return Lying(orient) ? r * Quaternion.Euler(-90f, 0f, 0f) : r;
        }
    }

    [Tooltip("Shelf spaces: BoxColliders products are lined up and stacked in. Products face the blue arrow.")]
    [SerializeField] private BoxCollider[] stockZones;
    [Tooltip("Clothes rails: BoxColliders whose top, along their longer side, is the rail. Products hang from it " +
             "by the top of their Hang Prefab (or a child named HangPoint). Only the length matters, unless Clothes " +
             "Fit Rail Box is on (then the box is the space they hang in).")]
    [SerializeField] private BoxCollider[] hangRails;
    [Tooltip("Space left between products on shelves, in meters.")]
    [SerializeField] private float spacing = 0.01f;
    [Tooltip("Distance between hangers on a rail, in meters. 0 = the product's own thickness plus Spacing.")]
    [SerializeField] private float hangSpacing = 0f;
    [Tooltip("Hang rails are the space clothes hang in, like a shelf zone: the box's top is the rail, its height " +
             "how far down they may hang, its depth (across the rail) how wide they may be. Clothes that don't fit " +
             "aren't hung there (\"doesn't fit\"). Off: only the rail's length matters, and clothes may hang " +
             "below the box (size the boxes before turning this on).")]
    [SerializeField] private bool clothesFitRailBox = false;
    [Tooltip("Never more than this many products in one zone, however small they are.")]
    [SerializeField] private int maxPerZone = 100;
    [Tooltip("Shelves fill column by column (each column front to back, then stacked up, before the next one), " +
             "starting from this side as a shopper facing the shelf sees it. A product takes only the width it " +
             "needs, so another can start after it...")]
    [SerializeField] private bool fillFromRight = true;
    [Tooltip("...and from the front (nearest the shopper) or the back.")]
    [SerializeField] private bool fillFromFront = true;
    [Tooltip("Starting a second product on a shelf spot splits it the way you face it: facing the front, side by " +
             "side; from its end, front to back. It leans toward splitting along the spot's long side, more the " +
             "longer it is than deep: this much per extra length (0: just the way you face).")]
    [SerializeField, Range(0f, 1f)] private float splitLongSideLean = 0.25f;
    [Tooltip("A zone with another placed thing in it (a shelf stacked on top, a box left there) is covered: " +
             "nothing can be stocked there and customers can't take from it until it's clear. What's already in " +
             "it can still be taken out by players.")]
    [SerializeField] private bool blockWhenCovered = true;
    [Tooltip("How far inside a zone's box something has to reach to cover it (meters), so things just " +
             "touching its edge don't count.")]
    [SerializeField] private float coverMargin = 0.02f;

    /// <summary>Every spawned container on this machine (customers can search these for stock).</summary>
    public static readonly List<StockContainer> All = new();

    private NetworkList<ZoneStock> zones;
    private readonly List<List<GameObject>> visuals = new();
    private readonly List<ZoneStock> visualState = new();
    private ZoneStock[] pendingStock; // server: contents to restore when spawned (edit mode)
    private StockVisualFollower visualRoot; // unscaled holder that follows this furniture

    /// <summary>This furniture started or stopped moving: its stock models follow only while it can.</summary>
    public void OnMovedOrStopped()
    {
        if (visualRoot != null) visualRoot.Refresh();
    }

    private int ShelfCount => stockZones != null ? stockZones.Length : 0;
    private int RailCount => hangRails != null ? hangRails.Length : 0;

    /// <summary>The zones set up on the prefab: shelf zones first, then hang rails.</summary>
    private int AuthoredCount => ShelfCount + RailCount;

    /// <summary>
    /// Zones: the ones set up on the prefab (shelf zones, then hang rails), then any parts added by
    /// splitting them (see the class notes). Some numbers may be unused (parts joined back up): GetBox
    /// is null for those.
    /// </summary>
    public int ZoneCount => zones != null && zones.Count > AuthoredCount ? zones.Count : AuthoredCount;

    // A zone's entry, whether or not it's spawned yet (default: not there).
    private ZoneStock Entry(int zone) => zones != null && zone >= 0 && zone < zones.Count ? zones[zone] : default;

    // Which of the prefab's zones a zone (part) is in.
    private static int BoxIndexOf(int zone, ZoneStock z) => z.box > 0 ? z.box - 1 : zone;

    private BoxCollider AuthoredBox(int b) =>
        b < 0 ? null : b < ShelfCount ? stockZones[b] : b < AuthoredCount ? hangRails[b - ShelfCount] : null;

    /// <summary>Is this zone a hang rail (not a shelf space)?</summary>
    public bool IsHangRail(int zone)
    {
        int b = BoxIndexOf(zone, Entry(zone));
        return b >= ShelfCount && b < AuthoredCount;
    }

    /// <summary>The product models in a zone, in the order they went in (local; for outlines and the like).</summary>
    public IReadOnlyList<GameObject> GetZoneModels(int zone) =>
        zone >= 0 && zone < visuals.Count ? visuals[zone] : System.Array.Empty<GameObject>();

    /// <summary>The model that taking one out of this zone removes (the last one in), or null.</summary>
    public GameObject GetTakeModel(int zone)
    {
        var list = GetZoneModels(zone);
        for (int i = list.Count - 1; i >= 0; i--) if (list[i] != null) return list[i];
        return null;
    }

    /// <summary>A zone's box in world space (centre, rotation with world up, full size): a part's own share.</summary>
    public bool TryGetZoneBox(int zone, out Vector3 center, out Quaternion rotation, out Vector3 size)
    {
        center = default; rotation = default; size = default;
        var box = GetBox(zone);
        if (box == null) return false;
        SegmentFrame(box, IsHangRail(zone), Entry(zone), out center, out rotation, out size, out _);
        return true;
    }

    // The prefab's zone a zone (part) is in; null if there's no such zone or it's an unused number.
    private BoxCollider GetBox(int zone)
    {
        if (zone < 0) return null;
        var z = Entry(zone);
        return z.Dead ? null : AuthoredBox(BoxIndexOf(zone, z));
    }

    /// <summary>
    /// A zone (part)'s box as products see it: the zone's ZoneFrame, narrowed to the part's share of its
    /// width (across shelves, from the Fill From Right side; along rails). Rail Along Z: which way the
    /// whole rail runs (a short part mustn't turn it).
    /// </summary>
    private void SegmentFrame(BoxCollider box, bool hang, ZoneStock z, out Vector3 center, out Quaternion rotation,
                              out Vector3 size, out bool railAlongZ)
    {
        ZoneFrame(box, out center, out rotation, out size);
        railAlongZ = size.z > size.x;
        if (z.Whole) return;
        float f0 = z.From, f1 = z.To;
        if (hang)
        {
            float length = railAlongZ ? size.z : size.x;
            float mid = -length * 0.5f + (f0 + f1) * 0.5f * length;
            center += rotation * ((railAlongZ ? Vector3.forward : Vector3.right) * mid);
            if (railAlongZ) size.z = (f1 - f0) * length; else size.x = (f1 - f0) * length;
        }
        else if (z.AcrossDepth)
        {
            // Front to back: from the Fill From Front side (the zone's +z) back.
            float depth = size.z;
            float mid = -depth * 0.5f + (f0 + f1) * 0.5f * depth;
            center += rotation * new Vector3(0f, 0f, fillFromFront ? -mid : mid);
            size.z = (f1 - f0) * depth;
        }
        else
        {
            // Side by side: from the Fill From Right side (the zone's -x) across.
            float width = size.x;
            float mid = -width * 0.5f + (f0 + f1) * 0.5f * width;
            center += rotation * new Vector3(fillFromRight ? mid : -mid, 0f, 0f);
            size.x = (f1 - f0) * width;
        }
    }

    // How long the whole zone is along the way it splits (across a shelf or front to back, along a rail).
    private static float SplitLength(Vector3 zoneSize, bool hang, bool acrossDepth) =>
        hang ? Mathf.Max(zoneSize.x, zoneSize.z) : acrossDepth ? zoneSize.z : zoneSize.x;

    // Which way a zone splits: the way its parts already are; a whole one, the way the player faces it
    // (facing the front: side by side; from an end: front to back), leaning toward its long side.
    private bool SplitAcrossDepth(int zone, Vector3 view)
    {
        var box = GetBox(zone);
        if (box == null || IsHangRail(zone)) return false;
        int b = BoxIndexOf(zone, Entry(zone));
        for (int i = 0; i < ZoneCount; i++)
        {
            var z = Entry(i);
            if (!z.Dead && !z.Whole && BoxIndexOf(i, z) == b) return z.AcrossDepth; // already split: that way
        }
        ZoneFrame(box, out _, out Quaternion rot, out Vector3 size);
        Vector3 flat = Vector3.ProjectOnPlane(view, Vector3.up);
        if (flat.sqrMagnitude < 1e-6f) return false;
        flat.Normalize();
        float alongX = Mathf.Abs(Vector3.Dot(flat, rot * Vector3.right));   // looking along its width (from an end)
        float alongZ = Mathf.Abs(Vector3.Dot(flat, rot * Vector3.forward)); // looking at its front or back
        float w = Mathf.Max(size.x, 0.01f), d = Mathf.Max(size.z, 0.01f);
        float lean = splitLongSideLean * (Mathf.Max(w, d) / Mathf.Min(w, d) - 1f);
        // Front to back needs looking along the width; the long side gets the benefit of the doubt.
        return w >= d ? alongX > alongZ + lean : alongX + lean > alongZ;
    }

    private void Awake()
    {
        zones = new NetworkList<ZoneStock>(null, NetworkVariableReadPermission.Everyone,
                                           NetworkVariableWritePermission.Server);
        for (int i = 0; i < AuthoredCount; i++)
        {
            var box = AuthoredBox(i);
            if (box != null) box.enabled = false; // authoring only
        }
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            zones.Clear();
            int n = Mathf.Max(AuthoredCount, pendingStock != null ? pendingStock.Length : 0); // (split parts too)
            for (int i = 0; i < n; i++)
                zones.Add(pendingStock != null && i < pendingStock.Length ? pendingStock[i] : default);
            pendingStock = null;
        }

        zones.OnListChanged += OnZonesChanged;
        All.Add(this);
        StoreListing.Track(gameObject);
        for (int i = 0; i < ZoneCount; i++) RefreshVisuals(i);
    }

    public override void OnNetworkDespawn()
    {
        All.Remove(this);
        zones.OnListChanged -= OnZonesChanged;
        StoreListing.Untrack(this);
        for (int i = 0; i < visuals.Count; i++) SetVisuals(i, default);
        if (visualRoot != null) Destroy(visualRoot.gameObject);
        visualRoot = null;
    }

    public override void OnDestroy()
    {
        if (visualRoot != null) Destroy(visualRoot.gameObject);
        base.OnDestroy();
    }

    private Transform GetVisualRoot()
    {
        if (visualRoot == null) visualRoot = StockVisualFollower.Create(transform, this);
        visualRoot.Refresh();
        return visualRoot.transform;
    }

    /* ========================= QUERIES ========================= */

    /// <summary>What's in a zone (empty if there's no such zone).</summary>
    public ZoneStock GetZone(int zone) =>
        IsSpawned && zone >= 0 && zone < zones.Count ? zones[zone] : default;

    public bool HasStock
    {
        get
        {
            if (!IsSpawned) return false;
            foreach (var z in zones.Each()) if (z.count > 0) return true;
            return false;
        }
    }

    /// <summary>Can this product go in this kind of zone? (Shelves need a Shelf Prefab, rails a Hang Prefab.)</summary>
    public bool Accepts(int zone, int itemId) => GetStockedPrefab(itemId, IsHangRail(zone)) != null;

    /// <summary>The model this zone shows for a product: its Hang Prefab on rails, its Shelf Prefab on shelves.</summary>
    public GameObject GetModelFor(int zone, int itemId) => GetStockedPrefab(itemId, IsHangRail(zone));

    /// <summary>
    /// The orientation a product would use in this zone: the zone's own if it already holds that
    /// product, otherwise the one given (the player's pick for an empty zone).
    /// </summary>
    public int OrientFor(int zone, int itemId, int wanted)
    {
        var z = GetZone(zone);
        return z.count > 0 && z.itemId == itemId ? z.orient : wanted;
    }

    /// <summary>
    /// How many of this product fit in a zone in that orientation (0 if it can't go there). A zone holding
    /// something else: how many fit in the new part it would start after it (see WouldSplit).
    /// </summary>
    public int GetCapacity(int zone, int itemId, int orient, uint? paint = null)
    {
        if (!Accepts(zone, itemId)) return 0;
        if (WouldSplit(zone, itemId, paint))
            return TryGetTail(zone, out var tail) && TryGetLayout(GetBox(zone), IsHangRail(zone), tail, itemId, orient, out var t)
                ? t.capacity : 0;
        return TryGetLayout(zone, itemId, OrientFor(zone, itemId, orient), out var l) ? l.capacity : 0;
    }

    /* ========================= SPLITTING ========================= */

    /// <summary>
    /// Would one of this product start a new part of this zone? It holds a different product (or the same in
    /// another color), so the new one goes after it: where its stock ends now. (See the class notes.)
    /// </summary>
    public bool WouldSplit(int zone, int itemId, uint? paint = null)
    {
        var z = GetZone(zone);
        return z.count > 0 && GetBox(zone) != null && (z.itemId != itemId || (paint.HasValue && z.paint != paint.Value));
    }

    /// <summary>
    /// Which way the local player (or, on the host, the player stocking right now) is looking: a zone not
    /// split yet splits the way they face it (see SplitAcrossDepth). Set before asking about stocking.
    /// </summary>
    public static Vector3 SplitView;

    // The part a new product would start after what's in this zone: from where its stock ends (in fill
    // order, the way the zone splits) to the end of the zone's part. Head: what's there, as it'll be laid
    // out once split. False if there's no room left.
    private bool TryGetTail(int zone, out ZoneStock tail) => TryGetTail(zone, out tail, out _);

    private bool TryGetTail(int zone, out ZoneStock tail, out ZoneStock head)
    {
        tail = default;
        head = default;
        var box = GetBox(zone);
        var z = GetZone(zone);
        if (box == null || z.count <= 0) return false;
        bool depth = SplitAcrossDepth(zone, SplitView);
        head = z;
        head.SetAcrossDepth(depth); // (a whole zone is laid out that way once split)
        float end = UsedEnd(box, IsHangRail(zone), head);
        if (z.To - end < 0.001f) return false;
        tail.box = (byte)(BoxIndexOf(zone, z) + 1);
        tail.SetAcrossDepth(depth);
        tail.SetSpan(end, z.To);
        if (tail.Whole) return false; // (can't be: it starts after something)
        head.SetSpan(head.From, end);
        return true;
    }

    // Where a part's stock ends, as a share of the zone in fill order the way it splits: the columns used
    // (side by side) or slices used (front to back) on a shelf, the hangers on a rail.
    private float UsedEnd(BoxCollider box, bool hang, ZoneStock z)
    {
        ZoneFrame(box, out _, out _, out Vector3 full);
        float length = SplitLength(full, hang, z.AcrossDepth);
        if (length <= 0f || !TryGetLayout(box, hang, z, z.itemId, z.orient, out var l)) return z.To;
        float used = hang ? Mathf.Min(l.railLength, z.count * l.step + spacing)
            : z.AcrossDepth ? Mathf.Min(Mathf.CeilToInt(z.count / (float)Mathf.Max(1, l.nx * l.ny)), l.nz) * l.cell.z // slices
            : Mathf.Min(Mathf.CeilToInt(z.count / (float)Mathf.Max(1, l.nz * l.ny)), l.nx) * l.cell.x;               // columns
        // A whole zone (centred) is lined up from its starting side once split, so measure from there.
        return Mathf.Min(z.To, z.From + used / length);
    }

    /// <summary>
    /// Where stocking this product here would split the zone (WouldSplit): a line across it at the split,
    /// on the shelf's floor (across the rail, on a rail), so the player sees where the new part starts.
    /// </summary>
    public bool TryGetSplitLine(int zone, int itemId, uint? paint, out Vector3 a, out Vector3 b)
    {
        a = b = default;
        var box = GetBox(zone);
        if (box == null || !WouldSplit(zone, itemId, paint) || !TryGetTail(zone, out var tail)) return false;
        bool hang = IsHangRail(zone);
        ZoneFrame(box, out Vector3 center, out Quaternion rot, out Vector3 size);
        float f = tail.From;
        Vector3 la, lb;
        if (hang)
        {
            bool alongZ = size.z > size.x;
            float length = alongZ ? size.z : size.x, c = -length * 0.5f + f * length;
            float across = (alongZ ? size.x : size.z) * 0.5f, top = size.y * 0.5f;
            la = alongZ ? new Vector3(-across, top, c) : new Vector3(c, top, -across);
            lb = alongZ ? new Vector3(across, top, c) : new Vector3(c, top, across);
        }
        else if (tail.AcrossDepth)
        {
            float c = -size.z * 0.5f + f * size.z, z = fillFromFront ? -c : c, y = -size.y * 0.5f + 0.01f;
            la = new Vector3(-size.x * 0.5f, y, z);
            lb = new Vector3(size.x * 0.5f, y, z);
        }
        else
        {
            float c = -size.x * 0.5f + f * size.x, x = fillFromRight ? c : -c, y = -size.y * 0.5f + 0.01f;
            la = new Vector3(x, y, -size.z * 0.5f);
            lb = new Vector3(x, y, size.z * 0.5f);
        }
        a = center + rot * la;
        b = center + rot * lb;
        return true;
    }

    // A number for a new part: an unused one (a part that joined back up) if there is one, else a new one.
    private int FreePartNumber()
    {
        for (int i = AuthoredCount; i < zones.Count; i++) if (zones[i].Dead) return i;
        return zones.Count;
    }

    // Server: empty parts of a zone next to each other join back into one (the zone's own number stays).
    // All of it empty again: the whole zone, as it started.
    private void ServerJoinEmptyParts(int boxIndex)
    {
        var parts = new List<int>();
        for (int i = 0; i < zones.Count; i++)
            if (!zones[i].Dead && BoxIndexOf(i, zones[i]) == boxIndex) parts.Add(i);
        if (parts.Count < 2) return;
        parts.Sort((a, b) => zones[a].From.CompareTo(zones[b].From));
        for (int k = 0; k < parts.Count - 1;)
        {
            var a = zones[parts[k]];
            var b = zones[parts[k + 1]];
            if (a.count > 0 || b.count > 0) { k++; continue; }
            // The zone's own number keeps going; the other number is put aside.
            int keep = parts[k + 1] == boxIndex ? parts[k + 1] : parts[k];
            int drop = keep == parts[k] ? parts[k + 1] : parts[k];
            var kept = zones[keep];
            kept.SetSpan(a.From, b.To);
            if (kept.Whole) kept.SetAcrossDepth(false); // all one again: fills the usual way
            if (kept.price == 0) { var d = zones[drop]; kept.price = d.price; kept.pricedItem = d.pricedItem; }
            zones[keep] = kept;
            zones[drop] = new ZoneStock { box = (byte)(boxIndex + 1), flags = 1 };
            parts[k] = keep;
            parts.RemoveAt(k + 1);
        }
    }

    /// <summary>How many of this product fit in a zone turned exactly this way (0: not that way).</summary>
    public int CapacityAt(int zone, int itemId, int orient)
    {
        if (!Accepts(zone, itemId)) return 0;
        if (WouldSplit(zone, itemId))
            return TryGetTail(zone, out var tail) && TryGetLayout(GetBox(zone), IsHangRail(zone), tail, itemId, orient, out var t)
                ? t.capacity : 0;
        return TryGetLayout(zone, itemId, orient, out var l) ? l.capacity : 0;
    }

    /// <summary>Why one more of this product can't go in this zone (for logs); "ok" if it can.</summary>
    public string WhyCantStock(int zone, int itemId, int orient)
    {
        if (zone < 0 || zone >= ZoneCount || GetBox(zone) == null) return $"no zone {zone}";
        var z = GetZone(zone);
        if (WouldSplit(zone, itemId) && GetCapacity(zone, itemId, orient) <= 0)
            return $"zone holds {ItemDebug.Describe(z.itemId)}, with no room left after it";
        if (IsCovered(zone)) return "something is placed in the way (covered)";
        if (!Accepts(zone, itemId)) return $"the crate has no {(IsHangRail(zone) ? "Hang" : "Shelf")} Prefab for this zone";
        if (!TryGetLayout(zone, itemId, OrientFor(zone, itemId, orient), out var l)) return "the product doesn't fit this zone in that orientation";
        if (z.count >= l.capacity) return $"full ({z.count}/{l.capacity})";
        return "ok";
    }

    /* ========================= COVERED ZONES ========================= */

    private bool[] covered;
    private int coverVersion = -1;
    private float coverCheckAt;
    private bool coverPending;
    private ItemPickupNet self;
    // Roomy: every stocked product has a collider, and a full zone's own products (and its neighbours')
    // are found too; with too small a list the thing actually covering it could be missed.
    private static readonly Collider[] CoverHits = new Collider[256];

    /// <summary>
    /// Is something else placed in this zone (another shelf stacked on it, a box left there)? Then it can't
    /// be stocked and customers can't take from it. Rechecked shortly after anything is placed or moved.
    /// </summary>
    public bool IsCovered(int zone)
    {
        if (!blockWhenCovered || zone < 0 || zone >= ZoneCount) return false;
        if (covered == null || covered.Length != ZoneCount) { covered = new bool[ZoneCount]; coverVersion = -1; }
        int v = ItemPickupNet.LayoutVersion;
        if (v != coverVersion)
        {
            coverVersion = v;
            coverPending = true;
            coverCheckAt = Time.time + 0.1f; // let what just landed settle into physics first
        }
        if (coverPending && Time.time >= coverCheckAt)
        {
            coverPending = false;
            for (int i = 0; i < ZoneCount; i++) covered[i] = CheckCovered(i);
        }
        return covered[zone];
    }

    // Another placed item's collider inside the zone's box (shrunk by the margin)? This furniture, its
    // products, items on carts and edit-mode copies don't count.
    private bool CheckCovered(int zone)
    {
        if (!TryGetZoneBox(zone, out Vector3 center, out Quaternion rotation, out Vector3 size)) return false;
        Vector3 half = Vector3.Max(size * 0.5f - Vector3.one * coverMargin, Vector3.one * 0.005f);
        if (self == null) self = GetComponent<ItemPickupNet>();
        int n = Physics.OverlapBoxNonAlloc(center, half, CoverHits, rotation, Physics.DefaultRaycastLayers,
                                           QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var col = CoverHits[i];
            if (col == null || col.transform.IsChildOf(transform)) continue;
            var item = col.GetComponentInParent<ItemPickupNet>();
            if (item == null || item == self || !item.IsSpawned || item.IsOnCart) continue;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Can one more of this product go in this zone? (orient: the pick, if the zone is empty. Paint: its
    /// colors; a zone holds one color, so a different one only goes in an empty zone. Null: any.)
    /// </summary>
    public bool HasRoomFor(int zone, int itemId, int orient, uint? paint = null)
    {
        // A covered rail takes nothing; a shelf only loses the places something's in the way of (ApplyCover).
        if (GetBox(zone) == null || (IsHangRail(zone) && IsCovered(zone))) return false;
        if (WouldSplit(zone, itemId, paint)) return GetCapacity(zone, itemId, orient, paint) > 0; // a new part after it
        var z = GetZone(zone);
        return Mathf.Max(0, z.count) < GetCapacity(zone, itemId, orient, paint);
    }

    /// <summary>
    /// The zone nearest a world position that can take one more of this product (wantStock false;
    /// orient = the pick for an empty zone), or that holds some of it (wantStock true; itemId 0 = any
    /// product). -1 if none.
    /// </summary>
    public int FindNearestZone(Vector3 near, int itemId, bool wantStock, int orient = 0, uint? paint = null)
    {
        int best = -1;
        float bestDist = float.MaxValue;
        for (int i = 0; i < ZoneCount; i++)
        {
            if (GetBox(i) == null) continue;
            var z = GetZone(i);
            bool ok = wantStock
                ? z.count > 0 && (itemId == 0 || z.itemId == itemId) && (!paint.HasValue || z.paint == paint.Value)
                : HasRoomFor(i, itemId, orient, paint);
            if (!ok) continue;

            float d = DistanceToZone(i, near);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    /// <summary>The zone nearest a world position, whatever is in it (-1 if there are none).</summary>
    public int NearestZone(Vector3 near)
    {
        int best = -1;
        float bestDist = float.MaxValue;
        for (int i = 0; i < ZoneCount; i++)
        {
            if (GetBox(i) == null) continue;
            float d = DistanceToZone(i, near);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    /// <summary>Does this product fit in this zone in any orientation?</summary>
    public bool FitsAnyWay(int zone, int itemId)
    {
        for (int o = 0; o < 8; o++)
            if (CapacityAt(zone, itemId, o) > 0) return true;
        return false;
    }

    /// <summary>A zone holding this item, or -1. For customers looking for something to buy.</summary>
    public int FindStock(int itemId)
    {
        for (int i = 0; i < ZoneCount; i++)
        {
            var z = GetZone(i);
            if (itemId != 0 && z.itemId == itemId && z.count > 0) return i;
        }
        return -1;
    }

    /// <summary>Where the next product put in this zone would go (for the placement preview).</summary>
    public bool TryGetNextSlot(int zone, int itemId, int orient, out Vector3 position, out Quaternion rotation,
                               uint? paint = null)
    {
        position = default;
        rotation = default;
        var box = GetBox(zone);
        if (box == null || !Accepts(zone, itemId)) return false;
        if (WouldSplit(zone, itemId, paint))
        {
            // The first of a new part, where the stock there ends now.
            if (!TryGetTail(zone, out var tail) || !TryGetLayout(box, IsHangRail(zone), tail, itemId, orient, out var t)) return false;
            GetSlotPose(box, IsHangRail(zone), tail, t, 0, out position, out rotation);
            return true;
        }
        var z = GetZone(zone);
        int index = z.count > 0 && z.itemId == itemId ? z.count : 0;
        if (!TryGetLayout(zone, itemId, OrientFor(zone, itemId, orient), out var layout) || index >= layout.capacity) return false;
        GetSlotPose(box, IsHangRail(zone), Entry(zone), layout, index, out position, out rotation);
        return true;
    }

    /// <summary>
    /// The orientation a product really takes on a shelf: lying or standing forced if its crate allows only
    /// one (Shelf Pose), otherwise as given.
    /// </summary>
    public static int AllowedOrient(int itemId, int orient)
    {
        var crate = GetCratePickup(itemId);
        if (crate == null) return orient;
        return crate.ShelfPoseAllowed switch
        {
            ItemPickupNet.ShelfPose.LyingOnly => Orientation.Make(Orientation.Turns(orient), true),
            ItemPickupNet.ShelfPose.StandingOnly => Orientation.Make(Orientation.Turns(orient), false),
            _ => orient,
        };
    }

    /// <summary>Can its products be laid down and stood up on shelves (T)? False if the crate allows only one.</summary>
    public static bool CanTip(int itemId)
    {
        var crate = GetCratePickup(itemId);
        return crate == null || crate.ShelfPoseAllowed == ItemPickupNet.ShelfPose.Either;
    }

    /// <summary>Is this item a stock crate (and does it say what one product looks like)?</summary>
    public static bool IsStockCrate(int itemId) => GetCratePickup(itemId) != null;

    /// <summary>How many products a full crate of this item holds (0 if it isn't a stock crate).</summary>
    public static int GetUnitsPerCrate(int itemId) => GetCratePickup(itemId)?.UnitsPerCrate ?? 0;

    /// <summary>
    /// What one product from this crate looks like on a hang rail (hang true) or a shelf (false).
    /// Null if it isn't a stock crate or has no model for that.
    /// </summary>
    public static GameObject GetStockedPrefab(int itemId, bool hang)
    {
        var crate = GetCratePickup(itemId);
        return crate == null ? null : hang ? crate.HangPrefab : crate.ShelfPrefab;
    }

    /// <summary>The model for the product in one slot of a zone (its variant, if the crate has several).</summary>
    public static GameObject GetVariantPrefab(int itemId, bool hang, int seed, int slot)
    {
        var crate = GetCratePickup(itemId);
        return crate == null ? null : crate.GetVariant(hang, seed, slot);
    }

    /// <summary>The model the next product put in this zone will use (for the preview).</summary>
    public GameObject GetNextModel(int zone, int itemId)
    {
        var z = GetZone(zone);
        bool same = z.count > 0 && z.itemId == itemId;
        return same ? GetVariantPrefab(itemId, IsHangRail(zone), z.seed, z.count) : GetModelFor(zone, itemId);
    }

    private static ItemPickupNet GetCratePickup(int itemId)
    {
        if (itemId == 0 || ItemCatalog.Instance == null || !ItemCatalog.Instance.TryGet(itemId, out var def) ||
            def.worldNetworkPrefab == null) return null;
        var pickup = def.worldNetworkPrefab.GetComponent<ItemPickupNet>();
        return pickup != null && pickup.IsStockCrate && (pickup.ShelfPrefab != null || pickup.HangPrefab != null)
            ? pickup : null;
    }

    /// <summary>The middle of a zone (a part's own middle), in the world.</summary>
    public Vector3 GetZoneCenter(int zone) =>
        TryGetZoneBox(zone, out Vector3 center, out _, out _) ? center : transform.position;

    private float DistanceToZone(int zone, Vector3 point)
    {
        if (!TryGetZoneBox(zone, out Vector3 center, out Quaternion rotation, out Vector3 size)) return float.MaxValue;
        Vector3 local = Quaternion.Inverse(rotation) * (point - center);
        Vector3 half = size * 0.5f;
        Vector3 clamped = new(Mathf.Clamp(local.x, -half.x, half.x), Mathf.Clamp(local.y, -half.y, half.y),
                              Mathf.Clamp(local.z, -half.z, half.z));
        return Vector3.Distance(point, center + rotation * clamped);
    }

    /* ========================= SERVER ========================= */

    /// <summary>
    /// Server: put one product in a zone. An empty zone takes the given orientation; otherwise the
    /// zone keeps its own. False if it's full, holds something else, or doesn't take this product.
    /// </summary>
    /// <summary>Server: one more product in a zone (paint: its colors; null: the zone's own, for putting back).</summary>
    /// <summary>Split: may it start a new part after something else in the zone (false: putting things back).</summary>
    public bool ServerAddOne(int zone, int itemId, int orient, uint? paint = null, bool split = true) =>
        ServerAddOne(zone, itemId, orient, paint, split, out _);

    /// <summary>
    /// Server: as ServerAddOne; Into: the zone it went in (a new part, when it started one after something
    /// else, see WouldSplit).
    /// </summary>
    public bool ServerAddOne(int zone, int itemId, int orient, uint? paint, bool split, out int into)
    {
        into = zone;
        if (!IsServer || zone < 0 || zone >= zones.Count || !HasRoomFor(zone, itemId, orient, paint)) return false;
        if (blockWhenCovered && IsHangRail(zone) && CheckCovered(zone)) return false; // checked now, not the last (maybe stale) look
        if (WouldSplit(zone, itemId, paint))
        {
            if (!split) return false;
            // A new part, from where what's here ends now; what's here keeps to the room it uses (laid out
            // the way the zone splits, so it may move this once).
            if (!TryGetTail(zone, out var tail, out var head)) return false;
            zones[zone] = head;
            int at = FreePartNumber();
            if (at < zones.Count) zones[at] = tail; else zones.Add(tail);
            zone = into = at;
        }
        var z = zones[zone];
        bool same = z.count > 0 && z.itemId == itemId;
        bool keepPrice = z.pricedItem == itemId && z.price > 0; // the shelf's own price tag stays for the same product
        int price = keepPrice ? z.price : 0;
        // No price of its own: use the store's price for this product, if "Set for all" gave it one.
        if (price == 0 && StorePlot.TryGetStorePriceAt(GetZoneCenter(zone), itemId, false, out int storePrice))
            price = storePrice;
        var now = z; // (where it is in the zone stays)
        now.itemId = itemId;
        now.count = (same ? z.count : 0) + 1;
        now.orient = OrientFor(zone, itemId, orient);
        now.seed = same ? z.seed : UnityEngine.Random.Range(1, int.MaxValue);
        now.price = price;
        now.pricedItem = price > 0 ? itemId : 0;
        now.paint = same ? z.paint : paint ?? 0;
        zones[zone] = now;
        return true;
    }

    /// <summary>Server: change how a zone's products sit. False if they wouldn't all fit that way.</summary>
    public bool ServerSetOrientation(int zone, int orient)
    {
        if (!IsServer || zone < 0 || zone >= zones.Count) return false;
        var z = zones[zone];
        if (z.count <= 0) return false;
        if (!TryGetLayout(zone, z.itemId, orient, out var layout) || layout.capacity < z.count) return false;
        z.orient = orient;
        zones[zone] = z;
        return true;
    }

    /// <summary>
    /// Server: take one product out of a zone (a customer bought it, say). The last model in the
    /// zone disappears for everyone. Returns the item id taken, or 0 if the zone was empty.
    /// </summary>
    public int ServerRemoveOne(int zone)
    {
        if (!IsServer || zone < 0 || zone >= zones.Count) return 0;
        var z = zones[zone];
        if (z.count <= 0) return 0;
        int taken = z.itemId;
        if (z.count == 1)
        {
            zones[zone] = z.Emptied(); // empty, but keeps its price tag (and its place in the zone)
            if (!z.Whole) ServerJoinEmptyParts(BoxIndexOf(zone, z));
        }
        else
        {
            z.count--;
            zones[zone] = z;
        }
        return taken;
    }

    /// <summary>
    /// Server: set the price tag of one zone, in cents (0 = not for sale). It's for the product in it
    /// now (or the one it was priced for, if it's empty). Returns false if there's nothing to price.
    /// </summary>
    public bool ServerSetPrice(int zone, int cents)
    {
        if (!IsServer || zone < 0 || zone >= zones.Count) return false;
        var z = zones[zone];
        int product = z.count > 0 ? z.itemId : z.pricedItem;
        if (product == 0) return false;
        z.pricedItem = product;
        z.price = Mathf.Max(0, cents);
        zones[zone] = z;
        return true;
    }

    /// <summary>The product a zone holds, or was priced for while it's empty (0 = neither).</summary>
    public int GetZoneProduct(int zone)
    {
        var z = GetZone(zone);
        return z.count > 0 ? z.itemId : z.pricedItem;
    }

    /// <summary>The zone nearest a point that holds a product or a price tag, or -1.</summary>
    public int FindNearestPriceableZone(Vector3 near)
    {
        int best = -1;
        float bestDist = float.MaxValue;
        for (int i = 0; i < ZoneCount; i++)
        {
            if (GetBox(i) == null || GetZoneProduct(i) == 0) continue;
            float d = DistanceToZone(i, near);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    /// <summary>Server: a copy of every zone (for edit mode).</summary>
    public ZoneStock[] ServerCopyStock()
    {
        var copy = new ZoneStock[zones.Count];
        for (int i = 0; i < copy.Length; i++) copy[i] = zones[i];
        return copy;
    }

    /// <summary>Server: what to put back in the zones once spawned. Call after Instantiate, before Spawn.</summary>
    public void ServerPrepareStock(ZoneStock[] contents) => pendingStock = contents;

    /* ========================= LAYOUT ========================= */

    private struct Layout
    {
        public int nx, ny, nz, capacity;
        public Vector3 cell;        // shelves: meters per product along the zone's x/y/z
        public float step;          // rails: meters between hangers
        public float railLength;    // rails: meters
        public bool railAlongZ;     // rails: the rail runs down the box's Z side (it's longer than X)
        public Vector3 hook;        // rails: the hang point from the pivot, already turned/tipped (rail frame)
        public Quaternion orient;   // product rotation relative to the zone
        public Vector3 pivotOffset; // from the product's pivot to the middle of its model (scaled, unrotated)
        public Vector3 half;        // product half size along the zone's axes, in this orientation
        public bool anchored;       // a part of a zone: lined up from its starting side (a whole zone is centred)
        public bool acrossDepth;    // filled front to back first, in slices (a zone split front to back)
        public int slots;           // places in it, covered or not (capacity: the ones nothing's put in the way of)
        public int[] order;         // shelves partly covered: the free places first, then the covered ones (null: in order)
    }

    private bool TryGetLayout(int zone, int itemId, int orient, out Layout layout)
    {
        layout = default;
        var box = GetBox(zone);
        if (box == null || !TryGetLayout(box, IsHangRail(zone), Entry(zone), itemId, orient, out layout)) return false;
        if (!IsHangRail(zone) && blockWhenCovered && IsSpawned) ApplyCover(zone, box, ref layout, itemId, orient);
        return true;
    }

    /* ========================= PARTLY COVERED SHELVES ========================= */

    // Something put on part of a shelf spot only takes that part: the places it's in the way of don't count
    // (products go in the free ones, in fill order). Worked out again whenever anything is placed or moved.
    private struct CoverKey { public int version, itemId, orient, capacity; public ushort from, to; public byte flags; }
    private readonly Dictionary<int, (CoverKey key, int[] order, int free)> coverCache = new();
    private static readonly Collider[] SlotHits = new Collider[64];

    private void ApplyCover(int zone, BoxCollider box, ref Layout l, int itemId, int orient)
    {
        var part = Entry(zone);
        var key = new CoverKey { version = ItemPickupNet.LayoutVersion, itemId = itemId, orient = orient, capacity = l.capacity,
                                 from = part.from, to = part.to, flags = part.flags };
        if (!coverCache.TryGetValue(zone, out var c) || !c.key.Equals(key))
        {
            var free = new List<int>();
            var covered = new List<int>();
            if (self == null) self = GetComponent<ItemPickupNet>();
            for (int i = 0; i < l.capacity; i++)
            {
                ShelfSlot(box, part, l, i, out Vector3 center, out Quaternion frame);
                (SlotInTheWay(center, l.half, frame) ? covered : free).Add(i);
            }
            int[] order = null;
            if (covered.Count > 0) { free.AddRange(covered); order = free.ToArray(); }
            c = (key, order, l.capacity - covered.Count);
            coverCache[zone] = c;
        }
        if (c.order == null) return;
        l.order = c.order;
        l.capacity = c.free;
    }

    // Another placed item where a product would go? (This furniture and its products, items on carts and
    // edit-mode copies don't count.)
    private bool SlotInTheWay(Vector3 center, Vector3 half, Quaternion frame)
    {
        Vector3 h = Vector3.Max(half * 0.85f, Vector3.one * 0.005f);
        int n = Physics.OverlapBoxNonAlloc(center, h, SlotHits, frame, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var col = SlotHits[i];
            if (col == null || col.transform.IsChildOf(transform)) continue;
            var item = col.GetComponentInParent<ItemPickupNet>();
            if (item == null || item == self || !item.IsSpawned || item.IsOnCart) continue;
            return true;
        }
        return false;
    }

    // Same result on every machine: it only depends on the prefab, the zone and the orientation.
    /// <summary>
    /// A zone's box as products see it: its middle, a turn whose up is world up, and its size along that
    /// turn's axes. Usually just the box. A box whose own up isn't up (a model imported turned, e.g.
    /// Blender's -90 on X, with the zone made under it) is stood upright the least turn needed, so
    /// products stand on its bottom and stack upward instead of filling it sideways.
    /// </summary>
    public static void ZoneFrame(BoxCollider box, out Vector3 center, out Quaternion rotation, out Vector3 size)
    {
        var t = box.transform;
        center = t.TransformPoint(box.center);
        Quaternion boxRot = t.rotation;
        Vector3 boxSize = ShipmentPacker.ZoneSize(box);
        Vector3 boxUp = boxRot * Vector3.up;
        if (Vector3.Dot(boxUp, Vector3.up) > 0.9f) { rotation = boxRot; size = boxSize; return; }

        rotation = Quaternion.FromToRotation(boxUp, Vector3.up) * boxRot;
        Vector3 bx = boxRot * Vector3.right, by = boxUp, bz = boxRot * Vector3.forward;
        float Along(Vector3 a) =>
            Mathf.Abs(Vector3.Dot(a, bx)) * boxSize.x + Mathf.Abs(Vector3.Dot(a, by)) * boxSize.y + Mathf.Abs(Vector3.Dot(a, bz)) * boxSize.z;
        size = new Vector3(Along(rotation * Vector3.right), Along(rotation * Vector3.up), Along(rotation * Vector3.forward));
    }

    private bool TryGetLayout(BoxCollider box, bool hang, ZoneStock part, int itemId, int orient, out Layout layout)
    {
        layout = default;
        var prefab = GetStockedPrefab(itemId, hang);
        if (box == null || prefab == null) return false;

        if (!hang) orient = AllowedOrient(itemId, orient);
        SegmentFrame(box, hang, part, out _, out _, out Vector3 size, out bool railAlongZ);
        Quaternion o = Orientation.Rotation(orient, hang);
        int max = Mathf.Max(0, maxPerZone);

        if (hang)
        {
            // Measured from the model's meshes (colliders often leave out the hook).
            // The rail frame's Z runs along the rail, so thickness along the rail is the turned model's Z.
            var model = GetHangShape(prefab);

            // Clothes on a rack: whichever way the model was built, its thinnest side (seen from above)
            // runs along the rail. R then turns it from there, T tips it first.
            Quaternion tip = Orientation.Lying(orient) ? Quaternion.Euler(-90f, 0f, 0f) : Quaternion.identity;
            Vector3 tipped = RotatedExtents(tip, model.half);
            float autoYaw = tipped.x < tipped.z - 1e-4f ? 90f : 0f;
            // Half turns only: hung across the rail like clothes on a hanger, whatever was picked.
            o = Quaternion.Euler(0f, (Orientation.Turns(orient) & 2) * 90f + autoYaw, 0f) * tip;
            Vector3 h = RotatedExtents(o, model.half);
            bool alongZ = railAlongZ;
            float length = alongZ ? size.z : size.x;
            float step = hangSpacing > 0f ? hangSpacing : h.z * 2f + spacing;
            int n = length <= h.z * 2f ? 1 : Mathf.FloorToInt((length - h.z * 2f) / step + 1e-4f) + 1;

            // Always hang from the top of the model as it hangs (after turning/tipping), unless it has a HangPoint.
            Vector3 c = o * model.center;
            Vector3 hook = model.hasHangPoint ? o * model.hook : new Vector3(c.x, c.y + h.y, c.z);
            if (length < h.z * 2f - 1e-4f) n = 0; // a part too short for even one
            if (clothesFitRailBox)
            {
                // Hung from the rail (the box's top): how far it drops below, and how wide it is across the rail.
                float drop = hook.y - (c.y - h.y);
                float across = alongZ ? size.x : size.z;
                if (drop > size.y + 1e-3f || h.x * 2f > across + 1e-3f) n = 0;
            }
            layout = new Layout { capacity = Mathf.Min(n, max), slots = Mathf.Min(n, max), step = step, orient = o, half = h,
                                  railLength = length, railAlongZ = alongZ, hook = hook, anchored = !part.Whole };
        }
        else
        {
            var product = ShipmentPacker.GetBox(prefab);
            Vector3 h = RotatedExtents(o, product.half);
            Vector3 cell = h * 2f + Vector3.one * spacing;
            int nx = Mathf.Max(0, Mathf.FloorToInt((size.x + spacing) / cell.x));
            int ny = Mathf.Max(0, Mathf.FloorToInt((size.y + spacing) / cell.y));
            int nz = Mathf.Max(0, Mathf.FloorToInt((size.z + spacing) / cell.z));
            layout = new Layout { nx = nx, ny = ny, nz = nz, capacity = Mathf.Min(nx * ny * nz, max),
                                  slots = Mathf.Min(nx * ny * nz, max), cell = cell,
                                  orient = o, pivotOffset = product.pivotOffset, half = h, anchored = !part.Whole,
                                  acrossDepth = part.AcrossDepth };
        }
        return layout.capacity > 0;
    }

    private struct HangShape
    {
        public Vector3 center;     // middle of the meshes from the pivot (scaled, unrotated)
        public Vector3 half;       // half size of the meshes (scaled, unrotated)
        public Vector3 hook;       // the HangPoint child from the pivot (scaled, unrotated)
        public bool hasHangPoint;
    }

    private static readonly Dictionary<GameObject, HangShape> HangShapes = new();

    // The hang point is a child named "HangPoint" if there is one, otherwise the top middle of the meshes.
    private static HangShape GetHangShape(GameObject prefab)
    {
        if (HangShapes.TryGetValue(prefab, out var shape)) return shape;

        Transform root = prefab.transform;
        Matrix4x4 toRoot = root.worldToLocalMatrix;
        Vector3 scale = root.localScale;
        bool any = false;
        Bounds b = default;

        void Add(Transform t, Bounds local)
        {
            Matrix4x4 m = toRoot * t.localToWorldMatrix;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = local.center + Vector3.Scale(local.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                Vector3 p = Vector3.Scale(m.MultiplyPoint3x4(corner), scale);
                if (!any) { b = new Bounds(p, Vector3.zero); any = true; }
                else b.Encapsulate(p);
            }
        }

        foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
            if (mf.sharedMesh != null) Add(mf.transform, mf.sharedMesh.bounds);
        foreach (var sk in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            Add(sk.transform, sk.localBounds);
        if (!any) b = new Bounds(Vector3.zero, Vector3.one * 0.3f);

        shape = new HangShape { center = b.center, half = b.extents };
        foreach (var t in prefab.GetComponentsInChildren<Transform>(true))
        {
            if (t.name != "HangPoint") continue;
            shape.hook = Vector3.Scale(toRoot.MultiplyPoint3x4(t.position), scale);
            shape.hasHangPoint = true;
            break;
        }

        HangShapes[prefab] = shape;
        return shape;
    }

    private static Vector3 RotatedExtents(Quaternion q, Vector3 half)
    {
        Vector3 x = q * new Vector3(half.x, 0f, 0f), y = q * new Vector3(0f, half.y, 0f), z = q * new Vector3(0f, 0f, half.z);
        return new Vector3(Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x),
                           Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y),
                           Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z));
    }

    // Shelves: column by column across the zone (from the Fill From Right side); each column front to back,
    // then up a layer. Rails: along the rail, the top of each product on the rail.
    private void GetSlotPose(BoxCollider box, bool hang, ZoneStock part, Layout l, int index, out Vector3 position,
                             out Quaternion rotation)
    {
        SegmentFrame(box, hang, part, out Vector3 zoneCenter, out Quaternion zoneRot, out Vector3 size, out _);

        if (hang)
        {
            // Hang points spaced along the rail (the box's top, down its longer side), centred on it; a part
            // of a rail lines them up from its start, so a part that's cut short keeps them where they are.
            float used = (l.capacity - 1) * l.step;
            float along = l.anchored ? -l.railLength * 0.5f + l.half.z + index * l.step : -used * 0.5f + index * l.step;
            Quaternion frame = l.railAlongZ ? zoneRot : zoneRot * Quaternion.Euler(0f, 90f, 0f); // frame Z = the rail
            Vector3 railPoint = zoneCenter + zoneRot * new Vector3(0f, size.y * 0.5f, 0f)
                              + frame * new Vector3(0f, 0f, along);
            rotation = frame * l.orient;
            position = railPoint - frame * l.hook;
            return;
        }
        // A partly covered shelf: the k-th product goes in the k-th free place.
        if (l.order != null && index >= 0 && index < l.order.Length) index = l.order[index];
        ShelfSlot(box, part, l, index, out Vector3 slotCenter, out zoneRot);
        rotation = zoneRot * l.orient;
        position = slotCenter - rotation * l.pivotOffset;
    }

    // Where a shelf place's product's middle is (its box lined up with the zone: Frame).
    private void ShelfSlot(BoxCollider box, ZoneStock part, Layout l, int index, out Vector3 center, out Quaternion frame)
    {
        SegmentFrame(box, false, part, out Vector3 zoneCenter, out Quaternion zoneRot, out Vector3 size, out _);
        Vector3 local;
        {
            // Column by column from the Fill From Right side: each column fills front to back (Fill From
            // Front), then stacks up a layer, before the next column starts. So a product takes up the width
            // it needs and no more, leaving the rest of the zone free for the next product (see WouldSplit).
            int col, row, layer;
            if (l.acrossDepth)
            {
                // Split front to back: slice by slice from the Fill From Front side, each slice across (from
                // the Fill From Right side) then stacked up, before the next slice back.
                int perSlice = Mathf.Max(1, l.nx * l.ny);
                row = index / perSlice;
                int inSlice = index % perSlice;
                layer = inSlice / Mathf.Max(1, l.nx);
                col = inSlice % Mathf.Max(1, l.nx);
            }
            else
            {
                int perColumn = Mathf.Max(1, l.nz * l.ny);
                col = index / perColumn;
                int inColumn = index % perColumn;
                layer = inColumn / Mathf.Max(1, l.nz);
                row = inColumn % Mathf.Max(1, l.nz);
            }

            // Centre the grid in the zone so leftover space is shared on both sides. A part of a zone lines
            // its columns up from its starting side instead (so cutting it short, or starting the next part
            // where it ends, moves nothing).
            float usedX = l.nx * l.cell.x - spacing;
            float usedZ = l.nz * l.cell.z - spacing;
            // Bottom layer first. A shopper facing the shelf (the zone's front, its blue arrow, toward them)
            // has their right at -x: Fill From Right starts there; Fill From Front starts at +z.
            float x = (l.anchored && !l.acrossDepth ? -size.x * 0.5f : -usedX * 0.5f) + l.half.x + col * l.cell.x;
            float z = (l.anchored && l.acrossDepth ? size.z * 0.5f : usedZ * 0.5f) - l.half.z - row * l.cell.z;
            local = new Vector3(fillFromRight ? x : -x, -size.y * 0.5f + l.half.y + layer * l.cell.y, fillFromFront ? z : -z);
        }

        frame = zoneRot;
        center = zoneCenter + zoneRot * local;
    }

    /* ========================= EDIT MODE STAND-INS ========================= */

    // Store edit mode works on plain copies of the world prefab (all scripts stripped), so these are
    // called on the PREFAB's StockContainer to find and fill the matching zones on such a copy.

    /// <summary>The copy's BoxColliders that match this prefab's zones (null where missing).</summary>
    public BoxCollider[] FindZonesOnCopy(Transform copyRoot)
    {
        var result = new BoxCollider[AuthoredCount];
        for (int i = 0; i < AuthoredCount; i++)
        {
            var box = AuthoredBox(i);
            if (box == null) continue;
            var t = FollowPath(copyRoot, PathFrom(transform, box.transform));
            result[i] = t != null ? t.GetComponent<BoxCollider>() : null;
        }
        return result;
    }

    /// <summary>
    /// The stock shown on an edit-mode copy. Drawn by StockBatcher with no object per product (a big
    /// store has tens of thousands); products that can't be batched get ordinary copies instead.
    /// Follow it when the copy moves; Clear it when the copy goes.
    /// </summary>
    public sealed class EditStock
    {
        internal readonly List<(StockBatcher.Copy copy, Matrix4x4 local)> batched = new();
        internal readonly List<GameObject> objects = new();

        /// <summary>The copy moved: draw its products where they are now.</summary>
        public void Follow(Transform copyRoot)
        {
            var frame = Matrix4x4.TRS(copyRoot.position, copyRoot.rotation, Vector3.one);
            foreach (var (copy, local) in batched) StockBatcher.MoveCopy(copy, frame * local);
        }

        public void Clear()
        {
            foreach (var (copy, _) in batched) StockBatcher.RemoveCopy(copy);
            batched.Clear();
            foreach (var o in objects) if (o != null) UnityEngine.Object.Destroy(o);
            objects.Clear();
        }
    }

    /// <summary>
    /// Lays out the stock on an edit-mode copy, so it moves with it (see EditStock). Nothing of it can be
    /// clicked, so clicks reach the copy.
    /// </summary>
    public EditStock BuildStockOnCopy(Transform copyRoot, ZoneStock[] stock)
    {
        var made = new EditStock();
        if (stock == null) return made;
        Transform holder = null; // only for products that can't be batched
        var frameInv = Matrix4x4.TRS(copyRoot.position, copyRoot.rotation, Vector3.one).inverse;
        var copyZones = FindZonesOnCopy(copyRoot);
        for (int i = 0; i < stock.Length; i++)
        {
            var part = stock[i];
            int b = BoxIndexOf(i, part); // (a part of a zone: that zone's box)
            if (part.Dead || part.count <= 0 || b < 0 || b >= copyZones.Length) continue;
            var box = copyZones[b];
            bool hang = b >= ShelfCount;
            var prefab = GetStockedPrefab(part.itemId, hang);
            uint paint = part.paint;
            if (box == null || prefab == null || !TryGetLayout(box, hang, part, part.itemId, part.orient, out var layout)) continue;

            for (int k = 0; k < part.count && k < layout.capacity; k++)
            {
                GetSlotPose(box, hang, part, layout, k, out Vector3 pos, out Quaternion rot);
                var model = GetVariantPrefab(part.itemId, hang, part.seed, k) ?? prefab;
                var template = BatchedTemplateFor(model, out var shape);
                shape = PaintedShape(model, shape, paint);
                if (shape != null)
                {
                    var world = Matrix4x4.TRS(pos, rot, template.transform.localScale);
                    made.batched.Add((StockBatcher.AddCopy(shape, world, editView: true), frameInv * world));
                    continue;
                }
                if (holder == null)
                {
                    holder = StockVisualFollower.Create(copyRoot, null).transform;
                    made.objects.Add(holder.gameObject);
                }
                var go = MakeVisual(model, pos, rot, holder, paint: paint);
                foreach (var c in go.GetComponentsInChildren<Collider>()) c.enabled = false;
                made.objects.Add(go);
            }
        }
        return made;
    }

    /// <summary>
    /// This player's screen only: show or hide this shelf's own (non-batched) stock objects, colliders
    /// included (edit mode hides the real items while their copies are edited). Batched stock is hidden
    /// by StockBatcher.Paused.
    /// </summary>
    public void SetLocalVisualsShown(bool shown)
    {
        if (visualRoot != null) visualRoot.gameObject.SetActive(shown);
    }

    private static List<int> PathFrom(Transform root, Transform target)
    {
        var path = new List<int>();
        for (var t = target; t != null && t != root; t = t.parent) path.Insert(0, t.GetSiblingIndex());
        return path;
    }

    private static Transform FollowPath(Transform root, List<int> path)
    {
        var t = root;
        foreach (int index in path)
        {
            if (index >= t.childCount) return null;
            t = t.GetChild(index);
        }
        return t;
    }

    /* ========================= VISUALS ========================= */

    private void OnZonesChanged(NetworkListEvent<ZoneStock> change)
    {
        // Only this spot's entry in its store's listing changes (and this shelf itself, which is only for
        // sale while it's empty).
        if (change.Type == NetworkListEvent<ZoneStock>.EventType.Value)
        {
            StoreListing.Track(this, change.Index);
            if (TryGetComponent<Sellable>(out var self)) StoreListing.Track(self);
            RefreshVisuals(change.Index);
        }
        else
        {
            StoreListing.Track(gameObject);
            for (int i = 0; i < ZoneCount; i++) RefreshVisuals(i);
        }
    }

    private void RefreshVisuals(int zone)
    {
        if (zone < 0 || zone >= ZoneCount) return;
        SetVisuals(zone, GetZone(zone));
    }

    // Adds or removes models at the end, so buying one only removes one. A different product,
    // orientation or place in the zone (a part cut short when the next one started) rebuilds it.
    private void SetVisuals(int zone, ZoneStock state)
    {
        while (visuals.Count <= zone) { visuals.Add(new List<GameObject>()); visualState.Add(default); }
        var list = visuals[zone];
        var shown = visualState[zone];
        if (shown.itemId != state.itemId || shown.orient != state.orient || shown.seed != state.seed ||
            shown.paint != state.paint || shown.from != state.from || shown.to != state.to || shown.box != state.box ||
            shown.flags != state.flags)
        {
            foreach (var go in list) if (go != null) Destroy(go);
            list.Clear();
        }
        visualState[zone] = state;

        while (list.Count > Mathf.Max(0, state.count))
        {
            int last = list.Count - 1;
            if (list[last] != null) Destroy(list[last]);
            list.RemoveAt(last);
        }

        if (list.Count >= state.count) return;
        var prefab = GetStockedPrefab(state.itemId, IsHangRail(zone));
        var box = GetBox(zone);
        if (prefab == null || !TryGetLayout(zone, state.itemId, state.orient, out var layout)) return;

        var root = GetVisualRoot();
        while (list.Count < state.count && list.Count < layout.slots) // (stock already there when something was put in the way stays)
        {
            GetSlotPose(box, IsHangRail(zone), state, layout, list.Count, out Vector3 pos, out Quaternion rot);
            var model = GetVariantPrefab(state.itemId, IsHangRail(zone), state.seed, list.Count) ?? prefab;
            var product = MakeVisual(model, pos, rot, root, batched: true, paint: state.paint);
            ShelfStockLayer.Apply(product); // players walk through it (the shelf is what blocks them)
            list.Add(product);
        }
    }

    private static Transform builder;
    private static readonly Dictionary<GameObject, GameObject> Templates = new();

    /// <summary>
    /// A plain copy of the prefab: looks and one box collider (for looking at it), no scripts, physics
    /// or networking. Each prefab is stripped once; after that a copy is a single Instantiate.
    /// </summary>
    public static GameObject MakeVisual(GameObject prefab, Vector3 position, Quaternion rotation, Transform parent,
                                       bool batched = false, uint paint = 0)
    {
        List<StockBatcher.PartShape> shape = null;
        var template = batched ? BatchedTemplateFor(prefab, out shape) : TemplateFor(prefab);
        var go = Instantiate(template, position, rotation, parent); // parent is unscaled, so the prefab keeps its size
        go.name = prefab.name;
        if (batched && shape != null) shape = PaintedShape(prefab, shape, paint);
        else if (paint != 0 || PaintParts.HasOriginalColors(prefab)) PaintParts.ApplySetToCopy(prefab, go, paint); // drawn by its own renderers
        if (batched && shape != null)
        {
            // Drawn with the rest of its kind nearby (StockBatcher); its own renderers are off.
            var model = go.AddComponent<StockModel>();
            model.Shape = shape;
            StockBatcher.Add(model);
        }
        return go;
    }

    // Painted products: the same parts with the color on their materials (one color per product: the
    // buy menu paints every zone the same). Cached per shape and color. Only prefabs with PaintParts.
    private static readonly Dictionary<(List<StockBatcher.PartShape>, byte), List<StockBatcher.PartShape>> Painted = new();

    private static List<StockBatcher.PartShape> PaintedShape(GameObject prefab, List<StockBatcher.PartShape> shape, uint paint)
    {
        byte color = PaintSet.Get(paint, 0);
        if (shape == null || !PaintParts.IsPaintable(prefab)) return shape;
        Color original = default;
        if (color == 0)
        {
            var zone = prefab.GetComponentInChildren<PaintParts>(true);
            if (zone == null || !zone.TryGetOriginalColor(out original)) return shape; // its own colors
        }
        if (Painted.TryGetValue((shape, color), out var done)) return done;
        done = color == 0 ? StockBatcher.Tinted(shape, original) : StockBatcher.Tinted(shape, color);
        Painted[(shape, color)] = done;
        return done;
    }

    // Shelf stock: the template with its renderers off, and the parts StockBatcher draws instead (null
    // parts: it can't be batched, and keeps its renderers).
    private static readonly Dictionary<GameObject, (GameObject template, List<StockBatcher.PartShape> shape)> BatchedTemplates = new();

    private static GameObject BatchedTemplateFor(GameObject prefab, out List<StockBatcher.PartShape> shape)
    {
        if (BatchedTemplates.TryGetValue(prefab, out var entry) && entry.template != null)
        {
            shape = entry.shape;
            return entry.template;
        }
        var t = Instantiate(TemplateFor(prefab), builder);
        t.name = prefab.name + " (batched stock template)";
        t.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
        shape = StockBatcher.TakeShape(t);
        if (shape != null) StripToCollider(t);
        BatchedTemplates[prefab] = (t, shape);
        return t;
    }

    // A batched product is drawn by StockBatcher from its shape, so all a copy needs is its transform and
    // the one box collider on the root (for looking at it). The mesh parts (a renderer, a mesh filter and
    // often a child object each) would otherwise be copied into every product on every shelf: with a full
    // mall that was ~260k renderers and filters and ~115k extra objects, a few hundred MB.
    private static void StripToCollider(GameObject template)
    {
        var root = template.transform;
        for (int i = root.childCount - 1; i >= 0; i--) DestroyImmediate(root.GetChild(i).gameObject);
        if (template.TryGetComponent<MeshRenderer>(out var r)) DestroyImmediate(r);
        if (template.TryGetComponent<MeshFilter>(out var f)) DestroyImmediate(f);
    }

    // The stripped copy every product of this prefab is copied from (kept under a hidden, switched-off
    // object, so nothing on it ever runs).
    private static GameObject TemplateFor(GameObject prefab)
    {
        if (Templates.TryGetValue(prefab, out var t) && t != null) return t;
        if (builder == null)
        {
            var b = new GameObject("[StockVisualTemplates]");
            b.SetActive(false);
            b.hideFlags = HideFlags.HideInHierarchy;
            DontDestroyOnLoad(b);
            builder = b.transform;
        }

        t = Instantiate(prefab, builder);
        t.name = prefab.name + " (stock template)";
        t.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
        bool hadCollider = TryGetColliderBounds(t, out Bounds box);
        DestroyAll<NetworkBehaviour>(t);
        DestroyAll<MonoBehaviour>(t);
        DestroyAll<NetworkObject>(t);
        DestroyAll<Rigidbody>(t);
        DestroyAll<AudioSource>(t);
        DestroyAll<Collider>(t);

        // One box around what the colliders covered (the model, if it had none): cheaper for physics than
        // a copy of every collider, and enough to look at, price or click it.
        if (hadCollider || TryGetMeshBounds(t, out box))
        {
            var c = t.AddComponent<BoxCollider>();
            c.center = box.center;
            c.size = Vector3.Max(box.size, Vector3.one * 0.01f);
        }
        Templates[prefab] = t;
        return t;
    }

    // Bounds of the solid colliders, in the root's own (unscaled) space. Read from their shapes, since
    // switched-off objects have no world bounds.
    private static bool TryGetColliderBounds(GameObject root, out Bounds bounds)
    {
        bounds = default;
        bool any = false;
        var toRoot = root.transform.worldToLocalMatrix;
        foreach (var c in root.GetComponentsInChildren<Collider>(true))
        {
            if (c.isTrigger || !c.enabled) continue;
            Bounds local;
            switch (c)
            {
                case BoxCollider b: local = new Bounds(b.center, b.size); break;
                case SphereCollider sp: local = new Bounds(sp.center, Vector3.one * sp.radius * 2f); break;
                case CapsuleCollider cap:
                    var size = Vector3.one * cap.radius * 2f;
                    size[cap.direction] = Mathf.Max(cap.height, cap.radius * 2f);
                    local = new Bounds(cap.center, size);
                    break;
                case MeshCollider mc when mc.sharedMesh != null: local = mc.sharedMesh.bounds; break;
                default: continue;
            }
            Encapsulate(ref bounds, ref any, toRoot * c.transform.localToWorldMatrix, local);
        }
        return any;
    }

    private static bool TryGetMeshBounds(GameObject root, out Bounds bounds)
    {
        bounds = default;
        bool any = false;
        var toRoot = root.transform.worldToLocalMatrix;
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            if (mf.sharedMesh != null) Encapsulate(ref bounds, ref any, toRoot * mf.transform.localToWorldMatrix, mf.sharedMesh.bounds);
        foreach (var sk in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            Encapsulate(ref bounds, ref any, toRoot * sk.transform.localToWorldMatrix, sk.localBounds);
        return any;
    }

    private static void Encapsulate(ref Bounds bounds, ref bool any, Matrix4x4 m, Bounds local)
    {
        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = local.center + Vector3.Scale(local.extents,
                new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
            Vector3 p = m.MultiplyPoint3x4(corner);
            if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; }
            else bounds.Encapsulate(p);
        }
    }

    private static void DestroyAll<T>(GameObject root) where T : Component
    {
        var comps = root.GetComponentsInChildren<T>(true);
        for (int i = comps.Length - 1; i >= 0; i--)
            if (comps[i] != null) DestroyImmediate(comps[i]);
    }

    // Zones are triggers so they never block placement or bump into things (edit mode keeps colliders).
    private void OnValidate()
    {
        if (stockZones != null) foreach (var box in stockZones) if (box != null) box.isTrigger = true;
        if (hangRails != null) foreach (var box in hangRails) if (box != null) box.isTrigger = true;
    }

    private void OnDrawGizmosSelected()
    {
        if (stockZones != null)
        {
            Gizmos.color = new Color(0.3f, 0.9f, 1f, 0.8f);
            foreach (var box in stockZones)
            {
                if (box == null) continue;
                Gizmos.matrix = box.transform.localToWorldMatrix;
                Gizmos.DrawWireCube(box.center, box.size);
                Gizmos.DrawLine(box.center, box.center + Vector3.forward * box.size.z * 0.5f); // front
            }
        }
        if (hangRails != null)
        {
            foreach (var box in hangRails)
            {
                if (box == null) continue;
                Gizmos.matrix = box.transform.localToWorldMatrix;
                Gizmos.color = new Color(1f, 0.7f, 0.3f, 0.5f);
                Gizmos.DrawWireCube(box.center, box.size);
                Gizmos.color = new Color(1f, 0.7f, 0.3f, 1f);
                Vector3 top = box.center + Vector3.up * box.size.y * 0.5f;
                Vector3 s = Vector3.Scale(box.size, box.transform.lossyScale);
                Vector3 rail = s.z > s.x ? Vector3.forward * box.size.z * 0.5f : Vector3.right * box.size.x * 0.5f;
                Gizmos.DrawLine(top - rail, top + rail); // the rail
            }
        }
        Gizmos.matrix = Matrix4x4.identity;
    }
}
