// Port of common/mutators/mutator/damagetext/cl_damagetext.qc (the CSQC DamageText draw class) +
// damagetext.qh. The server producer is DamagetextMutator (XonoticGodot.Common); this is the client draw.

using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Game.Client;

/// <summary>
/// Floating damage numbers (client draw) — port of the CSQC <c>DamageText</c> class in
/// common/mutators/mutator/damagetext/cl_damagetext.qc. The server's <see cref="DamagetextMutator"/> queues a
/// <see cref="DamageTextEvent"/> per hit (the actual health/armor removed + the pre-split potential + the
/// DTFLAG_* flags); the host drains them each frame and calls <see cref="Add"/>, and this layer draws the
/// number at the victim's location, fading/shrinking/moving it over its lifetime per the ~30
/// <c>cl_damagetext_*</c> cvars.
///
/// The number-formatting + size math is in <see cref="DamageTextFormat"/> (a pure, Godot-free helper) so it's
/// unit-testable; this node is the thin Godot draw wrapper. World-space numbers project through the host's
/// <see cref="Camera3D"/> (QC project_3d_to_2d); when the target's location isn't known the QC version falls
/// back to a 2D screen position, which here is the simple "no camera / behind camera" path.
///
/// Faithful to QC: the friendlyfire filter (cl_damagetext_friendlyfire 0/1/2), grouping/accumulation by
/// server target index (DTFLAG_STOP_ACCUMULATION + the accumulate alpha/lifetime gates), the
/// <c>cl_damagetext_format</c> token replacement, the size mapping (map_bound_ranges over potential), the
/// per-frame fade (alpha_lifetime), shrink (2d_size_lifetime) and move (velocity_world / velocity_screen), and
/// the 2D-vs-3D placement heuristics: a number switches to a fixed 2D screen position (with the
/// <c>cl_damagetext_2d_overlap_offset</c> stagger and the 2D fade/shrink lifetimes) when the local view is
/// playing/following a player and the victim is either within <c>cl_damagetext_2d_close_range</c> of the view
/// origin or off-screen (<c>cl_damagetext_2d_out_of_view</c>), otherwise it's a world number projected through
/// the camera. Per-weapon color and the verbose/hide-redundant format variants are honoured via cvars.
/// </summary>
public partial class DamageTextLayer : Control
{
    /// <summary>The host's 3D camera, for projecting world hit positions to screen (QC project_3d_to_2d).</summary>
    public Camera3D? Camera { get; set; }

    private sealed class Item
    {
        public int Group;            // QC m_group (server target index) — accumulation key
        public Vector3 WorldPos;     // QC origin (world) when 3D
        public bool ScreenCoords;    // QC m_screen_coords
        public Vector2 ScreenPos;    // QC origin (screen) when 2D
        public float HitTime;        // QC hit_time
        public float Health, Armor, Potential; // QC m_healthdamage/m_armordamage/m_potential_damage (×PRECISION)
        public int DeathTypeColorKey; // for per-weapon color (the weapon RegistryId, or -1)
        public bool FriendlyFire;    // QC m_friendlyfire
        public float Alpha;          // QC alpha (initial)
        public float FadeRate;       // QC fade_rate
        public float ShrinkRate;     // QC m_shrink_rate
        public float Size;           // QC m_size
        public string Text = "";     // QC text
    }

    private readonly List<Item> _items = new();
    private int _screenCount; // QC DamageText_screen_count (stagger for 2D overlap)

