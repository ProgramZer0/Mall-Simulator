using System;
using UnityEngine;

/// <summary>
/// On a string field (or a list of them): the Inspector shows a drop-down of the PaintPalette's colors
/// (from the palette in the open scene), so color names are picked, not typed.
/// </summary>
public class PaletteColorAttribute : PropertyAttribute { }

/// <summary>How much a color is liked (for an item, or by a kind of customer). Colors not listed: neutral.</summary>
[Serializable]
public class ColorTaste
{
    [PaletteColor] public string color;
    [Tooltip("-1: put off by it. 0: neutral. 1: love it.")]
    [Range(-1f, 1f)] public float weight;
}
