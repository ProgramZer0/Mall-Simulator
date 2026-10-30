using Unity.Netcode;
using UnityEngine;

/// <summary>
/// A local, non-networked stand-in for an item while its store is in edit mode.
/// Built from the item's world prefab with all networking and gameplay scripts removed,
/// but keeping its look, colliders and Rigidbody, so it can be clicked, collides with the
/// other copies, and can fall when gravity is switched on for it.
/// </summary>
public class EditLocalItem : MonoBehaviour
{
    private static Transform holder;

    private Rigidbody rb;
    private bool dragging;

    /// <summary>Position of this item in the host's edit session list.</summary>
    public int Index { get; private set; }
    public ItemDef Def { get; private set; }
    public bool Gravity { get; private set; }

    public static EditLocalItem Create(ItemDef def, int index, Vector3 position, Quaternion rotation)
    {
        // Built under an inactive parent so none of the prefab's scripts ever wake up.
        var go = Instantiate(def.worldNetworkPrefab.gameObject, position, rotation, GetHolder());
        go.name = $"{def.worldNetworkPrefab.name} (editing)";

        DestroyAll<NetworkBehaviour>(go);
        DestroyAll<MonoBehaviour>(go);
        DestroyAll<NetworkObject>(go);
        DestroyAll<AudioSource>(go);

        go.transform.SetParent(null, true);

        var item = go.AddComponent<EditLocalItem>();
        item.Index = index;
        item.Def = def;
        item.rb = go.GetComponent<Rigidbody>();
        item.ApplyKinematic();
        return item;
    }

    private static Transform GetHolder()
    {
        if (holder != null) return holder;
        var go = new GameObject("[EditItemBuilder]");
        go.SetActive(false);
        go.hideFlags = HideFlags.HideInHierarchy;
        DontDestroyOnLoad(go);
        holder = go.transform;
        return holder;
    }

    private static void DestroyAll<T>(GameObject root) where T : Component
    {
        // Back to front, so components added by [RequireComponent] go after the ones needing them.
        var comps = root.GetComponentsInChildren<T>(true);
        for (int i = comps.Length - 1; i >= 0; i--)
            if (comps[i] != null) DestroyImmediate(comps[i]);
    }

    public void SetPose(Vector3 position, Quaternion rotation)
    {
        transform.SetPositionAndRotation(position, rotation);
        if (rb == null) return;

        rb.position = position;
        rb.rotation = rotation;
        if (!rb.isKinematic)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
    }

    /// <summary>Gravity on: falls and settles. Off: hangs exactly where it's put.</summary>
    public void SetGravity(bool on)
    {
        Gravity = on;
        ApplyKinematic();
    }

    /// <summary>Held still while being dragged, even if gravity is on.</summary>
    public void SetDragging(bool on)
    {
        dragging = on;
        ApplyKinematic();
    }

    private void ApplyKinematic()
    {
        if (rb == null) return;

        bool kinematic = dragging || !Gravity;
        if (kinematic && !rb.isKinematic)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
        rb.isKinematic = kinematic;
        if (!kinematic) rb.WakeUp();
    }
}
