using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// Breaks things in the mall now and then (lights, screens: see Breakable). A player who spots one looks at
/// it and presses F to report it: they get a help point (ReportedBroken) and maintenance fixes it after
/// Repair Seconds. Unreported, it stays broken.
///
/// Setup: one in the scene, with a NetworkObject. Breakables are numbered by where they are, so every
/// machine has to have the same ones (put them in the mall scene, not on things spawned at runtime).
/// </summary>
public class BreakManager : NetworkBehaviour
{
    public static BreakManager Instance { get; private set; }

    [Tooltip("About how many things break an hour, across the whole mall.")]
    [SerializeField, Min(0f)] private float breaksPerHour = 8f;
    [Tooltip("At most this many broken (or reported, waiting for a fix) at once.")]
    [SerializeField, Min(0)] private int maxBroken = 3;
    [Tooltip("Nothing breaks for this long after the game starts (seconds).")]
    [SerializeField, Min(0f)] private float firstBreakAfter = 120f;
    [Tooltip("How long after it's reported maintenance fixes it (seconds).")]
    [SerializeField, Min(0f)] private float repairSeconds = 30f;
    [Tooltip("How far away a broken thing can be reported from (meters).")]
    [SerializeField, Min(1f)] private float reportRange = 15f;
    [Tooltip("Broken this long with nobody reporting it, shoppers start complaining: every player is told " +
             "something's broken (not what or where). 0: never.")]
    [SerializeField, Min(0f)] private float complainAfterSeconds = 60f;
    [SerializeField] private bool debugLogs;

    public enum State : byte { Working, Broken, Reported }

    public struct Entry : INetworkSerializeByMemcpy, IEquatable<Entry>
    {
        public ushort index;
        public State state;
        public bool Equals(Entry o) => index == o.index && state == o.state;
        public override bool Equals(object obj) => obj is Entry o && Equals(o);
        public override int GetHashCode() => index * 4 + (int)state;
    }

