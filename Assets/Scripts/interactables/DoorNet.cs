using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// A networked door: opened with F, or automatically when someone comes near. Every player sees the
/// same state; only "closed / open this way / open that way" and "locked" are synced, and each
/// machine animates the door itself with its DoorMotion.
///
/// Setup: NetworkObject + DoorMotion (the leaves and how they move) + this, on the door's root. The
/// door's colliders should be on a layer the player's interact raycast hits (any but Ignore Raycast).
///
/// Owner Store (optional): while that store is owned, its team can lock and unlock the door (L) and
/// always open it; other players can only open it while it's unlocked. A door that's locked while its
/// store has no owner (Start Locked) stays shut for everyone until the store is bought and that team
/// unlocks it. Public doors can never be locked.
///
/// Storefront doors don't need this: give them just a DoorMotion and the store opens them while
/// it's open and closes them while it's closed, for sale or being renovated.
/// NPCs don't open doors, so doors on their routes should be Automatic (or storefront doors).
/// </summary>
[RequireComponent(typeof(DoorMotion))]
public class DoorNet : NetworkBehaviour
{
    public enum Trigger { Manual, Automatic }

    [Tooltip("Manual: F opens and closes it. Automatic: opens when someone is near, closes after they leave.")]
    [SerializeField] private Trigger trigger = Trigger.Manual;
    [Tooltip("Public doors can never be locked: anyone can always open them (hallways, restrooms, mall entrances).")]
    [SerializeField] private bool isPublic;
    [Tooltip("Optional. While this store is owned, its team can lock and unlock the door and always open it.")]
    [SerializeField] private StorePlot ownerStore;
    [SerializeField] private bool startOpen;
    [Tooltip("Starts locked. Nobody can open or unlock it until its Owner Store is owned; then only that team can. " +
             "When the store goes back on sale, the door locks again.")]
    [SerializeField] private bool startLocked;
    [Tooltip("Optional: the Owner Store's segment this door is behind (e.g. a dock door behind an expansion wall), " +
             "as its index in the store's Segments list. Until that segment is bought the door is shut for everyone; " +
             "once it's bought the door starts out locked, for the team to unlock. -1 = none.")]
    [SerializeField] private int ownerSegment = -1;
    [Tooltip("Doors that open, close, lock and unlock together with this one (e.g. the other half of a double " +
             "door). Link them both ways or just one way; opening any linked door opens the whole group.")]
    [SerializeField] private DoorNet[] linkedDoors;
    [Tooltip("Furthest a player can be to open or lock it with F / L, in meters.")]
    [SerializeField] private float useRange = 3.5f;

    [Header("Automatic")]
    [Tooltip("Automatic doors open when something on these layers is within Sensor Range. Empty = the Player layer.")]
    [SerializeField] private LayerMask sensorMask;
    [SerializeField] private float sensorRange = 2.5f;
    [Tooltip("Seconds it stays open after the last person leaves.")]
    [SerializeField] private float closeDelay = 1.5f;

    // 0 = closed, 1 / -1 = open, swinging toward that side.
    private readonly NetworkVariable<sbyte> state =
        new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<bool> locked =
        new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>Every spawned networked door on this machine.</summary>
    public static readonly List<DoorNet> All = new();

    private DoorMotion motion;
    private float nextSense;
    private float clearSince = -1f;
    private static readonly Collider[] SensorHits = new Collider[16];

    public bool IsOpen => state.Value != 0;
    public bool IsLocked => (locked.Value && !isPublic) || IsShutByStore;

    /// <summary>
    /// The store has it shut, whatever its lock says: the store is being edited (renovation), or the
    /// door is behind a segment that hasn't been bought. Nobody can open, lock or unlock it then.
    /// </summary>
    public bool IsShutByStore =>
        ownerStore != null && (ownerStore.IsRenovating ||
                               (ownerSegment >= 0 && !ownerStore.IsSegmentPurchased(ownerSegment)));
    public bool IsPublic => isPublic;
    public bool IsManual => trigger == Trigger.Manual;
    private bool StoreIsOwned => ownerStore != null && ownerStore.HasOwner;

