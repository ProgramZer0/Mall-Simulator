using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>One deal card on the Market tab (see MarketScreen). Every part but the button is optional.</summary>
public class MarketDealRow : MonoBehaviour
{
    public Button buyButton;
    public TMP_Text buttonText;
    public Image icon;
    public TMP_Text nameText;
    [Tooltip("\"$45  (was $60)\" or \"$120  Market only\".")]
    public TMP_Text priceText;
    [Tooltip("\"4 of 10 left\".")]
    public TMP_Text leftText;
    [Tooltip("\"Ends in 4:32\".")]
    public TMP_Text timeText;
    [Tooltip("Optional: shows the color it comes in (hidden for items in their own colors).")]
    public Image colorSwatch;
    [Tooltip("Optional: the color's name (\"Teal\"), empty for items in their own colors.")]
    public TMP_Text colorText;
}
