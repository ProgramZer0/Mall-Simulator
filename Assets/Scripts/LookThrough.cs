using UnityEngine;

/// <summary>
/// Looking and clicking pass straight through this object's colliders (and its children's) to whatever is
/// behind: put it on a screen or a label sitting in front of something you click (the card reader's
/// display over its keys). Text objects (TextMeshPro) and world-space canvases are passed through already.
/// </summary>
[DisallowMultipleComponent]
public class LookThrough : MonoBehaviour
{
    /// <summary>Should the interact look ignore this collider?</summary>
    public static bool Ignore(Collider c) =>
        c != null && (c.TryGetComponent<TMPro.TMP_Text>(out _) || c.GetComponentInParent<LookThrough>() != null ||
                      c.GetComponentInParent<Canvas>() != null);
}
