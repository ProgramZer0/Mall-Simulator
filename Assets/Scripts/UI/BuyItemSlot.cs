using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Optional: put on each item tile in your buy page prefab to say which parts are which.
/// Without it, the buy menu uses each direct child of the page: its Image for the picture and
/// the Button inside it for buying.
/// </summary>
public class BuyItemSlot : MonoBehaviour
{
    public Image icon;
    public Button buyButton;
    [Tooltip("Optional.")] public TMP_Text nameText;
    [Tooltip("Optional.")] public TMP_Text priceText;
}
