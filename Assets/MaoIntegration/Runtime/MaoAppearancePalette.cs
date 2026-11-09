using UnityEngine;

internal static class MaoAppearancePalette
{
    public static Texture2D Recolor(Texture source, int kind, int id)
    {
        if (source == null || id == 0) return null;
        var rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
        var previous = RenderTexture.active;
        Texture2D result = null;
        try
        {
            Graphics.Blit(source, rt);
            RenderTexture.active = rt;
            result = new Texture2D(source.width, source.height, TextureFormat.RGBA32, true, false);
            result.name = "Miku appearance palette";
            result.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            Color[] pixels = result.GetPixels();
            for (int i = 0; i < pixels.Length; i++)
            {
                Color c = pixels[i];
                Color.RGBToHSV(c, out float h, out float s, out float v);
                if (kind == 1 && v > 0.16f)
                {
                    Color tone = id == 1 ? new Color(0.20f, 0.23f, 0.29f) : new Color(0.97f, 0.73f, 0.86f);
                    if (s > 0.35f) tone = id == 1 ? new Color(0.16f, 0.85f, 0.79f) : new Color(0.69f, 0.55f, 0.94f);
                    pixels[i] = new Color(tone.r * v, tone.g * v, tone.b * v, c.a);
                }
                else if (s > 0.20f && v > 0.10f && h > 0.30f && h < 0.65f)
                {
                    Color tone = Color.HSVToRGB(kind == 0 ? (id == 1 ? 0.91f : 0.63f) : (id == 1 ? 0.76f : 0.12f), s, v);
                    tone.a = c.a; pixels[i] = tone;
                }
            }
            result.SetPixels(pixels); result.Apply(true, true);
            result.wrapMode = source.wrapMode;
            result.filterMode = source.filterMode;
            return result;
        }
        catch
        {
            if (result != null) Object.Destroy(result);
            throw;
        }
        finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(rt); }
    }
}
