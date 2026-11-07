using TMPro;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// The store checker: how appealing the store you're standing in (or editing) is. Shows quality, what's
/// for sale and seen from the hallway, its decor, tips to improve it, and each customer personality's
/// chance to come in and interest. Only for your own team's stores.
///
/// Setup: on a Canvas object; Root (shown / hidden) and Text (TMP, rich text). Toggle Key opens and
/// closes it (default I). Refreshes every Refresh Seconds while open.
/// </summary>
public class StoreCheckerUI : MonoBehaviour
{
    [SerializeField] private GameObject root;
    [SerializeField] private TMP_Text text;
    [SerializeField] private KeyCode toggleKey = KeyCode.I;
    [SerializeField] private float refreshSeconds = 0.5f;

    private bool open;
    private float nextRefresh;

    private void Awake()
    {
        if (root != null) root.SetActive(false);
    }

    private void Update()
    {
        if (Input.GetKeyDown(toggleKey) && !UIState.IsMenuOpen)
        {
            open = !open;
            nextRefresh = 0f;
        }

        var plot = open ? CurrentPlot() : null;
        bool show = plot != null;
        if (root != null && root.activeSelf != show) root.SetActive(show);
        if (!show || text == null || Time.unscaledTime < nextRefresh) return;
        nextRefresh = Time.unscaledTime + refreshSeconds;

        string s = plot.Appeal.Describe(true);
        if (text.text != s) text.text = s;
    }

    // The store being edited, or the one the local player stands in (own team only).
    private static StorePlot CurrentPlot()
    {
        var nm = NetworkManager.Singleton;
        var mall = MallManager.Instance;
        if (nm == null || mall == null || !nm.IsClient && !nm.IsServer) return null;

        var edited = mall.GetPlotEditedBy(nm.LocalClientId);
        if (edited != null) return edited;

        var player = nm.LocalClient?.PlayerObject;
        if (player == null) return null;
        var plot = mall.GetPlotAt(player.transform.position + Vector3.up * 0.1f);
        return plot != null && plot.IsAuthorized(nm.LocalClientId) ? plot : null;
    }
}
