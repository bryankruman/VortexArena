using Godot;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// Center crosshair — port of the core of Base/.../qcsrc/client/hud/crosshair.qc. The QC crosshair has
/// per-weapon crosshair pics, dynamic scaling on fire, hit-indication color flash, a ring charge indicator
/// (Vortex/Vaporizer/Mortar fuse), true-aim coloring (HITENEMY/HITTEAM/HITWORLD/HITOBSTRUCTION) and a
/// weapon-switch cross-fade between the old and new crosshair image.
///
/// This port wires the per-weapon + ring pieces against the real weapon model. When a <see cref="Player"/>
/// is set the panel reads the active weapon (<see cref="Inventory.CurrentWeapon"/>): it tints the crosshair
/// with that weapon's color (QC <c>crosshair_per_weapon</c>) and, if the weapon supports charging
/// (<see cref="WeaponHud.IsChargeWeapon"/>), draws a charge ring around the center filled to
/// <see cref="ChargeFraction"/> (fed by the weapon/net layer, since per-slot charge state is networked). A
/// per-weapon crosshair texture is used when present (<see cref="TextureCache"/>), else the clean vector
/// crosshair (center gap + four ticks + dot) is drawn. <see cref="FiringRing"/> draws a transient
/// shrinking ring on fire, and <see cref="HitFlash"/> the hit-indication tint.
///
/// <para><b>T21 — true-aim coloring (QC <c>TrueAimCheck</c>/<c>EnemyHitCheck</c>).</b> Each frame the panel
/// runs a forward trace from the eye (<see cref="Api.Trace"/>, the same client trace service the rest of the
/// client uses) and classifies what it would hit — a teammate (<see cref="ShotType.HitTeam"/>), an enemy
/// (<see cref="ShotType.HitEnemy"/>), the world (<see cref="ShotType.HitWorld"/>) or an obstruction between
/// the shot origin and the aim point (<see cref="ShotType.HitObstruction"/>). The crosshair then signals an
/// invalid target the way QC does: a teammate shrinks the crosshair (QC <c>wcross_scale /= crosshair_hittest</c>)
/// and dims it, an obstruction dims it (QC <c>crosshair_hittest_blur_wall</c>), and an enemy/world leaves it at
/// full strength. The aim ray is taken from <see cref="AimOrigin"/>/<see cref="AimForward"/> when the view layer
/// feeds them (the authoritative render eye + look direction); absent that it is reconstructed from the
/// <see cref="Player"/>'s view angles + eye height, so coloring still works on the bare local-player path.</para>
///
/// <para><b>T21 — weapon-switch transition (QC <c>wcross_name_changestarttime</c>/<c>changedonetime</c>).</b>
/// When the active weapon's crosshair image changes the panel starts a short cross-fade over
/// <see cref="EffectTime"/>: the outgoing crosshair fades out while the incoming fades in (alpha only, as in
/// QC), mirroring the QC <c>CROSSHAIR_DRAW</c> of the previous pic over the new one during a switch.</para>
/// </summary>
public partial class CrosshairPanel : HudPanel
{
    // Port of crosshair.qc: SHOTTYPE_* — the true-aim hit classification.
    /// <summary>Classification of what the aim ray would hit (QC <c>SHOTTYPE_*</c>).</summary>
    public enum ShotType
    {
        /// <summary>Aim ray would hit a teammate — an invalid target (QC SHOTTYPE_HITTEAM = 1).</summary>
        HitTeam = 1,
        /// <summary>A wall/obstruction sits between the shot origin and the aim point (QC SHOTTYPE_HITOBSTRUCTION = 2).</summary>
        HitObstruction = 2,
        /// <summary>Aim ray hits the world (or nothing relevant) — the default (QC SHOTTYPE_HITWORLD = 3).</summary>
        HitWorld = 3,
        /// <summary>Aim ray would hit an enemy — a valid target (QC SHOTTYPE_HITENEMY = 4).</summary>
        HitEnemy = 4,
    }

    /// <summary>The local player (set by <see cref="Hud"/>); drives per-weapon crosshair color + charge ring + true-aim.</summary>
    public Player? Player { get; set; }

    /// <summary>Base crosshair color (QC crosshair_color). Alpha is the resting opacity.</summary>
    public Color Color { get; set; } = new(0.4f, 1f, 0.5f, 0.85f);

