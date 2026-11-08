using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A crosshair in the middle of the screen, so you can tell what you're aiming at. It changes colour
/// (and size) when you're looking at something you can use, and hides in menus and store edit mode.
///
/// Setup: put it on an object on the Canvas. Image is your crosshair sprite; leave it empty and a
/// small dot is made for you.
/// </summary>
public class CrosshairUI : MonoBehaviour
{
    [SerializeField] private Image image;
    [SerializeField] private Color normalColor = new(1f, 1f, 1f, 0.8f);
    [SerializeField] private Color targetColor = new(1f, 0.85f, 0.2f, 1f);
    [Tooltip("Size in pixels; it grows a little when there's something to use.")]
    [SerializeField] private float size = 6f;
    [SerializeField] private float targetSize = 10f;

    private void Awake()
    {
        if (image != null) return;
        // No sprite given: a plain dot in the middle.
        var go = new GameObject("Crosshair", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(transform, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        image = go.GetComponent<Image>();
        image.raycastTarget = false;
    }

    private void LateUpdate()
    {
        if (image == null) return;
        var nm = NetworkManager.Singleton;
        bool inGame = nm != null && (nm.IsClient || nm.IsServer) && nm.LocalClient?.PlayerObject != null;
        bool show = inGame && !UIState.IsMenuOpen && !Editing();
        if (image.enabled != show) image.enabled = show;
        if (!show) return;

        bool target = PlayerInteractInput.LocalHasTarget;
        image.color = target ? targetColor : normalColor;
        float px = target ? targetSize : size;
        var rt = image.rectTransform;
        if (rt.sizeDelta.x != px) rt.sizeDelta = new Vector2(px, px);
    }

    private static bool Editing()
    {
        var mall = MallManager.Instance;
        var nm = NetworkManager.Singleton;
        return mall != null && nm != null && mall.GetPlotEditedBy(nm.LocalClientId) != null;
    }
}
