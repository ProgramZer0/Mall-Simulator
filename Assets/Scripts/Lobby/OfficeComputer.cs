using UnityEngine;

/// <summary>
/// A computer in the office (the lobby): F zooms the camera in on it and the lobby screen (MainMenu)
/// shows on it: host a lobby, join a friend, your name, and while hosting the join code, players and
/// closing the lobby. F, Esc or right click steps back. Set it up like a shop monitor: Screen is an
/// empty child in the middle of the screen, its blue arrow (Z) pointing out toward the player.
/// Needs a collider on it (or a child).
/// </summary>
public class OfficeComputer : MonoBehaviour, IPlayerUsable
{
    [Tooltip("An empty child in the middle of the screen, its blue arrow (Z) pointing out of the screen.")]
    [SerializeField] private Transform screen;
    [Tooltip("Width of the screen's visible area, in meters.")]
    [SerializeField] private float screenWidth = 0.5f;
    [Tooltip("How far in front of the screen the camera stops, in meters. 0 = worked out from the screen's width.")]
    [SerializeField] private float viewDistance = 0f;
    [SerializeField] private string prompt = "F: Use the computer";

    public Transform Screen => screen;
    public float ScreenWidth => screenWidth;

    public string Prompt(ulong clientId) => MainMenu.IsShowing && !MainMenu.IsOpen ? prompt : null;

    public void Use(ulong clientId) => MainMenu.OpenOn(this);

    /// <summary>Where a camera of this field of view sits to see the whole screen, looking straight at it.</summary>
    public bool TryGetViewPose(Camera cam, out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = default;
        if (screen == null) return false;
        float distance = viewDistance;
        if (distance <= 0f && cam != null)
        {
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
