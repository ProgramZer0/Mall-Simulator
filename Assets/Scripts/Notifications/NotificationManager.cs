using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Sends notifications to the players they're for (one in the scene, with a NetworkObject).
///
/// Network use is kept tiny:
/// - What everyone can already see (mall events starting, ballots opening and closing, your level,
///   your stores' perk points) is noticed locally from the synced state: nothing extra is sent.
/// - What only the host knows (a delivery arrived, the dock is full) goes as a short code and two
///   numbers, only to that team's players; each machine writes the text itself.
/// - ServerNotifyTeamText sends text, for anything without a code yet.
/// Titles say everything on their own (the phone and toasts can show just the title); the body is
/// optional extra detail.
/// </summary>
public class NotificationManager : NetworkBehaviour
{
    public static NotificationManager Instance { get; private set; }

    /// <summary>What a coded notification says (see Describe). Add new ones at the end.</summary>
    public enum Kind : byte
    {
        Delivered,  // a = plot id, b = boxes
        DockFull,   // a = plot id (a delivery is waiting for room)
    }

    [Tooltip("Notify about mall events starting.")]
    [SerializeField] private bool mallEvents = true;
    [Tooltip("Notify when a mall vote opens and who won.")]
    [SerializeField] private bool ballots = true;
    [Tooltip("Notify on level ups and new perk points.")]
    [SerializeField] private bool progression = true;

