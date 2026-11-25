using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The phone prefab's screen (see PhoneController): a home screen with a button per app, and the apps.
///
/// Setup, on the phone model prefab:
/// - A child Canvas set to World Space, sized and placed on the phone's screen (like the shop monitor's
///   canvas), with a Graphic Raycaster. Its Event Camera is set to the player's camera at runtime.
/// - Home: the home screen panel. App Button Prefab (a PhoneAppButton) is made once per app into App
///   Grid, placed in a grid by code (Columns, Cell Size, Spacing): no layout group.
/// - Apps: panels under the canvas with an IPhoneApp component (NotificationsApp, PhoneInfoApp...).
///   They're found by themselves; the home screen lists them in hierarchy order.
/// - Optional: Title Text (the open app's name, or Home Title), Back Button (back to home).
/// - In the office (the lobby, the main menu scene) only Office Apps are on it; Office Only Apps (Quit)
///   are only there. (By App Name.)
/// </summary>
public class PhoneScreen : MonoBehaviour
{
    [SerializeField] private Canvas canvas;
    [Tooltip("The phone's background (behind the home screen and the apps): shows the colour and picture picked " +
             "in the Customize app. Empty: one is made as the canvas's first child. Panels on top of it need to be " +
             "see-through to show it.")]
    [SerializeField] private Image wallpaperImage;
    [Tooltip("Apps laid out in the prefab (Notifications, Franchises, Lost and Found...) and the home screen take " +
             "the Customize app's colors too: texts its Text color, buttons its Button color (a PhoneThemeRole on " +
             "any text or image picks another, or Keep). Off: only apps built in code follow the theme.")]
    [SerializeField] private bool themeDesignedApps = true;
    [Tooltip("Your button for every button the apps make in code (a Button with a TMP text in it), fitted to " +
             "each row. Empty: Resources/UIButton if there is one, else the built-in ones.")]
    [SerializeField] private Button buttonPrefab;

    [Header("Opening and closing apps")]
    [Tooltip("Apps grow out of their icon when opened and shrink back into it going home; the home screen " +
             "eases back and forth behind them.")]
    [SerializeField] private bool animateApps = true;
    [SerializeField, Min(0.01f)] private float appOpenSeconds = 0.2f;
    [SerializeField, Min(0.01f)] private float appCloseSeconds = 0.16f;
    [SerializeField] private GameObject home;
    [SerializeField] private PhoneAppButton appButtonPrefab;
    [SerializeField] private RectTransform appGrid;
    [SerializeField, Min(1)] private int columns = 3;
    [SerializeField] private Vector2 cellSize = new(90f, 100f);
    [SerializeField] private Vector2 spacing = new(10f, 10f);
    [SerializeField] private TMP_Text titleText;
    [SerializeField] private string homeTitle = "";
    [SerializeField] private Button backButton;
    [Tooltip("The only apps on the phone in the office (the lobby). Empty: all of them.")]
    [SerializeField] private string[] officeApps = { "Settings", "Help", "Multiplayer", "Customize", "Quit" };
    [Tooltip("Apps only on the phone in the office.")]
    [SerializeField] private string[] officeOnlyApps = { "Quit" };

    private readonly List<MonoBehaviour> apps = new();
    private readonly List<PhoneAppButton> buttons = new();
    private int current = -1; // -1 = home

    public bool OnHome => current < 0;
    /// <summary>The open app's App Name, or null on the home screen.</summary>
    public string CurrentApp => current >= 0 && current < apps.Count ? ((IPhoneApp)apps[current]).AppName : null;

    /// <summary>Called once by PhoneController after it makes the phone.</summary>
    private Color wallpaperStartColor, titleStartColor;
    private bool titleRemembered;
    private Sprite wallpaperStartSprite;
    private bool wallpaperMade, wallpaperRemembered;

    private void OnDestroy() => PhoneTheme.Changed -= ApplyTheme;

    // What each recolored part looked like as designed (back to that with the Default theme).
    private readonly System.Collections.Generic.Dictionary<Graphic, Color> designed = new();
    private float nextThemePass;

    private void Update()
    {
        // New rows appear in designed apps as they fill: recolor now and then while the phone's up.
        if (Time.unscaledTime < nextThemePass) return;
        nextThemePass = Time.unscaledTime + 0.4f;
        ThemeDesigned();
    }

