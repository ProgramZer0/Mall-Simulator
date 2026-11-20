using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// The appeal view in store edit mode (V): how appealing the store is as you rearrange it, before saving.
///
/// - A panel (EditAppealPanel on the HUD, else a plain box): Quality, what's for sale and how much of it
///   is seen from the hallway, how the everyday customer sees the store (walks in, wants what's inside),
///   and tips. +/-: the change since the view was first opened this edit.
/// - The floor is shaded where passers-by can see in from the hallway (through the windows and doors);
///   things for sale they can see get a green outline. Decor is labelled with what it counts as.
/// - Select an item: what's near it lights up from red (bad next to it) to green (good next to it): things
///   that go with it or clash with it (Category Pairs), and decor it needs (or that needs it).
/// - Everything that adds or takes away shows its points above it (+12, -4).
///
/// Worked out from the edit copies (StoreAppeal.CreatePreview), only for what moved, and never while
/// something's being dragged. Local to the editor. Added to the player by StoreEditController.
/// </summary>
public class EditAppealView : MonoBehaviour
{
    public const KeyCode ToggleKey = KeyCode.V;

    [Header("Colors")]
    [Tooltip("Everything in the store is drawn in this while the view's on (a plain model look), so the " +
             "highlights stand out.")]
    [SerializeField] private Color greyColor = new(0.84f, 0.86f, 0.88f, 1f);
    [Tooltip("The floor where passers-by can see in from the hallway.")]
    [SerializeField] private Color seenFloorColor = new(0.3f, 0.7f, 1f, 0.22f);
    [Tooltip("Things that add to the appeal.")]
    [SerializeField] private Color goodColor = new(0.2f, 0.85f, 0.3f, 1f);
    [Tooltip("Things that take away from it.")]
    [SerializeField] private Color badColor = new(0.95f, 0.25f, 0.2f, 1f);

    [Header("Overlay")]
    [Tooltip("Size of the floor squares checked for being seen from the hallway (meters). Smaller is finer and slower.")]
    [SerializeField, Range(0.25f, 2f)] private float floorCell = 0.5f;
    [Tooltip("How high above the floor a spot is checked from (about where products sit).")]
    [SerializeField, Min(0.1f)] private float floorCheckHeight = 1f;

    [Header("Customers (later)")]
    [Tooltip("Whose view the panel shows. Empty: the everyday customer (every kind, averaged by how often they come).")]
    [SerializeField] private CustomerPersonality viewAs;

    private StoreEditController editor;
    private Layout layout;
    private StoreAppeal preview;
    private bool shown;
    private int drawnRevision = -1;
    private EditLocalItem drawnSelection;
    private Vector3 drawnSelectionAt;
    private bool hasBaseline;
    private float baseQuality, baseEnter, baseInterest;
    private int baseForSale, baseSeen;
    private float nextPanel;
    private string panelText = "";

    private Mesh lines, fills;
    private Material lineMaterial;
    private readonly List<TextMeshPro> tags = new();
    private System.Action drawImgui;

    /// <summary>Who the panel's numbers are for (null: the everyday customer). A hook for picking types later.</summary>
    public CustomerPersonality ViewAs { get => viewAs; set { viewAs = value; nextPanel = 0f; } }

    private void Awake() => editor = GetComponent<StoreEditController>();

    private void OnDisable() => Close(true);
    private void OnDestroy()
    {
        Close(true);
        if (lines != null) Destroy(lines);
        if (fills != null) Destroy(fills);
        if (lineMaterial != null) Destroy(lineMaterial);
        if (greyMaterial != null) Destroy(greyMaterial);
    }

    private void Update()
    {
        if (editor == null || !editor.IsOwner) return;
        if (!editor.IsEditing)
        {
            if (preview != null || shown) Close(true);
            return;
        }
        if (Input.GetKeyDown(ToggleKey) && !TypingSomewhere())
        {
            if (shown) Close(false);
            else Open();
        }
        if (!shown) return;

        layout.Poll();
        if (!layout.Busy)
        {
            if (preview.Revision != drawnRevision)
            {
                Rebuild();
                drawnRevision = preview.Revision;
                drawnSelection = null;
            }
            var sel = editor.SelectedEditCopy;
            if (sel != drawnSelection || (sel != null && sel.transform.position != drawnSelectionAt))
            {
                drawnSelection = sel;
                drawnSelectionAt = sel != null ? sel.transform.position : default;
                BuildSelection(sel);
            }
        }
        if (Time.unscaledTime >= nextPanel)
        {
            nextPanel = Time.unscaledTime + 0.25f;
            UpdatePanel();
        }
        Draw();
    }