    // Locally noticed state.
    private readonly List<MallEventDef> activeNow = new();
    private readonly HashSet<MallEventDef> activeSeen = new();
    private bool ballotWasOpen;
    private int levelSeen = -1;
    private readonly Dictionary<int, int> pointsSeen = new();
    private bool watching;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[NotificationManager] More than one in the scene; using the first.", this);
            return;
        }
        Instance = this;
    }

    public override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        Unwatch();
        base.OnDestroy();
    }

    public override void OnNetworkSpawn() => Watch();
    public override void OnNetworkDespawn() => Unwatch();

    /* ========================= FROM THE HOST ========================= */

    /// <summary>Host: a coded notification for every player on a team.</summary>
    public void ServerNotifyTeam(ulong team, Kind kind, int a = 0, int b = 0)
    {
        if (!IsServer || !TryGetTeamTarget(team, out var target)) return;
        NoteRpc(kind, a, b, target);
    }

    /// <summary>Host: a written notification for every player on a team (for things without a Kind yet).</summary>
    public void ServerNotifyTeamText(ulong team, string title, string body = "", bool alarm = false)
    {
        if (!IsServer || !TryGetTeamTarget(team, out var target)) return;
        TextRpc(new FixedString64Bytes(Trim(title, 60)), new FixedString128Bytes(Trim(body, 124)), alarm, target);
    }

    private static string Trim(string s, int max) => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s.Substring(0, max);

    private bool TryGetTeamTarget(ulong team, out RpcParams target)
    {
        target = default;
        var mall = MallManager.Instance;
        if (mall == null) return false;
        var members = mall.GetTeamMembers(team);
        if (members == null || members.Count == 0) return false;
        target = RpcTarget.Group(members, RpcTargetUse.Temp);
        return true;
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void NoteRpc(Kind kind, int a, int b, RpcParams rpcParams)
    {
        Describe(kind, a, b, out string title, out string body, out bool alarm);
        Notifications.Post(title, body, alarm);
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void TextRpc(FixedString64Bytes title, FixedString128Bytes body, bool alarm, RpcParams rpcParams) =>
        Notifications.Post(title.ToString(), body.ToString(), alarm);

    private static void Describe(Kind kind, int a, int b, out string title, out string body, out bool alarm)
    {
        string store = MallManager.Instance != null && MallManager.Instance.TryGetPlot(a, out var plot) ? plot.DisplayName : "your store";
        alarm = false;
        switch (kind)
        {
            case Kind.Delivered:
                title = $"{b} box{(b == 1 ? "" : "es")} at the dock for {store}";
                body = $"{b} box{(b == 1 ? "" : "es")} for {store} at the loading dock.";
                break;
            case Kind.DockFull:
                title = $"Dock full: {store}'s delivery is waiting";
                body = $"No room at the loading dock for {store}'s delivery. Clear some space.";
                alarm = true;
                break;
            default:
                title = "Notification";
                body = "";
                break;
        }
    }

    /* ========================= NOTICED LOCALLY ========================= */

    private void Watch()
    {
        if (watching) return;
        watching = true;
        MallEventManager.Changed += OnEventsChanged;
        MallVoteManager.Changed += OnBallotChanged;
        if (ProgressionManager.Instance != null) ProgressionManager.Instance.Changed += OnProgressChanged;
        if (LicenseManager.Instance != null) LicenseManager.Instance.Changed += OnLicensesChanged;

        // What's already going on when we join isn't news.
        SnapshotEvents(post: false);
        ballotWasOpen = MallVoteManager.Instance != null && MallVoteManager.Instance.IsOpen;
        levelSeen = -1;
        pointsSeen.Clear();
        CheckProgress(post: false);
        licensesSeen.Clear();
        CheckLicenses(post: false);
    }

    private void Unwatch()
    {
        if (!watching) return;
        watching = false;
        MallEventManager.Changed -= OnEventsChanged;
        MallVoteManager.Changed -= OnBallotChanged;
        if (ProgressionManager.Instance != null) ProgressionManager.Instance.Changed -= OnProgressChanged;
        if (LicenseManager.Instance != null) LicenseManager.Instance.Changed -= OnLicensesChanged;
    }

    private void OnEventsChanged() => SnapshotEvents(post: mallEvents);

    private void SnapshotEvents(bool post)
    {
        var events = MallEventManager.Instance;
        if (events == null) return;
        events.GetActive(activeNow);
        foreach (var def in activeNow)
            if (activeSeen.Add(def) && post)
                Notifications.Post($"Mall event: {def.displayName}", string.IsNullOrEmpty(def.description) ? "A mall event has started." : def.description);
        activeSeen.RemoveWhere(d => !activeNow.Contains(d));
    }

    private void OnBallotChanged()
    {
        var vote = MallVoteManager.Instance;
        if (vote == null) return;
        bool open = vote.IsOpen;
        if (open == ballotWasOpen) return;
        ballotWasOpen = open;
        if (!ballots) return;
        if (open)
            Notifications.Post($"Mall vote open ({vote.SecondsLeft / 60f:0.#} min)", $"Vote at a kiosk: {vote.SecondsLeft / 60f:0.#} min left.");
        else
        {
            var winner = vote.LastWinner;
            Notifications.Post(winner != null ? $"Vote won: {winner.displayName}" : "Mall vote closed: no winner", winner != null ? $"{winner.displayName} won." : "Nothing won this time.");
        }
    }

    private void OnProgressChanged() => CheckProgress(post: progression);

    // Your team's licenses: bought (by anyone on the team) or sold.
    private readonly HashSet<int> licensesSeen = new();
    private readonly HashSet<int> licensesNow = new();

    private void OnLicensesChanged() => CheckLicenses(post: true);

    private void CheckLicenses(bool post)
    {
        var lm = LicenseManager.Instance;
        if (lm == null || !lm.IsSpawned || NetworkManager == null) return;
        ulong team = MallManager.GetTeamOf(NetworkManager.LocalClientId);
        licensesNow.Clear();
        foreach (var l in lm.Licenses) if (l != null && lm.Holds(team, l.id)) licensesNow.Add(l.id);
        if (post)
            foreach (var l in lm.Licenses)
            {
                if (l == null) continue;
                bool now = licensesNow.Contains(l.id), before = licensesSeen.Contains(l.id);
                if (now && !before) Notifications.Post($"License bought: {l.displayName}");
                else if (!now && before) Notifications.Post($"License sold: {l.displayName}");
            }
        licensesSeen.Clear();
        licensesSeen.UnionWith(licensesNow);
    }

    private void CheckProgress(bool post)
    {
        var pm = ProgressionManager.Instance;
        if (pm == null || !pm.IsSpawned || NetworkManager == null) return;
        ulong team = pm.LocalTeam;
        bool first = levelSeen < 0; // nothing to compare with yet

        int level = pm.GetLevel(team);
        if (levelSeen >= 0 && level > levelSeen && post)
        {
            int stores = ProgressionManager.StoresAllowed(team);
            int storesBefore = StoresAt(levelSeen);
            Notifications.Post(stores > storesBefore && stores < int.MaxValue ? $"Level {level}! You can own {stores} stores" : $"Level {level}! New items may be unlocked", stores > storesBefore && stores < int.MaxValue
                ? $"Your team can now own {stores} stores. New items may be unlocked in the shop."
                : "New items may be unlocked in the shop.");
        }
        levelSeen = level;

        if (MallManager.Instance == null) return;
        foreach (var plot in MallManager.Instance.Plots)
        {
            if (plot == null || !plot.HasOwner || plot.OwnerTeamId != team) continue;
            int points = pm.GetPointsEarned(plot.PlotId);
            int had = pointsSeen.TryGetValue(plot.PlotId, out int h) ? h : first ? points : 0;
            if (points > had && post)
                Notifications.Post($"{plot.DisplayName}: perk point earned", $"{plot.DisplayName} has {pm.GetPointsAvailable(plot.PlotId)} to spend on the shop monitor's Perks tab.");
            pointsSeen[plot.PlotId] = points;
        }
    }

    // How many stores a level allowed (for "you can now own N stores").
    private static int StoresAt(int level)
    {
        int n = 0;
        for (int count = 1; count < 100; count++)
        {
            int need = ProgressionManager.LevelForStores(count);
            if (need <= 0 || need > level) break;
            n = count;
        }
        return n;
    }
}
