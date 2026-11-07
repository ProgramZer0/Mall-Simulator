using TMPro;
using UnityEngine;

/// <summary>
/// The "F: Push cart" / "F: Buy Store02 (500)" prompt, on your own Canvas with TextMeshPro. The
/// player's PlayerInteractInput fills it in; while this is in the scene the built-in (IMGUI) prompt
/// isn't drawn.
///
/// Setup: Root (shown while there's a prompt) and Text.
/// </summary>
public class InteractPromptUI : MonoBehaviour
{
    public static InteractPromptUI Instance { get; private set; }

    [SerializeField] private GameObject root;
    [SerializeField] private TMP_Text text;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[InteractPromptUI] More than one in the scene; using the first.", this);
            return;
        }
        Instance = this;
        Show(null);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>Show a prompt, or hide it (null or empty).</summary>
    public void Show(string prompt)
    {
        bool on = !string.IsNullOrEmpty(prompt);
        if (text != null && on && text.text != prompt) text.text = prompt;
        if (root != null) { if (root.activeSelf != on) root.SetActive(on); }
        else if (text != null && text.gameObject.activeSelf != on) text.gameObject.SetActive(on);
    }
}
