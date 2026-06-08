using Godot;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;
using SC = XonoticGodot.Engine.Collision.SuperContents;

namespace XonoticGodot.Game.Client;

/// <summary>
/// The full-screen view post-effects overlay — the C# successor to the 2D <c>drawfill</c> screen tints in
/// QuakeC's <c>view.qc</c> (<c>HUD_Damage</c> and <c>HUD_Contents</c>,
/// Base/data/xonotic-data.pk3dir/qcsrc/client/view.qc). CSQC drew these as a single full-screen quad per
/// frame whose alpha it animated; here a <see cref="CanvasLayer"/> hosts one full-screen
/// <see cref="ColorRect"/> per effect and we animate <see cref="ColorRect.Color"/>'s alpha each frame.
///
/// Two effects are ported:
/// <list type="bullet">
///   <item><b>Damage red-flash</b> (<c>HUD_Damage</c>): an accumulator <c>myhealth_flash</c> that rises with
///         each chunk of damage taken (<c>dmg_take</c>) and decays over time; the visible alpha is the
///         accumulator minus a pain threshold. When dead (<c>myhealth &lt; 1</c>) it ramps up to a steady
///         death-fade. Colour is <c>hud_damage_color</c> (default red).</item>
///   <item><b>Liquid tint</b> (<c>HUD_Contents</c>): a low-passed <c>contentavgalpha</c> that fades in/out as
///         the eye enters/leaves a liquid, tinting the screen with the per-content colour
///         (water = blue, slime = green, lava = orange).</item>
/// </list>
///
/// Wiring (mirrors how the HUD reads live local-player state): the host adds one <see cref="ViewEffects"/>
/// to the scene next to the HUD, calls <see cref="ReportDamage"/> on the pain edge (a drop in the local
/// player's health), and each frame calls <see cref="UpdateEffects"/> with the player's current health and
/// the SUPERCONTENTS at the eye. The overlay then decays/animates itself.
///
/// What is NOT ported: the GLSL blur/sharpen post-process (<c>damage_blurpostprocess</c> /
/// <c>content_blurpostprocess</c> → <c>r_glsl_postprocess_uservec*</c>) and night-vision — those drive
/// engine shader cvars that have no Godot analogue here. The essential coloured screen reaction is faithful.
/// </summary>
public partial class ViewEffects : CanvasLayer
{
    // ColorRects stretched across the whole viewport; their alpha is what we animate (the RGB is the tint).
    private ColorRect _damage = null!;
    private ColorRect _contents = null!;

    // ---- HUD_Damage state (view.qc: myhealth / myhealth_flash / myhealth_prev) ----
    private float _myHealthFlash;   // the decaying damage accumulator
    private float _myHealthPrev;    // last frame's health (to detect respawn / death edges)
    private float _time;            // frametime-accumulated clock — QC's `time` in the pain-threshold sin-pulse

    // dmg_take accumulated between Update calls (QC reads the per-tick .dmg_take stat; we sum reported pain).
    private float _pendingDamage;

    // ---- HUD_Contents state (view.qc: contentavgalpha / liquidalpha_prev / liquidcolor_prev) ----
    private float _contentAvgAlpha;
    private float _liquidAlphaPrev;
    private Color _liquidColorPrev = new(0f, 0f, 0f);

