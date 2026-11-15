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

    [Tooltip("Around the entrance, customers coming in and going out don't steer around each other (they'd " +
             "only jam the doorway): they walk straight through, and someone leaving is out once inside it " +
             "and close to the door (Exit Reach on the CustomerManager). Meters; 0 = off.")]
    [Min(0f)] public float doorwayRadius = 3f;

    /// <summary>Is this point within any entrance's doorway (see Doorway Radius)?</summary>
    public static bool InAnyDoorway(Vector3 p)
    {
        foreach (var sp in All)
        {
            Vector3 d = sp.transform.position - p;
            if (Mathf.Abs(d.y) > 2f) continue;
            d.y = 0f;
            if (d.sqrMagnitude < sp.doorwayRadius * sp.doorwayRadius) return true;
        }
        return false;
    }

    private void OnEnable() => All.Add(this);
    private void OnDisable() => All.Remove(this);

    private void OnDrawGizmos()
    {
        Gizmos.color = new Color(0.2f, 0.8f, 1f);
        Gizmos.DrawWireSphere(transform.position, 0.4f);
        Gizmos.DrawLine(transform.position, transform.position + Vector3.up * 1.8f);
        if (doorwayRadius > 0f)
        {
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.35f);
            Gizmos.DrawWireSphere(transform.position, doorwayRadius);
        }
    }
}
