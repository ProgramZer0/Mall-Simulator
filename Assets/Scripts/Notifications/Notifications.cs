using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>One message on this player's phone.</summary>
public sealed class Note
{
    public string title;
    public string body;
    /// <summary>Stays (and is counted on the phone hint) until cleared, even when the list is full.</summary>
    public bool alarm;
    public bool read;
    /// <summary>Time.unscaledTime when it came in.</summary>
    public float time;
}

/// <summary>
/// This player's notifications (purely local). Anything can post one:
///   Notifications.Post("Delivery arrived", "3 boxes at the dock");
/// NotificationToasts shows each briefly; the phone (PhoneController + NotificationsApp) lists them to
/// read or clear. For messages from the host to certain players, see NotificationManager.
/// Keeps the newest Max Kept; the oldest non-alarm ones go first.
/// </summary>
public static class Notifications
{
    public const int MaxKept = 50;

    private static readonly List<Note> notes = new(); // oldest first

    /// <summary>A new note came in.</summary>
    public static event Action<Note> Posted;
    /// <summary>The list changed (posted, read, cleared).</summary>
    public static event Action Changed;

    public static IReadOnlyList<Note> All => notes;

    public static int UnreadCount
    {
        get
        {
            int n = 0;
            foreach (var note in notes) if (!note.read) n++;
            return n;
        }
    }

    public static int AlarmCount
    {
        get
        {
            int n = 0;
            foreach (var note in notes) if (note.alarm) n++;
            return n;
        }
    }

    public static Note Post(string title, string body = "", bool alarm = false)
    {
        var note = new Note { title = title ?? "", body = body ?? "", alarm = alarm, time = Time.unscaledTime };
        notes.Add(note);
        while (notes.Count > MaxKept)
        {
            int drop = notes.FindIndex(n => !n.alarm);
            notes.RemoveAt(drop >= 0 ? drop : 0);
        }
        Posted?.Invoke(note);
        Changed?.Invoke();
        return note;
    }

    public static void Clear(Note note)
    {
        if (notes.Remove(note)) Changed?.Invoke();
    }

    public static void ClearAll()
    {
        if (notes.Count == 0) return;
        notes.Clear();
        Changed?.Invoke();
    }

    public static void MarkAllRead()
    {
        bool any = false;
        foreach (var note in notes)
            if (!note.read) { note.read = true; any = true; }
        if (any) Changed?.Invoke();
    }

    /// <summary>"just now", "4 min ago", "2 h ago".</summary>
    public static string Age(Note note)
    {
        float s = Time.unscaledTime - note.time;
        if (s < 60f) return "just now";
        if (s < 3600f) return $"{(int)(s / 60f)} min ago";
        return $"{(int)(s / 3600f)} h ago";
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetOnPlay()
    {
        notes.Clear();
        Posted = null;
        Changed = null;
    }
}