    /// <summary>QC <c>crosshair_per_weapon</c>: tint the crosshair with the active weapon's color.</summary>
    public bool PerWeaponColor { get; set; } = true;

    /// <summary>
    /// The crosshair shape number (QC <c>crosshair</c> cvar): Xonotic crosshairs are numbered art
    /// (<c>gfx/crosshair&lt;N&gt;</c>), not per-weapon files. 0 disables the textured crosshair (vector fallback).
    /// </summary>
    public int CrosshairNumber { get; set; } = 3;

    /// <summary>Optional per-weapon crosshair number (QC <c>crosshair_per_weapon</c>), keyed by weapon NetName;
    /// overrides <see cref="CrosshairNumber"/> for that weapon when present. Host/config-populated.</summary>
    public System.Collections.Generic.Dictionary<string, int>? PerWeaponNumber { get; set; }

    /// <summary>Empty space (radius) at the very center, in pixels (QC crosshair ring/dot gap).</summary>
    public float GapPixels { get; set; } = 5f;

    /// <summary>Length of each of the four ticks, in pixels (QC crosshair size).</summary>
    public float TickLength { get; set; } = 8f;

    /// <summary>Line thickness, in pixels.</summary>
    public float Thickness { get; set; } = 2f;

    /// <summary>Radius of the center dot, in pixels. 0 disables it.</summary>
    public float DotRadius { get; set; } = 1.5f;

    /// <summary>
    /// Charge fraction in [0,1] for charge weapons (QC <c>wepent.vortex_charge</c>). The weapon/net layer
    /// feeds this each frame; the panel draws a ring filled to this fraction while the active weapon is a
    /// charge weapon. &lt; 0 means "no charge data" (ring hidden).
    /// </summary>
    public float ChargeFraction { get; set; } = -1f;

    /// <summary>Radius of the charge/firing ring, in pixels.</summary>
    public float RingRadius { get; set; } = 16f;

    /// <summary>
    /// Transient hit-indication strength in [0,1] (QC hitindication): briefly tints the crosshair toward
    /// <see cref="HitColor"/>. The owner sets this to 1 on a confirmed hit; it decays each frame.
    /// </summary>
    public float HitFlash { get; set; }

    /// <summary>Color blended in while <see cref="HitFlash"/> is active (QC hit indication red).</summary>
    public Color HitColor { get; set; } = new(1f, 0.2f, 0.2f, 1f);

    /// <summary>Seconds for <see cref="HitFlash"/> to decay from 1 to 0.</summary>
    public float HitDecay { get; set; } = 0.3f;

    /// <summary>
    /// Transient firing-ring strength in [0,1] (QC the fire/cooldown ring): the owner pulses this to 1 on
    /// fire and it decays, drawing a shrinking ring. Independent of the charge ring.
    /// </summary>
    public float FiringRing { get; set; }

    /// <summary>Seconds for <see cref="FiringRing"/> to decay.</summary>
    public float FiringDecay { get; set; } = 0.25f;

    // ---- T21: true-aim coloring (QC autocvar_crosshair_hittest + the SHOTTYPE coloring) ----

    /// <summary>
    /// QC <c>autocvar_crosshair_hittest</c>: enable the forward true-aim trace and colour the crosshair by what
    /// it would hit. When false the crosshair is always treated as <see cref="ShotType.HitWorld"/> (no trace),
    /// exactly like QC.
    /// </summary>
    public bool HitTest { get; set; } = true;

    /// <summary>
    /// QC <c>autocvar_crosshair_hittest</c> as the teammate-shrink divisor: a value &gt; 1 shrinks the crosshair
    /// over a teammate (QC <c>wcross_scale /= autocvar_crosshair_hittest</c>). 1 leaves the size unchanged.
    /// </summary>
    public float HitTestTeammateShrink { get; set; } = 1.25f;

    /// <summary>QC <c>autocvar_crosshair_hittest_blur_wall</c>: dim the crosshair over an obstruction.</summary>
    public bool BlurWall { get; set; } = true;

    /// <summary>QC <c>autocvar_crosshair_hittest_blur_teammate</c>: dim the crosshair over a teammate.</summary>
    public bool BlurTeammate { get; set; }

    /// <summary>
    /// The eye/shot origin for the true-aim trace, in Quake/sim coordinates (QC <c>traceorigin</c>). When this is
    /// fed by the view layer (the authoritative render eye) it is used as-is; otherwise the panel reconstructs it
    /// from the <see cref="Player"/>'s origin + eye height. <c>null</c> = reconstruct.
    /// </summary>
    public NVec3? AimOrigin { get; set; }

