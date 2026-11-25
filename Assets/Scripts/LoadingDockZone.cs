using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A shared loading dock: any player may pick items up or set them down inside it, so they can
/// collect deliveries and load carts. Deliveries themselves land on each store's own shipment
/// spots (StorePlot > Shipment Areas), which can sit inside a dock.
///
/// Scene setup: put this on an empty object per dock and add child BoxColliders covering the
/// dock floor (tall enough to contain a standing player). Like StorePlot, the colliders are
/// only shapes; they're switched off at runtime so they never block movement or aiming.
///
/// No NetworkObject needed: the dock never changes, so every client already agrees on it.
/// </summary>
public class LoadingDockZone : MonoBehaviour
{
    private static readonly List<LoadingDockZone> active = new();

    [SerializeField] private string displayName = "Loading Dock";
    [SerializeField] private BoxCollider[] areas;

    public string DisplayName => displayName;

    /// <summary>Every dock that's switched on.</summary>
    public static IReadOnlyList<LoadingDockZone> All => active;

    /// <summary>The space its boxes cover (their colliders are off at runtime, so worked out from them).</summary>
    public bool TryGetBounds(out Bounds bounds)
    {
        bounds = default;
        bool any = false;
        if (areas == null) return false;
        foreach (var box in areas)
        {
            if (box == null) continue;
            for (int i = 0; i < 8; i++)
            {
                Vector3 c = box.center + Vector3.Scale(box.size * 0.5f,
                    new Vector3(i % 2 == 0 ? -1f : 1f, (i / 2) % 2 == 0 ? -1f : 1f, i < 4 ? -1f : 1f));
                Vector3 w = box.transform.TransformPoint(c);
                if (!any) { bounds = new Bounds(w, Vector3.zero); any = true; }
                else bounds.Encapsulate(w);
            }
        }
        return any;
    }

    private void Awake()
    {
        foreach (var box in areas)
            if (box != null) box.enabled = false;
    }

    private void OnEnable() => active.Add(this);
    private void OnDisable() => active.Remove(this);

    public bool Contains(Vector3 worldPoint)
    {
        foreach (var box in areas)
            if (StorePlot.BoxContains(box, worldPoint)) return true;
        return false;
    }

    /// <summary>The dock containing this point, or null.</summary>
    public static LoadingDockZone GetDockAt(Vector3 worldPoint)
    {
        foreach (var dock in active)
            if (dock.Contains(worldPoint)) return dock;
        return null;
    }

    private void OnDrawGizmos()
    {
        Gizmos.color = new Color(0.7f, 0.4f, 1f, 0.6f);
        foreach (var box in areas)
        {
            if (box == null) continue;
            Gizmos.matrix = box.transform.localToWorldMatrix;
            Gizmos.DrawWireCube(box.center, box.size);
        }
        Gizmos.matrix = Matrix4x4.identity;
    }
}
