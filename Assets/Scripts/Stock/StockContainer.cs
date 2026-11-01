using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Something that holds stock: a shelf, rack, table, fridge... Put it on the furniture's world
/// prefab (next to its NetworkObject and ItemPickupNet) and add Stock Zones: BoxColliders on child
/// objects marking each space products go in. Add as many as you like, side by side or apart.
///
/// Each zone holds one kind of product. How many fit is worked out from the product's size: they're
/// lined up in a grid inside the box (turned 90 degrees if that fits more), stacked if the box is
/// tall enough. Products face the zone's forward (blue arrow) and fill from the front row back.
///
/// Hold a stock crate and press F to put one product in the zone nearest where you're looking;
/// G puts one back into your crate; F with empty hands takes one. Over the network each zone is
/// just (item id, count); every machine lays out the models itself. The zone colliders are made
/// triggers automatically and switched off at runtime, so they're only for authoring.
/// </summary>
public class StockContainer : NetworkBehaviour
{
    /// <summary>What's in one zone.</summary>
    public struct ZoneStock : INetworkSerializeByMemcpy, IEquatable<ZoneStock>
    {
        public int itemId; // 0 = empty
        public int count;

        public bool Equals(ZoneStock other) => itemId == other.itemId && count == other.count;
        public override bool Equals(object obj) => obj is ZoneStock other && Equals(other);
        public override int GetHashCode() => itemId * 397 ^ count;
    }

    [Tooltip("BoxColliders marking where products go. Each holds one kind of product; products face its blue arrow.")]
    [SerializeField] private BoxCollider[] stockZones;
    [Tooltip("Space left between products, in meters.")]
    [SerializeField] private float spacing = 0.01f;
    [Tooltip("Never more than this many products in one zone, however small they are.")]
    [SerializeField] private int maxPerZone = 100;

    /// <summary>Every spawned container on this machine (customers can search these for stock).</summary>
    public static readonly List<StockContainer> All = new();

    private NetworkList<ZoneStock> zones;
    private List<GameObject>[] visuals;
    private int[] visualItem;
    private ZoneStock[] pendingStock; // server: contents to restore when spawned (edit mode)

    public int ZoneCount => stockZones != null ? stockZones.Length : 0;

