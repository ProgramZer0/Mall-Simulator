using System;
using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// A checkout counter, worked in the world (no menus). Customers' things roll up a conveyor belt; a
/// player of the store's team clicks each one to scan it and it appears on the register screen with
/// the running total. Then the customer pays:
/// - Card: they put their card on the counter. Click it to take it, then click the card reader.
/// - Cash: they put cash on the counter. Click it to take it (it's laid out so you can count it).
///   The cash drawer pops open when you take the cash or press Enter (or click Drawer Toggle).
///   Optionally type what they gave on the keypad (2 5 0 0 = $25.00) and press Enter: the screen
///   shows the change due. Click the drawer's bills and coins to count out the change (it's laid
///   out on the counter, exactly the bills and coins you took; click one to put it back in the
///   drawer), then press Confirm (the drawer shuts). Too little change and they wait;
///   too much is lost.
/// The money goes to the store's team. Everyone sees the same screen, belt, cash and change.
///
/// Things too big for the belt go on as a price tag; things that fit but have no room yet wait.
///
/// Prefab setup (a catalog item, placed and moved in edit mode like shelves):
/// - NetworkObject + ItemPickupNet (Edit Mode Only, Physics Mode Frozen) + this, with colliders.
/// - Belt: child BoxCollider sitting ON the belt surface (its bottom face is the surface things ride
///   on; make it as tall as Max Item Height), blue arrow (+Z) toward the scanner end.
/// - Clickable parts: child objects with colliders and a CheckoutClickable: keypad keys (0-9, 00,
///   Clear, Enter), drawer slots (one per bill/coin, Value in cents), Clear Change, Confirm, Card Reader,
///   and Drawer Toggle (a button or the drawer's front). Put the drawer slots on the drawer itself
///   so they move with it.
/// - Drawer Motion (optional): a DoorMotion set to Slide whose leaf is the drawer; it opens and
///   shuts with the drawer state.
/// - Screen: a world-space Canvas on the register with TMP texts for Items, Total and Info.
/// - Spots (empty Transforms on the counter top): Customer Spot (where they put their cash or card),
///   Counting Spot (cash you've taken), Change Spot (change you're counting out).
/// - Optional models: Money Prefabs (one per bill/coin) and Card Prefab; plain labelled ones otherwise.
///
/// Testing: the host presses Test Key (F6) near a counter to send a test customer.
/// </summary>
public class CheckoutCounter : NetworkBehaviour
{
    public enum Phase : byte { Scanning, AwaitingPayment }
    public enum PayMethod : byte { Cash, Card }

    /// <summary>One thing on the belt.</summary>
    public struct BeltItem : INetworkSerializeByMemcpy, IEquatable<BeltItem>
    {
        public int key;
        public int itemId;
        public int priceCents;
        public int units;
        public int customer;
        public byte whole;  // 1: a whole item (sold loose); 0: one product off a shelf
        public byte tag;    // 1: too big for the belt, shown as a price tag

        public bool Equals(BeltItem o) => key == o.key && itemId == o.itemId && priceCents == o.priceCents &&
                                          units == o.units && customer == o.customer && whole == o.whole && tag == o.tag;
        public override bool Equals(object obj) => obj is BeltItem o && Equals(o);
        public override int GetHashCode() => key;
    }

    /// <summary>A line on the register screen.</summary>
    public struct Line : INetworkSerializeByMemcpy, IEquatable<Line>
    {
        public int itemId;
        public int priceCents;
        public bool Equals(Line o) => itemId == o.itemId && priceCents == o.priceCents;
        public override bool Equals(object obj) => obj is Line o && Equals(o);
        public override int GetHashCode() => itemId * 397 ^ priceCents;
    }

    /// <summary>The current customer: being scanned, or paying.</summary>
    public struct State : INetworkSerializeByMemcpy, IEquatable<State>
    {
        public Phase phase;
        public PayMethod method;
        public byte moneyTaken;    // the cashier picked up the card or cash
        public byte keypadEntered; // Enter pressed: the screen shows the change
        public byte drawerOpen;
        public int customer;
        public int totalCents;
        public int tenderedCents;  // cash handed over
        public int keypadCents;    // typed on the keypad
        public int changeCents;    // change counted out so far

        public int ChangeDueCents => Mathf.Max(0, tenderedCents - totalCents);

        public bool Equals(State o) => phase == o.phase && method == o.method && moneyTaken == o.moneyTaken &&
                                       keypadEntered == o.keypadEntered && drawerOpen == o.drawerOpen && customer == o.customer && totalCents == o.totalCents &&
                                       tenderedCents == o.tenderedCents && keypadCents == o.keypadCents && changeCents == o.changeCents;
        public override bool Equals(object obj) => obj is State o && Equals(o);
        public override int GetHashCode() => customer * 397 ^ totalCents ^ changeCents * 31;
    }

    [Serializable]
    public class MoneyModel
    {
        [Tooltip("The bill or coin in cents (2000 = $20, 25 = a quarter).")]
        public int cents = 100;
        public GameObject prefab;
    }

    public static readonly List<CheckoutCounter> All = new();
    private static readonly int[] Denominations = { 10000, 5000, 2000, 1000, 500, 100, 25, 10, 5, 1 };

