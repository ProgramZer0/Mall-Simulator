using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A kind of customer group (a couple, a few friends...). Make them with Create > Mall Sim > Customer Group
/// and add them to the CustomerManager's Groups (or put them in Resources/Customer Groups).
///
/// A group walks the hallways together behind its leader and picks stores on everyone's tastes. Inside,
/// each member shops for themselves, but they sway each other: buying something makes the others keener
/// on it, a member who loves the store keeps them all browsing longer, and one who doesn't hurries them
/// out. They leave each store together (whoever's done first waits by the door).
/// </summary>
[CreateAssetMenu(menuName = "Mall Sim/Customer Group", fileName = "New Customer Group")]
public class CustomerGroupDef : ScriptableObject
{
    public enum Kind { Couple, Friends }

    [Header("About")]
    public string displayName = "Group";
    [Tooltip("Couple: always together, and one of them pays for both. Friends: together, each pays for their own.")]
    public Kind kind = Kind.Couple;
    [Tooltip("How common this group is compared with the other groups (2 = twice as common as 1).")]
    [Min(0f)] public float spawnWeight = 1f;

    [Header("Who's in it")]
    [Tooltip("How many people (random between, inclusive). A couple is 2.")]
    public Vector2Int size = new(2, 2);
    [Tooltip("Who they can be (each member picked from these by Spawn Weight). Empty: anyone, as for single customers.")]
    public List<CustomerPersonality> personalities = new();

    [Header("Shopping together")]
    [Tooltip("Pay as one: while they're in the same store, the others hand their things to the leader, who " +
             "pays for everything. Couples do by default.")]
    public bool payTogether = true;
    [Tooltip("Things this group likes more when shopping together (an item category and how much more: " +
             "1 = twice as appealing). E.g. 'Couples' items for a couple. Put the same category on the items.")]
    public List<CustomerPersonality.Like> groupLikes = new();
    [Tooltip("One of them buying something makes the others keener on the same thing (1 = twice as keen), " +
             "and half that on things of the same category.")]
    [Min(0f)] public float buyTogether = 0.5f;
    [Tooltip("How much the group's mood in a store sways everyone: someone who loves it keeps them all " +
             "looking longer and buying more; someone who doesn't hurries them out. 0 = no sway.")]
    [Range(0f, 2f)] public float sway = 1f;

    public string Name => string.IsNullOrEmpty(displayName) ? name : displayName;

    /// <summary>How much more appealing this item is to the group (1 = no change).</summary>
    public float LikeBoost(ItemDef def)
    {
        if (def == null || groupLikes == null) return 1f;
        float best = 0f;
        foreach (var like in groupLikes)
            if (like != null && def.HasCategory(like.category)) best = Mathf.Max(best, like.weight);
        return 1f + best;
    }

    private void OnValidate()
    {
        size.x = Mathf.Max(2, size.x);
        size.y = Mathf.Max(size.x, size.y);
    }
}