    private void Awake() => motion = GetComponent<DoorMotion>();

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            if (startOpen && !(startLocked && !isPublic)) state.Value = 1;
            locked.Value = startLocked && !isPublic;
            if (ownerStore != null)
            {
                wasOwned = ownerStore.HasOwner;
                wasSegmentBought = SegmentBought;
                if (IsShutByStore) state.Value = 0;
                ownerStore.Changed += OnStoreChanged;
            }
        }
        state.OnValueChanged += OnStateChanged;
        motion.SetOpen(state.Value != 0, state.Value, instant: true);
        All.Add(this);
    }

    public override void OnNetworkDespawn()
    {
        state.OnValueChanged -= OnStateChanged;
        if (IsServer && ownerStore != null) ownerStore.Changed -= OnStoreChanged;
        All.Remove(this);
    }

    private bool wasOwned;

    // Server: when the store goes back on sale, its doors go back to how they started (locked ones lock and shut).
    private void OnStoreChanged(StorePlot plot)
    {
        // Edit mode started, or the door is behind a wall again: shut it.
        if (IsShutByStore && state.Value != 0) state.Value = 0;

        // Its segment was just bought: it starts out locked, for the team to unlock.
        bool bought = SegmentBought;
        if (bought != wasSegmentBought)
        {
            wasSegmentBought = bought;
            if (bought && !isPublic) locked.Value = true;
        }

        bool owned = plot.HasOwner;
        if (owned == wasOwned) return;
        wasOwned = owned;
        if (!owned && startLocked && !isPublic)
        {
            locked.Value = true;
            state.Value = 0;
        }
    }

    private bool wasSegmentBought;
    private bool SegmentBought => ownerStore != null && ownerSegment >= 0 && ownerStore.IsSegmentPurchased(ownerSegment);

    /// <summary>
    /// Doors players put in a store's walls (WallOpening) belong to that store: its team can lock them.
    /// Does nothing if the door already has an Owner Store.
    /// </summary>
    public void AssignStoreIfUnset(StorePlot plot)
    {
        if (ownerStore != null || plot == null) return;
        ownerStore = plot;
        if (IsSpawned && IsServer)
        {
            wasOwned = ownerStore.HasOwner;
            wasSegmentBought = SegmentBought;
            ownerStore.Changed += OnStoreChanged;
        }
    }

    private void OnStateChanged(sbyte previous, sbyte current) => motion.SetOpen(current != 0, current);

    /* ========================= RULES ========================= */

    /// <summary>
    /// Can this player open it? Unlocked: anyone. Locked: only the owning store's team (so a door locked
    /// while its store is for sale stays shut for everyone until someone buys the store).
    /// </summary>
    public bool CanOpen(ulong clientId) =>
        !IsShutByStore && (!IsLocked || (StoreIsOwned && ownerStore.IsAuthorized(clientId)));

    /// <summary>Can this player lock and unlock it? The owning store's team, and never on a public door.</summary>
    public bool CanLock(ulong clientId) =>
        !isPublic && !IsShutByStore && StoreIsOwned && ownerStore.IsAuthorized(clientId);

    private bool InUseRange(ulong clientId, out Vector3 playerPos)
    {
        playerPos = default;
        if (!NetworkManager.ConnectedClients.TryGetValue(clientId, out var client) || client.PlayerObject == null) return false;
        playerPos = client.PlayerObject.transform.position;
        return Vector3.Distance(playerPos, transform.position) <= useRange + 0.75f; // a little slack for lag
    }

    /* ========================= F / L ========================= */

    [Rpc(SendTo.Server, RequireOwnership = false)]
    public void RequestToggleRpc(RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        if (trigger != Trigger.Manual || !InUseRange(sender, out Vector3 pos)) return;

        if (IsOpen) ServerSetGroupOpen(false, pos); // anyone can close it
        else if (CanOpen(sender)) ServerSetGroupOpen(true, pos);
    }

    [Rpc(SendTo.Server, RequireOwnership = false)]
    public void RequestLockRpc(RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        if (!CanLock(sender) || !InUseRange(sender, out _)) return;
        ServerSetGroupLocked(!locked.Value);
    }

    /// <summary>Server: open or close it and its linked doors (e.g. from a button or a script).</summary>
    public void ServerSetOpen(bool open) => ServerSetGroupOpen(open, null);

    /// <summary>Server: lock or unlock it and its linked doors.</summary>
    public void ServerSetLocked(bool value) => ServerSetGroupLocked(value);

    /* ========================= LINKED DOORS (SERVER) ========================= */

    private static readonly List<DoorNet> Group = new();

    /// <summary>This door plus every door linked to it (either way, and their links in turn).</summary>
    private List<DoorNet> CollectGroup()
    {
        Group.Clear();
        Group.Add(this);
        for (int i = 0; i < Group.Count; i++)
        {
            var d = Group[i];
            if (d.linkedDoors != null)
                foreach (var other in d.linkedDoors)
                    if (other != null && !Group.Contains(other)) Group.Add(other);
            // Links made only on the other door count too.
            foreach (var other in All)
                if (other != null && !Group.Contains(other) && other.linkedDoors != null &&
                    System.Array.IndexOf(other.linkedDoors, d) >= 0) Group.Add(other);
        }
        return Group;
    }

    // Each door swings away from whoever opened it (or its default way with no opener).
    private void ServerSetGroupOpen(bool open, Vector3? from)
    {
        if (!IsServer) return;
        foreach (var d in CollectGroup())
        {
            if (!d.IsSpawned) continue;
            d.state.Value = !open ? (sbyte)0
                : from.HasValue ? (sbyte)(d.motion.SideAwayFrom(from.Value) > 0f ? 1 : -1) : (sbyte)1;
            d.clearSince = -1f;
        }
    }

    private void ServerSetGroupLocked(bool value)
    {
        if (!IsServer) return;
        foreach (var d in CollectGroup())
            if (d.IsSpawned && !d.isPublic) d.locked.Value = value; // public doors never lock
    }

    /* ========================= AUTOMATIC (SERVER) ========================= */

    private void Update()
    {
        if (!IsSpawned || !IsServer || trigger != Trigger.Automatic || Time.time < nextSense) return;
        nextSense = Time.time + 0.2f;

        int mask = sensorMask.value != 0 ? sensorMask.value : LayerMask.GetMask("Player");
        int n = Physics.OverlapSphereNonAlloc(transform.position, sensorRange, SensorHits, mask, QueryTriggerInteraction.Collide);

        // Someone who's allowed through (players are checked against the lock; anything else, like
        // NPCs, only gets through while it's unlocked).
        Vector3? who = null;
        for (int i = 0; i < n && who == null; i++)
        {
            var hit = SensorHits[i];
            var player = hit.GetComponentInParent<NetworkObject>();
            bool isPlayer = player != null && player.IsPlayerObject;
            if (isPlayer ? CanOpen(player.OwnerClientId) : !IsLocked) who = hit.transform.position;
        }

        if (who.HasValue)
        {
            clearSince = -1f;
            if (!IsOpen) ServerSetGroupOpen(true, who.Value);
        }
        else if (IsOpen)
        {
            if (clearSince < 0f) clearSince = Time.time;
            else if (Time.time - clearSince >= closeDelay && !GroupSensesSomeone()) ServerSetGroupOpen(false, null);
        }
    }

    // An automatic door in the group that still has someone in front of it keeps them all open.
    private bool GroupSensesSomeone()
    {
        foreach (var d in CollectGroup())
            if (d != this && d.trigger == Trigger.Automatic && d.clearSince < 0f && d.IsOpen) return true;
        return false;
    }

    private void OnDrawGizmosSelected()
    {
        if (linkedDoors != null)
        {
            Gizmos.color = new Color(1f, 0.9f, 0.3f, 0.8f);
            foreach (var d in linkedDoors)
                if (d != null) Gizmos.DrawLine(transform.position, d.transform.position);
        }
        if (trigger != Trigger.Automatic) return;
        Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.3f);
        Gizmos.DrawWireSphere(transform.position, sensorRange);
    }
}