    private void Awake()
    {
        zones = new NetworkList<ZoneStock>(null, NetworkVariableReadPermission.Everyone,
                                           NetworkVariableWritePermission.Server);
        visuals = new List<GameObject>[ZoneCount];
        visualItem = new int[ZoneCount];
        for (int i = 0; i < ZoneCount; i++)
        {
            visuals[i] = new List<GameObject>();
            if (stockZones[i] != null) stockZones[i].enabled = false; // authoring only
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
        for (int i = 0; i < ZoneCount; i++) SetVisualCount(i, 0, 0);
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

    /// <summary>How many of this product fit in a zone (0 if it isn't a stock crate or doesn't fit).</summary>
    public int GetCapacity(int zone, int itemId) => TryGetLayout(zone, itemId, out var l) ? l.capacity : 0;

    /// <summary>Can one more of this product go in this zone?</summary>
    public bool HasRoomFor(int zone, int itemId)
    {
        var z = GetZone(zone);
        if (z.count > 0 && z.itemId != itemId) return false;
        return Mathf.Max(0, z.count) < GetCapacity(zone, itemId);
    }

    /// <summary>
    /// The zone nearest a world position that can take one more of this product (wantStock false),
    /// or that holds some of it (wantStock true; itemId 0 = any product). -1 if none.
    /// </summary>
    public int FindNearestZone(Vector3 near, int itemId, bool wantStock)
    {
        int best = -1;
        float bestDist = float.MaxValue;
        for (int i = 0; i < ZoneCount; i++)
        {
            if (stockZones[i] == null) continue;
            var z = GetZone(i);
            bool ok = wantStock ? z.count > 0 && (itemId == 0 || z.itemId == itemId) : HasRoomFor(i, itemId);
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
    public bool TryGetNextSlot(int zone, int itemId, out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = default;
        var z = GetZone(zone);
        int index = z.count > 0 && z.itemId == itemId ? z.count : 0;
        if (!TryGetLayout(zone, itemId, out var layout) || index >= layout.capacity) return false;
        GetSlotPose(zone, layout, index, out position, out rotation);
        return true;
    }

    /// <summary>Where the last product in a zone sits (the one that would be taken next).</summary>
    public bool TryGetLastSlot(int zone, out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = default;
        var z = GetZone(zone);
        if (z.count <= 0 || !TryGetLayout(zone, z.itemId, out var layout)) return false;
        GetSlotPose(zone, layout, Mathf.Min(z.count, layout.capacity) - 1, out position, out rotation);
        return true;
    }

    /// <summary>Is this item a stock crate (and does it say what one product looks like)?</summary>
    public static bool IsStockCrate(int itemId) => GetCratePickup(itemId) != null;

    /// <summary>How many products a full crate of this item holds (0 if it isn't a stock crate).</summary>
    public static int GetUnitsPerCrate(int itemId) => GetCratePickup(itemId)?.UnitsPerCrate ?? 0;

    /// <summary>What one product from this crate looks like on a shelf (null if it isn't a stock crate).</summary>
    public static GameObject GetStockedPrefab(int itemId) => GetCratePickup(itemId)?.StockedPrefab;

    private static ItemPickupNet GetCratePickup(int itemId)
    {
        if (itemId == 0 || ItemCatalog.Instance == null || !ItemCatalog.Instance.TryGet(itemId, out var def) ||
            def.worldNetworkPrefab == null) return null;
        var pickup = def.worldNetworkPrefab.GetComponent<ItemPickupNet>();
        return pickup != null && pickup.IsStockCrate && pickup.StockedPrefab != null ? pickup : null;
    }

    /// <summary>The middle of a zone, in the world.</summary>
    public Vector3 GetZoneCenter(int zone)
    {
        var box = zone >= 0 && zone < ZoneCount ? stockZones[zone] : null;
        return box != null ? box.transform.TransformPoint(box.center) : transform.position;
    }

    private float DistanceToZone(int zone, Vector3 point)
    {
        var box = stockZones[zone];
        Vector3 local = box.transform.InverseTransformPoint(point) - box.center;
        Vector3 half = box.size * 0.5f;
        Vector3 clamped = new(Mathf.Clamp(local.x, -half.x, half.x), Mathf.Clamp(local.y, -half.y, half.y),
                              Mathf.Clamp(local.z, -half.z, half.z));
        return Vector3.Distance(point, box.transform.TransformPoint(clamped + box.center));
    }

    /* ========================= SERVER ========================= */

    /// <summary>Server: put one product in a zone. False if it's full or holds something else.</summary>
    public bool ServerAddOne(int zone, int itemId)
    {
        if (!IsServer || zone < 0 || zone >= zones.Count || !HasRoomFor(zone, itemId)) return false;
        var z = zones[zone];
        zones[zone] = new ZoneStock { itemId = itemId, count = (z.itemId == itemId ? z.count : 0) + 1 };
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
        zones[zone] = z.count == 1 ? default : new ZoneStock { itemId = z.itemId, count = z.count - 1 };
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
        public Vector3 cell;        // meters per product along the zone's x/y/z
        public bool turned;         // products turned 90 degrees to fit more
        public Vector3 pivotOffset; // from the product's pivot to the middle of its model (scaled)
        public Vector3 half;        // product half size along the zone's axes
    }

    // Same result on every machine: it only depends on the prefab and the zone.
    private bool TryGetLayout(int zone, int itemId, out Layout layout)
    {
        layout = default;
        var box = zone >= 0 && zone < ZoneCount ? stockZones[zone] : null;
        var prefab = GetStockedPrefab(itemId);
        if (box == null || prefab == null) return false;

        var product = ShipmentPacker.GetBox(prefab);
        Vector3 size = ShipmentPacker.ZoneSize(box);

        for (int turn = 0; turn < 2; turn++)
        {
            Vector3 h = turn == 0 ? product.half : new Vector3(product.half.z, product.half.y, product.half.x);
            Vector3 cell = h * 2f + Vector3.one * spacing;
            int nx = Mathf.Max(0, Mathf.FloorToInt((size.x + spacing) / cell.x));
            int ny = Mathf.Max(0, Mathf.FloorToInt((size.y + spacing) / cell.y));
            int nz = Mathf.Max(0, Mathf.FloorToInt((size.z + spacing) / cell.z));
            int cap = Mathf.Min(nx * ny * nz, Mathf.Max(0, maxPerZone));
            if (cap > layout.capacity)
                layout = new Layout { nx = nx, ny = ny, nz = nz, capacity = cap, cell = cell, turned = turn == 1,
                                      pivotOffset = product.pivotOffset, half = h };
        }
        return layout.capacity > 0;
    }

    // Front row first (the zone's +Z side), left to right, then the next row back, then the next layer up.
    private void GetSlotPose(int zone, Layout l, int index, out Vector3 position, out Quaternion rotation)
    {
        var box = stockZones[zone];
        Vector3 size = ShipmentPacker.ZoneSize(box);
        Quaternion zoneRot = box.transform.rotation;

        int perLayer = l.nx * l.nz;
        int layer = index / perLayer;
        int inLayer = index % perLayer;
        int row = inLayer / l.nx;
        int col = inLayer % l.nx;

        // Centre the grid in the zone so leftover space is shared on both sides.
        float usedX = l.nx * l.cell.x - spacing;
        float usedZ = l.nz * l.cell.z - spacing;
        Vector3 local = new(
            -usedX * 0.5f + l.half.x + col * l.cell.x,
            -size.y * 0.5f + l.half.y + layer * l.cell.y,
            usedZ * 0.5f - l.half.z - row * l.cell.z);

        rotation = l.turned ? zoneRot * Quaternion.Euler(0f, 90f, 0f) : zoneRot;
        Vector3 center = box.transform.TransformPoint(box.center) + zoneRot * local;
        position = center - rotation * l.pivotOffset;
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
        var z = GetZone(zone);
        SetVisualCount(zone, z.itemId, z.count);
    }

    // Adds or removes models at the end, so buying one only removes one.
    private void SetVisualCount(int zone, int itemId, int count)
    {
        var list = visuals[zone];
        if (visualItem[zone] != itemId)
        {
            foreach (var go in list) if (go != null) Destroy(go);
            list.Clear();
            visualItem[zone] = itemId;
        }

        while (list.Count > count)
        {
            int last = list.Count - 1;
            if (list[last] != null) Destroy(list[last]);
            list.RemoveAt(last);
        }

        if (list.Count >= count) return;
        var prefab = GetStockedPrefab(itemId);
        if (prefab == null || !TryGetLayout(zone, itemId, out var layout)) return;

        while (list.Count < count && list.Count < layout.capacity)
        {
            GetSlotPose(zone, layout, list.Count, out Vector3 pos, out Quaternion rot);
            list.Add(MakeVisual(prefab, pos, rot, stockZones[zone].transform));
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

        go.transform.SetParent(parent, true); // keeps the prefab's own size
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
        if (stockZones == null) return;
        foreach (var box in stockZones) if (box != null) box.isTrigger = true;
    }

    private void OnDrawGizmosSelected()
    {
        if (stockZones == null) return;
        foreach (var box in stockZones)
        {
            if (box == null) continue;
            Gizmos.color = new Color(0.3f, 0.9f, 1f, 0.8f);
            Gizmos.matrix = box.transform.localToWorldMatrix;
            Gizmos.DrawWireCube(box.center, box.size);
            Gizmos.DrawLine(box.center, box.center + Vector3.forward * box.size.z * 0.5f); // front
        }
        Gizmos.matrix = Matrix4x4.identity;
    }
}
