using TMPro;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// A voting kiosk in the mall: one physical button per ballot option (VoteButton, F to vote) and a
/// sign showing the ballot, the votes so far and the time left. Any number of kiosks show the same
/// ballot (MallVoteManager).
///
/// Setup: a scene object (no NetworkObject needed) with this. Children: a VoteButton per option
/// (Option 0, 1, 2...; each with a collider on a layer the player's interact ray hits), and a
/// TextMeshPro text for the sign. Buttons beyond the ballot's options are hidden.
/// </summary>
public class VoteKiosk : MonoBehaviour
{
    [Tooltip("The buttons, in option order (if empty, the VoteButtons under this kiosk).")]
    [SerializeField] private VoteButton[] buttons;
    [Tooltip("The sign: the ballot, votes and time left (TextMeshPro, 3D or world-space UI).")]
    [SerializeField] private TMP_Text sign;
    [Tooltip("Hide buttons with no option on the ballot (otherwise they just say so).")]
    [SerializeField] private bool hideUnusedButtons = true;

    private float nextRefresh;

    private void Awake()
    {
        if (buttons == null || buttons.Length == 0) buttons = GetComponentsInChildren<VoteButton>(true);
        for (int i = 0; i < buttons.Length; i++) if (buttons[i] != null) buttons[i].Setup(this, i);
    }

    private void OnEnable() => MallVoteManager.Changed += Refresh;
    private void OnDisable() => MallVoteManager.Changed -= Refresh;

    // The time left ticks down, so the sign is redrawn every second too.
    private void Update()
    {
        if (Time.unscaledTime < nextRefresh) return;
        nextRefresh = Time.unscaledTime + 1f;
        Refresh();
    }

    private void Refresh()
    {
        var votes = MallVoteManager.Instance;
        bool open = votes != null && votes.IsOpen;
        ulong me = NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId : ulong.MaxValue;
        int mine = open ? votes.VoteOf(me) : -1;

        for (int i = 0; i < buttons.Length; i++)
        {
            var b = buttons[i];
            if (b == null) continue;
            var option = open ? votes.GetOption(i) : null;
            if (hideUnusedButtons) b.gameObject.SetActive(option != null);
            b.Show(option != null ? option.displayName : "-", open ? votes.GetTally(i) : 0, i == mine);
        }

        if (sign == null) return;
        string text;
        if (votes == null) text = "MALL VOTE\nNot set up";
        else if (!open)
        {
            var last = votes.LastWinner;
            text = "MALL VOTE\nNo vote right now" + (last != null ? $"\nLast vote: {last.displayName}" : "");
        }
        else
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"MALL VOTE   {Mathf.CeilToInt(votes.SecondsLeft)}s left");
            for (int i = 0; i < votes.OptionCount; i++)
            {
                var o = votes.GetOption(i);
                if (o == null) continue;
                sb.Append($"\n{i + 1}. {o.displayName}: {votes.GetTally(i)}{(i == mine ? "  (your vote)" : "")}");
            }
            text = sb.ToString();
        }
        if (sign.text != text) sign.text = text;
    }
}
