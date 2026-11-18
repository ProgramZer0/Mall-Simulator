using System.Collections.Generic;
using UnityEngine;

// The mall's reputation: word gets around. Broken lights and screens nobody's reported, and lost children
// nobody brought back, keep shoppers away: fewer come in, and fewer at once (Arrivals Per Minute and Max
// Customers are both multiplied by it). Shown in the customer debug line.
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

    private readonly List<float> letDowns = new(); // host: when each lost child was let down

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
