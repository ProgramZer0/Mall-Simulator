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

    /// <summary>Server: how many products are left in this crate. Call after Instantiate, before Spawn.</summary>
    public void ServerSetUnits(int count) => units.Value = count >= UnitsPerCrate ? 0 : Mathf.Max(1, count);

    /// <summary>Every spawned item on this machine (stores use it to find their contents).</summary>
    public static readonly List<ItemPickupNet> All = new();

    /// <summary>Where the host froze this item. One value, so the flag and pose always arrive together.</summary>
    private struct FreezeState : INetworkSerializeByMemcpy
    {
        public bool frozen;
        public bool onWall;
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
    public void ServerPrepareFrozen(bool onWall = false)
    {
        freeze.Value = new FreezeState { frozen = true, onWall = onWall, position = transform.position, rotation = transform.rotation };
    }

    public override void OnNetworkSpawn()
    {
        All.Add(this);
        freeze.OnValueChanged += OnFreezeChanged;

        if (!Falls || rb == null) return;
        if (freeze.Value.frozen) ApplyFrozen(freeze.Value);
        else StartFalling();
    }

    public override void OnNetworkDespawn()
    {
        All.Remove(this);
        freeze.OnValueChanged -= OnFreezeChanged;

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
        var state = new FreezeState { frozen = true, position = position, rotation = rotation.normalized };
        freeze.Value = state;
        ApplyFrozen(state); // the host doesn't wait for its own change event
    }

    /// <summary>Server: unfreeze this item so it falls and settles again.</summary>
    public void ServerWake()
    {
        // Things hung on a wall stay put when what's below them is picked up.
        if (!IsServer || !Falls || rb == null || !freeze.Value.frozen || freeze.Value.onWall) return;
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
