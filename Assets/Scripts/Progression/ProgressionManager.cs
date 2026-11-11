using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Team levels and store perks (one in the scene, with a NetworkObject).
///
/// Team level: XP mostly from happy customers (customer AI calls ServerCustomerLeft), a little from
/// profit. Unhappy customers take XP away, but never below the start of the current level, so a level
/// is never lost. Levels unlock items (ItemDef Min Level) and more stores (Store Unlock Levels).
///
/// Store perks: each store earns perk points from its own profit (sale price minus what its team
/// actually paid for the item). Points belong to the store; spend them on the shop monitor's Perks tab.
/// Respecs are free. Losing the store loses its points and perks.
///
/// What was paid: the host remembers, per team and item, how many products it has bought and what it
/// paid in total (orders and buys from other players), and a sale costs the average. Nothing extra is
/// synced. Items it has no record of (starting stock) cost their catalog order price.
/// </summary>
public class ProgressionManager : NetworkBehaviour
{
    public static ProgressionManager Instance { get; private set; }

    public struct TeamLevel : INetworkSerializeByMemcpy, IEquatable<TeamLevel>
    {
        public ulong teamId;
        public int xp;    // total
        public int level; // never goes down
        public bool Equals(TeamLevel o) => teamId == o.teamId && xp == o.xp && level == o.level;
        public override bool Equals(object obj) => obj is TeamLevel o && Equals(o);
        public override int GetHashCode() => teamId.GetHashCode() ^ xp * 31 ^ level;
    }

    public struct StoreProgress : INetworkSerializeByMemcpy, IEquatable<StoreProgress>
    {
        public int plotId;
        public long profitCents; // since the team got it
        public int points;       // earned (never goes down)
        public bool Equals(StoreProgress o) => plotId == o.plotId && profitCents == o.profitCents && points == o.points;
        public override bool Equals(object obj) => obj is StoreProgress o && Equals(o);
        public override int GetHashCode() => plotId * 397 ^ profitCents.GetHashCode() ^ points;
    }

    public struct PerkRank : INetworkSerializeByMemcpy, IEquatable<PerkRank>
    {
        public int plotId;
        public int perkId;
        public int rank;
        public bool Equals(PerkRank o) => plotId == o.plotId && perkId == o.perkId && rank == o.rank;
        public override bool Equals(object obj) => obj is PerkRank o && Equals(o);
        public override int GetHashCode() => (plotId * 397 ^ perkId) * 31 ^ rank;
    }

    /// <summary>One thing sold: products is how many (a whole crate sold loose is many).</summary>
    public struct Sold
    {
        public int itemId;
        public int products;
        public int priceCents;
    }

    [Header("Team Levels")]
    [SerializeField, Min(1)] private int maxLevel = 50;
    [Tooltip("XP from level 1 to level 2.")]
    [SerializeField, Min(1)] private int firstLevelXp = 100;
    [Tooltip("Each level needs this many times more XP than the one before.")]
    [SerializeField, Min(1f)] private float levelXpGrowth = 1.3f;
    [Tooltip("XP for a customer who leaves fully happy (less for a bit happy).")]
    [SerializeField, Min(0)] private int happyCustomerXp = 10;
    [Tooltip("XP lost for a customer who leaves fully unhappy (never below the current level's start).")]
    [SerializeField, Min(0)] private int unhappyCustomerXp = 5;
    [Tooltip("XP per dollar of profit (kept small: customers are the main way up).")]
    [SerializeField, Min(0f)] private float xpPerProfitDollar = 0.05f;
    [Tooltip("The level each store needs: the 1st store at the 1st entry, a 2nd at the 2nd... Also capped by the " +
             "MallManager's Max Stores Per Team.")]
    [SerializeField] private int[] storeUnlockLevels = { 1, 5, 10, 15, 20, 30 };

    [Header("Store Perks")]
    [SerializeField] private List<PerkDef> perks = new();
    [Tooltip("Profit (dollars) for a store's first perk point.")]
    [SerializeField, Min(1f)] private float firstPointProfit = 250f;
    [Tooltip("Each point needs this many times more profit than the one before.")]
    [SerializeField, Min(1f)] private float pointProfitGrowth = 1.25f;

