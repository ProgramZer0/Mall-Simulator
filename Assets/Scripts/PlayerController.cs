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

    [Header("References")]
    [SerializeField] private Transform cameraRoot; 

    private CharacterController controller;
    private Camera playerCamera;
    private float yaw;
    private float pitch;
    private Vector3 velocity; 

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
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

        HandleLook();
        HandleMove();
    }

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

    private void HandleMove()
    {
        bool grounded = controller.isGrounded;

        float h = Input.GetAxisRaw("Horizontal");   
        float v = Input.GetAxisRaw("Vertical");    
        Vector3 inputDir = new Vector3(h, 0f, v);
        inputDir = Vector3.ClampMagnitude(inputDir, 1f);

        Vector3 moveDir = transform.TransformDirection(inputDir);

        float targetSpeed = Input.GetKey(KeyCode.LeftShift) ? sprintSpeed : walkSpeed;

        Vector3 horizontalVel = new Vector3(velocity.x, 0f, velocity.z);
        Vector3 targetHorizontal = moveDir * targetSpeed;

        if (grounded)
        {
            horizontalVel = targetHorizontal;
            if (Input.GetKeyDown(KeyCode.Space))
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