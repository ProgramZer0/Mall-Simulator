using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Marks which parts of something can be painted (see PaintPalette): e.g. a shelf's frame but not
/// its price tags. Put it on the root of an item's world prefab (next to ItemPickupNet), a front wall
/// module, or anything else a store paints.
///
/// Parts: a Renderer and which of its materials (Material Index -1 = all of them). Leave the list
/// empty to paint every renderer in the object. An item with no PaintParts can't be painted.
///
/// The hand model (the item's Hand Visual Prefab) can have its own PaintParts so it shows the color
/// while carried.
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

    [Tooltip("Renderers (and materials) that take the paint. Empty = every renderer in this object.")]
    [SerializeField] private List<Part> parts = new();

    public byte Current { get; private set; }

    /// <summary>Paint this object's parts (0 = back to its own colors).</summary>
    public void Apply(byte id)
    {
        Current = id;
        if (parts.Count == 0)
        {
            foreach (var r in GetComponentsInChildren<Renderer>(true)) PaintPalette.Apply(r, -1, id);
            return;
        }
        foreach (var p in parts)
            if (p.renderer != null) PaintPalette.Apply(p.renderer, p.materialIndex, id);
    }

    /// <summary>
    /// Paint a copy of a prefab that has PaintParts (edit mode's copies lose their scripts): the same
    /// parts, found in the copy by where they sit in the hierarchy.
    /// </summary>
    public static void ApplyToCopy(GameObject prefab, GameObject copy, byte id)
    {
        if (prefab == null || copy == null || !prefab.TryGetComponent<PaintParts>(out var source)) return;
        if (source.parts.Count == 0)
        {
            foreach (var r in copy.GetComponentsInChildren<Renderer>(true)) PaintPalette.Apply(r, -1, id);
            return;
        }
        foreach (var p in source.parts)
        {
            if (p.renderer == null) continue;
            string path = PathFrom(prefab.transform, p.renderer.transform);
            if (path == null) continue;
            Transform t = path.Length == 0 ? copy.transform : copy.transform.Find(path);
            if (t != null && t.TryGetComponent<Renderer>(out var r)) PaintPalette.Apply(r, p.materialIndex, id);
        }
    }

    /// <summary>Can this prefab be painted?</summary>
    public static bool IsPaintable(GameObject prefab) => prefab != null && prefab.TryGetComponent<PaintParts>(out _);

    /// <summary>Can this catalog item be painted?</summary>
    public static bool IsPaintable(ItemDef def) =>
        def != null && def.worldNetworkPrefab != null && IsPaintable(def.worldNetworkPrefab.gameObject);

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
