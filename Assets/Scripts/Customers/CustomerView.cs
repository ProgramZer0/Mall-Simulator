using UnityEngine;

/// <summary>
/// What a customer looks like on one machine: a look prefab (or a plain capsule) eased toward where
/// the host says the customer is. No colliders, no physics, no networking of its own; the
/// CustomerManager moves it. If the look has an Animator with a float "Speed" parameter, it's set
/// to the customer's walking speed (m/s).
/// </summary>
public class CustomerView : MonoBehaviour
{
    private static readonly int SpeedId = Animator.StringToHash("Speed");

    public ushort Id { get; private set; }

    private Vector3 target;
    private float targetYaw;
    private Animator animator;
    private bool hasSpeed;
    private float shownSpeed;
    private float lift; // the plain capsule's pivot is its middle; a look prefab's is its feet

    public static CustomerView Create(ushort id, GameObject look, Vector3 position, float yaw, Transform parent)
    {
        GameObject go;
        float lift = 0f;
        if (look != null) go = Instantiate(look, position, Quaternion.Euler(0f, yaw, 0f), parent);
        else
        {
            lift = 0.9f;
            go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.transform.SetParent(parent, false);
            go.transform.localScale = new Vector3(0.6f, 0.9f, 0.6f);
            go.transform.SetPositionAndRotation(position + Vector3.up * lift, Quaternion.Euler(0f, yaw, 0f));
            var r = go.GetComponent<Renderer>();
            if (r != null) r.material.color = Color.HSVToRGB((id * 0.137f) % 1f, 0.45f, 0.85f);
        }
        go.name = $"Customer {id}";
        foreach (var c in go.GetComponentsInChildren<Collider>(true)) Destroy(c); // never in anyone's way
        foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) Destroy(rb);

        var view = go.AddComponent<CustomerView>();
        view.Id = id;
        view.lift = lift;
        view.target = position;
        view.targetYaw = yaw;
        view.animator = go.GetComponentInChildren<Animator>();
        if (view.animator != null)
        {
            view.animator.cullingMode = AnimatorCullingMode.CullCompletely;
            foreach (var p in view.animator.parameters)
                if (p.nameHash == SpeedId && p.type == AnimatorControllerParameterType.Float) view.hasSpeed = true;
        }
        return view;
    }

    /// <summary>Where the customer is now (the view eases there).</summary>
    public void SetTarget(Vector3 position, float yaw, bool snap = false)
    {
        target = position;
        targetYaw = yaw;
        if (snap) transform.SetPositionAndRotation(position + Vector3.up * lift, Quaternion.Euler(0f, yaw, 0f));
    }

    private void Update()
    {
        Vector3 goal = target + Vector3.up * lift;
        Vector3 before = transform.position;
        Vector3 now = (goal - before).sqrMagnitude > 9f
            ? goal // far behind (lost updates, a teleport): jump
            : Vector3.Lerp(before, goal, 1f - Mathf.Exp(-Time.deltaTime * 10f));
        Quaternion rot = Quaternion.Slerp(transform.rotation, Quaternion.Euler(0f, targetYaw, 0f),
                                          1f - Mathf.Exp(-Time.deltaTime * 10f));
        transform.SetPositionAndRotation(now, rot);

        if (!hasSpeed) return;
        Vector3 moved = now - before;
        moved.y = 0f;
        float speed = Time.deltaTime > 0f ? moved.magnitude / Time.deltaTime : 0f;
        shownSpeed = Mathf.Lerp(shownSpeed, speed, 1f - Mathf.Exp(-Time.deltaTime * 8f));
        animator.SetFloat(SpeedId, shownSpeed);
    }
}
