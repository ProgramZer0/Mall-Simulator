using Unity.Netcode;
using UnityEngine;

public class PlayerInteractInput : NetworkBehaviour
{
    [SerializeField] private float interactRange = 3f;
    [SerializeField] private LayerMask interactMask = ~0; 
    [SerializeField] private LayerMask pickupMask = ~0;

    private Camera cam;
    private PlayerInventoryNet inventory;

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
        if (!IsOwner) return;
        if (cam == null) return;

        if (Input.GetMouseButtonDown(0))
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

        if (Input.GetMouseButtonDown(1) && inventory != null && inventory.HeldItemId.Value != 0)
        {
            Vector3 placePos;
            Quaternion placeRot = Quaternion.identity;

            if (Physics.Raycast(cam.transform.position, cam.transform.forward, out RaycastHit hit, 5f))
            {
                placePos = hit.point;
                placeRot = Quaternion.FromToRotation(Vector3.up, hit.normal);
            }
            else
            {
                placePos = cam.transform.position + cam.transform.forward * 2f;
            }

            inventory.RequestPlaceServerRpc(placePos, placeRot);
        }
    }
}
