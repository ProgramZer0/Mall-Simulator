using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// First-time help: the first time something happens to this player (their first crate, the first mall
/// event...), a small "Learn more about ..." alert comes up (HelpTipPopup) and bringing up the phone
/// right after opens the Help app on that topic. Each tip is shown once per machine: which ones have been
/// seen is saved here (PlayerPrefs); Settings → Reset help tips shows them all again.
///
/// From anywhere (local): HelpTips.Trigger("stocking"), with a Help topic's Id.
/// </summary>
public static class HelpTips
{
    private const string SavedKey = "HelpTips.Seen";

    private static HashSet<string> seen;
    private static readonly Queue<string> waiting = new();
    private static string pending;
    private static float pendingUntil;

    /// <summary>A tip to show now (HelpTipPopup listens).</summary>
    public static event Action<string> Show;

    /// <summary>Has this tip been shown on this machine?</summary>
    public static bool Seen(string id)
    {
        Load();
        return !string.IsNullOrEmpty(id) && seen.Contains(id);
    }

    /// <summary>
    /// Something happened for the first time: show its tip (once ever, on this machine). False: already
    /// seen, or there's no Help topic with that Id.
    /// </summary>
    public static bool Trigger(string id)
    {
        if (string.IsNullOrEmpty(id) || Seen(id) || HelpApp.TitleOf(id) == null) return false;
        seen.Add(id);
        Save();
        waiting.Enqueue(id);
        return true;
    }

    /// <summary>HelpTipPopup: the next tip waiting to be shown, if any.</summary>
    public static bool TryNext(out string id)
    {
        id = null;
        if (waiting.Count == 0) return false;
        id = waiting.Dequeue();
        Show?.Invoke(id);
        return true;
    }

    /// <summary>HelpTipPopup: this tip is up; the phone opens on it until Seconds from now.</summary>
    public static void SetPending(string id, float seconds)
    {
        pending = id;
        pendingUntil = Time.unscaledTime + seconds;
    }

    /// <summary>The phone's coming up: the tip it should open on (once), or null.</summary>
    public static string TakePending()
    {
        if (pending == null || Time.unscaledTime > pendingUntil) { pending = null; return null; }
        var id = pending;
        pending = null;
        return id;
    }

    /// <summary>Is a tip up right now (so bringing up the phone opens Help)?</summary>
    public static bool HasPending => pending != null && Time.unscaledTime <= pendingUntil;

    /// <summary>Show every tip again (Settings).</summary>
    public static void ResetSeen()
    {
        Load();
        seen.Clear();
        waiting.Clear();
        pending = null;
        PlayerPrefs.DeleteKey(SavedKey);
        PlayerPrefs.Save();
        Reset?.Invoke();
    }

    /// <summary>The seen list was cleared (HelpTipPopup starts watching for everything again).</summary>
    public static event Action Reset;

    private static void Load()
    {
        if (seen != null) return;
        seen = new HashSet<string>();
        string saved = PlayerPrefs.GetString(SavedKey, "");
        foreach (var id in saved.Split('|'))
            if (!string.IsNullOrWhiteSpace(id)) seen.Add(id.Trim());
    }

    private static void Save()
    {
        PlayerPrefs.SetString(SavedKey, string.Join("|", seen));
        PlayerPrefs.Save();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetOnPlay()
    {
        seen = null;
        waiting.Clear();
        pending = null;
        Show = null;
        Reset = null;
    }
}
