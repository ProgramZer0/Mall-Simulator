using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Runs mall events. Put it on the MallManager object and drag your Mall Event assets into Events.
///
/// The host decides when events start and end (their triggers, or ServerStart / ServerStop from code,
/// a button or a vote later). Only "which event, from when, until when" is synced; every machine has
/// the event assets, so each one works out the effects itself (see MarketModifiers).
///
/// Times are seconds of play (the host's network clock).
///
/// Seasons: drag Mall Event assets into Seasons and they run one after another in a loop, each for its
/// own Duration (their trigger settings are ignored). Exactly one season is always running.
///
/// Caps: each entry in Caps limits how far events can push one stat, as a percent of the item's normal
/// value (e.g. Order Cost -75 to +300 = never under a quarter or over 4x the catalog price). Stats with no
/// entry aren't capped.
/// </summary>
public class MallEventManager : NetworkBehaviour
{
    public struct ActiveEvent : INetworkSerializeByMemcpy, IEquatable<ActiveEvent>
    {
        public int eventIndex; // into Events
        public double startTime;
        public double endTime;

        public bool Equals(ActiveEvent o) => eventIndex == o.eventIndex && startTime == o.startTime && endTime == o.endTime;
        public override bool Equals(object obj) => obj is ActiveEvent o && Equals(o);
        public override int GetHashCode() => eventIndex * 397 ^ startTime.GetHashCode();
    }

    /// <summary>Limits on how far events can move one stat (percent of its normal value).</summary>
    [Serializable]
    public class StatCap
    {
        public MallEventDef.Stat stat;
        [Tooltip("Lowest it can go, as a percent change. -75 = never below a quarter of normal. -100 = can reach 0.")]
        public float minPercent = -75f;
        [Tooltip("Highest it can go, as a percent change. 300 = never above 4x normal.")]
        public float maxPercent = 300f;

        public StatCap() { }
        public StatCap(MallEventDef.Stat stat, float min, float max) { this.stat = stat; minPercent = min; maxPercent = max; }
    }

    public static MallEventManager Instance { get; private set; }

    [Tooltip("Every event that can happen. Their order matters once a game is running (it's how they're synced).")]
    [SerializeField] private List<MallEventDef> events = new();

    [Header("Seasons")]
    [Tooltip("Run the seasons below in a loop.")]
    [SerializeField] private bool seasonsEnabled = true;
    [Tooltip("Season events in order (Spring, Summer...). Each lasts its own Duration, then the next starts. Don't also put them in Events (it's fine if you do).")]
    [SerializeField] private List<MallEventDef> seasons = new();
    [Tooltip("Which season a new game starts in (0 = first).")]
    [SerializeField] private int startSeason;

    [Header("Caps")]
    [Tooltip("How far events can push each stat, all events together. Stats not listed have no cap (e.g. Demand, Attractiveness).")]
    [SerializeField] private List<StatCap> caps = new()
    {
        new StatCap(MallEventDef.Stat.OrderCost, -75f, 300f),
        new StatCap(MallEventDef.Stat.DeliveryCost, 0f, 100f),
        new StatCap(MallEventDef.Stat.DeliveryTime, -75f, 300f),
        new StatCap(MallEventDef.Stat.MarketPrice, -75f, 300f),
    };

    [Header("Testing")]
    [Tooltip("Show running events in a corner of the screen (for testing).")]
    [SerializeField] private bool showDebugHud = true;
    [Tooltip("Dev (host only): F7 starts the next event in the list, F8 stops them all (not the season), F9 skips to the next season.")]
    [SerializeField] private bool devHotkeys = true;

    private NetworkList<ActiveEvent> active;
    private readonly NetworkVariable<int> currentSeason = new(-1);
    private bool seasonRunning;    // server: the current season has been started at least once
    private double[] nextRoll;     // server: Random triggers
    private bool[] firedOnce;      // server: AtTime triggers
    private double[] nextRepeat;   // server: Repeating triggers
    private double sessionStart;
    private double seasonStart;      // server: when the current season began (session start without seasons)
    private int[] timesThisSeason;   // server: starts of each event this season (Max Per Season)
    private float nextCheck;
    private int devNext;

