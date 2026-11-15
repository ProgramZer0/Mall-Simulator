using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using Debug = UnityEngine.Debug;
using Random = UnityEngine.Random;

/// <summary>
/// The mall's customers. Only the host thinks: it spawns customers at the mall entrances
/// (CustomerSpawnPoint), walks them past stores, lets each store's appeal decide whether they go in,
/// has them browse what's for sale by their personality, take things off the shelves, queue and pay
/// at a checkout counter, then move on or leave.
///
/// Customers aren't NetworkObjects. Everyone gets one small message when a customer arrives or leaves,
/// and the positions of the customers that moved, packed together, a few times a second. Every machine
/// (the host too) draws them with a CustomerView.
///
/// Put this on the MallManager object (it has a NetworkObject), next to StoreAppealSettings (its
/// Personalities are who comes). Place CustomerSpawnPoints at the mall's entrances.
/// </summary>
public class CustomerManager : NetworkBehaviour
{
    public static CustomerManager Instance { get; private set; }

    [Header("Who comes")]
    [Tooltip("Most customers in the mall at once.")]
    [SerializeField] private int maxCustomers = 40;
    [Tooltip("New customers per minute while there's room (only while at least one store is open).")]
    [SerializeField] private float arrivalsPerMinute = 30f;
    [Tooltip("Most stores one customer goes into before leaving.")]
    [SerializeField] private int maxStoresPerVisit = 4;
    [Tooltip("Minutes a customer stays in the mall at most (then heads for the exit).")]
    [SerializeField] private float maxMinutesInMall = 8f;

    [Header("Walking")]
    [SerializeField] private Vector2 walkSpeed = new(1.1f, 1.5f);
    [Tooltip("A passer-by decides whether to go into a store when this close to its entrance (meters).")]
    [SerializeField] private float passRange = 6f;
    [Tooltip("Most customers in one store at once (Capacity perks add to it).")]
    [SerializeField] private int storeCapacity = 12;

    [Header("Shopping")]
    [Tooltip("How many things for sale a customer looks over when deciding what to go and see next.")]
    [SerializeField] private int browseSamples = 60;
    [Tooltip("Most shelves a customer goes to look at in one store.")]
    [SerializeField] private int maxLooksPerStore = 8;
    [Tooltip("Seconds spent looking at a shelf (random between).")]
    [SerializeField] private Vector2 lookSeconds = new(1.5f, 4f);
    [Tooltip("How readily they buy what they went to see: chance = worth × this (worth 1 = a good match at a fair price).")]
    [SerializeField] private float buyEagerness = 0.8f;

    [Header("Checkout")]
    [Tooltip("Meters between customers waiting in line.")]
    [SerializeField] private float queueSpacing = 0.8f;
    [Tooltip("Seconds a customer waits in line before giving up (Patience perks add to it).")]
    [SerializeField] private float patienceSeconds = 120f;

    [Header("Thinking (host)")]
    [Tooltip("Decisions per second per customer (walking is smooth regardless).")]
    [SerializeField] private float thinksPerSecond = 3f;
    [Tooltip("Most milliseconds per frame spent on customer decisions; the rest wait for the next frame.")]
    [SerializeField] private float thinkBudgetMs = 2f;

    [Header("Looks")]
    [Tooltip("Customer models (no NetworkObject; colliders are removed). Feet at the pivot, facing +Z. An " +
             "Animator float \"Speed\" gets their walking speed. Empty = plain capsules.")]
    [SerializeField] private List<GameObject> looks = new();

    [Header("Network")]
    [Tooltip("Position updates per second sent to the other players.")]
    [SerializeField] private float sendRate = 10f;

    [Header("Debug")]
    [SerializeField] private bool logDecisions;
    [SerializeField] private bool drawPaths = true;
    [Tooltip("Host: spawns a customer at the entrance nearest the player.")]
    [SerializeField] private KeyCode spawnKey = KeyCode.F3;

    /* ========================= STATE ========================= */

    private enum Mode : byte
    {
        Wander,      // walking toward Goal (a store's entrance, or the exit), deciding on stores passed
        Entering,    // walking into a store
        Browsing,    // inside: choosing what to look at next
        ToShelf,     // walking to it
        Looking,     // standing at it
        ToQueue,     // walking to their place in the checkout line
        Queued,      // in line (at the front: their things are on the belt)
        Leaving,     // walking out of the store
        Exiting,     // walking to the mall exit
    }

    private struct Taken
    {
        public StockContainer container; // off a shelf (already taken, so it can be put back)...
        public int zone;
        public Sellable loose;           // ...or a loose item (taken at the counter)
        public int itemId;
        public int priceCents;
        public int units;
    }

    private sealed class Customer
    {
        public ushort id;
        public CustomerPersonality personality;
        public byte look;
        public Vector3 pos;
        public float yaw;
        public float speed;
        public Mode mode;
        public float nextThink;
        public float until;        // Looking / patience timers
        public bool dirty = true;  // moved since the last send

        // Path
        public readonly List<Vector3> corners = new();
        public int corner;
        public bool arrived = true;
        public Vector3 destination;
        public int failures;

