using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.SceneManagement;
#if MALLSIM_RELAY
using System.Threading.Tasks;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
#endif

/// <summary>
/// Hosting and joining a mall, and getting back to the main menu. Put it on the NetworkManager's object
/// (it lives as long as the NetworkManager does).
///
/// Host: Online (a join code through Unity Relay; needs MALLSIM_RELAY, see below) or Direct IP (players
/// type the host's IP and port; on the internet the host must forward the port). Then the game scene loads
/// for everyone (NetworkManager → Enable Scene Management). Join: by code or by IP. Anyone can join any time.
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
    /// <summary>Why we last ended up back at the menu (shown there once), or null.</summary>
    public static string LastMessage { get; set; }

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

    private NetworkManager nm;
    private bool leaving;
    private bool approving; // our Approve is the NetworkManager's approval callback

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
    }

    private void OnDisable()
    {
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
        if (nm.IsListening) return "Already in a mall.";
        t.SetConnectionData("127.0.0.1", port, "0.0.0.0");
        JoinCode = null;
        return StartHosting(port, maxPlayers);
    }

#if MALLSIM_RELAY
    /// <summary>Host a mall online with a join code (Unity Relay). Error: why not (null: started).</summary>
    public async Task<string> HostOnline(int maxPlayers)
    {
        var t = Transport;
        if (nm == null || t == null) return "No NetworkManager with a Unity Transport.";
        if (nm.IsListening) return "Already in a mall.";
        try
        {
            await SignIn();
            var allocation = await RelayService.Instance.CreateAllocationAsync(Mathf.Clamp(maxPlayers, 1, MaxPlayersLimit) - 1);
            JoinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
            t.SetRelayServerData(AllocationUtils.ToRelayServerData(allocation, "dtls"));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Connection] Couldn't host online: {e}");
            return $"Couldn't host online ({e.Message}). Try Direct IP.";
        }
        return StartHosting(0, maxPlayers);
    }

    /// <summary>Join a mall by its join code. Error: why not (null: connecting).</summary>
    public async Task<string> JoinOnline(string code)
    {
        var t = Transport;
        if (nm == null || t == null) return "No NetworkManager with a Unity Transport.";
        if (nm.IsListening) return "Already in a mall.";
        code = (code ?? "").Trim().ToUpperInvariant();
        if (code.Length == 0) return "Type the host's join code.";
        try
        {
            await SignIn();
            var allocation = await RelayService.Instance.JoinAllocationAsync(code);
            t.SetRelayServerData(AllocationUtils.ToRelayServerData(allocation, "dtls"));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Connection] Couldn't join {code}: {e}");
            return $"No mall found for code {code}.";
        }
        return StartJoining();
    }

    private static async Task SignIn()
    {
        if (UnityServices.State != ServicesInitializationState.Initialized) await UnityServices.InitializeAsync();
        if (!AuthenticationService.Instance.IsSignedIn) await AuthenticationService.Instance.SignInAnonymouslyAsync();
    }
#endif

    // The game scene's loaded first (as if pressing Play in it), then the host starts there, so the mall's
    // scene objects spawn as usual. Joining players are taken to it by the NetworkManager.
    private string StartHosting(ushort port, int maxPlayers)
    {
        Port = port;
        MaxPlayers = Mathf.Clamp(maxPlayers, 1, MaxPlayersLimit);
        names.Clear();
        names[NetworkManager.ServerClientId] = PlayerName;
        leaving = false;
        if (string.IsNullOrEmpty(gameScene) || SceneManager.GetActiveScene().name == gameScene) return StartHostHere();
        if (!Application.CanStreamedLevelBeLoaded(gameScene)) return $"The '{gameScene}' scene isn't in Build Settings.";
        SceneManager.sceneLoaded += OnGameSceneLoaded;
        SceneManager.LoadScene(gameScene, LoadSceneMode.Single);
        return null;
    }

    private void OnGameSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (scene.name != gameScene) return;
        SceneManager.sceneLoaded -= OnGameSceneLoaded;
        string error = StartHostHere();
        if (error != null) BackToMenu(error);
    }

    private string StartHostHere()
    {
        nm.NetworkConfig.ConnectionApproval = true;
        nm.ConnectionApprovalCallback = Approve;
        approving = true;
        if (!nm.StartHost()) return Port > 0 ? $"Couldn't host on port {Port} (is something else using it?)." : "Couldn't start the mall.";
        Debug.Log($"[Connection] Hosting {(JoinCode != null ? $"online, code {JoinCode}" : $"on port {Port}")}, up to {MaxPlayers} players.");
        return null;
    }

    /* ========================= JOINING ========================= */

    /// <summary>Join a mall by IP and port. Error: why not (null: connecting).</summary>
    public string JoinDirect(string ip, ushort port)
    {
        var t = Transport;
        if (nm == null || t == null) return "No NetworkManager with a Unity Transport.";
        if (nm.IsListening) return "Already in a mall.";
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
        t.SetConnectionData(ip, port);
        JoinCode = null;
        return StartJoining();
    }

    private string StartJoining()
    {
        nm.NetworkConfig.ConnectionData = Encoding.UTF8.GetBytes($"{Application.version}\n{PlayerName}");
        leaving = false;
        return nm.StartClient() ? null : "Couldn't start connecting.";
    }

    // Host: let a player in, or say why not.
    private void Approve(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
    {
        response.CreatePlayerObject = true;
        response.Pending = false;
        if (request.ClientNetworkId == NetworkManager.ServerClientId) { response.Approved = true; return; }

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