    /// <summary>Raised on every machine when an event starts or ends.</summary>
    public static event Action Changed;

    public IReadOnlyList<MallEventDef> Events => events;
    public IReadOnlyList<MallEventDef> Seasons => seasons;

    /// <summary>The season running now (null if seasons are off or none are set).</summary>
    public MallEventDef CurrentSeason
    {
        get
        {
            int i = IsSpawned ? currentSeason.Value : -1;
            return i >= 0 && i < seasons.Count ? seasons[i] : null;
        }
    }

    /// <summary>Seconds until the next season starts.</summary>
    public float SecondsLeftInSeason => CurrentSeason != null ? SecondsLeft(CurrentSeason) : 0f;

    /// <summary>The cap for a stat, if it has one (percent change limits).</summary>
    public bool TryGetCap(MallEventDef.Stat stat, out float minPercent, out float maxPercent)
    {
        foreach (var c in caps)
            if (c != null && c.stat == stat)
            {
                minPercent = Mathf.Min(c.minPercent, c.maxPercent);
                maxPercent = Mathf.Max(c.minPercent, c.maxPercent);
                return true;
            }
        minPercent = maxPercent = 0f;
        return false;
    }

    private void Awake()
    {
        active = new NetworkList<ActiveEvent>(null, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // Seasons sync like any other event, so they need a place in Events. Done the same way on every
        // machine, so the indexes match.
        seasons.RemoveAll(x => x == null);
        foreach (var s in seasons)
            if (!events.Contains(s)) events.Add(s);
    }

    private void OnValidate() => MarketModifiers.Invalidate();

    public override void OnNetworkSpawn()
    {
        if (Instance != null && Instance != this) Debug.LogWarning("[MallEventManager] More than one; using the newest.", this);
        Instance = this;
        active.OnListChanged += OnActiveChanged;
        currentSeason.OnValueChanged += OnSeasonChanged;
        MarketModifiers.Invalidate();

        if (IsServer)
        {
            sessionStart = seasonStart = NetworkManager.ServerTime.Time;
            nextRoll = new double[events.Count];
            firedOnce = new bool[events.Count];
            nextRepeat = new double[events.Count];
            timesThisSeason = new int[events.Count];
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                if (e == null) continue;
                nextRoll[i] = sessionStart + Mathf.Max(1f, e.checkEvery);
                nextRepeat[i] = sessionStart + Mathf.Max(0f, e.startAt);
            }

            if (seasonsEnabled && seasons.Count > 0)
            {
                currentSeason.Value = Mathf.Clamp(startSeason, 0, seasons.Count - 1);
                seasonRunning = false;
                ServerTickSeason();
            }
        }
        Changed?.Invoke();
    }

    public override void OnNetworkDespawn()
    {
        active.OnListChanged -= OnActiveChanged;
        currentSeason.OnValueChanged -= OnSeasonChanged;
        if (Instance == this) Instance = null;
        MarketModifiers.Invalidate();
        Changed?.Invoke();
    }

    private void OnActiveChanged(NetworkListEvent<ActiveEvent> change)
    {
        MarketModifiers.Invalidate();
        StoreListing.MarkAllDirty();
        Changed?.Invoke();
    }

    private void OnSeasonChanged(int before, int now) => Changed?.Invoke();

    private bool IsSeason(MallEventDef def) => def != null && seasons.Contains(def);

    /* ========================= QUERIES ========================= */

    /// <summary>Every event running right now (on this machine).</summary>
    public void GetActive(List<MallEventDef> into)
    {
        into.Clear();
        if (!IsSpawned) return;
        foreach (var a in active)
            if (a.eventIndex >= 0 && a.eventIndex < events.Count && events[a.eventIndex] != null) into.Add(events[a.eventIndex]);
    }

