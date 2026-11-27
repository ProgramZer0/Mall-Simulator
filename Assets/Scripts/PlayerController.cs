using System.Collections;
using Unity.Netcode;
using UnityEngine;

public class PlayerController3D : NetworkBehaviour
{
    [Header("Movement")]
    [SerializeField] private float walkSpeed = 4.5f;
    [SerializeField] private float sprintSpeed = 7.0f;
    [SerializeField] private float jumpHeight = 1.2f;
    [SerializeField] private float gravity = -20f;
    [SerializeField] private float airControl = 0.5f;

    [Header("Crouch")]
    [Tooltip("Hold to crouch.")]
    [SerializeField] private KeyCode crouchKey = KeyCode.LeftControl;
    [Tooltip("Height of the CharacterController while crouched (standing height is whatever it's set to).")]
    [SerializeField] private float crouchHeight = 1.1f;
    [SerializeField] private float crouchSpeed = 2.2f;
    [Tooltip("How fast you go down / up, in meters per second.")]
    [SerializeField] private float crouchTransitionSpeed = 6f;
    [Tooltip("Optional: the body model, squashed down while crouched so other players see it.")]
    [SerializeField] private Transform crouchVisual;
    [Tooltip("Other things that should drop with you when you crouch (the hand anchor is included automatically). " +
             "Things under CameraRoot already move with the camera.")]
    [SerializeField] private Transform[] lowerWhenCrouching;

    // Synced so other players see you crouch (and bump into a shorter you).
    private readonly NetworkVariable<bool> crouching =
        new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    /// <summary>Crouched right now (synced: true on every machine).</summary>
    public bool IsCrouching => crouching.Value;

    private float standHeight;
    private Vector3 standCenter;
    private Vector3 cameraStandPos;
    private Vector3 visualStandScale;
    private float crouchAmount; // 0 standing .. 1 crouched
    private Transform[] lowered;        // what drops with the camera (hand anchor + Lower When Crouching)
    private Vector3[] loweredStandPos;

    [Header("Mouse Look")]
    [SerializeField] private float mouseSensitivity = 1.2f;
    [SerializeField] private float minPitch = -80f;
    [SerializeField] private float maxPitch = 80f;

    [Header("Spawn")]
    [Tooltip("When you join, move to the front of your team's store once it's assigned.")]
    [SerializeField] private bool spawnAtOwnStore = true;
    [Tooltip("Give up waiting for a store after this many seconds (stay where you spawned).")]
    [SerializeField] private float storeWaitTimeout = 10f;

    [Header("Falling")]
    [Tooltip("Wait for the floor under you after spawning (it may not be loaded yet on a joining machine) " +
             "before gravity starts, up to this many seconds.")]
    [SerializeField] private float waitForFloorSeconds = 5f;
    [Tooltip("Fell this far below the last place you stood: you're put back (at your store, or there).")]
    [SerializeField] private float fallRescueDistance = 15f;
    [Tooltip("Below this height you're always put back.")]
    [SerializeField] private float killHeight = -50f;
    [Tooltip("Still counts as standing this long after the floor was last touched. Running into something can " +
             "leave a frame or two touching only its side; without this, gravity builds up in them and you dip.")]
    [SerializeField, Min(0f)] private float groundedGrace = 0.15f;
    [Tooltip("Log to the Console when you drop more than 0.3 m below where you last stood without jumping " +
             "(what you were touching, and what's under you), to track down falling through the floor.")]
    [SerializeField] private bool debugFalls;
    [Tooltip("Floors you can never sink into: if your feet end up inside one (pushed in by a shelf product, a " +
             "step off a ledge...), you're lifted back on top instead of falling through. Empty: the Ground and " +
             "secondFloor layers.")]
    [SerializeField] private LayerMask floorLayers;
    [Tooltip("Lift you back up if your feet are at most this far inside a floor (meters). Deeper than this " +
             "you're past it, and keep falling (the fall rescue puts you back).")]
    [SerializeField, Min(0.05f)] private float maxSinkRescue = 1.2f;

    [Header("References")]
    [SerializeField] private Transform cameraRoot;

    private CharacterController controller;
    private Camera playerCamera;
    private float yaw;
    private float pitch;
    private Vector3 velocity;
    private StoreEditController storeEdit;

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        storeEdit = GetComponent<StoreEditController>();
        if (controller != null)
        {
            standHeight = controller.height;
            standCenter = controller.center;
        }
        if (crouchVisual != null) visualStandScale = crouchVisual.localScale;

