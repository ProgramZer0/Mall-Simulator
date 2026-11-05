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

    public static MallEventManager Instance { get; private set; }

    [Tooltip("Every event that can happen. Their order matters once a game is running (it's how they're synced).")]
    [SerializeField] private List<MallEventDef> events = new();
    [Tooltip("Show running events in a corner of the screen (for testing).")]
    [SerializeField] private bool showDebugHud = true;
    [Tooltip("Dev: F7 starts the next event in the list, F8 stops them all (host only).")]
    [SerializeField] private bool devHotkeys = true;

    private NetworkList<ActiveEvent> active;
    private double[] nextRoll;     // server: Random triggers
    private bool[] firedOnce;      // server: AtTime triggers
    private double[] nextRepeat;   // server: Repeating triggers
    private double sessionStart;
    private float nextCheck;
    private int devNext;

    /// <summary>Raised on every machine when an event starts or ends.</summary>
    public static event Action Changed;

    public IReadOnlyList<MallEventDef> Events => events;

    private void Awake()
    {
        active = new NetworkList<ActiveEvent>(null, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    }

    public override void OnNetworkSpawn()
    {
        if (Instance != null && Instance != this) Debug.LogWarning("[MallEventManager] More than one; using the newest.", this);
        Instance = this;
        active.OnListChanged += OnActiveChanged;
        MarketModifiers.Invalidate();

        if (IsServer)
        {
            sessionStart = NetworkManager.ServerTime.Time;
            nextRoll = new double[events.Count];
            firedOnce = new bool[events.Count];
            nextRepeat = new double[events.Count];
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                if (e == null) continue;
                nextRoll[i] = sessionStart + Mathf.Max(1f, e.checkEvery);
                nextRepeat[i] = sessionStart + Mathf.Max(0f, e.startAt);
            }
        }
        Changed?.Invoke();
    }

    public override void OnNetworkDespawn()
    {
        active.OnListChanged -= OnActiveChanged;
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

    /// <summary>Host: end every event.</summary>
    public void ServerStopAll()
    {
        if (IsServer) active.Clear();
    }

    private void Update()
    {
        if (!IsSpawned) return;

        if (IsServer && devHotkeys && !UIState.IsMenuOpen)
        {
            if (Input.GetKeyDown(KeyCode.F7) && events.Count > 0)
            {
                var def = events[devNext++ % events.Count];
                if (def != null && ServerStart(def)) Debug.Log($"[MallEventManager] Dev: started '{def.displayName}'.", this);
            }
            if (Input.GetKeyDown(KeyCode.F8)) ServerStopAll();
        }

        if (!IsServer || Time.time < nextCheck) return;
        nextCheck = Time.time + 0.5f;
        double now = NetworkManager.ServerTime.Time;

        // End what's run its time.
        for (int i = active.Count - 1; i >= 0; i--)
            if (now >= active[i].endTime) active.RemoveAt(i);

        // Start what's due.
        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (e == null) continue;
            switch (e.trigger)
            {
                case MallEventDef.TriggerType.AtTime:
                    if (!firedOnce[i] && now - sessionStart >= e.startAt) { firedOnce[i] = true; ServerStart(e); }
                    break;
                case MallEventDef.TriggerType.Repeating:
                    if (now >= nextRepeat[i])
                    {
                        nextRepeat[i] = now + Mathf.Max(1f, e.interval);
                        ServerStart(e);
                    }
                    break;
                case MallEventDef.TriggerType.Random:
                    if (now >= nextRoll[i])
                    {
                        nextRoll[i] = now + Mathf.Max(1f, e.checkEvery);
                        if (!IsActive(e) && UnityEngine.Random.value < e.chance) ServerStart(e);
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
        var r = new Rect(10f, Screen.height - 30f - hudList.Count * 20f, 340f, 10f + hudList.Count * 20f);
        GUI.Box(r, GUIContent.none);
        for (int i = 0; i < hudList.Count; i++)
            GUI.Label(new Rect(r.x + 8, r.y + 4 + i * 20, 330, 20), $"{hudList[i].displayName}: {SecondsLeft(hudList[i]):0}s left");
    }
}
