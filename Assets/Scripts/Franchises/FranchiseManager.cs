using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Franchise auctions (one in the scene, with a NetworkObject). Each franchise goes up for open bids;
/// a bid takes the money from the team right away and gives it back when someone outbids it; a bid
/// in the last Extend Window seconds pushes the end back to that. The top bid when time runs out wins:
/// that team alone can order the franchise's items for its Hold Seconds. Bidding for the next hold runs
/// during the last Auction Seconds of the current one, so there's no gap. An auction with no bids just
/// starts over. Items already in a store can still be sold after the franchise is lost.
///
/// Network: one small synced list of franchise states. The host holds the money and checks every bid.
/// Notifications (from the synced state, nothing extra sent): auctions starting, being outbid, winning
/// and losing a franchise.
/// </summary>
public class FranchiseManager : NetworkBehaviour
{
    public static FranchiseManager Instance { get; private set; }

    public const ulong Nobody = ulong.MaxValue;

    public struct State : INetworkSerializeByMemcpy, IEquatable<State>
    {
        public int franchiseId;
        public ulong holder;        // Nobody = not held
        public double heldUntil;    // server time
        public bool bidding;        // an auction is running
        public double auctionEnds;
        public ulong topTeam;       // Nobody = no bids yet
        public int topBid;
        public bool Equals(State o) => franchiseId == o.franchiseId && holder == o.holder && heldUntil == o.heldUntil &&
                                      bidding == o.bidding && auctionEnds == o.auctionEnds && topTeam == o.topTeam && topBid == o.topBid;
        public override bool Equals(object obj) => obj is State o && Equals(o);
        public override int GetHashCode() => franchiseId * 397 ^ topBid;
    }

    [SerializeField] private List<FranchiseDef> franchises = new();
    [Tooltip("Seconds after the game starts before the first auctions (each franchise a bit after the last).")]
    [SerializeField, Min(0f)] private float firstAuctionAfter = 120f;
    [SerializeField, Min(0f)] private float betweenFirstAuctions = 60f;
    [Tooltip("A bid this close to the end pushes the end back to this many seconds away.")]
    [SerializeField, Min(0f)] private float extendWindow = 30f;

    public event Action Changed;
    /// <summary>Owner: the answer to a bid ("" = done).</summary>
    public event Action<string> ResultReceived;

    public IReadOnlyList<FranchiseDef> Franchises => franchises;