    /// <summary>Is this event running?</summary>
    public bool IsActive(MallEventDef def)
    {
        int index = events.IndexOf(def);
        if (index < 0 || !IsSpawned) return false;
        foreach (var a in active) if (a.eventIndex == index) return true;
        return false;
    }

    /// <summary>Seconds left on a running event (0 if it isn't running).</summary>
    public float SecondsLeft(MallEventDef def)
    {
        int index = events.IndexOf(def);
        if (index < 0 || !IsSpawned) return 0f;
        double now = NetworkManager.ServerTime.Time;
        foreach (var a in active) if (a.eventIndex == index) return (float)Math.Max(0d, a.endTime - now);
        return 0f;
    }

    /* ========================= HOST ========================= */

    /// <summary>Host: start an event now (restarts its timer if it's already running).</summary>
    public bool ServerStart(MallEventDef def)
    {
        if (!IsServer) return false;
        int index = events.IndexOf(def);
        if (index < 0)
        {
            Debug.LogWarning($"[MallEventManager] '{(def != null ? def.name : "null")}' isn't in the Events list.", this);
            return false;
        }

        double now = NetworkManager.ServerTime.Time;
        var entry = new ActiveEvent { eventIndex = index, startTime = now, endTime = now + Mathf.Max(1f, def.duration) };
        for (int i = 0; i < active.Count; i++)
            if (active[i].eventIndex == index) { active[i] = entry; return true; }
        active.Add(entry);
        return true;
    }

    /// <summary>Host: end an event early.</summary>
    public void ServerStop(MallEventDef def)
    {
        if (!IsServer) return;
        int index = events.IndexOf(def);
        for (int i = active.Count - 1; i >= 0; i--)
            if (active[i].eventIndex == index) active.RemoveAt(i);
    }

    /// <summary>Host: end every event (the season keeps running).</summary>
    public void ServerStopAll()
    {
        if (!IsServer) return;
        for (int i = active.Count - 1; i >= 0; i--)
        {
            int index = active[i].eventIndex;
            if (index >= 0 && index < events.Count && IsSeason(events[index])) continue;
            active.RemoveAt(i);
        }
    }

    /// <summary>Host: end the current season now and start the next.</summary>
    public void ServerNextSeason()
    {
        if (!IsServer || seasons.Count == 0) return;
        var now = CurrentSeason;
        if (now != null) ServerStop(now); // the season tick moves on to the next one
        ServerTickSeason();
    }

    /// <summary>Host: jump to a season (by its place in Seasons); its timer starts from full.</summary>
    public void ServerSetSeason(int index)
    {
        if (!IsServer || index < 0 || index >= seasons.Count) return;
        var now = CurrentSeason;
        if (now != null) ServerStop(now);
        currentSeason.Value = index;
        seasonRunning = true;
        ServerStart(seasons[index]);
        OnServerSeasonStarted();
    }

    // Keeps one season running: when the current one ends, the next one starts.
    private void ServerTickSeason()
    {
        if (!seasonsEnabled || seasons.Count == 0) return;
        var season = CurrentSeason;
        if (season != null && IsActive(season)) return;

        int next;
        if (season == null) next = Mathf.Clamp(startSeason, 0, seasons.Count - 1); // not set yet
        else if (seasonRunning) next = (currentSeason.Value + 1) % seasons.Count; // it ended: move on
        else next = currentSeason.Value;                                          // first start
        currentSeason.Value = next;
        seasonRunning = true;
        ServerStart(seasons[currentSeason.Value]);
        OnServerSeasonStarted();
    }