    // -------------------------------------------------------------------------------------------------
    //  autocvar defaults (faithful to the cvar declarations these effects read in view.qc / _hud.cfg).
    //  Read live from the cvar service when set (the match loads the real Xonotic cfg tree); these are the
    //  fallbacks for standalone use, and for cvars GetFloat() returns 0 for when "0 means default" applies.
    // -------------------------------------------------------------------------------------------------
    // Defaults faithful to _hud_common.cfg:297-306 (the cvar declarations HUD_Damage reads).
    private const float DefDamageFactor       = 0.025f; // hud_damage_factor
    private const float DefDamageMaxAlpha      = 1.5f;   // hud_damage_maxalpha
    private const float DefDamageFadeRate      = 0.75f;  // hud_damage_fade_rate (per second)
    private const float DefDamagePainThreshold = 0.1f;   // hud_damage_pain_threshold
    private const float DefDamageGlobalAlpha   = 0.55f;  // hud_damage (master multiplier)
    // Near-death pain-threshold pulsing (_hud_common.cfg:307-310).
    private const float DefDamagePainThresholdLower       = 1.25f; // hud_damage_pain_threshold_lower
    private const float DefDamagePainThresholdLowerHealth = 50f;   // hud_damage_pain_threshold_lower_health
    private const float DefDamagePainPulsatingMin         = 0.6f;  // hud_damage_pain_threshold_pulsating_min
    private const float DefDamagePainPulsatingPeriod      = 0.8f;  // hud_damage_pain_threshold_pulsating_period

    private const float DefContentsWaterAlpha  = 0.5f;   // hud_contents_water_alpha
    private const float DefContentsLavaAlpha   = 0.7f;   // hud_contents_lava_alpha
    private const float DefContentsSlimeAlpha  = 0.7f;   // hud_contents_slime_alpha
    private const float DefContentsFadeIn      = 0.02f;  // hud_contents_fadeintime  (fast in)
    private const float DefContentsFadeOut     = 0.25f;  // hud_contents_fadeouttime (slow out)

    private static readonly Color WaterColor = new(0.4f, 0.6f, 1.0f);  // hud_contents_water_color  "0.4 0.6 1.0"
    private static readonly Color LavaColor  = new(1.0f, 0.3f, 0.0f);  // hud_contents_lava_color   "1.0 0.3 0.0"
    private static readonly Color SlimeColor = new(0.0f, 0.8f, 0.0f);  // hud_contents_slime_color  "0.0 0.8 0.0"
    private static readonly Color DefDamageColor = new(1.0f, 0.0f, 0.0f); // hud_damage_color "1 0 0" (_hud_common.cfg:302)

    public override void _Ready()
    {
        // Sit above the 3D view but below the HUD if the HUD's CanvasLayer has a higher layer (HUD defaults to 0;
        // we use a negative layer so health/ammo/crosshair composite ON TOP of the tint, matching CSQC draw order
        // where HUD_Damage/HUD_Contents run before the HUD panels but the crosshair draws last).
        Layer = -1;

        _contents = MakeFullScreenRect("ContentsTint");
        _damage = MakeFullScreenRect("DamageFlash");
        AddChild(_contents);
        AddChild(_damage); // damage on top of the liquid tint

        _myHealthPrev = 0f;
    }

    private static ColorRect MakeFullScreenRect(string name) => new()
    {
        Name = name,
        // Full-rect anchors so it tracks any window resize (QC redrew against vid_conwidth each frame).
        AnchorRight = 1f,
        AnchorBottom = 1f,
        OffsetLeft = 0f,
        OffsetTop = 0f,
        OffsetRight = 0f,
        OffsetBottom = 0f,
        Color = new Color(0f, 0f, 0f, 0f),
        // Never eat input — the overlay is purely cosmetic (QC hud_cursormode off; HUD never grabs).
        MouseFilter = Control.MouseFilterEnum.Ignore,
    };

    /// <summary>
    /// Report a chunk of damage just taken by the local player (QC <c>.dmg_take</c> on a stat update). The
    /// host calls this on the pain edge — typically a drop in the local player's <see cref="Entity.Health"/>.
    /// Accumulates until the next <see cref="UpdateEffects"/> folds it into the flash. Negative/zero is ignored.
    /// </summary>
    public void ReportDamage(float amount)
    {
        if (amount > 0f)
            _pendingDamage += amount;
    }

