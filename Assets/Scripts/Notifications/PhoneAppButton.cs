using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>One app on the phone's home screen (see PhoneScreen). Every part but the button is optional.</summary>
public class PhoneAppButton : MonoBehaviour
{
    public Button button;
    [Tooltip("Shows the app's Icon (left as is if the app has none).")]
    public Image icon;
    public TMP_Text label;
    [Tooltip("A number (unread notifications...). Badge Root is hidden when it's 0.")]
    public TMP_Text badge;
    public GameObject badgeRoot;
}
