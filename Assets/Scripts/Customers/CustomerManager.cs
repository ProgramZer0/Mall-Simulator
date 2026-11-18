using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Netcode;
using Unity.Profiling;
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
public partial class CustomerManager : NetworkBehaviour
{
    public static CustomerManager Instance { get; private set; }

    [Header("Who comes")]
    [Tooltip("Most customers in the mall at once.")]
    [SerializeField] private int maxCustomers = 40;
    [Tooltip("New customers per minute while there's room (only while at least one store is open).")]
    [SerializeField] private float arrivalsPerMinute = 30f;
    [Tooltip("Bystanders (Bystander personalities, and groups led by one) come on top of Max Customers, up to " +
             "this many at once, to fill the halls. They never shop, so they cost less than shoppers.")]
    [SerializeField] private int maxBystanders = 60;
    [Tooltip("New bystanders per minute while there's room (whether or not any store is open).")]
    [SerializeField] private float bystanderArrivalsPerMinute = 20f;
    [Tooltip("Most stores one customer goes into before leaving.")]
    [SerializeField] private int maxStoresPerVisit = 4;
    [Tooltip("Minutes a customer stays in the mall at most (then heads for the exit).")]
    [SerializeField] private float maxMinutesInMall = 8f;
    [Tooltip("Minutes a customer stays at least: with no store to go to before then, they stroll the hallways.")]
    [SerializeField] private float minMinutesInMall = 1.5f;

    [Header("Groups")]
    [Tooltip("Customer groups that come (couples, friends...). Empty: everything in Resources/Customer Groups.")]
    [SerializeField] private List<CustomerGroupDef> groups = new();
    [Tooltip("Chance an arrival is a group rather than someone on their own.")]
    [Range(0f, 1f)] [SerializeField] private float groupChance = 0.35f;
    [Tooltip("How far apart a group walks side by side (meters).")]
    [SerializeField] private float groupSpacing = 0.7f;
    [Tooltip("Longest a group member waits outside a store for the others (seconds) before they move on.")]
    [SerializeField] private float groupWaitSeconds = 90f;
    [Tooltip("How far out from a store's door a group member waits for the others (meters), so they don't crowd it.")]
    [SerializeField] private float groupWaitDistance = 2.5f;

    [Header("Walking")]
    [SerializeField] private Vector2 walkSpeed = new(1.1f, 1.5f);
    [Tooltip("A passer-by decides whether to go into a store when this close to its entrance (meters).")]
    [SerializeField] private float passRange = 6f;
    [Tooltip("Most customers in one store at once (Capacity perks add to it). Customers on their way to it and " +
             "waiting at its door count too: a store that's full (or about to be) isn't picked.")]
    [SerializeField] private int storeCapacity = 12;
    [Tooltip("How far a wandering customer notices a storefront (meters, needs a clear view of its entrance).")]
    [SerializeField] private float sightRange = 20f;
    [Tooltip("How readily a storefront they notice pulls them over: chance = what its window shows them × this.")]
    [SerializeField] private float sightPull = 1.2f;
    [Tooltip("Customers standing still (in line, at a shelf) keep at least this far apart (meters).")]
    [SerializeField] private float personalSpace = 0.55f;
    [Tooltip("Walking customers steer around other customers and players within this distance (meters), " +
             "keeping to the right to pass.")]
    [SerializeField] private float avoidRange = 1.1f;
    [Tooltip("Someone leaving is out of the mall once this close to the exit (meters), crowd or not.")]
    [SerializeField] private float exitReach = 1.5f;
    [Tooltip("Customers keep to their own lane, up to this far either side of the shortest path (meters), " +
             "so a hallway fills out instead of everyone walking one line.")]
    [SerializeField] private float laneSpread = 0.7f;
    [Tooltip("How much customers keep to the right of the path (0 = either side, 1 = always right), so people " +
             "going opposite ways down a hallway or round a corner use different sides instead of meeting head on.")]
    [Range(0f, 1f)] [SerializeField] private float keepRight = 0.6f;
    [Tooltip("At a corner of a wall, the path swings this far wide of it (meters) instead of hugging the corner, " +
             "and nobody cuts a corner unless they'd clear it by this much.")]
    [SerializeField] private float cornerClearance = 0.5f;
    [Tooltip("Chance a wandering customer's next stop is just a spot in the hallways rather than a store.")]
    [Range(0f, 1f)] [SerializeField] private float roamChance = 0.35f;
    [Tooltip("Bystanders (personalities set to Bystander) stand around this long at each spot they stroll to " +
             "(seconds, random between) before walking on.")]
    [SerializeField] private Vector2 lingerSeconds = new(5f, 25f);

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
    [Tooltip("How far off their place in line each customer stands (meters, fixed per customer so it doesn't " +
             "jitter). 0 = a perfectly neat line.")]
    [SerializeField] private float lineLooseness = 0.12f;
    [Tooltip("Customers waiting in line hold their place: nobody nudges them, and if they're bumped off it they " +
             "step back at this speed (meters a second).")]
    [SerializeField] private float lineSettleSpeed = 0.8f;
    [Tooltip("At the front of the line but kept just short of their spot (someone in the way): this close (meters)...")]
    [SerializeField] private float frontReach = 1.2f;
    [Tooltip("...for this long (seconds), and they count as there and put their things on the belt.")]
    [SerializeField] private float frontSettleSeconds = 3f;
    [Tooltip("Seconds a customer waits in line before giving up (Patience perks add to it).")]
    [SerializeField] private float patienceSeconds = 120f;
    [Tooltip("First guess at seconds per item to get through a checkout (scanning plus paying, spread over the " +
             "items). Each counter learns its real pace from then on.")]
    [SerializeField] private float secondsPerItemGuess = 6f;

    [Header("Theft")]
    [Tooltip("Tempted by a high price: an honest customer looking at something priced above its market price " +
             "may steal it instead, with their personality's Shoplift Chance times (1 + this × how far over the " +
             "market price it is). E.g. 3: at double the market price, 4× as likely. A personality with Shoplift " +
             "Chance 0 never steals.")]
    [Min(0f)] [SerializeField] private float overpriceTemptation = 3f;
    [Tooltip("The most a high price can multiply someone's Shoplift Chance by.")]
    [Min(1f)] [SerializeField] private float maxTemptation = 10f;
    [Tooltip("Chance a shoplifter's theft sets off the alarm as they leave the store (Theft Resistance perks add to it). " +
             "The store's team is told straight away and can still catch them before they leave the mall.")]
    [Range(0f, 1f)] [SerializeField] private float alarmChance = 0.15f;
    [Tooltip("A shoplifter won't try it with a player this close who can see them (meters); they pay instead.")]
    [SerializeField] private float watchedRange = 6f;
    [Tooltip("How much faster a shoplifter walks once they've taken off with something.")]
    [SerializeField] private float fleeSpeed = 1.6f;
    [Tooltip("How long a caught shoplifter stands still (after dropping what they took) before running for the exit.")]
    [SerializeField] private float caughtStopSeconds = 1.2f;
    [Tooltip("A customer who can't find a way out of a store (something's in the way) says so, gets less happy " +
             "and the store's team is told. If they're still stuck after this long (seconds), they're put outside " +
             "the door and leave very unhappy. (Each store keeps one doorway clear: MallManager > Doorways.)")]
    [SerializeField, Min(1f)] private float trappedLetOutSeconds = 180f;
    [Tooltip("XP the store's team loses when a trapped customer has to be put outside (on top of a very unhappy visit).")]
    [SerializeField, Min(0f)] private float trappedXpLoss = 100f;
    [Tooltip("How close a player must be to stop a customer (meters).")]
    [SerializeField] private float stopRange = 3f;

    [Header("Comparing prices")]
    [Tooltip("How likely a price-conscious customer is to know another open store sells the same thing for less " +
             "(times their personality's Price Sensitivity, so 0 sensitivity never compares). They put it back " +
             "and head there next.")]
    [Range(0f, 1f)] [SerializeField] private float comparePrices = 0.8f;
    [Tooltip("Only cheaper by at least this much counts (0.05 = 5%).")]
    [Range(0f, 1f)] [SerializeField] private float cheaperBy = 0.05f;

    [Header("Thinking (host)")]
    [Tooltip("Decisions per second per customer (walking is smooth regardless).")]
    [SerializeField] private float thinksPerSecond = 3f;
    [Tooltip("Most milliseconds per frame spent on customer decisions; the rest wait for the next frame.")]
    [SerializeField] private float thinkBudgetMs = 2f;

    [Header("Looks")]
    [Tooltip("Customer models (no NetworkObject; colliders are removed). Feet at the pivot, facing +Z. An " +
             "Animator float \"Speed\" gets their walking speed. Empty = plain capsules.")]
    [SerializeField] private List<GameObject> looks = new();
    [Tooltip("Child models (as Looks). Empty: children are an adult look drawn smaller (Child Scale). Up to 64.")]
    [SerializeField] private List<GameObject> childLooks = new();
    [Tooltip("How big children are drawn, compared with adults, when they use an adult look (no Child Looks).")]
    [Range(0.3f, 1f)] [SerializeField] private float childScale = 0.65f;
    private const byte ChildLook = 0x80; // the look byte's top bit: a child (sent with it, so every machine knows)
    private const byte ChildModel = 0x40; // ...and this one: the rest is a Child Looks number (not a Looks one)
    [Tooltip("A shopping bag a customer carries after buying in a store (one more per store, up to Max Bags). " +
             "Placed at a child of the look called \"BagAnchor\" if it has one (its pivot is where the hand holds it), " +
             "otherwise at their side. No colliders needed (they're removed).")]
    [SerializeField] private GameObject bagPrefab;
    [Tooltip("Each bag gets a random color from the Paint Palette (the same on every machine). Off: the bag's own colors.")]
    [SerializeField] private bool randomBagColors = true;
    [SerializeField, Min(0)] private int maxBags = 3;

    [Header("Reactions")]
    [Tooltip("Show reactions over customers' heads (for tuning). Off: players have to work out from sales why " +
             "something sells; reactions are still counted per store (StoreReport, benchmark overlay).")]
    [SerializeField] private bool showReactions = true;
    [Tooltip("What shows over a customer's head when something happens (text, colour, optional icon). " +
             "Kinds left out use their defaults.")]
    [SerializeField] private List<ReactionLook> reactionLooks = new();
    [Tooltip("Seconds a reaction stays up.")]
    [SerializeField] private float reactionSeconds = 2.5f;
    [Tooltip("Reactions further than this from your camera aren't shown (meters).")]
    [SerializeField] private float reactionRange = 30f;

    [Header("Network")]
    [Tooltip("Position updates per second sent to the other players.")]
    [SerializeField] private float sendRate = 10f;

    [Header("Debug")]
    [SerializeField] private bool logDecisions;
    [SerializeField] private bool drawPaths = true;
    [Tooltip("Scene view: a label over each customer (number, what they're doing, where they're going, path trouble).")]
    [SerializeField] private bool drawLabels = true;
    [Tooltip("Host: spawns a customer at the entrance nearest the player.")]
    [SerializeField] private KeyCode spawnKey = KeyCode.F3;

    /* ========================= REACTIONS ========================= */

    /// <summary>Things that happen to a customer that players can see over their head.</summary>
    public enum Reaction : byte
    {
        Interested,     // walking into a store
        Bought,         // took something
        TooPricey,      // put it back: dearer than it's worth to them
        SoldOut,        // what they came for was gone
        NothingForMe,   // left a store without finding anything
        LineTooLong,    // put everything back rather than queue
        GaveUpWaiting,  // waited too long in line
        Paid,           // paid at the checkout
        StoreClosed,    // the store shut while they were in it
        NoCheckout,     // nowhere to pay
        CheaperElsewhere, // put it back: another store sells it for less (and they'll go there)
        Caught,         // a shoplifter, stopped by a player
        Trapped,        // can't get out of a store: something's in the way
        LostChild,      // a parent: their child's gone
        LostMyFamily,   // the child: can't see their family
        Reunited,       // the family's together again
    }

    [Serializable]
    public class ReactionLook
    {
        public Reaction kind;
        public string text;
        public Color color = Color.white;
        [Tooltip("Optional: shown instead of the text.")]
        public Sprite icon;
    }

