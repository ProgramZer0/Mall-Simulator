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

    private static readonly int ColorId = Shader.PropertyToID("_Color");

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
            return;
        }

        bool previewing = Input.GetMouseButton(1);
        bool confirmPlace = Input.GetMouseButtonDown(0);

        if (previewing)
            UpdateGhostPreview();
        else
            DestroyGhost();

        if (confirmPlace && ghostPreview != null)
        {
            if (!IsPlacementValid(ghostPreview)) return;

            if (TryGetPlacementPose(out Vector3 pos, out Quaternion rot))
            {
                inventory.RequestPlaceServerRpc(pos, rot);
                DestroyGhost();
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

        UpdateGhostColor(ghostPreview);
    }

    private void CreateGhost()
    {
        if (!ItemCatalog.Instance.TryGet(inventory.HeldItemId.Value, out var def))
            return;

        var prefab = def.GetGhostPrefab();
        if (prefab == null) return;

        ghostPreview = Instantiate(prefab);
        ghostPreview.SetActive(true);

        foreach (var col in ghostPreview.GetComponentsInChildren<Collider>())
            col.enabled = false;
    }

    private void DestroyGhost()
    {
        if (ghostPreview != null)
            Destroy(ghostPreview);
    }

    private void UpdateGhostColor(GameObject ghost)
    {
        bool valid = IsPlacementValid(ghost);
        Color c = valid ? Color.green : Color.red;
        c.a = 0.4f;

        var block = new MaterialPropertyBlock();
        block.SetColor(ColorId, c);

        foreach (var r in ghost.GetComponentsInChildren<Renderer>())
            r.SetPropertyBlock(block);
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

        var col = ghost.GetComponentInChildren<Collider>();
        if (col == null) return true;

        Vector3 center = col.bounds.center;
        Vector3 halfExtents = col.bounds.extents * 0.9f;

        if (!Physics.Raycast(center + Vector3.up * 0.1f,
            Vector3.down, 2f, LayerMask.GetMask("Ground")))
            return false;

        var hits = Physics.OverlapBox(
            center,
            halfExtents,
            ghost.transform.rotation,
            placementBlockingMask,
            QueryTriggerInteraction.Ignore
        );

        return hits.Length == 0;
    }
}
