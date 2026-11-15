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
            bubble = new GameObject("Reaction", typeof(RectTransform)).transform; // TextMeshPro needs a RectTransform; adding it to a plain Transform swaps (destroys) the cached one
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

    private readonly System.Collections.Generic.List<GameObject> bags = new();

    /// <summary>
    /// Show this many shopping bags: at a child called "BagAnchor" if the look has one, else at their
    /// right side; each extra bag hangs a little further along. No prefab: a small paper-bag box.
    /// </summary>
    public void SetBags(int count, GameObject prefab)
    {
        while (bags.Count > count) { Destroy(bags[^1]); bags.RemoveAt(bags.Count - 1); }
        Transform anchor = FindChild(transform, "BagAnchor");
        while (bags.Count < count)
        {
            int i = bags.Count;
            GameObject bag;
            if (prefab != null) bag = Instantiate(prefab);
            else
            {
                bag = GameObject.CreatePrimitive(PrimitiveType.Cube);
                bag.transform.localScale = new Vector3(0.12f, 0.3f, 0.25f);
                var r = bag.GetComponent<Renderer>();
                if (r != null) r.material.color = new Color(0.78f, 0.62f, 0.42f);
            }
            bag.name = "Bag";
            foreach (var c in bag.GetComponentsInChildren<Collider>(true)) Destroy(c);
            var t = bag.transform;
            if (anchor != null)
            {
                t.SetParent(anchor, false);
                t.localPosition = new Vector3(0f, 0f, 0.08f * i);
            }
            else
            {
                t.SetParent(transform, false);
                t.localPosition = new Vector3(0.32f + 0.05f * i, 0.75f - lift, 0.1f * i - 0.05f);
            }
            if (prefab == null) t.localScale = new Vector3(0.12f, 0.3f, 0.25f) * (anchor != null ? 1f : 1f / Mathf.Max(0.01f, transform.lossyScale.x));
            bags.Add(bag);
        }
    }

    private static Transform FindChild(Transform root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t;
        return null;
    }

    /// <summary>Where the customer is now (the view eases there).</summary>
    public void SetTarget(Vector3 position, float yaw, bool snap = false)
    {
        target = position;
        targetYaw = yaw;
        if (snap) transform.SetPositionAndRotation(position + Vector3.up * lift, Quaternion.Euler(0f, yaw, 0f));
    }

    /// <summary>
    /// Ease toward where they are (called by the CustomerManager for every view in one loop, cheaper than an
    /// Update on each). Ease: this frame's 1 - e^(-dt·10); settle: 1 - e^(-dt·8).
    /// </summary>
    public void Tick(float dt, float ease, float settle)
    {
        if (bubble != null) UpdateBubble();
        Vector3 goal = target + Vector3.up * lift;
        transform.GetPositionAndRotation(out Vector3 before, out Quaternion was);
        Quaternion want = Quaternion.Euler(0f, targetYaw, 0f);
        bool still = (goal - before).sqrMagnitude < 1e-6f && Quaternion.Angle(was, want) < 0.5f;
        Vector3 now = before;
        if (!still) // standing still: leave the transform alone (no change for the renderer to pick up)
        {
            now = (goal - before).sqrMagnitude > 9f
                ? goal // far behind (lost updates, a teleport): jump
                : Vector3.Lerp(before, goal, ease);
            transform.SetPositionAndRotation(now, Quaternion.Slerp(was, want, ease));
        }

        if (!hasSpeed) return;
        Vector3 moved = now - before;
        moved.y = 0f;
        float speed = dt > 0f ? moved.magnitude / dt : 0f;
        if (speed < 0.01f && shownSpeed < 0.01f) return;
        shownSpeed = Mathf.Lerp(shownSpeed, speed, settle);
        animator.SetFloat(SpeedId, shownSpeed);
    }
}