    private static readonly Dictionary<Reaction, (string text, Color color)> DefaultLooks = new()
    {
        { Reaction.Interested, ("Ooh!", new Color(0.55f, 0.85f, 1f)) },
        { Reaction.Bought, ("Got it!", new Color(0.45f, 0.95f, 0.45f)) },
        { Reaction.TooPricey, ("Too pricey", new Color(1f, 0.7f, 0.25f)) },
        { Reaction.SoldOut, ("Sold out?!", new Color(1f, 0.6f, 0.2f)) },
        { Reaction.NothingForMe, ("Nothing for me", new Color(0.8f, 0.8f, 0.8f)) },
        { Reaction.LineTooLong, ("Line's too long", new Color(1f, 0.4f, 0.35f)) },
        { Reaction.GaveUpWaiting, ("Forget it!", new Color(1f, 0.3f, 0.3f)) },
        { Reaction.Paid, ("Thanks!", new Color(0.45f, 0.95f, 0.45f)) },
        { Reaction.StoreClosed, ("Closed?", new Color(0.8f, 0.8f, 0.8f)) },
        { Reaction.NoCheckout, ("Where do I pay?", new Color(1f, 0.4f, 0.35f)) },
        { Reaction.CheaperElsewhere, ("Cheaper elsewhere", new Color(1f, 0.85f, 0.3f)) },
        { Reaction.Caught, ("Busted!", new Color(1f, 0.25f, 0.25f)) },
        { Reaction.Trapped, ("Let me out!", new Color(1f, 0.35f, 0.3f)) },
        { Reaction.LostChild, ("Where's my kid?!", new Color(1f, 0.55f, 0.2f)) },
        { Reaction.LostMyFamily, ("Mom? Dad?", new Color(0.55f, 0.8f, 1f)) },
        { Reaction.Reunited, ("Thank you!", new Color(0.45f, 0.95f, 0.45f)) },
    };

    // Host: how often each reaction happened in each store (for tuning; see StoreReport).
    private readonly Dictionary<StorePlot, int[]> storeTally = new();

    // Host: a customer reacts; everyone sees it over their head, and it's counted for their store.
    private void React(Customer c, Reaction r)
    {
        if (c.group != null) GroupReact(c, r);
        if (c.store != null)
        {
            if (!storeTally.TryGetValue(c.store, out var t)) storeTally[c.store] = t = new int[Enum.GetValues(typeof(Reaction)).Length];
            t[(int)r]++;
        }
        if (!showReactions) return; // still counted above, just not shown (nothing sent)
        try { ShowReaction(c.id, r); } catch (Exception e) { Debug.LogException(e); } // a visual must never break their thinking
        ReactRpc(c.id, r);
    }

    [Rpc(SendTo.NotServer, Delivery = RpcDelivery.Unreliable)]
    private void ReactRpc(ushort id, Reaction r) => ShowReaction(id, r);

    private void ShowReaction(ushort id, Reaction r)
    {
        if (!views.TryGetValue(id, out var v) || v == null) return;
        var cam = Camera.main;
        if (cam != null && (cam.transform.position - v.transform.position).sqrMagnitude > reactionRange * reactionRange) return;
        string text = null;
        Color color = Color.white;
        Sprite icon = null;
        foreach (var look in reactionLooks)
            if (look != null && look.kind == r) { text = look.text; color = look.color; icon = look.icon; break; }
        if (text == null && icon == null && DefaultLooks.TryGetValue(r, out var d)) { text = d.text; color = d.color; }
        v.ShowReaction(text, color, icon, reactionSeconds);
    }

