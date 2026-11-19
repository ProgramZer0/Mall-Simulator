using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// The lobby terminal screen: the typing part of the main menu (your name, hosting for friends, joining
/// one). The lobby itself is the main menu scene you walk around in (see MallConnection and LobbyAction):
/// this screen comes up when a LobbyAction door or button opens it (Host, Join, Change Name), and Esc or
/// Close puts it away. Put it on an empty object in the menu scene (with the NetworkManager prefab, which
/// has MallConnection on it). It builds its own screen; style it with the colors below, or a Background
/// image/sprite.
///
/// Host: Online (join code, when Unity Relay is set up) or Direct IP (a port), and Max Players. Join: a
/// join code, or the host's IP and port. Why you came back to the menu (refused, the host left...) shows
/// at the top.
/// </summary>
public class MainMenu : MonoBehaviour
{
    /// <summary>A screen of the terminal.</summary>
    public enum Page { Main, Host, Join, Name }

    /// <summary>In the menu scene (the old test buttons stay hidden).</summary>
    public static bool IsShowing { get; private set; }
    /// <summary>The terminal screen is up.</summary>
    public static bool IsOpen => instance != null && instance.canvasRoot != null && instance.canvasRoot.activeSelf && !instance.zoomingBack;
    private static MainMenu instance;

    [SerializeField] private string title = "MALL SIM";
    [SerializeField] private Sprite background;
    [SerializeField] private Color backgroundColor = new(0.08f, 0.09f, 0.12f, 1f);
    [Tooltip("Off: the lobby shows through, dimmed (Dim Color). On: Background / Background Color cover the screen.")]
    [SerializeField] private bool coverScreen;
    [SerializeField] private Color dimColor = new(0f, 0f, 0f, 0.45f);
    [Tooltip("Messages (why you came back to the menu) show when the screen opens, this long after (seconds).")]
    [SerializeField, Min(0f)] private float messageSeconds = 30f;
    [SerializeField] private Color panelColor = new(0.14f, 0.16f, 0.21f, 0.95f);
    [SerializeField] private Color buttonColor = new(0.24f, 0.45f, 0.85f, 1f);
    [Tooltip("Color of the chosen option (Online or Direct IP) on the host screen.")]
    [SerializeField] private Color selectedButtonColor = new(0.2f, 0.7f, 0.35f, 1f);
    [SerializeField] private Color textColor = Color.white;
    [SerializeField] private TMP_FontAsset font;
    [Header("On an office computer")]
    [Tooltip("The screen's size on a computer, in UI pixels (width x height). It's scaled to the computer's Screen Width.")]
    [SerializeField] private Vector2 referenceSize = new(1920f, 1080f);
    [Tooltip("How long the camera takes to zoom in and back, in seconds.")]
    [SerializeField, Min(0.01f)] private float zoomSeconds = 0.35f;

    private RectTransform panel;
    private GameObject canvasRoot;
    private TMP_Text status;
    private TMP_InputField nameField, portField, maxField, codeField, ipField, joinPortField;
    private bool online;
    private bool busy;

    private void OnEnable()
    {
        IsShowing = true;
        instance = this;
    }

    private void OnDisable()
    {
        IsShowing = false;
        Close();
        if (instance == this) instance = null;
    }

    private void Start()
    {
        online = MallConnection.RelayAvailable;
        if (canvasRoot == null) BuildFrame();
        canvasRoot.SetActive(false);
    }

    private void Update()
    {
        if (!IsOpen) return;
        if (UIState.TakeEscape()) { Close(); return; }
        // On a computer: F (when not typing) or right click steps back, like the shop monitor.
        if (computer != null && Time.frameCount > openedFrame && !Typing() &&
            (Input.GetKeyDown(KeyCode.F) || Input.GetMouseButtonDown(1)))
            Close();
    }

    private static bool Typing()
    {
        var es = EventSystem.current;
        var go = es != null ? es.currentSelectedGameObject : null;
        return go != null && go.TryGetComponent<TMP_InputField>(out var field) && field.isFocused;
    }

    /// <summary>Bring the screen up on an office computer (the camera zooms in on it).</summary>
    public static void OpenOn(OfficeComputer at)
    {
        if (instance == null)
        {
            Debug.LogWarning("[MainMenu] No MainMenu in this scene for the computer's screen.");
            return;
        }
        var cam = Camera.main;
        if (at == null || at.Screen == null || cam == null) { instance.Show(Page.Main); return; }
        if (instance.canvasRoot == null) instance.BuildFrame();
        instance.PutOnComputer(at, cam);
        instance.Show(Page.Main);
    }

