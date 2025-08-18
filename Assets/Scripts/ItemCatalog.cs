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
    private GameObject generatedGhostPrefab;

    public GameObject GetGhostPrefab()
    {
        if (generatedGhostPrefab != null) return generatedGhostPrefab;
        if (worldNetworkPrefab == null) return null;

        // Clone world prefab
        generatedGhostPrefab = GameObject.Instantiate(worldNetworkPrefab.gameObject);
        generatedGhostPrefab.name = worldNetworkPrefab.name + "_Ghost";

        // Strip off scripts + colliders
        foreach (var c in generatedGhostPrefab.GetComponentsInChildren<Collider>())
            GameObject.Destroy(c);

        foreach (var nb in generatedGhostPrefab.GetComponentsInChildren<NetworkBehaviour>())
            GameObject.Destroy(nb);

        var no = generatedGhostPrefab.GetComponent<NetworkObject>();
        if (no != null) GameObject.Destroy(no);

        // Apply ghost material/shader
        foreach (var r in generatedGhostPrefab.GetComponentsInChildren<Renderer>())
        {
            foreach (var mat in r.materials)
            {
                mat.shader = Shader.Find("Standard");
                Color c = mat.color;
                c.a = 0.4f;
                mat.color = c;

                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.EnableKeyword("_ALPHABLEND_ON");
                mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                mat.renderQueue = 3000;
            }
        }

        // Hide until instantiated at runtime
        generatedGhostPrefab.SetActive(false);

        return generatedGhostPrefab;
    }
}