using System.Collections.Generic;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

/// <summary>
/// A push cart that belongs to a store. Look at it with empty hands and press F to push it; F again
/// lets go. Anything placed inside its Load Area rides along with it.
///
/// Prefab / scene setup: NetworkObject + NetworkTransform (Authority Mode: Owner, so the person
/// pushing moves it smoothly) + a kinematic Rigidbody + its colliders + this. Add a child BoxCollider
/// for the Load Area: a flat box over the cart's bed, a bit taller than what you want to stack.
/// Put the cart's own colliders on a layer in the player's Placement Mask so items can be set on it.
/// Place carts in the scene (at the docks), set Home Store, and optionally a Home Spot to recall to.
///
/// Only the home store's team can push it, load it or unload it. While it's being pushed, the
/// pusher owns it (so its NetworkTransform follows them without lag); letting go hands it back to
/// the host. Items on it are parented to it on the host, so they move with it for everyone.
/// </summary>
public class CartNet : NetworkBehaviour
{
    public const ulong NoPusher = ulong.MaxValue;

    [Tooltip("The store this cart belongs to. Only its team can push and load it.")]
    [SerializeField] private StorePlot homeStore;
    [Tooltip("Where Recall sends it (e.g. a spot at the docks). Empty = where it started.")]
    [SerializeField] private Transform homeSpot;
    [Tooltip("A BoxCollider over the cart's bed. Items placed inside it ride along. Made a trigger and switched off at runtime.")]
    [SerializeField] private BoxCollider loadArea;

    [Header("Pushing")]
    [Tooltip("How far in front of the pusher the cart's middle sits, in meters.")]
    [SerializeField] private float pushDistance = 1.1f;
    [Tooltip("Layers that stop the cart (walls, shelves, items, other carts). Empty = the pusher's Placement Blocking Mask.")]
    [SerializeField] private LayerMask blockingMask;
    [Tooltip("Furthest a player can be to start pushing, in meters.")]
    [SerializeField] private float grabRange = 3f;

    /// <summary>Every spawned cart on this machine.</summary>
    public static readonly List<CartNet> All = new();