    /// <summary>
    /// Advance both screen effects one frame and repaint the overlays. Port of the per-frame bodies of
    /// <c>HUD_Damage</c> and <c>HUD_Contents</c> in view.qc.
    /// </summary>
    /// <param name="dt">Frame time in seconds (QC <c>frametime</c> / <c>drawframetime</c>).</param>
    /// <param name="health">The local player's current health (QC <c>STAT(HEALTH)</c>).</param>
    /// <param name="eyeContents">SUPERCONTENTS bitmask at the eye (QC <c>pointcontents(view_origin)</c>).</param>
    /// <param name="dead">True if the local player is dead/observing — drives the death fade.</param>
    public void UpdateEffects(float dt, float health, int eyeContents, bool dead)
    {
        if (dt <= 0f)
            dt = 0.0166667f; // assume 60fps before we know the real frametime (QC drawframetime default)

        UpdateDamage(dt, health);
        UpdateContents(dt, eyeContents);
    }

    // -------------------------------------------------------------------------------------------------
    //  HUD_Damage (view.qc ~1237): myhealth_flash accumulate + decay; alpha = flash - pain_threshold.
    // -------------------------------------------------------------------------------------------------
    private void UpdateDamage(float dt, float health)
    {
        // Tint colour through the cvar (QC stov(autocvar_hud_damage_color), _hud_common.cfg:302), like the
        // liquid-tint sibling colours, instead of a hardcoded red.
        Color damageColor = CvarColor("hud_damage_color", DefDamageColor);

        float global = Cvar("hud_damage", DefDamageGlobalAlpha);
        if (global <= 0f)
        {
            // hud_damage 0 disables the effect entirely (QC early-returns); clear and bail.
            _pendingDamage = 0f;
            _myHealthFlash = 0f;
            SetAlpha(_damage, damageColor, 0f);
            _myHealthPrev = health;
            return;
        }

        float fadeRate = Cvar("hud_damage_fade_rate", DefDamageFadeRate);
        float factor   = Cvar("hud_damage_factor", DefDamageFactor);
        float maxAlpha = Cvar("hud_damage_maxalpha", DefDamageMaxAlpha);
        if (maxAlpha <= 0f) maxAlpha = DefDamageMaxAlpha;
        float painThreshold = Cvar("hud_damage_pain_threshold", DefDamagePainThreshold);

        float myhealth = health;

        // fade out, then add the new damage (QC: myhealth_flash drifts down each frame, jumps up on dmg_take).
        _myHealthFlash = Mathf.Max(0f, _myHealthFlash - fadeRate * dt);
        _myHealthFlash = Mathf.Clamp(_myHealthFlash + _pendingDamage * factor, 0f, maxAlpha);
        _pendingDamage = 0f;

        // Near-death pulsating pain-threshold (QC view.qc:1256-1264): as health drops below
        // hud_damage_pain_threshold_lower_health (50), subtract a sin-pulse from pain_threshold scaled by how
        // close to 0 health we are. A NEGATIVE pain_threshold then makes the flash persist (a low-health throb).
        // _time advances by frametime — the faithful analogue of QC's `time` in sin(PI*time/period).
        _time += dt;
        float painThresholdLower = Cvar("hud_damage_pain_threshold_lower", DefDamagePainThresholdLower);
        float painThresholdLowerHealth = Cvar("hud_damage_pain_threshold_lower_health", DefDamagePainThresholdLowerHealth);
        if (painThresholdLower != 0f && myhealth < painThresholdLowerHealth)
        {
            float pulsatingMin = Cvar("hud_damage_pain_threshold_pulsating_min", DefDamagePainPulsatingMin);
            float pulsatingPeriod = Cvar("hud_damage_pain_threshold_pulsating_period", DefDamagePainPulsatingPeriod);
            painThreshold -= Mathf.Max(pulsatingMin, Mathf.Abs(Mathf.Sin(Mathf.Pi * _time / pulsatingPeriod)))
                * painThresholdLower * (1f - Mathf.Max(0f, myhealth) / painThresholdLowerHealth);
        }

        float flashTemp = Mathf.Clamp(_myHealthFlash - painThreshold, 0f, 1f);

        // Respawn / death edges (QC: myhealth_prev < 1 branch).
        if (_myHealthPrev < 1f)
        {
            if (myhealth >= 1f)
            {
                // just (re)spawned — clear the flash so a fresh spawn doesn't bleed red.
                _myHealthFlash = 0f;
                flashTemp = 0f;
            }
            else
            {
                // still dead — ramp the flash up toward a steady death fade.
                _myHealthFlash += fadeRate * dt;
            }
        }

        _myHealthPrev = myhealth;

        SetAlpha(_damage, damageColor, flashTemp * global);
    }