        // The visit
        public float leaveAt;
        public int storesVisited;
        public int budgetCents;
        public int wants;
        public readonly HashSet<StorePlot> passed = new();
        public StorePlot goalStore;    // the store they're walking toward (null: the exit)
        public StorePlot mustEnter;    // a test customer: goes into this store whatever its appeal
        public Vector3 cameFrom;       // where they came into the mall (they leave by another way if there is one)
        public Entrance goalEntrance;

        // In a store
        public StorePlot store;
        public bool counted;       // counted in the store's head count
        public Entrance entrance;
        public float satisfaction;
        public int shelvesSeen;
        public readonly HashSet<StoreListing.Key> seen = new();
        public StoreListing.Key target;
        public Vector3 targetPoint;
        public float targetWorth;
        public readonly List<Taken> basket = new();

        // Checkout
        public CheckoutCounter counter;
        public Vector3 queueSpot;  // the place in line they're walking to
        public int ticket;         // the counter's customer number once their things are on the belt
        public bool paid;
    }

    private struct Entrance
    {
        public Vector3 outside, inside;
    }

    private readonly List<Customer> customers = new();
    private readonly Dictionary<ushort, Customer> byId = new();
    private readonly Dictionary<CheckoutCounter, List<Customer>> lines = new();
    private readonly Dictionary<StorePlot, List<Entrance>> entrances = new();
    private readonly Dictionary<StorePlot, int> inside = new();
    private readonly List<(StoreListing.Source s, float w)> scratchPicks = new();
    private NavMeshPath path;
    private ushort nextId = 1;
    private float spawnCredit;
    private float nextSend, nextFullSend, nextEntrances;
    private int thinkCursor;
    private bool warnedNoSpawn, warnedNoPersonality;
    private readonly Stopwatch thinkTimer = new();

    // Every machine: the drawn customers.
    private readonly Dictionary<ushort, CustomerView> views = new();
    private Transform viewRoot;

    /// <summary>Customers in the mall (host: thinking ones; others: drawn ones).</summary>
    public int Count => IsServer ? customers.Count : views.Count;

    /* ========================= LIFECYCLE ========================= */

    private void Awake()
    {
        Instance = this;
        if (GetComponent<CustomerNavMesh>() == null) gameObject.AddComponent<CustomerNavMesh>();
    }

    public override void OnNetworkSpawn()
    {
        path = new NavMeshPath();
        if (IsServer)
        {
            NetworkManager.OnClientConnectedCallback += OnClientConnected;
            foreach (var c in CheckoutCounter.All) if (c != null) c.ServerCustomerPaid += OnPaid;
            CheckoutCounter.Spawned += OnCounterSpawned;
        }
    }

    public override void OnNetworkDespawn()
    {
        if (NetworkManager != null) NetworkManager.OnClientConnectedCallback -= OnClientConnected;
        CheckoutCounter.Spawned -= OnCounterSpawned;
        foreach (var c in CheckoutCounter.All) if (c != null) c.ServerCustomerPaid -= OnPaid;
        customers.Clear();
        byId.Clear();
        lines.Clear();
        inside.Clear();
        foreach (var v in views.Values) if (v != null) Destroy(v.gameObject);
        views.Clear();
    }

    public override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    private void OnCounterSpawned(CheckoutCounter c)
    {
        c.ServerCustomerPaid -= OnPaid;
        c.ServerCustomerPaid += OnPaid;
    }

    /// <summary>Host: is a customer within range of this point (for automatic doors)?</summary>
    public static bool AnyNear(Vector3 point, float range)
    {
        var m = Instance;
        if (m == null || !m.IsServer) return false;
        float r2 = range * range;
        foreach (var c in m.customers)
            if ((c.pos - point).sqrMagnitude <= r2) return true;
        return false;
    }

    private void Update()
    {
        bool spawnPressed = spawnKey != KeyCode.None && Input.GetKeyDown(spawnKey) && !UIState.IsMenuOpen;
        if (spawnPressed && !IsSpawned)
            Debug.LogWarning("[Customers] Not running: the CustomerManager's object isn't spawned on the network " +
                             "(it needs a NetworkObject, like the MallManager object).", this);
        if (!IsSpawned || !IsServer) return;
        var nav = CustomerNavMesh.Instance;
        if (nav == null || !nav.Ready)
        {
            if (spawnPressed) Debug.LogWarning($"[Customers] Can't spawn yet: {(nav == null ? "no CustomerNavMesh" : nav.Describe())}.", this);
            return;
        }

        if (Time.time >= nextEntrances) RefreshEntrances();
        if (spawnPressed) SpawnNearLocalPlayer();
        UpdateArrivals();

        float dt = Time.deltaTime;
        foreach (var c in customers) Walk(c, dt);

        // Decisions, a few customers a frame, within the budget.
        thinkTimer.Restart();
        int n = customers.Count;
        for (int k = 0; k < n && thinkTimer.Elapsed.TotalMilliseconds < thinkBudgetMs; k++)
        {
            if (customers.Count == 0) break;
            if (thinkCursor >= customers.Count) thinkCursor = 0;
            var c = customers[thinkCursor];
            if (Time.time >= c.nextThink)
            {
                c.nextThink = Time.time + 1f / Mathf.Max(0.1f, thinksPerSecond) * Random.Range(0.8f, 1.2f);
                Think(c);
            }
            if (thinkCursor < customers.Count && customers[thinkCursor] == c) thinkCursor++;
        }

        // Host draws its own straight from the simulation.
        foreach (var c in customers)
            if (views.TryGetValue(c.id, out var v) && v != null) v.SetTarget(c.pos, c.yaw);

        SendMoves();
    }

