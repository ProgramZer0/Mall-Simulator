using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>One franchise in the Licenses tab's franchise list (see FranchisesPanel). All optional but the button.</summary>
public class FranchiseRow : MonoBehaviour
{
    public Button bidButton;
    [Tooltip("\"Bid $2,100\" (Shift: a bigger raise), or why you can't.")]
    public TMP_Text buttonText;
    public Image icon;
    public TMP_Text nameText;
    [Tooltip("\"Held by your team: 24:10 left\" / \"Bidding: top $2,000 (Corner Store), 1:12 left\".")]
    public TMP_Text statusText;
    [Tooltip("Optional: shown while your team holds it.")]
    public GameObject heldMark;
    [Tooltip("Optional: shown while your team holds it; gives it up (after the pop-up).")]
    public Button giveUpButton;
}
