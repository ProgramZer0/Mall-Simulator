using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// The mall's network brain. One per scene, on an object with a NetworkObject.
/// The host makes every decision (who owns which store, what they can do where);
/// clients can ask the same questions to show feedback, but the host has the final say.
///
/// Rules (from the to-do):
///  - Pick up / place: only while standing in your own store or at a loading dock, and only
///    on a spot inside your own store or a dock. Carrying something elsewhere is fine.
///  - Interactables (doors, registers, ...): allowed anywhere except another player's store.
///    Ones marked Only In Own Zones (carts) only work in your own store or at a dock.
///
/// Teams: stores belong to a team, not a single player. By default every player is a team of
/// one (team id = their client id). A team gets one free starter store and can buy more, up to
/// Max Stores Per Team. Co-op later just means putting more players on the same team.
/// </summary>
public class MallManager : NetworkBehaviour
{
    public static MallManager Instance { get; private set; }

    [Header("Ownership")]
    [Tooltip("Give every player a free starter store when they join.")]
    [SerializeField] private bool assignStarterPlots = true;
    [Tooltip("Free a team's stores when its last player leaves. (Client ids change on reconnect, " +
             "so keeping them would orphan the store until saves/persistent ids exist.)")]
    [SerializeField] private bool releaseOnDisconnect = true;
    [Tooltip("Stores one team may own, including its free starter store.")]
    [SerializeField] private int maxStoresPerTeam = 6;
    [Tooltip("Players can unlock a store's segments by looking at the segment's wall and pressing F. " +
             "Untick once segments are bought from the shop's license page instead.")]
    [SerializeField] private bool buySegmentsFromWalls = true;
    [Tooltip("Players allowed on one team. 1 = everyone plays solo (co-op off).")]
    [SerializeField] private int maxPlayersPerTeam = 1;

    [Header("Development")]
    [Tooltip("B = buy the next segment of the store you're standing in (no money check). " +
             "C = open/close your store (stand inside it or near one of its doors). " +
             "Turn off once the store menu exists.")]
    [SerializeField] private bool devHotkeys = true;

    private readonly Dictionary<int, StorePlot> plots = new();

