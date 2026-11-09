using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The paint brush's color wheel (TMP UI): hold the brush key and the palette's colors appear in a
/// ring; move the mouse toward one and let go to pick it. It fills itself from the PaintPalette.
///
/// No setup needed: without a PaintWheel in the scene, the brush makes a plain one by itself (on its
/// own overlay Canvas). Every color in the PaintPalette gets a swatch, drawn with the palette's Swatch
/// Sprite tinted to the color (round ones if it has none), so new colors show up by themselves. To style it, put a PaintWheel on your Canvas: Root (the wheel,
/// centered on screen, shown while open), Swatch Prefab (an Image, one per color, placed around the
/// ring), Radius in pixels, and an optional Name Text in the middle for the color pointed at.
/// </summary>
public class PaintWheel : MonoBehaviour
{
    public static PaintWheel Instance { get; private set; }

    [SerializeField] private RectTransform root;
    [SerializeField] private Image swatchPrefab;
    [SerializeField] private float radius = 140f;
    [Tooltip("How much bigger the color pointed at is drawn.")]
    [SerializeField] private float hoverScale = 1.35f;
    [SerializeField] private TMP_Text nameText;
    [Tooltip("How the Original swatch (no paint) is drawn.")]
    [SerializeField] private Color originalSwatchColor = new(1f, 1f, 1f, 0.35f);

    private readonly List<Image> swatches = new();
    private int builtFor = -1;

    public bool IsOpen => root != null && root.gameObject.activeSelf;

    /// <summary>The scene's wheel, or a plain one made on the spot when there's none.</summary>
    public static PaintWheel GetOrCreate()
    {
        if (Instance != null) return Instance;

        var canvasGo = new GameObject("[Paint Wheel]", typeof(Canvas), typeof(CanvasScaler));
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 500;
        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);

        var rootGo = new GameObject("Wheel", typeof(RectTransform));
        var rootRect = (RectTransform)rootGo.transform;
        rootRect.SetParent(canvasGo.transform, false);
        rootRect.anchorMin = rootRect.anchorMax = new Vector2(0.5f, 0.5f);
        rootRect.sizeDelta = Vector2.zero;

        // A soft dark disc behind the ring.
        var back = new GameObject("Back", typeof(RectTransform), typeof(Image)).GetComponent<Image>();
        back.rectTransform.SetParent(rootRect, false);
        back.rectTransform.sizeDelta = Vector2.one * 420f;
        back.sprite = Circle();
        back.color = new Color(0f, 0f, 0f, 0.45f);
        back.raycastTarget = false;

        // The swatch template (kept inactive; copies are made per color).
        var swatch = new GameObject("Swatch", typeof(RectTransform), typeof(Image)).GetComponent<Image>();
        swatch.rectTransform.SetParent(canvasGo.transform, false);
        swatch.rectTransform.sizeDelta = Vector2.one * 64f;
        swatch.sprite = Circle();
        swatch.raycastTarget = false;
        swatch.gameObject.SetActive(false);

        var label = new GameObject("Name", typeof(RectTransform), typeof(TextMeshProUGUI)).GetComponent<TextMeshProUGUI>();
        label.rectTransform.SetParent(rootRect, false);
        label.rectTransform.sizeDelta = new Vector2(220f, 50f);
        label.alignment = TextAlignmentOptions.Center;
        label.fontSize = 30f;
        label.raycastTarget = false;

        rootGo.SetActive(false);
        var wheel = canvasGo.AddComponent<PaintWheel>();
        wheel.root = rootRect;
        wheel.swatchPrefab = swatch;
        wheel.nameText = label;
        Instance = wheel;
        return wheel;
    }

    private static Sprite circle;

    // A white disc with a soft edge, made in code (so no art is needed).
    private static Sprite Circle()
    {
        if (circle != null) return circle;
        const int size = 64;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        float r = size * 0.5f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r));
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(r - d)));
            }
        tex.Apply();
        circle = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        return circle;
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[PaintWheel] More than one in the scene; using the first.", this);
            return;
        }
        Instance = this;
        if (root != null) root.gameObject.SetActive(false);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public void SetOpen(bool open)
    {
        if (root == null) return;
        if (open) Build();
        root.gameObject.SetActive(open);
    }

    /// <summary>Show which color is pointed at.</summary>
    public void Highlight(byte id)
    {
        for (int i = 0; i < swatches.Count; i++)
            if (swatches[i] != null) swatches[i].rectTransform.localScale = Vector3.one * (i == id ? hoverScale : 1f);
        if (nameText != null) nameText.text = PaintPalette.NameOf(id);
    }

    private void Build()
    {
        var palette = PaintPalette.Instance;
        int count = palette != null ? palette.Count + 1 : 1;
        if (builtFor == count) return;
        builtFor = count;
        foreach (var s in swatches) if (s != null) Destroy(s.gameObject);
        swatches.Clear();
        if (swatchPrefab == null) { Debug.LogWarning("[PaintWheel] Needs a Swatch Prefab.", this); return; }

        for (int i = 0; i < count; i++)
        {
            var img = Instantiate(swatchPrefab, root);
            img.gameObject.SetActive(true);
            img.name = $"Swatch {PaintPalette.NameOf((byte)i)}";
            PaintPalette.StyleSwatch(img, (byte)i, originalSwatchColor); // the palette's icon, tinted
            Vector2 dir = Direction(i, count);
            img.rectTransform.anchoredPosition = dir * radius;
            swatches.Add(img);
        }
    }

    /// <summary>Where swatch i of count sits on the ring (0 at the top, going clockwise).</summary>
    public static Vector2 Direction(int i, int count)
    {
        float a = Mathf.PI * 0.5f - i * Mathf.PI * 2f / Mathf.Max(1, count);
        return new Vector2(Mathf.Cos(a), Mathf.Sin(a));
    }

    /// <summary>Which swatch a direction from the middle points at.</summary>
    public static int Pick(Vector2 fromCenter, int count)
    {
        if (count <= 1) return 0;
        float a = Mathf.Atan2(fromCenter.y, fromCenter.x);
        float clockwiseFromTop = Mathf.Repeat(Mathf.PI * 0.5f - a, Mathf.PI * 2f);
        return Mathf.RoundToInt(clockwiseFromTop / (Mathf.PI * 2f / count)) % count;
    }
}
