using UnityEngine;

/// <summary>
/// Marks a for-sale wall so the player's F raycast can tell which store it belongs to.
/// StorePlot adds this automatically to every object in its For Sale Walls list,
/// so you don't need to add it by hand.
/// </summary>
public class ForSaleSign : MonoBehaviour
{
    public StorePlot Plot { get; private set; }

    public void Init(StorePlot plot) => Plot = plot;
}