    private static bool TypingSomewhere()
    {
        var es = UnityEngine.EventSystems.EventSystem.current;
        var go = es != null ? es.currentSelectedGameObject : null;
        return go != null && go.GetComponent<TMP_InputField>() != null;
    }

    /* ========================= OPEN / CLOSE ========================= */

    private void Open()
    {
        var plot = editor.EditPlot;
        if (plot == null) return;
        if (preview == null || preview.Plot != plot)
        {
            Close(true);
            layout = new Layout(editor);
            preview = StoreAppeal.CreatePreview(plot, layout);
            hasBaseline = false;
        }
        shown = true;
        drawnRevision = -1;
        drawnSelection = null;
        nextPanel = 0f;
        StockBatcher.HideEditView = true; // the shelves are colored, not their products
        GreyStore();
    }

    // Hide (the preview's kept for this edit, so the ▲/▼ still count from the first look), or end it.
    private void Close(bool end)
    {
        shown = false;
        StockBatcher.HideEditView = false;
        RestoreGrey();
        foreach (var t in tags) if (t != null) t.gameObject.SetActive(false);
        foreach (var t in nearTags) if (t != null) t.gameObject.SetActive(false);
        drawnSelection = null;
        var panel = EditAppealPanel.Instance;
        if (panel != null) panel.Hide();
        ImGuiHost.Set(this, null);
        if (!end) return;
        if (preview != null) Destroy(preview.gameObject);
        preview = null;
        layout = null;
        hasBaseline = false;
        foreach (var t in tags) if (t != null) Destroy(t.gameObject);
        tags.Clear();
        foreach (var t in nearTags) if (t != null) Destroy(t.gameObject);
        nearTags.Clear();
    }

    /* ========================= GREY ========================= */

    // Everything in the store (walls, floor, furniture, the edit copies) in one plain grey while the view's
    // on, put back as it was after. Each renderer's own materials and paint (property block) are kept.
    private readonly Dictionary<Renderer, (Material[] materials, MaterialPropertyBlock block)> greyed = new();
    private Material greyMaterial;
    private readonly Dictionary<int, Material[]> greyArrays = new();

    private void GreyStore()
    {
        var plot = editor.EditPlot;
        if (plot == null) return;
        if (greyMaterial == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (shader == null) return;
            greyMaterial = new Material(shader) { name = "Appeal View Grey", hideFlags = HideFlags.DontSave };
            if (greyMaterial.HasProperty("_BaseColor")) greyMaterial.SetColor("_BaseColor", greyColor);
            if (greyMaterial.HasProperty("_Color")) greyMaterial.SetColor("_Color", greyColor);
            if (greyMaterial.HasProperty("_Smoothness")) greyMaterial.SetFloat("_Smoothness", 0.1f);
        }
        var bounds = plot.GetWorldBounds();
        bounds.Expand(new Vector3(1.5f, 0.5f, 1.5f));
        EditLocalItem.RealMaterials = RealMaterialsOf;
        foreach (var r in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
            if (greyed.ContainsKey(r))
            {
                // Put back to its own look meanwhile (an edit copy un-faded after a drag): grey again.
                var now = r.sharedMaterials;
                var copy = r.GetComponentInParent<EditLocalItem>();
                if (now.Length > 0 && now[0] != greyMaterial && (copy == null || !copy.Faded))
                    r.sharedMaterials = GreyArray(now.Length);
                continue;
            }
            if (r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer || r is SpriteRenderer) continue;
            if (r.GetComponent<TMP_Text>() != null || r.GetComponentInParent<CharacterController>() != null) continue;
            if (!bounds.Intersects(r.bounds)) continue;
            var mats = r.sharedMaterials;
            MaterialPropertyBlock block = null;
            if (r.HasPropertyBlock())
            {
                block = new MaterialPropertyBlock();
                r.GetPropertyBlock(block);
            }
            greyed[r] = (mats, block);
            r.sharedMaterials = GreyArray(mats.Length);
            r.SetPropertyBlock(null);
        }
    }

    private Material[] GreyArray(int length)
    {
        if (!greyArrays.TryGetValue(length, out var grey))
        {
            grey = new Material[length];
            for (int i = 0; i < grey.Length; i++) grey[i] = greyMaterial;
            greyArrays[length] = grey;
        }
        return grey;
    }

    private Material[] RealMaterialsOf(Renderer r) => greyed.TryGetValue(r, out var v) ? v.materials : null;

    private void RestoreGrey()
    {
        if (EditLocalItem.RealMaterials == (System.Func<Renderer, Material[]>)RealMaterialsOf) EditLocalItem.RealMaterials = null;
        foreach (var kv in greyed)
        {
            var r = kv.Key;
            if (r == null) continue;
            r.sharedMaterials = kv.Value.materials;
            r.SetPropertyBlock(kv.Value.block);
        }
        greyed.Clear();
    }

