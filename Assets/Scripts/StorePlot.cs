using System;
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
    /// <summary>In edit mode: closed, renovation wall up, items frozen in the air.</summary>
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

        // Renovation walls start hidden until the synced edit state says otherwise.
        ApplyRenovationWalls();
    }

    public override void OnNetworkSpawn()
    {
        ownerClientId.OnValueChanged += OnOwnerChanged;
        purchasedSegments.OnValueChanged += OnSegmentsChanged;
        editorClientId.OnValueChanged += OnEditorChanged;
        ApplyWalls();
        ApplyRenovationWalls();

        if (MallManager.Instance != null) MallManager.Instance.Register(this);
    }

    public override void OnNetworkDespawn()
    {
        ownerClientId.OnValueChanged -= OnOwnerChanged;
        purchasedSegments.OnValueChanged -= OnSegmentsChanged;
        editorClientId.OnValueChanged -= OnEditorChanged;

        if (MallManager.Instance != null) MallManager.Instance.Unregister(this);
    }

    private void OnOwnerChanged(ulong prev, ulong next) => Changed?.Invoke(this);

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

    /// <summary>
    /// Server only. Starts (clientId) or ends (NoOwner) edit mode. Items in the store freeze
    /// in place while editing; ending it gives every item its gravity back.
    /// Use MallManager.ServerTryStartEdit / ServerStopEdit rather than calling this directly.
    /// </summary>
    public void ServerSetEditor(ulong clientId)
    {
        if (!IsServer || editorClientId.Value == clientId) return;

        bool ending = clientId == NoOwner;
        editorClientId.Value = clientId;

        // Snapshot first: refreshing physics can't change membership, but be safe anyway.
        foreach (var item in ItemPickupNet.All.ToArray())
        {
            if (item == null || !Contains(item.transform.position)) continue;
            if (ending) item.ServerSetGravityInEdit(false); // next session starts frozen again
            else item.ServerRefreshPhysics();
        }
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

        if (segments == null) return;
        for (int i = 0; i < segments.Length; i++)
        {
            Gizmos.color = playing && IsSegmentPurchased(i)
                ? new Color(0.2f, 0.9f, 0.3f, 0.6f)
                : new Color(1f, 0.8f, 0.1f, 0.6f);
            DrawBox(segments[i]?.area);
        }
    }

    private static void DrawBox(BoxCollider box)
    {
        if (box == null) return;
        Gizmos.matrix = box.transform.localToWorldMatrix;
        Gizmos.DrawWireCube(box.center, box.size);
        Gizmos.matrix = Matrix4x4.identity;
    }
}