    /// <summary>
    /// The look direction for the true-aim trace, in Quake/sim coordinates (QC <c>view_forward</c>). Fed by the
    /// view layer (the render look direction); absent that the panel derives it from the player's view angles.
    /// <c>null</c> = derive.
    /// </summary>
    public NVec3? AimForward { get; set; }

    /// <summary>Eye height above the player origin used to reconstruct the trace origin (Xonotic PL_VIEW_OFS '0 0 35').</summary>
    public float ViewHeight { get; set; } = 35f;

    /// <summary>The latest true-aim classification (QC <c>shottype</c>), recomputed each <see cref="_Process"/>.</summary>
    public ShotType ShotResult { get; private set; } = ShotType.HitWorld;

    // ---- T21: weapon-switch cross-fade (QC wcross_name_changestarttime / changedonetime) ----

    /// <summary>
    /// QC <c>autocvar_crosshair_effect_time</c>: duration of the weapon-switch crosshair cross-fade, in seconds.
    /// 0 disables the transition (the new crosshair snaps in).
    /// </summary>
    public float EffectTime { get; set; } = 0.2f;

    /// <summary>
    /// Current time used to drive the switch transition. If &lt; 0 (default) the panel uses the sim clock
    /// (<see cref="Api.Clock"/>) when available, else its own per-frame wall clock. The net/demo layer can
    /// slave it to the match clock.
    /// </summary>
    public double Now { get; set; } = -1.0;

    private double _localClock;

    // Weapon-switch cross-fade state — the port of QC's wcross_name_* "goal_prev" persisted globals. We key
    // the transition on the resolved crosshair texture changing (QC keys on wcross_name/resolution changing).
    private Texture2D? _crossPrev;       // the outgoing crosshair texture (QC wcross_name_goal_prev_prev)
    private float _changeStartTime;      // QC wcross_name_changestarttime
    private float _changeDoneTime;       // QC wcross_name_changedonetime
    private Texture2D? _lastResolved;    // what we resolved last frame, to detect a switch (QC wcross_name_goal_prev)

    public override void _Process(double delta)
    {
        _localClock += delta;

        bool dirty = false;
        if (HitFlash > 0f)
        {
            HitFlash = Mathf.Max(0f, HitFlash - (float)delta / Mathf.Max(0.001f, HitDecay));
            dirty = true;
        }
        if (FiringRing > 0f)
        {
            FiringRing = Mathf.Max(0f, FiringRing - (float)delta / Mathf.Max(0.001f, FiringDecay));
            dirty = true;
        }

        // True-aim classification (QC: run TrueAimCheck every frame while the crosshair is live).
        ShotType newShot = ComputeShotType();
        if (newShot != ShotResult)
        {
            ShotResult = newShot;
            dirty = true;
        }

        // Detect a weapon-switch crosshair change and (re)start the cross-fade (QC wcross_name change block).
        Texture2D? resolved = ResolveCrosshair(ActiveWeapon());
        if (!ReferenceEquals(resolved, _lastResolved))
        {
            if (EffectTime > 0f && (resolved is not null || _lastResolved is not null))
            {
                _crossPrev = _lastResolved;
                _changeStartTime = CurrentTime();
                _changeDoneTime = _changeStartTime + EffectTime;
            }
            _lastResolved = resolved;
            dirty = true;
        }
        // Keep repainting while a switch cross-fade is in flight.
        if (CurrentTime() < _changeDoneTime)
            dirty = true;

        // The charge ring follows live weapon state, so keep repainting while a charge weapon is active.
        if (ChargeFraction >= 0f && ActiveWeapon() is Weapon w && WeaponHud.IsChargeWeapon(w))
            dirty = true;

        if (dirty) QueueRedraw();
    }

    private float CurrentTime()
    {
        if (Now >= 0.0) return (float)Now;
        if (Api.Services is not null) return Api.Clock.Time;
        return (float)_localClock;
    }

    private Weapon? ActiveWeapon() => Player is not null ? Inventory.CurrentWeapon(Player) : null;