    [Header("Belt")]
    [Tooltip("BoxCollider sitting on the belt: its BOTTOM face is the surface things ride on. Blue arrow (+Z) toward the scanner.")]
    [SerializeField] private BoxCollider belt;
    [SerializeField] private float beltSpeed = 0.6f;
    [SerializeField] private float itemGap = 0.05f;
    [Tooltip("Taller than this (meters) and it goes on as a tag instead.")]
    [SerializeField] private float maxItemHeight = 0.6f;
    [Tooltip("What a too-big item shows as. Empty: a plain card.")]
    [SerializeField] private GameObject tagPrefab;
    [SerializeField] private float tagLength = 0.12f;

    [Header("Screen (world-space Canvas texts)")]
    [SerializeField] private TMP_Text screenItems;
    [SerializeField] private TMP_Text screenTotal;
    [SerializeField] private TMP_Text screenInfo;
    [Tooltip("How many scanned lines the screen shows (the latest).")]
    [SerializeField] private int screenLines = 8;

    [Header("Spots on the counter (empty Transforms)")]
    [Tooltip("Where the customer puts their cash or card ON the counter (not where they stand).")]
    [SerializeField] private Transform customerSpot;
    [Tooltip("Where cash you've taken is laid out to count.")]
    [SerializeField] private Transform countingSpot;
    [Tooltip("Where the change you count out is laid out.")]
    [SerializeField] private Transform changeSpot;

    [Header("Cash drawer")]
    [Tooltip("Optional: a DoorMotion (Slide) whose leaf is the drawer. It opens and shuts with the drawer.")]
    [SerializeField] private DoorMotion drawerMotion;

    [Header("Money models (optional)")]
    [SerializeField] private List<MoneyModel> moneyPrefabs = new();
    [SerializeField] private GameObject cardPrefab;

    [Header("Testing")]
    [Tooltip("Host: sends a test customer to the nearest counter within Test Range.")]
    [SerializeField] private KeyCode testKey = KeyCode.F6;
    [SerializeField] private float testRange = 8f;
    [SerializeField] private int testMaxItems = 6;

    private NetworkList<BeltItem> items;
    private NetworkList<Line> lines;
    private readonly List<ProgressionManager.Sold> sale = new(); // host: what's been scanned, for profit
    private NetworkList<int> change; // the bills and coins counted out, in cents, in the order taken
    private readonly NetworkVariable<State> state = new();

    // Server
    private readonly List<BeltItem> waiting = new();
    private int nextKey = 1, nextCustomer = 1;
    private float nextFill;

    // Local
    private string message;
    private float messageUntil;
    private bool screenDirty = true, moneyDirty = true;

    public State Current => state.Value;

    /// <summary>Has a price on it (up for sale)? Then customers can't check out here.</summary>
    public bool IsForSale => TryGetComponent<Sellable>(out var s) && s.Price > 0;

    /// <summary>
    /// Could this counter be put up for sale? Not while someone's being served, and not if it would leave
    /// the store without a counter customers can use (so a store can't lock itself out of selling).
    /// Works before the counter is spawned too (a counter being put down in a store that prices them all).
    /// </summary>
    public static bool CanBePutForSale(CheckoutCounter counter, out string reason)
    {
        reason = null;
        if (counter == null) return true;
        if (counter.IsBusy) { reason = "Finish serving the customer first"; return false; }
        var plot = counter.Plot;
        foreach (var other in All)
            if (other != null && other != counter && !other.IsForSale && other.Plot == plot) return true;
        reason = "Your store needs a checkout counter that isn't for sale";
        return false;
    }
    public int ItemCount => IsSpawned ? items.Count : 0;
    public StorePlot Plot => MallManager.Instance != null ? MallManager.Instance.GetPlotAt(transform.position + Vector3.up * 0.1f) : null;
    public bool CanUse(ulong clientId) { var p = Plot; return p != null && p.IsAuthorized(clientId); }

    /// <summary>Anyone still being served or waiting (edit mode waits until it's done).</summary>
    public bool IsBusy => IsSpawned && (items.Count > 0 || lines.Count > 0 || waiting.Count > 0);