    /// <summary>Bring the terminal screen up on a page (from a LobbyAction).</summary>
    public static void Open(Page page)
    {
        if (instance == null)
        {
            Debug.LogWarning("[MainMenu] No MainMenu in this scene for the terminal screen.");
            return;
        }
        instance.Show(page);
    }

    private void Show(Page page)
    {
        if (canvasRoot == null) BuildFrame();
        canvasRoot.SetActive(true);
        openedFrame = Time.frameCount;
        if (bgImage != null)
        {
            bool cover = coverScreen || computer != null;
            bgImage.color = !cover ? dimColor : background != null ? Color.white : backgroundColor;
            bgImage.sprite = cover ? background : null;
        }
        UIState.OpenMenu(this);
        busy = false;
        switch (page)
        {
            case Page.Host: ShowHost(); break;
            case Page.Join: ShowJoin(); break;
            case Page.Name: ShowName(); break;
            default: ShowMain(); break;
        }
        if (!string.IsNullOrEmpty(MallConnection.LastMessage) && Time.unscaledTime - MallConnection.LastMessageAt < messageSeconds)
            SetStatus(MallConnection.LastMessage, true);
    }

    /// <summary>Put the terminal screen away.</summary>
    public void Close()
    {
        if (canvasRoot != null && computer == null) canvasRoot.SetActive(false);
        if (computer != null) StartZoomBack(); // the screen goes off once the camera's back
        UIState.CloseMenu(this);
    }

    /* ========================= ON A COMPUTER ========================= */

    private int openedFrame;
    private Image bgImage;
    private OfficeComputer computer;
    private Camera viewCam;
    private Transform camParent;
    private Vector3 camLocalPos, zoomFromPos;
    private Quaternion camLocalRot, zoomFromRot;
    private float zoomT;
    private bool zoomingBack;

    private void PutOnComputer(OfficeComputer at, Camera viewer)
    {
        var canvas = canvasRoot.GetComponent<Canvas>();
        var rect = (RectTransform)canvasRoot.transform;
        canvasRoot.GetComponent<CanvasScaler>().enabled = false;
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = viewer; // clicks are worked out from this camera
        rect.SetParent(at.Screen, false);
        rect.localPosition = Vector3.forward * 0.002f;
        rect.localRotation = Quaternion.Euler(0f, 180f, 0f); // a world-space Canvas is seen from its -Z side
        rect.sizeDelta = referenceSize;
        float scale = at.ScreenWidth / Mathf.Max(1f, referenceSize.x);
        rect.localScale = new Vector3(scale, scale, scale);

        if (computer == null || viewCam != viewer)
        {
            camParent = viewer.transform.parent;
            camLocalPos = viewer.transform.localPosition;
            camLocalRot = viewer.transform.localRotation;
        }
        computer = at;
        viewCam = viewer;
        zoomFromPos = viewer.transform.position;
        zoomFromRot = viewer.transform.rotation;
        zoomT = 0f;
        zoomingBack = false;
    }

    private void StartZoomBack()
    {
        if (zoomingBack) return;
        if (viewCam == null) { TakeOffComputer(); return; }
        zoomFromPos = viewCam.transform.position;
        zoomFromRot = viewCam.transform.rotation;
        zoomT = 0f;
        zoomingBack = true;
    }

    private void TakeOffComputer()
    {
        if (canvasRoot != null)
        {
            canvasRoot.SetActive(false);
            var canvas = canvasRoot.GetComponent<Canvas>();
            var rect = (RectTransform)canvasRoot.transform;
            rect.SetParent(transform, false);
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.worldCamera = null;
            rect.localScale = Vector3.one;
            rect.localRotation = Quaternion.identity;
            canvasRoot.GetComponent<CanvasScaler>().enabled = true;
        }
        if (viewCam != null)
        {
            viewCam.transform.localPosition = camLocalPos;
            viewCam.transform.localRotation = camLocalRot;
        }
        computer = null;
        viewCam = null;
        zoomingBack = false;
    }

