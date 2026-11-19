using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Where players appear (the lobby, or the mall before they're sent to their store). Put a few around:
/// players take them in turn. Its forward (blue arrow) is the way they face.
/// </summary>
public class PlayerSpawnPoint : MonoBehaviour
{
    private static readonly List<PlayerSpawnPoint> all = new();
    private static int next;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetOnPlay() { all.Clear(); next = 0; }

    private void OnEnable() => all.Add(this);
    private void OnDisable() => all.Remove(this);

    /// <summary>The next spawn point's spot (false, with the fallback, when there are none).</summary>
    public static bool Pick(out Vector3 position, out Quaternion facing, Vector3 fallback, Quaternion fallbackFacing)
    {
        all.RemoveAll(p => p == null);
        if (all.Count == 0)
        {
            position = fallback;
            facing = fallbackFacing;
            return false;
        }
        var p = all[next++ % all.Count];
        position = p.transform.position;
        facing = Quaternion.Euler(0f, p.transform.eulerAngles.y, 0f);
        return true;
    }

    private void OnDrawGizmos()
    {
        Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.8f);
        Gizmos.DrawWireCube(transform.position + Vector3.up, new Vector3(0.6f, 2f, 0.6f));
        Gizmos.DrawLine(transform.position + Vector3.up, transform.position + Vector3.up + transform.forward * 0.8f);
    }
}
