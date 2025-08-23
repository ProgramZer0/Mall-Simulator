using Unity.Netcode;
using UnityEngine;

public class PlayerInteractInput : NetworkBehaviour
{
    [SerializeField] private float interactRange = 3f;
    [SerializeField] private LayerMask interactMask;
    [SerializeField] private LayerMask pickupMask;
    [SerializeField] private LayerMask placementMask; 


    private Camera cam;
    private PlayerInventoryNet inventory;

    private float placeRotationY = 0f;
    private GameObject ghostPreview;

    private void Awake()
    {
        inventory = GetComponent<PlayerInventoryNet>();
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        if (IsOwner)
        {
            cam = GetComponentInChildren<Camera>(true);
        }
    }

    private void Update()
    {
        if (!IsOwner || cam == null) return;

        if (Input.GetKeyDown(KeyCode.F))
        {
            if (inventory != null && inventory.HeldItemId.Value == 0)
            {
                if (Physics.Raycast(cam.transform.position, cam.transform.forward, out RaycastHit hit, interactRange, pickupMask))
                {
                    var no = hit.collider.GetComponentInParent<NetworkObject>();
                    var pickup = hit.collider.GetComponentInParent<ItemPickupNet>();
                    if (no != null && pickup != null)
                        inventory.RequestPickupServerRpc(no);
                }
            }
            else
            {
                if (Physics.Raycast(cam.transform.position, cam.transform.forward, out RaycastHit hit, interactRange, interactMask))
                {
                    var no = hit.collider.GetComponentInParent<NetworkObject>();
                    var interactable = hit.collider.GetComponentInParent<InteractableNet>();
                    if (no != null && interactable != null)
                        inventory.RequestUseOnServerRpc(no);
                }
            }
        }

        if (Input.GetKeyDown(KeyCode.R))
        {
            placeRotationY += 90f;
            if (placeRotationY >= 360f) placeRotationY = 0f;
        }

        if (inventory != null && inventory.HeldItemId.Value != 0)
        {
            if (Input.GetMouseButton(1))
            {
                UpdateGhostPreview();
            }
            else
            {
                if (ghostPreview != null)
                {
                    Vector3 placePos;
                    Quaternion placeRot;

                    Vector3 rayStart = cam.transform.position + cam.transform.forward * 0.3f;
                    if (Physics.Raycast(rayStart, cam.transform.forward, out RaycastHit hit, 5f, placementMask, QueryTriggerInteraction.Ignore))
                    {
                        placePos = hit.point;
                        placeRot = Quaternion.Euler(0f, placeRotationY, 0f) * Quaternion.FromToRotation(Vector3.up, hit.normal);
                    }
                    else
                    {
                        placePos = cam.transform.position + cam.transform.forward * 2f;
                        placeRot = Quaternion.Euler(0f, placeRotationY, 0f);
                    }

                    inventory.RequestPlaceServerRpc(placePos, placeRot);

                    Destroy(ghostPreview);
                }
            }
        }
        else
        {
            if (ghostPreview != null)
                Destroy(ghostPreview);
        }
    }

    private void UpdateGhostPreview()
    {
        if (ghostPreview == null)
        {
            if (ItemCatalog.Instance.TryGet(inventory.HeldItemId.Value, out var def))
            {
                var ghostBase = def.GetGhostPrefab();
                if (ghostBase != null)
                {
                    ghostPreview = GameObject.Instantiate(ghostBase);
                    ghostPreview.SetActive(true);
                }
            }
        }

        if (ghostPreview == null) return;

        Vector3 targetPos;
        Quaternion targetRot;

        if (Physics.Raycast(cam.transform.position + cam.transform.forward * 0.5f, cam.transform.forward, out RaycastHit hit, 5f))
        {
            targetPos = hit.point;
            targetRot = Quaternion.Euler(0f, placeRotationY, 0f) * Quaternion.FromToRotation(Vector3.up, hit.normal);
        }
        else
        {
            targetPos = cam.transform.position + cam.transform.forward * 2f;
            targetRot = Quaternion.Euler(0f, placeRotationY, 0f);
        }

        ghostPreview.transform.position = targetPos;
        ghostPreview.transform.rotation = targetRot;

        UpdateGhostColor(ghostPreview);
    }

    private void UpdateGhostColor(GameObject ghost)
    {
        bool valid = IsPlacementValid(ghost);

        foreach (var r in ghost.GetComponentsInChildren<Renderer>())
        {
            foreach (var mat in r.materials)
            {
                Color baseColor = valid ? Color.green : Color.red;
                mat.color = new Color(baseColor.r, baseColor.g, baseColor.b, 0.4f);
            }
        }
    }
    private bool IsPlacementValid(GameObject ghost)
    {
        if (ghost == null) return false;

        Collider ghostCollider = ghost.GetComponentInChildren<Collider>();
        if (ghostCollider == null) return true; 

        Vector3 pos = ghost.transform.position;
        Quaternion rot = ghost.transform.rotation;
        Vector3 size = ghostCollider.bounds.size * 0.9f; 

        LayerMask mask = ~LayerMask.GetMask("Ground"); 
        Collider[] hits = Physics.OverlapBox(pos, size / 2f, rot, mask);

        return hits.Length == 0;
    }
}