    protected override void DrawPanel()
    {
        Vector2 center = Size2 * 0.5f;
        Weapon? weapon = ActiveWeapon();

        // Base color: the crosshair color, optionally replaced by the active weapon's tint (QC crosshair_getcolor
        // case 1 / crosshair_per_weapon).
        Color c = Color;
        if (PerWeaponColor && weapon is not null)
        {
            Color wc = WeaponHud.ColorOf(weapon, Color.A);
            c = wc;
        }
        if (HitFlash > 0f)
            c = c.Lerp(HitColor, Mathf.Clamp(HitFlash, 0f, 1f));

        // T21 true-aim signalling (QC: HITTEAM shrinks the crosshair and, with blur_*, dims it; HITOBSTRUCTION
        // dims it when blur_wall is on). Enemy/world leave it at full strength.
        float scale = 1f;
        if (HitTest)
        {
            if (ShotResult == ShotType.HitTeam && HitTestTeammateShrink > 0f)
                scale /= HitTestTeammateShrink; // QC wcross_scale /= autocvar_crosshair_hittest
            if ((ShotResult == ShotType.HitTeam && BlurTeammate)
                || (ShotResult == ShotType.HitObstruction && BlurWall))
                c = new Color(c.R, c.G, c.B, c.A * 0.75f); // QC wcross_alpha *= 0.75 on a blurred target
        }

        // Numbered crosshair art (QC gfx/crosshair<N>), per-weapon number when configured; else the vector
        // crosshair. The real Xonotic art resolves from the mounted game data via TextureCache's VFS resolver.
        Texture2D? pic = ResolveCrosshair(weapon);

        // Weapon-switch cross-fade (QC wcross_name_changestarttime/changedonetime): fade the incoming crosshair
        // in while the outgoing fades out, over EffectTime. QC cross-fades alpha only (no size pop).
        float fadeIn = 1f;
        float switchScale = scale;
        if (EffectTime > 0f && CurrentTime() < _changeDoneTime && _changeDoneTime > _changeStartTime)
        {
            // f: 1 at switch start → 0 at switch end (QC: (changedonetime - time)/(changedonetime - changestarttime)).
            float f = (_changeDoneTime - CurrentTime()) / (_changeDoneTime - _changeStartTime);
            f = Mathf.Clamp(f, 0f, 1f);
            fadeIn = 1f - f;                 // the new crosshair's alpha ramps up

            // Draw the outgoing crosshair fading out underneath (QC CROSSHAIR_DRAW of wcross_name_goal_prev_prev).
            if (_crossPrev is not null && !ReferenceEquals(_crossPrev, pic))
            {
                var outc = new Color(c.R, c.G, c.B, c.A * f);
                DrawCrosshairTexture(_crossPrev, center, outc, switchScale);
            }
        }

        Color drawColor = new(c.R, c.G, c.B, c.A * fadeIn);
        if (pic is not null)
            DrawCrosshairTexture(pic, center, drawColor, switchScale);
        else
            DrawVectorCrosshair(center, drawColor, switchScale);

        // Charge ring (charge weapons only).
        if (weapon is not null && ChargeFraction >= 0f && WeaponHud.IsChargeWeapon(weapon))
            DrawRing(center, RingRadius, Mathf.Clamp(ChargeFraction, 0f, 1f),
                new Color(c.R, c.G, c.B, 0.9f * c.A));

        // Firing ring: a shrinking ring pulsed on fire.
        if (FiringRing > 0f)
        {
            float r = RingRadius * (1f + (1f - FiringRing) * 0.6f);
            DrawRing(center, r, 1f, new Color(c.R, c.G, c.B, FiringRing * c.A), segments: 40);
        }
    }

