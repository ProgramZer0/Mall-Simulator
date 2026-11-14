using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// The special market (one in the scene, with a NetworkObject): a few deals at a time, shared by the
/// whole mall. Each deal is one item at its own price with a limited amount and an end time; when it
/// ends or sells out, a new one replaces it after a short wait. Deals are picked from the catalog by
/// each item's Special Market Weight: normal shop items come up cheaper than usual, and items with
/// Show In Shop off (market only) can only be bought here. Each deal is one size (a random one, for
/// sized items) and one color (random, for paintable items), and that's what you get: no switching. Bought on the shop monitor's Market tab
/// (MarketScreen); they're delivered like any order.
///
/// Network: one small synced list of deals. The host checks every buy (amount left, level, license,
/// money) through the normal order path.
/// </summary>
public class SpecialMarket : NetworkBehaviour
{
    public static SpecialMarket Instance { get; private set; }

    public struct Deal : INetworkSerializeByMemcpy, IEquatable<Deal>
    {
        public int dealId;
        public int itemId;
        public int price;      // dollars per item, delivery included
        public int usualPrice; // what the shop charges (0 = market only)
        public int left;
        public int total;
        public byte paint;     // PaintPalette id it comes in (0 = its own colors)
        public double endsAt;  // server time
        public bool Equals(Deal o) => dealId == o.dealId && left == o.left && price == o.price && endsAt == o.endsAt;
        public override bool Equals(object obj) => obj is Deal o && Equals(o);
        public override int GetHashCode() => dealId * 397 ^ left;
        public bool MarketOnly => usualPrice == 0;
    }

    [Header("Deals")]
    [SerializeField, Min(1)] private int dealCount = 9;
    [Tooltip("How long a deal lasts, in seconds (random between).")]
    [SerializeField] private Vector2 dealSeconds = new(300f, 900f);
    [Tooltip("Seconds before an ended or sold-out deal is replaced.")]
    [SerializeField, Min(0f)] private float replaceAfter = 20f;
    [Tooltip("How many of the item a deal has (random between).")]
    [SerializeField] private Vector2Int amount = new(3, 12);

    [Header("Prices")]
    [Tooltip("Shop items: their usual price times this (random between): a discount.")]
    [SerializeField] private Vector2 shopItemPrice = new(0.6f, 0.9f);
    [Tooltip("Market-only items: their catalog price times this (random between).")]
    [SerializeField] private Vector2 marketOnlyPrice = new(0.9f, 1.25f);

    [Header("Notifications")]
    [Tooltip("Tell everyone when a market-only item comes up.")]
    [SerializeField] private bool announceMarketOnly = true;

    public event Action Changed;
    /// <summary>Owner: the answer to a buy.</summary>
    public event Action<EconomyManager.OrderResult> ResultReceived;

    private NetworkList<Deal> deals;
    private int nextDealId = 1;
    private readonly List<double> emptiedAt = new(); // host: when each empty slot emptied
    private readonly List<ItemDef> pool = new();
    private readonly HashSet<int> seen = new();                 // deals already announced here

