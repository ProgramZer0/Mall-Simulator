using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>The line on the floor from you to a dock (made by FindDockApp).</summary>
public class DockGuide : MonoBehaviour
{
    public Vector3 Target { get; private set; }

    /// <summary>There's no walkable way from you to the dock (nothing's drawn; the app says so).</summary>
    public bool NoPath { get; private set; }

    /// <summary>The walkable area's still being worked out on this machine (the app says so).</summary>
    public bool Searching { get; private set; }

    private void OnEnable()
    {
        if (!CustomerNavMesh.NeededHere && CustomerNavMesh.Instance != null) CustomerNavMesh.Instance.MarkDirty();
        CustomerNavMesh.NeededHere = true; // (only the host builds it otherwise)
    }

    private void OnDisable() => CustomerNavMesh.NeededHere = false;

    private LineRenderer line;
    private NavMeshPath path;
    private float height, arrive, until, nextPath;
    private static Material fallback;
    private readonly List<Vector3> points = new();

    public void Show(Vector3 target, Material material, Color color, float width, float lineHeight, float arriveDistance, float seconds)
    {
        Target = target;
        endLooked = false;
        NoPath = false;
        Searching = true;
        height = lineHeight;
        arrive = arriveDistance;
        until = seconds > 0f ? Time.time + seconds : float.MaxValue;
        if (line == null)
        {
            line = gameObject.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.numCornerVertices = 4;
            line.numCapVertices = 4;
            line.alignment = LineAlignment.View;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
        }
        if (material == null)
        {
            if (fallback == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
                if (shader != null) fallback = new Material(shader) { name = "Dock Guide" };
            }
            material = fallback;
        }
        line.sharedMaterial = material;
        line.startColor = line.endColor = color;
        if (material != null)
        {
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            else if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        }
        line.widthMultiplier = width;
        path ??= new NavMeshPath();
        nextPath = 0f;
        gameObject.SetActive(true);
        enabled = true;
    }

    public void Hide() => gameObject.SetActive(false);

    private bool endLooked, endFound;
    private Vector3 endPoint;

    private bool EndPoint(out Vector3 point)
    {
        if (!endLooked || !endFound) { endLooked = true; endFound = OnSameFloor(Target, out endPoint); } // (again until found: the floor may still be being built)
        point = endPoint;
        return endFound;
    }

    // The walkable floor nearest the dock, on the dock's own floor (within 1.5 m up or down): straight
    // below first, then rings round it out to 15 m.
    private static bool OnSameFloor(Vector3 target, out Vector3 point)
    {
        point = target;
        if (NavMesh.SamplePosition(target, out var hit, 1.5f, NavMesh.AllAreas) && Mathf.Abs(hit.position.y - target.y) < 1.5f)
        { point = hit.position; return true; }
        for (float r = 1f; r <= 15f; r += 1f)
            for (int i = 0; i < 16; i++)
            {
                float a = i * Mathf.PI * 2f / 16f;
                Vector3 p = target + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                if (NavMesh.SamplePosition(p, out hit, 1f, NavMesh.AllAreas) && Mathf.Abs(hit.position.y - target.y) < 1.5f)
                { point = hit.position; return true; }
            }
        return false;
    }

    private void Update()
    {
        var nm = NetworkManager.Singleton;
        var me = nm != null ? nm.LocalClient?.PlayerObject : null;
        if (me == null || Time.time > until) { Hide(); return; }
        Vector3 from = me.transform.position;
        Vector3 flat = from - Target;
        flat.y *= 0.5f;
        if (flat.magnitude <= arrive) { Hide(); return; }
        if (Time.time < nextPath) return;
        nextPath = Time.time + 0.5f;

        // Along the walkable floor, round walls and through doors: any door you can open (staff and dock
        // doors too: only customers keep out of those), but not a locked one your team can't unlock. The
        // dock's end is found on its own floor (a wide search could land on the floor below and send you
        // downstairs). Never a straight line through walls: if the floor doesn't join up, nothing's drawn
        // and the app says there's no way there.
        points.Clear();
        var nav = CustomerNavMesh.Instance;
        bool ready = nav == null || nav.Ready;
        bool found = false;
        if (NavMesh.SamplePosition(from, out var a, 3f, NavMesh.AllAreas) && EndPoint(out var b))
        {
            // Through locked doors too first (the shortest way, if they're your team's); if that crosses
            // one you can't open, the way that keeps out of every locked door.
            ulong me2 = nm.LocalClientId;
            found = TryPath(a.position, b, NavMesh.AllAreas) && !CrossesDoorYouCantOpen(me2);
            if (!found) found = TryPath(a.position, b, NavMesh.AllAreas & ~(1 << CustomerNavMesh.LockedArea));
            if (found)
            {
                Vector3 last = path.corners[path.corners.Length - 1];
                foreach (var c in path.corners) points.Add(c + Vector3.up * height);
                if (Mathf.Abs(last.y - Target.y) < 1.5f && Vector3.Distance(last, Target) < 3f)
                    points.Add(new Vector3(Target.x, last.y, Target.z) + Vector3.up * height);
            }
        }
        Searching = !found && !ready;
        NoPath = !found && ready;
        line.positionCount = points.Count;
        for (int i = 0; i < points.Count; i++) line.SetPosition(i, points[i]);
    }

    // A whole way to the dock's floor spot (a partial one counts only if it got there anyway).
    private bool TryPath(Vector3 from, Vector3 to, int mask)
    {
        if (!NavMesh.CalculatePath(from, to, mask, path) || path.status == NavMeshPathStatus.PathInvalid ||
            path.corners.Length < 2) return false;
        Vector3 last = path.corners[path.corners.Length - 1];
        return path.status == NavMeshPathStatus.PathComplete ||
               (Mathf.Abs(last.y - to.y) < 1f && Vector3.Distance(last, to) < 1.5f);
    }

    // Does the path go through a locked door this player can't open?
    private bool CrossesDoorYouCantOpen(ulong clientId)
    {
        var corners = path.corners;
        foreach (var door in DoorNet.All)
        {
            if (door == null || !door.IsLocked || door.CanOpen(clientId) ||
                !door.TryGetComponent<DoorMotion>(out var motion) || !motion.TryGetClosedBounds(out var bounds)) continue;
            bounds.Expand(new Vector3(0.6f, 0.6f, 0.6f));
            for (int i = 1; i < corners.Length; i++)
            {
                Vector3 p0 = corners[i - 1], d = corners[i] - p0;
                float len = d.magnitude;
                if (len < 0.001f) { if (bounds.Contains(p0)) return true; continue; }
                if (bounds.Contains(p0) || (bounds.IntersectRay(new Ray(p0, d / len), out float t) && t <= len)) return true;
            }
        }
        return false;
    }
}
