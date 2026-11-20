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
    [Tooltip("Play Level Up (SoundCues) when the level goes up. Only on one level display, or they double up.")]
    [SerializeField] private bool playSounds = true;

    private int shownLevel = -1, shownXp = -1;
    private bool visible = true;
    private bool applied; // shown or hidden at least once (the Root may start switched off in the scene)

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
        if (playSounds && shownLevel > 0 && level > shownLevel) Sfx.Play(Cue.LevelUp);
        shownLevel = level;
        shownXp = xp;

        int from = pm.XpForLevel(level), to = pm.XpForLevel(level + 1);
        bool max = level >= pm.MaxLevel || to <= from;
        if (text != null) text.text = string.Format(format, level, max ? 0 : xp - from, max ? 0 : to - from);
        if (progress != null) progress.fillAmount = max ? 1f : Mathf.Clamp01((xp - from) / (float)(to - from));
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
        else
        {
            if (text != null) text.enabled = on;
            if (progress != null) progress.enabled = on;
        }
    }
}
