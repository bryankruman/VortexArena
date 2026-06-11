using Godot;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// The HUD skin layer — the C# successor to QuakeC's <c>draw_BorderPicture</c> / <c>HUD_Panel_DrawBg</c>
/// (Base/.../qcsrc/lib/draw.qh + client/hud/hud.qc) and the engine's skinned <c>drawpic</c>. Xonotic frames
/// every panel with a 9-slice border image drawn from the active HUD skin (<c>gfx/hud/&lt;skin&gt;/…</c>,
/// default <c>luma</c>, falling back to <c>gfx/hud/default/…</c>). This static helper resolves the border
/// texture through the existing <see cref="TextureCache"/> and issues the nine <see cref="CanvasItem"/>
/// region blits that make up a stretched 9-slice frame.
///
/// All the per-panel <c>bg</c> names in the luma skin (<c>border_default_south</c>, <c>border_tab_south</c>,
/// <c>border_corner_northwest</c>, …) are <em>virtual variants</em> the engine synthesised by directionally
/// cropping the single <c>border_default</c> atlas. For phase 1 we render the full 9-slice frame for every
/// variant (visually close); the suffix→edge masking is a fidelity follow-up.
/// </summary>
public static class HudSkin
{
    /// <summary>The active skin folder name (QC <c>hud_skin</c>); default <c>luma</c>. Set by the manager
    /// when the <c>hud_skin</c> cvar changes (which also clears <see cref="TextureCache"/>).</summary>
    public static string SkinName { get; set; } = "luma";

    /// <summary>The Xolonium bold font (centerprint titles / emphasised text), wired by the host like
    /// <see cref="HudPanel.HudFont"/>. Falls back to the regular HUD font when null.</summary>
    public static Font? BoldFont { get; set; }

    /// <summary>
    /// Resolve a border/background image by its luma <paramref name="bg"/> name, trying the active skin then
    /// the default skin then the bare <c>border_default</c> atlas. Returns null when nothing resolves (caller
    /// draws a flat fallback). <c>""</c> and <c>"0"</c> are treated as "no image" by the caller, not here.
    /// </summary>
    public static Texture2D? ResolveBorderTex(string bg)
    {
        if (string.IsNullOrEmpty(bg) || bg == "0") return null;
        return TextureCache.GetFirst(
            $"gfx/hud/{SkinName}/{bg}",
            $"gfx/hud/default/{bg}",
            $"gfx/hud/{SkinName}/border_default",
            "gfx/hud/default/border_default");
    }

    /// <summary>
    /// Draw a 9-slice border picture into <paramref name="ci"/> over the (panel-local) <paramref name="rect"/>
    /// with corner thickness <paramref name="border"/> px, tinted <paramref name="color"/> at
    /// <paramref name="alpha"/>. The source texture is divided on the {0,.25,.75,1} UV grid (the Xonotic border
    /// atlas convention): the outer quarter on each side is the corner/edge, the central half stretches. When
    /// <paramref name="border"/> ≤ 0 the whole texture is stretched to the rect (no frame expansion). Returns
    /// false when no texture resolved so the caller can paint its translucent fallback.
    /// </summary>
    public static bool DrawBorderPicture(CanvasItem ci, Rect2 rect, string bg, Color color, float alpha, float border)
    {
        Texture2D? tex = ResolveBorderTex(bg);
        if (tex is null) return false;

        var mod = new Color(color.R, color.G, color.B, alpha);
        Vector2 ts = tex.GetSize();
        if (ts.X <= 0f || ts.Y <= 0f) return false;

        float x = rect.Position.X, y = rect.Position.Y, w = rect.Size.X, h = rect.Size.Y;
        if (w <= 0f || h <= 0f) return false;

        // No border, or the panel is too small to hold two corners → just stretch the whole atlas.
        float t = border;
        if (t <= 0f || w < 2f * t || h < 2f * t)
        {
            if (t > 0f) t = Mathf.Min(t, Mathf.Min(w, h) * 0.5f);
            if (t <= 0f)
            {
                ci.DrawTextureRect(tex, rect, false, mod);
                return true;
            }
        }

        // Destination columns/rows (px) and the source UV grid (fraction → px).
        float[] dx = { x, x + t, x + w - t, x + w };
        float[] dy = { y, y + t, y + h - t, y + h };
        float[] ux = { 0f, 0.25f, 0.75f, 1f };
        float[] uy = { 0f, 0.25f, 0.75f, 1f };

        for (int r = 0; r < 3; r++)
        for (int c = 0; c < 3; c++)
        {
            float dw = dx[c + 1] - dx[c];
            float dh = dy[r + 1] - dy[r];
            if (dw <= 0f || dh <= 0f) continue;
            var dst = new Rect2(dx[c], dy[r], dw, dh);
            var src = new Rect2(ux[c] * ts.X, uy[r] * ts.Y,
                                (ux[c + 1] - ux[c]) * ts.X, (uy[r + 1] - uy[r]) * ts.Y);
            ci.DrawTextureRectRegion(tex, dst, src, mod);
        }
        return true;
    }
}
