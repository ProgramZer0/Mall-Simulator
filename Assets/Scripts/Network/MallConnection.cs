using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.SceneManagement;
#if MALLSIM_RELAY
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
#endif

/// <summary>
/// Hosting and joining a mall, the lobby, and getting back to the main menu. Put it on the NetworkManager's
/// object (it lives as long as the NetworkManager does).
///
/// The main menu scene is a place you walk around in (the lobby). Coming into it you're playing alone
/// (Solo: a host only this machine can join, on 127.0.0.1). From there:
/// - New mall / a save (LobbyAction doors): the host takes everyone to the game scene (StartGame).
///   Alone, that's single player.
/// - Host: the lobby reopens for friends, Online (a join code through Unity Relay; needs MALLSIM_RELAY,
///   see below) or Direct IP (players type the host's IP and port; on the internet the host must forward
///   the port). Friends walk around the lobby with you until you start the game.
/// - Join: by code or by IP, into the host's lobby (or straight into their mall if it's started; anyone
///   can join any time).
/// Starting the game moves everyone to the game scene (NetworkManager → Enable Scene Management) and gives
/// them a fresh player there. Players appear at a PlayerSpawnPoint when the scene has any.
///
/// Every joining player sends their name and the game's version: a different version or a full mall
/// (Max Players) is refused, and they're told why. The host leaving ends the mall: everyone's sent back to
/// the menu, told why. A lost connection does the same.
///
/// Join codes (Unity Relay): install the Multiplayer Services package, link the project to a Unity Gaming
/// Services project (Edit → Project Settings → Services), and add MALLSIM_RELAY to Project Settings →
/// Player → Scripting Define Symbols. Without it only Direct IP is offered.
/// </summary>
[DefaultExecutionOrder(-9000)]
public class MallConnection : MonoBehaviour
{
    public static MallConnection Instance { get; private set; }

    [Tooltip("The scene with the mall (in Build Settings).")]
    [SerializeField] private string gameScene = "Main";
    [Tooltip("The main menu scene (in Build Settings).")]
    [SerializeField] private string menuScene = "MainMenu";
    [SerializeField] private ushort defaultPort = 7777;
    [Tooltip("Players in a mall unless the host picks otherwise.")]
    [SerializeField, Range(1, MaxPlayersLimit)] private int defaultMaxPlayers = 8;
    [Tooltip("Coming into the main menu scene starts a single player host, so you walk around the lobby.")]
    [SerializeField] private bool soloInMenu = true;
    [Tooltip("The port single player listens on (only on 127.0.0.1). 0: any free one.")]
    [SerializeField] private ushort soloPort = 0;

    /// <summary>What this machine is doing.</summary>
    public enum Mode { Off, Solo, Hosting, Client }
    /// <summary>What this machine is doing: playing alone, hosting for friends, or in someone else's mall.</summary>
    public Mode CurrentMode => nm != null && (nm.IsListening || nm.ShutdownInProgress) ? (nm.IsServer ? mode : Mode.Client) : Mode.Off;
    /// <summary>In the lobby (the main menu scene), not the mall.</summary>
    public bool InLobby => SceneManager.GetActiveScene().name == menuScene;
    /// <summary>The host can take everyone to the mall now (StartGame).</summary>
    public bool CanStartGame => nm != null && nm.IsServer && nm.IsListening && InLobby && !starting;
    /// <summary>The save to load in the mall that's starting (a LobbyAction save slot), or null for a new one.
    /// For the save system to read once the game scene's up.</summary>
    public static string PendingSave { get; set; }

    /// <summary>The most a host can let in.</summary>
    public const int MaxPlayersLimit = 32;

    public string GameScene => gameScene;
    public string MenuScene => menuScene;
    public ushort DefaultPort => defaultPort;
    public int DefaultMaxPlayers => defaultMaxPlayers;

    /// <summary>Join codes (Unity Relay) are built in.</summary>
    public static bool RelayAvailable =>
#if MALLSIM_RELAY
        true;
#else
        false;
#endif

    /* ---- what's shown about the running mall (host) ---- */

    /// <summary>Host: the join code friends type (online), or null.</summary>
    public string JoinCode { get; private set; }
    /// <summary>Host: the port it's listening on (Direct IP).</summary>
    public ushort Port { get; private set; }
    /// <summary>Host: how many players may be in the mall.</summary>
    public int MaxPlayers { get; private set; } = 8;
    /// <summary>Why we last ended up back at the menu (shown there for a while), or null.</summary>
    public static string LastMessage
    {
        get => lastMessage;
        set { lastMessage = value; LastMessageAt = Time.unscaledTime; }
    }
    private static string lastMessage;
    /// <summary>When LastMessage was set (Time.unscaledTime).</summary>
    public static float LastMessageAt { get; private set; }