    /* ========================= ARRIVING AND LEAVING ========================= */

    private void UpdateArrivals()
    {
        if (customers.Count >= maxCustomers || !AnyStoreOpen()) { spawnCredit = 0f; return; }
        spawnCredit += arrivalsPerMinute / 60f * Time.deltaTime;
        while (spawnCredit >= 1f && customers.Count < maxCustomers)
        {
            spawnCredit -= 1f;
            var sp = PickSpawnPoint(null);
            if (sp != null) Spawn(sp.transform.position, out _);
            else if (!warnedNoSpawn)
            {
                warnedNoSpawn = true;
                Debug.LogWarning("[Customers] No CustomerSpawnPoint in the scene: put some at the mall's entrances.", this);
            }
        }
    }

    private static bool AnyStoreOpen()
    {
        if (MallManager.Instance == null) return false;
        foreach (var p in MallManager.Instance.Plots) if (p != null && p.IsOpen) return true;
        return false;
    }

    private static CustomerSpawnPoint PickSpawnPoint(Func<CustomerSpawnPoint, bool> filter)
    {
        float total = 0f;
        foreach (var sp in CustomerSpawnPoint.All)
            if (sp != null && (filter == null || filter(sp))) total += sp.weight;
        if (total <= 0f) return null;
        float roll = Random.value * total;
        foreach (var sp in CustomerSpawnPoint.All)
        {
            if (sp == null || (filter != null && !filter(sp))) continue;
            roll -= sp.weight;
            if (roll <= 0f) return sp;
        }
        return null;
    }

    // Test (host key): a customer comes in at the entrance nearest the player. Standing in an open
    // store, they head straight for it and always go in. Says what happened either way.
    private void SpawnNearLocalPlayer()
    {
        var player = NetworkManager.LocalClient?.PlayerObject;
        Vector3 at = player != null ? player.transform.position : transform.position;
        CustomerSpawnPoint best = null;
        float bestD = float.MaxValue;
        foreach (var sp in CustomerSpawnPoint.All)
        {
            if (sp == null) continue;
            float d = (sp.transform.position - at).sqrMagnitude;
            if (d < bestD) { bestD = d; best = sp; }
        }
        if (best == null)
        {
            Debug.LogWarning("[Customers] No CustomerSpawnPoint in the scene: put some at the mall's entrances.", this);
            return;
        }

        var store = MallManager.Instance != null ? MallManager.Instance.GetPlotAt(at) : null;
        var c = Spawn(best.transform.position, out string why, store);
        if (c == null)
        {
            Debug.LogWarning($"[Customers] Test customer not spawned at '{best.name}': {why}", best);
            return;
        }
        string where = c.mode == Mode.Exiting ? "the exit (no open store with a door it can reach)"
                     : c.goalStore != null ? $"'{c.goalStore.DisplayName}'" : "nowhere";
        string note = store == null ? ""
                    : !store.IsOpen ? $" (you're in '{store.DisplayName}', but it isn't open)"
                    : !entrances.ContainsKey(store) ? $" (you're in '{store.DisplayName}', but no way in was found: " +
                                                      "its doors need walkable floor just inside and outside)"
                    : "";
        Debug.Log($"[Customers] Test customer #{c.id} ({c.personality.displayName}) came in at '{best.name}', " +
                  $"heading for {where}{note}.", this);
    }

    // A new customer near a point (null and why if not). Visit: a store they head straight for and go into.
    private Customer Spawn(Vector3 near, out string why, StorePlot visit = null)
    {
        why = null;
        var settings = StoreAppealSettings.Instance;
        var personality = settings != null ? settings.PickRandom() : null;
        if (personality == null)
        {
            why = "no customer personalities (StoreAppealSettings → Personalities)";
            if (!warnedNoPersonality) Debug.LogWarning($"[Customers] Not spawning: {why}.", this);
            warnedNoPersonality = true;
            return null;
        }
        if (!NavMesh.SamplePosition(near, out var hit, 5f, NavMesh.AllAreas))
        {
            why = "no walkable floor within 5 m of it (is it inside CustomerNavMesh's area, on a floor with a collider?)";
            return null;
        }

        while (byId.ContainsKey(nextId) || nextId == 0) nextId++;
        var c = new Customer
        {
            id = nextId++,
            personality = personality,
            look = (byte)(looks.Count > 0 ? Random.Range(0, looks.Count) : 0),
            pos = hit.position,
            yaw = Random.Range(0f, 360f),
            speed = Random.Range(walkSpeed.x, walkSpeed.y),
            leaveAt = Time.time + maxMinutesInMall * 60f,
            budgetCents = Mathf.RoundToInt(Random.Range(personality.budgetDollars.x, personality.budgetDollars.y) * 100f),
            wants = Random.Range(personality.wantItems.x, personality.wantItems.y + 1),
            nextThink = Time.time,
            cameFrom = hit.position,
        };
        customers.Add(c);
        byId[c.id] = c;
        MakeView(c.id, c.look, c.pos, c.yaw);
        ArriveRpc(new[] { c.id }, new[] { c.look }, new[] { c.pos }, new[] { PackYaw(c.yaw) });
        if (visit != null && visit.IsOpen && entrances.TryGetValue(visit, out var ways) && ways.Count > 0)
        {
            c.mustEnter = visit;
            c.goalStore = visit;
            c.goalEntrance = ways[0];
            c.mode = Mode.Wander;
            if (!PathTo(c, ways[0].outside)) PickGoal(c);
        }
        else PickGoal(c);
        Log(c, $"arrived ({personality.displayName}, ${c.budgetCents / 100} to spend, wants {c.wants})");
        return byId.ContainsKey(c.id) ? c : null;
    }

