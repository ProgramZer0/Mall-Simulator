using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A straight wall that windows and doors (WallOpening) can be set into: it cuts a hole wherever one
/// sits. Built walls get this by themselves; for a store's front wall, add it to the wall and drag the
/// wall into the store's Storefront Walls.
///
/// The wall must be a plain box: one object with a BoxCollider (or a box mesh), any size, scaled or
/// not, turned any way (e.g. Blender's -90 on X). Whichever of its own axes points most nearly up is
/// its height; of the other two, the longer is its length and the shorter its thickness. The wall
/// is redrawn in code (same material, texture repeating every Texture Meters), so the original mesh
/// is hidden and its colliders are replaced by ones that leave the holes open. Put any shaped model
/// (trim, bumps) on a separate object: on this one it would be replaced by the box.
///
/// Every machine cuts its own walls from the openings it can see, so nothing extra is synced.
/// </summary>
[DisallowMultipleComponent]
public class CuttableWall : MonoBehaviour
{
    public static readonly List<CuttableWall> All = new();

    [Tooltip("The store this wall belongs to. Storefront walls and built walls fill this in themselves; " +
             "set it for other walls inside a store that players may put windows and doors in.")]
    [SerializeField] private StorePlot plot;
    [Tooltip("The texture repeats every this many meters.")]
    [SerializeField] private float textureMeters = 1f;

    [Header("Cut a model instead (optional)")]
    [Tooltip("Instead of drawing itself as a box, cut the holes into a model's own meshes (Models). This " +
             "object's box is then just the invisible shape: where holes can go, and what you bump into. The " +
             "model's triangles inside a hole are removed and the ones across its edge trimmed; the hole's sides " +
             "aren't filled in (a window or door frame covers them). The models' own colliders are turned off.")]
    [SerializeField] private bool cutModels;
    [Tooltip("The meshes to cut. Empty = every mesh under this wall's WallModule (or its parent), plus this object's " +
             "own if it's a model (not a plain box). With the model on this object, give it a BoxCollider for the " +
             "wall's shape.")]
    [SerializeField] private List<MeshFilter> models = new();

    private const float Eps = 0.001f;
    private const float Facing = 0.9f;        // how square an opening must be to the wall
    private const float SlabTolerance = 0.1f; // how far off the middle of the wall an opening can sit

    public StorePlot Plot { get => plot; set => plot = value; }

    /// <summary>One of its store's front walls (the store needs a door in one of these).</summary>
    public bool IsStorefront { get; set; }

    /// <summary>Its paint color (PaintPalette id, 0 = its own material's color). Set by its store.</summary>
    public byte Paint { get; private set; }

    /// <summary>Paint this wall (shown on this machine; stores sync the color themselves).</summary>
    public void SetPaint(byte id)
    {
        Paint = id;
        if (originalRenderer != null) PaintPalette.Apply(originalRenderer, -1, id);
        if (cut != null && cut.TryGetComponent<MeshRenderer>(out var mr)) PaintPalette.Apply(mr, -1, id);
    }

    /// <summary>Windows and doors in this wall at the last cut.</summary>
    public int OpeningCount => holes.Count;

    public float Length { get; private set; }
    public float Height { get; private set; }
    public float Thickness { get; private set; }
    /// <summary>Points out of one face (openings may face either way).</summary>
    public Vector3 Normal => transform.TransformDirection(Axis(thickAxis)).normalized;
    public Vector3 Right => transform.TransformDirection(Axis(lengthAxis)).normalized;

    // Box in the wall's local space.
    private Vector3 boxCenter, boxSize, scale;
    private int lengthAxis, thickAxis; // the wall's own axes (0 = x, 1 = y, 2 = z)
    private int upAxis = 1;            // the own axis pointing most nearly up (1 = y for an upright wall)
    private float upSign = 1f;         // -1 when that axis points down

    private readonly List<Rect> holes = new();      // in wall space (u along, v up), meters
    private readonly List<Vector2[]> holeOutlines = new(); // each hole's outline (wall space, counter-clockwise)
    private readonly List<bool> holeCurved = new();     // the outline isn't just its rectangle
    private readonly List<Rect> builtHoles = new();
    private int seenVersion = -1;
    private bool built;

    private GameObject cut;
    private Mesh mesh;
    private readonly List<BoxCollider> colliders = new();
    private Renderer originalRenderer;
    private readonly List<Collider> originalColliders = new();

