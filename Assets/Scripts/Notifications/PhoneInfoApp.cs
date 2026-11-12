using TMPro;
using UnityEngine;

/// <summary>
/// A simple phone app that just shows a message: for apps that aren't made yet (Settings, Save...)
/// so their icons can be on the home screen now. Put it on a panel under the phone's canvas.
/// </summary>
public class PhoneInfoApp : MonoBehaviour, IPhoneApp
{
    [SerializeField] private string appName = "Settings";
    [SerializeField] private Sprite icon;
    [SerializeField] private TMP_Text messageText;
    [TextArea] [SerializeField] private string message = "Coming soon.";

    public string AppName => appName;
    public Sprite Icon => icon;
    public int Badge => 0;

    public void OnAppOpened()
    {
        if (messageText != null) messageText.text = message;
    }

    public void OnAppClosed() { }
}
