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

        // Along the walkable floor if there's a way (the mall's NavMesh), else straight there.
        points.Clear();
        bool found = false;
        if (NavMesh.SamplePosition(from, out var a, 3f, NavMesh.AllAreas) &&
            NavMesh.SamplePosition(Target, out var b, 4f, NavMesh.AllAreas) &&
            NavMesh.CalculatePath(a.position, b.position, NavMesh.AllAreas, path) &&
            path.status != NavMeshPathStatus.PathInvalid && path.corners.Length >= 2)
        {
            found = true;
            foreach (var c in path.corners) points.Add(c + Vector3.up * height);
            if (path.status == NavMeshPathStatus.PathPartial) points.Add(Target + Vector3.up * height);
        }
        if (!found)
        {
            points.Add(from + Vector3.up * height);
            points.Add(Target + Vector3.up * height);
        }
        line.positionCount = points.Count;
        for (int i = 0; i < points.Count; i++) line.SetPosition(i, points[i]);
    }
}