    private void Despawn(Customer c)
    {
        AbandonStore(c);
        customers.Remove(c);
        byId.Remove(c.id);
        RemoveView(c.id);
        LeaveRpc(new[] { c.id });
    }

    /* ========================= WALKING ========================= */

    private bool PathTo(Customer c, Vector3 to)
    {
        c.corners.Clear();
        c.corner = 0;
        if (!NavMesh.SamplePosition(to, out var hit, 2f, NavMesh.AllAreas) ||
            !NavMesh.CalculatePath(c.pos, hit.position, NavMesh.AllAreas, path) ||
            path.status == NavMeshPathStatus.PathInvalid || path.corners.Length == 0)
        {
            c.failures++;
            c.arrived = true;
            return false;
        }
        c.corners.AddRange(path.corners);
        c.destination = hit.position;
        c.arrived = false;
        c.failures = 0;
        return true;
    }

    private void Walk(Customer c, float dt)
    {
        if (c.arrived) return;
        float step = c.speed * dt;
        while (step > 0f && c.corner < c.corners.Count)
        {
            Vector3 to = c.corners[c.corner];
            Vector3 d = to - c.pos;
            float len = d.magnitude;
            if (len <= step)
            {
                c.pos = to;
                step -= len;
                c.corner++;
                continue;
            }
            c.pos += d / len * step;
            Vector3 flat = new(d.x, 0f, d.z);
            if (flat.sqrMagnitude > 1e-4f) c.yaw = Quaternion.LookRotation(flat).eulerAngles.y;
            step = 0f;
        }
        c.dirty = true;
        if (c.corner >= c.corners.Count)
        {
            c.arrived = true;
            c.nextThink = Time.time; // decide what's next straight away
        }
    }

    private void Face(Customer c, Vector3 point)
    {
        Vector3 flat = point - c.pos;
        flat.y = 0f;
        if (flat.sqrMagnitude > 1e-4f) c.yaw = Quaternion.LookRotation(flat).eulerAngles.y;
        c.dirty = true;
    }

    /* ========================= DECISIONS ========================= */

    private void Think(Customer c)
    {
        if (c.failures > 4) { Log(c, "stuck: gone"); Despawn(c); return; }

        // The store shut (closed, being renovated, sold) while they were in it: out they go.
        if (c.store != null && c.mode != Mode.Leaving && !c.store.IsOpen)
        {
            Log(c, $"'{c.store.DisplayName}' closed: leaving");
            LeaveStore(c, -0.2f);
            return;
        }

        switch (c.mode)
        {
            case Mode.Wander: ThinkWander(c); break;
            case Mode.Entering:
                if (c.arrived) { c.mode = Mode.Browsing; ThinkBrowse(c); }
                break;
            case Mode.Browsing: ThinkBrowse(c); break;
            case Mode.ToShelf:
                if (c.arrived)
                {
                    c.mode = Mode.Looking;
                    c.until = Time.time + Random.Range(lookSeconds.x, lookSeconds.y);
                    Face(c, c.targetPoint);
                }
                break;
            case Mode.Looking:
                if (Time.time >= c.until) { Decide(c); c.mode = Mode.Browsing; }
                break;
            case Mode.ToQueue:
            case Mode.Queued: ThinkQueue(c); break;
            case Mode.Leaving:
                if (c.arrived) FinishStore(c);
                break;
            case Mode.Exiting:
                if (c.arrived) { Log(c, "left the mall"); Despawn(c); }
                break;
        }
    }

    // Choose where to walk next: an open store's entrance (more likely the more its window draws this
    // kind of customer), or the exit when they're done.
    private void PickGoal(Customer c)
    {
        bool done = Time.time >= c.leaveAt || c.storesVisited >= maxStoresPerVisit || c.budgetCents <= 0;
        if (!done && MallManager.Instance != null)
        {
            float total = 0f;
            StorePlot pick = null;
            foreach (var plot in MallManager.Instance.Plots)
            {
                if (plot == null || !plot.IsOpen || plot.Appeal == null || c.passed.Contains(plot) ||
                    !entrances.TryGetValue(plot, out var list) || list.Count == 0)
                    continue;
                float w = 0.1f + plot.Appeal.For(c.personality).outsideDraw;
                total += w;
                if (Random.value * total < w) pick = plot; // one pass, weighted
            }
            if (pick != null)
            {
                var e = entrances[pick][Random.Range(0, entrances[pick].Count)];
                c.mode = Mode.Wander;
                c.goalStore = pick;
                c.goalEntrance = e;
                if (PathTo(c, e.outside)) return;
                c.passed.Add(pick); // can't get there
            }
        }
        c.goalStore = null;

        // Done (or nowhere to go): the exit.
        var exit = PickSpawnPoint(sp => sp.isExit && (sp.transform.position - c.cameFrom).sqrMagnitude > 25f)
                   ?? PickSpawnPoint(sp => sp.isExit);
        c.mode = Mode.Exiting;
        if (exit == null || !PathTo(c, exit.transform.position)) { Despawn(c); return; }
        Log(c, "heading for the exit");
    }

