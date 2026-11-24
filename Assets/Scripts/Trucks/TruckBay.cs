using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Where a delivery truck parks at a loading dock. Put it on the dock floor where the back of the truck
/// should stop, its blue arrow (Z) pointing out of the dock (the way the truck drives off). It belongs to the
/// LoadingDockZone it's in (Dock, if not set); any store whose shipment spots are in that dock can have its
/// trucks park here (one truck per store at a time).
/// </summary>
public class TruckBay : MonoBehaviour
{
    public static readonly List<TruckBay> All = new();

    [Tooltip("The dock this bay serves. Empty: the LoadingDockZone it stands in.")]
    [SerializeField] private LoadingDockZone dock;

    public LoadingDockZone Dock => dock != null ? dock : dock = LoadingDockZone.GetDockAt(transform.position + Vector3.up * 0.1f);

    /// <summary>Its place in All (the same on every machine: sorted by name, then position).</summary>
    public int Index => All.IndexOf(this);

    private void OnEnable()
    {
        All.Add(this);
        All.Sort((a, b) =>
        {
            int c = string.CompareOrdinal(a.name, b.name);
            return c != 0 ? c : a.transform.position.sqrMagnitude.CompareTo(b.transform.position.sqrMagnitude);
        });
    }

    private void OnDisable() => All.Remove(this);

    private void OnDrawGizmos()
    {
        Gizmos.color = new Color(1f, 0.7f, 0.2f, 0.9f);
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawWireCube(new Vector3(0f, 1.5f, 4f), new Vector3(2.6f, 3f, 8f)); // about where a truck goes
        Gizmos.DrawLine(Vector3.zero, Vector3.forward * 2f);
    }
}
