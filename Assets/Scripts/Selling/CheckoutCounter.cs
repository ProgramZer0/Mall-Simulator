using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// A checkout counter with a conveyor belt. Customers' items go onto the belt and roll up to the
/// scanner end; a player of the store's team scans them one at a time (F), then takes payment by cash
/// (give the right change) or card (charge the right amount). The money goes to the store's team.
///
/// Items too big for the belt (wider than it, longer than it, or taller than Max Item Height) go on
/// as a price tag instead. Items that would fit but there's no room for yet wait until there is.
///
/// Prefab setup (it's a catalog item, placed and moved in edit mode like shelves):
/// - NetworkObject + ItemPickupNet (Edit Mode Only, Physics Mode Frozen) + this. Colliders on the
///   counter so players can look at it.
/// - Belt: a child BoxCollider over the belt surface (it becomes a trigger). Its blue arrow (+Z)
///   points to the scanner end; items come in at the back and roll forward.
/// - Tag Prefab (optional): what a too-big item shows as. Without one a plain card is used.
/// - Add it to the NetworkManager prefab list and the catalog.
///
/// Only "who's on the belt, in what order" and the payment state are synced; every machine moves the
/// models along the belt itself.
///
/// Testing: the host presses Test Key (F6) near a counter to send a test customer: a few random
/// things for sale in the store are taken off the shelves and put on the belt (random catalog items
/// if the store has nothing for sale).
/// </summary>
public class CheckoutCounter : NetworkBehaviour
{
    public enum Phase : byte { Scanning, AwaitingPayment }
    public enum PayMethod : byte { Cash, Card }

    /// <summary>One thing on the belt.</summary>
    public struct BeltItem : INetworkSerializeByMemcpy, IEquatable<BeltItem>
    {
        public int key;         // unique on this counter (to track its model)
        public int itemId;
        public int priceCents;
        public int units;       // products in it (a whole crate sold loose); 1 otherwise
        public int customer;
        public byte whole;      // 1: a whole item (sold loose, e.g. a washing machine); 0: one product off a shelf
        public byte tag;        // 1: too big for the belt, shown as a price tag

        public bool Equals(BeltItem o) => key == o.key && itemId == o.itemId && priceCents == o.priceCents &&
                                          units == o.units && customer == o.customer && whole == o.whole && tag == o.tag;
        public override bool Equals(object obj) => obj is BeltItem o && Equals(o);
        public override int GetHashCode() => key;
    }

    /// <summary>Where the current customer is: being scanned, or waiting to pay.</summary>
    public struct State : INetworkSerializeByMemcpy, IEquatable<State>
    {
        public Phase phase;
        public PayMethod method;
        public int customer;       // 0: nobody yet
        public int totalCents;     // rung up so far
        public int tenderedCents;  // cash: what the customer handed over
        public int scanned;        // items rung up

        public int ChangeDueCents => Mathf.Max(0, tenderedCents - totalCents);

        public bool Equals(State o) => phase == o.phase && method == o.method && customer == o.customer &&
                                       totalCents == o.totalCents && tenderedCents == o.tenderedCents && scanned == o.scanned;
        public override bool Equals(object obj) => obj is State o && Equals(o);
        public override int GetHashCode() => customer * 397 ^ totalCents;
    }

    public static readonly List<CheckoutCounter> All = new();

    [Header("Belt")]
    [Tooltip("BoxCollider over the belt surface. Its blue arrow (+Z) points to the scanner end.")]
    [SerializeField] private BoxCollider belt;
    [Tooltip("How fast things roll along the belt (meters per second).")]
    [SerializeField] private float beltSpeed = 0.6f;
    [Tooltip("Space between things on the belt (meters).")]
    [SerializeField] private float itemGap = 0.05f;
    [Tooltip("Taller than this (meters) and it goes on as a tag instead.")]
    [SerializeField] private float maxItemHeight = 0.6f;

    [Header("Tags (items too big for the belt)")]
    [Tooltip("What a too-big item shows as on the belt. Its length along the belt is Tag Length. Empty: a plain card.")]
    [SerializeField] private GameObject tagPrefab;
    [SerializeField] private float tagLength = 0.12f;

    [Header("Testing")]
    [Tooltip("Host: sends a test customer to the nearest counter within Test Range.")]
    [SerializeField] private KeyCode testKey = KeyCode.F6;
    [SerializeField] private float testRange = 8f;
    [SerializeField] private int testMaxItems = 6;

    private NetworkList<BeltItem> items;
    private readonly NetworkVariable<State> state = new();