    private void Awake()
    {
        items = new NetworkList<BeltItem>(null, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        lines = new NetworkList<Line>(null, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        change = new NetworkList<int>(null, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        if (belt != null) belt.isTrigger = true;
    }

    public override void OnNetworkSpawn()
    {
        if (belt == null)
            Debug.LogError($"[CheckoutCounter] '{name}' has no Belt set, so nothing can go on it. Drag the belt's " +
                           "BoxCollider into Belt on the counter prefab.", this);
        All.Add(this);
        lines.OnListChanged += _ => screenDirty = true;
        change.OnListChanged += _ => moneyDirty = true;
        state.OnValueChanged += (_, now) =>
        {
            screenDirty = true;
            moneyDirty = true;
            if (drawerMotion != null) drawerMotion.SetOpen(now.drawerOpen != 0);
        };
        screenDirty = moneyDirty = true;
        if (drawerMotion != null) drawerMotion.SetOpen(state.Value.drawerOpen != 0, 1f, true);
    }

    public override void OnNetworkDespawn()
    {
        All.Remove(this);
        ClearModels();
        ClearMoney();
    }

    public override void OnDestroy()
    {
        ClearModels();
        ClearMoney();
        base.OnDestroy();
    }

    /* ========================= SIZES ========================= */

    private struct Shape
    {
        public GameObject model;
        public Quaternion rotation;   // relative to the belt (long side along it)
        public Vector3 extents;       // size across, up and along the belt (meters)
        public Vector3 center;        // bounds centre in the model's own space, scaled
    }

    private static readonly Dictionary<(int, bool), Shape> Shapes = new();

    private static bool TryGetShape(int itemId, bool whole, out Shape shape)
    {
        if (Shapes.TryGetValue((itemId, whole), out shape)) return shape.model != null;
        shape = default;
        if (ItemCatalog.Instance == null || !ItemCatalog.Instance.TryGet(itemId, out var def) || def.worldNetworkPrefab == null)
        {
            Shapes[(itemId, whole)] = shape;
            return false;
        }

        // One product off a shelf looks like its shelf model; a whole item looks like itself.
        GameObject model = def.worldNetworkPrefab.gameObject;
        if (!whole && def.worldNetworkPrefab.TryGetComponent<ItemPickupNet>(out var crate) && crate.IsStockCrate)
            model = crate.ShelfPrefab != null ? crate.ShelfPrefab : crate.HangPrefab != null ? crate.HangPrefab : model;

        Bounds b = PlacementCheck.ComputeLocalBounds(model);
        Vector3 s = model.transform.localScale;
        s = new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
        Vector3 size = Vector3.Scale(b.size, s);
        Quaternion own = model.transform.localRotation; // e.g. Blender's -90 on X

        Vector3 flat = Extents(own, size);
        Quaternion turn = flat.x > flat.z ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.identity;
        shape = new Shape { model = model, rotation = turn * own, extents = Extents(turn * own, size), center = Vector3.Scale(b.center, s) };
        Shapes[(itemId, whole)] = shape;
        return true;
    }

    private static Vector3 Extents(Quaternion q, Vector3 size)
    {
        Vector3 r = q * new Vector3(size.x, 0f, 0f), u = q * new Vector3(0f, size.y, 0f), f = q * new Vector3(0f, 0f, size.z);
        return new Vector3(Mathf.Abs(r.x) + Mathf.Abs(u.x) + Mathf.Abs(f.x),
                           Mathf.Abs(r.y) + Mathf.Abs(u.y) + Mathf.Abs(f.y),
                           Mathf.Abs(r.z) + Mathf.Abs(u.z) + Mathf.Abs(f.z));
    }

    private void GetBelt(out Vector3 front, out Vector3 forward, out Vector3 up, out float length, out float width)
    {
        Transform t = belt.transform;
        Vector3 s = t.lossyScale;
        length = Mathf.Abs(belt.size.z * s.z);
        width = Mathf.Abs(belt.size.x * s.x);
        float height = Mathf.Abs(belt.size.y * s.y);
        forward = t.forward;
        up = t.up;
        front = t.TransformPoint(belt.center) + forward * (length * 0.5f) - up * (height * 0.5f); // the box's bottom = the belt surface
    }

    private bool IsTooBig(int itemId, bool whole)
    {
        if (belt == null || !TryGetShape(itemId, whole, out var shape)) return true;
        GetBelt(out _, out _, out _, out float length, out float width);
        return shape.extents.x > width || shape.extents.z > length || shape.extents.y > maxItemHeight;
    }

    private float LengthOf(BeltItem item)
    {
        if (item.tag != 0) return tagLength;
        return TryGetShape(item.itemId, item.whole != 0, out var shape) ? shape.extents.z : tagLength;
    }

    public static string NameOf(int itemId) =>
        ItemCatalog.Instance != null && ItemCatalog.Instance.TryGet(itemId, out var def) ? def.displayName : $"Item {itemId}";

    /// <summary>Money as $12.50 (zero as $0.00).</summary>
    public static string FormatAmount(int cents) => $"${cents / 100}.{Mathf.Abs(cents % 100):00}";

    /* ========================= CUSTOMERS (SERVER) ========================= */

    /// <summary>One thing a customer brings to the counter.</summary>
    public struct Purchase
    {
        public int itemId;
        public int priceCents;
        public int units;
        public bool whole;
    }

    /// <summary>
    /// Server: a customer brings these to the counter. They go on the belt as room allows (too-big ones
    /// as tags), after anyone already waiting. Returns the customer number.
    /// </summary>
    public int ServerAddCustomer(IList<Purchase> purchases)
    {
        if (!IsServer || purchases == null || purchases.Count == 0) return 0;
        if (IsForSale) return 0; // a counter up for sale isn't open to customers
        int customer = nextCustomer++;
        foreach (var p in purchases)
            waiting.Add(new BeltItem
            {
                key = nextKey++,
                itemId = p.itemId,
                priceCents = Mathf.Max(0, p.priceCents),
                units = Mathf.Max(1, p.units),
                customer = customer,
                whole = (byte)(p.whole ? 1 : 0),
                tag = (byte)(IsTooBig(p.itemId, p.whole) ? 1 : 0),
            });
        ServerFill();
        return customer;
    }

    private void ServerFill()
    {
        if (belt == null) return;
        GetBelt(out _, out _, out _, out float length, out _);
        float used = 0f;
        foreach (var it in items) used += LengthOf(it) + itemGap;

        while (waiting.Count > 0)
        {
            float need = LengthOf(waiting[0]);
            if (items.Count > 0 && used + need > length) break;
            items.Add(waiting[0]);
            used += need + itemGap;
            waiting.RemoveAt(0);
        }

        if (state.Value.customer == 0 && items.Count > 0)
            state.Value = new State { phase = Phase.Scanning, customer = items[0].customer };
    }

    private void Update()
    {
        if (!IsSpawned) return;
        if (belt != null) UpdateModels();
        if (moneyDirty) RebuildMoney();
        if (screenDirty || (messageUntil > 0f && Time.time > messageUntil)) RefreshScreen();

        if (!IsServer) return;
        if (Time.time >= nextFill)
        {
            nextFill = Time.time + 0.25f;
            ServerFill();
        }
        if (testKey != KeyCode.None && Input.GetKeyDown(testKey) && !UIState.IsMenuOpen && IsNearestToHost())
            ServerAddTestCustomer();
    }

    /* ========================= CLICKS ========================= */

    /// <summary>What's going on at the counter, for a player looking at it (null: not their store).</summary>
    public string StatusFor(ulong clientId)
    {
        if (!IsSpawned || !CanUse(clientId)) return null;
        if (IsForSale && !IsBusy) return "For sale: customers can't use this counter";
        var s = state.Value;
        if (s.phase == Phase.AwaitingPayment)
            return s.method == PayMethod.Card
                ? (s.moneyTaken == 0 ? "Paying by card: take the card" : "Paying by card: swipe it in the card reader")
                : (s.moneyTaken == 0 ? "Paying cash: take the cash" : "Paying cash: count out the change from the drawer, then Confirm");
        if (items.Count > 0) return "Click the things on the belt to scan them";
        return $"No customers{(IsServer && testKey != KeyCode.None ? $"   {testKey}: test customer" : "")}";
    }

    /// <summary>What clicking this part would do (for the prompt); null if nothing right now.</summary>
    public string PromptFor(CheckoutClickable part, ulong clientId)
    {
        if (!IsSpawned || !CanUse(clientId)) return null;
        var s = state.Value;
        bool paying = s.phase == Phase.AwaitingPayment;
        bool cash = paying && s.method == PayMethod.Cash;

        switch (part.action)
        {
            case CheckoutClickable.Action.ScanItem:
                foreach (var it in items)
                    if (it.key == part.value)
                        return it.customer == s.customer && !paying
                            ? $"Click: scan {NameOf(it.itemId)}{(it.tag != 0 ? " (tag)" : "")} {FormatAmount(it.priceCents)}"
                            : "Next customer's";
                return null;
            case CheckoutClickable.Action.CustomerMoney:
                return paying && s.moneyTaken == 0 ? $"Click: take the {(cash ? "cash" : "card")}" : null;
            case CheckoutClickable.Action.CardReader:
                return paying && !cash ? (s.moneyTaken != 0 ? "Click: swipe the card" : "Take the card first") : null;
            case CheckoutClickable.Action.Drawer:
                return !cash ? null : s.drawerOpen != 0 ? $"Click: {FormatAmount(part.value)} as change" : "Open the drawer first";
            case CheckoutClickable.Action.DrawerToggle:
                return s.drawerOpen != 0 ? "Click: close the drawer" : "Click: open the drawer";
            case CheckoutClickable.Action.ClearChange:
                return cash && s.changeCents > 0 ? "Click: put the change back" : null;
            case CheckoutClickable.Action.ReturnChange:
                return !cash ? null : s.drawerOpen != 0 ? $"Click: put the {FormatAmount(part.value)} back in the drawer" : "Open the drawer first";
            case CheckoutClickable.Action.Confirm:
                return cash ? (s.moneyTaken != 0 ? $"Click: confirm (change {FormatAmount(s.changeCents)})" : "Take the cash first") : null;
            case CheckoutClickable.Action.KeypadDigit:
                return paying ? $"Click: {part.value}" : null;
            case CheckoutClickable.Action.KeypadDoubleZero:
                return paying ? "Click: 00" : null;
            case CheckoutClickable.Action.KeypadClear:
                return paying ? "Click: clear" : null;
            case CheckoutClickable.Action.KeypadEnter:
                return paying ? "Click: enter" : null;
        }
        return null;
    }

    /// <summary>The player clicked a part of this counter.</summary>
    public void Click(CheckoutClickable part)
    {
        if (part != null) RequestClickRpc(part.action, part.value);
    }

    [Rpc(SendTo.Server)]
    private void RequestClickRpc(CheckoutClickable.Action action, int value, RpcParams rpcParams = default)
    {
        if (!CanUse(rpcParams.Receive.SenderClientId)) return;
        var s = state.Value;
        bool paying = s.phase == Phase.AwaitingPayment;
        bool cash = paying && s.method == PayMethod.Cash;

        switch (action)
        {
            case CheckoutClickable.Action.ScanItem:
                if (!paying) ServerScan(value);
                return;
            case CheckoutClickable.Action.CustomerMoney:
                if (paying && s.moneyTaken == 0)
                {
                    s.moneyTaken = 1;
                    if (cash) s.drawerOpen = 1; // the till pops open for the cash
                    state.Value = s;
                }
                return;
            case CheckoutClickable.Action.DrawerToggle:
                s.drawerOpen = (byte)(s.drawerOpen != 0 ? 0 : 1);
                state.Value = s;
                return;
            case CheckoutClickable.Action.CardReader:
                if (paying && !cash && s.moneyTaken != 0) Complete(s.totalCents, "Card approved");
                return;
            case CheckoutClickable.Action.Drawer:
                if (cash && s.drawerOpen != 0 && value > 0 && s.changeCents < 1000000 && change.Count < 60)
                {
                    change.Add(value);
                    s.changeCents += value;
                    state.Value = s;
                }
                return;
            case CheckoutClickable.Action.ClearChange:
                if (cash)
                {
                    change.Clear();
                    s.changeCents = 0;
                    state.Value = s;
                }
                return;
            case CheckoutClickable.Action.ReturnChange:
                if (!cash || s.drawerOpen == 0) return;
                for (int i = change.Count - 1; i >= 0; i--)
                    if (change[i] == value)
                    {
                        change.RemoveAt(i);
                        s.changeCents -= value;
                        state.Value = s;
                        break;
                    }
                return;
            case CheckoutClickable.Action.Confirm:
                if (!cash) return;
                if (s.moneyTaken == 0) { MessageRpc("Take the cash first"); return; }
                if (s.changeCents < s.ChangeDueCents) { MessageRpc("Not enough change"); return; }
                int kept = s.tenderedCents - s.changeCents;
                Complete(kept, s.changeCents > s.ChangeDueCents ? $"Sold, but {FormatAmount(s.changeCents - s.ChangeDueCents)} too much change" : "Sold");
                return;
            case CheckoutClickable.Action.KeypadDigit:
            case CheckoutClickable.Action.KeypadDoubleZero:
                if (!paying || s.keypadCents > 9999999) return;
                if (s.keypadEntered != 0) { s.keypadCents = 0; s.keypadEntered = 0; } // typing again starts over
                s.keypadCents = action == CheckoutClickable.Action.KeypadDoubleZero ? s.keypadCents * 100 : s.keypadCents * 10 + Mathf.Clamp(value, 0, 9);
                state.Value = s;
                return;
            case CheckoutClickable.Action.KeypadClear:
                if (paying) { s.keypadCents = 0; s.keypadEntered = 0; state.Value = s; }
                return;
            case CheckoutClickable.Action.KeypadEnter:
                if (paying)
                {
                    s.keypadEntered = 1;
                    if (cash) s.drawerOpen = 1; // like a real till
                    state.Value = s;
                }
                return;
        }
    }

    private void ServerScan(int key)
    {
        var s = state.Value;
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it.key != key) continue;
            if (s.customer == 0) s.customer = it.customer;
            if (it.customer != s.customer) return; // the next customer's: this one pays first
            items.RemoveAt(i);
            lines.Add(new Line { itemId = it.itemId, priceCents = it.priceCents });
            sale.Add(new ProgressionManager.Sold { itemId = it.itemId, products = Mathf.Max(1, it.units), priceCents = it.priceCents });
            s.totalCents += it.priceCents;

            // That was this customer's last thing: they pay.
            if (!HasMoreFor(s.customer))
            {
                s.phase = Phase.AwaitingPayment;
                s.method = UnityEngine.Random.value < 0.5f ? PayMethod.Cash : PayMethod.Card;
                s.tenderedCents = s.method == PayMethod.Cash ? CashHandedOver(s.totalCents) : 0;
            }
            state.Value = s;
            ServerFill();
            return;
        }
    }

    private bool HasMoreFor(int customer)
    {
        foreach (var it in items) if (it.customer == customer) return true;
        foreach (var it in waiting) if (it.customer == customer) return true;
        return false;
    }

    // The exact amount now and then, otherwise the total rounded up to the next $5, $10 or $20 (or more).
    private static int CashHandedOver(int total)
    {
        if (total <= 0) return 0;
        if (UnityEngine.Random.value < 0.1f) return total;
        var options = new List<int>();
        foreach (int bill in new[] { 500, 1000, 2000, 5000, 10000 })
        {
            int amount = (total + bill - 1) / bill * bill;
            if (amount == total) amount += bill;
            if (!options.Contains(amount)) options.Add(amount);
            if (options.Count == 3) break;
        }
        return options[UnityEngine.Random.Range(0, options.Count)];
    }

    private void Complete(int receivedCents, string text)
    {
        var plot = Plot;
        var econ = EconomyManager.Instance;
        if (plot != null && plot.HasOwner && econ != null) econ.ServerAddCents(plot.OwnerTeamId, receivedCents);
        if (plot != null && ProgressionManager.Instance != null) ProgressionManager.Instance.ServerRecordSale(plot, sale, receivedCents);
        sale.Clear();
        lines.Clear();
        change.Clear();
        state.Value = new State { phase = Phase.Scanning, customer = items.Count > 0 ? items[0].customer : 0 };
        MessageRpc($"{text}: {FormatAmount(receivedCents)}");
        ServerFill();
    }

    [Rpc(SendTo.Everyone)]
    private void MessageRpc(string text)
    {
        message = text;
        messageUntil = Time.time + 3f;
        screenDirty = true;
    }

    /* ========================= SCREEN ========================= */

    private void RefreshScreen()
    {
        screenDirty = false;
        if (messageUntil > 0f && Time.time > messageUntil) { message = null; messageUntil = 0f; }
        var s = state.Value;

        if (screenItems != null)
        {
            var sb = new System.Text.StringBuilder();
            int start = Mathf.Max(0, lines.Count - Mathf.Max(1, screenLines));
            for (int i = start; i < lines.Count; i++)
                sb.Append(NameOf(lines[i].itemId)).Append("  ").Append(FormatAmount(lines[i].priceCents)).Append('\n');
            screenItems.text = sb.ToString().TrimEnd();
        }
        if (screenTotal != null) screenTotal.text = $"Total {FormatAmount(s.totalCents)}";

        if (screenInfo != null)
        {
            string info;
            if (!string.IsNullOrEmpty(message)) info = message;
            else if (s.phase == Phase.Scanning) info = items.Count > 0 ? "Scan items" : "Next customer, please";
            else if (s.method == PayMethod.Card) info = s.moneyTaken != 0 ? "Swipe the card" : "Card";
            else if (s.keypadEntered != 0)
                info = s.keypadCents >= s.totalCents
                    ? $"Paid {FormatAmount(s.keypadCents)}   Change {FormatAmount(s.keypadCents - s.totalCents)}"
                    : $"Paid {FormatAmount(s.keypadCents)}   Short {FormatAmount(s.totalCents - s.keypadCents)}";
            else if (s.keypadCents > 0) info = $"Paid {FormatAmount(s.keypadCents)}";
            else info = "Cash";
            screenInfo.text = info;
        }
    }

    /* ========================= THINGS ON THE BELT (EVERY MACHINE) ========================= */

    private class Shown
    {
        public GameObject go;
        public float offset;
    }

    private readonly Dictionary<int, Shown> shown = new();
    private readonly HashSet<int> present = new();

    private void UpdateModels()
    {
        GetBelt(out Vector3 front, out Vector3 forward, out Vector3 up, out float length, out _);
        Quaternion beltRot = Quaternion.LookRotation(forward, up);
        float target = 0f, step = beltSpeed * Time.deltaTime;
        present.Clear();

        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            present.Add(it.key);
            float len = LengthOf(it);
            if (!shown.TryGetValue(it.key, out var s))
            {
                s = new Shown { go = MakeItemModel(it), offset = Mathf.Max(target, length - len) }; // comes in at the back
                shown[it.key] = s;
            }
            s.offset = Mathf.Max(target, Mathf.MoveTowards(s.offset, target, step));
            Place(s, it, front, forward, up, beltRot, len);
            target = s.offset + len + itemGap;
        }

        if (shown.Count == present.Count) return;
        var gone = new List<int>();
        foreach (var kv in shown) if (!present.Contains(kv.Key)) gone.Add(kv.Key);
        foreach (int key in gone)
        {
            if (shown[key].go != null) Destroy(shown[key].go);
            shown.Remove(key);
        }
    }

