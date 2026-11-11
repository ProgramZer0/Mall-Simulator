using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>One perk on the Perks tab (see PerksScreen). Every part is optional except the button.</summary>
public class PerkRow : MonoBehaviour
{
    public Button buyButton;
    [Tooltip("The button's label: Buy (1 pt), Maxed, Needs ...")]
    public TMP_Text buttonText;
    public TMP_Text nameText;
    public TMP_Text descriptionText;
    public Image icon;
}
