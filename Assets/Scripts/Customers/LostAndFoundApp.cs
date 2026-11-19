using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The phone's Lost & Found app: the lost children in the mall right now. Child shows a live picture of
/// the child and what's around them, Parents the same for the family looking for them. Several lost at
/// once: Previous / Next go through them. Its badge counts them.
///
/// Setup: a panel under the phone's canvas with this on it, and:
/// - Picture: a Raw Image (the live picture goes in it).
/// - Child Button, Parents Button: which one is shown. Optional Selected Color tints the shown one's button.
/// - Optional: Info Text ("Lost child 1 of 2, lost 3 min ago"), Previous / Next Buttons (hidden when
///   there's just one), Empty: shown when nobody's lost (e.g. a "No lost children" text). Picture and
///   the buttons are hidden then.
/// The pictures are taken on this machine only, while the app is open.
/// </summary>
public class LostAndFoundApp : MonoBehaviour, IPhoneApp
{
    [SerializeField] private string appName = "Lost & Found";
    [SerializeField] private Sprite icon;

    [SerializeField] private RawImage picture;
    [SerializeField] private Button childButton;
    [SerializeField] private Button parentsButton;
    [SerializeField] private Button previousButton;
    [SerializeField] private Button nextButton;
    [SerializeField] private TMP_Text infoText;
    [SerializeField] private GameObject empty;
    [Tooltip("The shown one's button (Child or Parents) is tinted this.")]
    [SerializeField] private Color selectedColor = new(1f, 0.85f, 0.4f);

    [SerializeField] private LostChildShotSettings shot = new();

    public string AppName => appName;
    public Sprite Icon => icon;
    public int Badge => CustomerManager.Instance != null ? CustomerManager.Instance.LostCases.Count : 0;

    private LostChildShot cam;
    private bool open, showParents;
    private int index;
    private ushort shownKid; // stays on the same child when the list changes
    private Color childNormal, parentsNormal;

    private void Awake()
    {
        if (childButton != null)
        {
            childButton.onClick.AddListener(() => { showParents = false; Refresh(); });
            if (childButton.targetGraphic != null) childNormal = childButton.targetGraphic.color;
        }
        if (parentsButton != null)
        {
            parentsButton.onClick.AddListener(() => { showParents = true; Refresh(); });
            if (parentsButton.targetGraphic != null) parentsNormal = parentsButton.targetGraphic.color;
        }
        if (previousButton != null) previousButton.onClick.AddListener(() => Step(-1));
        if (nextButton != null) nextButton.onClick.AddListener(() => Step(1));
    }

    public void OnAppOpened()
    {
        open = true;
        still.Clear();
        cam ??= shot.Make("Lost & Found Camera");
        if (picture != null) picture.texture = cam.Texture;
        CustomerManager.LostCasesChanged += Refresh;
        Refresh();
    }

    public void OnAppClosed()
    {
        if (!open) return;
        open = false;
        CustomerManager.LostCasesChanged -= Refresh;
        if (cam != null) cam.On = false;
    }

    private void OnDisable() => OnAppClosed();

    private void OnDestroy()
    {
        cam?.Dispose();
        cam = null;
    }

    private void Step(int by)
    {
        var list = Cases();
        if (list == null || list.Count == 0) return;
        index = (index + by + list.Count) % list.Count;
        shownKid = list[index].kid;
        showParents = false;
        Refresh();
    }

    private System.Collections.Generic.IReadOnlyList<CustomerManager.LostCase> Cases() =>
        CustomerManager.Instance != null ? CustomerManager.Instance.LostCases : null;

    private void Refresh()
    {
        if (!open) return;
        var list = Cases();
        int n = list != null ? list.Count : 0;
        // Same child as before if they're still lost, else the nearest place in the list.
        int same = -1;
        for (int i = 0; i < n; i++) if (list[i].kid == shownKid) { same = i; break; }
        index = same >= 0 ? same : Mathf.Clamp(index, 0, Mathf.Max(0, n - 1));
        if (n > 0) shownKid = list[index].kid;

        if (empty != null) empty.SetActive(n == 0);
        if (picture != null) picture.gameObject.SetActive(n > 0);
        if (childButton != null) childButton.gameObject.SetActive(n > 0);
        if (parentsButton != null) parentsButton.gameObject.SetActive(n > 0);
        if (previousButton != null) previousButton.gameObject.SetActive(n > 1);
        if (nextButton != null) nextButton.gameObject.SetActive(n > 1);
        if (childButton != null && childButton.targetGraphic != null) childButton.targetGraphic.color = showParents ? childNormal : selectedColor;
        if (parentsButton != null && parentsButton.targetGraphic != null) parentsButton.targetGraphic.color = showParents ? selectedColor : parentsNormal;
        if (n == 0)
        {
            if (cam != null) cam.On = false;
            if (infoText != null) infoText.text = "";
        }
        else UpdateInfo(list[index], n);
    }

    private void UpdateInfo(CustomerManager.LostCase lost, int n)
    {
        if (infoText == null) return;
        float minutes = (Time.time - lost.since) / 60f;
        string ago = minutes < 1f ? "just now" : $"{(int)minutes} min ago";
        string who = showParents ? "Their parents" : "Lost child";
        string of = n > 1 ? $" {index + 1} of {n}" : "";
        var cm = CustomerManager.Instance;
        string with = !showParents && cm != null && cm.MarkOf(lost.kid) == CustomerManager.LostMark.Following ? ", following someone" : "";
        infoText.text = $"{who}{(showParents ? "" : of)}: lost {ago}{with}";
    }

    private float nextInfo;
    private readonly LostChildShotSettings.Still still = new();
    private int stillOf = int.MinValue; // who the still is of (the kid's id; parents: below 0)

    private void LateUpdate()
    {
        if (!open || cam == null) return;
        var list = Cases();
        if (list == null || list.Count == 0 || index >= list.Count) { cam.On = false; return; }
        var lost = list[index];
        // The child: a still picture (they're standing where they got lost). The parents: live (they're
        // looking everywhere).
        int target = showParents ? -lost.family - 1 : lost.kid;
        if (target != stillOf) { still.Clear(); stillOf = target; }
        bool seen;
        if (showParents) { seen = shot.Aim(cam, lost.family); cam.On = seen; }
        else seen = shot.KeepStill(cam, lost.kid, still);
        if (picture != null) picture.color = seen ? Color.white : new Color(0.2f, 0.2f, 0.2f); // gone: a dark picture
        if (Time.unscaledTime >= nextInfo) { nextInfo = Time.unscaledTime + 1f; UpdateInfo(lost, list.Count); }
    }
}
