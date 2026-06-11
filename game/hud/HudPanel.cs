using System.Globalization;
using Godot;
using XonoticGodot.Game.Menu;   // MenuState.Cvars — the shared menu/console store (live console `set` reaches it)
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// Base for every on-screen HUD panel — the C# successor to QuakeC's <c>HUDPanel</c> class plus the
/// <c>REGISTER_HUD_PANEL</c> machinery (Base/.../qcsrc/client/hud/hud.qh + panel/*.qc). A panel is a Godot
/// <see cref="Control"/> whose <see cref="_Draw"/> issues immediate-mode draw calls in panel-local space
/// (origin = the panel's top-left), exactly as the QC panels added <c>panel_pos</c> to every draw call.
///
/// This is now skin- and cvar-aware (the C# port of the luma HUD skin + the <c>hud_panel_&lt;id&gt;_*</c> cvar
/// layout system, see planning/HUD_PARITY_CONTRACT.md):
/// <list type="bullet">
///   <item>Each panel <b>self-declares</b> its identity + look via virtuals (<see cref="PanelId"/>,
///         <see cref="DefaultLayout"/>, <see cref="ShowFlags"/>, <see cref="DefaultBg"/>…) that DEFAULT to the
///         luma reference table (<see cref="HudLayoutDefaults"/>). A panel needs zero extra code to land at the
///         correct Xonotic position with the correct skin frame — it is discovered by <see cref="HudRegistry"/>
///         and laid out from its cvars automatically.</item>
///   <item><see cref="LoadConfig"/> (called once per frame by <see cref="Hud"/>) resolves a <see cref="Cfg"/>
///         snapshot — pixel pos/size, bg image/color/alpha/border, padding, fg alpha, font size — from the
///         <c>hud_panel_&lt;id&gt;_*</c> cvars (with the QC "" = inherit / "0" = off rules) and pushes
///         <c>Position</c>/<c>Size</c>. Throttled: it only re-resolves when a <c>hud_*</c> cvar changed or the
///         viewport resized, so a live console <c>set</c> moves the panel immediately.</item>
///   <item><see cref="DrawBackground()"/> paints the skin 9-slice frame (<see cref="HudSkin"/>); text uses the
///         Xolonium <see cref="HudFont"/>.</item>
/// </list>
/// </summary>
public abstract partial class HudPanel : Control
{
    // ---- font (Xolonium), wired by the host where TextureCache.VfsResolver is set ----

    /// <summary>The Xolonium HUD font (QC the engine's <c>FONT_USER</c>). Set by the host (NetGame/GameDemo);
    /// falls back to Godot's default font until then so nothing crashes.</summary>
    public static Font? HudFont { get; set; }

    /// <summary>The font panels draw with — Xolonium when wired, else the engine fallback.</summary>
    protected static Font Font => HudFont ?? ThemeDB.FallbackFont;

    // ---- compile-time defaults kept for method signatures (the live values come from Cfg) ----

    /// <summary>Default text size in px used as the <c>DrawText(... , size = FontSize)</c> signature default.
    /// Live body text should prefer <see cref="PanelConfig2.FontSize"/> (<see cref="Cfg"/>).</summary>
    protected const int FontSize = 16;

    /// <summary>Legacy padding constant (compile-time default). Live code reads <see cref="PanelConfig2.Padding"/>.</summary>
    protected const float Padding = 6f;

    /// <summary>Live foreground/text color (white at the resolved fg alpha × fade). Was a constant; now tracks
    /// <see cref="LiveFgAlpha"/> so all panels honour <c>hud_panel_fg_alpha</c> and the HUD fade.</summary>
    protected Color FgColor => new(1f, 1f, 1f, LiveFgAlpha);

    /// <summary>Live panel background color (the resolved skin bg color at the resolved bg alpha × fade).</summary>
    protected Color BgColor => new(Cfg.BgColor.R, Cfg.BgColor.G, Cfg.BgColor.B, LiveBgAlpha);

    // =================================================================================================
    //  Self-declaration surface (defaults look themselves up in the luma table by PanelId)
    // =================================================================================================

    private string? _panelId;

    /// <summary>QC <c>panel.panel_name</c>. The cvar prefix is <c>hud_panel_</c> + this. Defaults to the class
    /// name minus a trailing <c>Panel</c>/<c>Hud</c>, lowercased (HealthArmorPanel → "healtharmor").</summary>
    public virtual string PanelId => _panelId ??= HudLayoutDefaults.DeriveId(GetType());