    public IReadOnlyList<Deal> DealsNow => dealsView;
    private readonly List<Deal> dealsView = new();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[SpecialMarket] More than one in the scene; using the first.", this);
            return;
        }
        Instance = this;
        deals = new NetworkList<Deal>(null, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    }

    public override void OnNetworkSpawn()
    {
        deals.OnListChanged += OnDealsChanged;
        foreach (var d in deals) seen.Add(d.dealId); // already up when we joined: not news
        CopyView();
    }

    public override void OnNetworkDespawn() => deals.OnListChanged -= OnDealsChanged;

    public override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    private void OnDealsChanged(NetworkListEvent<Deal> e)
    {
        CopyView();
        if (announceMarketOnly && (e.Type == NetworkListEvent<Deal>.EventType.Add || e.Type == NetworkListEvent<Deal>.EventType.Insert)
            && seen.Add(e.Value.dealId) && e.Value.MarketOnly && ItemCatalog.Instance != null &&
            ItemCatalog.Instance.TryGet(e.Value.itemId, out var def))
            Notifications.Post($"Special market: {def.displayName} for ${e.Value.price:N0}");
        Changed?.Invoke();
    }

    private void CopyView()
    {
        dealsView.Clear();
        foreach (var d in deals) dealsView.Add(d);
    }

    /// <summary>Seconds until a deal ends.</summary>
    public float SecondsLeft(Deal d) => IsSpawned ? Mathf.Max(0f, (float)(d.endsAt - NetworkManager.ServerTime.Time)) : 0f;

    /* ========================= HOST: KEEPING DEALS UP ========================= */

    private float nextCheck;

    private void Update()
    {
        if (!IsServer || !IsSpawned || Time.unscaledTime < nextCheck) return;
        nextCheck = Time.unscaledTime + 1f;
        double now = NetworkManager.ServerTime.Time;

        // Ended or sold out: gone, and the slot waits Replace After before a new deal.
        for (int i = deals.Count - 1; i >= 0; i--)
            if (deals[i].left <= 0 || now >= deals[i].endsAt)
            {
                deals.RemoveAt(i);
                emptiedAt.Add(now);
            }

        emptiedAt.RemoveAll(t => now - t >= replaceAfter);
        int waiting = emptiedAt.Count;

        // Fill up to Deal Count (slots still waiting don't count as free yet).
        int tries = 0;
        while (deals.Count + waiting < dealCount && tries++ < dealCount * 4)
            if (!TryMakeDeal(now)) break;
    }

    private bool TryMakeDeal(double now)
    {
        var catalog = ItemCatalog.Instance;
        if (catalog == null) return false;

        pool.Clear();
        float total = 0f;
        foreach (var def in catalog.Items)
        {
            if (def == null || def.worldNetworkPrefab == null || def.specialMarketWeight <= 0f || IsListed(def.id)) continue;
            pool.Add(def);
            total += def.specialMarketWeight;
        }
        if (pool.Count == 0) return false;

        float pick = UnityEngine.Random.value * total;
        ItemDef chosen = pool[pool.Count - 1];
        foreach (var def in pool)
        {
            pick -= def.specialMarketWeight;
            if (pick <= 0f) { chosen = def; break; }
        }

        // A sized item: one of its sizes.
        var sizes = catalog.GetSizes(chosen.id);
        ItemDef item = sizes != null && sizes.Count > 0 ? sizes[UnityEngine.Random.Range(0, sizes.Count)] : chosen;

        int usual = MarketModifiers.OrderCost(item) + MarketModifiers.DeliveryFee(item);
        bool marketOnly = !chosen.showInShop;
        Vector2 range = marketOnly ? marketOnlyPrice : shopItemPrice;
        int price = Mathf.Max(1, Mathf.RoundToInt(usual * UnityEngine.Random.Range(range.x, range.y)));
        int count = UnityEngine.Random.Range(Mathf.Max(1, amount.x), Mathf.Max(amount.x, amount.y) + 1);
        // A paintable item: a random color (its own colors count as one of them).
        byte paint = PaintParts.IsPaintable(item) && PaintPalette.Instance != null
            ? (byte)UnityEngine.Random.Range(0, PaintPalette.Instance.Count + 1)
            : (byte)0;

        deals.Add(new Deal
        {
            dealId = nextDealId++,
            itemId = item.id,
            price = price,
            usualPrice = marketOnly ? 0 : usual,
            left = count,
            total = count,
            paint = paint,
            endsAt = now + UnityEngine.Random.Range(dealSeconds.x, Mathf.Max(dealSeconds.x, dealSeconds.y)),
        });
        return true;
    }

    // One deal per item (all sizes of a sized item count as one).
    private bool IsListed(int baseId)
    {
        foreach (var d in deals)
            if (ItemCatalog.Instance.TryGet(d.itemId, out var def) && def.BaseId == baseId) return true;
        return false;
    }

    /* ========================= BUYING ========================= */

    /// <summary>Buy some of a deal, delivered to this store.</summary>
    public void RequestBuy(int plotId, int dealId, int quantity) => BuyRpc(plotId, dealId, quantity);

    [Rpc(SendTo.Server, RequireOwnership = false)]
    private void BuyRpc(int plotId, int dealId, int quantity, RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        var result = EconomyManager.OrderResult.UnknownItem;
        for (int i = 0; i < deals.Count; i++)
        {
            var d = deals[i];
            if (d.dealId != dealId) continue;
            if (quantity < 1 || quantity > d.left || NetworkManager.ServerTime.Time >= d.endsAt)
            {
                result = EconomyManager.OrderResult.BadQuantity;
                break;
            }
            var eco = EconomyManager.Instance;
            if (eco == null) break;
            result = eco.ServerTryOrder(sender, plotId, new[] { d.itemId }, new[] { quantity }, -1f, new[] { d.paint }, new[] { d.price });
            if (result == EconomyManager.OrderResult.Ordered)
            {
                d.left -= quantity;
                deals[i] = d;
            }
            break;
        }
        ResultRpc(result, RpcTarget.Single(sender, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void ResultRpc(EconomyManager.OrderResult result, RpcParams rpcParams) => ResultReceived?.Invoke(result);
}
