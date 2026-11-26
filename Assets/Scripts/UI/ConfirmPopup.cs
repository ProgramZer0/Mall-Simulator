using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A yes / no pop-up: Show("Give up X?", () => ...) runs the action on Yes (null: just a message;
/// either button closes it).
///
/// One prefab for every screen: make a prefab with this on its root (Message text, Yes and No buttons;
/// Root optional, the panel to show and hide) and put it at Assets/Resources/ConfirmPopup.prefab.
/// Screens make their own copy of it the first time they need one (ConfirmPopup.For), so changing
/// the prefab changes them all. A screen can still be given its own (its Popup field: a pop-up in the
/// scene, or another prefab).
/// </summary>
public class ConfirmPopup : MonoBehaviour
{
    /// <summary>Where the shared prefab goes, under a Resources folder.</summary>
    public const string ResourcePath = "ConfirmPopup";

    private static ConfirmPopup defaultPrefab;
    private static bool lookedForDefault;

    /// <summary>The shared pop-up prefab (Resources/ConfirmPopup), or null if there isn't one.</summary>
    public static ConfirmPopup DefaultPrefab
    {
        get
        {
            if (!lookedForDefault)
            {
                lookedForDefault = true;
                defaultPrefab = Resources.Load<ConfirmPopup>(ResourcePath);
            }
            return defaultPrefab;
        }
    }

    /// <summary>
    /// The pop-up for a screen: Assigned if it's one in the scene; else one already under the screen;
    /// else a new copy of the prefab (Assigned if it's a prefab, otherwise the shared one) made under
    /// the screen. Null if there's none at all.
    /// </summary>
    public static ConfirmPopup For(Transform screen, ConfirmPopup assigned = null)
    {
        if (assigned != null && assigned.gameObject.scene.IsValid()) return assigned;
        if (screen == null) return null;
        var existing = screen.GetComponentInChildren<ConfirmPopup>(true);
        if (existing != null) return existing;
        var prefab = assigned != null ? assigned : DefaultPrefab;
        if (prefab == null) return null;
        var made = Instantiate(prefab, screen, false);
        made.name = prefab.name;
        return made;
    }

    [SerializeField] private GameObject root;
    [SerializeField] private TMP_Text messageText;
    [SerializeField] private Button yesButton;
    [SerializeField] private Button noButton;

    private Action onYes;
    private bool showing; // Show turned it on (so Awake, running just then, mustn't hide it)

    public bool IsOpen => Root.activeInHierarchy;

    private static int closedFrame = -1;
    /// <summary>A pop-up closed this frame (so the click that closed it isn't also taken by what's behind).</summary>
    public static bool JustClosed => Time.frameCount <= closedFrame + 1;

    private bool hooked;

    // Its buttons, once (Awake may not have run: its object left unticked under an active Root, say).
    private void Hook()
    {
        if (hooked) return;
        hooked = true;
        if (yesButton != null) yesButton.onClick.AddListener(Yes);
        if (noButton != null) noButton.onClick.AddListener(Close);
    }
    private GameObject Root => root != null ? root : gameObject;

    private void Awake()
    {
        Hook();
        // Hidden at start (but not when it's waking up because Show just turned it on).
        if (!showing) Root.SetActive(false);
    }

    public void Show(string message, Action yes)
    {
        onYes = yes;
        showing = true;
        Hook();
        if (messageText != null) messageText.text = message;
        if (!gameObject.activeSelf) gameObject.SetActive(true); // left unticked in the scene: on (Awake keeps Root showing)
        Root.SetActive(true);
        transform.SetAsLastSibling(); // on top of the rest of the screen
        if (Root != gameObject) Root.transform.SetAsLastSibling();
    }

    public void Close()
    {
        onYes = null;
        showing = false;
        closedFrame = Time.frameCount;
        Root.SetActive(false);
    }

    private void Yes()
    {
        var action = onYes;
        Close();
        action?.Invoke();
    }
}
