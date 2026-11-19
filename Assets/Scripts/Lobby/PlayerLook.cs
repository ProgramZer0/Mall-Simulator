using System;
using System.Text;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// How a player looks: picks (body model, hat, hair... each a list of objects, one shown) and colors
/// (shirt, pants, skin... each a list of colors put on some renderers). Everyone sees it; it's saved on
/// your machine and put back on whenever you spawn (the lobby, the mall). Changed with LookButtons.
///
/// Setup: on the player prefab.
/// - Picks: a name ("Hat") and its Options: child objects of the model, one turned on at a time. Can Be
///   None: one more choice with all of them off (no hat).
/// - Colors: a name ("Shirt"), the Renderers it colors (Material Index: which material on them, -1 all),
///   Color Property (_BaseColor for URP Lit, _Color for Built-in) and the Colors to pick from.
/// </summary>
public class PlayerLook : NetworkBehaviour
{
    [Serializable]
    public class Pick
    {
        public string name = "Hat";
        public GameObject[] options;
        [Tooltip("One more choice: none of them.")]
        public bool canBeNone;
        public int Count => (options?.Length ?? 0) + (canBeNone ? 1 : 0);
    }

    [Serializable]
    public class ColorSlot
    {
        public string name = "Shirt";
        public Renderer[] renderers;
        [Tooltip("Which material on those renderers (-1: all of them).")]
        public int materialIndex = -1;
        public string colorProperty = "_BaseColor";
        public Color[] colors = { Color.white, new(0.85f, 0.2f, 0.2f), new(0.2f, 0.45f, 0.85f), new(0.25f, 0.7f, 0.3f), new(0.95f, 0.8f, 0.2f), new(0.15f, 0.15f, 0.15f) };
        public int Count => colors?.Length ?? 0;
    }

    [SerializeField] private Pick[] picks = Array.Empty<Pick>();
    [SerializeField] private ColorSlot[] colors = Array.Empty<ColorSlot>();
    [Tooltip("Saved on this machine under this key.")]
    [SerializeField] private string saveKey = "MallSim.Look";

    /// <summary>"p0,p1,...|c0,c1,..." (choice per pick, then per color).</summary>
    private readonly NetworkVariable<FixedString128Bytes> look =
        new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    private int[] pickChoice = Array.Empty<int>();
    private int[] colorChoice = Array.Empty<int>();
    private MaterialPropertyBlock block;

    /// <summary>The local player's look (null when not spawned).</summary>
    public static PlayerLook Local { get; private set; }

    /// <summary>Raised on the local machine when the local player's look changes.</summary>
    public static event Action LocalChanged;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetOnPlay() { Local = null; LocalChanged = null; }

    public enum Part { Pick, Color }

    public override void OnNetworkSpawn()
    {
        look.OnValueChanged += OnLookChanged;
        if (IsOwner)
        {
            Local = this;
            Decode(PlayerPrefs.GetString(saveKey, ""));
            look.Value = new FixedString128Bytes(Encode());
            LocalChanged?.Invoke();
        }
        else Decode(look.Value.ToString());
        Apply();
    }

    public override void OnNetworkDespawn()
    {
        look.OnValueChanged -= OnLookChanged;
        if (Local == this) Local = null;
    }

    private void OnLookChanged(FixedString128Bytes previous, FixedString128Bytes current)
    {
        if (IsOwner) return; // already applied
        Decode(current.ToString());
        Apply();
    }

    /* ========================= ASKING AND CHANGING (owner) ========================= */

    /// <summary>The index of a pick or color by name (-1: none).</summary>
    public int IndexOf(Part part, string name)
    {
        if (part == Part.Pick) { for (int i = 0; i < picks.Length; i++) if (picks[i] != null && Same(picks[i].name, name)) return i; }
        else for (int i = 0; i < colors.Length; i++) if (colors[i] != null && Same(colors[i].name, name)) return i;
        return -1;
    }

