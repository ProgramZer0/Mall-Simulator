using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Optional: put on each item tile in your buy page prefab to say which parts are which.
/// Without it, the buy menu uses each direct child of the page: its Image for the picture and
/// the Button inside it for buying.
/// </summary>
public class BuyItemSlot : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public Image icon;
    public Button buyButton;
    [Tooltip("Optional.")] public TMP_Text nameText;
    [Tooltip("Optional.")] public TMP_Text priceText;
    [Tooltip("Optional: little swatches of the colors the item is limited to (its own, its franchise's or its " +
             "categories'), made here. Hidden when it comes in any color. Its height is the swatch size; a " +
             "Horizontal Layout Group lays them out, else they're placed in a row.")]
    public RectTransform colors;

    // The item shown on it, for the buy menu's tooltip (set by the menu).
    [System.NonSerialized] public ItemDef Def;
    [System.NonSerialized] public BuyMenu Menu;

    public void OnPointerEnter(PointerEventData eventData) { if (Menu != null) Menu.OnTileHover(this, true); }
    public void OnPointerExit(PointerEventData eventData) { if (Menu != null) Menu.OnTileHover(this, false); }
    private void OnDisable() { if (Menu != null) Menu.OnTileHover(this, false); }
}
