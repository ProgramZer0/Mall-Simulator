using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Store upgrades bought with money (one in the scene, with a NetworkObject), shown on the shop
/// monitor's Upgrades tab (StoreUpgradesScreen): renaming the store, its expansions (the store's
/// Segments, same as buying at their wall), bigger carts, and calling carts home.
///
/// Cart Tiers: each is a cart prefab (NetworkObject + CartNet, in the Network Prefabs list) that
/// replaces the store's carts where they stand, keeping what's on them. Tier 0 is the store's own
/// scene cart. Upgrades stay with the store if it changes hands; its name doesn't.
///
/// Network: one small synced list of (store, cart tier); the store's name lives on the StorePlot.
/// </summary>
public class StoreUpgrades : NetworkBehaviour
{
    public static StoreUpgrades Instance { get; private set; }

    [Serializable]
    public class CartTier
    {
        public string name = "Large Cart";
        [TextArea] public string description = "Carries more.";
        public Sprite icon;
        [Tooltip("The cart that replaces the store's carts (NetworkObject + CartNet; in the Network Prefabs list).")]
        public CartNet prefab;
        [Min(0)] public int price = 1500;
        [Tooltip("Team level needed (0 or 1 = from the start).")]
        [Min(0)] public int minLevel = 0;
    }

    public struct PlotUpgrades : INetworkSerializeByMemcpy, IEquatable<PlotUpgrades>
    {
        public int plotId;
        public int cartTier;
        public bool Equals(PlotUpgrades o) => plotId == o.plotId && cartTier == o.cartTier;
        public override bool Equals(object obj) => obj is PlotUpgrades o && Equals(o);
        public override int GetHashCode() => plotId * 397 ^ cartTier;
    }

    [Header("Name")]
    [SerializeField, Min(3)] private int maxNameLength = 24;
    [Tooltip("Seconds between renames of the same store (0 = any time).")]
    [SerializeField, Min(0f)] private float renameCooldown = 10f;

    [Header("Carts")]
    [Tooltip("What the store's starting cart is called on the Upgrades tab.")]
    [SerializeField] private string basicCartName = "Basic Cart";
    [Tooltip("Bigger carts, in order: buying the next one replaces the store's carts.")]
    [SerializeField] private List<CartTier> cartTiers = new();

    /// <summary>Upgrades changed (any store).</summary>
    public event Action Changed;
    /// <summary>Owner: the answer to a request ("" = done).</summary>
    public event Action<string> ResultReceived;

    public int MaxNameLength => maxNameLength;
    public IReadOnlyList<CartTier> CartTiers => cartTiers;

