using Unity.Netcode;
using UnityEngine;

public abstract class InteractableNet : NetworkBehaviour
{
    [Tooltip("Only usable inside your own store or at a loading dock (e.g. carts). " +
             "Off = usable anywhere except inside another player's store (e.g. doors).")]
    [SerializeField] private bool onlyInOwnZones;

    public bool OnlyInOwnZones => onlyInOwnZones;

    public abstract bool ServerTryInteract(int itemId, ulong actorClientId);
}