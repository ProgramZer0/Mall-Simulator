using UnityEngine;

/// <summary>
/// Marks a wall that closes off an expansion segment, so the player's F raycast can tell which store
/// and segment it belongs to. StorePlot adds this automatically to every object in each segment's
/// False Walls, so you don't need to add it by hand. The walls need colliders to be looked at.
/// </summary>
public class SegmentSign : MonoBehaviour
{
    public StorePlot Plot { get; private set; }
    public int Index { get; private set; }

    public void Init(StorePlot plot, int index)
    {
        Plot = plot;
        Index = index;
    }
}