    /* ========================= PANEL ========================= */

    private void UpdatePanel()
    {
        if (preview == null) return;
        float quality = preview.Quality;
        int forSale = preview.ForSale, seen = preview.SeenFromOutside;
        var r = viewAs != null ? preview.For(viewAs, true) : preview.ForAverage(true);
        bool ready = preview.Revision > 0;
        if (ready && !hasBaseline)
        {
            hasBaseline = true;
            baseQuality = quality; baseEnter = r.enterChance; baseInterest = r.interest;
            baseForSale = forSale; baseSeen = seen;
        }

        var sb = new System.Text.StringBuilder();
        sb.Append("<b>STORE APPEAL</b>   <size=70%>(").Append(ToggleKey).Append(": hide)</size>\n");
        if (!ready) sb.Append("Working it out...\n");
        sb.Append($"Quality: <b>{quality * 100f:0}%</b>{Delta(quality - baseQuality)}\n");
        sb.Append($"For sale: {forSale}{Delta(forSale - baseForSale, false)}   seen from the hallway: <b>{seen}</b>{Delta(seen - baseSeen, false)}\n");
        string who = viewAs != null ? viewAs.Name : "Customers";
        sb.Append($"{who}: walk in <b>{r.enterChance * 100f:0}%</b>{Delta(r.enterChance - baseEnter)}   " +
                  $"want what's here <b>{r.interest * 100f:0}%</b>{Delta(r.interest - baseInterest)}\n");
        var hints = preview.Hints;
        if (hints.Count > 0)
        {
            sb.Append("<size=85%>");
            foreach (var h in hints) sb.Append("• ").Append(h).Append('\n');
            sb.Append("</size>");
        }
        sb.Append("<size=70%><color=#5f5>Green</color>: adds to the appeal   <color=#f55>red</color>: takes away   grey: neither\n" +
                  "<color=#4af>Blue floor</color>: seen from the hallway   Click an item for its numbers</size>");
        sb.Append(selectedText);
        panelText = sb.ToString();

        var panel = EditAppealPanel.Instance;
        if (panel != null)
        {
            ImGuiHost.Set(this, null);
            panel.Show(panelText, quality);
        }
        else ImGuiHost.Set(this, drawImgui ??= DrawImgui);
    }

    private string Delta(float d, bool percent = true)
    {
        if (!hasBaseline) return "";
        float shown = percent ? d * 100f : d;
        if (Mathf.Abs(shown) < 0.5f) return "";
        return shown > 0f ? $" <color=#5f5>+{shown:0}</color>" : $" <color=#f66>-{-shown:0}</color>";
    }

    private void DrawImgui()
    {
        string plain = System.Text.RegularExpressions.Regex.Replace(panelText, "<.*?>", "");
        GUILayout.BeginArea(new Rect(Screen.width - 430, 10, 420, 400), GUI.skin.box);
        GUILayout.Label(plain);
        GUILayout.EndArea();
    }

    /* ========================= OVERLAY ========================= */

    private readonly List<StoreAppeal.OfferView> offers = new();
    private readonly List<(string feature, float amount, Vector3 position)> decor = new();
    // The store-wide part (floor shading), and the selected item's part (lines to what it's linked with).
    private readonly List<Vector3> baseLines = new(), baseFills = new(), nearLines = new(), nearFills = new();
    private readonly List<Color> baseLineColors = new(), baseFillColors = new(), nearLineColors = new(), nearFillColors = new();
    private readonly List<TextMeshPro> nearTags = new();
    private readonly List<Bounds> areaBuffer = new();

    // Something in the store that adds to or takes from its appeal: a shelf or thing for sale, a piece of
    // decor, a window or door, a delivery box. Its score is the sum of its links (in appeal points).
    private sealed class Thing
    {
        public Transform root;
        public string name;
        public Bounds bounds;
        public float score;
        public readonly List<(Thing other, float points, string why)> links = new();
    }

    private readonly Dictionary<Transform, Thing> things = new();
    private readonly List<Thing> thingList = new();

    private void Rebuild()
    {
        preview.GetOffers(offers);
        preview.GetDecor(decor);
        baseLines.Clear(); baseLineColors.Clear(); baseFills.Clear(); baseFillColors.Clear();
        GreyStore(); // anything new (a window just put in) goes grey too
        ShadeSeenFloor();
        Score();
        ColorThings();
        PlacePoints();
        BuildMeshes();
    }

    /* ---- working out what goes well together ---- */

