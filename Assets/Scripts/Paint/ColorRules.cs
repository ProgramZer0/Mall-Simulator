using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// Which colors something may be painted, and how its color goes down with customers.
///
/// Limits (first one that lists any colors):
///   1. the item's own Colors (ItemDef → Colors),
///   2. its categories' (PaintPalette → Limits; an item in several gets all of theirs),
///   3. none: any color that isn't Exclusive.
/// Plus, always, its franchise's Colors (FranchiseDef → Colors): the brand's colors are extra colors for
/// every item the franchise covers. Original (the model's own colors) is always allowed. Exclusive colors
/// only where a list names them. And a color can only be used once the team reaches its Unlock Level
/// (PaintPalette); until then pickers don't show it.
/// Walls, floors and storefronts: any unlocked color that isn't Exclusive.
///
/// Taste: an item's Color Tastes (how a color suits it) plus the customer's own (CustomerPersonality),
/// each -1..1, neutral when not listed; StoreAppealSettings → Color Strength turns it into how much more
/// or less the item is worth to them.
/// </summary>
public static class ColorRules
{
    private static readonly byte[] NoLimit = new byte[0];
    private static readonly Dictionary<ItemDef, (byte[] limit, byte[] extra)> limits = new();
    private static readonly Dictionary<List<ColorTaste>, float[]> tastes = new();
    private static readonly List<byte> scratch = new();

    /// <summary>Settings changed (palette, franchises): work the lists out again.</summary>
    public static void Forget()
    {
        limits.Clear();
        tastes.Clear();
    }

    /// <summary>
    /// The colors an item is limited to, its franchise's included (Original not listed but always fine), or
    /// null: any color that isn't Exclusive (plus its franchise's, see ExtraOf).
    /// </summary>
    public static IReadOnlyList<byte> LimitOf(ItemDef def)
    {
        var limit = Entry(def).limit;
        return limit == null || limit == NoLimit ? null : limit;
    }

    /// <summary>Its franchise's colors: extra colors on top of whatever else it may be (empty if none).</summary>
    public static IReadOnlyList<byte> ExtraOf(ItemDef def) => Entry(def).extra ?? NoLimit;

    /// <summary>
    /// The colors worth showing for an item (on its buy tile): its limit with its franchise's colors, or just
    /// the franchise's when it can otherwise be any color. Empty: nothing special.
    /// </summary>
    public static IReadOnlyList<byte> ShownFor(ItemDef def) => LimitOf(def) ?? ExtraOf(def);

    private static (byte[] limit, byte[] extra) Entry(ItemDef def)
    {
        if (def == null) return (null, null);
        def = def.BaseDef; // sizes share their item's colors
        if (!limits.TryGetValue(def, out var e))
        {
            scratch.Clear();
            var franchise = FranchiseManager.FranchiseOf(def);
            if (franchise != null) AddNames(franchise.colors, scratch);
            var extra = scratch.ToArray();
            var limit = Build(def);
            if (limit != NoLimit && extra.Length > 0) // the brand's colors on top of the limit
            {
                var both = new List<byte>(limit);
                foreach (var c in extra) if (!both.Contains(c)) both.Add(c);
                limit = both.ToArray();
            }
            e = (limit, extra);
            if (PaintPalette.Instance != null) limits[def] = e; // (not before the palette's there)
        }
        return e;
    }

    private static byte[] Build(ItemDef def)
    {
        scratch.Clear();
        if (AddNames(def.colors, scratch)) return scratch.ToArray();
        bool any = false;
        var palette = PaintPalette.Instance;
        if (palette != null)
            foreach (var c in palette.CategoryLimits)
                if (c != null && !string.IsNullOrWhiteSpace(c.category) && def.HasCategory(c.category))
                    any |= AddNames(c.colors, scratch);
        return any ? scratch.ToArray() : NoLimit;
    }

    // True if the list names anything (even colors no longer in the palette: still a limit).
    private static bool AddNames(List<string> names, List<byte> into)
    {
        if (names == null) return false;
        bool any = false;
        foreach (var n in names)
        {
            if (string.IsNullOrWhiteSpace(n)) continue;
            any = true;
            if (PaintPalette.TryGetId(n, out byte id) && id != 0 && !into.Contains(id)) into.Add(id);
        }
        return any;
    }

    /// <summary>Can this team's player use this color yet (its Unlock Level)?</summary>
    public static bool IsUnlocked(byte id, ulong team)
    {
        int level = PaintPalette.UnlockLevelOf(id);
        return level <= 1 || ProgressionManager.LevelOf(team) >= level;
    }

