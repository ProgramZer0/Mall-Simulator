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
/// Ordering: one purchase can hold several items. The host checks the store is yours, charges
/// the team and starts one delivery. Each item arrives as its own box (ItemDef > Shipment
/// Prefab), all at once, packed into the store's shipment zones wherever they fit. The countdown
/// only runs while there's room for the whole delivery; while there isn't, it pauses.
///
/// For the ordering GUI:
///   EconomyManager.Instance.LocalMoney                         money of your team
///   EconomyManager.Instance.MoneyChanged                       event when any team's money changes
///   EconomyManager.Instance.Order(plotId, itemIds, quantities, seconds)   place a purchase
///   EconomyManager.Instance.Order(plotId, itemId, quantity, seconds)      one kind of item
///   EconomyManager.Instance.OrderResultReceived                event with the host's answer
///   EconomyManager.Instance.GetOrdersForTeam(team)             pending deliveries
///   EconomyManager.Instance.GetOrderLines(orderId)             what's in one
///   EconomyManager.Instance.SecondsUntil(order) / IsBlocked(order)
/// </summary>
public class EconomyManager : NetworkBehaviour
{
    public static EconomyManager Instance { get; private set; }

    public enum OrderResult : byte
    {
        Ordered,
        NotYourStore,
        NoShipmentAreas, // the store has no shipment zones set up
        TooBig,          // couldn't fit in the store's zones even if they were empty, so it would never arrive
        UnknownItem,
        BadQuantity,
        NotEnoughMoney,
    }

    [Header("Deliveries")]
    [Tooltip("Seconds a delivery is held for the team that ordered it before anyone may take it (GDD: 10 minutes).")]
    [SerializeField] private float reservationSeconds = 600f;
    [Tooltip("Most items (boxes) one purchase may contain.")]
    [SerializeField] private int maxItemsPerOrder = 100;
    [Tooltip("What a delivery box may not overlap when it's set down (items, boxes, walls, players...). " +
             "The Ground layer never counts, so boxes can sit on the floor.")]
    [SerializeField] private LayerMask shipmentBlockingMask = ~0;
    [Tooltip("How finely (meters) the zone floor is searched for a free spot. Smaller packs tighter but costs more.")]
    [SerializeField] private float packStep = 0.2f;
    [Tooltip("How often (seconds) a waiting delivery re-checks whether it fits.")]
    [SerializeField] private float fitCheckInterval = 1f;

    [Header("Development")]
    [Tooltip("O = order Dev Order Item Id for the store you're standing in. M = give your team Dev Money Amount. " +
             "Turn off once the ordering GUI exists.")]
    [SerializeField] private bool devHotkeys = true;
    [SerializeField] private int devOrderItemId = 1;
    [SerializeField] private int devOrderQuantity = 1;
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