    private static void Place(Shown s, BeltItem it, Vector3 front, Vector3 forward, Vector3 up, Quaternion beltRot, float len)
    {
        if (s.go == null) return;
        Vector3 middle = front - forward * (s.offset + len * 0.5f);
        if (it.tag != 0 || !TryGetShape(it.itemId, it.whole != 0, out var shape))
        {
            s.go.transform.SetPositionAndRotation(middle + up * 0.005f, beltRot);
            return;
        }
        Quaternion rot = beltRot * shape.rotation;
        Vector3 centre = middle + up * (shape.extents.y * 0.5f);
        s.go.transform.SetPositionAndRotation(centre - rot * shape.center, rot);
    }

    private GameObject MakeItemModel(BeltItem it)
    {
        GameObject go;
        if (it.tag == 0 && TryGetShape(it.itemId, it.whole != 0, out var shape))
            go = StockContainer.MakeVisual(shape.model, transform.position, transform.rotation, null);
        else if (tagPrefab != null)
            go = StockContainer.MakeVisual(tagPrefab, transform.position, transform.rotation, null);
        else
            go = MakeLabelled(PrimitiveType.Cube, new Vector3(0.08f, 0.004f, tagLength), Color.white, $"{NameOf(it.itemId)}\n{FormatAmount(it.priceCents)}");

        // Clicking it scans it.
        var click = go.AddComponent<CheckoutClickable>();
        click.action = CheckoutClickable.Action.ScanItem;
        click.value = it.key;
        click.Counter = this;
        if (go.GetComponentInChildren<Collider>() == null) go.AddComponent<BoxCollider>();
        return go;
    }

