using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Draws the held phone on top of the world, so it never pokes into a shelf or wall you walk up to. The
/// phone goes on its own layer ("phoneView"), the player's camera stops drawing that layer, and a small
/// overlay camera (stacked on the player's, URP) draws only the phone, after everything else. It follows
/// the player camera's field of view and clip planes. Set up by PhoneController; nothing to do but have the
/// "phoneView" layer (Tags and Layers).
/// </summary>
public class PhoneOverlay : MonoBehaviour
{
    public const string LayerName = "phoneView";

    private Camera baseCam, cam;

    /// <summary>Put the phone on the overlay (false if the layer isn't set up: then it's drawn as before).</summary>
    public static bool Setup(Camera playerCam, GameObject phone)
    {
        int layer = LayerMask.NameToLayer(LayerName);
        if (layer < 0 || playerCam == null || phone == null)
        {
            if (layer < 0) Debug.LogWarning($"[PhoneOverlay] No '{LayerName}' layer: the phone can poke into things.");
            return false;
        }
        foreach (var t in phone.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;

        var existing = playerCam.GetComponentInChildren<PhoneOverlay>(true);
        if (existing == null)
        {
            var go = new GameObject("Phone Overlay Camera");
            go.transform.SetParent(playerCam.transform, false);
            existing = go.AddComponent<PhoneOverlay>();
            existing.baseCam = playerCam;
            existing.cam = go.AddComponent<Camera>();
            existing.cam.clearFlags = CameraClearFlags.Depth;
            existing.cam.cullingMask = 1 << layer;
            existing.cam.GetUniversalAdditionalCameraData().renderType = CameraRenderType.Overlay;
            var stack = playerCam.GetUniversalAdditionalCameraData().cameraStack;
            if (!stack.Contains(existing.cam)) stack.Add(existing.cam);
        }
        playerCam.cullingMask &= ~(1 << layer);
        existing.Follow();
        // The phone's screen is clicked through the camera that draws it (UI skips layers its camera doesn't
        // draw); it's in the same place as the player's, so nothing else changes.
        foreach (var canvas in phone.GetComponentsInChildren<Canvas>(true))
            if (canvas.renderMode == RenderMode.WorldSpace) canvas.worldCamera = existing.cam;
        return true;
    }

    private void LateUpdate() => Follow();

    private void Follow()
    {
        if (baseCam == null || cam == null) return;
        cam.fieldOfView = baseCam.fieldOfView;
        cam.nearClipPlane = Mathf.Min(baseCam.nearClipPlane, 0.01f);
        cam.farClipPlane = 5f; // only the phone, right in front
        cam.allowHDR = baseCam.allowHDR;
    }

    private void OnDestroy()
    {
        if (baseCam != null && cam != null)
        {
            var data = baseCam.GetUniversalAdditionalCameraData();
            if (data != null) data.cameraStack?.Remove(cam);
        }
    }
}
