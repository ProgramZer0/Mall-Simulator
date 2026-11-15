using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A mall entrance customers come in through (and leave by). Put a few at the mall's outside doors,
/// on the floor. Customers walk between these and the stores.
/// </summary>
public class CustomerSpawnPoint : MonoBehaviour
{
    public static readonly List<CustomerSpawnPoint> All = new();

    [Tooltip("How often customers come in here compared with the other entrances (2 = twice as often).")]
    [Min(0f)] public float weight = 1f;

    [Tooltip("Customers can also leave the mall here.")]
    public bool isExit = true;

    private void OnEnable() => All.Add(this);
    private void OnDisable() => All.Remove(this);

    private void OnDrawGizmos()
    {
        Gizmos.color = new Color(0.2f, 0.8f, 1f);
        Gizmos.DrawWireSphere(transform.position, 0.4f);
        Gizmos.DrawLine(transform.position, transform.position + Vector3.up * 1.8f);
    }
}
