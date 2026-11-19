using System.Collections;
using TMPro;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// The office's chosen mall (the lobby, the main menu scene) and the window that shows it. The host picks
/// it (the drafting table: a new mall; later the filing cabinet: a save), everyone sees it in the window
/// with its name under it, and the "Enter mall" door (LobbyAction Enter Mall) opens it.
///
/// Setup: on an object in the menu scene with a NetworkObject (e.g. the window).
/// - Picture: the renderer the mall's picture is shown on (a Quad with an unlit material is best), with
///   No Mall Picture (nothing picked yet: blinds, a dark closed mall...) and New Mall Picture.
///   Make the pictures with WindowShotCamera in the game scene.
/// - Looking out a window: with Window set (an empty in the middle of the glass, its blue arrow (Z)
///   pointing into the room) the picture is put Picture Depth behind the glass and made big enough to
///   fill the window from anywhere in the room, so it moves like a real view when you walk around.
/// - Name Text: a TMP text under the window (the mall's name).
/// </summary>
public class OfficeMall : NetworkBehaviour
{
    public const string NewMall = "new";

    [Header("Window")]
    [SerializeField] private Renderer picture;
    [Tooltip("The texture property on the picture's material (_BaseMap for URP, _MainTex for Built-in/Unlit).")]
    [SerializeField] private string textureProperty = "_BaseMap";
    [Tooltip("The color property, for fading between pictures (_BaseColor for URP, _Color for Built-in).")]
    [SerializeField] private string colorProperty = "_BaseColor";
    [SerializeField] private Texture noMallPicture;
    [SerializeField] private Texture newMallPicture;
    [Tooltip("Fading from one picture to the next, in seconds.")]
    [SerializeField, Min(0f)] private float fadeSeconds = 0.6f;

    [Header("Looking out a window (optional)")]
    [Tooltip("An empty in the middle of the glass, its blue arrow (Z) pointing into the room. Set it to have the picture placed behind the glass.")]
    [SerializeField] private Transform window;
    [SerializeField] private Vector2 windowSize = new(3f, 1.5f);
    [Tooltip("How far behind the glass the picture sits, in meters (further: it moves less as you walk, like a far view).")]
    [SerializeField, Min(0.5f)] private float pictureDepth = 6f;
    [Tooltip("How close to the glass players get, in meters (the picture's made big enough to fill the window from there).")]
    [SerializeField, Min(0.2f)] private float closestView = 1.5f;
    [Tooltip("How far players walk to the side of the window, in meters (more: a bigger picture, less of the edge seen).")]
    [SerializeField, Min(0f)] private float sideRoom = 1f;

    [Header("Name")]
    [SerializeField] private TMP_Text nameText;
    [SerializeField] private string noMallName = "No mall planned";
    [SerializeField] private string newMallName = "New Mall";

    private readonly NetworkVariable<FixedString64Bytes> choice = new();
    private readonly NetworkVariable<FixedString64Bytes> mallName = new();

    private MaterialPropertyBlock block;
    private Coroutine fade;
    private string shown;

    public static OfficeMall Instance { get; private set; }

    /// <summary>The chosen mall: "" none, "new" a new one (later: a save's id).</summary>
    public string Choice => choice.Value.ToString();
    public bool HasChoice => Choice.Length > 0;
    /// <summary>The save to load for the choice (null: a new mall).</summary>
    public string SaveToLoad => HasChoice && Choice != NewMall ? Choice : null;

    private void Awake()
    {
        Instance = this;
        PlacePicture();
        Show("", instant: true);
    }

    public override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    public override void OnNetworkSpawn()
    {
        choice.OnValueChanged += OnChoiceChanged;
        mallName.OnValueChanged += OnNameChanged;
        Show(Choice, instant: true);
    }

    public override void OnNetworkDespawn()
    {
        choice.OnValueChanged -= OnChoiceChanged;
        mallName.OnValueChanged -= OnNameChanged;
    }

    private void OnChoiceChanged(FixedString64Bytes a, FixedString64Bytes b) => Show(b.ToString(), false);
    private void OnNameChanged(FixedString64Bytes a, FixedString64Bytes b) => ShowName();

    /// <summary>Host: plan a new mall (the drafting table).</summary>
    public void ChooseNew()
    {
        if (!IsServer) return;
        mallName.Value = new FixedString64Bytes(newMallName);
        choice.Value = new FixedString64Bytes(NewMall);
    }

    /* ========================= THE WINDOW ========================= */

    private void Show(string id, bool instant)
    {
        ShowName();
        if (picture == null || id == shown) return;
        shown = id;
        Texture tex = id.Length == 0 ? noMallPicture : id == NewMall ? newMallPicture : newMallPicture; // saves: their screenshot, later
        if (fade != null) StopCoroutine(fade);
        if (instant || fadeSeconds <= 0f || !isActiveAndEnabled) { SetPicture(tex, 1f); return; }
        fade = StartCoroutine(FadeTo(tex));
    }

    private IEnumerator FadeTo(Texture tex)
    {
        float half = fadeSeconds * 0.5f;
        for (float t = 0f; t < half; t += Time.unscaledDeltaTime) { SetPicture(null, 1f - t / half); yield return null; }
        for (float t = 0f; t < half; t += Time.unscaledDeltaTime) { SetPicture(tex, t / half); yield return null; }
        SetPicture(tex, 1f);
        fade = null;
    }

    // Texture: null keeps the one it has. Brightness: 0 black, 1 as it is.
    private void SetPicture(Texture tex, float brightness)
    {
        block ??= new MaterialPropertyBlock();
        picture.GetPropertyBlock(block);
        if (tex != null && !string.IsNullOrEmpty(textureProperty)) block.SetTexture(textureProperty, tex);
        if (!string.IsNullOrEmpty(colorProperty)) block.SetColor(colorProperty, new Color(brightness, brightness, brightness, 1f));
        picture.SetPropertyBlock(block);
    }

    private void ShowName()
    {
        if (nameText == null) return;
        string n = mallName.Value.ToString();
        nameText.text = !HasChoice ? noMallName : n.Length > 0 ? n : newMallName;
    }

    // Behind the glass, big enough to fill the window seen from Closest View in front of it, from anywhere
    // up to Side Room either side.
    private void PlacePicture()
    {
        if (window == null || picture == null) return;
        float grow = (closestView + pictureDepth) / closestView;
        float w = (windowSize.x + 2f * sideRoom) * grow - 2f * sideRoom;
        float h = (windowSize.y + sideRoom) * grow - sideRoom; // a little room for looking up and down too
        var t = picture.transform;
        t.SetPositionAndRotation(window.position - window.forward * pictureDepth, Quaternion.LookRotation(-window.forward, window.up));
        Vector3 parentScale = t.parent != null ? t.parent.lossyScale : Vector3.one;
        t.localScale = new Vector3(w / Mathf.Max(0.0001f, parentScale.x), h / Mathf.Max(0.0001f, parentScale.y), 1f);
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (!Application.isPlaying) UnityEditor.EditorApplication.delayCall += () => { if (this != null) PlacePicture(); };
    }

    private void OnDrawGizmosSelected()
    {
        if (window == null) return;
        Gizmos.color = new Color(0.5f, 0.8f, 1f, 0.8f);
        Gizmos.matrix = Matrix4x4.TRS(window.position, window.rotation, Vector3.one);
        Gizmos.DrawWireCube(Vector3.zero, new Vector3(windowSize.x, windowSize.y, 0.01f));
        Gizmos.DrawLine(Vector3.zero, Vector3.forward * 0.5f);
    }
#endif
}
