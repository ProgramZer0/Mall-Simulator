using UnityEngine;

/// <summary>
/// When no camera is drawing to the screen (between scenes, while your player is being swapped for a new
/// one...), a black screen instead of Unity's "No cameras rendering". Made by itself when the game starts:
/// nothing to set up. It's off whenever any other camera draws to the screen.
/// </summary>
public class NoCameraFallback : MonoBehaviour
{
    private Camera cam;
    private static readonly Camera[] All = new Camera[32];

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Create()
    {
        var go = new GameObject("No Camera Fallback (black screen)");
        go.hideFlags = HideFlags.DontSave;
        DontDestroyOnLoad(go);
        go.AddComponent<NoCameraFallback>();
    }

    private void Awake()
    {
        cam = gameObject.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Color.black;
        cam.cullingMask = 0; // draws nothing, just black
        cam.depth = -100f;
        cam.useOcclusionCulling = false;
        cam.allowHDR = false;
        cam.allowMSAA = false;
        cam.enabled = false;
    }

    // After everything else has had its say this frame about which cameras are on.
    private void LateUpdate()
    {
        bool other = false;
        int n = Camera.GetAllCameras(All); // only the enabled ones
        for (int i = 0; i < n && i < All.Length; i++)
        {
            var c = All[i];
            if (c != null && c != cam && c.targetTexture == null && c.gameObject.activeInHierarchy) { other = true; break; }
        }
        System.Array.Clear(All, 0, All.Length);
        if (cam.enabled == other) cam.enabled = !other;
    }
}
