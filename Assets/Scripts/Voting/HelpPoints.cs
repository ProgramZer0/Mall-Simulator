using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Help points (one in the scene, with a NetworkObject): earned by players for helping the mall. Each
/// point gives the player's mall vote more weight: 1 + points / Points Per Extra Vote (at most Max
/// Extra Votes more). With Spend On Vote, the points that added weight are used up when the ballot
/// closes. Each kind of help has a cap per voting period (a period ends when a ballot closes), so one
/// kind can't be farmed.
///
/// Award from anywhere on the host: HelpPoints.Instance.ServerAward(clientId, HelpAction.X).
/// Network: one small synced list of (player, points); the player gets a notification.
/// </summary>
public class HelpPoints : NetworkBehaviour
{
    public static HelpPoints Instance { get; private set; }

    /// <summary>Kinds of help. Add new ones at the end.</summary>
    public enum HelpAction : byte
    {
        GaveUpUnusedLicense,  // gave up a license without ordering anything with it
        FoundLostChild,       // customer AI
        StoppedThief,         // customer AI
        DeliveredForOthers,   // brought another team's delivery to their store
    }

    [Serializable]
    public struct Rule
    {
        public HelpAction action;
        [Tooltip("Points per time.")]
        [Min(0)] public int points;
        [Tooltip("Most times it counts per voting period (0 = no limit).")]
        [Min(0)] public int maxPerPeriod;
        [Tooltip("Shown in the notification: \"+1 help point: ...\".")]
        public string description;
    }

    public struct PlayerPoints : INetworkSerializeByMemcpy, IEquatable<PlayerPoints>
    {
        public ulong clientId;
        public int points;
        public bool Equals(PlayerPoints o) => clientId == o.clientId && points == o.points;
        public override bool Equals(object obj) => obj is PlayerPoints o && Equals(o);
        public override int GetHashCode() => clientId.GetHashCode() ^ points;
    }

    [SerializeField] private List<Rule> rules = new()
    {
        new Rule { action = HelpAction.GaveUpUnusedLicense, points = 1, maxPerPeriod = 1, description = "gave up a license you didn't use" },
        new Rule { action = HelpAction.FoundLostChild, points = 1, maxPerPeriod = 3, description = "helped a lost child" },
        new Rule { action = HelpAction.StoppedThief, points = 1, maxPerPeriod = 3, description = "stopped a thief" },
        new Rule { action = HelpAction.DeliveredForOthers, points = 1, maxPerPeriod = 2, description = "delivered another store's order" },
    };

    [Header("Votes")]
    [Tooltip("Help points for each extra vote.")]
    [SerializeField, Min(1)] private int pointsPerExtraVote = 1;
    [Tooltip("Most extra votes a player can have (0 = no limit).")]
    [SerializeField, Min(0)] private int maxExtraVotes = 0;
    [Tooltip("Points that added weight to a vote are used up when the ballot closes (off: they're kept).")]
    [SerializeField] private bool spendOnVote = true;

    public event Action Changed;

    private NetworkList<PlayerPoints> points;
    private readonly Dictionary<(ulong, HelpAction), int> thisPeriod = new(); // host only

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[HelpPoints] More than one in the scene; using the first.", this);
            return;
        }
        Instance = this;
        points = new NetworkList<PlayerPoints>(null, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    }

    public override void OnNetworkSpawn()
    {
        points.OnListChanged += OnPointsChanged;
        if (IsServer) MallVoteManager.VoteWeight = VoteWeightOf;
    }

    /// <summary>How much a player's vote counts: 1 plus their extra votes.</summary>
    public int VoteWeightOf(ulong clientId) => 1 + ExtraVotes(clientId);

    public int ExtraVotes(ulong clientId)
    {
        int extra = GetPoints(clientId) / pointsPerExtraVote;
        return maxExtraVotes > 0 ? Mathf.Min(extra, maxExtraVotes) : extra;
    }
    public override void OnNetworkDespawn() => points.OnListChanged -= OnPointsChanged;

    public override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    private void OnPointsChanged(NetworkListEvent<PlayerPoints> e) => Changed?.Invoke();

    public int GetPoints(ulong clientId)
    {
        if (points != null) foreach (var p in points) if (p.clientId == clientId) return p.points;
        return 0;
    }

    public int LocalPoints => IsSpawned ? GetPoints(NetworkManager.LocalClientId) : 0;

    private bool TryGetRule(HelpAction action, out Rule rule)
    {
        foreach (var r in rules) if (r.action == action) { rule = r; return true; }
        rule = default;
        return false;
    }

    /// <summary>Host: a player helped. False if that kind has hit its cap this voting period.</summary>
    public bool ServerAward(ulong clientId, HelpAction action)
    {
        if (!IsServer || !TryGetRule(action, out var rule) || rule.points <= 0) return false;
        thisPeriod.TryGetValue((clientId, action), out int times);
        if (rule.maxPerPeriod > 0 && times >= rule.maxPerPeriod) return false;
        thisPeriod[(clientId, action)] = times + 1;

        bool found = false;
        for (int i = 0; i < points.Count; i++)
        {
            if (points[i].clientId != clientId) continue;
            var p = points[i];
            p.points += rule.points;
            points[i] = p;
            found = true;
            break;
        }
        if (!found) points.Add(new PlayerPoints { clientId = clientId, points = rule.points });

        AwardedRpc(action, rule.points, RpcTarget.Single(clientId, RpcTargetUse.Temp));
        if (MallVoteManager.Instance != null) MallVoteManager.Instance.ServerRecountVotes(); // a heavier vote now
        return true;
    }

    /// <summary>Host: a ballot closed. Voters' extra votes are used up (Spend On Vote); help caps start over.</summary>
    public void ServerBallotClosed(IEnumerable<ulong> voters)
    {
        if (!IsServer) return;
        if (spendOnVote && voters != null)
            foreach (ulong who in voters)
            {
                int used = ExtraVotes(who) * pointsPerExtraVote;
                if (used <= 0) continue;
                for (int i = 0; i < points.Count; i++)
                {
                    if (points[i].clientId != who) continue;
                    var p = points[i];
                    p.points = Mathf.Max(0, p.points - used);
                    points[i] = p;
                    break;
                }
            }
        ServerNewPeriod();
    }

    /// <summary>Host: a voting period ended (a ballot closed): the caps start over.</summary>
    public void ServerNewPeriod()
    {
        if (IsServer) thisPeriod.Clear();
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void AwardedRpc(HelpAction action, int amount, RpcParams rpcParams)
    {
        string what = TryGetRule(action, out var rule) && !string.IsNullOrEmpty(rule.description) ? rule.description : action.ToString();
        Notifications.Post($"+{amount} help point{(amount == 1 ? "" : "s")}: {what}");
    }
}
