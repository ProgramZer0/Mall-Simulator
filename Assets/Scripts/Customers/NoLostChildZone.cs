using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Somewhere a lost child never ends up (they walk on to somewhere else): a BoxCollider on the same
/// object marks the space (it's switched off at runtime). Stairs, ramps, doorways and stores are left out
/// already; use this for anything else (an escalator landing, a fountain edge, a narrow walkway).
/// </summary>
[RequireComponent(typeof(BoxCollider))]
public class NoLostChildZone : MonoBehaviour
{
    private static readonly List<NoLostChildZone> all = new();
    private BoxCollider box;

    private void Awake()
    {
        box = GetComponent<BoxCollider>();
        box.isTrigger = true;
        box.enabled = false;
    }

    private void OnEnable() => all.Add(this);
    private void OnDisable() => all.Remove(this);

    /// <summary>Is the spot inside any No Lost Child Zone?</summary>
    public static bool Contains(Vector3 point)
    {
        foreach (var z in all)
        {
            if (z == null || z.box == null) continue;
            Vector3 local = z.transform.InverseTransformPoint(point) - z.box.center;
            Vector3 h = z.box.size * 0.5f;
            if (Mathf.Abs(local.x) <= h.x && Mathf.Abs(local.y) <= h.y + 1f && Mathf.Abs(local.z) <= h.z) return true;
        }
        return false;
    }

    private void OnDrawGizmos()
    {
        var b = GetComponent<BoxCollider>();
        if (b == null) return;
        Gizmos.color = new Color(1f, 0.5f, 0.8f, 0.6f);
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawWireCube(b.center, b.size);
    }
}
