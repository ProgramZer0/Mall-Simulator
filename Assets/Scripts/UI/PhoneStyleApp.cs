using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The phone's Customize app: pick the phone's colours (Themes) and background picture (Wallpapers).
/// Saved on this machine and used straight away: the background at once, the apps' colours each
/// time an app is opened (see PhoneTheme).
///
/// Setup: like the Help app (duplicate that app object under the phone prefab's screen and swap HelpApp
/// for this). Fill Themes (the first can be "Default": tick Use App Colors to keep each app's own) and
/// Wallpapers (any sprites). PhoneScreen shows the background (its Wallpaper Image).
/// </summary>
public class PhoneStyleApp : MonoBehaviour, IPhoneApp
{
    [System.Serializable]
    public class Theme
    {
        public string name = "Default";
        [Tooltip("Keep each app's own colours (its Style) and the phone's own background.")]
        public bool useAppColors;
        public PhoneTheme.Colors colors = new();
    }

    [System.Serializable]
    public class Wallpaper
    {
        public string name = "Wallpaper";
        public Sprite sprite;
    }

    [SerializeField] private string appName = "Customize";
    [SerializeField] private Sprite icon;
    [SerializeField] private PhoneUI.Style style = new();
    [Tooltip("Colour themes to pick from.")]
    [SerializeField] private List<Theme> themes = new()
    {
        new Theme { name = "Default", useAppColors = true },
        new Theme { name = "Light", colors = new PhoneTheme.Colors {
            background = new Color(0.93f, 0.94f, 0.96f), text = new Color(0.1f, 0.12f, 0.16f), dimText = new Color(0.38f, 0.42f, 0.5f),
            button = new Color(0.82f, 0.85f, 0.9f), accent = new Color(0.15f, 0.45f, 0.95f), track = new Color(0.75f, 0.78f, 0.84f) } },
        new Theme { name = "Mint", colors = new PhoneTheme.Colors {
            background = new Color(0.06f, 0.13f, 0.11f), text = new Color(0.9f, 0.98f, 0.95f), dimText = new Color(0.6f, 0.78f, 0.72f),
            button = new Color(0.13f, 0.27f, 0.23f), accent = new Color(0.25f, 0.85f, 0.6f), track = new Color(0.1f, 0.2f, 0.17f) } },
        new Theme { name = "Sunset", colors = new PhoneTheme.Colors {
            background = new Color(0.16f, 0.07f, 0.1f), text = new Color(1f, 0.94f, 0.9f), dimText = new Color(0.85f, 0.68f, 0.65f),
            button = new Color(0.33f, 0.14f, 0.18f), accent = new Color(1f, 0.55f, 0.3f), track = new Color(0.25f, 0.1f, 0.13f) } },
        new Theme { name = "Midnight", colors = new PhoneTheme.Colors {
            background = new Color(0.02f, 0.02f, 0.05f), text = new Color(0.85f, 0.86f, 1f), dimText = new Color(0.55f, 0.56f, 0.75f),
            button = new Color(0.1f, 0.1f, 0.2f), accent = new Color(0.6f, 0.45f, 1f), track = new Color(0.08f, 0.08f, 0.15f) } },
    };
    [Tooltip("Background pictures to pick from (\"None\" is always there too).")]
    [SerializeField] private List<Wallpaper> wallpapers = new();

    private const string ThemeKey = "Phone.Theme", WallpaperKey = "Phone.Wallpaper";

    public string AppName => appName;
    public Sprite Icon => icon;
    public int Badge => 0;

    private RectTransform content;

    /// <summary>The saved look, applied (PhoneScreen calls this when the phone is made).</summary>
    public void ApplySaved()
    {
        int t = PlayerPrefs.GetInt(ThemeKey, 0), w = PlayerPrefs.GetInt(WallpaperKey, -1);
        var theme = t >= 0 && t < themes.Count ? themes[t] : null;
        var paper = w >= 0 && w < wallpapers.Count ? wallpapers[w].sprite : null;
        PhoneTheme.Set(theme == null || theme.useAppColors ? null : theme.colors, paper);
    }

