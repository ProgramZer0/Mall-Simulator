using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Shop icons made from an item's own model (used by Update Item Catalog for items with no icon, and by
/// Tools > Mall Sim > Items > Remake Item Icons). The prefab is put in a hidden scene, lit, and photographed
/// from the front at an angle; the background comes out transparent (it's shot on black and on white and
/// the difference gives each pixel's see-through-ness, so it works whatever the render pipeline does with
/// alpha). Saved as a sprite in Assets/Art/ItemIcons. An icon you set yourself (from anywhere else) is
/// never replaced.
/// </summary>
public static class ItemIconMaker
{
    public const string Folder = "Assets/Art/ItemIcons";
    private const string YawKey = "MallSim.IconYaw", PitchKey = "MallSim.IconPitch", SizeKey = "MallSim.IconSize";

    /// <summary>Which way round icons are shot (degrees around; 0 = looking at the model's front, its +Z side).</summary>
    public static float Yaw { get => EditorPrefs.GetFloat(YawKey, 30f); set => EditorPrefs.SetFloat(YawKey, value); }
    /// <summary>How far above (degrees).</summary>
    public static float Pitch { get => EditorPrefs.GetFloat(PitchKey, 20f); set => EditorPrefs.SetFloat(PitchKey, value); }
    /// <summary>Icon size in pixels.</summary>
    public static int Size { get => EditorPrefs.GetInt(SizeKey, 256); set => EditorPrefs.SetInt(SizeKey, value); }

    /// <summary>Is this icon one of ours (made here), so it may be remade?</summary>
    public static bool IsMadeIcon(Sprite icon) =>
        icon != null && AssetDatabase.GetAssetPath(icon).Replace('\\', '/').StartsWith(Folder + "/");

    /// <summary>Photograph the prefab and save it as its icon sprite. Null if it has nothing to see.</summary>
    public static Sprite Make(GameObject prefab, string fileName)
    {
        int size = Mathf.Clamp(Size, 32, 1024);
        var black = Shoot(prefab, size, Color.black);
        if (black == null) return null;
        var white = Shoot(prefab, size, Color.white);
        if (white == null) { Object.DestroyImmediate(black); return null; }

        // Shot on black (B) and on white (W): alpha = 1 - (W - B); colour = B / alpha.
        var b = black.GetPixels();
        var w = white.GetPixels();
        var result = new Color[b.Length];
        bool anything = false;
        for (int i = 0; i < b.Length; i++)
        {
            float a = 1f - ((w[i].r - b[i].r) + (w[i].g - b[i].g) + (w[i].b - b[i].b)) / 3f;
            a = Mathf.Clamp01(a);
            if (a < 0.004f) { result[i] = new Color(0f, 0f, 0f, 0f); continue; }
            anything = true;
            result[i] = new Color(Mathf.Clamp01(b[i].r / a), Mathf.Clamp01(b[i].g / a), Mathf.Clamp01(b[i].b / a), a);
        }
        Object.DestroyImmediate(white);
        if (!anything) { Object.DestroyImmediate(black); return null; }
        black.SetPixels(result);
        black.Apply();
        byte[] png = black.EncodeToPNG();
        Object.DestroyImmediate(black);

        Directory.CreateDirectory(Folder);
        string path = $"{Folder}/{Safe(fileName)}.png";
        File.WriteAllBytes(path, png);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        if (AssetImporter.GetAtPath(path) is TextureImporter importer)
        {
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Sprite>(path);
    }

    private static Texture2D Shoot(GameObject prefab, int size, Color background)
    {
        var scene = EditorSceneManager.NewPreviewScene();
        RenderTexture rt = null;
        try
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            // What there is to see.
            bool any = false;
            var bounds = new Bounds();
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer) continue;
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }
            if (!any || bounds.size.sqrMagnitude < 1e-8f) return null;

            var camGo = new GameObject("Icon Camera");
            SceneManager.MoveGameObjectToScene(camGo, scene);
            var cam = camGo.AddComponent<Camera>();
            cam.scene = scene;
            cam.enabled = false;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = background;
            cam.allowHDR = false;
            cam.allowMSAA = true;
            cam.fieldOfView = 25f;
            var look = Quaternion.Euler(Pitch, 180f + Yaw, 0f); // looking at the model's front (+Z), from a little to the side and above
            float radius = bounds.extents.magnitude;
            float dist = radius / Mathf.Sin(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.02f;
            cam.transform.SetPositionAndRotation(bounds.center - look * Vector3.forward * dist, look);
            cam.nearClipPlane = Mathf.Max(0.01f, dist - radius * 1.5f);
            cam.farClipPlane = dist + radius * 1.5f;

            Light(scene, Quaternion.Euler(45f, 180f + Yaw - 40f, 0f), 1.1f);
            Light(scene, Quaternion.Euler(-20f, Yaw + 30f, 0f), 0.45f); // from behind, so the dark side isn't black

            rt = RenderTexture.GetTemporary(size, size, 24, RenderTextureFormat.ARGB32);
            rt.antiAliasing = 4;
            cam.targetTexture = rt;
            cam.Render();

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            cam.targetTexture = null;
            return tex;
        }
        finally
        {
            if (rt != null) RenderTexture.ReleaseTemporary(rt);
            EditorSceneManager.ClosePreviewScene(scene);
        }
    }

    private static void Light(Scene scene, Quaternion rot, float intensity)
    {
        var go = new GameObject("Icon Light");
        SceneManager.MoveGameObjectToScene(go, scene);
        go.transform.rotation = rot;
        var l = go.AddComponent<Light>();
        l.type = LightType.Directional;
        l.intensity = intensity;
        l.shadows = LightShadows.None;
    }

    private static string Safe(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }
}
