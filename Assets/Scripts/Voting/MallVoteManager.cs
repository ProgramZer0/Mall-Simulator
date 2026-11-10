using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Mall votes (GDD 14a). Every so often the mall puts a few policies on a ballot; players vote at a
/// VoteKiosk; when the ballot closes, the winner starts as a Mall Event for its Duration.
///
/// Policies are Mall Events with Votable ticked (in the MallEventManager's Events list). A ballot
/// picks Options Per Ballot of them at random, leaving out ones already running or out of season.
///
/// Votes: each player has one vote and can change it until the ballot closes. How much it counts is
/// Vote Weight (1 for everyone for now; voting points can plug in there later). The most votes wins;
/// a tie is settled at random; with no votes at all nothing happens (unless No Votes Picks Random).
///
/// Setup: put this on a scene object with a NetworkObject (the MallManager's is fine), next to or
/// alongside the MallEventManager. Add VoteKiosks around the mall.
/// </summary>
public class MallVoteManager : NetworkBehaviour
{
    public static MallVoteManager Instance { get; private set; }

    [Tooltip("Seconds from one ballot opening to the next.")]
    [SerializeField] private float ballotEvery = 900f;
    [Tooltip("Seconds into the session before the first ballot opens.")]
    [SerializeField] private float firstBallotAt = 300f;
    [Tooltip("How long a ballot stays open for voting, in seconds.")]
    [SerializeField] private float ballotLength = 120f;
    [Tooltip("How many policies are on each ballot (kiosks show as many as they have buttons).")]
    [Range(2, 6)] [SerializeField] private int optionsPerBallot = 3;
    [Tooltip("When nobody votes, start one of the options anyway.")]
    [SerializeField] private bool noVotesPicksRandom;

    [Header("Testing")]
    [Tooltip("Dev (host only): F10 opens a ballot now (or closes the open one).")]
    [SerializeField] private bool devHotkey = true;

    /// <summary>
    /// How much a player's vote counts (host). Everyone's is 1 for now; set this to plug in voting
    /// points later (it's asked for each vote when the ballot is counted).
    /// </summary>
    public static Func<ulong, int> VoteWeight = _ => 1;

    public struct Vote : INetworkSerializeByMemcpy, IEquatable<Vote>
    {
        public ulong clientId;
        public int option; // index into the ballot's options
        public bool Equals(Vote o) => clientId == o.clientId && option == o.option;
        public override bool Equals(object obj) => obj is Vote o && Equals(o);
        public override int GetHashCode() => clientId.GetHashCode() ^ option;
    }

    private NetworkList<int> options;   // MallEventManager event indices on the open ballot
    private NetworkList<int> tallies;   // weighted votes per option
    private NetworkList<Vote> votes;    // who voted for what (so kiosks can light up your choice)
    private readonly NetworkVariable<bool> open = new(false);
    private readonly NetworkVariable<double> closesAt = new(0);
    private readonly NetworkVariable<int> lastWinner = new(-1); // event index, -1 = none yet / nothing won

    private double nextBallot;

    /// <summary>Raised on every machine when the ballot opens, closes or a vote changes.</summary>
    public static event Action Changed;

    public bool IsOpen => IsSpawned && open.Value;
    public int OptionCount => IsSpawned ? options.Count : 0;
    public float SecondsLeft => IsOpen ? (float)Math.Max(0d, closesAt.Value - NetworkManager.ServerTime.Time) : 0f;
    public float SecondsToNextBallot => IsServer && !IsOpen ? (float)Math.Max(0d, nextBallot - NetworkManager.ServerTime.Time) : 0f;

