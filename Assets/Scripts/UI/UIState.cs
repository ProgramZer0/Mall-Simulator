using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Tracks whether any menu is open on this machine. While one is, the player stops moving,
/// looking and interacting, and the cursor is freed (PlayerController handles the cursor).
///
/// Use it from your menus:
///   UIState.OpenMenu(this);   // when the menu appears
///   UIState.CloseMenu(this);  // when it goes away
/// or just put a MenuScreen component on the menu panel and show/hide the panel.
///
/// Several menus can be open at once; input comes back when the last one closes.
/// Purely local: nothing here is networked.
/// </summary>
public static class UIState
{
    private static readonly HashSet<object> openMenus = new();

    /// <summary>Raised when a menu opens or closes.</summary>
    public static event Action Changed;

    /// <summary>True while at least one menu is open.</summary>
    public static bool IsMenuOpen
    {
        get
        {
            // A menu destroyed without closing (scene change, deleted panel) shouldn't lock input forever.
            openMenus.RemoveWhere(m => m == null || (m is UnityEngine.Object o && o == null));
            return openMenus.Count > 0;
        }
    }

    public static void OpenMenu(object menu)
    {
        if (menu != null && openMenus.Add(menu)) Changed?.Invoke();
    }

    public static void CloseMenu(object menu)
    {
        if (menu != null && openMenus.Remove(menu)) Changed?.Invoke();
    }

    /// <summary>Close everything (e.g. on disconnect).</summary>
    public static void CloseAll()
    {
        if (openMenus.Count == 0) return;
        openMenus.Clear();
        Changed?.Invoke();
    }

    // Clear between Play sessions even with domain reload turned off.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetOnPlay()
    {
        openMenus.Clear();
        Changed = null;
    }
}
