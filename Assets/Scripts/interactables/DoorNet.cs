using Unity.Netcode;
using UnityEngine;

public class DoorNet : InteractableNet
{
    [SerializeField] private int requiredItemId = 1;
    private NetworkVariable<bool> isOpen = new(writePerm: NetworkVariableWritePermission.Server);

    public override bool ServerTryInteract(int itemId, ulong actorClientId)
    {
        if (isOpen.Value) return false;
        if (itemId != requiredItemId) return false;
        isOpen.Value = true;
        // TODO: play animation locally on all clients when isOpen changes.
        return true;
    }
}
