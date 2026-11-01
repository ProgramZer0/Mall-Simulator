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
/// product's front (blue arrow) points along the rail, like clothes on a real rack; R turns it, T tips
/// it 90 degrees (for a model built lying flat). Only the rail's length matters; clothes can hang
/// below the box. Set Hang Spacing to squeeze hangers closer than their model's thickness.
///
/// Each zone holds one kind of product, in one orientation (which way it faces, and on shelves
/// whether it stands up or lies down). The player picks it with R / T before stocking an empty
/// zone, and can change it later if everything still fits. How many fit is worked out from the
/// product's size in that orientation.
///
/// Over the network each zone is just (item id, count, orientation); every machine lays out the
/// models itself. Zone colliders are made triggers and switched off at runtime (authoring only).
/// </summary>
public class StockContainer : NetworkBehaviour
{
    /// <summary>What's in one zone.</summary>
    public struct ZoneStock : INetworkSerializeByMemcpy, IEquatable<ZoneStock>
    {
        public int itemId; // 0 = empty
        public int count;
        public int orient; // see Orientation

        public bool Equals(ZoneStock other) => itemId == other.itemId && count == other.count && orient == other.orient;
        public override bool Equals(object obj) => obj is ZoneStock other && Equals(other);
        public override int GetHashCode() => (itemId * 397 ^ count) * 31 + orient;
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
             "by the top of their Hang Prefab (or a child named HangPoint). Only the length matters.")]
    [SerializeField] private BoxCollider[] hangRails;
    [Tooltip("Space left between products on shelves, in meters.")]
    [SerializeField] private float spacing = 0.01f;
    [Tooltip("Distance between hangers on a rail, in meters. 0 = the product's own thickness plus Spacing.")]
    [SerializeField] private float hangSpacing = 0f;
    [Tooltip("Never more than this many products in one zone, however small they are.")]
    [SerializeField] private int maxPerZone = 100;

    /// <summary>Every spawned container on this machine (customers can search these for stock).</summary>
    public static readonly List<StockContainer> All = new();

    private NetworkList<ZoneStock> zones;
    private List<GameObject>[] visuals;
    private ZoneStock[] visualState;
    private ZoneStock[] pendingStock; // server: contents to restore when spawned (edit mode)
    private StockVisualFollower visualRoot; // unscaled holder that follows this furniture

    private int ShelfCount => stockZones != null ? stockZones.Length : 0;
    private int RailCount => hangRails != null ? hangRails.Length : 0;

    /// <summary>Shelf zones first, then hang rails.</summary>
    public int ZoneCount => ShelfCount + RailCount;

    /// <summary>Is this zone a hang rail (not a shelf space)?</summary>
    public bool IsHangRail(int zone) => zone >= ShelfCount && zone < ZoneCount;

    private BoxCollider GetBox(int zone) =>
        zone < 0 ? null : zone < ShelfCount ? stockZones[zone] : zone < ZoneCount ? hangRails[zone - ShelfCount] : null;

    private void Awake()
    {
        zones = new NetworkList<ZoneStock>(null, NetworkVariableReadPermission.Everyone,
                                           NetworkVariableWritePermission.Server);
        visuals = new List<GameObject>[ZoneCount];
        visualState = new ZoneStock[ZoneCount];
        for (int i = 0; i < ZoneCount; i++)
        {
            visuals[i] = new List<GameObject>();
            var box = GetBox(i);
            if (box != null) box.enabled = false; // authoring only
        }
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            zones.Clear();
            for (int i = 0; i < ZoneCount; i++)
                zones.Add(pendingStock != null && i < pendingStock.Length ? pendingStock[i] : default);
            pendingStock = null;
        }

        zones.OnListChanged += OnZonesChanged;
        for (int i = 0; i < ZoneCount; i++) RefreshVisuals(i);
        All.Add(this);
    }

    public override void OnNetworkDespawn()
    {
        All.Remove(this);
        zones.OnListChanged -= OnZonesChanged;
        for (int i = 0; i < ZoneCount; i++) SetVisuals(i, default);
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
        visualRoot.Sync();
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
            foreach (var z in zones) if (z.count > 0) return true;
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

    /// <summary>How many of this product fit in a zone in that orientation (0 if it can't go there).</summary>
    public int GetCapacity(int zone, int itemId, int orient) =>
        Accepts(zone, itemId) && TryGetLayout(zone, itemId, OrientFor(zone, itemId, orient), out var l) ? l.capacity : 0;

    /// <summary>Can one more of this product go in this zone? (orient: the pick, if the zone is empty.)</summary>
    public bool HasRoomFor(int zone, int itemId, int orient)
    {
        var z = GetZone(zone);
        if (z.count > 0 && z.itemId != itemId) return false;
        return Mathf.Max(0, z.count) < GetCapacity(zone, itemId, orient);
    }

    /// <summary>
    /// The zone nearest a world position that can take one more of this product (wantStock false;
    /// orient = the pick for an empty zone), or that holds some of it (wantStock true; itemId 0 = any
    /// product). -1 if none.
    /// </summary>
    public int FindNearestZone(Vector3 near, int itemId, bool wantStock, int orient = 0)
    {
        int best = -1;
        float bestDist = float.MaxValue;
        for (int i = 0; i < ZoneCount; i++)
        {
            if (GetBox(i) == null) continue;
            var z = GetZone(i);
            bool ok = wantStock ? z.count > 0 && (itemId == 0 || z.itemId == itemId) : HasRoomFor(i, itemId, orient);
            if (!ok) continue;

            float d = DistanceToZone(i, near);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
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
    public bool TryGetNextSlot(int zone, int itemId, int orient, out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = default;
        var z = GetZone(zone);
        int index = z.count > 0 && z.itemId == itemId ? z.count : 0;
        if (!Accepts(zone, itemId) || !TryGetLayout(zone, itemId, OrientFor(zone, itemId, orient), out var layout) ||
            index >= layout.capacity) return false;
        GetSlotPose(GetBox(zone), IsHangRail(zone), layout, index, out position, out rotation);
        return true;
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

    private static ItemPickupNet GetCratePickup(int itemId)
    {
        if (itemId == 0 || ItemCatalog.Instance == null || !ItemCatalog.Instance.TryGet(itemId, out var def) ||
            def.worldNetworkPrefab == null) return null;
        var pickup = def.worldNetworkPrefab.GetComponent<ItemPickupNet>();
        return pickup != null && pickup.IsStockCrate && (pickup.ShelfPrefab != null || pickup.HangPrefab != null)
            ? pickup : null;
    }

    /// <summary>The middle of a zone, in the world.</summary>
    public Vector3 GetZoneCenter(int zone)
    {
        var box = GetBox(zone);
        return box != null ? box.transform.TransformPoint(box.center) : transform.position;
    }

    private float DistanceToZone(int zone, Vector3 point)
    {
        var box = GetBox(zone);
        Vector3 local = box.transform.InverseTransformPoint(point) - box.center;
        Vector3 half = box.size * 0.5f;
        Vector3 clamped = new(Mathf.Clamp(local.x, -half.x, half.x), Mathf.Clamp(local.y, -half.y, half.y),
                              Mathf.Clamp(local.z, -half.z, half.z));
        return Vector3.Distance(point, box.transform.TransformPoint(clamped + box.center));
    }

    /* ========================= SERVER ========================= */

    /// <summary>
    /// Server: put one product in a zone. An empty zone takes the given orientation; otherwise the
    /// zone keeps its own. False if it's full, holds something else, or doesn't take this product.
    /// </summary>
    public bool ServerAddOne(int zone, int itemId, int orient)
    {
        if (!IsServer || zone < 0 || zone >= zones.Count || !HasRoomFor(zone, itemId, orient)) return false;
        var z = zones[zone];
        bool same = z.count > 0 && z.itemId == itemId;
        zones[zone] = new ZoneStock
        {
            itemId = itemId,
            count = (same ? z.count : 0) + 1,
            orient = OrientFor(zone, itemId, orient),
        };
        return true;
    }

    /// <summary>Server: change how a zone's products sit. False if they wouldn't all fit that way.</summary>
    public bool ServerSetOrientation(int zone, int orient)
    {
        if (!IsServer || zone < 0 || zone >= zones.Count) return false;
        var z = zones[zone];
        if (z.count <= 0) return false;
        if (!TryGetLayout(zone, z.itemId, orient, out var layout) || layout.capacity < z.count) return false;
        zones[zone] = new ZoneStock { itemId = z.itemId, count = z.count, orient = orient };
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
        zones[zone] = z.count == 1 ? default : new ZoneStock { itemId = z.itemId, count = z.count - 1, orient = z.orient };
        return z.itemId;
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
    }

    private bool TryGetLayout(int zone, int itemId, int orient, out Layout layout) =>
        TryGetLayout(GetBox(zone), IsHangRail(zone), itemId, orient, out layout);

    // Same result on every machine: it only depends on the prefab, the zone and the orientation.
    private bool TryGetLayout(BoxCollider box, bool hang, int itemId, int orient, out Layout layout)
    {
        layout = default;
        var prefab = GetStockedPrefab(itemId, hang);
        if (box == null || prefab == null) return false;

        Vector3 size = ShipmentPacker.ZoneSize(box);
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
            o = Quaternion.Euler(0f, Orientation.Turns(orient) * 90f + autoYaw, 0f) * tip;
            Vector3 h = RotatedExtents(o, model.half);
            bool alongZ = size.z > size.x;
            float length = alongZ ? size.z : size.x;
            float step = hangSpacing > 0f ? hangSpacing : h.z * 2f + spacing;
            int n = length <= h.z * 2f ? 1 : Mathf.FloorToInt((length - h.z * 2f) / step + 1e-4f) + 1;

            // Always hang from the top of the model as it hangs (after turning/tipping), unless it has a HangPoint.
            Vector3 c = o * model.center;
            Vector3 hook = model.hasHangPoint ? o * model.hook : new Vector3(c.x, c.y + h.y, c.z);
            layout = new Layout { capacity = Mathf.Min(n, max), step = step, orient = o, half = h,
                                  railLength = length, railAlongZ = alongZ, hook = hook };
        }
        else
        {
            var product = ShipmentPacker.GetBox(prefab);
            Vector3 h = RotatedExtents(o, product.half);
            Vector3 cell = h * 2f + Vector3.one * spacing;
            int nx = Mathf.Max(0, Mathf.FloorToInt((size.x + spacing) / cell.x));
            int ny = Mathf.Max(0, Mathf.FloorToInt((size.y + spacing) / cell.y));
            int nz = Mathf.Max(0, Mathf.FloorToInt((size.z + spacing) / cell.z));
            layout = new Layout { nx = nx, ny = ny, nz = nz, capacity = Mathf.Min(nx * ny * nz, max), cell = cell,
                                  orient = o, pivotOffset = product.pivotOffset, half = h };
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

    // Shelves: front row first (the zone's +Z side), left to right, then the next row back, then up a layer.
    // Rails: left to right along the rail, the top of each product on the rail.
    private void GetSlotPose(BoxCollider box, bool hang, Layout l, int index, out Vector3 position, out Quaternion rotation)
    {
        Vector3 size = ShipmentPacker.ZoneSize(box);
        Quaternion zoneRot = box.transform.rotation;
        Vector3 local;

        if (hang)
        {
            // Hang points spaced along the rail (the box's top, down its longer side), centred on it.
            float used = (l.capacity - 1) * l.step;
            float along = -used * 0.5f + index * l.step;
            Quaternion frame = l.railAlongZ ? zoneRot : zoneRot * Quaternion.Euler(0f, 90f, 0f); // frame Z = the rail
            Vector3 railPoint = box.transform.TransformPoint(box.center) + zoneRot * new Vector3(0f, size.y * 0.5f, 0f)
                              + frame * new Vector3(0f, 0f, along);
            rotation = frame * l.orient;
            position = railPoint - frame * l.hook;
            return;
        }
        else
        {
            int perLayer = l.nx * l.nz;
            int layer = index / perLayer;
            int inLayer = index % perLayer;
            int row = inLayer / l.nx;
            int col = inLayer % l.nx;

            // Centre the grid in the zone so leftover space is shared on both sides.
            float usedX = l.nx * l.cell.x - spacing;
            float usedZ = l.nz * l.cell.z - spacing;
            local = new Vector3(
                -usedX * 0.5f + l.half.x + col * l.cell.x,
                -size.y * 0.5f + l.half.y + layer * l.cell.y,
                usedZ * 0.5f - l.half.z - row * l.cell.z);
        }

        rotation = zoneRot * l.orient;
        Vector3 center = box.transform.TransformPoint(box.center) + zoneRot * local;
        position = center - rotation * l.pivotOffset;
    }

    /* ========================= EDIT MODE STAND-INS ========================= */

    // Store edit mode works on plain copies of the world prefab (all scripts stripped), so these are
    // called on the PREFAB's StockContainer to find and fill the matching zones on such a copy.

    /// <summary>The copy's BoxColliders that match this prefab's zones (null where missing).</summary>
    public BoxCollider[] FindZonesOnCopy(Transform copyRoot)
    {
        var result = new BoxCollider[ZoneCount];
        for (int i = 0; i < ZoneCount; i++)
        {
            var box = GetBox(i);
            if (box == null) continue;
            var t = FollowPath(copyRoot, PathFrom(transform, box.transform));
            result[i] = t != null ? t.GetComponent<BoxCollider>() : null;
        }
        return result;
    }

    /// <summary>
    /// Lays out the stock models on an edit-mode copy, under a holder that follows it so they move
    /// with it. Their colliders are off so clicks reach the copy. Returns what was made (destroy it
    /// all to clear).
    /// </summary>
    public List<GameObject> BuildStockOnCopy(Transform copyRoot, int[] itemIds, int[] counts, int[] orients)
    {
        var made = new List<GameObject>();
        var holder = StockVisualFollower.Create(copyRoot, null);
        made.Add(holder.gameObject);
        var copyZones = FindZonesOnCopy(copyRoot);
        for (int i = 0; i < copyZones.Length && i < itemIds.Length && i < counts.Length; i++)
        {
            var box = copyZones[i];
            var prefab = GetStockedPrefab(itemIds[i], IsHangRail(i));
            int orient = orients != null && i < orients.Length ? orients[i] : 0;
            if (box == null || prefab == null || counts[i] <= 0 ||
                !TryGetLayout(box, IsHangRail(i), itemIds[i], orient, out var layout)) continue;

            for (int k = 0; k < counts[i] && k < layout.capacity; k++)
            {
                GetSlotPose(box, IsHangRail(i), layout, k, out Vector3 pos, out Quaternion rot);
                var model = MakeVisual(prefab, pos, rot, holder.transform);
                foreach (var c in model.GetComponentsInChildren<Collider>()) c.enabled = false;
                made.Add(model);
            }
        }
        return made;
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
        if (change.Type == NetworkListEvent<ZoneStock>.EventType.Value) RefreshVisuals(change.Index);
        else for (int i = 0; i < ZoneCount; i++) RefreshVisuals(i);
    }

    private void RefreshVisuals(int zone)
    {
        if (zone < 0 || zone >= ZoneCount) return;
        SetVisuals(zone, GetZone(zone));
    }

    // Adds or removes models at the end, so buying one only removes one. A different product or
    // orientation rebuilds the zone.
    private void SetVisuals(int zone, ZoneStock state)
    {
        var list = visuals[zone];
        var shown = visualState[zone];
        if (shown.itemId != state.itemId || shown.orient != state.orient)
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
        while (list.Count < state.count && list.Count < layout.capacity)
        {
            GetSlotPose(box, IsHangRail(zone), layout, list.Count, out Vector3 pos, out Quaternion rot);
            list.Add(MakeVisual(prefab, pos, rot, root));
        }
    }

    private static Transform builder;

    /// <summary>A plain copy of the prefab: looks and colliders only, no scripts, physics or networking.</summary>
    private static GameObject MakeVisual(GameObject prefab, Vector3 position, Quaternion rotation, Transform parent)
    {
        if (builder == null)
        {
            var b = new GameObject("[StockVisualBuilder]");
            b.SetActive(false); // nothing on the copy wakes up while we strip it
            b.hideFlags = HideFlags.HideInHierarchy;
            DontDestroyOnLoad(b);
            builder = b.transform;
        }

        var go = Instantiate(prefab, builder);
        DestroyAll<NetworkBehaviour>(go);
        DestroyAll<MonoBehaviour>(go);
        DestroyAll<NetworkObject>(go);
        DestroyAll<Rigidbody>(go);
        DestroyAll<AudioSource>(go);

        go.transform.SetParent(parent, true); // parent is unscaled, so the prefab keeps its own size and shape
        go.transform.SetPositionAndRotation(position, rotation);
        return go;
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
