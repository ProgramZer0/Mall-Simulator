using UnityEngine;

/// <summary>
/// A box where nothing may be placed: no items set down or hung, and no windows or doors cut. Use it
/// on parts of a wall that aren't flat (pillars, trim, a ledge, a sign board), or anywhere else that
/// must stay clear.
///
/// Setup: add it to an object with a BoxCollider (one is added if missing) and size the box over the
/// area. It works inside front wall module prefabs too. The box becomes a trigger on the Ignore
/// Raycast layer, so it never blocks walking, aiming or picking things up.
/// </summary>
[RequireComponent(typeof(BoxCollider))]
public class NoPlaceArea : MonoBehaviour
{
    private static readonly Collider[] Hits = new Collider[32];

    private void Awake()
    {
        var box = GetComponent<BoxCollider>();
        box.isTrigger = true;
        gameObject.layer = 2; // Ignore Raycast
    }

    private void Reset()
    {
        GetComponent<BoxCollider>().isTrigger = true;
    }

    /// <summary>Does a box (world center, half size, rotation) touch any No Place Area? Returns its collider.</summary>
    public static Collider Find(Vector3 center, Vector3 halfExtents, Quaternion rotation)
    {
        int n = Physics.OverlapBoxNonAlloc(center, halfExtents, Hits, rotation, Physics.AllLayers, QueryTriggerInteraction.Collide);
        for (int i = 0; i < n; i++)
            if (Hits[i].isTrigger && Hits[i].TryGetComponent<NoPlaceArea>(out _)) return Hits[i];
        return null;
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (!TryGetComponent<BoxCollider>(out var box)) return;
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.25f);
        Gizmos.DrawCube(box.center, box.size);
        Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.9f);
        Gizmos.DrawWireCube(box.center, box.size);
    }
#endif
}
