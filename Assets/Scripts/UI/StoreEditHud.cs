using TMPro;
using UnityEngine;

/// <summary>
/// The store edit mode HUD on your own Canvas, with TextMeshPro. The edit controller fills in the
/// texts every frame (they change with the tool, the selected item and what's under the mouse); you
/// decide where and how they look.
///
/// Setup: put this on an object on the Canvas and assign what you want to show. Any text left empty is
/// skipped, so you can show only the parts you like.
/// - Root: shown while editing, hidden otherwise.
/// - Title Text: the mode ("STORE EDIT MODE", "WALL TOOL - REMOVING"...).
/// - Status Text: selected item, tool and gravity, or the wall height.
/// - Controls Text: the keys for what you're doing now (several lines).
/// - Info Text: live feedback ("2.5 m full wall: $120", "Too close to another window"...).
///   Info Root (optional) is hidden while there's no info.
/// - Message Root / Message Text: short messages ("Removed 2 items: $40 back."), shown for Message
///   Seconds. These can show outside edit mode too (e.g. "Someone is in the store").
///
/// Texts use TMP rich text, so key names come in bold.
/// While this is in the scene, the controller's built-in (IMGUI) HUD isn't drawn.
/// </summary>
public class StoreEditHud : MonoBehaviour
{
    public static StoreEditHud Instance { get; private set; }

    [SerializeField] private GameObject root;
    [SerializeField] private TMP_Text titleText;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private TMP_Text controlsText;
    [SerializeField] private TMP_Text infoText;
    [SerializeField] private GameObject infoRoot;

    [Header("Messages")]
    [SerializeField] private GameObject messageRoot;
    [SerializeField] private TMP_Text messageText;
    [SerializeField] private float messageSeconds = 4f;

    private float messageUntil;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[StoreEditHud] More than one in the scene; using the first.", this);
            return;
        }
        Instance = this;
        if (root != null) root.SetActive(false);
        if (messageRoot != null) messageRoot.SetActive(false);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Update()
    {
        if (messageUntil > 0f && Time.unscaledTime > messageUntil)
        {
            messageUntil = 0f;
            if (messageRoot != null) messageRoot.SetActive(false);
            else if (messageText != null) messageText.text = "";
        }
    }

    public void SetVisible(bool visible)
    {
        if (root != null && root.activeSelf != visible) root.SetActive(visible);
    }

    /// <summary>Called by the edit controller every frame while editing.</summary>
    public void SetTexts(string title, string status, string controls, string info)
    {
        Set(titleText, title);
        Set(statusText, status);
        Set(controlsText, controls);
        Set(infoText, info);
        if (infoRoot != null)
        {
            bool show = !string.IsNullOrEmpty(info);
            if (infoRoot.activeSelf != show) infoRoot.SetActive(show);
        }
    }

    public void ShowMessage(string message)
    {
        if (messageText == null) return;
        messageText.text = message;
        if (messageRoot != null) messageRoot.SetActive(true);
        messageUntil = Time.unscaledTime + messageSeconds;
    }

    // Only touch the text when it changes (TMP rebuilds its mesh on every set).
    private static void Set(TMP_Text text, string value)
    {
        if (text == null) return;
        value ??= "";
        if (text.text != value) text.text = value;
    }
}
