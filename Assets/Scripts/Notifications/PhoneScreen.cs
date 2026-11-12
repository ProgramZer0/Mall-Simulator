using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The phone prefab's screen (see PhoneController): a home screen with a button per app, and the apps.
///
/// Setup, on the phone model prefab:
/// - A child Canvas set to World Space, sized and placed on the phone's screen (like the shop monitor's
///   canvas), with a Graphic Raycaster. Its Event Camera is set to the player's camera at runtime.
/// - Home: the home screen panel. App Button Prefab (a PhoneAppButton) is made once per app into App
///   Grid, placed in a grid by code (Columns, Cell Size, Spacing): no layout group.
/// - Apps: panels under the canvas with an IPhoneApp component (NotificationsApp, PhoneInfoApp...).
///   They're found by themselves; the home screen lists them in hierarchy order.
/// - Optional: Title Text (the open app's name, or Home Title), Back Button (back to home).
/// </summary>
public class PhoneScreen : MonoBehaviour
{
    [SerializeField] private Canvas canvas;
    [SerializeField] private GameObject home;
    [SerializeField] private PhoneAppButton appButtonPrefab;
    [SerializeField] private RectTransform appGrid;
    [SerializeField, Min(1)] private int columns = 3;
    [SerializeField] private Vector2 cellSize = new(90f, 100f);
    [SerializeField] private Vector2 spacing = new(10f, 10f);
    [SerializeField] private TMP_Text titleText;
    [SerializeField] private string homeTitle = "";
    [SerializeField] private Button backButton;

    private readonly List<MonoBehaviour> apps = new();
    private readonly List<PhoneAppButton> buttons = new();
    private int current = -1; // -1 = home

    public bool OnHome => current < 0;

    /// <summary>Called once by PhoneController after it makes the phone.</summary>
    public void Init(Camera cam)
    {
        if (canvas == null) canvas = GetComponentInChildren<Canvas>(true);
        if (canvas != null)
        {
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = cam;
        }
        // A phone model in front of the camera mustn't bump into things.
        foreach (var c in GetComponentsInChildren<Collider>(true)) c.enabled = false;

        apps.Clear();
        foreach (var mb in GetComponentsInChildren<MonoBehaviour>(true))
            if (mb is IPhoneApp) apps.Add(mb);
        foreach (var app in apps) app.gameObject.SetActive(false);

        if (appGrid != null)
            foreach (var g in appGrid.GetComponents<LayoutGroup>()) g.enabled = false;
        if (appButtonPrefab != null && appGrid != null)
            for (int i = 0; i < apps.Count; i++)
            {
                var b = Instantiate(appButtonPrefab, appGrid);
                var rt = (RectTransform)b.transform;
                rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
                rt.pivot = new Vector2(0f, 1f);
                rt.sizeDelta = cellSize;
                rt.anchoredPosition = new Vector2((i % columns) * (cellSize.x + spacing.x), -(i / columns) * (cellSize.y + spacing.y));
                var app = (IPhoneApp)apps[i];
                if (b.label != null) b.label.text = app.AppName;
                if (b.icon != null && app.Icon != null) b.icon.sprite = app.Icon;
                int index = i;
                if (b.button != null) b.button.onClick.AddListener(() => ShowApp(index));
                buttons.Add(b);
            }
        if (backButton != null) backButton.onClick.AddListener(ShowHome);
    }

    public void ShowHome()
    {
        CloseCurrent();
        if (home != null) home.SetActive(true);
        if (titleText != null) titleText.text = homeTitle;
        if (backButton != null) backButton.gameObject.SetActive(false);
        RefreshBadges();
    }

    /// <summary>Show an app by its App Name. False if there's none by that name.</summary>
    public bool ShowApp(string appName)
    {
        for (int i = 0; i < apps.Count; i++)
            if (((IPhoneApp)apps[i]).AppName == appName) { ShowApp(i); return true; }
        return false;
    }

    public void ShowApp(int index)
    {
        if (index < 0 || index >= apps.Count) return;
        CloseCurrent();
        if (home != null) home.SetActive(false);
        current = index;
        apps[index].gameObject.SetActive(true);
        var app = (IPhoneApp)apps[index];
        app.OnAppOpened();
        if (titleText != null) titleText.text = app.AppName;
        if (backButton != null) backButton.gameObject.SetActive(true);
    }

    /// <summary>Put every app away (the phone is going down).</summary>
    public void CloseApps() => CloseCurrent();

    private void CloseCurrent()
    {
        if (current < 0 || current >= apps.Count) { current = -1; return; }
        ((IPhoneApp)apps[current]).OnAppClosed();
        apps[current].gameObject.SetActive(false);
        current = -1;
    }

    private void RefreshBadges()
    {
        for (int i = 0; i < buttons.Count && i < apps.Count; i++)
        {
            if (buttons[i].badge == null) continue;
            int n = ((IPhoneApp)apps[i]).Badge;
            buttons[i].badge.text = n > 0 ? n.ToString() : "";
            if (buttons[i].badgeRoot != null) buttons[i].badgeRoot.SetActive(n > 0);
        }
    }
}