    // -------------------------------------------------------------------------------------------------
    //  T21: true-aim classification — port of crosshair.qc TrueAimCheck() + EnemyHitCheck().
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Forward-trace from the eye and classify what the active weapon would hit (QC <c>TrueAimCheck</c>). Returns
    /// <see cref="ShotType.HitWorld"/> when hit-testing is off, there is no aim ray, or no trace service is wired.
    /// </summary>
    private ShotType ComputeShotType()
    {
        if (!HitTest || Player is null || Api.Services is null)
            return ShotType.HitWorld;

        Weapon? weapon = ActiveWeapon();
        // QC: WEP_FLAG_NOTRUEAIM weapons (Mortar/Hook/Porto/Tuba) launch straight from the eye — no true-aim.
        if (weapon is not null && (weapon.SpawnFlags & WeaponFlags.NoTrueAim) != 0)
            return ShotType.HitWorld;

        if (!TryGetAimRay(out NVec3 origin, out NVec3 forward))
            return ShotType.HitWorld;

        // QC: the rail family (Vortex/Vaporizer; QC also Overkill-Nex, not present in this port) traces against
        // players too (MOVE_NORMAL); everything else uses MOVE_NOMONSTERS for the aim line. Projectile-size
        // weapons additionally get a non-zero trace box.
        bool railLike = weapon is not null && weapon.NetName is "vortex" or "vaporizer";
        MoveFilter aimFilter = railLike ? MoveFilter.Normal : MoveFilter.NoMonsters;
        (NVec3 mins, NVec3 maxs) = ProjectileBox(weapon);

        float range = WeaponFiring.MaxShotDistance; // QC max_shot_distance
        NVec3 end = origin + forward * range;

        // 1) Aim line: where is the player pointing? (QC traceline(traceorigin, ... view_forward * max_shot_distance)).
        TraceResult aim = Api.Trace.Trace(origin, NVec3.Zero, NVec3.Zero, end, aimFilter, Player);
        NVec3 trueAimPoint = aim.EndPos + forward; // QC nudges the point a little forward for the final box trace

        // QC g_trueaim_minrange: keep the aim point at least a short distance ahead so close-range tracing is stable.
        const float trueAimMinRange = 44f; // Xonotic g_trueaim_minrange default
        if (System.Numerics.Vector3.Distance(trueAimPoint, origin) < trueAimMinRange)
            trueAimPoint = origin + forward * trueAimMinRange;

        // 2) Final box trace from the shot origin to the aim point, classify what it touches (QC the second
        //    tracebox + EnemyHitCheck). For an obstruction (the box stops well short of the aim point on the
        //    world) the crosshair signals a blocked shot.
        TraceResult shot = Api.Trace.Trace(origin, mins, maxs, trueAimPoint, MoveFilter.Normal, Player);
        ShotType shottype = EnemyHitCheck(shot);
        if (shottype != ShotType.HitWorld)
            return shottype;

        // HITOBSTRUCTION: a wall sits between the shot origin and the aim point. NOTE: QC's *real* obstruction
        // test (HUD_Crosshair, crosshair.qc:312-319) is screen-space — it reprojects the box-trace endpoint to 2D
        // (EnemyHitCheck's project_3d_to_2d into wcross_origin) and flags HITOBSTRUCTION when that 2D point drifts
        // from the previous frame's by > 0.01 of the screen. The 3D-distance form below is the dead `#if 0` block
        // (crosshair.qc:145-151), disabled in QC because it misfires for the rocket launcher / projectile-box
        // weapons. So we only run it for zero-box (hitscan) weapons, matching the RL/projectile exclusion; a full
        // screen-space port would need the projected-endpoint history the view layer doesn't feed us yet.
        bool projectileBox = mins != NVec3.Zero || maxs != NVec3.Zero;
        if (!projectileBox
            && shot.Fraction < 1f
            && System.Numerics.Vector3.Distance(shot.EndPos, trueAimPoint) > maxs.Length() + mins.Length() + 1f)
            return ShotType.HitObstruction;

        return ShotType.HitWorld;
    }

    /// <summary>
    /// Classify a completed trace by the entity it hit (QC <c>EnemyHitCheck</c>): a same-team player is HITTEAM,
    /// any other player is HITENEMY, anything else (world / no entity / spectator) is HITWORLD.
    /// </summary>
    private ShotType EnemyHitCheck(in TraceResult tr)
    {
        if (tr.Ent is not Player hit || ReferenceEquals(hit, Player))
            return ShotType.HitWorld;

        // QC: a spectator-team player is not a valid target (treated as world). Team 0 = no team.
        // QC: teamplay && entcs_GetTeam == myteam → teammate (an invalid target). SAME_TEAM semantics:
        // both players on the same nonzero team (only true in a team game, where teams are assigned).
        if (Player is not null && hit.Team != 0f && hit.Team == Player.Team)
            return ShotType.HitTeam;

        return ShotType.HitEnemy;
    }

