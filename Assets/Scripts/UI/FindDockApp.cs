using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// The phone's Find My Dock app: your stores' delivery spots (their Shipment Zones), how far each is,
/// and a Show The Way button that draws a line on the floor from you to it (round corners, along the
/// walkable floor). The line follows you as you walk and goes away when you get there, after a while,
/// or when you tap Stop.
///
/// Setup: like the Help app (duplicate that app object under the phone prefab's screen and swap HelpApp
/// for this). Optional Line Material (an unlit one; empty: URP Unlit).
/// </summary>
public class FindDockApp : MonoBehaviour, IPhoneApp
{
    [SerializeField] private string appName = "Find My Dock";
    [SerializeField] private Sprite icon;
    [SerializeField] private PhoneUI.Style style = new();

    [Header("The line")]
    [SerializeField] private Material lineMaterial;
    [SerializeField] private Color lineColor = new(0.3f, 0.8f, 1f, 0.9f);
    [SerializeField, Min(0.01f)] private float lineWidth = 0.18f;
    [Tooltip("Drawn this far above the floor (meters).")]
    [SerializeField, Min(0f)] private float lineHeight = 0.12f;
    [Tooltip("It goes away when you're this close to the dock (meters).")]
    [SerializeField, Min(0.5f)] private float arriveDistance = 2.5f;
    [Tooltip("And after this many seconds anyway (0: only when you get there).")]
    [SerializeField, Min(0f)] private float showSeconds = 120f;

    public string AppName => appName;
    public Sprite Icon => icon;
    public int Badge => 0;

    private RectTransform content;
    private float nextRefresh;
    private readonly List<(string name, Vector3 point)> docks = new();

    // The way being shown (lives on its own object, so it stays when the phone's put away).
    private static DockGuide guide;

    public void OnAppOpened()
    {
        if (content == null) content = PhoneUI.ScrollPage((RectTransform)transform);
        Build();
    }

    public void OnAppClosed() { }

    // Distances update in place; rebuilt only when the docks or the line change (rebuilding under the
    // mouse would eat a click).
    private readonly List<(TMP_Text label, Vector3 point)> distances = new();
    private string shape;

    private void Update()
    {
        if (content == null || Time.unscaledTime < nextRefresh) return;
        nextRefresh = Time.unscaledTime + 0.5f;
        FindDocks();
        if (Shape() != shape) { Build(); return; }
        var me = Me();
        foreach (var (label, point) in distances)
            if (label != null) label.text = me != null ? $"{Vector3.Distance(me.position, point):0} m" : "";
    }

    private string Shape()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (name, point) in docks) sb.Append(name).Append(point.ToString("F1"));
        sb.Append(guide != null && guide.isActiveAndEnabled ? guide.Target.ToString("F1") + guide.NoPath : "-");
        return sb.ToString();
    }

    private void Build()
    {
        nextRefresh = Time.unscaledTime + 0.5f;
        PhoneUI.Clear(content);
        distances.Clear();
        float y = style.padding;
        FindDocks();
        shape = Shape();
        var me = Me();

        var head = PhoneUI.Text(content, "<b>Your docks</b>", y, style.rowHeight * 0.8f, style, true, TextAlignmentOptions.BottomLeft);
        head.color = PhoneTheme.AccentOf(style);
        y += style.rowHeight * 0.8f + style.spacing;
        if (docks.Count == 0)
        {
            PhoneUI.Text(content, "No deliveries come to you yet: they arrive at your store's dock once you have a store.",
                         y, style.rowHeight * 2f, style, true);
            y += style.rowHeight * 2f;
        }
        for (int i = 0; i < docks.Count; i++)
        {
            var (name, point) = docks[i];
            string far = me != null ? $"{Vector3.Distance(me.position, point):0} m" : "";
            PhoneUI.Text(content, name, y, style.rowHeight, style, false, TextAlignmentOptions.Left);
            var d = PhoneUI.Text(content, far, y, style.rowHeight, style, true, TextAlignmentOptions.Right);
            d.color = PhoneTheme.DimOf(style);
            distances.Add((d, point));
            y += style.rowHeight;
            bool showing = guide != null && guide.isActiveAndEnabled && (guide.Target - point).sqrMagnitude < 0.01f;
            Vector3 p = point;
            PhoneUI.Button(content, showing ? "Stop showing the way" : "Show the way", y, style.rowHeight, style, () =>
            {
                if (showing) StopGuide(); else StartGuide(p);
                Build();
            });
            y += style.rowHeight + style.spacing;
            if (showing && guide.NoPath)
            {
                var none = PhoneUI.Text(content, "No path found: there's no walkable way from here to this dock.",
                                        y, style.rowHeight * 1.6f, style, false);
                none.color = PhoneTheme.DimOf(style);
                y += style.rowHeight * 1.6f;
            }
            y += style.spacing;
        }
        content.sizeDelta = new Vector2(content.sizeDelta.x, y + style.padding);
    }

    // Each of the team's stores' shipment zones (named after the loading dock it's in, if any).
    private void FindDocks()
    {
        docks.Clear();
        var nm = NetworkManager.Singleton;
        var mall = MallManager.Instance;
        if (nm == null || mall == null) return;
        ulong team = MallManager.GetTeamOf(nm.LocalClientId);
        foreach (var plot in mall.Plots)
        {
            if (plot == null || !plot.HasOwner || plot.OwnerTeamId != team || plot.ShipmentZones == null) continue;
            int n = 0;
            foreach (var z in plot.ShipmentZones) if (z != null) n++;
            int k = 0;
            foreach (var z in plot.ShipmentZones)
            {
                if (z == null) continue;
                k++;
                Vector3 point = z.transform.TransformPoint(z.center); // (its collider is off at runtime: no bounds)
                var dock = LoadingDockZone.GetDockAt(point);
                string name = dock != null ? $"{plot.DisplayName}: {dock.DisplayName}" : $"{plot.DisplayName} dock";
                if (n > 1) name += $" {k}";
                docks.Add((name, point));
            }
        }
    }

    private static Transform Me()
    {
        var nm = NetworkManager.Singleton;
        var po = nm != null ? nm.LocalClient?.PlayerObject : null;
        return po != null ? po.transform : null;
    }

    private void StartGuide(Vector3 point)
    {
        if (guide == null)
        {
            var go = new GameObject("Dock Guide (line)");
            guide = go.AddComponent<DockGuide>();
        }
        guide.Show(point, lineMaterial, lineColor, lineWidth, lineHeight, arriveDistance, showSeconds);
    }

    private static void StopGuide()
    {
        if (guide != null) guide.Hide();
    }
}