    // A new season began: seasonal events start their clocks and counts again, and the ones that end
    // with the season stop.
    private void OnServerSeasonStarted()
    {
        double now = NetworkManager.ServerTime.Time;
        seasonStart = now;
        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (e == null || IsSeason(e)) continue;
            if (timesThisSeason != null) timesThisSeason[i] = 0;
            if (e.endsWithSeason) ServerStop(e);
            if (e.timedFromSeasonStart)
            {
                if (firedOnce != null) firedOnce[i] = false;
                if (nextRepeat != null) nextRepeat[i] = now + Mathf.Max(0f, e.startAt);
            }
        }
    }

    /// <summary>Can this event happen in the current season (its Only In Seasons)?</summary>
    public bool IsInItsSeason(MallEventDef e)
    {
        if (e == null || e.onlyInSeasons == null || e.onlyInSeasons.Count == 0) return true;
        var season = CurrentSeason;
        return season != null && e.onlyInSeasons.Contains(season);
    }

    /// <summary>Seconds since the current season began (since the session began without seasons).</summary>
    public float SecondsIntoSeason => IsServer ? (float)(NetworkManager.ServerTime.Time - seasonStart) : 0f;

    // Host: start an event from its trigger (counts toward Max Per Season).
    private void TriggerStart(int index, MallEventDef e)
    {
        if (e.maxPerSeason > 0 && timesThisSeason[index] >= e.maxPerSeason) return;
        if (ServerStart(e)) timesThisSeason[index]++;
    }

    private void Update()
    {
        if (!IsSpawned) return;

        if (IsServer && devHotkeys && !UIState.IsMenuOpen)
        {
            if (Input.GetKeyDown(KeyCode.F7) && events.Count > 0)
            {
                var def = events[devNext++ % events.Count];
                if (def != null && !IsSeason(def) && ServerStart(def)) Debug.Log($"[MallEventManager] Dev: started '{def.displayName}'.", this);
            }
            if (Input.GetKeyDown(KeyCode.F8)) ServerStopAll();
            if (Input.GetKeyDown(KeyCode.F9)) ServerNextSeason();
        }

        if (!IsServer || Time.time < nextCheck) return;
        nextCheck = Time.time + 0.5f;
        double now = NetworkManager.ServerTime.Time;

        // End what's run its time.
        for (int i = active.Count - 1; i >= 0; i--)
            if (now >= active[i].endTime) active.RemoveAt(i);

        ServerTickSeason();

        // Start what's due.
        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (e == null || IsSeason(e)) continue; // seasons run on the cycle, not their own trigger
            bool inSeason = IsInItsSeason(e);       // outside its seasons, it waits
            double from = e.timedFromSeasonStart ? seasonStart : sessionStart;
            switch (e.trigger)
            {
                case MallEventDef.TriggerType.AtTime:
                    if (inSeason && !firedOnce[i] && now - from >= e.startAt) { firedOnce[i] = true; TriggerStart(i, e); }
                    break;
                case MallEventDef.TriggerType.Repeating:
                    if (now >= nextRepeat[i])
                    {
                        nextRepeat[i] = now + Mathf.Max(1f, e.interval);
                        if (inSeason) TriggerStart(i, e);
                    }
                    break;
                case MallEventDef.TriggerType.Random:
                    if (now >= nextRoll[i])
                    {
                        nextRoll[i] = now + Mathf.Max(1f, e.checkEvery);
                        if (inSeason && !IsActive(e) && UnityEngine.Random.value < e.chance) TriggerStart(i, e);
                    }
                    break;
            }
        }
    }

    /* ========================= DEBUG ========================= */

    private readonly List<MallEventDef> hudList = new();

    private void OnGUI()
    {
        if (!showDebugHud || !Debug.isDebugBuild || !IsSpawned || active.Count == 0) return;
        GetActive(hudList);
        var season = CurrentSeason;
        if (season != null) { hudList.Remove(season); hudList.Insert(0, season); } // season on top
        var r = new Rect(10f, Screen.height - 30f - hudList.Count * 20f, 340f, 10f + hudList.Count * 20f);
        GUI.Box(r, GUIContent.none);
        for (int i = 0; i < hudList.Count; i++)
            GUI.Label(new Rect(r.x + 8, r.y + 4 + i * 20, 330, 20), $"{(hudList[i] == season ? "Season: " : "")}{hudList[i].displayName}: {SecondsLeft(hudList[i]):0}s left");
    }
}
