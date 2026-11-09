using UnityEngine;

/// <summary>
/// Keeps something held in your own hand from poking through walls, shelves and other things right in
/// front of you. Added by itself to your own held item (and the price scanner); other players see it
/// normally in your hand.
///
/// How: when something is between the camera and the held model, the model is pulled toward the camera
/// and shrunk by the same amount. On screen that looks exactly the same (same size, same spot), but it
/// is now in front of the wall instead of inside it.
/// </summary>
public class HeldVisualNoClip : MonoBehaviour
{
    private const float Margin = 0.9f;   // stop a little short of what's in the way
    private const float NearPadding = 0.02f;

    private Camera cam;
    private Transform ignoreRoot;       // the player holding it
    private Vector3 restLocalPosition;
    private Vector3 restLocalScale;
    private Renderer[] renderers;

    private static readonly RaycastHit[] Hits = new RaycastHit[8];
    private static readonly Vector3[] Points = new Vector3[9];

    /// <summary>Turn it on for a held model. Call right after placing it in the hand.</summary>
    public static void Add(GameObject visual, Camera camera, Transform player)
    {
        if (visual == null || camera == null) return;
        var c = visual.GetComponent<HeldVisualNoClip>();
        if (c == null) c = visual.AddComponent<HeldVisualNoClip>();
        c.cam = camera;
        c.ignoreRoot = player;
        c.restLocalPosition = visual.transform.localPosition;
        c.restLocalScale = visual.transform.localScale;
        c.renderers = visual.GetComponentsInChildren<Renderer>(true);
    }

    private void LateUpdate()
    {
        if (cam == null) return;
        transform.localPosition = restLocalPosition;
        transform.localScale = restLocalScale;

        if (!TryGetBounds(out Bounds b)) return;
        Vector3 eye = cam.transform.position;

        // Rays from the eye to the model's middle and corners: the nearest thing in the way sets how far
        // in it has to come.
        Points[0] = b.center;
        for (int i = 0; i < 8; i++)
            Points[i + 1] = b.center + Vector3.Scale(b.extents,
                new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));

        float k = 1f, nearest = float.MaxValue;
        foreach (var p in Points)
        {
            Vector3 d = p - eye;
            float length = d.magnitude;
            if (length < 1e-4f) continue;
            nearest = Mathf.Min(nearest, length);
            int n = Physics.RaycastNonAlloc(eye, d / length, Hits, length, Physics.DefaultRaycastLayers,
                                            QueryTriggerInteraction.Ignore);
            for (int h = 0; h < n; h++)
            {
                Transform t = Hits[h].collider.transform;
                if (t.IsChildOf(transform) || (ignoreRoot != null && t.IsChildOf(ignoreRoot))) continue;
                k = Mathf.Min(k, Hits[h].distance / length * Margin);
            }
        }
        if (k >= 1f) return;

        // Never closer than the camera can draw.
        if (nearest < float.MaxValue) k = Mathf.Max(k, (cam.nearClipPlane + NearPadding) / nearest);
        k = Mathf.Clamp(k, 0.05f, 1f);

        transform.position = eye + (transform.position - eye) * k;
        transform.localScale = restLocalScale * k;
    }

    private bool TryGetBounds(out Bounds b)
    {
        b = default;
        bool any = false;
        if (renderers == null) return false;
        foreach (var r in renderers)
        {
            if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
            if (!any) { b = r.bounds; any = true; }
            else b.Encapsulate(r.bounds);
        }
        return any;
    }
}
