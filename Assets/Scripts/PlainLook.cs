using UnityEngine;

/// <summary>
/// The material for stand-in shapes made in code (plain capsule customers, paper bags, labelled
/// stand-ins at the counter). A primitive's own default material isn't included in builds, so those
/// showed pink there. Apply gives it this material, tinted with a property block (no copy per object).
/// CustomerManager's Plain Customer Material sets it; empty: URP Lit.
/// </summary>
public static class PlainLook
{
    /// <summary>Set from CustomerManager (Plain Customer Material). Null: a URP Lit material.</summary>
    public static Material Override;

    private static Material fallback;
    private static MaterialPropertyBlock block;
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor"), ColorId = Shader.PropertyToID("_Color");

    public static Material Material
    {
        get
        {
            if (Override != null) return Override;
            if (fallback == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                if (shader != null) fallback = new Material(shader) { name = "Plain (stand-in)", enableInstancing = true };
            }
            return fallback;
        }
    }

    /// <summary>Give a code-made shape the plain material in this color.</summary>
    public static void Apply(Renderer r, Color color)
    {
        if (r == null) return;
        var mat = Material;
        if (mat != null) r.sharedMaterial = mat;
        block ??= new MaterialPropertyBlock();
        r.GetPropertyBlock(block);
        block.SetColor(BaseColorId, color);
        block.SetColor(ColorId, color);
        r.SetPropertyBlock(block);
    }
}
