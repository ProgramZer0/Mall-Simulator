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
    public enum Kind { Couple, Friends, Family }

    [Header("About")]
    public string displayName = "Group";
    [Tooltip("Couple: always together, one pays for both. Friends: together, each pays for their own. " +
             "Family: parents and children; may split up (each parent with some of the children) and meet again.")]
    public Kind kind = Kind.Couple;
    [Tooltip("How common this group is compared with the other groups (2 = twice as common as 1).")]
    [Min(0f)] public float spawnWeight = 1f;

    [Header("Who's in it")]
    [Tooltip("How many people (random between, inclusive). A couple is 2. Not used by families (see Family).")]
    public Vector2Int size = new(2, 2);
    [Tooltip("Who they can be (each member picked from these by Spawn Weight). Empty: anyone, as for single customers.")]
    public List<CustomerPersonality> personalities = new();

    [Header("Family")]
    [Tooltip("How many parents (random between, inclusive).")]
    public Vector2Int adults = new(1, 2);
    [Tooltip("How many children (random between, inclusive). Each walks with one of the parents, always.")]
    public Vector2Int children = new(1, 2);
    [Tooltip("Who the children can be (picked evenly). Give these personalities a Spawn Weight of 0 so they " +
             "never come on their own. Their tastes pull the family into stores and make the parents buy for them.")]
    public List<CustomerPersonality> childPersonalities = new();
    [Tooltip("How much a child's tastes count compared with an adult's, choosing stores and things to buy " +
             "(1.5: kids get their way a bit more).")]
    [Min(0f)] public float childPull = 1.5f;
    [Tooltip("Chance the family splits up each time they choose where to go next (needs 2 parents): one " +
             "parent goes off with their children, and they meet back where they split.")]
    [Range(0f, 1f)] public float splitChance = 0.4f;
    [Tooltip("How long until they meet again (seconds, random between); sooner if a parent's done shopping.")]
    public Vector2 meetAfterSeconds = new(60f, 150f);

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
        adults.x = Mathf.Max(1, adults.x);
        adults.y = Mathf.Max(adults.x, adults.y);
        children.x = Mathf.Max(0, children.x);
        children.y = Mathf.Max(children.x, children.y);
        meetAfterSeconds.y = Mathf.Max(meetAfterSeconds.x, meetAfterSeconds.y);
    }
}