    private void ClearModels()
    {
        foreach (var s in shown.Values) if (s.go != null) Destroy(s.go);
        shown.Clear();
    }

    /* ========================= CASH, CARD AND CHANGE (EVERY MACHINE) ========================= */

    private readonly List<GameObject> money = new();

    // Lays out the customer's cash or card (on the counter, or taken) and the change counted out.
    private void RebuildMoney()
    {
        moneyDirty = false;
        ClearMoney();
        var s = state.Value;
        if (s.phase != Phase.AwaitingPayment) return;

        Transform where = s.moneyTaken == 0 ? Spot(customerSpot, 0.35f) : Spot(countingSpot, 0.15f);
        bool clickable = s.moneyTaken == 0;
        if (s.method == PayMethod.Card)
            Lay(new List<GameObject> { MakeCard() }, where, clickable ? CheckoutClickable.Action.CustomerMoney : null);
        else
            Lay(MakeCash(s.tenderedCents), where, clickable ? CheckoutClickable.Action.CustomerMoney : null);

        // The change: exactly the bills and coins taken from the drawer, biggest first; click one to put it back.
        if (s.method == PayMethod.Cash && change.Count > 0)
        {
            var given = new List<int>();
            foreach (int c in change) given.Add(c);
            given.Sort((a, b) => b.CompareTo(a));
            var models = new List<GameObject>();
            foreach (int c in given) models.Add(MakeBillOrCoin(c));
            Lay(models, Spot(changeSpot, -0.1f), CheckoutClickable.Action.ReturnChange, given);
        }
    }

