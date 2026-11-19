using TMPro;
using UnityEngine;

/// <summary>
/// A note on the wall: F opens its topic in the phone's Help app. Topic Id: a Help topic's Id (or its
/// title). Optional Text: a TMP text on the note, filled with Note Text (or the topic's title if empty).
/// Needs a collider on it (or a child).
/// </summary>
public class HelpNote : MonoBehaviour, IPlayerUsable
{
    [Tooltip("The Help topic it opens: its Id (controls, phone, your-store, stocking, prices...). A topic with no Id goes by its title.")]
    [SerializeField] private string topicId = "controls";
    [Tooltip("Optional: the text on the note.")]
    [SerializeField] private TMP_Text text;
    [Tooltip("What the note says (empty: the topic's title).")]
    [TextArea(2, 6)]
    [SerializeField] private string noteText;
    [Tooltip("The Help app's App Name on the phone.")]
    [SerializeField] private string helpApp = "Help";

    private void Start()
    {
        if (text != null) text.text = !string.IsNullOrEmpty(noteText) ? noteText : HelpApp.TitleOf(topicId) ?? topicId;
    }

    public string Prompt(ulong clientId)
    {
        string title = HelpApp.TitleOf(topicId);
        return title != null ? $"F: Read \"{title}\"" : null;
    }

    public void Use(ulong clientId)
    {
        var nm = Unity.Netcode.NetworkManager.Singleton;
        var player = nm != null ? nm.LocalClient?.PlayerObject : null;
        var phone = player != null ? player.GetComponentInChildren<PhoneController>(true) : null;
        if (phone == null) return;
        HelpTips.SetPending(topicId.Trim(), 5f); // the Help app opens straight on it
        phone.Open(helpApp);
    }
}
