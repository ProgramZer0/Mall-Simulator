using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Marks a part of something that can be painted (see PaintPalette). Each PaintParts is one paint
/// zone that takes its own color: put one on an item's world prefab root for the whole thing, or one on
/// the root and more on children to paint them separately (a door's frame and its doors). Up to 4
/// zones per item. Works on hand models too, and on front wall types, which can have any number: each
/// part is painted on its own, the same color on every section (and tile) of that type.
///
/// Parts: the Renderers (and which of their materials, Material Index -1 = all) this zone paints.
/// Leave it empty to paint every renderer under this object, except those under another PaintParts
/// (they're that zone's).
///
/// With the paint brush, the zone pointed at is the one painted: the smallest zone whose models are
/// under the crosshair (so the doors, not the frame around them). The buy menu paints every zone.
///
/// A hand model (the item's Hand Visual Prefab) can have the same zones, in the same order, to show the
/// colors while carried.
/// </summary>
public class PaintParts : MonoBehaviour
{
    [Serializable]
    public struct Part
    {
        public Renderer renderer;
        [Tooltip("Which material on the renderer; -1 = all of them.")]
        public int materialIndex;
    }

    /// <summary>The most zones one item can have (its colors are packed into one synced number).</summary>
    public const int MaxZones = 4;

    [Tooltip("Renderers (and materials) this zone paints. Empty = every renderer under this object that isn't " +
             "under another PaintParts.")]
    [SerializeField] private List<Part> parts = new();
    [Tooltip("Shown in the brush prompt (\"Paint Glass Door (Doors)\"). Empty: nothing for the root's zone, the " +
             "object's name for a child's.")]
    [SerializeField] private string zoneName;

    public byte Current { get; private set; }

    /// <summary>Its name for prompts: Zone Name, or the object's name.</summary>
    public string DisplayName => string.IsNullOrEmpty(zoneName) ? name : zoneName;

    /// <summary>
    /// How big the smallest of this zone's models at a world point is (squared size of its bounds), or
    /// float.MaxValue if none is there. For picking the zone under the crosshair.
    /// </summary>
    public float SizeAt(Vector3 point, float tolerance = 0.05f)
    {
        float best = float.MaxValue;
        foreach (var (r, _) in Targets(this, transform))
        {
            if (r == null || !r.enabled) continue;
            Bounds b = r.bounds;
            b.Expand(tolerance * 2f);
            if (b.Contains(point)) best = Mathf.Min(best, r.bounds.size.sqrMagnitude);
        }
        return best;
    }

    /// <summary>Paint this zone (0 = back to its own colors).</summary>
    public void Apply(byte id)
    {
        Current = id;
        foreach (var (r, index) in Targets(this, transform)) PaintPalette.Apply(r, index, id);
    }

    // What a zone paints, on the object it's on (Self) or the same spot in a copy (Root of the copy).
    private static IEnumerable<(Renderer, int)> Targets(PaintParts zone, Transform at)
    {
        if (zone.parts.Count > 0)
        {
            foreach (var p in zone.parts)
            {
                if (p.renderer == null) continue;
                Renderer r = p.renderer;
                if (at != zone.transform)
                {
                    string path = PathFrom(zone.transform, p.renderer.transform);
                    if (path == null) continue;
                    Transform t = path.Length == 0 ? at : at.Find(path);
                    if (t == null || !t.TryGetComponent(out r)) continue;
                }
                yield return (r, p.materialIndex);
            }
            yield break;
        }

        // Every renderer below, except under another zone.
        var stack = new Stack<(Transform source, Transform target)>();
        stack.Push((zone.transform, at));
        while (stack.Count > 0)
        {
            var (src, dst) = stack.Pop();
            if (src != zone.transform && src.GetComponent<PaintParts>() != null) continue;
            if (dst.TryGetComponent<Renderer>(out var r)) yield return (r, -1);
            for (int i = 0; i < src.childCount; i++)
            {
                Transform childSrc = src.GetChild(i);
                Transform childDst = at == zone.transform ? childSrc : dst.Find(childSrc.name);
                if (childDst != null) stack.Push((childSrc, childDst));
            }
        }
    }

    /* ========================= ZONES ========================= */

    private static readonly List<PaintParts> Found = new();

