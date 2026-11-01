using UnityEngine;

/// <summary>
/// Marks the shop monitor: look at it and press F to open the shop GUI for the store it stands in.
/// Only that store's team can use it.
///
/// Prefab setup: NetworkObject + ItemPickupNet (with Edit Mode Only ticked, so it can only be
/// moved in store edit mode) + a collider on the pickup layer + this. Give it an ItemCatalog
/// entry (World Network Prefab = the monitor) so edit mode can move it, and untick Show In Shop
/// if it shouldn't be for sale.
/// </summary>
public class ShopMonitor : MonoBehaviour
{
    /// <summary>The store this monitor is standing in (null in a hallway).</summary>
    public StorePlot GetPlot() =>
        MallManager.Instance != null ? MallManager.Instance.GetPlotAt(transform.position) : null;

    /// <summary>Can this player use it? (It must be in a store their team owns.)</summary>
    public bool CanUse(ulong clientId)
    {
        var plot = GetPlot();
        return plot != null && plot.IsAuthorized(clientId);
    }
}
