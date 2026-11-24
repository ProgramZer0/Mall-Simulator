using System;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

/// <summary>
/// Bits of phone UI made by code (for apps that build their own screens, like Settings and Help):
/// a scrolling page, text, buttons and sliders, placed by position (no layout groups). Colours and
/// sizes come from a Style, so an app can be restyled from the Inspector.
/// </summary>
public static class PhoneUI
{
    [Serializable]
    public class Style
    {
        public float rowHeight = 30f;
        public float spacing = 6f;
        public float padding = 8f;
        public float fontSize = 16f;
        public float smallFontSize = 13f;
        public Color text = new(0.92f, 0.94f, 0.97f);
        public Color dimText = new(0.65f, 0.7f, 0.78f);
        public Color button = new(0.2f, 0.24f, 0.32f);
        public Color accent = new(0.3f, 0.6f, 1f);
        public Color track = new(0.18f, 0.2f, 0.26f);
        public TMP_FontAsset font;
    }

    /// <summary>A scrolling page filling Parent; returns its Content (grow its height as rows are added).</summary>
    public static RectTransform ScrollPage(RectTransform parent)
    {
        var view = Rect("Scroll", parent);
        Stretch(view);
        var viewport = Rect("Viewport", view);
        Stretch(viewport);
        viewport.gameObject.AddComponent<RectMask2D>();
        var bg = viewport.gameObject.AddComponent<Image>(); // something to catch the scroll wheel
        bg.color = new Color(0f, 0f, 0f, 0f);
        var content = Rect("Content", viewport);
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        content.anchoredPosition = Vector2.zero;
        content.sizeDelta = new Vector2(0f, 0f);
        var scroll = view.gameObject.AddComponent<ScrollRect>();
        scroll.viewport = viewport;
        scroll.content = content;
        scroll.horizontal = false;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 25f;
        return content;
    }

    public static TMP_Text Text(RectTransform parent, string text, float y, float height, Style s, bool small = false,
                                TextAlignmentOptions align = TextAlignmentOptions.TopLeft, float left = -1f, float right = -1f)
    {
        var rt = Row("Text", parent, y, height, s, left, right);
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        if (s.font != null) t.font = s.font;
        t.text = text;
        t.fontSize = small ? s.smallFontSize : s.fontSize;
        t.color = small ? PhoneTheme.DimOf(s) : PhoneTheme.TextOf(s);
        t.alignment = align;
        t.textWrappingMode = TextWrappingModes.Normal;
        t.raycastTarget = false;
        PhoneTheme.ApplyOutline(t);
        t.gameObject.AddComponent<PhoneThemeRole>().role = PhoneThemeRole.Role.Keep; // themed already, as built
        return t;
    }

    public static Button Button(RectTransform parent, string label, float y, float height, Style s, UnityAction onClick,
                                float left = -1f, float right = -1f)
    {
        // Your button (PhoneScreen's Button Prefab, or Resources/UIButton), fitted to the row.
        var prefab = CodeButton.PhonePrefab != null ? CodeButton.PhonePrefab : CodeButton.Shared;
        if (prefab != null)
        {
            var own = CodeButton.Make(prefab, parent, label, onClick);
            var row = Row("Button Row", parent, y, height, s, left, right); // (just for where it goes)
            var ort = (RectTransform)own.transform;
            ort.anchorMin = row.anchorMin;
            ort.anchorMax = row.anchorMax;
            ort.pivot = row.pivot;
            ort.offsetMin = row.offsetMin;
            ort.offsetMax = row.offsetMax;
            ort.localScale = Vector3.one;
            UnityEngine.Object.Destroy(row.gameObject);
            var label2 = own.GetComponentInChildren<TMP_Text>(true);
            if (label2 != null) PhoneTheme.ApplyOutline(label2);
            return own;
        }
        var rt = Row("Button", parent, y, height, s, left, right);
        var img = rt.gameObject.AddComponent<Image>();
        img.color = PhoneTheme.ButtonOf(s);
        if (!img.TryGetComponent<PhoneThemeRole>(out _)) img.gameObject.AddComponent<PhoneThemeRole>().role = PhoneThemeRole.Role.Keep;
        var b = rt.gameObject.AddComponent<Button>();
        b.targetGraphic = img;
        var colors = b.colors;
        colors.highlightedColor = new Color(1.15f, 1.15f, 1.15f);
        colors.pressedColor = new Color(0.85f, 0.85f, 0.85f);
        b.colors = colors;
        if (onClick != null) b.onClick.AddListener(onClick);
        var t = Text(rt, label, 0f, height, s, false, TextAlignmentOptions.Center, 4f, 4f);
        Stretch((RectTransform)t.transform, 4f);
        return b;
    }

    public static Slider Slider(RectTransform parent, float y, Style s, float min, float max, float value,
                                UnityAction<float> onChange, float left, float right = -1f)
    {
        var rt = Row("Slider", parent, y, s.rowHeight, s, left, right);
        var track = Rect("Track", rt);
        track.anchorMin = new Vector2(0f, 0.5f);
        track.anchorMax = new Vector2(1f, 0.5f);
        track.sizeDelta = new Vector2(0f, 6f);
        track.gameObject.AddComponent<Image>().color = PhoneTheme.TrackOf(s);
        var area = Rect("Fill Area", rt);
        area.anchorMin = new Vector2(0f, 0.5f);
        area.anchorMax = new Vector2(1f, 0.5f);
        area.sizeDelta = new Vector2(0f, 6f);
        var fill = Rect("Fill", area);
        fill.gameObject.AddComponent<Image>().color = PhoneTheme.AccentOf(s);
        fill.sizeDelta = Vector2.zero;
        var handleArea = Rect("Handle Area", rt);
        Stretch(handleArea);
        handleArea.offsetMin = new Vector2(8f, 0f);
        handleArea.offsetMax = new Vector2(-8f, 0f);
        var handle = Rect("Handle", handleArea);
        handle.sizeDelta = new Vector2(16f, 0f);
        var handleImg = handle.gameObject.AddComponent<Image>();
        handleImg.color = PhoneTheme.TextOf(s);
        handle.gameObject.AddComponent<PhoneThemeRole>().role = PhoneThemeRole.Role.Keep;
        var slider = rt.gameObject.AddComponent<Slider>();
        slider.fillRect = fill;
        slider.handleRect = handle;
        slider.targetGraphic = handleImg;
        slider.minValue = min;
        slider.maxValue = max;
        slider.SetValueWithoutNotify(value);
        if (onChange != null) slider.onValueChanged.AddListener(onChange);
        return slider;
    }

    // A rect at y (down from the top of Parent), Height tall, inset Left / Right (padding when < 0).
    private static RectTransform Row(string name, RectTransform parent, float y, float height, Style s, float left, float right)
    {
        var rt = Rect(name, parent);
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        float l = left < 0f ? s.padding : left, r = right < 0f ? s.padding : right;
        rt.offsetMin = new Vector2(l, -y - height);
        rt.offsetMax = new Vector2(-r, -y);
        return rt;
    }

    private static RectTransform Rect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = parent.gameObject.layer;
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        return rt;
    }

    private static void Stretch(RectTransform rt, float inset = 0f)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(inset, 0f);
        rt.offsetMax = new Vector2(-inset, 0f);
    }

    /// <summary>Remove everything built under Content (before building again).</summary>
    public static void Clear(RectTransform content)
    {
        for (int i = content.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(content.GetChild(i).gameObject);
    }
}