    // After the player's own camera code (which stands still while a menu is open).
    private void LateUpdate()
    {
        if (computer == null) return;
        if (viewCam == null) { TakeOffComputer(); return; }
        zoomT = Mathf.Min(1f, zoomT + Time.unscaledDeltaTime / zoomSeconds);
        float t = zoomT * zoomT * (3f - 2f * zoomT);
        if (!zoomingBack)
        {
            if (!computer.TryGetViewPose(viewCam, out Vector3 pos, out Quaternion rot)) { Close(); return; }
            viewCam.transform.SetPositionAndRotation(Vector3.Lerp(zoomFromPos, pos, t), Quaternion.Slerp(zoomFromRot, rot, t));
            return;
        }
        Vector3 homePos = camParent != null ? camParent.TransformPoint(camLocalPos) : camLocalPos;
        Quaternion homeRot = camParent != null ? camParent.rotation * camLocalRot : camLocalRot;
        viewCam.transform.SetPositionAndRotation(Vector3.Lerp(zoomFromPos, homePos, t), Quaternion.Slerp(zoomFromRot, homeRot, t));
        if (zoomT >= 1f) TakeOffComputer();
    }

    private static bool Solo => Conn != null && Conn.CurrentMode == MallConnection.Mode.Solo;

    private static MallConnection Conn => MallConnection.Instance;

    /* ========================= SCREENS ========================= */

    private void ShowMain()
    {
        Clear();
        Label(title, 64, FontStyles.Bold);
        NameInput();
        Gap();
        var mode = Conn != null ? Conn.CurrentMode : MallConnection.Mode.Off;
        if (mode == MallConnection.Mode.Solo)
        {
            UIButton("Host a lobby", ShowHost);
            UIButton("Join a friend", ShowJoin);
        }
        else if (mode == MallConnection.Mode.Hosting)
        {
            var nm = Unity.Netcode.NetworkManager.Singleton;
            Label(!string.IsNullOrEmpty(Conn.JoinCode) ? $"Join code: <b>{Conn.JoinCode}</b>"
                  : $"Join at: <b>{MallConnection.LocalAddresses()} : {Conn.Port}</b>", 26);
            if (nm != null) Label($"Players: {nm.ConnectedClientsIds.Count} / {Conn.MaxPlayers}", 22);
            if (!string.IsNullOrEmpty(Conn.JoinCode)) UIButton("Copy the code", () => GUIUtility.systemCopyBuffer = Conn.JoinCode);
            UIButton("Close the lobby (play alone)", () => { Close(); Conn.Leave(); });
        }
        else if (mode == MallConnection.Mode.Client)
        {
            Label("In a friend's lobby. The host opens the mall.", 22);
            UIButton("Leave the lobby", () => { Close(); Conn.Leave(); });
        }
        UIButton(computer != null ? "Step back" : "Close", Close);
        if (computer == null) UIButton("Quit", Quit);
    }

    private void ShowName()
    {
        Clear();
        NameInput();
        Gap();
        UIButton("Save", () => { if (SaveName()) Close(); });
        UIButton("Close", Close);
    }

    private void NameInput()
    {
        Label("Your name", 22);
        nameField = UIInput(MallConnection.PlayerName, "Type your name", MallConnection.MaxNameLength, TMP_InputField.ContentType.Standard);
        nameField.onEndEdit.AddListener(v => SaveName());
    }

    private void ShowHost()
    {
        if (!SaveName()) return;
        if (!Solo) { ShowMain(); return; }
        Clear();
        Label("Host a mall", 44, FontStyles.Bold);
        if (MallConnection.RelayAvailable)
        {
            UIButton(online ? "> Online: friends join with a code <" : "Online: friends join with a code", () => { online = true; ShowHost(); }, online);
            UIButton(!online ? "> Direct IP: friends type your address <" : "Direct IP: friends type your address", () => { online = false; ShowHost(); }, !online);
        }
        else online = false;
        if (!online)
        {
            Label("Port", 22);
            portField = UIInput((Conn != null ? Conn.DefaultPort : (ushort)7777).ToString(), "7777", 5, TMP_InputField.ContentType.IntegerNumber);
            Label($"Friends on your network join with {MallConnection.LocalAddresses()}. Over the internet, forward this port on your router.", 18);
        }
        Label($"Max players (1-{MallConnection.MaxPlayersLimit})", 22);
        maxField = UIInput((Conn != null ? Conn.DefaultMaxPlayers : 8).ToString(), "8", 2, TMP_InputField.ContentType.IntegerNumber);
        Gap();
        UIButton("Open the lobby", StartHost);
        UIButton("Back", ShowMain);
    }

