using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public class PlayerInteractInput : NetworkBehaviour
{
    [SerializeField] private float interactRange = 3f;
    [SerializeField] private LayerMask interactMask;
    [SerializeField] private LayerMask pickupMask;
    [SerializeField] private LayerMask placementMask;
    [SerializeField] private LayerMask placementBlockingMask;

    private Camera cam;
    private PlayerInventoryNet inventory;

    private float placeRotationY;
    private GameObject ghostPreview;

    // After a place request, keep the hand item hidden until the server answers.
    // If the server rejects the place (HeldItemId never changes), it reappears after this.
    [SerializeField] private float placeResponseTimeout = 0.5f;
    private float placePendingUntil = -1f;

    [Header("Ghost Tint")]
    [Tooltip("How strongly an invalid placement is washed toward Invalid Color. 0 = no tint at all.")]
    [Range(0f, 1f)]
    [SerializeField] private float invalidTintStrength = 0.35f;
    [SerializeField] private Color invalidColor = new(1f, 0.25f, 0.25f, 1f);

    private readonly List<(Material mat, Color original)> ghostMaterials = new();
    private bool? ghostShownValid;

    private void Awake()
    {
        inventory = GetComponent<PlayerInventoryNet>();
    }

    public override void OnNetworkSpawn()
    {
        if (IsOwner)
            cam = GetComponentInChildren<Camera>(true);
    }

    private void Update()
    {
        if (!IsOwner || cam == null) return;

        HandleInteract();
        HandleRotation();
        HandlePlacement();
    }

    /* ========================= INTERACT ========================= */

    private void HandleInteract()
    {
        if (!Input.GetKeyDown(KeyCode.F)) return;

        if (inventory.HeldItemId.Value == 0)
        {
            if (Physics.Raycast(cam.transform.position, cam.transform.forward,
                out RaycastHit hit, interactRange, pickupMask))
            {
                var no = hit.collider.GetComponentInParent<NetworkObject>();
                var pickup = hit.collider.GetComponentInParent<ItemPickupNet>();

                if (no && pickup)
                    inventory.RequestPickupServerRpc(no);
            }
        }
        else
        {
            if (Physics.Raycast(cam.transform.position, cam.transform.forward,
                out RaycastHit hit, interactRange, interactMask))
            {
                var no = hit.collider.GetComponentInParent<NetworkObject>();
                var interactable = hit.collider.GetComponentInParent<InteractableNet>();

                if (no && interactable)
                    inventory.RequestUseOnServerRpc(no);
            }
        }
    }

    /* ========================= ROTATION ========================= */

    private void HandleRotation()
    {
        if (!Input.GetKeyDown(KeyCode.R)) return;

        placeRotationY = (placeRotationY + 90f) % 360f;
    }

    /* ========================= PLACEMENT ========================= */

    private void HandlePlacement()
    {
        if (inventory.HeldItemId.Value == 0)
        {
            DestroyGhost();
            placePendingUntil = -1f;
            return;
        }

        bool previewing = Input.GetMouseButton(1);
        bool confirmPlace = Input.GetMouseButtonDown(0);
        bool placePending = Time.time < placePendingUntil;

        if (previewing && !placePending)
        {
            UpdateGhostPreview();
            inventory.SetHandVisualHidden(true);
        }
        else
        {
            DestroyGhost();
            // Cancelled (or server rejected the place): put the item back in hand.
            if (!placePending)
                inventory.SetHandVisualHidden(false);
        }

        if (confirmPlace && ghostPreview != null)
        {
            if (!IsPlacementValid(ghostPreview)) return;

            if (TryGetPlacementPose(out Vector3 pos, out Quaternion rot))
            {
                inventory.RequestPlaceServerRpc(pos, rot);
                DestroyGhost();
                // Stay hidden so the item doesn't flash back into the hand before the server confirms.
                placePendingUntil = Time.time + placeResponseTimeout;
            }
        }
    }

    /* ========================= GHOST ========================= */

    private void UpdateGhostPreview()
    {
        if (ghostPreview == null)
            CreateGhost();

        if (ghostPreview == null) return;

        if (TryGetPlacementPose(out Vector3 pos, out Quaternion rot))
        {
            ghostPreview.transform.SetPositionAndRotation(pos, rot);
        }

        UpdateGhostColor(IsPlacementValid(ghostPreview));
    }

    private void CreateGhost()
    {
        if (!ItemCatalog.Instance.TryGet(inventory.HeldItemId.Value, out var def))
            return;

        var prefab = def.GetGhostPrefab();
        if (prefab == null) return;

        ghostPreview = Instantiate(prefab);
        ghostPreview.SetActive(true);

        // Per-ghost material copies so tinting never touches the cached template.
        ghostMaterials.Clear();
        foreach (var r in ghostPreview.GetComponentsInChildren<Renderer>())
            foreach (var mat in r.materials)
                ghostMaterials.Add((mat, GhostBuilder.GetColor(mat)));

        ghostShownValid = null;
    }

    private void DestroyGhost()
    {
        if (ghostPreview == null) return;

        foreach (var (mat, _) in ghostMaterials)
            if (mat != null) Destroy(mat);
        ghostMaterials.Clear();

        Destroy(ghostPreview);
        ghostPreview = null;
        ghostShownValid = null;
    }

    /// <summary>
    /// Valid: the item's own colors, just transparent. Invalid: a light red wash.
    /// Only touches materials when validity actually changes.
    /// </summary>
    private void UpdateGhostColor(bool valid)
    {
        if (ghostShownValid == valid) return;
        ghostShownValid = valid;

        foreach (var (mat, original) in ghostMaterials)
        {
            if (mat == null) continue;

            Color c = valid ? original : Color.Lerp(original, invalidColor, invalidTintStrength);
            c.a = original.a;
            GhostBuilder.SetColor(mat, c);
        }
    }

    /* ========================= PLACEMENT LOGIC ========================= */

    private bool TryGetPlacementPose(out Vector3 pos, out Quaternion rot)
    {
        Vector3 rayStart = cam.transform.position + cam.transform.forward * 0.5f;

        if (Physics.Raycast(rayStart, cam.transform.forward,
            out RaycastHit hit, 5f, placementMask, QueryTriggerInteraction.Ignore))
        {
            pos = hit.point;

            if (hit.collider.gameObject.layer == LayerMask.NameToLayer("Ground"))
                rot = Quaternion.Euler(0f, placeRotationY, 0f);
            else
                rot = Quaternion.Euler(0f, placeRotationY, 0f) *
                      Quaternion.FromToRotation(Vector3.up, hit.normal);

            return true;
        }

        pos = cam.transform.position + cam.transform.forward * 2f;
        rot = Quaternion.Euler(0f, placeRotationY, 0f);
        return true;
    }

    private bool IsPlacementValid(GameObject ghost)
    {
        if (ghost == null) return false;
        return FitsAt(inventory.HeldItemId.Value, ghost.transform.position, ghost.transform.rotation);
    }

    /// <summary>
    /// Fit test using this player's placement layers. Called on the owner for the preview
    /// and on the server before spawning (see PlayerInventoryNet.RequestPlaceServerRpc).
    /// </summary>
    public bool FitsAt(int itemId, Vector3 position, Quaternion rotation)
    {
        if (ItemCatalog.Instance == null || !ItemCatalog.Instance.TryGet(itemId, out var def))
            return false;

        // The floor must never count as "in the way", even if it's ticked in the blocking mask.
        int ground = LayerMask.NameToLayer("Ground");
        int blocking = placementBlockingMask.value;
        if (ground >= 0) blocking &= ~(1 << ground);

        return PlacementCheck.Fits(def, position, rotation, blocking, placementMask);
    }
}