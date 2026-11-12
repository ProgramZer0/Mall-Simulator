using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Quick, small messages in the corner of the HUD as notifications come in (see Notifications).
///
/// Setup: on the HUD canvas, an empty anchored bottom-right with a Vertical Layout Group (Child
/// Alignment Lower Right) as Parent; Toast Prefab a NotificationView (title, body, optional Canvas
/// Group for fading). Toasts are reused, never made and thrown away.
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

    private readonly List<(NotificationView view, float shownAt, float until)> shown = new();
    private readonly Stack<NotificationView> pool = new();

    private void Awake()
    {
        if (parent == null) parent = transform as RectTransform;
    }

    private void OnEnable() => Notifications.Posted += OnPosted;
    private void OnDisable() => Notifications.Posted -= OnPosted;

    private void OnPosted(Note note)
    {
        if (toastPrefab == null || PhoneController.IsOpen) return;
        while (shown.Count >= maxShown) Hide(0);

        var view = pool.Count > 0 ? pool.Pop() : Instantiate(toastPrefab, parent);
        view.gameObject.SetActive(true);
        view.transform.SetAsLastSibling();
        view.Show(note, true);
        if (view.timeText != null) view.timeText.text = "";
        float now = Time.unscaledTime;
        shown.Add((view, now, now + (note.alarm ? alarmSeconds : showSeconds)));
    }

    private void Update()
    {
        if (shown.Count == 0) return;
        float now = Time.unscaledTime;
        bool phone = PhoneController.IsOpen;
        for (int i = shown.Count - 1; i >= 0; i--)
        {
            var (view, from, until) = shown[i];
            if (phone || now >= until || view.Note == null) { Hide(i); continue; }
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
