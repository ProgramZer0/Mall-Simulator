using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// The delivery guy: special market orders come by hand, one guy per item. He runs in through a mall
/// entrance (Courier Looks: his own, fancier models), stops outside the store holding the package (it
/// looks like the item's shipment box) and waits Courier Wait Seconds. A player who presses F on him gets
/// the package in their hands (anyone may; taking another team's to them counts as delivering it), and he
/// runs off. Nobody comes: he sets it down on the floor in front of the store for anyone to take, and goes.
/// </summary>
public partial class CustomerManager
{
    [Header("Delivery guy (special market)")]
    [Tooltip("His models (fancier than shoppers'). Empty: a normal shopper look.")]
    [SerializeField] private List<GameObject> courierLooks = new();
    [Tooltip("Who he is to the AI (any personality; he never shops). Empty: a bystander personality, else any.")]
    [SerializeField] private CustomerPersonality courierPersonality;
    [Tooltip("Seconds he waits outside the store for someone to take the package; then he leaves it on the floor there.")]
    [SerializeField, Min(5f)] private float courierWaitSeconds = 120f;
    [Tooltip("How much faster than a shopper he moves (he runs in and out).")]
    [SerializeField, Min(1f)] private float courierSpeed = 1.8f;

    private const byte CourierLook = 0x40; // the look byte without the child bit: a Courier Looks number

    private struct Package { public int itemId; public byte paint; public ulong team; public StorePlot plot; }
    private readonly Dictionary<ushort, Package> packages = new();      // host: who carries what
    private readonly Dictionary<ushort, int> carrying = new();          // every machine: the item each carries (for its look)

    /// <summary>
    /// Host: send a delivery guy to this store with one package (one item). False if he can't come (no
    /// way in, no entrance to the store): then the caller sets it down itself.
    /// </summary>
    public bool ServerSendCourier(StorePlot plot, ulong team, int itemId, byte paint)
    {
        if (!IsServer || plot == null || !entrances.TryGetValue(plot, out var ways) || ways.Count == 0) return false;
        var sp = PickSpawnPoint(null);
        if (sp == null) return false;
        var who = courierPersonality != null ? courierPersonality : PickPersonality(true) ?? PickPersonality();
        byte look = courierLooks.Count > 0 ? (byte)(CourierLook | Mathf.Min(Random.Range(0, courierLooks.Count), 0x3F)) : (byte)0;
        var c = Spawn(sp.transform.position, out string why, null, who, pickGoal: false, lookOverride: courierLooks.Count > 0 ? look : (byte?)null);
        if (c == null) { Debug.LogWarning($"[Customers] No delivery guy: {why}.", this); return false; }
        c.courier = true;
        c.thief = false;
        c.budgetCents = 0;
        c.wants = 0;
        c.bystanderSlot = false;
        c.leaveAt = float.MaxValue;
        c.speed *= courierSpeed;
        c.goalEntrance = ways[0];
        c.mode = Mode.Courier;
        c.endUntil = 0f; // (milling-about timer, reused: when he gives up waiting)
        packages[c.id] = new Package { itemId = itemId, paint = paint, team = team, plot = plot };
        SetMark(c, LostMark.Courier);
        ShowCarry(c.id, itemId);
        CarryRpc(c.id, itemId);
        Act(c, AnimCue.Running);
        if (!PathTo(c, ways[0].outside)) { CourierDrop(c, "no way to the store", true); return true; }
        Log(c, $"delivery guy for '{plot.DisplayName}' with item {itemId}");
        return true;
    }

    private void ThinkCourier(Customer c)
    {
        if (!packages.TryGetValue(c.id, out var p)) { CourierLeave(c); return; }
        if (!c.arrived) return;
        if (c.endUntil <= 0f)
        {
            // Outside the store: waiting, facing it.
            c.endUntil = Time.time + courierWaitSeconds;
            Act(c, AnimCue.Running, false);
            Face(c, c.goalEntrance.inside);
            if (NotificationManager.Instance != null)
                NotificationManager.Instance.ServerNotifyTeamText(p.team, "Delivery's here",
                    $"A delivery guy is waiting outside {p.plot.DisplayName} with your {ItemName(p.itemId)}: take it from him (F).");
            Log(c, "waiting outside the store with the package");
            return;
        }
        if (Time.time >= c.endUntil) CourierDrop(c, "nobody came", true);
    }

