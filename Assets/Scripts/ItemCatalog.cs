using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public class ItemCatalog : MonoBehaviour
{
    public static ItemCatalog Instance { get; private set; }
    [SerializeField] private List<ItemDef> items = new();

    [Header("Placement Ghost")]
    [Tooltip("Opacity of the placement preview (0 = invisible, 1 = solid).")]
    [Range(0.05f, 1f)]
    [SerializeField] private float ghostAlpha = 0.45f;

    public float GhostAlpha => ghostAlpha;

    private Dictionary<int, ItemDef> map;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        map = new Dictionary<int, ItemDef>();
        foreach (var it in items) map[it.id] = it;
    }

    public bool TryGet(int id, out ItemDef def) => map.TryGetValue(id, out def);
}

[System.Serializable]
public class ItemDef
{
    public int id;
    public string displayName;
    public GameObject handVisualPrefab;
    public NetworkObject worldNetworkPrefab;

    [System.NonSerialized] private GameObject ghostTemplate;
    [System.NonSerialized] private bool localBoundsComputed;
    [System.NonSerialized] private Bounds localBounds;

    /// <summary>Physical size of the world prefab in its own local space (cached).</summary>
    public Bounds LocalBounds
    {
        get
        {
            if (!localBoundsComputed && worldNetworkPrefab != null)
            {
                localBounds = PlacementCheck.ComputeLocalBounds(worldNetworkPrefab.gameObject);
                localBoundsComputed = true;
            }
            return localBounds;
        }
    }

    /// <summary>
    /// Inactive, transparent, component-stripped copy of the world prefab.
    /// Built once per item and reused; Instantiate it to show a preview.
    /// </summary>
    public GameObject GetGhostPrefab()
    {
        if (ghostTemplate != null) return ghostTemplate;
        if (worldNetworkPrefab == null) return null;

        float alpha = ItemCatalog.Instance != null ? ItemCatalog.Instance.GhostAlpha : 0.45f;
        ghostTemplate = GhostBuilder.BuildTemplate(worldNetworkPrefab.gameObject, alpha);
        return ghostTemplate;
    }
}