    // A spot on the counter top (a fallback near the scanner end if it isn't set).
    private Transform Spot(Transform spot, float sideways)
    {
        if (spot != null) return spot;
        if (fallbackSpots.TryGetValue(sideways, out var t) && t != null) return t;
        var go = new GameObject($"[Spot {sideways}]");
        go.transform.SetParent(transform, false);
        if (belt != null)
        {
            // Beside the scanner end of the belt, a little apart from each other.
            GetBelt(out Vector3 front, out Vector3 forward, out Vector3 up, out _, out float width);
            Vector3 across = belt.transform.right * (width * 0.5f + 0.25f + sideways);
            go.transform.SetPositionAndRotation(front + forward * 0.1f + across, Quaternion.LookRotation(forward, up));
        }
        fallbackSpots[sideways] = go.transform;
        return go.transform;
    }

    private readonly Dictionary<float, Transform> fallbackSpots = new();

    // Fan the models out in a row across the spot, lying on the counter top under it.
    // Click: what clicking each one does (null: they can't be clicked); values: each one's Value.
    private void Lay(List<GameObject> models, Transform spot, CheckoutClickable.Action? click, List<int> values = null)
    {
        bool onCounter = TrySurfaceBelow(spot.position, out float surface);
        for (int i = 0; i < models.Count; i++)
        {
            var go = models[i];
            Vector3 pos = spot.position + spot.right * (i * 0.04f);
            go.transform.SetPositionAndRotation(pos, spot.rotation);
            // Bottom of the model on the counter (each one a hair above the last so they don't flicker).
            float bottom = BottomOf(go, pos.y);
            float floor = onCounter ? surface : spot.position.y;
            go.transform.position += Vector3.up * (floor - bottom + 0.0005f + i * 0.0008f);
            if (click.HasValue)
            {
                var part = go.AddComponent<CheckoutClickable>();
                part.action = click.Value;
                part.value = values != null && i < values.Count ? values[i] : 0;
                part.Counter = this;
                if (go.GetComponentInChildren<Collider>() == null) go.AddComponent<BoxCollider>();
            }
            else foreach (var c in go.GetComponentsInChildren<Collider>()) c.enabled = false;
            money.Add(go);
        }
    }

