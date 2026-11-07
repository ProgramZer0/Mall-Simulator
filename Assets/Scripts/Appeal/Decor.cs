using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Makes an item count as store decor: it gives the store features (Mannequin, Mirror, Lighting,
/// Seating, Plant...). Put it on the item's world prefab. It counts whether or not the item is for sale.
///
/// Feature names are free text (not case sensitive) and match the ones in StoreAppealSettings →
/// Category Needs, e.g. clothing stores want Mirror and Mannequin. Windows set into the store's walls
/// count as the Window feature by themselves.
/// </summary>
public class Decor : MonoBehaviour
{
    [Serializable]
    public class Feature
    {
        public string name = "Mannequin";
        [Tooltip("How much of the feature this gives (a big mirror might be 2).")]
        [Min(0f)] public float amount = 1f;
    }

    public static readonly List<Decor> All = new();

    /// <summary>Goes up when decor appears, moves or goes away.</summary>
    public static int Version { get; private set; }

    public List<Feature> features = new();

    private Vector3 lastPos;

    private void OnEnable()
    {
        All.Add(this);
        lastPos = transform.position;
        Version++;
    }

    private void OnDisable()
    {
        All.Remove(this);
        Version++;
    }

    private void LateUpdate()
    {
        if ((transform.position - lastPos).sqrMagnitude < 0.0025f) return;
        lastPos = transform.position;
        Version++;
    }
}