    private NetworkList<Entry> broken; // only the ones not working
    private readonly List<Breakable> ordered = new();
    private readonly Dictionary<Breakable, ushort> indexOf = new();
    private readonly Dictionary<ushort, float> repairAt = new(); // host
    private readonly Dictionary<ushort, float> complainAt = new(); // host: broken, nobody's said yet
    private float startedAt, nextRoll;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[BreakManager] More than one in the scene; using the first.", this);
            return;
        }
        Instance = this;
        broken = new NetworkList<Entry>(null, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    }

    public override void OnNetworkSpawn()
    {
        Number();
        broken.OnListChanged += OnChanged;
        startedAt = Time.time;
        ShowAll();
    }

    public override void OnNetworkDespawn() => broken.OnListChanged -= OnChanged;

    public override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    // The same order on every machine: by where they are (to the centimeter), then name.
    private void Number()
    {
        ordered.Clear();
        indexOf.Clear();
        foreach (var b in Breakable.All) if (b != null) ordered.Add(b);
        ordered.Sort((a, b) =>
        {
            Vector3Int pa = Vector3Int.RoundToInt(a.transform.position * 100f), pb = Vector3Int.RoundToInt(b.transform.position * 100f);
            int c = pa.x.CompareTo(pb.x);
            if (c == 0) c = pa.y.CompareTo(pb.y);
            if (c == 0) c = pa.z.CompareTo(pb.z);
            return c != 0 ? c : string.CompareOrdinal(a.name, b.name);
        });
        for (int i = 0; i < ordered.Count && i < ushort.MaxValue; i++) indexOf[ordered[i]] = (ushort)i;
        if (debugLogs) Debug.Log($"[BreakManager] {ordered.Count} breakable things.", this);
    }

    private void OnChanged(NetworkListEvent<Entry> e) => ShowAll();

    private void ShowAll()
    {
        var off = new HashSet<ushort>();
        foreach (var e in broken.Each()) off.Add(e.index);
        for (int i = 0; i < ordered.Count; i++)
            if (ordered[i] != null) ordered[i].SetBroken(off.Contains((ushort)i));
    }

    private State StateAt(ushort index)
    {
        foreach (var e in broken.Each()) if (e.index == index) return e.state;
        return State.Working;
    }

    /// <summary>Every machine: how many are in this state (Broken: nobody's reported them yet).</summary>
    public int Count(State state)
    {
        int n = 0;
        if (broken != null) foreach (var e in broken.Each()) if (e.state == state) n++;
        return n;
    }

    /// <summary>Every machine: how it is.</summary>
    public State StateOf(Breakable b)
    {
        if (b == null || broken == null || !indexOf.TryGetValue(b, out ushort i)) return State.Working;
        foreach (var e in broken.Each()) if (e.index == i) return e.state;
        return State.Working;
    }

    /// <summary>
    /// Broken things within Range of a spot (each reported one counts Reported Share): for customers who
    /// see them. 0 when there are none.
    /// </summary>
    public float BrokenNear(Vector3 pos, float range, float reportedShare)
    {
        if (broken == null || range <= 0f) return 0f;
        float n = 0f, r2 = range * range;
        foreach (var e in broken.Each())
        {
            if (e.state == State.Working || e.index >= ordered.Count) continue;
            var b = ordered[e.index];
            if (b == null || (b.transform.position - pos).sqrMagnitude > r2) continue;
            n += e.state == State.Reported ? reportedShare : 1f;
        }
        return n;
    }

    /* ========================= HOST: BREAKING AND FIXING ========================= */

    private void Update()
    {
        if (!IsSpawned || !IsServer) return;
        float now = Time.time;

        // Fixed: reported, and maintenance has been.
        if (repairAt.Count > 0)
            foreach (var kv in new List<KeyValuePair<ushort, float>>(repairAt))
            {
                if (now < kv.Value) continue;
                repairAt.Remove(kv.Key);
                for (int i = 0; i < broken.Count; i++)
                    if (broken[i].index == kv.Key) { broken.RemoveAt(i); break; }
                if (debugLogs) Debug.Log($"[BreakManager] Fixed {Describe(kv.Key)}.", this);
            }

        // Broken a while and nobody's reported it: shoppers complain.
        if (complainAt.Count > 0)
            foreach (var kv in new List<KeyValuePair<ushort, float>>(complainAt))
            {
                if (now < kv.Value) continue;
                complainAt.Remove(kv.Key);
                if (StateAt(kv.Key) != State.Broken || NotificationManager.Instance == null) continue;
                string noun = kv.Key < ordered.Count && ordered[kv.Key] != null ? ordered[kv.Key].Noun : "thing";
                foreach (var client in NetworkManager.ConnectedClientsList)
                    NotificationManager.Instance.ServerNotifyClientText(client.ClientId, "Shoppers are complaining",
                        $"There's a broken {noun} somewhere in the mall, and it's keeping shoppers away. Find it and report it.");
            }

        // Once a second: maybe something breaks.
        if (now < nextRoll) return;
        nextRoll = now + 1f;
        if (now - startedAt < firstBreakAfter || broken.Count >= maxBroken || breaksPerHour <= 0f) return;
        if (Random.value >= breaksPerHour / 3600f) return;
        BreakOne();
    }

    private void BreakOne()
    {
        float total = 0f;
        for (int i = 0; i < ordered.Count; i++) if (CanBreak(i)) total += ordered[i].BreakWeight;
        if (total <= 0f) return;
        float pick = Random.value * total;
        for (int i = 0; i < ordered.Count; i++)
        {
            if (!CanBreak(i)) continue;
            pick -= ordered[i].BreakWeight;
            if (pick > 0f) continue;
            broken.Add(new Entry { index = (ushort)i, state = State.Broken });
            if (complainAfterSeconds > 0f) complainAt[(ushort)i] = Time.time + complainAfterSeconds;
            if (debugLogs) Debug.Log($"[BreakManager] Broke {Describe((ushort)i)}.", ordered[i]);
            return;
        }
    }

    private bool CanBreak(int i)
    {
        if (ordered[i] == null || ordered[i].BreakWeight <= 0f || !ordered[i].gameObject.activeInHierarchy) return false;
        foreach (var e in broken.Each()) if (e.index == i) return false;
        return true;
    }

    /// <summary>Host: break this one now (testing).</summary>
    public void ServerBreak(Breakable b)
    {
        if (!IsServer || b == null || !indexOf.TryGetValue(b, out ushort i) || StateOf(b) != State.Working) return;
        broken.Add(new Entry { index = i, state = State.Broken });
        if (complainAfterSeconds > 0f) complainAt[i] = Time.time + complainAfterSeconds;
    }

    private string Describe(ushort i) => i < ordered.Count && ordered[i] != null ? $"{ordered[i].Noun} '{ordered[i].name}'" : $"#{i}";

    /* ========================= REPORTING ========================= */

    /// <summary>Report a broken thing (the local player).</summary>
    public void Report(Breakable b)
    {
        if (b != null && indexOf.TryGetValue(b, out ushort i))
        {
            Sfx.Play(Cue.Reported, b.transform.position);
            ReportRpc(i);
        }
    }

    [Rpc(SendTo.Server)]
    private void ReportRpc(ushort index, RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        if (index >= ordered.Count || ordered[index] == null) return;
        int at = -1;
        for (int i = 0; i < broken.Count; i++) if (broken[i].index == index) { at = i; break; }
        if (at < 0 || broken[at].state != State.Broken) return; // working, or someone beat them to it
        var player = NetworkManager.ConnectedClients.TryGetValue(sender, out var client) ? client.PlayerObject : null;
        if (player == null) return;
        var bounds = ordered[index].LookBounds;
        float far = reportRange + 2f;
        if (bounds.SqrDistance(player.transform.position) > far * far) return;

        broken[at] = new Entry { index = index, state = State.Reported };
        repairAt[index] = Time.time + repairSeconds;
        complainAt.Remove(index);
        string noun = ordered[index].Noun;
        if (HelpPoints.Instance != null) HelpPoints.Instance.ServerAward(sender, HelpPoints.HelpAction.ReportedBroken);
        if (NotificationManager.Instance != null)
            NotificationManager.Instance.ServerNotifyClientText(sender, $"Reported a broken {noun}", "Maintenance is on the way to fix it.");
        if (debugLogs) Debug.Log($"[BreakManager] Player {sender} reported {Describe(index)}.", this);
    }

    /// <summary>
    /// Every machine: the broken (or reported) thing in front of a ray within Report Range, with nothing
    /// solid in the way; null if none.
    /// </summary>
    public Breakable FindLooked(Vector3 from, Vector3 direction, out State state)
    {
        state = State.Working;
        if (broken == null || broken.Count == 0) return null;
        var ray = new Ray(from, direction);
        Breakable best = null;
        float bestDistance = float.MaxValue;
        foreach (var e in broken.Each())
        {
            if (e.index >= ordered.Count) continue;
            var b = ordered[e.index];
            if (b == null || !b.LookBounds.IntersectRay(ray, out float d) || d > reportRange || d >= bestDistance) continue;
            // A wall in between? (Its own colliders don't count.)
            if (d > 0.1f && Physics.Raycast(from, direction, out var hit, d - 0.05f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) &&
                !hit.collider.transform.IsChildOf(b.transform)) continue;
            best = b;
            bestDistance = d;
            state = e.state;
        }
        return best;
    }
}