    // The top of the counter straight below a point (its own colliders only).
    private bool TrySurfaceBelow(Vector3 point, out float y)
    {
        y = point.y;
        var hits = Physics.RaycastAll(point + Vector3.up * 0.3f, Vector3.down, 1.5f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        bool found = false;
        foreach (var h in hits)
        {
            if (!h.collider.transform.IsChildOf(transform) || h.distance >= best) continue;
            best = h.distance;
            y = h.point.y;
            found = true;
        }
        return found;
    }

    private static float BottomOf(GameObject go, float fallback)
    {
        bool any = false;
        float bottom = fallback;
        foreach (var r in go.GetComponentsInChildren<Renderer>())
        {
            if (r.GetComponent<TextMeshPro>() != null || r.GetComponent<TMP_SubMesh>() != null) continue; // labels sit on top
            bottom = any ? Mathf.Min(bottom, r.bounds.min.y) : r.bounds.min.y;
            any = true;
        }
        return bottom;
    }

    // Bills and coins making up an amount, largest first.
    private List<GameObject> MakeCash(int cents)
    {
        var list = new List<GameObject>();
        foreach (int d in Denominations)
            while (cents >= d && list.Count < 40)
            {
                cents -= d;
                list.Add(MakeBillOrCoin(d));
            }
        return list;
    }

    private GameObject MakeBillOrCoin(int cents)
    {
        foreach (var m in moneyPrefabs)
            if (m != null && m.cents == cents && m.prefab != null)
                return StockContainer.MakeVisual(m.prefab, transform.position, transform.rotation, null);

        string label = cents >= 100 ? $"${cents / 100}" : $"{cents}c";
        return cents >= 100
            ? MakeLabelled(PrimitiveType.Cube, new Vector3(0.07f, 0.002f, 0.15f), new Color(0.55f, 0.75f, 0.5f), label)
            : MakeLabelled(PrimitiveType.Cylinder, new Vector3(0.024f, 0.001f, 0.024f), new Color(0.8f, 0.8f, 0.8f), label);
    }

    private GameObject MakeCard()
    {
        if (cardPrefab != null) return StockContainer.MakeVisual(cardPrefab, transform.position, transform.rotation, null);
        return MakeLabelled(PrimitiveType.Cube, new Vector3(0.054f, 0.002f, 0.086f), new Color(0.2f, 0.35f, 0.8f), "CARD");
    }

    // A plain stand-in model with its label printed on top (when no model is set).
    private static GameObject MakeLabelled(PrimitiveType type, Vector3 size, Color color, string label)
    {
        var root = new GameObject($"[{label.Replace('\n', ' ')}]");
        var body = GameObject.CreatePrimitive(type);
        body.transform.SetParent(root.transform, false);
        body.transform.localScale = size;
        var r = body.GetComponent<Renderer>();
        if (r != null) r.material.color = color;

        var textGo = new GameObject("Label");
        textGo.transform.SetParent(root.transform, false);
        textGo.transform.localPosition = Vector3.up * (size.y * 0.5f + 0.001f);
        textGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        var tmp = textGo.AddComponent<TextMeshPro>();
        tmp.text = label;
        tmp.fontSize = 0.25f;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = Color.black;
        tmp.rectTransform.sizeDelta = new Vector2(Mathf.Max(size.x, 0.06f), Mathf.Max(size.z, 0.04f));
        return root;
    }

    private void ClearMoney()
    {
        foreach (var go in money)
            if (go != null)
            {
                foreach (var r in go.GetComponentsInChildren<Renderer>())
                    if (r.sharedMaterial != null && r.sharedMaterial.name.EndsWith("(Instance)")) Destroy(r.sharedMaterial);
                Destroy(go);
            }
        money.Clear();
    }

    // Selected in the editor: the belt surface (green) and an arrow to the scanner end.
    private void OnDrawGizmosSelected()
    {
        if (belt == null) return;
        GetBelt(out Vector3 front, out Vector3 forward, out Vector3 up, out float length, out float width);
        Vector3 right = Vector3.Cross(up, forward).normalized;
        Vector3 back = front - forward * length;
        Gizmos.color = new Color(0.2f, 1f, 0.3f, 0.9f);
        Vector3 a = back - right * width * 0.5f, b = back + right * width * 0.5f;
        Vector3 c = front + right * width * 0.5f, d = front - right * width * 0.5f;
        Gizmos.DrawLine(a, b); Gizmos.DrawLine(b, c); Gizmos.DrawLine(c, d); Gizmos.DrawLine(d, a);
        Gizmos.DrawLine(back, front);
        Gizmos.DrawLine(front, front - forward * 0.15f + right * 0.08f);
        Gizmos.DrawLine(front, front - forward * 0.15f - right * 0.08f);
    }

    /* ========================= TESTING ========================= */

    private bool IsNearestToHost()
    {
        var player = NetworkManager.LocalClient?.PlayerObject;
        if (player == null) return false;
        Vector3 p = player.transform.position;
        float mine = Vector3.Distance(p, transform.position);
        if (mine > testRange) return false;
        foreach (var c in All)
            if (c != this && c != null && Vector3.Distance(p, c.transform.position) < mine) return false;
        return true;
    }

    /// <summary>Host: a test customer takes a few random things for sale in the store and brings them here.</summary>
    [ContextMenu("Test: Send A Customer")]
    public void ServerAddTestCustomer()
    {
        if (!IsServer) return;
        if (IsForSale)
        {
            Debug.LogWarning($"[CheckoutCounter] '{name}' is for sale, so customers can't use it. Take its price off first.", this);
            return;
        }
        var plot = Plot;
        var purchases = new List<Purchase>();
        int want = UnityEngine.Random.Range(1, Mathf.Max(2, testMaxItems + 1));

        if (plot != null)
        {
            var sources = new List<StoreListing.Source>(plot.Listing.Sources);
            for (int n = 0; n < want && sources.Count > 0; n++)
            {
                int pick = UnityEngine.Random.Range(0, sources.Count);
                var src = sources[pick];
                if (src.container != null)
                {
                    int itemId = src.container.ServerRemoveOne(src.zone); // off the shelf, for everyone
                    if (itemId == 0) { sources.RemoveAt(pick); continue; }
                    purchases.Add(new Purchase { itemId = itemId, priceCents = src.priceCents, units = 1 });
                    if (--src.quantity <= 0) sources.RemoveAt(pick); else sources[pick] = src;
                }
                else if (src.item != null && src.item.IsSpawned)
                {
                    int units = src.item.TryGetComponent<ItemPickupNet>(out var pickup) && pickup.IsStockCrate ? pickup.Units : 1;
                    purchases.Add(new Purchase { itemId = src.item.ItemId, priceCents = src.priceCents, units = units, whole = true });
                    src.item.NetworkObject.Despawn(true); // the customer carries it to the counter
                    sources.RemoveAt(pick);
                }
                else sources.RemoveAt(pick);
            }
        }

        // Nothing for sale here: random catalog things at a bit over market price, so the counter can be tried anyway.
        if (purchases.Count == 0 && ItemCatalog.Instance != null)
        {
            var pool = new List<ItemDef>();
            foreach (var def in ItemCatalog.Instance.Items)
                if (def != null && def.worldNetworkPrefab != null && def.marketPrice > 0f) pool.Add(def);
            for (int n = 0; n < want && pool.Count > 0; n++)
            {
                var def = pool[UnityEngine.Random.Range(0, pool.Count)];
                purchases.Add(new Purchase
                {
                    itemId = def.id,
                    priceCents = Mathf.RoundToInt(MarketModifiers.MarketPriceCents(def) * 1.2f),
                    units = 1,
                    whole = !(def.worldNetworkPrefab.TryGetComponent<ItemPickupNet>(out var p) && p.IsStockCrate),
                });
            }
        }

        if (purchases.Count == 0)
        {
            Debug.LogWarning("[CheckoutCounter] Test customer: nothing for sale here and no catalog items with a Market Price.", this);
            return;
        }
        ServerAddCustomer(purchases);
        Debug.Log($"[CheckoutCounter] Test customer brought {purchases.Count} thing(s) to '{name}' " +
                  $"({items.Count} on the belt, {waiting.Count} waiting for room).", this);
        if (belt == null)
            Debug.LogError($"[CheckoutCounter] '{name}' has no Belt set: its customers' things can't go on it.", this);
    }
}