    private void ThinkWander(Customer c)
    {
        // Passing an open store: does its storefront pull them in?
        if (MallManager.Instance != null)
        {
            float r2 = passRange * passRange;
            foreach (var plot in MallManager.Instance.Plots)
            {
                if (plot == null || !plot.IsOpen || plot.Appeal == null || c.passed.Contains(plot) ||
                    !entrances.TryGetValue(plot, out var list)) continue;
                foreach (var e in list)
                {
                    if ((e.outside - c.pos).sqrMagnitude > r2) continue;
                    if (Consider(c, plot, e)) return;
                    break;
                }
            }
        }
        if (!c.arrived) return;
        // At the store they were heading for (if passing it didn't already decide).
        if (c.goalStore != null && !c.passed.Contains(c.goalStore) && c.goalStore.IsOpen && c.goalStore.Appeal != null &&
            Consider(c, c.goalStore, c.goalEntrance)) return;
        PickGoal(c);
    }

    // A store's entrance, right there: roll its walk-in chance for this customer (once per store).
    private bool Consider(Customer c, StorePlot plot, Entrance e)
    {
        c.passed.Add(plot);
        float chance = plot.Appeal.For(c.personality).enterChance;
        bool full = InsideCount(plot) >= Capacity(plot);
        if (plot == c.mustEnter || (!full && Random.value < chance))
        {
            Enter(c, plot, e);
            return true;
        }
        Log(c, $"walked past '{plot.DisplayName}' (chance {chance:0.00}{(full ? ", full" : "")})");
        return false;
    }

    private int Capacity(StorePlot plot) =>
        Mathf.Max(1, Mathf.RoundToInt(storeCapacity * (1f + ProgressionManager.StoreStat(plot, PerkStat.Capacity))));

    private int InsideCount(StorePlot plot) => inside.TryGetValue(plot, out int n) ? n : 0;

    private void Enter(Customer c, StorePlot plot, Entrance e)
    {
        c.store = plot;
        c.entrance = e;
        c.satisfaction = 0f;
        c.shelvesSeen = 0;
        c.seen.Clear();
        c.basket.Clear();
        c.counter = null;
        c.ticket = 0;
        c.paid = false;
        inside[plot] = InsideCount(plot) + 1;
        c.counted = true;
        c.mode = Mode.Entering;
        if (!PathTo(c, e.inside)) { LeaveStore(c, 0f); return; }
        Log(c, $"going into '{plot.DisplayName}'");
    }

    // Inside: pick something to go and look at, or head to the checkout (or out) when done.
    private void ThinkBrowse(Customer c)
    {
        var plot = c.store;
        bool done = c.basket.Count >= c.wants || c.budgetCents <= 0 || c.shelvesSeen >= maxLooksPerStore;
        if (!done && TryPickShelf(c, plot, out var source, out float worth))
        {
            c.shelvesSeen++;
            c.target = source.Key;
            c.targetPoint = source.Position;
            c.targetWorth = worth;
            c.seen.Add(source.Key);
            c.mode = Mode.ToShelf;
            if (!PathTo(c, source.Position)) c.mode = Mode.Browsing; // can't reach it; pick another next time
            return;
        }

        if (c.basket.Count > 0) GoToCheckout(c);
        else
        {
            Log(c, $"found nothing to buy in '{plot.DisplayName}'");
            LeaveStore(c, c.shelvesSeen == 0 ? -0.3f : -0.1f);
        }
    }

    // Look over a sample of what's for sale and choose one of the best for this customer.
    private bool TryPickShelf(Customer c, StorePlot plot, out StoreListing.Source pick, out float worth)
    {
        pick = default;
        worth = 0f;
        var all = plot.Listing.Sources;
        int count = all.Count;
        if (count == 0) return false;

        float tolerance = 1f + ProgressionManager.StoreStat(plot, PerkStat.PriceTolerance);
        scratchPicks.Clear();
        int samples = Mathf.Min(browseSamples, count);
        int start = Random.Range(0, count);
        int stride = count <= samples ? 1 : Mathf.Max(1, count / samples);
        for (int k = 0; k < samples; k++)
        {
            var s = all[(start + k * stride) % count];
            if (s.quantity <= 0 || c.seen.Contains(s.Key) || s.priceCents > c.budgetCents) continue;
            float w = plot.Appeal.WorthTo(c.personality, s) * tolerance;
            if (w <= 0.01f) continue;
            scratchPicks.Add((s, w));
        }
        if (scratchPicks.Count == 0) return false;

        // The best few, then a weighted pick among them (so they don't all crowd one shelf).
        scratchPicks.Sort((a, b) => b.w.CompareTo(a.w));
        int top = Mathf.Min(3, scratchPicks.Count);
        float total = 0f;
        for (int i = 0; i < top; i++) total += scratchPicks[i].w;
        float roll = Random.value * total;
        for (int i = 0; i < top; i++)
        {
            roll -= scratchPicks[i].w;
            if (roll > 0f && i < top - 1) continue;
            pick = scratchPicks[i].s;
            worth = scratchPicks[i].w;
            return true;
        }
        return false;
    }