    /// <summary>Which team each connected player is on. Synced so clients can check permissions too.</summary>
    public struct TeamMember : INetworkSerializable, IEquatable<TeamMember>
    {
        public ulong clientId;
        public ulong teamId;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref clientId);
            serializer.SerializeValue(ref teamId);
        }

        public bool Equals(TeamMember other) => clientId == other.clientId && teamId == other.teamId;
    }

    private NetworkList<TeamMember> teamMembers;

    /// <summary>Raised on every machine when anyone changes team.</summary>
    public event Action TeamsChanged;

    public int MaxStoresPerTeam => maxStoresPerTeam;

    /// <summary>Can segments be unlocked by looking at their wall (F)? Set on the MallManager.</summary>
    public bool BuySegmentsFromWalls => buySegmentsFromWalls;

    public IReadOnlyCollection<StorePlot> Plots => plots.Values;

    /* ========================= LIFECYCLE ========================= */

    private void Awake()
    {
        EnsureTeamList();

        if (Instance != null && Instance != this)
        {
            Debug.LogError("[MallManager] More than one in the scene. Keep exactly one.", this);
            return;
        }
        Instance = this;
    }

    // NetworkLists must be created before the object spawns, and only once.
    private void EnsureTeamList()
    {
        teamMembers ??= new NetworkList<TeamMember>(
            null, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    }

    public override void OnNetworkSpawn()
    {
        // Plots may have spawned before us; pick them all up (Register ignores repeats).
        foreach (var plot in FindObjectsByType<StorePlot>(FindObjectsSortMode.None))
            Register(plot);

        teamMembers.OnListChanged += OnTeamListChanged;

        if (!IsServer) return;

        NetworkManager.OnClientConnectedCallback += OnClientConnected;
        NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;

        // Anyone already connected (the host itself) gets a store too.
        foreach (ulong id in NetworkManager.ConnectedClientsIds)
            OnClientConnected(id);
    }

    public override void OnNetworkDespawn()
    {
        teamMembers.OnListChanged -= OnTeamListChanged;

        if (IsServer && NetworkManager != null)
        {
            NetworkManager.OnClientConnectedCallback -= OnClientConnected;
            NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
        }
    }

    public override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    public void Register(StorePlot plot)
    {
        if (plot == null) return;
        if (plots.TryGetValue(plot.PlotId, out var existing))
        {
            if (existing != plot)
                Debug.LogError($"[MallManager] Plot id {plot.PlotId} is used by both " +
                               $"'{existing.name}' and '{plot.name}'. Ids must be unique.", plot);
            return;
        }
        plots.Add(plot.PlotId, plot);
    }

    public void Unregister(StorePlot plot)
    {
        if (plot != null && plots.TryGetValue(plot.PlotId, out var existing) && existing == plot)
            plots.Remove(plot.PlotId);
    }

    /* ========================= QUERIES ========================= */

    public bool TryGetPlot(int plotId, out StorePlot plot) => plots.TryGetValue(plotId, out plot);

    /// <summary>The store whose (purchased) space contains this point, or null for hallways etc.</summary>
    public StorePlot GetPlotAt(Vector3 worldPoint)
    {
        foreach (var plot in plots.Values)
            if (plot.Contains(worldPoint)) return plot;
        return null;
    }

    /// <summary>
    /// The store containing this point, or else one within Reach meters of its space (for things that
    /// stick out a little, like windows and doors in the front wall). Null if none.
    /// </summary>
    public StorePlot GetPlotNear(Vector3 worldPoint, float reach)
    {
        var inside = GetPlotAt(worldPoint);
        if (inside != null || reach <= 0f) return inside;
        foreach (var plot in plots.Values)
            if (plot.IsInsideOrNear(worldPoint, reach)) return plot;
        return null;
    }

    /// <summary>Why a team can't own another store (null if it can): Max Stores Per Team, or its level.</summary>
    public string WhyCantOwnMore(ulong teamId)
    {
        int owned = CountPlotsOwnedByTeam(teamId);
        if (owned >= maxStoresPerTeam) return $"Your team owns the most stores allowed ({maxStoresPerTeam})";
        if (owned >= ProgressionManager.StoresAllowed(teamId))
        {
            int need = ProgressionManager.LevelForStores(owned + 1);
            return need > 0 ? $"Reach level {need} to own another store" : "Your team can't own more stores";
        }
        return null;
    }

    public int CountPlotsOwnedByTeam(ulong teamId)
    {
        int n = 0;
        foreach (var plot in plots.Values)
            if (plot.HasOwner && plot.OwnerTeamId == teamId) n++;
        return n;
    }

    /* ========================= TEAMS ========================= */

    /// <summary>
    /// The team a player is on. Players nobody has put on a team are a team of one,
    /// so with no MallManager (or before syncing) this is just their client id.
    /// </summary>
    public static ulong GetTeamOf(ulong clientId)
    {
        var list = Instance != null ? Instance.teamMembers : null;
        if (list != null)
            foreach (var m in list)
                if (m.clientId == clientId) return m.teamId;
        return clientId;
    }

    /// <summary>Connected players on a team.</summary>
    public List<ulong> GetTeamMembers(ulong teamId)
    {
        var result = new List<ulong>();
        foreach (var m in teamMembers)
            if (m.teamId == teamId) result.Add(m.clientId);
        return result;
    }

    private void OnTeamListChanged(NetworkListEvent<TeamMember> e)
    {
        foreach (var plot in plots.Values) plot.RefreshLocalView();
        TeamsChanged?.Invoke();
    }

    private void ServerSetTeam(ulong clientId, ulong teamId)
    {
        for (int i = 0; i < teamMembers.Count; i++)
        {
            if (teamMembers[i].clientId != clientId) continue;
            teamMembers[i] = new TeamMember { clientId = clientId, teamId = teamId };
            return;
        }
        teamMembers.Add(new TeamMember { clientId = clientId, teamId = teamId });
    }

    private void ServerRemoveFromTeams(ulong clientId)
    {
        for (int i = teamMembers.Count - 1; i >= 0; i--)
            if (teamMembers[i].clientId == clientId) teamMembers.RemoveAt(i);
    }

    /// <summary>
    /// Server: move a player onto another team (co-op). Fails if that team is full. Their old
    /// team keeps its stores while it still has players; an empty team's stores are freed.
    /// Nothing calls this yet; it's the hook for the co-op menu.
    /// </summary>
    public bool ServerTryJoinTeam(ulong clientId, ulong teamId)
    {
        if (!IsServer) return false;

        ulong oldTeam = GetTeamOf(clientId);
        if (oldTeam == teamId) return true;
        if (GetTeamMembers(teamId).Count >= maxPlayersPerTeam) return false;

        ServerStopEdit(clientId);
        ServerSetTeam(clientId, teamId);
        if (GetTeamMembers(oldTeam).Count == 0) ServerReleaseTeam(oldTeam);
        return true;
    }

    private void ServerReleaseTeam(ulong teamId)
    {
        foreach (var plot in plots.Values)
            if (plot.HasOwner && plot.OwnerTeamId == teamId)
                plot.ServerRelease();

        if (EconomyManager.Instance != null) EconomyManager.Instance.ServerForgetTeam(teamId);
        if (LicenseManager.Instance != null) LicenseManager.Instance.ServerForgetTeam(teamId);
    }

    /// <summary>Server: charge a team, if there's an economy. With no EconomyManager everything is free.</summary>
    private static bool ServerTryCharge(ulong teamId, int amount, string what, UnityEngine.Object context)
    {
        var eco = EconomyManager.Instance;
        if (eco == null || !eco.IsSpawned || eco.ServerTrySpend(teamId, amount)) return true;
        Debug.Log($"[MallManager] Team {teamId} can't afford {what} ({amount}).", context);
        return false;
    }

    /* ========================= PERMISSIONS ========================= */

    /// <summary>
    /// A "work zone" for this player: a store they're authorized for, or any loading dock.
    /// </summary>
    public bool IsWorkZoneFor(ulong clientId, Vector3 worldPoint)
    {
        var plot = GetPlotAt(worldPoint);
        if (plot != null) return plot.IsAuthorized(clientId);
        return LoadingDockZone.GetDockAt(worldPoint) != null;
    }

    /// <summary>
    /// Pick up / place: the player must be standing in one of their work zones (own store or
    /// a dock), and the item / target spot must be in one too. Holding is never checked, so
    /// carrying something out into the hallway is fine; you just can't set it down there.
    /// With no MallManager in the scene everything is allowed, so test scenes still work.
    /// </summary>
    /// <summary>
    /// True if this spot is on a shipment area of a store this player isn't allowed in, so they
    /// can't block someone else's deliveries by parking things there.
    /// </summary>
    public static bool IsOthersShipmentArea(ulong clientId, Vector3 position)
    {
        if (Instance == null) return false;
        foreach (var plot in Instance.plots.Values)
            if (!plot.IsAuthorized(clientId) && plot.IsOnShipmentArea(position)) return true;
        return false;
    }

    public static bool CanPickupOrPlace(ulong clientId, Vector3 playerPosition, Vector3 targetPosition)
    {
        if (Instance == null) return true;

        // Carts: your own team's cart can be loaded and unloaded anywhere (it's how things travel);
        // another team's cart never.
        var cart = CartNet.FindCartAt(targetPosition);
        if (cart != null) return cart.CanUse(clientId);

        return Instance.IsWorkZoneFor(clientId, playerPosition) &&
               Instance.IsWorkZoneFor(clientId, targetPosition);
    }

    /// <summary>
    /// Nobody may set things down here: the loading docks (deliveries are only picked up there),
    /// unless it's onto a cart standing at the dock.
    /// </summary>
    public static bool IsNoPlaceZone(Vector3 targetPosition) =>
        LoadingDockZone.GetDockAt(targetPosition) != null && CartNet.FindCartAt(targetPosition) == null;

    /// <summary>Server: send a store's carts back to their home spots (for the recall button).</summary>
    public void ServerRecallCarts(ulong clientId, int plotId)
    {
        if (!IsServer || !plots.TryGetValue(plotId, out var plot) || !plot.IsAuthorized(clientId)) return;
        foreach (var cart in CartNet.GetCartsOf(plot)) cart.ServerRecall();
    }

    /// <summary>For the recall-cart button: MallManager.Instance.RequestRecallCartsServerRpc(plot.PlotId).</summary>
    [ServerRpc(RequireOwnership = false)]
    public void RequestRecallCartsServerRpc(int plotId, ServerRpcParams rpcParams = default)
    {
        ServerRecallCarts(rpcParams.Receive.SenderClientId, plotId);
    }

    /// <summary>
    /// Interactables: never inside another player's store. Ones marked Only In Own Zones
    /// (carts) additionally only work inside your own store or at a dock.
    /// </summary>
    public static bool CanInteract(ulong clientId, Vector3 targetPosition, bool onlyInOwnZones = false)
    {
        if (Instance == null) return true;

        if (onlyInOwnZones)
            return Instance.IsWorkZoneFor(clientId, targetPosition);

        var plot = Instance.GetPlotAt(targetPosition);
        return plot == null || !plot.HasOwner || plot.IsAuthorized(clientId);
    }

    /* ========================= OWNERSHIP (SERVER) ========================= */

    private void OnClientConnected(ulong clientId)
    {
        // Everyone starts as a team of one.
        if (!ContainsMember(clientId)) ServerSetTeam(clientId, clientId);

        ulong team = GetTeamOf(clientId);
        if (!assignStarterPlots || CountPlotsOwnedByTeam(team) > 0) return;

        foreach (var plot in plots.Values)
        {
            if (plot.IsStarterPlot && !plot.HasOwner)
            {
                plot.ServerSetOwner(team);
                // A team's first store pays its Starting Money (once per team).
                if (EconomyManager.Instance != null)
                    EconomyManager.Instance.ServerGrantStartingMoney(team, plot.StartingMoney);
                return;
            }
        }

        Debug.LogWarning($"[MallManager] No free starter store for client {clientId}. " +
                         "Add more StorePlots marked Is Starter Plot.", this);
    }

    private bool ContainsMember(ulong clientId)
    {
        foreach (var m in teamMembers)
            if (m.clientId == clientId) return true;
        return false;
    }

    private void OnClientDisconnected(ulong clientId)
    {
        // Never leave a store stuck in edit mode (closed, items floating) after its editor leaves.
        ServerStopEdit(clientId);

        ulong team = GetTeamOf(clientId);
        ServerRemoveFromTeams(clientId);

        // A team keeps its stores while any teammate is still connected.
        if (releaseOnDisconnect && GetTeamMembers(team).Count == 0)
            ServerReleaseTeam(team);
    }

    /// <summary>
    /// Server: put the store the player is standing in into edit mode, with them as the editor.
    /// Fails if it isn't their store or someone else is already editing it.
    /// </summary>
    public bool ServerTryStartEdit(ulong clientId, Vector3 playerPosition) =>
        ServerTryStartEdit(clientId, playerPosition, out _);

    /// <summary>Server: start editing the store this player stands in. reason says why not, if it fails.</summary>
    public bool ServerTryStartEdit(ulong clientId, Vector3 playerPosition, out string reason)
    {
        reason = null;
        if (!IsServer) return false;

        var plot = GetPlotAt(playerPosition);
        if (plot == null || !plot.IsAuthorized(clientId)) { reason = "Stand in a store you own to edit it."; return false; }
        if (plot.IsRenovating)
        {
            if (plot.EditorClientId == clientId) return true;
            reason = "Someone else is already editing this store.";
            return false;
        }

        // Editing takes everything in the store out and walls it off, so nobody else may be inside.
        foreach (var client in NetworkManager.ConnectedClientsList)
        {
            if (client.ClientId == clientId || client.PlayerObject == null) continue;
            if (plot.Contains(client.PlayerObject.transform.position))
            {
                reason = "Someone is still in the store. Edit once everyone else has left.";
                return false;
            }
        }

        // Editing takes the counters away too, so customers at a checkout are served first.
        foreach (var counter in CheckoutCounter.All)
            if (counter != null && counter.IsBusy && counter.Plot == plot)
            {
                reason = "A customer is still at checkout. Finish serving them first.";
                return false;
            }

        ServerStopEdit(clientId); // one store at a time
        plot.ServerSetEditor(clientId);
        return true;
    }

    /// <summary>Server: end every edit session this player has open.</summary>
    public void ServerStopEdit(ulong clientId)
    {
        if (!IsServer) return;
        foreach (var plot in plots.Values)
            if (plot.EditorClientId == clientId)
                plot.ServerSetEditor(StorePlot.NoOwner);
    }

    /// <summary>The store this player is currently editing, or null.</summary>
    public StorePlot GetPlotEditedBy(ulong clientId)
    {
        foreach (var plot in plots.Values)
            if (plot.IsRenovating && plot.EditorClientId == clientId) return plot;
        return null;
    }

    /// <summary>
    /// Server: give an unowned store to a player. Money isn't checked yet;
    /// that hook goes here once there's an economy.
    /// </summary>
    public bool ServerTryClaimPlot(ulong clientId, int plotId)
    {
        if (!IsServer || !plots.TryGetValue(plotId, out var plot)) return false;
        if (plot.HasOwner) return false;
        ulong team = GetTeamOf(clientId);
        string why = WhyCantOwnMore(team);
        if (why != null)
        {
            Debug.Log($"[MallManager] Team {team} can't buy '{plot.DisplayName}': {why}.", plot);
            return false;
        }

        if (!ServerTryCharge(team, plot.Price, $"'{plot.DisplayName}'", plot)) return false;
        plot.ServerSetOwner(team);
        Debug.Log($"[MallManager] Client {clientId} bought '{plot.DisplayName}' for team {team}.", plot);
        return true;
    }

    /// <summary>
    /// Server: buy a store from its for-sale wall. Same as claiming, plus a check that the
    /// player is actually standing at that store's for-sale wall.
    /// </summary>
    public bool ServerTryBuyFromSign(ulong clientId, int plotId)
    {
        if (!IsServer || !plots.TryGetValue(plotId, out var plot)) return false;

        var player = NetworkManager.ConnectedClients.TryGetValue(clientId, out var client) ? client.PlayerObject : null;
        if (player == null || !plot.IsNearForSaleWall(player.transform.position)) return false;

        return ServerTryClaimPlot(clientId, plotId);
    }

    [ServerRpc(RequireOwnership = false)]
    public void RequestBuyFromSignServerRpc(int plotId, ServerRpcParams rpcParams = default)
    {
        ServerTryBuyFromSign(rpcParams.Receive.SenderClientId, plotId);
    }

    /// <summary>Server: unlock an expansion segment (removes its false walls on every client).</summary>
    public bool ServerTryBuySegment(ulong clientId, int plotId, int segmentIndex, bool charge = true)
    {
        if (!IsServer || !plots.TryGetValue(plotId, out var plot)) return false;
        if (!plot.IsAuthorized(clientId)) return false;
        if (segmentIndex < 0 || segmentIndex >= plot.SegmentCount) return false;
        if (plot.IsSegmentPurchased(segmentIndex)) return false;
        // Its walls go away: nothing may be hung on them (and the store's items are put away while editing).
        if (plot.IsRenovating || plot.CountItemsOnSegmentWalls(segmentIndex) > 0) return false;

        if (charge && !ServerTryCharge(GetTeamOf(clientId), plot.GetSegmentPrice(segmentIndex),
                $"segment {segmentIndex} of '{plot.DisplayName}'", plot)) return false;
        plot.ServerUnlockSegment(segmentIndex);
        return true;
    }

    [ServerRpc(RequireOwnership = false)]
    public void RequestClaimPlotServerRpc(int plotId, ServerRpcParams rpcParams = default)
    {
        ServerTryClaimPlot(rpcParams.Receive.SenderClientId, plotId);
    }

    /// <summary>
    /// Buy a segment of one of your stores from anywhere. This is the call for the shop's license /
    /// buy menu: MallManager.Instance.RequestBuySegmentServerRpc(plot.PlotId, index). Use
    /// GetSegmentOffers to list what can be bought.
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    public void RequestBuySegmentServerRpc(int plotId, int segmentIndex, ServerRpcParams rpcParams = default)
    {
        ServerTryBuySegment(rpcParams.Receive.SenderClientId, plotId, segmentIndex);
    }

    /// <summary>Server: buy a segment from its wall (only while Buy Segments From Walls is on, and only standing at it).</summary>
    public bool ServerTryBuySegmentFromWall(ulong clientId, int plotId, int segmentIndex)
    {
        if (!IsServer || !buySegmentsFromWalls || !plots.TryGetValue(plotId, out var plot)) return false;
        var player = NetworkManager.ConnectedClients.TryGetValue(clientId, out var client) ? client.PlayerObject : null;
        if (player == null || !plot.IsNearSegmentWall(segmentIndex, player.transform.position)) return false;
        return ServerTryBuySegment(clientId, plotId, segmentIndex);
    }

    [ServerRpc(RequireOwnership = false)]
    public void RequestBuySegmentFromWallServerRpc(int plotId, int segmentIndex, ServerRpcParams rpcParams = default)
    {
        ServerTryBuySegmentFromWall(rpcParams.Receive.SenderClientId, plotId, segmentIndex);
    }

    /// <summary>One segment a player's team could buy (for the license / buy menu).</summary>
    public struct SegmentOffer
    {
        public StorePlot plot;
        public int index;
        public string name;
        public int price;
    }

    /// <summary>Every segment not yet bought in the stores this player's team owns (works on any machine).</summary>
    public void GetSegmentOffers(ulong clientId, List<SegmentOffer> into)
    {
        into.Clear();
        foreach (var plot in plots.Values)
        {
            if (plot == null || !plot.IsAuthorized(clientId)) continue;
            for (int i = 0; i < plot.SegmentCount; i++)
                if (!plot.IsSegmentPurchased(i))
                    into.Add(new SegmentOffer { plot = plot, index = i, name = plot.GetSegmentName(i), price = plot.GetSegmentPrice(i) });
        }
    }

    /// <summary>Dev hotkey B: segment for free (only while the host has Dev Hotkeys on).</summary>
    [ServerRpc(RequireOwnership = false)]
    private void DevBuySegmentServerRpc(int plotId, int segmentIndex, ServerRpcParams rpcParams = default)
    {
        if (devHotkeys) ServerTryBuySegment(rpcParams.Receive.SenderClientId, plotId, segmentIndex, charge: false);
    }

    /* ========================= OPEN / CLOSED ========================= */

    /// <summary>Server: open or close a store. Only its team may.</summary>
    public bool ServerTrySetStoreClosed(ulong clientId, int plotId, bool closed)
    {
        if (!IsServer || !plots.TryGetValue(plotId, out var plot)) return false;
        if (!plot.IsAuthorized(clientId)) return false;
        plot.ServerSetClosed(closed);
        return true;
    }

    /// <summary>
    /// Open or close one of your stores, from any client. This is the call for the store menu:
    /// MallManager.Instance.RequestSetStoreClosedServerRpc(plot.PlotId, true).
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    public void RequestSetStoreClosedServerRpc(int plotId, bool closed, ServerRpcParams rpcParams = default)
    {
        ServerTrySetStoreClosed(rpcParams.Receive.SenderClientId, plotId, closed);
    }

    /* ========================= DEV ========================= */

    private void Update()
    {
        if (!devHotkeys || !IsSpawned || UIState.IsMenuOpen) return;
        bool editing = GetPlotEditedBy(NetworkManager.LocalClientId) != null; // edit mode uses B and C itself
        if (!editing && Input.GetKeyDown(KeyCode.C)) DevToggleClosed();
        if (editing || !Input.GetKeyDown(KeyCode.B)) return;

        var player = NetworkManager.LocalClient?.PlayerObject;
        if (player == null) return;

        // Uses the full footprint so you can buy a segment while standing at its edge.
        foreach (var plot in plots.Values)
        {
            if (!plot.ContainsIncludingUnpurchased(player.transform.position)) continue;

            for (int i = 0; i < plot.SegmentCount; i++)
            {
                if (plot.IsSegmentPurchased(i)) continue;
                DevBuySegmentServerRpc(plot.PlotId, i);
                Debug.Log($"[MallManager] Dev: buying segment {i} of '{plot.DisplayName}'.", plot);
                return;
            }
            Debug.Log($"[MallManager] Dev: '{plot.DisplayName}' has no segments left to buy.", plot);
            return;
        }
    }

    /// <summary>C: open/close the store you're in, or whose door you're standing at.</summary>
    private void DevToggleClosed()
    {
        var player = NetworkManager.LocalClient?.PlayerObject;
        if (player == null) return;

        Vector3 pos = player.transform.position;
        ulong me = NetworkManager.LocalClientId;
        foreach (var plot in plots.Values)
        {
            if (!plot.IsAuthorized(me)) continue;
            if (!plot.Contains(pos) && !plot.IsNearDoor(pos, 4f)) continue;

            bool close = !plot.IsClosedByOwner;
            RequestSetStoreClosedServerRpc(plot.PlotId, close);
            Debug.Log($"[MallManager] Dev: {(close ? "closing" : "opening")} '{plot.DisplayName}'.", plot);
            return;
        }
        Debug.Log("[MallManager] Dev: stand in your store (or at one of its doors) to open/close it.");
    }
}