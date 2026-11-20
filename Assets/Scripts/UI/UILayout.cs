using UnityEngine;

/// <summary>
/// Placing UI by code instead of layout groups (perks, licenses, size picker...).
/// </summary>
public static class UILayout
{
    /// <summary>How many Cell Size columns fit across a parent's width (at least 1).</summary>
    public static int ColumnsThatFit(RectTransform parent, Vector2 cellSize, Vector2 spacing, float padding)
    {
        if (parent == null || parent.rect.width <= 0f) return 1;
        return Mathf.Max(1, Mathf.FloorToInt((parent.rect.width - padding * 2f + spacing.x) / (cellSize.x + spacing.x)));
    }

    /// <summary>Put cell i of a grid in place: left to right, then down, from the top-left corner.</summary>
    public static void PlaceInGrid(RectTransform cell, int i, int columns, Vector2 cellSize, Vector2 spacing, float padding)
    {
        columns = Mathf.Max(1, columns);
        cell.anchorMin = cell.anchorMax = new Vector2(0f, 1f);
        cell.pivot = new Vector2(0f, 1f);
        cell.sizeDelta = cellSize;
        cell.anchoredPosition = new Vector2(padding + (i % columns) * (cellSize.x + spacing.x),
                                            -(padding + (i / columns) * (cellSize.y + spacing.y)));
    }

    /// <summary>How tall a grid of this many cells is (to size a scroll view's Content).</summary>
    public static float GridHeight(int count, int columns, Vector2 cellSize, Vector2 spacing, float padding)
    {
        int lines = (count + Mathf.Max(1, columns) - 1) / Mathf.Max(1, columns);
        return lines == 0 ? 0f : padding * 2f + lines * cellSize.y + (lines - 1) * spacing.y;
    }

    /// <summary>
    /// Put a pop-up beside a cell without covering Keep Clear (another list on the screen): its right, left,
    /// below or above, the first that fits. A cell inside Keep Clear itself: as usual.
    /// </summary>
    public static void PlaceBeside(RectTransform tip, RectTransform cell, Vector2 offset, RectTransform keepClear)
    {
        if (keepClear == null || tip == null || cell == null || tip.parent is not RectTransform area || cell.IsChildOf(keepClear))
        {
            PlaceBeside(tip, cell, offset);
            return;
        }
        Rect bounds = area.rect;
        Vector2 size = tip.rect.size;
        Rect c = LocalRect(area, cell), clear = LocalRect(area, keepClear);
        tip.pivot = new Vector2(0f, 1f);
        // Right, left, below, above: the first that fits and doesn't cover Keep Clear.
        var tries = new[]
        {
            new Vector2(c.xMax + offset.x, c.yMax + offset.y),
            new Vector2(c.xMin - offset.x - size.x, c.yMax + offset.y),
            new Vector2(c.xMin, c.yMin - Mathf.Abs(offset.x)),
            new Vector2(c.xMin, c.yMax + Mathf.Abs(offset.x) + size.y),
        };
        for (int i = 0; i < tries.Length; i++)
        {
            var at = tries[i];
            if (i < 2) at.y = Mathf.Clamp(at.y, bounds.yMin + size.y, bounds.yMax); // beside: slid up or down to fit
            var r = new Rect(at.x, at.y - size.y, size.x, size.y);
            if (r.xMin < bounds.xMin || r.xMax > bounds.xMax || r.yMin < bounds.yMin || r.yMax > bounds.yMax) continue;
            if (r.Overlaps(clear)) continue;
            tip.localPosition = new Vector3(at.x, at.y, 0f);
            return;
        }
        PlaceBeside(tip, cell, offset); // nowhere clear: as usual
    }

    // A RectTransform's rect in another's local space.
    private static Rect LocalRect(RectTransform space, RectTransform rt)
    {
        Vector3 a = space.InverseTransformPoint(rt.TransformPoint(rt.rect.min));
        Vector3 b = space.InverseTransformPoint(rt.TransformPoint(rt.rect.max));
        return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
    }

    /// <summary>
    /// Put a pop-up beside a cell: on its right, or its left if it'd go off the side, kept inside the
    /// pop-up's parent.
    /// </summary>
    public static void PlaceBeside(RectTransform tip, RectTransform cell, Vector2 offset)
    {
        if (tip == null || cell == null || tip.parent is not RectTransform area) return;
        Vector3 right = area.InverseTransformPoint(cell.TransformPoint(new Vector3(cell.rect.xMax, cell.rect.yMax)));
        Vector3 left = area.InverseTransformPoint(cell.TransformPoint(new Vector3(cell.rect.xMin, cell.rect.yMax)));
        Rect bounds = area.rect;
        Vector2 size = tip.rect.size;
        tip.pivot = new Vector2(0f, 1f);
        float x = right.x + offset.x;
        if (x + size.x > bounds.xMax) x = left.x - offset.x - size.x;
        float y = Mathf.Clamp(right.y + offset.y, bounds.yMin + size.y, bounds.yMax);
        tip.localPosition = new Vector3(Mathf.Max(bounds.xMin, x), y, 0f);
    }
}
