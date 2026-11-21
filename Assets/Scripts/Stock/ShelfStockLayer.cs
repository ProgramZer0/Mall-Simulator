using UnityEngine;

/// <summary>
/// Products sitting on shelves and hang rails go on their own layer, "shelfStock", which players walk
/// through: the shelf itself is what you bump into. Their box collider is only there to aim at them
/// (take, buy, price, paint). Before this, a product sticking out over a shelf's edge (shoes on a bottom
/// shelf) was a step a player could climb onto or get pushed down by, sometimes through a thin floor.
///
/// Every mask that looks for loose items (the Pickup layer) also looks for shelf stock, so aiming and
/// placing work as before. Loose items stay on Pickup and still block players.
/// </summary>
public static class ShelfStockLayer
{
    public const string Name = "shelfStock";

    /// <summary>The layer number (-1 if the layer isn't set up in Tags and Layers).</summary>
    public static int Layer { get; private set; } = -1;

    private static int pickup = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Init()
    {
        Layer = LayerMask.NameToLayer(Name);
        pickup = LayerMask.NameToLayer("Pickup");
        if (Layer < 0)
        {
            Debug.LogWarning($"[ShelfStockLayer] No '{Name}' layer in Tags and Layers: shelf products stay on " +
                             "their own layer and still block players.");
            return;
        }
        int player = LayerMask.NameToLayer("Player");
        if (player >= 0) Physics.IgnoreLayerCollision(player, Layer, true);
    }

    /// <summary>The mask, plus shelf stock if it looks for loose items (Pickup).</summary>
    public static LayerMask With(LayerMask mask)
    {
        if (Layer < 0 || pickup < 0 || (mask.value & (1 << pickup)) == 0) return mask;
        return mask.value | (1 << Layer);
    }

    /// <summary>Put a shelf product (just its root, where its box collider is) on the layer.</summary>
    public static void Apply(GameObject product)
    {
        if (Layer >= 0 && product != null) product.layer = Layer;
    }
}