    // -------------------------------------------------------------------------------------------------
    //  HUD_Contents (view.qc ~1167): low-pass contentavgalpha toward in/out of liquid; tint by content.
    // -------------------------------------------------------------------------------------------------
    private void UpdateContents(float dt, int contents)
    {
        float liquidAlpha;
        Color liquidColor;
        int inContent;

        // pointcontents() → the dominant liquid (QC switch on CONTENT_WATER/LAVA/SLIME).
        if ((contents & SC.Lava) != 0)
        {
            liquidAlpha = Cvar("hud_contents_lava_alpha", DefContentsLavaAlpha);
            liquidColor = LavaColor;
            inContent = 1;
        }
        else if ((contents & SC.Slime) != 0)
        {
            liquidAlpha = Cvar("hud_contents_slime_alpha", DefContentsSlimeAlpha);
            liquidColor = SlimeColor;
            inContent = 1;
        }
        else if ((contents & SC.Water) != 0)
        {
            liquidAlpha = Cvar("hud_contents_water_alpha", DefContentsWaterAlpha);
            liquidColor = WaterColor;
            inContent = 1;
        }
        else
        {
            liquidAlpha = 0f;
            liquidColor = new Color(0f, 0f, 0f);
            inContent = 0;
        }

        // Different fade speeds in vs out (instant-ish in, slow out) so leaving water lingers (QC comment).
        float fadeTime;
        if (inContent != 0)
        {
            fadeTime = Cvar("hud_contents_fadeintime", DefContentsFadeIn);
            // latch the colour/alpha so it persists while fading OUT (not reset until a new content entered).
            _liquidAlphaPrev = liquidAlpha;
            _liquidColorPrev = liquidColor;
        }
        else
        {
            fadeTime = Cvar("hud_contents_fadeouttime", DefContentsFadeOut);
        }

        float a = Mathf.Clamp(dt / Mathf.Max(0.0001f, fadeTime), 0f, 1f);
        _contentAvgAlpha = _contentAvgAlpha * (1f - a) + inContent * a;

        SetAlpha(_contents, _liquidColorPrev, _contentAvgAlpha * _liquidAlphaPrev);
    }

    private static void SetAlpha(ColorRect rect, Color rgb, float alpha)
    {
        alpha = Mathf.Clamp(alpha, 0f, 1f);
        rect.Visible = alpha > 0.0001f;
        if (rect.Visible)
            rect.Color = new Color(rgb.R, rgb.G, rgb.B, alpha);
    }

    /// <summary>
    /// Read a float cvar live, falling back to <paramref name="fallback"/> only when the cvar is genuinely unset
    /// (its string value is empty). An explicit <c>0</c> is honored — so e.g. <c>hud_damage 0</c> disables the
    /// flash rather than being reinterpreted as "use the default".
    /// </summary>
    private static float Cvar(string name, float fallback)
    {
        if (Api.Services is null)
            return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrWhiteSpace(s) ? fallback : Api.Cvars.GetFloat(name);
    }

    /// <summary>
    /// Read an "r g b" vector cvar as a <see cref="Color"/> (QC <c>stov(autocvar_…)</c>), falling back to
    /// <paramref name="fallback"/> when the cvar is unset or unparseable. Alpha is left at 1 (the effect's own
    /// alpha is applied separately in <see cref="SetAlpha"/>).
    /// </summary>
    private static Color CvarColor(string name, Color fallback)
    {
        if (Api.Services is null)
            return fallback;
        string s = Api.Cvars.GetString(name);
        if (string.IsNullOrWhiteSpace(s))
            return fallback;
        string[] parts = s.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3
            || !float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float r)
            || !float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float g)
            || !float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float b))
            return fallback;
        return new Color(r, g, b);
    }
}
