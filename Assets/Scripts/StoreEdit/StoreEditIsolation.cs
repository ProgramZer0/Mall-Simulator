using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Edit mode privacy: while the edit camera draws, everything that isn't inside the store
/// being edited is switched off, so you can't see into neighbouring stores or the hallway.
/// Walls and floors that touch the store stay visible.
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

    /// <summary>How often (seconds) to look for newly spawned objects.</summary>
    public float RefreshInterval = 0.5f;

    /// <summary>Objects under these are never hidden (e.g. the edit handles).</summary>
    public Transform[] AlwaysShow;

    private Camera cam;
    private Renderer[] renderers = new Renderer[0];
    private float nextRefresh;
    private readonly List<Renderer> hidden = new();
    private readonly List<Bounds> areas = new();

    private void Awake() => cam = GetComponent<Camera>();

    private void OnEnable()
    {
        nextRefresh = 0f;
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

        if (Time.unscaledTime >= nextRefresh)
        {
            renderers = FindObjectsByType<Renderer>(FindObjectsSortMode.None);
            nextRefresh = Time.unscaledTime + RefreshInterval;
        }

        areas.Clear();
        Plot.GetAreaBounds(areas);
        if (areas.Count == 0) return;
        for (int i = 0; i < areas.Count; i++)
        {
            Bounds a = areas[i];
            a.Expand(Margin * 2f);
            areas[i] = a;
        }

        foreach (var r in renderers)
        {
            // Leave alone anything already off, so we only ever restore what we changed.
            if (r == null || !r.enabled || r.forceRenderingOff || !r.gameObject.activeInHierarchy) continue;
            if (IsAlwaysShown(r.transform)) continue;

            Bounds b = r.bounds;
            bool inStore = false;
            foreach (var a in areas)
            {
                if (a.Intersects(b)) { inStore = true; break; }
            }
            if (inStore) continue;

            r.forceRenderingOff = true;
            hidden.Add(r);
        }
    }

    private void Restore()
    {
        foreach (var r in hidden)
            if (r != null) r.forceRenderingOff = false;
        hidden.Clear();
    }

    private bool IsAlwaysShown(Transform t)
    {
        if (AlwaysShow == null) return false;
        foreach (var root in AlwaysShow)
            if (root != null && t.IsChildOf(root)) return true;
        return false;
    }
}
