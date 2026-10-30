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
/// </summary>
public class MallManager : NetworkBehaviour
{
    public static MallManager Instance { get; private set; }

    [Header("Ownership")]
    [Tooltip("Give every player a free starter store when they join.")]
    [SerializeField] private bool assignStarterPlots = true;
    [Tooltip("Free a player's stores when they leave. (Client ids change on reconnect, so " +
             "keeping them would orphan the store until saves/persistent ids exist.)")]
    [SerializeField] private bool releaseOnDisconnect = true;
    [Tooltip("Stores one player may own. Will come from Player Level later.")]
    [SerializeField] private int maxStoresPerPlayer = 1;

    [Header("Development")]
    [Tooltip("B = buy the next segment of the store you're standing in (no money check). " +
             "Turn off once the store menu exists.")]
    [SerializeField] private bool devHotkeys = true;

    private readonly Dictionary<int, StorePlot> plots = new();

    public IReadOnlyCollection<StorePlot> Plots => plots.Values;

    /* ========================= LIFECYCLE ========================= */

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogError("[MallManager] More than one in the scene. Keep exactly one.", this);
            return;
        }
        Instance = this;
    }

    public override void OnNetworkSpawn()
    {
        // Plots may have spawned before us; pick them all up (Register ignores repeats).
        foreach (var plot in FindObjectsByType<StorePlot>(FindObjectsSortMode.None))
            Register(plot);

        if (!IsServer) return;

        NetworkManager.OnClientConnectedCallback += OnClientConnected;
        NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;

        // Anyone already connected (the host itself) gets a store too.
        foreach (ulong id in NetworkManager.ConnectedClientsIds)
            OnClientConnected(id);
    }

    public override void OnNetworkDespawn()
    {
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

    public int CountPlotsOwnedBy(ulong clientId)
    {
        int n = 0;
        foreach (var plot in plots.Values)
            if (plot.HasOwner && plot.OwnerClientId == clientId) n++;
        return n;
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
    public static bool CanPickupOrPlace(ulong clientId, Vector3 playerPosition, Vector3 targetPosition)
    {
        if (Instance == null) return true;
        return Instance.IsWorkZoneFor(clientId, playerPosition) &&
               Instance.IsWorkZoneFor(clientId, targetPosition);
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
        if (!assignStarterPlots || CountPlotsOwnedBy(clientId) > 0) return;

        foreach (var plot in plots.Values)
        {
            if (plot.IsStarterPlot && !plot.HasOwner)
            {
                plot.ServerSetOwner(clientId);
                return;
            }
        }

        Debug.LogWarning($"[MallManager] No free starter store for client {clientId}. " +
                         "Add more StorePlots marked Is Starter Plot.", this);
    }

    private void OnClientDisconnected(ulong clientId)
    {
        // Never leave a store stuck in edit mode (closed, items floating) after its editor leaves.
        ServerStopEdit(clientId);

        if (!releaseOnDisconnect) return;

        foreach (var plot in plots.Values)
            if (plot.HasOwner && plot.OwnerClientId == clientId)
                plot.ServerRelease();
    }

    /// <summary>
    /// Server: put the store the player is standing in into edit mode, with them as the editor.
    /// Fails if it isn't their store or someone else is already editing it.
    /// </summary>
    public bool ServerTryStartEdit(ulong clientId, Vector3 playerPosition)
    {
        if (!IsServer) return false;

        var plot = GetPlotAt(playerPosition);
        if (plot == null || !plot.IsAuthorized(clientId)) return false;
        if (plot.IsRenovating) return plot.EditorClientId == clientId;

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
        if (CountPlotsOwnedBy(clientId) >= maxStoresPerPlayer)
        {
            Debug.Log($"[MallManager] Client {clientId} can't buy '{plot.DisplayName}': already owns " +
                      $"{maxStoresPerPlayer} store(s) (Max Stores Per Player).", plot);
            return false;
        }

        // TODO economy: charge plot.Price.
        plot.ServerSetOwner(clientId);
        Debug.Log($"[MallManager] Client {clientId} bought '{plot.DisplayName}'.", plot);
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
    public bool ServerTryBuySegment(ulong clientId, int plotId, int segmentIndex)
    {
        if (!IsServer || !plots.TryGetValue(plotId, out var plot)) return false;
        if (!plot.IsAuthorized(clientId)) return false;
        if (segmentIndex < 0 || segmentIndex >= plot.SegmentCount) return false;
        if (plot.IsSegmentPurchased(segmentIndex)) return false;

        // TODO economy: charge plot.GetSegmentPrice(segmentIndex).
        plot.ServerUnlockSegment(segmentIndex);
        return true;
    }

    [ServerRpc(RequireOwnership = false)]
    public void RequestClaimPlotServerRpc(int plotId, ServerRpcParams rpcParams = default)
    {
        ServerTryClaimPlot(rpcParams.Receive.SenderClientId, plotId);
    }

    [ServerRpc(RequireOwnership = false)]
    public void RequestBuySegmentServerRpc(int plotId, int segmentIndex, ServerRpcParams rpcParams = default)
    {
        ServerTryBuySegment(rpcParams.Receive.SenderClientId, plotId, segmentIndex);
    }

    /* ========================= DEV ========================= */

    private void Update()
    {
        if (!devHotkeys || !IsSpawned || !Input.GetKeyDown(KeyCode.B)) return;

        var player = NetworkManager.LocalClient?.PlayerObject;
        if (player == null) return;

        // Uses the full footprint so you can buy a segment while standing at its edge.
        foreach (var plot in plots.Values)
        {
            if (!plot.ContainsIncludingUnpurchased(player.transform.position)) continue;

            for (int i = 0; i < plot.SegmentCount; i++)
            {
                if (plot.IsSegmentPurchased(i)) continue;
                RequestBuySegmentServerRpc(plot.PlotId, i);
                Debug.Log($"[MallManager] Dev: buying segment {i} of '{plot.DisplayName}'.", plot);
                return;
            }
            Debug.Log($"[MallManager] Dev: '{plot.DisplayName}' has no segments left to buy.", plot);
            return;
        }
    }
}