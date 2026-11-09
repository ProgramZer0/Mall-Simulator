using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Serialization;

/// <summary>A physical item in the world that can be picked up, placed and edited.</summary>
public class ItemPickupNet : NetworkBehaviour
{
    public enum PhysicsMode
    {
        /// <summary>Stays exactly where it was put.</summary>
        Frozen,
        /// <summary>Falls when put down, then freezes once it has landed and held still for a while.</summary>
        FallThenFreeze,
        /// <summary>Always runs physics. Only looks the same for everyone if you add a NetworkTransform.</summary>
        AlwaysSimulate,
    }

    /// <summary>Where an item may be placed.</summary>
    public enum Mounting { Floor, Wall, FloorOrWall }

    [SerializeField] public int itemId;
    [Tooltip("Floor: stands on floors and shelves. Wall: hangs on walls (pictures, signs). FloorOrWall: either.\n" +
             "Wall items hang by their back, the side opposite their blue arrow, and never fall.")]
    [SerializeField] private Mounting mounting = Mounting.Floor;
    [Tooltip("Can't be picked up in normal play, only moved in store edit mode (e.g. the shop monitor).")]
    [SerializeField] private bool editModeOnly;
    [Tooltip("Extra turn so the model faces the right way when it's placed or hung, on top of the rotation its " +
             "prefab root already has (that one is kept automatically, e.g. Blender's -90 on X). Select the item: " +
             "the yellow arrow is its front, the side that faces out from a wall. If the arrow comes out of its " +
             "back, try (0, 180, 0); out of a side, (0, 90, 0) or (0, -90, 0). Windows and doors use their " +
             "WallOpening's Model Rotation instead.")]
    [SerializeField] private Vector3 modelRotation;
    [Tooltip("Placed in the scene hanging on a wall: stays there when the game starts instead of falling. " +
             "Set by itself when you hang it with the scene tool (Tools > Mall Sim > Hang On Wall); " +
             "untick it if you move the item off the wall by hand.")]
    [SerializeField] private bool startsOnWall;

    [Header("Stocking")]
    [Tooltip("This item is stock: hold it, look at a StockContainer (shelf, rack...) and press F to put one product on it.")]
    [SerializeField] private bool isStockCrate;
    [Tooltip("One product as it sits on a shelf (a container's Stock Zones). Add several for variants (e.g. colours); " +
             "they should all be the same size, the first one sets the layout. Leave empty if it can't go on shelves. " +
             "Plain visuals: any scripts, physics or networking on them are stripped.")]
    [SerializeField] private List<GameObject> shelfPrefabs = new();
    [Tooltip("One product as it hangs on a rail (a container's Hang Rails), hanger included. Add several for variants; " +
             "same size, the first sets the layout. It hangs from the top of its model, or a child named HangPoint. " +
             "Leave empty if it can't be hung.")]
    [SerializeField] private List<GameObject> hangPrefabs = new();
    [Tooltip("On: each product stocked is a random pick from the list (a box of mixed colours). " +
             "Off: always the first one. With one entry it makes no difference.")]
    [SerializeField] private bool randomVariants;

    // Older single-model fields; moved into the lists above automatically.
    [Tooltip("How many products a full crate holds.")]
    [Min(1)] [SerializeField] private int unitsPerCrate = 1;

    [Header("Physics")]
    [Tooltip("Frozen: stays where it's put.\n" +
             "Fall Then Freeze: falls, and once it has touched something and held still for Still Seconds, " +
             "the host freezes it and everyone snaps to the host's spot.\n" +
             "Always Simulate: normal physics forever (add a NetworkTransform or players will see it in different places).")]
    [SerializeField] private PhysicsMode physicsMode = PhysicsMode.FallThenFreeze;
    [Tooltip("How long it must stay still after landing before it freezes.")]
    [SerializeField] private float stillSeconds = 4f;
    [Tooltip("Freeze wherever it is after this long, even if it never settled.")]
    [SerializeField] private float maxFallSeconds = 20f;
    [Tooltip("If it falls this far below where it was put (through the floor), put it back there.")]
    [SerializeField] private float lostFallDistance = 30f;
    [Tooltip("Slower than this (m/s) counts as still.")]
    [SerializeField] private float stillSpeed = 0.05f;

