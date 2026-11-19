using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// A door or button in the lobby (the main menu scene) that does something with F. Put it on (or above)
/// the object's collider and pick what it does:
/// - New Mall: the host opens a new mall for everyone in the lobby (alone: single player).
/// - Load Save: the same, with Save Slot (see MallConnection.PendingSave; for the save system).
/// - Start Game: like New Mall, but only there while hosting for friends (the "start" button).
/// - Host: the terminal screen to host for friends (online code or IP). Only while playing alone.
/// - Join: the terminal screen to join a friend (code or IP). Only while playing alone.
/// - Leave Lobby: the host closes the lobby (back to alone), a friend leaves it.
/// - Change Name: the terminal screen with your name.
/// - Quit: quits the game.
///
/// Only When Usable: objects shown only while it does something for you (e.g. the start button only
/// while hosting). On Used: hook for a sound or animation (door opening...).
/// </summary>
public class LobbyAction : MonoBehaviour, IPlayerUsable
{
    public enum Kind { NewMall, LoadSave, StartGame, Host, Join, LeaveLobby, ChangeName, Quit }

    [SerializeField] private Kind action;
    [Tooltip("Load Save: the save this opens.")]
    [SerializeField] private string saveSlot = "1";
    [Tooltip("Optional: the prompt instead of the default (e.g. \"F: Open a new mall\").")]
    [SerializeField] private string promptOverride;
    [Tooltip("Shown only while this does something for you (e.g. the Start button only while hosting).")]
    [SerializeField] private GameObject[] onlyWhenUsable;
    [Tooltip("Played when it's used (a sound, a door opening animation...).")]
    [SerializeField] private UnityEvent onUsed;

    private static MallConnection Conn => MallConnection.Instance;

    private static int PlayerCount()
    {
        var nm = Unity.Netcode.NetworkManager.Singleton;
        return nm != null && nm.IsServer ? nm.ConnectedClientsIds.Count : FindObjectsByType<PlayerNameTag>(FindObjectsSortMode.None).Length;
    }

    /// <summary>What it does for this machine right now (null: nothing).</summary>
    private string DefaultPrompt()
    {
        var c = Conn;
        if (c == null) return null;
        var mode = c.CurrentMode;
        bool host = mode == MallConnection.Mode.Solo || mode == MallConnection.Mode.Hosting;
        switch (action)
        {
            case Kind.NewMall:
            case Kind.LoadSave:
            case Kind.StartGame:
                if (!c.InLobby) return null;
                if (action == Kind.StartGame && mode != MallConnection.Mode.Hosting && mode != MallConnection.Mode.Client) return null;
                if (mode == MallConnection.Mode.Client) return action == Kind.StartGame ? "Waiting for the host to start the mall" : null;
                if (!host) return null;
                string what = action == Kind.LoadSave ? $"Load save {saveSlot}" : action == Kind.NewMall ? "Open a new mall" : "Start the mall";
                return mode == MallConnection.Mode.Solo ? $"F: {what}" : $"F: {what} for everyone ({PlayerCount()} players)";
            case Kind.Host:
                return mode == MallConnection.Mode.Solo && c.InLobby ? "F: Host a mall for friends" : null;
            case Kind.Join:
                return mode == MallConnection.Mode.Solo && c.InLobby ? "F: Join a friend's mall" : null;
            case Kind.LeaveLobby:
                if (!c.InLobby) return null;
                return mode == MallConnection.Mode.Hosting ? "F: Close the lobby (play alone)"
                     : mode == MallConnection.Mode.Client ? "F: Leave the lobby" : null;
            case Kind.ChangeName:
                return "F: Change your name";
            case Kind.Quit:
                return "F: Quit the game";
        }
        return null;
    }

    public string Prompt(ulong clientId)
    {
        string p = DefaultPrompt();
        if (p == null) return null;
        return !string.IsNullOrEmpty(promptOverride) && p.StartsWith("F:") ? promptOverride : p;
    }

    public void Use(ulong clientId)
    {
        var c = Conn;
        if (c == null || DefaultPrompt() is not string p || !p.StartsWith("F:")) return;
        string error = null;
        switch (action)
        {
            case Kind.NewMall:
            case Kind.StartGame: error = c.StartGame(); break;
            case Kind.LoadSave: error = c.StartGame(saveSlot); break;
            case Kind.Host: MainMenu.Open(MainMenu.Page.Host); break;
            case Kind.Join: MainMenu.Open(MainMenu.Page.Join); break;
            case Kind.ChangeName: MainMenu.Open(MainMenu.Page.Name); break;
            case Kind.LeaveLobby: c.Leave(); break;
            case Kind.Quit: MainMenu.Quit(); break;
        }
        if (error != null)
        {
            MallConnection.LastMessage = error;
            Debug.LogWarning($"[Lobby] {error}", this);
        }
        else onUsed?.Invoke();
    }

    private void Update()
    {
        if (onlyWhenUsable == null || onlyWhenUsable.Length == 0) return;
        bool show = DefaultPrompt() != null;
        foreach (var go in onlyWhenUsable)
            if (go != null && go != gameObject && go.activeSelf != show) go.SetActive(show);
    }
}
