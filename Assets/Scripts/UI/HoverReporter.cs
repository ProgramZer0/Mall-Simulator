using System;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>Tells code when the mouse goes onto or off a UI element (added by code, e.g. market cards).</summary>
public class HoverReporter : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public event Action<bool> Hovered;

    public void OnPointerEnter(PointerEventData eventData) => Hovered?.Invoke(true);
    public void OnPointerExit(PointerEventData eventData) => Hovered?.Invoke(false);
    private void OnDisable() => Hovered?.Invoke(false);
}
