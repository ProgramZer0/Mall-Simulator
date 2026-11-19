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
///
/// Each color can unlock at a team level (hidden from pickers until then), and can be Exclusive: only on items (or franchises) that list
/// it. Which colors an item may come in, and how its colors go down with customers: see ColorRules.
/// </summary>
public class PaintPalette : MonoBehaviour
{
    public static PaintPalette Instance { get; private set; }

    [Serializable]
    public struct Swatch
    {
        public string name;
        public Color color;
        [Tooltip("Team level it can be used from (0 or 1: from the start). Locked colors show dimmed.")]
        [Min(0)] public int unlockLevel;
        [Tooltip("Only on items or franchises that list it in their Colors (a special finish): never offered for " +
                 "anything else, walls and floors included.")]
        public bool exclusive;
    }

    /// <summary>Colors a category of items is limited to (an item's own Colors list comes first).</summary>
    [Serializable]
    public class CategoryColors
    {
        public string category;
        [Tooltip("The colors these items may come in (Original always can). Exclusive colors listed here can be used.")]
        [PaletteColor] public List<string> colors = new();
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

    [Header("Limits")]
    [Tooltip("Categories limited to some colors (\"Appliances: White, Black, Silver\"). An item in several takes " +
             "the colors of all of them. An item's own Colors, or its franchise's, come first.")]
    [SerializeField] private List<CategoryColors> categoryColors = new();
    public IReadOnlyList<CategoryColors> CategoryLimits => categoryColors;

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

    /// <summary>Team level a color can be used from (0 for Original or an unknown id).</summary>
    public static int UnlockLevelOf(byte id) =>
        id != 0 && Instance != null && id <= Instance.Count ? Instance.colors[id - 1].unlockLevel : 0;

    /// <summary>
    /// A color's name for pickers: "Gold (exclusive)" for an exclusive one ("Green (level 5)" if a locked one
    /// is ever shown).
    /// </summary>
    public static string PickerName(byte id)
    {
        string n = NameOf(id);
        if (id == 0) return n;
        if (!ColorRules.IsUnlocked(id, LocalTeam)) return $"{n} (level {UnlockLevelOf(id)})";
        return IsExclusive(id) ? $"{n} (exclusive)" : n;
    }

    /// <summary>
    /// Not shown in the local player's pickers: locked for their team (Unlock Level), or an exclusive color
    /// that nothing their team can order comes in (level, licenses, franchises).
    /// </summary>
    public static bool IsLockedHere(byte id)
    {
        ulong team = LocalTeam;
        return !ColorRules.IsUnlocked(id, team) || !ColorRules.ExclusiveInUse(id, team);
    }

    private static ulong LocalTeam =>
        Unity.Netcode.NetworkManager.Singleton != null ? MallManager.GetTeamOf(Unity.Netcode.NetworkManager.Singleton.LocalClientId) : 0;

    /// <summary>Only on items or franchises that list it?</summary>
    public static bool IsExclusive(byte id) =>
        id != 0 && Instance != null && id <= Instance.Count && Instance.colors[id - 1].exclusive;

    private Dictionary<string, byte> byName;

    /// <summary>A color's id from its name (not case sensitive; "Original" is 0). False if there's none.</summary>
    public static bool TryGetId(string name, out byte id)
    {
        id = 0;
        var p = Instance;
        if (p == null || string.IsNullOrWhiteSpace(name)) return false;
        if (p.byName == null)
        {
            p.byName = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < p.Count; i++)
                if (!string.IsNullOrWhiteSpace(p.colors[i].name)) p.byName.TryAdd(p.colors[i].name.Trim(), (byte)(i + 1));
        }
        if (string.Equals(name.Trim(), p.originalName, StringComparison.OrdinalIgnoreCase)) return true;
        return p.byName.TryGetValue(name.Trim(), out id);
    }

    /// <summary>Every color's name, Original first (for editor drop-downs).</summary>
    public IEnumerable<string> Names()
    {
        yield return originalName;
        for (int i = 0; i < Count; i++) yield return colors[i].name;
    }

#if UNITY_EDITOR
    private void OnValidate() { byName = null; ColorRules.Forget(); }
#endif

    public static string NameOf(byte id)
    {
        if (Instance == null) return id == 0 ? "Original" : $"Color {id}";
        if (id == 0) return Instance.originalName;
        return id <= Instance.Count ? Instance.colors[id - 1].name : $"Color {id}";
    }

    /* ========================= APPLYING ========================= */

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
    private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
    private static MaterialPropertyBlock block;

    /// <summary>
    /// Paint one renderer (Material Index -1: all its materials) with a color id; 0 takes the paint off.
    /// Uses a property block, so materials aren't copied.
    /// </summary>
    public static void Apply(Renderer renderer, int materialIndex, byte id) => Apply(renderer, materialIndex, id, false);

    /// <summary>
    /// Cover Texture: painted, the material's texture is hidden too (solid paint over wood, a print...), so
    /// the color shows as it is (white paint really is white). Normal maps and the like still show.
    /// </summary>
    public static void Apply(Renderer renderer, int materialIndex, byte id, bool coverTexture)
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
            if (coverTexture)
            {
                block.SetTexture(BaseMapId, Texture2D.whiteTexture);
                block.SetTexture(MainTexId, Texture2D.whiteTexture);
            }
            renderer.SetPropertyBlock(block, i);
        }
    }
}
