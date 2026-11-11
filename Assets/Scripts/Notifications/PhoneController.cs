using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A phone app (see PhoneController). Put a component that implements it on each app's screen
/// under the phone's Screen; the phone finds them and shows a button for each when there's more than one.
/// NotificationsApp is the first; more can come later (bids, store stats...).
/// </summary>
public interface IPhoneApp
{
    string AppName { get; }
    /// <summary>The app is shown (its GameObject has just been turned on).</summary>
    void OnAppOpened();
    /// <summary>The app is hidden (or the phone put away).</summary>
    void OnAppClosed();
}

/// <summary>
/// The player's phone: Tab brings it up (Tab or Esc puts it away) (the cursor is freed and the player stops, like other menus)
/// and puts it away. Purely local.
///
/// Setup (on the HUD canvas or anywhere in the scene):
/// - Phone Root: the phone UI panel (hidden at start). Apps: GameObjects with an IPhoneApp component
///   under it (e.g. NotificationsApp); the first is opened by default.
/// - Optional: App Bar + App Button Prefab (a Button with a TMP label) to switch apps, made only when
///   there's more than one. Hand Model Prefab: shown in front of the camera while the phone is out.
/// - Optional: Hint Text on the HUD, e.g. "Tab: Phone (3)" with the unread count.
/// </summary>
public class PhoneController : MonoBehaviour
{
    public static bool IsOpen { get; private set; }

    [SerializeField] private KeyCode key = KeyCode.Tab;
    [SerializeField] private GameObject phoneRoot;
    [SerializeField] private RectTransform appBar;
    [SerializeField] private Button appButtonPrefab;
    [Tooltip("Optional: a phone model held in front of the camera while it's out.")]
    [SerializeField] private GameObject handModelPrefab;
    [SerializeField] private Vector3 handModelOffset = new(0.18f, -0.16f, 0.4f);
    [SerializeField] private Vector3 handModelRotation = new(-15f, 180f, 0f);
    [Tooltip("Optional HUD text: {0} = unread count. Empty when nothing's unread.")]
    [SerializeField] private TMP_Text hintText;
    [SerializeField] private string hintFormat = "Tab: Phone ({0})";

    private readonly List<MonoBehaviour> apps = new();
    private int current = -1;
    private GameObject handModel;

    private void Awake()
    {
        if (phoneRoot != null)
        {
            foreach (var mb in phoneRoot.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb is IPhoneApp) apps.Add(mb);
            foreach (var app in apps) app.gameObject.SetActive(false);
            phoneRoot.SetActive(false);
        }
        if (appBar != null && appButtonPrefab != null && apps.Count > 1)
            for (int i = 0; i < apps.Count; i++)
            {
                int index = i;
                var b = Instantiate(appButtonPrefab, appBar);
                var label = b.GetComponentInChildren<TMP_Text>();
                if (label != null) label.text = ((IPhoneApp)apps[i]).AppName;
                b.onClick.AddListener(() => OpenApp(index));
            }
        IsOpen = false;
    }

    private void OnEnable()
    {
        Notifications.Changed += RefreshHint;
        RefreshHint();
    }

    private void OnDisable()
    {
        Notifications.Changed -= RefreshHint;
        if (IsOpen) Close();
    }

    private void Update()
    {
        if (IsOpen && Input.GetKeyDown(KeyCode.Escape)) { Close(); return; }
        if (!Input.GetKeyDown(key)) return;
        if (IsOpen) Close();
        else if (!UIState.IsMenuOpen && InGame()) Open();
    }

    private static bool InGame()
    {
        var nm = Unity.Netcode.NetworkManager.Singleton;
        return nm != null && nm.IsListening && nm.LocalClient?.PlayerObject != null;
    }

    public void Open()
    {
        if (IsOpen || phoneRoot == null) return;
        IsOpen = true;
        phoneRoot.SetActive(true);
        UIState.OpenMenu(this);
        OpenApp(current >= 0 ? current : 0);
        ShowHandModel(true);
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        if (current >= 0 && current < apps.Count)
        {
            ((IPhoneApp)apps[current]).OnAppClosed();
            apps[current].gameObject.SetActive(false);
        }
        if (phoneRoot != null) phoneRoot.SetActive(false);
        UIState.CloseMenu(this);
        ShowHandModel(false);
    }

    /// <summary>Show one of the phone's apps (by its order under Phone Root).</summary>
    public void OpenApp(int index)
    {
        if (index < 0 || index >= apps.Count) return;
        if (current >= 0 && current < apps.Count && current != index && apps[current].gameObject.activeSelf)
        {
            ((IPhoneApp)apps[current]).OnAppClosed();
            apps[current].gameObject.SetActive(false);
        }
        current = index;
        apps[index].gameObject.SetActive(true);
        ((IPhoneApp)apps[index]).OnAppOpened();
    }

    private void ShowHandModel(bool on)
    {
        if (handModelPrefab == null) return;
        var cam = Camera.main;
        if (on && handModel == null && cam != null)
        {
            handModel = Instantiate(handModelPrefab, cam.transform);
            handModel.transform.localPosition = handModelOffset;
            handModel.transform.localRotation = Quaternion.Euler(handModelRotation);
            foreach (var c in handModel.GetComponentsInChildren<Collider>()) c.enabled = false;
        }
        if (handModel != null) handModel.SetActive(on);
    }

    private void RefreshHint()
    {
        if (hintText == null) return;
        int unread = Notifications.UnreadCount;
        hintText.text = unread > 0 ? string.Format(hintFormat, unread) : "";
    }

    private void OnDestroy()
    {
        if (IsOpen) UIState.CloseMenu(this);
        IsOpen = false;
    }
}
