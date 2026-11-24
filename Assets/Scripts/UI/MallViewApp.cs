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
/// for this). To design it yourself, lay out the app's panel with texts and put a MallViewField on each
/// (what it shows): with any of those under it, the app fills them in instead of building its own page.
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

    private MallViewField[] designed;

    public void OnAppOpened()
    {
        designed ??= GetComponentsInChildren<MallViewField>(true);
        if (designed.Length == 0 && content == null) content = PhoneUI.ScrollPage((RectTransform)transform);
        Build();
    }

    public void OnAppClosed() { }

    private void Update()
    {
        if ((content == null && (designed == null || designed.Length == 0)) || Time.unscaledTime < nextRefresh) return;
        Build();
    }

    private void Build()
    {
        if (designed != null && designed.Length > 0) { FillDesigned(); return; }
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

    /* ========================= DESIGNED (MallViewField) ========================= */

    private void FillDesigned()
    {
        nextRefresh = Time.unscaledTime + refreshEvery;
        foreach (var f in designed) if (f != null) f.Show(Value(f.field));
    }

    // One field's value as text ("" when there's nothing: no event on, no vote open...).
    private string Value(MallViewField.Field field)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening) return "";
        ulong me = nm.LocalClientId;
        ulong team = MallManager.GetTeamOf(me);
        var hp = HelpPoints.Instance;
        var em = MallEventManager.Instance;
        var season = em != null && em.IsSpawned ? em.CurrentSeason : null;
        switch (field)
        {
            case MallViewField.Field.HelpPoints: return hp != null && hp.IsSpawned ? hp.GetPoints(me).ToString() : "";
            case MallViewField.Field.VoteWeight: return hp != null && hp.IsSpawned ? $"×{Mathf.Max(1, hp.VoteWeightOf(me))}" : "";
            case MallViewField.Field.TeamLevel: return ProgressionManager.LevelOf(team).ToString();
            case MallViewField.Field.Money: return EconomyManager.Instance != null ? $"${EconomyManager.Instance.LocalMoney:N0}" : "";
            case MallViewField.Field.SeasonName: return season != null ? season.displayName : "";
            case MallViewField.Field.SeasonDescription: return season != null ? season.description : "";
            case MallViewField.Field.SeasonTimeLeft: return season != null ? Left(em.SecondsLeft(season)) : "";
            case MallViewField.Field.EventNames:
            case MallViewField.Field.EventsList:
            {
                if (em == null || !em.IsSpawned) return "";
                events.Clear();
                em.GetActive(events);
                events.Remove(season);
                var sb = new System.Text.StringBuilder();
                foreach (var e in events)
                {
                    if (sb.Length > 0) sb.Append(field == MallViewField.Field.EventNames ? ", " : "\n\n");
                    if (field == MallViewField.Field.EventNames) { sb.Append(e.displayName); continue; }
                    sb.Append($"<b>{e.displayName}</b>  ({Left(em.SecondsLeft(e))})");
                    if (!string.IsNullOrEmpty(e.description)) sb.Append('\n').Append(e.description);
                }
                return sb.ToString();
            }
            case MallViewField.Field.VoteTimeLeft:
            {
                var vote = MallVoteManager.Instance;
                return vote != null && vote.IsOpen ? Left(vote.SecondsLeft) : "";
            }
            case MallViewField.Field.Shoppers: return CustomerManager.Instance != null ? CustomerManager.Instance.Count.ToString() : "";
            case MallViewField.Field.Players:
            {
                int players = 0;
                if (nm.SpawnManager != null) foreach (var o in nm.SpawnManager.SpawnedObjectsList) if (o.IsPlayerObject) players++;
                return players.ToString();
            }
        }
        // Stores.
        var mall = MallManager.Instance;
        if (mall == null) return "";
        int total = 0, owned = 0, open = 0, ours = 0;
        foreach (var plot in mall.Plots)
        {
            if (plot == null) continue;
            total++;
            if (plot.HasOwner) owned++;
            if (plot.IsOpen) open++;
            if (plot.HasOwner && plot.OwnerTeamId == team) ours++;
        }
        return field switch
        {
            MallViewField.Field.StoresOpen => open.ToString(),
            MallViewField.Field.StoresTaken => owned.ToString(),
            MallViewField.Field.StoresTotal => total.ToString(),
            MallViewField.Field.YourStores => ours.ToString(),
            _ => "",
        };
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