    // Server
    private readonly List<BeltItem> waiting = new();
    private int nextKey = 1, nextCustomer = 1;
    private float nextFill;

    /// <summary>Raised on this machine when the belt or payment changes.</summary>
    public event Action Changed;

    public State Current => state.Value;
    public int ItemCount => IsSpawned ? items.Count : 0;
    public StorePlot Plot => MallManager.Instance != null ? MallManager.Instance.GetPlotAt(transform.position + Vector3.up * 0.1f) : null;
    public bool CanUse(ulong clientId) { var p = Plot; return p != null && p.IsAuthorized(clientId); }

    /// <summary>Anyone still being served or waiting (edit mode waits until it's done).</summary>
    public bool IsBusy => IsSpawned && (items.Count > 0 || state.Value.scanned > 0 || waiting.Count > 0);

    public bool TryGetFront(out BeltItem item)
    {
        item = default;
        if (!IsSpawned || items.Count == 0) return false;
        item = items[0];
        return true;
    }

    private void Awake()
    {
        items = new NetworkList<BeltItem>(null, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        if (belt != null) belt.isTrigger = true;
    }

    public override void OnNetworkSpawn()
    {
        All.Add(this);
        items.OnListChanged += OnItemsChanged;
        state.OnValueChanged += OnStateChanged;
    }

    public override void OnNetworkDespawn()
    {
        All.Remove(this);
        items.OnListChanged -= OnItemsChanged;
        state.OnValueChanged -= OnStateChanged;
        ClearModels();
        if (CheckoutUI.Instance != null && CheckoutUI.Instance.Counter == this) CheckoutUI.Instance.Close();
    }

    private void OnItemsChanged(NetworkListEvent<BeltItem> e) => Changed?.Invoke();
    private void OnStateChanged(State a, State b) => Changed?.Invoke();

    /* ========================= ITEMS AND SIZES ========================= */

    private struct Shape
    {
        public GameObject model;
        public Quaternion rotation;   // relative to the belt (long side along it)
        public Vector3 extents;       // size along belt right, up, forward (meters)
        public Vector3 center;        // bounds centre in the model's own space, scaled
    }

    private static readonly Dictionary<(int, bool), Shape> Shapes = new();

    // The model for an item on the belt and its size, turned so its long side runs along the belt.
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
        shape = new Shape
        {
            model = model,
            rotation = turn * own,
            extents = Extents(turn * own, size),
            center = Vector3.Scale(b.center, s),
        };
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
        front = t.TransformPoint(belt.center) + forward * (length * 0.5f) + up * (height * 0.5f); // top of the scanner end
    }

    // Too big for the belt: wider than it, longer than it, or too tall.
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

    // Move waiting things onto the belt while there's room.
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
        if (IsSpawned && belt != null) UpdateModels();
        if (!IsServer || !IsSpawned) return;

        if (Time.time >= nextFill)
        {
            nextFill = Time.time + 0.25f;
            ServerFill();
        }

