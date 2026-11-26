using System.Collections.Generic;
using System.Text;
using TMPro;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// The phone's Shipments app: every delivery your team has coming, with which store it's for, what's in it,
/// and where it is: on its way (arrives in m:ss), in line behind another, waiting for a truck bay, at the
/// dock (door shut: leaves in m:ss unless opened, with a part refund), or unloading (leaves in m:ss with
/// whatever's still on it). Updates every second while open.
///
/// Setup: like the Help app (duplicate that app object under the phone prefab's screen and swap HelpApp
/// for this).
/// </summary>
public class ShipmentsApp : MonoBehaviour, IPhoneApp
{
    [SerializeField] private string appName = "Shipments";
    [SerializeField] private Sprite icon;
    [SerializeField] private PhoneUI.Style style = new();
    [SerializeField] private string[] truckNames = { "Small truck", "Truck", "Big truck" };

    public string AppName => appName;
    public Sprite Icon => icon;
    public int Badge
    {
        get
        {
            // Trucks at the dock waiting to be opened or unloaded.
            var em = EconomyManager.Instance;
            var nm = NetworkManager.Singleton;
            if (em == null || !em.IsSpawned || nm == null) return 0;
            int n = 0;
            foreach (var o in em.GetOrdersForTeam(MallManager.GetTeamOf(nm.LocalClientId)))
                if (o.truckSize >= 0 && o.stage >= EconomyManager.OrderInfo.AtDock) n++;
            return n;
        }
    }

    private RectTransform content;
    private float nextRefresh;
    private string shape;
    private readonly List<(TMP_Text label, int orderId)> statuses = new();

    public void OnAppOpened()
    {
        if (content == null) content = PhoneUI.ScrollPage((RectTransform)transform);
        Build();
    }

    public void OnAppClosed() { }

    // Statuses (the times) update in place; the page is rebuilt when deliveries come or go or change stage.
    private void Update()
    {
        if (content == null || Time.unscaledTime < nextRefresh) return;
        nextRefresh = Time.unscaledTime + 1f;
        var list = Orders();
        if (Shape(list) + confirming != shape) { Build(); return; }
        foreach (var (label, id) in statuses)
            foreach (var o in list)
                if (o.orderId == id && label != null) label.text = Status(o);
    }

    private static List<EconomyManager.OrderInfo> Orders()
    {
        var em = EconomyManager.Instance;
        var nm = NetworkManager.Singleton;
        if (em == null || !em.IsSpawned || nm == null) return new List<EconomyManager.OrderInfo>();
        return em.GetOrdersForTeam(MallManager.GetTeamOf(nm.LocalClientId));
    }

    private static string Shape(List<EconomyManager.OrderInfo> list)
    {
        var sb = new StringBuilder();
        foreach (var o in list) sb.Append(o.orderId).Append(':').Append(o.stage).Append(o.inLine ? 'l' : '-').Append(o.blocked ? 'b' : '-').Append(',');
        return sb.ToString();
    }

    private int confirming = -1; // the delivery being asked about (Send it off now)