    /// <summary>One pending delivery (one purchase).</summary>
    public struct OrderInfo : INetworkSerializable, IEquatable<OrderInfo>
    {
        public int orderId;
        public ulong teamId;
        public int plotId;
        public int boxCount;     // total items = boxes
        public float remaining;  // seconds left as of stampTime
        public double stampTime; // server time when remaining was recorded
        public bool blocked;     // no room in the shipment zones: countdown paused

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref orderId);
            s.SerializeValue(ref teamId);
            s.SerializeValue(ref plotId);
            s.SerializeValue(ref boxCount);
            s.SerializeValue(ref remaining);
            s.SerializeValue(ref stampTime);
            s.SerializeValue(ref blocked);
        }

        public bool Equals(OrderInfo o) =>
            orderId == o.orderId && teamId == o.teamId && plotId == o.plotId && boxCount == o.boxCount &&
            remaining.Equals(o.remaining) && stampTime.Equals(o.stampTime) && blocked == o.blocked;

        /// <summary>Seconds left at a given server time (doesn't go down while blocked).</summary>
        public float RemainingAt(double now) =>
            blocked ? remaining : Mathf.Max(0f, remaining - (float)(now - stampTime));
    }

    /// <summary>One kind of item in a delivery.</summary>
    public struct OrderLine : INetworkSerializable, IEquatable<OrderLine>
    {
        public int orderId;
        public int itemId;
        public int quantity;

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref orderId);
            s.SerializeValue(ref itemId);
            s.SerializeValue(ref quantity);
        }

        public bool Equals(OrderLine o) => orderId == o.orderId && itemId == o.itemId && quantity == o.quantity;
    }

    private NetworkList<TeamMoney> money;
    private NetworkList<OrderInfo> orders;
    private NetworkList<OrderLine> orderLines;

    /// <summary>Any team's money changed (on every machine).</summary>
    public event Action MoneyChanged;
    /// <summary>A pending delivery was added, delivered, or paused/resumed (on every machine).</summary>
    public event Action OrdersChanged;
    /// <summary>The host answered one of this player's orders (on that player's machine only).</summary>
    public event Action<OrderResult> OrderResultReceived;

    // Server only
    private readonly HashSet<ulong> startingMoneyGiven = new();
    private readonly List<(ulong team, int amount)> pendingGrants = new();
    private readonly List<ShipmentPacker.Box> planBoxes = new();
    private readonly List<(NetworkObject prefab, int itemId)> planItems = new();
    private readonly List<Pose> plan = new();
    private readonly Dictionary<int, (double nextCheck, bool fits)> fitCache = new();
    private int nextOrderId = 1;

    private OrderResult? lastResult;
    private float lastResultTime;

    /* ========================= LIFECYCLE ========================= */

    private void Awake()
    {
        // NetworkLists must exist before the object spawns.
        money = new NetworkList<TeamMoney>(null, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        orders = new NetworkList<OrderInfo>(null, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        orderLines = new NetworkList<OrderLine>(null, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

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
        double now = IsSpawned ? NetworkManager.ServerTime.Time : 0d;
        var list = new List<OrderInfo>();
        foreach (var o in orders)
            if (o.teamId == teamId) list.Add(o);
        list.Sort((a, b) => a.RemainingAt(now).CompareTo(b.RemainingAt(now)));
        return list;
    }

    /// <summary>The items in one delivery.</summary>
    public List<OrderLine> GetOrderLines(int orderId)
    {
        var list = new List<OrderLine>();
        foreach (var l in orderLines)
            if (l.orderId == orderId) list.Add(l);
        return list;
    }

    /// <summary>Seconds until a delivery arrives. Holds still while it's blocked.</summary>
    public float SecondsUntil(OrderInfo order) =>
        IsSpawned ? order.RemainingAt(NetworkManager.ServerTime.Time) : order.remaining;

    /// <summary>True while a delivery is paused because the store's shipment zones are too full.</summary>
    public bool IsBlocked(OrderInfo order) => order.blocked;

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

    /// <summary>Server: a team is gone (last player left). Drop its money and pending deliveries.</summary>
    public void ServerForgetTeam(ulong teamId)
    {
        if (!IsServer) return;
        for (int i = money.Count - 1; i >= 0; i--)
            if (money[i].teamId == teamId) money.RemoveAt(i);
        for (int i = orders.Count - 1; i >= 0; i--)
            if (orders[i].teamId == teamId) ServerRemoveOrderAt(i);
        startingMoneyGiven.Remove(teamId);
    }

    /* ========================= ORDERING ========================= */

    /// <summary>
    /// Buy several items for one of your stores in one delivery. itemIds[i] comes quantities[i]
    /// times. deliverySeconds &lt; 0 uses the slowest item's Delivery Seconds. The host's answer
    /// arrives through OrderResultReceived.
    /// </summary>
    public void Order(int plotId, int[] itemIds, int[] quantities, float deliverySeconds = -1f) =>
        RequestOrderRpc(plotId, itemIds, quantities, deliverySeconds);

    /// <summary>Buy one kind of item (any quantity) in one delivery.</summary>
    public void Order(int plotId, int itemId, int quantity, float deliverySeconds = -1f) =>
        RequestOrderRpc(plotId, new[] { itemId }, new[] { quantity }, deliverySeconds);

    [Rpc(SendTo.Server, RequireOwnership = false)]
    private void RequestOrderRpc(int plotId, int[] itemIds, int[] quantities, float deliverySeconds,
                                 RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        var result = ServerTryOrder(sender, plotId, itemIds, quantities, deliverySeconds);
        OrderResultRpc(result, RpcTarget.Single(sender, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void OrderResultRpc(OrderResult result, RpcParams rpcParams = default)
    {
        lastResult = result;
        lastResultTime = Time.time;
        OrderResultReceived?.Invoke(result);
    }

    /// <summary>Server: check, charge and start a delivery.</summary>
    public OrderResult ServerTryOrder(ulong clientId, int plotId, int[] itemIds, int[] quantities, float deliverySeconds)
    {
        var mall = MallManager.Instance;
        if (!IsServer || mall == null || !mall.TryGetPlot(plotId, out var plot) || !plot.IsAuthorized(clientId))
            return OrderResult.NotYourStore;
        if (!plot.HasShipmentZones)
            return OrderResult.NoShipmentAreas;
        if (itemIds == null || quantities == null || itemIds.Length == 0 || itemIds.Length != quantities.Length)
            return OrderResult.BadQuantity;

        long cost = 0;
        int boxes = 0;
        float slowest = 0f;
        for (int i = 0; i < itemIds.Length; i++)
        {
            if (ItemCatalog.Instance == null || !ItemCatalog.Instance.TryGet(itemIds[i], out var def) ||
                def.worldNetworkPrefab == null)
                return OrderResult.UnknownItem;
            if (quantities[i] < 1) return OrderResult.BadQuantity;

            boxes += quantities[i];
            if (boxes > maxItemsPerOrder) return OrderResult.BadQuantity;
            cost += (long)Mathf.Max(0, def.price) * quantities[i];
            slowest = Mathf.Max(slowest, def.deliverySeconds);
        }

        BuildBoxes(itemIds, quantities);
        if (!ShipmentPacker.CouldEverFit(plot.ShipmentZones, planBoxes))
            return OrderResult.TooBig;

        ulong team = MallManager.GetTeamOf(clientId);
        if (cost > int.MaxValue || !ServerTrySpend(team, (int)cost))
            return OrderResult.NotEnoughMoney;

        int id = nextOrderId++;
        orders.Add(new OrderInfo
        {
            orderId = id,
            teamId = team,
            plotId = plotId,
            boxCount = boxes,
            remaining = Mathf.Max(0f, deliverySeconds < 0f ? slowest : deliverySeconds),
            stampTime = NetworkManager.ServerTime.Time,
            blocked = false,
        });
        for (int i = 0; i < itemIds.Length; i++)
            orderLines.Add(new OrderLine { orderId = id, itemId = itemIds[i], quantity = quantities[i] });

        return OrderResult.Ordered;
    }

    /* ========================= DELIVERY (SERVER) ========================= */

    private void Update()
    {
        if (IsServer && IsSpawned) ServerUpdateOrders();
        if (devHotkeys && IsSpawned && !UIState.IsMenuOpen) DevKeys();
    }

    /// <summary>
    /// Pause or resume each delivery depending on whether its store has room for all its boxes,
    /// and drop the whole delivery once its countdown is done.
    /// </summary>
    private void ServerUpdateOrders()
    {
        var mall = MallManager.Instance;
        double now = NetworkManager.ServerTime.Time;

        for (int i = orders.Count - 1; i >= 0; i--)
        {
            var o = orders[i];
            if (mall == null || !mall.TryGetPlot(o.plotId, out var plot))
            {
                Debug.LogWarning($"[EconomyManager] Dropping delivery {o.orderId}: its store no longer exists.");
                ServerRemoveOrderAt(i);
                continue;
            }

            bool fits = CheckFits(o, plot, now);
            float left = o.RemainingAt(now);

            if (!fits)
            {
                // Pause: freeze the time left until the spots clear.
                if (!o.blocked)
                {
                    o.remaining = left;
                    o.stampTime = now;
                    o.blocked = true;
                    orders[i] = o;
                }
                continue;
            }

            if (o.blocked)
            {
                // Resume from where it paused.
                o.blocked = false;
                o.stampTime = now;
                orders[i] = o;
                continue;
            }

            if (left > 0f) continue;

            // Due: find room for every box right now; if something moved in the way, wait.
            if (!ServerDeliver(o, plot))
            {
                o.remaining = 0f;
                o.stampTime = now;
                o.blocked = true;
                orders[i] = o;
                fitCache[o.orderId] = (now + fitCheckInterval, false);
                continue;
            }
            ServerRemoveOrderAt(i);
        }
    }

    /// <summary>Does this delivery fit in its store's zones? Re-checked every Fit Check Interval.</summary>
    private bool CheckFits(OrderInfo o, StorePlot plot, double now)
    {
        if (fitCache.TryGetValue(o.orderId, out var c) && now < c.nextCheck) return c.fits;
        bool fits = PlanOrder(o, plot);
        fitCache[o.orderId] = (now + fitCheckInterval, fits);
        return fits;
    }

    /// <summary>Plans spots for all of a delivery's boxes (fills planItems + plan).</summary>
    private bool PlanOrder(OrderInfo o, StorePlot plot)
    {
        var lines = GetOrderLines(o.orderId);
        var ids = new int[lines.Count];
        var qty = new int[lines.Count];
        for (int i = 0; i < lines.Count; i++) { ids[i] = lines[i].itemId; qty[i] = lines[i].quantity; }
        BuildBoxes(ids, qty);
        return ShipmentPacker.TryPlan(plot.ShipmentZones, planBoxes, BlockingMask(), packStep, plan);
    }

    /// <summary>Fills planBoxes/planItems with one entry per box for these items.</summary>
    private void BuildBoxes(int[] itemIds, int[] quantities)
    {
        planBoxes.Clear();
        planItems.Clear();
        for (int i = 0; i < itemIds.Length; i++)
        {
            if (ItemCatalog.Instance == null || !ItemCatalog.Instance.TryGet(itemIds[i], out var def)) continue;
            var prefab = def.GetShipmentPrefab();
            if (prefab == null) continue;
            var box = ShipmentPacker.GetBox(prefab.gameObject);
            for (int q = 0; q < quantities[i]; q++)
            {
                planBoxes.Add(box);
                planItems.Add((prefab, itemIds[i]));
            }
        }
    }

    private int BlockingMask()
    {
        int mask = shipmentBlockingMask.value;
        int ground = LayerMask.NameToLayer("Ground");
        if (ground >= 0) mask &= ~(1 << ground);
        return mask;
    }

    /// <summary>Plans fresh spots and spawns every box of a delivery. False if it doesn't fit right now.</summary>
    private bool ServerDeliver(OrderInfo order, StorePlot plot)
    {
        if (!PlanOrder(order, plot) || plan.Count != planItems.Count) return false;
        for (int i = 0; i < plan.Count; i++)
            SpawnBox(planItems[i].prefab, planItems[i].itemId, plan[i], order.teamId);
        return true;
    }

    private void SpawnBox(NetworkObject prefab, int itemId, Pose pose, ulong teamId)
    {
        var spawned = Instantiate(prefab, pose.position, pose.rotation);
        if (spawned.TryGetComponent<ShipmentPackage>(out var box))
            box.ServerSetContents(itemId);
        else if (!spawned.TryGetComponent<ItemPickupNet>(out var plain) || plain.itemId != itemId)
            Debug.LogWarning($"[EconomyManager] Shipment prefab '{prefab.name}' has no ShipmentPackage, so " +
                             "picking it up won't give the item inside.", prefab);
        spawned.Spawn(true);
        Physics.SyncTransforms(); // so the next plan sees this box

        if (box != null) box.ServerReserve(teamId, reservationSeconds);
    }

    private void ServerRemoveOrderAt(int index)
    {
        int id = orders[index].orderId;
        fitCache.Remove(id);
        for (int i = orderLines.Count - 1; i >= 0; i--)
            if (orderLines[i].orderId == id) orderLines.RemoveAt(i);
        orders.RemoveAt(index);
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
            Order(plot.PlotId, devOrderItemId, Mathf.Max(1, devOrderQuantity), devDeliverySeconds);
        }
    }

    [Rpc(SendTo.Server, RequireOwnership = false)]
    private void DevGiveMoneyRpc(int amount, RpcParams rpcParams = default)
    {
        if (!devHotkeys) return; // the host's setting decides
        ServerAddMoney(MallManager.GetTeamOf(rpcParams.Receive.SenderClientId), amount);
    }

    // The HUD text is rebuilt a few times a second instead of on every OnGUI call (which runs
    // several times per frame), so it doesn't make garbage every frame.
    private const float HudRefreshSeconds = 0.25f;
    private readonly List<string> hudLines = new();
    private readonly List<string> hudParts = new();
    private readonly System.Text.StringBuilder hudSb = new();
    private float hudNextRefresh;

    private void RefreshHud()
    {
        hudLines.Clear();
        hudLines.Add($"Money: ${LocalMoney:N0}");

        ulong team = MallManager.GetTeamOf(NetworkManager.LocalClientId);
        var mine = GetOrdersForTeam(team);
        hudLines.Add(mine.Count == 0 ? "No deliveries pending" : "Deliveries:");

        int shown = Mathf.Min(mine.Count, 6);
        for (int i = 0; i < shown; i++)
        {
            var o = mine[i];
            hudParts.Clear();
            foreach (var l in GetOrderLines(o.orderId))
            {
                string name = ItemCatalog.Instance != null && ItemCatalog.Instance.TryGet(l.itemId, out var def)
                    ? def.displayName : $"Item {l.itemId}";
                hudParts.Add($"{name} x{l.quantity}");
            }
            hudSb.Clear().Append("  ");
            for (int p = 0; p < hudParts.Count; p++) hudSb.Append(p == 0 ? "" : ", ").Append(hudParts[p]);
            hudSb.Append(": ").Append(o.blocked ? $"paused, no room ({SecondsUntil(o):0}s left)" : $"{SecondsUntil(o):0}s");
            hudLines.Add(hudSb.ToString());
        }

        if (lastResult.HasValue && Time.time - lastResultTime < 5f)
            hudLines.Add($"Last order: {lastResult.Value}");
    }

    private void OnGUI()
    {
        if (!showDebugHud || !Debug.isDebugBuild || !IsSpawned) return;

        if (Event.current.type == EventType.Layout && Time.unscaledTime >= hudNextRefresh)
        {
            hudNextRefresh = Time.unscaledTime + HudRefreshSeconds;
            RefreshHud();
        }
        if (hudLines.Count == 0) return;

        var r = new Rect(Screen.width - 330f, 96f, 320f, 10f + hudLines.Count * 18f);
        GUI.Box(r, GUIContent.none);
        for (int i = 0; i < hudLines.Count; i++)
            GUI.Label(new Rect(r.x + 8, r.y + 4 + i * 18, 310, 20), hudLines[i]);
    }
}