    /// <summary>
    /// Resolve the eye origin + look direction for the true-aim trace. Prefers the view-layer-fed
    /// <see cref="AimOrigin"/>/<see cref="AimForward"/> (the authoritative render eye); otherwise reconstructs
    /// the origin from the player origin + <see cref="ViewHeight"/> and the forward from the player's view angles
    /// (QC AngleVectors of the view angles). Returns false if neither a ray nor a player is available.
    /// </summary>
    private bool TryGetAimRay(out NVec3 origin, out NVec3 forward)
    {
        if (AimForward is { } f && f != NVec3.Zero)
        {
            forward = System.Numerics.Vector3.Normalize(f);
            origin = AimOrigin ?? (Player!.Origin + new NVec3(0f, 0f, ViewHeight));
            return true;
        }

        if (Player is null)
        {
            origin = default;
            forward = default;
            return false;
        }

        // Derive the look direction from the player's view angles (QC view_forward = AngleVectors(view_angles)).
        QMath.AngleVectors(Player.Angles, out NVec3 fq, out _, out _);
        forward = fq;
        origin = AimOrigin ?? (Player.Origin + new NVec3(0f, 0f, ViewHeight));
        return forward != NVec3.Zero;
    }

    /// <summary>
    /// QC <c>TrueAimCheck</c>: the trace box for projectile-size weapons (Devastator/Fireball/Seeker/Electro).
    /// Hitscan weapons use a zero-size point trace.
    /// </summary>
    private static (NVec3 mins, NVec3 maxs) ProjectileBox(Weapon? weapon) => weapon?.NetName switch
    {
        "devastator" => (new NVec3(-3f, -3f, -3f), new NVec3(3f, 3f, 3f)),
        "fireball" => (new NVec3(-16f, -16f, -16f), new NVec3(16f, 16f, 16f)),
        "seeker" => (new NVec3(-2f, -2f, -2f), new NVec3(2f, 2f, 2f)),
        "electro" => (new NVec3(0f, 0f, -3f), new NVec3(0f, 0f, -3f)),
        _ => (NVec3.Zero, NVec3.Zero),
    };

    // -------------------------------------------------------------------------------------------------
    //  Drawing
    // -------------------------------------------------------------------------------------------------

    /// <summary>Resolve the numbered crosshair texture (per-weapon number when configured), or null → vector.</summary>
    private Texture2D? ResolveCrosshair(Weapon? weapon)
    {
        int number = CrosshairNumber;
        if (weapon is not null && PerWeaponNumber is not null
            && PerWeaponNumber.TryGetValue(weapon.NetName, out int n))
            number = n;
        if (number <= 0) return null;

        // VFS art first (gfx/crosshair<N>), then a project override under res://.
        return TextureCache.GetFirst($"gfx/crosshair{number}", $"res://art/hud/crosshairs/crosshair{number}.png");
    }

    private void DrawCrosshairTexture(Texture2D pic, Vector2 center, Color c, float scale = 1f)
    {
        Vector2 s = pic.GetSize() * scale;
        var at = new Rect2(center - s * 0.5f, s);
        DrawTextureRect(pic, at, tile: false, c);
    }

    private void DrawVectorCrosshair(Vector2 center, Color c, float scale = 1f)
    {
        float g = GapPixels * scale;
        float t = TickLength * scale;
        float thick = Thickness * scale;
        float half = thick * 0.5f;

        // Four ticks (left, right, up, down), leaving GapPixels of empty space at the center.
        DrawRect(new Rect2(center.X - g - t, center.Y - half, t, thick), c); // left
        DrawRect(new Rect2(center.X + g, center.Y - half, t, thick), c);     // right
        DrawRect(new Rect2(center.X - half, center.Y - g - t, thick, t), c); // up
        DrawRect(new Rect2(center.X - half, center.Y + g, thick, t), c);     // down

        if (DotRadius > 0f)
            DrawCircle(center, DotRadius * scale, c);
    }

    /// <summary>
    /// Draw an arc ring of <paramref name="fraction"/> of a full circle around <paramref name="center"/>
    /// (QC the charge ring). Approximated as a connected poly-line of short segments.
    /// </summary>
    private void DrawRing(Vector2 center, float radius, float fraction, Color color, int segments = 48)
    {
        fraction = Mathf.Clamp(fraction, 0f, 1f);
        if (fraction <= 0f) return;
        int steps = Mathf.Max(1, (int)(segments * fraction));
        float arc = Mathf.Tau * fraction;
        float start = -Mathf.Pi / 2f; // start at 12 o'clock, sweep clockwise
        Vector2 prev = center + new Vector2(Mathf.Cos(start), Mathf.Sin(start)) * radius;
        for (int i = 1; i <= steps; i++)
        {
            float a = start + arc * (i / (float)steps);
            Vector2 p = center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
            DrawLine(prev, p, color, 2f);
            prev = p;
        }
    }
}
