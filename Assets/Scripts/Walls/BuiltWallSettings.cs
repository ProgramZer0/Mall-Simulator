using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>One wall a player built in edit mode (synced through its store).</summary>
public struct BuiltWall : INetworkSerializeByMemcpy, IEquatable<BuiltWall>
{
    public int id;        // unique within its store, so a wall can be removed by id
    public Vector3 a;     // start, on the floor
    public Vector3 b;     // end, on the floor
    public float height;  // meters

    public float Length => Vector3.Distance(a, b);

    public bool Equals(BuiltWall o) => id == o.id && a == o.a && b == o.b && height == o.height;
    public override bool Equals(object obj) => obj is BuiltWall o && Equals(o);
    public override int GetHashCode() => id;
}

/// <summary>
/// Settings for walls players build in store edit mode (B). Put one on the MallManager object.
///
/// Wall Prefab: a 1 x 1 x 1 m box with its pivot in the middle (Unity's default cube works) with
/// your wall material and a collider. Put it on your walls layer so wall items (pictures) can hang
/// on built walls. It's stretched to each wall's length, height and Thickness.
/// </summary>
public class BuiltWallSettings : MonoBehaviour
{
    public static BuiltWallSettings Instance { get; private set; }

    [Tooltip("A 1 x 1 x 1 m box, pivot in the middle, with a collider. Stretched to each wall. Put it on your walls layer.")]
    [SerializeField] private GameObject wallPrefab;
    [Tooltip("How thick built walls are, in meters.")]
    [SerializeField] private float thickness = 0.15f;
    [Tooltip("Height of a half wall (counters, dividers). Full walls go from the floor to the top of the store.")]
    [SerializeField] private float halfHeight = 1.1f;
    [Tooltip("Height of a full wall in meters. 0 = all the way up to the store's ceiling (edit-mode cut height).")]
    [SerializeField] private float fullHeight = 2.5f;
    [Tooltip("Wall ends snap to this grid, in meters.")]
    [SerializeField] private float gridSize = 0.5f;
    [Tooltip("Shortest wall allowed, in meters.")]
    [SerializeField] private float minLength = 0.5f;
    [Tooltip("Most walls one store can have.")]
    [SerializeField] private int maxWallsPerStore = 64;
    [Tooltip("Money per meter of wall (full walls). Half walls cost half.")]
    [SerializeField] private int costPerMeter = 50;
    [Tooltip("Share of the price given back when a wall is removed (0.5 = half).")]
    [Range(0f, 1f)] [SerializeField] private float refundFraction = 0.5f;

    [Header("Windows and doors (WallOpening)")]
    [Tooltip("Closest a window or door can be to the end or top of a wall, in meters.")]
    [SerializeField] private float openingEdgeMargin = 0.15f;
    [Tooltip("Smallest gap between two windows or doors in the same wall, in meters.")]
    [SerializeField] private float openingGap = 0.15f;
    [Tooltip("Share of a window's, door's or wall item's price given back when it's removed in edit mode, " +
             "by itself or with the wall it's on (0.5 = half).")]
    [Range(0f, 1f)] [SerializeField] private float openingRefundFraction = 0.5f;
    [Tooltip("Windows and doors slide along walls in steps of this many meters (hold Ctrl to move freely).")]
    [SerializeField] private float openingSnap = 0.25f;

    public GameObject WallPrefab => wallPrefab;
    public float Thickness => Mathf.Max(0.01f, thickness);
    public float HalfHeight => Mathf.Max(0.1f, halfHeight);

    /// <summary>A full wall's height in this store: Full Height, but never taller than the store.</summary>
    public float FullHeightFor(StorePlot plot) =>
        plot == null ? Mathf.Max(0.5f, fullHeight) : fullHeight > 0f ? Mathf.Min(fullHeight, plot.FullWallHeight) : plot.FullWallHeight;
    public float GridSize => Mathf.Max(0.05f, gridSize);
    public float MinLength => Mathf.Max(0.05f, minLength);
    public int MaxWallsPerStore => Mathf.Max(1, maxWallsPerStore);
    public float OpeningEdgeMargin => Mathf.Max(0f, openingEdgeMargin);
    public float OpeningGap => Mathf.Max(0f, openingGap);
    public float OpeningRefundFraction => openingRefundFraction;
    public float OpeningSnap => Mathf.Max(0f, openingSnap);

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[BuiltWallSettings] More than one in the scene; using the first.", this);
            return;
        }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>What a wall costs. Half walls (shorter than a full one) cost half per meter.</summary>
    public int GetCost(float length, bool half) =>
        Mathf.Max(0, Mathf.CeilToInt(length * costPerMeter * (half ? 0.5f : 1f)));

    /// <summary>What removing a wall gives back.</summary>
    public int GetRefund(float length, bool half) => Mathf.FloorToInt(GetCost(length, half) * refundFraction);

    /// <summary>A point snapped to the wall grid (x and z; y is left alone).</summary>
    public Vector3 SnapToGrid(Vector3 p)
    {
        float g = GridSize;
        return new Vector3(Mathf.Round(p.x / g) * g, p.y, Mathf.Round(p.z / g) * g);
    }

    /// <summary>
    /// Where a wall from start toward the mouse ends: straight along x or z, or at 45 degrees, with
    /// both ends on the grid (like The Sims).
    /// </summary>
    public Vector3 SnapEnd(Vector3 start, Vector3 mouse)
    {
        float g = GridSize;
        Vector3 d = mouse - start;
        float ax = Mathf.Abs(d.x), az = Mathf.Abs(d.z);

        float dx, dz;
        if (ax >= az * 2f) { dx = Mathf.Round(d.x / g) * g; dz = 0f; }
        else if (az >= ax * 2f) { dx = 0f; dz = Mathf.Round(d.z / g) * g; }
        else
        {
            float m = Mathf.Round((ax + az) * 0.5f / g) * g;
            dx = Mathf.Sign(d.x) * m;
            dz = Mathf.Sign(d.z) * m;
        }
        return new Vector3(start.x + dx, start.y, start.z + dz);
    }

    /// <summary>
    /// Position, rotation and scale of a wall's box that reaches Extend A past a and Extend B past b
    /// (half a thickness closes a corner with a plain box; 0 for an end that's cut at an angle).
    /// </summary>
    public void GetWallPose(Vector3 a, Vector3 b, float height, float extendA, float extendB,
                            out Vector3 position, out Quaternion rotation, out Vector3 scale)
    {
        Vector3 flat = b - a;
        flat.y = 0f;
        float length = flat.magnitude;
        Vector3 dir = length > 1e-4f ? flat / length : Vector3.forward;
        rotation = Quaternion.LookRotation(dir, Vector3.up);
        scale = new Vector3(Thickness, height, length + extendA + extendB);
        position = (a - dir * extendA + b + dir * extendB) * 0.5f + Vector3.up * height * 0.5f;
    }

    /// <summary>Position, rotation and scale of a wall's box.</summary>
    public void GetWallPose(Vector3 a, Vector3 b, float height, out Vector3 position, out Quaternion rotation, out Vector3 scale)
    {
        Vector3 flat = b - a;
        flat.y = 0f;
        float length = flat.magnitude;
        rotation = length > 1e-4f ? Quaternion.LookRotation(flat / length, Vector3.up) : Quaternion.identity;
        // Overlap the ends by half a thickness so corners join without a gap.
        scale = new Vector3(Thickness, height, length + Thickness);
        position = (a + b) * 0.5f + Vector3.up * height * 0.5f;
    }
}