    /// <summary>
    /// Theme something just made on the phone right away (a button from your prefab, a rebuilt page), so
    /// it doesn't show in its prefab's colors until the next pass and then flash over to the theme's.
    /// </summary>
    public static void ThemeNow(Transform made)
    {
        if (made == null) return;
        var screen = made.GetComponentInParent<PhoneScreen>(true);
        if (screen != null) screen.ThemeDesigned(made);
    }

    private void ThemeDesigned() => ThemeDesigned(canvas != null ? canvas.transform : null, false); // (what's showing)

    private void ThemeDesigned(Transform root, bool includeHidden = true)
    {
        if (!themeDesignedApps || root == null) return;
        var theme = PhoneTheme.Current;
        foreach (var g in root.GetComponentsInChildren<Graphic>(includeHidden))
        {
            if (g == wallpaperImage) continue;
            var role = g.TryGetComponent<PhoneThemeRole>(out var r) ? r.role : PhoneThemeRole.Role.Auto;
            if (role == PhoneThemeRole.Role.Auto)
            {
                if (g is TMPro.TMP_Text) role = PhoneThemeRole.Role.Text;
                else if (g.GetComponentInParent<Selectable>() is Selectable sel && sel.targetGraphic == g) role = PhoneThemeRole.Role.Button;
            }
            var text = g as TMPro.TMP_Text;
            if (role == PhoneThemeRole.Role.Auto || role == PhoneThemeRole.Role.Keep)
            {
                if (text != null) PhoneTheme.ApplyOutline(text);
                continue;
            }
            if (!designed.ContainsKey(g)) designed[g] = g.color;
            Color c = theme == null ? designed[g] : role switch
            {
                PhoneThemeRole.Role.Text => theme.text,
                PhoneThemeRole.Role.DimText => theme.dimText,
                PhoneThemeRole.Role.Button => theme.button,
                PhoneThemeRole.Role.Accent => theme.accent,
                PhoneThemeRole.Role.Background => theme.background,
                PhoneThemeRole.Role.Track => theme.track,
                _ => g.color,
            };
            c.a = theme == null ? c.a : designed[g].a * c.a; // keep see-through parts see-through
            if (g.color != c) g.color = c;
            if (text != null) PhoneTheme.ApplyOutline(text); // (after the color: the outline's worked out from it)
        }
    }