    private NetworkList<State> states;
    private readonly Dictionary<int, FranchiseDef> byId = new();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[FranchiseManager] More than one in the scene; using the first.", this);
            return;
        }
        Instance = this;
        states = new NetworkList<State>(null, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        foreach (var f in franchises)
        {
            if (f == null) continue;
            if (f.id == 0 || byId.ContainsKey(f.id)) Debug.LogWarning($"[FranchiseManager] Franchise '{f.name}' needs its own Id.", f);
            else byId[f.id] = f;
        }
    }

    public override void OnNetworkSpawn()
    {
        states.OnListChanged += OnStatesChanged;
        if (IsServer)
        {
            double now = NetworkManager.ServerTime.Time;
            int n = 0;
            foreach (var f in franchises)
            {
                if (f == null || !byId.ContainsKey(f.id)) continue;
                // Not bidding yet: the first auction starts at "held until" minus its length.
                double start = now + firstAuctionAfter + betweenFirstAuctions * n++;
                states.Add(new State
                {
                    franchiseId = f.id, holder = Nobody, heldUntil = start + f.auctionSeconds,
                    topTeam = Nobody,
                });
            }
        }
        Snapshot();
    }

    public override void OnNetworkDespawn() => states.OnListChanged -= OnStatesChanged;

    public override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    /* ========================= QUESTIONS ========================= */

    public bool TryGetState(int franchiseId, out State s)
    {
        if (states != null)
            foreach (var st in states) if (st.franchiseId == franchiseId) { s = st; return true; }
        s = default;
        return false;
    }

    public bool TryGetDef(int id, out FranchiseDef def) => byId.TryGetValue(id, out def);

    public double Now => IsSpawned ? NetworkManager.ServerTime.Time : 0;

    /// <summary>The first franchise this item belongs to that the team doesn't hold (null: it can be ordered).</summary>
    public FranchiseDef MissingFor(ulong team, ItemDef item)
    {
        if (item == null) return null;
        foreach (var f in franchises)
        {
            if (f == null || !item.HasCategory(f.category)) continue;
            if (!TryGetState(f.id, out var s) || s.holder != team) return f;
        }
        return null;
    }

    /// <summary>Can this team order this item, franchise-wise?</summary>
    public static bool CanOrder(ulong team, ItemDef item, out FranchiseDef missing)
    {
        missing = Instance != null && Instance.IsSpawned ? Instance.MissingFor(team, item) : null;
        return missing == null;
    }

    /// <summary>Is this item a franchise's (whoever holds it)?</summary>
    public static bool IsFranchiseItem(ItemDef item)
    {
        if (Instance == null || item == null) return false;
        foreach (var f in Instance.franchises) if (f != null && item.HasCategory(f.category)) return true;
        return false;
    }

    /// <summary>The lowest bid that would be accepted now.</summary>
    public int MinimumBid(FranchiseDef f, State s) => s.topTeam == Nobody ? f.startingBid : s.topBid + f.minRaise;

    public bool CanBid(ulong team, FranchiseDef f, int amount, out string reason)
    {
        reason = null;
        if (f == null || !TryGetState(f.id, out var s)) reason = "Unknown franchise";
        else if (!s.bidding) reason = "Bidding isn't open";
        else if (s.topTeam == team) reason = "You have the top bid";
        else if (f.minLevel > ProgressionManager.LevelOf(team)) reason = $"Needs level {f.minLevel}";
        else if (amount < MinimumBid(f, s)) reason = $"Bid at least ${MinimumBid(f, s):N0}";
        else if (EconomyManager.Instance != null && EconomyManager.Instance.GetTeamMoney(team) < amount) reason = "Not enough money";
        return reason == null;
    }

    /// <summary>"Your team", or the name of one of the team's stores.</summary>
    public static string TeamLabel(ulong team)
    {
        if (team == Nobody) return "nobody";
        var nm = NetworkManager.Singleton;
        if (nm != null && MallManager.GetTeamOf(nm.LocalClientId) == team) return "your team";
        if (MallManager.Instance != null)
            foreach (var p in MallManager.Instance.Plots)
                if (p != null && p.HasOwner && p.OwnerTeamId == team) return p.DisplayName;
        return "another team";
    }

    /* ========================= HOST: AUCTIONS ========================= */

    private float nextCheck;

    private void Update()
    {
        if (!IsServer || !IsSpawned || Time.unscaledTime < nextCheck) return;
        nextCheck = Time.unscaledTime + 0.5f;
        double now = NetworkManager.ServerTime.Time;

        for (int i = 0; i < states.Count; i++)
        {
            var s = states[i];
            if (!byId.TryGetValue(s.franchiseId, out var f)) continue;

            // Bidding opens for the next hold during the last Auction Seconds of this one.
            if (!s.bidding && now >= s.heldUntil - f.auctionSeconds)
            {
                s.bidding = true;
                s.auctionEnds = Math.Max(s.heldUntil, now + 10.0);
                s.topTeam = Nobody;
                s.topBid = 0;
                states[i] = s;
                continue;
            }

            if (s.bidding && now >= s.auctionEnds)
            {
                s.bidding = false;
                if (s.topTeam != Nobody)
                {
                    // The money was taken when the bid was made.
                    s.holder = s.topTeam;
                    s.heldUntil = now + f.holdSeconds;
                }
                else
                {
                    // No bids: nobody holds it and bidding starts over.
                    s.holder = Nobody;
                    s.heldUntil = now + f.auctionSeconds;
                }
                s.topTeam = Nobody;
                s.topBid = 0;
                states[i] = s;
            }
        }
    }

    public void RequestBid(int franchiseId, int amount) => BidRpc(franchiseId, amount);

    [Rpc(SendTo.Server, RequireOwnership = false)]
    private void BidRpc(int franchiseId, int amount, RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        ulong team = MallManager.GetTeamOf(sender);
        string result = "Unknown franchise";
        for (int i = 0; i < states.Count; i++)
        {
            var s = states[i];
            if (s.franchiseId != franchiseId || !byId.TryGetValue(franchiseId, out var f)) continue;
            if (!CanBid(team, f, amount, out result)) break;
            var eco = EconomyManager.Instance;
            if (eco != null && !eco.ServerTrySpend(team, amount)) { result = "Not enough money"; break; }

            // The bid it beat gets its money back.
            if (s.topTeam != Nobody && eco != null) eco.ServerAddMoney(s.topTeam, s.topBid);
            s.topTeam = team;
            s.topBid = amount;
            double now = NetworkManager.ServerTime.Time;
            if (s.auctionEnds - now < extendWindow) s.auctionEnds = now + extendWindow;
            states[i] = s;
            result = "";
            break;
        }
        ResultRpc(result ?? "", RpcTarget.Single(sender, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void ResultRpc(string result, RpcParams rpcParams) => ResultReceived?.Invoke(result);

    /// <summary>Host: a team is gone (reset): its top bids are refunded, its franchises freed.</summary>
    public void ServerForgetTeam(ulong team)
    {
        if (!IsServer) return;
        double now = NetworkManager.ServerTime.Time;
        for (int i = 0; i < states.Count; i++)
        {
            var s = states[i];
            bool changed = false;
            if (s.topTeam == team)
            {
                if (EconomyManager.Instance != null) EconomyManager.Instance.ServerAddMoney(team, s.topBid);
                s.topTeam = Nobody;
                s.topBid = 0;
                changed = true;
            }
            if (s.holder == team && byId.TryGetValue(s.franchiseId, out var f))
            {
                s.holder = Nobody;
                if (!s.bidding) s.heldUntil = Math.Min(s.heldUntil, now + f.auctionSeconds); // up for bids now
                changed = true;
            }
            if (changed) states[i] = s;
        }
    }

    /* ========================= NOTIFICATIONS (LOCAL) ========================= */

    private readonly Dictionary<int, State> before = new();

    private void OnStatesChanged(NetworkListEvent<State> e)
    {
        if (NetworkManager != null && e.Type == NetworkListEvent<State>.EventType.Value && byId.TryGetValue(e.Value.franchiseId, out var f) &&
            before.TryGetValue(e.Value.franchiseId, out var was))
        {
            ulong me = MallManager.GetTeamOf(NetworkManager.LocalClientId);
            var now = e.Value;
            if (!was.bidding && now.bidding)
                Notifications.Post($"Franchise auction open: {f.displayName} ({Mathf.CeilToInt(f.auctionSeconds / 60f)} min)");
            if (was.topTeam == me && now.topTeam != me && now.bidding)
                Notifications.Post($"Outbid on {f.displayName}: ${now.topBid:N0}", "", true);
            if (now.holder == me && was.holder != me)
                Notifications.Post($"Franchise won: {f.displayName}");
            else if (was.holder == me && now.holder != me)
                Notifications.Post($"Franchise lost: {f.displayName}");
        }
        Snapshot();
        Changed?.Invoke();
    }

    private void Snapshot()
    {
        before.Clear();
        foreach (var s in states) before[s.franchiseId] = s;
    }
}
