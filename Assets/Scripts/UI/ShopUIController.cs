using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Something on a shop tab that wants to know which store the shop was opened for (the buy menu,
/// and any screens you add). Put it on a component anywhere under a tab's screen.
/// </summary>
public interface IShopScreen
{
    /// <summary>The shop was opened (or reopened) for this store.</summary>
    void OnShopOpened(StorePlot plot);
}

/// <summary>
/// The shop monitor GUI. Put this on your Canvas (a scene object; each player has their own copy
/// of the scene, so the GUI is automatically just theirs).
///
/// Root: the panel that holds everything; hidden until the monitor is used.
/// Tabs: each tab is a button plus the screen it shows. The first tab (the buy menu) opens by default.
/// While it's open the player can't move or interact and the cursor is free (see UIState).
/// </summary>
public class ShopUIController : MonoBehaviour
{
    [Serializable]
    public class Tab
    {
        public string name = "Tab";
        public Button button;
        public GameObject screen;
    }

    public static ShopUIController Instance { get; private set; }

    [SerializeField] private GameObject root;
    [SerializeField] private Button closeButton;
    [Tooltip("First tab is shown when the shop opens.")]
    [SerializeField] private Tab[] tabs;
    [SerializeField] private KeyCode closeKey = KeyCode.Escape;

    [Header("Tab Look (optional)")]
    [Tooltip("Tint the selected tab's button with this colour and the others with Normal.")]
    [SerializeField] private bool tintTabs = true;
    [SerializeField] private Color selectedTabColor = new(1f, 1f, 1f, 1f);
    [SerializeField] private Color normalTabColor = new(0.75f, 0.75f, 0.75f, 1f);

    /// <summary>Is the shop showing?</summary>
    public bool IsOpen { get; private set; }

    /// <summary>The store the shop was opened for.</summary>
    public StorePlot CurrentPlot { get; private set; }

    public int CurrentTab { get; private set; }

    public event Action Opened;
    public event Action Closed;
    public event Action<int> TabChanged;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[ShopUIController] More than one in the scene; using the first.", this);
            return;
        }
        Instance = this;

        if (closeButton != null) closeButton.onClick.AddListener(Close);
        if (tabs != null)
        {
            for (int i = 0; i < tabs.Length; i++)
            {
                int index = i; // captured per button
                if (tabs[i]?.button != null) tabs[i].button.onClick.AddListener(() => ShowTab(index));
            }
        }

        if (root != null) root.SetActive(false);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        UIState.CloseMenu(this);
    }

    private void Update()
    {
        if (!IsOpen) return;

        // Close on the key, if the session ended, or if the store stopped being this player's.
        var nm = NetworkManager.Singleton;
        bool lost = nm == null || !nm.IsListening || CurrentPlot == null || !CurrentPlot.IsAuthorized(nm.LocalClientId);
        if (Input.GetKeyDown(closeKey) || lost) Close();
    }

    /// <summary>Show the shop for a store, on the first tab.</summary>
    public void Open(StorePlot plot)
    {
        if (plot == null) return;
        CurrentPlot = plot;
        IsOpen = true;
        if (root != null) root.SetActive(true);
        UIState.OpenMenu(this);

        ShowTab(0);
        Opened?.Invoke();
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        if (root != null) root.SetActive(false);
        UIState.CloseMenu(this);
        Closed?.Invoke();
    }

    /// <summary>Show one tab's screen and hide the rest.</summary>
    public void ShowTab(int index)
    {
        if (tabs == null || tabs.Length == 0) return;
        index = Mathf.Clamp(index, 0, tabs.Length - 1);
        CurrentTab = index;

        for (int i = 0; i < tabs.Length; i++)
        {
            var tab = tabs[i];
            if (tab == null) continue;
            if (tab.screen != null) tab.screen.SetActive(i == index);
            if (tintTabs && tab.button != null && tab.button.targetGraphic != null)
                tab.button.targetGraphic.color = i == index ? selectedTabColor : normalTabColor;
        }

        // Let the screen know which store it's for.
        var screen = tabs[index]?.screen;
        if (screen != null && CurrentPlot != null)
            foreach (var s in screen.GetComponentsInChildren<IShopScreen>(true))
                s.OnShopOpened(CurrentPlot);

        TabChanged?.Invoke(index);
    }
}
