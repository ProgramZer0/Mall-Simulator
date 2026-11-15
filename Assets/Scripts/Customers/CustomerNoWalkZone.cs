using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Somewhere customers never walk: put it on an object with a Box Collider and size the box over a
/// doorway, a staff corridor, a stage... (floor to about head height). The collider is made a trigger,
/// so players walk through it as normal; only customer paths go around it.
///
/// The rest is automatic: customers can walk anywhere there's floor, so it's usually less work to
/// block the few places they shouldn't go than to mark where they can.
/// </summary>
[RequireComponent(typeof(BoxCollider))]
public class CustomerNoWalkZone : MonoBehaviour
{
    public static readonly List<CustomerNoWalkZone> All = new();

    /// <summary>Goes up when a zone is added, removed or moved (customer paths update).</summary>
    public static int Version { get; private set; }

    private BoxCollider box;
    private Matrix4x4 lastPose;

    private void Reset()
    {
        var b = GetComponent<BoxCollider>();
        b.isTrigger = true;
        b.size = new Vector3(2f, 2.2f, 1f);
        b.center = new Vector3(0f, 1.1f, 0f);
    }

    private void Awake()
    {
        box = GetComponent<BoxCollider>();
        box.isTrigger = true; // never in a player's way
    }

    private void OnEnable()
    {
        All.Add(this);
        lastPose = transform.localToWorldMatrix;
        Version++;
    }

    private void OnDisable()
    {
        All.Remove(this);
        Version++;
    }

    private void Update()
    {
        // Moved (an animated barrier, a door frame that slides): paths update.
        if (transform.hasChanged && transform.localToWorldMatrix != lastPose)
        {
            lastPose = transform.localToWorldMatrix;
            Version++;
        }
        transform.hasChanged = false;
    }

    /// <summary>The zone as a solid, not-walkable block for the walkable-area build.</summary>
    public NavMeshBuildSource ToSource() => new()
    {
        shape = NavMeshBuildSourceShape.Box,
        transform = transform.localToWorldMatrix * Matrix4x4.Translate(box.center),
        size = box.size,
        area = 1, // Not Walkable
    };

    public Bounds WorldBounds => box.bounds;

    private void OnDrawGizmos()
    {
        var b = box != null ? box : GetComponent<BoxCollider>();
        if (b == null) return;
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.25f);
        Gizmos.DrawCube(b.center, b.size);
        Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.9f);
        Gizmos.DrawWireCube(b.center, b.size);
    }
}
