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

    [Header("Space")]
    [SerializeField] private BoxCollider[] baseAreas;
    [Tooltip("Max 32. Order matters: the index is what gets synced.")]
    [SerializeField] private Segment[] segments;

    [Header("Renovation")]
    [Tooltip("Shown over the storefront while the store is in edit mode (hidden otherwise).")]
    [SerializeField] private GameObject[] renovationWalls;

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

    private readonly NetworkVariable<ulong> ownerClientId =
        new(NoOwner, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // Bit i set = segments[i] has been bought.
    private readonly NetworkVariable<int> purchasedSegments =
        new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // Who is editing this store right now (NoOwner = open for business).
    private readonly NetworkVariable<ulong> editorClientId =
        new(NoOwner, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>Raised on every client when the owner, purchased segments or edit state change.</summary>
    public event Action<StorePlot> Changed;

    public ulong EditorClientId => editorClientId.Value;
    /// <summary>In edit mode: closed, renovation wall up, items out of the world until it ends.</summary>
    public bool IsRenovating => editorClientId.Value != NoOwner;
    /// <summary>Owned and not under renovation. Customers/sales should check this.</summary>
    public bool IsOpen => HasOwner && !IsRenovating;

    public int PlotId => plotId;
    public string DisplayName => displayName;
    public int Price => price;
    public bool IsStarterPlot => isStarterPlot;
    public ulong OwnerClientId => ownerClientId.Value;
    public bool HasOwner => ownerClientId.Value != NoOwner;
    public int SegmentCount => segments?.Length ?? 0;

    private void Awake()
    {
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

    public override void OnNetworkSpawn()
    {
        ownerClientId.OnValueChanged += OnOwnerChanged;
        purchasedSegments.OnValueChanged += OnSegmentsChanged;
        editorClientId.OnValueChanged += OnEditorChanged;
        ApplyWalls();
        ApplyRenovationWalls();
        ApplyOwnerVisuals();
        UpdateStatus();
        Changed += OnAnyChange;

        if (MallManager.Instance != null) MallManager.Instance.Register(this);
    }

    public override void OnNetworkDespawn()
    {
        ownerClientId.OnValueChanged -= OnOwnerChanged;
        purchasedSegments.OnValueChanged -= OnSegmentsChanged;
        editorClientId.OnValueChanged -= OnEditorChanged;
        Changed -= OnAnyChange;

        if (MallManager.Instance != null) MallManager.Instance.Unregister(this);
    }

    private void OnOwnerChanged(ulong prev, ulong next)
    {
        ApplyOwnerVisuals();
        Changed?.Invoke(this);
    }

    private void OnAnyChange(StorePlot _) => UpdateStatus();

    private void OnEditorChanged(ulong prev, ulong next)
    {
        ApplyRenovationWalls();
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
    /// Whether this client may build/stock here. Owner only for now; co-op members (#14)
    /// get added here so every permission check picks them up automatically.
    /// </summary>
    public bool IsAuthorized(ulong clientId) => HasOwner && ownerClientId.Value == clientId;

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
    public void ServerSetOwner(ulong clientId)
    {
        if (!IsServer) return;
        ownerClientId.Value = clientId;
    }

    /// <summary>Server only. Ends any edit session, clears the owner and rebuilds the false walls.</summary>
    public void ServerRelease()
    {
        if (!IsServer) return;
        ServerSetEditor(NoOwner);
        ownerClientId.Value = NoOwner;
        purchasedSegments.Value = 0;
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

    /// <summary>Human-readable owner, e.g. "Client 1 (Host)", or "nobody".</summary>
    public string OwnerLabel
    {
        get
        {
            if (!HasOwner) return "nobody";
            ulong id = ownerClientId.Value;
            string label = $"Client {id}";
            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsListening)
            {
                if (id == NetworkManager.ServerClientId) label += " (Host)";
                if (id == nm.LocalClientId) label += " (You)";
            }
            return label;
        }
    }

    private string BuildStatus()
    {
        if (!Application.isPlaying || !IsSpawned)
            return isStarterPlot ? "Starter store (given to a joining player)" : $"Buy-only, price {price}";

        string state = !HasOwner ? $"FOR SALE ({price})" : IsRenovating ? "Renovating" : "Open";
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

    private static void DrawBox(BoxCollider box)
    {
        if (box == null) return;
        Gizmos.matrix = box.transform.localToWorldMatrix;
        Gizmos.DrawWireCube(box.center, box.size);
        Gizmos.matrix = Matrix4x4.identity;
    }
}