    /* ---- the player's name (saved on this machine) ---- */

    public const int MaxNameLength = 20;

    public static string PlayerName
    {
        get => PlayerPrefs.GetString("MallSim.PlayerName", "");
        set { PlayerPrefs.SetString("MallSim.PlayerName", CleanName(value)); PlayerPrefs.Save(); }
    }

    public static string CleanName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var sb = new StringBuilder();
        foreach (char c in name.Trim())
            if (c != '<' && c != '>' && !char.IsControl(c)) sb.Append(c);
        string s = sb.ToString();
        return s.Length > MaxNameLength ? s.Substring(0, MaxNameLength) : s;
    }

    // Host: everyone's name, by client id.
    private readonly Dictionary<ulong, string> names = new();

    /// <summary>Host: a player's name ("Player 2" when they didn't give one).</summary>
    public string NameOf(ulong clientId) =>
        names.TryGetValue(clientId, out var n) && n.Length > 0 ? n : $"Player {clientId + 1}";

    /// <summary>Host: a player changed their name.</summary>
    public void SetName(ulong clientId, string name) => names[clientId] = CleanName(name);

    private NetworkManager nm;
    private bool leaving;
    private bool approving; // our Approve is the NetworkManager's approval callback
    private bool relayHost; // hosting online: the Relay spot is made once the lobby's loaded
    private Mode mode;      // as host: Solo or Hosting
    private bool hostAfterLoad; // the lobby is reloading to host for friends
    private bool starting;  // the game scene is loading

    private void Awake()
    {
        Instance = this;
        nm = GetComponent<NetworkManager>();
        MaxPlayers = defaultMaxPlayers;
    }

    private void OnEnable()
    {
        if (nm == null) nm = GetComponent<NetworkManager>();
        if (nm == null) return;
        nm.OnClientDisconnectCallback += OnClientDisconnect;
        nm.OnTransportFailure += OnTransportFailure;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void Start() => StartCoroutine(SoloNextFrame()); // the first scene

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        if (nm == null) return;
        nm.OnClientDisconnectCallback -= OnClientDisconnect;
        nm.OnTransportFailure -= OnTransportFailure;
        if (approving) nm.ConnectionApprovalCallback = null;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private UnityTransport Transport => nm != null ? nm.GetComponent<UnityTransport>() : null;

    /* ========================= HOSTING ========================= */

    /// <summary>Host a mall by Direct IP. Error: why not (null: started).</summary>
    public string HostDirect(ushort port, int maxPlayers)
    {
        var t = Transport;
        if (nm == null || t == null) return "No NetworkManager with a Unity Transport.";
        if (nm.IsListening && mode != Mode.Solo) return "Already in a mall.";
        JoinCode = null;
        relayHost = false;
        return StartHosting(port, maxPlayers);
    }

#if MALLSIM_RELAY
    /// <summary>Host a mall online with a join code (Unity Relay). Error: why not (null: started).</summary>
    public async Task<string> HostOnline(int maxPlayers)
    {
        var t = Transport;
        if (nm == null || t == null) return "No NetworkManager with a Unity Transport.";
        if (nm.IsListening && mode != Mode.Solo) return "Already in a mall.";
        try { await SignIn(); }
        catch (Exception e)
        {
            Debug.LogWarning($"[Connection] Couldn't sign in to Unity Services: {e}");
            return $"Couldn't reach Unity Services ({e.Message}). Try Direct IP.";
        }
        // The Relay spot is made once the lobby's loaded (it expires if the host doesn't connect to it soon).
        relayHost = true;
        JoinCode = null;
        return StartHosting(0, maxPlayers);
    }

    // The Relay spot for the host, and its join code. Error: why not.
    private async Task<string> MakeRelay()
    {
        try
        {
            var allocation = await RelayService.Instance.CreateAllocationAsync(Mathf.Clamp(MaxPlayers, 1, MaxPlayersLimit) - 1);
            JoinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
            Transport.SetRelayServerData(AllocationUtils.ToRelayServerData(allocation, "dtls"));
            return null;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Connection] Couldn't host online: {e}");
            return $"Couldn't host online ({e.Message}). Is Relay turned on for the project in the Unity Cloud dashboard? Try Direct IP.";
        }
    }

    /// <summary>Join a mall by its join code. Error: why not (null: connecting).</summary>
    public async Task<string> JoinOnline(string code)
    {
        var t = Transport;
        if (nm == null || t == null) return "No NetworkManager with a Unity Transport.";
        if (nm.IsListening && mode != Mode.Solo) return "Already in a mall.";
        code = (code ?? "").Trim().ToUpperInvariant();
        if (code.Length == 0) return "Type the host's join code.";
        Unity.Services.Relay.Models.JoinAllocation allocation;
        try
        {
            await SignIn();
            allocation = await RelayService.Instance.JoinAllocationAsync(code);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Connection] Couldn't join {code}: {e}");
            return $"No mall found for code {code}.";
        }
        string error = await StopSolo();
        if (error != null) return error;
        t.SetRelayServerData(AllocationUtils.ToRelayServerData(allocation, "dtls"));
        return StartJoining();
    }

    private static async Task SignIn()
    {
        if (UnityServices.State != ServicesInitializationState.Initialized) await UnityServices.InitializeAsync();
        if (!AuthenticationService.Instance.IsSignedIn) await AuthenticationService.Instance.SignInAnonymouslyAsync();
    }