    // Cut Models: each model's own mesh (kept to cut from) and the cut copy shown instead.
    private readonly List<(MeshFilter filter, Mesh source, Mesh cut)> cutTargets = new();
    private bool cutTargetsFound;
    private readonly List<Collider> modelColliders = new();

    public bool CutsModels => cutModels;

    /// <summary>
    /// Cut these meshes (as well as or instead of what Models lists). Used when front wall tiles are
    /// joined into one panel: it cuts every tile's model.
    /// </summary>
    public void SetCutModels(IEnumerable<MeshFilter> filters)
    {
        RestoreModels();
        models.Clear();
        models.AddRange(filters);
        cutModels = true;
        cutTargetsFound = false;
        built = false; // cut again next frame
    }

    private void Awake()
    {
        Measure();
        // A shaped model here would be replaced by a plain box: say so.
        if (!cutModels && TryGetComponent<MeshFilter>(out var shape) && shape.sharedMesh != null && shape.sharedMesh.vertexCount > 24)
            Debug.LogWarning($"[CuttableWall] '{name}' has a shaped mesh ({shape.sharedMesh.vertexCount} vertices). The wall is " +
                             "redrawn as a plain box, so that model won't show: put the model on a separate object and keep " +
                             "this one a plain box (e.g. a cube) for the flat, cuttable part.", this);
        originalRenderer = GetComponent<Renderer>();
        foreach (var c in GetComponents<Collider>()) originalColliders.Add(c);
    }

    /// <summary>Goes up whenever a wall appears or goes away.</summary>
    public static int Version { get; private set; }

    private void OnEnable()
    {
        All.Add(this);
        Version++;
        seenVersion = -1;
    }

    private void OnDisable()
    {
        All.Remove(this);
        Version++;
    }

    private void OnDestroy()
    {
        RestoreModels();
        if (mesh != null) Destroy(mesh);
        foreach (var m in mitredMeshes) if (m != null) Destroy(m);
    }

    private void LateUpdate()
    {
        if (seenVersion == WallOpening.Version && built) return;
        seenVersion = WallOpening.Version;

        Measure();
        FindHoles(holes);
        if (built && SameHoles()) return;
        Build();
    }

    /* ========================= CUT MODELS ========================= */

    private void FindCutTargets()
    {
        if (cutTargetsFound) return;
        cutTargetsFound = true;
        cutTargets.Clear();
        modelColliders.Clear();

        var filters = new List<MeshFilter>();
        if (models.Count > 0) filters.AddRange(models);
        else
        {
            var module = GetComponentInParent<WallModule>();
            Transform root = module != null ? module.transform : transform.parent != null ? transform.parent : transform;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                var owner = mf.GetComponentInParent<CuttableWall>();
                if (owner != null && owner != this) continue; // another wall's
                if (cut != null && mf.transform.IsChildOf(cut.transform)) continue;
                // Its own mesh only if it's a model: a plain box here is just the wall's shape.
                if (mf.transform == transform && (mf.sharedMesh == null || mf.sharedMesh.vertexCount <= 24)) continue;
                filters.Add(mf);
            }
        }

