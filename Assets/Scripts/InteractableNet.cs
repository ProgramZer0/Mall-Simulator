using Unity.Netcode;
using UnityEngine;

public abstract class InteractableNet : NetworkBehaviour
{
    public abstract bool ServerTryInteract(int itemId, ulong actorClientId);
}
