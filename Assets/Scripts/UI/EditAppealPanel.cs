using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The panel for the store edit mode appeal view (V; see EditAppealView). Put it on the HUD canvas.
/// Root: the panel (hidden until the view's on). Text: a TMP text for everything (rich text). Optional
/// Quality Fill: an Image (Filled) that shows Quality as a bar. Without this, the view uses a plain box.
/// </summary>
public class EditAppealPanel : MonoBehaviour
{
    public static EditAppealPanel Instance { get; private set; }

    [SerializeField] private GameObject root;
    [SerializeField] private TMP_Text text;
    [SerializeField] private Image qualityFill;
    [Tooltip("Quality bar color from poor to great.")]
    [SerializeField] private Gradient qualityColors = DefaultColors();

    private void Awake()
    {
        Instance = this;
        if (root == null) root = gameObject;
        if (root != gameObject) root.SetActive(false);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public void Show(string body, float quality)
    {
        if (root != null) root.SetActive(true);
        if (text != null) text.text = body;
        if (qualityFill != null)
        {
            qualityFill.fillAmount = Mathf.Clamp01(quality);
            qualityFill.color = qualityColors.Evaluate(Mathf.Clamp01(quality));
        }
    }

    public void Hide()
    {
        if (root != null && root != gameObject) root.SetActive(false);
    }

    private static Gradient DefaultColors()
    {
        var g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(new Color(0.9f, 0.3f, 0.25f), 0f), new GradientColorKey(new Color(1f, 0.8f, 0.2f), 0.5f),
                    new GradientColorKey(new Color(0.3f, 0.9f, 0.4f), 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
        return g;
    }
}
