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

    [Header("Store quality")]
    public List<CategoryNeeds> categoryNeeds = new();
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
