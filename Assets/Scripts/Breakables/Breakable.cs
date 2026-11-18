using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Something in the mall that can break now and then: a light, a screen. Players who spot a broken one
/// look at it and press F to report it (a help point; see BreakManager), and maintenance fixes it a little
/// later.
///
/// Setup: put it on the light or screen in the mall scene (scene objects only, not things spawned at
/// runtime). Added, it finds its own Lights (Light) or its Renderers (Screen). When broken:
/// - Lights flicker (Flicker) or go out (Off).
/// - Swap Renderers get Broken Material (a cracked or static screen, a dead bulb), if one is set.
/// - Show When Broken objects are turned on (cracks, sparks), Hide When Broken ones off.
/// It doesn't need a collider: it's found by the bounds of its renderers (or around its lights).
/// </summary>
public class Breakable : MonoBehaviour
{
    public enum Kind { Light, Screen, Other }
    public enum LightLook { Flicker, Off }

    [SerializeField] private Kind kind = Kind.Light;
    [Tooltip("What it's called in prompts (\"F: Report broken {name}\"). Empty: light / screen / thing.")]
    [SerializeField] private string displayName = "";
    [Tooltip("How likely it is to be the one that breaks, next to the others (0: never breaks).")]
    [SerializeField, Min(0f)] private float breakWeight = 1f;

    [Header("When broken")]
    [SerializeField] private Light[] lights = new Light[0];
    [SerializeField] private LightLook lightLook = LightLook.Flicker;
    [SerializeField] private Renderer[] swapRenderers = new Renderer[0];
    [SerializeField] private Material brokenMaterial;
    [SerializeField] private GameObject[] showWhenBroken = new GameObject[0];
    [SerializeField] private GameObject[] hideWhenBroken = new GameObject[0];

    /// <summary>Every one in the loaded scenes (BreakManager numbers them the same on every machine).</summary>
    public static readonly List<Breakable> All = new();

    public float BreakWeight => breakWeight;
    public string Noun => !string.IsNullOrWhiteSpace(displayName) ? displayName.Trim()
        : kind == Kind.Light ? "light" : kind == Kind.Screen ? "screen" : "thing";

    private float[] intensity;
    private Material[][] materials;
    private bool broken;
    private float nextFlicker;

    private void Reset()
    {
        lights = GetComponentsInChildren<Light>(true);
        if (lights.Length > 0) { kind = Kind.Light; swapRenderers = new Renderer[0]; return; }
        kind = Kind.Screen;
        swapRenderers = GetComponentsInChildren<Renderer>(true);
    }

    private void Awake()
    {
        if (!All.Contains(this)) All.Add(this);
        intensity = new float[lights.Length];
        for (int i = 0; i < lights.Length; i++) intensity[i] = lights[i] != null ? lights[i].intensity : 0f;
        materials = new Material[swapRenderers.Length][];
        for (int i = 0; i < swapRenderers.Length; i++) materials[i] = swapRenderers[i] != null ? swapRenderers[i].sharedMaterials : null;
        lookBounds = WorkOutBounds();
        enabled = false; // Update only flickers while broken
    }

    private void OnDestroy() => All.Remove(this);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetOnPlay() => All.Clear();

    // Testing: right-click the component in Play mode (on the host).
    [ContextMenu("Break Now (host, Play mode)")]
    private void BreakNow()
    {
        if (BreakManager.Instance != null) BreakManager.Instance.ServerBreak(this);
    }

    /// <summary>Show it broken or working (BreakManager, every machine).</summary>
    public void SetBroken(bool on)
    {
        if (broken == on || intensity == null) return;
        broken = on;
        for (int i = 0; i < lights.Length; i++)
            if (lights[i] != null) lights[i].intensity = on && lightLook == LightLook.Off ? 0f : intensity[i];
        if (brokenMaterial != null)
            for (int i = 0; i < swapRenderers.Length; i++)
            {
                var r = swapRenderers[i];
                if (r == null || materials[i] == null) continue;
                if (!on) { r.sharedMaterials = materials[i]; continue; }
                var swapped = new Material[materials[i].Length];
                for (int k = 0; k < swapped.Length; k++) swapped[k] = brokenMaterial;
                r.sharedMaterials = swapped;
            }
        foreach (var g in showWhenBroken) if (g != null) g.SetActive(on);
        foreach (var g in hideWhenBroken) if (g != null) g.SetActive(!on);
        enabled = on && lightLook == LightLook.Flicker && lights.Length > 0;
    }

    // Broken and flickering: off mostly, with short bursts of light.
    private void Update()
    {
        if (Time.time < nextFlicker) return;
        bool lit = Random.value < 0.3f;
        nextFlicker = Time.time + (lit ? Random.Range(0.03f, 0.15f) : Random.Range(0.1f, 0.8f));
        float level = lit ? Random.Range(0.5f, 1f) : Random.Range(0f, 0.08f);
        for (int i = 0; i < lights.Length; i++)
            if (lights[i] != null) lights[i].intensity = intensity[i] * level;
    }

    private Bounds lookBounds;

    /// <summary>Where it is, to look at: its renderers' bounds, else a little box around its lights.</summary>
    public Bounds LookBounds => lookBounds;

    private Bounds WorkOutBounds()
    {
        {
            bool any = false;
            var b = new Bounds(transform.position, Vector3.one * 0.4f);
            foreach (var r in GetComponentsInChildren<Renderer>())
            {
                if (r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            if (!any)
                foreach (var l in lights)
                    if (l != null) b.Encapsulate(new Bounds(l.transform.position, Vector3.one * 0.4f));
            b.Expand(0.15f); // a little easier to aim at
            return b;
        }
    }
}
