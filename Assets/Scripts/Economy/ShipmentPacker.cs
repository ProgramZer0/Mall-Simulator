using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Works out where a delivery's boxes can go inside a store's shipment zones. Each box is tried
/// across the zone floor (and turned 90 degrees if that helps), and a spot only counts if the box
/// would be fully inside the zone and touch nothing already there: other boxes, items, walls or
/// players. Boxes in the same delivery are kept from overlapping each other too.
/// </summary>
public static class ShipmentPacker
{
    /// <summary>One box's size, from its prefab.</summary>
    public struct Box
    {
        public Vector3 half;        // half size in meters, in the box's own axes
        public Vector3 pivotOffset; // from the prefab's pivot to the middle of its mesh (scaled, unrotated)
    }

    // Keeps a box resting on the floor (or against a neighbour) from counting as overlapping it.
    private const float Skin = 0.02f;

    private static readonly Collider[] Hits = new Collider[8];
    private static readonly Dictionary<GameObject, Box> BoxCache = new();

    /// <summary>A prefab's box size (cached).</summary>
    public static Box GetBox(GameObject prefab)
    {
        if (BoxCache.TryGetValue(prefab, out var box)) return box;

        Bounds b = PlacementCheck.ComputeLocalBounds(prefab);
        Vector3 scale = prefab.transform.localScale;
        if (b.size == Vector3.zero) b = new Bounds(Vector3.zero, Vector3.one * 0.5f);

        box = new Box
        {
            half = new Vector3(Mathf.Abs(b.extents.x * scale.x), Mathf.Abs(b.extents.y * scale.y), Mathf.Abs(b.extents.z * scale.z)),
            pivotOffset = Vector3.Scale(b.center, scale),
        };
        BoxCache[prefab] = box;
        return box;
    }

    /// <summary>
    /// Could this delivery ever fit in these zones if they were empty? Used to refuse orders that
    /// never could, instead of leaving them paused forever.
    /// </summary>
    public static bool CouldEverFit(BoxCollider[] zones, List<Box> boxes)
    {
        float zoneFloor = 0f;
        foreach (var z in zones)
        {
            if (z == null) continue;
            Vector3 size = ZoneSize(z);
            zoneFloor += size.x * size.z;
        }

        float needed = 0f;
        foreach (var b in boxes)
        {
            needed += b.half.x * b.half.z * 4f;

            bool fitsSomewhere = false;
            foreach (var z in zones)
            {
                if (z == null) continue;
                Vector3 s = ZoneSize(z);
                bool upright = s.y >= b.half.y * 2f;
                if (upright && ((s.x >= b.half.x * 2f && s.z >= b.half.z * 2f) ||
                                (s.x >= b.half.z * 2f && s.z >= b.half.x * 2f)))
                {
                    fitsSomewhere = true;
                    break;
                }
            }
            if (!fitsSomewhere) return false;
        }
        return needed <= zoneFloor;
    }

    /// <summary>
    /// Finds a spot for every box. On success, plan[i] is where boxes[i]'s pivot goes (position +
    /// rotation). Fails (empty plan) if even one box has nowhere to go.
    /// </summary>
    public static bool TryPlan(BoxCollider[] zones, List<Box> boxes, int blockingMask, float step, List<Pose> plan)
    {
        plan.Clear();
        if (zones == null || boxes.Count == 0) return boxes.Count == 0;

        // Biggest boxes first: they're the hardest to fit.
        var order = new List<int>();
        for (int i = 0; i < boxes.Count; i++) order.Add(i);
        order.Sort((a, b) => (boxes[b].half.x * boxes[b].half.z).CompareTo(boxes[a].half.x * boxes[a].half.z));

        var placed = new List<(int zone, Vector2 min, Vector2 max)>();
        var result = new Pose[boxes.Count];
        foreach (int i in order)
        {
            if (!TryPlaceOne(zones, boxes[i], blockingMask, step, placed, out result[i]))
                return false;
        }

        plan.AddRange(result);
        return true;
    }

    private static bool TryPlaceOne(BoxCollider[] zones, Box box, int mask, float step,
                                    List<(int zone, Vector2 min, Vector2 max)> placed, out Pose pose)
    {
        step = Mathf.Max(0.05f, step);

        for (int zi = 0; zi < zones.Length; zi++)
        {
            var zone = zones[zi];
            if (zone == null) continue;

            Vector3 zsize = ZoneSize(zone);
            Quaternion zrot = Quaternion.Euler(0f, zone.transform.eulerAngles.y, 0f);
            Vector3 zcenter = zone.transform.TransformPoint(zone.center);
            float bottom = zcenter.y - zsize.y * 0.5f;
            if (zsize.y < box.half.y * 2f) continue;

            for (int turn = 0; turn < 2; turn++)
            {
                // The box's footprint along the zone's own x/z, for this rotation.
                Vector3 h = turn == 0 ? box.half : new Vector3(box.half.z, box.half.y, box.half.x);
                if (h.x * 2f > zsize.x || h.z * 2f > zsize.z) continue;
                Quaternion rot = turn == 0 ? zrot : zrot * Quaternion.Euler(0f, 90f, 0f);

                for (float lz = -zsize.z * 0.5f + h.z; lz <= zsize.z * 0.5f - h.z + 1e-4f; lz += step)
                {
                    for (float lx = -zsize.x * 0.5f + h.x; lx <= zsize.x * 0.5f - h.x + 1e-4f; lx += step)
                    {
                        var min = new Vector2(lx - h.x, lz - h.z);
                        var max = new Vector2(lx + h.x, lz + h.z);
                        if (OverlapsPlanned(placed, zi, min, max)) continue;

                        Vector3 center = zcenter + zrot * new Vector3(lx, 0f, lz);
                        center.y = bottom + box.half.y;

                        int n = Physics.OverlapBoxNonAlloc(center + Vector3.up * Skin,
                            new Vector3(Mathf.Max(0.001f, box.half.x - Skin), Mathf.Max(0.001f, box.half.y - Skin),
                                        Mathf.Max(0.001f, box.half.z - Skin)),
                            Hits, rot, mask, QueryTriggerInteraction.Ignore);
                        if (n > 0) continue;

                        placed.Add((zi, min, max));
                        pose = new Pose(center - rot * box.pivotOffset, rot);
                        return true;
                    }
                }
            }
        }

        pose = default;
        return false;
    }

    private static bool OverlapsPlanned(List<(int zone, Vector2 min, Vector2 max)> placed, int zone, Vector2 min, Vector2 max)
    {
        foreach (var p in placed)
        {
            if (p.zone != zone) continue;
            if (min.x < p.max.x - 1e-4f && max.x > p.min.x + 1e-4f &&
                min.y < p.max.y - 1e-4f && max.y > p.min.y + 1e-4f)
                return true;
        }
        return false;
    }

    /// <summary>A zone's size in meters.</summary>
    public static Vector3 ZoneSize(BoxCollider zone)
    {
        Vector3 s = zone.transform.lossyScale;
        return new Vector3(Mathf.Abs(zone.size.x * s.x), Mathf.Abs(zone.size.y * s.y), Mathf.Abs(zone.size.z * s.z));
    }
}
