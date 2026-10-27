using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A shared loading dock. Deliveries arrive here, and any player may pick items up or set
/// them down inside it, so they can collect their shipments and load carts.
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

    // TODO (GDD 13a): deliveries stay reserved for their owner for 10 minutes before anyone
    // may claim them. That check belongs per delivery item, consulted from
    // MallManager.CanPickupOrPlace when the target is inside a dock.

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
