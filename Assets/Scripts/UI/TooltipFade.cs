using UnityEngine;

/// <summary>
/// Tooltips fade in and out instead of popping. The screens with tooltips (licenses, perks, buy menu)
/// call TooltipFade.Show / Hide on their Tooltip Root instead of SetActive; this component (and a
/// CanvasGroup that never blocks the mouse) is added to it by itself. Nothing to set up.
/// </summary>
[DisallowMultipleComponent]
public class TooltipFade : MonoBehaviour
{
    /// <summary>Seconds to fade in, and out (every tooltip).</summary>
    public static float FadeInSeconds = 0.12f, FadeOutSeconds = 0.1f;

    private CanvasGroup group;
    private float target;

    public static void Show(GameObject root)
    {
        if (root == null) return;
        var f = Get(root);
        bool was = root.activeSelf;
        root.SetActive(true);
        if (!was) f.group.alpha = 0f; // coming up from hidden: from clear
        f.target = 1f;
    }

    public static void Hide(GameObject root, bool instant = false)
    {
        if (root == null) return;
        if (instant || !root.activeInHierarchy) { root.SetActive(false); return; }
        Get(root).target = 0f;
    }

    private static TooltipFade Get(GameObject root)
    {
        if (!root.TryGetComponent<TooltipFade>(out var f)) f = root.AddComponent<TooltipFade>();
        if (f.group == null)
        {
            if (!root.TryGetComponent(out f.group)) f.group = root.AddComponent<CanvasGroup>();
            f.group.blocksRaycasts = false; // a tooltip never gets in the way of the mouse
            f.group.interactable = false;
        }
        return f;
    }

    // Closed along with its screen while fading out: comes back hidden, not half-faded.
    private void OnDisable() { if (target <= 0f && group != null) group.alpha = 0f; }

    private void Update()
    {
        if (group == null) return;
        float seconds = target > group.alpha ? FadeInSeconds : FadeOutSeconds;
        group.alpha = seconds <= 0f ? target : Mathf.MoveTowards(group.alpha, target, Time.unscaledDeltaTime / seconds);
        if (target <= 0f && group.alpha <= 0f) gameObject.SetActive(false);
    }
}
