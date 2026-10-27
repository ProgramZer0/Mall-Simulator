using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Builds placement-preview ghosts from world prefabs: strips gameplay/network/physics
/// components and converts materials to URP transparent while keeping their own
/// textures and colors.
/// </summary>
public static class GhostBuilder
{
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId     = Shader.PropertyToID("_Color");
    private static readonly int BaseMapId   = Shader.PropertyToID("_BaseMap");
    private static readonly int MainTexId   = Shader.PropertyToID("_MainTex");
    private static readonly int SurfaceId   = Shader.PropertyToID("_Surface");
    private static readonly int BlendId     = Shader.PropertyToID("_Blend");
    private static readonly int AlphaClipId = Shader.PropertyToID("_AlphaClip");
    private static readonly int SrcBlendId  = Shader.PropertyToID("_SrcBlend");
    private static readonly int DstBlendId  = Shader.PropertyToID("_DstBlend");
    private static readonly int SrcBlendAId = Shader.PropertyToID("_SrcBlendAlpha");
    private static readonly int DstBlendAId = Shader.PropertyToID("_DstBlendAlpha");
    private static readonly int ZWriteId    = Shader.PropertyToID("_ZWrite");

    private static Transform templateRoot;

    /// <summary>
    /// Creates an inactive ghost template for a world prefab. Instantiate it to get a preview.
    /// </summary>
    public static GameObject BuildTemplate(GameObject worldPrefab, float alpha)
    {
        // Instantiating under an inactive parent means no Awake/OnEnable runs on the
        // prefab's scripts (NetworkObject, NetworkTransform, gameplay code).
        var ghost = Object.Instantiate(worldPrefab, GetTemplateRoot());
        ghost.name = worldPrefab.name + "_Ghost";

        StripComponents(ghost);

        foreach (var r in ghost.GetComponentsInChildren<Renderer>(true))
        {
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;

            var src = r.sharedMaterials;
            var dst = new Material[src.Length];
            for (int i = 0; i < src.Length; i++)
                dst[i] = src[i] != null ? MakeTransparent(src[i], alpha) : null;
            r.sharedMaterials = dst;
        }

        return ghost;
    }

    private static Transform GetTemplateRoot()
    {
        if (templateRoot != null) return templateRoot;

        var go = new GameObject("[GhostTemplates]");
        go.SetActive(false);
        go.hideFlags = HideFlags.HideInHierarchy;
        Object.DontDestroyOnLoad(go);
        templateRoot = go.transform;
        return templateRoot;
    }

    private static void StripComponents(GameObject ghost)
    {
        // Order matters: scripts first (some [RequireComponent] Rigidbody/Collider/NetworkObject),
        // then NetworkObject, joints before rigidbodies, then colliders.
        DestroyAll<NetworkBehaviour>(ghost);
        DestroyAll<MonoBehaviour>(ghost);
        DestroyAll<NetworkObject>(ghost);
        DestroyAll<Joint>(ghost);
        DestroyAll<Rigidbody>(ghost);
        DestroyAll<Collider>(ghost);
        DestroyAll<AudioSource>(ghost);
    }

    private static void DestroyAll<T>(GameObject root) where T : Component
    {
        // Reverse order: components added by [RequireComponent] come before the ones that
        // need them, so removing back-to-front avoids "can't remove, X depends on it".
        var comps = root.GetComponentsInChildren<T>(true);
        for (int i = comps.Length - 1; i >= 0; i--)
            if (comps[i] != null) Object.DestroyImmediate(comps[i]);
    }

    /// <summary>
    /// Copy of the material set to URP transparent alpha blending, keeping its texture and color.
    /// </summary>
    public static Material MakeTransparent(Material src, float alpha)
    {
        Material m;

        if (src.HasProperty(SurfaceId))
        {
            // Already a URP Lit / Simple Lit / Unlit style shader — just flip it to transparent.
            m = new Material(src);
        }
        else
        {
            // Unknown shader: rebuild on URP Lit using its main texture and color.
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null) return new Material(src);

            m = new Material(lit);
            Texture tex = src.HasProperty(BaseMapId) ? src.GetTexture(BaseMapId)
                        : src.HasProperty(MainTexId) ? src.GetTexture(MainTexId) : null;
            if (tex != null) m.SetTexture(BaseMapId, tex);
            m.SetColor(BaseColorId, GetColor(src));
        }

        m.name = src.name + "_Ghost";

        m.SetFloat(SurfaceId, 1f);   // Transparent
        m.SetFloat(BlendId, 0f);     // Alpha blend
        m.SetFloat(AlphaClipId, 0f);
        m.SetFloat(SrcBlendId,  (float)BlendMode.SrcAlpha);
        m.SetFloat(DstBlendId,  (float)BlendMode.OneMinusSrcAlpha);
        m.SetFloat(SrcBlendAId, (float)BlendMode.One);
        m.SetFloat(DstBlendAId, (float)BlendMode.OneMinusSrcAlpha);

        // Keep depth writes ON: the ghost hides its own back faces, so it still reads as a
        // solid 3D object instead of a flat see-through blob. Opaque scenery behind it
        // still shows through because opaques render before the transparent queue.
        m.SetFloat(ZWriteId, 1f);

        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.DisableKeyword("_ALPHATEST_ON");
        m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        m.SetOverrideTag("RenderType", "Transparent");
        m.renderQueue = (int)RenderQueue.Transparent;
        m.SetShaderPassEnabled("ShadowCaster", false);

        Color c = GetColor(m);
        c.a = alpha;
        SetColor(m, c);

        return m;
    }

    public static Color GetColor(Material m)
    {
        if (m.HasProperty(BaseColorId)) return m.GetColor(BaseColorId);
        if (m.HasProperty(ColorId)) return m.GetColor(ColorId);
        return Color.white;
    }

    public static void SetColor(Material m, Color c)
    {
        if (m.HasProperty(BaseColorId)) m.SetColor(BaseColorId, c);
        else if (m.HasProperty(ColorId)) m.SetColor(ColorId, c);
    }
}