    private void ShowJoin()
    {
        if (!SaveName()) return;
        if (!Solo) { ShowMain(); return; }
        Clear();
        Label("Join a mall", 44, FontStyles.Bold);
        if (MallConnection.RelayAvailable)
        {
            Label("Join code", 22);
            codeField = UIInput("", "ABC123", 8, TMP_InputField.ContentType.Alphanumeric);
            UIButton("Join with code", JoinCode);
            Gap();
            Label("...or by IP address", 22);
        }
        else Label("Host's IP address", 22);
        ipField = UIInput(PlayerPrefs.GetString("MallSim.LastIp", "127.0.0.1"), "192.168.1.20", 64, TMP_InputField.ContentType.Standard);
        Label("Port", 22);
        joinPortField = UIInput(PlayerPrefs.GetString("MallSim.LastPort", (Conn != null ? Conn.DefaultPort : (ushort)7777).ToString()),
                              "7777", 5, TMP_InputField.ContentType.IntegerNumber);
        UIButton("Join by IP", JoinIp);
        Gap();
        UIButton("Back", ShowMain);
    }

    private bool SaveName()
    {
        if (nameField != null && MallConnection.CleanName(nameField.text) != MallConnection.PlayerName)
        {
            MallConnection.PlayerName = nameField.text;
            if (PlayerNameTag.Local != null && MallConnection.PlayerName.Length > 0) PlayerNameTag.Local.Rename(MallConnection.PlayerName);
        }
        if (MallConnection.PlayerName.Length > 0) return true;
        SetStatus("Type your name first.", true);
        return false;
    }

    /* ========================= ACTIONS ========================= */

    private async void StartHost()
    {
        if (busy || Conn == null) { if (Conn == null) SetStatus("No MallConnection on the NetworkManager.", true); return; }
        int max = Mathf.Clamp(ParseInt(maxField, 8), 1, MallConnection.MaxPlayersLimit);
        busy = true;
        SetStatus("Starting the mall...", false);
        string error;
#if MALLSIM_RELAY
        if (online) error = await Conn.HostOnline(max);
        else
#endif
            error = Conn.HostDirect((ushort)Mathf.Clamp(ParseInt(portField, Conn.DefaultPort), 1, 65535), max);
        await System.Threading.Tasks.Task.Yield();
        busy = false;
        if (error != null) SetStatus(error, true);
        // (The lobby reloads open for friends; the screen goes with it.)
    }

    private async void JoinCode()
    {
#if MALLSIM_RELAY
        if (busy || Conn == null) return;
        busy = true;
        SetStatus("Joining...", false);
        string error = await Conn.JoinOnline(codeField != null ? codeField.text : "");
        busy = false;
        if (error != null) SetStatus(error, true);
        else SetStatus("Connecting...", false);
#else
        await System.Threading.Tasks.Task.Yield();
#endif
    }

    private async void JoinIp()
    {
        if (busy || Conn == null) { if (Conn == null) SetStatus("No MallConnection on the NetworkManager.", true); return; }
        string ip = ipField != null ? ipField.text : "";
        int port = Mathf.Clamp(ParseInt(joinPortField, Conn.DefaultPort), 1, 65535);
        PlayerPrefs.SetString("MallSim.LastIp", ip);
        PlayerPrefs.SetString("MallSim.LastPort", port.ToString());
        PlayerPrefs.Save();
        busy = true;
        SetStatus($"Connecting to {ip}:{port}...", false);
        string error = await Conn.JoinDirect(ip, (ushort)port);
        busy = false;
        if (this == null) return;
        if (error != null) SetStatus(error, true);
    }

    private static int ParseInt(TMP_InputField f, int fallback) => f != null && int.TryParse(f.text, out int v) ? v : fallback;

    public static void Quit()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    private void SetStatus(string text, bool error)
    {
        if (status == null) return;
        status.text = text;
        status.color = error ? new Color(1f, 0.55f, 0.5f) : textColor;
    }

    /* ========================= BUILDING THE SCREEN ========================= */

