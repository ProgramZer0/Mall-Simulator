using TMPro;
using UnityEngine;

/// <summary>
/// A phone app (see PhoneScreen). Put a component that implements it on each app's screen inside the
/// phone prefab's canvas; the home screen gets a button for each.
/// </summary>
public interface IPhoneApp
{
    string AppName { get; }
    /// <summary>Its home screen icon (null: the button's own picture).</summary>
    Sprite Icon { get; }
    /// <summary>A number on its home screen button (unread notifications...), 0 for none.</summary>
    int Badge { get; }
    /// <summary>The app is shown (its GameObject has just been turned on).</summary>
    void OnAppOpened();
    /// <summary>The app is hidden (or the phone put away).</summary>
    void OnAppClosed();
    /// <summary>Why it can't be opened yet ("Level 15"), or null when it can. Locked apps show greyed out.</summary>
    string LockedReason => null;
}

/// <summary>
/// The player's phone, held up in front of the camera with its UI on the phone itself (like the shop
/// on the monitor), so the screen always fits the phone. Purely local.
///
/// Keys (Esc and Tab both):
/// - Bring the phone up where it was left (the app it was on, or home), unless something new came in
///   while it was down. Then, first match: a first-time Help tip that's still up opens Help; a child
///   lost since (and still lost) opens Lost & Found; a new notification opens Notifications.
/// - Again: put it away (Esc first goes back to home if Esc Backs Out To Home is on). The phone's Back
///   button goes home.
/// Esc that something else used first (closing the shop, cancelling a drag, the scanner...) doesn't
/// bring it up (see UIState.TakeEscape).
/// While it's up the cursor is free and the player stands still, like other menus.
///
/// Setup: put this on any always-on object (e.g. the HUD canvas), or on the player prefab: there, only
/// the local player's copy does anything (other players' copies on this machine stay out of it). Phone Prefab: the phone model with a
/// PhoneScreen (and its world-space canvas) on it. It's made once, under the camera, at Hold Offset.
/// Optional Hint Text on the HUD: "Tab: Phone (3)" while there are unread notifications.
/// </summary>
public class PhoneController : MonoBehaviour
{
    public static bool IsOpen { get; private set; }

    [SerializeField] private PhoneScreen phonePrefab;
    [Tooltip("Where it's held, from the camera (right, up, forward).")]
    [SerializeField] private Vector3 holdOffset = new(0f, -0.02f, 0.32f);
    [SerializeField] private Vector3 holdRotation = new(0f, 180f, 0f);
    [Tooltip("Comes up from this far below the held spot.")]
    [SerializeField, Min(0f)] private float raiseFrom = 0.25f;
    [SerializeField, Min(0.01f)] private float raiseSeconds = 0.15f;
    [Tooltip("The field of view Hold Offset is set for. With a wider one (Settings → Field of view) the phone " +
             "is held that much closer, so it looks the same size and stays readable; narrower, further. 0: off.")]
    [SerializeField, Min(0f)] private float holdOffsetFov = 60f;

    [SerializeField] private KeyCode homeKey = KeyCode.Escape;
    [SerializeField] private KeyCode notificationsKey = KeyCode.Tab;
    [Tooltip("The app opened when a new notification came in while the phone was down (its App Name).")]
    [SerializeField] private string notificationsApp = "Notifications";
    [Tooltip("The app opened when a child got lost while the phone was down (its App Name).")]
    [SerializeField] private string lostAndFoundApp = "Lost & Found";
    [Tooltip("Off: Esc with the phone up puts it away, like Tab, so it comes back on the same app. On: Esc goes back to home first, then puts it away.")]
    [SerializeField] private bool escBacksOutToHome;

    [Tooltip("Optional HUD text: {0} = unread count. Empty when nothing's unread.")]
    [SerializeField] private TMP_Text hintText;
    [SerializeField] private string hintFormat = "Tab: Phone ({0})";

    private PhoneScreen phone;
    private Unity.Netcode.NetworkObject owner; // on a player prefab: that player
    private bool ownerChecked;
    private float raisedAt;
    private int closedFrame = -1; // the key that put it away mustn't bring it straight back up
    private string lastApp; // where it was put away (null: home)
    private bool newNote, newLost; // came in while it was down

    // Reset once per play (not per copy: another player's copy appearing mustn't mark ours shut).
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetOnPlay() => IsOpen = false;

    // On a player prefab every player has a copy on every machine; only the local player's may act
    // (otherwise one copy closes, and loses track of, the phone the other opened).
    private bool IsMine
    {
        get
        {
            if (!ownerChecked)
            {
                owner = GetComponentInParent<Unity.Netcode.NetworkObject>();
                ownerChecked = true;
            }
            return owner == null || (owner.IsSpawned && owner.IsOwner);
        }
    }

    private void OnEnable()
    {
        Notifications.Changed += RefreshHint;
        Notifications.Posted += OnPosted;
        CustomerManager.LostCaseOpened += OnLostCase;
        ownerChecked = false;
        // First-time tips need the Help topics before the phone's ever been brought up.
        if (HelpApp.Prefab == null && phonePrefab != null) HelpApp.Prefab = phonePrefab.GetComponentInChildren<HelpApp>(true);
        RefreshHint();
    }

    private void OnDisable()
    {
        Notifications.Changed -= RefreshHint;
        Notifications.Posted -= OnPosted;
        CustomerManager.LostCaseOpened -= OnLostCase;
        if (IsOpen && phone != null) Close();
    }

