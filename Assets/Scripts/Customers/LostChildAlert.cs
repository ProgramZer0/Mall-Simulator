using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A HUD alert when a child gets lost: live pictures of the child and of their parents, side by side,
/// for a few seconds. (The phone's Lost & Found app shows them for as long as the child's lost.)
///
/// Setup: on the HUD canvas. Root: the alert panel (hidden by itself until needed). Child Picture and
/// Parents Picture: Raw Images in it. Optional Title Text, and a Canvas Group on Root to fade it.
/// Hidden while the phone's up. Purely local.
/// </summary>
public class LostChildAlert : MonoBehaviour
{
    [SerializeField] private GameObject root;
    [SerializeField] private RawImage childPicture;
    [SerializeField] private RawImage parentsPicture;
    [SerializeField] private TMP_Text titleText;
    [SerializeField] private string title = "A family lost their child!\nTab: phone, Lost & Found to find them";
    [SerializeField, Min(1f)] private float showSeconds = 8f;
    [SerializeField, Min(0f)] private float fadeSeconds = 0.4f;
    [SerializeField] private LostChildShotSettings shot = new() { size = new Vector2Int(256, 192) };

    private LostChildShot childShot, parentsShot;
    private CustomerManager.LostCase shown;
    private float shownAt = -1f;
    private CanvasGroup fade;

    private void Awake()
    {
        if (root == null) root = gameObject;
        fade = root.GetComponent<CanvasGroup>();
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
        shownAt = Time.unscaledTime;
        root.SetActive(true);
    }

    private void Hide()
    {
        shownAt = -1f;
        if (childShot != null) childShot.On = false;
        if (parentsShot != null) parentsShot.On = false;
        if (root != null && root != gameObject) root.SetActive(false);
        else if (fade != null) fade.alpha = 0f;
    }

    private void LateUpdate()
    {
        if (shownAt < 0f) return;
        float t = Time.unscaledTime - shownAt;
        if (t >= showSeconds || PhoneController.IsOpen || !StillLost()) { Hide(); return; }
        if (fade != null && fadeSeconds > 0f) fade.alpha = Mathf.Clamp01(Mathf.Min(t, showSeconds - t) / fadeSeconds);
        childShot.On = shot.Aim(childShot, shown.kid);
        parentsShot.On = shot.Aim(parentsShot, shown.family);
    }

    private bool StillLost()
    {
        var cm = CustomerManager.Instance;
        if (cm == null) return false;
        foreach (var c in cm.LostCases) if (c.kid == shown.kid) return true;
        return false;
    }
}
