using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Shows your team's level (like MoneyDisplay shows money). Put it on the HUD or anywhere else.
///
/// Setup: Text (a TMP text). Optional: Progress (an Image set to Filled) for how far into the level,
/// Root (shown/hidden with the game). Format: {0} level, {1} XP into the level, {2} XP the level needs.
/// </summary>
public class LevelDisplay : MonoBehaviour
{
    [SerializeField] private TMP_Text text;
    [SerializeField] private Image progress;
    [SerializeField] private GameObject root;
    [SerializeField] private string format = "Lv {0}";

    private int shownLevel = -1, shownXp = -1;
    private bool visible = true;

    private void Awake()
    {
        if (text == null) text = GetComponent<TMP_Text>();
    }

    private void Update()
    {
        var pm = ProgressionManager.Instance;
        bool inGame = pm != null && pm.IsSpawned;
        SetVisible(inGame);
        if (!inGame) { shownLevel = -1; return; }

        ulong team = pm.LocalTeam;
        int level = pm.GetLevel(team), xp = pm.GetXp(team);
        if (level == shownLevel && xp == shownXp) return;
        shownLevel = level;
        shownXp = xp;

        int from = pm.XpForLevel(level), to = pm.XpForLevel(level + 1);
        bool max = level >= pm.MaxLevel || to <= from;
        if (text != null) text.text = string.Format(format, level, max ? 0 : xp - from, max ? 0 : to - from);
        if (progress != null) progress.fillAmount = max ? 1f : Mathf.Clamp01((xp - from) / (float)(to - from));
    }

    private void SetVisible(bool on)
    {
        if (on == visible) return;
        visible = on;
        if (root != null) root.SetActive(on);
        else
        {
            if (text != null) text.enabled = on;
            if (progress != null) progress.enabled = on;
        }
    }
}
