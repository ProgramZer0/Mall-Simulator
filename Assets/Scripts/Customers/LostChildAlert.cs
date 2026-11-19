using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A HUD alert when a child gets lost: live pictures of the child and of their parents, side by side,
/// for a few seconds. (The phone's Lost & Found app shows them for as long as the child's lost.)
///
/// It comes up big in the middle of the screen, holds there, then shrinks down to where you placed it
/// (its small size), turning see-through as it goes, and at the end fades out.
///
/// Setup: on the HUD canvas. Root: the alert panel, placed and sized where it should sit small (e.g. a
/// corner); it's hidden by itself until needed. Child Picture and Parents Picture: Raw Images in it.
/// Optional Title Text. (A Canvas Group is added to Root for the fading if it has none.)
/// Hidden while the phone's up. Purely local.
/// </summary>
public class LostChildAlert : MonoBehaviour
{
    [SerializeField] private GameObject root;
    [SerializeField] private RawImage childPicture;
    [SerializeField] private RawImage parentsPicture;
    [SerializeField] private TMP_Text titleText;
    [SerializeField] private string title = "A family lost their child!\nTab: phone, Lost & Found to find them";
    [Tooltip("How long it's up in all (seconds).")]
    [SerializeField, Min(1f)] private float showSeconds = 10f;
    [Tooltip("Fading in at the start and out at the end (seconds).")]
    [SerializeField, Min(0f)] private float fadeSeconds = 0.3f;

    [Header("Big, then small")]
    [Tooltip("How big it starts, in the middle of the screen (times its placed size).")]
    [SerializeField, Min(0.1f)] private float bigScale = 2.2f;
    [Tooltip("How long it stays big (seconds)...")]
    [SerializeField, Min(0f)] private float bigSeconds = 2.5f;
    [Tooltip("...and how long it takes to shrink to where it's placed (seconds).")]
    [SerializeField, Min(0.01f)] private float shrinkSeconds = 0.8f;
    [Tooltip("How see-through it is once small (1 = solid, 0 = gone).")]
    [SerializeField, Range(0.05f, 1f)] private float smallAlpha = 0.6f;
    [SerializeField] private LostChildShotSettings shot = new() { size = new Vector2Int(256, 192) };

    private LostChildShot childShot, parentsShot;
    private CustomerManager.LostCase shown;
    private float shownAt = -1f;
    private CanvasGroup fade;
    private readonly LostChildShotSettings.Still childStill = new();
    private RectTransform rect;
    private Vector3 smallScale;

    private void Awake()
    {
        if (root == null) root = gameObject;
        if (!root.TryGetComponent(out fade)) fade = root.AddComponent<CanvasGroup>();
        fade.blocksRaycasts = false; // never in the way of clicks
        fade.interactable = false;
        rect = root.transform as RectTransform;
        if (rect != null) { smallScale = rect.localScale; placedAt = rect.anchoredPosition; }
        if (root != gameObject) root.SetActive(false);
        else if (fade != null) fade.alpha = 0f;
    }

    private void OnEnable() => CustomerManager.LostCaseOpened += Show;
    private void OnDisable()
    {
        CustomerManager.LostCaseOpened -= Show;
        Hide();
    }

    private void OnDestroy()
    {
        childShot?.Dispose();
        parentsShot?.Dispose();
    }

    private void Show(CustomerManager.LostCase lost)
    {
        childShot ??= shot.Make("Lost Child Alert Camera");
        parentsShot ??= shot.Make("Lost Child Alert Parents Camera");
        if (childPicture != null) childPicture.texture = childShot.Texture;
        if (parentsPicture != null) parentsPicture.texture = parentsShot.Texture;
        if (titleText != null) titleText.text = title;
        shown = lost;
        childStill.Clear();
        shownAt = Time.unscaledTime;
        root.SetActive(true);
        Place(0f);
        fade.alpha = 0f;
    }

    private void Hide()
    {
        shownAt = -1f;
        if (childShot != null) childShot.On = false;
        if (parentsShot != null) parentsShot.On = false;
        Place(0f);
        if (root != null && root != gameObject) root.SetActive(false);
        else if (fade != null) fade.alpha = 0f;
    }

    private void LateUpdate()
    {
        if (shownAt < 0f) return;
        float t = Time.unscaledTime - shownAt;
        if (t >= showSeconds || PhoneController.IsOpen || !StillLost()) { Hide(); return; }
        // Big in the middle, then shrinking to its place and turning see-through.
        float shrink = Mathf.Clamp01((t - bigSeconds) / shrinkSeconds);
        shrink = shrink * shrink * (3f - 2f * shrink); // ease in and out
        Place(shrink);
        float alpha = Mathf.Lerp(1f, smallAlpha, shrink);
        if (fadeSeconds > 0f) alpha *= Mathf.Clamp01(Mathf.Min(t, showSeconds - t) / fadeSeconds);
        fade.alpha = alpha;
        shot.KeepStill(childShot, shown.kid, childStill); // a still picture of the child; the parents live
        parentsShot.On = shot.Aim(parentsShot, shown.family);
    }

    // 0: big in the middle of the screen; 1: small where it was placed.
    private void Place(float small)
    {
        if (rect == null) return;
        // Back where it was placed first (so any anchors work), then moved from there.
        rect.localScale = smallScale;
        rect.anchoredPosition = placedAt;
        var parent = rect.parent as RectTransform;
        if (parent == null) { rect.localScale = smallScale * Mathf.Lerp(bigScale, 1f, small); return; }
        Vector3 home = rect.position;
        // Where its pivot goes for its middle to sit in the middle of the parent (the screen).
        Vector3 middleOffset = rect.TransformVector(rect.rect.center); // pivot to its middle (world)
        Vector3 big = parent.TransformPoint(parent.rect.center) - middleOffset * bigScale;
        rect.localScale = smallScale * Mathf.Lerp(bigScale, 1f, small);
        rect.position = Vector3.Lerp(big, home, small);
    }

    private Vector2 placedAt;

    private bool StillLost()
    {
        var cm = CustomerManager.Instance;
        if (cm == null) return false;
        foreach (var c in cm.LostCases) if (c.kid == shown.kid) return true;
        return false;
    }
}
