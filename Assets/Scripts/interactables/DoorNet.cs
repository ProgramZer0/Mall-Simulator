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
/// always open it; other players can only open it while it's unlocked. With no Owner Store, or while
/// the store is for sale, anyone can open it and it can't be locked.
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
    [Tooltip("Optional. While this store is owned, its team can lock the door and other players can't open it locked.")]
    [SerializeField] private StorePlot ownerStore;
    [SerializeField] private bool startOpen;
    [SerializeField] private bool startLocked;
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

    private DoorMotion motion;
    private float nextSense;
    private float clearSince = -1f;
    private static readonly Collider[] SensorHits = new Collider[16];

    public bool IsOpen => state.Value != 0;
    public bool IsLocked => locked.Value && StoreIsOwned;
    public bool IsManual => trigger == Trigger.Manual;
    private bool StoreIsOwned => ownerStore != null && ownerStore.HasOwner;

    private void Awake() => motion = GetComponent<DoorMotion>();

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            if (startOpen) state.Value = 1;
            locked.Value = startLocked;
        }
        state.OnValueChanged += OnStateChanged;
        motion.SetOpen(state.Value != 0, state.Value, instant: true);
    }

    public override void OnNetworkDespawn() => state.OnValueChanged -= OnStateChanged;

    private void OnStateChanged(sbyte previous, sbyte current) => motion.SetOpen(current != 0, current);

    /* ========================= RULES ========================= */

    /// <summary>Can this player open it? (Unlocked, the store's team, or nobody owns the store.)</summary>
    public bool CanOpen(ulong clientId) => !IsLocked || ownerStore.IsAuthorized(clientId);

    /// <summary>Can this player lock and unlock it? (The owning store's team.)</summary>
    public bool CanLock(ulong clientId) => StoreIsOwned && ownerStore.IsAuthorized(clientId);

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

        if (IsOpen) state.Value = 0; // anyone can close it
        else if (CanOpen(sender)) state.Value = (sbyte)(motion.SideAwayFrom(pos) > 0f ? 1 : -1);
    }

    [Rpc(SendTo.Server, RequireOwnership = false)]
    public void RequestLockRpc(RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        if (!CanLock(sender) || !InUseRange(sender, out _)) return;
        locked.Value = !locked.Value;
    }

    /// <summary>Server: open or close it (e.g. from a button or a script).</summary>
    public void ServerSetOpen(bool open, float side = 1f)
    {
        if (!IsServer) return;
        state.Value = open ? (sbyte)(side < 0f ? -1 : 1) : (sbyte)0;
    }

    /// <summary>Server: lock or unlock it.</summary>
    public void ServerSetLocked(bool value)
    {
        if (IsServer) locked.Value = value;
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
            if (!IsOpen) state.Value = (sbyte)(motion.SideAwayFrom(who.Value) > 0f ? 1 : -1);
        }
        else if (IsOpen)
        {
            if (clearSince < 0f) clearSince = Time.time;
            else if (Time.time - clearSince >= closeDelay) state.Value = 0;
        }
    }

    private void OnDrawGizmosSelected()
    {
        if (trigger != Trigger.Automatic) return;
        Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.3f);
        Gizmos.DrawWireSphere(transform.position, sensorRange);
    }
}
