using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Clicks on &lt;link="id"&gt; parts of a TMP text: Clicked(id, button) for the link under the mouse. Added
/// by code (the buy menu's cart lines); the text needs Raycast Target on (it's turned on).
/// </summary>
[RequireComponent(typeof(TMP_Text))]
public class TextLinkClicks : MonoBehaviour, IPointerClickHandler
{
    public event Action<string, PointerEventData> Clicked;

    private TMP_Text text;

    private void Awake()
    {
        text = GetComponent<TMP_Text>();
        text.raycastTarget = true;
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        if (text == null) return;
        var cam = eventData.pressEventCamera != null ? eventData.pressEventCamera : eventData.enterEventCamera;
        int i = TMP_TextUtilities.FindIntersectingLink(text, eventData.position, cam);
        if (i < 0 || i >= text.textInfo.linkCount) return;
        Clicked?.Invoke(text.textInfo.linkInfo[i].GetLinkID(), eventData);
    }
}