    private static bool Same(string a, string b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    public int ChoiceCount(Part part, int index) =>
        part == Part.Pick ? (Valid(part, index) ? picks[index].Count : 0) : (Valid(part, index) ? colors[index].Count : 0);

    public int Choice(Part part, int index) =>
        !Valid(part, index) ? 0 : part == Part.Pick ? pickChoice[index] : colorChoice[index];

    /// <summary>A color option (for a button's swatch).</summary>
    public Color ColorOption(int slot, int choice) =>
        Valid(Part.Color, slot) && choice >= 0 && choice < colors[slot].Count ? colors[slot].colors[choice] : Color.white;

    private bool Valid(Part part, int index) =>
        index >= 0 && (part == Part.Pick ? index < picks.Length && index < pickChoice.Length && picks[index] != null
                                         : index < colors.Length && index < colorChoice.Length && colors[index] != null);

    /// <summary>Owner: step a pick or color by delta (wrapping round).</summary>
    public void Step(Part part, int index, int delta)
    {
        int n = ChoiceCount(part, index);
        if (n <= 0) return;
        Set(part, index, ((Choice(part, index) + delta) % n + n) % n);
    }

    /// <summary>Owner: set a pick or color to a choice.</summary>
    public void Set(Part part, int index, int choice)
    {
        if (!IsOwner || !Valid(part, index)) return;
        int n = ChoiceCount(part, index);
        if (n <= 0) return;
        choice = Mathf.Clamp(choice, 0, n - 1);
        if (part == Part.Pick) pickChoice[index] = choice; else colorChoice[index] = choice;
        Apply();
        string code = Encode();
        look.Value = new FixedString128Bytes(code);
        PlayerPrefs.SetString(saveKey, code);
        PlayerPrefs.Save();
        LocalChanged?.Invoke();
    }

    /* ========================= SHOWING ========================= */

    private void Apply()
    {
        for (int i = 0; i < picks.Length && i < pickChoice.Length; i++)
        {
            var p = picks[i];
            if (p?.options == null) continue;
            for (int o = 0; o < p.options.Length; o++)
                if (p.options[o] != null) p.options[o].SetActive(o == pickChoice[i]); // the "none" choice is past the end
        }
        block ??= new MaterialPropertyBlock();
        for (int i = 0; i < colors.Length && i < colorChoice.Length; i++)
        {
            var c = colors[i];
            if (c?.renderers == null || c.Count == 0 || string.IsNullOrEmpty(c.colorProperty)) continue;
            Color col = c.colors[Mathf.Clamp(colorChoice[i], 0, c.Count - 1)];
            foreach (var r in c.renderers)
            {
                if (r == null) continue;
                int mats = r.sharedMaterials.Length;
                for (int m = 0; m < mats; m++)
                {
                    if (c.materialIndex >= 0 && m != c.materialIndex) continue;
                    r.GetPropertyBlock(block, m);
                    block.SetColor(c.colorProperty, col);
                    r.SetPropertyBlock(block, m);
                }
            }
        }
    }

    /* ========================= SAVING ========================= */

    private string Encode()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < pickChoice.Length; i++) { if (i > 0) sb.Append(','); sb.Append(pickChoice[i]); }
        sb.Append('|');
        for (int i = 0; i < colorChoice.Length; i++) { if (i > 0) sb.Append(','); sb.Append(colorChoice[i]); }
        return sb.ToString();
    }

    // Unknown or out-of-range parts (the lists changed since it was saved) fall back to the first choice.
    private void Decode(string code)
    {
        pickChoice = new int[picks.Length];
        colorChoice = new int[colors.Length];
        if (string.IsNullOrEmpty(code)) return;
        string[] halves = code.Split('|');
        Read(halves[0], pickChoice, i => picks[i]?.Count ?? 0);
        if (halves.Length > 1) Read(halves[1], colorChoice, i => colors[i]?.Count ?? 0);
    }

    private static void Read(string list, int[] into, Func<int, int> countOf)
    {
        string[] parts = list.Split(',');
        for (int i = 0; i < into.Length && i < parts.Length; i++)
            if (int.TryParse(parts[i], out int v) && v >= 0 && v < countOf(i)) into[i] = v;
    }
}
