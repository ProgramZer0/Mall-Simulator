using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One end of the mall's walkway, for customers' walks end to end before they leave (CustomerManager →
/// Hall Legs Before Leaving). Put one at each end of each floor's main hall, on the walkway: each walk goes to
/// one at random, on this floor or another (Other Floor End Chance). Without any,
/// the ends are worked out from the walkable floor (its two far ends along its longest side).
/// </summary>
public class MallEnd : MonoBehaviour
{
    public static readonly List<MallEnd> All = new();

    private void OnEnable() => All.Add(this);
    private void OnDisable() => All.Remove(this);

    private void OnDrawGizmos()
    {
        Gizmos.color = new Color(0.3f, 0.9f, 0.5f, 0.9f);
        Gizmos.DrawWireSphere(transform.position + Vector3.up * 0.5f, 0.5f);
        Gizmos.DrawLine(transform.position, transform.position + Vector3.up * 2f);
    }
}
