using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A thin box outline drawn around whatever the local player is aiming at (the product F would take,
/// the shelf space it would stock, what the price scanner would price), so it's clear what a key will
/// act on. Local only: nobody else sees it.
///
/// Use: TargetOutline.For(this).Show(...) every frame you have a target, Hide() when you don't.
/// Each owner gets its own outline (stocking and the price scanner can't fight over one).
/// </summary>
public class TargetOutline : MonoBehaviour
{
    private static readonly Dictionary<Object, TargetOutline> Owned = new();
    private static Material lineMaterial;

    private LineRenderer line;
    private readonly Vector3[] points = new Vector3[16];

    /// <summary>The outline belonging to this owner (made the first time).</summary>
    public static TargetOutline For(Object owner)
    {
        if (Owned.TryGetValue(owner, out var o) && o != null) return o;
        var go = new GameObject($"[Target Outline] {owner.name}");
        o = go.AddComponent<TargetOutline>();
        o.Build();
        Owned[owner] = o;
        return o;
    }

    /// <summary>Remove this owner's outline (call from the owner's OnDestroy).</summary>
    public static void Release(Object owner)
    {
        if (Owned.TryGetValue(owner, out var o) && o != null) Destroy(o.gameObject);
        Owned.Remove(owner);
    }

    private void Build()
    {
        line = gameObject.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.loop = false;
        line.positionCount = points.Length;
        line.widthMultiplier = 0.008f;
        line.numCapVertices = 0;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        if (lineMaterial == null)
        {
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit");
            lineMaterial = new Material(shader) { name = "Target Outline" };
        }
        line.sharedMaterial = lineMaterial;
        line.enabled = false;
    }

    public void Hide()
    {
        if (line != null && line.enabled) line.enabled = false;
    }

    /// <summary>Outline a box: centre, rotation and full size, in world space.</summary>
    public void ShowBox(Vector3 center, Quaternion rotation, Vector3 size, Color color)
    {
        Vector3 h = size * 0.5f + Vector3.one * 0.01f; // a touch bigger, so it sits outside the thing
        Vector3 C(float x, float y, float z) => center + rotation * new Vector3(x * h.x, y * h.y, z * h.z);
        Vector3 c0 = C(-1, -1, -1), c1 = C(1, -1, -1), c2 = C(1, -1, 1), c3 = C(-1, -1, 1);
        Vector3 c4 = C(-1, 1, -1), c5 = C(1, 1, -1), c6 = C(1, 1, 1), c7 = C(-1, 1, 1);
        // One unbroken path along all twelve edges.
        Vector3[] path = { c0, c1, c2, c3, c0, c4, c5, c1, c5, c6, c2, c6, c7, c3, c7, c4 };
        for (int i = 0; i < path.Length; i++) points[i] = path[i];
        line.SetPositions(points);
        line.startColor = line.endColor = color;
        line.enabled = true;
    }

    /// <summary>Outline everything visible in these objects, boxed along the first one's axes.</summary>
    public void ShowObjects(IList<GameObject> objects, Color color)
    {
        Transform frame = null;
        Bounds local = default;
        bool any = false;
        foreach (var go in objects)
        {
            if (go == null) continue;
            if (frame == null) frame = go.transform;
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                if (r is LineRenderer || r is ParticleSystemRenderer) continue;
                Bounds b = r.localBounds;
                Matrix4x4 m = frame.worldToLocalMatrix * r.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    Vector3 p = m.MultiplyPoint3x4(corner);
                    if (!any) { local = new Bounds(p, Vector3.zero); any = true; }
                    else local.Encapsulate(p);
                }
            }
        }
        if (!any) { Hide(); return; }
        Vector3 s = frame.lossyScale;
        ShowBox(frame.TransformPoint(local.center), frame.rotation,
                Vector3.Scale(local.size, new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z))), color);
    }

    private static readonly List<GameObject> One = new(1);

    public void ShowObject(GameObject go, Color color)
    {
        One.Clear();
        One.Add(go);
        ShowObjects(One, color);
    }
}
