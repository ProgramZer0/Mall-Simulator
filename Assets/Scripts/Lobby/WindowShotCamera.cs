using UnityEngine;

/// <summary>
/// Makes the pictures for the office window (OfficeMall): put it on a Camera in the game scene where
/// the office window would look from (up high, looking over the mall), then right click the component →
/// Save Window Shot. The picture's saved to Folder as File Name.png (in edit mode or Play mode), ready to
/// drag into OfficeMall's New Mall Picture. The camera's turned off afterwards so it doesn't render in the game.
/// Field of view: wide (60-80) looks most like a real window.
/// </summary>
[RequireComponent(typeof(Camera))]
public class WindowShotCamera : MonoBehaviour
{
    [SerializeField] private string folder = "Assets/Art/WindowShots";
    [SerializeField] private string fileName = "NewMall";
    [SerializeField] private Vector2Int size = new(2048, 1024);

#if UNITY_EDITOR
    [ContextMenu("Save Window Shot")]
    private void SaveShot()
    {
        var cam = GetComponent<Camera>();
        var rt = new RenderTexture(size.x, size.y, 24, RenderTextureFormat.ARGB32);
        var oldTarget = cam.targetTexture;
        var oldActive = RenderTexture.active;
        try
        {
            cam.enabled = true;
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(size.x, size.y, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, size.x, size.y), 0, 0);
            tex.Apply();
            System.IO.Directory.CreateDirectory(folder);
            string path = $"{folder}/{fileName}.png";
            System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
            DestroyImmediate(tex);
            UnityEditor.AssetDatabase.ImportAsset(path);
            Debug.Log($"[WindowShot] Saved {path}.", UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(path));
        }
        finally
        {
            cam.targetTexture = oldTarget;
            RenderTexture.active = oldActive;
            cam.enabled = false;
            rt.Release();
            DestroyImmediate(rt);
        }
    }
#endif
}
