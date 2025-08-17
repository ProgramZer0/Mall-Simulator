using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public class ItemCatalog : MonoBehaviour
{
    public static ItemCatalog Instance { get; private set; }
    [SerializeField] private List<ItemDef> items = new();

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
}