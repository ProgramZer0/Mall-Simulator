using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public class ItemCatalog : MonoBehaviour
{
    public static ItemCatalog Instance { get; private set; }
    [SerializeField] private List<ItemDef> items = new();

    [Header("Default Item Sounds")]
    [Tooltip("Used for any item that doesn't set its own Pickup Clip.")]
    [SerializeField] private AudioClip defaultPickupClip;
    [Tooltip("Used for any item that doesn't set its own Place Clip.")]
    [SerializeField] private AudioClip defaultPlaceClip;

    public AudioClip DefaultPickupClip => defaultPickupClip;

    [Header("Default Shipment Box")]
    [Tooltip("Box delivered for items that don't set their own Shipment Prefab. Needs a NetworkObject, " +
             "ItemPickupNet and ShipmentPackage, and must be in the NetworkManager's prefab list.")]
    [SerializeField] private NetworkObject defaultShipmentPrefab;

    public NetworkObject DefaultShipmentPrefab => defaultShipmentPrefab;
    public AudioClip DefaultPlaceClip => defaultPlaceClip;

    private Dictionary<int, ItemDef> map;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        map = new Dictionary<int, ItemDef>();
        foreach (var it in items) map[it.id] = it;
    }

    public bool TryGet(int id, out ItemDef def) => map.TryGetValue(id, out def);

    /// <summary>Every item in the catalog, in the order listed (the shop shows them in this order).</summary>
    public IReadOnlyList<ItemDef> Items => items;
}

[System.Serializable]
public class ItemDef
{
    public int id;
    public string displayName;
    public GameObject handVisualPrefab;
    public NetworkObject worldNetworkPrefab;

    [Header("Buying")]
    [Tooltip("List this item in the shop monitor's buy menu.")]
    public bool showInShop = true;
    [Tooltip("Picture shown in the buy menu.")]
    public Sprite icon;
    [Tooltip("Buy menu filter group, e.g. Shelves, Products, Decor. Must match a filter's Category exactly.")]
    public string category = "General";
    [Tooltip("Cost of one when ordered.")]
    public int price = 10;
    [Tooltip("Default seconds from ordering until it arrives (used when the order doesn't give its own time).")]
    public float deliverySeconds = 30f;
    [Tooltip("What arrives at the shipment area: a box (NetworkObject + ItemPickupNet + ShipmentPackage). " +
             "Picking it up gives you this item; placing it puts down the real item. Empty = the catalog's " +
             "default box (or, if that's empty too, the item itself).")]
    public NetworkObject shipmentPrefab;

    /// <summary>The object to deliver for this item.</summary>
    public NetworkObject GetShipmentPrefab()
    {
        if (shipmentPrefab != null) return shipmentPrefab;
        if (ItemCatalog.Instance != null && ItemCatalog.Instance.DefaultShipmentPrefab != null)
            return ItemCatalog.Instance.DefaultShipmentPrefab;
        return worldNetworkPrefab;
    }

    [Header("Sounds (leave empty to use the catalog defaults)")]
    public AudioClip pickupClip;
    public AudioClip placeClip;

    public AudioClip PickupClip => pickupClip != null ? pickupClip
        : ItemCatalog.Instance != null ? ItemCatalog.Instance.DefaultPickupClip : null;
    public AudioClip PlaceClip => placeClip != null ? placeClip
        : ItemCatalog.Instance != null ? ItemCatalog.Instance.DefaultPlaceClip : null;

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

        // Starting opacity only; PlayerInteractInput sets the real per-ghost opacity.
        ghostTemplate = GhostBuilder.BuildTemplate(worldNetworkPrefab.gameObject, 0.45f);
        return ghostTemplate;
    }
}