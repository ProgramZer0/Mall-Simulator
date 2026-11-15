using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.AI;
using Debug = UnityEngine.Debug;

/// <summary>
/// Host: the walkable area customers path on, built at runtime from the mall's colliders and kept up
/// to date. Anything placed, moved, built or removed marks it changed; a moment after the last change
/// it's updated in the background (only the parts that changed are rebuilt).
///
/// Left out: shelf products, players, carts, doors (customers walk through doorways), edit-mode
/// copies and trigger colliders. Placed items are added as boxes of their colliders' shapes on any
/// layer (see AddItems). Put this on the same object as the CustomerManager; it's added
/// automatically if missing.
/// </summary>
public class CustomerNavMesh : MonoBehaviour
{
    public static CustomerNavMesh Instance { get; private set; }

    [Tooltip("Optional: a BoxCollider covering everywhere customers can walk. Empty = every store's space " +
             "and the customer entrances, grown by Margin.")]
    [SerializeField] private BoxCollider area;
    [SerializeField] private float margin = 30f;

    [Tooltip("Layers whose colliders are walked on or around. Leave out layers that only hold small " +
             "loose things or players; it makes updates quicker.")]
    [SerializeField] private LayerMask layers = ~0;

    [Header("Customer size")]
    [SerializeField] private float agentRadius = 0.3f;
    [SerializeField] private float agentHeight = 1.8f;
    [SerializeField] private float maxStep = 0.35f;
    [SerializeField] private float maxSlope = 40f;

    [Tooltip("Seconds after the last change before updating (a burst of changes is done together).")]
    [SerializeField] private float rebuildDelay = 1.5f;
    [SerializeField] private bool logBuilds = true;

    /// <summary>The space the walkable area was last built over.</summary>
    public Bounds BuiltBounds { get; private set; }

    /// <summary>Built at least once (customers can start walking).</summary>
    public bool Ready { get; private set; }

    /// <summary>Where it's at, in words (for messages).</summary>
    public string Describe()
    {
        var nm = Unity.Netcode.NetworkManager.Singleton;
        if (nm == null || !nm.IsServer) return "the walkable area is only built on the host";
        if (building != null) return $"the walkable area is still being built ({buildTimer.Elapsed.TotalSeconds:0.0} s so far)";
        if (!Ready) return "the walkable area hasn't been built yet";
        return $"walkable area ready ({sources.Count} colliders)";
    }

    private NavMeshData data;
    private NavMeshDataInstance dataInstance;
    private NavMeshBuildSettings settings;
    private AsyncOperation building;
    private readonly Stopwatch buildTimer = new();
    private double prepMs;
    private readonly List<NavMeshBuildSource> sources = new();
    private readonly List<NavMeshBuildMarkup> markups = new();
    private bool pending = true;
    private float lastChange = -999f;
    private float nextPoll;
    private int seenItems = -1, seenWalls = -1, seenOpenings = -1, seenDoors = -1, seenZones = -1;

