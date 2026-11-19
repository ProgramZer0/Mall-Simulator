using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;

/// <summary>
/// A sign in the lobby: how friends join (the join code or your address), who's here, and why you came
/// back to the menu (refused, the host left...). Put it on an object with a TMP text (a world-space
/// TextMeshPro, or a TMP text on a world-space canvas), or point Text at one.
/// </summary>
public class LobbySign : MonoBehaviour
{
    [SerializeField] private TMP_Text text;
    [Tooltip("Shown when not in a lobby (playing alone).")]
    [SerializeField] private string soloText = "Join / Host";
    [Tooltip("Messages (why you came back here) stay up this long (seconds).")]
    [SerializeField, Min(0f)] private float messageSeconds = 20f;
    [Tooltip("List everyone's names.")]
    [SerializeField] private bool listPlayers = true;
    [SerializeField, Min(0.05f)] private float refreshSeconds = 0.5f;

    private float nextRefresh;
    private string shown;
    private readonly StringBuilder sb = new();
    private readonly List<string> names = new();

    private void Awake()
    {
        if (text == null) text = GetComponentInChildren<TMP_Text>(true);
    }

    private void Update()
    {
        if (text == null || Time.unscaledTime < nextRefresh) return;
        nextRefresh = Time.unscaledTime + refreshSeconds;
        string now = Build();
        if (now != shown) text.text = shown = now;
    }

    private string Build()
    {
        sb.Clear();
        var c = MallConnection.Instance;
        var nm = Unity.Netcode.NetworkManager.Singleton;
        if (!string.IsNullOrEmpty(MallConnection.LastMessage) && Time.unscaledTime - MallConnection.LastMessageAt < messageSeconds)
            sb.Append("<color=#ff9a8a>").Append(MallConnection.LastMessage).Append("</color>\n\n");
        if (c == null || nm == null) return sb.ToString();
        switch (c.CurrentMode)
        {
            case MallConnection.Mode.Off: sb.Append("Starting..."); break;
            case MallConnection.Mode.Solo: sb.Append(soloText); break;
            case MallConnection.Mode.Hosting:
                if (!string.IsNullOrEmpty(c.JoinCode)) sb.Append("Join code: <b>").Append(c.JoinCode).Append("</b>");
                else if (c.Port > 0) sb.Append("Join at: <b>").Append(MallConnection.LocalAddresses()).Append(" : ").Append(c.Port).Append("</b>");
                else sb.Append("Opening the lobby...");
                sb.Append("\nPlayers: ").Append(nm.ConnectedClientsIds.Count).Append(" / ").Append(c.MaxPlayers);
                break;
            case MallConnection.Mode.Client:
                sb.Append(nm.IsConnectedClient ? "In a friend's lobby\nWaiting for the host to start" : "Connecting...");
                break;
        }
        if (listPlayers && c.CurrentMode != MallConnection.Mode.Solo && c.CurrentMode != MallConnection.Mode.Off)
        {
            names.Clear();
            foreach (var tag in FindObjectsByType<PlayerNameTag>(FindObjectsSortMode.None))
                if (tag.IsSpawned) names.Add(tag.PlayerName.Length > 0 ? tag.PlayerName : PlayerNameTag.NameOf(tag.OwnerClientId));
            names.Sort();
            if (names.Count > 0) sb.Append("\n\n").Append(string.Join("\n", names));
        }
        return sb.ToString();
    }
}
