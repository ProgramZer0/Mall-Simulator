using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// One license on the shop monitor's Licenses tab (see LicensesScreen): its picture as a button.
/// Hover for details; click to buy (or, if it's yours, click twice to sell). Only the button is required.
/// </summary>
public class LicenseCard : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public Button button;
    public Image icon;
    [Tooltip("Optional: the license's name.")]
    public TMP_Text nameText;
    [Tooltip("Optional: how many are left in the mall (\"2 left\").")]
    public TMP_Text countText;
    [Tooltip("Optional: shown when your team holds it.")]
    public GameObject ownedMark;
    [Tooltip("Optional: shown on cards that are a franchise your team holds (instead of a license).")]
    public GameObject franchiseMark;
    [Tooltip("Optional: tinted with the screen's Franchise Tint on franchise cards (e.g. the card's background).")]
    public Graphic tintTarget;

    public event Action<LicenseCard, bool> Hovered;

    public void OnPointerEnter(PointerEventData eventData) => Hovered?.Invoke(this, true);
    public void OnPointerExit(PointerEventData eventData) => Hovered?.Invoke(this, false);
    private void OnDisable() => Hovered?.Invoke(this, false);
}
