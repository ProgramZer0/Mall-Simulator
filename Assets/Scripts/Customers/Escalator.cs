using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// A one-way escalator: Way Up carries people from Bottom to Top, Way Down from Top to Bottom. Put one on
/// each escalator (a pair: one Up, one Down).
///
/// Set up:
/// - Bottom and Top: empty objects on the landings just off each end of the steps, on the walkable floor.
/// - Steps: a BoxCollider over the moving steps (a trigger is fine). Players standing in it are carried
///   along at Speed (they can still walk on it). Customers never walk onto it the wrong way: its floor is
///   kept out of their walkable area, and a one-way link from its start to its end is the only way across.
///   Customers ride it standing, at Speed.
/// </summary>
public class Escalator : MonoBehaviour
{
    public static readonly List<Escalator> All = new();

    public enum Way { Up, Down }

    [SerializeField] private Transform bottom;
    [SerializeField] private Transform top;
    [SerializeField] private Way way = Way.Up;
    [Tooltip("The moving steps (a BoxCollider over them, trigger or not).")]
    [SerializeField] private BoxCollider steps;
    [Tooltip("Meters per second along the steps.")]
    [SerializeField, Min(0.05f)] private float speed = 0.6f;
    [Tooltip("How wide the way across is for customers (meters).")]
    [SerializeField, Min(0.3f)] private float width = 1f;

    public Way Direction => way;
    public float Speed => speed;
    public Vector3 Start => (way == Way.Up ? bottom : top) != null ? (way == Way.Up ? bottom : top).position : transform.position;
    public Vector3 End => (way == Way.Up ? top : bottom) != null ? (way == Way.Up ? top : bottom).position : transform.position;

    private NavMeshLinkInstance link;

    private void OnEnable()
    {
        All.Add(this);
        AddLink();
        CustomerNavMesh.Instance?.MarkDirty(); // its steps kept out of the walkable area
    }

    private void OnDisable()
    {
        All.Remove(this);
        if (link.valid) link.Remove();
        CustomerNavMesh.Instance?.MarkDirty();
    }

    private void AddLink()
    {
        if (bottom == null || top == null) return;
        if (link.valid) link.Remove();
        link = NavMesh.AddLink(new NavMeshLinkData
        {
            startPosition = Start,
            endPosition = End,
            width = width,
            bidirectional = false, // one way only
            costModifier = -1f,
            area = 0,
            agentTypeID = NavMesh.GetSettingsByID(0).agentTypeID,
        });
    }

    /// <summary>For the walkable-area build: the steps' floor, kept out of customers' area.</summary>
    public bool TryGetSource(int keepOutArea, out NavMeshBuildSource source)
    {
        source = default;
        if (steps == null) return false;
        var t = steps.transform;
        Vector3 s = t.lossyScale;
        Vector3 size = Vector3.Scale(steps.size, new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z))) + Vector3.up * 1f;
        source = new NavMeshBuildSource
        {
            shape = NavMeshBuildSourceShape.ModifierBox,
            transform = Matrix4x4.TRS(t.TransformPoint(steps.center) + Vector3.down * 0.5f, t.rotation, Vector3.one),
            size = size,
            area = keepOutArea,
        };
        return true;
    }

    public Bounds StepsBounds => steps != null ? steps.bounds : new Bounds(transform.position, Vector3.zero);

    /// <summary>Is this stretch of a path (From → To) this escalator's way across? (Customers ride it.)</summary>
    public static bool TryRide(Vector3 from, Vector3 to, out Escalator escalator)
    {
        foreach (var e in All)
            if (e != null && (from - e.Start).sqrMagnitude < 1.5f * 1.5f && (to - e.End).sqrMagnitude < 1.5f * 1.5f)
            { escalator = e; return true; }
        escalator = null;
        return false;
    }

    private bool InSteps(Vector3 p)
    {
        if (steps == null) return false;
        Vector3 local = steps.transform.InverseTransformPoint(p) - steps.center;
        Vector3 h = steps.size * 0.5f + new Vector3(0f, 1f, 0f); // feet a little above
        return Mathf.Abs(local.x) <= h.x && Mathf.Abs(local.y) <= h.y && Mathf.Abs(local.z) <= h.z;
    }

    // The local player standing on the steps is carried along.
    private void Update()
    {
        var nm = NetworkManager.Singleton;
        var me = nm != null ? nm.LocalClient?.PlayerObject : null;
        if (me == null || !InSteps(me.transform.position)) return;
        if (!me.TryGetComponent<CharacterController>(out var cc) || !cc.enabled) return;
        Vector3 dir = End - Start;
        if (dir.sqrMagnitude < 1e-4f) return;
        cc.Move(dir.normalized * (speed * Time.deltaTime));
    }

    private void OnDrawGizmos()
    {
        if (bottom == null || top == null) return;
        Gizmos.color = way == Way.Up ? Color.green : Color.cyan;
        Vector3 a = Start, b = End;
        Gizmos.DrawLine(a, b);
        Vector3 d = (b - a).normalized;
        Vector3 side = Vector3.Cross(d, Vector3.up).normalized * 0.3f;
        Gizmos.DrawLine(b, b - d * 0.5f + side);
        Gizmos.DrawLine(b, b - d * 0.5f - side);
    }
}
