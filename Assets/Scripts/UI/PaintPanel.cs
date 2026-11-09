using TMPro;
using UnityEngine;

/// <summary>
/// Store edit mode's paint panel (TMP UI), shown while the paint tool is on (P). Pick a color, then
/// click an item, a built wall, the storefront or the floor to paint it.
///
/// Setup (on a Canvas with an EventSystem): Root is the panel shown and hidden; Swatches is a
/// PaintSwatchBar inside it. Info Text (optional) shows what's under the mouse and what painting it
/// costs. Without this panel in the scene, edit mode shows a simple built-in color list instead.
/// </summary>
public class PaintPanel : MonoBehaviour
{
    public static PaintPanel Instance { get; private set; }

    [SerializeField] private GameObject root;
    [SerializeField] private PaintSwatchBar swatches;
    [Tooltip("What's under the mouse and what painting it costs (optional).")]
    [SerializeField] private TMP_Text infoText;

    public PaintSwatchBar Swatches => swatches;
    public bool IsOpen => root != null && root.activeSelf;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[PaintPanel] More than one in the scene; using the first.", this);
            return;
        }
        Instance = this;
        if (root != null) root.SetActive(false);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public void SetOpen(bool open)
    {
        if (root != null) root.SetActive(open);
    }

    public void SetInfo(string text)
    {
        if (infoText != null && infoText.text != text) infoText.text = text ?? "";
    }
}
