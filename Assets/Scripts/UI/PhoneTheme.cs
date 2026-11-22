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
    }

    /// <summary>The picked colours (null: each app's own, as set up).</summary>
    public static Colors Current { get; private set; }
    /// <summary>The picked background picture (null: none).</summary>
    public static Sprite Wallpaper { get; private set; }

    /// <summary>The look changed (PhoneScreen refreshes its background; apps use it next time they're built).</summary>
    public static event Action Changed;

    public static void Set(Colors colors, Sprite wallpaper)
    {
        Current = colors;
        Wallpaper = wallpaper;
        Changed?.Invoke();
    }

    public static Color TextOf(PhoneUI.Style s) => Current != null ? Current.text : s.text;
    public static Color DimOf(PhoneUI.Style s) => Current != null ? Current.dimText : s.dimText;
    public static Color ButtonOf(PhoneUI.Style s) => Current != null ? Current.button : s.button;
    public static Color AccentOf(PhoneUI.Style s) => Current != null ? Current.accent : s.accent;
    public static Color TrackOf(PhoneUI.Style s) => Current != null ? Current.track : s.track;
}
