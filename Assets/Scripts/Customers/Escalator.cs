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
        if (NavMesh.IsLinkValid(link)) NavMesh.RemoveLink(link);
        CustomerNavMesh.Instance?.MarkDirty();
    }

    // Its link's ends: Start and End, moved onto the nearest floor customers may walk on (within 2 m), so
    // a Bottom or Top a little off (or inside the steps' kept-out floor) still joins up.
    private Vector3 linkStart, linkEnd;
    private bool startOnFloor, endOnFloor;

    private void AddLink()
    {
        if (bottom == null || top == null) return;
        if (NavMesh.IsLinkValid(link)) NavMesh.RemoveLink(link);
        startOnFloor = NavMesh.SamplePosition(Start, out var s, 2f, CustomerNavMesh.CustomerMask);
        endOnFloor = NavMesh.SamplePosition(End, out var e, 2f, CustomerNavMesh.CustomerMask);
        linkStart = startOnFloor ? s.position : Start;
        linkEnd = endOnFloor ? e.position : End;
        link = NavMesh.AddLink(new NavMeshLinkData
        {
            startPosition = linkStart,
            endPosition = linkEnd,
            width = width,
            bidirectional = false, // one way only
            costModifier = -1f,
            area = 0,
            agentTypeID = NavMesh.GetSettingsByID(0).agentTypeID,
        });
    }

    /// <summary>The walkable area was (re)built: links redone onto it, and each escalator checked (logged once).</summary>
    public static void AfterBuild()
    {
        foreach (var e in All) if (e != null) { e.AddLink(); e.Check(); }
    }

    private string lastCheck;
    private static NavMeshPath testPath; // made on first use (Unity won't make one while loading the class)

    // Can a customer get from its start to its end? Says what's wrong if not.
    private void Check()
    {
        string problem = null;
        if (bottom == null || top == null) problem = "Bottom and Top need setting";
        else if (steps == null) problem = "Steps (its BoxCollider) needs setting: without it customers walk it both ways";
        else if (!startOnFloor) problem = $"no customer floor within 2 m of its start {Start} (put {(way == Way.Up ? "Bottom" : "Top")} on the landing, off the steps)";
        else if (!endOnFloor) problem = $"no customer floor within 2 m of its end {End} (put {(way == Way.Up ? "Top" : "Bottom")} on the landing, off the steps)";
        else if (!NavMesh.IsLinkValid(link)) problem = "its link didn't take (agent type?)";
        else if (!NavMesh.CalculatePath(linkStart, linkEnd, CustomerNavMesh.CustomerMask, testPath ??= new NavMeshPath()) || testPath.status != NavMeshPathStatus.PathComplete)
            problem = $"a customer can't get from its start to its end ({testPath.status})";
        string text = problem == null ? "ok" : problem;
        if (text == lastCheck) return;
        lastCheck = text;
        if (problem != null) Debug.LogWarning($"[Escalator] '{name}' ({way}): {problem}.", this);
        else Debug.Log($"[Escalator] '{name}' ({way}): customers can ride it ({linkStart} → {linkEnd}).", this);
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
            if (e != null && (from - e.linkStart).sqrMagnitude < 1.5f * 1.5f && (to - e.linkEnd).sqrMagnitude < 1.5f * 1.5f)
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
