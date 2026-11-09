using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// One rentable store space in the mall.
///
/// Scene setup:
///  - Put this (plus a NetworkObject) on an empty object per store.
///  - Base Areas: BoxColliders covering the starting floor space. Make them tall enough to
///    include a standing player. They're only used as shapes: this script disables them
///    at runtime so they never block movement or raycasts.
///  - Segments: extra floor space unlocked by buying it. Each has its own BoxCollider area
///    and the false walls that disappear once it's bought.
///
/// Ownership and purchased segments are synced; the walls themselves are plain scene objects
/// that every client shows/hides from the synced state.
/// </summary>
public class StorePlot : NetworkBehaviour
{
    public const ulong NoOwner = ulong.MaxValue;

    [Serializable]
    public class Segment
    {
        public string name = "Expansion";
        [Tooltip("Floor space this segment adds once bought: one or more boxes (for L shapes, side rooms...).")]
        public BoxCollider[] areas;
        [Tooltip("Old single area; it's moved into Areas automatically.")]
        [HideInInspector] public BoxCollider area;
        [Tooltip("Walls removed when this segment is bought.")]
        public GameObject[] falseWalls;
        public int price = 1000;

        /// <summary>Every box of this segment (including an old single Area not moved over yet).</summary>
        public IEnumerable<BoxCollider> Areas
        {
            get
            {
                if (area != null) yield return area;
                if (areas == null) yield break;
                foreach (var box in areas)
                    if (box != null && box != area) yield return box;
            }
        }

        public bool Contains(Vector3 worldPoint)
        {
            foreach (var box in Areas)
                if (BoxContains(box, worldPoint)) return true;
            return false;
        }
    }

    [Header("Identity")]
    [Tooltip("Unique per plot. Used to refer to this store in menus, saves and RPCs.")]
    [SerializeField] private int plotId;
    [SerializeField] private string displayName = "Store";
    [SerializeField] private int price = 0;
    [Tooltip("Handed out automatically to players who join without a store.")]
    [SerializeField] private bool isStarterPlot = true;
    [Tooltip("Money a team starts the game with when this is the starter store it's given. " +
             "Lets you balance starter stores against each other (e.g. a worse spot = more cash).")]
    [SerializeField] private int startingMoney = 5000;

    [Header("Space")]
    [SerializeField] private BoxCollider[] baseAreas;
    [Tooltip("Max 32. Order matters: the index is what gets synced.")]
    [SerializeField] private Segment[] segments;

    [Header("Front Wall")]
    [Tooltip("The wall sections (scene objects with a WallModule) the owner can swap in edit mode. " +
             "Drag them in from the Hierarchy; the order here is the order they're numbered in.")]
    [SerializeField] private WallModule[] frontWalls;
    [Tooltip("If Front Walls is empty, find wall modules near the store automatically instead.")]
    [SerializeField] private bool autoFindFrontWalls;
    [Tooltip("Automatic search only: wall modules within this many meters of the store's space " +
             "belong to it. Each module goes to the closest store.")]
    [SerializeField] private float frontWallReach = 1f;
    [Tooltip("The store's front wall(s) with a CuttableWall: players put windows and doors (WallOpening " +
             "items) anywhere in them in edit mode. While any are set, the store needs at least one door in " +
             "them to save edit mode.")]
    [SerializeField] private CuttableWall[] storefrontWalls;

    [Header("Deliveries")]
    [Tooltip("BoxColliders marking where this store's deliveries are set down. Boxes are packed in " +
             "wherever they fit without touching anything already there. Keep each zone's bottom on the " +
             "floor and put zones where the team can pick up (inside a loading dock zone or the store). " +
             "Other teams can't place things in them. Like the area boxes, they're switched off at runtime.")]
    [SerializeField] private BoxCollider[] shipmentZones;

    [Header("Paint")]
    [Tooltip("The store's floor renderers, painted with the paint brush (2). The floor's collider must be " +
             "on the same object or a child of it, so clicks find it. Leave empty if the floor can't be painted.")]
    [SerializeField] private Renderer[] floorRenderers;

    [Header("Doors")]
    [Tooltip("Door objects for this store (e.g. the door inside each doorway). With a DoorMotion they " +
             "swing/slide/lift open while the store is open and close while it's closed, for sale or being " +
             "renovated; without one they're shown while closed and hidden while open. Doors of doorway modules " +
             "in Front Walls (WallModule > Door) are included automatically. A door can also be in " +
             "For Sale Walls; this rule wins.")]
    [SerializeField] private GameObject[] doors;

    [Header("Spawn")]
    [Tooltip("Where a player appears when this is their starter store. Leave empty to use a spot " +
             "just outside the storefront, worked out from the renovation (or for-sale) walls.")]
    [SerializeField] private Transform spawnPoint;
    [Tooltip("How far outside the storefront the automatic spawn spot is, in meters.")]
    [SerializeField] private float spawnDistance = 1.5f;

    [Header("Renovation")]
    [Tooltip("Shown over the storefront while the store is in edit mode (hidden otherwise).")]
    [SerializeField] private GameObject[] renovationWalls;

    [Header("Edit View")]
    [Tooltip("In edit mode the camera cuts away everything above this store (the floor above, " +
             "upstairs players, NPCs, items). By default the cut is the top of the base area boxes.")]
    [SerializeField] private bool overrideCutHeight;
    [Tooltip("World height (Y) of the cut, used when Override Cut Height is ticked.")]
    [SerializeField] private float cutHeight = 3f;

    [Header("For Sale")]
    [Tooltip("Shown while nobody owns this store. Players look at it and press F to buy. " +
             "Untick Is Starter Plot to make a store buy-only.")]
    [SerializeField] private GameObject[] forSaleWalls;
    [Tooltip("How close (meters) a player must be to a for-sale wall to buy the store.")]
    [SerializeField] private float buyRange = 4f;

    [Header("Owner Only")]
    [Tooltip("Shown only on the owner's screen, e.g. a floor glow or a 'Your Store' sign, " +
             "so players can spot their own store. Hidden for everyone else.")]
    [SerializeField] private GameObject[] ownerOnlyVisuals;

    [Header("Debug (read only)")]
    [SerializeField] private string status = "Not spawned";

