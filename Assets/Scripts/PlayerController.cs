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

    // Synced so other players see you crouch (and bump into a shorter you).
    private readonly NetworkVariable<bool> crouching =
        new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    private float standHeight;
    private Vector3 standCenter;
    private Vector3 cameraStandPos;
    private Vector3 visualStandScale;
    private float crouchAmount; // 0 standing .. 1 crouched

    [Header("Mouse Look")]
    [SerializeField] private float mouseSensitivity = 1.2f;
    [SerializeField] private float minPitch = -80f;
    [SerializeField] private float maxPitch = 80f;

    [Header("Spawn")]
    [Tooltip("When you join, move to the front of your team's store once it's assigned.")]
    [SerializeField] private bool spawnAtOwnStore = true;
    [Tooltip("Give up waiting for a store after this many seconds (stay where you spawned).")]
    [SerializeField] private float storeWaitTimeout = 10f;

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
    }

    private void OnDestroy()
    {
        if (IsOwner)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
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
        float mouseX = Input.GetAxis("Mouse X") * 100f * mouseSensitivity * Time.deltaTime;
        float mouseY = Input.GetAxis("Mouse Y") * 100f * mouseSensitivity * Time.deltaTime;

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

    private void HandleMove(bool allowInput = true)
    {
        bool grounded = controller.isGrounded;

        float h = allowInput ? Input.GetAxisRaw("Horizontal") : 0f;
        float v = allowInput ? Input.GetAxisRaw("Vertical") : 0f;
        Vector3 inputDir = new Vector3(h, 0f, v);
        inputDir = Vector3.ClampMagnitude(inputDir, 1f);

        Vector3 moveDir = transform.TransformDirection(inputDir);

        bool crouched = crouching.Value;
        bool pushingCart = CartNet.GetPushedBy(OwnerClientId) != null;
        float targetSpeed = crouched ? crouchSpeed
                          : allowInput && Input.GetKey(KeyCode.LeftShift) ? sprintSpeed : walkSpeed;

        Vector3 horizontalVel = new Vector3(velocity.x, 0f, velocity.z);
        Vector3 targetHorizontal = moveDir * targetSpeed;

        if (grounded)
        {
            horizontalVel = targetHorizontal;
            // No jumping while crouched or pushing a cart.
            if (allowInput && !crouched && !pushingCart && Input.GetKeyDown(KeyCode.Space))
            {
                velocity.y = Mathf.Sqrt(-2f * gravity * jumpHeight);
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

        controller.Move(finalVel * Time.deltaTime);

        velocity.x = horizontalVel.x;
        velocity.z = horizontalVel.z;
    }
}