    private readonly NetworkVariable<ulong> pusher =
        new(NoPusher, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private NetworkTransform netTransform;
    private Bounds bodyBounds;       // the cart's own colliders, local space (before anything is loaded)
    private float pivotAboveBase;    // pivot height above the cart's lowest point
    private Vector3 startPos;
    private Quaternion startRot;

    // Pushing (owner side)
    private Vector3 lastPlayerPos;
    private readonly HashSet<Collider> ignoredWithPusher = new();
    private int ignoredChildCount = -1;
    private static readonly Collider[] Hits = new Collider[32];
    private static readonly RaycastHit[] DownHits = new RaycastHit[16];

    public StorePlot HomeStore => homeStore;
    public ulong Pusher => pusher.Value;
    public bool IsPushed => pusher.Value != NoPusher;

    private void Awake()
    {
        netTransform = GetComponent<NetworkTransform>();
        var rb = GetComponent<Rigidbody>();
        if (rb != null) rb.isKinematic = true;

        if (loadArea != null) loadArea.isTrigger = true; // so it isn't counted as part of the body
        bodyBounds = PlacementCheck.ComputeLocalBounds(gameObject);
        if (bodyBounds.size == Vector3.zero) bodyBounds = new Bounds(Vector3.up * 0.5f, new Vector3(0.8f, 1f, 1.2f));
        pivotAboveBase = -bodyBounds.min.y * transform.localScale.y;

        if (loadArea != null) loadArea.enabled = false; // only its shape is used
    }

    public override void OnNetworkSpawn()
    {
        All.Add(this);
        startPos = transform.position;
        startRot = transform.rotation;
        if (IsServer)
        {
            NetworkObject.DontDestroyWithOwner = true; // the pusher leaving mustn't delete the cart
            NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;
        }
        pusher.OnValueChanged += OnPusherChanged;
    }

    public override void OnNetworkDespawn()
    {
        All.Remove(this);
        if (IsServer && NetworkManager != null) NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
        pusher.OnValueChanged -= OnPusherChanged;
        RestorePusherCollisions();
    }

    /* ========================= QUERIES ========================= */

    /// <summary>Can this player push and load this cart? (Its home store's team.)</summary>
    public bool CanUse(ulong clientId) => homeStore == null || homeStore.IsAuthorized(clientId);

    /// <summary>Is this point inside the cart's load area?</summary>
    public bool InLoadArea(Vector3 worldPoint) => loadArea != null && StorePlot.BoxContains(loadArea, worldPoint);

    /// <summary>The cart whose load area holds this point, or null.</summary>
    public static CartNet FindCartAt(Vector3 worldPoint)
    {
        foreach (var cart in All)
            if (cart != null && cart.InLoadArea(worldPoint)) return cart;
        return null;
    }

    /// <summary>The cart this player is pushing, or null.</summary>
    public static CartNet GetPushedBy(ulong clientId)
    {
        foreach (var cart in All)
            if (cart != null && cart.pusher.Value == clientId) return cart;
        return null;
    }

    /// <summary>The carts that belong to a store.</summary>
    public static IEnumerable<CartNet> GetCartsOf(StorePlot plot)
    {
        foreach (var cart in All)
            if (cart != null && cart.homeStore == plot) yield return cart;
    }

    /* ========================= PUSH / LET GO ========================= */

    [Rpc(SendTo.Server, RequireOwnership = false)]
    public void RequestPushRpc(RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        if (IsPushed || !CanUse(sender) || GetPushedBy(sender) != null) return;
        if (!NetworkManager.ConnectedClients.TryGetValue(sender, out var client) || client.PlayerObject == null) return;
        if (Vector3.Distance(client.PlayerObject.transform.position, transform.position) > grabRange + 1f) return;

        pusher.Value = sender;
        NetworkObject.ChangeOwnership(sender);
    }

    [Rpc(SendTo.Server, RequireOwnership = false)]
    public void RequestReleaseRpc(RpcParams rpcParams = default)
    {
        if (pusher.Value != rpcParams.Receive.SenderClientId) return;
        ServerRelease();
    }

    /// <summary>Server: nobody's pushing it any more; the host owns it again.</summary>
    public void ServerRelease()
    {
        if (!IsServer) return;
        pusher.Value = NoPusher;
        if (OwnerClientId != NetworkManager.ServerClientId) NetworkObject.RemoveOwnership();
    }

    /// <summary>Server: send the cart (and whatever's on it) back to its home spot.</summary>
    public void ServerRecall()
    {
        if (!IsServer) return;
        ServerRelease();
        Vector3 pos = homeSpot != null ? homeSpot.position : startPos;
        Quaternion rot = homeSpot != null ? homeSpot.rotation : startRot;
        if (netTransform != null) netTransform.Teleport(pos, rot, transform.localScale);
        else transform.SetPositionAndRotation(pos, rot);
    }

    private void OnClientDisconnected(ulong clientId)
    {
        if (pusher.Value == clientId) ServerRelease();
    }

    private void OnPusherChanged(ulong previous, ulong current)
    {
        if (previous == NetworkManager.LocalClientId) RestorePusherCollisions();
        if (current == NetworkManager.LocalClientId)
        {
            var player = NetworkManager.LocalClient?.PlayerObject;
            if (player != null) lastPlayerPos = player.transform.position;
        }
    }

    /* ========================= LOADING (SERVER) ========================= */

    /// <summary>Server: put a spawned item on the cart so it rides along.</summary>
    public void ServerLoad(NetworkObject item)
    {
        if (!IsServer || item == null || !item.IsSpawned) return;
        if (item.transform.parent != transform) item.TrySetParent(NetworkObject, true);
    }

    /* ========================= PUSHING (PUSHER'S MACHINE) ========================= */

    private void LateUpdate()
    {
        if (!IsSpawned || pusher.Value != NetworkManager.LocalClientId || !IsOwner) return;

        var player = NetworkManager.LocalClient?.PlayerObject;
        if (player == null) return;

        // Edit mode or an open menu: let go.
        var edit = player.GetComponent<StoreEditController>();
        if ((edit != null && edit.IsEditing) || UIState.IsMenuOpen)
        {
            RequestReleaseRpc();
            return;
        }

        IgnorePusherCollisions(player);

        Vector3 fwd = player.transform.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-6f) return;
        fwd.Normalize();

        Quaternion targetRot = Quaternion.LookRotation(fwd, Vector3.up);
        Vector3 targetPos = player.transform.position + fwd * pushDistance;
        targetPos.y = FloorHeightAt(targetPos, player.transform) + pivotAboveBase;

        int mask = BlockingMask(player);
        if (IsClearAt(targetPos, targetRot, mask, player.transform))
            transform.SetPositionAndRotation(targetPos, targetRot);
        else if (IsClearAt(targetPos, transform.rotation, mask, player.transform))
            transform.position = targetPos; // can't turn here, but can move
        else
        {
            // Stuck: hold the pusher back so they don't walk away from (or through) the cart.
            var cc = player.GetComponent<CharacterController>();
            if (cc != null)
            {
                bool was = cc.enabled;
                cc.enabled = false;
                player.transform.position = new Vector3(lastPlayerPos.x, player.transform.position.y, lastPlayerPos.z);
                cc.enabled = was;
            }
        }
        lastPlayerPos = player.transform.position;
    }