    private NetworkList<PlotUpgrades> upgrades;
    private readonly Dictionary<int, double> lastRename = new(); // host

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[StoreUpgrades] More than one in the scene; using the first.", this);
            return;
        }
        Instance = this;
        upgrades = new NetworkList<PlotUpgrades>(null, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    }

    public override void OnNetworkSpawn() => upgrades.OnListChanged += OnUpgradesChanged;
    public override void OnNetworkDespawn() => upgrades.OnListChanged -= OnUpgradesChanged;

    public override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    private void OnUpgradesChanged(NetworkListEvent<PlotUpgrades> e) => Changed?.Invoke();

    /* ========================= NAME ========================= */

    /// <summary>A name as it'd be saved: trimmed, no rich text, at most Max Name Length.</summary>
    public string CleanName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var chars = new System.Text.StringBuilder();
        foreach (char c in raw.Trim())
            if (c != '<' && c != '>' && !char.IsControl(c)) chars.Append(c);
        string s = chars.ToString().Trim();
        if (s.Length > maxNameLength) s = s.Substring(0, maxNameLength).TrimEnd();
        // FixedString64Bytes holds 61 bytes of UTF-8.
        while (System.Text.Encoding.UTF8.GetByteCount(s) > 61) s = s.Substring(0, s.Length - 1);
        return s;
    }

    /// <summary>Rename a store (empty = back to its default name).</summary>
    public void RequestRename(int plotId, string newName) => RenameRpc(plotId, new FixedString64Bytes(CleanName(newName)));

    [Rpc(SendTo.Server, RequireOwnership = false)]
    private void RenameRpc(int plotId, FixedString64Bytes newName, RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        string result;
        if (!TryGetOwnPlot(sender, plotId, out var plot)) result = "Not your store";
        else if (renameCooldown > 0f && lastRename.TryGetValue(plotId, out double last) &&
                 NetworkManager.ServerTime.Time - last < renameCooldown)
            result = $"Wait {Mathf.CeilToInt((float)(renameCooldown - (NetworkManager.ServerTime.Time - last)))} s to rename again";
        else
        {
            string name = CleanName(newName.ToString());
            plot.ServerSetName(name == plot.DefaultName ? "" : name);
            lastRename[plotId] = NetworkManager.ServerTime.Time;
            result = "";
        }
        ResultRpc(result, RpcTarget.Single(sender, RpcTargetUse.Temp));
    }

    /* ========================= CARTS ========================= */

    /// <summary>The store's cart tier: 0 = its starting cart, 1 = Cart Tiers[0]...</summary>
    public int GetCartTier(int plotId)
    {
        if (upgrades != null) foreach (var u in upgrades) if (u.plotId == plotId) return u.cartTier;
        return 0;
    }

    public string CartTierName(int tier) =>
        tier <= 0 ? basicCartName : tier <= cartTiers.Count ? cartTiers[tier - 1].name : "";

    /// <summary>The next cart the store can buy (null when it has the biggest).</summary>
    public CartTier NextCartTier(int plotId)
    {
        int tier = GetCartTier(plotId);
        return tier < cartTiers.Count ? cartTiers[tier] : null;
    }

    public bool CanBuyCartTier(StorePlot plot, out string reason)
    {
        reason = null;
        var next = plot != null ? NextCartTier(plot.PlotId) : null;
        if (next == null) reason = "Biggest cart already";
        else if (next.prefab == null) reason = "No cart prefab set";
        else if (next.minLevel > ProgressionManager.LevelOf(plot.OwnerTeamId)) reason = $"Needs level {next.minLevel}";
        else if (EconomyManager.Instance != null && EconomyManager.Instance.GetTeamMoney(plot.OwnerTeamId) < next.price) reason = "Not enough money";
        else
            foreach (var cart in CartNet.GetCartsOf(plot))
                if (cart.IsPushed) { reason = "Someone is pushing the cart"; break; }
        return reason == null;
    }

    public void RequestBuyCartTier(int plotId) => BuyCartTierRpc(plotId);

    [Rpc(SendTo.Server, RequireOwnership = false)]
    private void BuyCartTierRpc(int plotId, RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        string result;
        if (!TryGetOwnPlot(sender, plotId, out var plot)) result = "Not your store";
        else if (!CanBuyCartTier(plot, out result)) { }
        else if (EconomyManager.Instance != null && !EconomyManager.Instance.ServerTrySpend(plot.OwnerTeamId, NextCartTier(plotId).price))
            result = "Not enough money";
        else
        {
            var tier = NextCartTier(plotId);
            ServerReplaceCarts(plot, tier.prefab);
            SetCartTier(plotId, GetCartTier(plotId) + 1);
            result = "";
        }
        ResultRpc(result ?? "", RpcTarget.Single(sender, RpcTargetUse.Temp));
    }

    // Each of the store's carts becomes the new one, where it stands, with its load moved over.
    private void ServerReplaceCarts(StorePlot plot, CartNet prefab)
    {
        var old = new List<CartNet>(CartNet.GetCartsOf(plot));
        foreach (var cart in old)
        {
            var made = Instantiate(prefab, cart.transform.position, cart.transform.rotation);
            made.ServerSetHome(plot, cart.HomePose);
            made.NetworkObject.Spawn(true);
            cart.ServerMoveLoadTo(made);
            cart.NetworkObject.Despawn(true);
        }
    }

    private void SetCartTier(int plotId, int tier)
    {
        for (int i = 0; i < upgrades.Count; i++)
            if (upgrades[i].plotId == plotId)
            {
                var u = upgrades[i];
                u.cartTier = tier;
                upgrades[i] = u;
                return;
            }
        upgrades.Add(new PlotUpgrades { plotId = plotId, cartTier = tier });
    }

    /// <summary>Send the store's carts back to their home spots (free).</summary>
    public void RequestRecallCarts(int plotId)
    {
        if (MallManager.Instance != null) MallManager.Instance.RequestRecallCartsServerRpc(plotId);
    }

    /* ========================= SHARED ========================= */

    private static bool TryGetOwnPlot(ulong clientId, int plotId, out StorePlot plot)
    {
        plot = null;
        return MallManager.Instance != null && MallManager.Instance.TryGetPlot(plotId, out plot) && plot.IsAuthorized(clientId);
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void ResultRpc(string result, RpcParams rpcParams) => ResultReceived?.Invoke(result);
}