    // Points: how a link shows in the numbers (a full "need" met is worth Points Per Need, a pair's effect
    // of 1 is Points Per Pair).
    [Header("Points")]
    [Tooltip("A piece of decor a category needs, in reach: up to this many points (times the need's importance).")]
    [SerializeField, Min(0f)] private float pointsPerNeed = 10f;
    [Tooltip("Two things that go together (a pair with effect 1) in reach: this many points (negative effects take away).")]
    [SerializeField, Min(0f)] private float pointsPerPair = 10f;
    [Tooltip("Something for sale passers-by can see from the hallway.")]
    [SerializeField, Min(0f)] private float pointsSeen = 2f;
    [Tooltip("Points at which an item is fully green (or fully red, below zero).")]
    [SerializeField, Min(1f)] private float fullColorAt = 10f;

    [Header("Points above items")]
    [Tooltip("Show each item's points above it (+12, -4).")]
    [SerializeField] private bool showPoints = true;
    [Tooltip("Size of the numbers (they grow with distance so they stay readable).")]
    [SerializeField, Min(0.1f)] private float pointsSize = 1f;
    [Tooltip("Height above the top of the item, in meters.")]
    [SerializeField, Min(0f)] private float pointsHeight = 0.25f;

    private void Score()
    {
        things.Clear();
        thingList.Clear();
        var settings = StoreAppealSettings.Instance;
        if (settings == null) return;

        // What's for sale, by the thing it's on (each shelf spot counts on its own).
        var spotOf = new List<(Thing thing, StoreAppeal.OfferView offer)>();
        foreach (var o in offers)
        {
            var t = ThingOf(o.key.thing as Component);
            if (t != null) spotOf.Add((t, o));
        }
        // Decor (windows too), by the thing it's on.
        var decorOf = new List<(Thing thing, string feature, float amount, Vector3 at)>();
        foreach (var (feature, amount, position) in decor)
        {
            var t = ThingAt(position);
            if (t != null) decorOf.Add((t, feature, amount, position));
        }
        // Delivery boxes.
        var boxOf = new List<(Thing thing, Vector3 at)>();
        foreach (var b in preview.Boxes)
        {
            var t = BoxAt(b);
            if (t != null) boxOf.Add((t, b));
        }

        float decorReach2 = settings.decorReach * settings.decorReach, pairReach2 = settings.pairReach * settings.pairReach;
        var anyPairs = settings.PairsFor(StoreAppealSettings.AnyCategory);
        var pairs = new List<(string other, float effect, int wanted)>();

        // A shelf's spots share its points (a shelf of 10 spots next to a mirror gets the mirror's points once,
        // not ten times).
        var spots = new Dictionary<Thing, int>();
        foreach (var (t, _) in spotOf) { spots.TryGetValue(t, out int n); spots[t] = n + 1; }

        foreach (var (thing, o) in spotOf)
        {
            float share = 1f / spots[thing];
            if (o.seenFromOutside && pointsSeen > 0f) Link(thing, null, pointsSeen * share, "seen from the hallway");
            foreach (var cat in o.def.Categories)
            {
                // Decor it needs: each piece in reach adds; none at all takes away only with Missing Decor Hurts.
                var needs = settings.NeedsFor(cat);
                if (needs != null)
                    foreach (var need in needs.needs)
                    {
                        if (need == null || string.IsNullOrWhiteSpace(need.feature) || need.wanted <= 0f) continue;
                        string f = need.feature.Trim();
                        bool any = false;
                        foreach (var (dt, feature, amount, at) in decorOf)
                        {
                            if (!string.Equals(feature, f, System.StringComparison.OrdinalIgnoreCase) || Flat2(at, o.position) > decorReach2) continue;
                            any = true;
                            Link(thing, dt, share * pointsPerNeed * need.importance * Mathf.Clamp01(amount / need.wanted), $"{cat} needs a {f}");
                        }
                        if (!any && settings.missingDecorHurts) Link(thing, null, -share * pointsPerNeed * need.importance * 0.5f, $"{cat} needs a {f} near it");
                    }

                // Things it goes with (or clashes with) in reach, and delivery boxes.
                pairs.Clear();
                var own = settings.PairsFor(cat);
                if (own != null) pairs.AddRange(own);
                if (anyPairs != null && !string.Equals(cat, StoreAppealSettings.AnyCategory, System.StringComparison.OrdinalIgnoreCase)) pairs.AddRange(anyPairs);
                foreach (var (other, effect, _) in pairs)
                {
                    if (effect == 0f) continue;
                    if (string.Equals(other, StoreAppealSettings.ShipmentCategory, System.StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var (bt, at) in boxOf)
                            if (Flat2(at, o.position) <= pairReach2 && Mathf.Abs(at.y - o.position.y) <= 3f)
                                Link(thing, bt, share * pointsPerPair * effect, effect > 0f ? $"{cat} goes with delivery boxes" : $"{cat} next to delivery boxes");
                        continue;
                    }
                    bool anyCat = string.Equals(other, StoreAppealSettings.AnyCategory, System.StringComparison.OrdinalIgnoreCase);
                    foreach (var (nt, n) in spotOf)
                    {
                        if (nt == thing || (!anyCat && !n.def.HasCategory(other))) continue;
                        if (Flat2(n.position, o.position) > pairReach2 || Mathf.Abs(n.position.y - o.position.y) > 3f) continue;
                        Link(thing, nt, share * pointsPerPair * effect, effect > 0f ? $"{cat} goes with {(anyCat ? n.def.displayName : other)}"
                                                                           : $"{cat} clashes with {(anyCat ? n.def.displayName : other)}");
                    }
                }
            }
        }

        // Merge links to the same thing for the same reason, and add up the scores.
        foreach (var t in thingList)
        {
            var merged = new Dictionary<(Thing, string), float>();
            foreach (var (other, points, why) in t.links)
            {
                merged.TryGetValue((other, why), out float have);
                merged[(other, why)] = have + points;
            }
            t.links.Clear();
            t.score = 0f;
            foreach (var kv in merged)
            {
                t.links.Add((kv.Key.Item1, kv.Value, kv.Key.Item2));
                t.score += kv.Value;
            }
            t.links.Sort((a, b) => Mathf.Abs(b.points).CompareTo(Mathf.Abs(a.points)));
        }
    }

