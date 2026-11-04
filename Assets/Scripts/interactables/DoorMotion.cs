using System;
using UnityEngine;

/// <summary>
/// Moves a door between closed and open in code, so any door model works without an animation.
/// It only animates; what opens it is up to whoever calls SetOpen: DoorNet (F, automatic,
/// lockable doors) or the store (storefront doors open while the store is open).
///
/// Setup: put the moving part of each leaf under its own empty child, placed where it should move
/// around (the hinge for a swing door), and drag those into Leaves. The leaf's closed pose is how
/// it's placed in the scene.
///   Swing: turns around the leaf's up (green) axis by Open Angle.
///   Slide: moves along the leaf's red (x) axis by Distance.
///   Lift:  moves up the leaf's green (y) axis by Distance (roller shutters, garage doors).
/// Double doors: two leaves with Direction 1 and -1, so they open away from each other.
/// </summary>
public class DoorMotion : MonoBehaviour
{
    public enum Motion { Swing, Slide, Lift }

    [Serializable]
    public class Leaf
    {
        [Tooltip("The moving part (an empty at the hinge, with the door mesh under it, works best).")]
        public Transform part;
        [Tooltip("1 or -1: which way this leaf opens (flip it for the second leaf of a double door). " +
             "Swing doors with Swing Away From User work it out themselves and ignore this.")]
        public float direction = 1f;
    }

    [SerializeField] private Motion motion = Motion.Swing;
    [SerializeField] private Leaf[] leaves;
    [Tooltip("Swing: how far it turns, in degrees.")]
    [SerializeField] private float openAngle = 90f;
    [Tooltip("Slide / Lift: how far it moves, in meters.")]
    [SerializeField] private float distance = 1f;
    [Tooltip("Seconds to fully open or close.")]
    [SerializeField] private float duration = 0.6f;
    [Tooltip("Swing doors: open away from whoever opens them (DoorNet passes their side).")]
    [SerializeField] private bool swingAwayFromUser = true;

    private Vector3[] closedPos;
    private Quaternion[] closedRot;
    private float amount;      // 0 closed .. 1 open (what's shown)
    private float target;      // where it's heading
    private float side = 1f;   // which side of the door the opener stood on (1 or -1, see SideAwayFrom)

    // Swing geometry, in this object's local space (worked out from the models, so it doesn't matter
    // which way the axes point or which edge the hinge is on).
    private Vector3[] swingDir;   // per leaf: which way its middle moves for a positive turn
    private Vector3 doorNormal;   // the first leaf's swing direction: the door's "front"
    private Vector3 doorCenter;   // middle of all leaves

    public bool IsOpen => target > 0.5f;
    public Motion Type => motion;

    private void Awake()
    {
        int n = leaves != null ? leaves.Length : 0;
        closedPos = new Vector3[n];
        closedRot = new Quaternion[n];
        swingDir = new Vector3[n];
        Vector3 centerSum = Vector3.zero;
        int centers = 0;
        for (int i = 0; i < n; i++)
        {
            var part = leaves[i]?.part;
            if (part == null) continue;
            closedPos[i] = part.localPosition;
            closedRot[i] = part.localRotation;

            // The leaf's middle (from what you see under it) moves around the hinge's up axis.
            Vector3 mid = LeafMiddle(part);
            Vector3 dir = Vector3.Cross(part.up, mid - part.position);
            swingDir[i] = dir.sqrMagnitude > 1e-8f ? transform.InverseTransformDirection(dir.normalized) : Vector3.zero;
            if (doorNormal == Vector3.zero && swingDir[i] != Vector3.zero) doorNormal = swingDir[i];
            centerSum += transform.InverseTransformPoint(mid);
            centers++;
        }
        doorCenter = centers > 0 ? centerSum / centers : Vector3.zero;
        if (doorNormal == Vector3.zero) doorNormal = Vector3.forward;
    }

    private static Vector3 LeafMiddle(Transform part)
    {
        bool any = false;
        Bounds b = default;
        foreach (var r in part.GetComponentsInChildren<Renderer>(true))
        {
            if (!any) { b = r.bounds; any = true; }
            else b.Encapsulate(r.bounds);
        }
        return any ? b.center : part.position + part.forward * 0.5f;
    }

    /// <summary>
    /// Open or close. openSide (swing doors with Swing Away From User): 1 or -1, the side to swing
    /// toward. instant: jump there (e.g. for players joining late).
    /// </summary>
    public void SetOpen(bool open, float openSide = 1f, bool instant = false)
    {
        target = open ? 1f : 0f;
        if (open && swingAwayFromUser && Mathf.Abs(openSide) > 0.01f) side = Mathf.Sign(openSide);
        if (instant)
        {
            amount = target;
            Apply();
        }
    }

    /// <summary>
    /// Which side of the door someone standing here is on (1 or -1). Pass it to SetOpen and swing
    /// doors open away from them.
    /// </summary>
    public float SideAwayFrom(Vector3 position)
    {
        Vector3 local = transform.InverseTransformPoint(position) - doorCenter;
        return Vector3.Dot(local, doorNormal) >= 0f ? 1f : -1f;
    }

    private void Update()
    {
        if (Mathf.Approximately(amount, target)) return;
        float step = duration > 0f ? Time.deltaTime / duration : 1f;
        amount = Mathf.MoveTowards(amount, target, step);
        Apply();
    }

    private void Apply()
    {
        if (leaves == null || closedPos == null) return;
        float t = Mathf.SmoothStep(0f, 1f, amount);
        for (int i = 0; i < leaves.Length; i++)
        {
            var leaf = leaves[i];
            if (leaf?.part == null || i >= closedPos.Length) continue;
            float dir = leaf.direction == 0f ? 1f : leaf.direction;

            switch (motion)
            {
                case Motion.Swing:
                    // Away from the opener: turn each leaf so its middle moves to the other side of the
                    // door from them (works for either hinge edge and double doors). Otherwise Direction.
                    float sign = dir;
                    if (swingAwayFromUser && swingDir != null && i < swingDir.Length && swingDir[i] != Vector3.zero)
                        sign = -side * Mathf.Sign(Vector3.Dot(swingDir[i], doorNormal));
                    leaf.part.localRotation = closedRot[i] * Quaternion.Euler(0f, openAngle * t * sign, 0f);
                    break;
                case Motion.Slide:
                    leaf.part.localPosition = closedPos[i] + closedRot[i] * Vector3.right * (distance * t * dir);
                    break;
                case Motion.Lift:
                    leaf.part.localPosition = closedPos[i] + closedRot[i] * Vector3.up * (distance * t * dir);
                    break;
            }
        }
    }

    private void OnDrawGizmosSelected()
    {
        if (leaves == null) return;
        Gizmos.color = new Color(1f, 0.6f, 0.2f, 0.9f);
        foreach (var leaf in leaves)
        {
            if (leaf?.part == null) continue;
            Transform p = leaf.part;
            float dir = leaf.direction == 0f ? 1f : leaf.direction;
            Vector3 axis = motion == Motion.Swing ? p.up : motion == Motion.Slide ? p.right * dir : p.up * dir;
            Gizmos.DrawLine(p.position, p.position + axis * (motion == Motion.Swing ? 0.6f : distance));
            Gizmos.DrawWireSphere(p.position, 0.05f);
        }
    }
}
