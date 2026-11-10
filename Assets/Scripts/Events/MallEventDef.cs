using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One mall event (Black Friday, a heatwave, a supplier strike, a season...). Make one with
/// Create > Mall Sim > Mall Event, fill it in, and add it to the MallEventManager's Events list.
///
/// An event has:
/// - When it starts (Trigger) and how long it lasts (Duration, in seconds of play).
/// - Rules: each rule says which items it affects (Targets) and what happens to them (Effects).
///   Use several rules for different things in one event ("electronics cost more, toys sell for less").
///
/// Effects stack: every active event that matches an item adds its percents and amounts together.
/// </summary>
[CreateAssetMenu(menuName = "Mall Sim/Mall Event", fileName = "New Mall Event")]
public class MallEventDef : ScriptableObject
{
    public enum TriggerType
    {
        /// <summary>Only when started from code, a button or a vote (MallEventManager.ServerStart).</summary>
        Manual,
        /// <summary>Once, Start At seconds into the session (or into each season: Timed From Season Start).</summary>
        AtTime,
        /// <summary>Every Interval seconds (first at Start At). (For seasons, use the manager's Seasons list instead.)</summary>
        Repeating,
        /// <summary>Each Check Every seconds, with Chance (0-1) of starting.</summary>
        Random,
    }

    /// <summary>What an effect changes about an item.</summary>
    public enum Stat
    {
        /// <summary>What the player pays to order one (their cost).</summary>
        OrderCost,
        /// <summary>An extra fee per one ordered (0 normally; percents are of the Order Cost).</summary>
        DeliveryCost,
        /// <summary>Seconds from ordering until it arrives.</summary>
        DeliveryTime,
        /// <summary>What one is really worth to customers (Market Price).</summary>
        MarketPrice,
        /// <summary>How much customers want it (1 = normal).</summary>
        Demand,
        /// <summary>How much it draws customers when on display (1 = normal).</summary>
        Attractiveness,
    }

    public enum Mode
    {
        /// <summary>Amount is a percent: 20 = +20%, -25 = 25% less.</summary>
        Percent,
        /// <summary>Amount is added as-is (dollars for prices, seconds for time, points for the rest).</summary>
        Amount,
    }

    public enum MatchMode
    {
        /// <summary>An item is affected if it passes EVERY filter that's set.</summary>
        All,
        /// <summary>An item is affected if it passes ANY filter that's set.</summary>
        Any,
    }

    [Serializable]
    public class Effect
    {
        public Stat stat = Stat.MarketPrice;
        public Mode mode = Mode.Percent;
        [Tooltip("Percent: 20 = +20%, -25 = 25% less. Amount: dollars, seconds or points added (negative takes away).")]
        public float amount = 10f;
    }

    [Serializable]
    public class Targets
    {
        [Tooltip("Affect every item (ignores the filters below).")]
        public bool everything;
        [Tooltip("All: an item must pass every filter that's set. Any: passing one is enough.")]
        public MatchMode match = MatchMode.Any;

        [Tooltip("These exact items (their catalog ids).")]
        public List<int> itemIds = new();
        [Tooltip("Items in these categories (the catalog's Category, not case sensitive).")]
        public List<string> categories = new();
        [Tooltip("Items whose name contains any of these (not case sensitive), e.g. \"shirt\".")]
        public List<string> nameContains = new();

        [Tooltip("Filter by price.")]
        public bool usePriceRange;
        [Tooltip("Which price the range is about.")]
        public bool priceRangeUsesMarketPrice;
        [Tooltip("Dollars, inclusive.")]
        public float minPrice;
        public float maxPrice = 100f;

        [Tooltip("Filter by delivery time.")]
        public bool useDeliveryRange;
        [Tooltip("Seconds, inclusive.")]
        public float minDeliverySeconds;
        public float maxDeliverySeconds = 60f;

        /// <summary>Does this item count?</summary>
        public bool Matches(ItemDef def)
        {
            if (def == null) return false;
            if (everything) return true;

            int set = 0, passed = 0;
            void Check(bool isSet, Func<bool> test)
            {
                if (!isSet) return;
                set++;
                if (test()) passed++;
            }

            Check(itemIds != null && itemIds.Count > 0, () => itemIds.Contains(def.id));
            Check(categories != null && categories.Count > 0, () =>
            {
                foreach (var c in categories)
                    if (def.HasCategory(c)) return true;
                return false;
            });
            Check(nameContains != null && nameContains.Count > 0, () =>
            {
                string n = def.displayName ?? "";
                foreach (var part in nameContains)
                    if (!string.IsNullOrEmpty(part) && n.IndexOf(part.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                return false;
            });
            Check(usePriceRange, () =>
            {
                float p = priceRangeUsesMarketPrice ? def.marketPrice : def.price;
                return p >= minPrice && p <= maxPrice;
            });
            Check(useDeliveryRange, () => def.deliverySeconds >= minDeliverySeconds && def.deliverySeconds <= maxDeliverySeconds);

            if (set == 0) return false; // nothing chosen (and not Everything): affects nothing
            return match == MatchMode.All ? passed == set : passed > 0;
        }
    }

    [Serializable]
    public class Rule
    {
        [Tooltip("Optional note, e.g. \"Electronics up\".")]
        public string note;
        public Targets targets = new();
        public List<Effect> effects = new();
    }

    [Header("About")]
    public string displayName = "New Event";
    [TextArea] public string description;

    [Header("When")]
    public TriggerType trigger = TriggerType.Manual;
    [Tooltip("AtTime / Repeating: seconds into the session for the (first) start.")]
    public float startAt = 600f;
    [Tooltip("Repeating: seconds from one start to the next.")]
    public float interval = 1800f;
    [Tooltip("Random: how often to roll, in seconds.")]
    public float checkEvery = 60f;
    [Tooltip("Random: chance of starting on each roll (0-1).")]
    [Range(0f, 1f)] public float chance = 0.05f;
    [Tooltip("How long it lasts once started, in seconds. For a season, this is the season's length.")]
    public float duration = 300f;

    [Header("Seasonal (optional)")]
    [Tooltip("Only happens during these seasons (season events from the manager's Seasons list). Empty = any time.")]
    public List<MallEventDef> onlyInSeasons = new();
    [Tooltip("At Time / Repeating: Start At counts from the start of each season instead of the session, " +
             "so At Time happens once every season (e.g. 300 = five minutes into the season).")]
    public bool timedFromSeasonStart;
    [Tooltip("Stops when the season changes, even if its Duration isn't up.")]
    public bool endsWithSeason;
    [Tooltip("At most this many times per season (0 = no limit). Handy for Random: \"maybe once per winter\".")]
    [Min(0)] public int maxPerSeason;

    [Header("Voting")]
    [Tooltip("Can be on a mall vote's ballot (MallVoteManager). Winning starts it like any other event. Usually " +
             "with Trigger set to Manual, so it only happens when voted in.")]
    public bool votable;

    [Header("What it does")]
    public List<Rule> rules = new();
}