    public override void _Ready()
    {
        // The HUD must NEVER eat mouse input (QC hud_cursormode off) — a full-rect Control defaults to
        // MouseFilter.Stop, which would consume mouse-look motion + fire clicks in the GUI layer before they
        // reach NetGame/PlayerController._UnhandledInput. Mirror every other HUD Control (HudManager:194,
        // ViewEffects:109) and pass input straight through.
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Process(double delta)
    {
        using var _scope = FrameProfiler.Scope("damagetext"); // [profiling] §18: out of proc:other
        // Prune dead items (alpha/size/lifetime expired) and request a redraw each frame (animated).
        float now = Now();
        var cfg = DamageTextConfig.Read();
        _items.RemoveAll(it =>
        {
            float since = now - it.HitTime;
            float a = it.Alpha - since * it.FadeRate;
            float s = it.Size - since * it.ShrinkRate * it.Size;
            bool hasLifetime = cfg.Lifetime < 0f || since < cfg.Lifetime;
            bool dead = a <= 0f || s <= 0f || !hasLifetime;
            if (dead && _screenCount > 0) --_screenCount; // QC DESTRUCTOR: --DamageText_screen_count
            return dead;
        });
        QueueRedraw();
    }

    /// <summary>
    /// Add a floating damage number from a drained server event (QC NET_HANDLE(damagetext) → NEW(DamageText)).
    /// <paramref name="worldPos"/> is the victim's world location; <paramref name="deathTypeColorKey"/> is the
    /// weapon RegistryId for per-weapon color (or -1). <paramref name="canUse2d"/> mirrors QC
    /// <c>spectatee_status != -1</c> (the local view is playing or following a player, so it has a meaningful
    /// view origin); when false the 2D heuristics are skipped (a free-fly observer always gets a world number).
    /// Applies the friendlyfire filter, the 2D-vs-3D placement heuristics (close-range / out-of-view), and
    /// groups/accumulates onto an existing number for the same target when the accumulation gates allow.
    /// </summary>
    public void Add(in DamageTextEvent ev, Vector3 worldPos, int deathTypeColorKey, bool canUse2d = false)
    {
        var cfg = DamageTextConfig.Read();
        if (cfg.Enabled == 0) return;

        bool friendlyFire = (ev.Flags & DamageTextWire.FlagSameTeam) != 0;
        // QC the wire splits health/armor/potential per the BIG_*/NO_* flags; the server already gave us the
        // un-multiplied amounts, so just scale by the precision multiplier to match the QC stored fields.
        float health = ev.Health * DamageTextWire.PrecisionMultiplier;
        float armor = (ev.Flags & DamageTextWire.FlagNoArmor) != 0 ? 0f : ev.Armor * DamageTextWire.PrecisionMultiplier;
        float potential = (ev.Flags & DamageTextWire.FlagNoPotential) != 0
            ? health + armor
            : ev.Potential * DamageTextWire.PrecisionMultiplier;

        // Friendly-fire filter (QC: 0 never; 1 only when >0 damage; 2 always).
        if (friendlyFire)
        {
            if (cfg.FriendlyFire == 0) return;
            if (cfg.FriendlyFire == 1 && health == 0f && armor == 0f) return;
        }

        int group = ev.Target.Index; // server target index stand-in (QC server_entity_index)
        float now = Now();
        float alphaThreshold = cfg.AccumulateAlphaRel * cfg.AlphaStart;

        // 2D-vs-3D placement heuristic (QC cl_damagetext.qc:253-256). can_use_3d is true whenever we know the
        // victim's world origin (we always do here). too_close: the victim is within close_range of the view
        // origin (numbers right in your face read better pinned to the screen). prefer_in_view: out_of_view is
        // enabled and the victim doesn't project onto the visible screen rect. prefer_2d requires a meaningful
        // view origin (canUse2d == QC spectatee_status != -1) AND 2d enabled AND (too_close || prefer_in_view).
        bool tooClose = false, preferInView = false;
        if (Camera is not null)
        {
            Vector3 victimG = ToGodot(worldPos);
            Vector3 viewOrigin = Camera.GlobalPosition;
            // QC close_range is in Quake units; the view-origin delta here is in Godot metres, so compare against
            // the converted range (Coords inch→metre is the engine-wide 0.0254 used throughout this layer).
            float closeRangeM = cfg.CloseRange2d * 0.0254f;
            tooClose = victimG.DistanceTo(viewOrigin) < closeRangeM;
            if (cfg.OutOfView2d)
                preferInView = !ProjectedOnScreen(victimG);
        }
        bool prefer2d = canUse2d && cfg.Use2d && (tooClose || preferInView);

        // Accumulate onto an existing number for this target if the gates allow (QC IL_EACH g_damagetext).
        Item? acc = null;
        foreach (Item it in _items)
        {
            if (it.Group != group) continue;
            float curAlpha = it.Alpha - (now - it.HitTime) * it.FadeRate;
            bool disown = (ev.Flags & DamageTextWire.FlagStopAccumulation) != 0
                || curAlpha < alphaThreshold
                || (cfg.AccumulateLifetime >= 0f && (now - it.HitTime) > cfg.AccumulateLifetime);
            if (disown) it.Group = 0; // QC disowns; a fresh number is spawned below
            else
            {
                health += it.Health;
                armor += it.Armor;
                potential += it.Potential;
                acc = it;
            }
            break;
        }

        // QC: if can_use_3d && !prefer_2d → world coords; else if 2d && spectatee_status != -1 → screen coords
        // (with the overlap stagger); else no number at all (a free-fly observer with 2d off and an off-screen
        // hit). can_use_3d is always true here (we have worldPos), so the only "return nothing" case is
        // prefer_2d-but-2d-disabled, which prefer_2d already excludes — so we only bail when !canUse2d forces the
        // 2D branch off. Concretely: 3D unless prefer_2d.
        bool is2d;
        Vector2 screenPos = default;
        if (!prefer2d)
        {
            is2d = false; // world coords
        }
        else
        {
            is2d = true; // screen coords (QC vid_conwidth/height * 2d_pos)
            Vector2 vp = ViewportSize();
            screenPos = new Vector2(vp.X * cfg.Pos2d.X, vp.Y * cfg.Pos2d.Y);
        }

        if (acc is not null)
        {
            // QC updateDT calls DamageText_update only — it does NOT re-run the CONSTRUCTOR, so the existing
            // number keeps its original fade/shrink rates; only its position/amounts/alpha are refreshed.
            Update(acc, worldPos, screenPos, is2d, health, armor, potential, deathTypeColorKey, cfg);
            return;
        }

        var item = new Item
        {
            Group = group,
            FriendlyFire = friendlyFire,
        };
        // QC spawnnewDT: a 2D number gets an overlap stagger so simultaneous hits don't stack on one another.
        if (is2d)
            screenPos += new Vector2(cfg.OverlapOffset2d.X, cfg.OverlapOffset2d.Y) * _screenCount;
        ++_screenCount;
        ApplyPlacement(item, is2d, cfg);
        Update(item, worldPos, screenPos, is2d, health, armor, potential, deathTypeColorKey, cfg);
        _items.Add(item);
    }

    // QC CONSTRUCTOR fade/shrink rate selection: a 2D (screen-coords) number fades over 2d_alpha_lifetime and
    // shrinks over 2d_size_lifetime; a 3D (world) number fades over alpha_lifetime and never shrinks.
    private static void ApplyPlacement(Item it, bool screenCoords, in DamageTextConfig cfg)
    {
        if (screenCoords)
        {
            it.FadeRate = cfg.Alpha2dLifetime > 0f ? 1f / cfg.Alpha2dLifetime : 0f;
            it.ShrinkRate = cfg.Size2dLifetime > 0f ? 1f / cfg.Size2dLifetime : 0f;
        }
        else
        {
            it.FadeRate = cfg.AlphaLifetime > 0f ? 1f / cfg.AlphaLifetime : 0f;
            it.ShrinkRate = 0f;
        }
    }

    // QC projected_on_screen: the world point projects to a 2D point inside the visible screen rect (and in
    // front of the camera). Used by the out-of-view heuristic to prefer a 2D number when the victim is off-screen.
    private bool ProjectedOnScreen(Vector3 worldG)
    {
        if (Camera is null || Camera.IsPositionBehind(worldG)) return false;
        Vector2 p = Camera.UnprojectPosition(worldG);
        Vector2 v = ViewportSize();
        return p.X >= 0f && p.Y >= 0f && p.X <= v.X && p.Y <= v.Y;
    }

    private Vector2 ViewportSize() => GetViewportRect().Size;

    // DamageText_update — refresh the amounts, rebuild the label + size, restamp the hit time. The position is
    // world-space (worldPos) for a 3D number or screen-space (screenPos) for a 2D one (QC setorigin to whichever).
    private void Update(Item it, Vector3 worldPos, Vector2 screenPos, bool screenCoords, float health, float armor,
        float potential, int deathTypeColorKey, in DamageTextConfig cfg)
    {
        it.Health = health;
        it.Armor = armor;
        it.Potential = potential;
        it.DeathTypeColorKey = deathTypeColorKey;
        it.HitTime = Now();
        it.WorldPos = worldPos;
        it.ScreenPos = screenPos;
        it.ScreenCoords = screenCoords;
        it.Alpha = screenCoords ? cfg.Alpha2dStart : cfg.AlphaStart;
        it.Text = DamageTextFormat.Build(cfg.Format, cfg.FormatVerbose, cfg.FormatHideRedundant,
            health, armor, potential);
        it.Size = DamageTextFormat.MapSize(potential / DamageTextWire.PrecisionMultiplier,
            cfg.SizeMinDamage, cfg.SizeMaxDamage, cfg.SizeMin, cfg.SizeMax);
    }

    public override void _Draw()
    {
        float now = Now();
        var cfg = DamageTextConfig.Read();
        Vector2 viewport = GetViewportRect().Size;

        foreach (Item it in _items)
        {
            float since = now - it.HitTime;
            float size = it.Size - since * it.ShrinkRate * it.Size;
            float alpha = it.Alpha - since * it.FadeRate;
            if (alpha <= 0f || size <= 0f) continue;

            // Resolve screen position (QC: world → project_3d_to_2d, or the 2D screen path).
            Vector2 pos;
            if (it.ScreenCoords || Camera is null)
            {
                // QC m_screen_coords path: the stored screen origin (cl_damagetext_2d_pos + overlap stagger),
                // drifting by 2d_velocity over its lifetime. A null camera still falls back to screen-center.
                Vector2 origin = it.ScreenCoords
                    ? it.ScreenPos
                    : new Vector2(viewport.X * cfg.Pos2d.X, viewport.Y * cfg.Pos2d.Y);
                pos = origin + new Vector2(cfg.Velocity2d.X, cfg.Velocity2d.Y) * since;
            }
            else
            {
                if (Camera.IsPositionBehind(ToGodot(it.WorldPos))) continue;
                // world_offset moves the number up over time (velocity_world) + a fixed offset_world; QC adds
                // these along the view basis, approximated here as a world-Z rise (the dominant component).
                Vector3 worldOffset = new(0f, 0f, cfg.OffsetWorld.Z + cfg.VelocityWorld.Z * since);
                Vector2 projected = Camera.UnprojectPosition(ToGodot(it.WorldPos) + new Vector3(0f, worldOffset.Z * 0.0254f, 0f));
                pos = projected + new Vector2(cfg.VelocityScreen.X, cfg.VelocityScreen.Y) * since
                    + new Vector2(cfg.OffsetScreen.X, cfg.OffsetScreen.Y);
            }

            Color rgb = it.FriendlyFire ? cfg.FriendlyFireColor : cfg.Color;
            if (cfg.ColorPerWeapon && it.DeathTypeColorKey >= 0)
            {
                Weapon? w = XonoticGodot.Common.Framework.Registry<Weapon>.ById(it.DeathTypeColorKey);
                if (w is not null) rgb = new Color(w.Color.X, w.Color.Y, w.Color.Z);
            }
            rgb.A = alpha;

            int isize = Mathf.Max(1, (int)size);
            float width = ThemeDB.FallbackFont.GetStringSize(it.Text, HorizontalAlignment.Left, -1f, isize).X;
            DrawString(ThemeDB.FallbackFont, new Vector2(pos.X - width * 0.5f, pos.Y), it.Text,
                HorizontalAlignment.Left, -1f, isize, rgb);
        }
    }

    private static Vector3 ToGodot(Vector3 v) => v; // worldPos already a Godot Vector3 from the caller

    private float Now()
    {
        if (Api.Services is not null) return Api.Clock.Time;
        return (float)Time.GetTicksMsec() / 1000f;
    }
}