    /// <summary>May this item be this color, for this team? Why not: a message for the player.</summary>
    public static bool Allows(ItemDef def, byte id, ulong team, out string why)
    {
        why = null;
        if (id == 0) return true;
        string color = PaintPalette.NameOf(id);
        if (!PaintPalette.IsValid(id)) { why = "That isn't a color"; return false; }
        string what = def != null ? def.BaseDef.displayName : "This";
        var limit = LimitOf(def);
        if (limit != null)
        {
            if (!Contains(limit, id))
            {
                // (only the colors the team can use are named)
                var open = new List<byte>();
                foreach (var c in limit) if (IsUnlocked(c, team)) open.Add(c);
                why = open.Count > 0 ? $"{what} only comes in {List(open)}" : $"{what} only comes in its own colors";
                return false;
            }
        }
        else if (PaintPalette.IsExclusive(id) && !Contains(ExtraOf(def), id))
        {
            why = $"{color} is an exclusive color: {what} doesn't come in it";
            return false;
        }
        if (!IsUnlocked(id, team))
        {
            why = $"{color} unlocks at level {PaintPalette.UnlockLevelOf(id)}";
            return false;
        }
        return true;
    }

    /// <summary>Every zone of a paint set (see PaintSet) allowed?</summary>
    public static bool AllowsSet(ItemDef def, uint set, ulong team, out string why)
    {
        why = null;
        for (int zone = 0; zone < PaintParts.MaxZones; zone++)
            if (!Allows(def, PaintSet.Get(set, zone), team, out why)) return false;
        return true;
    }

    /// <summary>Walls, floors, storefronts: any unlocked color that isn't Exclusive.</summary>
    public static bool AllowsPlain(byte id, ulong team, out string why)
    {
        why = null;
        if (id == 0) return true;
        if (!PaintPalette.IsValid(id)) { why = "That isn't a color"; return false; }
        if (PaintPalette.IsExclusive(id)) { why = $"{PaintPalette.NameOf(id)} is an exclusive color: only for some items"; return false; }
        if (!IsUnlocked(id, team)) { why = $"{PaintPalette.NameOf(id)} unlocks at level {PaintPalette.UnlockLevelOf(id)}"; return false; }
        return true;
    }

    /// <summary>A random color this item may come in, levels aside (the special market): Original counts as one.</summary>
    public static byte RandomFor(ItemDef def)
    {
        var palette = PaintPalette.Instance;
        if (palette == null) return 0;
        var limit = LimitOf(def);
        if (limit != null)
        {
            int pick = Random.Range(0, limit.Count + 1); // limit.Count: Original
            return pick < limit.Count ? limit[pick] : (byte)0;
        }
        scratch.Clear();
        scratch.Add(0);
        var extra = ExtraOf(def);
        for (int i = 1; i <= palette.Count; i++)
            if (!PaintPalette.IsExclusive((byte)i) || Contains(extra, (byte)i)) scratch.Add((byte)i);
        return scratch[Random.Range(0, scratch.Count)];
    }

    /// <summary>How a list of tastes rates a color (-1..1, 0 when it's not listed).</summary>
    public static float TasteOf(List<ColorTaste> list, byte id)
    {
        if (list == null || list.Count == 0 || id == 0) return 0f;
        var palette = PaintPalette.Instance;
        if (palette == null) return 0f;
        if (!tastes.TryGetValue(list, out var byId))
        {
            byId = new float[palette.Count + 1];
            foreach (var t in list)
                if (t != null && PaintPalette.TryGetId(t.color, out byte c)) byId[c] = Mathf.Clamp(t.weight, -1f, 1f);
            tastes[list] = byId;
        }
        return id < byId.Length ? byId[id] : 0f;
    }

    /// <summary>
    /// How much more or less an item in this color is worth to this kind of customer: 1 + Color Strength ×
    /// (the item's taste + the customer's), never below 0. Original: 1.
    /// </summary>
    public static float Factor(ItemDef def, CustomerPersonality p, byte id)
    {
        if (id == 0) return 1f;
        var settings = StoreAppealSettings.Instance;
        float strength = settings != null ? settings.colorStrength : 0.15f;
        if (strength <= 0f) return 1f;
        float taste = (def != null ? TasteOf(def.BaseDef.colorTastes, id) : 0f) + (p != null ? TasteOf(p.colorTastes, id) : 0f);
        return Mathf.Max(0f, 1f + strength * taste);
    }

    private static bool Contains(IReadOnlyList<byte> list, byte id)
    {
        for (int i = 0; i < list.Count; i++) if (list[i] == id) return true;
        return false;
    }

    /// <summary>"White, Black or Gold".</summary>
    public static string List(IReadOnlyList<byte> ids)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < ids.Count; i++)
        {
            if (i > 0) sb.Append(i == ids.Count - 1 ? " or " : ", ");
            sb.Append(PaintPalette.NameOf(ids[i]));
        }
        return sb.ToString();
    }
}