    private void Awake() => Instance = this;

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (dataInstance.valid) dataInstance.Remove();
    }

    /// <summary>Something customers walk on or around changed: update soon.</summary>
    public void MarkDirty()
    {
        pending = true;
        lastChange = Time.time;
    }

    private void Update()
    {
        var nm = Unity.Netcode.NetworkManager.Singleton;
        if (nm == null || !nm.IsServer) return; // only the host paths customers

        if (Time.time >= nextPoll)
        {
            nextPoll = Time.time + 0.5f;
            if (ItemPickupNet.LayoutVersion != seenItems || CuttableWall.Version != seenWalls ||
                WallOpening.Version != seenOpenings || DoorNet.CustomerVersion != seenDoors ||
                CustomerNoWalkZone.Version != seenZones)
            {
                if (seenItems >= 0) MarkDirty();
                seenItems = ItemPickupNet.LayoutVersion;
                seenWalls = CuttableWall.Version;
                seenOpenings = WallOpening.Version;
                seenDoors = DoorNet.CustomerVersion;
                seenZones = CustomerNoWalkZone.Version;
            }
        }

        if (building != null)
        {
            if (!building.isDone) return;
            building = null;
            Ready = true;
            if (logBuilds)
                Debug.Log($"[CustomerNavMesh] Updated in {buildTimer.ElapsedMilliseconds} ms ({prepMs:0.0} ms of it on the main thread, {sources.Count} colliders).", this);
        }
        if (pending && Time.time - lastChange >= (Ready ? rebuildDelay : 0f)) Build();
    }

    private void Build()
    {
        pending = false;
        buildTimer.Restart();

        settings = NavMesh.GetSettingsByID(0);
        settings.agentRadius = agentRadius;
        settings.agentHeight = agentHeight;
        settings.agentClimb = maxStep;
        settings.agentSlope = maxSlope;

        Bounds bounds = BuildBounds();
        BuiltBounds = bounds;
        NavMeshBuilder.CollectSources(bounds, layers, NavMeshCollectGeometry.PhysicsColliders, 0, markups, sources);
        AddedAsItem.Clear();
        sources.RemoveAll(Skip);
        AddItems(bounds);
        AddDoorBlockers(bounds);
        foreach (var zone in CustomerNoWalkZone.All)
            if (zone != null && bounds.Intersects(zone.WorldBounds)) sources.Add(zone.ToSource());

        if (data == null)
        {
            data = new NavMeshData(settings.agentTypeID);
            dataInstance = NavMesh.AddNavMeshData(data);
        }
        building = NavMeshBuilder.UpdateNavMeshDataAsync(data, settings, sources, bounds);
        prepMs = buildTimer.Elapsed.TotalMilliseconds; // the part on the main thread (the rest runs in the background)
    }

    private static bool Skip(NavMeshBuildSource s)
    {
        var c = s.component;
        if (c == null) return false;
        if (c is Collider col && col.isTrigger) return true;
        if (c.TryGetComponent<StockModel>(out _)) return true; // shelf products
        return c.GetComponentInParent<PlayerController3D>() != null ||
               c.GetComponentInParent<CartNet>() != null ||
               c.GetComponentInParent<DoorMotion>() != null ||
               c.GetComponentInParent<EditLocalItem>() != null ||
               c.GetComponentInParent<CustomerView>() != null ||
               IsAddedAsItem(c.GetComponentInParent<ItemPickupNet>()); // added by AddItems instead
    }

    // Windows and doors keep their real colliders (a doorway's gap matters); other placed things are boxes.
    private static bool IsAddedAsItem(ItemPickupNet item)
    {
        if (item == null) return false;
        if (!AddedAsItem.TryGetValue(item, out bool yes))
            AddedAsItem[item] = yes = item.GetComponentInChildren<WallOpening>(true) == null &&
                                      item.GetComponentInChildren<DoorNet>(true) == null;
        return yes;
    }

    // Per build: whether each item is added as boxes (many of its colliders ask).
    private static readonly Dictionary<ItemPickupNet, bool> AddedAsItem = new();
    private readonly List<Collider> itemColliders = new();

    // Placed things (furniture, shelves, boxes on the floor) as solid shapes, whatever layer they're on and
    // even while their colliders are switched off (hidden on the host during store edit mode), so customers
    // always walk around them. Flat things (rugs, mats) stay walkable.
    private void AddItems(Bounds area)
    {
        foreach (var item in ItemPickupNet.All)
        {
            if (!IsAddedAsItem(item) || item.IsOnCart ||
                item.GetComponentInParent<EditLocalItem>() != null) continue;
            item.GetComponentsInChildren(true, itemColliders);
            foreach (var col in itemColliders)
            {
                if (col.isTrigger || col.GetComponentInParent<StockModel>() != null) continue;
                if (!TryLocalBox(col, out Vector3 center, out Vector3 size)) continue;
                Matrix4x4 m = col.transform.localToWorldMatrix * Matrix4x4.Translate(center);
                Vector3 world = Vector3.Scale(size, col.transform.lossyScale);
                Vector3 pos = m.GetColumn(3);
                if (Mathf.Abs(world.y) < maxStep * 0.5f) continue; // flat: walk on it
                if (!area.Contains(pos)) continue;
                sources.Add(new NavMeshBuildSource
                {
                    shape = NavMeshBuildSourceShape.Box,
                    size = size,
                    transform = m,
                    area = 0,
                    component = col,
                });
            }
        }
    }

    // A collider's shape as a box in its own space (works while it's switched off).
    private static bool TryLocalBox(Collider col, out Vector3 center, out Vector3 size)
    {
        switch (col)
        {
            case BoxCollider b: center = b.center; size = b.size; return true;
            case SphereCollider sp: center = sp.center; size = Vector3.one * sp.radius * 2f; return true;
            case CapsuleCollider cap:
            {
                center = cap.center;
                float d = cap.radius * 2f;
                size = new Vector3(d, d, d);
                size[cap.direction] = Mathf.Max(d, cap.height);
                return true;
            }
            case MeshCollider mc when mc.sharedMesh != null:
                center = mc.sharedMesh.bounds.center;
                size = mc.sharedMesh.bounds.size;
                return true;
        }
        center = size = Vector3.zero;
        return false;
    }

    // Door leaves are left out above (doorways are walkable). Doors customers mustn't use (locked,
    // or manual and not public: staff, back rooms, docks) get a solid block where the shut door is.
    private void AddDoorBlockers(Bounds area)
    {
        foreach (var door in DoorNet.All)
        {
            if (door == null || door.CustomersCanUse || !door.TryGetComponent<DoorMotion>(out var motion) ||
                !motion.TryGetClosedBounds(out var b) || !area.Intersects(b)) continue;
            Vector3 size = b.size;
            size.x = Mathf.Max(size.x, 0.5f); // a thin leaf would slip between the build's samples
            size.z = Mathf.Max(size.z, 0.5f);
            size.y += 0.2f;
            sources.Add(new NavMeshBuildSource
            {
                shape = NavMeshBuildSourceShape.Box,
                size = size,
                transform = Matrix4x4.TRS(b.center - Vector3.up * 0.1f, Quaternion.identity, Vector3.one),
                area = 1, // Not Walkable
            });
        }
    }

    private Bounds BuildBounds()
    {
        if (area != null) return area.bounds;
        bool any = false;
        Bounds b = default;
        void Add(Bounds x)
        {
            if (!any) { b = x; any = true; }
            else b.Encapsulate(x);
        }
        if (MallManager.Instance != null)
            foreach (var plot in MallManager.Instance.Plots) if (plot != null) Add(plot.GetWorldBounds());
        foreach (var sp in CustomerSpawnPoint.All) if (sp != null) Add(new Bounds(sp.transform.position, Vector3.one));
        if (!any) b = new Bounds(transform.position, Vector3.one * 100f);
        b.Expand(new Vector3(margin * 2f, 10f, margin * 2f));
        return b;
    }
}
