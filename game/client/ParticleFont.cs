using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace XonoticGodot.Game.Client;

/// <summary>
/// The Darkplaces particle-texture atlas (<c>particles/particlefont.tga</c> + <c>particlefont.txt</c>) — the
/// source of every particle/decal SPRITE the effect system draws. DP references particle textures by an
/// integer index (the effectinfo <c>tex</c> / <c>staintex</c> ranges): smoke 0-7, bullet/scorch decals 8-15,
/// blood decals 16-23, blood particles 24-31, rain/square/etc. 32-47, fire/flame 48-55, debris/beam/bubble
/// 56-63, and the beam strips 200-205. Each index maps to a sub-rectangle of the 2048² atlas; the <c>.txt</c>
/// sidecar lists those rects in normalized UV (<c>index left top right bottom</c>, the DP particlefont format).
///
/// This loads the atlas once (via the host's VFS texture loader) and the UV table (via the VFS text loader),
/// then hands out a cached <see cref="ImageTexture"/> per index, cropped out of the atlas. The effect system
/// textures its billboard quads and projected decals with these instead of flat solid-color discs, which is
/// what makes an explosion read as a real fireball/smoke/spark burst and a scorch/blood mark read as the real
/// Xonotic decal. A failed load (no content mounted) leaves <see cref="Loaded"/> false and callers fall back
/// to the generated solid quads/discs, so the game still runs with no atlas.
/// </summary>
public sealed class ParticleFont
{
    // index -> pixel rect inside the atlas (x, y, w, h).
    private readonly Dictionary<int, Rect2I> _cells = new();
    private Image? _atlas;
    private readonly Dictionary<int, ImageTexture> _cellTex = new();
    private readonly Dictionary<int, ImageTexture> _decalTex = new();

    /// <summary>True once both the atlas image and at least one UV cell parsed.</summary>
    public bool Loaded => _atlas is not null && _cells.Count > 0;

    /// <summary>Default VFS paths for the atlas image (extension-agnostic) and its UV table.</summary>
    public const string AtlasVPath = "particles/particlefont";
    public const string TableVPath = "particles/particlefont.txt";

    /// <summary>
    /// Build the atlas from the mounted content. <paramref name="texLoader"/> resolves the atlas image
    /// (e.g. <c>AssetSystem.LoadTexture</c>); <paramref name="textLoader"/> reads the UV table text
    /// (e.g. <c>VirtualFileSystem.ReadText</c>). Returns a loaded font, or a font with <see cref="Loaded"/>
    /// false if either source is missing/unreadable (never throws — the caller falls back to solid quads).
    /// </summary>
    public static ParticleFont Load(Func<string, Texture2D?>? texLoader, Func<string, string?>? textLoader)
    {
        var font = new ParticleFont();
        if (texLoader is null || textLoader is null)
            return font;

        // 1) the UV table — the same normalized rects DP loads in CL_Particles_Init.
        try
        {
            string? table = textLoader(TableVPath);
            if (!string.IsNullOrEmpty(table))
                font.ParseTable(table);
        }
        catch { /* leave _cells empty -> not Loaded */ }

        if (font._cells.Count == 0)
            return font;

        // 2) the atlas image. LoadTexture returns an ImageTexture; pull its Image for cropping.
        try
        {
            Texture2D? tex = texLoader(AtlasVPath);
            Image? img = tex?.GetImage();
            if (img is not null)
            {
                // Cropping needs an uncompressed format; decompress if the source was a compressed DDS.
                if (img.IsCompressed())
                    img.Decompress();
                font._atlas = img;
                // The UV table is normalized; resolve to pixels now that we know the atlas size.
                font.ResolveCellPixels(img.GetWidth(), img.GetHeight());
            }
        }
        catch { font._atlas = null; }

        return font;
    }

    // Normalized rects parsed from the .txt, kept until we know the atlas pixel size.
    private readonly Dictionary<int, (float L, float T, float R, float B)> _uv = new();

