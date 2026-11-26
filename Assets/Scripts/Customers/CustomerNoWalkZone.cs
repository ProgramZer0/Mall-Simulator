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
    /// The zone for the walkable-area build: it marks the floor inside it. No Walk: the customers-only-keep-out
    /// area (the same one staff doorways use), so customers never path through it but players' guides
    /// (Find My Dock) still can. Avoid: the avoid area (walkable, but costly to path through). Reaches a
    /// little below the box, so a box sat on the floor still marks it.
    /// </summary>
    public NavMeshBuildSource ToSource(int avoidArea, int noWalkArea, float agentRadius)
    {
        // In world units (any scale). It only marks the floor inside it, so it reaches down to the floor
        // under a box that floats a little (1.5 m), and No Walk is grown by the customers' radius all
        // round: the clearance they kept when it was a solid block.
        Vector3 scale = transform.lossyScale;
        Vector3 size = Vector3.Scale(box.size, new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
        float pad = kind == Kind.Avoid ? 0f : agentRadius;
        const float reachDown = 1.5f;
        size += new Vector3(pad * 2f, reachDown, pad * 2f);
        Vector3 center = transform.TransformPoint(box.center) + Vector3.down * (reachDown * 0.5f);
        return new NavMeshBuildSource
        {
            shape = NavMeshBuildSourceShape.ModifierBox,
            transform = Matrix4x4.TRS(center, transform.rotation, Vector3.one),
            size = size,
            area = kind == Kind.Avoid ? avoidArea : noWalkArea,
        };
    }

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
