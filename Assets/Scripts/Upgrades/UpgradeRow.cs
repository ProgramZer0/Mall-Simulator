using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>One row on the Upgrades tab (see StoreUpgradesScreen). Every part but the button is optional.</summary>
public class UpgradeRow : MonoBehaviour
{
    public Button button;
    public TMP_Text buttonText;
    public TMP_Text nameText;
    public TMP_Text descriptionText;
    public TMP_Text priceText;
    public Image icon;
}
