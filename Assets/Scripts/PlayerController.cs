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
        if (cameraRoot == null)
        {
            cameraRoot = transform.Find("CameraRoot");
        }

        if (cameraRoot != null)
        {
            playerCamera = cameraRoot.GetComponentInChildren<Camera>(true);
        }
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        if (!IsOwner)
        {
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

    private void HandleLook()
    {
        float mouseX = Input.GetAxis("Mouse X") * 100f * mouseSensitivity * Time.deltaTime;
        float mouseY = Input.GetAxis("Mouse Y") * 100f * mouseSensitivity * Time.deltaTime;

        yaw += mouseX;
        pitch -= mouseY;
        pitch = Mathf.Clamp(pitch, minPitch, maxPitch);

        transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        if (cameraRoot != null)
        {
            cameraRoot.localRotation = Quaternion.Euler(pitch, 0f, 0f);
        }
    }

    private void HandleMove(bool allowInput = true)
    {
        bool grounded = controller.isGrounded;

        float h = allowInput ? Input.GetAxisRaw("Horizontal") : 0f;
        float v = allowInput ? Input.GetAxisRaw("Vertical") : 0f;
        Vector3 inputDir = new Vector3(h, 0f, v);
        inputDir = Vector3.ClampMagnitude(inputDir, 1f);

        Vector3 moveDir = transform.TransformDirection(inputDir);

        float targetSpeed = allowInput && Input.GetKey(KeyCode.LeftShift) ? sprintSpeed : walkSpeed;

        Vector3 horizontalVel = new Vector3(velocity.x, 0f, velocity.z);
        Vector3 targetHorizontal = moveDir * targetSpeed;

        if (grounded)
        {
            horizontalVel = targetHorizontal;
            if (allowInput && Input.GetKeyDown(KeyCode.Space))
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