    private void BuildFrame()
    {
        if (FindAnyObjectByType<EventSystem>() == null)
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));

        var canvasGo = new GameObject("Main Menu", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGo.transform.SetParent(transform, false);
        canvasRoot = canvasGo;
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;

        var bg = Rect("Background", canvasGo.transform);
        Stretch(bg);
        bgImage = bg.gameObject.AddComponent<Image>();
        bgImage.color = !coverScreen ? dimColor : background != null ? Color.white : backgroundColor;
        bgImage.sprite = coverScreen ? background : null;
        bgImage.preserveAspect = false;

        status = Text(canvasGo.transform, "", 24, FontStyles.Normal);
        var st = status.rectTransform;
        st.anchorMin = new Vector2(0.5f, 1f); st.anchorMax = new Vector2(0.5f, 1f);
        st.pivot = new Vector2(0.5f, 1f);
        st.anchoredPosition = new Vector2(0f, -30f);
        st.sizeDelta = new Vector2(1200f, 80f);

        panel = Rect("Panel", canvasGo.transform);
        panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0.5f, 0.5f);
        panel.sizeDelta = new Vector2(620f, 0f);
        panel.gameObject.AddComponent<Image>().color = panelColor;
        var layout = panel.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(40, 40, 36, 36);
        layout.spacing = 12f;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        panel.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
    }

    private void Clear()
    {
        for (int i = panel.childCount - 1; i >= 0; i--) Destroy(panel.GetChild(i).gameObject);
        codeField = ipField = joinPortField = portField = maxField = null;
        if (status != null && !busy) status.text = "";
    }

    private static RectTransform Rect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }

    private static void Stretch(RectTransform r)
    {
        r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
        r.offsetMin = r.offsetMax = Vector2.zero;
    }

    private TMP_Text Text(Transform parent, string text, float size, FontStyles style)
    {
        var r = Rect("Text", parent);
        var t = r.gameObject.AddComponent<TextMeshProUGUI>();
        if (font != null) t.font = font;
        t.text = text;
        t.fontSize = size;
        t.fontStyle = style;
        t.color = textColor;
        t.alignment = TextAlignmentOptions.Center;
        t.textWrappingMode = TextWrappingModes.Normal;
        return t;
    }

    private void Label(string text, float size, FontStyles style = FontStyles.Normal)
    {
        var t = Text(panel, text, size, style);
        t.gameObject.AddComponent<LayoutElement>().minHeight = size * 1.3f;
    }

    private void Gap() => Rect("Gap", panel).gameObject.AddComponent<LayoutElement>().minHeight = 10f;

    private void UIButton(string text, UnityEngine.Events.UnityAction onClick, bool selected = false)
    {
        var r = Rect("Button", panel);
        r.gameObject.AddComponent<LayoutElement>().minHeight = 58f;
        var img = r.gameObject.AddComponent<Image>();
        img.color = selected ? selectedButtonColor : buttonColor;
        var b = r.gameObject.AddComponent<Button>();
        b.targetGraphic = img;
        b.onClick.AddListener(onClick);
        var t = Text(r, text, 26, FontStyles.Bold);
        Stretch(t.rectTransform);
    }

    private TMP_InputField UIInput(string value, string placeholder, int limit, TMP_InputField.ContentType type)
    {
        var r = Rect("Input", panel);
        r.gameObject.AddComponent<LayoutElement>().minHeight = 52f;
        var img = r.gameObject.AddComponent<Image>();
        img.color = new Color(1f, 1f, 1f, 0.92f);
        var area = Rect("Text Area", r);
        Stretch(area);
        area.offsetMin = new Vector2(14f, 6f); area.offsetMax = new Vector2(-14f, -6f);
        area.gameObject.AddComponent<RectMask2D>();
        var text = Text(area, "", 26, FontStyles.Normal);
        text.color = new Color(0.1f, 0.1f, 0.12f);
        text.alignment = TextAlignmentOptions.MidlineLeft;
        Stretch(text.rectTransform);
        var ph = Text(area, placeholder, 26, FontStyles.Italic);
        ph.color = new Color(0.1f, 0.1f, 0.12f, 0.4f);
        ph.alignment = TextAlignmentOptions.MidlineLeft;
        Stretch(ph.rectTransform);
        var field = r.gameObject.AddComponent<TMP_InputField>();
        field.textViewport = area;
        field.textComponent = text;
        field.placeholder = ph;
        field.characterLimit = limit;
        field.contentType = type;
        if (font != null) field.fontAsset = font;
        field.text = value;
        return field;
    }
}
