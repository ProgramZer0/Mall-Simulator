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

    public static CustomerView Create(ushort id, GameObject look, Vector3 position, float yaw, Transform parent,
                                      float scale = 1f)
    {
        GameObject go;
        float lift = 0f;
        if (look != null)
        {
            go = Instantiate(look, position, Quaternion.Euler(0f, yaw, 0f), parent);
            if (!Mathf.Approximately(scale, 1f)) go.transform.localScale *= scale; // a child
        }
        else
        {
            lift = 0.9f * scale;
            go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.transform.SetParent(parent, false);
            go.transform.localScale = new Vector3(0.6f, 0.9f, 0.6f) * scale;
            go.transform.SetPositionAndRotation(position + Vector3.up * lift, Quaternion.Euler(0f, yaw, 0f));
            var r = go.GetComponent<Renderer>();
            PlainLook.Apply(r, Color.HSVToRGB((id * 0.137f) % 1f, 0.45f, 0.85f)); // (a primitive's own material is pink in builds)
        }
        go.name = $"Customer {id}";
        foreach (var c in go.GetComponentsInChildren<Collider>(true)) Destroy(c); // never in anyone's way
        foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) Destroy(rb);

        var view = go.AddComponent<CustomerView>();
        view.Id = id;
        view.lift = lift;
        view.target = position;
        view.targetYaw = yaw;
        view.shown = position + Vector3.up * lift;
        view.animator = go.GetComponentInChildren<Animator>();
        if (view.animator != null)
        {
            view.animator.cullingMode = AnimatorCullingMode.CullCompletely;
            foreach (var p in view.animator.parameters)
                if (p.nameHash == SpeedId && p.type == AnimatorControllerParameterType.Float) view.hasSpeed = true;
        }
        SetLayer(go);
        return view;
    }

    private static int customersLayer = -2;

    /// <summary>Everything a customer is made of goes on the "Customers" layer (if it's set up).</summary>
    public static void SetLayer(GameObject go)
    {
        if (customersLayer == -2) customersLayer = LayerMask.NameToLayer("Customers");
        if (customersLayer < 0 || go == null) return;
        foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = customersLayer;
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

    /// <summary>Where their feet are shown.</summary>
    public Vector3 Feet => transform.position - Vector3.up * lift;
    /// <summary>A child (set when made).</summary>
    public bool IsChild { get; set; }

    private readonly System.Collections.Generic.List<GameObject> bags = new();
    private GameObject carried;

    /// <summary>
    /// Something held in front of them in both hands (the delivery guy's package): at a child called
    /// "CarryAnchor" if the look has one, else at chest height in front. Null: nothing.
    /// </summary>
    public void SetCarry(GameObject model)
    {
        if (carried != null) Destroy(carried);
        carried = model;
        if (model == null) return;
        SetLayer(model);
        foreach (var c in model.GetComponentsInChildren<Collider>(true)) Destroy(c);
        var t = model.transform;
        Transform anchor = FindChild(transform, "CarryAnchor");
        if (anchor != null) { t.SetParent(anchor, false); t.localPosition = Vector3.zero; return; }
        var me = transform;
        Vector3 feet = me.position - Vector3.up * lift;
        t.SetPositionAndRotation(feet + Vector3.up * 1.0f + me.forward * 0.4f, me.rotation * t.rotation);
        t.SetParent(me, true);
    }

    private GameObject pushed;

    /// <summary>
    /// Something pushed in front of them (a stroller): at a child called "PushAnchor" if the look has one,
    /// else on the floor in front of them, facing the way they face. Null: nothing.
    /// </summary>
    public void SetPush(GameObject model)
    {
        if (pushed != null) Destroy(pushed);
        pushed = model;
        if (model == null) return;
        SetLayer(model);
        foreach (var c in model.GetComponentsInChildren<Collider>(true)) Destroy(c);
        foreach (var b in model.GetComponentsInChildren<Rigidbody>(true)) Destroy(b);
        var t = model.transform;
        Transform anchor = FindChild(transform, "PushAnchor");
        if (anchor != null) { t.SetParent(anchor, false); t.localPosition = Vector3.zero; t.localRotation = Quaternion.identity; return; }
        var me = transform;
        Vector3 feet = me.position - Vector3.up * lift;
        t.SetPositionAndRotation(feet + me.forward * 0.75f, me.rotation * t.rotation);
        t.SetParent(me, true);
    }

    /// <summary>
    /// Show this many shopping bags: at a child called "BagAnchor" if the look has one, else at their
    /// right side; each extra bag hangs a little further along. No prefab: a small paper-bag box.
    /// </summary>
    public void SetBags(int count, GameObject prefab, bool randomColors = false)
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
                PlainLook.Apply(r, new Color(0.78f, 0.62f, 0.42f));
            }
            bag.name = "Bag";
            SetLayer(bag);
            if (randomColors) PaintBag(bag, i);
            foreach (var c in bag.GetComponentsInChildren<Collider>(true)) Destroy(c);
            var t = bag.transform;
            if (anchor != null)
            {
                t.SetParent(anchor, false);
                t.localPosition = new Vector3(0f, 0f, 0.08f * i);
            }
            else
            {
                // Placed in the world, beside their hand, then attached keeping that (the plain capsule is
                // scaled, so local positions under it would land inside the body).
                var me = transform;
                Vector3 feet = me.position - Vector3.up * lift;
                t.SetPositionAndRotation(
                    feet + Vector3.up * 0.6f + me.right * (0.42f + 0.07f * i) + me.forward * (0.12f * i - 0.06f),
                    me.rotation * (prefab != null ? prefab.transform.localRotation : Quaternion.identity)); // the model's own turn (Blender's -90 on X) kept
                if (prefab == null) t.localScale = new Vector3(0.12f, 0.3f, 0.25f);
                t.SetParent(me, true);
            }
            bags.Add(bag);
        }
    }

    // A color from the palette, picked from who they are and which bag it is (so every machine agrees
    // without sending it). Painted where the bag has Paint Parts, else all over.
    private void PaintBag(GameObject bag, int index)
    {
        int n = PaintPalette.Instance != null ? PaintPalette.Instance.Count : 0;
        if (n == 0) return;
        uint h = unchecked((uint)Id * 2654435761u ^ (uint)(index + 1) * 40503u);
        h ^= h >> 15;
        byte color = (byte)(1 + h % (uint)n);
        if (PaintParts.ZonesOf(bag).Count > 0) PaintParts.ApplySet(bag, PaintSet.All(color));
        else foreach (var r in bag.GetComponentsInChildren<Renderer>()) PaintPalette.Apply(r, -1, color);
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
    /// <summary>Where they're shown standing (feet), as of the last Tick: no transform read needed.</summary>
    public Vector3 Shown => shown - Vector3.up * lift;
    private Vector3 shown;
    private Transform tr;

    public void Tick(float dt, float ease, float settle)
    {
        if (bubble != null) UpdateBubble();
        if (tr == null) tr = transform;
        Vector3 goal = target + Vector3.up * lift;
        tr.GetPositionAndRotation(out Vector3 before, out Quaternion was);
        Quaternion want = Quaternion.Euler(0f, targetYaw, 0f);
        bool still = (goal - before).sqrMagnitude < 1e-6f && Quaternion.Angle(was, want) < 0.5f;
        Vector3 now = before;
        if (!still) // standing still: leave the transform alone (no change for the renderer to pick up)
        {
            now = (goal - before).sqrMagnitude > 9f
                ? goal // far behind (lost updates, a teleport): jump
                : Vector3.Lerp(before, goal, ease);
            tr.SetPositionAndRotation(now, Quaternion.Slerp(was, want, ease));
        }
        shown = now;

        if (!hasSpeed) return;
        Vector3 moved = now - before;
        moved.y = 0f;
        float speed = dt > 0f ? moved.magnitude / dt : 0f;
        if (speed < 0.01f && shownSpeed < 0.01f) return;
        shownSpeed = Mathf.Lerp(shownSpeed, speed, settle);
        animator.SetFloat(SpeedId, shownSpeed);
    }
}
