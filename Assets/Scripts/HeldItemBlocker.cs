using UnityEngine;

/// <summary>
/// Gives your own held item a body: a box the size of the item, in your hand, that walls push back on.
/// Walk or turn into a wall with something in your hands and you're pushed back far enough that the
/// item doesn't poke through. Added by itself to your own held item (and the price scanner); nothing
/// changes for other players, who just see you stop at the wall.
///
/// The box is a trigger on the Ignore Raycast layer, so it never gets in the way of aiming, picking up
/// or other players. Only the player's Held Item Blocked By layers push back (the "walls" layer by
/// default), so shelves and counters don't shove you away while stocking or checking out.
/// </summary>
public class HeldItemBlocker : MonoBehaviour
{
    private const float Skin = 0.01f;
    private static readonly Collider[] Hits = new Collider[16];

    private BoxCollider box;
    private CharacterController controller;
    private Transform player;
    private int mask;

    /// <summary>Give a held model its box. Call right after placing it in the hand.</summary>
    public static void Add(GameObject visual, Transform player, int blockedBy)
    {
        if (visual == null || player == null) return;
        var controller = player.GetComponent<CharacterController>();
        if (controller == null) return;

        Bounds local = PlacementCheck.ComputeLocalBounds(visual);
        if (local.size == Vector3.zero) return;

        var go = new GameObject("Held Item Box");
        go.layer = 2; // Ignore Raycast
        go.transform.SetParent(visual.transform, false);
        var box = go.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.center = local.center;
        box.size = local.size;

        var b = go.AddComponent<HeldItemBlocker>();
        b.box = box;
        b.controller = controller;
        b.player = player;
        b.mask = blockedBy != 0 ? blockedBy : PlacementCheck.DefaultWallMask;
    }

    // After the player has moved and turned this frame (Update), push back out of any wall.
    private void LateUpdate()
    {
        if (box == null || controller == null || !controller.enabled) return;

        Transform t = box.transform;
        Vector3 center = t.TransformPoint(box.center);
        Vector3 s = t.lossyScale;
        Vector3 half = Vector3.Scale(box.size * 0.5f, new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z)));
        int n = Physics.OverlapBoxNonAlloc(center, half, Hits, t.rotation, mask, QueryTriggerInteraction.Ignore);
        if (n == 0) return;

        Vector3 push = Vector3.zero;
        for (int i = 0; i < n; i++)
        {
            Collider other = Hits[i];
            if (other == box || other.transform.IsChildOf(player)) continue;
            if (!Physics.ComputePenetration(box, t.position, t.rotation,
                    other, other.transform.position, other.transform.rotation, out Vector3 dir, out float distance))
                continue;
            // Sideways only: looking down at the floor mustn't lift you.
            dir.y = 0f;
            Vector3 step = dir * (distance + Skin);
            // Keep the biggest push along each axis, so two walls in a corner don't add up twice.
            push.x = Mathf.Abs(step.x) > Mathf.Abs(push.x) ? step.x : push.x;
            push.z = Mathf.Abs(step.z) > Mathf.Abs(push.z) ? step.z : push.z;
        }
        if (push.sqrMagnitude > 1e-8f) controller.Move(push);
    }
}
