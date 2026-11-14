using UnityEngine;

/// <summary>
/// A franchise: the sole right to sell one set of items (those with its Category) for a while, won at
/// auction (see FranchiseManager). Make them with Create > Mall Sim > Franchise and list them on the
/// FranchiseManager. Only the holding team can order its items; nobody can while no one holds it.
/// </summary>
[CreateAssetMenu(menuName = "Mall Sim/Franchise", fileName = "New Franchise")]
public class FranchiseDef : ScriptableObject
{
    [Tooltip("Unique number for this franchise (not 0).")]
    public int id = 1;
    public string displayName = "Franchise";
    [TextArea] public string description;
    public Sprite icon;

    [Tooltip("The item category it covers (as on items' Categories; not case sensitive), e.g. \"Brand X\".")]
    public string category = "Franchise";
    [Tooltip("Lowest first bid, in dollars.")]
    [Min(1)] public int startingBid = 2000;
    [Tooltip("Each bid must beat the top bid by at least this, in dollars.")]
    [Min(1)] public int minRaise = 100;
    [Tooltip("How long the winner holds it, in seconds, before it goes up for bids again.")]
    [Min(30f)] public float holdSeconds = 1800f;
    [Tooltip("How long bidding lasts, in seconds (late bids add time).")]
    [Min(10f)] public float auctionSeconds = 180f;
    [Tooltip("Team level needed to bid (0 or 1 = from the start).")]
    [Min(0)] public int minLevel = 0;
}