#endif

    // The lobby (the main menu scene) is loaded fresh, then the host starts there, so its scene objects
    // spawn as usual. (Without a menu scene in Build Settings: the scene you're in.) Joining players are
    // taken to it by the NetworkManager.
    private string StartHosting(ushort port, int maxPlayers)
    {
        Port = port;
        MaxPlayers = Mathf.Clamp(maxPlayers, 1, MaxPlayersLimit);
        leaving = false;
        string scene = !string.IsNullOrEmpty(menuScene) && Application.CanStreamedLevelBeLoaded(menuScene)
            ? menuScene : SceneManager.GetActiveScene().name;
        hostAfterLoad = true;
        StartCoroutine(StopThenLoad(scene));
        return null;
    }

    // Stop single player (if it's running), then load the scene; OnSceneLoaded carries on from there.
    private IEnumerator StopThenLoad(string scene)
    {
        if (nm.IsListening)
        {
            leaving = true;
            nm.Shutdown();
        }
        while (nm != null && nm.ShutdownInProgress) yield return null;
        leaving = false;
        SceneManager.LoadScene(scene, LoadSceneMode.Single);
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode loadMode)
    {
        if (loadMode != LoadSceneMode.Single) return;
        if (hostAfterLoad)
        {
            hostAfterLoad = false;
            StartHostWhenReady();
            return;
        }
        StartCoroutine(SoloNextFrame());
    }

    private async void StartHostWhenReady()
    {
        string error = null;
#if MALLSIM_RELAY
        if (relayHost) error = await MakeRelay();
#else
        await Task.Yield();
#endif
        // (SetConnectionData also switches the transport back from Relay to direct, after trying online.)
        if (error == null && !relayHost) Transport.SetConnectionData("127.0.0.1", Port, "0.0.0.0");
        if (error == null)
        {
            names.Clear();
            names[NetworkManager.ServerClientId] = PlayerName;
            error = StartHostHere(Mode.Hosting);
        }
        if (error != null) BackToMenu(error);
    }

    private string StartHostHere(Mode as_)
    {
        mode = as_;
        starting = false;
        nm.NetworkConfig.ConnectionApproval = true;
        nm.ConnectionApprovalCallback = Approve;
        approving = true;
        if (!nm.StartHost())
            return as_ == Mode.Solo ? "Couldn't start single player." :
                   Port > 0 ? $"Couldn't host on port {Port} (is something else using it?)." : "Couldn't start the mall.";
        Debug.Log(as_ == Mode.Solo ? "[Connection] Playing alone." :
            $"[Connection] Hosting {(JoinCode != null ? $"online, code {JoinCode}" : $"on port {Port}")}, up to {MaxPlayers} players.");
        return null;
    }

    /* ========================= SINGLE PLAYER (the lobby, alone) ========================= */

    // In the menu scene with nothing running: play alone (a host nobody else can reach).
    private IEnumerator SoloNextFrame()
    {
        yield return null;
        if (!soloInMenu || nm == null || hostAfterLoad || !InLobby) yield break;
        while (nm != null && nm.ShutdownInProgress) yield return null;
        if (nm == null || nm.IsListening) yield break;
        StartSolo();
    }

    /// <summary>Play alone: a host on 127.0.0.1 that turns everyone else away. Error: why not.</summary>
    public string StartSolo()
    {
        var t = Transport;
        if (nm == null || t == null) return "No NetworkManager with a Unity Transport.";
        if (nm.IsListening) return "Already in a mall.";
        t.SetConnectionData("127.0.0.1", soloPort, "127.0.0.1");
        JoinCode = null;
        relayHost = false;
        Port = 0;
        MaxPlayers = 1;
        leaving = false;
        names.Clear();
        names[NetworkManager.ServerClientId] = PlayerName;
        string error = StartHostHere(Mode.Solo);
        if (error != null) Debug.LogWarning($"[Connection] {error} (Solo Port {soloPort} on MallConnection.)");
        return error;
    }

    // Before joining someone: stop playing alone. Error: why not.
    private async Task<string> StopSolo()
    {
        if (nm == null) return "No NetworkManager.";
        if (nm.IsListening && mode != Mode.Solo) return "Already in a mall.";
        if (nm.IsListening)
        {
            leaving = true;
            nm.Shutdown();
        }
        while (nm != null && nm.ShutdownInProgress) await Task.Yield();
        await Task.Yield();
        leaving = false;
        return nm == null ? "No NetworkManager." : null;
    }

    /* ========================= STARTING THE GAME ========================= */

    /// <summary>
    /// Host: take everyone from the lobby to the mall (the game scene), each with a fresh player. Save:
    /// which save to load there (see PendingSave), or null for a new mall. Error: why not (null: going).
    /// </summary>
    public string StartGame(string save = null)
    {
        if (nm == null || !nm.IsServer || !nm.IsListening) return "Only the host can start the mall.";
        if (starting) return null;
        if (!InLobby) return "The mall's already open.";
        if (string.IsNullOrEmpty(gameScene) || !Application.CanStreamedLevelBeLoaded(gameScene))
            return $"The '{gameScene}' scene isn't in Build Settings.";
        if (nm.SceneManager == null) return "Turn on Enable Scene Management on the NetworkManager.";
        PendingSave = save;
        UIState.CloseAll();
        nm.SceneManager.OnLoadEventCompleted += OnGameLoaded;
        var status = nm.SceneManager.LoadScene(gameScene, LoadSceneMode.Single);
        if (status != SceneEventProgressStatus.Started)
        {
            nm.SceneManager.OnLoadEventCompleted -= OnGameLoaded;
            return $"Couldn't open the mall ({status}).";
        }
        starting = true;
        Debug.Log($"[Connection] Opening the mall{(save != null ? $" from save '{save}'" : "")} for {nm.ConnectedClientsIds.Count} player(s).");
        return null;
    }

    // Everyone's in the mall: a fresh player each (so everything that sets up a player does it there).
    private void OnGameLoaded(string sceneName, LoadSceneMode loadMode, List<ulong> done, List<ulong> timedOut)
    {
        if (sceneName != gameScene) return;
        nm.SceneManager.OnLoadEventCompleted -= OnGameLoaded;
        starting = false;
        foreach (var id in new List<ulong>(nm.ConnectedClientsIds)) RespawnPlayer(id);
    }

    private void RespawnPlayer(ulong clientId)
    {
        var prefab = nm.NetworkConfig.PlayerPrefab;
        if (prefab == null) return;
        if (nm.ConnectedClients.TryGetValue(clientId, out var client) && client.PlayerObject != null && client.PlayerObject.IsSpawned)
            client.PlayerObject.Despawn(true);
        PlayerSpawnPoint.Pick(out Vector3 pos, out Quaternion rot, prefab.transform.position, prefab.transform.rotation);
        var go = Instantiate(prefab, pos, rot);
        go.GetComponent<NetworkObject>().SpawnAsPlayerObject(clientId);
    }

    /* ========================= JOINING ========================= */

    /// <summary>Join a mall by IP and port. Error: why not (null: connecting).</summary>
    public async Task<string> JoinDirect(string ip, ushort port)
    {
        var t = Transport;
        if (nm == null || t == null) return "No NetworkManager with a Unity Transport.";
        if (nm.IsListening && mode != Mode.Solo) return "Already in a mall.";
        ip = (ip ?? "").Trim();
        if (ip.Length == 0) return "Type the host's IP address.";
        if (ip.Equals("localhost", StringComparison.OrdinalIgnoreCase)) ip = "127.0.0.1";
        if (!System.Net.IPAddress.TryParse(ip, out _))
        {
            try
            {
                var found = System.Net.Dns.GetHostAddresses(ip);
                ip = Array.Find(found, a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.ToString();
            }
            catch { ip = null; }
            if (ip == null) return "That isn't an IP address.";
        }
        string error = await StopSolo();
        if (error != null) return error;
        t.SetConnectionData(ip, port);
        JoinCode = null;
        return StartJoining();
    }

    private string StartJoining()
    {
        nm.NetworkConfig.ConnectionData = Encoding.UTF8.GetBytes($"{Application.version}\n{PlayerName}");
        leaving = false;
        mode = Mode.Client;
        if (nm.StartClient()) return null;
        BackToMenu("Couldn't start connecting.");
        return "Couldn't start connecting.";
    }

    // Host: let a player in, or say why not.
    private void Approve(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
    {
        response.CreatePlayerObject = true;
        response.Pending = false;
        if (PlayerSpawnPoint.Pick(out Vector3 spawnAt, out Quaternion spawnFacing, Vector3.zero, Quaternion.identity))
        {
            response.Position = spawnAt;
            response.Rotation = spawnFacing;
        }
        if (request.ClientNetworkId == NetworkManager.ServerClientId) { response.Approved = true; return; }
        if (mode == Mode.Solo)
        {
            response.Approved = false;
            response.Reason = "That player is playing alone.";
            return;
        }

        string text = request.Payload != null ? Encoding.UTF8.GetString(request.Payload) : "";
        string[] parts = text.Split('\n');
        string version = parts.Length > 0 ? parts[0] : "";
        string name = parts.Length > 1 ? CleanName(parts[1]) : "";
        if (version != Application.version)
        {
            response.Approved = false;
            response.Reason = $"Different game version: the host has {Application.version}, you have {(version.Length > 0 ? version : "an older one")}.";
            return;
        }
        if (nm.ConnectedClientsIds.Count >= MaxPlayers)
        {
            response.Approved = false;
            response.Reason = $"The mall is full ({MaxPlayers} players).";
            return;
        }
        names[request.ClientNetworkId] = name;
        response.Approved = true;
    }

    /* ========================= LEAVING ========================= */

    /// <summary>Leave the mall (a client), or end it for everyone (the host). Back to the menu.</summary>
    public void Leave()
    {
        if (nm == null || !nm.IsListening) { BackToMenu(null); return; }
        if (nm.IsServer) StartCoroutine(EndForEveryone());
        else
        {
            leaving = true;
            nm.Shutdown();
            BackToMenu(null);
        }
    }

    // Host: tell everyone why, give the messages a moment to go out, then stop.
    private IEnumerator EndForEveryone()
    {
        leaving = true;
        var others = new List<ulong>();
        foreach (var id in nm.ConnectedClientsIds) if (id != NetworkManager.ServerClientId) others.Add(id);
        foreach (var id in others) nm.DisconnectClient(id, "The host ended the mall.");
        yield return new WaitForSecondsRealtime(0.3f);
        nm.Shutdown();
        BackToMenu(null);
    }

    private void OnClientDisconnect(ulong clientId)
    {
        if (nm == null) return;
        if (nm.IsServer)
        {
            if (clientId != NetworkManager.ServerClientId) names.Remove(clientId);
            return;
        }
        // This machine was the one cut off (refused, kicked, the host left, or the connection dropped).
        if (clientId != nm.LocalClientId && nm.IsConnectedClient) return;
        if (leaving) return;
        string reason = nm.DisconnectReason;
        if (string.IsNullOrEmpty(reason)) reason = nm.IsConnectedClient || SceneManager.GetActiveScene().name == gameScene
            ? "Lost the connection to the host."
            : "Couldn't reach the mall. Check the address (and that the host's port is open).";
        leaving = true;
        nm.Shutdown();
        BackToMenu(reason);
    }

    private void OnTransportFailure()
    {
        if (leaving) return;
        leaving = true;
        BackToMenu("The connection failed.");
    }

    private void BackToMenu(string reason)
    {
        LastMessage = reason;
        JoinCode = null;
        hostAfterLoad = false;
        starting = false;
        UIState.CloseAll();
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        if (string.IsNullOrEmpty(menuScene) || !Application.CanStreamedLevelBeLoaded(menuScene))
        {
            if (!string.IsNullOrEmpty(reason)) Debug.Log($"[Connection] {reason} (no '{menuScene}' scene in Build Settings to go back to).");
            return;
        }
        StartCoroutine(LoadMenuWhenStopped());
    }

    private IEnumerator LoadMenuWhenStopped()
    {
        while (nm != null && nm.ShutdownInProgress) yield return null;
        SceneManager.LoadScene(menuScene, LoadSceneMode.Single);
    }

    /* ========================= ADDRESSES (shown to the host) ========================= */

    /// <summary>This machine's address(es) on the local network, for friends on the same network.</summary>
    public static string LocalAddresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
                    ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) list.Add(a.Address.ToString());
            }
        }
        catch { }
        return list.Count > 0 ? string.Join(", ", list) : "unknown";
    }
}
