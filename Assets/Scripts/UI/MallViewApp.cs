using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// The phone's Mall app: what's going on in the mall. Your help points (and how much extra your vote
/// counts), the season and any events on now (with time left), a mall vote that's open, and some
/// numbers: shoppers, players, stores open and owned, your team's level. Refreshes while open.
///
/// Setup: like the Help app (duplicate that app object under the phone prefab's screen and swap HelpApp
/// for this).
/// </summary>
public class MallViewApp : MonoBehaviour, IPhoneApp
{
    [SerializeField] private string appName = "Mall";
    [SerializeField] private Sprite icon;
    [SerializeField] private PhoneUI.Style style = new();
    [SerializeField, Min(0.2f)] private float refreshEvery = 1f;

    public string AppName => appName;
    public Sprite Icon => icon;
    public int Badge => 0;

    private RectTransform content;
    private float y, nextRefresh;
    private readonly List<MallEventDef> events = new();

    public void OnAppOpened()
    {
        if (content == null) content = PhoneUI.ScrollPage((RectTransform)transform);
        Build();
    }

    public void OnAppClosed() { }

    private void Update()
    {
        if (content == null || Time.unscaledTime < nextRefresh) return;
        Build();
    }

    private void Build()
    {
        nextRefresh = Time.unscaledTime + refreshEvery;
        PhoneUI.Clear(content);
        y = style.padding;
        var nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening) { Line("Not in a mall."); Finish(); return; }
        ulong me = nm.LocalClientId;
        ulong team = MallManager.GetTeamOf(me);

        // You.
        Header("You");
        var hp = HelpPoints.Instance;
        if (hp != null && hp.IsSpawned)
        {
            Row("Help points", hp.GetPoints(me).ToString());
            int weight = hp.VoteWeightOf(me);
            Row("Your mall vote counts", weight > 1 ? $"×{weight}" : "×1");
            Line("<size=85%>Earn help points by helping out: returning lost children, and more.</size>");
        }
        Row("Team level", ProgressionManager.LevelOf(team).ToString());

        // Season and events.
        var em = MallEventManager.Instance;
        if (em != null && em.IsSpawned)
        {
            var season = em.CurrentSeason;
            if (season != null)
            {
                Header("Season");
                Row(season.displayName, Left(em.SecondsLeft(season)));
                if (!string.IsNullOrEmpty(season.description)) Line(season.description);
            }
            events.Clear();
            em.GetActive(events);
            events.Remove(season);
            Header(events.Count == 1 ? "Event on now" : "Events on now");
            if (events.Count == 0) Line("Nothing special going on.");
            foreach (var e in events)
            {
                Row(e.displayName, Left(em.SecondsLeft(e)));
                if (!string.IsNullOrEmpty(e.description)) Line(e.description);
            }
        }

        var vote = MallVoteManager.Instance;
        if (vote != null && vote.IsOpen)
        {
            Header("Mall vote");
            Row("Voting is open", Left(vote.SecondsLeft));
        }

        // Numbers.
        Header("The mall");
        var cm = CustomerManager.Instance;
        if (cm != null) Row("Shoppers", cm.Count.ToString());
        int players = 0;
        if (nm.SpawnManager != null) foreach (var o in nm.SpawnManager.SpawnedObjectsList) if (o.IsPlayerObject) players++;
        Row("Players", players.ToString());
        var mall = MallManager.Instance;
        if (mall != null)
        {
            int total = 0, owned = 0, open = 0, ours = 0;
            foreach (var plot in mall.Plots)
            {
                if (plot == null) continue;
                total++;
                if (plot.HasOwner) owned++;
                if (plot.IsOpen) open++;
                if (plot.HasOwner && plot.OwnerTeamId == team) ours++;
            }
            Row("Stores open", $"{open} of {total}");
            Row("Stores taken", $"{owned} of {total}");
            Row("Your stores", ours.ToString());
        }
        Finish();
    }

    private static string Left(float seconds)
    {
        int s = Mathf.Max(0, Mathf.CeilToInt(seconds));
        return s >= 3600 ? $"{s / 3600}h {s % 3600 / 60}m left" : s >= 60 ? $"{s / 60}m {s % 60:00}s left" : $"{s}s left";
    }

    private void Header(string text)
    {
        if (y > style.padding) y += style.spacing;
        var t = PhoneUI.Text(content, $"<b>{text}</b>", y, style.rowHeight * 0.8f, style, true, TextAlignmentOptions.BottomLeft);
        t.color = PhoneTheme.AccentOf(style);
        y += style.rowHeight * 0.8f + style.spacing * 0.5f;
    }

    private void Row(string name, string value)
    {
        PhoneUI.Text(content, name, y, style.rowHeight, style, false, TextAlignmentOptions.Left);
        var t = PhoneUI.Text(content, value, y, style.rowHeight, style, false, TextAlignmentOptions.Right);
        t.color = PhoneTheme.DimOf(style);
        y += style.rowHeight + style.spacing * 0.5f;
    }

    private void Line(string text)
    {
        var t = PhoneUI.Text(content, text, y, style.rowHeight, style, true, TextAlignmentOptions.TopLeft);
        t.ForceMeshUpdate();
        float h = Mathf.Max(style.rowHeight * 0.8f, t.preferredHeight + 2f);
        var rt = (RectTransform)t.transform;
        rt.sizeDelta = new Vector2(rt.sizeDelta.x, h);
        y += h + style.spacing * 0.5f;
    }

    private void Finish() => content.sizeDelta = new Vector2(content.sizeDelta.x, y + style.padding);
}
