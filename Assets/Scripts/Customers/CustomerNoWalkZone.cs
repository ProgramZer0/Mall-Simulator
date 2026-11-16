using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Somewhere customers never walk: put it on an object with a Box Collider and size the box over a
/// doorway, a staff corridor, a stage... (floor to about head height). The collider is made a trigger,
/// so players walk through it as normal; only customer paths go around it.
///
/// Set Kind to Avoid for somewhere they'd rather not walk (the inside of a busy corner): paths go round
/// it when there's another way and nobody cuts through it, but bumping into each other can still push
/// them in, and it's crossed if it's the only way.
///
/// The rest is automatic: customers can walk anywhere there's floor, so it's usually less work to
/// block the few places they shouldn't go than to mark where they can.
/// </summary>
[RequireComponent(typeof(BoxCollider))]
public class CustomerNoWalkZone : MonoBehaviour
{
    public static readonly List<CustomerNoWalkZone> All = new();

    public enum Kind { NoWalk, Avoid }

    [Tooltip("No Walk: customers never go here. Avoid: they keep out of it when there's another way, but " +
             "can still be bumped into it (see the CustomerNavMesh's Avoid settings for how hard they try).")]
    public Kind kind = Kind.NoWalk;

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

    private void OnValidate() => Version++; // kind or size changed in the Inspector: paths update

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

    /// <summary>
    /// The zone for the walkable-area build: No Walk is a solid, not-walkable block; Avoid marks the floor
    /// inside it as the avoid area (walkable, but costly to path through).
    /// </summary>
    public NavMeshBuildSource ToSource(int avoidArea) => new()
    {
        shape = kind == Kind.Avoid ? NavMeshBuildSourceShape.ModifierBox : NavMeshBuildSourceShape.Box,
        transform = transform.localToWorldMatrix * Matrix4x4.Translate(box.center),
        size = box.size,
        area = kind == Kind.Avoid ? avoidArea : 1, // 1: Not Walkable
    };

    public Bounds WorldBounds => box.bounds;

    private void OnDrawGizmos()
    {
        var b = box != null ? box : GetComponent<BoxCollider>();
        if (b == null) return;
        Gizmos.matrix = transform.localToWorldMatrix;
        Color c = kind == Kind.Avoid ? new Color(1f, 0.75f, 0.1f) : new Color(1f, 0.2f, 0.2f);
        Gizmos.color = new Color(c.r, c.g, c.b, 0.25f);
        Gizmos.DrawCube(b.center, b.size);
        Gizmos.color = new Color(c.r, c.g, c.b, 0.9f);
        Gizmos.DrawWireCube(b.center, b.size);
    }
}