    public bool EditModeOnly => editModeOnly;
    /// <summary>Extra turn on top of the prefab root's rotation (see PlacementCheck.ModelFix).</summary>
    public Vector3 ModelRotation => modelRotation;
    public Mounting MountingType => mounting;

    // Wall-only items never fall (nothing to land on).
    private bool Falls => physicsMode == PhysicsMode.FallThenFreeze && mounting != Mounting.Wall;
    public bool IsStockCrate => isStockCrate;
    /// <summary>The main shelf model (the first in the list); sets the shelf layout.</summary>
    public GameObject ShelfPrefab => FirstOf(shelfPrefabs);
    /// <summary>The main hanging model (the first in the list); sets the rail layout.</summary>
    public GameObject HangPrefab => FirstOf(hangPrefabs);

    /// <summary>
    /// The model for one stocked product. With Random Variants on, a pick from the list that's the
    /// same on every machine for the same seed and slot; otherwise the first.
    /// </summary>
    public GameObject GetVariant(bool hang, int seed, int slot)
    {
        var list = hang ? hangPrefabs : shelfPrefabs;
        GameObject main = hang ? HangPrefab : ShelfPrefab;
        if (!randomVariants || list == null) return main;

        int count = 0;
        foreach (var p in list) if (p != null) count++;
        if (count <= 1) return main;

        int pick = Hash(seed, slot) % count;
        foreach (var p in list)
        {
            if (p == null) continue;
            if (pick-- == 0) return p;
        }
        return main;
    }

    private static GameObject FirstOf(List<GameObject> list)
    {
        if (list != null) foreach (var p in list) if (p != null) return p;
        return null;
    }

    // Plain integer mixing, so every machine picks the same variant (unlike string/object hash codes).
    private static int Hash(int a, int b)
    {
        unchecked
        {
            uint h = (uint)a * 0x9E3779B1u ^ (uint)b * 0x85EBCA77u;
            h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
            return (int)(h & 0x7FFFFFFF);
        }
    }

#if UNITY_EDITOR
    /// <summary>
    /// Editor: how this item's model stands and faces (its prefab root's rotation, then Model Rotation),
    /// for a prefab being edited or an item in the scene.
    /// </summary>
    public Quaternion EditorModelFix()
    {
        var source = UnityEditor.PrefabUtility.GetCorrespondingObjectFromOriginalSource(gameObject);
        Quaternion root = source != null && source != gameObject ? source.transform.localRotation : transform.localRotation;
        Vector3 extra = TryGetComponent<WallOpening>(out var o) ? o.modelRotation : modelRotation;
        return root * Quaternion.Euler(extra);
    }

    // The yellow arrow: the item's front (it faces out from a wall when hung). The short line is up.
    private void OnDrawGizmosSelected()
    {
        Bounds local = PlacementCheck.ComputeLocalBounds(gameObject);
        Vector3 center = transform.TransformPoint(local.center);
        Quaternion upright = transform.rotation * Quaternion.Inverse(EditorModelFix());
        Vector3 front = upright * Vector3.forward, up = upright * Vector3.up;
        float length = Mathf.Max(0.3f, local.extents.magnitude * Mathf.Abs(transform.lossyScale.x) * 1.5f);
        Vector3 tip = center + front * length;
        Vector3 side = Vector3.Cross(up, front) * length * 0.15f;
        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(center, tip);
        Gizmos.DrawLine(tip, tip - front * length * 0.2f + side);
        Gizmos.DrawLine(tip, tip - front * length * 0.2f - side);
        Gizmos.DrawLine(center, center + up * length * 0.4f); // and which way is up
    }

