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
/// copies and trigger colliders. Put this on the same object as the CustomerManager; it's added
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
    private readonly List<NavMeshBuildSource> sources = new();
    private readonly List<NavMeshBuildMarkup> markups = new();
    private bool pending = true;
    private float lastChange = -999f;
    private float nextPoll;
    private int seenItems = -1, seenWalls = -1, seenOpenings = -1;

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
                WallOpening.Version != seenOpenings)
            {
                if (seenItems >= 0) MarkDirty();
                seenItems = ItemPickupNet.LayoutVersion;
                seenWalls = CuttableWall.Version;
                seenOpenings = WallOpening.Version;
            }
        }

        if (building != null)
        {
            if (!building.isDone) return;
            building = null;
            Ready = true;
            if (logBuilds)
                Debug.Log($"[CustomerNavMesh] Updated in {buildTimer.ElapsedMilliseconds} ms ({sources.Count} colliders).", this);
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
        NavMeshBuilder.CollectSources(bounds, layers, NavMeshCollectGeometry.PhysicsColliders, 0, markups, sources);
        sources.RemoveAll(Skip);

        if (data == null)
        {
            data = new NavMeshData(settings.agentTypeID);
            dataInstance = NavMesh.AddNavMeshData(data);
        }
        building = NavMeshBuilder.UpdateNavMeshDataAsync(data, settings, sources, bounds);
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
               c.GetComponentInParent<CustomerView>() != null;
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
