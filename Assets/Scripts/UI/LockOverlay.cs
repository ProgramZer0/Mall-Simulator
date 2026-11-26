using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A locked picture on a card (licenses, perks): the card's own Locked Mark if it has one, else the
/// screen's Locked Icon laid over the card's picture (made once, same size and place). The picture
/// underneath is dimmed while it's locked.
/// </summary>
public static class LockOverlay
{
    public static void Show(Image icon, ref GameObject mark, Sprite lockedIcon, bool locked, float dim = 0.45f)
    {
        if (mark == null && locked && icon != null && lockedIcon != null)
        {
            var go = new GameObject("Locked", typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(icon.transform, false);
            rt.anchorMin = new Vector2(0.2f, 0.2f); // centred, a bit smaller than the picture
            rt.anchorMax = new Vector2(0.8f, 0.8f);
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            var img = go.GetComponent<Image>();
            img.sprite = lockedIcon;
            img.preserveAspect = true;
            img.raycastTarget = false; // the card still shows its tooltip
            mark = go;
        }
        if (mark != null && mark.activeSelf != locked) mark.SetActive(locked);
        if (icon == null) return;
        // Dimmed while locked, back to its own colour after (only what this dimmed).
        if (locked && !dimmed.ContainsKey(icon)) { dimmed[icon] = icon.color; icon.color = icon.color * new Color(dim, dim, dim, 1f); }
        else if (!locked && dimmed.TryGetValue(icon, out var was)) { icon.color = was; dimmed.Remove(icon); }
    }

    private static readonly System.Collections.Generic.Dictionary<Image, Color> dimmed = new();
}
