using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The phone's notifications app (see PhoneController): every notification, newest first. Click one
/// (its Open Button) to see its full message; Clear removes it; Clear All removes everything.
/// Opening the app marks everything read.
///
/// Setup: Row Prefab (a NotificationView with Open and Clear buttons), Row Parent (Vertical Layout
/// Group, in a Scroll View). Optional: Clear All Button, Empty Text ("No notifications").
/// </summary>
public class NotificationsApp : MonoBehaviour, IPhoneApp
{
    [SerializeField] private string appName = "Notifications";
    [SerializeField] private NotificationView rowPrefab;
    [SerializeField] private RectTransform rowParent;
    [SerializeField] private Button clearAllButton;
    [SerializeField] private GameObject emptyText;

    private readonly List<NotificationView> rows = new();
    private readonly HashSet<Note> expanded = new();
    private bool open;

    public string AppName => appName;

    private void Awake()
    {
        if (clearAllButton != null) clearAllButton.onClick.AddListener(Notifications.ClearAll);
    }

    public void OnAppOpened()
    {
        open = true;
        Rebuild();
        // Read now, but the rows keep their unread marks until the list next changes.
        Notifications.MarkAllRead();
        Notifications.Changed += Rebuild;
    }

    public void OnAppClosed()
    {
        if (!open) return;
        open = false;
        Notifications.Changed -= Rebuild;
        expanded.Clear();
    }

    private void OnDisable() => OnAppClosed();

    // Rows are reused: only as many are made as notes ever shown at once (at most Max Kept).
    private void Rebuild()
    {
        if (rowPrefab == null || rowParent == null) return;
        var all = Notifications.All;
        expanded.RemoveWhere(n => !Contains(all, n));
        while (rows.Count < all.Count)
        {
            var row = Instantiate(rowPrefab, rowParent);
            var r = row;
            if (row.openButton != null) row.openButton.onClick.AddListener(() => Toggle(r.Note));
            if (row.clearButton != null) row.clearButton.onClick.AddListener(() => { if (r.Note != null) Notifications.Clear(r.Note); });
            rows.Add(row);
        }
        for (int i = 0; i < rows.Count; i++)
        {
            bool used = i < all.Count;
            rows[i].gameObject.SetActive(used);
            if (!used) continue;
            var note = all[all.Count - 1 - i]; // newest first
            rows[i].Show(note, expanded.Contains(note));
        }
        if (emptyText != null) emptyText.SetActive(all.Count == 0);
    }

    private void Toggle(Note note)
    {
        if (note == null) return;
        if (!expanded.Remove(note)) expanded.Add(note);
        Rebuild();
    }

    private static bool Contains(IReadOnlyList<Note> list, Note n)
    {
        for (int i = 0; i < list.Count; i++) if (list[i] == n) return true;
        return false;
    }
}
