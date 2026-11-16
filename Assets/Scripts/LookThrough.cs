using UnityEngine;

/// <summary>
/// Looking and clicking pass straight through this object's colliders (and its children's) to whatever is
/// behind: put it on a screen or a label sitting in front of something you click (the card reader's
/// display over its keys). Text objects (TextMeshPro) and world-space canvases are passed through already.
/// </summary>
[DisallowMultipleComponent]
public class LookThrough : MonoBehaviour
{
    /// <summary>
    /// Should the interact look ignore this collider? Text and canvases always; under a LookThrough too,
    /// except a checkout part to click that's inside it (a LookThrough on the whole card reader still
    /// lets its keys be pressed: only the rest of it is seen through).
    /// </summary>
    public static bool Ignore(Collider c)
    {
        if (c == null) return false;
        if (c.TryGetComponent<TMPro.TMP_Text>(out _) || c.GetComponentInParent<Canvas>() != null) return true;
        var through = c.GetComponentInParent<LookThrough>();
        if (through == null) return false;
        var part = c.GetComponentInParent<CheckoutClickable>();
        return part == null || !part.transform.IsChildOf(through.transform);
    }
}