    public void OnAppOpened()
    {
        if (content == null) content = PhoneUI.ScrollPage((RectTransform)transform);
        Build();
    }

    public void OnAppClosed() { }

    private void Build()
    {
        PhoneUI.Clear(content);
        float y = style.padding;
        int theme = PlayerPrefs.GetInt(ThemeKey, 0), paper = PlayerPrefs.GetInt(WallpaperKey, -1);

        y = Header("Colors", y);
        for (int i = 0; i < themes.Count; i++)
        {
            int index = i;
            var b = PhoneUI.Button(content, (i == theme ? "> " : "") + themes[i].name, y, style.rowHeight, style, () => Pick(ThemeKey, index));
            if (!themes[i].useAppColors) Swatches(b, themes[i].colors);
            y += style.rowHeight + style.spacing;
        }

        y = Header("Background", y);
        PhoneUI.Button(content, (paper < 0 ? "> " : "") + "None", y, style.rowHeight, style, () => Pick(WallpaperKey, -1));
        y += style.rowHeight + style.spacing;
        for (int i = 0; i < wallpapers.Count; i++)
        {
            int index = i;
            float h = style.rowHeight * 2f;
            var b = PhoneUI.Button(content, (i == paper ? "> " : "") + wallpapers[i].name, y, h, style, () => Pick(WallpaperKey, index));
            if (wallpapers[i].sprite != null) Thumbnail(b, wallpapers[i].sprite, h);
            y += h + style.spacing;
        }
        if (wallpapers.Count == 0)
        {
            PhoneUI.Text(content, "No pictures set up yet.", y, style.rowHeight, style, true);
            y += style.rowHeight + style.spacing;
        }
        content.sizeDelta = new Vector2(content.sizeDelta.x, y + style.padding);
    }

    private void Pick(string key, int value)
    {
        PlayerPrefs.SetInt(key, value);
        PlayerPrefs.Save();
        ApplySaved();
        Build(); // this page in the new colours
    }

    private float Header(string text, float y)
    {
        if (y > style.padding) y += style.spacing;
        var t = PhoneUI.Text(content, $"<b>{text}</b>", y, style.rowHeight * 0.8f, style, true, TextAlignmentOptions.BottomLeft);
        t.color = PhoneTheme.AccentOf(style);
        return y + style.rowHeight * 0.8f + style.spacing * 0.5f;
    }

    // Little squares of the theme's colours on the right of its button.
    private void Swatches(Button b, PhoneTheme.Colors c)
    {
        var colors = new[] { c.background, c.button, c.accent, c.text };
        float size = style.rowHeight * 0.55f;
        for (int i = 0; i < colors.Length; i++)
        {
            var go = new GameObject("Swatch", typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(b.transform, false);
            rt.anchorMin = rt.anchorMax = new Vector2(1f, 0.5f);
            rt.pivot = new Vector2(1f, 0.5f);
            rt.sizeDelta = new Vector2(size, size);
            rt.anchoredPosition = new Vector2(-6f - i * (size + 3f), 0f);
            var img = go.GetComponent<Image>();
            img.color = colors[colors.Length - 1 - i];
            img.raycastTarget = false;
        }
    }

    // The picture, small, on the right of its button.
    private static void Thumbnail(Button b, Sprite sprite, float height)
    {
        var go = new GameObject("Thumbnail", typeof(RectTransform), typeof(Image));
        var rt = (RectTransform)go.transform;
        rt.SetParent(b.transform, false);
        rt.anchorMin = rt.anchorMax = new Vector2(1f, 0.5f);
        rt.pivot = new Vector2(1f, 0.5f);
        float h = height - 8f;
        float aspect = sprite.rect.height > 0f ? sprite.rect.width / sprite.rect.height : 1f;
        rt.sizeDelta = new Vector2(h * aspect, h);
        rt.anchoredPosition = new Vector2(-4f, 0f);
        var img = go.GetComponent<Image>();
        img.sprite = sprite;
        img.preserveAspect = true;
        img.raycastTarget = false;
    }
}
