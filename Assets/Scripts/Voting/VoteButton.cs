using TMPro;
using UnityEngine;

/// <summary>
/// One button on a VoteKiosk: look at it and press F to vote for that option of the open ballot.
/// Needs a collider on a layer the player's interact ray hits. Optional: a TextMeshPro label (the
/// option's name and votes) and a renderer that lights up on the option you voted for.
/// </summary>
public class VoteButton : MonoBehaviour
{
    [SerializeField] private TMP_Text label;
    [Tooltip("Lit (Your Vote Color) on the option you voted for.")]
    [SerializeField] private Renderer lamp;
    [SerializeField] private Color yourVoteColor = new(0.3f, 1f, 0.4f, 1f);
    [SerializeField] private Color otherColor = new(0.35f, 0.35f, 0.35f, 1f);

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private MaterialPropertyBlock block;

    public VoteKiosk Kiosk { get; private set; }
    public int Option { get; private set; }

    public void Setup(VoteKiosk kiosk, int option)
    {
        Kiosk = kiosk;
        Option = option;
    }

    public void Show(string name, int votes, bool mine)
    {
        if (label != null)
        {
            string text = $"{name}\n{votes}";
            if (label.text != text) label.text = text;
        }
        if (lamp != null)
        {
            block ??= new MaterialPropertyBlock();
            lamp.GetPropertyBlock(block);
            Color c = mine ? yourVoteColor : otherColor;
            block.SetColor(BaseColorId, c);
            block.SetColor(ColorId, c);
            lamp.SetPropertyBlock(block);
        }
    }

    /// <summary>What F would do here (for the interact prompt), or null.</summary>
    public string Prompt(ulong clientId)
    {
        var votes = MallVoteManager.Instance;
        if (votes == null || !votes.IsOpen) return "No mall vote right now";
        var option = votes.GetOption(Option);
        if (option == null) return null;
        return votes.VoteOf(clientId) == Option ? $"You voted for {option.displayName}" : $"F: Vote for {option.displayName}";
    }

    public void Press()
    {
        var votes = MallVoteManager.Instance;
        if (votes != null && votes.IsOpen && votes.GetOption(Option) != null) votes.Cast(Option);
    }
}
