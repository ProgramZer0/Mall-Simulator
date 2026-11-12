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
/// The shop monitor GUI (a scene object; each player has their own copy of the scene, so the GUI is
/// automatically just theirs).
///
/// Root: the panel that holds everything; hidden until the monitor is used.
/// Tabs: each tab is a button plus the screen it shows. The first tab (the buy menu) opens by default.
/// While it's open the player can't move or interact and the cursor is free (see UIState).
///
/// On the monitor's screen: put the shop on its own Canvas (nothing else on it) and drag that Canvas
/// into Shop Canvas. When a monitor with a Screen is used, the camera zooms in on it and this Canvas
/// is moved onto the monitor's screen (world space, sized to Reference Size); stepping back (F, Esc,
/// right click) puts both back. Without a Shop Canvas, or for a monitor with no Screen, the shop
/// opens over the whole view instead.
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

    [Header("On the monitor (optional)")]
    [Tooltip("The Canvas the shop is on (only the shop). It's moved onto the monitor's screen while in use.")]
    [SerializeField] private Canvas shopCanvas;
    [Tooltip("The shop's size on the monitor, in UI pixels (width x height). It's scaled to the monitor's Screen Width.")]
    [SerializeField] private Vector2 referenceSize = new(1600f, 900f);
    [Tooltip("How long the camera takes to zoom in and back, in seconds.")]
    [SerializeField] private float zoomSeconds = 0.35f;

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

        // Close on the key (F and right click too, on a monitor), if the session ended, or if the store
        // stopped being this player's.
        var nm = NetworkManager.Singleton;
        bool lost = nm == null || !nm.IsListening || CurrentPlot == null || !CurrentPlot.IsAuthorized(nm.LocalClientId);
        bool stepBack = monitor != null && Time.frameCount > openedFrame && !Typing() &&
                        (Input.GetKeyDown(KeyCode.F) || Input.GetMouseButtonDown(1)); // not the F that opened it
        bool closeKeyDown = Input.GetKeyDown(closeKey);
        if (closeKeyDown && closeKey == KeyCode.Escape) UIState.MarkEscapeTaken(); // not the phone too
        if (closeKeyDown || stepBack || lost) Close();
    }

    // Typing in a text box (the search): F is a letter then, not "step back".
    private static bool Typing()
    {
        var es = UnityEngine.EventSystems.EventSystem.current;
        var go = es != null ? es.currentSelectedGameObject : null;
        return go != null && go.TryGetComponent<TMPro.TMP_InputField>(out var field) && field.isFocused;
    }

    /// <summary>Show the shop for a store, on the first tab, over the whole view.</summary>
    public void Open(StorePlot plot) => Open(plot, null, null);

    /// <summary>
    /// Show the shop for a store. With a monitor that has a Screen (and a Shop Canvas), the viewer's
    /// camera zooms in and the shop shows on the monitor; otherwise it's over the whole view.
    /// </summary>
    public void Open(StorePlot plot, ShopMonitor at, Camera viewer)
    {
        if (plot == null) return;
        CurrentPlot = plot;
        IsOpen = true;
        openedFrame = Time.frameCount;
        if (at != null && at.Screen != null && shopCanvas != null && viewer != null) PutOnMonitor(at, viewer);
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
        if (monitor != null) StartZoomBack();
        Closed?.Invoke();
    }

    /* ========================= ON THE MONITOR ========================= */

    private int openedFrame;
    private ShopMonitor monitor;
    private Camera viewCam;
    private Transform camParent;
    private Vector3 camLocalPos;
    private Quaternion camLocalRot;
    private Vector3 zoomFromPos;
    private Quaternion zoomFromRot;
    private float zoomT;
    private bool zoomingBack;

    // How the Canvas was before it went onto a monitor.
    private bool canvasSaved;
    private RenderMode savedMode;
    private Transform savedParent;
    private Vector3 savedLocalPos;
    private Quaternion savedLocalRot;
    private Vector3 savedScale;
    private Vector2 savedSize;
    private Camera savedCamera;

    private void PutOnMonitor(ShopMonitor at, Camera viewer)
    {
        var rect = (RectTransform)shopCanvas.transform;
        if (!canvasSaved)
        {
            savedMode = shopCanvas.renderMode;
            savedParent = rect.parent;
            savedLocalPos = rect.localPosition;
            savedLocalRot = rect.localRotation;
            savedScale = rect.localScale;
            savedSize = rect.sizeDelta;
            savedCamera = shopCanvas.worldCamera;
            canvasSaved = true;
        }

        // The Canvas lies on the screen, facing out of it (a world-space Canvas is seen from its -Z side).
        shopCanvas.renderMode = RenderMode.WorldSpace;
        shopCanvas.worldCamera = viewer; // clicks are worked out from this camera
        rect.SetParent(at.Screen, false);
        rect.localPosition = Vector3.forward * 0.002f;
        rect.localRotation = Quaternion.Euler(0f, 180f, 0f);
        rect.sizeDelta = referenceSize;
        float scale = at.ScreenWidth / Mathf.Max(1f, referenceSize.x);
        rect.localScale = new Vector3(scale, scale, scale);

        // Zoom the camera in from wherever it is. (Still on its way back from last time: it keeps the
        // spot it goes home to.)
        if (monitor == null || viewCam != viewer)
        {
            camParent = viewer.transform.parent;
            camLocalPos = viewer.transform.localPosition;
            camLocalRot = viewer.transform.localRotation;
        }
        monitor = at;
        viewCam = viewer;
        zoomFromPos = viewer.transform.position;
        zoomFromRot = viewer.transform.rotation;
        zoomT = 0f;
        zoomingBack = false;
    }

    /// <summary>
    /// A monitor is going away: if the shop is on it, close the shop, put the Canvas back and the camera
    /// home right now (no zoom).
    /// </summary>
    public void ReleaseMonitor(ShopMonitor going)
    {
        if (going == null || monitor != going) return;
        if (IsOpen)
        {
            IsOpen = false;
            if (root != null) root.SetActive(false);
            UIState.CloseMenu(this);
            Closed?.Invoke();
        }
        RestoreCanvas();
        if (viewCam != null)
        {
            viewCam.transform.localPosition = camLocalPos;
            viewCam.transform.localRotation = camLocalRot;
        }
        monitor = null;
        viewCam = null;
    }

    private void StartZoomBack()
    {
        if (viewCam == null) { RestoreCanvas(); monitor = null; return; }
        zoomFromPos = viewCam.transform.position;
        zoomFromRot = viewCam.transform.rotation;
        zoomT = 0f;
        zoomingBack = true;
    }

    private void RestoreCanvas()
    {
        if (!canvasSaved || shopCanvas == null) return;
        var rect = (RectTransform)shopCanvas.transform;
        rect.SetParent(savedParent, false);
        shopCanvas.renderMode = savedMode;
        shopCanvas.worldCamera = savedCamera;
        rect.localPosition = savedLocalPos;
        rect.localRotation = savedLocalRot;
        rect.localScale = savedScale;
        rect.sizeDelta = savedSize;
    }

    // After the player's own camera code (which stands still while a menu is open).
    private void LateUpdate()
    {
        if (monitor == null || viewCam == null) return;
        zoomT = Mathf.Min(1f, zoomT + Time.unscaledDeltaTime / Mathf.Max(0.01f, zoomSeconds));
        float t = zoomT * zoomT * (3f - 2f * zoomT);

        if (!zoomingBack)
        {
            // The monitor can't move while it's in use, but follow it anyway.
            if (!monitor.TryGetViewPose(viewCam, out Vector3 pos, out Quaternion rot)) { Close(); return; }
            viewCam.transform.SetPositionAndRotation(Vector3.Lerp(zoomFromPos, pos, t), Quaternion.Slerp(zoomFromRot, rot, t));
            return;
        }

        // Back to where the camera sits on the player.
        Vector3 homePos = camParent != null ? camParent.TransformPoint(camLocalPos) : camLocalPos;
        Quaternion homeRot = camParent != null ? camParent.rotation * camLocalRot : camLocalRot;
        viewCam.transform.SetPositionAndRotation(Vector3.Lerp(zoomFromPos, homePos, t), Quaternion.Slerp(zoomFromRot, homeRot, t));
        if (zoomT < 1f) return;
        viewCam.transform.localPosition = camLocalPos;
        viewCam.transform.localRotation = camLocalRot;
        RestoreCanvas();
        monitor = null;
        viewCam = null;
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
