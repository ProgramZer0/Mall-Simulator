using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>A drop-down of the scene's PaintPalette colors for [PaletteColor] strings (a text box without one).</summary>
[CustomPropertyDrawer(typeof(PaletteColorAttribute))]
public class PaletteColorDrawer : PropertyDrawer
{
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        if (property.propertyType != SerializedPropertyType.String)
        {
            EditorGUI.PropertyField(position, property, label);
            return;
        }
        var palette = PaintPalette.Instance != null ? PaintPalette.Instance : Object.FindFirstObjectByType<PaintPalette>();
        if (palette == null)
        {
            EditorGUI.PropertyField(position, property, label);
            return;
        }
        var names = palette.Names().Select(n => n ?? "").ToList();
        string current = property.stringValue ?? "";
        int index = names.FindIndex(n => string.Equals(n, current, System.StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            // Not a palette color (renamed, or not picked yet): keep it, shown first.
            names.Insert(0, string.IsNullOrEmpty(current) ? "(pick a color)" : $"{current} (not in the palette)");
            index = 0;
        }
        EditorGUI.BeginProperty(position, label, property);
        int picked = EditorGUI.Popup(position, label.text, index, names.ToArray());
        if (picked != index) property.stringValue = palette.Names().ElementAt(picked - (names.Count - palette.Names().Count()));
        EditorGUI.EndProperty();
    }
}
