using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Quick, small messages in the corner of the HUD as notifications come in (see Notifications).
///
/// Setup: on the HUD canvas, Parent: any RectTransform (e.g. the whole HUD). Toasts are placed by code
/// in its bottom-right corner (Margin in), newest at the bottom, older ones pushed up: no layout group.
/// Toast Prefab: a NotificationView (title, body, optional Canvas Group for fading). It's sized to
/// Toast Size (0 = the prefab's own size), and its time, buttons and unread dot are hidden, so the
/// phone's row prefab works as a toast too. Toasts are reused, never made and thrown away.
/// Hidden while the phone is open (it shows them all).
/// </summary>
public class NotificationToasts : MonoBehaviour
{
    [SerializeField] private NotificationView toastPrefab;
    [SerializeField] private RectTransform parent;
    [SerializeField, Min(0.5f)] private float showSeconds = 3f;
    [Tooltip("Alarms stay up a little longer.")]
    [SerializeField, Min(0.5f)] private float alarmSeconds = 5f;
    [SerializeField, Min(0f)] private float fadeSeconds = 0.25f;
    [SerializeField, Min(1)] private int maxShown = 3;
    [Tooltip("Each toast's size in the HUD's units (0 = the prefab's size).")]
    [SerializeField] private Vector2 toastSize = new(320f, 70f);
    [Tooltip("Gap from the corner of Parent (right, bottom).")]
    [SerializeField] private Vector2 margin = new(16f, 16f);
    [SerializeField, Min(0f)] private float spacing = 6f;

    private readonly List<(NotificationView view, float shownAt, float until)> shown = new();
    private readonly Stack<NotificationView> pool = new();

    private void Awake()
    {
        if (parent == null) parent = transform as RectTransform;
        if (parent != null)
            foreach (var g in parent.GetComponents<UnityEngine.UI.LayoutGroup>()) g.enabled = false; // we place them
    }

    private void OnEnable() => Notifications.Posted += OnPosted;
    private void OnDisable() => Notifications.Posted -= OnPosted;

    private void OnPosted(Note note)
    {
        if (toastPrefab == null || PhoneController.IsOpen) return;
        while (shown.Count >= maxShown) Hide(0);

        var view = pool.Count > 0 ? pool.Pop() : MakeToast();
        view.gameObject.SetActive(true);
        view.transform.SetAsLastSibling();
        view.Show(note, true);
        // Just the message: no time, buttons or unread dot on a toast.
        if (view.timeText != null) view.timeText.gameObject.SetActive(false);
        if (view.unreadMark != null) view.unreadMark.SetActive(false);
        float now = Time.unscaledTime;
        shown.Add((view, now, now + (note.alarm ? alarmSeconds : showSeconds)));
        Place();
    }

    private NotificationView MakeToast()
    {
        var view = Instantiate(toastPrefab, parent);
        if (view.openButton != null) view.openButton.gameObject.SetActive(false);
        if (view.clearButton != null) view.clearButton.gameObject.SetActive(false);
        // Never in the way of clicks.
        foreach (var g in view.GetComponentsInChildren<UnityEngine.UI.Graphic>(true)) g.raycastTarget = false;
        return view;
    }

    // Bottom-right corner: newest at the bottom, older ones above it.
    private void Place()
    {
        float y = margin.y;
        for (int i = shown.Count - 1; i >= 0; i--)
        {
            var rt = (RectTransform)shown[i].view.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(1f, 0f);
            if (toastSize.x > 0f && toastSize.y > 0f) rt.sizeDelta = toastSize;
            rt.anchoredPosition = new Vector2(-margin.x, y);
            y += rt.rect.height + spacing;
        }
    }

    private void Update()
    {
        if (shown.Count == 0) return;
        float now = Time.unscaledTime;
        bool phone = PhoneController.IsOpen;
        for (int i = shown.Count - 1; i >= 0; i--)
        {
            var (view, from, until) = shown[i];
            if (phone || now >= until || view.Note == null) { Hide(i); Place(); continue; }
            if (view.canvasGroup != null && fadeSeconds > 0f)
                view.canvasGroup.alpha = Mathf.Clamp01(Mathf.Min(now - from, until - now) / fadeSeconds);
        }
    }

    private void Hide(int index)
    {
        var view = shown[index].view;
        shown.RemoveAt(index);
        view.gameObject.SetActive(false);
        pool.Push(view);
    }
}
