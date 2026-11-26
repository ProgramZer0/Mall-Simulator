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
        LevelTooLow,     // an item needs a higher team level (ItemDef Min Level)
        NeedsLicense,    // an item is in a licensed category the team doesn't hold (LicenseManager)
        NeedsFranchise,  // a franchise's item, and the team doesn't hold the franchise (FranchiseManager)
    }

    [Header("Deliveries")]
    [Tooltip("Seconds a delivery is held for the team that ordered it before anyone may take it (GDD: 10 minutes).")]
    [SerializeField] private float reservationSeconds = 600f;

    /// <summary>How long a delivery is held for the team that ordered it (seconds).</summary>
    public float ReservationSeconds => reservationSeconds;
    [Tooltip("A delivery's boxes come down one at a time, this many seconds apart (0: all at once).")]
    [SerializeField, Min(0f)] private float boxSpawnInterval = 0.3f;
    [Tooltip("One delivery at a time per store: another order waits in line, and its clock only starts once the one " +
             "ahead of it has been delivered. Off: every order counts down at once.")]
    [SerializeField] private bool oneDeliveryAtATime = true;
    [Tooltip("Most items (boxes) one purchase may contain.")]
    [SerializeField] private int maxItemsPerOrder = 100;
    [Tooltip("What a delivery box may not overlap when it's set down (items, boxes, walls, players...). " +
             "The Ground layer never counts, so boxes can sit on the floor.")]
    [SerializeField] private LayerMask shipmentBlockingMask = ~0;
    [Tooltip("How finely (meters) the zone floor is searched for a free spot. Smaller packs tighter but costs more.")]
    [SerializeField] private float packStep = 0.2f;
    [Tooltip("A delivery that's due but has no room at the dock looks again this often (seconds).")]
    [SerializeField] private float fitCheckInterval = 1f;

    [Header("Trucks (stores whose dock has Truck Bays)")]
    [Tooltip("The delivery trucks, smallest first (small, medium, large). A shipment comes in the smallest whose " +
             "cargo floor it fills no more than Truck Max Fill of. Empty: deliveries are set down on the shipment " +
             "spots as before.")]
    [SerializeField] private DeliveryTruck[] truckPrefabs = new DeliveryTruck[0];
    [Tooltip("How much of a truck's cargo floor a shipment may take (0.75 = 75%) before it needs the next size.")]
    [SerializeField, Range(0.2f, 1f)] private float truckMaxFill = 0.75f;
    [Tooltip("Seconds added to (or taken off) the delivery time for each truck size: small, medium, large.")]
    [SerializeField] private float[] truckSizeSeconds = { -30f, 0f, 45f };
    [Tooltip("Delivery time = the slowest item's time + this much of all the items' times added up (0.05 = 5%) " +
             "+ the truck size's seconds (then Logistics perks).")]
    [SerializeField, Range(0f, 1f)] private float truckTotalTimeShare = 0.05f;
    [Tooltip("Seconds the truck waits at the dock for someone to open its door; then it leaves with the shipment " +
             "and part of the cost comes back (Unopened Refund).")]
    [SerializeField, Min(10f)] private float truckDoorWaitSeconds = 180f;
    [SerializeField, Range(0f, 1f)] private float truckUnopenedRefund = 0.5f;
    [Tooltip("Seconds to unload once the door's open; then the door shuts and the truck leaves with whatever's " +
             "still on it (no refund).")]
    [SerializeField, Min(10f)] private float truckUnloadSeconds = 120f;
    [Tooltip("Emptied: it leaves this many seconds later.")]
    [SerializeField, Min(0f)] private float truckLeaveWhenEmptySeconds = 4f;
    [Tooltip("Someone's in the truck as its door comes down: it goes back up, their team pays this fine, and they " +
             "get Truck Get Out Seconds more to get out.")]
    [SerializeField, Min(0)] private int truckInsideFine = 50;
    [SerializeField, Min(1f)] private float truckGetOutSeconds = 10f;

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
        public bool blocked;     // due, but no room in the shipment zones yet: waiting
        public bool inLine;      // behind another delivery to the same store: its clock hasn't started
        public int cost;         // what it cost (for a refund)
        public sbyte truckSize;  // which truck brings it (-1: none, set down on the shipment spots)
        public byte stage;       // trucks: 0 on its way, 1 waiting for a bay, 2 at the dock (door shut), 3 unloading, 4 door closing
        public double deadline;  // trucks: server time the truck leaves (stages 2 and 3)

        public const byte OnItsWay = 0, WaitingForBay = 1, AtDock = 2, Unloading = 3, Closing = 4;

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref orderId);
            s.SerializeValue(ref teamId);
            s.SerializeValue(ref plotId);
            s.SerializeValue(ref boxCount);
            s.SerializeValue(ref remaining);
            s.SerializeValue(ref stampTime);
            s.SerializeValue(ref blocked);
            s.SerializeValue(ref inLine);
            s.SerializeValue(ref cost);
            s.SerializeValue(ref truckSize);
            s.SerializeValue(ref stage);
            s.SerializeValue(ref deadline);
        }

        public bool Equals(OrderInfo o) =>
            orderId == o.orderId && teamId == o.teamId && plotId == o.plotId && boxCount == o.boxCount &&
            remaining.Equals(o.remaining) && stampTime.Equals(o.stampTime) && blocked == o.blocked && inLine == o.inLine &&
            cost == o.cost && truckSize == o.truckSize && stage == o.stage && deadline.Equals(o.deadline);

        /// <summary>Seconds left at a given server time (doesn't go down while blocked or in line).</summary>
        public float RemainingAt(double now) =>
            blocked || inLine ? remaining : Mathf.Max(0f, remaining - (float)(now - stampTime));
    }

    /// <summary>One kind of item in a delivery.</summary>
    public struct OrderLine : INetworkSerializable, IEquatable<OrderLine>
    {
        public int orderId;
        public int itemId;
        public int quantity;
        public byte paint;   // PaintPalette color id the items come in (0 = their own colors)

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref orderId);
            s.SerializeValue(ref itemId);
            s.SerializeValue(ref quantity);
            s.SerializeValue(ref paint);
        }

        public bool Equals(OrderLine o) => orderId == o.orderId && itemId == o.itemId && quantity == o.quantity && paint == o.paint;
    }

    private NetworkList<TeamMoney> money;
    private NetworkList<OrderInfo> orders;
    private NetworkList<OrderLine> orderLines;
    private readonly HashSet<int> dockFullNoticed = new(); // host: orders already reported waiting for room
    private readonly Dictionary<int, float> soonLeft = new(); // host: each order's time left at the last check
    private const float SoonSeconds = 60f;

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
    private readonly List<(NetworkObject prefab, int itemId, byte paint)> planItems = new();
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
        foreach (var m in money.Each())
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
        foreach (var o in orders.Each())
            if (o.teamId == teamId) list.Add(o);
        list.Sort((a, b) => a.RemainingAt(now).CompareTo(b.RemainingAt(now)));
        return list;
    }

    /// <summary>The items in one delivery.</summary>
    public List<OrderLine> GetOrderLines(int orderId)
    {
        var list = new List<OrderLine>();
        foreach (var l in orderLines.Each())
            if (l.orderId == orderId) list.Add(l);
        return list;
    }

    /// <summary>Seconds until a delivery arrives (0 once it's due, including while it waits for room).</summary>
    public float SecondsUntil(OrderInfo order) =>
        IsSpawned ? order.RemainingAt(NetworkManager.ServerTime.Time) : order.remaining;

    /// <summary>True while a delivery is due but waiting for room in the store's shipment zones.</summary>
    public bool IsBlocked(OrderInfo order) => order.blocked;

    /// <summary>True while a delivery waits behind another one to the same store (its clock hasn't started).</summary>
    public bool IsInLine(OrderInfo order) => order.inLine;

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

    // Sales are in cents but money is in whole dollars: the cents left over wait here until they add up.
    private readonly Dictionary<ulong, int> centsLeftOver = new();

    /// <summary>Server: add money in cents (a sale). Whole dollars go in now; the cents carry over.</summary>
    public void ServerAddCents(ulong teamId, int cents)
    {
        if (!IsServer || cents <= 0) return;
        centsLeftOver.TryGetValue(teamId, out int left);
        left += cents;
        int dollars = left / 100;
        centsLeftOver[teamId] = left % 100;
        if (dollars > 0) ServerAddMoney(teamId, dollars);
    }

    /// <summary>
    /// Server: take money in cents whether or not the team has it all (e.g. too much change handed back);
    /// what they don't have is just gone, they never go below zero.
    /// </summary>
    public void ServerTakeCents(ulong teamId, int cents)
    {
        if (!IsServer || cents <= 0) return;
        centsLeftOver.TryGetValue(teamId, out int left);
        long have = (long)GetTeamMoney(teamId) * 100 + left;
        long after = Math.Max(0L, have - cents);
        centsLeftOver[teamId] = (int)(after % 100);
        ServerAddMoney(teamId, (int)(after / 100) - GetTeamMoney(teamId));
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

    /// <summary>
    /// Server: take money in cents if the team has enough (its dollars plus any cents it has waiting).
    /// For buying things priced in cents, like another player's goods.
    /// </summary>
    public bool ServerTrySpendCents(ulong teamId, int cents)
    {
        if (!IsServer) return false;
        if (cents <= 0) return true;
        centsLeftOver.TryGetValue(teamId, out int left);
        long have = (long)GetTeamMoney(teamId) * 100 + left;
        if (have < cents) return false;
        long after = have - cents;
        int dollars = (int)(after / 100);
        centsLeftOver[teamId] = (int)(after % 100);
        ServerAddMoney(teamId, dollars - GetTeamMoney(teamId));
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
        RequestOrderRpc(plotId, itemIds, quantities, new byte[0], deliverySeconds);

    /// <summary>
    /// Same, with a paint color for each line (PaintPalette id; 0 = the item's own colors). Painting at
    /// purchase is free; items that can't be painted ignore it.
    /// </summary>
    public void Order(int plotId, int[] itemIds, int[] quantities, byte[] paints, float deliverySeconds = -1f) =>
        RequestOrderRpc(plotId, itemIds, quantities, paints ?? new byte[0], deliverySeconds);

    /// <summary>Buy one kind of item (any quantity) in one delivery.</summary>
    public void Order(int plotId, int itemId, int quantity, float deliverySeconds = -1f) =>
        RequestOrderRpc(plotId, new[] { itemId }, new[] { quantity }, new byte[0], deliverySeconds);

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestOrderRpc(int plotId, int[] itemIds, int[] quantities, byte[] paints, float deliverySeconds,
                                 RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        var result = ServerTryOrder(sender, plotId, itemIds, quantities, deliverySeconds, paints);
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
    public OrderResult ServerTryOrder(ulong clientId, int plotId, int[] itemIds, int[] quantities, float deliverySeconds,
                                      byte[] paints = null, int[] unitPrices = null, bool anyoneMayBuy = false)
    {
        // Anyone May Buy: no level, license or franchise needed (the special market: first come, first served).
        // Unit Prices (optional, dollars per item, delivery included): a set price instead of the
        // catalog's, e.g. a special market deal.
        int UnitCost(int i, ItemDef d) => unitPrices != null && i < unitPrices.Length && unitPrices[i] >= 0
            ? unitPrices[i]
            : MarketModifiers.OrderCost(d) + MarketModifiers.DeliveryFee(d);
        var mall = MallManager.Instance;
        if (!IsServer || mall == null || !mall.TryGetPlot(plotId, out var plot) || !plot.IsAuthorized(clientId))
            return OrderResult.NotYourStore;
        if (!plot.HasShipmentZones)
            return OrderResult.NoShipmentAreas;
        if (itemIds == null || quantities == null || itemIds.Length == 0 || itemIds.Length != quantities.Length)
            return OrderResult.BadQuantity;

        ulong team = MallManager.GetTeamOf(clientId);
        long cost = 0;
        int boxes = 0;
        float slowest = 0f, allTimes = 0f;
        for (int i = 0; i < itemIds.Length; i++)
        {
            if (ItemCatalog.Instance == null || !ItemCatalog.Instance.TryGet(itemIds[i], out var def) ||
                def.worldNetworkPrefab == null)
                return OrderResult.UnknownItem;
            if (quantities[i] < 1) return OrderResult.BadQuantity;
            if (!anyoneMayBuy)
            {
                if (!ProgressionManager.CanOrder(team, def)) return OrderResult.LevelTooLow;
                if (!LicenseManager.CanOrder(team, def, out _)) return OrderResult.NeedsLicense;
                if (!FranchiseManager.CanOrder(team, def, out _)) return OrderResult.NeedsFranchise;
            }

            boxes += quantities[i];
            if (boxes > maxItemsPerOrder) return OrderResult.BadQuantity;
            // Running mall events can change the cost, add a delivery fee and change delivery time.
            cost += (long)UnitCost(i, def) * quantities[i];
            slowest = Mathf.Max(slowest, MarketModifiers.DeliverySeconds(def));
            allTimes += MarketModifiers.DeliverySeconds(def) * quantities[i];
        }

        BuildBoxes(itemIds, quantities, null);
        // By truck (the store's dock has bays): the smallest truck it fits; else on the shipment spots.
        bool byTruck = UsesTrucks(plot);
        if (byTruck)
        {
            foreach (var b in planBoxes) if (!FitsBiggestTruck(b)) return OrderResult.TooBig; // one box too big for any truck
        }
        else if (!ShipmentPacker.CouldEverFit(plot.ShipmentZones, planBoxes))
            return OrderResult.TooBig;

        if (cost > int.MaxValue || !ServerTrySpend(team, (int)cost))
            return OrderResult.NotEnoughMoney;

        // Licenses used by this order (giving up an unused one earns a help point).
        if (LicenseManager.Instance != null)
            for (int i = 0; i < itemIds.Length; i++)
                if (ItemCatalog.Instance.TryGet(itemIds[i], out var licensed)) LicenseManager.Instance.ServerNoteOrdered(team, licensed);

        // What was paid, for the store's profit when it sells (see ProgressionManager).
        var progression = ProgressionManager.Instance;
        if (progression != null)
            for (int i = 0; i < itemIds.Length; i++)
                if (ItemCatalog.Instance.TryGet(itemIds[i], out var paidFor))
                    progression.ServerRecordOrder(team, itemIds[i], quantities[i], (long)UnitCost(i, paidFor) * quantities[i]);

        // Logistics perks: sooner deliveries (at most 75% off).
        float sooner = 1f - Mathf.Clamp(ProgressionManager.StoreStat(plot, PerkStat.FasterDelivery), 0f, 0.75f);

        // The colors the items come in: only on items that can be painted, only a real color, and (from the
        // shop) only one the item comes in that the team has unlocked; otherwise their own colors.
        var lineColors = new byte[itemIds.Length];
        for (int i = 0; i < itemIds.Length; i++)
        {
            byte paint = paints != null && i < paints.Length ? paints[i] : (byte)0;
            if (!PaintPalette.IsValid(paint) || !(ItemCatalog.Instance.TryGet(itemIds[i], out var d) && PaintParts.IsPaintable(d)))
                paint = 0;
            else if (!anyoneMayBuy && !ColorRules.Allows(d, paint, team, out _))
                paint = 0;
            lineColors[i] = paint;
        }

        // By truck: as many truckloads as it takes (by volume), each its own delivery in the smallest truck
        // it fits; else one delivery to the shipment spots.
        var loads = byTruck ? SplitIntoTruckloads(itemIds, quantities) : new List<int[]> { (int[])quantities.Clone() };
        foreach (var load in loads)
        {
            int count = 0;
            long loadCost = 0;
            float loadSlowest = 0f, loadAll = 0f;
            for (int i = 0; i < itemIds.Length; i++)
            {
                if (load[i] <= 0 || !ItemCatalog.Instance.TryGet(itemIds[i], out var def)) continue;
                count += load[i];
                loadCost += (long)UnitCost(i, def) * load[i];
                loadSlowest = Mathf.Max(loadSlowest, MarketModifiers.DeliverySeconds(def));
                loadAll += MarketModifiers.DeliverySeconds(def) * load[i];
            }
            if (count == 0) continue;
            sbyte truckSize = -1;
            if (byTruck)
            {
                BuildBoxes(itemIds, load, null);
                truckSize = (sbyte)Mathf.Max(0, PickTruckSize(planBoxes));
            }
            float baseTime = deliverySeconds < 0f ? (byTruck ? loadSlowest : slowest) : deliverySeconds;

            int id = nextOrderId++;
            orders.Add(new OrderInfo
            {
                orderId = id,
                teamId = team,
                plotId = plotId,
                boxCount = count,
                remaining = Mathf.Max(0f, TruckTime(baseTime, loadAll, truckSize) * sooner),
                stampTime = NetworkManager.ServerTime.Time,
                blocked = false,
                inLine = oneDeliveryAtATime && HasOrderFor(plotId, waitingOnly: truckSize >= 0),
                cost = (int)Mathf.Min(int.MaxValue, loadCost),
                truckSize = truckSize,
                stage = OrderInfo.OnItsWay,
            });
            for (int i = 0; i < itemIds.Length; i++)
                if (load[i] > 0)
                    orderLines.Add(new OrderLine { orderId = id, itemId = itemIds[i], quantity = load[i], paint = lineColors[i] });
        }

        return OrderResult.Ordered;
    }

    /* ========================= DELIVERY (SERVER) ========================= */

    private System.Action drawHud;

    private void Update()
    {
        ImGuiHost.Set(this, showDebugHud && Debug.isDebugBuild && IsSpawned ? drawHud ??= DrawHud : null);
        if (IsServer && IsSpawned) { ServerSpawnQueued(); ServerUpdateOrders(); ServerDespawnGoneTrucks(); }
        if (devHotkeys && IsSpawned && !UIState.IsMenuOpen) DevKeys();
    }

    /// <summary>
    /// Count each delivery down, and when it's due, set its boxes down if there's room. If there isn't,
    /// it waits at the dock (re-checking every Fit Check Interval) until there is. Nothing checks for
    /// room before then.
    /// </summary>
    private bool HasOrderFor(int plotId, bool waitingOnly = false)
    {
        // (Trucks: one at the dock doesn't hold up the next one's clock; only one on its way does.)
        foreach (var o in orders.Each()) if (o.plotId == plotId && (!waitingOnly || o.truckSize < 0 || o.stage < OrderInfo.AtDock)) return true;
        return false;
    }

    // One delivery at a time per store: when none is on its way, the next in line (the oldest) starts.
    // (Off: everything in line starts now.)
    private void ServerStartNextInLine(double now)
    {
        for (int i = 0; i < orders.Count; i++)
        {
            var o = orders[i];
            if (!o.inLine) continue;
            bool ahead = false;
            if (oneDeliveryAtATime)
                for (int j = 0; j < orders.Count && !ahead; j++)
                {
                    var other = orders[j];
                    if (j != i && other.plotId == o.plotId && (!other.inLine || other.orderId < o.orderId) &&
                        !(o.truckSize >= 0 && other.truckSize >= 0 && other.stage >= OrderInfo.AtDock)) ahead = true; // a truck at the dock doesn't hold it up
                }
            if (ahead) continue;
            o.inLine = false;
            o.stampTime = now;
            orders[i] = o;
        }
    }

    private void ServerUpdateOrders()
    {
        var mall = MallManager.Instance;
        double now = NetworkManager.ServerTime.Time;
        ServerStartNextInLine(now);

        for (int i = orders.Count - 1; i >= 0; i--)
        {
            var o = orders[i];
            if (mall == null || !mall.TryGetPlot(o.plotId, out var plot))
            {
                Debug.LogWarning($"[EconomyManager] Dropping delivery {o.orderId}: its store no longer exists.");
                ServerRemoveOrderAt(i);
                continue;
            }

            if (o.inLine) continue;
            float left = o.RemainingAt(now);

            // A minute to go (only for deliveries that had longer than that).
            if (soonLeft.TryGetValue(o.orderId, out float before) && before > SoonSeconds && left <= SoonSeconds &&
                NotificationManager.Instance != null)
                NotificationManager.Instance.ServerNotifyTeam(o.teamId, NotificationManager.Kind.DeliverySoon, o.plotId, o.boxCount);
            soonLeft[o.orderId] = left;

            if (o.truckSize >= 0)
            {
                if (left <= 0f || o.stage > OrderInfo.OnItsWay) ServerTruckStep(i, o, plot, now);
                continue;
            }
            if (left > 0f) continue;

            // Due. Another delivery's boxes still coming down in this store: after them (their spots are taken).
            if (QueuedFor(o.plotId)) continue;
            // Waiting for room: only look again every Fit Check Interval.
            if (o.blocked && fitCache.TryGetValue(o.orderId, out var c) && now < c.nextCheck) continue;

            if (!ServerDeliver(o, plot))
            {
                if (!o.blocked)
                {
                    o.remaining = 0f;
                    o.stampTime = now;
                    o.blocked = true;
                    orders[i] = o;
                }
                fitCache[o.orderId] = (now + fitCheckInterval, false);
                // Once per order: it'd otherwise nag every check while the dock stays full.
                if (dockFullNoticed.Add(o.orderId) && NotificationManager.Instance != null)
                    NotificationManager.Instance.ServerNotifyTeam(o.teamId, NotificationManager.Kind.DockFull, o.plotId);
                continue;
            }
            if (NotificationManager.Instance != null)
                NotificationManager.Instance.ServerNotifyTeam(o.teamId, NotificationManager.Kind.Delivered, o.plotId, o.boxCount);
            ServerRemoveOrderAt(i);
        }
    }

    /// <summary>Plans spots for all of a delivery's boxes (fills planItems + plan).</summary>
    private bool PlanOrder(OrderInfo o, StorePlot plot)
    {
        var lines = GetOrderLines(o.orderId);
        var ids = new int[lines.Count];
        var qty = new int[lines.Count];
        var paints = new byte[lines.Count];
        for (int i = 0; i < lines.Count; i++) { ids[i] = lines[i].itemId; qty[i] = lines[i].quantity; paints[i] = lines[i].paint; }
        BuildBoxes(ids, qty, paints);
        return ShipmentPacker.TryPlan(plot.ShipmentZones, planBoxes, BlockingMask(), packStep, plan);
    }

    /// <summary>Fills planBoxes/planItems with one entry per box for these items.</summary>
    private void BuildBoxes(int[] itemIds, int[] quantities, byte[] paints)
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
                planItems.Add((prefab, itemIds[i], paints != null && i < paints.Length ? paints[i] : (byte)0));
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

    // Boxes planned but not down yet (Box Spawn Interval apart), in order.
    private struct QueuedBox
    {
        public NetworkObject prefab;
        public int itemId, plotId;
        public byte paint;
        public Pose pose;
        public ulong teamId;
        public float at; // Time.time it comes down
        public int truckOrder; // on a truck: its order (0: not)
    }
    private readonly List<QueuedBox> queued = new();

    private bool QueuedFor(int plotId)
    {
        foreach (var q in queued) if (q.plotId == plotId) return true;
        return false;
    }

    // Host: the boxes whose turn it is.
    private void ServerSpawnQueued()
    {
        int done = 0;
        while (done < queued.Count && Time.time >= queued[done].at)
        {
            var q = queued[done++];
            if (q.prefab == null) continue;
            var no = SpawnBox(q.prefab, q.itemId, q.paint, q.pose, q.teamId);
            if (q.truckOrder != 0 && no != null) TruckBoxes(q.truckOrder).Add(no);
        }
        if (done > 0) queued.RemoveRange(0, done);
    }

    /// <summary>
    /// Plans fresh spots for every box of a delivery, then sets them down one at a time (Box Spawn Interval
    /// apart; the first straight away). False if it doesn't fit right now.
    /// </summary>
    private bool ServerDeliver(OrderInfo order, StorePlot plot)
    {
        if (!PlanOrder(order, plot) || plan.Count != planItems.Count) return false;
        float t = Time.time;
        foreach (var q in queued) if (q.at + boxSpawnInterval > t) t = q.at + boxSpawnInterval; // after any still coming
        for (int i = 0; i < plan.Count; i++)
        {
            var box = new QueuedBox
            {
                prefab = planItems[i].prefab, itemId = planItems[i].itemId, paint = planItems[i].paint,
                pose = plan[i], teamId = order.teamId, plotId = order.plotId, at = t + i * boxSpawnInterval,
            };
            if (boxSpawnInterval <= 0f) SpawnBox(box.prefab, box.itemId, box.paint, box.pose, box.teamId);
            else queued.Add(box);
        }
        ServerSpawnQueued(); // the first one now
        return true;
    }

    private NetworkObject SpawnBox(NetworkObject prefab, int itemId, byte paint, Pose pose, ulong teamId)
    {
        var spawned = Instantiate(prefab, pose.position, pose.rotation);
        Sfx.ForAll(Cue.DeliveryArrived, pose.position); // each box as it comes down (Min Gap keeps a big order tidy)
        ItemDebug.Log($"Delivering {ItemDebug.Describe(itemId)} as '{prefab.name}' at {pose.position}.");
        if (spawned.TryGetComponent<ShipmentPackage>(out var box))
        {
            box.ServerSetContents(itemId);
            box.ServerSetPaint(PaintSet.All(paint)); // bought in one color: every zone
        }
        else if (spawned.TryGetComponent<ItemPickupNet>(out var bare) && bare.itemId == BaseIdOf(itemId))
        {
            bare.ServerSetItemId(itemId); // which size
            bare.ServerSetPaint(PaintSet.All(paint)); // delivered as itself
        }
        else
            Debug.LogWarning($"[EconomyManager] Shipment prefab '{prefab.name}' has no ShipmentPackage, so " +
                             "picking it up won't give the item inside.", prefab);
        spawned.Spawn(true);
        Physics.SyncTransforms(); // so the next plan sees this box

        if (box != null) box.ServerReserve(teamId, reservationSeconds);
        return spawned;
    }

    private static int BaseIdOf(int itemId) =>
        ItemCatalog.Instance != null && ItemCatalog.Instance.TryGet(itemId, out var def) ? def.BaseId : itemId;

    private void ServerRemoveOrderAt(int index)
    {
        int id = orders[index].orderId;
        truckBoxes.Remove(id);
        loadedTrucks.Remove(id);
        fitCache.Remove(id);
        dockFullNoticed.Remove(id);
        soonLeft.Remove(id);
        for (int i = orderLines.Count - 1; i >= 0; i--)
            if (orderLines[i].orderId == id) orderLines.RemoveAt(i);
        orders.RemoveAt(index);
    }

    /* ========================= TRUCKS (SERVER) ========================= */

    private readonly Dictionary<int, List<NetworkObject>> truckBoxes = new(); // order → the boxes put on its truck
    private readonly HashSet<int> loadedTrucks = new();                       // orders whose truck has been loaded
    private readonly List<DeliveryTruck> leavingTrucks = new();
    private readonly List<TruckBay> baysScratch = new();

    private List<NetworkObject> TruckBoxes(int order)
    {
        if (!truckBoxes.TryGetValue(order, out var list)) truckBoxes[order] = list = new List<NetworkObject>();
        return list;
    }

    /// <summary>Does this store get its deliveries by truck? (Trucks set up, and a Truck Bay at its dock.)</summary>
    public bool UsesTrucks(StorePlot plot) => truckPrefabs != null && truckPrefabs.Length > 0 && BaysFor(plot, baysScratch) > 0;

    /// <summary>The truck bays at the docks this store's shipment spots are in.</summary>
    public static int BaysFor(StorePlot plot, List<TruckBay> into)
    {
        into.Clear();
        if (plot == null || plot.ShipmentZones == null) return 0;
        foreach (var z in plot.ShipmentZones)
        {
            if (z == null) continue;
            var dock = LoadingDockZone.GetDockAt(z.transform.TransformPoint(z.center));
            if (dock == null) continue;
            foreach (var bay in TruckBay.All)
                if (bay != null && bay.Dock == dock && !into.Contains(bay)) into.Add(bay);
        }
        return into.Count;
    }

    // A truck's cargo space (meters), and how much of it a load may fill (Truck Max Fill).
    // Across, up, along the truck (whichever way its model and Cargo Area are turned).
    private static Vector3 CargoSize(DeliveryTruck t) =>
        t != null && t.CargoFrame(out _, out _, out var size) ? size : Vector3.zero;

    private float Capacity(DeliveryTruck t) { var c = CargoSize(t); return c.x * c.y * c.z * truckMaxFill; }

    private static float Volume(ShipmentPacker.Box b) => b.half.x * b.half.y * b.half.z * 8f;

    private DeliveryTruck Biggest()
    {
        DeliveryTruck best = null;
        foreach (var t in truckPrefabs) if (t != null && t.cargoArea != null && (best == null || Capacity(t) > Capacity(best))) best = t;
        return best;
    }

    // Does this box go in the biggest truck at all (either way round)?
    private bool FitsBiggestTruck(ShipmentPacker.Box b)
    {
        var c = CargoSize(Biggest());
        Vector3 s = b.half * 2f;
        return s.y <= c.y + 0.01f && ((s.x <= c.x + 0.01f && s.z <= c.z + 0.01f) || (s.z <= c.x + 0.01f && s.x <= c.z + 0.01f));
    }

    // The smallest truck whose cargo space this load fills no more than Truck Max Fill of (by volume), with
    // room for its biggest box (-1: none).
    private int PickTruckSize(List<ShipmentPacker.Box> boxes)
    {
        float need = 0f;
        foreach (var b in boxes) need += Volume(b);
        for (int i = 0; i < truckPrefabs.Length; i++)
        {
            var t = truckPrefabs[i];
            if (t == null || t.cargoArea == null || need > Capacity(t)) continue;
            var c = CargoSize(t);
            bool all = true;
            foreach (var b in boxes)
            {
                Vector3 s = b.half * 2f;
                if (!(s.y <= c.y + 0.01f && ((s.x <= c.x + 0.01f && s.z <= c.z + 0.01f) || (s.z <= c.x + 0.01f && s.x <= c.z + 0.01f)))) { all = false; break; }
            }
            if (all) return i;
        }
        return -1;
    }

    // An order split into truckloads (how many of each line in each), each no more than the biggest truck
    // holds (by volume): biggest things first, a new truck when the current one's full.
    private List<int[]> SplitIntoTruckloads(int[] itemIds, int[] quantities)
    {
        float cap = Mathf.Max(0.001f, Capacity(Biggest()));
        var units = new List<(int line, float volume)>();
        for (int i = 0; i < itemIds.Length; i++)
        {
            if (!ItemCatalog.Instance.TryGet(itemIds[i], out var def)) continue;
            var prefab = def.GetShipmentPrefab();
            if (prefab == null) continue;
            float v = Volume(ShipmentPacker.GetBox(prefab.gameObject));
            for (int q = 0; q < quantities[i]; q++) units.Add((i, v));
        }
        units.Sort((a, b) => b.volume.CompareTo(a.volume));
        var loads = new List<(int[] qty, float used)>();
        foreach (var (line, v) in units)
        {
            int k = loads.FindIndex(l => l.used + v <= cap); // the first truck it still fits in
            if (k < 0) { loads.Add((new int[itemIds.Length], 0f)); k = loads.Count - 1; }
            var l2 = loads[k];
            l2.qty[line]++;
            l2.used += v;
            loads[k] = l2;
        }
        return loads.ConvertAll(l => l.qty);
    }

    // Slowest item's time + a share of all the items' times + the truck size's seconds.
    private float TruckTime(float slowest, float allTimes, int size)
    {
        if (size < 0) return slowest;
        float extra = truckSizeSeconds != null && size < truckSizeSeconds.Length ? truckSizeSeconds[size] : 0f;
        return Mathf.Max(5f, slowest + truckTotalTimeShare * allTimes + extra);
    }

    // One truck delivery, once its time's up: to a free bay, loaded once there, waiting to be opened,
    // unloaded, gone.
    private void ServerTruckStep(int index, OrderInfo o, StorePlot plot, double now)
    {
        var truck = TruckFor(o.orderId);
        switch (o.stage)
        {
            case OrderInfo.OnItsWay:
            case OrderInfo.WaitingForBay:
            {
                // One truck per store at a time, at a free bay at its dock.
                foreach (var other in orders.Each())
                    if (other.orderId != o.orderId && other.plotId == o.plotId && other.stage >= OrderInfo.AtDock) { SetStage(index, o, OrderInfo.WaitingForBay); return; }
                TruckBay free = null;
                BaysFor(plot, baysScratch);
                foreach (var bay in baysScratch)
                {
                    bool taken = false;
                    foreach (var t in DeliveryTruck.All) if (t != null && t.BayIndex == bay.Index) { taken = true; break; }
                    if (!taken) { free = bay; break; }
                }
                if (free == null)
                {
                    if (o.stage != OrderInfo.WaitingForBay && NotificationManager.Instance != null)
                        NotificationManager.Instance.ServerNotifyTeamText(o.teamId, "Truck waiting", $"Every truck bay at {plot.DisplayName}'s dock is in use: your truck parks as soon as one's free.");
                    SetStage(index, o, OrderInfo.WaitingForBay);
                    return;
                }
                var prefab = truckPrefabs[Mathf.Clamp(o.truckSize, 0, truckPrefabs.Length - 1)];
                if (prefab == null) { ServerRemoveOrderAt(index); return; }
                // Facing the bay's way, with the prefab root's own turn on top (a Blender model's -90° X).
                Quaternion prefabTurn = prefab.transform.rotation;
                var made = Instantiate(prefab, free.transform.position, free.transform.rotation * prefabTurn);
                made.NetworkObject.Spawn(true);
                made.ServerSetup(o.orderId, o.teamId, free.Index, free.transform.rotation, prefabTurn);
                o.stage = OrderInfo.AtDock;
                o.deadline = now + truckDoorWaitSeconds + 10f; // (+ driving in)
                orders[index] = o;
                return;
            }
            case OrderInfo.AtDock:
            {
                if (truck == null) { ServerRemoveOrderAt(index); return; }
                if (!loadedTrucks.Contains(o.orderId))
                {
                    if (!truck.Arrived) return;
                    ServerLoadTruck(o, truck);
                    truck.ServerParked();
                    o.deadline = now + truckDoorWaitSeconds;
                    orders[index] = o;
                    if (NotificationManager.Instance != null)
                        NotificationManager.Instance.ServerNotifyTeam(o.teamId, NotificationManager.Kind.Delivered, o.plotId, o.boxCount);
                    return;
                }
                if (truck.DoorOpen)
                {
                    // Opening it adds Truck Unload Seconds to the time it had left.
                    o.stage = OrderInfo.Unloading;
                    o.deadline = System.Math.Max(o.deadline, now) + truckUnloadSeconds;
                    orders[index] = o;
                    return;
                }
                if (now < o.deadline) return;
                // Nobody came: off it goes with the shipment, part of the cost back.
                int refund = Mathf.RoundToInt(o.cost * truckUnopenedRefund);
                if (refund > 0) ServerAddMoney(o.teamId, refund);
                if (NotificationManager.Instance != null)
                    NotificationManager.Instance.ServerNotifyTeamText(o.teamId, "Truck left",
                        $"Nobody opened the truck at {plot.DisplayName}'s dock: it took the shipment back. ${refund:N0} refunded.");
                SendOff(index, o, truck);
                return;
            }
            case OrderInfo.Unloading:
            {
                if (truck == null) { ServerRemoveOrderAt(index); return; }
                int left = BoxesOnTruck(o.orderId, truck);
                if (left == 0 && o.deadline > now + truckLeaveWhenEmptySeconds)
                {
                    o.deadline = now + truckLeaveWhenEmptySeconds; // emptied: off soon
                    orders[index] = o;
                }
                if (now < o.deadline) return;
                // Time's up: the door comes all the way down first, then it drives off.
                truck.ServerSetDoor(false);
                o.stage = OrderInfo.Closing;
                o.deadline = now + DoorSecondsAt(truck) + 0.3f;
                orders[index] = o;
                return;
            }
            case OrderInfo.Closing:
            {
                if (truck == null) { ServerRemoveOrderAt(index); return; }
                // Someone's still inside: back up it goes, a fine, and a little longer to get out.
                if (PlayerInTruck(truck, out ulong inside))
                {
                    truck.ServerSetDoor(true);
                    ulong team = MallManager.GetTeamOf(inside);
                    if (truckInsideFine > 0) ServerAddMoney(team, -truckInsideFine);
                    o.stage = OrderInfo.Unloading;
                    o.deadline = now + truckGetOutSeconds;
                    orders[index] = o;
                    if (NotificationManager.Instance != null)
                    {
                        string msg = $"Someone was inside the truck at {plot.DisplayName}'s dock as its door came down: " +
                                     $"${truckInsideFine:N0} fine. {truckGetOutSeconds:0} seconds to get out.";
                        NotificationManager.Instance.ServerNotifyTeamText(team, "Get out of the truck!", msg, alarm: true);
                        if (o.teamId != team) NotificationManager.Instance.ServerNotifyTeamText(o.teamId, "Truck held up", msg);
                    }
                    return;
                }
                if (truck.DoorOpen) // opened again from outside (F) while it was coming down: down it comes
                {
                    truck.ServerSetDoor(false);
                    o.deadline = now + DoorSecondsAt(truck) + 0.3f;
                    orders[index] = o;
                    return;
                }
                if (now < o.deadline) return;
                int left = BoxesOnTruck(o.orderId, truck);
                if (left > 0 && NotificationManager.Instance != null)
                    NotificationManager.Instance.ServerNotifyTeamText(o.teamId, "Truck left",
                        $"The truck at {plot.DisplayName}'s dock left with {left} thing{(left == 1 ? "" : "s")} still on it (no refund).");
                SendOff(index, o, truck);
                return;
            }
        }
    }

    // How long its bay's door takes to come down (a second if it has none).
    private static float DoorSecondsAt(DeliveryTruck truck)
    {
        int i = truck.BayIndex;
        return i >= 0 && i < TruckBay.All.Count && TruckBay.All[i] != null ? TruckBay.All[i].DoorSeconds : 1f;
    }

    // A player standing in the truck's cargo space?
    private static bool PlayerInTruck(DeliveryTruck truck, out ulong clientId)
    {
        clientId = 0;
        var nm = NetworkManager.Singleton;
        if (nm == null) return false;
        foreach (var client in nm.ConnectedClientsList)
        {
            var player = client.PlayerObject;
            if (player == null) continue;
            if (truck.CargoContains(player.transform.position + Vector3.up * 0.5f)) { clientId = client.ClientId; return true; }
        }
        return false;
    }

    private void SetStage(int index, OrderInfo o, byte stage)
    {
        if (o.stage == stage) return;
        o.stage = stage;
        orders[index] = o;
    }

    private static DeliveryTruck TruckFor(int orderId)
    {
        foreach (var t in DeliveryTruck.All) if (t != null && t.OrderId == orderId && t.CurrentPhase != DeliveryTruck.Phase.Leaving) return t;
        return null;
    }

    // The shipment set down in the truck's cargo area (one box at a time, like the dock).
    private void ServerLoadTruck(OrderInfo o, DeliveryTruck truck)
    {
        loadedTrucks.Add(o.orderId);
        var lines = GetOrderLines(o.orderId);
        var ids = new int[lines.Count];
        var qty = new int[lines.Count];
        var paints = new byte[lines.Count];
        for (int i = 0; i < lines.Count; i++) { ids[i] = lines[i].itemId; qty[i] = lines[i].quantity; paints[i] = lines[i].paint; }
        BuildBoxes(ids, qty, paints);
        if (!PackTruck(truck, planBoxes, plan) || plan.Count != planItems.Count)
        {
            // Packed in with nothing in the way (it fit by floor area when ordered).
            Debug.LogWarning($"[EconomyManager] Order {o.orderId} didn't pack neatly into its truck: stacking it in the middle.");
            plan.Clear();
            truck.CargoFrame(out var mid, out var facing, out _);
            for (int i = 0; i < planItems.Count; i++)
                plan.Add(new Pose(mid + Vector3.up * (0.4f * i), facing));
        }
        float t = Time.time;
        for (int i = 0; i < plan.Count; i++)
            queued.Add(new QueuedBox
            {
                prefab = planItems[i].prefab, itemId = planItems[i].itemId, paint = planItems[i].paint, pose = plan[i],
                teamId = o.teamId, plotId = -1 - o.plotId, at = t + i * Mathf.Min(boxSpawnInterval, 0.05f), truckOrder = o.orderId,
            });
    }

    // Boxes stacked into a truck's cargo space: biggest first, in rows across, rows back to front, then a
    // layer on top (each box turned whichever way fits). Poses for planBoxes' order; false if it runs out of room.
    private static bool PackTruck(DeliveryTruck truck, List<ShipmentPacker.Box> boxes, List<Pose> poses)
    {
        poses.Clear();
        if (!truck.CargoFrame(out var center, out var rot, out var c)) return false;
        var order = new List<int>();
        for (int i = 0; i < boxes.Count; i++) { order.Add(i); poses.Add(default); }
        order.Sort((a, b) => Volume(boxes[b]).CompareTo(Volume(boxes[a])));
        const float gap = 0.02f;
        float x = 0f, z = 0f, y = 0f, rowDepth = 0f, layerHeight = 0f;
        foreach (int i in order)
        {
            Vector3 s = boxes[i].half * 2f;
            bool turn = s.x > c.x - x + 0.001f && s.z <= c.x - x + 0.001f; // turned, if that fits this row
            float w = turn ? s.z : s.x, d = turn ? s.x : s.z;
            if (x + w > c.x + 0.001f) { x = 0f; z += rowDepth + gap; rowDepth = 0f; }            // next row
            if (z + d > c.z + 0.001f) { z = 0f; x = 0f; y += layerHeight + gap; layerHeight = 0f; rowDepth = 0f; } // next layer
            if (y + s.y > c.y + 0.001f || x + w > c.x + 0.001f || z + d > c.z + 0.001f) return false;
            // From the cargo space's corner (its local -x, floor, -z) to this box's middle.
            Vector3 local = new Vector3(-c.x * 0.5f + x + w * 0.5f, -c.y * 0.5f + y + s.y * 0.5f, -c.z * 0.5f + z + d * 0.5f);
            Vector3 middle = center + rot * local;
            Quaternion r = turn ? rot * Quaternion.Euler(0f, 90f, 0f) : rot;
            poses[i] = new Pose(middle - r * boxes[i].pivotOffset, r); // (the prefab's pivot, not its middle)
            x += w + gap;
            rowDepth = Mathf.Max(rowDepth, d);
            layerHeight = Mathf.Max(layerHeight, s.y);
        }
        return true;
    }

    // How many of its boxes are still in the truck (not picked up, not carried off).
    private int BoxesOnTruck(int orderId, DeliveryTruck truck)
    {
        if (!truckBoxes.TryGetValue(orderId, out var list)) return 0;
        int n = 0;
        foreach (var no in list) if (no != null && no.IsSpawned && truck.CargoContains(no.transform.position)) n++;
        foreach (var q in queued) if (q.truckOrder == orderId) n++; // still being loaded
        return n;
    }

    // Door shut, whatever's still on it goes with it, the order's done.
    private void SendOff(int index, OrderInfo o, DeliveryTruck truck)
    {
        if (truckBoxes.TryGetValue(o.orderId, out var list))
            foreach (var no in list)
                if (no != null && no.IsSpawned && truck.CargoContains(no.transform.position)) no.Despawn(true);
        queued.RemoveAll(q => q.truckOrder == o.orderId);
        truck.ServerLeave();
        leavingTrucks.Add(truck);
        ServerRemoveOrderAt(index);
    }

    // Trucks that have driven off: gone.
    private void ServerDespawnGoneTrucks()
    {
        for (int i = leavingTrucks.Count - 1; i >= 0; i--)
        {
            var t = leavingTrucks[i];
            if (t == null) { leavingTrucks.RemoveAt(i); continue; }
            if (!t.Gone) continue;
            if (t.IsSpawned) t.NetworkObject.Despawn(true);
            leavingTrucks.RemoveAt(i);
        }
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

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
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
            hudSb.Append(": ").Append(o.blocked ? "here, waiting for room at the dock" : o.inLine ? $"in line ({SecondsUntil(o):0}s once it starts)" : $"{SecondsUntil(o):0}s");
            hudLines.Add(hudSb.ToString());
        }

        if (lastResult.HasValue && Time.time - lastResultTime < 5f)
            hudLines.Add($"Last order: {lastResult.Value}");
    }

    private void DrawHud()
    {

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