    private int BlockingMask(NetworkObject player)
    {
        if (blockingMask.value != 0) return blockingMask.value;
        var interact = player.GetComponent<PlayerInteractInput>();
        return interact != null ? interact.PlacementBlockingMask.value : Physics.DefaultRaycastLayers;
    }

    // Would the cart's body fit here? Its own colliders, what's on it and the pusher don't count.
    private bool IsClearAt(Vector3 pos, Quaternion rot, int mask, Transform pushingPlayer)
    {
        Vector3 scale = transform.localScale;
        Vector3 half = Vector3.Scale(bodyBounds.extents, scale) - Vector3.one * 0.02f;
        half = Vector3.Max(half, Vector3.one * 0.01f);
        Vector3 center = pos + rot * Vector3.Scale(bodyBounds.center, scale) + Vector3.up * 0.03f; // just off the floor

        int n = Physics.OverlapBoxNonAlloc(center, half, Hits, rot, mask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var t = Hits[i].transform;
            if (t.IsChildOf(transform) || t.IsChildOf(pushingPlayer)) continue;
            int ground = LayerMask.NameToLayer("Ground");
            if (ground >= 0 && Hits[i].gameObject.layer == ground) continue;
            return false;
        }
        return true;
    }

    private float FloorHeightAt(Vector3 pos, Transform pushingPlayer)
    {
        int n = Physics.RaycastNonAlloc(pos + Vector3.up * 1f, Vector3.down, DownHits, 3f,
                                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        float best = float.NegativeInfinity;
        for (int i = 0; i < n; i++)
        {
            var t = DownHits[i].transform;
            if (t.IsChildOf(transform) || t.IsChildOf(pushingPlayer) || DownHits[i].normal.y < 0.7f) continue;
            // The highest floor below the cart's bed (so it doesn't climb onto things it bumps).
            float y = DownHits[i].point.y;
            if (y <= pos.y + 0.35f && y > best) best = y;
        }
        return float.IsNegativeInfinity(best) ? transform.position.y - pivotAboveBase : best;
    }

    // The pusher's capsule would bump into the cart (and what's on it) every frame otherwise.
    private void IgnorePusherCollisions(NetworkObject player)
    {
        int childCount = transform.hierarchyCount; // changes when something is loaded or unloaded
        if (childCount == ignoredChildCount) return;
        ignoredChildCount = childCount;

        var cc = player.GetComponent<CharacterController>();
        if (cc == null) return;
        foreach (var c in GetComponentsInChildren<Collider>())
        {
            if (c == null || c == cc || ignoredWithPusher.Contains(c)) continue;
            Physics.IgnoreCollision(cc, c, true);
            ignoredWithPusher.Add(c);
        }
    }

    private void RestorePusherCollisions()
    {
        var player = NetworkManager != null ? NetworkManager.LocalClient?.PlayerObject : null;
        var cc = player != null ? player.GetComponent<CharacterController>() : null;
        if (cc != null)
            foreach (var c in ignoredWithPusher)
                if (c != null) Physics.IgnoreCollision(cc, c, false);
        ignoredWithPusher.Clear();
        ignoredChildCount = -1;
    }

    private void OnValidate()
    {
        if (loadArea != null) loadArea.isTrigger = true;
    }

    private void OnDrawGizmosSelected()
    {
        if (loadArea == null) return;
        Gizmos.color = new Color(0.4f, 1f, 0.4f, 0.8f);
        Gizmos.matrix = loadArea.transform.localToWorldMatrix;
        Gizmos.DrawWireCube(loadArea.center, loadArea.size);
        Gizmos.matrix = Matrix4x4.identity;
    }
}
