using UnityEngine;

/// <summary>
/// A live picture of someone and what's around them (the Lost & Found app, the lost child alert): its own
/// small camera, a few meters off and above them looking down, rendering into Texture while it's on.
/// Purely local. Each shot keeps the side it looks from until that side gets blocked (so the picture
/// doesn't jump around), then picks the clearest of 8 sides.
/// </summary>
public sealed class LostChildShot : System.IDisposable
{
    public RenderTexture Texture { get; }

    private readonly Camera cam;
    private int side = -1;
    private const int Sides = 8;

    public LostChildShot(string name, int width, int height, LayerMask cullingMask, float fieldOfView)
    {
        Texture = new RenderTexture(Mathf.Max(16, width), Mathf.Max(16, height), 24) { name = name };
        var go = new GameObject(name) { hideFlags = HideFlags.DontSave };
        cam = go.AddComponent<Camera>();
        cam.targetTexture = Texture;
        cam.cullingMask = cullingMask;
        cam.fieldOfView = fieldOfView;
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 80f;
        cam.enabled = false;
    }

    /// <summary>Rendering (only while something shows it).</summary>
    public bool On
    {
        get => cam != null && cam.enabled;
        set { if (cam != null) cam.enabled = value; }
    }

    /// <summary>
    /// Point it at someone standing at Feet (Height tall): from Distance away and Above their feet, pulled in
    /// when a wall's in the way. Blockers: what counts as in the way.
    /// </summary>
    public void Aim(Vector3 feet, float height, float distance, float above, LayerMask blockers)
    {
        if (cam == null) return;
        Vector3 look = feet + Vector3.up * (height * 0.6f);
        float best = -1f;
        int bestSide = 0;
        Vector3 bestOut = Vector3.back;
        for (int k = 0; k < Sides; k++)
        {
            int i = side < 0 ? k : (side + k) % Sides; // the side it's on first
            Vector3 dir = Quaternion.Euler(0f, i * (360f / Sides), 0f) * Vector3.back * distance + Vector3.up * (above - height * 0.6f);
            float full = dir.magnitude;
            dir /= full;
            float clear = Physics.SphereCast(look, 0.2f, dir, out var hit, full, blockers, QueryTriggerInteraction.Ignore) ? hit.distance : full;
            if (clear >= full * 0.95f) { best = clear; bestSide = i; bestOut = dir; break; } // clear: take it
            if (clear > best) { best = clear; bestSide = i; bestOut = dir; }
        }
        side = bestSide;
        Vector3 at = look + bestOut * Mathf.Max(0.5f, best - 0.15f);
        cam.transform.SetPositionAndRotation(at, Quaternion.LookRotation(look - at));
    }

    public void Dispose()
    {
        if (cam != null) Object.Destroy(cam.gameObject);
        if (Texture != null) { Texture.Release(); Object.Destroy(Texture); }
    }
}

/// <summary>How a lost child picture is taken (Inspector settings for the app and the alert).</summary>
[System.Serializable]
public class LostChildShotSettings
{
    [Tooltip("Picture size, pixels.")]
    public Vector2Int size = new(384, 288);
    [Tooltip("What the picture shows (leave UI out).")]
    public LayerMask cullingMask = ~(1 << 5);
    [Tooltip("What can be in the way (walls): the camera comes in closer.")]
    public LayerMask blockers = ~(1 << 5);
    [Range(20f, 100f)] public float fieldOfView = 55f;
    [Tooltip("How far off the camera is (meters, across the floor)...")]
    [Min(0.5f)] public float distance = 4f;
    [Tooltip("...and how high above their feet.")]
    [Min(0.2f)] public float above = 3f;
    [Tooltip("How tall a child is, and an adult (meters): what the camera looks at.")]
    [Min(0.2f)] public float childHeight = 1.1f;
    [Min(0.2f)] public float adultHeight = 1.75f;

    public LostChildShot Make(string name) => new(name, size.x, size.y, cullingMask, fieldOfView);

    /// <summary>Aim the shot at a customer (false: they aren't shown here, e.g. they left).</summary>
    public bool Aim(LostChildShot shot, ushort customer)
    {
        var cm = CustomerManager.Instance;
        if (shot == null || cm == null || !cm.TryGetShown(customer, out var feet, out bool child)) return false;
        shot.Aim(feet, child ? childHeight : adultHeight, distance, above, blockers);
        return true;
    }
}
