using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// One perk on the Perks tab (see PerksScreen): its picture as a button. Click to buy; hover to see
/// its name and description (and why it can't be bought). Greyed out when it can't be bought.
/// Button is required; Icon shows the perk's Icon; Rank Text (optional) shows "2/3".
/// </summary>
public class PerkRow : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public Button buyButton;
    public Image icon;
    [Tooltip("Optional: \"2/3\" for perks with more than one rank, empty otherwise.")]
    public TMP_Text rankText;
    [Tooltip("Optional: shown once it's maxed (a check mark).")]
    public GameObject maxedMark;

    [Header("Old row layout (unused)")]
    public TMP_Text buttonText;
    public TMP_Text nameText;
    public TMP_Text descriptionText;

    /// <summary>The pointer went over (true) or off (false) this perk.</summary>
    public event Action<PerkRow, bool> Hovered;

    public void OnPointerEnter(PointerEventData eventData) => Hovered?.Invoke(this, true);
    public void OnPointerExit(PointerEventData eventData) => Hovered?.Invoke(this, false);

    private void OnDisable() => Hovered?.Invoke(this, false);
}
