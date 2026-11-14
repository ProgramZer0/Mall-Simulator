using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A yes / no pop-up: Show("Give up X?", () => ...) runs the action on Yes. Put it on a panel (hidden
/// at start, last on its screen so it draws on top) with a Message text, a Yes and a No button.
/// </summary>
public class ConfirmPopup : MonoBehaviour
{
    [SerializeField] private GameObject root;
    [SerializeField] private TMP_Text messageText;
    [SerializeField] private Button yesButton;
    [SerializeField] private Button noButton;

    private Action onYes;

    public bool IsOpen => Root.activeSelf;
    private GameObject Root => root != null ? root : gameObject;

    private void Awake()
    {
        if (yesButton != null) yesButton.onClick.AddListener(Yes);
        if (noButton != null) noButton.onClick.AddListener(Close);
        // Hidden at start (but not when it's waking up because Show just turned it on).
        if (onYes == null) Root.SetActive(false);
    }

    public void Show(string message, Action yes)
    {
        onYes = yes;
        if (messageText != null) messageText.text = message;
        Root.SetActive(true);
        Root.transform.SetAsLastSibling();
    }

    public void Close()
    {
        onYes = null;
        Root.SetActive(false);
    }

    private void Yes()
    {
        var action = onYes;
        Close();
        action?.Invoke();
    }
}
