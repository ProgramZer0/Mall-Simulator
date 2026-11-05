using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// An item's numbers after every running event has had its say. Use these instead of the raw
/// catalog values wherever events should matter:
///
///   OrderCost(def)       what the player pays to order one (dollars)
///   DeliveryFee(def)     extra per one ordered (dollars, normally 0)
///   DeliverySeconds(def) how long it takes to arrive
///   MarketPriceCents(def) what one is really worth to customers (cents)
///   Demand(def)          how much customers want it (1 = normal)
///   Attractiveness(def)  how much it draws customers on display (1 = normal)
///
/// How effects combine: every matching effect's percents are added up and its amounts are added up,
/// then value = base × (1 + percents / 100) + amounts (never below 0). If the MallEventManager has a
/// cap for that stat, the result is then kept inside it (e.g. Order Cost -75..+300 = between a quarter
/// and 4x the catalog price). Works on every machine; cached until an event starts or ends.
/// </summary>
public static class MarketModifiers
{
    private struct Totals
    {
        public float percent;
        public float amount;
        public float Apply(float value) => Mathf.Max(0f, value * (1f + percent / 100f) + amount);
    }

    private static readonly Dictionary<(int, MallEventDef.Stat), Totals> Cache = new();
    private static readonly List<MallEventDef> Active = new();
    private static int version, cachedVersion = -1;

    /// <summary>Something changed (an event started or ended): work things out again.</summary>
    public static void Invalidate() => version++;

    public static int OrderCost(ItemDef def) =>
        def == null ? 0 : Mathf.RoundToInt(Capped(MallEventDef.Stat.OrderCost, def.price, Get(def, MallEventDef.Stat.OrderCost).Apply(def.price)));

    public static int DeliveryFee(ItemDef def)
    {
        if (def == null) return 0;
        var t = Get(def, MallEventDef.Stat.DeliveryCost);
        // Percents are of the order cost (there's no fee to take a percent of normally), and so is the cap:
        // a cap of 0..100 keeps the fee between nothing and the item's price.
        float fee = Mathf.Max(0f, def.price * t.percent / 100f + t.amount);
        if (MallEventManager.Instance != null && MallEventManager.Instance.TryGetCap(MallEventDef.Stat.DeliveryCost, out float min, out float max))
            fee = Mathf.Clamp(fee, Mathf.Max(0f, def.price * min / 100f), Mathf.Max(0f, def.price * max / 100f));
        return Mathf.RoundToInt(fee);
    }

    public static float DeliverySeconds(ItemDef def) =>
        def == null ? 0f : Capped(MallEventDef.Stat.DeliveryTime, def.deliverySeconds, Get(def, MallEventDef.Stat.DeliveryTime).Apply(def.deliverySeconds));

    public static int MarketPriceCents(ItemDef def) =>
        def == null ? 0 : Mathf.RoundToInt(Capped(MallEventDef.Stat.MarketPrice, def.marketPrice, Get(def, MallEventDef.Stat.MarketPrice).Apply(def.marketPrice)) * 100f);

    public static float Demand(ItemDef def) =>
        def == null ? 0f : Capped(MallEventDef.Stat.Demand, 1f, Get(def, MallEventDef.Stat.Demand).Apply(1f));

    public static float Attractiveness(ItemDef def) =>
        def == null ? 0f : Capped(MallEventDef.Stat.Attractiveness, 1f, Get(def, MallEventDef.Stat.Attractiveness).Apply(1f));

    // Keeps a value inside the stat's cap (if it has one), measured from its normal value.
    private static float Capped(MallEventDef.Stat stat, float normal, float value)
    {
        var m = MallEventManager.Instance;
        if (m == null || !m.TryGetCap(stat, out float min, out float max)) return value;
        float lo = Mathf.Max(0f, normal * (1f + min / 100f));
        float hi = Mathf.Max(lo, normal * (1f + max / 100f));
        return Mathf.Clamp(value, lo, hi);
    }

    /// <summary>The same, by catalog id.</summary>
    public static ItemDef Def(int itemId) =>
        ItemCatalog.Instance != null && ItemCatalog.Instance.TryGet(itemId, out var def) ? def : null;

    private static Totals Get(ItemDef def, MallEventDef.Stat stat)
    {
        if (cachedVersion != version)
        {
            Cache.Clear();
            cachedVersion = version;
            if (MallEventManager.Instance != null) MallEventManager.Instance.GetActive(Active);
            else Active.Clear();
        }

        var key = (def.id, stat);
        if (Cache.TryGetValue(key, out var totals)) return totals;

        totals = default;
        foreach (var ev in Active)
        {
            if (ev?.rules == null) continue;
            foreach (var rule in ev.rules)
            {
                if (rule?.effects == null || rule.targets == null || !rule.targets.Matches(def)) continue;
                foreach (var e in rule.effects)
                {
                    if (e == null || e.stat != stat) continue;
                    if (e.mode == MallEventDef.Mode.Percent) totals.percent += e.amount;
                    else totals.amount += e.amount;
                }
            }
        }
        Cache[key] = totals;
        return totals;
    }
}
