using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// A button in the lobby (or anywhere) that changes how you look with F: the next or previous choice of
/// a PlayerLook pick or color (by its name: "Hat", "Shirt"...), or a set one (e.g. a paint swatch for
/// that color). Put it on (or above) the button's collider.
/// Optional Swatch: a renderer tinted the color it sets (Set) or the one you have.
/// </summary>
public class LookButton : MonoBehaviour, IPlayerUsable
{
    public enum Mode { Next, Previous, Set }

    [SerializeField] private PlayerLook.Part part = PlayerLook.Part.Pick;
    [Tooltip("The pick or color's name on PlayerLook.")]
    [SerializeField] private string partName = "Hat";
    [SerializeField] private Mode mode = Mode.Next;
    [Tooltip("Set: the choice it sets (0 = the first).")]
    [SerializeField, Min(0)] private int value;
    [Tooltip("Optional: the prompt instead of the default.")]
    [SerializeField] private string promptOverride;
    [Tooltip("Optional: tinted the color this sets (Set) or the one you have now (colors only).")]
    [SerializeField] private Renderer swatch;
    [SerializeField] private string swatchProperty = "_BaseColor";
    [Tooltip("Played when it's used (a sound, a click animation...).")]
    [SerializeField] private UnityEvent onUsed;

    private MaterialPropertyBlock block;

    private void OnEnable()
    {
        PlayerLook.LocalChanged += RefreshSwatch;
        RefreshSwatch();
    }

    private void OnDisable() => PlayerLook.LocalChanged -= RefreshSwatch;

    public string Prompt(ulong clientId)
    {
        var look = PlayerLook.Local;
        if (look == null) return null;
        int i = look.IndexOf(part, partName);
        int n = i >= 0 ? look.ChoiceCount(part, i) : 0;
        if (n <= 0) return null;
        if (!string.IsNullOrEmpty(promptOverride)) return promptOverride;
        int at = look.Choice(part, i) + 1;
        return mode switch
        {
            Mode.Next => $"F: Next {partName.ToLowerInvariant()} ({at}/{n})",
            Mode.Previous => $"F: Previous {partName.ToLowerInvariant()} ({at}/{n})",
            _ => look.Choice(part, i) == value ? $"{partName}: this one" : $"F: {partName} {value + 1}",
        };
    }

    public void Use(ulong clientId)
    {
        var look = PlayerLook.Local;
        if (look == null) return;
        int i = look.IndexOf(part, partName);
        if (i < 0) return;
        if (mode == Mode.Set) look.Set(part, i, value);
        else look.Step(part, i, mode == Mode.Next ? 1 : -1);
        onUsed?.Invoke();
    }

    private void RefreshSwatch()
    {
        if (swatch == null || part != PlayerLook.Part.Color) return;
        var look = PlayerLook.Local;
        if (look == null) return;
        int i = look.IndexOf(part, partName);
        if (i < 0) return;
        block ??= new MaterialPropertyBlock();
        swatch.GetPropertyBlock(block);
        block.SetColor(swatchProperty, look.ColorOption(i, mode == Mode.Set ? value : look.Choice(part, i)));
        swatch.SetPropertyBlock(block);
    }
}
