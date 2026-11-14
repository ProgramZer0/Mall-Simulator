using UnityEngine;

/// <summary>
/// The phone's Franchises app: bid on franchises on the go. Unlocks at a team level (greyed out on the
/// home screen until then). Put it on a panel under the phone's canvas, with a FranchisesPanel on the
/// same panel (or under it) for the list. Its badge counts auctions open right now.
/// </summary>
public class FranchisesApp : MonoBehaviour, IPhoneApp
{
    [SerializeField] private string appName = "Franchises";
    [SerializeField] private Sprite icon;
    [Tooltip("Team level the app unlocks at.")]
    [SerializeField, Min(0)] private int unlockLevel = 15;

    public string AppName => appName;
    public Sprite Icon => icon;

    public int Badge
    {
        get
        {
            var fm = FranchiseManager.Instance;
            if (fm == null || !fm.IsSpawned) return 0;
            int n = 0;
            foreach (var f in fm.Franchises)
                if (f != null && fm.TryGetState(f.id, out var s) && s.bidding) n++;
            return n;
        }
    }

    public string LockedReason
    {
        get
        {
            var nm = Unity.Netcode.NetworkManager.Singleton;
            ulong team = nm != null ? MallManager.GetTeamOf(nm.LocalClientId) : 0;
            return ProgressionManager.LevelOf(team) >= unlockLevel ? null : $"Level {unlockLevel}";
        }
    }

    // The FranchisesPanel refreshes itself while shown.
    public void OnAppOpened() { }
    public void OnAppClosed() { }
}
