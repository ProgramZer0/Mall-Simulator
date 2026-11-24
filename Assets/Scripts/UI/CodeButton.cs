using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

/// <summary>
/// Buttons made by code (phone apps' pages, the office computer's menu) use your button prefab instead of
/// a plain box: put a Button (with a TMP text in it for the label) at Assets/Resources/UIButton.prefab, or
/// set the phone's own on PhoneScreen → Button Prefab. Without one, they're built as before.
/// </summary>
public static class CodeButton
{
    public const string ResourcePath = "UIButton";

    /// <summary>The phone's button (PhoneScreen → Button Prefab); over the shared one for phone pages.</summary>
    public static Button PhonePrefab;

    private static Button shared;
    private static bool looked;

    /// <summary>The shared button (Resources/UIButton), or null.</summary>
    public static Button Shared
    {
        get
        {
            if (!looked) { looked = true; shared = Resources.Load<Button>(ResourcePath); }
            return shared;
        }
    }

    /// <summary>A copy of the prefab under Parent with this label and click (positioning is the caller's).</summary>
    public static Button Make(Button prefab, Transform parent, string label, UnityAction onClick)
    {
        if (prefab == null) return null;
        var b = Object.Instantiate(prefab, parent, false);
        b.name = "Button";
        b.onClick.RemoveAllListeners();
        if (onClick != null) b.onClick.AddListener(onClick);
        var text = b.GetComponentInChildren<TMP_Text>(true);
        if (text != null) text.text = label;
        return b;
    }
}
