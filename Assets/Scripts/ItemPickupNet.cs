using Unity.Netcode;
using UnityEngine;

public class ItemPickupNet : NetworkBehaviour
{
    [SerializeField] public int itemId;
}
