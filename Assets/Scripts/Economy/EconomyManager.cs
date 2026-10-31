using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Money and orders. Put this on the MallManager object (it shares that NetworkObject).
///
/// Money belongs to a team (by default each player is their own team, so it's each player's
/// money; with co-op on, teammates share a wallet). The host owns every number; clients see
/// them through synced lists and ask the host to spend.
///
/// Ordering: call RequestOrderRpc(plotId, itemId, quantity, deliverySeconds). The host checks
/// the store is yours, charges the team, and after the delivery time the items appear on free
/// shipment spots at that store's dock, held for your team for a while.
///
/// For the ordering GUI:
///   EconomyManager.Instance.LocalMoney                 money of your team
///   EconomyManager.Instance.MoneyChanged               event when any team's money changes
///   EconomyManager.Instance.RequestOrderRpc(...)       place an order
///   EconomyManager.Instance.OrderResultReceived        event with the host's answer to your order
///   EconomyManager.Instance.GetOrdersForTeam(team)     pending deliveries (with arrival times)
///   EconomyManager.Instance.SecondsUntil(order)        countdown for one of them
/// </summary>
public class EconomyManager : NetworkBehaviour
{
    public static EconomyManager Instance { get; private set; }

    public enum OrderResult : byte
    {
        Ordered,
        NotYourStore,
        NoDock,
        UnknownItem,
        BadQuantity,
        NotEnoughMoney,
    }

    [Header("Deliveries")]
    [Tooltip("Seconds a delivery is held for the team that ordered it before anyone may take it (GDD: 10 minutes).")]
    [SerializeField] private float reservationSeconds = 600f;
    [Tooltip("Largest quantity a single order may ask for.")]
    [SerializeField] private int maxOrderQuantity = 100;

    [Header("Development")]
    [Tooltip("O = order Dev Order Item Id for the store you're standing in. M = give your team Dev Money Amount. " +
             "Turn off once the ordering GUI exists.")]
    [SerializeField] private bool devHotkeys = true;
    [SerializeField] private int devOrderItemId = 1;
    [SerializeField] private float devDeliverySeconds = 10f;
    [SerializeField] private int devMoneyAmount = 1000;
    [Tooltip("Small on-screen readout of your money and pending deliveries.")]
    [SerializeField] private bool showDebugHud = true;

    /* ========================= SYNCED STATE ========================= */

