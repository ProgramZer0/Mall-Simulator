using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The phone's Settings app (see PhoneScreen): volume, mouse, field of view, graphics quality, V-Sync,
/// frame cap and fullscreen. Saved on this machine (GameSettings) and applied straight away.
///
/// Setup: an empty panel under the phone's canvas, stretched over the app area, with this on it. The
/// page is built inside it by code (Style sets colours and sizes). Replaces the Settings placeholder
/// (PhoneInfoApp): remove that one.
/// </summary>
public class SettingsApp : MonoBehaviour, IPhoneApp
{
    [SerializeField] private string appName = "Settings";
    [SerializeField] private Sprite icon;
    [SerializeField] private PhoneUI.Style style = new();

    public string AppName => appName;
    public Sprite Icon => icon;
    public int Badge => 0;

    private RectTransform content;
    private float y;
    private readonly List<(TMP_Text label, System.Func<string> text)> live = new();

    private bool tipsReset; // (just this time the app's open)

    public void OnAppOpened()
    {
        tipsReset = false;
        leaveArmed = false;
        if (content == null) content = PhoneUI.ScrollPage((RectTransform)transform);
        Build();
        GameSettings.Changed += Refresh;
    }

    public void OnAppClosed() => GameSettings.Changed -= Refresh;

    private void Build()
    {
        PhoneUI.Clear(content);
        live.Clear();
        y = style.padding;

        MallSection();

        Header("Sound");
        SliderRow("Volume", () => $"{GameSettings.MasterVolume * 100f:0}%", 0f, 1f, GameSettings.MasterVolume, GameSettings.SetMasterVolume);

        Header("Controls");
        SliderRow("Mouse sensitivity", () => $"{GameSettings.MouseSensitivity:0.00}×", 0.1f, 4f, GameSettings.MouseSensitivity, GameSettings.SetMouseSensitivity);
        ButtonRow("Invert mouse Y", () => GameSettings.InvertY ? "On" : "Off", () => GameSettings.SetInvertY(!GameSettings.InvertY));

        Header("Display");
        float fov = GameSettings.FieldOfView > 0f ? GameSettings.FieldOfView : CurrentFov();
        SliderRow("Field of view", () => $"{(GameSettings.FieldOfView > 0f ? GameSettings.FieldOfView : CurrentFov()):0}°", 50f, 110f, fov,
                  v => GameSettings.SetFieldOfView(Mathf.Round(v)));
        ButtonRow("Quality", () => QualityName(GameSettings.Quality), () =>
            GameSettings.SetQuality((GameSettings.Quality + 1) % Mathf.Max(1, QualitySettings.names.Length)));
        ButtonRow("V-Sync", () => GameSettings.VSync ? "On" : "Off", () => GameSettings.SetVSync(!GameSettings.VSync));
        ButtonRow("Max FPS", () => GameSettings.MaxFps > 0 ? GameSettings.MaxFps.ToString() : "No limit", NextFps);
        ButtonRow("Fullscreen", () => GameSettings.Fullscreen ? "On" : "Off", () => GameSettings.SetFullscreen(!GameSettings.Fullscreen));

        Header("Help");
        ButtonRow("First-time tips", () => tipsReset ? "Will show again" : "Reset", () => { HelpTips.ResetSeen(); tipsReset = true; Refresh(); });

        y += style.spacing * 2f;
        PhoneUI.Button(content, "Reset to defaults", y, style.rowHeight, style, () => { GameSettings.ResetAll(); Build(); });
        y += style.rowHeight + style.padding;
        content.sizeDelta = new Vector2(content.sizeDelta.x, y);
    }

    private bool leaveArmed; // Leave was tapped once: tap again to really go