    private void Awake()
    {
        options = new NetworkList<int>(null, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        tallies = new NetworkList<int>(null, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        votes = new NetworkList<Vote>(null, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[MallVoteManager] More than one in the scene; using the first.", this);
            return;
        }
        Instance = this;
    }

    public override void OnNetworkSpawn()
    {
        options.OnListChanged += _ => Changed?.Invoke();
        tallies.OnListChanged += _ => Changed?.Invoke();
        votes.OnListChanged += _ => Changed?.Invoke();
        open.OnValueChanged += (_, _) => Changed?.Invoke();
        lastWinner.OnValueChanged += (_, _) => Changed?.Invoke();
        if (IsServer) nextBallot = NetworkManager.ServerTime.Time + Mathf.Max(0f, firstBallotAt);
        Changed?.Invoke();
    }

    public override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    /* ========================= READING (every machine) ========================= */

    /// <summary>The event for one option of the open ballot (null if none).</summary>
    public MallEventDef GetOption(int option)
    {
        var events = MallEventManager.Instance;
        if (!IsSpawned || events == null || option < 0 || option >= options.Count) return null;
        int index = options[option];
        return index >= 0 && index < events.Events.Count ? events.Events[index] : null;
    }

    public int GetTally(int option) => IsSpawned && option >= 0 && option < tallies.Count ? tallies[option] : 0;

    /// <summary>Which option this player voted for, or -1.</summary>
    public int VoteOf(ulong clientId)
    {
        if (!IsSpawned) return -1;
        foreach (var v in votes) if (v.clientId == clientId) return v.option;
        return -1;
    }

    /// <summary>What the last ballot started (null if nothing yet, or nobody voted).</summary>
    public MallEventDef LastWinner
    {
        get
        {
            var events = MallEventManager.Instance;
            int i = IsSpawned ? lastWinner.Value : -1;
            return events != null && i >= 0 && i < events.Events.Count ? events.Events[i] : null;
        }
    }

    /* ========================= VOTING ========================= */

    /// <summary>Vote for an option of the open ballot (again to change it).</summary>
    public void Cast(int option) => CastRpc(option);

    [Rpc(SendTo.Server, RequireOwnership = false)]
    private void CastRpc(int option, RpcParams rpcParams = default)
    {
        if (!open.Value || option < 0 || option >= options.Count) return;
        ulong who = rpcParams.Receive.SenderClientId;
        for (int i = 0; i < votes.Count; i++)
            if (votes[i].clientId == who)
            {
                if (votes[i].option == option) return;
                votes[i] = new Vote { clientId = who, option = option };
                ServerRecount();
                return;
            }
        votes.Add(new Vote { clientId = who, option = option });
        ServerRecount();
    }

    private void ServerRecount()
    {
        var counts = new int[options.Count];
        var connected = NetworkManager.ConnectedClients;
        foreach (var v in votes)
            if (v.option >= 0 && v.option < counts.Length && connected.ContainsKey(v.clientId))
                counts[v.option] += Mathf.Max(0, VoteWeight(v.clientId));
        for (int i = 0; i < counts.Length; i++)
        {
            if (i < tallies.Count) { if (tallies[i] != counts[i]) tallies[i] = counts[i]; }
            else tallies.Add(counts[i]);
        }
    }

    /* ========================= BALLOTS (host) ========================= */

    private void Update()
    {
        if (!IsServer || !IsSpawned) return;
        double now = NetworkManager.ServerTime.Time;

        if (devHotkey && !UIState.IsMenuOpen && Input.GetKeyDown(KeyCode.F10))
        {
            if (open.Value) ServerClose();
            else ServerOpenBallot();
        }

        if (open.Value)
        {
            if (now >= closesAt.Value) ServerClose();
        }
        else if (now >= nextBallot) ServerOpenBallot();
    }

    /// <summary>Host: put a new ballot up now (does nothing if one is open or nothing is votable).</summary>
    public bool ServerOpenBallot()
    {
        if (!IsServer || open.Value) return false;
        double now = NetworkManager.ServerTime.Time;
        nextBallot = now + Mathf.Max(ballotLength + 1f, ballotEvery);

        var events = MallEventManager.Instance;
        if (events == null) return false;
        var pool = new List<int>();
        for (int i = 0; i < events.Events.Count; i++)
        {
            var e = events.Events[i];
            if (e != null && e.votable && !events.IsActive(e) && events.IsInItsSeason(e)) pool.Add(i);
        }
        if (pool.Count < 2)
        {
            Debug.Log("[MallVoteManager] Fewer than 2 votable events available; no ballot this time.", this);
            return false;
        }

        options.Clear();
        tallies.Clear();
        votes.Clear();
        int count = Mathf.Min(optionsPerBallot, pool.Count);
        for (int k = 0; k < count; k++)
        {
            int pick = UnityEngine.Random.Range(0, pool.Count);
            options.Add(pool[pick]);
            tallies.Add(0);
            pool.RemoveAt(pick);
        }
        closesAt.Value = now + Mathf.Max(5f, ballotLength);
        open.Value = true;
        return true;
    }

    /// <summary>Host: close the open ballot now and start the winner.</summary>
    public void ServerClose()
    {
        if (!IsServer || !open.Value) return;
        ServerRecount();

        int best = -1, bestCount = 0;
        var tied = new List<int>();
        for (int i = 0; i < tallies.Count; i++)
        {
            if (tallies[i] > bestCount) { bestCount = tallies[i]; tied.Clear(); tied.Add(i); }
            else if (tallies[i] == bestCount && bestCount > 0) tied.Add(i);
        }
        if (tied.Count > 0) best = tied[UnityEngine.Random.Range(0, tied.Count)];
        else if (noVotesPicksRandom && options.Count > 0) best = UnityEngine.Random.Range(0, options.Count);

        int winner = best >= 0 ? options[best] : -1;
        var events = MallEventManager.Instance;
        if (winner >= 0 && events != null) events.ServerStart(events.Events[winner]);
        lastWinner.Value = winner;
        open.Value = false;
    }
}