    /// <summary>Editor: mark this scene item as hanging on a wall (the scene tool calls it).</summary>
    public void SetStartsOnWallInEditor(bool on)
    {
        if (startsOnWall == on) return;
        UnityEditor.Undo.RecordObject(this, "Hang On Wall");
        startsOnWall = on;
        UnityEditor.EditorUtility.SetDirty(this);
        UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(this);
    }

    private void OnValidate()
    {
        // Move models set before variants existed into the lists.
        bool changed = false;
        if (changed) UnityEditor.EditorUtility.SetDirty(this);
    }
#endif
    public int UnitsPerCrate => Mathf.Max(1, unitsPerCrate);

    // Products left in this crate. 0 means "full", so crates spawned from a prefab start full.
    private readonly NetworkVariable<int> units =
        new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>Products left in this crate (meaningless for items that aren't stock crates).</summary>
    public int Units => units.Value > 0 ? Mathf.Min(units.Value, UnitsPerCrate) : UnitsPerCrate;

    // The colors of its paint zones (PaintSet: one PaintPalette id per PaintParts zone, 0 = own colors).
    private readonly NetworkVariable<uint> paint =
        new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>The colors of its paint zones (see PaintSet).</summary>
    public uint Paint => paint.Value;

    /// <summary>Server: set every zone's color. Call after Instantiate (before Spawn is fine).</summary>
    public void ServerSetPaint(uint set)
    {
        if (PaintSet.IsValid(set)) paint.Value = set;
    }

    /// <summary>Server: paint one zone.</summary>
    public void ServerSetZonePaint(int zone, byte id)
    {
        if (PaintPalette.IsValid(id)) paint.Value = PaintSet.With(paint.Value, zone, id);
    }

    private void OnPaintChanged(uint before, uint now) => ApplyPaint();

    private void ApplyPaint() => PaintParts.ApplySet(gameObject, paint.Value);

    /// <summary>Server: how many products are left in this crate. Call after Instantiate, before Spawn.</summary>
    public void ServerSetUnits(int count) => units.Value = count >= UnitsPerCrate ? 0 : Mathf.Max(1, count);

    // A part-used crate for sale is worth less: the store's listing needs redoing when its count changes.
    private void OnUnitsChanged(int before, int now)
    {
        if (TryGetComponent<Sellable>(out _)) StoreListing.MarkAllDirty();
    }

    /// <summary>Every spawned item on this machine (stores use it to find their contents).</summary>
    public static readonly List<ItemPickupNet> All = new();

    /// <summary>Where the host froze this item. One value, so the flag and pose always arrive together.</summary>
    private struct FreezeState : INetworkSerializeByMemcpy
    {
        public bool frozen;
        public bool onWall;
        public bool carried; // riding on a cart: the cart's parenting places it, not this pose
        public Vector3 position;
        public Quaternion rotation;
    }

    private readonly NetworkVariable<FreezeState> freeze =
        new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private Rigidbody rb;
    private bool falling;
    private bool touched;
    private float fallTime;
    private float stillTime;
    private Vector3 dropPosition;
    private Quaternion dropRotation;

    private static readonly Collider[] WakeHits = new Collider[32];

    public bool IsFalling => falling;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        // Start still; OnNetworkSpawn decides whether it falls.
        if (rb != null && physicsMode != PhysicsMode.AlwaysSimulate) rb.isKinematic = true;
    }

    /// <summary>
    /// Server: spawn this item already frozen where it stands (e.g. coming back from edit mode, where
    /// the player already chose whether it falls). Call after Instantiate, before Spawn.
    /// </summary>
    public void ServerPrepareFrozen(bool onWall = false, bool carried = false)
    {
        freeze.Value = new FreezeState
        {
            frozen = true, onWall = onWall, carried = carried,
            position = transform.position, rotation = transform.rotation,
        };
    }

    /// <summary>Is this item riding on a cart?</summary>
    public bool IsOnCart => transform.parent != null && transform.parent.GetComponent<CartNet>() != null;