    // At the shelf: still there, still worth it at this price? Then take one.
    private void Decide(Customer c)
    {
        var plot = c.store;
        if (!plot.Listing.TryGetSource(c.target, out var s) || s.quantity <= 0)
        {
            c.satisfaction -= 0.15f;
            Log(c, "what they came for was gone");
            return;
        }
        if (s.priceCents > c.budgetCents)
        {
            c.satisfaction -= 0.05f;
            return;
        }
        float tolerance = 1f + ProgressionManager.StoreStat(plot, PerkStat.PriceTolerance);
        float worth = plot.Appeal.WorthTo(c.personality, s) * tolerance;
        if (Random.value >= Mathf.Clamp01(worth * buyEagerness))
        {
            Log(c, $"looked at {CheckoutCounter.NameOf(s.itemId)} for {CheckoutCounter.FormatAmount(s.priceCents)}, didn't take it");
            return;
        }

        if (s.container != null)
        {
            int itemId = s.container.ServerRemoveOne(s.zone); // the host settles who gets the last one
            if (itemId == 0) { c.satisfaction -= 0.15f; return; }
            c.basket.Add(new Taken { container = s.container, zone = s.zone, itemId = itemId, priceCents = s.priceCents, units = 1 });
        }
        else if (s.item != null)
        {
            // A loose item stays where it is until they pay for it at the counter.
            int units = s.item.TryGetComponent<ItemPickupNet>(out var pickup) && pickup.IsStockCrate ? pickup.Units : 1;
            c.basket.Add(new Taken { loose = s.item, itemId = s.itemId, priceCents = s.priceCents, units = units });
        }
        else return;

        c.budgetCents -= s.priceCents;
        c.satisfaction += 0.25f;
        Log(c, $"took {CheckoutCounter.NameOf(s.itemId)} ({CheckoutCounter.FormatAmount(s.priceCents)})");
    }

    /* ========================= CHECKOUT ========================= */

    private void GoToCheckout(Customer c)
    {
        CheckoutCounter best = null;
        int bestLine = int.MaxValue;
        foreach (var counter in CheckoutCounter.All)
        {
            if (counter == null || !counter.IsSpawned || counter.IsForSale || counter.Plot != c.store) continue;
            int n = lines.TryGetValue(counter, out var l) ? l.Count : 0;
            if (n < bestLine) { bestLine = n; best = counter; }
        }
        if (best == null)
        {
            Log(c, $"no checkout in '{c.store.DisplayName}': put things back and left");
            LeaveStore(c, -0.6f);
            return;
        }
        if (!lines.TryGetValue(best, out var line)) lines[best] = line = new List<Customer>();
        line.Add(c);
        c.counter = best;
        c.queueSpot = Vector3.positiveInfinity;
        c.until = Time.time + patienceSeconds * (1f + ProgressionManager.StoreStat(c.store, PerkStat.Patience));
        c.mode = Mode.ToQueue;
        WalkToPlaceInLine(c);
    }

    private void WalkToPlaceInLine(Customer c)
    {
        int place = lines.TryGetValue(c.counter, out var line) ? line.IndexOf(c) : 0;
        c.counter.GetQueueSpot(Mathf.Max(0, place), queueSpacing, out Vector3 spot, out Vector3 facing);
        if ((c.queueSpot - spot).sqrMagnitude < 0.09f) // same place as before: on the way, or there
        {
            if (c.arrived) Face(c, c.pos + facing);
            return;
        }
        c.queueSpot = spot; // moved up (or just joined)
        PathTo(c, spot);
    }

    private void ThinkQueue(Customer c)
    {
        var counter = c.counter;
        if (counter == null || !counter.IsSpawned || counter.IsForSale)
        {
            Log(c, "the checkout went away");
            LeaveStore(c, -0.4f);
            return;
        }
        if (c.paid) { LeaveStore(c, c.satisfaction + 0.2f); return; }

        int place = lines.TryGetValue(counter, out var line) ? line.IndexOf(c) : 0;
        WalkToPlaceInLine(c);

        // At the front: their things go on the belt.
        if (place == 0 && c.arrived && c.ticket == 0)
        {
            c.mode = Mode.Queued;
            var purchases = new List<CheckoutCounter.Purchase>();
            for (int i = c.basket.Count - 1; i >= 0; i--)
            {
                var t = c.basket[i];
                if (t.loose != null)
                {
                    if (!t.loose.IsSpawned) { c.basket.RemoveAt(i); c.satisfaction -= 0.15f; continue; } // someone else got it
                    t.loose.NetworkObject.Despawn(true);
                    purchases.Add(new CheckoutCounter.Purchase { itemId = t.itemId, priceCents = t.priceCents, units = t.units, whole = true });
                }
                else purchases.Add(new CheckoutCounter.Purchase { itemId = t.itemId, priceCents = t.priceCents, units = 1 });
            }
            if (purchases.Count == 0) { LeaveStore(c, c.satisfaction); return; }
            c.ticket = counter.ServerAddCustomer(purchases);
            if (c.ticket == 0) { LeaveStore(c, -0.4f); return; }
            c.basket.Clear(); // on the belt now: theirs to pay for
            Log(c, $"put {purchases.Count} thing(s) on the belt");
            return;
        }

        // Waited too long (and not being served yet): give up.
        if (Time.time >= c.until && (c.ticket == 0 || counter.ServerCancelCustomer(c.ticket)))
        {
            Log(c, "waited too long in line: left");
            c.ticket = 0;
            LeaveStore(c, -0.6f);
        }
    }

