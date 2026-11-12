using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The phone's notifications app (see PhoneScreen): every notification, newest first. Click one (its
/// Open Button) to see its full message; Clear removes it; Clear All removes everything. Opening the
/// app marks everything read.
///
/// Setup: a panel under the phone's canvas. Row Parent: a Scroll View's Content (anchored top,
/// stretched across). Row Prefab: a NotificationView with Open and Clear buttons; its Body is shown
/// only when opened. Rows are stacked by code (Row Height; an opened row grows to fit its Body):
/// no layout group. Optional: Clear All Button, Empty Text ("No notifications").
/// </summary>
public class NotificationsApp : MonoBehaviour, IPhoneApp
{
    [SerializeField] private string appName = "Notifications";
    [SerializeField] private Sprite icon;
    [SerializeField] private NotificationView rowPrefab;
    [SerializeField] private RectTransform rowParent;
    [SerializeField, Min(1f)] private float rowHeight = 56f;
    [SerializeField, Min(0f)] private float rowSpacing = 4f;
    [SerializeField, Min(0f)] private float padding = 4f;
    [SerializeField] private Button clearAllButton;
    [SerializeField] private GameObject emptyText;

    private readonly List<NotificationView> rows = new();
    private readonly HashSet<Note> expanded = new();
    private bool open;

    public string AppName => appName;
    public Sprite Icon => icon;
    public int Badge => Notifications.UnreadCount;

    private void Awake()
    {
        if (clearAllButton != null) clearAllButton.onClick.AddListener(Notifications.ClearAll);
        if (rowParent != null)
        {
            foreach (var g in rowParent.GetComponents<LayoutGroup>()) g.enabled = false;
            foreach (var f in rowParent.GetComponents<ContentSizeFitter>()) f.enabled = false;
        }
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

        float y = padding;
        float width = rowParent.rect.width - padding * 2f;
        for (int i = 0; i < rows.Count; i++)
        {
            bool used = i < all.Count;
            rows[i].gameObject.SetActive(used);
            if (!used) continue;
            var note = all[all.Count - 1 - i]; // newest first
            bool full = expanded.Contains(note);
            rows[i].Show(note, full);

            // Opened: tall enough for the body text too.
            float height = rowHeight;
            if (full && rows[i].bodyText != null && !string.IsNullOrEmpty(note.body))
                height += rows[i].bodyText.GetPreferredValues(note.body, Mathf.Max(10f, width - 16f), 0f).y + 6f;

            var rt = (RectTransform)rows[i].transform;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(padding, -(y + height));
            rt.offsetMax = new Vector2(-padding, -y);
            y += height + rowSpacing;
        }
        rowParent.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, all.Count == 0 ? 0f : y - rowSpacing + padding);
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