    private static float Flat2(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return dx * dx + dz * dz;
    }

    // Both ways: the item gets it, and so does what it's linked with (the mirror helping the shirts is good
    // for the mirror too). With no other thing: just the item.
    private static void Link(Thing a, Thing b, float points, string why)
    {
        if (Mathf.Abs(points) < 0.01f) return;
        a.links.Add((b, points, why));
        if (b != null && b != a) b.links.Add((a, points, why));
    }

    // The edit copy (or real thing) a real component is drawn as.
    private Thing ThingOf(Component real)
    {
        if (real == null) return null;
        Transform root = real.transform;
        string name = real.name;
        if (editor.TryGetEditCopy(real, out var copy))
        {
            if (copy == null) return null;
            root = copy.transform;
            if (copy.Def != null) name = copy.Def.displayName;
        }
        else
        {
            var item = real.GetComponentInParent<ItemPickupNet>();
            if (item != null) root = item.transform;
            if (item != null && ItemCatalog.Instance != null && ItemCatalog.Instance.TryGet(item.ItemId, out var def)) name = def.displayName;
        }
        return ThingFor(root, name);
    }

    private Thing ThingFor(Transform root, string name)
    {
        if (root == null) return null;
        if (things.TryGetValue(root, out var t)) return t;
        t = new Thing { root = root, name = name };
        TryRendererBounds(root, out t.bounds);
        things[root] = t;
        thingList.Add(t);
        return t;
    }

    // The thing a piece of decor sits on: the smallest edit copy (or window, door) whose model is around
    // the spot.
    private Thing ThingAt(Vector3 at)
    {
        Transform best = null;
        float bestSize = float.MaxValue;
        string bestName = null;
        void Try(Transform root, string name)
        {
            if (root == null || !TryRendererBounds(root, out var b)) return;
            b.Expand(0.2f);
            if (!b.Contains(at)) return;
            float size = b.size.x * b.size.y * b.size.z;
            if (size < bestSize) { bestSize = size; best = root; bestName = name; }
        }
        foreach (var c in editor.EditCopies) if (c != null) Try(c.transform, c.Def != null ? c.Def.displayName : c.name);
        foreach (var c in editor.AddedEditCopies) if (c != null) Try(c.transform, c.Def != null ? c.Def.displayName : c.name);
        if (best == null)
            foreach (var o in WallOpening.All)
                if (o != null && !editor.TryGetEditCopy(o, out _)) Try(o.transform, o.name);
        return best != null ? ThingFor(best, bestName) : null;
    }

    private Thing BoxAt(Vector3 at)
    {
        foreach (var item in ItemPickupNet.All)
            if (item != null && item.TryGetComponent<ShipmentPackage>(out _) && (item.transform.position - at).sqrMagnitude < 0.01f)
                return ThingFor(item.transform, "Delivery box");
        return null;
    }

    /* ---- showing it ---- */

    private MaterialPropertyBlock tint;