    private void OnPaid(CheckoutCounter counter, int ticket)
    {
        if (!lines.TryGetValue(counter, out var line)) return;
        foreach (var c in line)
        {
            if (c.ticket != ticket) continue;
            c.paid = true;
            c.nextThink = Time.time;
            Log(c, "paid");
            return;
        }
    }

    /* ========================= LEAVING A STORE ========================= */

    // Out of the line, things not paid for back on their shelves.
    private void AbandonStore(Customer c)
    {
        if (c.counter != null && lines.TryGetValue(c.counter, out var line))
        {
            line.Remove(c);
            foreach (var other in line) if (other.mode == Mode.Queued || other.mode == Mode.ToQueue) other.nextThink = Time.time;
        }
        if (c.ticket != 0 && c.counter != null && c.counter.IsSpawned) c.counter.ServerCancelCustomer(c.ticket); // not paid: off the belt
        c.ticket = 0;
        c.counter = null;
        foreach (var t in c.basket)
            if (t.container != null && t.container.IsSpawned)
                t.container.ServerAddOne(t.zone, t.itemId, t.container.GetZone(t.zone).orient);
        c.basket.Clear();
        if (c.counted && c.store != null && inside.TryGetValue(c.store, out int n)) inside[c.store] = Mathf.Max(0, n - 1);
        c.counted = false;
    }

    private void LeaveStore(Customer c, float satisfaction)
    {
        var plot = c.store;
        AbandonStore(c);
        c.satisfaction = satisfaction;
        c.mode = Mode.Leaving;
        if (plot == null || !PathTo(c, c.entrance.outside)) FinishStore(c);
    }

    private void FinishStore(Customer c)
    {
        var plot = c.store;
        if (plot != null)
        {
            c.storesVisited++;
            if (plot.HasOwner && ProgressionManager.Instance != null && Mathf.Abs(c.satisfaction) > 0.01f)
                ProgressionManager.Instance.ServerCustomerLeft(plot.OwnerTeamId, Mathf.Clamp(c.satisfaction, -1f, 1f));
            Log(c, $"left '{plot.DisplayName}' ({(c.satisfaction >= 0f ? "happy" : "unhappy")} {c.satisfaction:0.00})");
        }
        c.store = null;
        PickGoal(c);
    }

    /* ========================= STORE ENTRANCES ========================= */

    private readonly List<Bounds> scratchDoors = new();

    // Each open store's ways in: a spot just outside and just inside each of its doors.
    private void RefreshEntrances()
    {
        nextEntrances = Time.time + 3f;
        entrances.Clear();
        if (MallManager.Instance == null) return;
        foreach (var plot in MallManager.Instance.Plots)
        {
            if (plot == null || !plot.IsOpen) continue;
            plot.GetDoorBounds(scratchDoors);
            if (scratchDoors.Count == 0) continue;
            var list = new List<Entrance>();
            Vector3 middle = plot.GetWorldBounds().center;
            foreach (var d in scratchDoors)
            {
                Vector3 inward = middle - d.center;
                inward.y = 0f;
                inward = inward.sqrMagnitude > 1e-4f ? inward.normalized : Vector3.forward;
                Vector3 foot = new(d.center.x, d.min.y, d.center.z);
                if (!NavMesh.SamplePosition(foot - inward * 1.2f, out var outHit, 1.5f, NavMesh.AllAreas) ||
                    !NavMesh.SamplePosition(foot + inward * 1.2f, out var inHit, 1.5f, NavMesh.AllAreas)) continue;
                if (!plot.Contains(inHit.position + Vector3.up * 0.1f) || plot.Contains(outHit.position + Vector3.up * 0.1f)) continue;
                list.Add(new Entrance { outside = outHit.position, inside = inHit.position });
            }
            if (list.Count > 0) entrances[plot] = list;
        }
    }

    /* ========================= NETWORK ========================= */

    // Packed position: id (2), x and z at 2 cm (2 + 2), height at 1 cm (2), facing (1) = 9 bytes.
    private const int Packed = 9;
    private const int PerMessage = 120; // stays under the size of one unreliable packet

    private static byte PackYaw(float yaw) => (byte)Mathf.RoundToInt(Mathf.Repeat(yaw, 360f) / 360f * 256f);
    private static float UnpackYaw(byte b) => b * (360f / 256f);
    private static short Q(float v, float step) => (short)Mathf.Clamp(Mathf.RoundToInt(v / step), short.MinValue, short.MaxValue);

    private readonly List<Customer> toSend = new();

