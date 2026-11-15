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

    // The bubble over their head (made the first time it's needed).
    private Transform bubble;
    private TMPro.TextMeshPro bubbleText;
    private SpriteRenderer bubbleIcon;
    private float bubbleUntil, bubbleFor;
    private Color bubbleColor;

    /// <summary>Show a reaction over their head for a few seconds (an icon if given, else the text).</summary>
    public void ShowReaction(string text, Color color, Sprite icon, float seconds)
    {
        if (bubble == null)
        {
            bubble = new GameObject("Reaction").transform;
            bubble.SetParent(transform, false);
            bubble.localPosition = Vector3.up * (2.15f - lift);
            bubbleText = bubble.gameObject.AddComponent<TMPro.TextMeshPro>();
            bubbleText.alignment = TMPro.TextAlignmentOptions.Center;
            bubbleText.fontSize = 2.2f;
            bubbleText.outlineWidth = 0.2f;
            bubbleText.rectTransform.sizeDelta = new Vector2(4f, 1f);
            var iconGo = new GameObject("Icon");
            iconGo.transform.SetParent(bubble, false);
            bubbleIcon = iconGo.AddComponent<SpriteRenderer>();
            iconGo.transform.localScale = Vector3.one * 0.5f;
        }
        bubble.gameObject.SetActive(true);
        bubbleText.text = icon == null ? text ?? "" : "";
        bubbleIcon.sprite = icon;
        bubbleColor = color;
        bubbleFor = Mathf.Max(0.1f, seconds);
        bubbleUntil = Time.time + bubbleFor;
    }

    private void UpdateBubble()
    {
        if (bubble == null || !bubble.gameObject.activeSelf) return;
        float left = bubbleUntil - Time.time;
        if (left <= 0f) { bubble.gameObject.SetActive(false); return; }
        var cam = Camera.main;
        if (cam != null) bubble.rotation = Quaternion.LookRotation(bubble.position - cam.transform.position); // face the camera
        float rise = 1f - left / bubbleFor;
        bubble.localPosition = Vector3.up * (2.15f - lift + rise * 0.3f);
        var c = bubbleColor;
        c.a = Mathf.Clamp01(left / 0.5f); // fade out at the end
        bubbleText.color = c;
        bubbleIcon.color = new Color(1f, 1f, 1f, c.a);
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
        UpdateBubble();
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
