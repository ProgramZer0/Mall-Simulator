using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Edit mode privacy: nothing outside the store being edited is drawn by the edit camera.
///
/// Two layers:
///  1. Objects entirely outside the store are switched off, so they can't block the view.
///  2. The "MallSim/Edit Plot Clip" full-screen pass (if the edit renderer has it) paints over
///     every remaining pixel outside the store's boxes, cutting walls and floors that cross the
///     boundary exactly at the edge. Without that pass, those just show in full.
///
/// Only the edit camera is affected. Objects are turned off just before that camera draws and
/// back on right after, so nobody else (and no other camera) ever sees anything hidden.
/// StoreEditController adds and drives this; you don't need to add it yourself.
/// </summary>
[RequireComponent(typeof(Camera))]
public class StoreEditIsolation : MonoBehaviour
{
    /// <summary>The store whose contents stay visible.</summary>
    public StorePlot Plot;

    /// <summary>Anything within this many meters of the store's space still shows (walls on the edge).</summary>
    public float Margin = 0.3f;


    /// <summary>Objects under these are never hidden (e.g. the edit handles).</summary>
    public Transform[] AlwaysShow;

    /// <summary>Colour painted where nothing is drawn (outside the store).</summary>
    public Color Background = Color.black;

    private const int MaxBoxes = 16;
    private static readonly int EnabledId = Shader.PropertyToID("_EditClipEnabled");
    private static readonly int CountId = Shader.PropertyToID("_EditClipBoxCount");
    private static readonly int BoxesId = Shader.PropertyToID("_EditClipBoxes");
    private static readonly int BackgroundId = Shader.PropertyToID("_EditClipBackground");
    private readonly Matrix4x4[] boxMatrices = new Matrix4x4[MaxBoxes];
    private readonly List<BoxCollider> boxes = new();
    private bool warnedTooManyBoxes;

    private Camera cam;
    private readonly HashSet<Renderer> outside = new(); // what to hide (kept up to date a bit each frame)
    private StorePlot foundFor;
    private readonly List<Renderer> hidden = new();
    private readonly List<Bounds> areas = new();

    private void Awake() => cam = GetComponent<Camera>();

    private void OnEnable()
    {
        nextFullScan = 0f;
        foundFor = null;
        RenderPipelineManager.beginCameraRendering += OnBeginCamera;
        RenderPipelineManager.endCameraRendering += OnEndCamera;
        Camera.onPreCull += OnPreCullBuiltIn;      // in case the project ever runs without URP
        Camera.onPostRender += OnPostRenderBuiltIn;
    }

