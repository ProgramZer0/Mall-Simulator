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
        [Tooltip("Floor space this segment adds once bought.")]
        public BoxCollider area;
        [Tooltip("Walls removed when this segment is bought.")]
        public GameObject[] falseWalls;
        public int price = 1000;
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

    [Header("Deliveries")]
    [Tooltip("Loading docks this store's orders can arrive at. Each delivery goes to the first one " +
             "(in this order) with a free shipment spot. Several stores can share a dock.")]
    [SerializeField] private LoadingDockZone[] docks;
    // Older single-dock setting; still honoured (added to the list) so nothing set up is lost.
    [SerializeField, HideInInspector] private LoadingDockZone dock;

    [Header("Doors")]
    [Tooltip("Door objects for this store (e.g. the door inside each doorway). Shown while the store " +
             "is closed, for sale or being renovated; hidden while it's open. Doors of doorway modules " +
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
    /// <summary>This store's docks in preference order (the Docks list, plus the old single Dock if set).</summary>
    public List<LoadingDockZone> GetDocks()
    {
        var list = new List<LoadingDockZone>();
        if (docks != null)
            foreach (var d in docks)
                if (d != null && !list.Contains(d)) list.Add(d);
        if (dock != null && !list.Contains(dock)) list.Add(dock);
        return list;
    }
    public ulong OwnerTeamId => ownerTeamId.Value;
    public bool HasOwner => ownerTeamId.Value != NoOwner;
    public int SegmentCount => segments?.Length ?? 0;

    private void Awake()
    {
        // Which module each storefront section shows (0 = the one placed in the scene).
        frontOverrides = new NetworkList<byte>(
            null, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // The colliders are just authoring shapes; Contains() reads them directly.
        foreach (var box in baseAreas)
            if (box != null) box.enabled = false;
        if (segments != null)
            foreach (var seg in segments)
                if (seg?.area != null) seg.area.enabled = false;

        if (SegmentCount > 32)
            Debug.LogError($"[StorePlot] '{displayName}' has {SegmentCount} segments; max is 32.", this);

        // Mark the for-sale walls so the player's F raycast can find this store from them.
        if (forSaleWalls != null)
            foreach (var w in forSaleWalls)
                if (w != null && !w.TryGetComponent<ForSaleSign>(out _))
                    w.AddComponent<ForSaleSign>().Init(this);

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
        ApplyFrontWall();
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
            if (IsSegmentPurchased(i) && BoxContains(segments[i].area, worldPoint)) return true;

        return false;
    }

    /// <summary>True if the point is in this plot's footprint, bought or not.</summary>
    public bool ContainsIncludingUnpurchased(Vector3 worldPoint)
    {
        if (Contains(worldPoint)) return true;
        for (int i = 0; i < SegmentCount; i++)
            if (BoxContains(segments[i].area, worldPoint)) return true;
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

        if (frontSlots == null) yield break;
        foreach (var slot in frontSlots)
        {
            GameObject shown = slot.replacement != null ? slot.replacement
                             : slot.original != null ? slot.original.gameObject : null;
            if (shown == null || !shown.TryGetComponent<WallModule>(out var wm) || wm.Door == null) continue;
            yield return wm.Door;
        }
    }

    /// <summary>Doors show while the store is for sale, closed or being renovated; hidden while open.</summary>
    private void ApplyDoors()
    {
        bool show = !HasOwner || closedByOwner.Value || IsRenovating;
        foreach (var d in AllDoors())
            d.SetActive(show);
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
            frontSlots.Add(new FrontSlot { original = module, anchor = center });
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
            foreach (var seg in segments) best = Mathf.Min(best, DistanceToBox(seg?.area, worldPoint));
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
        go.transform.position += slot.anchor - WallModule.GeometryBounds(go).center;

        slot.original.gameObject.SetActive(false);
        slot.replacement = go;
        slot.replacementId = moduleId;
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
            if (item == null || !item.IsSpawned || !Contains(item.transform.position)) continue;

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
                rotation = item.transform.rotation
            });
            item.NetworkObject.Despawn(true);
        }

        editorClientId.Value = clientId;
    }

    /// <summary>
    /// Server only. Ends the edit session and respawns its items. Each item uses the editor's
    /// position when one was given and it passes the checks (inside this store, isValid);
    /// otherwise it goes back to where it was before editing.
    /// </summary>
    public void ServerEndEdit(Vector3[] positions, Quaternion[] rotations,
                              Func<ItemDef, Vector3, Quaternion, bool> isValid)
    {
        if (!IsServer || !IsRenovating) return;

        var catalog = ItemCatalog.Instance;
        for (int i = 0; i < editSession.Count; i++)
        {
            var entry = editSession[i];
            if (catalog == null || !catalog.TryGet(entry.itemId, out var def) || def.worldNetworkPrefab == null)
                continue;

            Vector3 pos = entry.position;
            Quaternion rot = entry.rotation;

            if (positions != null && rotations != null && i < positions.Length && i < rotations.Length)
            {
                Vector3 p = positions[i];
                Quaternion r = rotations[i];
                if (IsFinite(p) && IsFinite(r))
                {
                    r = r.normalized;
                    Physics.SyncTransforms(); // see the items respawned earlier in this loop
                    if (ContainsItem(def, p, r) && (isValid == null || isValid(def, p, r)))
                    {
                        pos = p;
                        rot = r;
                    }
                }
            }

            var spawned = Instantiate(def.worldNetworkPrefab, pos, rot);
            spawned.Spawn(true);
        }

        editSession.Clear();
        editorClientId.Value = NoOwner;
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
            if (IsSegmentPurchased(i) && segments[i].area != null) into.Add(BoxWorldBounds(segments[i].area));
    }

    /// <summary>Adds the store's space boxes (base areas plus purchased segments) to the list.</summary>
    public void GetAreaBoxes(List<BoxCollider> into)
    {
        foreach (var box in baseAreas)
            if (box != null) into.Add(box);
        for (int i = 0; i < SegmentCount; i++)
            if (IsSegmentPurchased(i) && segments[i].area != null) into.Add(segments[i].area);
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
            if (IsSegmentPurchased(i)) Add(segments[i].area);

        return any ? result : new Bounds(transform.position, Vector3.one);
    }

    public void ServerUnlockSegment(int index)
    {
        if (!IsServer || index < 0 || index >= SegmentCount) return;
        purchasedSegments.Value |= 1 << index;
    }

    public int GetSegmentPrice(int index) =>
        index >= 0 && index < SegmentCount ? segments[index].price : 0;

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
                DrawBox(segments[i]?.area);
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

    private void OnDrawGizmosSelected()
    {
        // Lines to the docks this store's deliveries can go to.
        Gizmos.color = new Color(0.7f, 0.4f, 1f, 0.9f);
        Vector3 storeCenter = GetWorldBounds().center;
        foreach (var d in GetDocks())
            Gizmos.DrawLine(storeCenter, d.transform.position);

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