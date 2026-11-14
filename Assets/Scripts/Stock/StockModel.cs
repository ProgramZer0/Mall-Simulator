using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// On a shelf product drawn by StockBatcher (added by StockContainer; nothing to set up). Takes the
/// product out of the batch when it's destroyed.
/// </summary>
public sealed class StockModel : MonoBehaviour
{
    internal List<StockBatcher.PartShape> Shape;
    internal StockBatcher.Part[] Parts;

    private void OnDestroy() => StockBatcher.Remove(this);
}
