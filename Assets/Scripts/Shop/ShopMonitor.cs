using UnityEngine;

/// <summary>
/// Marks the shop monitor: look at it and press F to use the shop for the store it stands in. Only
/// that store's team can use it (the host also refuses orders from anyone else).
///
/// With a Screen set (and the shop on its own Canvas, see ShopUIController), the camera zooms in on
/// the monitor and the shop shows on its screen; F, Esc or right click steps back. Without one, the
/// shop opens over the whole view as before.
///
/// Prefab setup: NetworkObject + ItemPickupNet (with Edit Mode Only ticked, so it can only be
/// moved in store edit mode) + a collider on the pickup layer + this. Give it an ItemCatalog
/// entry (World Network Prefab = the monitor) so edit mode can move it, and untick Show In Shop
/// if it shouldn't be for sale.
/// </summary>
public class ShopMonitor : MonoBehaviour
{
    [Tooltip("An empty child in the middle of the screen, its blue arrow (Z) pointing out of the screen toward " +
             "whoever's looking at it. The shop is drawn here.")]
    [SerializeField] private Transform screen;
    [Tooltip("Width of the screen's visible area, in meters. The shop is scaled to fit it.")]
    [SerializeField] private float screenWidth = 0.5f;
    [Tooltip("How far in front of the screen the camera stops, in meters. 0 = worked out from the screen's width.")]
    [SerializeField] private float viewDistance = 0f;

    public Transform Screen => screen;
    public float ScreenWidth => screenWidth;

    /// <summary>The store this monitor is standing in (null in a hallway).</summary>
    public StorePlot GetPlot() =>
        MallManager.Instance != null ? MallManager.Instance.GetPlotAt(transform.position) : null;

    /// <summary>Can this player use it? (It must be in a store their team owns.)</summary>
    public bool CanUse(ulong clientId)
    {
        var plot = GetPlot();
        return plot != null && plot.IsAuthorized(clientId);
    }

    /// <summary>Where a camera of this field of view sits to see the whole screen, looking straight at it.</summary>
    public bool TryGetViewPose(Camera cam, out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = default;
        if (screen == null) return false;
        float distance = viewDistance;
        if (distance <= 0f && cam != null)
        {
            // Far enough that the screen's width fills about 85% of the view.
            float halfH = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float halfW = halfH * Mathf.Max(0.1f, cam.aspect);
            distance = screenWidth * 0.5f / (halfW * 0.85f);
        }
        position = screen.position + screen.forward * Mathf.Max(0.05f, distance);
        rotation = Quaternion.LookRotation(-screen.forward, screen.up);
        return true;
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (screen == null) return;
        Gizmos.color = Color.cyan;
        Gizmos.matrix = Matrix4x4.TRS(screen.position, screen.rotation, Vector3.one);
        Gizmos.DrawWireCube(Vector3.zero, new Vector3(screenWidth, screenWidth * 9f / 16f, 0.001f));
        Gizmos.DrawLine(Vector3.zero, Vector3.forward * 0.2f);
    }
#endif
}
