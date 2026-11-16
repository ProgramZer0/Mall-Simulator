using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Draws every product on every shelf with GPU instancing, a few draws per area instead of one
/// renderer per product.
///
/// Why: a full mall has tens of thousands of products. As separate renderers, every one of them is
/// checked for visibility and sorted every frame (culling, BuildDrawListsJob), even though almost none
/// are on screen. Here products have no active renderer at all: they're grouped by 16 m area and by
/// mesh and material, and each group is drawn with one call (per 1023), culled as one box.
///
/// The product objects still exist (with their box collider), so looking at, taking and pricing
/// products works as before; they're just a transform and that collider, with no renderers or mesh
/// parts of their own (see StockContainer.StripToCollider). Shelf stock made by
/// StockContainer uses this; the checkout belt, cash and edit-mode copies keep ordinary renderers.
///
/// Edit mode: the editing player's own screen doesn't draw these (their store's stock is shown with
/// edit copies, and nothing outside the store may show), see Paused.
///
/// Builds: product materials are drawn instanced. Either tick Enable GPU Instancing on them, or set
/// Project Settings → Graphics → Instancing Variants to Keep All, or the instanced shader variants are
/// left out of the build and products don't show.
/// </summary>
public sealed class StockBatcher : MonoBehaviour
{
    /// <summary>
    /// Don't draw shelf stock (the local player is in edit mode). Edit-view copies (AddCopy with
    /// editView) still draw.
    /// </summary>
    public static bool Paused;

    /// <summary>
    /// Areas farther than this (metres) from a camera aren't drawn for it. Every copy that is drawn is
    /// sent to the GPU again each frame, so this is what keeps a full mall cheap.
    /// </summary>
    public static float DrawDistance = 60f;

    /// <summary>
    /// Light products from light probes. Off: they get the scene's ambient light instead, which is
    /// much cheaper (with probes on, Unity looks up a probe for every copy, every frame).
    /// Read when an area group is made, so set it before products are spawned.
    /// </summary>
    public static bool UseLightProbes = false;

    private const float CellSize = 16f;
    private const int MaxPerCall = 1023;

    /// <summary>One mesh part (mesh + material + submesh) of a product prefab, relative to its root.</summary>
    public sealed class PartShape
    {
        public Mesh mesh;
        public int submesh;
        public Material material;
        public Matrix4x4 local;
        public ShadowCastingMode shadows;
        public bool receiveShadows;
        public int layer;
        public uint renderingLayers;
    }

    // One drawn copy of a part.
    public sealed class Part
    {
        internal PartShape shape;
        internal bool edit;
        internal Batch batch;
        internal int index;
    }

    /// <summary>A product drawn with no GameObject of its own (edit-mode shelf copies), see AddCopy.</summary>
    public sealed class Copy
    {
        internal Part[] parts;
    }

    internal sealed class Batch
    {
        public PartShape shape;
        public Vector3Int cell;
        public bool edit;
        public readonly List<Matrix4x4> matrices = new();
        public readonly List<Part> parts = new();
        public bool hasBounds;
        public RenderParams rp; // its worldBounds: a box around every copy (only ever grows; that's fine)
    }

    private static StockBatcher instance;
    private static readonly Dictionary<(Vector3Int, Mesh, Material, int, int, ShadowCastingMode, bool), Batch> Batches = new();
    private static readonly List<Batch> All = new();
    private static readonly Dictionary<Material, Material> Instanced = new();
    private static Camera[] cameras = new Camera[4];
    private static readonly Plane[] Planes = new Plane[6];

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetOnPlay()
    {
        Batches.Clear();
        All.Clear();
        Paused = false;
    }

    /* ========================= SHAPES ========================= */

    /// <summary>
    /// The parts of a stripped product template, with its renderers switched off; null if it can't be
    /// drawn this way (skinned, LODs, no meshes), in which case it keeps its renderers.
    /// </summary>
    public static List<PartShape> TakeShape(GameObject template)
    {
        if (template.GetComponentInChildren<SkinnedMeshRenderer>(true) != null ||
            template.GetComponentInChildren<LODGroup>(true) != null) return null;

        var parts = new List<PartShape>();
        var rootInv = template.transform.worldToLocalMatrix;
        var renderers = template.GetComponentsInChildren<MeshRenderer>(true);
        foreach (var r in renderers)
        {
            if (!r.enabled || !r.TryGetComponent<MeshFilter>(out var mf) || mf.sharedMesh == null) continue;
            var mats = r.sharedMaterials;
            for (int s = 0; s < mf.sharedMesh.subMeshCount && s < mats.Length; s++)
            {
                if (mats[s] == null) continue;
                parts.Add(new PartShape
                {
                    mesh = mf.sharedMesh,
                    submesh = s,
                    material = InstancedOf(mats[s]),
                    local = rootInv * r.transform.localToWorldMatrix,
                    shadows = r.shadowCastingMode,
                    receiveShadows = r.receiveShadows,
                    layer = r.gameObject.layer,
                    renderingLayers = r.renderingLayerMask,
                });
            }
        }
        if (parts.Count == 0) return null;
        foreach (var r in renderers) r.enabled = false; // drawn by the batcher instead
        return parts;
    }

