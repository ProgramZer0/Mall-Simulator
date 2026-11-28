using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Your own body in first person, without seeing the inside of your head: put it on the player model
/// (next to its Animator). On your own player only, the head bone is shrunk to nothing every frame (after the
/// animation), and anything listed in Hide (hair, a hat, glasses, a separate head mesh) only casts shadows.
/// Everyone else sees the whole body as normal.
/// </summary>
public class FirstPersonBody : MonoBehaviour
{
    [Tooltip("Shrink the head bone on your own player (a Humanoid's Head, or Head Bone below).")]
    [SerializeField] private bool hideHead = true;
    [Tooltip("Optional: the head bone, if the model isn't Humanoid.")]
    [SerializeField] private Transform headBone;
    [Tooltip("Meshes you shouldn't see on your own player (they still cast shadows).")]
    [SerializeField] private Renderer[] hide = new Renderer[0];

    private NetworkObject net;
    private Transform head;
    private Vector3 headScale = Vector3.one;
    private bool hidden;

    private void Awake()
    {
        net = GetComponentInParent<NetworkObject>();
        head = headBone;
        if (head == null)
        {
            var animator = GetComponent<Animator>();
            if (animator != null && animator.isHuman) head = animator.GetBoneTransform(HumanBodyBones.Head);
        }
        if (head != null) headScale = head.localScale;
    }

    private bool Mine => net != null && net.IsSpawned && net.IsOwner;

    private void LateUpdate()
    {
        bool mine = Mine;
        if (mine != hidden)
        {
            hidden = mine;
            foreach (var r in hide)
                if (r != null) r.shadowCastingMode = mine ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;
            if (!mine && head != null) head.localScale = headScale;
        }
        if (mine && hideHead && head != null) head.localScale = Vector3.one * 0.001f;
    }

    private void OnDisable()
    {
        if (head != null) head.localScale = headScale;
    }
}
