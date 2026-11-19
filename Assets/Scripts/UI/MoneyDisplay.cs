using TMPro;
using UnityEngine;

/// <summary>
/// Shows your team's money in one TMP text. Put it on your always-on game HUD; add another anywhere
/// else you want the money shown (a menu, a big counter), no code needed.
///
/// It only rewrites the text when the amount changes, and hides while there's no game running (main
/// menu, before connecting).
///
/// Setup: Text (required), Root (optional: shown/hidden with the game; otherwise the text itself is).
/// Format: how it's written, {0} is the amount, e.g. "${0:N0}" gives $12,500.
/// </summary>
public class MoneyDisplay : MonoBehaviour
{
    [SerializeField] private TMP_Text text;
    [SerializeField] private GameObject root;
    [SerializeField] private string format = "${0:N0}";

    private int shown = int.MinValue;
    private bool visible = true;
    private bool applied; // shown or hidden at least once (the Root may start switched off in the scene)

    private void Awake()
    {
        if (text == null) text = GetComponent<TMP_Text>();
    }

    private void Update()
    {
        var econ = EconomyManager.Instance;
        bool inGame = econ != null && econ.IsSpawned;
        SetVisible(inGame);
        if (!inGame) { shown = int.MinValue; return; }

        // Cheap to read; the text (and TMP's mesh) only changes when the number does. Reading it here
        // also catches joining another team, which doesn't raise MoneyChanged.
        int money = econ.LocalMoney;
        if (money == shown || text == null) return;
        shown = money;
        text.text = string.Format(format, money);
    }

    private void SetVisible(bool on)
    {
        if (on == visible && applied) return;
        visible = on;
        applied = true;
        if (root != null)
        {
            // A Root this sits on (or under) can't be switched off: this would stop too, and never show it
            // again. Fade it out instead.
            if (transform.IsChildOf(root.transform))
            {
                if (!root.TryGetComponent<CanvasGroup>(out var group)) group = root.AddComponent<CanvasGroup>();
                group.alpha = on ? 1f : 0f;
            }
            else root.SetActive(on);
        }
        else if (text != null) text.enabled = on;
    }
}