        // The hand (held items, the scanner) and anything listed drop with the camera when crouching,
        // unless they're already under the camera.
        var list = new System.Collections.Generic.List<Transform>();
        var inv = GetComponent<PlayerInventoryNet>();
        if (inv != null && inv.HandAnchor != null) list.Add(inv.HandAnchor);
        if (lowerWhenCrouching != null)
            foreach (var t in lowerWhenCrouching) if (t != null && !list.Contains(t)) list.Add(t);
        Transform camRoot = cameraRoot != null ? cameraRoot : transform.Find("CameraRoot");
        list.RemoveAll(t => camRoot != null && t.IsChildOf(camRoot));
        lowered = list.ToArray();
        loweredStandPos = new Vector3[lowered.Length];
        for (int i = 0; i < lowered.Length; i++) loweredStandPos[i] = lowered[i].localPosition;

        // The CharacterController moves the player. A Rigidbody here would also pull every player down
        // with gravity on everyone else's machine, fighting the NetworkTransform (players sinking through
        // the floor and snapping back), so it's only kept as a kinematic body for triggers.
        if (TryGetComponent<Rigidbody>(out var body))
        {
            body.isKinematic = true;
            body.useGravity = false;
        }
        if (cameraRoot == null)
        {
            cameraRoot = transform.Find("CameraRoot");
        }

        if (cameraRoot != null)
        {
            playerCamera = cameraRoot.GetComponentInChildren<Camera>(true);
            cameraStandPos = cameraRoot.localPosition;
        }
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        if (!IsOwner)
        {
            // This component switches itself off for other players, so their crouch is applied from
            // the synced flag instead.
            crouching.OnValueChanged += OnRemoteCrouchChanged;
            OnRemoteCrouchChanged(false, crouching.Value);

            if (playerCamera != null) playerCamera.enabled = false;
            var listener = playerCamera != null ? playerCamera.GetComponent<AudioListener>() : null;
            if (listener != null) listener.enabled = false;

            enabled = false;
            return;
        }

        if (playerCamera != null) playerCamera.enabled = true;
        var audioListener = playerCamera != null ? playerCamera.GetComponent<AudioListener>() : null;
        if (audioListener != null) audioListener.enabled = true;