    [Header("Testing")]
    [SerializeField] private bool devHotkeys = true;
    [Tooltip("A fully happy customer for your team.")]
    [SerializeField] private KeyCode devHappyCustomerKey = KeyCode.F11;
    [Tooltip("Dev Profit dollars of profit for the store you're standing in.")]
    [SerializeField] private KeyCode devProfitKey = KeyCode.F12;
    [SerializeField] private int devProfit = 100;

    /// <summary>Anything changed (levels, profit, points, perks).</summary>
    public event Action Changed;
    /// <summary>A team's level changed.</summary>
    public event Action LevelChanged;

    public IReadOnlyList<PerkDef> Perks => perks;
    public int MaxLevel => maxLevel;

    private NetworkList<TeamLevel> teams;
    private NetworkList<StoreProgress> stores;
    private NetworkList<PerkRank> ranks;

    private readonly Dictionary<int, PerkDef> perkById = new();
    private int[] levelStartXp;

    // Host only.
    private readonly Dictionary<ulong, float> xpRemainder = new();
    private readonly Dictionary<(ulong team, int itemId), (long products, long cents)> paid = new();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[ProgressionManager] More than one in the scene; using the first.", this);
            return;
        }
        Instance = this;

        teams = new NetworkList<TeamLevel>(null, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        stores = new NetworkList<StoreProgress>(null, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        ranks = new NetworkList<PerkRank>(null, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        foreach (var p in perks)
        {
            if (p == null) continue;
            if (p.id == 0 || perkById.ContainsKey(p.id))
                Debug.LogWarning($"[ProgressionManager] Perk '{p.name}' needs its own Id (not 0, not shared).", p);
            else perkById[p.id] = p;
        }

        // Total XP at the start of each level (index = level).
        levelStartXp = new int[maxLevel + 2];
        double need = firstLevelXp, total = 0;
        for (int level = 2; level <= maxLevel + 1; level++)
        {
            total += Math.Round(need);
            levelStartXp[level] = (int)Math.Min(int.MaxValue, total);
            need *= levelXpGrowth;
        }
    }

    public override void OnNetworkSpawn()
    {
        teams.OnListChanged += OnTeamsChanged;
        stores.OnListChanged += OnAnyChanged;
        ranks.OnListChanged += OnRanksChanged;
    }

    public override void OnNetworkDespawn()
    {
        teams.OnListChanged -= OnTeamsChanged;
        stores.OnListChanged -= OnAnyChanged;
        ranks.OnListChanged -= OnRanksChanged;
    }

    public override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    private void OnTeamsChanged(NetworkListEvent<TeamLevel> e)
    {
        if (e.Type != NetworkListEvent<TeamLevel>.EventType.Value || e.PreviousValue.level != e.Value.level)
            LevelChanged?.Invoke();
        Changed?.Invoke();
    }

    private void OnAnyChanged(NetworkListEvent<StoreProgress> e) => Changed?.Invoke();

    private void OnRanksChanged(NetworkListEvent<PerkRank> e)
    {
        statCache.Clear();
        Changed?.Invoke();
    }

    /* ========================= LEVELS ========================= */

    /// <summary>Total XP needed to be at a level (level 1 = 0).</summary>
    public int XpForLevel(int level) => levelStartXp[Mathf.Clamp(level, 1, maxLevel + 1)];

    private int LevelForXp(int xp)
    {
        int level = 1;
        while (level < maxLevel && xp >= levelStartXp[level + 1]) level++;
        return level;
    }

    public int GetLevel(ulong team) => TryGetTeam(team, out var t, out _) ? t.level : 1;
    public int GetXp(ulong team) => TryGetTeam(team, out var t, out _) ? t.xp : 0;

    /// <summary>This machine's team.</summary>
    public ulong LocalTeam => NetworkManager != null ? MallManager.GetTeamOf(NetworkManager.LocalClientId) : 0;
    public int LocalLevel => IsSpawned ? GetLevel(LocalTeam) : 1;

    /// <summary>A team's level, 1 without a ProgressionManager.</summary>
    public static int LevelOf(ulong team) => Instance != null && Instance.IsSpawned ? Instance.GetLevel(team) : 1;

    /// <summary>Can this team order this item yet?</summary>
    public static bool CanOrder(ulong team, ItemDef def) => def == null || def.minLevel <= LevelOf(team);

    /// <summary>How many stores a team's level allows (no limit without a ProgressionManager).</summary>
    public static int StoresAllowed(ulong team)
    {
        var pm = Instance;
        if (pm == null || pm.storeUnlockLevels == null || pm.storeUnlockLevels.Length == 0) return int.MaxValue;
        int level = LevelOf(team), n = 0;
        foreach (int need in pm.storeUnlockLevels) if (level >= need) n++;
        return Mathf.Max(1, n);
    }

    /// <summary>The level needed to own this many stores (0 = no level allows it).</summary>
    public static int LevelForStores(int count)
    {
        var pm = Instance;
        if (pm == null || pm.storeUnlockLevels == null) return 1;
        var sorted = (int[])pm.storeUnlockLevels.Clone();
        Array.Sort(sorted);
        return count >= 1 && count <= sorted.Length ? Mathf.Max(1, sorted[count - 1]) : 0;
    }

    private bool TryGetTeam(ulong team, out TeamLevel t, out int index)
    {
        if (teams != null)
            for (int i = 0; i < teams.Count; i++)
                if (teams[i].teamId == team) { t = teams[i]; index = i; return true; }
        t = new TeamLevel { teamId = team, xp = 0, level = 1 };
        index = -1;
        return false;
    }

    /// <summary>Host: give (or take) XP. Taking never goes below the start of the team's level.</summary>
    public void ServerAddXp(ulong team, float amount)
    {
        if (!IsServer || amount == 0f || float.IsNaN(amount)) return;
        xpRemainder.TryGetValue(team, out float rest);
        rest += amount;
        int whole = (int)rest; // toward zero, the rest waits
        xpRemainder[team] = rest - whole;
        if (whole == 0) return;

        TryGetTeam(team, out var t, out int index);
        long xp = Math.Max(XpForLevel(t.level), (long)t.xp + whole);
        t.xp = (int)Math.Min(int.MaxValue, xp);
        t.level = Mathf.Max(t.level, LevelForXp(t.xp));
        if (index >= 0) teams[index] = t;
        else teams.Add(t);
    }

    /// <summary>
    /// Host (customer AI): a customer left a team's store. Satisfaction from -1 (furious) to 1 (delighted).
    /// </summary>
    public void ServerCustomerLeft(ulong team, float satisfaction)
    {
        satisfaction = Mathf.Clamp(satisfaction, -1f, 1f);
        ServerAddXp(team, satisfaction >= 0f ? satisfaction * happyCustomerXp : satisfaction * unhappyCustomerXp);
    }

    /* ========================= WHAT WAS PAID (HOST) ========================= */

    private static int ProductsPerBox(int itemId) => Mathf.Max(1, StockContainer.GetUnitsPerCrate(itemId));

    /// <summary>Host: a team bought products of an item (an order, or from another player) for this much.</summary>
    public void ServerRecordPurchase(ulong team, int itemId, int products, long cents)
    {
        if (!IsServer || products <= 0 || cents < 0) return;
        paid.TryGetValue((team, itemId), out var had);
        paid[(team, itemId)] = (had.products + products, had.cents + cents);
    }

    /// <summary>Host: an order of boxes (each box holds Products Per Box) for this many dollars.</summary>
    public void ServerRecordOrder(ulong team, int itemId, int boxes, long dollars) =>
        ServerRecordPurchase(team, itemId, boxes * ProductsPerBox(itemId), dollars * 100);

    // What the products sold cost the team: their average paid price, used up from what's remembered.
    private long TakeCost(ulong team, int itemId, int products)
    {
        long cost = 0;
        if (paid.TryGetValue((team, itemId), out var had) && had.products > 0)
        {
            long n = Math.Min(products, had.products);
            long part = had.cents * n / had.products;
            cost += part;
            had.products -= n;
            had.cents -= part;
            if (had.products <= 0) paid.Remove((team, itemId)); else paid[(team, itemId)] = had;
            products -= (int)n;
        }
        // Not remembered (starting stock): what it'd cost to order now.
        if (products > 0 && ItemCatalog.Instance != null && ItemCatalog.Instance.TryGet(itemId, out var def))
            cost += (long)(MarketModifiers.OrderCost(def) + MarketModifiers.DeliveryFee(def)) * 100 * products / ProductsPerBox(itemId);
        return cost;
    }

    /* ========================= SALES (HOST) ========================= */

    private readonly List<Sold> one = new();

    /// <summary>Host: a store sold one thing at its price.</summary>
    public void ServerRecordSale(StorePlot plot, int itemId, int products, int priceCents)
    {
        one.Clear();
        one.Add(new Sold { itemId = itemId, products = products, priceCents = priceCents });
        ServerRecordSale(plot, one, priceCents);
    }

    /// <summary>
    /// Host: a store sold these and kept Received Cents for them (less than their prices if too much
    /// change was given). The profit counts toward the store's perk points and a little team XP.
    /// </summary>
    public void ServerRecordSale(StorePlot plot, IList<Sold> items, int receivedCents)
    {
        if (!IsServer || plot == null || !plot.HasOwner || items == null) return;
        ulong team = plot.OwnerTeamId;
        long cost = 0;
        foreach (var s in items) cost += TakeCost(team, s.itemId, Mathf.Max(1, s.products));
        long profit = receivedCents - cost;
        ServerAddStoreProfit(plot.PlotId, profit);
        if (profit > 0) ServerAddXp(team, profit / 100f * xpPerProfitDollar);
    }

    /* ========================= STORE POINTS ========================= */

    /// <summary>Total profit (cents) a store needs for this many points.</summary>
    public long ProfitForPoints(int points)
    {
        double need = firstPointProfit, total = 0;
        for (int i = 0; i < points; i++)
        {
            total += need;
            need *= pointProfitGrowth;
            if (total > 1e15) break;
        }
        return (long)Math.Round(total * 100);
    }

    private int PointsForProfit(long cents)
    {
        int n = 0;
        while (n < 10000 && ProfitForPoints(n + 1) <= cents) n++;
        return n;
    }

    private bool TryGetStore(int plotId, out StoreProgress s, out int index)
    {
        if (stores != null)
            for (int i = 0; i < stores.Count; i++)
                if (stores[i].plotId == plotId) { s = stores[i]; index = i; return true; }
        s = new StoreProgress { plotId = plotId };
        index = -1;
        return false;
    }

    public long GetStoreProfitCents(int plotId) => TryGetStore(plotId, out var s, out _) ? s.profitCents : 0;
    public int GetPointsEarned(int plotId) => TryGetStore(plotId, out var s, out _) ? s.points : 0;

    public int GetPointsSpent(int plotId)
    {
        int spent = 0;
        foreach (var r in ranks)
            if (r.plotId == plotId && perkById.TryGetValue(r.perkId, out var p)) spent += r.rank * p.costPerRank;
        return spent;
    }

    public int GetPointsAvailable(int plotId) => Mathf.Max(0, GetPointsEarned(plotId) - GetPointsSpent(plotId));

    /// <summary>Host: add (or, for a loss, take) profit. Points earned are kept.</summary>
    public void ServerAddStoreProfit(int plotId, long cents)
    {
        if (!IsServer || cents == 0) return;
        TryGetStore(plotId, out var s, out int index);
        s.profitCents += cents;
        s.points = Mathf.Max(s.points, PointsForProfit(s.profitCents));
        if (index >= 0) stores[index] = s;
        else stores.Add(s);
    }

    /// <summary>Host: a store changed hands or went back on sale: its profit, points and perks go.</summary>
    public void ServerForgetStore(int plotId)
    {
        if (!IsServer || !IsSpawned) return;
        for (int i = stores.Count - 1; i >= 0; i--) if (stores[i].plotId == plotId) stores.RemoveAt(i);
        for (int i = ranks.Count - 1; i >= 0; i--) if (ranks[i].plotId == plotId) ranks.RemoveAt(i);
    }

    /* ========================= PERKS ========================= */

    public bool TryGetPerk(int perkId, out PerkDef perk) => perkById.TryGetValue(perkId, out perk);

    public int GetRank(int plotId, int perkId)
    {
        if (ranks != null)
            foreach (var r in ranks) if (r.plotId == plotId && r.perkId == perkId) return r.rank;
        return 0;
    }

    /// <summary>Can this store buy the next rank of this perk? Reason says why not.</summary>
    public bool CanBuyPerk(int plotId, PerkDef perk, out string reason)
    {
        reason = null;
        if (perk == null || !perkById.TryGetValue(perk.id, out var known) || known != perk) { reason = "Unknown perk"; return false; }
        if (GetRank(plotId, perk.id) >= perk.maxRank) { reason = "Maxed"; return false; }
        if (perk.requires != null && GetRank(plotId, perk.requires.id) < perk.requiresRank)
        {
            reason = $"Needs {perk.requires.displayName}" + (perk.requiresRank > 1 ? $" {perk.requiresRank}" : "");
            return false;
        }
        if (GetPointsAvailable(plotId) < perk.costPerRank) { reason = "Not enough points"; return false; }
        return true;
    }

    private readonly Dictionary<(int, PerkStat), float> statCache = new();

    /// <summary>How much a store's perks add to a stat (0 = nothing).</summary>
    public float GetStoreStat(int plotId, PerkStat stat)
    {
        if (statCache.TryGetValue((plotId, stat), out float v)) return v;
        v = 0f;
        if (ranks != null)
            foreach (var r in ranks)
            {
                if (r.plotId != plotId || !perkById.TryGetValue(r.perkId, out var p)) continue;
                foreach (var e in p.effects) if (e.stat == stat) v += e.perRank * r.rank;
            }
        statCache[(plotId, stat)] = v;
        return v;
    }

    /// <summary>A store's perk bonus for a stat (0 without a ProgressionManager).</summary>
    public static float StoreStat(StorePlot plot, PerkStat stat) =>
        plot != null && Instance != null && Instance.IsSpawned ? Instance.GetStoreStat(plot.PlotId, stat) : 0f;

    /// <summary>Buy the next rank of a perk for a store (the host checks it).</summary>
    public void RequestBuyPerk(int plotId, int perkId) => BuyPerkRpc(plotId, perkId);

    /// <summary>Take back every perk of a store (free).</summary>
    public void RequestResetPerks(int plotId) => ResetPerksRpc(plotId);

    [Rpc(SendTo.Server, RequireOwnership = false)]
    private void BuyPerkRpc(int plotId, int perkId, RpcParams rpcParams = default)
    {
        if (!ServerCanManage(rpcParams, plotId) || !perkById.TryGetValue(perkId, out var perk)) return;
        if (!CanBuyPerk(plotId, perk, out _)) return;
        for (int i = 0; i < ranks.Count; i++)
            if (ranks[i].plotId == plotId && ranks[i].perkId == perkId)
            {
                var r = ranks[i];
                r.rank++;
                ranks[i] = r;
                return;
            }
        ranks.Add(new PerkRank { plotId = plotId, perkId = perkId, rank = 1 });
    }

    [Rpc(SendTo.Server, RequireOwnership = false)]
    private void ResetPerksRpc(int plotId, RpcParams rpcParams = default)
    {
        if (!ServerCanManage(rpcParams, plotId)) return;
        for (int i = ranks.Count - 1; i >= 0; i--) if (ranks[i].plotId == plotId) ranks.RemoveAt(i);
    }

    private static bool ServerCanManage(RpcParams rpcParams, int plotId) =>
        MallManager.Instance != null && MallManager.Instance.TryGetPlot(plotId, out var plot) &&
        plot.IsAuthorized(rpcParams.Receive.SenderClientId);

    /* ========================= TESTING ========================= */

    private void Update()
    {
        if (!devHotkeys || !IsSpawned || UIState.IsMenuOpen) return;
        if (Input.GetKeyDown(devHappyCustomerKey)) DevRpc(false);
        if (Input.GetKeyDown(devProfitKey)) DevRpc(true);
    }

    [Rpc(SendTo.Server, RequireOwnership = false)]
    private void DevRpc(bool profit, RpcParams rpcParams = default)
    {
        if (!devHotkeys) return; // the host's setting decides
        ulong sender = rpcParams.Receive.SenderClientId;
        ulong team = MallManager.GetTeamOf(sender);
        if (!profit)
        {
            ServerCustomerLeft(team, 1f);
            Debug.Log($"[ProgressionManager] Dev: happy customer for team {team} (level {GetLevel(team)}, {GetXp(team)} XP).");
            return;
        }
        var player = NetworkManager.ConnectedClients.TryGetValue(sender, out var c) ? c.PlayerObject : null;
        var plot = player != null && MallManager.Instance != null ? MallManager.Instance.GetPlotAt(player.transform.position) : null;
        if (plot == null || !plot.IsAuthorized(sender))
        {
            Debug.Log("[ProgressionManager] Dev: stand in your store to give it profit.");
            return;
        }
        ServerAddStoreProfit(plot.PlotId, devProfit * 100L);
        if (devProfit > 0) ServerAddXp(team, devProfit * xpPerProfitDollar);
        Debug.Log($"[ProgressionManager] Dev: +${devProfit} profit for '{plot.DisplayName}' " +
                  $"({GetPointsAvailable(plot.PlotId)} point(s) to spend).");
    }
}
