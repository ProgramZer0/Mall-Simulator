using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Something that holds stock: a shelf, rack, table, fridge... Put it on the furniture's world
/// prefab (next to its NetworkObject and ItemPickupNet) and add empty child objects as Place Points.
///
/// Hold a stock crate (an item with Is Stock Crate ticked), look at the container and press F: the
/// crate goes onto the free place point nearest where you're looking and shows as the crate's
/// Stocked Prefab. Look at stocked goods with empty hands and press F to take that crate back.
///
/// Only one synced list of item ids per container goes over the network (not a network object
/// per product); each machine builds the visuals itself. A container with stock can't be picked
/// up, and its stock comes along when it's moved in store edit mode.
/// </summary>
public class StockContainer : NetworkBehaviour
{
    [Tooltip("Where stock goes. The Stocked Prefab's pivot sits on the point, facing the point's forward (blue arrow).")]
    [SerializeField] private Transform[] placePoints;

    private NetworkList<int> stock;          // item id per place point, 0 = empty
    private GameObject[] visuals;
    private int[] pendingStock;              // server: contents to restore when spawned (edit mode)

    /// <summary>Every spawned container on this machine (customers can search these for stock).</summary>
    public static readonly System.Collections.Generic.List<StockContainer> All = new();

    public int PointCount => placePoints != null ? placePoints.Length : 0;

    private void Awake()
    {
        stock = new NetworkList<int>(null, NetworkVariableReadPermission.Everyone,
                                     NetworkVariableWritePermission.Server);
        visuals = new GameObject[PointCount];
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            stock.Clear();
            for (int i = 0; i < PointCount; i++)
                stock.Add(pendingStock != null && i < pendingStock.Length ? pendingStock[i] : 0);
            pendingStock = null;
        }

        stock.OnListChanged += OnStockChanged;
        RebuildAllVisuals();
        All.Add(this);
    }

    public override void OnNetworkDespawn()
    {
        All.Remove(this);
        stock.OnListChanged -= OnStockChanged;
        for (int i = 0; i < visuals.Length; i++) ClearVisual(i);
    }

    /* ========================= QUERIES ========================= */

    /// <summary>The item id at a place point (0 = empty or no such point).</summary>
    public int GetStock(int point) =>
        IsSpawned && point >= 0 && point < stock.Count ? stock[point] : 0;

    public bool HasStock
    {
        get
        {
            if (!IsSpawned) return false;
            foreach (int id in stock) if (id != 0) return true;
            return false;
        }
    }

    public Transform GetPoint(int point) =>
        point >= 0 && point < PointCount ? placePoints[point] : null;

    /// <summary>
    /// The empty (wantFilled false) or stocked (true) point nearest a world position, or -1.
    /// onlyItem: only stocked points holding this item.
    /// </summary>
    public int FindNearestPoint(Vector3 near, bool wantFilled, int onlyItem = 0)
    {
        int best = -1;
        float bestDist = float.MaxValue;
        for (int i = 0; i < PointCount; i++)
        {
            int id = GetStock(i);
            if (placePoints[i] == null || (id != 0) != wantFilled) continue;
            if (onlyItem != 0 && id != onlyItem) continue;
            float d = (placePoints[i].position - near).sqrMagnitude;
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    /// <summary>Is this item a stock crate (and does it say what it looks like on a shelf)?</summary>
    public static bool IsStockCrate(int itemId) => GetCratePickup(itemId) != null;

    /// <summary>How many products a full crate of this item holds (0 if it isn't a stock crate).</summary>
    public static int GetUnitsPerCrate(int itemId) => GetCratePickup(itemId)?.UnitsPerCrate ?? 0;

    /// <summary>What one product from this crate looks like on a place point (null if it isn't a stock crate).</summary>
    public static GameObject GetStockedPrefab(int itemId) => GetCratePickup(itemId)?.StockedPrefab;

    private static ItemPickupNet GetCratePickup(int itemId)
    {
        if (itemId == 0 || ItemCatalog.Instance == null || !ItemCatalog.Instance.TryGet(itemId, out var def) ||
            def.worldNetworkPrefab == null) return null;
        var pickup = def.worldNetworkPrefab.GetComponent<ItemPickupNet>();
        return pickup != null && pickup.IsStockCrate && pickup.StockedPrefab != null ? pickup : null;
    }

    /* ========================= SERVER ========================= */

    /// <summary>Server: put an item on a place point (0 empties it).</summary>
    public void ServerSetStock(int point, int itemId)
    {
        if (!IsServer || point < 0 || point >= stock.Count) return;
        stock[point] = itemId;
    }

    /// <summary>A stocked point holding this item, or -1. For customers looking for something to buy.</summary>
    public int FindStock(int itemId)
    {
        for (int i = 0; i < PointCount; i++)
            if (GetStock(i) == itemId && itemId != 0) return i;
        return -1;
    }

    /// <summary>
    /// Server: take one product off a point (a customer bought it, say). Its model disappears for
    /// everyone. Returns the item id that was there, or 0 if the point was empty.
    /// </summary>
    public int ServerRemoveOne(int point)
    {
        int id = IsServer ? GetStock(point) : 0;
        if (id != 0) stock[point] = 0;
        return id;
    }

    /// <summary>Server: a copy of what's on every point (for edit mode).</summary>
    public int[] ServerCopyStock()
    {
        var copy = new int[stock.Count];
        for (int i = 0; i < copy.Length; i++) copy[i] = stock[i];
        return copy;
    }

    /// <summary>Server: what to put back on the points once spawned. Call after Instantiate, before Spawn.</summary>
    public void ServerPrepareStock(int[] contents) => pendingStock = contents;

    /* ========================= VISUALS ========================= */

    private void OnStockChanged(NetworkListEvent<int> change)
    {
        if (change.Type == NetworkListEvent<int>.EventType.Value) RebuildVisual(change.Index);
        else RebuildAllVisuals();
    }

    private void RebuildAllVisuals()
    {
        for (int i = 0; i < PointCount; i++) RebuildVisual(i);
    }

    private void RebuildVisual(int point)
    {
        if (point < 0 || point >= PointCount) return;
        ClearVisual(point);

        var prefab = GetStockedPrefab(GetStock(point));
        if (prefab == null || placePoints[point] == null) return;

        visuals[point] = MakeVisual(prefab, placePoints[point]);
    }

    private void ClearVisual(int point)
    {
        if (visuals[point] == null) return;
        Destroy(visuals[point]);
        visuals[point] = null;
    }

    private static Transform builder;

    /// <summary>A plain copy of the prefab: looks and colliders only, no scripts, physics or networking.</summary>
    private static GameObject MakeVisual(GameObject prefab, Transform point)
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

        go.transform.SetParent(point, false);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        return go;
    }

    private static void DestroyAll<T>(GameObject root) where T : Component
    {
        var comps = root.GetComponentsInChildren<T>(true);
        for (int i = comps.Length - 1; i >= 0; i--)
            if (comps[i] != null) DestroyImmediate(comps[i]);
    }

    private void OnDrawGizmosSelected()
    {
        if (placePoints == null) return;
        foreach (var p in placePoints)
        {
            if (p == null) continue;
            Gizmos.color = new Color(0.3f, 0.9f, 1f, 0.8f);
            Gizmos.DrawWireCube(p.position + p.up * 0.05f, new Vector3(0.1f, 0.1f, 0.1f));
            Gizmos.DrawLine(p.position, p.position + p.forward * 0.2f);
        }
    }
}
