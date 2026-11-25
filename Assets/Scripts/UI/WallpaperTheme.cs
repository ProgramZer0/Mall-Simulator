using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Customize → Colors → Auto: a phone theme made to read well on the picked background picture.
///
/// How: the picture is shrunk to 24×24 on the graphics card and read back (works without Read/Write on the
/// texture), then:
/// - light or dark: its average brightness picks dark text or light text (whichever stands out more);
/// - busy: how much the brightness varies; a busier picture gets more solid buttons, a stronger outline
///   round every text, and the picture itself dimmed a little behind light text;
/// - colours: buttons and background are the picture's own average colour, pushed darker (light text) or
///   lighter (dark text); the accent is its strongest colour (most saturated common hue), made bright or
///   deep enough to stand out on the buttons.
/// Worked out once per picture and kept.
/// </summary>
public static class WallpaperTheme
{
    private static readonly Dictionary<Sprite, (PhoneTheme.Colors colors, Color tint)> made = new();

    /// <summary>The theme for a picture, and the tint to show the picture with (white: as it is).</summary>
    public static PhoneTheme.Colors For(Sprite picture, out Color pictureTint)
    {
        pictureTint = Color.white;
        if (picture == null || picture.texture == null) return null;
        if (made.TryGetValue(picture, out var known)) { pictureTint = known.tint; return known.colors; }

        var pixels = Sample(picture, 24);
        if (pixels == null) return null;

        // Brightness: average and spread; average colour; strongest hue.
        float sumL = 0f, sumL2 = 0f;
        Vector3 avg = Vector3.zero;
        var hueWeight = new float[12];
        var hueColor = new Vector3[12];
        foreach (var p in pixels)
        {
            float l = Luma(p);
            sumL += l;
            sumL2 += l * l;
            avg += new Vector3(p.r, p.g, p.b);
            Color.RGBToHSV(p, out float h, out float s, out float v);
            float w = s * s * v; // saturated, not too dark
            int bin = Mathf.Clamp((int)(h * 12f), 0, 11);
            hueWeight[bin] += w;
            hueColor[bin] += new Vector3(p.r, p.g, p.b) * w;
        }
        int n = pixels.Length;
        float meanL = sumL / n;
        float spread = Mathf.Sqrt(Mathf.Max(0f, sumL2 / n - meanL * meanL)); // 0 flat … ~0.5 very busy
        float busy = Mathf.Clamp01(spread / 0.3f);
        avg /= n;
        Color average = new(avg.x, avg.y, avg.z, 1f);

        int best = 0;
        for (int i = 1; i < 12; i++) if (hueWeight[i] > hueWeight[best]) best = i;
        Color strong = hueWeight[best] > 0.001f
            ? (Color)(Vector4)(hueColor[best] / hueWeight[best]) : average;
        strong.a = 1f;

        bool lightText = meanL < 0.55f;
        var c = new PhoneTheme.Colors { name = "Auto" };
        Color.RGBToHSV(average, out float ah, out float asat, out _);
        if (lightText)
        {
            c.text = new Color(0.96f, 0.97f, 0.98f);
            c.dimText = new Color(0.78f, 0.8f, 0.84f);
            c.background = Color.HSVToRGB(ah, Mathf.Min(asat, 0.6f), 0.14f);
            c.button = Color.HSVToRGB(ah, Mathf.Min(asat, 0.5f), 0.2f);
            c.track = Color.HSVToRGB(ah, Mathf.Min(asat, 0.4f), 0.3f);
            c.outlineColor = new Color(0f, 0f, 0f, Mathf.Lerp(0.6f, 0.9f, busy));
            pictureTint = Color.Lerp(new Color(0.85f, 0.85f, 0.85f), new Color(0.6f, 0.6f, 0.6f), busy); // dimmed a bit
        }
        else
        {
            c.text = new Color(0.06f, 0.07f, 0.09f);
            c.dimText = new Color(0.25f, 0.27f, 0.3f);
            c.background = Color.HSVToRGB(ah, Mathf.Min(asat, 0.25f), 0.95f);
            c.button = Color.HSVToRGB(ah, Mathf.Min(asat, 0.25f), 0.92f);
            c.track = Color.HSVToRGB(ah, Mathf.Min(asat, 0.3f), 0.8f);
            c.outlineColor = new Color(1f, 1f, 1f, Mathf.Lerp(0.55f, 0.85f, busy));
        }
        // Buttons see-through on a calm picture, nearly solid on a busy one.
        c.button.a = Mathf.Lerp(0.6f, 0.92f, busy);
        c.track.a = Mathf.Lerp(0.7f, 0.95f, busy);

        // The accent: the picture's own strong colour, bright on dark buttons, deep on light ones.
        Color.RGBToHSV(strong, out float sh, out float ss, out _);
        ss = Mathf.Clamp(ss, 0.55f, 0.95f);
        c.accent = lightText ? Color.HSVToRGB(sh, ss * 0.85f, 1f) : Color.HSVToRGB(sh, ss, 0.55f);
        c.outline = true; // texts sit right on the picture

        made[picture] = (c, pictureTint);
        return c;
    }

    private static float Luma(Color c) => c.r * 0.2126f + c.g * 0.7152f + c.b * 0.0722f;

    // The sprite's part of its texture, shrunk to size×size and read back.
    private static Color[] Sample(Sprite sprite, int size)
    {
        var tex = sprite.texture;
        Rect r = sprite.textureRect;
        var scale = new Vector2(r.width / tex.width, r.height / tex.height);
        var offset = new Vector2(r.x / tex.width, r.y / tex.height);
        var rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var was = RenderTexture.active;
        try
        {
            Graphics.Blit(tex, rt, scale, offset);
            RenderTexture.active = rt;
            var read = new Texture2D(size, size, TextureFormat.RGBA32, false);
            read.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            read.Apply();
            var px = read.GetPixels();
            Object.Destroy(read);
            return px;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[WallpaperTheme] Couldn't look at '{sprite.name}': {e.Message}");
            return null;
        }
        finally
        {
            RenderTexture.active = was;
            RenderTexture.ReleaseTemporary(rt);
        }
    }
}
