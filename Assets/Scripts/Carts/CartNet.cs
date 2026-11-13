using System.Collections.Generic;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

/// <summary>
/// A push cart that belongs to a store. Look at it with empty hands and press F to push it; F again
/// lets go. Anything placed inside its Load Area rides along with it.
///
/// Prefab / scene setup: NetworkObject + NetworkTransform (Authority Mode: Owner) + Rigidbody + its
/// colliders + this. The colliders on a moving physics body must be convex: tick Convex on a
/// MeshCollider, or (smoother along walls) use a few BoxColliders. Add a child BoxCollider for the
/// Load Area: a box over the cart's bed, as tall as you'll stack. Put the cart's colliders on a layer
/// in the player's Placement Mask so items can be set on it. A Physic Material on the colliders sets
/// how much it bounces off walls (Bounciness) and how it scrapes along them (Friction).
///
/// Pushing is physics: while you push, the cart is a real physics body on your machine, driven
/// toward a spot in front of your hands like a stiff spring. Walls, shelves and other players stop
/// it, it slides along them and can bounce, and you're never moved by it. Pull too far away from it
/// (it's wedged) and you let go. Everyone else sees it through its NetworkTransform; when nobody is
/// pushing it, it's kinematic and can't be shoved around.
///
/// Only the home store's team can push it, load it or unload it. Items on it are parented to it on
/// the host, so they move with it for everyone.
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
    [Tooltip("How hard the cart chases the spot in front of your hands (higher = tighter, lower = floatier).")]
    [SerializeField] private float followStrength = 12f;
    [Tooltip("How hard the cart turns to face the way you face.")]
    [SerializeField] private float turnStrength = 10f;
    [Tooltip("Fastest the cart moves, in meters per second.")]
    [SerializeField] private float maxSpeed = 6f;
    [Tooltip("Fastest the cart turns, in degrees per second.")]
    [SerializeField] private float maxTurnSpeed = 240f;
    [Tooltip("Extra pull down while pushed, in m/s² on top of gravity, so the wheels stay on the floor.")]
    [SerializeField] private float extraGravity = 15f;
    [Tooltip("How far you can get ahead of where the cart's handle really is before the cart holds you " +
             "back, in meters. A little gives the loose, grabbed feel when turning; you can't walk through the " +
             "cart when it's stopped by a wall.")]
    [SerializeField] private float playerSlack = 0.3f;
    [Tooltip("If the handle ends up this far from where your hands are (the cart is stuck), you let go, in meters.")]
    [SerializeField] private float breakDistance = 1.5f;
    [Tooltip("Furthest a player can be from the Handle (or the cart's middle, without one) to start pushing, in meters.")]
    [SerializeField] private float grabRange = 1.5f;

    // For carts made at runtime (StoreUpgrades' bigger carts): the store, synced so everyone knows.
    private readonly NetworkVariable<int> homePlotId =
        new(-1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private bool hasHomePose;
    private Vector3 homePos;
    private Quaternion homeRot;

    /// <summary>Every spawned cart on this machine.</summary>
    public static readonly List<CartNet> All = new();

    private readonly NetworkVariable<ulong> pusher =
        new(NoPusher, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private NetworkTransform netTransform;
    private Rigidbody rb;
    private Bounds bodyBounds;       // the cart's own colliders, local space (before anything is loaded)
    private readonly List<Collider> bodyColliders = new(); // the cart's own colliders (not what's loaded)
    private bool simulating;         // this machine is running the cart's physics (it's the pusher)
    private float wheelBottom;       // the model's lowest point (the wheels) relative to the pivot, in meters
    private static readonly RaycastHit[] FloorHits = new RaycastHit[16];
    private bool releaseRequested;
    private Vector3 startPos;
    private Quaternion startRot;

    // Pushing (owner side)
    private readonly HashSet<Collider> ignoredWithPusher = new();
    private int ignoredChildCount = -1;
    private int loadIgnoreCount = -1;

    public StorePlot HomeStore
    {
        get
        {
            if (homeStore == null && homePlotId.Value >= 0 && MallManager.Instance != null)
                MallManager.Instance.TryGetPlot(homePlotId.Value, out homeStore);
            return homeStore;
        }
    }

    /// <summary>Where Recall sends it.</summary>
    public Pose HomePose => homeSpot != null ? new Pose(homeSpot.position, homeSpot.rotation)
        : hasHomePose ? new Pose(homePos, homeRot) : new Pose(startPos, startRot);

    /// <summary>Host, before spawning a cart made at runtime: its store and where Recall sends it.</summary>
    public void ServerSetHome(StorePlot plot, Pose home)
    {
        homeStore = plot;
        homePlotId.Value = plot != null ? plot.PlotId : -1;
        hasHomePose = true;
        homePos = home.position;
        homeRot = home.rotation;
    }

    /// <summary>Host: move everything riding on this cart onto another (it keeps its place in the world).</summary>
    public void ServerMoveLoadTo(CartNet other)
    {
        if (!IsServer || other == null) return;
        var riders = new List<NetworkObject>();
        foreach (Transform child in transform)
            if (child.TryGetComponent<NetworkObject>(out var no) && no.IsSpawned) riders.Add(no);
        foreach (var no in riders) no.TrySetParent(other.NetworkObject, true);
    }

    /// <summary>Which way the cart faces (degrees around up), for lining things up with it.</summary>
    public float Yaw => FlatYaw(transform.forward);

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
        rb = GetComponent<Rigidbody>();
        if (rb == null) Debug.LogWarning($"[CartNet] '{name}' needs a Rigidbody to be pushed.", this);
        else rb.isKinematic = true; // until someone pushes it

        if (loadArea != null) loadArea.isTrigger = true; // so it isn't counted as part of the body
        bodyBounds = PlacementCheck.ComputeLocalBounds(gameObject);
        if (bodyBounds.size == Vector3.zero) bodyBounds = new Bounds(Vector3.up * 0.5f, new Vector3(0.8f, 1f, 1.2f));

        // Where the wheels are: the bottom of what you see, not of the colliders (which may only cover
        // the cart's deck). Pushing keeps this on the floor.
        bool anyRenderer = false;
        float minY = 0f;
        foreach (var r in GetComponentsInChildren<Renderer>(true))
        {
            if (r is ParticleSystemRenderer) continue;
            if (!anyRenderer || r.bounds.min.y < minY) minY = r.bounds.min.y;
            anyRenderer = true;
        }
        wheelBottom = anyRenderer ? minY - transform.position.y : bodyBounds.min.y * transform.localScale.y;

        foreach (var c in GetComponentsInChildren<Collider>(true))
        {
            if (c.isTrigger) continue;
            bodyColliders.Add(c);
            if (c is MeshCollider mc && !mc.convex)
                Debug.LogWarning($"[CartNet] '{c.name}' is a non-convex MeshCollider; a pushed cart needs convex " +
                                 "colliders. Tick Convex, or use BoxColliders.", c);
        }

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
    public bool CanUse(ulong clientId) => HomeStore == null || HomeStore.IsAuthorized(clientId);

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
            if (cart != null && cart.HomeStore == plot) yield return cart;
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
        var home = HomePose;
        Vector3 pos = home.position;
        Quaternion rot = home.rotation;
        if (netTransform != null) netTransform.Teleport(pos, rot, transform.localScale);
        else transform.SetPositionAndRotation(pos, rot);
    }

    private void OnClientDisconnected(ulong clientId)
    {
        if (pusher.Value == clientId) ServerRelease();
    }

    private void OnPusherChanged(ulong previous, ulong current)
    {
        releaseRequested = false;
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

    /* ========================= LOADING (SERVER) ========================= */

    /// <summary>Server: put a spawned item on the cart so it rides along.</summary>
    public void ServerLoad(NetworkObject item)
    {
        if (!IsServer || item == null || !item.IsSpawned) return;
        if (item.transform.parent != transform) item.TrySetParent(NetworkObject, true);
    }

    /* ========================= PUSHING (PUSHER'S MACHINE) ========================= */

    private void Update()
    {
        if (!IsSpawned) return;

        // Physics runs only on the pusher's machine, and only while they own it.
        bool want = rb != null && IsOwner && pusher.Value == NetworkManager.LocalClientId;
        if (want != simulating) SetSimulating(want);
        if (!simulating) return;

        var player = NetworkManager.LocalClient?.PlayerObject;
        if (player == null) return;

        // Edit mode or an open menu: let go.
        var edit = player.GetComponent<StoreEditController>();
        if ((edit != null && edit.IsEditing) || UIState.IsMenuOpen) Release();
    }

    private void SetSimulating(bool on)
    {
        simulating = on;
        if (on)
        {
            rb.isKinematic = false;
            rb.useGravity = true;
            rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ; // never tips
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative; // no tunneling through thin floors
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            loadIgnoreCount = -1;
        }
        else
        {
            if (!rb.isKinematic)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
            rb.isKinematic = true;
            rb.interpolation = RigidbodyInterpolation.None;
        }
    }

    // After the player has moved this frame: if they've walked more than Player Slack forward past
    // where they should stand to hold the cart (it's stopped against something), hold them back.
    private void LateUpdate()
    {
        if (!simulating) return;
        var player = NetworkManager.LocalClient?.PlayerObject;
        if (player == null) return;
        var cc = player.GetComponent<CharacterController>();
        if (cc == null || !cc.enabled) return;

        Vector3 fwd = player.transform.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-6f) return;
        fwd.Normalize();

        // Where the player would stand to hold the handle where it actually is.
        Vector3 anchorNow = transform.position + transform.rotation * AnchorLocal;
        Vector3 spot = anchorNow - fwd * Reach;
        Vector3 offset = player.transform.position - spot;
        offset.y = 0f;

        // Only walking forward into the cart is held back. Sideways (turning) and falling behind are
        // left to the spring, so turning keeps its loose, grabbed feel.
        float over = Vector3.Dot(offset, fwd) - playerSlack;
        if (over > 0.001f) cc.Move(-fwd * over);
    }

    private void Release()
    {
        if (releaseRequested) return;
        releaseRequested = true;
        RequestReleaseRpc();
    }

    private void FixedUpdate()
    {
        if (!simulating) return;
        var player = NetworkManager.LocalClient?.PlayerObject;
        if (player == null) return;

        IgnorePusherCollisions(player);
        IgnoreLoadCollisions();

        Vector3 fwd = player.transform.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-6f) return;
        fwd.Normalize();

        // How far the handle (or middle) is from the spot in front of the player's hands.
        Vector3 anchorNow = rb.position + rb.rotation * AnchorLocal;
        Vector3 anchorWant = player.transform.position + fwd * Reach;
        Vector3 error = anchorWant - anchorNow;
        error.y = 0f;

        // Wedged and the player kept walking: they lose their grip.
        if (error.magnitude > breakDistance)
        {
            Release();
            return;
        }

        // Drive it like a stiff spring: velocity toward the spot, turning toward the player's facing.
        // Physics does the rest (walls stop it, it slides along them, it bounces per its Physic Material).
        Vector3 v = Vector3.ClampMagnitude(error * followStrength, maxSpeed);
        rb.linearVelocity = new Vector3(v.x, rb.linearVelocity.y, v.z);

        float cartYaw = FlatYaw(rb.rotation * Quaternion.Inverse(pushRelative) * Vector3.forward);
        float yawError = Mathf.DeltaAngle(cartYaw, FlatYaw(fwd));
        float turn = Mathf.Clamp(yawError * turnStrength, -maxTurnSpeed, maxTurnSpeed) * Mathf.Deg2Rad;
        rb.angularVelocity = new Vector3(0f, turn, 0f);

        // Stay flat: keep only the cart's turn (yaw), whatever a bump or the model's own axes tried to
        // tilt, and press it onto the floor. Its own lean as built (pushRelative) is kept.
        rb.rotation = Quaternion.Euler(0f, cartYaw, 0f) * pushRelative;
        KeepWheelsOnFloor(player.transform);
    }

    // The colliders may only cover the deck, which would let the wheels sink until the deck hits the
    // floor (or fall through a thin floor). So the floor under the cart is found with a ray and the
    // wheels are held on it: no gravity while they're on the floor, normal gravity plus Extra Gravity
    // when there's a drop.
    private void KeepWheelsOnFloor(Transform pushingPlayer)
    {
        Vector3 center = rb.position + rb.rotation * Vector3.Scale(bodyBounds.center, transform.localScale);
        float bottom = rb.position.y + wheelBottom;

        int n = Physics.RaycastNonAlloc(new Vector3(center.x, Mathf.Max(bottom + 0.6f, center.y), center.z), Vector3.down, FloorHits, 5f,
                                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        float floor = float.NegativeInfinity;
        for (int i = 0; i < n; i++)
        {
            var t = FloorHits[i].transform;
            if (t.IsChildOf(transform) || t.IsChildOf(pushingPlayer) || FloorHits[i].normal.y < 0.7f) continue;
            if (FloorHits[i].point.y > floor) floor = FloorHits[i].point.y;
        }

        bool grounded = !float.IsNegativeInfinity(floor) && bottom <= floor + 0.03f;
        rb.useGravity = !grounded;
        if (grounded)
        {
            Vector3 p = rb.position;
            p.y = floor - wheelBottom;
            rb.position = p;
            Vector3 v = rb.linearVelocity;
            if (v.y < 0f) rb.linearVelocity = new Vector3(v.x, 0f, v.z);
        }
        else rb.AddForce(Vector3.down * extraGravity, ForceMode.Acceleration);
    }

    // Loaded items are separate (kinematic) bodies riding on the cart. Touching the cart, they'd push
    // it around, so they don't collide with it.
    private void IgnoreLoadCollisions()
    {
        int count = transform.hierarchyCount;
        if (count == loadIgnoreCount) return;
        loadIgnoreCount = count;

        foreach (var c in GetComponentsInChildren<Collider>())
        {
            if (c == null || c.isTrigger || bodyColliders.Contains(c)) continue;
            foreach (var body in bodyColliders)
                if (body != null) Physics.IgnoreCollision(c, body, true);
        }
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
