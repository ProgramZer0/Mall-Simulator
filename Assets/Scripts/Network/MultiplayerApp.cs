using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

/// <summary>
/// The phone's Multiplayer app: how friends join (the join code or address, tap to copy), who's here
/// (name, and their ping for the host), and leaving: back to the main menu, leave the mall or lobby, or
/// end it for everyone (tap twice). It used to be the Mall part of Settings.
///
/// Setup: like the Help app (duplicate that app object under the phone prefab's screen and swap HelpApp
/// for this). To have it in the office too, add "Multiplayer" to PhoneScreen's Office Apps.
/// </summary>
public class MultiplayerApp : MonoBehaviour, IPhoneApp
{
    [SerializeField] private string appName = "Multiplayer";
    [SerializeField] private Sprite icon;
    [SerializeField] private PhoneUI.Style style = new();
    [Tooltip("The players list (and pings) refresh this often while the app is open (seconds).")]
    [SerializeField, Min(0.2f)] private float refreshEvery = 1f;

    public string AppName => appName;
    public Sprite Icon => icon;
    public int Badge => 0;

    private RectTransform content;
    private float y;
    private bool leaveArmed;
    private float nextRefresh;
    private string lastShape; // rebuilt when who's here changes

    public void OnAppOpened()
    {
        leaveArmed = false;
        if (content == null) content = PhoneUI.ScrollPage((RectTransform)transform);
        Build();
    }

    public void OnAppClosed() => leaveArmed = false;

    private void Update()
    {
        if (content == null || Time.unscaledTime < nextRefresh) return;
        nextRefresh = Time.unscaledTime + refreshEvery;
        Build();
    }

    private void Build()
    {
        nextRefresh = Time.unscaledTime + refreshEvery;
        PhoneUI.Clear(content);
        y = style.padding;

        var nm = NetworkManager.Singleton;
        var conn = MallConnection.Instance;
        if (nm == null || !nm.IsListening)
        {
            Line("Not connected.");
            Finish();
            return;
        }
        var mode = conn != null ? conn.CurrentMode : MallConnection.Mode.Off;
        bool lobby = conn != null && conn.InLobby;

        if (mode == MallConnection.Mode.Solo)
        {
            Header("Playing alone");
            Line(lobby ? "To play with friends, host from an office computer. Friends join from theirs with your code."
                       : "This mall is single player. To play with friends, host from an office computer.");
        }
        else if (mode == MallConnection.Mode.Hosting)
        {
            Header(lobby ? "Your lobby" : "Your mall");
            if (!string.IsNullOrEmpty(conn.JoinCode))
                Row("Join code", conn.JoinCode, () => GUIUtility.systemCopyBuffer = conn.JoinCode);
            else if (conn.Port > 0)
                Row("Join at", $"{MallConnection.LocalAddresses()} : {conn.Port}",
                    () => GUIUtility.systemCopyBuffer = $"{MallConnection.LocalAddresses()}:{conn.Port}");
            Line("<size=80%>Tap to copy.</size>");
        }
        else Header(lobby ? "In a friend's lobby" : "In a friend's mall");

        if (mode != MallConnection.Mode.Solo) Players(nm, conn);

        if (!(mode == MallConnection.Mode.Solo && lobby)) LeaveButton(nm, conn, mode, lobby);
        Finish();
    }

    private void Players(NetworkManager nm, MallConnection conn)
    {
        var utp = nm.NetworkConfig.NetworkTransport as UnityTransport;
        var ids = new List<ulong>();
        if (nm.IsServer) ids.AddRange(nm.ConnectedClientsIds);
        else if (nm.SpawnManager != null)
            foreach (var o in nm.SpawnManager.SpawnedObjectsList) if (o.IsPlayerObject) ids.Add(o.OwnerClientId);
        ids.Sort();
        Header(conn != null && conn.MaxPlayers > 0 ? $"Players  {ids.Count} / {conn.MaxPlayers}" : $"Players  {ids.Count}");
        foreach (ulong id in ids)
        {
            string name = PlayerNameTag.NameOf(id);
            if (string.IsNullOrEmpty(name)) name = $"Player {id}";
            string tags = "";
            if (id == NetworkManager.ServerClientId) tags += " (host)";
            if (id == nm.LocalClientId) tags += " (you)";
            string ping = "";
            if (utp != null && id != nm.LocalClientId)
            {
                if (nm.IsServer) ping = $"{utp.GetCurrentRtt(id)} ms";
                else if (id == NetworkManager.ServerClientId) ping = $"{utp.GetCurrentRtt(NetworkManager.ServerClientId)} ms";
            }
            Row(name + tags, ping, null);
        }
    }

    private void LeaveButton(NetworkManager nm, MallConnection conn, MallConnection.Mode mode, bool lobby)
    {
        y += style.spacing * 2f;
        string label = mode == MallConnection.Mode.Solo ? "Back to the main menu"
                     : nm.IsServer ? (lobby ? "Close the lobby" : "End mall for everyone")
                     : lobby ? "Leave the lobby" : "Leave mall";
        if (leaveArmed)
            label = mode == MallConnection.Mode.Solo ? "Tap again to go" : nm.IsServer ? "Tap again: end it for everyone" : "Tap again to leave";
        PhoneUI.Button(content, label, y, style.rowHeight, style, () =>
        {
            if (!leaveArmed) { leaveArmed = true; Build(); return; }
            leaveArmed = false;
            if (conn != null) conn.Leave(); else nm.Shutdown();
        });
        y += style.rowHeight + style.spacing;
    }

    private void Header(string text)
    {
        if (y > style.padding) y += style.spacing;
        var t = PhoneUI.Text(content, $"<b>{text}</b>", y, style.rowHeight * 0.8f, style, true, TextAlignmentOptions.BottomLeft);
        t.color = style.accent;
        y += style.rowHeight * 0.8f + style.spacing * 0.5f;
    }

    private void Line(string text)
    {
        PhoneUI.Text(content, text, y, style.rowHeight * 1.4f, style, true, TextAlignmentOptions.TopLeft);
        y += style.rowHeight * 1.4f + style.spacing * 0.5f;
    }

    // A name on the left, its value on the right (a button to tap when there's something to do).
    private void Row(string name, string value, UnityEngine.Events.UnityAction onClick)
    {
        PhoneUI.Text(content, name, y, style.rowHeight, style, false, TextAlignmentOptions.Left);
        if (onClick != null)
        {
            float width = content.rect.width > 0f ? content.rect.width : 300f;
            var b = PhoneUI.Button(content, value, y, style.rowHeight, style, onClick, width * 0.55f);
            b.GetComponentInChildren<TMP_Text>().text = value;
        }
        else if (!string.IsNullOrEmpty(value))
        {
            var t = PhoneUI.Text(content, value, y, style.rowHeight, style, false, TextAlignmentOptions.Right);
            t.color = style.dimText;
        }
        y += style.rowHeight + style.spacing;
    }

    private void Finish()
    {
        y += style.padding;
        content.sizeDelta = new Vector2(content.sizeDelta.x, y);
    }
}
