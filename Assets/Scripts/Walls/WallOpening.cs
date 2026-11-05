using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A window or door that sits IN a wall and cuts its own hole (see CuttableWall). Put it on the world
/// prefab of a catalog item (next to its NetworkObject and ItemPickupNet).
///
/// Building the prefab:
/// - Width along X (red arrow), height along Y, front facing +Z (blue arrow). The pivot can be anywhere.
/// - The hole is the size of the item's colliders (or meshes), unless Hole Size is set.
/// - Doors go on the floor. A store's front wall needs at least one door before edit mode can be saved.
/// - Door types are up to the prefab:
///     a plain doorway: no extra scripts.
///     a door the store opens and shuts (open while the store is open): add DoorMotion.
///     a door players open with F (or automatically) and can lock: add DoorMotion and DoorNet.
///   The store is filled in on DoorNet by itself.
///
/// Players buy these in store edit mode (N) and drag them along walls. Tick Edit Mode Only on the
/// ItemPickupNet and untick Show In Shop on the catalog entry, so they're only handled there.
/// </summary>
[DisallowMultipleComponent]
public class WallOpening : MonoBehaviour
{
    /// <summary>Every opening in the scene (networked ones and edit-mode copies).</summary>
    public static readonly List<WallOpening> All = new();

    /// <summary>Goes up whenever an opening appears, moves or goes away, so walls know to re-cut.</summary>
    public static int Version { get; private set; }

    [Tooltip("A door: sits on the floor, and counts as the way into the store on a front wall.")]
    public bool isDoor;
    [Tooltip("Size of the hole in meters (width, height). Leave at 0 to use the item's own size.")]
    public Vector2 holeSize;

    private Bounds bounds;        // root-local, before the root's scale
    private bool hasBounds;
    private Vector3 lastPos;
    private Quaternion lastRot;
    private StorePlot lastPlot;
    private DoorNet doorNet;
    private int seenWalls = -1;

    /// <summary>Where an opening is and how big, in world space.</summary>
    public struct Shape
    {
        public Vector3 center;   // middle of the hole
        public Vector3 right;    // along its width
        public Vector3 up;
        public Vector3 forward;  // its front
        public Vector2 size;     // width, height (meters)
    }

    private void Awake()
    {
        _ = LocalBounds;
        doorNet = GetComponent<DoorNet>();
    }

    private Bounds LocalBounds
    {
        get
        {
            if (!hasBounds)
            {
                bounds = PlacementCheck.ComputeLocalBounds(gameObject);
                hasBounds = true;
            }
            return bounds;
        }
    }

#if UNITY_EDITOR
    /// <summary>Editor: set this opening into the nearest CuttableWall (for doors placed in the scene).</summary>
    [ContextMenu("Snap Into Nearest Wall")]
    private void SnapIntoNearestWall()
    {
        hasBounds = false;
        CuttableWall best = null;
        float bestDistance = float.MaxValue;
        Vector3 c = Current.center;
        foreach (var w in FindObjectsByType<CuttableWall>(FindObjectsSortMode.None))
        {
            float d = w.DistanceInEditor(c);
            if (d < bestDistance) { bestDistance = d; best = w; }
        }
        if (best == null) { Debug.LogWarning("[WallOpening] No CuttableWall in the scene.", this); return; }

        UnityEditor.Undo.RecordObject(transform, "Snap Into Wall");
        best.SnapInEditor(this);
        Debug.Log($"[WallOpening] '{name}' set into '{best.name}'.", this);
    }
#endif

    private void OnEnable()
    {
        All.Add(this);
        lastPos = transform.position;
        lastRot = transform.rotation;
        Version++;
    }

    private void OnDisable()
    {
        All.Remove(this);
        Version++;
        if (lastPlot != null) lastPlot.RefreshDoors();
        lastPlot = null;
    }

    private void LateUpdate()
    {
        bool moved = transform.position != lastPos || transform.rotation != lastRot;
        if (moved)
        {
            lastPos = transform.position;
            lastRot = transform.rotation;
            Version++;
        }
        if (!moved && seenWalls == CuttableWall.Version) return;
        seenWalls = CuttableWall.Version;

        // Which store's wall it's in: doors the store opens and shuts need to know.
        var host = CuttableWall.HostOf(this);
        var plot = host != null ? host.Plot : null;
        if (plot == lastPlot) return;
        var old = lastPlot;
        lastPlot = plot;
        if (doorNet != null && plot != null) doorNet.AssignStoreIfUnset(plot);
        if (old != null) old.RefreshDoors();
        if (plot != null) plot.RefreshDoors();
    }

    /// <summary>Copy the settings of another opening (edit-mode copies get theirs from the prefab).</summary>
    public void CopyFrom(WallOpening other)
    {
        if (other == null) return;
        isDoor = other.isDoor;
        holeSize = other.holeSize;
    }

    /// <summary>The store opens and shuts this door itself (it moves, but players don't work it).</summary>
    public bool IsStoreDoor => isDoor && doorNet == null && TryGetComponent<DoorMotion>(out _);

    /// <summary>An edit-mode copy (not a real item).</summary>
    public bool IsEditCopy => GetComponent<EditLocalItem>() != null;

    /// <summary>This opening as it is now.</summary>
    public Shape Current => Measure(LocalBounds, transform.lossyScale, holeSize, transform.position, transform.rotation);

    /// <summary>Hole size (width, height) in meters.</summary>
    public Vector2 Size => Measure(LocalBounds, transform.lossyScale, holeSize, Vector3.zero, Quaternion.identity).size;

    /// <summary>Where the pivot goes so the hole's middle lands on center.</summary>
    public Vector3 PivotFor(Vector3 center, Quaternion rotation) =>
        center - rotation * Vector3.Scale(LocalBounds.center, transform.lossyScale);

    /* ========================= FROM A CATALOG ITEM ========================= */

    /// <summary>The opening on an item's world prefab, if it has one.</summary>
    public static bool TryGet(ItemDef def, out WallOpening prefabOpening)
    {
        prefabOpening = null;
        return def != null && def.worldNetworkPrefab != null && def.worldNetworkPrefab.TryGetComponent(out prefabOpening);
    }

    public static bool IsOpening(ItemDef def) => TryGet(def, out _);

    /// <summary>The opening an item would make at this pose (for checks before it exists).</summary>
    public static Shape Measure(ItemDef def, Vector3 position, Quaternion rotation)
    {
        TryGet(def, out var o);
        return Measure(def.LocalBounds, def.worldNetworkPrefab.transform.localScale,
                       o != null ? o.holeSize : Vector2.zero, position, rotation);
    }

    public static Shape Measure(Bounds local, Vector3 scale, Vector2 holeSizeOverride, Vector3 position, Quaternion rotation)
    {
        scale = new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
        Vector2 size = new(local.size.x * scale.x, local.size.y * scale.y);
        if (holeSizeOverride.x > 0f) size.x = holeSizeOverride.x;
        if (holeSizeOverride.y > 0f) size.y = holeSizeOverride.y;
        return new Shape
        {
            center = position + rotation * Vector3.Scale(local.center, scale),
            right = rotation * Vector3.right,
            up = rotation * Vector3.up,
            forward = rotation * Vector3.forward,
            size = size,
        };
    }
}
