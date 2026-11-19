using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// The appeal view in store edit mode (V): how appealing the store is as you rearrange it, before saving.
///
/// - A panel (EditAppealPanel on the HUD, else a plain box): Quality, what's for sale and how much of it
///   is seen from the hallway, how the everyday customer sees the store (walks in, wants what's inside),
///   and tips. ▲/▼: the change since the view was first opened this edit.
/// - In the store: products seen from the hallway outlined green, the rest grey; what passers-by can see
///   through each window and door shaded on the floor; decor labelled with what it counts as; things for
///   sale that go well together joined by green lines, badly by red ones.
///
/// Worked out from the edit copies (StoreAppeal.CreatePreview), only for what moved, and never while
/// something's being dragged. Local to the editor. Added to the player by StoreEditController.
/// </summary>
public class EditAppealView : MonoBehaviour
{
    public const KeyCode ToggleKey = KeyCode.V;

    [Header("Colors")]
    [SerializeField] private Color seenColor = new(0.2f, 1f, 0.35f, 1f);
    [SerializeField] private Color hiddenColor = new(0.6f, 0.6f, 0.6f, 0.6f);
    [SerializeField] private Color viewColor = new(0.3f, 0.75f, 1f, 0.18f);
    [SerializeField] private Color viewEdgeColor = new(0.3f, 0.75f, 1f, 0.7f);
    [SerializeField] private Color goodPairColor = new(0.2f, 1f, 0.35f, 0.9f);
    [SerializeField] private Color badPairColor = new(1f, 0.3f, 0.25f, 0.9f);
    [SerializeField] private Color decorTagColor = new(1f, 0.9f, 0.5f, 1f);

    [Header("Overlay")]
    [Tooltip("How far into the store the window and door views are drawn (meters).")]
    [SerializeField, Min(1f)] private float viewLength = 12f;
    [Tooltip("At most this many neighbor lines (the closest pairs first).")]
    [SerializeField, Min(0)] private int maxPairLines = 300;
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
        if (preview.Revision != drawnRevision && !layout.Busy)
        {
            Rebuild();
            drawnRevision = preview.Revision;
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
    }

    // Hide (the preview's kept for this edit, so the ▲/▼ still count from the first look), or end it.
    private void Close(bool end)
    {
        shown = false;
        foreach (var t in tags) if (t != null) t.gameObject.SetActive(false);
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
        sb.Append("<size=70%><color=#5f5>green</color>: seen from the hallway, <color=#999>grey</color>: not   " +
                  "<color=#6bf>blue</color>: what the windows and doors show   lines: good / bad neighbors</size>");
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
    private readonly List<Vector3> lineVerts = new();
    private readonly List<Color> lineColors = new();
    private readonly List<Vector3> fillVerts = new();
    private readonly List<Color> fillColors = new();

    private void Rebuild()
    {
        preview.GetOffers(offers);
        preview.GetDecor(decor);
        lineVerts.Clear(); lineColors.Clear(); fillVerts.Clear(); fillColors.Clear();

        // Seen from the hallway: a box round each shelf spot and loose thing for sale.
        foreach (var o in offers)
        {
            var color = o.seenFromOutside ? seenColor : hiddenColor;
            if (o.key.thing is StockContainer container && container.TryGetZoneBox(o.key.zone, out var c, out var rot, out var size))
                WireBox(preview.MapPoint(container, c), preview.MapRotation(container, rot), size, color);
            else if (o.key.thing is Component item && TryCopyBounds(item, out var b))
                WireBox(b.center, Quaternion.identity, b.size, color);
            else
                WireBox(o.position + Vector3.up * 0.15f, Quaternion.identity, Vector3.one * 0.3f, color);
        }

        AddViews();
        AddPairs();
        BuildMeshes();
        BuildTags();
    }

    private bool TryCopyBounds(Component real, out Bounds b)
    {
        b = default;
        Transform t = real.transform;
        if (editor.TryGetEditCopy(real, out var copy)) { if (copy == null) return false; t = copy.transform; }
        bool any = false;
        foreach (var r in t.GetComponentsInChildren<Renderer>(true))
        {
            if (r is ParticleSystemRenderer) continue;
            if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
        }
        return any;
    }

    // What passers-by see through each window and door: a wedge on the floor from the opening into the store.
    private void AddViews()
    {
        var plot = editor.EditPlot;
        var settings = StoreAppealSettings.Instance;
        float distance = settings != null ? settings.viewerDistance : 2f;
        float height = settings != null ? settings.viewerHeight : 1.6f;
        float floor = plot.FloorHeight + 0.03f;
        foreach (var o in WallOpening.All)
        {
            if (o == null) continue;
            if (!o.IsEditCopy && editor.TryGetEditCopy(o, out _)) continue; // its copy is drawn instead
            var host = CuttableWall.HostOf(o);
            if (host == null || host.Plot != plot || !host.IsStorefront) continue;

            var shape = o.Current;
            Vector3 a = shape.center + shape.forward * distance, b = shape.center - shape.forward * distance;
            Vector3 eye = !plot.Contains(new Vector3(a.x, plot.FloorHeight + 0.5f, a.z)) ? a : b;
            eye.y = plot.FloorHeight + height;
            Vector3 inward = (shape.center - eye); inward.y = 0f;
            if (inward.sqrMagnitude < 1e-4f) continue;
            inward.Normalize();

            Vector3 half = shape.right * (shape.size.x * 0.5f);
            Vector3 l = shape.center - half, r = shape.center + half;
            l.y = r.y = floor;
            Vector3 eyeFlat = new(eye.x, floor, eye.z);
            Vector3 dl = l - eyeFlat, dr = r - eyeFlat;
            if (dl.sqrMagnitude < 1e-4f || dr.sqrMagnitude < 1e-4f) continue;
            Vector3 lf = l + dl.normalized * viewLength, rf = r + dr.normalized * viewLength;
            Tri(l, r, rf, viewColor);
            Tri(l, rf, lf, viewColor);
            Line(l, lf, viewEdgeColor);
            Line(r, rf, viewEdgeColor);
            Line(l, r, viewEdgeColor);
        }
    }

    // Things for sale near something they go with (green) or clash with (red), within Pair Reach.
    private void AddPairs()
    {
        var settings = StoreAppealSettings.Instance;
        if (settings == null || settings.categoryPairs.Count == 0 || maxPairLines <= 0) return;
        float reach2 = settings.pairReach * settings.pairReach;
        var anyPairs = settings.PairsFor(StoreAppealSettings.AnyCategory);
        var found = new List<(float d2, int i, int j, bool good)>();
        var both = new List<(string other, float effect, int wanted)>();
        for (int i = 0; i < offers.Count; i++)
        {
            var o = offers[i];
            foreach (var cat in o.def.Categories)
            {
                both.Clear();
                var own = settings.PairsFor(cat);
                if (own != null) both.AddRange(own);
                if (anyPairs != null) both.AddRange(anyPairs);
                foreach (var (other, effect, _) in both)
                {
                    if (effect == 0f || string.Equals(other, StoreAppealSettings.ShipmentCategory, System.StringComparison.OrdinalIgnoreCase)) continue;
                    bool any = string.Equals(other, StoreAppealSettings.AnyCategory, System.StringComparison.OrdinalIgnoreCase);
                    for (int j = i + 1; j < offers.Count; j++)
                    {
                        var n = offers[j];
                        if (!any && !n.def.HasCategory(other)) continue;
                        float dx = n.position.x - o.position.x, dz = n.position.z - o.position.z;
                        float d2 = dx * dx + dz * dz;
                        if (d2 > reach2 || Mathf.Abs(n.position.y - o.position.y) > 3f) continue;
                        found.Add((d2, i, j, effect > 0f));
                    }
                }
            }
        }
        found.Sort((x, y) => x.d2.CompareTo(y.d2));
        var drawn = new HashSet<(int, int)>();
        foreach (var (_, i, j, good) in found)
        {
            if (drawn.Count >= maxPairLines) break;
            if (!drawn.Add((i, j))) continue;
            Line(offers[i].position + Vector3.up * 0.3f, offers[j].position + Vector3.up * 0.3f, good ? goodPairColor : badPairColor);
        }
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
            var tag = n < tags.Count ? tags[n] : NewTag();
            tag.gameObject.SetActive(true);
            tag.text = kv.Value.text;
            tag.transform.position = kv.Value.at + Vector3.up * decorTagHeight;
            n++;
        }
        for (int i = n; i < tags.Count; i++) tags[i].gameObject.SetActive(false);
    }

