using UnityEngine;

/// <summary>Added by TruckBay to its dock door (when the door isn't under the bay): F on the door opens the truck.</summary>
public class TruckBayDoor : MonoBehaviour, IPlayerUsable
{
    public TruckBay bay;

    public string Prompt(ulong clientId) => bay != null ? bay.Prompt(clientId) : null;

    public void Use(ulong clientId) { if (bay != null) bay.Use(clientId); }
}
