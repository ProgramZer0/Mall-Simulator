using UnityEngine;

/// <summary>
/// Holds a container's stock models and follows the container's position and rotation (not its
/// scale). Added at runtime by StockContainer; nothing to set up.
///
/// The models aren't parented to the furniture itself because furniture is often scaled unevenly
/// (e.g. a rack stretched along one side). A model turned relative to an unevenly scaled parent
/// gets squashed and skewed, so they live under this unscaled holder instead.
/// </summary>
public class StockVisualFollower : MonoBehaviour
{
    /// <summary>The furniture to follow.</summary>
    public Transform Target { get; private set; }

    /// <summary>The container these models belong to (null for edit-mode copies).</summary>
    public StockContainer Container { get; private set; }

    public static StockVisualFollower Create(Transform target, StockContainer container)
    {
        var go = new GameObject($"[Stock] {target.name}");
        var f = go.AddComponent<StockVisualFollower>();
        f.Target = target;
        f.Container = container;
        f.Sync();
        return f;
    }

    /// <summary>Snap to the target now (call before placing models by world position).</summary>
    public void Sync()
    {
        if (Target != null) transform.SetPositionAndRotation(Target.position, Target.rotation);
    }

    private void LateUpdate()
    {
        if (Target == null) { Destroy(gameObject); return; } // the furniture is gone
        if (Target.position != transform.position || Target.rotation != transform.rotation) Sync();
    }
}