    // Open keys are read after everything else (LateUpdate), so an Esc something else used this
    // frame doesn't also bring the phone up.
    private void Update()
    {
        if (!IsMine || phone == null || !IsOpen) return;
        if (Input.GetKeyDown(notificationsKey)) { Close(); return; }
        if (Input.GetKeyDown(homeKey))
        {
            if (homeKey == KeyCode.Escape) UIState.MarkEscapeTaken();
            if (escBacksOutToHome && phone != null && !phone.OnHome) phone.ShowHome();
            else Close();
        }
        if (phone != null) Raise();
    }

    private void LateUpdate()
    {
        if (!IsMine || IsOpen || closedFrame == Time.frameCount || UIState.IsMenuOpen || !InGame()) return;
        if (Input.GetKeyDown(notificationsKey) ||
            (Input.GetKeyDown(homeKey) && !(homeKey == KeyCode.Escape && UIState.EscapeTakenThisFrame)))
            Open(ResumeApp());
    }

    private void OnPosted(Note note) { if (!IsOpen) newNote = true; }
    private void OnLostCase(CustomerManager.LostCase lost) { if (!IsOpen) newLost = true; }

    // Where the keys bring it up: something new that came in while it was down, else where it was left.
    // (A first-time Help tip is handled in Open.)
    private string ResumeApp()
    {
        if (newLost)
        {
            var cm = CustomerManager.Instance;
            if (cm != null && cm.LostCases.Count > 0) return lostAndFoundApp;
        }
        if (newNote) return notificationsApp;
        return lastApp;
    }

    // Playing (not in store edit mode: its own camera is on and the player's, which holds the phone, is
    // off, so the phone would come up unseen and freeze the edit controls until put away).
    private bool InGame()
    {
        var nm = Unity.Netcode.NetworkManager.Singleton;
        if (nm == null || !nm.IsListening || nm.LocalClient?.PlayerObject == null) return false;
        if (phone != null) return phone.transform.parent != null && phone.transform.parent.TryGetComponent(out Camera held) && held.isActiveAndEnabled;
        var cam = Camera.main;
        return cam != null && cam.isActiveAndEnabled;
    }

    /// <summary>Bring the phone up on an app (by App Name), or its home screen (null).</summary>
    public void Open(string app)
    {
        if (IsOpen || !EnsurePhone()) return;
        IsOpen = true;
        // A first-time tip is up: straight to its Help topic.
        if (HelpTips.HasPending) app = HelpApp.Prefab != null ? HelpApp.Prefab.AppName : "Help";
        if (homeKey == KeyCode.Escape) UIState.MarkEscapeTaken();
        phone.gameObject.SetActive(true);
        raisedAt = Time.unscaledTime;
        Raise();
        UIState.OpenMenu(this);
        newNote = newLost = false;
        if (app == null || !phone.ShowApp(app)) phone.ShowHome();
        Sfx.Play(Cue.PhoneUp);
        Sfx.ForAll(Cue.None, transform.position, AnimCue.UsingPhone, Sfx.Me, true);
    }

    public void Close()
    {
        if (!IsOpen || phone == null) return; // only the copy that opened it closes it
        IsOpen = false;
        closedFrame = Time.frameCount;
        if (phone != null)
        {
            lastApp = phone.CurrentApp;
            phone.CloseApps();
            phone.gameObject.SetActive(false);
        }
        UIState.CloseMenu(this);
        Sfx.Play(Cue.PhoneDown);
        Sfx.ForAll(Cue.None, transform.position, AnimCue.UsingPhone, Sfx.Me, false);
    }

    private bool EnsurePhone()
    {
        if (phone != null) return true;
        var cam = Camera.main;
        if (phonePrefab == null || cam == null)
        {
            Debug.LogWarning("[PhoneController] Needs a Phone Prefab (with a PhoneScreen) and a main camera.", this);
            return false;
        }
        phone = Instantiate(phonePrefab, cam.transform);
        phone.Init(cam);
        phone.gameObject.SetActive(false);
        return true;
    }

    // Slide up into the hand.
    private void Raise()
    {
        float t = Mathf.Clamp01((Time.unscaledTime - raisedAt) / raiseSeconds);
        t = 1f - (1f - t) * (1f - t);
        phone.transform.localPosition = (holdOffset + Vector3.down * (raiseFrom * (1f - t))) * FovScale();
        phone.transform.localRotation = Quaternion.Euler(holdRotation);
    }

    // Same size on screen whatever the field of view: closer when it's wider (never inside the near clip).
    private float FovScale()
    {
        var cam = phone != null && phone.transform.parent != null ? phone.transform.parent.GetComponent<Camera>() : null;
        if (holdOffsetFov <= 0f || cam == null) return 1f;
        float scale = Mathf.Tan(holdOffsetFov * 0.5f * Mathf.Deg2Rad) / Mathf.Tan(Mathf.Clamp(cam.fieldOfView, 10f, 170f) * 0.5f * Mathf.Deg2Rad);
        if (holdOffset.z > 0f) scale = Mathf.Max(scale, (cam.nearClipPlane + 0.03f) / holdOffset.z);
        return scale;
    }

    private void RefreshHint()
    {
        if (hintText == null || !IsMine) return;
        int unread = Notifications.UnreadCount;
        hintText.text = unread > 0 ? string.Format(hintFormat, unread) : "";
    }

    private void OnDestroy()
    {
        if (phone == null) return; // never this machine's phone
        if (IsOpen) UIState.CloseMenu(this);
        IsOpen = false;
    }
}