    private static readonly Collider[] TopHits = new Collider[16];

    /// <summary>
    /// On a cart: is something else on the cart resting on top of this? Then it has to come off first,
    /// so nothing is left floating.
    /// </summary>
    public bool HasItemOnTopOnCart()
    {
        if (!IsOnCart || !TryGetLocalShape(out Bounds local)) return false;

        Vector3 scale = transform.lossyScale;
        Vector3 half = Vector3.Scale(local.extents, new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
        Vector3 up = transform.up;
        Vector3 top = transform.TransformPoint(local.center) + up * half.y;

        // A thin slab just above the top, a bit smaller than the footprint (so neighbours beside it
        // don't count, only things sitting on it).
        Vector3 slabCenter = top + up * 0.06f;
        Vector3 slabHalf = new(half.x * 0.85f, 0.04f, half.z * 0.85f);
        int n = Physics.OverlapBoxNonAlloc(slabCenter, slabHalf, TopHits, transform.rotation, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var other = TopHits[i].GetComponentInParent<ItemPickupNet>();
            if (other != null && other != this && other.transform.parent == transform.parent) return true;
        }
        return false;
    }

    // The item's colliders as a box in its own local space (unscaled).
    private bool TryGetLocalShape(out Bounds local)
    {
        local = default;
        bool any = false;
        foreach (var c in GetComponentsInChildren<Collider>())
        {
            if (!c.enabled || c.isTrigger) continue;
            Bounds w = c.bounds;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = w.center + Vector3.Scale(w.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                Vector3 p = transform.InverseTransformPoint(corner);
                if (!any) { local = new Bounds(p, Vector3.zero); any = true; }
                else local.Encapsulate(p);
            }
        }
        return any;
    }

    public override void OnNetworkSpawn()
    {
        units.OnValueChanged += OnUnitsChanged;
        All.Add(this);
        freeze.OnValueChanged += OnFreezeChanged;
        paint.OnValueChanged += OnPaintChanged;
        if (paint.Value != 0) ApplyPaint();

        // Hung on a wall in the scene: frozen there from the start, and it stays put when what's
        // below it is picked up (like one a player hung).
        if (IsServer && startsOnWall && !freeze.Value.frozen && !IsOnCart) ServerPrepareFrozen(onWall: true);

        if (!Falls || rb == null) return;
        if (freeze.Value.frozen) ApplyFrozen(freeze.Value);
        else StartFalling();
    }

    public override void OnNetworkDespawn()
    {
        units.OnValueChanged -= OnUnitsChanged;
        All.Remove(this);
        freeze.OnValueChanged -= OnFreezeChanged;
        paint.OnValueChanged -= OnPaintChanged;

        // Something resting on this item would be left floating, so let it fall again.
        if (IsServer && NetworkManager != null && !NetworkManager.ShutdownInProgress) ServerWakeItemsOnTop();
    }

    private void OnFreezeChanged(FreezeState previous, FreezeState current)
    {
        if (!Falls || rb == null) return;
        if (current.frozen) ApplyFrozen(current);
        else if (!falling) StartFalling();
    }

    // Every machine runs the fall itself so it looks smooth; the host decides where it ends up.
    private void StartFalling()
    {
        falling = true;
        touched = false;
        fallTime = 0f;
        stillTime = 0f;
        dropPosition = transform.position;
        dropRotation = transform.rotation;
        rb.isKinematic = false;
        rb.WakeUp();
    }

    private void ApplyFrozen(FreezeState state)
    {
        falling = false;
        if (!rb.isKinematic)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
        rb.isKinematic = true;
        if (state.carried) return; // on a cart, which may have moved since; parenting keeps it in place
        transform.SetPositionAndRotation(state.position, state.rotation);
        rb.position = state.position;
        rb.rotation = state.rotation;
    }

    private void FixedUpdate()
    {
        if (!falling || !IsSpawned) return;

        // Clients just watch; a client whose copy fell through the floor holds it until the host's answer.
        if (!IsServer)
        {
            if (transform.position.y < dropPosition.y - lostFallDistance) HoldAt(dropPosition, dropRotation);
            return;
        }

        fallTime += Time.fixedDeltaTime;

        if (transform.position.y < dropPosition.y - lostFallDistance)
        {
            ServerFreeze(dropPosition, dropRotation);
            return;
        }

        bool still = rb.IsSleeping() ||
                     (rb.linearVelocity.sqrMagnitude < stillSpeed * stillSpeed &&
                      rb.angularVelocity.sqrMagnitude < stillSpeed * stillSpeed * 4f);
        stillTime = touched && still ? stillTime + Time.fixedDeltaTime : 0f;

        if (stillTime >= stillSeconds || fallTime >= maxFallSeconds)
            ServerFreeze(rb.position, rb.rotation);
    }

    private void HoldAt(Vector3 position, Quaternion rotation)
    {
        falling = false;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        rb.isKinematic = true;
        transform.SetPositionAndRotation(position, rotation);
    }

    private void OnCollisionEnter(Collision collision) => touched = true;
    private void OnCollisionStay(Collision collision) => touched = true;

    private void ServerFreeze(Vector3 position, Quaternion rotation)
    {
        // Landed on a cart? Then it rides along from now on.
        var cart = CartNet.FindCartAt(GetCenter(position, rotation));
        var state = new FreezeState { frozen = true, carried = cart != null, position = position, rotation = rotation.normalized };
        freeze.Value = state;
        transform.SetPositionAndRotation(position, rotation);
        ApplyFrozen(state); // the host doesn't wait for its own change event
        if (cart != null) cart.ServerLoad(NetworkObject);
    }

    // The middle of the item's model at a pose (its pivot may be anywhere).
    private Vector3 GetCenter(Vector3 position, Quaternion rotation) =>
        ItemCatalog.Instance != null && ItemCatalog.Instance.TryGet(itemId, out var def) &&
        PlacementCheck.GetTestBox(def, position, rotation, out Vector3 c, out _) ? c : position;

    /// <summary>Is it frozen in place (not falling)?</summary>
    public bool IsFrozen => !Falls || rb == null || freeze.Value.frozen;

    /// <summary>
    /// Server: stop it falling and freeze it where it is right now. Used when something is stocked onto
    /// it or set down on it while it's still settling, so the two can't knock each other flying.
    /// </summary>
    public void ServerFreezeNow()
    {
        if (!IsServer || !IsSpawned || IsFrozen) return;
        ServerFreeze(rb.position, rb.rotation);
    }

    /// <summary>Server: unfreeze this item so it falls and settles again.</summary>
    public void ServerWake()
    {
        // Things hung on a wall stay put when what's below them is picked up.
        if (!IsServer || !Falls || rb == null || !freeze.Value.frozen || freeze.Value.onWall || IsOnCart) return;
        freeze.Value = default;
        StartFalling();
    }

    private void ServerWakeItemsOnTop()
    {
        if (physicsMode == PhysicsMode.AlwaysSimulate) return;

        var cols = GetComponentsInChildren<Collider>();
        if (cols.Length == 0) return;
        Bounds b = default;
        bool any = false;
        foreach (var c in cols)
        {
            if (!c.enabled || c.isTrigger) continue;
            if (!any) { b = c.bounds; any = true; }
            else b.Encapsulate(c.bounds);
        }
        if (!any) return;

        // A thin slab just above the top, a little wider than the item.
        Vector3 center = new(b.center.x, b.max.y + 0.05f, b.center.z);
        Vector3 half = new(b.extents.x + 0.02f, 0.08f, b.extents.z + 0.02f);
        int n = Physics.OverlapBoxNonAlloc(center, half, WakeHits, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var other = WakeHits[i].GetComponentInParent<ItemPickupNet>();
            if (other != null && other != this && other.IsSpawned) other.ServerWake();
        }
    }
}