    // How friends join (the host), and leaving.
    private void MallSection()
    {
        var nm = Unity.Netcode.NetworkManager.Singleton;
        var conn = MallConnection.Instance;
        if (nm == null || !nm.IsListening) return;
        var mode = conn != null ? conn.CurrentMode : MallConnection.Mode.Off;
        if (mode == MallConnection.Mode.Solo && conn.InLobby) return; // nothing to leave
        Header("Mall");
        if (mode == MallConnection.Mode.Hosting)
        {
            if (!string.IsNullOrEmpty(conn.JoinCode))
                ButtonRow("Join code", () => conn.JoinCode, () => GUIUtility.systemCopyBuffer = conn.JoinCode);
            else if (conn.Port > 0)
                ButtonRow("Join at", () => $"{MallConnection.LocalAddresses()} : {conn.Port}",
                          () => GUIUtility.systemCopyBuffer = $"{MallConnection.LocalAddresses()}:{conn.Port}");
            ButtonRow("Players", () => $"{nm.ConnectedClientsIds.Count} / {conn.MaxPlayers}", () => { });
        }
        bool lobby = conn != null && conn.InLobby;
        string label = mode == MallConnection.Mode.Solo ? "Back to the main menu"
                     : nm.IsServer ? (lobby ? "Close the lobby" : "End mall for everyone")
                     : lobby ? "Leave the lobby" : "Leave mall";
        var b = PhoneUI.Button(content, label, y, style.rowHeight, style, () =>
        {
            if (!leaveArmed) { leaveArmed = true; Build(); return; }
            leaveArmed = false;
            if (conn != null) conn.Leave(); else nm.Shutdown();
        });
        if (leaveArmed)
        {
            var t = b.GetComponentInChildren<TMPro.TMP_Text>();
            if (t != null) t.text = mode == MallConnection.Mode.Solo ? "Tap again to go" : nm.IsServer ? "Tap again: end it for everyone" : "Tap again to leave";
        }
        y += style.rowHeight + style.spacing;
    }

    private void Header(string text)
    {
        if (y > style.padding) y += style.spacing;
        var t = PhoneUI.Text(content, $"<b>{text}</b>", y, style.rowHeight * 0.8f, style, true, TextAlignmentOptions.BottomLeft);
        t.color = style.accent;
        y += style.rowHeight * 0.8f + style.spacing * 0.5f;
    }

    // A name with its value on the left; a slider under it.
    private void SliderRow(string name, System.Func<string> value, float min, float max, float now, UnityEngine.Events.UnityAction<float> set)
    {
        var label = PhoneUI.Text(content, "", y, style.rowHeight * 0.75f, style, false, TextAlignmentOptions.Left);
        live.Add((label, () => $"{name}  <color=#{ColorUtility.ToHtmlStringRGB(style.dimText)}>{value()}</color>"));
        y += style.rowHeight * 0.75f;
        PhoneUI.Slider(content, y, style, min, max, now, set, style.padding);
        y += style.rowHeight + style.spacing;
        Refresh();
    }

    // A name on the left, a button with its value on the right (click to change).
    private void ButtonRow(string name, System.Func<string> value, UnityEngine.Events.UnityAction onClick)
    {
        PhoneUI.Text(content, name, y, style.rowHeight, style, false, TextAlignmentOptions.Left);
        float width = content.rect.width > 0f ? content.rect.width : 300f;
        var b = PhoneUI.Button(content, "", y, style.rowHeight, style, onClick, width * 0.55f);
        live.Add((b.GetComponentInChildren<TMP_Text>(), value));
        y += style.rowHeight + style.spacing;
        Refresh();
    }

    private void Refresh()
    {
        foreach (var (label, text) in live) if (label != null) label.text = text();
    }

    private void NextFps()
    {
        var choices = GameSettings.FpsChoices;
        int i = System.Array.IndexOf(choices, GameSettings.MaxFps);
        GameSettings.SetMaxFps(choices[(i + 1) % choices.Length]);
    }

    private static string QualityName(int i) =>
        i >= 0 && i < QualitySettings.names.Length ? QualitySettings.names[i] : QualitySettings.names[QualitySettings.GetQualityLevel()];

    private static float CurrentFov()
    {
        var cam = Camera.main;
        return cam != null ? cam.fieldOfView : 60f;
    }
}
