using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Shows one notification: a toast (NotificationToasts) or a row on the phone (NotificationsApp).
/// Every part is optional. Open Button (phone) shows or hides the full Body; Clear Button removes it.
/// Alarm Mark is shown for alarms (they stay until cleared).
/// </summary>
public class NotificationView : MonoBehaviour
{
    public TMP_Text titleText;
    public TMP_Text bodyText;
    public TMP_Text timeText;
    public GameObject alarmMark;
    [Tooltip("Shown for notes not read yet.")]
    public GameObject unreadMark;
    public Button openButton;
    public Button clearButton;
    [Tooltip("Optional: fades toasts in and out.")]
    public CanvasGroup canvasGroup;

    public Note Note { get; private set; }

    public void Show(Note note, bool fullBody)
    {
        Note = note;
        if (titleText != null) titleText.text = note.title;
        if (bodyText != null)
        {
            bodyText.text = note.body;
            bodyText.gameObject.SetActive(fullBody && !string.IsNullOrEmpty(note.body));
        }
        if (timeText != null) timeText.text = Notifications.Age(note);
        if (alarmMark != null) alarmMark.SetActive(note.alarm);
        if (unreadMark != null) unreadMark.SetActive(!note.read);
    }
}
