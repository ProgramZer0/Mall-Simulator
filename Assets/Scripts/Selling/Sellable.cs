using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Marks an item as something the store can sell. Only items with this can be priced and sold.
///
/// Put it on the item's world prefab (next to its NetworkObject and ItemPickupNet):
/// - Loose items (a washing machine on display): the item itself gets a price tag with the scanner.
/// - Stock crates: the products stocked from it on shelves and racks can be priced (the price lives
///   on the shelf zone). Untick Sell When Loose so the crate itself can't be priced.
///
/// Prices are in cents. No price (0) = not for sale; customers won't buy it.
/// </summary>
public class Sellable : NetworkBehaviour
{
    [Tooltip("The item itself can be priced and sold when it's set down in the store (e.g. a washing machine). " +
             "Untick for stock crates: their products are priced on the shelf instead.")]
    [SerializeField] private bool sellWhenLoose = true;

    /// <summary>Every spawned sellable item on this machine (customers can search these).</summary>
    public static readonly List<Sellable> All = new();

    private readonly NetworkVariable<int> priceCents =
        new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public bool SellWhenLoose => sellWhenLoose;

    /// <summary>The price tag in cents (0 = not for sale). For a stock crate it's the price of a FULL crate.</summary>
    public int Price => priceCents.Value;

    /// <summary>
    /// What it actually sells for, in cents. A part-used stock crate sells for its share of the full
    /// crate's price (3 of 12 left at $24 a crate: $6); anything else sells for its price tag.
    /// </summary>
    public int SalePrice
    {
        get
        {
            int price = priceCents.Value;
            if (price <= 0 || !TryGetComponent<ItemPickupNet>(out var crate) || !crate.IsStockCrate) return price;
            return Mathf.Max(1, Mathf.RoundToInt(price * (float)crate.Units / crate.UnitsPerCrate));
        }
    }

    /// <summary>
    /// Priced and sold as a loose item? A shelf or rack (anything with a StockContainer) isn't for sale
    /// while it has stock on it; it's listed again once it's emptied.
    /// </summary>
    public bool IsForSale =>
        sellWhenLoose && priceCents.Value > 0 && !(TryGetComponent<StockContainer>(out var shelf) && shelf.HasStock);

    public int ItemId => TryGetComponent<ItemPickupNet>(out var p) ? p.itemId : 0;

    public override void OnNetworkSpawn()
    {
        All.Add(this);
        priceCents.OnValueChanged += OnPriceChanged;
        StoreListing.MarkAllDirty();
    }

    public override void OnNetworkDespawn()
    {
        All.Remove(this);
        priceCents.OnValueChanged -= OnPriceChanged;
        StoreListing.MarkAllDirty();
    }

    private void OnPriceChanged(int previous, int current) => StoreListing.MarkAllDirty();

    /// <summary>Server: set the price in cents (0 = not for sale). Before Spawn works too.</summary>
    public void ServerSetPrice(int cents)
    {
        if (sellWhenLoose) priceCents.Value = Mathf.Max(0, cents);
    }

    /// <summary>Can this catalog item be sold (its world prefab has a Sellable)?</summary>
    public static bool IsSellable(int itemId) => GetFor(itemId) != null;

    /// <summary>Can this catalog item be priced as a loose item?</summary>
    public static bool IsSellableLoose(int itemId) => GetFor(itemId)?.sellWhenLoose ?? false;

    private static Sellable GetFor(int itemId)
    {
        if (itemId == 0 || ItemCatalog.Instance == null || !ItemCatalog.Instance.TryGet(itemId, out var def) ||
            def.worldNetworkPrefab == null) return null;
        return def.worldNetworkPrefab.GetComponent<Sellable>();
    }

    /// <summary>Cents as "$12.50".</summary>
    public static string FormatPrice(int cents) => cents <= 0 ? "Not for sale" : $"${cents / 100}.{cents % 100:00}";

    /// <summary>"12.5", "$12.50", "12" -> cents. False if it isn't a number.</summary>
    public static bool TryParsePrice(string text, out int cents)
    {
        cents = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim().TrimStart('$').Replace(",", "");
        if (!decimal.TryParse(text, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out decimal value) || value < 0) return false;
        cents = (int)System.Math.Round(value * 100m);
        return true;
    }
}