    private void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
        RenderPipelineManager.endCameraRendering -= OnEndCamera;
        Camera.onPreCull -= OnPreCullBuiltIn;
        Camera.onPostRender -= OnPostRenderBuiltIn;
        Restore();
    }

    private void OnBeginCamera(ScriptableRenderContext ctx, Camera c) { if (c == cam) Hide(); }
    private void OnEndCamera(ScriptableRenderContext ctx, Camera c) { if (c == cam) Restore(); }

    private void OnPreCullBuiltIn(Camera c)
    {
        if (c == cam && GraphicsSettings.currentRenderPipeline == null) Hide();
    }

    private void OnPostRenderBuiltIn(Camera c)
    {
        if (c == cam && GraphicsSettings.currentRenderPipeline == null) Restore();
    }

    private void Hide()
    {
        Restore();
        if (Plot == null) return;

        SetPixelClip();
        UpdateOutside();

        foreach (var r in outside)
        {
            // Leave alone anything already off, so we only ever restore what we changed.
            if (r == null || !r.enabled || r.forceRenderingOff || !r.gameObject.activeInHierarchy) continue;
            r.forceRenderingOff = true;
            hidden.Add(r);
        }
    }

    /* What to hide is kept up to date a little at a time, so a full mall (tens of thousands of
       renderers) never costs much in one frame:
       - every Full Scan Interval the scene's renderers are listed once;
       - each frame Checks Per Frame of them are sorted into "hide" or "leave";
       - only things that could come between the camera and the store are hidden: anything in the box
         around the store and the camera. Everything else is either out of view or painted over by
         the clip pass anyway, so it's left alone (that's most of the mall). */

    /// <summary>How often (seconds) the scene's renderers are listed again (to catch new objects).</summary>
    public float FullScanInterval = 3f;
    /// <summary>Renderers sorted into hide / leave each frame.</summary>
    public int ChecksPerFrame = 3000;

    private Renderer[] all = System.Array.Empty<Renderer>();
    private int scanIndex;
    private float nextFullScan;

    private void UpdateOutside()
    {
        bool newStore = foundFor != Plot;
        if (newStore || Time.unscaledTime >= nextFullScan)
        {
            nextFullScan = Time.unscaledTime + FullScanInterval;
            foundFor = Plot;
            areas.Clear();
            Plot.GetAreaBounds(areas);
            for (int i = 0; i < areas.Count; i++)
            {
                Bounds a = areas[i];
                a.Expand(Margin * 2f);
                areas[i] = a;
            }
            all = FindObjectsByType<Renderer>(FindObjectsSortMode.None);
            scanIndex = 0;
            outside.RemoveWhere(r => r == null); // destroyed since
            if (newStore)
            {
                // A different store: sort everything now, so the first frame is already right.
                outside.Clear();
                Bounds view0 = ViewVolume();
                foreach (var r in all) Sort(r, view0);
                return;
            }
        }
        if (all.Length == 0 || areas.Count == 0) return;

        Bounds view = ViewVolume();
        int n = Mathf.Min(ChecksPerFrame, all.Length);
        for (int k = 0; k < n; k++)
        {
            Sort(all[scanIndex], view);
            scanIndex = (scanIndex + 1) % all.Length;
        }
    }

    // The box around the store and the camera: only things in it can block the view of the store.
    private Bounds ViewVolume()
    {
        if (areas.Count == 0) return new Bounds(transform.position, Vector3.zero);
        Bounds v = areas[0];
        for (int i = 1; i < areas.Count; i++) v.Encapsulate(areas[i]);
        v.Encapsulate(transform.position);
        v.Expand(1f);
        return v;
    }

    private void Sort(Renderer r, Bounds view)
    {
        if (r == null) return;
        bool hide = r.enabled && r.gameObject.activeInHierarchy && !IsAlwaysShown(r.transform);
        if (hide)
        {
            Bounds b = r.bounds;
            hide = view.Intersects(b);
            if (hide)
                foreach (var a in areas)
                    if (a.Intersects(b)) { hide = false; break; }
        }
        if (hide) outside.Add(r);
        else outside.Remove(r);
    }

    private void Restore()
    {
        foreach (var r in hidden)
            if (r != null) r.forceRenderingOff = false;
        hidden.Clear();
        Shader.SetGlobalFloat(EnabledId, 0f); // every other camera draws normally
    }

    /// <summary>Hands the store's boxes (grown by Margin) to the full-screen clip shader.</summary>
    private void SetPixelClip()
    {
        boxes.Clear();
        Plot.GetAreaBoxes(boxes);

        if (boxes.Count > MaxBoxes && !warnedTooManyBoxes)
        {
            warnedTooManyBoxes = true;
            Debug.LogWarning($"[StoreEdit] '{Plot.DisplayName}' has {boxes.Count} area boxes; only the first " +
                             $"{MaxBoxes} are used for the edit view cut.", Plot);
        }

        int count = Mathf.Min(boxes.Count, MaxBoxes);
        for (int i = 0; i < MaxBoxes; i++)
        {
            if (i >= count) { boxMatrices[i] = Matrix4x4.identity; continue; }

            var box = boxes[i];
            Vector3 scale = box.transform.lossyScale;
            Vector3 world = new(Mathf.Abs(box.size.x * scale.x), Mathf.Abs(box.size.y * scale.y),
                                Mathf.Abs(box.size.z * scale.z));
            // Grow the box by Margin on every side (in meters), so the store's own walls stay.
            Vector3 grow = new(
                (world.x + Margin * 2f) / Mathf.Max(world.x, 1e-4f),
                (world.y + Margin * 2f) / Mathf.Max(world.y, 1e-4f),
                (world.z + Margin * 2f) / Mathf.Max(world.z, 1e-4f));

            Matrix4x4 boxToWorld = box.transform.localToWorldMatrix *
                                   Matrix4x4.TRS(box.center, Quaternion.identity, Vector3.Scale(box.size, grow));
            boxMatrices[i] = boxToWorld.inverse;
        }

        Color bg = QualitySettings.activeColorSpace == ColorSpace.Linear ? Background.linear : Background;
        Shader.SetGlobalMatrixArray(BoxesId, boxMatrices);
        Shader.SetGlobalFloat(CountId, count);
        Shader.SetGlobalColor(BackgroundId, bg);
        Shader.SetGlobalFloat(EnabledId, count > 0 ? 1f : 0f);
    }

    private bool IsAlwaysShown(Transform t)
    {
        if (AlwaysShow == null) return false;
        foreach (var root in AlwaysShow)
            if (root != null && t.IsChildOf(root)) return true;
        return false;
    }
}
