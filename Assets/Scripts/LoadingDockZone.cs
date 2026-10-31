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

    [Header("Deliveries")]
    [Tooltip("Spots where ordered items appear (empty child objects; their rotation sets the item's " +
             "facing). One item per spot; when all are taken, deliveries wait until one is free.")]
    [SerializeField] private Transform[] shipmentAreas;
    [Tooltip("A spot counts as taken while any item is within this many meters of it.")]
    [SerializeField] private float shipmentAreaRadius = 0.75f;

    public string DisplayName => displayName;
    public int ShipmentAreaCount => shipmentAreas?.Length ?? 0;

    /// <summary>Server: the first shipment spot with nothing on it.</summary>
    public bool TryGetFreeShipmentArea(out Transform area)
    {
        area = null;
        if (shipmentAreas == null) return false;
        foreach (var a in shipmentAreas)
        {
            if (a == null || IsOccupied(a.position)) continue;
            area = a;
            return true;
        }
        return false;
    }

    private bool IsOccupied(Vector3 spot)
    {
        float r2 = shipmentAreaRadius * shipmentAreaRadius;
        foreach (var item in ItemPickupNet.All)
        {
            if (item == null) continue;
            Vector3 d = item.transform.position - spot;
            if (d.x * d.x + d.z * d.z <= r2 && Mathf.Abs(d.y) < 2f) return true;
        }
        return false;
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

    // Deliveries are held for the ordering team for a while (EconomyManager > Reservation Seconds,
    // checked in PlayerInventoryNet's pickup) before anyone may take them (GDD 13a).

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

        // Shipment spots: a ring of the free-check radius plus an arrow for which way items face.
        if (shipmentAreas == null) return;
        Gizmos.color = new Color(1f, 0.6f, 0.9f, 0.9f);
        foreach (var a in shipmentAreas)
        {
            if (a == null) continue;
            Vector3 p = a.position;
            const int seg = 24;
            for (int i = 0; i < seg; i++)
            {
                float t0 = i * Mathf.PI * 2f / seg, t1 = (i + 1) * Mathf.PI * 2f / seg;
                Gizmos.DrawLine(p + new Vector3(Mathf.Cos(t0), 0f, Mathf.Sin(t0)) * shipmentAreaRadius,
                                p + new Vector3(Mathf.Cos(t1), 0f, Mathf.Sin(t1)) * shipmentAreaRadius);
            }
            Gizmos.DrawLine(p, p + a.forward * shipmentAreaRadius);
        }
    }
}