        if (testKey != KeyCode.None && Input.GetKeyDown(testKey) && !UIState.IsMenuOpen && IsNearestToHost())
            ServerAddTestCustomer();
    }

    /* ========================= SCANNING AND PAYING ========================= */

    /// <summary>Scan the next thing on the belt.</summary>
    [Rpc(SendTo.Server)]
    public void RequestScanRpc(RpcParams rpcParams = default)
    {
        if (!CanUse(rpcParams.Receive.SenderClientId) || items.Count == 0) return;
        var s = state.Value;
        if (s.phase != Phase.Scanning) return;

        var front = items[0];
        if (s.customer == 0) s.customer = front.customer;
        if (front.customer != s.customer) return; // the next customer's things: this one pays first
        items.RemoveAt(0);
        s.totalCents += front.priceCents;
        s.scanned++;

        // That was this customer's last thing: time to pay.
        if (!HasMoreFor(s.customer))
        {
            s.phase = Phase.AwaitingPayment;
            s.method = UnityEngine.Random.value < 0.5f ? PayMethod.Cash : PayMethod.Card;
            s.tenderedCents = s.method == PayMethod.Cash ? CashHandedOver(s.totalCents) : 0;
        }
        state.Value = s;
        ServerFill();
    }

    private bool HasMoreFor(int customer)
    {
        foreach (var it in items) if (it.customer == customer) return true;
        foreach (var it in waiting) if (it.customer == customer) return true;
        return false;
    }

    // What a customer hands over in cash: the exact amount now and then, otherwise the total rounded up
    // to the next $5, $10, $20, $50 or $100 (one of the smaller sensible ones).
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

    /// <summary>Cash: hand back this much change. Too little and the customer waits for the rest; too much is money lost.</summary>
    [Rpc(SendTo.Server)]
    public void RequestPayCashRpc(int changeCents, RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        var s = state.Value;
        if (!CanUse(sender) || s.phase != Phase.AwaitingPayment || s.method != PayMethod.Cash) return;
        changeCents = Mathf.Max(0, changeCents);
        if (changeCents < s.ChangeDueCents)
        {
            ResultRpc(false, $"Not enough change: they're owed {Sellable.FormatPrice(s.ChangeDueCents)}.", RpcTarget.Single(sender, RpcTargetUse.Temp));
            return;
        }
        int kept = s.tenderedCents - changeCents;
        string note = changeCents > s.ChangeDueCents ? $" (gave {Sellable.FormatPrice(changeCents - s.ChangeDueCents)} too much change)" : "";
        Complete(kept, sender, $"Sold for {Sellable.FormatPrice(kept)}{note}.");
    }

    /// <summary>Card: charge this much. More than the total and the customer refuses; less is money lost.</summary>
    [Rpc(SendTo.Server)]
    public void RequestPayCardRpc(int chargeCents, RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        var s = state.Value;
        if (!CanUse(sender) || s.phase != Phase.AwaitingPayment || s.method != PayMethod.Card) return;
        if (chargeCents > s.totalCents)
        {
            ResultRpc(false, $"The customer refuses: that's more than {Sellable.FormatPrice(s.totalCents)}.", RpcTarget.Single(sender, RpcTargetUse.Temp));
            return;
        }
        if (chargeCents <= 0)
        {
            ResultRpc(false, "Enter the amount to charge.", RpcTarget.Single(sender, RpcTargetUse.Temp));
            return;
        }
        string note = chargeCents < s.totalCents ? $" (charged {Sellable.FormatPrice(s.totalCents - chargeCents)} too little)" : "";
        Complete(chargeCents, sender, $"Sold for {Sellable.FormatPrice(chargeCents)}{note}.");
    }

    private void Complete(int receivedCents, ulong cashier, string message)
    {
        var plot = Plot;
        var econ = EconomyManager.Instance;
        if (plot != null && plot.HasOwner && econ != null) econ.ServerAddCents(plot.OwnerTeamId, receivedCents);
        state.Value = new State { phase = Phase.Scanning, customer = items.Count > 0 ? items[0].customer : 0 };
        ResultRpc(true, message, RpcTarget.Single(cashier, RpcTargetUse.Temp));
        ServerFill();
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void ResultRpc(bool ok, string message, RpcParams rpcParams)
    {
        if (CheckoutUI.Instance != null) CheckoutUI.Instance.ShowResult(this, ok, message);
        else
        {
            fallbackMessage = message;
            if (ok) fallbackOpen = false;
        }
        lastMessage = message;
        lastMessageUntil = Time.time + 3f;
    }

    /* ========================= PLAYER ========================= */

    private string lastMessage;
    private float lastMessageUntil;

    /// <summary>The F prompt for a player looking at this counter (null: none).</summary>
    public string PromptFor(ulong clientId)
    {
        if (!IsSpawned || !CanUse(clientId)) return null;
        string message = Time.time < lastMessageUntil ? $"   {lastMessage}" : "";
        var s = state.Value;
        if (s.phase == Phase.AwaitingPayment)
            return $"F: Take payment ({(s.method == PayMethod.Cash ? "cash" : "card")}, {Sellable.FormatPrice(s.totalCents)}){message}";
        if (items.Count > 0)
        {
            var front = items[0];
            string total = s.totalCents > 0 ? $"   Total {Sellable.FormatPrice(s.totalCents)}" : "";
            return $"F: Scan {NameOf(front.itemId)}{(front.tag != 0 ? " (tag)" : "")} {Sellable.FormatPrice(front.priceCents)}{total}{message}";
        }
        return $"No customers{(IsServer && testKey != KeyCode.None ? $"   {testKey}: test customer" : "")}{message}";
    }

    /// <summary>F on the counter: scan the next thing, or open the payment.</summary>
    public void Interact()
    {
        if (state.Value.phase == Phase.AwaitingPayment)
        {
            if (CheckoutUI.Instance != null) CheckoutUI.Instance.Open(this);
            else { fallbackOpen = true; fallbackGiven = 0; fallbackCard = ""; fallbackMessage = null; }
            return;
        }
        if (items.Count > 0) RequestScanRpc();
    }

    /* ========================= MODELS ON THE BELT (EVERY MACHINE) ========================= */

    private class Shown
    {
        public GameObject go;
        public float offset;   // distance from the scanner end to its front edge
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
                s = new Shown { go = MakeModel(it), offset = Mathf.Max(target, length - len) }; // comes in at the back
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

    private void Place(Shown s, BeltItem it, Vector3 front, Vector3 forward, Vector3 up, Quaternion beltRot, float len)
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

    private GameObject MakeModel(BeltItem it)
    {
        if (it.tag == 0 && TryGetShape(it.itemId, it.whole != 0, out var shape))
            return StockContainer.MakeVisual(shape.model, transform.position, transform.rotation, null);

        if (tagPrefab != null) return StockContainer.MakeVisual(tagPrefab, transform.position, transform.rotation, null);
        var card = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(card.GetComponent<Collider>());
        card.name = $"[Tag] {NameOf(it.itemId)}";
        card.transform.localScale = new Vector3(0.08f, 0.004f, tagLength);
        return card;
    }

    private void ClearModels()
    {
        foreach (var s in shown.Values) if (s.go != null) Destroy(s.go);
        shown.Clear();
    }

    public override void OnDestroy()
    {
        ClearModels();
        base.OnDestroy();
    }

    /* ========================= TESTING ========================= */

    // Host: is this the closest counter to the host's player (within Test Range)?
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
        Debug.Log($"[CheckoutCounter] Test customer brought {purchases.Count} thing(s).", this);
    }

    /* ========================= BUILT-IN PAYMENT WINDOW (without a CheckoutUI) ========================= */

    private bool fallbackOpen;
    private int fallbackGiven;
    private string fallbackCard = "";
    private string fallbackMessage;
    private static readonly int[] Denominations = { 2000, 1000, 500, 100, 25, 10, 5, 1 };

    private void OnGUI()
    {
        if (!fallbackOpen || CheckoutUI.Instance != null) return;
        var s = state.Value;
        if (s.phase != Phase.AwaitingPayment) { fallbackOpen = false; return; }

        var r = new Rect(Screen.width * 0.5f - 200f, Screen.height * 0.5f - 150f, 400f, 300f);
        GUI.Box(r, s.method == PayMethod.Cash ? "Cash payment" : "Card payment");
        float y = r.y + 30f;
        GUI.Label(new Rect(r.x + 10, y, 380, 20), $"Total: {Sellable.FormatPrice(s.totalCents)}"); y += 22f;

        if (s.method == PayMethod.Cash)
        {
            GUI.Label(new Rect(r.x + 10, y, 380, 20), $"Customer gave {Sellable.FormatPrice(s.tenderedCents)}. Change due: {FormatAmount(s.ChangeDueCents)}"); y += 22f;
            GUI.Label(new Rect(r.x + 10, y, 380, 20), $"Change given: {FormatAmount(fallbackGiven)}"); y += 26f;
            for (int i = 0; i < Denominations.Length; i++)
                if (GUI.Button(new Rect(r.x + 10 + (i % 4) * 95, y + (i / 4) * 30, 90, 26), FormatAmount(Denominations[i])))
                    fallbackGiven += Denominations[i];
            y += 66f;
            if (GUI.Button(new Rect(r.x + 10, y, 120, 28), "Clear")) fallbackGiven = 0;
            if (GUI.Button(new Rect(r.x + 140, y, 120, 28), "Give change")) RequestPayCashRpc(fallbackGiven);
        }
        else
        {
            GUI.Label(new Rect(r.x + 10, y, 380, 20), "Amount to charge ($):"); y += 22f;
            fallbackCard = GUI.TextField(new Rect(r.x + 10, y, 200, 24), fallbackCard ?? ""); y += 32f;
            if (GUI.Button(new Rect(r.x + 10, y, 120, 28), "Charge"))
            {
                if (Sellable.TryParsePrice(fallbackCard, out int cents)) RequestPayCardRpc(cents);
                else fallbackMessage = "Enter an amount like 12.50";
            }
        }
        if (GUI.Button(new Rect(r.x + 270, r.yMax - 36, 120, 28), "Close")) fallbackOpen = false;
        if (!string.IsNullOrEmpty(fallbackMessage)) GUI.Label(new Rect(r.x + 10, r.yMax - 64, 380, 24), fallbackMessage);
    }

    /// <summary>Money as $12.50 (zero shown as $0.00, unlike Sellable.FormatPrice).</summary>
    public static string FormatAmount(int cents) => $"${cents / 100}.{Mathf.Abs(cents % 100):00}";
}
