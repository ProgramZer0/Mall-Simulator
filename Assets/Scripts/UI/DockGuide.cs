using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>The line on the floor from you to a dock (made by FindDockApp).</summary>
public class DockGuide : MonoBehaviour
{
    public Vector3 Target { get; private set; }

    private LineRenderer line;
    private NavMeshPath path;
    private float height, arrive, until, nextPath;
    private static Material fallback;
    private readonly List<Vector3> points = new();

    public void Show(Vector3 target, Material material, Color color, float width, float lineHeight, float arriveDistance, float seconds)
    {
        Target = target;
        endLooked = false;
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
        if (!endLooked) { endLooked = true; endFound = OnSameFloor(Target, out endPoint); }
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

        // Along the walkable floor. The dock's end is found on its own floor (a wide search could land on
        // the floor below and send you downstairs), and where the walkable floor stops short of the dock,
        // the line only goes straight on for the last few meters (never across the mall through walls).
        points.Clear();
        if (NavMesh.SamplePosition(from, out var a, 3f, NavMesh.AllAreas) && EndPoint(out var b) &&
            NavMesh.CalculatePath(a.position, b, NavMesh.AllAreas, path) &&
            path.status != NavMeshPathStatus.PathInvalid && path.corners.Length >= 2)
        {
            foreach (var c in path.corners) points.Add(c + Vector3.up * height);
            Vector3 last = path.corners[path.corners.Length - 1];
            if (Mathf.Abs(last.y - Target.y) < 1.5f && Vector3.Distance(last, Target) < 8f)
                points.Add(new Vector3(Target.x, last.y, Target.z) + Vector3.up * height);
        }
        else
        {
            points.Add(from + Vector3.up * height);
            points.Add(Target + Vector3.up * height);
        }
        line.positionCount = points.Count;
        for (int i = 0; i < points.Count; i++) line.SetPosition(i, points[i]);
    }
}
