using System.Collections.Generic;
using UnityEngine;

// The mall's reputation: word gets around. Broken lights and screens nobody's reported, and lost children
// nobody brought back, keep shoppers away: fewer come in, and fewer at once (Arrivals Per Minute and Max
// Customers are both multiplied by it). Shown in the customer debug line.
//
// Broken things nearby also bother the shoppers already here: in a store with one in sight they get
// unhappier the longer they stay and buy less, and they're less likely to walk into a store with one by
// its door.
public partial class CustomerManager
{
    [Header("Mall Reputation")]
    [Tooltip("Each broken thing (light, screen) nobody's reported: this much fewer shoppers (0.15 = 15% fewer " +
             "coming in, and at once), for as long as it's broken.")]
    [SerializeField, Range(0f, 1f)] private float brokenPenalty = 0.15f;
    [Tooltip("A reported one waiting for maintenance counts this much of that (word's still out).")]
    [SerializeField, Range(0f, 1f)] private float reportedBrokenShare = 0.33f;
    [Tooltip("A lost child no player brought back (they found their family alone, or left without them): this " +
             "much fewer shoppers...")]
    [SerializeField, Range(0f, 1f)] private float lostChildPenalty = 0.3f;
    [Tooltip("...wearing off over this many minutes.")]
    [SerializeField, Min(0.5f)] private float lostChildPenaltyMinutes = 10f;
    [Tooltip("Never fewer than this share of shoppers, however bad it gets (0.2 = at most 80% fewer).")]
    [SerializeField, Range(0f, 1f)] private float worstReputation = 0.2f;

    [Header("Broken Things Nearby")]
    [Tooltip("Broken lights and screens within this many meters of a shopper bother them (0: off). A reported " +
             "one counts Reported Broken Share of one.")]
    [SerializeField, Min(0f)] private float brokenNearbyRange = 10f;
    [Tooltip("In a store: satisfaction lost a minute for each broken thing nearby (satisfaction runs -1 to 1; " +
             "leaving below 0 is an unhappy customer, which costs the store).")]
    [SerializeField, Min(0f)] private float brokenNearbyPerMinute = 0.3f;
    [Tooltip("In a store: their chance of buying, times (1 - this) for each broken thing nearby...")]
    [SerializeField, Range(0f, 1f)] private float brokenNearbyBuyPenalty = 0.15f;
    [Tooltip("...never less than this share of it.")]
    [SerializeField, Range(0f, 1f)] private float brokenNearbyMinBuy = 0.4f;
    [Tooltip("Walking past: a store with broken things by its door, its walk-in chance times (1 - this) for each " +
             "(never less than Broken Nearby Min Buy of it).")]
    [SerializeField, Range(0f, 1f)] private float brokenNearbyEnterPenalty = 0.15f;

    private readonly List<float> letDowns = new(); // host: when each lost child was let down

    // Broken things a shopper can see from here (weighted).
    private float BrokenNear(Vector3 pos)
    {
        var breaks = BreakManager.Instance;
        return breaks != null && breaks.IsSpawned ? breaks.BrokenNear(pos, brokenNearbyRange, reportedBrokenShare) : 0f;
    }

    // Multiplier for a chance with N broken things nearby.
    private float BrokenFactor(float n, float penaltyEach) =>
        n <= 0f ? 1f : Mathf.Max(brokenNearbyMinBuy, Mathf.Pow(1f - penaltyEach, n));

    // While shopping in a store: broken things in sight wear their mood down (once a think).
    private void BrokenNearbyMood(Customer c)
    {
        if (c.store == null || c.child || brokenNearbyPerMinute <= 0f) return;
        float dt = Mathf.Min(5f, Time.time - c.moodCheckedAt);
        c.moodCheckedAt = Time.time;
        float n = BrokenNear(c.pos);
        c.brokenNear = n;
        if (n <= 0f) return;
        c.satisfaction -= brokenNearbyPerMinute / 60f * dt * n;
        if (!c.sawBroken)
        {
            c.sawBroken = true;
            Log(c, $"bothered by {n:0.#} broken thing(s) nearby");
            React(c, Reaction.BrokenNearby);
        }
    }

    /// <summary>
    /// Host: how many shoppers come, next to normal (1 = all of them, 0.5 = half), from broken things and
    /// lost children let down.
    /// </summary>
    public float Reputation
    {
        get
        {
            float penalty = 0f;
            var breaks = BreakManager.Instance;
            if (breaks != null && breaks.IsSpawned)
                penalty += brokenPenalty * (breaks.Count(BreakManager.State.Broken) +
                                            reportedBrokenShare * breaks.Count(BreakManager.State.Reported));
            float now = Time.time, span = lostChildPenaltyMinutes * 60f;
            for (int i = letDowns.Count - 1; i >= 0; i--)
            {
                float left = 1f - (now - letDowns[i]) / span;
                if (left <= 0f) { letDowns.RemoveAt(i); continue; }
                penalty += lostChildPenalty * left;
            }
            return Mathf.Max(worstReputation, 1f - penalty);
        }
    }

    private string ReputationWhy()
    {
        var breaks = BreakManager.Instance;
        int broken = breaks != null && breaks.IsSpawned ? breaks.Count(BreakManager.State.Broken) : 0;
        int reported = breaks != null && breaks.IsSpawned ? breaks.Count(BreakManager.State.Reported) : 0;
        return $"{broken} broken, {reported} reported, {letDowns.Count} lost child(ren) let down";
    }

    // A lost child nobody helped: they gave up waiting, or their family left without them.
    private void LostChildLetDown(Customer kid)
    {
        kid.lostLetDown = true;
        if (lostChildPenalty <= 0f) return;
        letDowns.Add(Time.time);
        Log(kid, "nobody helped them find their family: the mall's reputation takes a hit");
        var notes = NotificationManager.Instance;
        if (notes == null) return;
        foreach (var client in NetworkManager.ConnectedClientsList)
            notes.ServerNotifyClientText(client.ClientId, "Nobody helped a lost child",
                $"Word's getting around: fewer shoppers for the next {lostChildPenaltyMinutes:0} minutes.", alarm: true);
    }
}