    private void Build()
    {
        nextRefresh = Time.unscaledTime + 1f;
        PhoneUI.Clear(content);
        statuses.Clear();
        var list = Orders();
        shape = Shape(list) + confirming;
        float y = style.padding;
        if (list.Count == 0)
        {
            PhoneUI.Text(content, "Nothing on its way. Orders from the shop show up here.", y, style.rowHeight * 2f, style, true);
            y += style.rowHeight * 2f;
        }
        var em = EconomyManager.Instance;
        var mall = MallManager.Instance;
        var catalog = ItemCatalog.Instance;
        foreach (var o in list)
        {
            string store = mall != null && mall.TryGetPlot(o.plotId, out var plot) ? plot.DisplayName : "your store";
            string truck = o.truckSize >= 0 && o.truckSize < truckNames.Length ? $"{truckNames[o.truckSize]} · " : "";
            var head = PhoneUI.Text(content, $"<b>{store}</b>  <size=80%>{truck}{o.boxCount} thing{(o.boxCount == 1 ? "" : "s")}</size>",
                                    y, style.rowHeight, style, false, TextAlignmentOptions.Left);
            head.color = PhoneTheme.AccentOf(style);
            y += style.rowHeight;
            var status = PhoneUI.Text(content, Status(o), y, style.rowHeight, style, false, TextAlignmentOptions.Left);
            statuses.Add((status, o.orderId));
            y += style.rowHeight;

            // At the dock: send it off now (asks first).
            if (o.truckSize >= 0 && (o.stage == EconomyManager.OrderInfo.AtDock || o.stage == EconomyManager.OrderInfo.Unloading))
            {
                int id = o.orderId;
                if (confirming == id)
                {
                    var ask = PhoneUI.Text(content, "<color=#ffb060>Send this truck off now?</color> Everything still on it goes " +
                                           "with it, and there's no refund.", y, style.rowHeight * 1.6f, style, true);
                    y += style.rowHeight * 1.6f;
                    PhoneUI.Button(content, "Yes, send it off", y, style.rowHeight, style, () =>
                    {
                        confirming = -1;
                        if (EconomyManager.Instance != null) EconomyManager.Instance.RequestSendOff(id);
                        Build();
                    });
                    y += style.rowHeight + style.spacing;
                    PhoneUI.Button(content, "Cancel", y, style.rowHeight, style, () => { confirming = -1; Build(); });
                    y += style.rowHeight + style.spacing;
                }
                else
                {
                    PhoneUI.Button(content, "Send it off now", y, style.rowHeight, style, () => { confirming = id; Build(); });
                    y += style.rowHeight + style.spacing;
                }
            }

            // What's in it.
            var sb = new StringBuilder();
            if (em != null)
                foreach (var line in em.GetOrderLines(o.orderId))
                {
                    string name = catalog != null && catalog.TryGet(line.itemId, out var def) ? def.displayName : $"Item {line.itemId}";
                    string color = line.paint != 0 ? $" ({PaintPalette.NameOf(line.paint)})" : "";
                    sb.Append("• ").Append(name).Append(color).Append(" ×").Append(line.quantity).Append('\n');
                }
            var contents = PhoneUI.Text(content, sb.ToString().TrimEnd(), y, 10f, style, true);
            float width = Mathf.Max(50f, (content.rect.width > 0f ? content.rect.width : 300f) - style.padding * 2f);
            float h = contents.GetPreferredValues(contents.text, width, 0f).y + 4f;
            var rt = (RectTransform)contents.transform;
            rt.offsetMin = new Vector2(rt.offsetMin.x, -y - h);
            y += h + style.spacing * 3f;
        }
        content.sizeDelta = new Vector2(content.sizeDelta.x, y + style.padding);
    }

    private static string Status(EconomyManager.OrderInfo o)
    {
        var em = EconomyManager.Instance;
        double now = NetworkManager.Singleton != null ? NetworkManager.Singleton.ServerTime.Time : 0d;
        string Clock(double seconds)
        {
            int s = Mathf.Max(0, Mathf.CeilToInt((float)seconds));
            return $"{s / 60}:{s % 60:00}";
        }
        if (o.inLine) return "In line behind another delivery";
        if (o.truckSize < 0)
        {
            float left = em != null ? em.SecondsUntil(o) : o.remaining;
            if (o.blocked) return "<color=#ffb060>Here: waiting for room on your shipment spots</color>";
            return left > 0f ? $"Arrives in {Clock(left)}" : "Arriving";
        }
        switch (o.stage)
        {
            case EconomyManager.OrderInfo.WaitingForBay: return "<color=#ffb060>Waiting for a free truck bay</color>";
            case EconomyManager.OrderInfo.AtDock: return $"<color=#7fd0ff>At the dock: open it (F)!</color> Leaves in {Clock(o.deadline - now)}";
            case EconomyManager.OrderInfo.Unloading: return $"<color=#7fff9a>Unloading:</color> leaves in {Clock(o.deadline - now)}";
            case EconomyManager.OrderInfo.Closing: return "<color=#ffd27f>Door closing:</color> leaving now";
            default:
                float left = em != null ? em.SecondsUntil(o) : o.remaining;
                return left > 0f ? $"On its way: arrives in {Clock(left)}" : "Pulling in";
        }
    }
}