    /// <summary>QC <c>PANEL_SHOW_*</c> — the contexts this panel may draw in. Defaults to the luma table.</summary>
    public virtual PanelShow ShowFlags => HudLayoutDefaults.For(PanelId).Show;

    /// <summary>QC <c>PANEL_CONFIG_*</c> — whether the user may disable / the editor may move it.</summary>
    public virtual PanelConfig ConfigFlags => HudLayoutDefaults.For(PanelId).Config;

    /// <summary>Normalized 0..1 default pos/size (QC <c>hud_panel_&lt;id&gt;_pos/_size</c>). Defaults to luma.</summary>
    public virtual PanelLayoutDefault DefaultLayout(Vector2 viewport)
    {
        HudLayoutDefaults.Entry e = HudLayoutDefaults.For(PanelId);
        return new PanelLayoutDefault(e.Pos, e.Size);
    }

    /// <summary>Per-panel background-image name default ("" = inherit hud_panel_bg, "0" = none, else border_*).</summary>
    public virtual string DefaultBg => HudLayoutDefaults.For(PanelId).Bg;
    public virtual string DefaultBgColor => HudLayoutDefaults.For(PanelId).BgColor;
    public virtual string DefaultBgAlpha => HudLayoutDefaults.For(PanelId).BgAlpha;
    public virtual string DefaultBgBorder => HudLayoutDefaults.For(PanelId).Border;
    public virtual string DefaultEnable => HudLayoutDefaults.For(PanelId).Enable;

    /// <summary>Whether contents change every frame (health/ammo/timer/crosshair/killfeed). Unchanged.</summary>
    public virtual bool IsDynamic => true;

    /// <summary>
    /// Whether this panel's DISPLAYED content actually changed since its last draw (3.2-3). A dynamic panel is
    /// re-recorded (QueueRedraw) every frame by default, which re-runs <see cref="DrawPanel"/> (re-formatting
    /// strings + re-recording canvas commands) even when nothing visible changed — wasteful for readouts whose
    /// value updates ~1×/s (fps, timer, ping). Such panels override this to compare a cheap, alloc-free snapshot
    /// of what they would draw against the last frame's, returning false to skip the redraw. The default (true)
    /// preserves the every-frame redraw for panels that genuinely animate (health/ammo/crosshair/killfeed).
    /// </summary>
    public virtual bool NeedsRedraw() => true;

    // =================================================================================================
    //  Resolved per-frame config (QC HUD_Panel_LoadCvars → current_panel_*)
    // =================================================================================================

    /// <summary>The resolved, pre-fade per-panel state. Pixel space.</summary>
    protected readonly record struct PanelConfig2(
        Vector2 PosPx, Vector2 SizePx,
        string Bg, Color BgColor, float BgAlpha, float BgBorder, float Padding,
        float FgAlpha, int FontSize, bool Enabled);

    /// <summary>The current pre-fade snapshot (resolved by <see cref="LoadConfig"/>).</summary>
    protected PanelConfig2 Cfg { get; private set; } = new(
        Vector2.Zero, new Vector2(64, 64), "0", new Color(0.10f, 0.14f, 0.25f), 1f, 2f, 3f, 0.9f, 16, true);

    /// <summary>Post-fade background alpha for this frame (<c>Cfg.BgAlpha × hud_fade × panel_fade</c>).</summary>
    protected float LiveBgAlpha { get; private set; } = 0.45f;

    /// <summary>Post-fade foreground alpha for this frame (<c>Cfg.FgAlpha × hud_fade × panel_fade</c>).</summary>
    protected float LiveFgAlpha { get; private set; } = 0.9f;

    /// <summary>The panel's screen rectangle in absolute pixels. Mirrors QC <c>panel_pos</c>/<c>panel_size</c>.</summary>
    public Rect2 PanelRect { get; private set; }

    /// <summary>The panel's local content size (its rect size). Convenience for draw code.</summary>
    protected Vector2 Size2 => Cfg.SizePx;

    private bool _configDirty = true;
    private Vector2 _lastViewport = new(-1, -1);
    private bool _subscribed;

    public override void _EnterTree()
    {
        // React to live cvar edits (console `set hud_*` / menu sliders) by marking the snapshot dirty so the
        // next LoadConfig re-resolves immediately. Leak-safe: unsubscribed in _ExitTree.
        if (!_subscribed)
        {
            MenuState.Cvars.Changed += OnCvarChanged;
            _subscribed = true;
        }
    }

    public override void _ExitTree()
    {
        if (_subscribed)
        {
            MenuState.Cvars.Changed -= OnCvarChanged;
            _subscribed = false;
        }
    }

