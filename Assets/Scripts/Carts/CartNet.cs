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
    [Tooltip("Where the player holds the cart: an empty child at the handle, its blue arrow pointing INTO the cart " +
             "(the way you push). Grabbing puts you there, facing that way, and you must be within Grab Range of it. " +
             "Empty = grab from wherever you stand (the cart keeps the angle it was grabbed at).")]
    [SerializeField] private Transform handle;
    [Tooltip("With a Handle: how far in front of the player's middle the handle is, in meters.")]
    [SerializeField] private float handleReach = 0.45f;
    [Tooltip("Without a Handle: how far in front of the pusher the cart's middle sits, in meters.")]
    [SerializeField] private float pushDistance = 1.1f;
    [Tooltip("Layers that stop the cart (walls, shelves, items, other carts). Empty = the pusher's Placement Blocking Mask.")]
    [SerializeField] private LayerMask blockingMask;
    [Tooltip("Furthest a player can be from the Handle (or the cart's middle, without one) to start pushing, in meters.")]
    [SerializeField] private float grabRange = 1.5f;

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
    private readonly HashSet<Collider> ignoredWithPusher = new();
    private int ignoredChildCount = -1;
    private static readonly Collider[] Hits = new Collider[32];
    private static readonly RaycastHit[] DownHits = new RaycastHit[16];

    public StorePlot HomeStore => homeStore;

    /// <summary>Where a player has to be near to grab the cart: the Handle, or the body's middle.</summary>
    public Vector3 GrabPoint => handle != null ? handle.position : BodyCenter;

    /// <summary>Is this position close enough to grab the cart?</summary>
    public bool InGrabRange(Vector3 playerPosition)
    {
        Vector3 d = playerPosition - GrabPoint;
        d.y = 0f; // ignore height (the player's pivot may be at their middle)
        return d.magnitude <= grabRange;
    }

    /// <summary>The middle of the cart's body in the world (its pivot may be somewhere else).</summary>
    public Vector3 BodyCenter => transform.position + transform.rotation * Vector3.Scale(bodyBounds.center, transform.localScale);
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

        // Measured in the world, so a model imported lying down (e.g. from Blender, turned -90 on x)
        // still sits on its wheels.
        pivotAboveBase = transform.position.y - WorldBodyBounds(transform.rotation).min.y;

        if (loadArea != null) loadArea.enabled = false; // only its shape is used

        if (handle != null)
        {
            // The handle's spot and the cart's turn relative to the handle's facing, in the cart's own terms.
            handleOffset = Quaternion.Inverse(transform.rotation) * (handle.position - transform.position);
            handleRelative = Quaternion.Inverse(Quaternion.Euler(0f, FlatYaw(handle.forward), 0f)) * transform.rotation;
        }

        float offset = Vector3.Scale(bodyBounds.center, transform.localScale).magnitude;
        if (offset > 2f)
            Debug.LogWarning($"[CartNet] '{name}': the cart's model is {offset:0.0} m from the cart object's position. " +
                             "Set the mesh child's (and Load Area's) local position near 0,0,0 and move the cart object instead.", this);
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
        if (IsPushed || !CanUse(sender) || GetPushedBy(sender) != null)
        {
            Debug.LogWarning($"[CartNet] {sender} can't push '{name}': " +
                             (IsPushed ? "someone is already pushing it" : !CanUse(sender) ? "it isn't their store's cart" : "they're pushing another cart"), this);
            return;
        }
        if (!NetworkManager.ConnectedClients.TryGetValue(sender, out var client) || client.PlayerObject == null) return;
        // Measured to the handle (or the body's middle), not the pivot, with a little slack for lag.
        Vector3 d = client.PlayerObject.transform.position - GrabPoint;
        d.y = 0f;
        if (d.magnitude > grabRange + 0.75f)
        {
            Debug.LogWarning($"[CartNet] {sender} is too far from '{name}' to push it.", this);
            return;
        }

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
            if (player != null)
            {
                if (handle != null)
                {
                    // Step up to the handle, facing the way it pushes.
                    Vector3 fwd = Quaternion.Euler(0f, FlatYaw(handle.forward), 0f) * Vector3.forward;
                    var controller = player.GetComponent<PlayerController3D>();
                    if (controller != null)
                        controller.Teleport(handle.position - fwd * handleReach, Quaternion.LookRotation(fwd, Vector3.up));
                    pushRelative = handleRelative;
                }
                else
                {
                    // Turn the cart only by how much the player turns from here, so its tilt and the
                    // angle it was grabbed at are kept.
                    pushRelative = Quaternion.Inverse(Quaternion.Euler(0f, player.transform.eulerAngles.y, 0f)) * transform.rotation;
                }
            }
        }
    }

    private Vector3 handleOffset;                         // handle from the pivot, cart-local (scaled)
    private Quaternion handleRelative = Quaternion.identity; // cart rotation relative to the handle's facing
    private Quaternion pushRelative = Quaternion.identity;   // cart rotation relative to the pusher's facing

    private static float FlatYaw(Vector3 dir)
    {
        dir.y = 0f;
        return dir.sqrMagnitude < 1e-6f ? 0f : Quaternion.LookRotation(dir.normalized, Vector3.up).eulerAngles.y;
    }

    // The point the pusher holds (handle, or the body's middle) and how far in front of them it is.
    private Vector3 AnchorLocal => handle != null ? handleOffset : Vector3.Scale(bodyBounds.center, transform.localScale);
    private float Reach => handle != null ? handleReach : pushDistance;

    // Where the pusher stands for the cart at this pose.
    private Vector3 PusherSpot(Vector3 pos, Quaternion rot)
    {
        Vector3 dir = rot * Quaternion.Inverse(pushRelative) * Vector3.forward;
        dir.y = 0f;
        dir.Normalize();
        return pos + rot * AnchorLocal - dir * Reach;
    }

    // The cart body's box in the world at a rotation, around its pivot at the origin.
    private Bounds WorldBodyBounds(Quaternion rot)
    {
        Vector3 scale = transform.localScale;
        Vector3 c = transform.position + rot * Vector3.Scale(bodyBounds.center, scale);
        Vector3 e = Vector3.Scale(bodyBounds.extents, scale);
        Vector3 x = rot * new Vector3(e.x, 0f, 0f), y = rot * new Vector3(0f, e.y, 0f), z = rot * new Vector3(0f, 0f, e.z);
        Vector3 ext = new(Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x),
                          Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y),
                          Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z));
        return new Bounds(c, ext * 2f);
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

        // Where the cart wants to be: its handle (or middle) Reach in front of the player, facing with them.
        Quaternion targetRot = Quaternion.Euler(0f, FlatYaw(fwd), 0f) * pushRelative;
        Quaternion curRot = transform.rotation;
        Vector3 anchor = player.transform.position + fwd * Reach;
        Vector3 bodyAt = anchor - targetRot * AnchorLocal + targetRot * Vector3.Scale(bodyBounds.center, transform.localScale);
        float y = FloorHeightAt(bodyAt, player.transform) + pivotAboveBase;

        Vector3 PosFor(Quaternion r) { var p = anchor - r * AnchorLocal; p.y = y; return p; }

        // Try the full move; then without turning; then sliding along whichever way is free (so it
        // scrapes along a wall instead of stopping dead).
        Vector3 cur = transform.position;
        int mask = BlockingMask(player);
        bool found = false;
        Vector3 bestPos = cur;
        Quaternion bestRot = curRot;

        void Try(Vector3 p, Quaternion r)
        {
            if (found || !IsClearAt(p, r, mask, player.transform)) return;
            found = true;
            bestPos = p;
            bestRot = r;
        }

        Vector3 full = PosFor(targetRot);
        Try(full, targetRot);
        Try(PosFor(curRot), curRot);
        Vector3 d = full - cur;
        Try(new Vector3(cur.x + d.x, y, cur.z), targetRot);
        Try(new Vector3(cur.x, y, cur.z + d.z), targetRot);
        Try(new Vector3(cur.x + d.x, y, cur.z), curRot);
        Try(new Vector3(cur.x, y, cur.z + d.z), curRot);

        if (found) transform.SetPositionAndRotation(bestPos, bestRot);

        // Keep the pusher at the handle of wherever the cart actually ended up: they slide along with
        // it, or are held back when it's stuck. CharacterController.Move keeps them out of walls.
        Vector3 spot = PusherSpot(transform.position, transform.rotation);
        Vector3 correction = spot - player.transform.position;
        correction.y = 0f;
        if (correction.sqrMagnitude > 1e-6f)
        {
            var cc = player.GetComponent<CharacterController>();
            if (cc != null && cc.enabled) cc.Move(correction);
        }
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
        if (handle != null)
        {
            Gizmos.color = new Color(1f, 0.8f, 0.2f, 0.9f);
            Gizmos.DrawWireSphere(handle.position, 0.08f);
            Gizmos.DrawLine(handle.position, handle.position + handle.forward * 0.5f); // push direction
            Gizmos.color = new Color(1f, 0.8f, 0.2f, 0.25f);
            Gizmos.DrawWireSphere(handle.position, grabRange);
        }
        if (loadArea == null) return;
        Gizmos.color = new Color(0.4f, 1f, 0.4f, 0.8f);
        Gizmos.matrix = loadArea.transform.localToWorldMatrix;
        Gizmos.DrawWireCube(loadArea.center, loadArea.size);
        Gizmos.matrix = Matrix4x4.identity;
    }
}