    private static readonly Dictionary<(Material, byte), Material> TintedMaterials = new();
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");

    /// <summary>A shape's parts with a paint color (PaintPalette id) on their materials (new parts, shared materials).</summary>
    public static List<PartShape> Tinted(List<PartShape> shape, byte color)
    {
        if (shape == null || !PaintPalette.TryGetColor(color, out Color c)) return shape;
        var result = new List<PartShape>(shape.Count);
        foreach (var p in shape)
        {
            if (!TintedMaterials.TryGetValue((p.material, color), out var m) || m == null)
            {
                m = new Material(p.material) { name = p.material.name + " (paint " + color + ")", enableInstancing = true };
                if (m.HasProperty(BaseColorId)) m.SetColor(BaseColorId, c);
                if (m.HasProperty(ColorId)) m.SetColor(ColorId, c);
                TintedMaterials[(p.material, color)] = m;
            }
            result.Add(new PartShape
            {
                mesh = p.mesh, submesh = p.submesh, material = m, local = p.local, shadows = p.shadows,
                receiveShadows = p.receiveShadows, layer = p.layer, renderingLayers = p.renderingLayers,
            });
        }
        return result;
    }

    // The material with GPU instancing on (a copy if the asset doesn't have it ticked).
    private static Material InstancedOf(Material m)
    {
        if (m.enableInstancing) return m;
        if (!Instanced.TryGetValue(m, out var copy) || copy == null)
        {
            copy = new Material(m) { name = m.name + " (instanced)", enableInstancing = true };
            Instanced[m] = copy;
        }
        return copy;
    }

    /* ========================= INSTANCES ========================= */

    /// <summary>Start drawing a product (its shape's parts, where its transform is).</summary>
    public static void Add(StockModel model)
    {
        if (model == null || model.Shape == null || model.Parts != null) return;
        model.Parts = Make(model.Shape, model.transform.localToWorldMatrix, false);
    }

    /// <summary>Stop drawing a product.</summary>
    public static void Remove(StockModel model)
    {
        if (model == null || model.Parts == null) return;
        foreach (var part in model.Parts) Take(part);
        model.Parts = null;
    }

    /// <summary>The product moved (its furniture moved): draw it where it is now.</summary>
    public static void Move(StockModel model)
    {
        if (model != null && model.Parts != null) Place(model.Parts, model.transform.localToWorldMatrix);
    }

    /// <summary>
    /// Draw a product from its shape at a pose (world: position, rotation and the product's own scale),
    /// with no GameObject. Edit View: drawn while Paused (edit-mode copies on the editor's screen).
    /// </summary>
    public static Copy AddCopy(List<PartShape> shape, Matrix4x4 world, bool editView)
    {
        if (shape == null) return null;
        return new Copy { parts = Make(shape, world, editView) };
    }

    /// <summary>Draw a copy at its new pose.</summary>
    public static void MoveCopy(Copy copy, Matrix4x4 world)
    {
        if (copy != null && copy.parts != null) Place(copy.parts, world);
    }

    /// <summary>Stop drawing a copy.</summary>
    public static void RemoveCopy(Copy copy)
    {
        if (copy == null || copy.parts == null) return;
        foreach (var part in copy.parts) Take(part);
        copy.parts = null;
    }

    private static Part[] Make(List<PartShape> shape, Matrix4x4 world, bool edit)
    {
        Ensure();
        var parts = new Part[shape.Count];
        for (int i = 0; i < shape.Count; i++)
        {
            parts[i] = new Part { shape = shape[i], edit = edit };
            Insert(parts[i], world * shape[i].local);
        }
        return parts;
    }

    private static void Place(Part[] parts, Matrix4x4 world)
    {
        foreach (var part in parts)
        {
            var m = world * part.shape.local;
            if (part.batch != null && part.batch.cell == CellOf(m.GetColumn(3)))
            {
                part.batch.matrices[part.index] = m;
                Grow(part.batch, m);
            }
            else
            {
                Take(part);
                Insert(part, m);
            }
        }
    }