    /// <summary>The zones of an object, in a fixed order (hierarchy order, at most MaxZones).</summary>
    public static List<PaintParts> ZonesOf(GameObject go, List<PaintParts> into = null)
    {
        into ??= new List<PaintParts>();
        into.Clear();
        if (go == null) return into;
        go.GetComponentsInChildren(true, Found);
        for (int i = 0; i < Found.Count && into.Count < MaxZones; i++) into.Add(Found[i]);
        return into;
    }

    /// <summary>Paint every zone of a live object from a paint set (see PaintSet).</summary>
    public static void ApplySet(GameObject go, uint set)
    {
        var zones = ZonesOf(go);
        for (int i = 0; i < zones.Count; i++) zones[i].Apply(PaintSet.Get(set, i));
    }

    /// <summary>
    /// Paint a copy of a prefab (edit mode's copies lose their scripts): each of the prefab's zones,
    /// found in the copy by where it sits in the hierarchy.
    /// </summary>
    public static void ApplySetToCopy(GameObject prefab, GameObject copy, uint set)
    {
        if (prefab == null || copy == null) return;
        var zones = ZonesOf(prefab);
        for (int i = 0; i < zones.Count; i++)
        {
            string path = PathFrom(prefab.transform, zones[i].transform);
            if (path == null) continue;
            Transform at = path.Length == 0 ? copy.transform : copy.transform.Find(path);
            if (at == null) continue;
            byte id = PaintSet.Get(set, i);
            foreach (var (r, index) in Targets(zones[i], at)) PaintPalette.Apply(r, index, id);
        }
    }

    /// <summary>
    /// Which zone of a live object is at a world point: the smallest zone with a model there (so the
    /// door rather than the frame around it). -1 if none.
    /// </summary>
    public static int ZoneAt(GameObject go, Vector3 point, float tolerance = 0.05f)
    {
        var zones = ZonesOf(go);
        int best = -1;
        float bestSize = float.MaxValue;
        for (int i = 0; i < zones.Count; i++)
        {
            float size = zones[i].SizeAt(point, tolerance);
            if (size < bestSize) { bestSize = size; best = i; }
        }
        if (best < 0 && zones.Count > 0) best = 0; // somewhere on it, but not on a model: the main zone
        return best;
    }

    /// <summary>Can this prefab be painted?</summary>
    public static bool IsPaintable(GameObject prefab) => prefab != null && prefab.GetComponentInChildren<PaintParts>(true) != null;

    /// <summary>Can this catalog item be painted?</summary>
    public static bool IsPaintable(ItemDef def) =>
        def != null && def.worldNetworkPrefab != null && IsPaintable(def.worldNetworkPrefab.gameObject);

    /// <summary>A zone's name for prompts ("" when there's only one zone, or for an unnamed root zone).</summary>
    public static string ZoneName(GameObject go, int zone)
    {
        var zones = ZonesOf(go);
        if (zones.Count <= 1 || zone < 0 || zone >= zones.Count) return "";
        var z = zones[zone];
        if (!string.IsNullOrEmpty(z.zoneName)) return z.zoneName;
        return z.gameObject == go ? "" : z.name;
    }

    private static string PathFrom(Transform root, Transform t)
    {
        var names = new List<string>();
        while (t != null && t != root)
        {
            names.Add(t.name);
            t = t.parent;
        }
        if (t != root) return null;
        names.Reverse();
        return string.Join("/", names);
    }
}

/// <summary>
/// The colors of an item's paint zones (PaintParts), packed into one number: zone i's PaintPalette id
/// is byte i. 0 = every zone in its own colors.
/// </summary>
public static class PaintSet
{
    public static byte Get(uint set, int zone) =>
        zone < 0 || zone >= PaintParts.MaxZones ? (byte)0 : (byte)((set >> (zone * 8)) & 0xFF);

    public static uint With(uint set, int zone, byte id)
    {
        if (zone < 0 || zone >= PaintParts.MaxZones) return set;
        int shift = zone * 8;
        return (set & ~(0xFFu << shift)) | ((uint)id << shift);
    }

    /// <summary>Every zone the same color.</summary>
    public static uint All(byte id) => id * 0x01010101u;

    /// <summary>Every zone's color is one the palette has.</summary>
    public static bool IsValid(uint set)
    {
        for (int i = 0; i < PaintParts.MaxZones; i++)
            if (!PaintPalette.IsValid(Get(set, i))) return false;
        return true;
    }
}