        foreach (var mf in filters)
        {
            if (mf == null || mf.sharedMesh == null) continue;
            if (!mf.sharedMesh.isReadable)
            {
                Debug.LogWarning($"[CuttableWall] Can't cut '{mf.name}': its mesh isn't readable. Tick Read/Write on the " +
                                 "model's import settings.", mf);
                continue;
            }
            var copy = new Mesh { name = $"{mf.sharedMesh.name} (cut)" };
            if (mf.transform == transform && !TryGetComponent<BoxCollider>(out _))
                Debug.LogWarning($"[CuttableWall] '{name}' cuts its own model but has no BoxCollider: add one sized to the " +
                                 "wall (its shape is measured from it).", this);
            cutTargets.Add((mf, mf.sharedMesh, copy));
            mf.sharedMesh = copy;
            // The box is what you bump into; the model's own colliders would fill the holes.
            foreach (var c in mf.GetComponents<Collider>())
                if (c.enabled && !c.isTrigger) { c.enabled = false; modelColliders.Add(c); }
        }
        if (cutTargets.Count == 0)
            Debug.LogWarning($"[CuttableWall] '{name}' has Cut Models on but found no meshes it can cut, so it's drawn as a " +
                             "plain box instead.", this);
    }

    private void CutModels()
    {
        FindCutTargets();
        foreach (var (filter, source, copy) in cutTargets)
        {
            if (filter == null) continue;
            Transform t = filter.transform;
            MeshHoleCutter.Cut(source, copy, v =>
            {
                Vector3 w = ToWall(t.TransformPoint(v));
                return new Vector2(w.x, w.y);
            }, holeOutlines);
        }
    }

    private void RestoreModels()
    {
        foreach (var (filter, source, copy) in cutTargets)
        {
            if (filter != null) filter.sharedMesh = source;
            if (copy != null) Destroy(copy);
        }
        cutTargets.Clear();
        foreach (var c in modelColliders) if (c != null) c.enabled = true;
        modelColliders.Clear();
        cutTargetsFound = false;
    }

    /* ========================= SHAPE ========================= */

    private static Vector3 Axis(int i) => i == 0 ? Vector3.right : i == 1 ? Vector3.up : Vector3.forward;

    private void Measure()
    {
        if (TryGetComponent<BoxCollider>(out var box))
        {
            boxCenter = box.center;
            boxSize = box.size;
        }
        else if (TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh != null)
        {
            boxCenter = mf.sharedMesh.bounds.center;
            boxSize = mf.sharedMesh.bounds.size;
        }
        else
        {
            Debug.LogWarning($"[CuttableWall] '{name}' has no BoxCollider or mesh to measure.", this);
            boxCenter = Vector3.zero;
            boxSize = Vector3.one;
        }

        Vector3 s = transform.lossyScale;
        scale = new Vector3(Mathf.Max(Eps, Mathf.Abs(s.x)), Mathf.Max(Eps, Mathf.Abs(s.y)), Mathf.Max(Eps, Mathf.Abs(s.z)));

        // Up is whichever own axis points most nearly up in the world (y for an upright wall).
        float best = -1f;
        for (int i = 0; i < 3; i++)
        {
            float d = Vector3.Dot(transform.rotation * Axis(i), Vector3.up);
            if (Mathf.Abs(d) > best) { best = Mathf.Abs(d); upAxis = i; upSign = d >= 0f ? 1f : -1f; }
        }
        int a = upAxis == 0 ? 1 : 0, b = upAxis == 2 ? 1 : 2; // the two flat ones
        float am = boxSize[a] * scale[a], bm = boxSize[b] * scale[b];
        lengthAxis = am >= bm ? a : b;
        thickAxis = am >= bm ? b : a;
        Length = Mathf.Max(am, bm);
        Thickness = Mathf.Min(am, bm);
        Height = boxSize[upAxis] * scale[upAxis];
    }

    // Wall space v (meters up from the bottom) to the up axis's local coordinate, and back.
    private float UpLocal(float v) => boxCenter[upAxis] + upSign * (v / scale[upAxis] - boxSize[upAxis] * 0.5f);
    private float UpMeters(float local) => (upSign * (local - boxCenter[upAxis]) + boxSize[upAxis] * 0.5f) * scale[upAxis];

    /// <summary>World point to wall space: u along (from one end), v up (from the bottom), w across (from the middle).</summary>
    public Vector3 ToWall(Vector3 world)
    {
        Vector3 l = transform.InverseTransformPoint(world);
        return new Vector3(
            (l[lengthAxis] - (boxCenter[lengthAxis] - boxSize[lengthAxis] * 0.5f)) * scale[lengthAxis],
            UpMeters(l[upAxis]),
            (l[thickAxis] - boxCenter[thickAxis]) * scale[thickAxis]);
    }

    /// <summary>Wall space back to the wall's local space.</summary>
    private Vector3 LocalOf(float u, float v, float w)
    {
        Vector3 l = default;
        l[lengthAxis] = boxCenter[lengthAxis] - boxSize[lengthAxis] * 0.5f + u / scale[lengthAxis];
        l[upAxis] = UpLocal(v);
        l[thickAxis] = boxCenter[thickAxis] + w / scale[thickAxis];
        return l;
    }

    public Vector3 FromWall(float u, float v, float w = 0f) => transform.TransformPoint(LocalOf(u, v, w));

    /* ========================= OPENINGS ========================= */

    /// <summary>Where an opening would sit in this wall (wall space), if it's in it at all.</summary>
    private bool TryHole(WallOpening.Shape s, out Rect hole)
    {
        hole = default;
        if (Mathf.Abs(Vector3.Dot(s.forward, Normal)) < Facing) return false;
        Vector3 c = ToWall(s.center);
        if (Mathf.Abs(c.z) > Thickness * 0.5f + SlabTolerance) return false;
        hole = new Rect(c.x - s.size.x * 0.5f, c.y - s.size.y * 0.5f, s.size.x, s.size.y);
        // Its middle has to be on the wall (one just past the end belongs to the next wall).
        return c.x > 0f && c.x < Length && c.y > 0f && c.y < Height;
    }

    private readonly List<(Rect rect, Vector2[] outline, bool curved)> found = new();

    private void FindHoles(List<Rect> into)
    {
        into.Clear();
        found.Clear();
        foreach (var o in WallOpening.All)
        {
            if (o == null) continue;
            var shape = o.Current;
            if (TryHole(shape, out Rect r)) found.Add((r, OutlineOf(shape, r), shape.outline != null));
        }
        found.Sort((a, b) => a.rect.xMin != b.rect.xMin ? a.rect.xMin.CompareTo(b.rect.xMin) : a.rect.yMin.CompareTo(b.rect.yMin));
        holeOutlines.Clear();
        holeCurved.Clear();
        foreach (var f in found)
        {
            into.Add(f.rect);
            holeOutlines.Add(f.outline);
            holeCurved.Add(f.curved);
        }
    }

    // A hole's outline in wall space: its rectangle, or its model's outline (arches...).
    private Vector2[] OutlineOf(WallOpening.Shape s, Rect r)
    {
        if (s.outline == null) return MeshHoleCutter.Outline(r);
        var points = new Vector2[s.outline.Length];
        for (int i = 0; i < points.Length; i++)
        {
            Vector3 w = ToWall(s.OutlinePoint(s.outline[i]));
            points[i] = new Vector2(w.x, w.y);
        }
        return MeshHoleCutter.CounterClockwise(points);
    }

    private bool SameHoles()
    {
        if (holes.Count != builtHoles.Count) return false;
        for (int i = 0; i < holes.Count; i++)
        {
            Rect a = holes[i], b = builtHoles[i];
            if (Mathf.Abs(a.x - b.x) > Eps || Mathf.Abs(a.y - b.y) > Eps ||
                Mathf.Abs(a.width - b.width) > Eps || Mathf.Abs(a.height - b.height) > Eps) return false;
        }
        return true;
    }

    /// <summary>The wall an opening is in (null if none).</summary>
    public static CuttableWall HostOf(WallOpening o) => o == null ? null : HostOf(o.Current);

    public static CuttableWall HostOf(WallOpening.Shape s)
    {
        foreach (var w in All)
            if (w != null && w.TryHole(s, out _)) return w;
        return null;
    }

    /// <summary>The wall an item would be in at this pose (for checks before it exists).</summary>
    public static CuttableWall HostOf(ItemDef def, Vector3 position, Quaternion rotation) =>
        WallOpening.IsOpening(def) ? HostOf(WallOpening.Measure(def, position, rotation)) : null;

    /// <summary>
    /// Can an opening of this size go here? Checks the ends, top and bottom of the wall, and the gap to
    /// every other opening in it. Ignore: the opening being moved. Skip Edit Copies: on the host while
    /// saving, the editor's copies are still in the scene and mustn't count.
    /// </summary>
    public bool CanFit(Rect hole, WallOpening ignore, bool skipEditCopies, out string reason)
    {
        var s = BuiltWallSettings.Instance;
        float margin = s != null ? s.OpeningEdgeMargin : 0.15f;
        float gap = s != null ? s.OpeningGap : 0.15f;

        if (hole.width > Length - margin * 2f + Eps) { reason = "Too wide for this wall"; return false; }
        if (hole.xMin < margin - Eps || hole.xMax > Length - margin + Eps) { reason = "Too close to the end of the wall"; return false; }
        if (hole.yMin < -Eps || hole.yMax > Height - margin + Eps) { reason = "Too tall for this wall"; return false; }

        foreach (var o in WallOpening.All)
        {
            if (o == null || o == ignore || (skipEditCopies && o.IsEditCopy)) continue;
            if (!TryHole(o.Current, out Rect other)) continue;
            var grown = new Rect(other.x - gap, other.y - gap, other.width + gap * 2f, other.height + gap * 2f);
            if (grown.Overlaps(hole)) { reason = "Too close to another window or door"; return false; }
        }
        reason = null;
        return true;
    }

    /// <summary>
    /// Fit an opening with its middle at (u, v) on this wall: kept inside the wall (doors go down to
    /// the floor), facing out of the wall's Normal side (or the other side if flip). Fails if there's
    /// no room, but still gives the pose. Pose is for the opening's pivot.
    /// </summary>
    public bool TryFit(WallOpening opening, float u, float v, bool flip, bool skipEditCopies,
                       out Vector3 position, out Quaternion rotation, out string reason)
    {
        position = default;
        rotation = default;
        var s = BuiltWallSettings.Instance;
        float margin = s != null ? s.OpeningEdgeMargin : 0.15f;
        Vector2 size = opening.Size;

        float floor = FloorV;
        u = Mathf.Clamp(u, margin + size.x * 0.5f, Mathf.Max(margin + size.x * 0.5f, Length - margin - size.x * 0.5f));
        v = opening.IsDoor
            ? floor + size.y * 0.5f
            : Mathf.Clamp(v, floor + margin + size.y * 0.5f, Mathf.Max(floor + margin + size.y * 0.5f, Height - margin - size.y * 0.5f));

        // The pose is filled in even when it doesn't fit, so it can be shown (faded) there.
        Vector3 forward = flip ? -Normal : Normal;
        rotation = opening.RotationFor(Quaternion.LookRotation(forward, transform.up));
        position = opening.PivotFor(FromWall(u, v), rotation);

        var hole = new Rect(u - size.x * 0.5f, v - size.y * 0.5f, size.x, size.y);
        return CanFit(hole, opening, skipEditCopies, out reason);
    }

    /// <summary>
    /// Height of its store's floor up the wall (0 if the wall starts at the floor or has no store).
    /// Doors stand on it.
    /// </summary>
    public float FloorV
    {
        get
        {
            if (plot == null) return 0f;
            Vector3 bottom = FromWall(0f, 0f);
            float v = ToWall(new Vector3(bottom.x, plot.FloorHeight, bottom.z)).y;
            return Mathf.Clamp(v, 0f, Height);
        }
    }

    /// <summary>Where a ray crosses the middle of this wall, in wall space (u, v). False if it misses.</summary>
    public bool Raycast(Ray ray, out float u, out float v, out float distance)
    {
        u = v = distance = 0f;
        var plane = new Plane(Normal, FromWall(0f, 0f));
        if (!plane.Raycast(ray, out distance)) return false;
        Vector3 p = ToWall(ray.GetPoint(distance));
        u = p.x;
        v = p.y;
        return u >= 0f && u <= Length && v >= 0f && v <= Height;
    }