    private static Vector3Int CellOf(Vector3 p) =>
        new(Mathf.FloorToInt(p.x / CellSize), Mathf.FloorToInt(p.y / CellSize), Mathf.FloorToInt(p.z / CellSize));

    private static void Insert(Part part, Matrix4x4 m)
    {
        var s = part.shape;
        var cell = CellOf(m.GetColumn(3));
        var key = (cell, s.mesh, s.material, s.submesh, s.layer, s.shadows, part.edit);
        if (!Batches.TryGetValue(key, out var batch))
        {
            batch = new Batch
            {
                shape = s,
                cell = cell,
                edit = part.edit,
                rp = new RenderParams(s.material)
                {
                    shadowCastingMode = s.shadows,
                    receiveShadows = s.receiveShadows,
                    layer = s.layer,
                    renderingLayerMask = s.renderingLayers,
                    lightProbeUsage = UseLightProbes ? LightProbeUsage.BlendProbes : LightProbeUsage.Off,
                    reflectionProbeUsage = ReflectionProbeUsage.BlendProbes,
                },
            };
            Batches[key] = batch;
            All.Add(batch);
        }
        part.batch = batch;
        part.index = batch.matrices.Count;
        batch.matrices.Add(m);
        batch.parts.Add(part);
        Grow(batch, m);
    }

    // Swap-remove: the last one fills the gap.
    private static void Take(Part part)
    {
        var batch = part.batch;
        if (batch == null) return;
        int last = batch.matrices.Count - 1;
        if (part.index != last)
        {
            batch.matrices[part.index] = batch.matrices[last];
            var moved = batch.parts[last];
            batch.parts[part.index] = moved;
            moved.index = part.index;
        }
        batch.matrices.RemoveAt(last);
        batch.parts.RemoveAt(last);
        part.batch = null;
        part.index = -1;
    }

    /* ========================= DRAWING ========================= */

    private static void Ensure()
    {
        if (instance != null) return;
        var go = new GameObject("[StockBatcher]") { hideFlags = HideFlags.HideInHierarchy };
        DontDestroyOnLoad(go);
        instance = go.AddComponent<StockBatcher>();
    }

    // Each camera only gets the area groups in front of it and within DrawDistance: everything handed
    // to RenderMeshInstanced is copied and uploaded that frame even if the camera then culls it.
    // (The Scene view isn't a game camera, so products only show in the Game view.)
    private void LateUpdate()
    {
        if (All.Count == 0) return;
        if (cameras.Length < Camera.allCamerasCount) cameras = new Camera[Camera.allCamerasCount * 2];
        int camCount = Camera.GetAllCameras(cameras);
        float maxSq = DrawDistance * DrawDistance;

        for (int c = 0; c < camCount; c++)
        {
            var cam = cameras[c];
            cameras[c] = null;
            if (cam == null) continue;
            GeometryUtility.CalculateFrustumPlanes(cam, Planes);
            Vector3 eye = cam.transform.position;
            int mask = cam.cullingMask;

            for (int b = 0; b < All.Count; b++)
            {
                var batch = All[b];
                int count = batch.matrices.Count;
                if (count == 0 || (Paused && !batch.edit) || (mask & (1 << batch.shape.layer)) == 0) continue;
                var bounds = batch.rp.worldBounds;
                // (Edit-view copies have no distance limit: the edit camera can zoom out over a big store.)
                if ((!batch.edit && bounds.SqrDistance(eye) > maxSq) || !GeometryUtility.TestPlanesAABB(Planes, bounds)) continue;

                var rp = batch.rp;
                rp.camera = cam;
                for (int start = 0; start < count; start += MaxPerCall)
                    Graphics.RenderMeshInstanced(rp, batch.shape.mesh, batch.shape.submesh, batch.matrices,
                                                 Mathf.Min(MaxPerCall, count - start), start);
            }
        }
    }

    // Take in one more copy's box (what the camera culls the group by).
    private static void Grow(Batch batch, Matrix4x4 m)
    {
        Bounds mb = batch.shape.mesh.bounds;
        Bounds all = batch.rp.worldBounds;
        for (int i = 0; i < 8; i++)
        {
            Vector3 p = m.MultiplyPoint3x4(mb.center + Vector3.Scale(mb.extents,
                new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
            if (!batch.hasBounds) { all = new Bounds(p, Vector3.zero); batch.hasBounds = true; }
            else all.Encapsulate(p);
        }
        batch.rp.worldBounds = all;
    }
}
