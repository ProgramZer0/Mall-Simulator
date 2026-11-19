using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// The appeal view in store edit mode (V): how appealing the store is as you rearrange it, before saving.
///
/// - A panel (EditAppealPanel on the HUD, else a plain box): Quality, what's for sale and how much of it
///   is seen from the hallway, how the everyday customer sees the store (walks in, wants what's inside),
///   and tips. ▲/▼: the change since the view was first opened this edit.
/// - The floor is shaded where passers-by can see in from the hallway (through the windows and doors);
///   things for sale they can see get a green outline. Decor is labelled with what it counts as.
/// - Select an item: what's near it lights up from red (bad next to it) to green (good next to it): things
///   that go with it or clash with it (Category Pairs), and decor it needs (or that needs it).
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
    [Tooltip("Things for sale passers-by can see from the hallway.")]
    [SerializeField] private Color seenColor = new(0.15f, 0.55f, 1f, 0.55f);
    [Tooltip("Things for sale passers-by can't see.")]
    [SerializeField] private Color hiddenColor = new(1f, 0.6f, 0.15f, 0.55f);
    [SerializeField] private Color goodColor = new(0.2f, 1f, 0.35f, 1f);
    [SerializeField] private Color badColor = new(1f, 0.25f, 0.2f, 1f);
    [Tooltip("How see-through the red/green shading of things near the selected item is.")]
    [SerializeField, Range(0.05f, 1f)] private float nearAlpha = 0.35f;
    [SerializeField] private Color decorTagColor = new(1f, 0.9f, 0.5f, 1f);

    [Header("Overlay")]
    [Tooltip("Size of the floor squares checked for being seen from the hallway (meters). Smaller is finer and slower.")]
    [SerializeField, Range(0.25f, 2f)] private float floorCell = 0.5f;
    [Tooltip("How high above the floor a spot is checked from (about where products sit).")]
    [SerializeField, Min(0.1f)] private float floorCheckHeight = 1f;
    [SerializeField, Min(0.1f)] private float decorTagSize = 2.2f;
    [SerializeField] private float decorTagHeight = 2f;

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
        nextPanel = 0f;
        GreyStore();
    }

    // Hide (the preview's kept for this edit, so the ▲/▼ still count from the first look), or end it.
    private void Close(bool end)
    {
        shown = false;
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
        sb.Append("<size=70%><color=#4af>Blue</color>: seen from the hallway (floor and things for sale)   <color=#fa4>orange</color>: for sale, not seen\n" +
                  "Select an item: what's near it, <color=#f55>red</color> (bad) to <color=#5f5>green</color> (good)</size>");
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
        return shown > 0f ? $" <color=#5f5>▲{shown:0}</color>" : $" <color=#f66>▼{-shown:0}</color>";
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
    // The store-wide part (floor shading, seen outlines), and the selected item's part, built separately.
    private readonly List<Vector3> baseLines = new(), baseFills = new(), nearLines = new(), nearFills = new();
    private readonly List<Color> baseLineColors = new(), baseFillColors = new(), nearLineColors = new(), nearFillColors = new();
    private readonly List<TextMeshPro> nearTags = new();
    private readonly List<Bounds> areaBuffer = new();

    private void Rebuild()
    {
        preview.GetOffers(offers);
        preview.GetDecor(decor);
        baseLines.Clear(); baseLineColors.Clear(); baseFills.Clear(); baseFillColors.Clear();

        ShadeSeenFloor();

        // Things for sale: solid boxes, blue where passers-by can see them, orange where they can't.
        foreach (var o in offers)
            if (TryOfferBox(o, out var c, out var rot, out var size))
            {
                var color = o.seenFromOutside ? seenColor : hiddenColor;
                SolidBox(baseFills, baseFillColors, c, rot, size, color);
                color.a = 1f;
                WireBox(baseLines, baseLineColors, c, rot, size, color);
            }
        GreyStore(); // anything new (a window just put in) goes grey too

        BuildTags();
        BuildMeshes();
    }

    // The floor where passers-by can see in: one square per cell whose middle (at product height) they can see.
    private void ShadeSeenFloor()
    {
        var plot = editor.EditPlot;
        if (plot == null || preview.Viewers.Count == 0) return;
        areaBuffer.Clear();
        plot.GetAreaBounds(areaBuffer);
        float floor = plot.FloorHeight, cell = floorCell;
        // A big store: bigger squares, so it stays quick (at most about 4000 checks).
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

    // The box round a shelf spot or loose thing for sale (where its edit copy is).
    private bool TryOfferBox(StoreAppeal.OfferView o, out Vector3 center, out Quaternion rotation, out Vector3 size)
    {
        rotation = Quaternion.identity;
        if (o.key.thing is StockContainer container && container.TryGetZoneBox(o.key.zone, out center, out var rot, out size))
        {
            center = preview.MapPoint(container, center);
            rotation = preview.MapRotation(container, rot);
            return true;
        }
        if (o.key.thing is Component item && TryCopyBounds(item, out var b))
        {
            center = b.center;
            size = b.size;
            return true;
        }
        center = o.position + Vector3.up * 0.15f;
        size = Vector3.one * 0.3f;
        return true;
    }

    private bool TryCopyBounds(Component real, out Bounds b)
    {
        b = default;
        Transform t = real.transform;
        if (editor.TryGetEditCopy(real, out var copy)) { if (copy == null) return false; t = copy.transform; }
        return TryRendererBounds(t, out b);
    }

    private static bool TryRendererBounds(Transform t, out Bounds b)
    {
        b = default;
        bool any = false;
        foreach (var r in t.GetComponentsInChildren<Renderer>(true))
        {
            if (r is ParticleSystemRenderer) continue;
            if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
        }
        return any;
    }

    /* ---- the selected item: what's near it, red to green ---- */

    private readonly HashSet<string> selCategories = new(System.StringComparer.OrdinalIgnoreCase);
    private readonly List<string> selFeatures = new();
    private readonly List<Vector3> selSpots = new();

    private void BuildSelection(EditLocalItem sel)
    {
        nearLines.Clear(); nearLineColors.Clear(); nearFills.Clear(); nearFillColors.Clear();
        int tagCount = 0;
        var settings = StoreAppealSettings.Instance;
        if (sel != null && sel.Opening == null && settings != null)
        {
            // What it sells (its shelf spots, or itself) and what decor it is.
            var real = editor.RealOf(sel);
            selCategories.Clear(); selFeatures.Clear(); selSpots.Clear();
            var mine = new HashSet<int>();
            for (int i = 0; i < offers.Count; i++)
            {
                if (real == null || !(offers[i].key.thing is Component t) || t == null) continue;
                if (t.gameObject != real.gameObject && !t.transform.IsChildOf(real.transform)) continue;
                mine.Add(i);
                selSpots.Add(offers[i].position);
                foreach (var c in offers[i].def.Categories) selCategories.Add(c);
            }
            if (selSpots.Count == 0) selSpots.Add(sel.transform.position);
            Decor d = real != null ? real.GetComponentInChildren<Decor>(true) : null;
            if (d == null && sel.Def != null && sel.Def.worldNetworkPrefab != null) d = sel.Def.worldNetworkPrefab.GetComponentInChildren<Decor>(true);
            if (d != null) foreach (var f in d.features) if (f != null && !string.IsNullOrWhiteSpace(f.name)) selFeatures.Add(f.name.Trim());

            var anyPairs = settings.PairsFor(StoreAppealSettings.AnyCategory);
            // Things for sale nearby that go with it, or clash.
            for (int j = 0; j < offers.Count; j++)
            {
                if (mine.Contains(j)) continue;
                var n = offers[j];
                float score = 0f;
                string why = null;
                if (selCategories.Count > 0 && Near(n.position, settings.pairReach, true))
                {
                    foreach (var cat in selCategories)
                    {
                        score += PairEffect(settings, cat, n.def, anyPairs, ref why);
                        foreach (var other in n.def.Categories) score += PairEffect(settings, other, cat, ref why);
                    }
                }
                // It's decor this one needs.
                if (selFeatures.Count > 0 && Near(n.position, settings.decorReach, false))
                    foreach (var f in selFeatures)
                        if (Needs(settings, n.def, f)) { score += 1f; why ??= $"needs {f}"; }
                if (Mathf.Abs(score) < 0.01f || !TryOfferBox(n, out var c, out var rot, out var size)) continue;
                Shade(c, rot, size, score);
                NearTag(ref tagCount, c + Vector3.up * (size.y * 0.5f + 0.3f), $"{(score > 0f ? "✓" : "✗")} {n.def.displayName}", score);
            }
            // Decor nearby it needs.
            if (selCategories.Count > 0)
                foreach (var (feature, _, position) in decor)
                {
                    if (!Near(position, settings.decorReach, false)) continue;
                    bool needed = false;
                    foreach (var cat in selCategories)
                    {
                        var needs = settings.NeedsFor(cat);
                        if (needs == null) continue;
                        foreach (var need in needs.needs)
                            if (need != null && string.Equals(need.feature?.Trim(), feature, System.StringComparison.OrdinalIgnoreCase)) needed = true;
                    }
                    if (!needed) continue;
                    Shade(position + Vector3.up * 0.5f, Quaternion.identity, new Vector3(0.6f, 1f, 0.6f), 1f);
                    NearTag(ref tagCount, position + Vector3.up * 1.3f, $"✓ {feature}", 1f);
                }
        }
        for (int i = tagCount; i < nearTags.Count; i++) nearTags[i].gameObject.SetActive(false);
        BuildMeshes();
    }

    // Within reach (across the floor) of the selected item's spots; Same Floor: and not far above or below.
    private bool Near(Vector3 p, float reach, bool sameFloor)
    {
        float r2 = reach * reach;
        foreach (var s in selSpots)
        {
            float dx = p.x - s.x, dz = p.z - s.z;
            if (dx * dx + dz * dz <= r2 && (!sameFloor || Mathf.Abs(p.y - s.y) <= 3f)) return true;
        }
        return false;
    }

    // How much a category likes having this item near it (its Category Pairs, "Any" ones too).
    private static float PairEffect(StoreAppealSettings settings, string category, ItemDef near,
                                    List<(string other, float effect, int wanted)> anyPairs, ref string why)
    {
        float sum = 0f;
        var own = settings.PairsFor(category);
        if (own != null)
            foreach (var (other, effect, _) in own)
                if (IsPairCategory(near, other)) { sum += effect; why ??= other; }
        if (anyPairs != null && !string.Equals(category, StoreAppealSettings.AnyCategory, System.StringComparison.OrdinalIgnoreCase))
            foreach (var (other, effect, _) in anyPairs)
                if (IsPairCategory(near, other)) { sum += effect; why ??= other; }
        return sum;
    }

    // How much Category likes being near Other (one way).
    private static float PairEffect(StoreAppealSettings settings, string category, string other, ref string why)
    {
        float sum = 0f;
        var own = settings.PairsFor(category);
        if (own != null)
            foreach (var (o, effect, _) in own)
                if (string.Equals(o, other, System.StringComparison.OrdinalIgnoreCase)) { sum += effect; why ??= o; }
        return sum;
    }

    private static bool IsPairCategory(ItemDef def, string category) =>
        !string.Equals(category, StoreAppealSettings.ShipmentCategory, System.StringComparison.OrdinalIgnoreCase) &&
        (string.Equals(category, StoreAppealSettings.AnyCategory, System.StringComparison.OrdinalIgnoreCase) || def.HasCategory(category));

    private static bool Needs(StoreAppealSettings settings, ItemDef def, string feature)
    {
        foreach (var cat in def.Categories)
        {
            var needs = settings.NeedsFor(cat);
            if (needs == null) continue;
            foreach (var need in needs.needs)
                if (need != null && string.Equals(need.feature?.Trim(), feature, System.StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // A see-through box from red (bad) to green (good), with an outline.
    private void Shade(Vector3 center, Quaternion rotation, Vector3 size, float score)
    {
        float t = Mathf.Clamp01((Mathf.Clamp(score, -1f, 1f) + 1f) * 0.5f);
        var color = Color.Lerp(badColor, goodColor, t);
        WireBox(nearLines, nearLineColors, center, rotation, size, color);
        color.a = nearAlpha;
        SolidBox(nearFills, nearFillColors, center, rotation, size, color);
    }

    private void NearTag(ref int count, Vector3 at, string text, float score)
    {
        var tag = count < nearTags.Count ? nearTags[count] : NewTag(nearTags);
        count++;
        tag.gameObject.SetActive(true);
        tag.text = text;
        tag.color = score > 0f ? goodColor : badColor;
        tag.transform.position = at;
    }

    private void BuildTags()
    {
        // One label per spot (several features on one thing go together).
        var bySpot = new Dictionary<Vector3Int, (Vector3 at, string text)>();
        foreach (var (feature, amount, position) in decor)
        {
            var key = Vector3Int.RoundToInt(position * 10f);
            string part = amount == 1f ? feature : $"{feature} ×{amount:0.#}";
            bySpot[key] = bySpot.TryGetValue(key, out var have) ? (have.at, have.text + "\n" + part) : (position, part);
        }
        int n = 0;
        foreach (var kv in bySpot)
        {
            var tag = n < tags.Count ? tags[n] : NewTag(tags);
            tag.gameObject.SetActive(true);
            tag.text = kv.Value.text;
            tag.color = decorTagColor;
            tag.transform.position = kv.Value.at + Vector3.up * decorTagHeight;
            n++;
        }
        for (int i = n; i < tags.Count; i++) tags[i].gameObject.SetActive(false);
    }

    private TextMeshPro NewTag(List<TextMeshPro> pool)
    {
        var go = new GameObject("Appeal Tag") { hideFlags = HideFlags.DontSave };
        var t = go.AddComponent<TextMeshPro>();
        t.alignment = TextAlignmentOptions.Center;
        t.fontSize = decorTagSize;
        t.color = decorTagColor;
        t.outlineWidth = 0.25f;
        t.fontStyle = FontStyles.Bold;
        t.rectTransform.sizeDelta = new Vector2(4f, 1f);
        t.isOverlay = true;
        pool.Add(t);
        return t;
    }

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
                    t.transform.rotation = Quaternion.LookRotation(t.transform.position - cam.transform.position);
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