    private void SendMoves()
    {
        if (Time.time < nextSend) return;
        nextSend = Time.time + 1f / Mathf.Max(1f, sendRate);
        bool all = Time.time >= nextFullSend; // now and then everyone, in case an update was lost
        if (all) nextFullSend = Time.time + 2f;

        toSend.Clear();
        foreach (var c in customers)
            if (c.dirty || all) { toSend.Add(c); c.dirty = false; }
        if (toSend.Count == 0 || NetworkManager.ConnectedClientsIds.Count <= 1) return;

        for (int start = 0; start < toSend.Count; start += PerMessage)
        {
            int n = Mathf.Min(PerMessage, toSend.Count - start);
            var bytes = new byte[n * Packed];
            for (int k = 0; k < n; k++)
            {
                var c = toSend[start + k];
                int o = k * Packed;
                Put(bytes, o, (short)c.id);
                Put(bytes, o + 2, Q(c.pos.x, 0.02f));
                Put(bytes, o + 4, Q(c.pos.z, 0.02f));
                Put(bytes, o + 6, Q(c.pos.y, 0.01f));
                bytes[o + 8] = PackYaw(c.yaw);
            }
            MoveRpc(bytes);
        }
    }

    private static void Put(byte[] b, int o, short v)
    {
        b[o] = (byte)v;
        b[o + 1] = (byte)(v >> 8);
    }

    private static short Get(byte[] b, int o) => (short)(b[o] | (b[o + 1] << 8));

    [Rpc(SendTo.NotServer, Delivery = RpcDelivery.Unreliable)]
    private void MoveRpc(byte[] bytes)
    {
        if (bytes == null) return;
        for (int o = 0; o + Packed <= bytes.Length; o += Packed)
        {
            ushort id = (ushort)Get(bytes, o);
            if (!views.TryGetValue(id, out var v) || v == null) continue;
            var pos = new Vector3(Get(bytes, o + 2) * 0.02f, Get(bytes, o + 6) * 0.01f, Get(bytes, o + 4) * 0.02f);
            v.SetTarget(pos, UnpackYaw(bytes[o + 8]));
        }
    }

    [Rpc(SendTo.NotServer)]
    private void ArriveRpc(ushort[] ids, byte[] lookIds, Vector3[] positions, byte[] yaws) =>
        ReceiveArrivals(ids, lookIds, positions, yaws);

    [Rpc(SendTo.SpecifiedInParams)]
    private void EveryoneRpc(ushort[] ids, byte[] lookIds, Vector3[] positions, byte[] yaws, RpcParams rpcParams) =>
        ReceiveArrivals(ids, lookIds, positions, yaws);

    private void ReceiveArrivals(ushort[] ids, byte[] lookIds, Vector3[] positions, byte[] yaws)
    {
        for (int i = 0; i < ids.Length; i++)
            MakeView(ids[i], i < lookIds.Length ? lookIds[i] : (byte)0, positions[i], i < yaws.Length ? UnpackYaw(yaws[i]) : 0f);
    }

    [Rpc(SendTo.NotServer)]
    private void LeaveRpc(ushort[] ids)
    {
        foreach (var id in ids) RemoveView(id);
    }

    // A player joined: tell them about everyone already here.
    private void OnClientConnected(ulong clientId)
    {
        if (clientId == NetworkManager.ServerClientId || customers.Count == 0) return;
        for (int start = 0; start < customers.Count; start += 200)
        {
            int n = Mathf.Min(200, customers.Count - start);
            var ids = new ushort[n];
            var lookIds = new byte[n];
            var positions = new Vector3[n];
            var yaws = new byte[n];
            for (int k = 0; k < n; k++)
            {
                var c = customers[start + k];
                ids[k] = c.id;
                lookIds[k] = c.look;
                positions[k] = c.pos;
                yaws[k] = PackYaw(c.yaw);
            }
            EveryoneRpc(ids, lookIds, positions, yaws, RpcTarget.Single(clientId, RpcTargetUse.Temp));
        }
    }

    private void MakeView(ushort id, byte look, Vector3 pos, float yaw)
    {
        if (views.TryGetValue(id, out var old) && old != null) { old.SetTarget(pos, yaw, snap: true); return; }
        if (viewRoot == null)
        {
            viewRoot = new GameObject("[Customers]").transform;
            DontDestroyOnLoad(viewRoot.gameObject);
        }
        var prefab = look < looks.Count ? looks[look] : null;
        views[id] = CustomerView.Create(id, prefab, pos, yaw, viewRoot);
    }

    private void RemoveView(ushort id)
    {
        if (views.TryGetValue(id, out var v) && v != null) Destroy(v.gameObject);
        views.Remove(id);
    }

    /* ========================= DEBUG ========================= */

    private void Log(Customer c, string what)
    {
        if (logDecisions) Debug.Log($"[Customers] #{c.id} {what}", this);
    }

    private void OnDrawGizmos()
    {
        if (!drawPaths || !Application.isPlaying) return;
        foreach (var c in customers)
        {
            Gizmos.color = c.store != null ? Color.green : Color.cyan;
            Vector3 from = c.pos + Vector3.up * 0.1f;
            for (int i = c.corner; i < c.corners.Count; i++)
            {
                Vector3 to = c.corners[i] + Vector3.up * 0.1f;
                Gizmos.DrawLine(from, to);
                from = to;
            }
        }
    }
}