    private void OnCvarChanged(string name)
    {
        if (name.StartsWith("hud_", System.StringComparison.Ordinal))
            _configDirty = true;
    }

    /// <summary>Force the next <see cref="LoadConfig"/> to fully re-resolve (e.g. on viewport resize).</summary>
    public void InvalidateConfig() => _configDirty = true;

    /// <summary>
    /// Resolve pos/size/bg/alpha/font from the <c>hud_panel_&lt;id&gt;_*</c> cvars (throttled) and push
    /// <c>Position</c>/<c>Size</c>; then re-apply the HUD fade to the live alphas every frame so fades animate
    /// between throttled reloads. <paramref name="fadeAlpha"/> = global HUD fade (menu), <paramref name="panelFade"/>
    /// = this panel's extra fade (scoreboard cross-fade).
    /// </summary>
    public void LoadConfig(Vector2 viewport, float fadeAlpha, float panelFade)
    {
        if (_configDirty || viewport != _lastViewport)
        {
            Cfg = Resolve(viewport);
            _lastViewport = viewport;
            _configDirty = false;

            PanelRect = new Rect2(Cfg.PosPx, Cfg.SizePx);
            Position = Cfg.PosPx;
            Size = Cfg.SizePx;
        }

        float fade = Mathf.Clamp(fadeAlpha * panelFade, 0f, 1f);
        LiveBgAlpha = Cfg.BgAlpha * fade;
        LiveFgAlpha = Cfg.FgAlpha * fade;
    }

    private PanelConfig2 Resolve(Vector2 viewport)
    {
        PanelLayoutDefault def = DefaultLayout(viewport);

        Vector2 posF = ParseVec2(CvarStr("pos"), def.PosFraction);
        Vector2 sizeF = ParseVec2(CvarStr("size"), def.SizeFraction);
        Vector2 posPx = new(posF.X * viewport.X, posF.Y * viewport.Y);
        Vector2 sizePx = new(Mathf.Max(8f, sizeF.X * viewport.X), Mathf.Max(8f, sizeF.Y * viewport.Y));

        // bg name: "" inherits the global hud_panel_bg; "0" = no frame.
        string bg = CvarStr("bg");
        if (string.IsNullOrWhiteSpace(bg)) bg = GlobalStr("hud_panel_bg");
        if (string.IsNullOrWhiteSpace(bg)) bg = "0";

        // bg color: "" inherits hud_panel_bg_color; "shirt"/"pants" not yet supported → inherit.
        string bgColStr = CvarStr("bg_color");
        if (string.IsNullOrWhiteSpace(bgColStr) || bgColStr == "shirt" || bgColStr == "pants")
            bgColStr = GlobalStr("hud_panel_bg_color");
        Color bgCol = TryParseRgb(bgColStr, out Color c) ? c : new Color(0f, 0.14f, 0.25f);

        float bgAlpha = CvarF("bg_alpha", GlobalF("hud_panel_bg_alpha", 1f));
        float bgBorder = CvarF("bg_border", GlobalF("hud_panel_bg_border", 2f));
        float padding = CvarF("bg_padding", GlobalF("hud_panel_bg_padding", 3f));
        padding = Mathf.Min(Mathf.Min(sizePx.X, sizePx.Y) * 0.5f - 5f, padding);
        if (padding < 0f) padding = 0f;

        float fgAlpha = CvarF("fg_alpha", GlobalF("hud_panel_fg_alpha", 1f));

        // Font size scales with the viewport: hud_fontsize is authored at a ~800px reference width.
        float baseFont = CvarF("fontsize", GlobalF("hud_fontsize", 11f));
        float refW = GlobalF("hud_width", 0f);
        if (refW <= 0f) refW = 800f;
        int fontPx = Mathf.Max(8, Mathf.RoundToInt(baseFont * viewport.X / refW));

        bool enabled = !ConfigFlags.HasFlag(PanelConfig.CanBeOff) ||
                       MenuState.Cvars.GetFloat("hud_panel_" + PanelId) != 0f;

        return new PanelConfig2(posPx, sizePx, bg, bgCol, bgAlpha, bgBorder, padding, fgAlpha, fontPx, enabled);
    }

    // -------------------------------------------------------------------------------------------------
    //  Live cvar accessors (MenuState.Cvars — same store the console + menu dialogs write)
    // -------------------------------------------------------------------------------------------------

    /// <summary>Read <c>hud_panel_&lt;id&gt;_&lt;suffix&gt;</c> as a raw string ("" when unset).</summary>
    protected string CvarStr(string suffix) => MenuState.Cvars.GetString($"hud_panel_{PanelId}_{suffix}");

