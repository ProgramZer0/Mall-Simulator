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
    /// Put a pop-up beside a cell, never on top of it: on its right, left, below or above, the first where
    /// it fits inside its parent (slid along that side as needed) without covering Keep Clear (another list
    /// on the screen; optional). Where it doesn't fit as it is (the last column, near an edge), it changes
    /// shape: narrower (and taller, when its text wraps) beside the cell, or wider and shorter above or
    /// below it, to the side with the most room. A pop-up that sizes itself (a Content Size Fitter) grows
    /// to fit its text; one without keeps its height.
    /// </summary>
    public static void PlaceBeside(RectTransform tip, RectTransform cell, Vector2 offset, RectTransform keepClear)
    {
        if (tip == null || cell == null || tip.parent is not RectTransform area) return;
        bool useClear = keepClear != null && !cell.IsChildOf(keepClear);
        Rect bounds = area.rect, c = LocalRect(area, cell);
        Rect clear = useClear ? LocalRect(area, keepClear) : default;
        float gap = Mathf.Max(0f, Mathf.Abs(offset.x));
        tip.pivot = new Vector2(0f, 1f);

        // Back to its own shape (a previous placement may have reshaped it), measured with its new text.
        Vector2 natural = NaturalSize(tip);

        // Room on each side of the cell: right, left, below, above.
        var room = new[]
        {
            new Rect(c.xMax + gap, bounds.yMin, bounds.xMax - c.xMax - gap, bounds.height),
            new Rect(bounds.xMin, bounds.yMin, c.xMin - gap - bounds.xMin, bounds.height),
            new Rect(bounds.xMin, bounds.yMin, bounds.width, c.yMin - gap - bounds.yMin),
            new Rect(bounds.xMin, c.yMax + gap, bounds.width, bounds.yMax - c.yMax - gap),
        };

        // As it is, the first side it fits on (and that keeps Keep Clear clear); then again ignoring Keep Clear.
        for (int pass = 0; pass < 2; pass++)
            for (int side = 0; side < 4; side++)
                if (TryPlace(tip, side, room[side], c, offset, natural, pass == 0 && useClear, clear)) return;

        // Reshaped to the side with the most room (by area), within what that side allows.
        int best = -1;
        float bestArea = 0f;
        for (int side = 0; side < 4; side++)
        {
            float area2 = Mathf.Max(0f, room[side].width) * Mathf.Max(0f, room[side].height);
            if (room[side].width >= 40f && room[side].height >= 30f && area2 > bestArea) { bestArea = area2; best = side; }
        }
        if (best < 0) { TryPlace(tip, 0, room[0], c, offset, natural, false, clear, force: true); return; }
        bool beside = best < 2;
        float width = beside ? Mathf.Min(natural.x, room[best].width) : Mathf.Min(Mathf.Max(natural.x, room[best].width * 0.9f), room[best].width);
        Vector2 size = Reshape(tip, width);
        if (!beside && size.y > room[best].height) size = Reshape(tip, room[best].width); // as wide as it can be, to be short enough
        TryPlace(tip, best, room[best], c, offset, size, false, clear, force: true);
    }

    /// <summary>Put a pop-up beside a cell, never on top of it (see the overload with Keep Clear).</summary>
    public static void PlaceBeside(RectTransform tip, RectTransform cell, Vector2 offset) => PlaceBeside(tip, cell, offset, null);

    // On one side of the cell, slid along it to stay inside its room. False if it doesn't fit there.
    private static bool TryPlace(RectTransform tip, int side, Rect room, Rect c, Vector2 offset, Vector2 size,
                                 bool avoidClear, Rect clear, bool force = false)
    {
        if (!force && (size.x > room.width + 0.5f || size.y > room.height + 0.5f)) return false;
        float x, top;
        if (side < 2) // beside: level with the cell's top (plus offset), slid up or down to fit
        {
            x = side == 0 ? room.xMin : room.xMax - size.x;
            top = Mathf.Clamp(c.yMax + offset.y, room.yMin + size.y, room.yMax);
        }
        else // below or above: lined up with the cell's left, slid sideways to fit
        {
            x = Mathf.Clamp(c.xMin, room.xMin, room.xMax - size.x);
            top = side == 2 ? room.yMax : room.yMin + size.y;
        }
        if (force) { x = Mathf.Max(room.xMin, x); top = Mathf.Min(room.yMax, top); }
        var r = new Rect(x, top - size.y, size.x, size.y);
        if (!force && avoidClear && r.Overlaps(clear)) return false;
        tip.localPosition = new Vector3(x, top, 0f);
        return true;
    }

    // Each pop-up's own width (and whether it sized itself sideways), to go back to after a reshape.
    private static readonly System.Collections.Generic.Dictionary<int, (float width, float height, bool fitsWidth)> Natural = new();

    private static Vector2 NaturalSize(RectTransform tip)
    {
        var fitter = tip.GetComponent<UnityEngine.UI.ContentSizeFitter>();
        int key = tip.GetInstanceID();
        if (Natural.TryGetValue(key, out var n))
        {
            if (n.fitsWidth && fitter != null) fitter.horizontalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
            else tip.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, n.width);
            if (!SizesHeight(fitter)) tip.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, n.height);
        }
        else Natural[key] = (tip.rect.width, tip.rect.height,
                             fitter != null && fitter.horizontalFit != UnityEngine.UI.ContentSizeFitter.FitMode.Unconstrained);
        UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(tip);
        return tip.rect.size;
    }

    // This wide (its text wrapping to it), as tall as that makes it.
    private static Vector2 Reshape(RectTransform tip, float width)
    {
        var fitter = tip.GetComponent<UnityEngine.UI.ContentSizeFitter>();
        if (fitter != null && fitter.horizontalFit != UnityEngine.UI.ContentSizeFitter.FitMode.Unconstrained)
            fitter.horizontalFit = UnityEngine.UI.ContentSizeFitter.FitMode.Unconstrained;
        tip.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, Mathf.Max(40f, width));
        UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(tip);
        if (!SizesHeight(fitter))
        {
            // No fitter to grow it: taller by however much its texts now need (they wrap to the new width).
            float more = 0f;
            foreach (var t in tip.GetComponentsInChildren<TMPro.TMP_Text>())
            {
                t.ForceMeshUpdate();
                more += Mathf.Max(0f, t.preferredHeight - t.rectTransform.rect.height);
            }
            if (more > 0f) tip.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, tip.rect.height + more);
        }
        return tip.rect.size;
    }

    private static bool SizesHeight(UnityEngine.UI.ContentSizeFitter f) =>
        f != null && f.verticalFit != UnityEngine.UI.ContentSizeFitter.FitMode.Unconstrained;

    // A RectTransform's rect in another's local space.
    private static Rect LocalRect(RectTransform space, RectTransform rt)
    {
        Vector3 a = space.InverseTransformPoint(rt.TransformPoint(rt.rect.min));
        Vector3 b = space.InverseTransformPoint(rt.TransformPoint(rt.rect.max));
        return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
    }
}