        localOwner = this;
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        if (spawnAtOwnStore) StartCoroutine(MoveToOwnStoreWhenAssigned());
    }

    /* ========================= SPAWN AT STORE ========================= */

    /// <summary>
    /// The host hands out starter stores as players join, which can land a moment after this
    /// player spawns. Wait for our team's store, then step to its front door once.
    /// </summary>
    private IEnumerator MoveToOwnStoreWhenAssigned()
    {
        float giveUpAt = Time.time + storeWaitTimeout;
        while (Time.time < giveUpAt)
        {
            var plot = FindOwnStore();
            if (plot != null)
            {
                plot.GetSpawnPose(out Vector3 pos, out Quaternion rot);
                Teleport(pos, rot);
                yield break;
            }
            yield return null;
        }
    }

    /// <summary>Our team's store, preferring the starter store if the team owns several.</summary>
    private StorePlot FindOwnStore()
    {
        var mall = MallManager.Instance;
        if (mall == null) return null;

        StorePlot any = null;
        foreach (var plot in mall.Plots)
        {
            if (!plot.IsAuthorized(OwnerClientId)) continue;
            if (plot.IsStarterPlot) return plot;
            any ??= plot;
        }
        return any;
    }

    /// <summary>Host: move this player (on their own machine, which moves them).</summary>
    [Rpc(SendTo.Owner)]
    public void TeleportOwnerRpc(Vector3 feetPosition, Quaternion facing) => Teleport(feetPosition, facing);

    /// <summary>Move the player (feet on the floor) and face a direction.</summary>
    public void Teleport(Vector3 feetPosition, Quaternion facing)
    {
        // Find the actual floor under the spot, in case the store's box sits a little off it.
        Vector3 pos = feetPosition;
        if (Physics.Raycast(feetPosition + Vector3.up * 2f, Vector3.down, out RaycastHit hit, 5f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) && !hit.collider.transform.IsChildOf(transform))
            pos.y = hit.point.y;

        // Lift by however far the controller's bottom is below the pivot.
        if (controller != null)
            pos.y += controller.height * 0.5f - controller.center.y + controller.skinWidth;

        // CharacterController overrides the transform while enabled, so switch it off to move.
        bool wasEnabled = controller != null && controller.enabled;
        if (controller != null) controller.enabled = false;

        transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, facing.eulerAngles.y, 0f));
        yaw = facing.eulerAngles.y;
        pitch = 0f;
        freeLookYaw = 0f;
        velocity = Vector3.zero;
        if (cameraRoot != null) cameraRoot.localRotation = Quaternion.identity;

        if (controller != null) controller.enabled = wasEnabled;
        lastSafe = pos; // a new place to fall from
        hasSafe = true;
    }

    // This machine's own player. Starting the mall swaps it for a fresh one, and the old one can go
    // after the new one has locked the cursor: only the one still ours frees it.
    private static PlayerController3D localOwner;

    public override void OnDestroy()
    {
        if (IsOwner && (localOwner == this || localOwner == null))
        {
            localOwner = null;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        base.OnDestroy();
    }

    private void Update()
    {
        if (!IsOwner) return;

        // Store edit mode takes over the mouse and keyboard with its own camera.
        if (storeEdit != null && storeEdit.IsEditing) return;

        // A menu is open: free the cursor and stop looking/walking (gravity still applies).
        bool menuOpen = UIState.IsMenuOpen;
        if (menuOpen)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        else if (wasMenuOpen)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
        wasMenuOpen = menuOpen;

        // Field of view from the player's settings (phone → Settings), live even with the phone up.
        if (playerCamera != null && GameSettings.FieldOfView > 0f && !Mathf.Approximately(playerCamera.fieldOfView, GameSettings.FieldOfView))
            playerCamera.fieldOfView = GameSettings.FieldOfView;
        if (!menuOpen) HandleLook();
        HandleMove(allowInput: !menuOpen);
    }

    private bool wasMenuOpen;

    [Header("Free Look (pushing a cart)")]
    [Tooltip("While pushing a cart, hold this mouse button to look around without turning (0 left, 1 right, 2 middle).")]
    [SerializeField] private int freeLookButton = 1;
    [Tooltip("How far you can look to either side, in degrees.")]
    [SerializeField] private float freeLookMaxAngle = 150f;
    [Tooltip("How fast the view swings back to straight ahead when you let go, in degrees per second.")]
    [SerializeField] private float freeLookReturnSpeed = 540f;
    private float freeLookYaw;

    private void HandleLook()
    {
        float sensitivity = mouseSensitivity * GameSettings.MouseSensitivity; // the player's own setting (phone → Settings)
        float mouseX = Input.GetAxis("Mouse X") * 100f * sensitivity * Time.deltaTime;
        float mouseY = Input.GetAxis("Mouse Y") * 100f * sensitivity * Time.deltaTime * (GameSettings.InvertY ? -1f : 1f);

        // Pushing a cart and holding the button: turn only the head, so the body (and the cart) keep going straight.
        bool freeLook = Input.GetMouseButton(freeLookButton) && CartNet.GetPushedBy(OwnerClientId) != null;
        if (freeLook) freeLookYaw = Mathf.Clamp(freeLookYaw + mouseX, -freeLookMaxAngle, freeLookMaxAngle);
        else
        {
            yaw += mouseX;
            freeLookYaw = Mathf.MoveTowards(freeLookYaw, 0f, freeLookReturnSpeed * Time.deltaTime);
        }
        pitch -= mouseY;
        pitch = Mathf.Clamp(pitch, minPitch, maxPitch);

        transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        if (cameraRoot != null)
        {
            cameraRoot.localRotation = Quaternion.Euler(pitch, freeLookYaw, 0f);
        }
    }

    /* ========================= CROUCH ========================= */

    private void OnRemoteCrouchChanged(bool previous, bool current)
    {
        if (controller == null) return;
        crouchAmount = current ? 1f : 0f;
        ApplyCrouch();
    }

    public override void OnNetworkDespawn()
    {
        crouching.OnValueChanged -= OnRemoteCrouchChanged;
        base.OnNetworkDespawn();
    }

    // Everyone runs this (Update returns early for other players, so it's driven from LateUpdate):
    // the owner decides, the rest follow the synced flag.
    private void LateUpdate()
    {
        if (controller == null || !IsSpawned) return;

        if (IsOwner)
        {
            bool wants = Input.GetKey(crouchKey) && !UIState.IsMenuOpen && !(storeEdit != null && storeEdit.IsEditing);
            // Standing back up needs headroom.
            if (!wants && crouching.Value && !HasHeadroom()) wants = true;
            if (crouching.Value != wants) crouching.Value = wants;
        }

        float target = crouching.Value ? 1f : 0f;
        if (Mathf.Approximately(crouchAmount, target)) return;
        float range = Mathf.Max(0.01f, standHeight - crouchHeight);
        crouchAmount = Mathf.MoveTowards(crouchAmount, target, crouchTransitionSpeed / range * Time.deltaTime);
        ApplyCrouch();
    }

    private void ApplyCrouch()
    {
        float drop = (standHeight - crouchHeight) * crouchAmount;
        // Shorter capsule with the feet where they were.
        controller.height = standHeight - drop;
        controller.center = standCenter - Vector3.up * (drop * 0.5f);
        if (cameraRoot != null) cameraRoot.localPosition = cameraStandPos - Vector3.up * drop;
        if (lowered != null)
            for (int i = 0; i < lowered.Length; i++)
                if (lowered[i] != null)
                {
                    // Down in the player's own space, whatever the thing's parent is.
                    Vector3 down = lowered[i].parent != null
                        ? lowered[i].parent.InverseTransformVector(transform.up * -drop) : Vector3.down * drop;
                    lowered[i].localPosition = loweredStandPos[i] + down;
                }
        if (crouchVisual != null)
        {
            float k = controller.height / Mathf.Max(0.01f, standHeight);
            crouchVisual.localScale = new Vector3(visualStandScale.x, visualStandScale.y * k, visualStandScale.z);
        }
    }

    // Room above to stand all the way up?
    private bool HasHeadroom()
    {
        float r = controller.radius * 0.95f;
        Vector3 feet = transform.position + controller.center - Vector3.up * (controller.height * 0.5f - controller.radius);
        Vector3 standingTop = transform.position + standCenter + Vector3.up * (standHeight * 0.5f - controller.radius);
        return !Physics.CheckCapsule(feet + Vector3.up * 0.05f, standingTop, r, Physics.DefaultRaycastLayers & ~(1 << gameObject.layer),
                                     QueryTriggerInteraction.Ignore);
    }

    private Vector3 lastSafe;
    private bool hasSafe;
    private float floorWaitUntil = -1f;
    private float nextSafeSample;

    // Holds the player still until there's floor under them (after spawning), and puts them back if
    // they fall out of the world.
    private bool FallGuard()
    {
        if (floorWaitUntil < 0f) floorWaitUntil = Time.time + waitForFloorSeconds;
        if (!hasSafe)
        {
            bool floor = Physics.Raycast(transform.position + Vector3.up * 0.5f, Vector3.down, out var hit, 50f,
                                         Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) &&
                         !hit.collider.transform.IsChildOf(transform);
            if (!floor && Time.time < floorWaitUntil)
            {
                velocity = Vector3.zero;
                return false; // not yet: don't fall into nothing
            }
            lastSafe = transform.position;
            hasSafe = true;
        }

        if (controller.isGrounded && Time.time >= nextSafeSample)
        {
            nextSafeSample = Time.time + 0.5f;
            lastSafe = transform.position;
        }

        if (transform.position.y < killHeight || transform.position.y < lastSafe.y - fallRescueDistance)
        {
            var store = FindOwnStore();
            if (store != null)
            {
                store.GetSpawnPose(out Vector3 pos, out Quaternion rot);
                Teleport(pos, rot);
            }
            else Teleport(lastSafe, transform.rotation);
            Debug.Log("[PlayerController] Fell out of the world: put back.", this);
            return false;
        }
        return true;
    }

    private float lastGroundedAt = -10f;
    private float lastGroundY;
    private bool jumped;
    private CollisionFlags lastFlags;

    private void HandleMove(bool allowInput = true)
    {
        if (!FallGuard()) return;
        if (controller.isGrounded)
        {
            lastGroundedAt = Time.time;
            lastGroundY = transform.position.y;
            jumped = false;
        }
        bool grounded = controller.isGrounded || (!jumped && Time.time - lastGroundedAt < groundedGrace);

        float h = allowInput ? Input.GetAxisRaw("Horizontal") : 0f;
        float v = allowInput ? Input.GetAxisRaw("Vertical") : 0f;
        Vector3 inputDir = new Vector3(h, 0f, v);
        inputDir = Vector3.ClampMagnitude(inputDir, 1f);

        Vector3 moveDir = transform.TransformDirection(inputDir);

        bool crouched = crouching.Value;
        var pushedCart = CartNet.GetPushedBy(OwnerClientId);
        float targetSpeed = crouched ? crouchSpeed
                          : allowInput && Input.GetKey(KeyCode.LeftShift) ? sprintSpeed : walkSpeed;

        Vector3 horizontalVel = new Vector3(velocity.x, 0f, velocity.z);
        Vector3 targetHorizontal = moveDir * targetSpeed;

        if (grounded)
        {
            horizontalVel = targetHorizontal;
            // No jumping while crouched. Jumping while pushing a cart lets go of it first.
            if (allowInput && !crouched && Input.GetKeyDown(KeyCode.Space))
            {
                if (pushedCart != null) pushedCart.RequestReleaseRpc();
                velocity.y = Mathf.Sqrt(-2f * gravity * jumpHeight);
                jumped = true;
            }
            else if (velocity.y < 0f)
            {
                velocity.y = -2f;
            }
        }
        else
        {
            horizontalVel = Vector3.Lerp(horizontalVel, targetHorizontal, airControl * Time.deltaTime);
        }

        velocity.y += gravity * Time.deltaTime;

        Vector3 finalVel = new Vector3(horizontalVel.x, velocity.y, horizontalVel.z);

        lastFlags = controller.Move(finalVel * Time.deltaTime);
        if (!controller.isGrounded && !jumped) LiftOutOfFloor();

        velocity.x = horizontalVel.x;
        velocity.z = horizontalVel.z;

        if (debugFalls && !jumped && transform.position.y < lastGroundY - 0.3f) LogDip();
    }

    // A CharacterController ignores anything it's already inside, so feet pushed into a thin floor keep
    // going down through it. Look down from the middle of the body for a floor top above the feet, and
    // put the feet back on it.
    private void LiftOutOfFloor()
    {
        int mask = floorLayers.value != 0 ? floorLayers.value : LayerMask.GetMask("Ground", "secondFloor");
        if (mask == 0) return;
        float half = controller.height * 0.5f;
        Vector3 middle = transform.position + controller.center;
        float feetY = middle.y - half;
        float from = Mathf.Min(half, maxSinkRescue); // above the feet: the middle, or less for a short drop
        if (!Physics.Raycast(new Vector3(middle.x, feetY + from, middle.z), Vector3.down, out var hit, from + 0.01f,
                             mask, QueryTriggerInteraction.Ignore)) return;
        if (hit.normal.y < 0.5f || hit.collider.transform.IsChildOf(transform)) return; // not a floor top
        float sunk = hit.point.y - feetY;
        if (sunk < 0.02f) return; // on it, not in it
        if (debugFalls)
            Debug.Log($"[PlayerController] Feet {sunk:0.00} m inside '{hit.collider.name}': lifted back on top.", this);
        bool was = controller.enabled;
        controller.enabled = false; // it ignores moves into what it's inside, so set the position
        transform.position += Vector3.up * (sunk + controller.skinWidth);
        controller.enabled = was;
        if (velocity.y < 0f) velocity.y = -2f;
        lastGroundedAt = Time.time;
    }

    private float nextDipLog;

    private void LogDip()
    {
        if (Time.time < nextDipLog) return;
        nextDipLog = Time.time + 0.5f;
        string under = Physics.Raycast(transform.position + Vector3.up * 0.5f, Vector3.down, out var hit, 5f,
                                       Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
            ? $"'{hit.collider.name}' (layer {LayerMask.LayerToName(hit.collider.gameObject.layer)}) {hit.distance - 0.5f:0.00} m below"
            : "nothing within 5 m";
        var near = Physics.OverlapCapsule(transform.position + controller.center + Vector3.up * (controller.height * 0.5f - controller.radius),
                                          transform.position + controller.center - Vector3.up * (controller.height * 0.5f - controller.radius),
                                          controller.radius + 0.1f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        var names = new System.Text.StringBuilder();
        foreach (var c in near)
            if (!c.transform.IsChildOf(transform)) names.Append($"'{c.name}' ");
        Debug.Log($"[PlayerController] Dipped {lastGroundY - transform.position.y:0.00} m below where I last stood " +
                  $"(at {transform.position}, grounded {controller.isGrounded}, last move hit {lastFlags}, " +
                  $"velocity {velocity}). Under me: {under}. Touching: {(names.Length > 0 ? names.ToString() : "nothing")}.", this);
    }
}