    private void ApplyTheme()
    {
        nextThemePass = 0f;
        if (this == null) return;
        if (wallpaperImage == null && canvas != null && (PhoneTheme.Current != null || PhoneTheme.Wallpaper != null))
        {
            var go = new GameObject("Wallpaper", typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(canvas.transform, false);
            rt.SetAsFirstSibling();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            wallpaperImage = go.GetComponent<Image>();
            wallpaperImage.raycastTarget = false;
            wallpaperMade = true;
        }
        if (wallpaperImage == null) { ApplyTitle(); return; }
        if (!wallpaperRemembered)
        {
            wallpaperRemembered = true;
            wallpaperStartColor = wallpaperMade ? new Color(0f, 0f, 0f, 0f) : wallpaperImage.color;
            wallpaperStartSprite = wallpaperMade ? null : wallpaperImage.sprite;
        }
        var paper = PhoneTheme.Wallpaper;
        wallpaperImage.sprite = paper != null ? paper : wallpaperStartSprite;
        wallpaperImage.color = paper != null ? PhoneTheme.WallpaperTint
                             : PhoneTheme.Current != null ? PhoneTheme.Current.background : wallpaperStartColor;
        wallpaperImage.preserveAspect = false;
        ApplyTitle();
    }

    private void ApplyTitle()
    {
        if (titleText != null)
        {
            if (!titleRemembered) { titleRemembered = true; titleStartColor = titleText.color; }
            titleText.color = PhoneTheme.Current != null ? PhoneTheme.Current.text : titleStartColor;
        }
    }

    public void Init(Camera cam)
    {
        if (canvas == null) canvas = GetComponentInChildren<Canvas>(true);
        if (canvas != null)
        {
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = cam;
        }
        if (buttonPrefab != null) CodeButton.PhonePrefab = buttonPrefab;

        // The player's chosen look (Customize app), and the background showing it.
        var styleApp = GetComponentInChildren<PhoneStyleApp>(true);
        if (styleApp != null) styleApp.ApplySaved();
        PhoneTheme.Changed -= ApplyTheme;
        PhoneTheme.Changed += ApplyTheme;
        ApplyTheme();

        // A phone model in front of the camera mustn't bump into things.
        foreach (var c in GetComponentsInChildren<Collider>(true)) c.enabled = false;

        apps.Clear();
        foreach (var mb in GetComponentsInChildren<MonoBehaviour>(true))
            if (mb is IPhoneApp) apps.Add(mb);
        foreach (var app in apps) app.gameObject.SetActive(false);
        bool office = MallConnection.Instance != null && MallConnection.Instance.InLobby;
        apps.RemoveAll(a =>
        {
            string n = ((IPhoneApp)a).AppName;
            return office ? officeApps != null && officeApps.Length > 0 && System.Array.IndexOf(officeApps, n) < 0
                          : officeOnlyApps != null && System.Array.IndexOf(officeOnlyApps, n) >= 0;
        });

        if (appGrid != null)
            foreach (var g in appGrid.GetComponents<LayoutGroup>()) g.enabled = false;
        if (appButtonPrefab != null && appGrid != null)
            for (int i = 0; i < apps.Count; i++)
            {
                var b = Instantiate(appButtonPrefab, appGrid);
                var rt = (RectTransform)b.transform;
                rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
                rt.pivot = new Vector2(0f, 1f);
                rt.sizeDelta = cellSize;
                rt.anchoredPosition = new Vector2((i % columns) * (cellSize.x + spacing.x), -(i / columns) * (cellSize.y + spacing.y));
                var app = (IPhoneApp)apps[i];
                if (b.label != null) b.label.text = app.AppName;
                if (b.icon != null && app.Icon != null) b.icon.sprite = app.Icon;
                int index = i;
                if (b.button != null) b.button.onClick.AddListener(() => ShowApp(index));
                buttons.Add(b);
            }
        if (backButton != null) backButton.onClick.AddListener(ShowHome);
    }

    public void ShowHome()
    {
        int was = current;
        CloseCurrent(animated: true);
        if (home != null) { home.SetActive(true); ThemeDesigned(home.transform); }
        if (was >= 0) Animate(home, from: 1.08f, to: 1f, fadeIn: true, appCloseSeconds, null, null);
        if (titleText != null) titleText.text = homeTitle;
        if (backButton != null) backButton.gameObject.SetActive(false);
        RefreshBadges();
    }

    /// <summary>Show an app by its App Name. False if there's none by that name.</summary>
    public bool ShowApp(string appName)
    {
        for (int i = 0; i < apps.Count; i++)
            if (((IPhoneApp)apps[i]).AppName == appName)
            {
                if (((IPhoneApp)apps[i]).LockedReason != null) return false;
                ShowApp(i);
                return true;
            }
        return false;
    }

    public void ShowApp(int index)
    {
        if (index < 0 || index >= apps.Count || ((IPhoneApp)apps[index]).LockedReason != null) return;
        CloseCurrent();
        if (home != null)
        {
            if (animateApps && home.activeInHierarchy) Animate(home, from: 1f, to: 1.08f, fadeIn: false, appOpenSeconds, null, () => home.SetActive(false));
            else home.SetActive(false);
        }
        current = index;
        apps[index].gameObject.SetActive(true);
        Animate(apps[index].gameObject, from: 0.25f, to: 1f, fadeIn: true, appOpenSeconds, IconOf(index), null);
        var app = (IPhoneApp)apps[index];
        app.OnAppOpened();
        ThemeDesigned(apps[index].transform); // its page in the theme's colors from the first frame (no flash)
        if (titleText != null) titleText.text = app.AppName;
        if (backButton != null) backButton.gameObject.SetActive(true);
    }

    /// <summary>Put every app away (the phone is going down).</summary>
    public void CloseApps()
    {
        FinishAnimations();
        CloseCurrent();
    }

    private void CloseCurrent(bool animated = false)
    {
        if (current < 0 || current >= apps.Count) { current = -1; return; }
        ((IPhoneApp)apps[current]).OnAppClosed();
        var go = apps[current].gameObject;
        if (animated && animateApps) Animate(go, from: 1f, to: 0.25f, fadeIn: false, appCloseSeconds, IconOf(current), () => go.SetActive(false));
        else go.SetActive(false);
        current = -1;
    }

    /* ========================= APP ANIMATIONS ========================= */

    private class Tween
    {
        public RectTransform rt;
        public CanvasGroup group;
        public float from, to, seconds, t;
        public bool fadeIn;
        public Vector2 offset; // toward the icon at the small end
        public System.Action done;
    }

    private readonly List<Tween> tweens = new();
    private readonly Dictionary<RectTransform, (Vector2 pos, Vector3 scale)> rests = new();

    private RectTransform IconOf(int appIndex) =>
        appIndex >= 0 && appIndex < buttons.Count && buttons[appIndex] != null ? (RectTransform)buttons[appIndex].transform : null;

    // Scale From → To (and fade in or out), the small end at the icon if there is one; Done when finished.
    private void Animate(GameObject go, float from, float to, bool fadeIn, float seconds, RectTransform icon, System.Action done)
    {
        if (go == null || go.transform is not RectTransform rt) { done?.Invoke(); return; }
        for (int i = tweens.Count - 1; i >= 0; i--) // one at a time per object: the last one jumps to its end
            if (tweens[i].rt == rt) { Finish(tweens[i]); tweens.RemoveAt(i); }
        if (!rests.ContainsKey(rt)) rests[rt] = (rt.anchoredPosition, rt.localScale);
        if (!animateApps || !isActiveAndEnabled) { done?.Invoke(); return; }
        if (!go.TryGetComponent<CanvasGroup>(out var group)) group = go.AddComponent<CanvasGroup>();
        Vector2 offset = Vector2.zero;
        if (icon != null && rt.parent is RectTransform parent)
        {
            Vector3 iconCenter = parent.InverseTransformPoint(icon.TransformPoint(icon.rect.center));
            Vector3 panelCenter = parent.InverseTransformPoint(rt.TransformPoint(rt.rect.center));
            offset = (Vector2)(iconCenter - panelCenter);
        }
        var tw = new Tween { rt = rt, group = group, from = from, to = to, seconds = seconds, fadeIn = fadeIn, offset = offset, done = done };
        tweens.Add(tw);
        Step(tw, 0f);
    }

    private void LateUpdate()
    {
        for (int i = tweens.Count - 1; i >= 0; i--)
        {
            var tw = tweens[i];
            if (tw.rt == null) { tweens.RemoveAt(i); continue; }
            tw.t += Time.unscaledDeltaTime / tw.seconds;
            if (tw.t >= 1f) { Finish(tw); tweens.RemoveAt(i); }
            else Step(tw, tw.t);
        }
    }

    private void Step(Tween tw, float t)
    {
        var (pos, scale) = rests[tw.rt];
        float e = 1f - Mathf.Pow(1f - Mathf.Clamp01(t), 3f); // ease out
        float s = Mathf.Lerp(tw.from, tw.to, e);
        tw.rt.localScale = scale * s;
        // At the small end it sits on the icon; at full size, where it belongs.
        float small = Mathf.Min(tw.from, tw.to), big = Mathf.Max(tw.from, tw.to);
        float towardIcon = big > small ? Mathf.InverseLerp(big, small, s) : 0f;
        tw.rt.anchoredPosition = pos + tw.offset * towardIcon;
        if (tw.group != null) tw.group.alpha = tw.fadeIn ? Mathf.Clamp01(e * 1.4f) : 1f - Mathf.Clamp01(e * 1.2f);
    }

    private void Finish(Tween tw)
    {
        if (tw.rt != null && rests.TryGetValue(tw.rt, out var rest))
        {
            tw.rt.localScale = rest.scale;
            tw.rt.anchoredPosition = rest.pos;
        }
        if (tw.group != null) tw.group.alpha = 1f;
        tw.done?.Invoke();
    }

    private void FinishAnimations()
    {
        for (int i = tweens.Count - 1; i >= 0; i--) Finish(tweens[i]);
        tweens.Clear();
    }

    private void RefreshBadges()
    {
        for (int i = 0; i < buttons.Count && i < apps.Count; i++)
        {
            // Locked apps: greyed out, with why under the name.
            var app = (IPhoneApp)apps[i];
            string locked = app.LockedReason;
            if (buttons[i].button != null) buttons[i].button.interactable = locked == null;
            if (buttons[i].label != null) buttons[i].label.text = locked == null ? app.AppName : $"{app.AppName}\n{locked}";

            if (buttons[i].badge == null) continue;
            int n = locked == null ? app.Badge : 0;
            buttons[i].badge.text = n > 0 ? n.ToString() : "";
            if (buttons[i].badgeRoot != null) buttons[i].badgeRoot.SetActive(n > 0);
        }
    }
}