#if UNITY_EDITOR
    /// <summary>Editor: how far a point is from this wall (for snapping openings placed in the scene).</summary>
    public float DistanceInEditor(Vector3 world)
    {
        Measure();
        Vector3 c = ToWall(world);
        float du = c.x < 0f ? -c.x : c.x > Length ? c.x - Length : 0f;
        float dv = c.y < 0f ? -c.y : c.y > Height ? c.y - Height : 0f;
        return new Vector3(du, dv, Mathf.Abs(c.z)).magnitude;
    }

    /// <summary>Editor: centre an opening in this wall where it is (doors down to the floor), keeping which way it faces.</summary>
    public void SnapInEditor(WallOpening o)
    {
        Measure();
        Vector3 c = ToWall(o.Current.center);
        Vector2 size = o.Size;
        float floor = FloorV;
        float u = Mathf.Clamp(c.x, size.x * 0.5f, Mathf.Max(size.x * 0.5f, Length - size.x * 0.5f));
        float v = o.IsDoor ? floor + size.y * 0.5f : Mathf.Clamp(c.y, floor + size.y * 0.5f, Mathf.Max(floor + size.y * 0.5f, Height - size.y * 0.5f));
        bool flip = Vector3.Dot(o.Current.forward, Normal) < 0f;
        Quaternion rot = o.RotationFor(Quaternion.LookRotation(flip ? -Normal : Normal, transform.up));
        o.transform.SetPositionAndRotation(o.PivotFor(FromWall(u, v), rot), rot);
    }
