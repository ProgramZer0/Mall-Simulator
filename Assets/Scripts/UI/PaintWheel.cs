using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The paint brush's color wheel (TMP UI): hold the brush key and the palette's colors appear in a
/// ring; move the mouse toward one and let go to pick it. It fills itself from the PaintPalette.
///
/// Setup (on a Canvas): Root (the wheel, centered on screen, shown while open), Swatch Prefab (an
/// Image, one per color, placed around the ring), Radius in pixels, and an optional Name Text in the
/// middle for the color pointed at. Without a PaintWheel in the scene, a simple built-in wheel is drawn.
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
            img.name = $"Swatch {PaintPalette.NameOf((byte)i)}";
            img.color = PaintPalette.TryGetColor((byte)i, out Color c) ? c : originalSwatchColor;
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