    /// <summary>
    /// Host: what customers made of a store since play started (for tuning prices and stock), e.g.
    /// "Store02: 40 came in, 25 bought, 9 too pricey, 3 sold out, 6 nothing for them, 2 line too long".
    /// </summary>
    public string StoreReport(StorePlot plot)
    {
        if (plot == null || !storeTally.TryGetValue(plot, out var t)) return null;
        return $"{plot.DisplayName}: {t[(int)Reaction.Interested]} came in, {t[(int)Reaction.Bought]} things taken, " +
               $"{t[(int)Reaction.Paid]} paid, {t[(int)Reaction.TooPricey]} too pricey, {t[(int)Reaction.SoldOut]} sold out, " +
               $"{t[(int)Reaction.NothingForMe]} nothing for them, {t[(int)Reaction.LineTooLong]} line too long, " +
               $"{t[(int)Reaction.GaveUpWaiting]} gave up waiting, {t[(int)Reaction.CheaperElsewhere]} cheaper elsewhere";
    }

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
        Follow,      // in a group, walking with the leader
        Wait,        // in a group, waiting outside a store for the others
        Caught,      // a shoplifter stopped by a player: stands there a moment, then runs for the exit
        Lost,        // a child who wandered off from their family (see CustomerLostChild.cs)
        WithHelper,  // a lost child following the player who found them
        Searching,   // a family looking for their lost child
    }

    private struct Taken
    {
        public StockContainer container; // off a shelf (already taken, so it can be put back)...
        public int zone;
        public Sellable loose;           // ...or a loose item (taken at the counter)
        public int itemId;
        public int priceCents;
        public int units;
        public uint paint;               // the products' colors (off a shelf)
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
        public float bestLeft;     // least path left so far (for noticing they're stuck)
        public float stuckSince;
        public float trappedSince = -1f; // when they found they couldn't get out of a store (-1: not trapped)
        public int stuckRepaths;   // new paths in a row without getting anywhere
        public string pathProblem; // the last reason a path failed (debug)
        public float lane;         // their side of the path (meters, + = right)
        public int laneCorner = -1;
        public float nextSeeCheck;      // when to look past the next corner again (see Walk)
        public int remainingCorner = -1; // RemainingAfter worked out for this corner...
        public float remaining;          // ...as this
        public Vector3 laneTarget; // the current corner, moved over to their lane

        // The visit
        public float leaveAt;
        public float stayUntil;    // they don't leave before this unless done shopping
        public int storesVisited;
        public int budgetCents;
        public int wants;
        public readonly HashSet<StorePlot> passed = new();
        public StorePlot goalStore;    // the store they're walking toward (null: the exit)
        public readonly HashSet<StorePlot> noticed = new(); // storefronts they've seen from a distance
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

        // Theft
        public bool thief;          // a shoplifter (decided when they arrive)
        public bool fleeing;        // walking off with things they haven't paid for
        public bool alarmed;        // set off the alarm (the store's team knows)
        public StorePlot robbed;    // the store they stole from
        public readonly List<Taken> stolen = new();
        public float stoppedAt = -99f;
        public byte bags;           // shopping bags they carry (one per store they bought in)
        public StorePlot baggedIn;  // the store their last bag came from
        public StorePlot bargainStore; // somewhere they know sells something for less: going there next
        public int bargainItem;        // ...this

        // Groups (see CustomerGroups.cs)
        public Group group;
        public Customer follow;        // who they walk with (null: they choose where to go)
        public bool child;             // a child: always with a parent, never shops themselves
        public int handed;             // things in their basket that others in the group handed them to pay for
        public StorePlot waitFor;      // the store they're waiting outside of
        public bool meeting;           // a family that split: waiting at the meeting spot
        public StorePlot partyStore;   // (leading a party) the store they're in, and how it's going for them
        public float partyMood;
        public bool lingering;         // a bystander standing around at a spot they strolled to
        public bool bystanderSlot;     // counts toward Max Bystanders (not Max Customers)

        // Lost children (see CustomerLostChild.cs)
        public Customer lostFrom;      // (a lost child) the parent they wandered off from
        public float lostSince;
        public Vector3 lostAt;         // where they wandered off (their family searches around there)
        public bool lostLetDown;       // (a lost child) never found by a player: the mall's reputation took the hit
        public ulong helper = ulong.MaxValue; // (a lost child) the player they're following
        public float helperFarSince = -1f;
        public Customer searchingFor;  // (a family's party leader) the child they're looking for
        public byte mark;              // lost, following a player, searching (LostMark): sent to everyone

        // Checkout
        public CheckoutCounter counter;
        public int itemsAtCounter;  // how many things they put on the belt
        public float frontSince;    // when they reached the front (for the counter's pace)
        public float nearFrontSince = -1f; // when they got close to the front spot without quite reaching it
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
            MallEventManager.Changed += OnEventsChanged;
            eventsDirty = true;
            foreach (var c in CheckoutCounter.All) if (c != null) c.ServerCustomerPaid += OnPaid;
            CheckoutCounter.Spawned += OnCounterSpawned;
        }
    }

    public override void OnNetworkDespawn()
    {
        if (NetworkManager != null) NetworkManager.OnClientConnectedCallback -= OnClientConnected;
        CheckoutCounter.Spawned -= OnCounterSpawned;
        MallEventManager.Changed -= OnEventsChanged;
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

    // Profiler markers (show up under CustomerManager.Update without Deep Profile).
    private static readonly ProfilerMarker WalkMarker = new("Customers.Walk");
    private static readonly ProfilerMarker ThinkMarker = new("Customers.Think");
    private static readonly ProfilerMarker ApartMarker = new("Customers.KeepApart");
    private static readonly ProfilerMarker GridMarker = new("Customers.Grid");
    private static readonly ProfilerMarker ViewsMarker = new("Customers.Views");
    private static readonly ProfilerMarker SendMarker = new("Customers.Send");
    private static readonly ProfilerMarker ArrivalsMarker = new("Customers.Arrivals");

    // Every machine: ease each customer's view toward where they are, all in one loop.
    private void TickViews()
    {
        using var _ = ViewsMarker.Auto();
        float dt = Time.deltaTime;
        float ease = 1f - Mathf.Exp(-dt * 10f), settle = 1f - Mathf.Exp(-dt * 8f);
        foreach (var v in views.Values) if (v != null) v.Tick(dt, ease, settle);
    }

    private void Update()
    {
        TickViews();
        bool spawnPressed = (spawnKey != KeyCode.None) && Input.GetKeyDown(spawnKey);
        if (spawnPressed && UIState.IsMenuOpen)
        {
            Debug.Log($"[Customers] {spawnKey} ignored: a menu counts as open ({UIState.Describe()}).", this);
            spawnPressed = false;
        }
        if (spawnPressed && !IsSpawned)
            Debug.LogWarning("[Customers] Not running: the CustomerManager's object isn't spawned on the network " +
                             "(it needs a NetworkObject, like the MallManager object).", this);
        // Any player can call a test customer: a client asks the host.
        if (spawnPressed && IsSpawned && !IsServer)
        {
            var me = NetworkManager.LocalClient?.PlayerObject;
            TestCustomerRpc(me != null ? me.transform.position : transform.position);
        }
        if (!IsSpawned || !IsServer) return;
        var nav = CustomerNavMesh.Instance;
        if (nav == null || !nav.Ready)
        {
            if (spawnPressed) Debug.LogWarning($"[Customers] Can't spawn yet: {(nav == null ? "no CustomerNavMesh" : nav.Describe())}.", this);
            return;
        }

        if (Time.time >= nextEntrances) RefreshEntrances();
        if (spawnPressed || testRequested)
        {
            testRequested = false;
            SpawnTestCustomer(testAt ?? LocalPlayerPosition());
            testAt = null;
        }
        using (ArrivalsMarker.Auto()) UpdateArrivals();

        float dt = Time.deltaTime;
        using (GridMarker.Auto())
        {
            FillGrid();
            GatherPlayers();
        }
        using (WalkMarker.Auto())
            foreach (var c in customers) Walk(c, dt);

        // Decisions, a few customers a frame, within the budget.
        var thinking = ThinkMarker.Auto();
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

        thinking.Dispose();

        using (ApartMarker.Auto()) KeepApart(dt);

        // Host draws its own straight from the simulation.
        foreach (var c in customers)
            if (views.TryGetValue(c.id, out var v) && v != null) v.SetTarget(c.pos, c.yaw);

        using (SendMarker.Auto()) SendMoves();
    }

    /* ========================= ARRIVING AND LEAVING ========================= */

    /* ========================= EVENTS AND SEASONS ========================= */

    // What the running events (seasons included) do to customers, worked out when events change.
    private float eventArrivals = 1f, eventMax = 1f, eventBudget = 1f;
    private readonly Dictionary<CustomerPersonality, float> eventPersonality = new();
    private readonly List<MallEventDef> activeEvents = new();
    private bool eventsDirty = true;

    private void OnEventsChanged() => eventsDirty = true;

    private void RefreshEventEffects()
    {
        eventsDirty = false;
        eventArrivals = eventMax = eventBudget = eventShoplifting = 1f;
        eventPersonality.Clear();
        if (MallEventManager.Instance == null) return;
        MallEventManager.Instance.GetActive(activeEvents);
        foreach (var e in activeEvents)
        {
            eventArrivals *= Factor(e.customerArrivalsPercent);
            eventMax *= Factor(e.maxCustomersPercent);
            eventBudget *= Factor(e.customerBudgetPercent);
            eventShoplifting *= Factor(e.shopliftingPercent);
            if (e.personalityShifts == null) continue;
            foreach (var shift in e.personalityShifts)
            {
                if (shift == null || shift.personality == null) continue;
                eventPersonality[shift.personality] =
                    (eventPersonality.TryGetValue(shift.personality, out float f) ? f : 1f) * Factor(shift.percent);
            }
        }
    }

    private static float Factor(float percent) => Mathf.Max(0f, 1f + percent / 100f);

    /// <summary>Most customers at once right now (events, seasons and the mall's reputation included).</summary>
    public int MaxCustomersNow => Mathf.Max(0, Mathf.RoundToInt(maxCustomers * eventMax * Reputation));

    /// <summary>New customers per minute right now (events, seasons and the mall's reputation included).</summary>
    public float ArrivalsPerMinuteNow => arrivalsPerMinute * eventArrivals * Reputation;

    // Who comes: each personality by its Spawn Weight, times what the running events do to it.
    // A shopper's personality (bystanders false), or a bystander's (true), by Spawn Weight.
    private CustomerPersonality PickPersonality(bool bystanders = false)
    {
        var list = bystanders ? BystanderPersonalities() : StoreAppealSettings.Instance?.personalities;
        if (list == null) return null;
        float total = 0f;
        foreach (var p in list) if (p != null && p.bystander == bystanders) total += WeightOf(p);
        if (total <= 0f) return null;
        float roll = Random.value * total;
        foreach (var p in list)
        {
            if (p == null || p.bystander != bystanders) continue;
            roll -= WeightOf(p);
            if (roll <= 0f) return p;
        }
        return null;
    }

    // Bystander personalities: those in StoreAppealSettings, else any in Resources/Customer Personalities.
    private List<CustomerPersonality> bystanderPersonalities;

    private List<CustomerPersonality> BystanderPersonalities()
    {
        if (bystanderPersonalities != null) return bystanderPersonalities;
        bystanderPersonalities = new List<CustomerPersonality>();
        var settings = StoreAppealSettings.Instance;
        if (settings != null)
            foreach (var p in settings.personalities) if (p != null && p.bystander) bystanderPersonalities.Add(p);
        if (bystanderPersonalities.Count == 0)
            foreach (var p in Resources.LoadAll<CustomerPersonality>("Customer Personalities"))
                if (p != null && p.bystander) bystanderPersonalities.Add(p);
        return bystanderPersonalities;
    }

    private float WeightOf(CustomerPersonality p) =>
        p.spawnWeight * (eventPersonality.TryGetValue(p, out float f) ? f : 1f);

    private float bystanderCredit;

    private void UpdateArrivals()
    {
        if (eventsDirty) RefreshEventEffects();
        int shoppers = 0, bystanders = 0;
        foreach (var c in customers) { if (c.bystanderSlot) bystanders++; else shoppers++; }
        UpdateBystanderArrivals(bystanders);

        int max = MaxCustomersNow;
        if (shoppers >= max || !AnyStoreOpen()) { spawnCredit = 0f; return; }
        spawnCredit += ArrivalsPerMinuteNow / 60f * Time.deltaTime;
        while (spawnCredit >= 1f && shoppers < max)
        {
            spawnCredit -= 1f;
            var sp = PickSpawnPoint(null);
            CustomerGroupDef groupDef = Random.value < groupChance ? PickGroupDef(false) : null;
            int came = 1;
            if (sp != null && groupDef != null) spawnCredit -= Mathf.Max(0, (came = SpawnGroup(groupDef, sp.transform.position)) - 1);
            else if (sp != null) Spawn(sp.transform.position, out _);
            shoppers += came;
            if (sp == null && !warnedNoSpawn)
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

    private bool testRequested;
    private Vector3? testAt;

    private Vector3 LocalPlayerPosition()
    {
        var player = NetworkManager != null ? NetworkManager.LocalClient?.PlayerObject : null;
        return player != null ? player.transform.position : transform.position;
    }

    /// <summary>Test from the Inspector (right-click the component): a customer at the host player's nearest entrance.</summary>
    [ContextMenu("Test: Spawn A Customer")]
    private void SpawnFromMenu()
    {
        testRequested = true;
        if (!IsServer) Debug.LogWarning("[Customers] Only the host runs customers: use this on the host.", this);
    }

    [Rpc(SendTo.Server)]
    private void TestCustomerRpc(Vector3 playerPosition)
    {
        testRequested = true;
        testAt = playerPosition;
    }

    // Test: a customer comes in at the entrance nearest a player. Standing in an open store, they head
    // straight for it and always go in. Says what happened either way.
    private void SpawnTestCustomer(Vector3 at)
    {
        RefreshEntrances(); // up to date for the message
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
        string where = c.mode == Mode.Exiting ? $"the exit, no store to go to ({WhyNoStores()})"
                     : c.goalStore != null ? $"'{c.goalStore.DisplayName}'" : "nowhere";
        string note = store == null || c.mode == Mode.Exiting ? ""
                    : !store.IsOpen ? $" (you're in '{store.DisplayName}', but it isn't open)"
                    : !entrances.ContainsKey(store) ? $" (you're in '{store.DisplayName}', but no way in: " +
                                                      $"{(entranceProblems.TryGetValue(store, out var why2) ? why2 : "unknown")})"
                    : "";
        Debug.Log($"[Customers] Test customer #{c.id} ({c.personality.displayName}) came in at '{best.name}', " +
                  $"heading for {where}{note}.", this);
    }

    // A new customer near a point (null and why if not). Visit: a store they head straight for and go into.
    private Customer Spawn(Vector3 near, out string why, StorePlot visit = null,
                           CustomerPersonality personality = null, bool pickGoal = true, bool child = false)
    {
        why = null;
        var settings = StoreAppealSettings.Instance;
        if (eventsDirty) RefreshEventEffects();
        if (personality == null) personality = settings != null ? PickPersonality() : null;
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
            stayUntil = Time.time + minMinutesInMall * 60f,
            budgetCents = Mathf.RoundToInt(Random.Range(personality.budgetDollars.x, personality.budgetDollars.y) * 100f * eventBudget),
            wants = Random.Range(personality.wantItems.x, personality.wantItems.y + 1),
            nextThink = Time.time,
            cameFrom = hit.position,
            lane = Random.Range(Mathf.Lerp(-laneSpread, 0f, keepRight), laneSpread),
        };
        c.thief = Random.value < personality.shopliftChance * eventShoplifting;
        if (personality.bystander) // here for a walk: stays a while, whatever their money
        {
            c.leaveAt = Time.time + Random.Range(minMinutesInMall, maxMinutesInMall) * 60f;
            c.bystanderSlot = true;
        }
        if (child)
        {
            c.child = true;
            // Their own child models when there are some (drawn as they are), else an adult look made smaller.
            if (childLooks.Count > 0) c.look = (byte)(Random.Range(0, Mathf.Min(childLooks.Count, 64)) | ChildModel);
            c.look |= ChildLook; // a child, on every machine
            c.thief = false;
            c.budgetCents = 0; // their parent buys for them
            c.wants = 0;
        }
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
        else if (pickGoal) PickGoal(c);
        Log(c, $"arrived ({personality.displayName}, ${c.budgetCents / 100} to spend, wants {c.wants})");
        return byId.ContainsKey(c.id) ? c : null;
    }

    private void Despawn(Customer c)
    {
        if (c.group != null)
        {
            if (c.group.Leader == c && c.mode == Mode.Exiting) GroupLeftMall(c);
            else DropFromGroup(c);
        }
        // Children with them go too (one left far behind stays, for now on their own). A child of theirs
        // who's lost goes home with them (wherever they are).
        foreach (var kid in customers.ToArray())
        {
            if (kid.lostFrom == c && byId.ContainsKey(kid.id)) { kid.lostFrom = null; Despawn(kid); continue; }
            if (kid.follow != c) continue;
            kid.follow = null;
            if (kid.child && FlatDistance(kid.pos, c.pos) < 6f) Despawn(kid);
        }
        if (c.stolen.Count > 0) GotAway(c);
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
        string problem = null;
        if (!NavMesh.SamplePosition(to, out var hit, 2f, NavMesh.AllAreas))
            problem = $"no walkable floor within 2 m of {to:F1}";
        else if (!NavMesh.CalculatePath(c.pos, hit.position, NavMesh.AllAreas, path) ||
                 path.status == NavMeshPathStatus.PathInvalid || path.corners.Length == 0)
            problem = $"no path from {c.pos:F1} to {hit.position:F1}";
        // Partial: the place can't be reached (behind a wall or a shut door); the path only gets as near
        // as it can, which is how customers ended up piled against walls.
        else if (path.status == NavMeshPathStatus.PathPartial &&
                 FlatDistance(path.corners[path.corners.Length - 1], hit.position) > 1f)
            problem = $"{hit.position:F1} can't be reached from {c.pos:F1} (gets within " +
                      $"{FlatDistance(path.corners[path.corners.Length - 1], hit.position):0.0} m)";

        if (problem != null)
        {
            c.pathProblem = problem;
            c.failures++;
            c.arrived = true;
            pathFailures++;
            Log(c, $"path failed: {problem}");
            return false;
        }
        c.corners.AddRange(path.corners);
        c.destination = hit.position;
        c.arrived = false;
        c.failures = 0;
        c.bestLeft = float.MaxValue;
        c.stuckSince = Time.time;
        c.laneCorner = -1;
        c.remainingCorner = -1;
        c.stuckRepaths = 0;
        paths++;
        return true;
    }

    // Along the path, in their own lane, steering around people on the way. Each step stays on the
    // walkable area and slides along edges instead of stopping at them; a corner they can already see
    // past is skipped. Someone who stops making progress finds a new path.
    private void Walk(Customer c, float dt)
    {
        if (c.arrived) return;
        if (c.corner >= c.corners.Count) { Arrive(c); return; }

        Vector3 to = LaneTarget(c);
        float left = FlatDistance(c.pos, to);
        // Looking past the next corner costs a few raycasts: a few times a second is plenty.
        bool seePast = false;
        if (left < 2f && c.corner < c.corners.Count - 1 && Time.time >= c.nextSeeCheck)
        {
            c.nextSeeCheck = Time.time + 0.2f;
            seePast = CanSeeNext(c);
        }
        if (left < 0.35f || seePast)
        {
            c.corner++;
            if (c.corner >= c.corners.Count) { Arrive(c); return; }
            to = LaneTarget(c);
            left = FlatDistance(c.pos, to);
        }
        Vector3 dir = to - c.pos;
        dir.y = 0f;
        dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.forward;

        // Close to the end, people in the way matter less (someone may be standing near their spot).
        float toEnd = FlatDistance(c.pos, c.destination);
        float avoidWeight = Mathf.Clamp01(toEnd - 0.3f);
        if (c.mode == Mode.Exiting && toEnd < exitReach) { Arrive(c); return; } // out the door
        if (InDoorway(c.pos)) avoidWeight = 0f; // coming and going: straight through
        float speed = GroupSpeed(c);
        Vector3 vel = dir * speed + Avoidance(c, dir) * (speed * avoidWeight);
        vel = Vector3.ClampMagnitude(vel, speed * 1.15f);
        if (vel.magnitude * dt > left && Vector3.Dot(vel, dir) > 0.5f * vel.magnitude)
            vel = vel.normalized * (left / Mathf.Max(dt, 1e-4f)); // don't overshoot

        Vector3 step = vel * dt;
        Vector3 next = c.pos + step;
        float along = left > 1e-4f ? Mathf.Clamp01(Vector3.Dot(step, dir) / left) : 1f;
        next.y = Mathf.Lerp(c.pos.y, to.y, along); // height follows the path (stairs, ramps)
        if (NavMesh.Raycast(c.pos, next, out var hit, NavMesh.AllAreas))
        {
            // Hit an edge (a wall, a shelf corner): slide along it instead of stopping.
            Vector3 slide = Vector3.ProjectOnPlane(step, hit.normal);
            slide.y = 0f;
            Vector3 alt = hit.position + slide * 0.9f;
            alt.y = next.y;
            next = NavMesh.Raycast(hit.position, alt, out var hit2, NavMesh.AllAreas) ? hit2.position : alt;
        }

        Vector3 moved = next - c.pos;
        moved.y = 0f;
        if (moved.sqrMagnitude > 1e-6f) c.yaw = Mathf.LerpAngle(c.yaw, Quaternion.LookRotation(moved).eulerAngles.y, 1f - Mathf.Exp(-dt * 12f));
        c.pos = next;
        c.dirty = true;

        // Stuck (wedged on a corner, in a crowd): no less path left for 2 s → a new path from here.
        float pathLeft = FlatDistance(c.pos, c.corners[c.corner]) + RemainingAfter(c);
        if (pathLeft < c.bestLeft - 0.3f) { c.bestLeft = pathLeft; c.stuckSince = Time.time; c.stuckRepaths = 0; }
        else if (Time.time - c.stuckSince > 2f)
        {
            stuckEvents++;
            c.stuckRepaths++;
            if (logDecisions) Log(c, $"stuck at {c.pos:F1} heading for {c.destination:F1} (try {c.stuckRepaths})");
            int tries = c.stuckRepaths;
            // A few new paths; still nowhere: give up on this spot (they decide again from where they are).
            if (tries > 3 || !PathTo(c, c.destination))
            {
                c.pathProblem = $"stuck at {c.pos:F1}, gave up on {c.destination:F1}";
                c.failures++;
                Arrive(c);
            }
            else c.stuckRepaths = tries; // (PathTo resets the timer, not the count)
        }
    }

    // The current corner, moved sideways into this customer's lane (not past an edge). The last corner
    // (where they're going) stays put.
    private Vector3 LaneTarget(Customer c)
    {
        if (c.laneCorner == c.corner) return c.laneTarget;
        c.laneCorner = c.corner;
        Vector3 corner = c.corners[c.corner];
        c.laneTarget = corner;
        if (c.corner == c.corners.Count - 1) return corner;

        Vector3 from = c.corner > 0 ? c.corners[c.corner - 1] : c.pos;
        Vector3 seg = corner - from;
        seg.y = 0f;
        if (seg.sqrMagnitude < 1e-4f) return corner;
        seg.Normalize();

        // A bend in the path is a wall's corner on its inside: swing wide of it (out of the turn), and take
        // their lane across the bend rather than across the way in.
        Vector3 side = new Vector3(seg.z, 0f, -seg.x);
        Vector3 wide = Vector3.zero;
        Vector3 after = c.corners[c.corner + 1] - corner;
        after.y = 0f;
        if (after.sqrMagnitude > 1e-4f)
        {
            after.Normalize();
            Vector3 outward = seg - after; // away from the inside of the turn
            if (outward.sqrMagnitude > 0.01f) wide = outward.normalized * cornerClearance;
            Vector3 mid = seg + after;
            if (mid.sqrMagnitude > 1e-4f) { mid.Normalize(); side = new Vector3(mid.z, 0f, -mid.x); }
        }
        Vector3 offset = corner + wide + side * c.lane;
        if ((offset - corner).sqrMagnitude < 0.0025f) return corner;
        c.laneTarget = NavMesh.Raycast(corner, offset, out var hit, CustomerNavMesh.PreferMask)
            ? Vector3.Lerp(corner, hit.position, 0.7f) // not right up against the edge
            : offset;
        return c.laneTarget;
    }

    // Can they walk straight to the corner after this one (skip this one)?
    private bool CanSeeNext(Customer c)
    {
        Vector3 nextCorner = c.corners[c.corner + 1];
        if (NavMesh.Raycast(c.pos, nextCorner, out _, CustomerNavMesh.PreferMask)) return false;
        // Clear by a margin on both sides too, so they don't cut so tight they scrape (and wedge on) the corner.
        Vector3 d = nextCorner - c.pos;
        d.y = 0f;
        if (cornerClearance <= 0f || d.sqrMagnitude < 1e-4f) return true;
        Vector3 side = new Vector3(d.z, 0f, -d.x).normalized * cornerClearance;
        return !NavMesh.Raycast(c.pos + side, nextCorner + side, out _, CustomerNavMesh.PreferMask) &&
               !NavMesh.Raycast(c.pos - side, nextCorner - side, out _, CustomerNavMesh.PreferMask);
    }

    // Path length after the current corner (only changes when they pass a corner or get a new path).
    private static float RemainingAfter(Customer c)
    {
        if (c.remainingCorner == c.corner) return c.remaining;
        float sum = 0f;
        for (int i = c.corner + 1; i < c.corners.Count; i++) sum += FlatDistance(c.corners[i - 1], c.corners[i]);
        c.remainingCorner = c.corner;
        c.remaining = sum;
        return sum;
    }

    // Debug counts (since play started).
    private int paths, pathFailures, stuckEvents;

    /// <summary>Every machine: how many customers are shown here.</summary>
    public int CustomersShown => views.Count;

    /// <summary>One line for overlays: how many customers are doing what, and path trouble so far.</summary>
    public string DebugSummary()
    {
        if (!IsServer) return $"Customers: {views.Count}";
        int walking = 0, inStores = 0, inLine = 0, standing = 0, bystanders = 0;
        foreach (var c in customers)
        {
            if (c.bystanderSlot) bystanders++;
            if (c.mode == Mode.ToQueue || c.mode == Mode.Queued) inLine++;
            else if (c.store != null) inStores++;
            if (!c.arrived) walking++;
            else standing++;
        }
        string events = eventArrivals != 1f || eventMax != 1f || eventBudget != 1f || eventPersonality.Count > 0
            ? $"; events: arrivals ×{eventArrivals:0.##}, max ×{eventMax:0.##} ({MaxCustomersNow}), budget ×{eventBudget:0.##}" +
              (eventPersonality.Count > 0 ? $", {eventPersonality.Count} personality shift(s)" : "")
            : "";
        return $"Customers: {customers.Count} ({bystanders} bystanders; {walking} walking, {standing} standing; {inStores} in stores, {inLine} in line); " +
               $"since start: {paths} paths, {pathFailures} failed, {stuckEvents} stuck{events}" +
               (Reputation < 1f ? $"; reputation ×{Reputation:0.##} ({ReputationWhy()})" : "");
    }

    private void Arrive(Customer c)
    {
        c.arrived = true;
        c.nextThink = Time.time; // decide what's next straight away
    }

    private static float FlatDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    // A push away from nearby customers and players, plus a sidestep to the right for anyone ahead
    // (so two people walking at each other pass instead of stopping nose to nose). Unit-ish length.
    private Vector3 Avoidance(Customer c, Vector3 dir)
    {
        Vector3 push = Vector3.zero;
        float range = avoidRange;
        Vector3 right = new(dir.z, 0f, -dir.x);
        int cx = Mathf.FloorToInt(c.pos.x), cz = Mathf.FloorToInt(c.pos.z);
        for (int dx = -1; dx <= 1; dx++)
        for (int dz = -1; dz <= 1; dz++)
        {
            if (!grid.TryGetValue(new Vector2Int(cx + dx, cz + dz), out var list)) continue;
            foreach (var o in list)
                if (o != c && !SameGroup(c, o)) push += AvoidOne(c.pos, o.pos, dir, right, range); // a group walks together
        }
        foreach (var p in players) push += AvoidOne(c.pos, p, dir, right, range) * 1.5f; // give players room
        return Vector3.ClampMagnitude(push, 1.2f);
    }

    private static Vector3 AvoidOne(Vector3 me, Vector3 other, Vector3 dir, Vector3 right, float range)
    {
        Vector3 d = me - other;
        if (Mathf.Abs(d.y) > 1.2f) return Vector3.zero; // another floor
        d.y = 0f;
        float dist = d.magnitude;
        if (dist >= range || dist < 1e-4f) return Vector3.zero;
        float w = 1f - dist / range;
        Vector3 away = d / dist * w;
        // In front of me: step to the right to pass.
        float front = Vector3.Dot(-d / dist, dir);
        if (front > 0.3f) away += right * (w * front);
        return away;
    }

    // Everyone's position in 1 m cells (for who's near whom).
    private readonly Dictionary<Vector2Int, List<Customer>> grid = new();
    private readonly Stack<List<Customer>> spareCells = new();
    private readonly List<Vector3> players = new();

    private void FillGrid()
    {
        foreach (var list in grid.Values) { list.Clear(); spareCells.Push(list); }
        grid.Clear();
        foreach (var c in customers)
        {
            var key = new Vector2Int(Mathf.FloorToInt(c.pos.x), Mathf.FloorToInt(c.pos.z));
            if (!grid.TryGetValue(key, out var list)) grid[key] = list = spareCells.Count > 0 ? spareCells.Pop() : new List<Customer>();
            list.Add(c);
        }
    }

    // The entrances' doorways (see CustomerSpawnPoint.doorwayRadius), gathered once a frame.
    private readonly List<(Vector3 at, float r2)> doorways = new();

    private bool InDoorway(Vector3 p)
    {
        foreach (var (at, r2) in doorways)
        {
            float dx = at.x - p.x, dz = at.z - p.z;
            if (dx * dx + dz * dz < r2 && Mathf.Abs(at.y - p.y) <= 2f) return true;
        }
        return false;
    }

    private void GatherPlayers()
    {
        doorways.Clear();
        foreach (var sp in CustomerSpawnPoint.All)
            if (sp != null && sp.doorwayRadius > 0f) doorways.Add((sp.transform.position, sp.doorwayRadius * sp.doorwayRadius));
        players.Clear();
        foreach (var client in NetworkManager.ConnectedClientsList)
            if (client.PlayerObject != null) players.Add(client.PlayerObject.transform.position);
    }

    // Customers standing still (in line, looking at a shelf, browsing) ease apart so they don't stand
    // inside each other (walkers steer around them; see Avoidance).
    private void KeepApart(float dt)
    {
        float min = personalSpace;
        float maxStep = 1.2f * dt;
        foreach (var c in customers)
        {
            if (!c.arrived) continue;
            if ((c.mode == Mode.ToQueue || c.mode == Mode.Queued) && float.IsFinite(c.queueSpot.x))
            {
                // In line: hold their place (others ease away from them instead), stepping back if moved off it.
                Vector3 off = c.queueSpot - c.pos;
                off.y = 0f;
                if (off.sqrMagnitude < 0.0004f) continue;
                c.pos += Vector3.ClampMagnitude(off, lineSettleSpeed * dt);
                c.dirty = true;
                continue;
            }
            Vector3 push = Vector3.zero;
            int cx = Mathf.FloorToInt(c.pos.x), cz = Mathf.FloorToInt(c.pos.z);
            for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (!grid.TryGetValue(new Vector2Int(cx + dx, cz + dz), out var list)) continue;
                foreach (var o in list)
                {
                    if (o == c) continue;
                    Vector3 d = c.pos - o.pos;
                    d.y = 0f;
                    float d2 = d.sqrMagnitude;
                    float space = SameGroup(c, o) ? min * 0.75f : min; // a group stands closer together
                    if (d2 >= space * space || Mathf.Abs(c.pos.y - o.pos.y) > 1f) continue;
                    if (d2 < 1e-6f) d = new Vector3(Mathf.Sin(c.id), 0f, Mathf.Cos(c.id)) * 0.01f; // exactly on top: split
                    float dist = Mathf.Sqrt(Mathf.Max(d2, 1e-6f));
                    push += d / dist * (space - dist);
                }
            }
            if (push.sqrMagnitude < 1e-6f) continue;
            Vector3 to = c.pos + Vector3.ClampMagnitude(push * 0.5f, maxStep);
            if (NavMesh.Raycast(c.pos, to, out var hit, NavMesh.AllAreas)) to = hit.position; // stay on the floor
            c.pos = to;
            c.dirty = true;
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
        if (c.failures > 4)
        {
            if (Trapped(c)) return;
            Log(c, "stuck: gone");
            Despawn(c);
            return;
        }
        if (c.trappedSince >= 0f && c.failures == 0 && !c.arrived) c.trappedSince = -1f; // found a way out

        // The store shut (closed, being renovated, sold) while they were in it: out they go.
        if (c.store != null && c.mode != Mode.Leaving && !c.store.IsOpen)
        {
            Log(c, $"'{c.store.DisplayName}' closed: leaving");
            React(c, Reaction.StoreClosed);
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
                if (Time.time >= c.until) { c.mode = Mode.Browsing; Decide(c); } // mode first, so an error in Decide can't repeat it every think
                break;
            case Mode.ToQueue:
            case Mode.Queued: ThinkQueue(c); break;
            case Mode.Leaving:
                if (c.arrived) FinishStore(c);
                break;
            case Mode.Exiting:
                if (c.arrived) { Log(c, "left the mall"); Despawn(c); }
                break;
            case Mode.Follow: ThinkFollow(c); break;
            case Mode.Wait: ThinkWait(c); break;
            case Mode.Caught:
                if (Time.time >= c.until) RunForExit(c);
                break;
            case Mode.Lost: ThinkLost(c); break;
            case Mode.WithHelper: ThinkWithHelper(c); break;
            case Mode.Searching: ThinkSearching(c); break;
        }
    }

    // Choose where to walk next: an open store's entrance (more likely the more its window draws this
    // kind of customer), or the exit when they're done.
    private void PickGoal(Customer c)
    {
        if (FollowInstead(c)) return; // in a group: the leader chooses
        if (FamilyPlans(c)) return;   // a family splitting up, or meeting again

        // A bystander just walks the halls (until it's time to go).
        bool bystander = IsBystander(c);
        if (bystander && Time.time < c.leaveAt && TryHallwaySpot(c.pos, out var walk))
        {
            c.mode = Mode.Wander;
            c.goalStore = null;
            c.lingering = false;
            if (PathTo(c, walk)) { Log(c, "strolling the halls"); return; }
        }
        bool done = bystander || Time.time >= c.leaveAt || c.storesVisited >= maxStoresPerVisit || BudgetLeft(c) <= 0;
        if (!done && MallManager.Instance != null)
        {
            // Someone who knows the mall heads for a store that sells what they like; anyone else
            // wanders to a store at random and sees what catches their eye on the way.
            bool knows = Random.value < c.personality.knowsTheMall;

            // Off to where they know it's cheaper.
            var deal = c.bargainStore;
            if (deal != null && deal != c.store && deal.IsOpen && entrances.TryGetValue(deal, out var dealWays) && dealWays.Count > 0)
            {
                var e = dealWays[Random.Range(0, dealWays.Count)];
                c.mode = Mode.Wander;
                c.goalStore = deal;
                c.goalEntrance = e;
                if (PathTo(c, Near(e.outside, 1.5f, deal))) { Log(c, $"heading to '{deal.DisplayName}' for a better price"); return; }
                c.bargainStore = null;
            }
            else c.bargainStore = null;

            // Or just a stroll to somewhere in the hallways.
            if (!knows && Random.value < roamChance && TryHallwaySpot(c.pos, out var spot))
            {
                c.mode = Mode.Wander;
                c.goalStore = null;
                if (PathTo(c, spot)) { Log(c, "strolling"); return; }
            }
            float total = 0f;
            StorePlot pick = null;
            foreach (var plot in MallManager.Instance.Plots)
            {
                if (plot == null || !plot.IsOpen || plot.Appeal == null || c.passed.Contains(plot) ||
                    !entrances.TryGetValue(plot, out var list) || list.Count == 0 || Crowded(plot, c))
                    continue;
                float w = knows ? 0.02f + AppealTo(c, plot).insideDraw : 1f;
                total += w;
                if (Random.value * total < w) pick = plot; // one pass, weighted
            }
            if (pick != null) Log(c, knows ? $"knows '{pick.DisplayName}' has what they like" : $"wandering toward '{pick.DisplayName}'");
            if (pick != null)
            {
                var e = entrances[pick][Random.Range(0, entrances[pick].Count)];
                c.mode = Mode.Wander;
                c.goalStore = pick;
                c.goalEntrance = e;
                if (PathTo(c, Near(e.outside, 1.5f, pick))) return;
                c.passed.Add(pick); // can't get there
            }
        }
        c.goalStore = null;

        // Nowhere to go yet, but not ready to leave: a stroll.
        if (!done && Time.time < c.stayUntil && TryHallwaySpot(c.pos, out var stroll))
        {
            c.mode = Mode.Wander;
            if (PathTo(c, stroll)) { Log(c, $"no store to go to ({WhyNoStores()}): strolling"); return; }
        }

        // Done (or nowhere to go): the exit.
        var exit = PickSpawnPoint(sp => sp.isExit && (sp.transform.position - c.cameFrom).sqrMagnitude > 25f)
                   ?? PickSpawnPoint(sp => sp.isExit);
        c.mode = Mode.Exiting;
        if (exit == null || !PathTo(c, exit.transform.position)) { Despawn(c); return; }
        Log(c, done ? "done: heading for the exit" : $"heading for the exit, no store to go to ({WhyNoStores()})");
    }

    // A walkable spot within Radius of a point, outside the store (so people don't all aim at one point).
    private static Vector3 Near(Vector3 point, float radius, StorePlot notIn)
    {
        for (int tries = 0; tries < 4; tries++)
        {
            Vector2 r = Random.insideUnitCircle * radius;
            if (NavMesh.SamplePosition(point + new Vector3(r.x, 0f, r.y), out var hit, 1f, CustomerNavMesh.PreferMask) &&
                (notIn == null || !notIn.Contains(hit.position + Vector3.up * 0.1f)))
                return hit.position;
        }
        return point;
    }

    // A random walkable spot in the hallways (not in any store), at least 8 m away.
    private static bool TryHallwaySpot(Vector3 from, out Vector3 spot)
    {
        spot = default;
        var nav = CustomerNavMesh.Instance;
        if (nav == null || MallManager.Instance == null) return false;
        Bounds b = nav.BuiltBounds;
        for (int tries = 0; tries < 8; tries++)
        {
            var p = new Vector3(Random.Range(b.min.x, b.max.x), from.y, Random.Range(b.min.z, b.max.z));
            if (!NavMesh.SamplePosition(p, out var hit, 4f, CustomerNavMesh.PreferMask)) continue;
            if (Mathf.Abs(hit.position.y - from.y) > 3f || (hit.position - from).sqrMagnitude < 64f) continue; // same floor, a walk away
            bool inStore = false;
            foreach (var plot in MallManager.Instance.Plots)
                if (plot != null && plot.Contains(hit.position + Vector3.up * 0.1f)) { inStore = true; break; }
            if (inStore) continue;
            spot = hit.position;
            return true;
        }
        return false;
    }

    private void ThinkWander(Customer c)
    {
        if (IsBystander(c)) { ThinkStroll(c); return; }
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
        if (Notice(c)) return;
        if (!c.arrived) return;
        // At the store they were heading for (if passing it didn't already decide).
        if (c.goalStore != null && !c.passed.Contains(c.goalStore) && c.goalStore.IsOpen && c.goalStore.Appeal != null &&
            Consider(c, c.goalStore, c.goalEntrance)) return;
        PickGoal(c);
    }

    // A bystander: no stores, just a stroll to a spot, a while standing there, then on to the next.
    private void ThinkStroll(Customer c)
    {
        if (!c.arrived) return;
        if (!c.lingering)
        {
            c.lingering = true;
            c.until = Time.time + Random.Range(lingerSeconds.x, lingerSeconds.y);
            return;
        }
        if (Time.time < c.until) return;
        c.lingering = false;
        PickGoal(c);
    }

    // A storefront in view (in range, nothing in the way): if what's in its window appeals to them,
    // they head over to it. A couple of looks per decision, nearest first.
    private bool Notice(Customer c)
    {
        if (MallManager.Instance == null) return false;
        float r2 = sightRange * sightRange;
        Vector3 eye = c.pos + Vector3.up * 1.6f;
        int looked = 0;
        foreach (var plot in MallManager.Instance.Plots)
        {
            if (looked >= 2) break;
            if (plot == null || plot == c.goalStore || !plot.IsOpen || plot.Appeal == null || c.passed.Contains(plot) ||
                c.noticed.Contains(plot) || !entrances.TryGetValue(plot, out var list) || Crowded(plot, c)) continue;
            foreach (var e in list)
            {
                if ((e.outside - c.pos).sqrMagnitude > r2) continue;
                looked++;
                Vector3 seen = e.outside + Vector3.up * 1.2f;
                if (Physics.Linecast(eye, seen, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) break;
                c.noticed.Add(plot);
                float pull = Mathf.Clamp01(AppealTo(c, plot).outsideDraw * sightPull);
                if (Random.value >= pull)
                {
                    Log(c, $"saw '{plot.DisplayName}', not interested ({pull:0.00})");
                    break;
                }
                c.goalStore = plot;
                c.goalEntrance = e;
                if (!PathTo(c, Near(e.outside, 1.5f, plot))) return false;
                Log(c, $"saw '{plot.DisplayName}' and is heading over ({pull:0.00})");
                return true;
            }
        }
        return false;
    }

    // A store's entrance, right there: roll its walk-in chance for this customer (once per store).
    private bool Consider(Customer c, StorePlot plot, Entrance e)
    {
        c.passed.Add(plot);
        float chance = AppealTo(c, plot).enterChance;
        bool full = InsideCount(plot) >= Capacity(plot);
        bool deal = plot == c.bargainStore;
        if (plot == c.mustEnter || deal || (!full && Random.value < chance))
        {
            if (deal) c.bargainStore = null; // there now (once: no coming back for it again and again)
            Enter(c, plot, e);
            return true;
        }
        Log(c, $"walked past '{plot.DisplayName}' (chance {chance:0.00}{(full ? ", full" : "")})");
        return false;
    }

    private int Capacity(StorePlot plot) =>
        Mathf.Max(1, Mathf.RoundToInt(storeCapacity * (1f + ProgressionManager.StoreStat(plot, PerkStat.Capacity))));

    private int InsideCount(StorePlot plot) => inside.TryGetValue(plot, out int n) ? n : 0;

    // Customers on their way to a store, or waiting at its door for their party (worked out once a frame).
    private readonly Dictionary<StorePlot, int> outsideCount = new();
    private int outsideFrame = -1;

    private int CrowdCount(StorePlot plot)
    {
        if (outsideFrame != Time.frameCount)
        {
            outsideFrame = Time.frameCount;
            outsideCount.Clear();
            foreach (var o in customers)
            {
                var p = HeadingFor(o);
                if (p != null) outsideCount[p] = (outsideCount.TryGetValue(p, out int k) ? k : 0) + 1;
            }
        }
        return InsideCount(plot) + (outsideCount.TryGetValue(plot, out int n) ? n : 0);
    }

    // The store a customer is about to add to the crowd at (not in one yet): where they're walking
    // (or their party's leader is), or the door they're waiting at.
    private static StorePlot HeadingFor(Customer o)
    {
        if (o.store != null) return null;
        if (o.mode == Mode.Wait) return o.waitFor;
        var who = o.mode == Mode.Follow ? Root(o) : o;
        return who.store == null && (who.mode == Mode.Wander) ? who.goalStore : null;
    }

    // Is this store full, counting everyone inside, on their way, and waiting at the door (but not this customer)?
    private bool Crowded(StorePlot plot, Customer c) =>
        CrowdCount(plot) - (HeadingFor(c) == plot ? 1 : 0) >= Capacity(plot);

    private void Enter(Customer c, StorePlot plot, Entrance e)
    {
        c.store = plot;
        c.entrance = e;
        c.satisfaction = 0f;
        c.shelvesSeen = 0;
        c.seen.Clear();
        c.basket.Clear();
        c.handed = 0;
        c.waitFor = null;
        c.counter = null;
        c.ticket = 0;
        c.paid = false;
        inside[plot] = InsideCount(plot) + 1;
        c.counted = true;
        c.mode = Mode.Entering;
        GroupEntered(c, plot); // the leader: everyone with them comes in too
        if (!PathTo(c, e.inside)) { LeaveStore(c, 0f); return; }
        Log(c, $"going into '{plot.DisplayName}'");
        React(c, Reaction.Interested);
    }

    // Inside: pick something to go and look at, or head to the checkout (or out) when done.
    private void ThinkBrowse(Customer c)
    {
        var plot = c.store;
        bool done = c.basket.Count - c.handed >= c.wants || (!c.thief && c.budgetCents <= 0) ||
                    c.shelvesSeen >= Mathf.Max(1, Mathf.RoundToInt(maxLooksPerStore * GroupLooks(c)));
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

        if (c.basket.Count > 0 && c.thief && TryShoplift(c)) return;
        if (HandToPayer(c)) return; // a couple: the leader pays for both
        if (c.basket.Count > 0) GoToCheckout(c);
        else
        {
            Log(c, $"found nothing to buy in '{plot.DisplayName}'");
            React(c, Reaction.NothingForMe);
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
            if (s.quantity <= 0 || c.seen.Contains(s.Key) || (!c.thief && s.priceCents > c.budgetCents)) continue;
            if (s.container != null && s.container.IsCovered(s.zone)) continue; // can't get at it
            if (Heading(s.Key) >= s.quantity && !LastOfIt(plot, s)) continue; // others already have it covered; there's more elsewhere
            if (c.thief && s.container == null) continue; // a shoplifter only takes what fits in a bag
            float w = plot.Appeal.WorthTo(c.personality, s) * tolerance;
            if (c.thief) w *= 1f + s.priceCents / 10000f; // and likes it pricey
            if (s.itemId == c.bargainItem && c.bargainItem != 0) w *= 5f; // what they came over for
            if (c.group != null) // the children's wishes, group tastes, "I'll get one too"
                w = Mathf.Max(w, KidsWorth(c, plot, s, tolerance)) * GroupItemBoost(c, s.itemId);
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

    // Who's already walking to (or standing at) each shelf spot, counted once a frame when someone asks.
    private readonly Dictionary<StoreListing.Key, int> heading = new();
    private int headingFrame = -1;

    private int Heading(StoreListing.Key key)
    {
        if (headingFrame != Time.frameCount)
        {
            headingFrame = Time.frameCount;
            heading.Clear();
            foreach (var o in customers)
                if (o.mode == Mode.ToShelf || o.mode == Mode.Looking)
                    heading[o.target] = heading.TryGetValue(o.target, out int n) ? n + 1 : 1;
        }
        return heading.TryGetValue(key, out int count) ? count : 0;
    }

    // Is this the only place in the store with this product still up for grabs? Then they try anyway.
    private bool LastOfIt(StorePlot plot, StoreListing.Source s)
    {
        foreach (var o in plot.Listing.SourcesOf(s.itemId))
            if (!o.Key.Equals(s.Key) && o.quantity > Heading(o.Key)) return false;
        return true;
    }

    // Priced well above what it's worth on the market (per product).
    private static bool AboveMarket(StoreListing.Source s)
    {
        var catalog = ItemCatalog.Instance;
        if (catalog == null || !catalog.TryGet(s.itemId, out var def)) return false;
        int market = MarketModifiers.MarketPriceCents(def) * UnitsOf(s);
        return market > 0 && s.priceCents > market * 1.15f;
    }

    // Products in a listing: a whole crate counts its units.
    private static int UnitsOf(StoreListing.Source s) =>
        s.item != null && s.item.TryGetComponent<ItemPickupNet>(out var p) && p.IsStockCrate ? Mathf.Max(1, p.Units) : 1;

    // How much a price over the market one multiplies the chance of stealing it (1 = not over).
    private float Temptation(StoreListing.Source s)
    {
        var catalog = ItemCatalog.Instance;
        if (catalog == null || !catalog.TryGet(s.itemId, out var def)) return 1f;
        int market = MarketModifiers.MarketPriceCents(def) * UnitsOf(s);
        if (market <= 0 || s.priceCents <= market) return 1f;
        return Mathf.Min(maxTemptation, 1f + overpriceTemptation * (s.priceCents / (float)market - 1f));
    }

    // The cheapest price per product of each thing across the open stores, gathered every couple of seconds.
    private readonly Dictionary<int, (float perUnit, StorePlot plot)> cheapest = new();
    private float cheapestUntil = -1f;

    private bool CheaperElsewhere(StorePlot here, StoreListing.Source s, out StorePlot there)
    {
        there = null;
        if (Time.time >= cheapestUntil)
        {
            cheapestUntil = Time.time + 2f;
            cheapest.Clear();
            if (MallManager.Instance != null)
                foreach (var plot in MallManager.Instance.Plots)
                {
                    if (plot == null || !plot.IsOpen || plot.Listing == null ||
                        !entrances.TryGetValue(plot, out var ways) || ways.Count == 0) continue;
                    foreach (var o in plot.Listing.Sources)
                    {
                        if (o.quantity <= 0) continue;
                        float per = o.priceCents / (float)UnitsOf(o);
                        if (!cheapest.TryGetValue(o.itemId, out var best) || per < best.perUnit)
                            cheapest[o.itemId] = (per, plot);
                    }
                }
        }
        if (!cheapest.TryGetValue(s.itemId, out var low) || low.plot == here || low.plot == null || !low.plot.IsOpen) return false;
        float mine = s.priceCents / (float)UnitsOf(s);
        if (low.perUnit > mine * (1f - cheaperBy)) return false;
        there = low.plot;
        return true;
    }

    // At the shelf: still there, still worth it at this price? Then take one.
    private void Decide(Customer c)
    {
        var plot = c.store;
        if (!plot.Listing.TryGetSource(c.target, out var s) || s.quantity <= 0 ||
            (s.container != null && s.container.IsCovered(s.zone)))
        {
            c.satisfaction -= 0.15f;
            Log(c, "what they came for was gone");
            React(c, Reaction.SoldOut);
            return;
        }
        if (!c.thief && s.priceCents > c.budgetCents)
        {
            c.satisfaction -= 0.05f;
            React(c, Reaction.TooPricey); // more than they brought
            return;
        }
        // Another store sells it for less, and they know it: put it back and go there next.
        if (!c.thief && CheaperElsewhere(plot, s, out var there) &&
            Random.value < comparePrices * Mathf.Clamp01(c.personality.priceSensitivity))
        {
            c.bargainStore = there;
            c.bargainItem = s.itemId;
            c.passed.Remove(there);
            c.satisfaction -= 0.05f;
            Log(c, $"{CheckoutCounter.NameOf(s.itemId)} is cheaper at '{there.DisplayName}', going there next");
            React(c, Reaction.CheaperElsewhere);
            return;
        }

        // A price well over the market one tempts them: their usual chance of stealing, multiplied.
        if (!c.thief && s.container != null && c.personality.shopliftChance > 0f &&
            Random.value < c.personality.shopliftChance * eventShoplifting * Temptation(s))
        {
            c.thief = true;
            Log(c, $"tempted by {CheckoutCounter.NameOf(s.itemId)} at {CheckoutCounter.FormatAmount(s.priceCents)}: stealing it");
        }

        float tolerance = 1f + ProgressionManager.StoreStat(plot, PerkStat.PriceTolerance);
        float worth = plot.Appeal.WorthTo(c.personality, s) * tolerance;
        if (c.group != null) worth = Mathf.Max(worth, KidsWorth(c, plot, s, tolerance)) * GroupItemBoost(c, s.itemId) * GroupEagerness(c);
        if (!c.thief && Random.value >= Mathf.Clamp01(worth * buyEagerness))
        {
            Log(c, $"looked at {CheckoutCounter.NameOf(s.itemId)} for {CheckoutCounter.FormatAmount(s.priceCents)}, didn't take it");
            if (AboveMarket(s) && c.personality.priceSensitivity > 0.3f) React(c, Reaction.TooPricey);
            return;
        }

        if (s.container != null)
        {
            uint paint = s.container.GetZone(s.zone).paint;
            int itemId = s.container.ServerRemoveOne(s.zone); // the host settles who gets the last one
            if (itemId == 0) { c.satisfaction -= 0.15f; React(c, Reaction.SoldOut); return; }
            c.basket.Add(new Taken { container = s.container, zone = s.zone, itemId = itemId, priceCents = s.priceCents, units = 1, paint = paint });
        }
        else if (s.item != null)
        {
            // A loose item stays where it is until they pay for it at the counter.
            int units = s.item.TryGetComponent<ItemPickupNet>(out var pickup) && pickup.IsStockCrate ? pickup.Units : 1;
            c.basket.Add(new Taken { loose = s.item, itemId = s.itemId, priceCents = s.priceCents, units = units });
        }
        else return;

        if (!c.thief) c.budgetCents -= s.priceCents;
        if (s.itemId == c.bargainItem) { c.bargainItem = 0; c.bargainStore = null; } // found the deal
        GroupBought(c, s.itemId);
        c.satisfaction += 0.25f;
        Log(c, $"took {CheckoutCounter.NameOf(s.itemId)} ({CheckoutCounter.FormatAmount(s.priceCents)})");
        React(c, Reaction.Bought);
        if (c.thief) GiveBag(c, plot); // a shoplifter brought their own bag (and fills it before paying...)
    }

    /* ========================= CHECKOUT ========================= */

    private void GoToCheckout(Customer c)
    {
        // The line that looks quickest: everyone's things ahead of them, at that counter's pace.
        CheckoutCounter best = null;
        float bestWait = float.MaxValue;
        foreach (var counter in CheckoutCounter.All)
        {
            if (counter == null || !counter.IsSpawned || counter.IsForSale || counter.Plot != c.store) continue;
            float wait = ExpectedWait(counter);
            if (wait < bestWait) { bestWait = wait; best = counter; }
        }
        if (best == null)
        {
            Log(c, $"no checkout in '{c.store.DisplayName}': put things back and left");
            React(c, Reaction.NoCheckout);
            LeaveStore(c, -0.6f);
            return;
        }

        // Is it worth the wait? The longer past their patience, the likelier they put it all back.
        float patience = patienceSeconds * (1f + ProgressionManager.StoreStat(c.store, PerkStat.Patience));
        if (bestWait > patience && Random.value < Mathf.Clamp01(bestWait / patience - 0.5f))
        {
            Log(c, $"line looks like {bestWait:0} s (patience {patience:0} s): put things back and left");
            React(c, Reaction.LineTooLong);
            LeaveStore(c, -0.4f);
            return;
        }

        if (!lines.TryGetValue(best, out var line)) lines[best] = line = new List<Customer>();
        line.Add(c);
        c.counter = best;
        c.queueSpot = Vector3.positiveInfinity;
        c.nearFrontSince = -1f;
        c.until = Time.time + patience;
        c.mode = Mode.ToQueue;
        Log(c, $"joined a line of {line.Count - 1} (about {bestWait:0} s)");
        WalkToPlaceInLine(c);
    }

    // Seconds per item each counter has actually been taking (learned as customers pay).
    private readonly Dictionary<CheckoutCounter, float> counterPace = new();

    private float PaceOf(CheckoutCounter counter) =>
        counterPace.TryGetValue(counter, out float pace) ? pace : secondsPerItemGuess;

    // How long someone joining this line now would wait: the things of everyone in it.
    private float ExpectedWait(CheckoutCounter counter)
    {
        if (!lines.TryGetValue(counter, out var line) || line.Count == 0) return 0f;
        float pace = PaceOf(counter), wait = 0f;
        foreach (var o in line)
        {
            int things = o.ticket != 0 ? o.itemsAtCounter : o.basket.Count;
            float theirs = Mathf.Max(1, things) * pace;
            if (o.ticket != 0) theirs = Mathf.Max(0f, theirs - (Time.time - o.frontSince)); // part done already
            wait += theirs;
        }
        return wait;
    }

    // Where each place in a counter's line is: back from the counter, bending round walls and shelves
    // so places don't end up on top of each other. Worked out again every couple of seconds.
    private readonly Dictionary<CheckoutCounter, (float at, List<Vector3> spots, Vector3 front)> lineShapes = new();

    private Vector3 QueueSpot(CheckoutCounter counter, int place, out Vector3 facing)
    {
        if (!lineShapes.TryGetValue(counter, out var shape) || Time.time - shape.at > 2f)
        {
            shape = (Time.time, new List<Vector3>(), Vector3.zero);
            counter.GetQueueSpot(0, queueSpacing, out Vector3 p0, out Vector3 frontFacing);
            counter.GetQueueSpot(1, queueSpacing, out Vector3 p1, out _);
            if (NavMesh.SamplePosition(p0, out var h0, 1.5f, NavMesh.AllAreas)) p0 = h0.position;
            shape.front = frontFacing;
            shape.spots.Add(p0);
            Vector3 dir = p1 - p0;
            dir.y = 0f;
            dir = dir.sqrMagnitude > 1e-4f ? dir.normalized : -frontFacing;
            ExtendLine(shape.spots, ref dir, 12);
            lineShapes[counter] = shape;
        }
        var spots = shape.spots;
        if (place >= spots.Count)
        {
            Vector3 d = spots.Count > 1 ? (spots[^1] - spots[^2]).normalized : -shape.front;
            ExtendLine(spots, ref d, place - spots.Count + 1);
        }
        int i = Mathf.Min(place, spots.Count - 1);
        Vector3 ahead = i == 0 ? spots[0] + shape.front : spots[i - 1];
        facing = ahead - spots[i];
        facing.y = 0f;
        return spots[i];
    }

    private static readonly float[] Bends = { 0f, 30f, -30f, 60f, -60f, 90f, -90f, 135f, -135f };

    // Add places to the end of a line: Spacing on from the last one, turning if the way on is blocked
    // or would land near an earlier place.
    private void ExtendLine(List<Vector3> spots, ref Vector3 dir, int count)
    {
        for (int k = 0; k < count; k++)
        {
            Vector3 last = spots[^1];
            bool placed = false;
            foreach (float bend in Bends)
            {
                Vector3 d = Quaternion.Euler(0f, bend, 0f) * dir;
                Vector3 want = last + d * queueSpacing;
                if (!NavMesh.SamplePosition(want, out var hit, 0.4f, CustomerNavMesh.PreferMask)) continue;
                if (NavMesh.Raycast(last, hit.position, out _, CustomerNavMesh.PreferMask)) continue;
                bool clear = true;
                for (int j = 0; j < spots.Count - 1 && clear; j++)
                    if (FlatDistance(spots[j], hit.position) < queueSpacing * 0.8f) clear = false;
                if (!clear) continue;
                spots.Add(hit.position);
                Vector3 nd = hit.position - last;
                nd.y = 0f;
                if (nd.sqrMagnitude > 1e-4f) dir = nd.normalized;
                placed = true;
                break;
            }
            if (!placed) spots.Add(last + dir * queueSpacing); // nowhere good: straight on (they'll spread)
        }
    }

    private void WalkToPlaceInLine(Customer c)
    {
        int place = lines.TryGetValue(c.counter, out var line) ? line.IndexOf(c) : 0;
        Vector3 spot = QueueSpot(c.counter, Mathf.Max(0, place), out Vector3 facing);
        if (lineLooseness > 0f) // a little off the line, the same way every time for this customer
        {
            Vector3 loose = new Vector3(Mathf.Sin(c.id * 12.9898f), 0f, Mathf.Cos(c.id * 78.233f)) * lineLooseness;
            if (!NavMesh.Raycast(spot, spot + loose, out _, NavMesh.AllAreas)) spot += loose;
        }
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

        // At the front but held just short of the spot (someone standing in it, a tight corner): close
        // enough for long enough counts as there, so they don't stand waiting forever.
        if (place == 0 && !c.arrived && c.ticket == 0 && float.IsFinite(c.queueSpot.x))
        {
            if (FlatDistance(c.pos, c.queueSpot) > frontReach) c.nearFrontSince = -1f;
            else if (c.nearFrontSince < 0f) c.nearFrontSince = Time.time;
            else if (Time.time - c.nearFrontSince >= frontSettleSeconds)
            {
                Log(c, $"close enough to the front ({FlatDistance(c.pos, c.queueSpot):0.00} m): counts as there");
                c.corners.Clear();
                c.corner = 0;
                c.arrived = true;
            }
        }

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
                    uint loosePaint = t.loose.TryGetComponent<ItemPickupNet>(out var lp) ? lp.Paint : 0u;
                    t.loose.NetworkObject.Despawn(true);
                    purchases.Add(new CheckoutCounter.Purchase { itemId = t.itemId, priceCents = t.priceCents, units = t.units, whole = true, paint = loosePaint });
                }
                else purchases.Add(new CheckoutCounter.Purchase { itemId = t.itemId, priceCents = t.priceCents, units = 1, paint = t.paint });
            }
            if (purchases.Count == 0) { LeaveStore(c, c.satisfaction); return; }
            c.ticket = counter.ServerAddCustomer(purchases, c.personality != null ? c.personality.cashChance : 0.5f);
            if (c.ticket == 0) { LeaveStore(c, -0.4f); return; }
            c.itemsAtCounter = purchases.Count;
            c.frontSince = Time.time;
            c.basket.Clear(); // on the belt now: theirs to pay for
            Log(c, $"put {purchases.Count} thing(s) on the belt");
            return;
        }

        // Waited too long (and not being served yet): give up.
        if (Time.time >= c.until)
        {
            Log(c, "waited too long in line: left their things on the counter");
            React(c, Reaction.GaveUpWaiting);
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
            // The counter's pace: how long they took from reaching the front, per thing.
            float perItem = (Time.time - c.frontSince) / Mathf.Max(1, c.itemsAtCounter);
            counterPace[counter] = Mathf.Lerp(PaceOf(counter), Mathf.Clamp(perItem, 1f, 120f), 0.3f);
            c.nextThink = Time.time;
            Log(c, "paid");
            React(c, Reaction.Paid);
            GiveBag(c, c.store);
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
        // Walking off from the checkout line: their things stay on the counter for a player to put back.
        if (c.counter != null && c.counter.IsSpawned && !c.paid)
        {
            if (c.ticket != 0) c.counter.ServerCustomerWalkedOff(c.ticket);
            else if (c.basket.Count > 0) c.counter.ServerLeaveOnCounter(ToPurchases(c));
            if (c.ticket != 0 || c.basket.Count > 0) c.basket.Clear();
        }
        c.ticket = 0;
        c.counter = null;
        foreach (var t in c.basket)
            if (t.container != null && t.container.IsSpawned)
                t.container.ServerAddOne(t.zone, t.itemId, t.container.GetZone(t.zone).orient, t.paint, split: false);
        c.basket.Clear();
        if (c.counted && c.store != null && inside.TryGetValue(c.store, out int n)) inside[c.store] = Mathf.Max(0, n - 1);
        c.counted = false;
    }

    // What's in their hands, as things to put on a counter (loose items were never picked up: they stay put).
    private static List<CheckoutCounter.Purchase> ToPurchases(Customer c)
    {
        var list = new List<CheckoutCounter.Purchase>();
        foreach (var t in c.basket)
            if (t.loose == null) list.Add(new CheckoutCounter.Purchase { itemId = t.itemId, priceCents = t.priceCents, units = 1, paint = t.paint });
        return list;
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
        if (plot != null && c.fleeing)
        {
            DropFromGroup(c); // running off: on their own now
            OutTheDoor(c, plot);
            c.store = null;
            c.mode = Mode.Exiting;
            var exit = PickSpawnPoint(sp => sp.isExit && (sp.transform.position - c.cameFrom).sqrMagnitude > 25f)
                       ?? PickSpawnPoint(sp => sp.isExit);
            if (exit == null || !PathTo(c, exit.transform.position)) Despawn(c);
            return;
        }
        if (plot != null)
        {
            c.storesVisited++;
            if (plot.HasOwner && ProgressionManager.Instance != null && Mathf.Abs(c.satisfaction) > 0.01f)
                ProgressionManager.Instance.ServerCustomerLeft(plot.OwnerTeamId, Mathf.Clamp(c.satisfaction, -1f, 1f));
            Log(c, $"left '{plot.DisplayName}' ({(c.satisfaction >= 0f ? "happy" : "unhappy")} {c.satisfaction:0.00})");
        }
        c.store = null;
        if (plot != null && GroupFinishedStore(c, plot)) return; // together: wait for the others
        PickGoal(c);
    }

    /* ========================= BAGS ========================= */

    // One more bag for a store they haven't got one from yet (up to Max Bags). Everyone sees it.
    private void GiveBag(Customer c, StorePlot from)
    {
        if (from == null || from == c.baggedIn || c.bags >= maxBags) return;
        c.baggedIn = from;
        c.bags++;
        SetBags(c.id, c.bags);
        BagsRpc(c.id, c.bags);
    }

    [Rpc(SendTo.NotServer)]
    private void BagsRpc(ushort id, byte count) => SetBags(id, count);

    private void SetBags(ushort id, byte count)
    {
        if (views.TryGetValue(id, out var v) && v != null) v.SetBags(count, bagPrefab, randomBagColors);
    }

    /* ========================= THEFT ========================= */

    private float eventShoplifting = 1f;

    // Done picking things: a shoplifter walks out with them instead of paying, unless someone is
    // watching or the store's Theft Resistance puts them off (then they pay like anyone else).
    private bool TryShoplift(Customer c)
    {
        var plot = c.store;
        float resistance = Mathf.Clamp01(ProgressionManager.StoreStat(plot, PerkStat.TheftResistance));
        if (Watched(c.pos))
        {
            Log(c, "a shoplifter, but someone's watching: paying instead");
            return false;
        }
        if (Random.value < resistance * 0.8f)
        {
            Log(c, $"a shoplifter, but put off by the store's security ({resistance:0.00}): paying instead");
            return false;
        }
        c.fleeing = true;
        c.speed *= fleeSpeed;
        Log(c, $"SHOPLIFTING {c.basket.Count} thing(s) from '{plot.DisplayName}'");
        c.mode = Mode.Leaving;
        if (!PathTo(c, c.entrance.outside)) FinishStore(c);
        return true;
    }

    // Is a player near and able to see this spot?
    private bool Watched(Vector3 at)
    {
        Vector3 eye = at + Vector3.up * 1.6f;
        foreach (var p in players)
        {
            if ((p - at).sqrMagnitude > watchedRange * watchedRange) continue;
            if (!Physics.Linecast(eye, p + Vector3.up * 1.2f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) return true;
        }
        return false;
    }

    // Out of the store with things unpaid for: they're stolen now. The alarm may go off.
    private void OutTheDoor(Customer c, StorePlot plot)
    {
        c.robbed = plot;
        c.stolen.Clear();
        foreach (var t in c.basket) if (t.container != null) c.stolen.Add(t);
        c.basket.Clear();
        if (c.counted && inside.TryGetValue(plot, out int n)) inside[plot] = Mathf.Max(0, n - 1);
        c.counted = false;

        float chance = Mathf.Clamp01(alarmChance + ProgressionManager.StoreStat(plot, PerkStat.TheftResistance));
        if (Random.value < chance && plot.HasOwner && NotificationManager.Instance != null)
        {
            c.alarmed = true;
            NotificationManager.Instance.ServerNotifyTeamText(plot.OwnerTeamId, $"Shoplifter at {plot.DisplayName}!",
                $"The alarm went off: someone just walked out with {StolenText(c)}. Catch them before they leave the mall (F).",
                alarm: true);
        }
        Log(c, $"out of '{plot.DisplayName}' with {StolenText(c)}{(c.alarmed ? " (ALARM)" : "")}");
    }

    // Left the mall with it: gone for good. The store's team finds out (if the alarm didn't tell them).
    private void GotAway(Customer c)
    {
        var plot = c.robbed;
        if (plot != null && plot.HasOwner && NotificationManager.Instance != null)
            NotificationManager.Instance.ServerNotifyTeamText(plot.OwnerTeamId, $"Stock stolen from {plot.DisplayName}",
                c.alarmed ? $"The shoplifter got away with {StolenText(c)}." : $"{StolenText(c)} went missing without being paid for.");
        Log(c, $"got away with {StolenText(c)}");
        c.stolen.Clear();
    }

    private static string StolenText(Customer c)
    {
        int total = 0;
        foreach (var t in c.stolen.Count > 0 ? c.stolen : c.basket) total += t.priceCents;
        int count = c.stolen.Count > 0 ? c.stolen.Count : c.basket.Count;
        return $"{count} thing{(count == 1 ? "" : "s")} ({CheckoutCounter.FormatAmount(total)})";
    }

    /// <summary>
    /// The customer the local player is looking at, close enough to stop (0 = none). Any machine: from
    /// the drawn customers. Something solid in between hides them.
    /// </summary>
    public ushort CustomerInView(Camera cam, out float distance)
    {
        distance = 0f;
        if (cam == null) return 0;
        Vector3 eye = cam.transform.position, fwd = cam.transform.forward;
        // Who's on the crosshair line, from plain numbers (no transforms); the nearest few then get a
        // line-of-sight check, nearest first, until one isn't behind something.
        inView.Clear();
        float range2 = stopRange * stopRange;
        foreach (var kv in views)
        {
            var v = kv.Value;
            if (ReferenceEquals(v, null)) continue; // a plain check: destroyed views are taken out of the list
            Vector3 to = v.Shown + Vector3.up * 1f - eye;
            if (to.sqrMagnitude > range2) continue;
            float along = Vector3.Dot(to, fwd);
            if (along <= 0f || along > stopRange) continue;
            if ((to - fwd * along).sqrMagnitude > 0.45f * 0.45f) continue; // how far from the crosshair line
            inView.Add((kv.Key, along));
        }
        if (inView.Count == 0) return 0;
        inView.Sort((a, b) => a.along.CompareTo(b.along));
        for (int i = 0; i < inView.Count && i < 4; i++)
        {
            var (id, along) = inView[i];
            Vector3 body = eye + fwd * along; // close enough to their middle for "is something in the way"
            if (Physics.Linecast(eye, body, out var hit, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) &&
                hit.distance < along - 0.4f) continue;
            distance = along;
            return id;
        }
        return 0;
    }

    private readonly List<(ushort id, float along)> inView = new();

    /// <summary>Any player: stop the customer they're looking at and check their bag.</summary>
    public void RequestStop(ushort id) => StopCustomerRpc(id);

    [Rpc(SendTo.Server)]
    private void StopCustomerRpc(ushort id, RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        if (!byId.TryGetValue(id, out var c)) return;
        var player = NetworkManager.ConnectedClients.TryGetValue(sender, out var client) ? client.PlayerObject : null;
        if (player == null || (player.transform.position - c.pos).sqrMagnitude > (stopRange + 1f) * (stopRange + 1f)) return;
        if (HelpLostChild(c, sender)) return; // a lost child: they follow the player now
        if (Time.time - c.stoppedAt < 5f) return;
        c.stoppedAt = Time.time;
        var notes = NotificationManager.Instance;

        // Caught: they stop, drop what they took (as crates of stock at their feet, for the store to pick
        // up and put back), then run for the exit.
        var taken = c.stolen.Count > 0 ? c.stolen : c.fleeing ? c.basket : null;
        if (taken != null && taken.Count > 0)
        {
            string what = StolenText(c);
            var plot = c.robbed ?? c.store;
            int dropped = DropStolen(c, taken);
            taken.Clear();
            Log(c, $"CAUGHT by client {sender}: {what}, {dropped} dropped");
            // A help point only for helping another team: catching your own store's thief is just business.
            bool helped = plot != null && plot.HasOwner && TeamOf(sender) != plot.OwnerTeamId;
            if (helped && HelpPoints.Instance != null) HelpPoints.Instance.ServerAward(sender, HelpPoints.HelpAction.StoppedThief);
            string where = plot != null ? $" from {plot.DisplayName}" : "";
            if (notes != null)
            {
                notes.ServerNotifyClientText(sender, "You caught a shoplifter!",
                    $"They had {what}{where}. They dropped it: pick it up and put it back on the shelves.");
                if (helped)
                    notes.ServerNotifyTeamText(plot.OwnerTeamId, "Shoplifter caught",
                        $"Another player stopped someone leaving {plot.DisplayName} with {what}. It's on the floor where they were caught.");
            }
            c.fleeing = false;
            c.robbed = null;
            if (c.store != null) { AbandonStore(c); c.store = null; }
            DropFromGroup(c); // caught: on their own now
            React(c, Reaction.Caught);
            // Stopped where they are for a moment...
            c.mode = Mode.Caught;
            c.corners.Clear();
            c.corner = 0;
            c.arrived = true;
            c.until = Time.time + caughtStopSeconds;
            c.nextThink = c.until;
            return;
        }

        // Nothing on them: an honest customer, and now an annoyed one.
        c.satisfaction -= 0.5f;
        Log(c, $"stopped by client {sender}: had nothing (annoyed)");
        if (notes != null) notes.ServerNotifyClientText(sender, "Nothing on them", "That customer hadn't taken anything, and they're not happy about being stopped.");
    }

    private static ulong TeamOf(ulong clientId) => MallManager.GetTeamOf(clientId);

    /* ========================= TRAPPED IN A STORE ========================= */

    // Keeps failing to find a way anywhere while standing in a store: something's blocking the way out.
    // They say so (and the team hears), get unhappier, and keep trying; after a while they're let out at
    // the door so a glitch can't hold them forever. False if they're not in a store (stuck elsewhere).
    private bool Trapped(Customer c)
    {
        var plot = c.store != null ? c.store : MallManager.Instance != null ? MallManager.Instance.GetPlotAt(c.pos + Vector3.up * 0.1f) : null;
        if (plot == null) return false;
        c.failures = 0; // keep trying (the way may be cleared)
        if (c.trappedSince < 0f)
        {
            c.trappedSince = Time.time;
            Log(c, $"TRAPPED in '{plot.DisplayName}': {c.pathProblem}");
            React(c, Reaction.Trapped);
            if (plot.HasOwner && NotificationManager.Instance != null && (!trappedNoticeAt.TryGetValue(plot, out float quietUntil) || Time.time >= quietUntil))
            {
                trappedNoticeAt[plot] = Time.time + 30f; // once in a while per store, not per customer
                NotificationManager.Instance.ServerNotifyTeamText(plot.OwnerTeamId, $"Customer stuck in {plot.DisplayName}",
                    "Something's blocking their way out. Clear a path to the door.");
            }
        }
        c.satisfaction = Mathf.Min(c.satisfaction, 0f) - 0.1f;
        if (Time.time - c.trappedSince < trappedLetOutSeconds)
        {
            if (Random.value < 0.3f) React(c, Reaction.Trapped);
            c.nextThink = Time.time + 1.5f;
            return true;
        }

        // Still stuck: let out at the nearest door, very unhappy.
        Vector3 door = c.pos;
        float best = float.MaxValue;
        if (entrances.TryGetValue(plot, out var ways))
            foreach (var e in ways)
            {
                float d = (e.outside - c.pos).sqrMagnitude;
                if (d < best) { best = d; door = e.outside; }
            }
        if (best == float.MaxValue) { Log(c, "trapped, and no door to let them out of: gone"); Despawn(c); return true; }
        Log(c, $"let out of '{plot.DisplayName}' after {Time.time - c.trappedSince:0} s trapped");
        if (c.store != null)
        {
            c.satisfaction = -1f;
            AbandonStore(c);
            if (plot.HasOwner && ProgressionManager.Instance != null)
                ProgressionManager.Instance.ServerCustomerLeft(plot.OwnerTeamId, -1f);
            c.store = null;
        }
        else if (plot.HasOwner && ProgressionManager.Instance != null)
            ProgressionManager.Instance.ServerCustomerLeft(plot.OwnerTeamId, -1f);
        if (plot.HasOwner && ProgressionManager.Instance != null && trappedXpLoss > 0f)
        {
            ProgressionManager.Instance.ServerAddXp(plot.OwnerTeamId, -trappedXpLoss);
            if (NotificationManager.Instance != null)
                NotificationManager.Instance.ServerNotifyTeamText(plot.OwnerTeamId, $"Customer stuck in {plot.DisplayName}",
                $"They were stuck for {trappedLetOutSeconds / 60f:0.#} minutes and had to be let out: -{trappedXpLoss:0} XP. " +
                "Keep a way to the door clear.");
        }
        DropFromGroup(c);
        c.trappedSince = -1f;
        c.pos = door;
        c.corners.Clear();
        c.corner = 0;
        c.dirty = true;
        SnapRpc(c.id, door, PackYaw(c.yaw));
        c.leaveAt = 0f; // had enough: off home
        c.mode = Mode.Exiting;
        var exit = PickSpawnPoint(sp => sp.isExit);
        if (exit == null || !PathTo(c, exit.transform.position)) Despawn(c);
        return true;
    }

    private readonly Dictionary<StorePlot, float> trappedNoticeAt = new();

    // Moved, not walked (let out of a store): every machine puts them there at once.
    [Rpc(SendTo.Everyone)]
    private void SnapRpc(ushort id, Vector3 pos, byte yaw)
    {
        if (views.TryGetValue(id, out var v) && v != null) v.SetTarget(pos, UnpackYaw(yaw), snap: true);
    }

    // ...then off to the mall exit (still at a shoplifter's run).
    private void RunForExit(Customer c)
    {
        c.mode = Mode.Exiting;
        var exit = PickSpawnPoint(sp => sp.isExit);
        if (exit == null || !PathTo(c, exit.transform.position)) Despawn(c);
    }

    // What a caught shoplifter took, dropped at their feet: one crate per product and color (more if
    // it's more than a crate holds), part-filled with what they had. Returns how many products dropped.
    private int DropStolen(Customer c, List<Taken> taken)
    {
        var piles = new Dictionary<(int item, uint paint), int>();
        foreach (var t in taken)
        {
            if (t.loose != null || t.itemId == 0) continue; // a loose item was never picked up: still where it was
            piles.TryGetValue((t.itemId, t.paint), out int n);
            piles[(t.itemId, t.paint)] = n + Mathf.Max(1, t.units);
        }
        var catalog = ItemCatalog.Instance;
        int dropped = 0, crates = 0;
        foreach (var kv in piles)
        {
            if (catalog == null || !catalog.TryGet(kv.Key.item, out var def) || def.worldNetworkPrefab == null) continue;
            int perCrate = Mathf.Max(1, StockContainer.GetUnitsPerCrate(kv.Key.item));
            for (int left = kv.Value; left > 0; left -= perCrate)
            {
                // Spread around their feet, a little up so it settles onto the floor.
                float angle = crates * 2.4f;
                Vector3 at = c.pos + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * (crates == 0 ? 0.35f : 0.6f) + Vector3.up * 0.3f;
                var spawned = Instantiate(def.worldNetworkPrefab, at, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
                if (spawned.TryGetComponent<ItemPickupNet>(out var crate))
                {
                    if (crate.IsStockCrate) crate.ServerSetUnits(Mathf.Min(left, perCrate));
                    if (kv.Key.paint != 0) crate.ServerSetPaint(kv.Key.paint);
                }
                spawned.Spawn(true);
                dropped += Mathf.Min(left, perCrate);
                crates++;
            }
        }
        return dropped;
    }

    /* ========================= STORE ENTRANCES ========================= */

    private readonly List<Bounds> scratchDoors = new();

    // Why stores have no way in (for the test key's message).
    private readonly Dictionary<StorePlot, string> entranceProblems = new();
    private static readonly float[] DoorDepths = { 0.8f, 1.4f, 2.2f, 3.2f };

    // Each open store's ways in: a spot just outside and just inside each of its doors. Tries a few
    // depths either side of the door (store fronts vary in thickness, and a store's space may reach a
    // little into the hallway).
    private void RefreshEntrances()
    {
        nextEntrances = Time.time + 3f;
        entrances.Clear();
        entranceProblems.Clear();
        if (MallManager.Instance == null) return;
        foreach (var plot in MallManager.Instance.Plots)
        {
            if (plot == null || !plot.IsOpen) continue;
            plot.GetDoorBounds(scratchDoors);
            if (scratchDoors.Count == 0) { entranceProblems[plot] = "no doors found in its walls"; continue; }

            var list = new List<Entrance>();
            string problem = null;
            Vector3 middle = plot.GetWorldBounds().center;
            foreach (var d in scratchDoors)
            {
                Vector3 inward = middle - d.center;
                inward.y = 0f;
                inward = inward.sqrMagnitude > 1e-4f ? inward.normalized : Vector3.forward;
                Vector3 foot = new(d.center.x, d.min.y + 0.1f, d.center.z);

                bool found = false, blocked = false;
                bool sawInside = false, sawOutside = false;
                foreach (float depth in DoorDepths)
                {
                    bool inOk = NavMesh.SamplePosition(foot + inward * depth, out var inHit, 1f, NavMesh.AllAreas) &&
                                plot.Contains(inHit.position + Vector3.up * 0.1f);
                    bool outOk = NavMesh.SamplePosition(foot - inward * depth, out var outHit, 1f, NavMesh.AllAreas) &&
                                 !plot.Contains(outHit.position + Vector3.up * 0.1f);
                    sawInside |= inOk;
                    sawOutside |= outOk;
                    if (!inOk || !outOk) continue;
                    // Through the doorway, not round the long way (or not at all: a shut door).
                    if (!NavMesh.CalculatePath(outHit.position, inHit.position, NavMesh.AllAreas, path) ||
                        path.status != NavMeshPathStatus.PathComplete || PathLength(path) > depth * 2f + 4f)
                    {
                        blocked = true;
                        continue;
                    }
                    list.Add(new Entrance { outside = outHit.position, inside = inHit.position });
                    found = true;
                    break;
                }
                if (!found)
                    problem ??= blocked ? $"its door at {foot:F1} can't be walked through (locked, staff-only, or blocked)"
                              : $"its door at {foot:F1} has no walkable floor {(sawInside ? "outside it (in the hallway)" : sawOutside ? "inside it (in the store's space)" : "on either side")}";
            }
            if (list.Count > 0) entrances[plot] = list;
            else entranceProblems[plot] = problem ?? "no usable doors";
        }
    }

    private static float PathLength(NavMeshPath p)
    {
        float sum = 0f;
        var corners = p.corners;
        for (int i = 1; i < corners.Length; i++) sum += Vector3.Distance(corners[i - 1], corners[i]);
        return sum;
    }

    // Why no customer can get into any store (for the test key's message).
    private string WhyNoStores()
    {
        if (MallManager.Instance == null) return "no MallManager";
        var parts = new List<string>();
        bool anyOpen = false;
        foreach (var plot in MallManager.Instance.Plots)
        {
            if (plot == null || !plot.IsOpen) continue;
            anyOpen = true;
            if (entranceProblems.TryGetValue(plot, out var why)) parts.Add($"'{plot.DisplayName}': {why}");
        }
        if (!anyOpen) return "no store is open";
        return parts.Count > 0 ? string.Join("; ", parts) : "every open store was already passed";
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
    private void EveryoneRpc(ushort[] ids, byte[] lookIds, Vector3[] positions, byte[] yaws, byte[] bags, RpcParams rpcParams)
    {
        ReceiveArrivals(ids, lookIds, positions, yaws);
        for (int i = 0; i < ids.Length && i < bags.Length; i++) if (bags[i] > 0) SetBags(ids[i], bags[i]);
    }

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
            var bags = new byte[n];
            for (int k = 0; k < n; k++)
            {
                var c = customers[start + k];
                ids[k] = c.id;
                lookIds[k] = c.look;
                positions[k] = c.pos;
                yaws[k] = PackYaw(c.yaw);
                bags[k] = c.bags;
            }
            EveryoneRpc(ids, lookIds, positions, yaws, bags, RpcTarget.Single(clientId, RpcTargetUse.Temp));
        }
        foreach (var c in customers) // lost children and their families: marked for them too
            if (c.mark != 0) MarkRpc(c.id, c.mark, RpcTarget.Single(clientId, RpcTargetUse.Temp));
        SendCases(clientId);
    }

    private void MakeView(ushort id, byte look, Vector3 pos, float yaw)
    {
        if (views.TryGetValue(id, out var old) && old != null) { old.SetTarget(pos, yaw, snap: true); return; }
        bool child = (look & ChildLook) != 0;
        bool childModel = child && (look & ChildModel) != 0; // one of the Child Looks (already child-sized)
        int index = look & (childModel ? 0x3F : 0x7F);
        var list = childModel ? childLooks : looks;
        var prefab = index < list.Count ? list[index] : null;
        // At the scene root, not under one parent: Unity updates separate root hierarchies in parallel,
        // and moving hundreds of children of one parent is slower.
        views[id] = CustomerView.Create(id, prefab, pos, yaw, null, child && !childModel ? childScale : 1f);
        views[id].IsChild = child;
    }

    private void RemoveView(ushort id)
    {
        if (views.TryGetValue(id, out var v) && v != null) Destroy(v.gameObject);
        views.Remove(id);
        marks.Remove(id);
        ForgetCasesOf(id);
    }

    /* ========================= DEBUG ========================= */

    private void Log(Customer c, string what)
    {
        if (logDecisions) Debug.Log($"[Customers] #{c.id} {what}", this);
    }

    private void OnDrawGizmos()
    {
        if (!Application.isPlaying) return;
        foreach (var c in customers)
        {
            bool trouble = c.stuckRepaths > 0 || c.failures > 0;
            if (drawPaths && !c.arrived)
            {
                Gizmos.color = trouble ? Color.red : c.store != null ? Color.green : Color.cyan;
                Vector3 from = c.pos + Vector3.up * 0.1f;
                for (int i = c.corner; i < c.corners.Count; i++)
                {
                    Vector3 to = (i == c.corner && c.laneCorner == c.corner ? c.laneTarget : c.corners[i]) + Vector3.up * 0.1f;
                    Gizmos.DrawLine(from, to);
                    from = to;
                }
                Gizmos.DrawWireSphere(c.destination + Vector3.up * 0.1f, 0.2f);
            }
#if UNITY_EDITOR
            if (drawLabels)
            {
                string goal = c.goalStore != null ? c.goalStore.DisplayName : c.mode == Mode.Exiting ? "exit" : "-";
                string text = $"#{c.id} {c.mode}{(c.store != null ? $" in {c.store.DisplayName}" : "")}\n→ {goal}" +
                              (c.stuckRepaths > 0 ? $"\nSTUCK x{c.stuckRepaths}" : "") +
                              (c.failures > 0 && c.pathProblem != null ? $"\n{c.pathProblem}" : "");
                var style = new GUIStyle(UnityEditor.EditorStyles.miniLabel)
                {
                    normal = { textColor = trouble ? Color.red : Color.white },
                };
                UnityEditor.Handles.Label(c.pos + Vector3.up * 2.1f, text, style);
            }
#endif
        }
    }
}
