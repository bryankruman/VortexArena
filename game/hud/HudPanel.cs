using Godot;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// Base for every on-screen HUD panel — the C# successor to QuakeC's <c>HUDPanel</c> entity class and
/// the <c>REGISTER_HUD_PANEL</c> machinery (Base/.../qcsrc/client/hud/hud.qh + panel/*.qc). In QC a
/// panel was an edict carrying a <c>panel_pos</c>/<c>panel_size</c> rect, a background, and a
/// <c>.panel_draw(bool should_draw)</c> callback that all the <c>drawpic</c>/<c>drawstring</c> calls hung
/// off of. Here a panel is a Godot <see cref="Control"/> whose <see cref="_Draw"/> override issues the
/// equivalent <c>DrawRect</c>/<c>DrawString</c> immediate-mode calls.
///
/// We are modernizing rather than porting 1:1: the cvar-driven skin system, the in-game HUD editor
/// (hud_config.qc), per-panel background pictures and the dynamic-follow shake are all dropped. What
/// remains is the layout contract — anchor a rect to a screen edge, optionally paint a translucent
/// background, then draw bars/text/icons inside it — which is what the panel subclasses need.
///
/// Layout model (QC <c>HUD_Panel_UpdatePosSize</c> used normalized 0..1 cvars scaled by the viewport):
/// each panel is given a <see cref="PanelRect"/> in absolute pixels by <see cref="Hud"/> via
/// <see cref="Configure"/>. The Control's own <c>Position</c>/<c>Size</c> are set to that rect so child
/// draw coordinates are panel-local (origin at the panel's top-left), matching how the QC panels added
/// <c>panel_pos</c> to every draw call.
/// </summary>
public abstract partial class HudPanel : Control
{
    // ---- shared visual constants (replacing the QC hud_panel_* skin cvars) ----

    /// <summary>Default translucent panel background (QC panel_bg_color @ panel_bg_alpha).</summary>
    protected static readonly Color BgColor = new(0.10f, 0.10f, 0.12f, 0.45f);

    /// <summary>Default foreground/text color (QC '1 1 1' * panel_fg_alpha).</summary>
    protected static readonly Color FgColor = new(1f, 1f, 1f, 0.9f);

    /// <summary>Padding inside the panel rect before content is laid out (QC panel_bg_padding).</summary>
    protected const float Padding = 6f;

    /// <summary>Default text size in pixels for panel labels.</summary>
    protected const int FontSize = 16;

    /// <summary>
    /// Whether this panel's contents change every frame (health, ammo, timer, crosshair, killfeed) and so
    /// must be redrawn continuously, vs. a panel that only changes when its data is pushed (scoreboard).
    /// <see cref="Hud"/> calls <c>QueueRedraw()</c> on dynamic panels each
    /// <c>_Process</c>. Subclasses that animate or read live values should keep this <c>true</c>.
    /// </summary>
    public virtual bool IsDynamic => true;

    /// <summary>
    /// The panel's screen rectangle in absolute pixels. Mirrors QC <c>panel_pos</c>/<c>panel_size</c>.
    /// Kept in sync with the Control's <c>Position</c>/<c>Size</c> by <see cref="Configure"/>; draw code
    /// works in panel-local space (top-left = origin), so most subclasses only read <see cref="Size2"/>.
    /// </summary>
    public Rect2 PanelRect { get; private set; }

    /// <summary>The panel's local content size (its rect size). Convenience for draw code.</summary>
    protected Vector2 Size2 => PanelRect.Size;

    /// <summary>
    /// Place + size the panel (QC <c>HUD_Panel_UpdatePosSize</c> + <c>HUD_Panel_ScalePosSize</c>). The
    /// <see cref="Hud"/> computes the absolute rect from the viewport and an anchor; we store it and
    /// push it onto the Control transform so child draw coords are panel-local.
    /// </summary>
    public void Configure(Rect2 rect)
    {
        PanelRect = rect;
        Position = rect.Position;
        Size = rect.Size;
        QueueRedraw();
    }

    /// <summary>
    /// Godot immediate-mode draw entry point. We keep the engine override thin and delegate to
    /// <see cref="DrawPanel"/> so subclasses implement one clearly-named method (the analogue of the QC
    /// <c>panel_draw</c> callback). Coordinates passed to the helpers are panel-local.
    /// </summary>
    public override void _Draw() => DrawPanel();

    /// <summary>
    /// Draw the panel contents in panel-local space (origin = top-left of <see cref="PanelRect"/>). The
    /// successor to each QC <c>HUD_&lt;Name&gt;(bool should_draw)</c> body. Implementations should bail
    /// early when they have nothing to show (no player, dead, empty queue) just like the QC
    /// <c>should_draw</c>/<c>spectatee_status</c> guards.
    /// </summary>
    protected abstract void DrawPanel();

