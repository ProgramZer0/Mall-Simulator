using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Tools > Mall Sim > UI > Create Tooltip Prefab: the shared tooltip (see TooltipView), ready to restyle.</summary>
public static class TooltipPrefabTool
{
    private const string Path = "Assets/Resources/" + TooltipView.ResourcePath + ".prefab";

    [MenuItem("Tools/Mall Sim/UI/Create Tooltip Prefab")]
    private static void Create()
    {
        if (File.Exists(Path) && !EditorUtility.DisplayDialog("Tooltip prefab",
                $"{Path} is already there. Replace it with a fresh one?", "Replace", "Keep it"))
        {
            Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(Path);
            return;
        }

        var root = new GameObject("Tooltip", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup),
                                  typeof(ContentSizeFitter), typeof(CanvasGroup), typeof(TooltipView));
        var rt = (RectTransform)root.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = new Vector2(280f, 80f);
        var bg = root.GetComponent<Image>();
        bg.color = new Color(0.08f, 0.09f, 0.12f, 0.95f);
        bg.raycastTarget = false;
        var layout = root.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(12, 12, 10, 10);
        layout.spacing = 4f;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        var fit = root.GetComponent<ContentSizeFitter>();
        fit.horizontalFit = ContentSizeFitter.FitMode.Unconstrained; // a set width; the text wraps to it
        fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var group = root.GetComponent<CanvasGroup>();
        group.blocksRaycasts = false;
        group.interactable = false;

        var view = root.GetComponent<TooltipView>();
        view.nameText = Text(root.transform, "Name", 20f, FontStyles.Bold, Color.white);
        view.descriptionText = Text(root.transform, "Description", 16f, FontStyles.Normal, new Color(0.82f, 0.85f, 0.9f));

        Directory.CreateDirectory("Assets/Resources");
        var prefab = PrefabUtility.SaveAsPrefabAsset(root, Path);
        Object.DestroyImmediate(root);
        Selection.activeObject = prefab;
        EditorGUIUtility.PingObject(prefab);
        Debug.Log($"[Tooltip] Made {Path}: screens with no Tooltip Root of their own use it. Restyle it as you like " +
                  "(keep TooltipView's Name Text and Description Text set).");
    }

    private static TMP_Text Text(Transform parent, string name, float size, FontStyles style, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        var t = go.GetComponent<TextMeshProUGUI>();
        t.text = name;
        t.fontSize = size;
        t.fontStyle = style;
        t.color = color;
        t.enableWordWrapping = true;
        t.raycastTarget = false;
        return t;
    }
}