#endif

    /* ========================= BUILDING ========================= */

    private void Build()
    {
        built = true;
        builtHoles.Clear();
        builtHoles.AddRange(holes);

        if (cut == null)
        {
            cut = new GameObject("[Cut Wall]");
            cut.layer = gameObject.layer;
            cut.transform.SetParent(transform, false);
            mesh = new Mesh { name = $"{name} (cut)" };
            cut.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = cut.AddComponent<MeshRenderer>();
            if (originalRenderer != null)
            {
                mr.sharedMaterials = originalRenderer.sharedMaterials;
                mr.shadowCastingMode = originalRenderer.shadowCastingMode;
                mr.receiveShadows = originalRenderer.receiveShadows;
            }
            if (Paint != 0) PaintPalette.Apply(mr, -1, Paint);
        }
        if (originalRenderer != null) originalRenderer.enabled = false;
        foreach (var c in originalColliders) if (c != null) c.enabled = false;

        var boxes = new List<Rect>();
        SolidParts(boxes);

        // Cut Models: the model shows (with the holes cut into it), not the box. With nothing it can cut
        // (e.g. a mesh without Read/Write), the box is drawn after all so the wall doesn't vanish.
        if (cutModels) FindCutTargets();
        bool cutting = cutModels && cutTargets.Count > 0;
        if (cut.TryGetComponent<MeshRenderer>(out var boxRenderer)) boxRenderer.enabled = !cutting;
        if (cutting)
        {
            CutModels();
            // Its own model is one of the cut ones: it stays visible (showing the cut copy).
            if (originalRenderer != null)
                foreach (var target in cutTargets)
                    if (target.filter != null && target.filter.transform == transform) originalRenderer.enabled = true;
        }

        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();
        float t = Thickness * 0.5f;
        foreach (var r in boxes) AddBox(r, -t, t, verts, normals, uvs, tris);
        // Curved holes (arches): the solid corners between the outline and its rectangle, front and back.
        // Their sides stay open (the frame covers them); the colliders stay rectangular.
        for (int i = 0; i < holes.Count && i < holeOutlines.Count; i++)
        {
            if (!holeCurved[i]) continue;
            flat.Clear();
            MeshHoleCutter.RectMinusHole(holes[i], holeOutlines[i], flat);
            AddFlat(flat, t, +1, verts, normals, uvs, tris);
            AddFlat(flat, -t, -1, verts, normals, uvs, tris);
        }

        mesh.Clear();
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();

        // One collider per solid part: a box, or for a part at a mitred end, a convex mesh with the
        // same slanted end as the drawn wall (so items can't poke through the corner).
        while (colliders.Count < boxes.Count) colliders.Add(cut.AddComponent<BoxCollider>());
        int mitred = 0;
        for (int i = 0; i < colliders.Count; i++)
        {
            bool on = i < boxes.Count;
            colliders[i].enabled = false;
            if (!on) continue;
            Rect r = boxes[i];
            bool slanted = (r.xMin < Eps && startSlope != 0f) || (r.xMax > Length - Eps && endSlope != 0f);
            if (slanted)
            {
                SetMitredCollider(mitred++, r, t);
                continue;
            }
            colliders[i].enabled = true;
            Vector3 a = LocalOf(r.xMin, r.yMin, -t), b = LocalOf(r.xMax, r.yMax, t);
            colliders[i].center = (a + b) * 0.5f;
            colliders[i].size = new Vector3(Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y), Mathf.Abs(b.z - a.z));
        }
        for (int i = mitred; i < mitredColliders.Count; i++) mitredColliders[i].enabled = false;
    }

    private readonly List<MeshCollider> mitredColliders = new();
    private readonly List<Mesh> mitredMeshes = new();

    // A convex collider for one solid part whose end(s) are cut at an angle.
    private void SetMitredCollider(int index, Rect r, float t)
    {
        while (mitredColliders.Count <= index)
        {
            var mc = cut.AddComponent<MeshCollider>();
            mc.convex = true;
            var m = new Mesh { name = $"{name} (mitred collider)" };
            mitredColliders.Add(mc);
            mitredMeshes.Add(m);
        }

        var corners = new Vector3[8];
        int k = 0;
        foreach (float w in new[] { -t, t })
            foreach (float v in new[] { r.yMin, r.yMax })
                foreach (float u in new[] { r.xMin, r.xMax })
                    corners[k++] = LocalOf(EndU(u, w), v, w);

        // Box faces (winding doesn't matter for a convex hull).
        int[] tris =
        {
            0, 1, 3, 0, 3, 2,  4, 6, 7, 4, 7, 5,  0, 4, 5, 0, 5, 1,
            2, 3, 7, 2, 7, 6,  0, 2, 6, 0, 6, 4,  1, 5, 7, 1, 7, 3,
        };
        var mesh = mitredMeshes[index];
        mesh.Clear();
        mesh.vertices = corners;
        mesh.triangles = tris;
        mesh.RecalculateBounds();

        var collider = mitredColliders[index];
        collider.sharedMesh = null; // re-cook
        collider.sharedMesh = mesh;
        collider.enabled = true;
    }

    /// <summary>The wall minus its holes, as rectangles (wall space).</summary>
    private void SolidParts(List<Rect> into)
    {
        var xs = new List<float> { 0f, Length };
        foreach (var h in holes)
        {
            xs.Add(Mathf.Clamp(h.xMin, 0f, Length));
            xs.Add(Mathf.Clamp(h.xMax, 0f, Length));
        }
        xs.Sort();

        // Columns between the hole edges, each with its solid stretches up the wall. Neighbouring
        // columns that look the same are joined, so a plain wall is one box.
        List<Vector2> prev = null;
        float prevStart = 0f, prevEnd = 0f;
        for (int i = 0; i + 1 < xs.Count; i++)
        {
            float a = xs[i], b = xs[i + 1];
            if (b - a < Eps) continue;
            float mid = (a + b) * 0.5f;

            var gaps = new List<Vector2>();
            foreach (var h in holes)
                if (h.xMin < mid && h.xMax > mid) gaps.Add(new Vector2(Mathf.Max(0f, h.yMin), Mathf.Min(Height, h.yMax)));
            var solid = Complement(gaps, Height);

            if (prev != null && Same(prev, solid) && Mathf.Abs(prevEnd - a) < Eps)
            {
                prevEnd = b;
                continue;
            }
            Flush(prev, prevStart, prevEnd, into);
            prev = solid;
            prevStart = a;
            prevEnd = b;
        }
        Flush(prev, prevStart, prevEnd, into);
    }

    private static void Flush(List<Vector2> solid, float a, float b, List<Rect> into)
    {
        if (solid == null) return;
        foreach (var s in solid) into.Add(new Rect(a, s.x, b - a, s.y - s.x));
    }

    private static List<Vector2> Complement(List<Vector2> gaps, float height)
    {
        gaps.Sort((p, q) => p.x.CompareTo(q.x));
        var result = new List<Vector2>();
        float y = 0f;
        foreach (var g in gaps)
        {
            if (g.x - y > Eps) result.Add(new Vector2(y, g.x));
            y = Mathf.Max(y, g.y);
        }
        if (height - y > Eps) result.Add(new Vector2(y, height));
        return result;
    }

    private static bool Same(List<Vector2> a, List<Vector2> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if ((a[i] - b[i]).sqrMagnitude > Eps * Eps) return false;
        return true;
    }

    // How much the end faces lean: u moves by w × slope at the wall's start (u = 0) and end (u = Length).
    private float startSlope, endSlope;

    /// <summary>
    /// Cut the wall's ends at an angle so two walls meeting at a corner join cleanly (built walls set
    /// this from their neighbours). Slope: how far along the wall the end moves per meter across it.
    /// </summary>
    public void SetEndSlopes(float start, float end)
    {
        startSlope = start;
        endSlope = end;
        built = false; // redraw
        seenVersion = -1;
    }

    private float EndU(float u, float w)
    {
        if (Mathf.Abs(u) < Eps) return u + w * startSlope;
        if (Mathf.Abs(u - Length) < Eps) return u + w * endSlope;
        return u;
    }

    // Wall space (u, v, w) maps to local (length axis, up axis, thickness axis). An odd reordering of
    // the axes, or an up axis pointing down, flips handedness, so the winding test flips with it.
    private float Handedness
    {
        get
        {
            bool even = (lengthAxis == 0 && upAxis == 1) || (lengthAxis == 1 && upAxis == 2) || (lengthAxis == 2 && upAxis == 0);
            return (even ? 1f : -1f) * upSign;
        }
    }

    // A box from (u0, v0, w0) to (u1, v1, w1) in wall space, with all six faces.
    private readonly List<Vector2> flat = new();

    // Flat triangles (wall space, every 3 points one) as a face of the wall at depth W, facing Sign.
    private void AddFlat(List<Vector2> triangles, float w, int sign, List<Vector3> verts, List<Vector3> normals,
                         List<Vector2> uvs, List<int> tris)
    {
        float tm = Mathf.Max(0.01f, textureMeters);
        Vector3 nm = Vector3.zero;
        nm[thickAxis] = sign;
        Vector3 n = Vector3.Scale(nm, scale).normalized;
        Vector3 outward = new(0f, 0f, sign);
        for (int k = 0; k + 2 < triangles.Count; k += 3)
        {
            int start = verts.Count;
            for (int j = 0; j < 3; j++)
            {
                Vector2 p = triangles[k + j];
                verts.Add(LocalOf(p.x, p.y, w));
                normals.Add(n);
                uvs.Add(p / tm);
            }
            Vector3 m0 = triangles[k], m1 = triangles[k + 1], m2 = triangles[k + 2];
            bool keepOrder = Vector3.Dot(Vector3.Cross(m1 - m0, m2 - m0), outward) * Handedness > 0f;
            tris.Add(start);
            tris.Add(keepOrder ? start + 1 : start + 2);
            tris.Add(keepOrder ? start + 2 : start + 1);
        }
    }

    private void AddBox(Rect r, float w0, float w1, List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs, List<int> tris)
    {
        float u0 = r.xMin, u1 = r.xMax, v0 = r.yMin, v1 = r.yMax;
        float tm = Mathf.Max(0.01f, textureMeters);

        // Each face: four corners (u, v, w) going round, the face's outward direction, and which two of
        // (u, v, w) make its texture coordinates.
        Face(new Vector3(u0, v0, w1), new Vector3(u1, v0, w1), new Vector3(u1, v1, w1), new Vector3(u0, v1, w1), 2, +1, 0, 1);
        Face(new Vector3(u1, v0, w0), new Vector3(u0, v0, w0), new Vector3(u0, v1, w0), new Vector3(u1, v1, w0), 2, -1, 0, 1);
        Face(new Vector3(u0, v1, w1), new Vector3(u1, v1, w1), new Vector3(u1, v1, w0), new Vector3(u0, v1, w0), 1, +1, 0, 2);
        Face(new Vector3(u0, v0, w0), new Vector3(u1, v0, w0), new Vector3(u1, v0, w1), new Vector3(u0, v0, w1), 1, -1, 0, 2);
        Face(new Vector3(u1, v0, w1), new Vector3(u1, v0, w0), new Vector3(u1, v1, w0), new Vector3(u1, v1, w1), 0, +1, 2, 1);
        Face(new Vector3(u0, v0, w0), new Vector3(u0, v0, w1), new Vector3(u0, v1, w1), new Vector3(u0, v1, w0), 0, -1, 2, 1);

        void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d, int axis, int sign, int su, int sv)
        {
            // Outward direction in meters, laid out on the wall's local axes.
            Vector3 nm = Vector3.zero;
            int local = axis == 0 ? lengthAxis : axis == 1 ? upAxis : thickAxis;
            nm[local] = axis == 1 ? sign * upSign : sign;

            // The wall's own ends can be cut at an angle (mitred corners): the end face leans.
            if (axis == 0)
            {
                float u = a.x;
                if (sign < 0 && Mathf.Abs(u) < Eps && startSlope != 0f) nm[thickAxis] = startSlope;
                else if (sign > 0 && Mathf.Abs(u - Length) < Eps && endSlope != 0f) nm[thickAxis] = -endSlope;
            }
            // Normals in local space (the wall may be scaled unevenly).
            Vector3 n = Vector3.Scale(nm, scale).normalized;

            int start = verts.Count;
            Vector3 m0 = default, m1 = default, m2 = default;
            int k = 0;
            foreach (var p in new[] { a, b, c, d })
            {
                float u = EndU(p.x, p.z);
                verts.Add(LocalOf(u, p.y, p.z));
                normals.Add(n);
                uvs.Add(new Vector2(su == 0 ? u : p[su], sv == 0 ? u : p[sv]) / tm);
                var m = new Vector3(u, p.y, p.z);
                if (k == 0) m0 = m; else if (k == 1) m1 = m; else if (k == 2) m2 = m;
                k++;
            }

            // Wind so the face points along its outward direction (Unity: Cross(b - a, c - a) points out
            // of a front face). Worked out in wall space (u, v, w): outward there is (nu, nv, nw).
            Vector3 outward = new(nm[lengthAxis], nm[upAxis] * upSign, nm[thickAxis]);
            bool keepOrder = Vector3.Dot(Vector3.Cross(m1 - m0, m2 - m0), outward) * Handedness > 0f;
            if (keepOrder)
            {
                tris.Add(start); tris.Add(start + 1); tris.Add(start + 2);
                tris.Add(start); tris.Add(start + 2); tris.Add(start + 3);
            }
            else
            {
                tris.Add(start); tris.Add(start + 2); tris.Add(start + 1);
                tris.Add(start); tris.Add(start + 3); tris.Add(start + 2);
            }
        }
    }
}