    // Every greyed renderer back to plain grey, then each thing tinted by its score: green good, red bad.
    private void ColorThings()
    {
        foreach (var kv in greyed) if (kv.Key != null) kv.Key.SetPropertyBlock(null);
        tint ??= new MaterialPropertyBlock();
        foreach (var t in thingList)
        {
            if (Mathf.Abs(t.score) < 0.5f || t.root == null) continue;
            float k = Mathf.Clamp01(Mathf.Abs(t.score) / fullColorAt);
            var c = Color.Lerp(greyColor, t.score > 0f ? goodColor : badColor, Mathf.Lerp(0.35f, 1f, k));
            tint.Clear();
            tint.SetColor("_BaseColor", c);
            tint.SetColor("_Color", c);
            foreach (var r in t.root.GetComponentsInChildren<Renderer>())
                if (greyed.ContainsKey(r)) r.SetPropertyBlock(tint);
        }
    }

    // Each thing's points above it (the tags pool).
    private void PlacePoints()
    {
        int used = 0;
        if (showPoints)
            foreach (var t in thingList)
            {
                if (Mathf.Abs(t.score) < 0.5f || t.root == null) continue;
                var tag = PointsTag(used++);
                tag.text = t.score > 0f ? $"+{t.score:0}" : $"-{-t.score:0}";
                tag.color = t.score > 0f ? goodColor : badColor;
                tag.transform.position = new Vector3(t.bounds.center.x, t.bounds.max.y + pointsHeight, t.bounds.center.z);
                tag.gameObject.SetActive(true);
            }
        for (int i = used; i < tags.Count; i++) if (tags[i] != null) tags[i].gameObject.SetActive(false);
    }

    private TextMeshPro PointsTag(int i)
    {
        while (tags.Count <= i)
        {
            var go = new GameObject("Appeal Points");
            var t = go.AddComponent<TextMeshPro>();
            t.alignment = TextAlignmentOptions.Center;
            t.fontSize = 4f;
            t.fontStyle = FontStyles.Bold;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.outlineWidth = 0.2f;
            t.outlineColor = new Color32(0, 0, 0, 255);
            t.rectTransform.sizeDelta = new Vector2(4f, 1f);
            tags.Add(t);
        }
        if (tags[i] == null)
        {
            tags.RemoveAt(i);
            return PointsTag(i);
        }
        return tags[i];
    }

    // The floor where passers-by can see in: one square per cell whose middle (at product height) they can see.
    private void ShadeSeenFloor()
    {
        var plot = editor.EditPlot;
        if (plot == null || preview.Viewers.Count == 0) return;
        areaBuffer.Clear();
        plot.GetAreaBounds(areaBuffer);
        float floor = plot.FloorHeight, cell = floorCell;
        float area = 0f;
        foreach (var b in areaBuffer) area += b.size.x * b.size.z;
        if (area / (cell * cell) > 4000f) cell = Mathf.Sqrt(area / 4000f);

        var done = new HashSet<Vector2Int>();
        foreach (var b in areaBuffer)
            for (float x = b.min.x + cell * 0.5f; x < b.max.x; x += cell)
                for (float z = b.min.z + cell * 0.5f; z < b.max.z; z += cell)
                {
                    var key = new Vector2Int(Mathf.RoundToInt(x / cell), Mathf.RoundToInt(z / cell));
                    if (!done.Add(key)) continue;
                    if (!plot.Contains(new Vector3(x, floor + 0.5f, z))) continue;
                    if (!preview.IsPointSeen(new Vector3(x, floor + floorCheckHeight, z))) continue;
                    float h = cell * 0.5f, y = floor + 0.03f;
                    Quad(baseFills, baseFillColors, new Vector3(x - h, y, z - h), new Vector3(x + h, y, z - h),
                         new Vector3(x + h, y, z + h), new Vector3(x - h, y, z + h), seenFloorColor);
                }
    }

    private static bool TryRendererBounds(Transform t, out Bounds b)
    {
        b = default;
        bool any = false;
        foreach (var r in t.GetComponentsInChildren<Renderer>(true))
        {
            if (r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer || r.GetComponent<TMP_Text>() != null) continue;
            if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
        }
        return any;
    }

    /* ---- the selected item: its numbers ---- */

    private string selectedText = "";

