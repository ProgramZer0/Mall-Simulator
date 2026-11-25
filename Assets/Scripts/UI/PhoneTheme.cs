using System;
using UnityEngine;

/// <summary>
/// The phone's look the player picked in the Customize app (PhoneStyleApp): colours for the text,
/// buttons, accents and background, and a background picture. Everything the phone apps build with
/// PhoneUI uses it (over each app's own Style colours); PhoneScreen shows the background. Purely local.
/// </summary>
public static class PhoneTheme
{
    [Serializable]
    public class Colors
    {
        public string name = "Theme";
        public Color background = new(0.08f, 0.09f, 0.12f);
        public Color text = new(0.92f, 0.94f, 0.97f);
        public Color dimText = new(0.65f, 0.7f, 0.78f);
        public Color button = new(0.2f, 0.24f, 0.32f);
        public Color accent = new(0.3f, 0.6f, 1f);
        public Color track = new(0.18f, 0.2f, 0.26f);
        [Tooltip("Outline every phone text in Outline Color (always, with this theme). Texts get one anyway over a " +
                 "background picture, so they read on any picture.")]
        public bool outline;
        public Color outlineColor = new(0f, 0f, 0f, 0.75f);
    }

    /// <summary>Outline phone texts? (A background picture is up, or the theme asks for it.)</summary>
    public static bool Outline => Wallpaper != null || (Current != null && Current.outline);

    /// <summary>The outline color: the theme's, else dark (or light, for dark text).</summary>
    public static Color OutlineColorFor(Color text) =>
        Current != null && Current.outline ? Current.outlineColor
        : (text.r * 0.3f + text.g * 0.59f + text.b * 0.11f) > 0.5f ? new Color(0f, 0f, 0f, 0.75f) : new Color(1f, 1f, 1f, 0.75f);

    /// <summary>Give a phone text the outline (or take it off) as the look says.</summary>
    public static void ApplyOutline(TMPro.TMP_Text t)
    {
        if (t == null) return;
        bool on = Outline;
        if (!on && t.outlineWidth <= 0f) return; // (don't make a material copy for nothing)
        // Only when it changes: setting them redoes the text's material (a flicker, every pass).
        float width = on ? 0.18f : 0f;
        if (!Mathf.Approximately(t.outlineWidth, width)) t.outlineWidth = width;
        if (on)
        {
            Color32 want = OutlineColorFor(t.color);
            Color32 has = t.outlineColor;
            if (want.r != has.r || want.g != has.g || want.b != has.b || want.a != has.a) t.outlineColor = want;
        }
    }

    /// <summary>The picked colours (null: each app's own, as set up).</summary>
    public static Colors Current { get; private set; }
    /// <summary>The picked background picture (null: none).</summary>
    public static Sprite Wallpaper { get; private set; }

    /// <summary>What the picture is shown multiplied by (Auto dims a busy one behind light text).</summary>
    public static Color WallpaperTint { get; private set; } = Color.white;

    /// <summary>The look changed (PhoneScreen refreshes its background; apps use it next time they're built).</summary>
    public static event Action Changed;

    public static void Set(Colors colors, Sprite wallpaper, Color? wallpaperTint = null)
    {
        Current = colors;
        Wallpaper = wallpaper;
        WallpaperTint = wallpaperTint ?? Color.white;
        Changed?.Invoke();
    }

    public static Color TextOf(PhoneUI.Style s) => Current != null ? Current.text : s.text;
    public static Color DimOf(PhoneUI.Style s) => Current != null ? Current.dimText : s.dimText;
    public static Color ButtonOf(PhoneUI.Style s) => Current != null ? Current.button : s.button;
    public static Color AccentOf(PhoneUI.Style s) => Current != null ? Current.accent : s.accent;
    public static Color TrackOf(PhoneUI.Style s) => Current != null ? Current.track : s.track;
}
