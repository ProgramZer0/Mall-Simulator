using TMPro;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// The phone's Store Hours app: open or close any of your stores from anywhere (like the store's own
/// open/close button). Locked until your team reaches Unlock Level.
///
/// Setup: like the Help app (duplicate that app object under the phone prefab's screen and swap HelpApp
/// for this).
/// </summary>
public class StoreHoursApp : MonoBehaviour, IPhoneApp
{
    [SerializeField] private string appName = "Store Hours";
    [SerializeField] private Sprite icon;
    [SerializeField] private PhoneUI.Style style = new();
    [Tooltip("Team level needed to use it (0 or 1: from the start).")]
    [SerializeField, Min(0)] private int unlockLevel = 5;

    public string AppName => appName;
    public Sprite Icon => icon;
    public int Badge => 0;

    public string LockedReason
    {
        get
        {
            var nm = NetworkManager.Singleton;
            ulong team = nm != null ? MallManager.GetTeamOf(nm.LocalClientId) : 0;
            return ProgressionManager.LevelOf(team) >= unlockLevel ? null : $"Level {unlockLevel}";
        }
    }

    private RectTransform content;
    private float nextRefresh;

    public void OnAppOpened()
    {
        if (content == null) content = PhoneUI.ScrollPage((RectTransform)transform);
        Build();
    }

    public void OnAppClosed() { }

    private string shape;

    // Rebuilt only when a store opens, closes or changes hands (rebuilding under the mouse eats clicks).
    private void Update()
    {
        if (content == null || Time.unscaledTime < nextRefresh) return;
        nextRefresh = Time.unscaledTime + 0.3f;
        if (Shape() != shape) Build();
    }

    private static string Shape()
    {
        var sb = new System.Text.StringBuilder();
        var nm = NetworkManager.Singleton;
        var mall = MallManager.Instance;
        if (nm == null || mall == null) return "";
        foreach (var plot in mall.Plots)
            if (plot != null && plot.HasOwner && plot.IsAuthorized(nm.LocalClientId))
                sb.Append(plot.PlotId).Append(plot.IsRenovating ? 'r' : plot.IsOpen ? 'o' : 'c');
        return sb.ToString();
    }

    private void Build()
    {
        shape = Shape();
        PhoneUI.Clear(content);
        float y = style.padding;
        var nm = NetworkManager.Singleton;
        var mall = MallManager.Instance;
        int shown = 0;
        if (nm != null && mall != null)
        {
            ulong me = nm.LocalClientId;
            foreach (var plot in mall.Plots)
            {
                if (plot == null || !plot.HasOwner || !plot.IsAuthorized(me)) continue;
                shown++;
                string state = plot.IsRenovating ? "Being renovated" : plot.IsOpen ? "<color=#6f6>Open</color>" : "<color=#f77>Closed</color>";
                PhoneUI.Text(content, plot.DisplayName, y, style.rowHeight, style, false, TextAlignmentOptions.Left);
                PhoneUI.Text(content, state, y, style.rowHeight, style, true, TextAlignmentOptions.Right);
                y += style.rowHeight;
                if (!plot.IsRenovating)
                {
                    var p = plot;
                    PhoneUI.Button(content, p.IsOpen ? "Close the store" : "Open the store", y, style.rowHeight, style, () =>
                    {
                        if (MallManager.Instance != null) MallManager.Instance.RequestSetStoreClosedServerRpc(p.PlotId, p.IsOpen);
                    });
                    y += style.rowHeight;
                }
                y += style.spacing * 2f;
            }
        }
        if (shown == 0)
        {
            PhoneUI.Text(content, "You don't have a store yet.", y, style.rowHeight, style, true);
            y += style.rowHeight;
        }
        content.sizeDelta = new Vector2(content.sizeDelta.x, y + style.padding);
    }
}