    // Lines from the selected item to what it's linked with (green good, red bad), and its numbers for the panel.
    private void BuildSelection(EditLocalItem sel)
    {
        nearLines.Clear(); nearLineColors.Clear(); nearFills.Clear(); nearFillColors.Clear();
        selectedText = "";
        Thing t = null;
        if (sel != null) things.TryGetValue(sel.transform, out t);
        if (sel != null && t == null)
            selectedText = $"\n<b>{(sel.Def != null ? sel.Def.displayName : sel.name)}</b>: doesn't add to or take from the appeal here.";
        if (t != null)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"\n<b>{t.name}</b>: <b>{Signed(t.score)}</b>\n<size=85%>");
            int shownLines = 0;
            foreach (var (other, points, why) in t.links)
            {
                if (shownLines++ >= 12) { sb.Append($"…and {t.links.Count - 12} more\n"); break; }
                string col = points > 0f ? "#5f5" : "#f66";
                sb.Append($"<color={col}>{Signed(points)}</color>  {why}{(other != null ? $" ({other.name})" : "")}\n");
                if (other != null)
                {
                    var c = points > 0f ? goodColor : badColor;
                    Line(nearLines, nearLineColors, t.bounds.center + Vector3.up * 0.3f, other.bounds.center + Vector3.up * 0.3f, c);
                }
            }
            sb.Append("</size>");
            selectedText = sb.ToString();
        }
        BuildMeshes();
        nextPanel = 0f;
    }

    private static string Signed(float v) => v >= 0f ? $"+{v:0.#}" : $"{v:0.#}";

    private void Draw()
    {
        if (lineMaterial == null)
        {
            var shader = Shader.Find("Sprites/Default");
            if (shader == null) return;
            lineMaterial = new Material(shader) { hideFlags = HideFlags.DontSave };
        }
        if (fills != null && fills.vertexCount > 0) Graphics.DrawMesh(fills, Matrix4x4.identity, lineMaterial, 0);
        if (lines != null && lines.vertexCount > 0) Graphics.DrawMesh(lines, Matrix4x4.identity, lineMaterial, 0);

        // Labels face the camera.
        var cam = Camera.main;
        if (cam == null) return;
        foreach (var pool in new[] { tags, nearTags })
            foreach (var t in pool)
                if (t != null && t.gameObject.activeSelf)
                {
                    t.transform.rotation = Quaternion.LookRotation(t.transform.position - cam.transform.position);
                    if (pool == tags)
                    {
                        float d = Vector3.Distance(cam.transform.position, t.transform.position);
                        t.transform.localScale = Vector3.one * (pointsSize * Mathf.Clamp(d / 10f, 0.4f, 3f));
                    }
                }
    }

    private readonly List<Vector3> meshVerts = new();
    private readonly List<Color> meshColors = new();

    private void BuildMeshes()
    {
        lines ??= new Mesh { name = "Appeal Lines", hideFlags = HideFlags.DontSave };
        fills ??= new Mesh { name = "Appeal Shading", hideFlags = HideFlags.DontSave };
        Fill(lines, baseLines, baseLineColors, nearLines, nearLineColors, MeshTopology.Lines);
        Fill(fills, baseFills, baseFillColors, nearFills, nearFillColors, MeshTopology.Triangles);
    }

    private void Fill(Mesh mesh, List<Vector3> a, List<Color> ac, List<Vector3> b, List<Color> bc, MeshTopology topology)
    {
        meshVerts.Clear(); meshColors.Clear();
        meshVerts.AddRange(a); meshColors.AddRange(ac);
        meshVerts.AddRange(b); meshColors.AddRange(bc);
        mesh.Clear();
        if (meshVerts.Count == 0) return;
        mesh.indexFormat = meshVerts.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.SetVertices(meshVerts);
        mesh.SetColors(meshColors);
        var idx = new int[meshVerts.Count];
        for (int i = 0; i < idx.Length; i++) idx[i] = i;
        mesh.SetIndices(idx, topology, 0);
        mesh.RecalculateBounds();
    }

    private static void Line(List<Vector3> verts, List<Color> colors, Vector3 a, Vector3 b, Color c)
    {
        verts.Add(a); verts.Add(b);
        colors.Add(c); colors.Add(c);
    }

    private static void Quad(List<Vector3> verts, List<Color> colors, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color)
    {
        verts.Add(a); verts.Add(b); verts.Add(c);
        verts.Add(a); verts.Add(c); verts.Add(d);
        for (int i = 0; i < 6; i++) colors.Add(color);
    }

    private static readonly Vector3[] Corners = new Vector3[8];

    private static void SetCorners(Vector3 center, Quaternion rotation, Vector3 size)
    {
        Vector3 h = size * 0.5f;
        for (int i = 0; i < 8; i++)
            Corners[i] = center + rotation * new Vector3((i & 1) == 0 ? -h.x : h.x, (i & 2) == 0 ? -h.y : h.y, (i & 4) == 0 ? -h.z : h.z);
    }

    private static void WireBox(List<Vector3> verts, List<Color> colors, Vector3 center, Quaternion rotation, Vector3 size, Color c)
    {
        SetCorners(center, rotation, size);
        // The 12 edges: corners differing in one bit.
        for (int i = 0; i < 8; i++)
            for (int bit = 1; bit < 8; bit <<= 1)
                if ((i & bit) == 0) Line(verts, colors, Corners[i], Corners[i | bit], c);
    }

    private static readonly int[][] Faces =
    {
        new[] { 0, 1, 3, 2 }, new[] { 4, 6, 7, 5 }, new[] { 0, 4, 5, 1 }, new[] { 2, 3, 7, 6 }, new[] { 0, 2, 6, 4 }, new[] { 1, 5, 7, 3 },
    };

    private static void SolidBox(List<Vector3> verts, List<Color> colors, Vector3 center, Quaternion rotation, Vector3 size, Color c)
    {
        SetCorners(center, rotation, size);
        foreach (var f in Faces) Quad(verts, colors, Corners[f[0]], Corners[f[1]], Corners[f[2]], Corners[f[3]], c);
    }

    /* ========================= THE LAYOUT (the edit copies) ========================= */

    // Watches the edit copies: what was moved (once let go), added or taken out.
    private sealed class Layout : IAppealLayout
    {
        private readonly StoreEditController editor;
        private readonly Dictionary<int, (Vector3 p, Quaternion r)> session = new();
        private readonly Dictionary<EditLocalItem, (Vector3 p, Quaternion r)> added = new();
        private readonly List<Component> moved = new();
        private bool openings;
        private float nextPoll;
        private bool first = true;

        public Layout(StoreEditController editor) => this.editor = editor;

        public int Version { get; private set; }
        public bool Busy => editor.EditBusy;

        public bool TryGetCopy(Component real, out Transform copy)
        {
            copy = null;
            if (!editor.TryGetEditCopy(real, out var c)) return false;
            copy = c != null ? c.transform : null;
            return true;
        }

        public void TakeChanges(List<Component> into, out bool openingsChanged)
        {
            into.Clear();
            into.AddRange(moved);
            moved.Clear();
            openingsChanged = openings;
            openings = false;
        }

        public void GetExtraDecor(List<(string feature, float amount, Vector3 position)> into)
        {
            into.Clear();
            foreach (var copy in editor.AddedEditCopies)
            {
                if (copy == null || copy.Def == null || copy.Def.worldNetworkPrefab == null) continue;
                var d = copy.Def.worldNetworkPrefab.GetComponent<Decor>();
                if (d == null) continue;
                foreach (var f in d.features)
                    if (f != null && !string.IsNullOrWhiteSpace(f.name)) into.Add((f.name.Trim(), f.amount, copy.transform.position));
            }
        }

        // A few times a second: compare every copy with where it was (only once nothing's being dragged).
        public void Poll()
        {
            if (Time.unscaledTime < nextPoll || editor.EditBusy) return;
            nextPoll = Time.unscaledTime + 0.2f;
            bool changed = false;

            var copies = editor.EditCopies;
            for (int i = 0; i < copies.Count; i++)
            {
                var c = copies[i];
                bool had = session.TryGetValue(i, out var was);
                if (c == null)
                {
                    if (!had) continue;
                    session.Remove(i); // taken out
                    changed |= Note(i);
                    continue;
                }
                var t = c.transform;
                if (had && (t.position - was.p).sqrMagnitude < 1e-6f && Quaternion.Angle(t.rotation, was.r) < 0.1f) continue;
                session[i] = (t.position, t.rotation);
                if (!first) { changed |= Note(i, c); if (c.Opening != null) openings = true; }
            }

            // Bought this edit (windows, doors, wall items): any change redoes everything.
            int count = 0;
            foreach (var c in editor.AddedEditCopies)
            {
                if (c == null) continue;
                count++;
                var t = c.transform;
                if (added.TryGetValue(c, out var was) && (t.position - was.p).sqrMagnitude < 1e-6f &&
                    Quaternion.Angle(t.rotation, was.r) < 0.1f) continue;
                added[c] = (t.position, t.rotation);
                if (!first) { openings = true; changed = true; }
            }
            if (count != added.Count)
            {
                var gone = new List<EditLocalItem>();
                foreach (var kv in added) if (kv.Key == null) gone.Add(kv.Key);
                foreach (var g in gone) added.Remove(g);
                openings = true;
                changed = true;
            }

            first = false;
            if (changed || openings) Version++;
        }

        private bool Note(int index, EditLocalItem copy = null)
        {
            Component real = copy != null ? editor.RealOf(copy) : editor.RealAt(index);
            if (real == null) return false;
            moved.Add(real);
            return true;
        }
    }
}