    /// <summary>Read <c>hud_panel_&lt;id&gt;_&lt;suffix&gt;</c> as a float (unset → <paramref name="fallback"/>).</summary>
    protected float CvarF(string suffix, float fallback)
    {
        string s = MenuState.Cvars.GetString($"hud_panel_{PanelId}_{suffix}");
        return string.IsNullOrWhiteSpace(s) ? fallback : MenuState.Cvars.GetFloat($"hud_panel_{PanelId}_{suffix}");
    }

    /// <summary>Read <c>hud_panel_&lt;id&gt;_&lt;suffix&gt;</c> as a bool (non-zero).</summary>
    protected bool CvarBool(string suffix) => CvarF(suffix, 0f) != 0f;

    /// <summary>Read a global cvar by full name.</summary>
    protected static string GlobalStr(string name) => MenuState.Cvars.GetString(name);

    /// <summary>Read a global cvar as float (unset → <paramref name="fallback"/>).</summary>
    protected static float GlobalF(string name, float fallback)
    {
        string s = MenuState.Cvars.GetString(name);
        return string.IsNullOrWhiteSpace(s) ? fallback : MenuState.Cvars.GetFloat(name);
    }

    /// <summary>Parse a space-separated "r g b" (0..1) cvar string.</summary>
    protected static bool TryParseRgb(string s, out Color c)
    {
        c = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        string[] p = s.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 3) return false;
        if (!float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float r)) return false;
        if (!float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float g)) return false;
        if (!float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float b)) return false;
        c = new Color(r, g, b);
        return true;
    }

    private static Vector2 ParseVec2(string s, Vector2 fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        string[] p = s.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 2) return fallback;
        if (!float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)) return fallback;
        if (!float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)) return fallback;
        return new Vector2(x, y);
    }

    // -------------------------------------------------------------------------------------------------
    //  Legacy layout shim (NetGame.cs still calls _scoreboard.Configure; LoadConfig wins the next frame).
    // -------------------------------------------------------------------------------------------------

    /// <summary>Place + size the panel directly (legacy path; <see cref="LoadConfig"/> overrides it next frame
    /// for cvar-driven panels). Kept so external callers (the net layer's scoreboard sizing) still compile.</summary>
    public void Configure(Rect2 rect)
    {
        PanelRect = rect;
        Position = rect.Position;
        Size = rect.Size;
        QueueRedraw();
    }

    public override void _Draw() => DrawPanel();

    /// <summary>Draw the panel contents in panel-local space (origin = top-left). Successor to each QC
    /// <c>HUD_&lt;Name&gt;(bool should_draw)</c> body. Bail early when there's nothing to show.</summary>
    protected abstract void DrawPanel();

    // -------------------------------------------------------------------------------------------------
    //  Shared draw helpers (the modernized stand-ins for draw.qh's drawpic/drawfill/drawstring family).
    //  All take panel-local coordinates and draw with the Xolonium font.
    // -------------------------------------------------------------------------------------------------

    /// <summary>Paint the panel chrome: the skin 9-slice border frame (QC <c>HUD_Panel_DrawBg</c>). No-op when
    /// the resolved bg is "0"/inherit-of-0. Flat translucent fallback only when the border texture is missing.</summary>
    protected void DrawBackground()
    {
        string bg = Cfg.Bg;
        if (string.IsNullOrEmpty(bg) || bg == "0") return; // luma: no frame for this panel
        var col = new Color(Cfg.BgColor.R, Cfg.BgColor.G, Cfg.BgColor.B);
        float t = Cfg.BgBorder;
        var outer = new Rect2(-t, -t, Size2.X + 2f * t, Size2.Y + 2f * t);
        if (!HudSkin.DrawBorderPicture(this, outer, bg, col, LiveBgAlpha, t))
            DrawRect(new Rect2(Vector2.Zero, Size2), new Color(col.R, col.G, col.B, LiveBgAlpha * 0.7f));
    }

    /// <summary>Paint a flat translucent fill over an arbitrary local rect (inner sub-region backing — the
    /// legacy behavior, NOT the skin frame). Tracks the live bg alpha.</summary>
    protected void DrawBackground(Rect2 local)
        => DrawRect(local, new Color(0.10f, 0.10f, 0.12f, 0.45f * Mathf.Clamp(LiveBgAlpha / 0.45f, 0f, 1f)));

    /// <summary>Draw a skin/art pic by bare VFS name (skin → default fall-through). Returns false on miss.</summary>
    protected bool DrawSkinPic(string bareName, Rect2 local, Color modulate)
        => DrawSkinPicFirst(local, modulate,
            $"gfx/hud/{HudSkin.SkinName}/{bareName}", $"gfx/hud/default/{bareName}");

    /// <summary>Draw the first art pic in <paramref name="candidates"/> that resolves. Returns false on miss.</summary>
    protected bool DrawSkinPicFirst(Rect2 local, Color modulate, params string?[] candidates)
    {
        Texture2D? tex = TextureCache.GetFirst(candidates);
        if (tex is null) return false;
        DrawTextureRect(tex, local, false, modulate);
        return true;
    }

    /// <summary>Draw a horizontal progress bar (QC <c>HUD_Panel_DrawProgressBar</c>).</summary>
    protected void DrawBar(Rect2 area, float fraction, Color fill)
    {
        fraction = Mathf.Clamp(fraction, 0f, 1f);
        DrawRect(area, new Color(0f, 0f, 0f, 0.35f));
        if (fraction > 0f)
            DrawRect(new Rect2(area.Position, new Vector2(area.Size.X * fraction, area.Size.Y)), fill);
        DrawRect(area, new Color(1f, 1f, 1f, 0.15f), filled: false, width: 1f);
    }

    /// <summary>Draw left-aligned text at a panel-local baseline-top position (with a subtle drop shadow so it
    /// reads over the world the way Xonotic's outlined HUD font does).</summary>
    protected void DrawText(Vector2 pos, string text, Color color, int size = FontSize)
    {
        if (string.IsNullOrEmpty(text)) return;
        Vector2 at = pos + new Vector2(0f, size);
        DrawString(Font, at + new Vector2(1f, 1f), text, HorizontalAlignment.Left, -1f, size, ShadowOf(color));
        DrawString(Font, at, text, HorizontalAlignment.Left, -1f, size, color);
    }

    /// <summary>Draw text horizontally centered within <paramref name="width"/> (QC align 0.5).</summary>
    protected void DrawTextCentered(Vector2 pos, float width, string text, Color color, int size = FontSize)
    {
        if (string.IsNullOrEmpty(text)) return;
        Vector2 at = pos + new Vector2(0f, size);
        DrawString(Font, at + new Vector2(1f, 1f), text, HorizontalAlignment.Center, width, size, ShadowOf(color));
        DrawString(Font, at, text, HorizontalAlignment.Center, width, size, color);
    }

    /// <summary>Draw text right-aligned to end at <paramref name="rightX"/> (panel-local).</summary>
    protected void DrawTextRight(float rightX, float topY, float width, string text, Color color, int size = FontSize)
    {
        if (string.IsNullOrEmpty(text)) return;
        Vector2 at = new(rightX - width, topY + size);
        DrawString(Font, at + new Vector2(1f, 1f), text, HorizontalAlignment.Right, width, size, ShadowOf(color));
        DrawString(Font, at, text, HorizontalAlignment.Right, width, size, color);
    }

    private static Color ShadowOf(Color c) => new(0f, 0f, 0f, c.A * 0.7f);

    /// <summary>Measure a string's pixel width at the given size (QC <c>stringwidth</c>).</summary>
    protected static float MeasureText(string text, int size = FontSize)
        => string.IsNullOrEmpty(text)
            ? 0f
            : Font.GetStringSize(text, HorizontalAlignment.Left, -1f, size).X;

    // ---- color helpers (QC HUD_Get_Num_Color: tint a value by how low it is) ----

    /// <summary>Color a resource number by its fraction of max (QC <c>HUD_Get_Num_Color</c>).</summary>
    protected Color NumColor(float value, float max)
    {
        if (max <= 0f) return FgColor;
        float f = Mathf.Clamp(value / max, 0f, 1f);
        Color c = f >= 0.5f
            ? new Color(1f, 1f, Mathf.Lerp(0.4f, 1f, (f - 0.5f) * 2f))
            : new Color(1f, Mathf.Lerp(0.1f, 1f, f * 2f), 0.1f);
        c.A = LiveFgAlpha;
        return c;
    }

    /// <summary>Convert a sim-side (System.Numerics) color vector to a Godot <see cref="Color"/>.</summary>
    protected static Color ToColor(NVec3 c, float alpha = 1f) => new(c.X, c.Y, c.Z, alpha);

    /// <summary>Format a whole-seconds duration as M:SS (QC <c>seconds_tostring</c>).</summary>
    protected static string SecondsToString(float seconds)
    {
        if (seconds < 0f) seconds = 0f;
        int total = (int)seconds;
        return $"{total / 60}:{total % 60:D2}";
    }
}
