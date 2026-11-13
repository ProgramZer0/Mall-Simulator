using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Licenses (one in the scene, with a NetworkObject): a team needs the license for each licensed
/// category of an item to order it. A limited number per mall; how many a team can hold goes up with
/// its level; selling one back refunds part of its price and frees it for others. Items already on
/// shelves can still be sold after a license is sold.
///
/// Network: one small synced list of (license, team) pairs. The host checks every buy, sell and order.
/// </summary>
public class LicenseManager : NetworkBehaviour
{
    public static LicenseManager Instance { get; private set; }

    public struct Held : INetworkSerializeByMemcpy, IEquatable<Held>
    {
        public int licenseId;
        public ulong teamId;
        public bool Equals(Held o) => licenseId == o.licenseId && teamId == o.teamId;
        public override bool Equals(object obj) => obj is Held o && Equals(o);
        public override int GetHashCode() => licenseId * 397 ^ teamId.GetHashCode();
    }

    [SerializeField] private List<LicenseDef> licenses = new();
    [Tooltip("Share of the price given back when a license is sold (0.5 = half).")]
    [SerializeField, Range(0f, 1f)] private float sellBackShare = 0.5f;
    [Tooltip("Licenses a team can hold at level 1.")]
    [SerializeField, Min(0)] private int licensesAtStart = 1;
    [Tooltip("One more license every this many levels.")]
    [SerializeField, Min(1)] private int levelsPerExtraLicense = 5;

    /// <summary>Who holds what changed.</summary>
    public event Action Changed;
    /// <summary>Owner: the answer to a buy or sell ("" = done).</summary>
    public event Action<string> ResultReceived;

    public IReadOnlyList<LicenseDef> Licenses => licenses;

    private NetworkList<Held> held;
    private readonly Dictionary<int, LicenseDef> byId = new();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[LicenseManager] More than one in the scene; using the first.", this);
            return;
        }
        Instance = this;
        held = new NetworkList<Held>(null, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        foreach (var l in licenses)
        {
            if (l == null) continue;
            if (l.id == 0 || byId.ContainsKey(l.id)) Debug.LogWarning($"[LicenseManager] License '{l.name}' needs its own Id (not 0, not shared).", l);
            else byId[l.id] = l;
        }
    }

    public override void OnNetworkSpawn() => held.OnListChanged += OnHeldChanged;
    public override void OnNetworkDespawn() => held.OnListChanged -= OnHeldChanged;

    public override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    private void OnHeldChanged(NetworkListEvent<Held> e) => Changed?.Invoke();

    /* ========================= QUESTIONS ========================= */

    public bool Holds(ulong team, int licenseId)
    {
        if (held != null) foreach (var h in held) if (h.teamId == team && h.licenseId == licenseId) return true;
        return false;
    }

    public int HolderCount(int licenseId)
    {
        int n = 0;
        if (held != null) foreach (var h in held) if (h.licenseId == licenseId) n++;
        return n;
    }

    public int HeldBy(ulong team)
    {
        int n = 0;
        if (held != null) foreach (var h in held) if (h.teamId == team) n++;
        return n;
    }

    /// <summary>Which teams hold a license.</summary>
    public void GetHolders(int licenseId, List<ulong> into)
    {
        into.Clear();
        if (held != null) foreach (var h in held) if (h.licenseId == licenseId) into.Add(h.teamId);
    }

    /// <summary>How many licenses a team's level lets it hold.</summary>
    public int AllowedFor(ulong team) => licensesAtStart + (ProgressionManager.LevelOf(team) - 1) / levelsPerExtraLicense;

    public int SellBackPrice(LicenseDef l) => Mathf.FloorToInt(l.price * sellBackShare);

    /// <summary>The first license this item needs that the team doesn't hold (null: it can be ordered).</summary>
    public LicenseDef MissingFor(ulong team, ItemDef item)
    {
        if (item == null) return null;
        foreach (var l in licenses)
            if (l != null && item.HasCategory(l.category) && !Holds(team, l.id)) return l;
        return null;
    }

    /// <summary>Can this team order this item, license-wise? Missing: the license it needs.</summary>
    public static bool CanOrder(ulong team, ItemDef item, out LicenseDef missing)
    {
        missing = Instance != null && Instance.IsSpawned ? Instance.MissingFor(team, item) : null;
        return missing == null;
    }

    /// <summary>Can this team buy this license? Reason says why not.</summary>
    public bool CanBuy(ulong team, LicenseDef l, out string reason)
    {
        reason = null;
        if (l == null || !byId.ContainsKey(l.id)) reason = "Unknown license";
        else if (Holds(team, l.id)) reason = "Already yours";
        else if (l.perMall > 0 && HolderCount(l.id) >= l.perMall) reason = "None left in the mall";
        else if (l.minLevel > ProgressionManager.LevelOf(team)) reason = $"Needs level {l.minLevel}";
        else if (HeldBy(team) >= AllowedFor(team)) reason = $"Your team can hold {AllowedFor(team)} (more every {levelsPerExtraLicense} levels)";
        else if (EconomyManager.Instance != null && EconomyManager.Instance.GetTeamMoney(team) < l.price) reason = "Not enough money";
        return reason == null;
    }

    /* ========================= BUY / SELL ========================= */

    public void RequestBuy(int licenseId) => BuyRpc(licenseId);
    public void RequestSell(int licenseId) => SellRpc(licenseId);

    [Rpc(SendTo.Server, RequireOwnership = false)]
    private void BuyRpc(int licenseId, RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        ulong team = MallManager.GetTeamOf(sender);
        string result;
        if (!byId.TryGetValue(licenseId, out var l)) result = "Unknown license";
        else if (!CanBuy(team, l, out result)) { }
        else if (EconomyManager.Instance == null || !EconomyManager.Instance.ServerTrySpend(team, l.price)) result = "Not enough money";
        else
        {
            held.Add(new Held { licenseId = l.id, teamId = team });
            result = "";
        }
        ResultRpc(result ?? "", RpcTarget.Single(sender, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.Server, RequireOwnership = false)]
    private void SellRpc(int licenseId, RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        ulong team = MallManager.GetTeamOf(sender);
        string result = "Not yours";
        for (int i = 0; i < held.Count; i++)
        {
            if (held[i].teamId != team || held[i].licenseId != licenseId) continue;
            held.RemoveAt(i);
            if (byId.TryGetValue(licenseId, out var l) && EconomyManager.Instance != null)
                EconomyManager.Instance.ServerAddMoney(team, SellBackPrice(l));
            result = "";
            break;
        }
        ResultRpc(result, RpcTarget.Single(sender, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void ResultRpc(string result, RpcParams rpcParams) => ResultReceived?.Invoke(result);

    /// <summary>Host: a team is gone (reset): its licenses go back to the mall.</summary>
    public void ServerForgetTeam(ulong team)
    {
        if (!IsServer) return;
        for (int i = held.Count - 1; i >= 0; i--) if (held[i].teamId == team) held.RemoveAt(i);
    }
}
