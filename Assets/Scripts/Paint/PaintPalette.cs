using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The colors players can paint things with: items (their marked parts, see PaintParts), built walls,
/// the storefront and the store's floor. Put one in the scene (the MallManager object is a good spot).
///
/// Colors are picked in the buy menu (free, with the purchase) and with the paint brush (2), where
/// each paint job costs the fees below, paid straight away. Each color is synced as its place in the list, so
/// add new colors at the end and don't reorder them once stores use them. 0 always means "Original":
/// the model's own colors.
///
/// Paint replaces the material's base color (URP "_BaseColor", Built-in "_Color") and the texture
/// still shows through, so paintable materials look truest with a white base color.
/// </summary>
public class PaintPalette : MonoBehaviour
{
    public static PaintPalette Instance { get; private set; }

    [Serializable]
    public struct Swatch
    {
        public string name;
        public Color color;
    }

    [Tooltip("Up to 255 colors. Synced by their place in the list: add new ones at the end.")]
    [SerializeField] private List<Swatch> colors = new()
    {
        new Swatch { name = "White", color = new Color(0.95f, 0.95f, 0.93f) },
        new Swatch { name = "Charcoal", color = new Color(0.2f, 0.2f, 0.22f) },
        new Swatch { name = "Red", color = new Color(0.75f, 0.18f, 0.16f) },
        new Swatch { name = "Orange", color = new Color(0.93f, 0.5f, 0.15f) },
        new Swatch { name = "Yellow", color = new Color(0.95f, 0.82f, 0.25f) },
        new Swatch { name = "Green", color = new Color(0.25f, 0.6f, 0.3f) },
        new Swatch { name = "Teal", color = new Color(0.15f, 0.55f, 0.55f) },
        new Swatch { name = "Blue", color = new Color(0.2f, 0.4f, 0.8f) },
        new Swatch { name = "Purple", color = new Color(0.5f, 0.3f, 0.7f) },
        new Swatch { name = "Pink", color = new Color(0.93f, 0.55f, 0.7f) },
        new Swatch { name = "Brown", color = new Color(0.45f, 0.3f, 0.2f) },
        new Swatch { name = "Beige", color = new Color(0.86f, 0.8f, 0.66f) },
    };
    [Tooltip("Name shown for color 0 (the model's own colors).")]
    [SerializeField] private string originalName = "Original";

    [Header("Swatch icons (color wheel and buy menu)")]
    [Tooltip("A white icon, tinted to each color: one swatch is made from it for every color in the list, so adding " +
             "a color adds a swatch. Empty = plain circles (wheel) / the swatch prefab's own image (buy menu).")]
    [SerializeField] private Sprite swatchSprite;
    [Tooltip("Icon for Original (no paint), drawn untinted. Empty = the swatch icon in a see-through white.")]
    [SerializeField] private Sprite originalSprite;
    [Tooltip("Turn each swatch icon by up to this many degrees either way, so they don't all look the same. Each " +
             "color always gets the same turn. 0 = no turning.")]
    [Range(0f, 180f)] [SerializeField] private float swatchTurn = 180f;

    [Header("Paint brush fees (painting at purchase is free)")]
    [Tooltip("Per item painted.")]
    [Min(0)] [SerializeField] private int itemFee = 10;
    [Tooltip("Per meter of built wall painted (rounded up).")]
    [Min(0)] [SerializeField] private int wallFeePerMeter = 5;
    [Tooltip("Painting the whole storefront.")]
    [Min(0)] [SerializeField] private int frontFee = 100;
    [Tooltip("Painting the store's floor.")]
    [Min(0)] [SerializeField] private int floorFee = 150;

    public int ItemFee => itemFee;
    public int FrontFee => frontFee;
    public int FloorFee => floorFee;
    public int WallFee(float meters) => Mathf.CeilToInt(Mathf.Max(0f, meters)) * wallFeePerMeter;

    /// <summary>Number of colors (ids 1..Count; 0 is Original).</summary>
    public int Count => Mathf.Min(colors.Count, 255);

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[PaintPalette] More than one in the scene; using the first.", this);
            return;
        }
        Instance = this;
        if (colors.Count > 255) Debug.LogWarning("[PaintPalette] Only the first 255 colors can be used.", this);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>
    /// Set up a swatch image for a color: the palette's icon tinted to the color (Original: its own icon,
    /// untinted, or the tinted icon see-through). Without icons it just gets the color.
    /// </summary>
    public static void StyleSwatch(UnityEngine.UI.Image image, byte id, Color originalColor, bool turn = true)
    {
        if (image == null) return;
        var p = Instance;
        // A random-looking turn that's the same for this color every time (and on every machine).
        if (turn && p != null && p.swatchTurn > 0f)
        {
            float t = Mathf.Repeat((id + 1) * 0.6180339f, 1f) * 2f - 1f;
            image.rectTransform.localRotation = Quaternion.Euler(0f, 0f, t * p.swatchTurn);
        }
        if (id == 0 && p != null && p.originalSprite != null)
        {
            image.sprite = p.originalSprite;
            image.color = Color.white;
            return;
        }
        if (p != null && p.swatchSprite != null) image.sprite = p.swatchSprite;
        image.color = TryGetColor(id, out Color c) ? c : originalColor;
    }

    /// <summary>A valid color id: 0 (Original) or one in the list.</summary>
    public static bool IsValid(byte id) => id == 0 || (Instance != null && id <= Instance.Count);

    public static bool TryGetColor(byte id, out Color color)
    {
        color = Color.white;
        if (id == 0 || Instance == null || id > Instance.Count) return false;
        color = Instance.colors[id - 1].color;
        return true;
    }

    public static string NameOf(byte id)
    {
        if (Instance == null) return id == 0 ? "Original" : $"Color {id}";
        if (id == 0) return Instance.originalName;
        return id <= Instance.Count ? Instance.colors[id - 1].name : $"Color {id}";
    }

    /* ========================= APPLYING ========================= */

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static MaterialPropertyBlock block;

    /// <summary>
    /// Paint one renderer (Material Index -1: all its materials) with a color id; 0 takes the paint off.
    /// Uses a property block, so materials aren't copied.
    /// </summary>
    public static void Apply(Renderer renderer, int materialIndex, byte id)
    {
        if (renderer == null) return;
        int count = renderer.sharedMaterials.Length;
        block ??= new MaterialPropertyBlock();
        if (!TryGetColor(id, out Color c))
        {
            // An empty block takes the paint off.
            block.Clear();
            for (int i = 0; i < count; i++)
                if (materialIndex < 0 || i == materialIndex) renderer.SetPropertyBlock(block, i);
            return;
        }

        for (int i = 0; i < count; i++)
        {
            if (materialIndex >= 0 && i != materialIndex) continue;
            renderer.GetPropertyBlock(block, i);
            block.SetColor(BaseColorId, c);
            block.SetColor(ColorId, c);
            renderer.SetPropertyBlock(block, i);
        }
    }
}
