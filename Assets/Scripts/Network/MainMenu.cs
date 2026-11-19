using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// The main menu: your name, Host a mall, Join a mall, Quit. Put it on an empty object in the menu scene
/// (with the NetworkManager prefab, which has MallConnection on it). It builds its own screen; style it
/// with the colors below, or a Background image/sprite.
///
/// Host: Online (join code, when Unity Relay is set up) or Direct IP (a port), and Max Players. Join: a
/// join code, or the host's IP and port. Why you came back to the menu (refused, the host left...) shows
/// at the top.
/// </summary>
public class MainMenu : MonoBehaviour
{
    /// <summary>The menu is up (the old test buttons stay hidden).</summary>
    public static bool IsShowing { get; private set; }

    [SerializeField] private string title = "MALL SIM";
    [SerializeField] private Sprite background;
    [SerializeField] private Color backgroundColor = new(0.08f, 0.09f, 0.12f, 1f);
    [SerializeField] private Color panelColor = new(0.14f, 0.16f, 0.21f, 0.95f);
    [SerializeField] private Color buttonColor = new(0.24f, 0.45f, 0.85f, 1f);
    [SerializeField] private Color textColor = Color.white;
    [SerializeField] private TMP_FontAsset font;

    private RectTransform panel;
    private TMP_Text status;
    private TMP_InputField nameField, portField, maxField, codeField, ipField, joinPortField;
    private bool online;
    private bool busy;

    private void OnEnable() => IsShowing = true;
    private void OnDisable() => IsShowing = false;

    private void Start()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        online = MallConnection.RelayAvailable;
        BuildFrame();
        ShowMain();
        if (!string.IsNullOrEmpty(MallConnection.LastMessage)) SetStatus(MallConnection.LastMessage, true);
        MallConnection.LastMessage = null;
    }

    private static MallConnection Conn => MallConnection.Instance;

    /* ========================= SCREENS ========================= */

    private void ShowMain()
    {
        Clear();
        Label(title, 64, FontStyles.Bold);
        Label("Your name", 22);
        nameField = UIInput(MallConnection.PlayerName, "Type your name", MallConnection.MaxNameLength, TMP_InputField.ContentType.Standard);
        nameField.onEndEdit.AddListener(v => MallConnection.PlayerName = v);
        Gap();
        UIButton("Host a mall", ShowHost);
        UIButton("Join a mall", ShowJoin);
        UIButton("Quit", Quit);
    }

    private void ShowHost()
    {
        if (!SaveName()) return;
        Clear();
        Label("Host a mall", 44, FontStyles.Bold);
        if (MallConnection.RelayAvailable)
        {
            UIButton(online ? "Online: friends join with a code ✓" : "Online: friends join with a code", () => { online = true; ShowHost(); });
            UIButton(!online ? "Direct IP: friends type your address ✓" : "Direct IP: friends type your address", () => { online = false; ShowHost(); });
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
        UIButton("Start", StartHost);
        UIButton("Back", ShowMain);
    }

    private void ShowJoin()
    {
        if (!SaveName()) return;
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
        if (nameField != null) MallConnection.PlayerName = nameField.text;
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

    private void JoinIp()
    {
        if (busy || Conn == null) { if (Conn == null) SetStatus("No MallConnection on the NetworkManager.", true); return; }
        string ip = ipField != null ? ipField.text : "";
        int port = Mathf.Clamp(ParseInt(joinPortField, Conn.DefaultPort), 1, 65535);
        PlayerPrefs.SetString("MallSim.LastIp", ip);
        PlayerPrefs.SetString("MallSim.LastPort", port.ToString());
        PlayerPrefs.Save();
        string error = Conn.JoinDirect(ip, (ushort)port);
        SetStatus(error ?? $"Connecting to {ip}:{port}...", error != null);
    }

    private static int ParseInt(TMP_InputField f, int fallback) => f != null && int.TryParse(f.text, out int v) ? v : fallback;

    private static void Quit()
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
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;

        var bg = Rect("Background", canvasGo.transform);
        Stretch(bg);
        var bgImage = bg.gameObject.AddComponent<Image>();
        bgImage.color = background != null ? Color.white : backgroundColor;
        bgImage.sprite = background;
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

    private void UIButton(string text, UnityEngine.Events.UnityAction onClick)
    {
        var r = Rect("Button", panel);
        r.gameObject.AddComponent<LayoutElement>().minHeight = 58f;
        var img = r.gameObject.AddComponent<Image>();
        img.color = buttonColor;
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