    public struct TeamMoney : INetworkSerializable, IEquatable<TeamMoney>
    {
        public ulong teamId;
        public int money;

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref teamId);
            s.SerializeValue(ref money);
        }

        public bool Equals(TeamMoney o) => teamId == o.teamId && money == o.money;
    }

    public struct OrderInfo : INetworkSerializable, IEquatable<OrderInfo>
    {
        public int orderId;
        public ulong teamId;
        public int plotId;
        public int itemId;
        public int quantity;   // still to arrive
        public double arriveAt; // server time

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref orderId);
            s.SerializeValue(ref teamId);
            s.SerializeValue(ref plotId);
            s.SerializeValue(ref itemId);
            s.SerializeValue(ref quantity);
            s.SerializeValue(ref arriveAt);
        }

        public bool Equals(OrderInfo o) =>
            orderId == o.orderId && teamId == o.teamId && plotId == o.plotId && itemId == o.itemId &&
            quantity == o.quantity && arriveAt.Equals(o.arriveAt);
    }

    private NetworkList<TeamMoney> money;
    private NetworkList<OrderInfo> orders;

    /// <summary>Any team's money changed (on every machine).</summary>
    public event Action MoneyChanged;
    /// <summary>The pending orders list changed (on every machine).</summary>
    public event Action OrdersChanged;
    /// <summary>The host answered one of this player's orders (on that player's machine only).</summary>
    public event Action<OrderResult> OrderResultReceived;

    // Server only
    private readonly Dictionary<int, LoadingDockZone> orderDocks = new();
    private readonly HashSet<ulong> startingMoneyGiven = new();
    private readonly List<(ulong team, int amount)> pendingGrants = new();
    private int nextOrderId = 1;

    private OrderResult? lastResult;
    private float lastResultTime;

    /* ========================= LIFECYCLE ========================= */

    private void Awake()
    {
        // NetworkLists must exist before the object spawns.
        money = new NetworkList<TeamMoney>(null, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        orders = new NetworkList<OrderInfo>(null, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        if (Instance != null && Instance != this)
        {
            Debug.LogError("[EconomyManager] More than one in the scene. Keep exactly one.", this);
            return;
        }
        Instance = this;
    }

    public override void OnNetworkSpawn()
    {
        money.OnListChanged += OnMoneyListChanged;
        orders.OnListChanged += OnOrderListChanged;

        // Starting money handed out before we were ready (the host's own team, usually).
        if (IsServer)
        {
            foreach (var (team, amount) in pendingGrants) ServerGrantStartingMoney(team, amount);
            pendingGrants.Clear();
        }
    }

    public override void OnNetworkDespawn()
    {
        money.OnListChanged -= OnMoneyListChanged;
        orders.OnListChanged -= OnOrderListChanged;
    }

    public override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    private void OnMoneyListChanged(NetworkListEvent<TeamMoney> e) => MoneyChanged?.Invoke();
    private void OnOrderListChanged(NetworkListEvent<OrderInfo> e) => OrdersChanged?.Invoke();

    /* ========================= QUERIES ========================= */

    /// <summary>A team's money (0 if it has none yet).</summary>
    public int GetTeamMoney(ulong teamId)
    {
        foreach (var m in money)
            if (m.teamId == teamId) return m.money;
        return 0;
    }

    /// <summary>A player's money (their team's wallet).</summary>
    public int GetMoneyOf(ulong clientId) => GetTeamMoney(MallManager.GetTeamOf(clientId));

    /// <summary>This player's money.</summary>
    public int LocalMoney => IsSpawned ? GetMoneyOf(NetworkManager.LocalClientId) : 0;

    /// <summary>Pending deliveries for a team, soonest first.</summary>
    public List<OrderInfo> GetOrdersForTeam(ulong teamId)
    {
        var list = new List<OrderInfo>();
        foreach (var o in orders)
            if (o.teamId == teamId) list.Add(o);
        list.Sort((a, b) => a.arriveAt.CompareTo(b.arriveAt));
        return list;
    }

    /// <summary>Seconds until an order arrives (0 once it's due; it may still be waiting for a free spot).</summary>
    public float SecondsUntil(OrderInfo order) =>
        IsSpawned ? Mathf.Max(0f, (float)(order.arriveAt - NetworkManager.ServerTime.Time)) : 0f;

    /* ========================= MONEY (SERVER) ========================= */

    public bool ServerCanAfford(ulong teamId, int amount) => amount <= 0 || GetTeamMoney(teamId) >= amount;

    public void ServerAddMoney(ulong teamId, int amount)
    {
        if (!IsServer || amount == 0) return;
        for (int i = 0; i < money.Count; i++)
        {
            if (money[i].teamId != teamId) continue;
            long total = (long)money[i].money + amount;
            money[i] = new TeamMoney { teamId = teamId, money = (int)Math.Clamp(total, int.MinValue, int.MaxValue) };
            return;
        }
        money.Add(new TeamMoney { teamId = teamId, money = amount });
    }

    /// <summary>Server: take money if the team has enough. Free (0 or less) always succeeds.</summary>
    public bool ServerTrySpend(ulong teamId, int amount)
    {
        if (!IsServer) return false;
        if (amount <= 0) return true;
        if (!ServerCanAfford(teamId, amount)) return false;
        ServerAddMoney(teamId, -amount);
        return true;
    }

    /// <summary>Server: a team's first store pays out its Starting Money, once per team.</summary>
    public void ServerGrantStartingMoney(ulong teamId, int amount)
    {
        if (!IsSpawned)
        {
            pendingGrants.Add((teamId, amount));
            return;
        }
        if (!IsServer || !startingMoneyGiven.Add(teamId)) return;
        ServerAddMoney(teamId, amount);
    }

    /// <summary>Server: a team is gone (last player left). Drop its money and pending orders.</summary>
    public void ServerForgetTeam(ulong teamId)
    {
        if (!IsServer) return;
        for (int i = money.Count - 1; i >= 0; i--)
            if (money[i].teamId == teamId) money.RemoveAt(i);
        for (int i = orders.Count - 1; i >= 0; i--)
        {
            if (orders[i].teamId != teamId) continue;
            orderDocks.Remove(orders[i].orderId);
            orders.RemoveAt(i);
        }
        startingMoneyGiven.Remove(teamId);
    }

    /* ========================= ORDERS ========================= */

    /// <summary>
    /// Order items for one of your stores. deliverySeconds &lt; 0 uses the item's own Delivery
    /// Seconds. The answer comes back through OrderResultReceived.
    /// </summary>
    [Rpc(SendTo.Server, RequireOwnership = false)]
    public void RequestOrderRpc(int plotId, int itemId, int quantity, float deliverySeconds, RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        var result = ServerTryOrder(sender, plotId, itemId, quantity, deliverySeconds);
        OrderResultRpc(result, RpcTarget.Single(sender, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void OrderResultRpc(OrderResult result, RpcParams rpcParams = default)
    {
        lastResult = result;
        lastResultTime = Time.time;
        OrderResultReceived?.Invoke(result);
    }

    /// <summary>Server: check, charge and queue an order.</summary>
    public OrderResult ServerTryOrder(ulong clientId, int plotId, int itemId, int quantity, float deliverySeconds)
    {
        var mall = MallManager.Instance;
        if (!IsServer || mall == null || !mall.TryGetPlot(plotId, out var plot) || !plot.IsAuthorized(clientId))
            return OrderResult.NotYourStore;
        if (plot.Dock == null || plot.Dock.ShipmentAreaCount == 0)
            return OrderResult.NoDock;
        if (ItemCatalog.Instance == null || !ItemCatalog.Instance.TryGet(itemId, out var def) || def.worldNetworkPrefab == null)
            return OrderResult.UnknownItem;
        if (quantity < 1 || quantity > maxOrderQuantity)
            return OrderResult.BadQuantity;

        ulong team = MallManager.GetTeamOf(clientId);
        long cost = (long)Mathf.Max(0, def.price) * quantity;
        if (cost > int.MaxValue || !ServerTrySpend(team, (int)cost))
            return OrderResult.NotEnoughMoney;

        float wait = deliverySeconds < 0f ? def.deliverySeconds : deliverySeconds;
        int id = nextOrderId++;
        orders.Add(new OrderInfo
        {
            orderId = id,
            teamId = team,
            plotId = plotId,
            itemId = itemId,
            quantity = quantity,
            arriveAt = NetworkManager.ServerTime.Time + Mathf.Max(0f, wait),
        });
        orderDocks[id] = plot.Dock; // deliver to the dock the store had when ordered
        return OrderResult.Ordered;
    }

    /* ========================= DELIVERY (SERVER) ========================= */

    private void Update()
    {
        if (IsServer && IsSpawned) ServerDeliverDueOrders();
        if (devHotkeys && IsSpawned) DevKeys();
    }

    /// <summary>Put due items on free shipment spots; anything that doesn't fit waits for a spot.</summary>
    private void ServerDeliverDueOrders()
    {
        double now = NetworkManager.ServerTime.Time;
        for (int i = orders.Count - 1; i >= 0; i--)
        {
            var o = orders[i];
            if (o.arriveAt > now) continue;

            if (!orderDocks.TryGetValue(o.orderId, out var dock) || dock == null ||
                ItemCatalog.Instance == null || !ItemCatalog.Instance.TryGet(o.itemId, out var def) ||
                def.worldNetworkPrefab == null)
            {
                Debug.LogWarning($"[EconomyManager] Dropping order {o.orderId}: its dock or item no longer exists.");
                orderDocks.Remove(o.orderId);
                orders.RemoveAt(i);
                continue;
            }

            int left = o.quantity;
            while (left > 0 && dock.TryGetFreeShipmentArea(out var area))
            {
                SpawnDelivery(def, area, o.teamId);
                left--;
            }

            if (left == 0)
            {
                orderDocks.Remove(o.orderId);
                orders.RemoveAt(i);
            }
            else if (left != o.quantity)
            {
                o.quantity = left;
                orders[i] = o;
            }
        }
    }

    private void SpawnDelivery(ItemDef def, Transform area, ulong teamId)
    {
        // Sit it on whatever is under the spot, by its base rather than its pivot.
        Vector3 pos = area.position;
        if (Physics.Raycast(pos + Vector3.up, Vector3.down, out RaycastHit hit, 3f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            pos.y = hit.point.y;
        pos.y += PlacementCheck.PivotHeightAboveBase(def);

        var spawned = Instantiate(def.worldNetworkPrefab, pos, Quaternion.Euler(0f, area.eulerAngles.y, 0f));
        spawned.Spawn(true);

        if (spawned.TryGetComponent<ItemPickupNet>(out var item))
            item.ServerReserve(teamId, reservationSeconds);
    }

    /* ========================= DEV ========================= */

    private void DevKeys()
    {
        var player = NetworkManager.LocalClient?.PlayerObject;
        if (player == null) return;

        if (Input.GetKeyDown(KeyCode.M))
            DevGiveMoneyRpc(devMoneyAmount);

        if (Input.GetKeyDown(KeyCode.O))
        {
            var plot = MallManager.Instance != null ? MallManager.Instance.GetPlotAt(player.transform.position) : null;
            if (plot == null)
            {
                Debug.Log("[EconomyManager] Dev: stand in your store to order for it.");
                return;
            }
            RequestOrderRpc(plot.PlotId, devOrderItemId, 1, devDeliverySeconds);
        }
    }

    [Rpc(SendTo.Server, RequireOwnership = false)]
    private void DevGiveMoneyRpc(int amount, RpcParams rpcParams = default)
    {
        if (!devHotkeys) return; // the host's setting decides
        ServerAddMoney(MallManager.GetTeamOf(rpcParams.Receive.SenderClientId), amount);
    }

    private void OnGUI()
    {
        if (!showDebugHud || !IsSpawned) return;

        ulong team = MallManager.GetTeamOf(NetworkManager.LocalClientId);
        var mine = GetOrdersForTeam(team);

        float h = 46f + Mathf.Min(mine.Count, 6) * 18f + (lastResult.HasValue ? 18f : 0f);
        var r = new Rect(Screen.width - 260f, 96f, 250f, h);
        GUI.Box(r, GUIContent.none);
        GUI.Label(new Rect(r.x + 8, r.y + 4, 240, 20), $"Money: ${LocalMoney:N0}");
        GUI.Label(new Rect(r.x + 8, r.y + 22, 240, 20), mine.Count == 0 ? "No deliveries pending" : "Deliveries:");

        for (int i = 0; i < mine.Count && i < 6; i++)
        {
            var o = mine[i];
            string name = ItemCatalog.Instance != null && ItemCatalog.Instance.TryGet(o.itemId, out var def)
                ? def.displayName : $"Item {o.itemId}";
            float s = SecondsUntil(o);
            string when = s > 0f ? $"{s:0}s" : "waiting for a free spot";
            GUI.Label(new Rect(r.x + 8, r.y + 40 + i * 18, 240, 20), $"  {name} x{o.quantity}: {when}");
        }

        if (lastResult.HasValue && Time.time - lastResultTime < 5f)
            GUI.Label(new Rect(r.x + 8, r.y + h - 22, 240, 20), $"Last order: {lastResult.Value}");
    }
}