    private void ParseTable(string text)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '/')
                continue;
            string[] tok = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tok.Length < 5)
                continue;
            if (!int.TryParse(tok[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
                continue;
            if (TryF(tok[1], out float l) && TryF(tok[2], out float t) &&
                TryF(tok[3], out float r) && TryF(tok[4], out float b))
            {
                _uv[idx] = (l, t, r, b);
                _cells[idx] = default; // placeholder; filled by ResolveCellPixels
            }
        }
    }

    private void ResolveCellPixels(int w, int h)
    {
        foreach (KeyValuePair<int, (float L, float T, float R, float B)> kv in _uv)
        {
            (float l, float t, float r, float b) = kv.Value;
            int x = Mathf.Clamp((int)MathF.Round(l * w), 0, w - 1);
            int y = Mathf.Clamp((int)MathF.Round(t * h), 0, h - 1);
            int cw = Mathf.Clamp((int)MathF.Round((r - l) * w), 1, w - x);
            int ch = Mathf.Clamp((int)MathF.Round((b - t) * h), 1, h - y);
            _cells[kv.Key] = new Rect2I(x, y, cw, ch);
        }
    }

    private static bool TryF(string s, out float v)
        => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    /// <summary>
    /// The sprite texture for atlas <paramref name="index"/>, cropped from the atlas and cached. Returns null
    /// when the font isn't loaded or the index has no cell (caller falls back to a solid quad/disc).
    /// </summary>
    public ImageTexture? Cell(int index)
    {
        if (_atlas is null || !_cells.TryGetValue(index, out Rect2I rect) || rect.Size.X <= 0)
            return null;
        if (_cellTex.TryGetValue(index, out ImageTexture? cached))
            return cached;

        ImageTexture? tex = null;
        try
        {
            Image region = _atlas.GetRegion(rect);
            tex = ImageTexture.CreateFromImage(region);
        }
        catch { tex = null; }

        _cellTex[index] = tex!; // cache even null to avoid re-cropping a bad cell
        return tex;
    }

    /// <summary>
    /// The DECAL form of atlas <paramref name="index"/>: the same sprite reworked for a Godot <see cref="Decal"/>
    /// node. DP's bullet/scorch/blood decal cells (8-23) are stored fully OPAQUE with the mark encoded in RGB
    /// luminance (for INVMOD blending) — projected straight onto a Decal they'd paint an opaque dark SQUARE.
    /// So we rebuild the cell as alpha = luminance (the mark becomes the coverage) over white RGB (so the
    /// Decal's modulate fully controls the scorch/blood tint). Cells that already carry real alpha keep it
    /// (combined with luminance), so this is safe for any index. Cached separately from the particle form.
    /// </summary>
    public ImageTexture? DecalCell(int index)
    {
        if (_atlas is null || !_cells.TryGetValue(index, out Rect2I rect) || rect.Size.X <= 0)
            return null;
        if (_decalTex.TryGetValue(index, out ImageTexture? cached))
            return cached;

        ImageTexture? tex = null;
        try
        {
            Image region = _atlas.GetRegion(rect);
            if (region.GetFormat() != Image.Format.Rgba8)
                region.Convert(Image.Format.Rgba8);
            int w = region.GetWidth(), h = region.GetHeight();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    Color p = region.GetPixel(x, y);
                    float lum = p.R * 0.299f + p.G * 0.587f + p.B * 0.114f;
                    // Coverage = mark luminance gated by any native alpha. White RGB lets modulate tint it.
                    region.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp(lum * p.A, 0f, 1f)));
                }
            tex = ImageTexture.CreateFromImage(region);
        }
        catch { tex = null; }

        _decalTex[index] = tex!;
        return tex;
    }

    /// <summary>
    /// A representative sprite for a DP <c>[tex0, tex1)</c> range (tex1 exclusive, as in DP's
    /// <c>tex0 + rand%(tex1-tex0)</c>). DP randomises per particle; we pick one cell per emitter — random
    /// within the range when it spans several cells (so repeated bursts vary), else the single cell.
    /// <paramref name="decal"/> selects the Decal-node form (alpha = luminance) instead of the particle form.
    /// </summary>
    public ImageTexture? CellInRange(int tex0, int tex1, bool decal = false)
    {
        if (!Loaded)
            return null;
        int lo = tex0, hi = tex1;
        if (hi <= lo)
            hi = lo + 1;
        int span = hi - lo;
        int idx = span <= 1 ? lo : lo + (int)(GD.Randi() % (uint)span);
        return decal ? (DecalCell(idx) ?? DecalCell(lo)) : (Cell(idx) ?? Cell(lo));
    }
}
