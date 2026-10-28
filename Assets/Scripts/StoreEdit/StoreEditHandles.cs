using UnityEngine;

/// <summary>
/// Draws store edit mode visuals with LineRenderers: the selection box, move arrows and
/// rotate rings. Purely visual and local; StoreEditController does the picking math using
/// the same shapes (see the static geometry helpers below).
/// </summary>
public class StoreEditHandles : MonoBehaviour
{
    public const int None = -1;
    public const int RingSegments = 48;

    public static readonly Vector3[] Axes = { Vector3.right, Vector3.up, Vector3.forward };
    private static readonly Color[] AxisColors =
    {
        new(0.95f, 0.25f, 0.25f), new(0.3f, 0.9f, 0.3f), new(0.3f, 0.5f, 1f)
    };
    private static readonly Color HighlightColor = new(1f, 0.85f, 0.1f);

    private Material lineMaterial;
    private LineRenderer box;
    private readonly LineRenderer[] arrows = new LineRenderer[3];
    private readonly LineRenderer[] rings = new LineRenderer[3];
    private readonly Vector3[] ringBuffer = new Vector3[RingSegments];

    public static StoreEditHandles Create()
    {
        var go = new GameObject("[StoreEditHandles]");
        var h = go.AddComponent<StoreEditHandles>();
        h.Build();
        return h;
    }

    private void Build()
    {
        // Sprites/Default is unlit, uses vertex color and is always included in builds.
        var shader = Shader.Find("Sprites/Default");
        lineMaterial = new Material(shader) { renderQueue = 4000 };

        box = MakeLine("Box", false);
        for (int i = 0; i < 3; i++)
        {
            arrows[i] = MakeLine("Arrow" + i, false);
            rings[i] = MakeLine("Ring" + i, true);
        }
        HideAll();
    }

    private LineRenderer MakeLine(string name, bool loop)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.sharedMaterial = lineMaterial;
        lr.useWorldSpace = true;
        lr.loop = loop;
        lr.numCapVertices = 2;
        lr.numCornerVertices = 2;
        lr.alignment = LineAlignment.View;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        return lr;
    }

    private void OnDestroy()
    {
        if (lineMaterial != null) Destroy(lineMaterial);
    }

    public void HideAll()
    {
        box.enabled = false;
        for (int i = 0; i < 3; i++) { arrows[i].enabled = false; rings[i].enabled = false; }
    }

    /* ========================= DRAWING ========================= */

    public void DrawBox(Vector3 center, Vector3 half, Quaternion rot, Color color, float width)
    {
        Vector3 P(int i) => center + rot * new Vector3(
            (i & 1) == 0 ? -half.x : half.x,
            (i & 2) == 0 ? -half.y : half.y,
            (i & 4) == 0 ? -half.z : half.z);

        // One continuous path that covers all 12 edges (a few are traced twice).
        int[] path = { 0, 1, 3, 2, 0, 4, 5, 7, 6, 4, 5, 1, 3, 7, 6, 2 };
        box.positionCount = path.Length;
        for (int i = 0; i < path.Length; i++) box.SetPosition(i, P(path[i]));
        SetStyle(box, color, width);
        box.enabled = true;
    }

    public void HideBox() => box.enabled = false;

    public void DrawArrows(Vector3 center, float length, Vector3 viewDir, int hover, int active, float width)
    {
        for (int i = 0; i < 3; i++)
        {
            Vector3 a = Axes[i];
            Vector3 tip = center + a * length;
            Vector3 side = Vector3.Cross(a, viewDir);
            if (side.sqrMagnitude < 1e-4f) side = Vector3.Cross(a, Vector3.up);
            if (side.sqrMagnitude < 1e-4f) side = Vector3.Cross(a, Vector3.right);
            side.Normalize();
            float head = length * 0.18f;

            var lr = arrows[i];
            lr.positionCount = 5;
            lr.SetPosition(0, center);
            lr.SetPosition(1, tip);
            lr.SetPosition(2, tip - a * head + side * head * 0.5f);
            lr.SetPosition(3, tip);
            lr.SetPosition(4, tip - a * head - side * head * 0.5f);

            bool lit = i == active || (active == None && i == hover);
            SetStyle(lr, lit ? HighlightColor : AxisColors[i], lit ? width * 1.6f : width);
            lr.enabled = active == None || i == active;
        }
    }

    public void HideArrows()
    {
        for (int i = 0; i < 3; i++) arrows[i].enabled = false;
    }

    public void DrawRings(Vector3 center, float radius, int hover, int active, float width)
    {
        for (int i = 0; i < 3; i++)
        {
            FillRing(center, Axes[i], radius, ringBuffer);
            var lr = rings[i];
            lr.positionCount = RingSegments;
            lr.SetPositions(ringBuffer);

            bool lit = i == active || (active == None && i == hover);
            SetStyle(lr, lit ? HighlightColor : AxisColors[i], lit ? width * 1.6f : width);
            lr.enabled = active == None || i == active;
        }
    }

    public void HideRings()
    {
        for (int i = 0; i < 3; i++) rings[i].enabled = false;
    }

    private static void SetStyle(LineRenderer lr, Color c, float width)
    {
        lr.startColor = lr.endColor = c;
        lr.widthMultiplier = width;
    }

    /* ========================= SHARED GEOMETRY ========================= */

    /// <summary>Points of a circle of the given radius around an axis (used for drawing and picking).</summary>
    public static void FillRing(Vector3 center, Vector3 axis, float radius, Vector3[] into)
    {
        Vector3 u = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
        Vector3 v = Vector3.Cross(axis, u);
        for (int k = 0; k < into.Length; k++)
        {
            float t = k * Mathf.PI * 2f / into.Length;
            into[k] = center + (u * Mathf.Cos(t) + v * Mathf.Sin(t)) * radius;
        }
    }
}