    // -------------------------------------------------------------------------------------------------
    //  Shared draw helpers (the modernized stand-ins for draw.qh's drawpic/drawfill/drawstring family).
    //  All take panel-local coordinates.
    // -------------------------------------------------------------------------------------------------

    /// <summary>Paint the panel background over the whole rect (QC <c>HUD_Panel_DrawBg</c>).</summary>
    protected void DrawBackground() => DrawBackground(new Rect2(Vector2.Zero, Size2));

    /// <summary>Paint a translucent rounded-feel background over an arbitrary local rect.</summary>
    protected void DrawBackground(Rect2 local) => DrawRect(local, BgColor);

    /// <summary>
    /// Draw a horizontal progress bar (QC <c>HUD_Panel_DrawProgressBar</c>): a dim track plus a filled
    /// portion clamped to <paramref name="fraction"/> in [0,1], tinted <paramref name="fill"/>.
    /// </summary>
    protected void DrawBar(Rect2 area, float fraction, Color fill)
    {
        fraction = Mathf.Clamp(fraction, 0f, 1f);
        // track
        DrawRect(area, new Color(0f, 0f, 0f, 0.35f));
        if (fraction > 0f)
        {
            var filled = new Rect2(area.Position, new Vector2(area.Size.X * fraction, area.Size.Y));
            DrawRect(filled, fill);
        }
        // thin border for readability
        DrawRect(area, new Color(1f, 1f, 1f, 0.15f), filled: false, width: 1f);
    }

    /// <summary>Draw left-aligned text at a panel-local baseline-top position.</summary>
    protected void DrawText(Vector2 pos, string text, Color color, int size = FontSize)
    {
        if (string.IsNullOrEmpty(text)) return;
        DrawString(ThemeDB.FallbackFont, pos + new Vector2(0f, size), text,
            HorizontalAlignment.Left, -1f, size, color);
    }

    /// <summary>
    /// Draw text horizontally centered within <paramref name="width"/> starting at <paramref name="pos"/>
    /// (QC drawstring_aspect with an align factor of 0.5). Godot's <c>DrawString</c> centers within the
    /// given width when <see cref="HorizontalAlignment.Center"/> is used.
    /// </summary>
    protected void DrawTextCentered(Vector2 pos, float width, string text, Color color, int size = FontSize)
    {
        if (string.IsNullOrEmpty(text)) return;
        DrawString(ThemeDB.FallbackFont, pos + new Vector2(0f, size), text,
            HorizontalAlignment.Center, width, size, color);
    }

    /// <summary>Draw text right-aligned to end at <paramref name="rightX"/> (panel-local).</summary>
    protected void DrawTextRight(float rightX, float topY, float width, string text, Color color, int size = FontSize)
    {
        if (string.IsNullOrEmpty(text)) return;
        DrawString(ThemeDB.FallbackFont, new Vector2(rightX - width, topY + size), text,
            HorizontalAlignment.Right, width, size, color);
    }

    /// <summary>Measure a string's pixel width at the given size (QC <c>stringwidth</c>).</summary>
    protected static float MeasureText(string text, int size = FontSize)
        => string.IsNullOrEmpty(text)
            ? 0f
            : ThemeDB.FallbackFont.GetStringSize(text, HorizontalAlignment.Left, -1f, size).X;

    // ---- color helpers (QC HUD_Get_Num_Color: tint a value by how low it is) ----

    /// <summary>
    /// Color a resource number by its fraction of max (QC <c>HUD_Get_Num_Color</c>): white when healthy,
    /// shading toward yellow then red as it drops. Used for health/armor/ammo readouts.
    /// </summary>
    protected static Color NumColor(float value, float max)
    {
        if (max <= 0f) return FgColor;
        float f = Mathf.Clamp(value / max, 0f, 1f);
        // >=0.5 white->yellow-ish, <0.5 yellow->red.
        Color c = f >= 0.5f
            ? new Color(1f, 1f, Mathf.Lerp(0.4f, 1f, (f - 0.5f) * 2f))
            : new Color(1f, Mathf.Lerp(0.1f, 1f, f * 2f), 0.1f);
        c.A = FgColor.A;
        return c;
    }

    /// <summary>Convert a sim-side (System.Numerics) color vector to a Godot <see cref="Color"/>.</summary>
    protected static Color ToColor(NVec3 c, float alpha = 1f) => new(c.X, c.Y, c.Z, alpha);

    /// <summary>Format a whole-seconds duration as M:SS (QC <c>seconds_tostring</c>).</summary>
    protected static string SecondsToString(float seconds)
    {
        if (seconds < 0f) seconds = 0f;
        int total = (int)seconds;
        int m = total / 60;
        int s = total % 60;
        return $"{m}:{s:D2}";
    }
}
