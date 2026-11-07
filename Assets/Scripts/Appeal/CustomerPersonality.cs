using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A kind of customer (a teen, a bargain hunter, a collector...). Make as many as you like with
/// Create > Mall Sim > Customer Personality and add them to StoreAppealSettings → Personalities.
///
/// A personality decides how much each item is worth to that customer:
///   value = how much they like it (Likes) × how the price looks to them (Price Sensitivity, against the
///           item's market price) × how rare it is to them (Exotic Love) × the item's attractiveness and
///           demand (events and seasons move those).
/// Stores then add those values up (what's seen from the hallway, and everything inside) and weigh in
/// store quality (Looks Matter). See StoreAppeal.
/// </summary>
[CreateAssetMenu(menuName = "Mall Sim/Customer Personality", fileName = "New Personality")]
public class CustomerPersonality : ScriptableObject
{
    [Serializable]
    public class Like
    {
        [Tooltip("An item category (the catalog's Categories, not case sensitive).")]
        public string category;
        [Tooltip("How much they like it: 1 = a lot, 2 = can't resist, 0.5 = a bit.")]
        [Range(0f, 2f)] public float weight = 1f;
    }

    [Header("About")]
    public string displayName = "Shopper";
    [TextArea] public string description;
    [Tooltip("How common they are compared with the others (2 = twice as common as 1).")]
    [Min(0f)] public float spawnWeight = 1f;

    [Header("Taste")]
    public List<Like> likes = new();
    [Tooltip("Interest in things that aren't in Likes (0 = none at all).")]
    [Range(0f, 1f)] public float otherThings = 0.2f;
    [Tooltip("How much price matters: 0 = don't care, 1 = normal, 2 = very careful. Prices are judged " +
             "against the item's market price.")]
    [Range(0f, 2f)] public float priceSensitivity = 1f;
    [Tooltip("How much rare and exotic items draw them: 0 = not at all, 2 = collector.")]
    [Range(0f, 2f)] public float exoticLove = 0.5f;
    [Tooltip("How much store quality (the right decor for what it sells) sways them: 0 = not at all, 2 = a lot.")]
    [Range(0f, 2f)] public float looksMatter = 1f;
    [Tooltip("Chance they wander into an open store even if nothing in the window catches their eye.")]
    [Range(0f, 1f)] public float curiosity = 0.1f;

    /// <summary>How much they like this item (the best match among its categories).</summary>
    public float LikeOf(ItemDef def)
    {
        if (def == null) return 0f;
        float best = -1f;
        if (likes != null)
            foreach (var like in likes)
                if (like != null && def.HasCategory(like.category)) best = Mathf.Max(best, like.weight);
        return best >= 0f ? best : otherThings;
    }

    public string Name => string.IsNullOrEmpty(displayName) ? name : displayName;
}
