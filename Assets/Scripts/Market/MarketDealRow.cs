using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>One deal on the Market tab (see MarketScreen). Every part but the button is optional.</summary>
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
}
