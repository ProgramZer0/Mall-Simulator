using UnityEngine;

/// <summary>
/// The phone's Quit app: quits to the desktop (tap twice). Put it on an app object under the phone
/// prefab's screen, like the Help app (duplicate that one and swap HelpApp for this). By default it's
/// only on the phone in the office (PhoneScreen's Office Only Apps).
/// </summary>
public class QuitApp : MonoBehaviour, IPhoneApp
{
    [SerializeField] private string appName = "Quit";
    [SerializeField] private Sprite icon;
    [SerializeField] private PhoneUI.Style style = new();

    public string AppName => appName;
    public Sprite Icon => icon;
    public int Badge => 0;

    private RectTransform content;
    private bool armed;

    public void OnAppOpened()
    {
        if (content == null) content = PhoneUI.ScrollPage((RectTransform)transform);
        armed = false;
        Build();
    }

    public void OnAppClosed() => armed = false;

    private void Build()
    {
        PhoneUI.Clear(content);
        float y = style.padding;
        var conn = MallConnection.Instance;
        bool hosting = conn != null && conn.CurrentMode == MallConnection.Mode.Hosting;
        string note = hosting ? "Quitting closes the lobby for everyone." : "See you next time.";
        PhoneUI.Text(content, note, y, style.rowHeight * 1.5f, style, true);
        y += style.rowHeight * 1.5f + style.spacing;
        PhoneUI.Button(content, armed ? "Tap again to quit" : "Quit to desktop", y, style.rowHeight, style, () =>
        {
            if (!armed) { armed = true; Build(); return; }
            MainMenu.Quit();
        });
        y += style.rowHeight + style.padding;
        content.sizeDelta = new Vector2(content.sizeDelta.x, y);
    }
}
