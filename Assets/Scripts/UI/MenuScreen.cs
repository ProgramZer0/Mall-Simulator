using UnityEngine;

/// <summary>
/// Put this on a menu panel. While the panel is active, the player can't move, look or interact
/// and the cursor is free; hide the panel (SetActive(false)) and control comes back.
/// </summary>
public class MenuScreen : MonoBehaviour
{
    private void OnEnable() => UIState.OpenMenu(this);
    private void OnDisable() => UIState.CloseMenu(this);
}