    private TextMeshPro NewTag()
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
        tags.Add(t);
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
        foreach (var t in tags)
            if (t != null && t.gameObject.activeSelf)
                t.transform.rotation = Quaternion.LookRotation(t.transform.position - cam.transform.position);
    }

    private void BuildMeshes()
    {
        lines ??= new Mesh { name = "Appeal Lines", hideFlags = HideFlags.DontSave };
        fills ??= new Mesh { name = "Appeal Views", hideFlags = HideFlags.DontSave };
        Fill(lines, lineVerts, lineColors, MeshTopology.Lines);
        Fill(fills, fillVerts, fillColors, MeshTopology.Triangles);
    }

    private static void Fill(Mesh mesh, List<Vector3> verts, List<Color> colors, MeshTopology topology)
    {
        mesh.Clear();
        if (verts.Count == 0) return;
        mesh.indexFormat = verts.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.SetVertices(verts);
        mesh.SetColors(colors);
        var idx = new int[verts.Count];
        for (int i = 0; i < idx.Length; i++) idx[i] = i;
        mesh.SetIndices(idx, topology, 0);
        mesh.RecalculateBounds();
    }

    private void Line(Vector3 a, Vector3 b, Color c)
    {
        lineVerts.Add(a); lineVerts.Add(b);
        lineColors.Add(c); lineColors.Add(c);
    }

    private void Tri(Vector3 a, Vector3 b, Vector3 c, Color color)
    {
        // Both sides (whichever way round the corners come).
        fillVerts.Add(a); fillVerts.Add(b); fillVerts.Add(c);
        fillVerts.Add(a); fillVerts.Add(c); fillVerts.Add(b);
        for (int i = 0; i < 6; i++) fillColors.Add(color);
    }

    private static readonly Vector3[] Corners = new Vector3[8];

    private void WireBox(Vector3 center, Quaternion rotation, Vector3 size, Color c)
    {
        Vector3 h = size * 0.5f;
        for (int i = 0; i < 8; i++)
            Corners[i] = center + rotation * new Vector3((i & 1) == 0 ? -h.x : h.x, (i & 2) == 0 ? -h.y : h.y, (i & 4) == 0 ? -h.z : h.z);
        // The 12 edges: corners differing in one bit.
        for (int i = 0; i < 8; i++)
            for (int bit = 1; bit < 8; bit <<= 1)
                if ((i & bit) == 0) Line(Corners[i], Corners[i | bit], c);
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