    // F on him: the package into the player's hands (empty hands only), and off he goes.
    private bool CourierHandOver(Customer c, ulong clientId)
    {
        if (!c.courier) return false;
        if (!packages.TryGetValue(c.id, out var p)) return true;
        var player = NetworkManager.ConnectedClients.TryGetValue(clientId, out var client) ? client.PlayerObject : null;
        var inventory = player != null ? player.GetComponent<PlayerInventoryNet>() : null;
        if (inventory == null) return true;
        if (!inventory.ServerGiveDelivered(p.itemId, PaintSet.All(p.paint), p.team))
        {
            NotificationManager.Instance?.ServerNotifyClientText(clientId, "Hands full", "Put down what you're holding to take the package.");
            return true;
        }
        packages.Remove(c.id);
        Log(c, $"handed the package to client {clientId}");
        CourierLeave(c);
        return true;
    }

    // Nobody took it: on the floor in front of the store, for anyone.
    private void CourierDrop(Customer c, string why, bool leave)
    {
        if (packages.TryGetValue(c.id, out var p))
        {
            packages.Remove(c.id);
            Vector3 at = c.goalEntrance.outside;
            if (c.arrived) at = c.pos + Quaternion.Euler(0f, c.yaw, 0f) * Vector3.forward * 0.6f;
            float yaw = c.yaw;
            if (EconomyManager.Instance != null) EconomyManager.Instance.ServerDropPackage(p.itemId, p.paint, at, yaw);
            if (NotificationManager.Instance != null)
                NotificationManager.Instance.ServerNotifyTeamText(p.team, "Package left outside",
                    $"Nobody took your {ItemName(p.itemId)} from the delivery guy: it's on the floor in front of {p.plot.DisplayName}, for anyone to grab.");
            Log(c, $"left the package on the floor ({why})");
        }
        if (leave) CourierLeave(c);
    }

    private void CourierLeave(Customer c)
    {
        ShowCarry(c.id, 0);
        CarryRpc(c.id, 0);
        SetMark(c, LostMark.None);
        c.mode = Mode.Exiting;
        c.courier = false; // done: just someone leaving now
        StartRunning(c);
        var exit = PickSpawnPoint(sp => sp.isExit);
        if (exit == null || !PathTo(c, exit.transform.position)) Despawn(c);
    }

    // Gone some other way (stuck, the mall shutting) with the package still on him: it's left at the store.
    private void CourierGone(Customer c)
    {
        if (packages.ContainsKey(c.id)) CourierDrop(c, "he had to go", false);
        carrying.Remove(c.id);
    }

    private static string ItemName(int itemId) =>
        ItemCatalog.Instance != null && ItemCatalog.Instance.TryGet(itemId, out var d) ? d.displayName : "delivery";

    [Rpc(SendTo.NotServer)]
    private void CarryRpc(ushort id, int itemId) => ShowCarry(id, itemId);

    // His package in his hands (the item's shipment box), on every machine.
    private void ShowCarry(ushort id, int itemId)
    {
        if (itemId == 0) carrying.Remove(id); else carrying[id] = itemId;
        if (!views.TryGetValue(id, out var v) || v == null) return;
        GameObject model = null;
        if (itemId != 0 && ItemCatalog.Instance != null && ItemCatalog.Instance.TryGet(itemId, out var def) && def.GetShipmentPrefab() is var box && box != null)
            model = StockContainer.MakeVisual(box.gameObject, v.transform.position, v.transform.rotation, null);
        v.SetCarry(model);
    }

    private GameObject CourierLookPrefab(byte look)
    {
        int index = look & 0x3F;
        return index < courierLooks.Count ? courierLooks[index] : null;
    }

    private static bool IsCourierLook(byte look) => (look & ChildLook) == 0 && (look & CourierLook) != 0;
}
