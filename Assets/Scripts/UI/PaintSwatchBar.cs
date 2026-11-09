using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A row of color swatches from the PaintPalette (TMP UI), used by the buy menu. It fills itself, so
/// new palette colors show up by themselves.
///
/// Setup (on a Canvas with an EventSystem):
/// - Container: where swatches go, e.g. an object with a Horizontal or Grid Layout Group.
/// - Swatch Prefab: a Button whose Image is tinted with the color. Optional children: one named
///   "Selected" (shown on the picked swatch) and a TMP text (gets the color's name).
/// - Optional: Name Text shows the picked color's name.
/// The first swatch is "Original" (the model's own colors), drawn with Original Swatch Color.
/// </summary>
public class PaintSwatchBar : MonoBehaviour
{
    [SerializeField] private RectTransform container;
    [SerializeField] private Button swatchPrefab;
    [Tooltip("Shows the picked color's name (optional).")]
    [SerializeField] private TMP_Text nameText;
    [Tooltip("How the Original swatch (no paint) is drawn.")]
    [SerializeField] private Color originalSwatchColor = new(1f, 1f, 1f, 0.35f);

    private readonly List<(byte id, Button button, GameObject selected)> swatches = new();
    private int builtFor = -1;

    /// <summary>The picked color (PaintPalette id, 0 = Original).</summary>
    public byte Selected { get; private set; }

    /// <summary>A swatch was clicked.</summary>
    public event Action<byte> Picked;

    private void OnEnable() => Build();

    /// <summary>Pick a color without raising Picked (e.g. to show what something already is).</summary>
    public void SetSelected(byte id)
    {
        Selected = PaintPalette.IsValid(id) ? id : (byte)0;
        RefreshSelection();
    }

    private void Build()
    {
        var palette = PaintPalette.Instance;
        int count = palette != null ? palette.Count : 0;
        if (builtFor == count && swatches.Count > 0) { RefreshSelection(); return; }
        builtFor = count;

        foreach (var s in swatches) if (s.button != null) Destroy(s.button.gameObject);
        swatches.Clear();
        if (swatchPrefab == null)
        {
            Debug.LogWarning("[PaintSwatchBar] Needs a Swatch Prefab.", this);
            return;
        }
        Transform parent = container != null ? container : transform;

        for (int i = 0; i <= count; i++)
        {
            byte id = (byte)i;
            var button = Instantiate(swatchPrefab, parent);
            button.name = $"Swatch {PaintPalette.NameOf(id)}";
            // Turning the icon would turn the button's label and Selected mark too, so only label-less ones turn.
            PaintPalette.StyleSwatch(button.image, id, originalSwatchColor,
                                     turn: button.GetComponentInChildren<TMP_Text>(true) == null && button.transform.childCount == 0);
            var label = button.GetComponentInChildren<TMP_Text>(true);
            if (label != null) label.text = PaintPalette.NameOf(id);
            var selected = button.transform.Find("Selected");
            button.onClick.AddListener(() =>
            {
                Selected = id;
                RefreshSelection();
                Picked?.Invoke(id);
            });
            swatches.Add((id, button, selected != null ? selected.gameObject : null));
        }
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        foreach (var s in swatches)
            if (s.selected != null) s.selected.SetActive(s.id == Selected);
        if (nameText != null) nameText.text = PaintPalette.NameOf(Selected);
    }
}
