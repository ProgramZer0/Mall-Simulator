using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>The line on the floor from you to a dock (made by FindDockApp).</summary>
public class DockGuide : MonoBehaviour
{
    public Vector3 Target { get; private set; }

    /// <summary>
    /// Find My Dock → Debug Log: what the guide sees, in the Console (the walkable area, where you and the
    /// dock are on it, each path tried and why it was turned down). Logged when it changes, at most every
    /// Debug Every seconds otherwise.
    /// </summary>
    public static bool DebugLog;
    public static float DebugEvery = 3f;
    /// <summary>Find My Dock → Debug Draw Partial: when there's no whole way, draw how far it got (red).</summary>
    public static bool DebugDrawPartial;

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
    private float height, arrive, until, nextPath, lastLogAt = -999f;
    private string lastLog = "";
    private Color lineColor;
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
        lineColor = color;
        line.startColor = line.endColor = color;
        if (material != null)
        {
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            else if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        }
        line.widthMultiplier = width;
        path ??= new NavMeshPath();
        nextPath = 0f;
        lastLog = "";
        Log($"Show the way to {target} (dock point). {NavState()}", true);
        gameObject.SetActive(true);
        enabled = true;
    }

    public void Hide() => gameObject.SetActive(false);

    private bool endLooked, endFound;
    private Vector3 endPoint;
    private string endWhy = "";

    private bool EndPoint(out Vector3 point)
    {
        if (!endLooked || !endFound) { endLooked = true; endFound = OnSameFloor(Target, out endPoint, out endWhy); } // (again until found: the floor may still be being built)
        point = endPoint;
        return endFound;
    }

    // The walkable floor nearest the dock, on the dock's own floor. The dock point is the middle of its
    // Shipment Zone box, which can be well above the floor, so first find the floor under it (a ray down,
    // solid colliders only); then the walkable area there (within 1.5 m up or down): straight below
    // first, then rings round it out to 15 m.
    private static bool OnSameFloor(Vector3 target, out Vector3 point, out string why)
    {
        point = target;
        float floorY = target.y;
        string floor = "no floor found under it (kept its own height)";
        if (Physics.Raycast(target + Vector3.up * 0.5f, Vector3.down, out var down, 15f, ~0, QueryTriggerInteraction.Ignore))
        {
            floorY = down.point.y;
            floor = $"floor under it: '{down.collider.name}' (layer {LayerMask.LayerToName(down.collider.gameObject.layer)}) at y {floorY:0.00}" +
                    (CustomerNavMesh.Instance != null && (CustomerNavMesh.Instance.Layers.value & (1 << down.collider.gameObject.layer)) == 0
                        ? " - NOT one of CustomerNavMesh's Layers, so it isn't walkable" : "");
        }
        Vector3 at = new(target.x, floorY, target.z);
        if (NavMesh.SamplePosition(at, out var hit, 1.5f, NavMesh.AllAreas) && Mathf.Abs(hit.position.y - floorY) < 1.5f)
        { point = hit.position; why = $"{floor}; walkable spot right there {hit.position}"; return true; }
        for (float r = 1f; r <= 15f; r += 1f)
            for (int i = 0; i < 16; i++)
            {
                float a = i * Mathf.PI * 2f / 16f;
                Vector3 p = at + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                if (NavMesh.SamplePosition(p, out hit, 1f, NavMesh.AllAreas) && Mathf.Abs(hit.position.y - floorY) < 1.5f)
                { point = hit.position; why = $"{floor}; nearest walkable spot {r:0} m away at {hit.position}"; return true; }
            }
        why = $"{floor}; no walkable area within 15 m on that floor";
        if (NavMesh.SamplePosition(at, out hit, 30f, NavMesh.AllAreas))
            why += $" (nearest walkable area at all: {hit.position}, {Vector3.Distance(hit.position, at):0.0} m away, " +
                   $"{hit.position.y - floorY:+0.0;-0.0} m up/down)";
        else why += " (no walkable area within 30 m at all)";
        return false;
    }

    private void Update()
    {
        var nm = NetworkManager.Singleton;
        var me = nm != null ? nm.LocalClient?.PlayerObject : null;
        if (me == null) { Log("Hidden: no local player.", true); Hide(); return; }
        if (Time.time > until) { Log("Hidden: shown for its full time.", true); Hide(); return; }
        Vector3 from = me.transform.position;
        Vector3 flat = from - Target;
        flat.y *= 0.5f;
        if (flat.magnitude <= arrive) { Log("Hidden: you got there.", true); Hide(); return; }
        if (Time.time < nextPath) return;
        nextPath = Time.time + 0.5f;

        // Along the walkable floor, round walls and through doors: any door you can open (staff and dock
        // doors too: only customers keep out of those), but not a locked one your team can't unlock. Never
        // a straight line through walls: if the floor doesn't join up, nothing's drawn and the app says
        // there's no way there.
        points.Clear();
        var nav = CustomerNavMesh.Instance;
        bool ready = nav == null || nav.Ready;
        bool found = false;
        var report = DebugLog ? new System.Text.StringBuilder() : null;
        report?.Append(NavState()).Append('\n');

        bool fromOk = NavMesh.SamplePosition(from, out var a, 3f, NavMesh.AllAreas);
        if (report != null)
        {
            if (fromOk) report.Append($"You: {from} -> on the walkable area at {a.position} ({Vector3.Distance(from, a.position):0.00} m off)\n");
            else if (NavMesh.SamplePosition(from, out var far, 30f, NavMesh.AllAreas))
                report.Append($"You: {from} -> NOT on the walkable area (nearest is {Vector3.Distance(from, far.position):0.0} m away at {far.position})\n");
            else report.Append($"You: {from} -> no walkable area within 30 m of you at all\n");
        }
        bool endOk = EndPoint(out var b);
        report?.Append($"Dock: {Target} -> {(endOk ? "OK" : "FAILED")}: {endWhy}\n");

        Vector3[] partial = null;
        if (fromOk && endOk)
        {
            // Through locked doors too first (the shortest way, if they're your team's); if that crosses
            // one you can't open, the way that keeps out of every locked door.
            ulong id = nm.LocalClientId;
            found = TryPath(a.position, b, NavMesh.AllAreas, "Every door", report, ref partial);
            if (found && CrossesDoorYouCantOpen(id, out string door))
            {
                report?.Append($"  ...but it goes through locked door '{door}', which your team can't open\n");
                found = false;
            }
            if (!found)
                found = TryPath(a.position, b, NavMesh.AllAreas & ~(1 << CustomerNavMesh.LockedArea), "Not through locked doors", report, ref partial);
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

        bool red = false;
        if (!found && DebugDrawPartial && partial != null)
        {
            foreach (var c in partial) points.Add(c + Vector3.up * height);
            red = true;
        }
        line.startColor = line.endColor = red ? new Color(1f, 0.25f, 0.2f, 0.9f) : lineColor;
        line.positionCount = points.Count;
        for (int i = 0; i < points.Count; i++) line.SetPosition(i, points[i]);

        if (report != null)
        {
            report.Insert(0, found ? "Way found. " : Searching ? "Still building the walkable area. " : "NO PATH. ");
            Log(report.ToString().TrimEnd(), false);
        }
    }

    // A whole way to the dock's floor spot (a partial one counts only if it got there anyway).
    private bool TryPath(Vector3 from, Vector3 to, int mask, string what, System.Text.StringBuilder report, ref Vector3[] partial)
    {
        bool calc = NavMesh.CalculatePath(from, to, mask, path);
        if (!calc || path.status == NavMeshPathStatus.PathInvalid || path.corners.Length < 2)
        {
            report?.Append($"  {what}: no path at all (CalculatePath {calc}, {path.status}, {path.corners.Length} corners)\n");
            return false;
        }
        Vector3 last = path.corners[path.corners.Length - 1];
        bool ok = path.status == NavMeshPathStatus.PathComplete ||
                  (Mathf.Abs(last.y - to.y) < 1f && Vector3.Distance(last, to) < 1.5f);
        if (report != null)
        {
            float len = 0f;
            for (int i = 1; i < path.corners.Length; i++) len += Vector3.Distance(path.corners[i - 1], path.corners[i]);
            report.Append($"  {what}: {path.status}, {path.corners.Length} corners, {len:0.0} m, ends at {last} " +
                          $"({Vector3.Distance(last, to):0.0} m short of the dock spot){(ok ? "" : " - stops short: the walkable area doesn't join up there (a wall, a gap, a step too high, or a door that isn't baked as a doorway)")}\n");
        }
        if (!ok && (partial == null || partial.Length < path.corners.Length)) partial = path.corners;
        return ok;
    }

    // Does the path go through a locked door this player can't open?
    private bool CrossesDoorYouCantOpen(ulong clientId, out string doorName)
    {
        doorName = null;
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
                bool hit = len < 0.001f ? bounds.Contains(p0)
                         : bounds.Contains(p0) || (bounds.IntersectRay(new Ray(p0, d / len), out float t) && t <= len);
                if (hit) { doorName = door.name; return true; }
            }
        }
        return false;
    }

    private static string NavState()
    {
        var nm = NetworkManager.Singleton;
        var nav = CustomerNavMesh.Instance;
        if (nav == null) return "Walkable area: NO CustomerNavMesh in the scene.";
        var bb = nav.BuiltBounds;
        int doors = 0, locked = 0;
        foreach (var d in DoorNet.All) if (d != null && !d.CustomersCanUse) { doors++; if (d.IsLocked) locked++; }
        return $"Walkable area: {(nm != null && nm.IsServer ? "host" : "client")}, {(nav.Ready ? "built" : "NOT BUILT YET")}, " +
               $"{nav.Describe()}, over {bb.min} to {bb.max}; staff doorways area {CustomerNavMesh.StaffArea} ({doors - locked}), " +
               $"locked doorways area {CustomerNavMesh.LockedArea} ({locked}).";
    }

    // Debug Log: when what it says changes, or every Debug Every seconds.
    private void Log(string text, bool always)
    {
        if (!DebugLog) return;
        if (!always && text == lastLog && Time.time - lastLogAt < DebugEvery) return;
        // (the positions change as you walk: compare without the "You:" line, so standing still doesn't spam)
        if (!always && Time.time - lastLogAt < DebugEvery && StripYou(text) == StripYou(lastLog)) return;
        lastLog = text;
        lastLogAt = Time.time;
        Debug.Log("[FindMyDock] " + text, this);
    }

    private static string StripYou(string s)
    {
        var lines = s.Split('\n');
        var sb = new System.Text.StringBuilder();
        foreach (var l in lines) if (!l.StartsWith("You:") && !l.Contains(" m, ends at ")) sb.Append(l).Append('\n');
        return sb.ToString();
    }
}
