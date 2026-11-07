using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Mall-wide settings for how appealing stores are. Put it on the MallManager object.
///
/// - Personalities: every kind of customer (Customer Personality assets).
/// - Category Needs: what decor each kind of store wants. "Clothing: Window 2, Mannequin 2, Mirror 1"
///   means a store selling clothing is at its best with two windows, two mannequins and a mirror.
///   Importance weighs the needs against each other. Categories with no entry don't care about decor.
/// - The rest tunes how quickly displays draw people in (see StoreAppeal).
/// </summary>
public class StoreAppealSettings : MonoBehaviour
{
    public static StoreAppealSettings Instance { get; private set; }

    [Serializable]
    public class FeatureNeed
    {
        [Tooltip("A feature name (Decor features; Window is given by windows in the store's walls).")]
        public string feature = "Window";
        [Tooltip("How many make it perfect.")]
        [Min(0.01f)] public float wanted = 1f;
        [Tooltip("How much this one matters compared with the category's other needs.")]
        [Min(0f)] public float importance = 1f;
    }

    [Serializable]
    public class CategoryNeeds
    {
        public string category = "Clothing";
        public List<FeatureNeed> needs = new();
    }

    [Header("Customers")]
    public List<CustomerPersonality> personalities = new();

    [Serializable]
    public class RarityScore
    {
        public ItemDef.Rarity rarity;
        [Tooltip("Multiplier for a customer with Exotic Love 0.")]
        [Min(0f)] public float atLove0 = 1f;
        [Tooltip("...with Exotic Love 1.")]
        [Min(0f)] public float atLove1 = 1f;
        [Tooltip("...with Exotic Love 2 (a collector).")]
        [Min(0f)] public float atLove2 = 1f;

        public RarityScore() { }
        public RarityScore(ItemDef.Rarity r, float a0, float a1, float a2) { rarity = r; atLove0 = a0; atLove1 = a1; atLove2 = a2; }
    }

    [Header("Price")]
    [Tooltip("Price score at half price (or less) for a customer with Price Sensitivity 0. Anything under " +
             "market price scores above 1 for everyone, more the cheaper it is, up to half price.")]
    [Min(1f)] public float halfPriceScoreAtSensitivity0 = 1.5f;
    [Tooltip("Price score at half price (or less) with Price Sensitivity 2. Sensitivity 1 gets the middle.")]
    [Min(1f)] public float halfPriceScoreAtSensitivity2 = 2f;

    [Header("Rarity")]
    [Tooltip("How much each rarity is worth to a customer, by their Exotic Love (in between is in between). " +
             "Collectors (2) all but ignore common things.")]
    public List<RarityScore> rarityScores = new()
    {
        new RarityScore(ItemDef.Rarity.Common, 1f, 1f, 0.1f),
        new RarityScore(ItemDef.Rarity.Uncommon, 1f, 1.33f, 0.3f),
        new RarityScore(ItemDef.Rarity.Rare, 1f, 1.67f, 2.33f),
        new RarityScore(ItemDef.Rarity.Exotic, 1f, 2f, 3f),
    };

    [Header("Competition")]
    [Tooltip("Each OTHER open store selling the same item lowers what it's worth: × 1 / (1 + this × stores). " +
             "0.25: one other store → 80%, three → 57%. Items nobody else has keep their full value.")]
    [Min(0f)] public float competitionPerStore = 0.25f;
    [Tooltip("It never drops below this, however many stores sell it.")]
    [Range(0f, 1f)] public float minCompetition = 0.3f;

    [Header("Store quality")]
    public List<CategoryNeeds> categoryNeeds = new();
    [Tooltip("Decor helps a category only within this many meters of something for sale in that category " +
             "(a mirror by the clothes, not at the far end of a big store).")]
    [Min(0.5f)] public float decorReach = 8f;
    [Tooltip("Quality of a store whose categories have no needs listed (0-1). 0.5 = neither helps nor hurts.")]
    [Range(0f, 1f)] public float neutralQuality = 0.5f;
    [Tooltip("The feature that windows set into the store's walls count as.")]
    public string windowFeature = "Window";

    [Header("Drawing people in")]
    [Tooltip("Value seen from the hallway that draws in about 2 of 3 passers-by who like it. Higher = it takes more on display.")]
    [Min(0.1f)] public float outsideSaturation = 10f;
    [Tooltip("Value inside the store that makes about 2 of 3 visitors want to buy. Higher = it takes more stock.")]
    [Min(0.1f)] public float insideSaturation = 30f;
    [Tooltip("Where a passer-by's eyes are: this far outside a front window or door...")]
    [Min(0.1f)] public float viewerDistance = 2f;
    [Tooltip("...and this high above the floor.")]
    [Min(0.1f)] public float viewerHeight = 1.6f;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[StoreAppealSettings] More than one; using the first.", this);
            return;
        }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void OnValidate() => StoreAppeal.MarkAllDirty();

    /// <summary>The needs for a category (null if it has none).</summary>
    public CategoryNeeds NeedsFor(string category)
    {
        foreach (var c in categoryNeeds)
            if (c != null && string.Equals(c.category?.Trim(), category?.Trim(), StringComparison.OrdinalIgnoreCase))
                return c;
        return null;
    }

    /// <summary>How much an item of this rarity is worth to a customer with this Exotic Love (0-2).</summary>
    public float RarityMultiplier(ItemDef.Rarity rarity, float exoticLove)
    {
        foreach (var r in rarityScores)
        {
            if (r == null || r.rarity != rarity) continue;
            float love = Mathf.Clamp(exoticLove, 0f, 2f);
            return love <= 1f ? Mathf.Lerp(r.atLove0, r.atLove1, love) : Mathf.Lerp(r.atLove1, r.atLove2, love - 1f);
        }
        return 1f;
    }

    /// <summary>
    /// How the price looks to a customer. Over market: 1 - sensitivity × how much over (never below 0).
    /// Under market: always above 1, rising to the half-price score at half price.
    /// </summary>
    public float PriceScore(float priceOverMarket, float sensitivity)
    {
        if (priceOverMarket >= 1f) return Mathf.Max(0f, 1f - sensitivity * (priceOverMarket - 1f));
        float halfScore = Mathf.Lerp(halfPriceScoreAtSensitivity0, halfPriceScoreAtSensitivity2, Mathf.Clamp01(sensitivity / 2f));
        float discount = Mathf.Clamp01((1f - priceOverMarket) / 0.5f); // 0 at market, 1 at half price or less
        return 1f + (halfScore - 1f) * discount;
    }

    /// <summary>What other stores selling the same item leaves of its value.</summary>
    public float CompetitionFactor(int otherStores) =>
        Mathf.Max(minCompetition, 1f / (1f + competitionPerStore * Mathf.Max(0, otherStores)));

    /// <summary>A random personality, the common ones more often (for spawning customers).</summary>
    public CustomerPersonality PickRandom()
    {
        float total = 0f;
        foreach (var p in personalities) if (p != null) total += p.spawnWeight;
        if (total <= 0f) return null;
        float roll = UnityEngine.Random.value * total;
        foreach (var p in personalities)
        {
            if (p == null) continue;
            roll -= p.spawnWeight;
            if (roll <= 0f) return p;
        }
        return null;
    }
}