    // The team that owns this store (a team id is its founding player's client id).
    private readonly NetworkVariable<ulong> ownerTeamId =
        new(NoOwner, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // Bit i set = segments[i] has been bought.
    private readonly NetworkVariable<int> purchasedSegments =
        new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // Who is editing this store right now (NoOwner = open for business).
    private readonly NetworkVariable<ulong> editorClientId =
        new(NoOwner, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // Paint colors (PaintPalette ids, 0 = own colors) for the storefront and the floor.
    private readonly NetworkVariable<byte> frontPaint =
        new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<byte> floorPaint =
        new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // The owners closed the store (doors shut, no customers). Separate from renovating.
    private readonly NetworkVariable<bool> closedByOwner =
        new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>Raised on every client when the owner, purchased segments or edit state change.</summary>
    public event Action<StorePlot> Changed;

    public ulong EditorClientId => editorClientId.Value;
    /// <summary>In edit mode: closed, renovation wall up, items out of the world until it ends.</summary>
    public bool IsRenovating => editorClientId.Value != NoOwner;
    /// <summary>The owners have closed the store.</summary>
    public bool IsClosedByOwner => closedByOwner.Value;
    /// <summary>Owned, not closed and not under renovation. Customers/sales should check this.</summary>
    public bool IsOpen => HasOwner && !IsRenovating && !closedByOwner.Value;

    public int PlotId => plotId;
    public string DisplayName => displayName;
    public int Price => price;
    public bool IsStarterPlot => isStarterPlot;
    public int StartingMoney => startingMoney;
    /* ========================= SHIPMENT ZONES ========================= */

    /// <summary>The zones deliveries are packed into.</summary>
    public BoxCollider[] ShipmentZones => shipmentZones;

    public bool HasShipmentZones
    {
        get
        {
            if (shipmentZones != null)
                foreach (var z in shipmentZones) if (z != null) return true;
            return false;
        }
    }

    /// <summary>Is this position inside one of the store's shipment zones?</summary>
    public bool IsOnShipmentArea(Vector3 position)
    {
        if (shipmentZones == null) return false;
        foreach (var z in shipmentZones)
            if (BoxContains(z, position)) return true;
        return false;
    }

    public ulong OwnerTeamId => ownerTeamId.Value;
    public bool HasOwner => ownerTeamId.Value != NoOwner;
    public int SegmentCount => segments?.Length ?? 0;

    /// <summary>What this store is selling: products, prices, quantities (worked out locally, nothing extra synced).</summary>
    public StoreListing Listing { get; private set; }

    /// <summary>How appealing the store is: quality, what's seen from outside, each personality's view.</summary>
    public StoreAppeal Appeal { get; private set; }

    private void Awake()
    {
        Listing = GetComponent<StoreListing>();
        if (Listing == null) Listing = gameObject.AddComponent<StoreListing>();
        Appeal = GetComponent<StoreAppeal>();
        if (Appeal == null) Appeal = gameObject.AddComponent<StoreAppeal>();

        // Which module each storefront section shows (0 = the one placed in the scene).
        // Store-wide prices set with "Set for all": new stock and newly placed items pick them up.
        storePrices = new NetworkList<StorePrice>(
            null, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        frontOverrides = new NetworkList<byte>(
            null, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // Walls the owners built in edit mode.
        builtWalls = new NetworkList<BuiltWall>(
            null, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // The colliders are just authoring shapes; Contains() reads them directly.
        foreach (var box in baseAreas)
            if (box != null) box.enabled = false;
        if (segments != null)
            foreach (var seg in segments)
                if (seg != null)
                    foreach (var box in seg.Areas) box.enabled = false;
        if (shipmentZones != null)
            foreach (var z in shipmentZones)
                if (z != null) z.enabled = false;

        if (SegmentCount > 32)
            Debug.LogError($"[StorePlot] '{displayName}' has {SegmentCount} segments; max is 32.", this);

        // Mark the for-sale walls so the player's F raycast can find this store from them.
        if (forSaleWalls != null)
            foreach (var w in forSaleWalls)
                if (w != null && !w.TryGetComponent<ForSaleSign>(out _))
                    w.AddComponent<ForSaleSign>().Init(this);

        // Segment walls: looking at one offers to unlock that segment (when the mall allows it).
        if (segments != null)
            for (int i = 0; i < segments.Length; i++)
                if (segments[i]?.falseWalls != null)
                    foreach (var w in segments[i].falseWalls)
                        if (w != null && !w.TryGetComponent<SegmentSign>(out _))
                            w.AddComponent<SegmentSign>().Init(this, i);

        if (storefrontWalls != null)
            foreach (var w in storefrontWalls)
                if (w != null)
                {
                    w.Plot = this;
                    w.IsStorefront = true;
                }

        // Start in the "nobody owns it" look until the synced state says otherwise.
        ApplyRenovationWalls();
        ApplyOwnerVisuals();
    }

    private void Start()
    {
        // A wall section can only belong to one store, once.
        if (frontWalls == null) return;
        var allPlots = FindObjectsByType<StorePlot>(FindObjectsSortMode.None);
        for (int i = 0; i < frontWalls.Length; i++)
        {
            var w = frontWalls[i];
            if (w == null) continue;
            for (int j = 0; j < i; j++)
                if (frontWalls[j] == w)
                    Debug.LogWarning($"[StorePlot] '{displayName}' lists front wall '{w.name}' twice.", this);
            foreach (var other in allPlots)
            {
                if (other == this || other.frontWalls == null || other.plotId < plotId) continue; // warn once per pair
                foreach (var ow in other.frontWalls)
                    if (ow == w)
                        Debug.LogWarning($"[StorePlot] Front wall '{w.name}' is listed by both '{displayName}' " +
                                         $"and '{other.displayName}'. Give it to one store only.", w);
            }
        }
    }

    public override void OnNetworkSpawn()
    {
        ownerTeamId.OnValueChanged += OnOwnerChanged;
        purchasedSegments.OnValueChanged += OnSegmentsChanged;
        editorClientId.OnValueChanged += OnEditorChanged;
        closedByOwner.OnValueChanged += OnClosedChanged;
        frontOverrides.OnListChanged += OnFrontOverridesChanged;
        builtWalls.OnListChanged += OnBuiltWallsChanged;
        frontPaint.OnValueChanged += OnFrontPaintChanged;
        floorPaint.OnValueChanged += OnFloorPaintChanged;
        RebuildBuiltWalls();
        ApplyFrontWall();
        ApplyFloorPaint();
        ApplyWalls();
        ApplyRenovationWalls();
        ApplyOwnerVisuals();
        UpdateStatus();
        Changed += OnAnyChange;

        if (MallManager.Instance != null) MallManager.Instance.Register(this);
    }

    public override void OnNetworkDespawn()
    {
        ownerTeamId.OnValueChanged -= OnOwnerChanged;
        purchasedSegments.OnValueChanged -= OnSegmentsChanged;
        editorClientId.OnValueChanged -= OnEditorChanged;
        closedByOwner.OnValueChanged -= OnClosedChanged;
        frontOverrides.OnListChanged -= OnFrontOverridesChanged;
        builtWalls.OnListChanged -= OnBuiltWallsChanged;
        frontPaint.OnValueChanged -= OnFrontPaintChanged;
        floorPaint.OnValueChanged -= OnFloorPaintChanged;
        if (builtWallRoot != null) Destroy(builtWallRoot.gameObject);
        builtWallRoot = null;
        Changed -= OnAnyChange;

        if (MallManager.Instance != null) MallManager.Instance.Unregister(this);
    }

    private void OnOwnerChanged(ulong prev, ulong next)
    {
        ApplyOwnerVisuals();
        Changed?.Invoke(this);
    }

    private void OnAnyChange(StorePlot _) => UpdateStatus();

    /// <summary>Re-check "is this mine?" visuals and the status label (e.g. after a team change).</summary>
    public void RefreshLocalView()
    {
        ApplyOwnerVisuals();
        UpdateStatus();
    }

    private void OnClosedChanged(bool prev, bool next)
    {
        ApplyDoors();
        Changed?.Invoke(this);
    }

    private void OnEditorChanged(ulong prev, ulong next)
    {
        ApplyRenovationWalls();
        ApplyDoors();
        Changed?.Invoke(this);
    }

    private void OnSegmentsChanged(int prev, int next)
    {
        ApplyWalls();
        Changed?.Invoke(this);
    }

    /* ========================= QUERIES ========================= */

    public bool IsSegmentPurchased(int index) =>
        index >= 0 && index < SegmentCount && (purchasedSegments.Value & (1 << index)) != 0;

    /// <summary>
    /// Whether this player may build/stock here: anyone on the owning team. Every permission
    /// check goes through this, so co-op works everywhere once players share a team.
    /// </summary>
    public bool IsAuthorized(ulong clientId) => HasOwner && MallManager.GetTeamOf(clientId) == ownerTeamId.Value;

    /// <summary>True if the point is inside the base space or any purchased segment.</summary>
    public bool Contains(Vector3 worldPoint)
    {
        foreach (var box in baseAreas)
            if (BoxContains(box, worldPoint)) return true;

        for (int i = 0; i < SegmentCount; i++)
            if (IsSegmentPurchased(i) && segments[i].Contains(worldPoint)) return true;

        return false;
    }

    /// <summary>True if the point is in this plot's footprint, bought or not.</summary>
    public bool ContainsIncludingUnpurchased(Vector3 worldPoint)
    {
        if (Contains(worldPoint)) return true;
        for (int i = 0; i < SegmentCount; i++)
            if (segments[i].Contains(worldPoint)) return true;
        return false;
    }

    /// <summary>Point-in-box test that works on disabled colliders (used by plots and docks).</summary>
    public static bool BoxContains(BoxCollider box, Vector3 worldPoint)
    {
        if (box == null) return false;
        Vector3 local = box.transform.InverseTransformPoint(worldPoint) - box.center;
        Vector3 half = box.size * 0.5f;
        return Mathf.Abs(local.x) <= half.x && Mathf.Abs(local.y) <= half.y && Mathf.Abs(local.z) <= half.z;
    }

    /* ========================= SERVER ========================= */

    /// <summary>Server only. Use MallManager's purchase methods rather than calling this directly.</summary>
    public void ServerSetOwner(ulong teamId)
    {
        if (!IsServer) return;
        ownerTeamId.Value = teamId;
    }

    /// <summary>Server only. Ends any edit session, clears the owner and rebuilds the false walls.</summary>
    public void ServerRelease()
    {
        if (!IsServer) return;
        ServerSetEditor(NoOwner);
        ownerTeamId.Value = NoOwner;
        purchasedSegments.Value = 0;
        closedByOwner.Value = false;
        frontOverrides.Clear(); // storefront goes back to how the scene placed it
        builtWalls.Clear();     // and built walls come down
        storePrices.Clear();    // and the price list goes
    }

    /// <summary>
    /// Server only. Open or close the store (doors shown while closed). Use
    /// MallManager.ServerTrySetStoreClosed / RequestSetStoreClosedServerRpc rather than calling
    /// this directly, so ownership is checked.
    /// </summary>
    public void ServerSetClosed(bool closed)
    {
        if (!IsServer) return;
        closedByOwner.Value = closed;
    }

    /* ========================= STORE PRICES ========================= */

    /// <summary>A price set for every one of a product in the store.</summary>
    public struct StorePrice : INetworkSerializeByMemcpy, IEquatable<StorePrice>
    {
        public int itemId;
        public int cents;
        public int loose; // 1 = loose items (e.g. whole crates), 0 = on shelves and racks

        public bool Equals(StorePrice o) => itemId == o.itemId && cents == o.cents && loose == o.loose;
        public override bool Equals(object obj) => obj is StorePrice o && Equals(o);
        public override int GetHashCode() => (itemId * 397 ^ cents) * 2 + loose;
    }

    private NetworkList<StorePrice> storePrices;

    /// <summary>The store-wide price for a product on shelves (loose false) or as loose items (true).</summary>
    public bool TryGetStorePrice(int itemId, bool loose, out int cents)
    {
        cents = 0;
        if (storePrices == null) return false;
        int l = loose ? 1 : 0;
        foreach (var p in storePrices)
            if (p.itemId == itemId && p.loose == l) { cents = p.cents; return cents > 0; }
        return false;
    }

    /// <summary>Server: set (or with 0, remove) the store-wide price for a product.</summary>
    public void ServerSetStorePrice(int itemId, bool loose, int cents)
    {
        if (!IsServer || itemId == 0) return;
        int l = loose ? 1 : 0;
        for (int i = 0; i < storePrices.Count; i++)
        {
            if (storePrices[i].itemId != itemId || storePrices[i].loose != l) continue;
            if (cents > 0) storePrices[i] = new StorePrice { itemId = itemId, cents = cents, loose = l };
            else storePrices.RemoveAt(i);
            return;
        }
        if (cents > 0) storePrices.Add(new StorePrice { itemId = itemId, cents = cents, loose = l });
    }

    /// <summary>The store-wide price for a product at this spot, if it's in a store that has one.</summary>
    public static bool TryGetStorePriceAt(Vector3 point, int itemId, bool loose, out int cents)
    {
        cents = 0;
        var plot = MallManager.Instance != null ? MallManager.Instance.GetPlotAt(point) : null;
        return plot != null && plot.TryGetStorePrice(itemId, loose, out cents);
    }

    /* ========================= BUILT WALLS ========================= */

    private NetworkList<BuiltWall> builtWalls;
    private Transform builtWallRoot;
    private int nextWallId = 1; // server

    public int BuiltWallCount => builtWalls != null ? builtWalls.Count : 0;
    public BuiltWall GetBuiltWall(int index) => builtWalls[index];

    /// <summary>World height of this store's floor (bottom of its space).</summary>
    public float FloorHeight => GetWorldBounds().min.y;

    /// <summary>Height of a full wall: from the floor to the top of the store's space.</summary>
    public float FullWallHeight => Mathf.Max(0.5f, GetEditCutHeight() - FloorHeight);

    /// <summary>Height of a full built wall here (BuiltWallSettings' Full Height, no taller than the store).</summary>
    public float BuiltWallFullHeight =>
        BuiltWallSettings.Instance != null ? BuiltWallSettings.Instance.FullHeightFor(this) : FullWallHeight;

    /// <summary>
    /// Can a wall go from a to b (both on the floor)? Checks the length, both ends and the middle
    /// are inside the store, and the wall limit. Doesn't check money.
    /// </summary>
    public bool CanBuildWall(Vector3 a, Vector3 b, out string reason)
    {
        reason = null;
        var settings = BuiltWallSettings.Instance;
        if (settings == null || settings.WallPrefab == null) { reason = "No wall prefab set"; return false; }
        Vector3 flat = b - a;
        flat.y = 0f;
        if (flat.magnitude < settings.MinLength - 1e-3f) { reason = "Too short"; return false; }

        Vector3 up = Vector3.up * 0.1f; // just above the floor, so floor-level points count as inside
        if (!Contains(a + up) || !Contains(b + up) || !Contains((a + b) * 0.5f + up)) { reason = "Outside the store"; return false; }
        return true;
    }

    /// <summary>What building a wall from a to b will really do (see PlanWall).</summary>
    public struct WallPlan
    {
        public Vector3 a, b;        // the wall that will stand: the new one joined with walls it runs into
        public float addedLength;   // the new part (what's paid for)
        public List<int> joins;     // existing walls it takes in (replaced by the joined wall)
    }

    /// <summary>
    /// Built walls never overlap. A new wall drawn along the same line as existing walls (over them,
    /// through them, or carrying one on) joins them into one wall; only the new length is paid for.
    /// Fails if it would lie over a wall of another height, or squeeze alongside one (parallel, closer
    /// than a wall's thickness), or adds nothing. Walls crossing at an angle are fine.
    /// </summary>
    public bool PlanWall(Vector3 a, Vector3 b, float height, out WallPlan plan, out string reason)
    {
        plan = new WallPlan { a = a, b = b, joins = new List<int>() };
        reason = null;
        var settings = BuiltWallSettings.Instance;
        if (settings == null) { reason = "No wall settings"; return false; }

        Vector3 dir = Flat(b - a);
        if (dir == Vector3.zero) { reason = "Too short"; return false; }
        float length = Flat(b - a, false).magnitude;
        static float Cross2(Vector3 x, Vector3 y) => x.x * y.z - x.z * y.x;
        const float tol = 0.01f;

        float lo = 0f, hi = length, covered = 0f;
        foreach (var w in builtWalls)
        {
            Vector3 d = Flat(w.b - w.a);
            if (d == Vector3.zero || Mathf.Abs(Cross2(d, dir)) > 0.01f) continue; // crossing: fine

            float t1 = Vector3.Dot(Flat(w.a - a, false), dir), t2 = Vector3.Dot(Flat(w.b - a, false), dir);
            float wlo = Mathf.Min(t1, t2), whi = Mathf.Max(t1, t2);
            bool overlaps = whi > tol && wlo < length - tol; // shares some length
            bool touches = whi > -tol && wlo < length + tol; // or meets end to end
            float offset = Mathf.Abs(Cross2(dir, Flat(w.a - a, false)));

            if (offset > 0.01f)
            {
                if (overlaps && offset < settings.Thickness - 0.005f) { reason = "Too close to another wall"; return false; }
                continue;
            }
            if (!touches) continue;
            if (Mathf.Abs(w.height - height) > 0.01f)
            {
                if (overlaps) { reason = "A wall of another height is already there"; return false; }
                continue; // different heights meeting end to end stay separate walls
            }

            plan.joins.Add(w.id);
            lo = Mathf.Min(lo, wlo);
            hi = Mathf.Max(hi, whi);
            covered += Mathf.Max(0f, Mathf.Min(length, whi) - Mathf.Max(0f, wlo));
        }

        plan.addedLength = Mathf.Max(0f, length - covered);
        if (plan.addedLength < 0.01f) { reason = "There's already a wall here"; return false; }
        if (BuiltWallCount - plan.joins.Count + 1 > settings.MaxWallsPerStore) { reason = "Too many walls"; return false; }

        plan.a = a + dir * lo;
        plan.b = a + dir * hi;
        plan.a.y = a.y;
        plan.b.y = a.y;
        return true;
    }

    /// <summary>Server: build a planned wall, taking in the walls it joins. Returns the new wall's id.</summary>
    public int ServerBuildPlannedWall(WallPlan plan, float height)
    {
        if (!IsServer) return 0;
        // Joined walls keep the first one's paint.
        byte paint = 0;
        bool first = true;
        foreach (int id in plan.joins)
        {
            if (ServerRemoveWall(id, out var old) && first) { paint = old.paint; first = false; }
        }
        return ServerAddWall(plan.a, plan.b, height, paint);
    }

    /// <summary>
    /// Built walls: the store's edge usually isn't on the wall grid, so an end that lands within half a
    /// grid step of it (just inside or just outside) is slid along the wall onto the edge. The wall
    /// keeps its line, so the grid stays as predictable as before; only those ends change.
    /// </summary>
    public void FitWallToEdges(ref Vector3 a, ref Vector3 b)
    {
        var settings = BuiltWallSettings.Instance;
        if (settings == null) return;
        Vector3 dir = Flat(b - a);
        if (dir == Vector3.zero) return;
        float length = Flat(b - a, false).magnitude;
        float reach = settings.GridSize * 0.5f + 0.01f;
        const float inset = 0.002f; // a hair inside, so the end still counts as in the store

        // Where the line through the wall is inside the store, as distances from a.
        var spans = new List<Vector2>();
        Vector3 origin = a + Vector3.up * 0.1f;
        foreach (var box in baseAreas) AddLineSpan(box, origin, dir, spans);
        for (int i = 0; i < SegmentCount; i++)
            if (IsSegmentPurchased(i))
                foreach (var box in segments[i].Areas) AddLineSpan(box, origin, dir, spans);
        if (spans.Count == 0) return;
        spans.Sort((p, q) => p.x.CompareTo(q.x));
        var merged = new List<Vector2> { spans[0] };
        for (int i = 1; i < spans.Count; i++)
        {
            var last = merged[merged.Count - 1];
            if (spans[i].x <= last.y + 0.01f) merged[merged.Count - 1] = new Vector2(last.x, Mathf.Max(last.y, spans[i].y));
            else merged.Add(spans[i]);
        }

        // The stretch of store the wall's middle is in.
        float mid = length * 0.5f;
        foreach (var span in merged)
        {
            if (mid < span.x || mid > span.y) continue;
            Vector3 start = a;
            if (Mathf.Abs(span.x) < reach) a = start + dir * (span.x + inset);
            if (Mathf.Abs(span.y - length) < reach) b = start + dir * (span.y - inset);
            return;
        }
    }

    // Where a flat line (origin + dir × s) is inside a box, as an s range.
    private static void AddLineSpan(BoxCollider box, Vector3 origin, Vector3 dir, List<Vector2> into)
    {
        if (box == null) return;
        Vector3 lp = box.transform.InverseTransformPoint(origin) - box.center;
        Vector3 ld = box.transform.InverseTransformVector(dir);
        Vector3 half = box.size * 0.5f;
        float lo = float.NegativeInfinity, hi = float.PositiveInfinity;
        for (int i = 0; i < 3; i++)
        {
            if (Mathf.Abs(ld[i]) < 1e-6f)
            {
                if (Mathf.Abs(lp[i]) > half[i]) return;
                continue;
            }
            float t1 = (-half[i] - lp[i]) / ld[i], t2 = (half[i] - lp[i]) / ld[i];
            lo = Mathf.Max(lo, Mathf.Min(t1, t2));
            hi = Mathf.Min(hi, Mathf.Max(t1, t2));
        }
        if (lo <= hi) into.Add(new Vector2(lo, hi));
    }

    /// <summary>Is a point in the store, or within Reach meters of it?</summary>
    public bool IsInsideOrNear(Vector3 worldPoint, float reach) => Contains(worldPoint) || DistanceToSpace(worldPoint) <= reach;

    /// <summary>Server: add a wall (checks done by the caller). Returns its id.</summary>
    public int ServerAddWall(Vector3 a, Vector3 b, float height, byte paint = 0)
    {
        if (!IsServer) return 0;
        int id = nextWallId++;
        builtWalls.Add(new BuiltWall { id = id, a = a, b = b, height = height, paint = paint });
        return id;
    }

    /// <summary>Server: paint a built wall. False if there's no such wall.</summary>
    public bool ServerPaintWall(int id, byte paint)
    {
        if (!IsServer || !PaintPalette.IsValid(paint)) return false;
        for (int i = 0; i < builtWalls.Count; i++)
        {
            if (builtWalls[i].id != id) continue;
            var w = builtWalls[i];
            w.paint = paint;
            builtWalls[i] = w;
            return true;
        }
        return false;
    }

    /// <summary>Server: remove a wall by id. Returns false if there's no such wall.</summary>
    public bool ServerRemoveWall(int id, out BuiltWall removed)
    {
        removed = default;
        if (!IsServer) return false;
        for (int i = 0; i < builtWalls.Count; i++)
        {
            if (builtWalls[i].id != id) continue;
            removed = builtWalls[i];
            builtWalls.RemoveAt(i);
            return true;
        }
        return false;
    }

    public bool TryGetBuiltWallById(int id, out BuiltWall wall)
    {
        foreach (var w in builtWalls)
            if (w.id == id) { wall = w; return true; }
        wall = default;
        return false;
    }

    /// <summary>A stretch of one built wall, in meters from its start (a).</summary>
    public struct WallSpan
    {
        public BuiltWall wall;
        public float from, to;

        public Vector3 Dir => Flat(wall.b - wall.a);
        public Vector3 From => wall.a + Dir * from;
        public Vector3 To => wall.a + Dir * to;
        public float Length => to - from;
        /// <summary>The whole wall.</summary>
        public static WallSpan Whole(BuiltWall w) => new() { wall = w, from = 0f, to = w.Length };
    }

    /// <summary>
    /// The parts of built walls that lie along the line from a to b (same direction, on the same line),
    /// i.e. what erasing along that line takes away. A leftover shorter than the shortest wall allowed
    /// goes too.
    /// </summary>
    public void GetEraseSpans(Vector3 a, Vector3 b, List<WallSpan> into)
    {
        into.Clear();
        var settings = BuiltWallSettings.Instance;
        if (settings == null) return;
        Vector3 e = Flat(b - a);
        if (e == Vector3.zero) return;
        static float Cross2(Vector3 x, Vector3 y) => x.x * y.z - x.z * y.x;

        foreach (var w in builtWalls)
        {
            Vector3 d = Flat(w.b - w.a);
            if (d == Vector3.zero || Mathf.Abs(Cross2(d, e)) > 0.01f) continue;          // not parallel
            if (Mathf.Abs(Cross2(d, Flat(a - w.a, false))) > settings.Thickness * 0.5f + 0.01f) continue; // not on its line

            float length = w.Length;
            float t1 = Vector3.Dot(Flat(a - w.a, false), d), t2 = Vector3.Dot(Flat(b - w.a, false), d);
            float from = Mathf.Max(0f, Mathf.Min(t1, t2)), to = Mathf.Min(length, Mathf.Max(t1, t2));
            if (to - from < 0.01f) continue;
            if (from < settings.MinLength) from = 0f;
            if (length - to < settings.MinLength) to = length;
            into.Add(new WallSpan { wall = w, from = from, to = to });
        }
    }

    /// <summary>
    /// Server: take away the parts of built walls along a to b (see GetEraseSpans); what's left of each
    /// wall stays as shorter walls. Returns the meters taken away and the refund owed.
    /// </summary>
    public float ServerEraseWalls(Vector3 a, Vector3 b, out int refund)
    {
        refund = 0;
        var settings = BuiltWallSettings.Instance;
        if (!IsServer || settings == null) return 0f;

        var spans = new List<WallSpan>();
        GetEraseSpans(a, b, spans);
        float meters = 0f;
        foreach (var span in spans)
        {
            if (!ServerRemoveWall(span.wall.id, out var w)) continue;
            bool half = w.height < BuiltWallFullHeight - 0.01f;
            Vector3 d = Flat(w.b - w.a);
            if (span.from > 0f) ServerAddWall(w.a, w.a + d * span.from, w.height, w.paint);
            if (span.to < w.Length) ServerAddWall(w.a + d * span.to, w.b, w.height, w.paint);
            meters += span.Length;
            refund += settings.GetRefund(span.Length, half);
        }
        return meters;
    }

    private void OnBuiltWallsChanged(NetworkListEvent<BuiltWall> change) => RebuildBuiltWalls();

    // Every machine builds the wall objects itself from the list. They go under an unscaled holder
    // (not under the store, which may be scaled) so they keep their exact size.
    private void RebuildBuiltWalls()
    {
        if (builtWallRoot != null)
            for (int i = builtWallRoot.childCount - 1; i >= 0; i--)
            {
                // Switched off first: Destroy waits for the end of the frame, and until then the old wall
                // would still count as holding the windows and doors that were in it.
                var old = builtWallRoot.GetChild(i).gameObject;
                old.SetActive(false);
                Destroy(old);
            }

        var settings = BuiltWallSettings.Instance;
        if (builtWalls.Count == 0 || settings == null || settings.WallPrefab == null) return;
        if (builtWallRoot == null) builtWallRoot = new GameObject($"[Built Walls] {displayName}").transform;

        float half = settings.Thickness * 0.5f;
        foreach (var w in builtWalls)
        {
            // Where exactly one other wall of the same height meets an end, both are cut at an angle so
            // the corner is clean on both sides. Elsewhere the end reaches half a thickness past its
            // point, which closes T-joins and straight corners.
            bool mitreA = TryGetMitre(w, true, out float slopeA);
            bool mitreB = TryGetMitre(w, false, out float slopeB);
            settings.GetWallPose(w.a, w.b, w.height, mitreA ? 0f : half, mitreB ? 0f : half,
                                 out Vector3 pos, out Quaternion rot, out Vector3 scale);
            var go = Instantiate(settings.WallPrefab, pos, rot, builtWallRoot);
            go.transform.localScale = scale;
            go.name = $"Built Wall {w.id}";
            var piece = go.AddComponent<BuiltWallPiece>();
            piece.Plot = this;
            piece.WallId = w.id;
            // Windows and doors can go in built walls too.
            if (!go.TryGetComponent<CuttableWall>(out var cuttable)) cuttable = go.AddComponent<CuttableWall>();
            cuttable.Plot = this;
            cuttable.SetEndSlopes(mitreA ? slopeA : 0f, mitreB ? slopeB : 0f);
            cuttable.SetPaint(w.paint);
        }
    }

    /// <summary>
    /// Built walls: should this end be cut at an angle to meet the wall joined to it? Only when exactly
    /// one other wall of the same height ends at the same point. Slope: how far along the wall the end
    /// moves per meter across it (across = the wall's right side, Cross(up, a to b)).
    /// </summary>
    private bool TryGetMitre(BuiltWall w, bool atStart, out float slope)
    {
        slope = 0f;
        Vector3 p = atStart ? w.a : w.b;
        Vector3 dir = Flat(w.b - w.a);
        if (dir == Vector3.zero) return false;

        Vector3 into = Vector3.zero;
        int found = 0;
        foreach (var o in builtWalls)
        {
            if (o.id == w.id) continue;
            bool atA = Flat(o.a - p, false).sqrMagnitude < 0.0004f;
            bool atB = !atA && Flat(o.b - p, false).sqrMagnitude < 0.0004f;
            if (!atA && !atB) continue;
            found++;
            if (Mathf.Abs(o.height - w.height) > 0.01f) return false;
            into = Flat(atA ? o.b - o.a : o.a - o.b);
        }
        if (found != 1 || into == Vector3.zero) return false;

        Vector3 dA = atStart ? dir : -dir;          // from the corner into this wall
        Vector3 across = new(dir.z, 0f, -dir.x);    // Cross(up, dir)
        Vector3 sum = dA + into;
        if (sum.sqrMagnitude < 1e-6f) return true;  // straight on: flat ends meet flush

        Vector3 bis = sum.normalized;
        static float Cross2(Vector3 x, Vector3 y) => x.x * y.z - x.z * y.x;
        float denom = Cross2(bis, dA);
        if (Mathf.Abs(denom) < 1e-4f) return false; // folds back on itself
        // A point "w" across this wall meets the other wall on the line that halves the corner.
        float along = -Cross2(bis, across) / denom;
        slope = Mathf.Clamp(atStart ? along : -along, -3f, 3f);
        return true;
    }

    private static Vector3 Flat(Vector3 v, bool normalize = true)
    {
        v.y = 0f;
        return normalize ? (v.sqrMagnitude > 1e-8f ? v.normalized : Vector3.zero) : v;
    }

    /* ========================= DOORS ========================= */

    /// <summary>
    /// Every door of this store: the Doors list plus the door of each storefront section's
    /// current module (original or one a player put in).
    /// </summary>
    private IEnumerable<GameObject> AllDoors()
    {
        if (doors != null)
            foreach (var d in doors)
                if (d != null) yield return d;

        // Doors players put in this store's walls that the store opens and shuts itself.
        foreach (var o in WallOpening.All)
            if (o != null && o.IsStoreDoor && !o.IsEditCopy && CuttableWall.HostOf(o)?.Plot == this)
                yield return o.gameObject;

        if (frontSlots == null) yield break;
        foreach (var slot in frontSlots)
        {
            GameObject shown = slot.replacement != null ? slot.replacement
                             : slot.original != null ? slot.original.gameObject : null;
            if (shown == null || !shown.TryGetComponent<WallModule>(out var wm) || wm.Door == null) continue;
            yield return wm.Door;
        }
    }

    /// <summary>
    /// Doors are shut while the store is for sale, closed or being renovated, and open while it's
    /// open. Doors with a DoorMotion animate open and shut; plain ones are shown (shut) or hidden (open).
    /// </summary>
    private void ApplyDoors()
    {
        bool shut = !HasOwner || closedByOwner.Value || IsRenovating;
        bool instant = !doorsApplied; // the first time (e.g. joining late), no animation
        doorsApplied = true;
        foreach (var d in AllDoors())
        {
            if (d.TryGetComponent<DoorMotion>(out var motion))
            {
                d.SetActive(true);
                motion.SetOpen(!shut, 1f, instant);
            }
            else d.SetActive(shut);
        }
    }

    private bool doorsApplied;

    /// <summary>A door was added to or taken out of this store: open or shut it to match the store.</summary>
    public void RefreshDoors()
    {
        if (IsSpawned) ApplyDoors();
    }

    /* ========================= WINDOWS AND DOORS ========================= */

    /// <summary>The store has front walls players put doors in (and so must keep one).</summary>
    public bool HasStorefrontWalls
    {
        get
        {
            if (storefrontWalls != null)
                foreach (var w in storefrontWalls) if (w != null) return true;
            // Front modules with flat panels count too.
            EnsureFrontSlots();
            foreach (var slot in frontSlots)
                if (slot.original != null && slot.original.GetComponentInChildren<CuttableWall>(true) != null) return true;
            return false;
        }
    }

    /// <summary>Is this item (at this pose) a door in one of this store's front walls?</summary>
    public bool IsStorefrontDoor(ItemDef def, Vector3 position, Quaternion rotation)
    {
        if (!WallOpening.TryGet(def, out var o) || !o.IsDoor) return false;
        var host = CuttableWall.HostOf(def, position, rotation);
        return host != null && host.Plot == this && host.IsStorefront;
    }

    /// <summary>Is this item (at this pose) a window or door in one of this store's walls?</summary>
    public bool IsOpeningInMyWall(ItemDef def, Vector3 position, Quaternion rotation)
    {
        var host = CuttableWall.HostOf(def, position, rotation);
        return host != null && host.Plot == this;
    }

    /// <summary>
    /// Could this window or door stay here? It must be in one of this store's walls with room around it
    /// (the editor's copies don't count, since they're the same items).
    /// </summary>
    public bool CanPlaceOpening(ItemDef def, Vector3 position, Quaternion rotation, out string reason)
    {
        reason = "Not in one of your store's walls";
        var host = CuttableWall.HostOf(def, position, rotation);
        if (host == null || host.Plot != this) return false;
        var shape = WallOpening.Measure(def, position, rotation);
        Vector3 c = host.ToWall(shape.center);
        var hole = new Rect(c.x - shape.size.x * 0.5f, c.y - shape.size.y * 0.5f, shape.size.x, shape.size.y);
        return host.CanFit(hole, null, true, out reason);
    }

    /// <summary>Is this position within range of one of the store's doors?</summary>
    public bool IsNearDoor(Vector3 position, float range)
    {
        float r2 = range * range;
        foreach (var d in AllDoors())
            if (WallModule.GeometryBounds(d).SqrDistance(position) <= r2) return true;
        return false;
    }

    /* ========================= FRONT WALL ========================= */

    private class FrontSlot
    {
        public WallModule original;    // the module placed in the scene
        public Vector3 anchor;         // middle of its mesh; replacements line up on this
        public GameObject replacement; // spawned locally when another module is chosen
        public int replacementId;
    }

    private List<FrontSlot> frontSlots;
    private NetworkList<byte> frontOverrides;

    /// <summary>Number of storefront sections this store has.</summary>
    public int FrontSlotCount { get { EnsureFrontSlots(); return frontSlots.Count; } }

    /// <summary>
    /// This store's storefront sections: the Front Walls list if it has any entries; otherwise,
    /// with Auto Find Front Walls on, every WallModule within Front Wall Reach that isn't closer
    /// to another store. Either way every machine numbers the sections the same way.
    /// </summary>
    private void EnsureFrontSlots()
    {
        if (frontSlots != null) return;
        frontSlots = new List<FrontSlot>();
        foreach (var (module, center) in FindFrontModules())
        {
            frontSlots.Add(new FrontSlot { original = module, anchor = center });
            MarkStorefrontPanels(module.gameObject);
        }
    }

    // A front module's flat panels (CuttableWalls) are this store's front wall: windows and doors go
    // in them, and the store needs a door in one of them.
    private void MarkStorefrontPanels(GameObject module)
    {
        foreach (var w in module.GetComponentsInChildren<CuttableWall>(true))
        {
            w.Plot = this;
            w.IsStorefront = true;
        }
    }

    private List<(WallModule module, Vector3 center)> FindFrontModules()
    {
        var result = new List<(WallModule module, Vector3 center)>();

        // Hand-picked list: use exactly these, in this order.
        if (frontWalls != null && frontWalls.Length > 0)
        {
            foreach (var m in frontWalls)
                if (m != null) result.Add((m, WallModule.GeometryBounds(m.gameObject).center));
            return result;
        }

        if (!autoFindFrontWalls) return result;
        return AutoFindFrontModules();
    }

    private List<(WallModule module, Vector3 center)> AutoFindFrontModules()
    {
        var allPlots = FindObjectsByType<StorePlot>(FindObjectsSortMode.None);
        var claimed = new List<(WallModule module, Vector3 center)>();

        foreach (var m in FindObjectsByType<WallModule>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (m == null || m.IsReplacement) continue;
            if (IsListedByAnotherStore(m, allPlots)) continue;

            Vector3 center = WallModule.GeometryBounds(m.gameObject).center;
            float mine = DistanceToSpace(center);
            if (mine > frontWallReach) continue;

            bool closest = true;
            foreach (var other in allPlots)
            {
                if (other == this) continue;
                float theirs = other.DistanceToSpace(center);
                if (theirs < mine || (Mathf.Approximately(theirs, mine) && other.plotId < plotId))
                {
                    closest = false;
                    break;
                }
            }
            if (closest) claimed.Add((m, center));
        }

        claimed.Sort((a, b) =>
        {
            int c = Mathf.RoundToInt(a.center.x * 100f).CompareTo(Mathf.RoundToInt(b.center.x * 100f));
            if (c == 0) c = Mathf.RoundToInt(a.center.z * 100f).CompareTo(Mathf.RoundToInt(b.center.z * 100f));
            if (c == 0) c = Mathf.RoundToInt(a.center.y * 100f).CompareTo(Mathf.RoundToInt(b.center.y * 100f));
            return c;
        });

        return claimed;
    }

    private bool IsListedByAnotherStore(WallModule m, StorePlot[] allPlots)
    {
        foreach (var other in allPlots)
        {
            if (other == this || other.frontWalls == null) continue;
            foreach (var w in other.frontWalls)
                if (w == m) return true;
        }
        return false;
    }

    /// <summary>How far (meters) a point is from this store's space; 0 if inside.</summary>
    private float DistanceToSpace(Vector3 worldPoint)
    {
        float best = float.MaxValue;
        foreach (var box in baseAreas) best = Mathf.Min(best, DistanceToBox(box, worldPoint));
        if (segments != null)
            foreach (var seg in segments)
                if (seg != null)
                    foreach (var box in seg.Areas) best = Mathf.Min(best, DistanceToBox(box, worldPoint));
        return best;
    }

    private static float DistanceToBox(BoxCollider box, Vector3 worldPoint)
    {
        if (box == null) return float.MaxValue;
        Vector3 local = box.transform.InverseTransformPoint(worldPoint) - box.center;
        Vector3 half = box.size * 0.5f;
        Vector3 scale = box.transform.lossyScale;
        var outside = new Vector3(
            Mathf.Max(0f, Mathf.Abs(local.x) - half.x) * Mathf.Abs(scale.x),
            Mathf.Max(0f, Mathf.Abs(local.y) - half.y) * Mathf.Abs(scale.y),
            Mathf.Max(0f, Mathf.Abs(local.z) - half.z) * Mathf.Abs(scale.z));
        return outside.magnitude;
    }

    private void OnFrontOverridesChanged(NetworkListEvent<byte> e) => ApplyFrontWall();

    /// <summary>Make every storefront section show the module the synced state says.</summary>
    private void ApplyFrontWall()
    {
        EnsureFrontSlots();
        for (int i = 0; i < frontSlots.Count; i++)
            SetSlotVisual(i, i < frontOverrides.Count ? frontOverrides[i] : 0);
        ApplyDoors(); // a swapped-in doorway brings its own door
        ApplyFrontPaint();
    }

    /* ========================= PAINT ========================= */

    /// <summary>The storefront's paint color (all its panels and marked parts).</summary>
    public byte FrontPaint => frontPaint.Value;
    /// <summary>The floor's paint color.</summary>
    public byte FloorPaint => floorPaint.Value;
    /// <summary>Does this store have a floor that can be painted?</summary>
    public bool HasPaintableFloor
    {
        get
        {
            if (floorRenderers != null) foreach (var r in floorRenderers) if (r != null) return true;
            return false;
        }
    }

    /// <summary>Is this object part of the store's paintable floor?</summary>
    public bool IsFloor(Transform t)
    {
        if (t == null || floorRenderers == null) return false;
        foreach (var r in floorRenderers)
            if (r != null && t.IsChildOf(r.transform)) return true;
        return false;
    }

    /// <summary>Is this object part of the storefront (a panel or a front module)?</summary>
    public bool IsStorefrontPart(Transform t)
    {
        if (t == null) return false;
        var wall = t.GetComponentInParent<CuttableWall>();
        if (wall != null && wall.Plot == this && wall.IsStorefront) return true;
        return TryGetFrontSlot(t, out _);
    }

    public void ServerPaintFront(byte id)
    {
        if (IsServer && PaintPalette.IsValid(id)) frontPaint.Value = id;
    }

    public void ServerPaintFloor(byte id)
    {
        if (IsServer && PaintPalette.IsValid(id)) floorPaint.Value = id;
    }

    private void OnFrontPaintChanged(byte before, byte now) => ApplyFrontPaint();
    private void OnFloorPaintChanged(byte before, byte now) => ApplyFloorPaint();

    // Every storefront panel, plus the marked parts (PaintParts) of the front modules shown.
    /// <summary>
    /// This machine only: show the storefront / floor in a color for a moment (the paint brush's
    /// preview). Show Paint() puts the synced colors back.
    /// </summary>
    public void PreviewFrontPaint(byte id) => ApplyFrontPaint(id);
    public void PreviewFloorPaint(byte id) => ApplyFloorPaint(id);

    /// <summary>Show the storefront and floor in their synced colors again (ends a preview).</summary>
    public void ShowPaint()
    {
        ApplyFrontPaint();
        ApplyFloorPaint();
    }

    private void ApplyFrontPaint() => ApplyFrontPaint(frontPaint.Value);

    private void ApplyFrontPaint(byte id)
    {
        foreach (var w in CuttableWall.All)
            if (w != null && w.Plot == this && w.IsStorefront) w.SetPaint(id);
        if (storefrontWalls != null)
            foreach (var w in storefrontWalls) if (w != null) w.SetPaint(id); // also while switched off
        EnsureFrontSlots();
        for (int i = 0; i < frontSlots.Count; i++)
        {
            var shown = GetFrontSlotShown(i);
            if (shown == null) continue;
            foreach (var w in shown.GetComponentsInChildren<CuttableWall>(true)) w.SetPaint(id);
            foreach (var parts in shown.GetComponentsInChildren<PaintParts>(true)) parts.Apply(id);
        }
    }

    private void ApplyFloorPaint() => ApplyFloorPaint(floorPaint.Value);

    private void ApplyFloorPaint(byte id)
    {
        if (floorRenderers == null) return;
        foreach (var r in floorRenderers) PaintPalette.Apply(r, -1, id);
    }

    private void SetSlotVisual(int index, int moduleId)
    {
        var slot = frontSlots[index];
        if (slot.original == null) return;

        // Back to the module the scene placed here.
        if (moduleId == 0 || moduleId == slot.original.ModuleId || !FrontWallCatalog.TryGet(moduleId, out var prefab))
        {
            if (slot.replacement != null) Destroy(slot.replacement);
            slot.replacement = null;
            slot.replacementId = 0;
            slot.original.gameObject.SetActive(true);
            return;
        }

        if (slot.replacement != null && slot.replacementId == moduleId) return;
        if (slot.replacement != null) Destroy(slot.replacement);

        // Same rotation, scale and parent as the original, then slide it so the meshes line up
        // (the prefabs' pivots are in different places, so matching positions wouldn't work).
        Transform t = slot.original.transform;
        var go = Instantiate(prefab.gameObject, t.position, t.rotation, t.parent);
        go.transform.localScale = t.localScale;
        go.name = $"{prefab.name} ({displayName} front {index})";
        if (go.TryGetComponent<WallModule>(out var wm)) wm.IsReplacement = true;
        if (prefab.FitToSection) FitToSection(go, slot.original.gameObject, t);
        go.transform.position += slot.anchor - WallModule.GeometryBounds(go).center;
        MarkStorefrontPanels(go);

        slot.original.gameObject.SetActive(false);
        slot.replacement = go;
        slot.replacementId = moduleId;
    }

    // Stretch a swapped-in module to the length and height of the section it replaces; the thinnest
    // side (its thickness) stays as built.
    private static void FitToSection(GameObject module, GameObject section, Transform frame)
    {
        Bounds want = WallModule.GeometryBoundsIn(section, frame);
        Bounds have = WallModule.GeometryBoundsIn(module, frame);
        Vector3 w = want.size, h = have.size;
        int thin = w.x <= w.y && w.x <= w.z ? 0 : w.y <= w.z ? 1 : 2;
        Vector3 f = Vector3.one;
        for (int a = 0; a < 3; a++)
            if (a != thin && h[a] > 1e-3f && w[a] > 1e-3f) f[a] = w[a] / h[a];
        module.transform.localScale = Vector3.Scale(module.transform.localScale, f);
    }

    /// <summary>The module type a section currently shows.</summary>
    public int GetFrontModuleId(int index)
    {
        EnsureFrontSlots();
        if (index < 0 || index >= frontSlots.Count || frontSlots[index].original == null) return 0;
        int over = index < frontOverrides.Count ? frontOverrides[index] : 0;
        return over != 0 ? over : frontSlots[index].original.ModuleId;
    }

    /// <summary>Name of the module a section shows, for menus.</summary>
    public string GetFrontModuleName(int index)
    {
        int id = GetFrontModuleId(index);
        if (FrontWallCatalog.TryGet(id, out var m)) return m.DisplayName;
        return index >= 0 && index < FrontSlotCount && frontSlots[index].original != null
            ? frontSlots[index].original.DisplayName : "?";
    }

    /// <summary>The object a section shows right now (its scene module, or the one swapped in).</summary>
    public GameObject GetFrontSlotShown(int index)
    {
        EnsureFrontSlots();
        if (index < 0 || index >= frontSlots.Count) return null;
        var slot = frontSlots[index];
        return slot.replacement != null ? slot.replacement : slot.original != null ? slot.original.gameObject : null;
    }

    /// <summary>World box around a section's current module (for highlighting).</summary>
    public Bounds GetFrontSlotBounds(int index)
    {
        EnsureFrontSlots();
        var slot = frontSlots[index];
        GameObject shown = slot.replacement != null ? slot.replacement : slot.original.gameObject;
        return WallModule.GeometryBounds(shown);
    }

    /// <summary>Which storefront section (if any) this object is part of.</summary>
    public bool TryGetFrontSlot(Transform t, out int index)
    {
        EnsureFrontSlots();
        index = -1;
        var module = t != null ? t.GetComponentInParent<WallModule>() : null;
        if (module == null) return false;

        for (int i = 0; i < frontSlots.Count; i++)
        {
            var slot = frontSlots[i];
            if (slot.original == module || (slot.replacement != null && slot.replacement == module.gameObject))
            {
                index = i;
                return true;
            }
        }
        return false;
    }

    /// <summary>True for the renovation and for-sale walls (they stand in front of the storefront).</summary>
    public bool IsStorefrontCover(Transform t)
    {
        if (t == null) return false;
        foreach (var arr in new[] { renovationWalls, forSaleWalls })
        {
            if (arr == null) continue;
            foreach (var w in arr)
                if (w != null && t.IsChildOf(w.transform)) return true;
        }
        return false;
    }

    /// <summary>
    /// Would putting this module in this section leave the store without a doorway?
    /// Only counts if the store had a doorway to begin with.
    /// </summary>
    public bool WouldRemoveLastEntrance(int index, int moduleId)
    {
        if (FrontWallCatalog.Instance != null && !FrontWallCatalog.Instance.RequireEntrance) return false;
        return CountEntrances(-1, 0) > 0 && CountEntrances(index, moduleId) == 0;
    }

    private int CountEntrances(int changedIndex, int newId)
    {
        EnsureFrontSlots();
        int n = 0;
        for (int i = 0; i < frontSlots.Count; i++)
        {
            var original = frontSlots[i].original;
            if (original == null) continue;
            int id = i == changedIndex ? (newId == 0 ? original.ModuleId : newId) : GetFrontModuleId(i);
            bool entrance = id == original.ModuleId ? original.IsEntrance
                          : FrontWallCatalog.TryGet(id, out var m) && m.IsEntrance;
            if (entrance) n++;
        }
        return n;
    }

    /// <summary>
    /// Server: swap one storefront section. Fails for unknown modules, bad indexes, or a change
    /// that would remove the store's last doorway. Every player sees the change.
    /// </summary>
    public bool ServerTrySetFrontModule(int index, int moduleId)
    {
        if (!IsServer) return false;
        EnsureFrontSlots();
        if (index < 0 || index >= frontSlots.Count || frontSlots[index].original == null) return false;
        if (moduleId < 0 || moduleId > 255) return false;
        if (moduleId != 0 && moduleId != frontSlots[index].original.ModuleId && !FrontWallCatalog.TryGet(moduleId, out _))
            return false;
        if (WouldRemoveLastEntrance(index, moduleId)) return false;

        if (moduleId == frontSlots[index].original.ModuleId) moduleId = 0;
        while (frontOverrides.Count < frontSlots.Count) frontOverrides.Add(0);
        frontOverrides[index] = (byte)moduleId;
        return true;
    }

    /* ========================= EDIT SESSIONS (SERVER) ========================= */

    /// <summary>One item taken out of the world for an edit session.</summary>
    public struct EditEntry
    {
        public int itemId;
        public Vector3 position;
        public Quaternion rotation;
        public StockContainer.ZoneStock[] stock; // what was in its StockContainer, if it has one
        public int units;   // products left, if it's a stock crate
        public int price;   // its price tag (cents), if it's a loose Sellable
        public uint paint;  // its paint colors (PaintSet)
        public bool removed; // taken out during the session (not put back)
    }

    private readonly List<EditEntry> editSession = new();

    /// <summary>Server: the items in the current edit session, in the order sent to the editor.</summary>
    public IReadOnlyList<EditEntry> ServerEditSession => editSession;

    /// <summary>Server: bumps every session so late messages from an old one are ignored.</summary>
    public int ServerEditSessionId { get; private set; }

    /// <summary>
    /// Server only. Starts (clientId) or ends (NoOwner) edit mode.
    ///
    /// Starting records every item in the store and despawns it; the editor works on local
    /// copies, so nothing is sent over the network while they drag things around. Ending this
    /// way (disconnect, store released) respawns everything where it was. A normal finish goes
    /// through ServerEndEdit with the editor's final positions instead.
    /// Use MallManager.ServerTryStartEdit / ServerStopEdit rather than calling this directly.
    /// </summary>
    public void ServerSetEditor(ulong clientId)
    {
        if (!IsServer || editorClientId.Value == clientId) return;

        if (IsRenovating) ServerEndEdit(null, null, null); // put the old session's items back
        if (clientId == NoOwner) return;

        editSession.Clear();
        ServerEditSessionId++;

        var catalog = ItemCatalog.Instance;
        foreach (var item in ItemPickupNet.All.ToArray())
        {
            if (item == null || !item.IsSpawned) continue;
            // Windows and doors in this store's walls count even if they stick out of its space.
            bool inMyWall = item.TryGetComponent<WallOpening>(out var opening) && CuttableWall.HostOf(opening)?.Plot == this;
            if (!inMyWall && !Contains(item.transform.position)) continue;
            if (item.TryGetComponent<ShipmentPackage>(out _)) continue; // boxes stay where they are
            if (item.IsOnCart) continue;                                 // so do things riding on a cart

            // Only take items we can respawn later; anything else is left untouched.
            if (catalog == null || !catalog.TryGet(item.itemId, out var def) || def.worldNetworkPrefab == null)
            {
                Debug.LogWarning($"[StorePlot] '{item.name}' (item id {item.itemId}) has no catalog entry " +
                                 "with a world prefab, so it can't be edited and stays where it is.", item);
                continue;
            }

            editSession.Add(new EditEntry
            {
                itemId = item.itemId,
                position = item.transform.position,
                rotation = item.transform.rotation,
                stock = item.TryGetComponent<StockContainer>(out var container) ? container.ServerCopyStock() : null,
                units = item.IsStockCrate ? item.Units : 0,
                price = item.TryGetComponent<Sellable>(out var sellable) ? sellable.Price : 0,
                paint = item.Paint,
            });
            item.NetworkObject.Despawn(true);
        }

        editorClientId.Value = clientId;
    }

    /// <summary>A window or door taken out of its box in edit mode (spawned, and the box used up, on save).</summary>
    public struct NewEditItem
    {
        public int itemId;
        public Vector3 position;
        public Quaternion rotation;
        public uint paint;   // PaintSet
        public ulong boxId;  // the ShipmentPackage it came out of
    }

    // Windows and doors taken out of walls this session: they go back in boxes when it ends.
    private readonly List<(int itemId, uint paint, Vector3 near)> boxesOnSave = new();

    /// <summary>
    /// Server only. Ends the edit session and respawns its items. Each item uses the editor's
    /// position when one was given and it passes the checks (inside this store, isValid);
    /// otherwise it goes back to where it was before editing.
    ///
    /// Windows and doors (WallOpening) bought during the session come in Added and are paid for here,
    /// by Team. They're checked against this store's walls instead of its space. Items taken out
    /// during the session (ServerRemoveEditItem) aren't put back.
    /// </summary>
    public void ServerEndEdit(Vector3[] positions, Quaternion[] rotations,
                              Func<ItemDef, Vector3, Quaternion, bool> isValid, bool log = false,
                              IList<NewEditItem> added = null, ulong team = NoOwner)
    {
        if (!IsServer || !IsRenovating) return;

        int moved = 0, unchanged = 0, rejected = 0, noPose = 0, sold = 0, bought = 0, notBought = 0;
        var catalog = ItemCatalog.Instance;
        var econ = EconomyManager.Instance;

        bool Allowed(ItemDef def, Vector3 p, Quaternion r)
        {
            if (WallOpening.IsOpening(def))
            {
                Physics.SyncTransforms();
                if (!CanPlaceOpening(def, p, r, out string why))
                {
                    if (log) Debug.LogWarning($"[StorePlot] '{def.displayName}' can't go at {p}: {why}.", this);
                    return false;
                }
            }
            else if (!ContainsItem(def, p, r))
            {
                if (log) Debug.LogWarning($"[StorePlot] '{def.displayName}' was moved outside '{displayName}' " +
                                          $"(to {p}); it goes back to where it was.", this);
                return false;
            }
            Physics.SyncTransforms(); // see the items respawned earlier in this loop
            return isValid == null || isValid(def, p, r);
        }

        for (int i = 0; i < editSession.Count; i++)
        {
            var entry = editSession[i];
            if (catalog == null || !catalog.TryGet(entry.itemId, out var def) || def.worldNetworkPrefab == null)
                continue;

            // Taken out during the session (already refunded).
            if (entry.removed)
            {
                sold++;
                continue;
            }

            Vector3 pos = entry.position;
            Quaternion rot = entry.rotation;

            if (positions != null && rotations != null && i < positions.Length && i < rotations.Length)
            {
                Vector3 p = positions[i];
                Quaternion r = rotations[i];
                if (IsFinite(p) && IsFinite(r))
                {
                    r = r.normalized;
                    bool samePlace = (p - pos).sqrMagnitude < 1e-6f && Quaternion.Angle(r, rot) < 0.1f;
                    if (Allowed(def, p, r))
                    {
                        if (samePlace) unchanged++; else moved++;
                        pos = p;
                        rot = r;
                    }
                    else rejected++;
                }
                else noPose++;
            }
            else noPose++;

            // A window or door whose wall is gone (and nowhere valid to go) is sold back, not left floating.
            if (WallOpening.IsOpening(def))
            {
                Physics.SyncTransforms();
                if (!CanPlaceOpening(def, pos, rot, out string why))
                {
                    if (log) Debug.LogWarning($"[StorePlot] '{def.displayName}' has no wall to sit in ({why}); back in its box.", this);
                    boxesOnSave.Add((entry.itemId, entry.paint, pos));
                    sold++;
                    continue;
                }
            }

            SpawnEditItem(def, pos, rot, entry.units, entry.price, entry.stock, entry.paint);
        }

        // Windows and doors taken out of their boxes in edit mode: each box is used up.
        if (added != null)
        {
            var usedBoxes = new HashSet<ulong>();
            foreach (var n in added)
            {
                if (catalog == null || !catalog.TryGet(n.itemId, out var def) || def.worldNetworkPrefab == null ||
                    !WallOpening.IsOpening(def) || !IsFinite(n.position) || !IsFinite(n.rotation))
                { notBought++; continue; }
                if (!usedBoxes.Add(n.boxId) || !TryGetPieceBox(n.boxId, n.itemId, out var box))
                {
                    if (log) Debug.LogWarning($"[StorePlot] The box for '{def.displayName}' isn't in the store any more.", this);
                    notBought++;
                    continue;
                }

                Quaternion r = n.rotation.normalized;
                if (!Allowed(def, n.position, r)) { notBought++; continue; }
                uint paint = box.ContainedPaint;
                box.NetworkObject.Despawn(true);
                SpawnEditItem(def, n.position, r, 0, 0, null, paint);
                bought++;
            }
        }

        // Windows and doors taken out of walls go back in their boxes, near where they were.
        foreach (var (itemId, paint, near) in boxesOnSave) ServerSpawnBoxNear(itemId, paint, near);
        boxesOnSave.Clear();

        if (log)
            Debug.Log($"[StorePlot] Edit of '{displayName}' saved: {moved} moved, {unchanged} unchanged, " +
                      $"{rejected} put back (see warnings above), {noPose} with no position from the editor, " +
                      $"{sold} removed, {bought} windows/doors put in, {notBought} not put in.", this);

        editSession.Clear();
        editorClientId.Value = NoOwner;
    }

    /// <summary>Can this item be taken out (sold back) in edit mode? Windows, doors and wall items.</summary>
    public static bool CanRemoveInEdit(ItemDef def) =>
        def != null && (WallOpening.IsOpening(def) || PlacementCheck.GetMounting(def) != ItemPickupNet.Mounting.Floor);

    /// <summary>
    /// Server: take an item out of the edit session for good (it won't come back on save) and give
    /// Team part of its price back. Only windows, doors and wall items.
    /// </summary>
    public bool ServerRemoveEditItem(int index, ulong team, out int refund)
    {
        refund = 0;
        if (!IsServer || !IsRenovating || index < 0 || index >= editSession.Count) return false;
        var entry = editSession[index];
        var catalog = ItemCatalog.Instance;
        if (entry.removed || catalog == null || !catalog.TryGet(entry.itemId, out var def) || !CanRemoveInEdit(def)) return false;

        entry.removed = true;
        editSession[index] = entry;

        // A window or door goes back in its box, on the floor near where it was (when the session ends).
        if (WallOpening.IsOpening(def))
        {
            boxesOnSave.Add((entry.itemId, entry.paint, entry.position));
            return true;
        }

        refund = RemovalRefund(def);
        var econ = EconomyManager.Instance;
        if (econ != null && econ.IsSpawned && team != NoOwner) econ.ServerAddMoney(team, refund);
        return true;
    }

    /// <summary>
    /// A delivery box in (or just outside) this store holding this window or door, not held for
    /// another team. Edit mode takes pieces out of these.
    /// </summary>
    public bool TryGetPieceBox(ulong boxId, int itemId, out ShipmentPackage box)
    {
        box = null;
        var nm = NetworkManager;
        if (nm == null || nm.SpawnManager == null || !nm.SpawnManager.SpawnedObjects.TryGetValue(boxId, out var no)) return false;
        if (!no.TryGetComponent(out box) || box.ContainedItemId != itemId) return false;
        if (!IsInsideOrNear(no.transform.position, 1f)) return false;
        return !box.IsReservedAgainst(ownerTeamId.Value);
    }

    /// <summary>
    /// Server: put a window or door back in its shipment box, on this store's floor near a point (a spot
    /// that's clear, a little into the store). Without a clear spot it's set down there and settles.
    /// </summary>
    public void ServerSpawnBoxNear(int itemId, uint paint, Vector3 near)
    {
        if (!IsServer || ItemCatalog.Instance == null || !ItemCatalog.Instance.TryGet(itemId, out var def)) return;
        var prefab = PlacementCheck.CartBoxOf(def);
        if (prefab == null) prefab = def.GetShipmentPrefab();
        if (prefab == null) return;

        float floor = FloorHeight;
        Bounds space = GetWorldBounds();
        Vector3 inward = space.center - near;
        inward.y = 0f;
        inward = inward.sqrMagnitude > 1e-4f ? inward.normalized : Vector3.forward;
        Vector3 start = new Vector3(near.x, floor, near.z) + inward * 0.6f;
        Quaternion rot = Quaternion.LookRotation(inward, Vector3.up);
        float lift = PlacementCheck.PivotHeightAboveBase(prefab.gameObject);

        int ground = LayerMask.NameToLayer("Ground");
        int blocking = Physics.DefaultRaycastLayers & ~(ground >= 0 ? 1 << ground : 0);
        Vector3 spot = start + Vector3.up * (lift + 0.3f); // fallback: drop it there
        Physics.SyncTransforms();
        bool found = false;
        for (int ring = 0; ring <= 6 && !found; ring++)
            for (int k = 0; k < Mathf.Max(1, ring * 8) && !found; k++)
            {
                float a = k * Mathf.PI * 2f / Mathf.Max(1, ring * 8);
                Vector3 p = start + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * (ring * 0.45f);
                if (!Contains(p + Vector3.up * 0.1f)) continue;
                Vector3 at = p + Vector3.up * lift;
                if (PlacementCheck.FitsPrefab(prefab.gameObject, at, rot, blocking, Physics.DefaultRaycastLayers))
                {
                    spot = at;
                    found = true;
                }
            }

        var spawned = Instantiate(prefab, spot, rot);
        if (spawned.TryGetComponent<ShipmentPackage>(out var box))
        {
            box.ServerSetContents(itemId);
            box.ServerSetPaint(paint);
        }
        spawned.Spawn(true);
    }

    /// <summary>What removing a wall item in edit mode gives back.</summary>
    public static int RemovalRefund(ItemDef def)
    {
        float share = BuiltWallSettings.Instance != null ? BuiltWallSettings.Instance.OpeningRefundFraction : 0.5f;
        // The lower of its normal and current price, so buying in a sale and selling later isn't a profit.
        return Mathf.FloorToInt(Mathf.Min(def.price, MarketModifiers.OrderCost(def)) * share);
    }

    private static readonly HashSet<int> WarnedNested = new();

    /// <summary>
    /// NGO can't spawn a prefab with a NetworkObject inside another one (only scene objects can), so a
    /// child's DoorNet would silently not work. Says how to fix the prefab, once per item.
    /// </summary>
    public static void WarnIfNestedNetworkObjects(ItemDef def, GameObject instance)
    {
        if (def == null || WarnedNested.Contains(def.id)) return;
        var all = instance.GetComponentsInChildren<NetworkObject>(true);
        if (all.Length <= 1) return;
        WarnedNested.Add(def.id);
        var names = new List<string>();
        foreach (var no in all) if (no.gameObject != instance) names.Add(no.name);
        Debug.LogError($"[StorePlot] '{def.displayName}' has a NetworkObject inside it ({string.Join(", ", names)}). " +
                       "Spawned items can only have one, on the root. Fix the prefab: remove the NetworkObject (and " +
                       "DoorNet) from the child, and put DoorMotion + DoorNet on the item's root instead, with the " +
                       "child's hinge as the DoorMotion leaf.", def.worldNetworkPrefab);
    }

    private void SpawnEditItem(ItemDef def, Vector3 pos, Quaternion rot, int units, int price, StockContainer.ZoneStock[] stock,
                               uint paint = 0)
    {
        var spawned = Instantiate(def.worldNetworkPrefab, pos, rot);
        WarnIfNestedNetworkObjects(def, spawned.gameObject);
        // Edit mode already let the player choose whether it falls, so it comes back exactly as left.
        if (spawned.TryGetComponent<ItemPickupNet>(out var item))
        {
            // Hung on a wall in edit mode? Then it stays up even if what's below it is picked up.
            // Windows and doors are always held by their wall.
            bool onWall = WallOpening.IsOpening(def) ||
                          (item.MountingType != ItemPickupNet.Mounting.Floor &&
                           PlacementCheck.HasWallBehind(def, pos, rot, PlacementCheck.DefaultWallMask));
            item.ServerPrepareFrozen(onWall);
            if (units > 0) item.ServerSetUnits(units);
            if (price > 0 && spawned.TryGetComponent<Sellable>(out var sellable)) sellable.ServerSetPrice(price, restore: true);
            if (paint != 0) item.ServerSetPaint(paint);
        }
        if (stock != null && spawned.TryGetComponent<StockContainer>(out var container))
            container.ServerPrepareStock(stock);
        // A lockable door belongs to this store.
        if (spawned.TryGetComponent<DoorNet>(out var door)) door.AssignStoreIfUnset(this);
        spawned.Spawn(true);
    }

    /// <summary>True if the middle of the item at this pose is inside the store.</summary>
    public bool ContainsItem(ItemDef def, Vector3 position, Quaternion rotation)
    {
        Vector3 center = PlacementCheck.GetTestBox(def, position, rotation, out Vector3 c, out _) ? c : position;
        return Contains(center);
    }

    private static bool IsFinite(Vector3 v) =>
        float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

    private static bool IsFinite(Quaternion q) =>
        float.IsFinite(q.x) && float.IsFinite(q.y) && float.IsFinite(q.z) && float.IsFinite(q.w) &&
        (q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w) > 1e-6f;

    /// <summary>
    /// Height (world Y) above which the edit camera draws nothing: the top of the store's
    /// space, or the override.
    /// </summary>
    public float GetEditCutHeight() => overrideCutHeight ? cutHeight : GetWorldBounds().max.y;

    /// <summary>
    /// Adds a world-space box for each piece of this store's space (base areas plus purchased
    /// segments) to the list. Used by edit mode to hide everything that isn't in the store.
    /// </summary>
    public void GetAreaBounds(List<Bounds> into)
    {
        foreach (var box in baseAreas)
            if (box != null) into.Add(BoxWorldBounds(box));
        for (int i = 0; i < SegmentCount; i++)
            if (IsSegmentPurchased(i))
                foreach (var box in segments[i].Areas) into.Add(BoxWorldBounds(box));
    }

    /// <summary>Adds the store's space boxes (base areas plus purchased segments) to the list.</summary>
    public void GetAreaBoxes(List<BoxCollider> into)
    {
        foreach (var box in baseAreas)
            if (box != null) into.Add(box);
        for (int i = 0; i < SegmentCount; i++)
            if (IsSegmentPurchased(i))
                foreach (var box in segments[i].Areas) into.Add(box);
    }

    private static Bounds BoxWorldBounds(BoxCollider box)
    {
        Vector3 c = box.center, e = box.size * 0.5f;
        var b = new Bounds(box.transform.TransformPoint(c), Vector3.zero);
        for (int i = 0; i < 8; i++)
            b.Encapsulate(box.transform.TransformPoint(c + new Vector3(
                (i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z)));
        return b;
    }

    /// <summary>
    /// Where a player should appear for this store: the Spawn Point if set, otherwise just
    /// outside the storefront facing in. The height is the store's floor; the player adjusts
    /// for its own size.
    /// </summary>
    public void GetSpawnPose(out Vector3 position, out Quaternion rotation)
    {
        if (spawnPoint != null)
        {
            position = spawnPoint.position;
            rotation = Quaternion.Euler(0f, spawnPoint.eulerAngles.y, 0f);
            return;
        }

        Bounds store = GetWorldBounds();
        float floor = store.min.y;

        // The storefront is where the renovation walls (or for-sale walls) stand.
        if (TryGetWallsCenter(renovationWalls, out Vector3 front) || TryGetWallsCenter(forSaleWalls, out front))
        {
            Vector3 outward = front - store.center;
            outward.y = 0f;
            if (outward.sqrMagnitude < 1e-4f) outward = Vector3.forward;
            outward.Normalize();

            position = new Vector3(front.x, floor, front.z) + outward * spawnDistance;
            rotation = Quaternion.LookRotation(-outward, Vector3.up);
            return;
        }

        // No storefront walls to go by: stand in the middle of the store.
        position = new Vector3(store.center.x, floor, store.center.z);
        rotation = Quaternion.identity;
    }

    /// <summary>Center of a set of wall objects (from their renderers/colliders, even while hidden).</summary>
    private static bool TryGetWallsCenter(GameObject[] walls, out Vector3 center)
    {
        center = default;
        if (walls == null) return false;

        bool any = false;
        Bounds b = default;
        foreach (var w in walls)
        {
            if (w == null) continue;
            foreach (var r in w.GetComponentsInChildren<Renderer>(true))
            {
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            if (!any) { b = new Bounds(w.transform.position, Vector3.zero); any = true; }
        }
        if (any) center = b.center;
        return any;
    }

    /// <summary>World-space box around the base space plus every purchased segment.</summary>
    public Bounds GetWorldBounds()
    {
        bool any = false;
        Bounds result = default;

        void Add(BoxCollider box)
        {
            if (box == null) return;
            Vector3 c = box.center, e = box.size * 0.5f;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = box.transform.TransformPoint(c + new Vector3(
                    (i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z));
                if (!any) { result = new Bounds(corner, Vector3.zero); any = true; }
                else result.Encapsulate(corner);
            }
        }

        foreach (var box in baseAreas) Add(box);
        for (int i = 0; i < SegmentCount; i++)
            if (IsSegmentPurchased(i))
                foreach (var box in segments[i].Areas) Add(box);

        return any ? result : new Bounds(transform.position, Vector3.one);
    }

    public void ServerUnlockSegment(int index)
    {
        if (!IsServer || index < 0 || index >= SegmentCount) return;
        purchasedSegments.Value |= 1 << index;
    }

    /// <summary>
    /// How many things are hung on a segment's walls or set into them (windows, doors). Unlocking takes
    /// those walls away, so they'd be left floating: they have to come off first. Works on every
    /// machine (from the spawned items).
    /// </summary>
    public int CountItemsOnSegmentWalls(int index)
    {
        if (index < 0 || index >= SegmentCount || segments[index].falseWalls == null) return 0;
        var walls = segments[index].falseWalls;
        bool InSegmentWall(Transform t)
        {
            foreach (var w in walls)
                if (w != null && w.activeInHierarchy && t.IsChildOf(w.transform)) return true;
            return false;
        }
        bool IsSegmentWall(Collider c) => InSegmentWall(c.transform);

        float floor = FloorHeight;
        int count = 0;
        var catalog = ItemCatalog.Instance;
        foreach (var item in ItemPickupNet.All)
        {
            if (item == null || !item.IsSpawned) continue;
            if (item.TryGetComponent<WallOpening>(out var o))
            {
                var host = CuttableWall.HostOf(o);
                if (host != null && InSegmentWall(host.transform)) count++;
                continue;
            }
            if (catalog == null || !catalog.TryGet(item.itemId, out var def)) continue;
            if (PlacementCheck.IsHungAgainst(def, item.transform.position, item.transform.rotation, floor, IsSegmentWall)) count++;
        }
        return count;
    }

    public int GetSegmentPrice(int index) =>
        index >= 0 && index < SegmentCount ? segments[index].price : 0;

    public string GetSegmentName(int index) =>
        index >= 0 && index < SegmentCount ? segments[index].name : "";

    /// <summary>Server: is this position within buy range of one of a segment's walls?</summary>
    public bool IsNearSegmentWall(int index, Vector3 position)
    {
        if (index < 0 || index >= SegmentCount || segments[index].falseWalls == null) return false;
        foreach (var w in segments[index].falseWalls)
        {
            if (w == null || !w.activeInHierarchy) continue;
            foreach (var col in w.GetComponentsInChildren<Collider>())
                if (col.enabled && Vector3.Distance(col.ClosestPoint(position), position) <= buyRange) return true;
        }
        return false;
    }

    /* ========================= WALLS ========================= */

    private void ApplyWalls()
    {
        for (int i = 0; i < SegmentCount; i++)
        {
            bool open = IsSegmentPurchased(i);
            var walls = segments[i].falseWalls;
            if (walls == null) continue;
            foreach (var w in walls)
                if (w != null) w.SetActive(!open);
        }
    }

    /// <summary>For-sale walls while unowned; owner-only visuals for the owner's screen only.</summary>
    private void ApplyOwnerVisuals()
    {
        if (forSaleWalls != null)
            foreach (var w in forSaleWalls)
                if (w != null) w.SetActive(!HasOwner);

        bool mine = false;
        var nm = NetworkManager.Singleton;
        if (nm != null && nm.IsListening) mine = IsAuthorized(nm.LocalClientId);

        if (ownerOnlyVisuals != null)
            foreach (var v in ownerOnlyVisuals)
                if (v != null) v.SetActive(mine);

        ApplyDoors();
    }

    /* ========================= BUYING ========================= */

    public bool IsForSale => !HasOwner;

    /// <summary>Server: is this position within buy range of one of the for-sale walls?</summary>
    public bool IsNearForSaleWall(Vector3 position)
    {
        if (forSaleWalls == null) return false;
        foreach (var w in forSaleWalls)
        {
            if (w == null || !w.activeInHierarchy) continue;
            foreach (var col in w.GetComponentsInChildren<Collider>())
            {
                if (!col.enabled) continue;
                if (Vector3.Distance(col.ClosestPoint(position), position) <= buyRange) return true;
            }
        }
        return false;
    }

    /* ========================= STATUS ========================= */

    /// <summary>Human-readable owner, e.g. "Team 0 [Client 0 (Host, You)]", or "nobody".</summary>
    public string OwnerLabel
    {
        get
        {
            if (!HasOwner) return "nobody";
            ulong team = ownerTeamId.Value;

            var mall = MallManager.Instance;
            var members = mall != null ? mall.GetTeamMembers(team) : new List<ulong>();
            if (members.Count == 0) return $"Team {team} [nobody online]";

            var nm = NetworkManager.Singleton;
            var names = new List<string>();
            foreach (ulong id in members)
            {
                var tags = new List<string>();
                if (id == NetworkManager.ServerClientId) tags.Add("Host");
                if (nm != null && nm.IsListening && id == nm.LocalClientId) tags.Add("You");
                names.Add(tags.Count > 0 ? $"Client {id} ({string.Join(", ", tags)})" : $"Client {id}");
            }
            return $"Team {team} [{string.Join(", ", names)}]";
        }
    }

    private string BuildStatus()
    {
        if (!Application.isPlaying || !IsSpawned)
            return isStarterPlot ? $"Starter store, start money {startingMoney}" : $"Buy-only, price {price}";

        string state = !HasOwner ? $"FOR SALE ({price})" : IsRenovating ? "Renovating"
                     : closedByOwner.Value ? "Closed" : "Open";
        return $"Owner: {OwnerLabel} | {state}";
    }

    private void UpdateStatus() => status = BuildStatus();

    private void ApplyRenovationWalls()
    {
        if (renovationWalls == null) return;
        foreach (var w in renovationWalls)
            if (w != null) w.SetActive(IsRenovating);
    }

    /* ========================= EDITOR ========================= */

    private void OnDrawGizmos()
    {
        bool playing = Application.isPlaying && IsSpawned;

        Gizmos.color = playing && HasOwner ? new Color(0.2f, 0.9f, 0.3f, 0.6f) : new Color(0.2f, 0.8f, 1f, 0.6f);
        foreach (var box in baseAreas) DrawBox(box);

        if (segments != null)
        {
            for (int i = 0; i < segments.Length; i++)
            {
                Gizmos.color = playing && IsSegmentPurchased(i)
                    ? new Color(0.2f, 0.9f, 0.3f, 0.6f)
                    : new Color(1f, 0.8f, 0.1f, 0.6f);
                if (segments[i] != null)
                    foreach (var box in segments[i].Areas) DrawBox(box);
            }
        }

#if UNITY_EDITOR
        // Name, owner and state floating above the store in the Scene view.
        Vector3 top = transform.position;
        bool found = false;
        foreach (var box in baseAreas)
        {
            if (box == null) continue;
            Vector3 p = box.transform.TransformPoint(box.center + Vector3.up * box.size.y * 0.5f);
            if (!found || p.y > top.y) { top = new Vector3(p.x, p.y, p.z); found = true; }
        }
        var style = new GUIStyle(UnityEditor.EditorStyles.boldLabel) { alignment = TextAnchor.MiddleCenter };
        style.normal.textColor = !playing ? Color.white : HasOwner ? new Color(0.4f, 1f, 0.5f) : new Color(1f, 0.85f, 0.3f);
        UnityEditor.Handles.Label(top + Vector3.up * 0.5f, $"{displayName} (#{plotId})\n{BuildStatus()}", style);
#endif
    }

#if UNITY_EDITOR
    // Segments used to have one Area; move it into the Areas list so nothing set up is lost.
    private void OnValidate()
    {
        if (segments == null) return;
        bool changed = false;
        foreach (var seg in segments)
        {
            if (seg?.area == null) continue;
            if (seg.areas == null || Array.IndexOf(seg.areas, seg.area) < 0)
            {
                var list = new List<BoxCollider>(seg.areas ?? Array.Empty<BoxCollider>()) { seg.area };
                seg.areas = list.ToArray();
            }
            seg.area = null;
            changed = true;
        }
        if (changed) UnityEditor.EditorUtility.SetDirty(this);
    }
#endif

    private void OnDrawGizmosSelected()
    {
        // Shipment zones: pink boxes with a line back to the store.
        if (shipmentZones != null)
        {
            Vector3 storeCenter = GetWorldBounds().center;
            foreach (var z in shipmentZones)
            {
                if (z == null) continue;
                Gizmos.color = new Color(1f, 0.6f, 0.9f, 0.9f);
                DrawBox(z);
                Gizmos.color = new Color(1f, 0.6f, 0.9f, 0.35f);
                Gizmos.DrawLine(storeCenter, z.transform.TransformPoint(z.center));
            }
        }

        // Outline the wall sections this store treats as its storefront (numbered like in edit mode).
        var found = FindFrontModules();
        for (int i = 0; i < found.Count; i++)
        {
            Bounds mb = WallModule.GeometryBounds(found[i].module.gameObject);
            Gizmos.color = new Color(1f, 0.75f, 0.2f, 0.9f);
            Gizmos.DrawWireCube(mb.center, mb.size);
#if UNITY_EDITOR
            UnityEditor.Handles.Label(mb.center, $"{i + 1}");
#endif
        }

        // Show the edit-view cut as a flat outline at its height.
        Bounds b = GetWorldBounds();
        float y = GetEditCutHeight();
        Gizmos.color = new Color(0.3f, 0.9f, 1f, 0.8f);
        Gizmos.DrawWireCube(new Vector3(b.center.x, y, b.center.z), new Vector3(b.size.x + 1f, 0.01f, b.size.z + 1f));
    }

    private static void DrawBox(BoxCollider box)
    {
        if (box == null) return;
        Gizmos.matrix = box.transform.localToWorldMatrix;
        Gizmos.DrawWireCube(box.center, box.size);
        Gizmos.matrix = Matrix4x4.identity;
    }
}