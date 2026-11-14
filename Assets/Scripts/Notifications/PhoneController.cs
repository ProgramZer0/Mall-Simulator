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
/// Keys:
/// - Esc: brings the phone up on its home screen; Esc again goes back to home, then puts it away.
///   Esc that something else used first (closing the shop, cancelling a drag, the scanner...) doesn't
///   bring it up (see UIState.TakeEscape).
/// - Tab: brings it up straight on the Notifications app; Tab again puts it away.
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

    [SerializeField] private KeyCode homeKey = KeyCode.Escape;
    [SerializeField] private KeyCode notificationsKey = KeyCode.Tab;
    [Tooltip("The app Tab opens (its App Name).")]
    [SerializeField] private string notificationsApp = "Notifications";

    [Tooltip("Optional HUD text: {0} = unread count. Empty when nothing's unread.")]
    [SerializeField] private TMP_Text hintText;
    [SerializeField] private string hintFormat = "Tab: Phone ({0})";

    private PhoneScreen phone;
    private Unity.Netcode.NetworkObject owner; // on a player prefab: that player
    private bool ownerChecked;
    private float raisedAt;
    private int closedFrame = -1; // the key that put it away mustn't bring it straight back up

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
        ownerChecked = false;
        RefreshHint();
    }

    private void OnDisable()
    {
        Notifications.Changed -= RefreshHint;
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
            if (phone != null && !phone.OnHome) phone.ShowHome();
            else Close();
        }
        if (phone != null) Raise();
    }

    private void LateUpdate()
    {
        if (!IsMine || IsOpen || closedFrame == Time.frameCount || UIState.IsMenuOpen || !InGame()) return;
        if (Input.GetKeyDown(notificationsKey)) Open(notificationsApp);
        else if (Input.GetKeyDown(homeKey) && !(homeKey == KeyCode.Escape && UIState.EscapeTakenThisFrame)) Open(null);
    }

    private static bool InGame()
    {
        var nm = Unity.Netcode.NetworkManager.Singleton;
        return nm != null && nm.IsListening && nm.LocalClient?.PlayerObject != null;
    }

    /// <summary>Bring the phone up on an app (by App Name), or its home screen (null).</summary>
    public void Open(string app)
    {
        if (IsOpen || !EnsurePhone()) return;
        IsOpen = true;
        if (homeKey == KeyCode.Escape) UIState.MarkEscapeTaken();
        phone.gameObject.SetActive(true);
        raisedAt = Time.unscaledTime;
        Raise();
        UIState.OpenMenu(this);
        if (app == null || !phone.ShowApp(app)) phone.ShowHome();
    }

    public void Close()
    {
        if (!IsOpen || phone == null) return; // only the copy that opened it closes it
        IsOpen = false;
        closedFrame = Time.frameCount;
        if (phone != null)
        {
            phone.CloseApps();
            phone.gameObject.SetActive(false);
        }
        UIState.CloseMenu(this);
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
        phone.transform.localPosition = holdOffset + Vector3.down * (raiseFrom * (1f - t));
        phone.transform.localRotation = Quaternion.Euler(holdRotation);
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
