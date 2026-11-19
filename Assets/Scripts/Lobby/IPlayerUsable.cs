/// <summary>
/// Something the player uses with F that isn't one of the built-in kinds (lobby doors and buttons, the
/// look buttons...). Put a component that implements it on (or above) the object's collider.
/// </summary>
public interface IPlayerUsable
{
    /// <summary>The F prompt for this player ("F: Host a mall"), or null when it does nothing for them
    /// (then it isn't looked at).</summary>
    string Prompt(ulong clientId);
    /// <summary>F was pressed on it (on that player's machine).</summary>
    void Use(ulong clientId);
}
