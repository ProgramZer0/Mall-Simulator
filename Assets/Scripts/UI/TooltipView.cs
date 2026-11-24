using TMPro;
using UnityEngine;

/// <summary>
/// One tooltip look for every screen (licenses, perks, buy menu). Make it once: Tools > Mall Sim > UI >
/// Create Tooltip Prefab writes Assets/Resources/Tooltip.prefab (a panel with a name and a description,
/// sizing itself to its text), or build your own with this on its root. A screen whose own Tooltip Root
/// is empty makes its copy of it the first time it needs one (TooltipView.For), so changing the prefab
/// changes them all. A screen can still be given its own (Tooltip Prefab, or a Tooltip Root in the scene).
/// </summary>
public class TooltipView : MonoBehaviour
{
    /// <summary>Where the shared prefab goes, under a Resources folder.</summary>
    public const string ResourcePath = "Tooltip";

    public TMP_Text nameText;
    public TMP_Text descriptionText;

    private static TooltipView defaultPrefab;
    private static bool lookedForDefault;

    /// <summary>The shared tooltip prefab (Resources/Tooltip), or null if there isn't one.</summary>
    public static TooltipView DefaultPrefab
    {
        get
        {
            if (!lookedForDefault)
            {
                lookedForDefault = true;
                defaultPrefab = Resources.Load<TooltipView>(ResourcePath);
            }
            return defaultPrefab;
        }
    }

    /// <summary>
    /// The tooltip for a screen: one already under it, else a new copy of Prefab (or the shared one) made
    /// under it, hidden, drawn last. Null if there's none at all.
    /// </summary>
    public static TooltipView For(Transform screen, TooltipView prefab = null)
    {
        if (screen == null) return null;
        var existing = screen.GetComponentInChildren<TooltipView>(true);
        if (existing != null) return existing;
        prefab = prefab != null ? prefab : DefaultPrefab;
        if (prefab == null) return null;
        var made = Instantiate(prefab, screen, false);
        made.name = prefab.name;
        made.transform.SetAsLastSibling();
        TooltipFade.Hide(made.gameObject, instant: true);
        return made;
    }

    /// <summary>
    /// For screens: fill in their tooltip fields from the shared prefab when they weren't set (Root and
    /// texts). False if there's no tooltip to be had.
    /// </summary>
    public static bool Fill(Transform screen, TooltipView prefab, ref GameObject root, ref TMP_Text name, ref TMP_Text description)
    {
        if (root != null) return true;
        var view = For(screen, prefab);
        if (view == null) return false;
        root = view.gameObject;
        name = view.nameText;
        description = view.descriptionText;
        return true;
    }
}
