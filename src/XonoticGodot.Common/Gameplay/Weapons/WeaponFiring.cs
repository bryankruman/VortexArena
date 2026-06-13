using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>Result of <see cref="WeaponFiring.SetupShot"/> — the C# successor to QC's w_shotorg / w_shotdir / w_shotend globals.</summary>
public readonly struct ShotInfo
{
    /// <summary>Muzzle origin the projectile/bullet leaves from (QC w_shotorg).</summary>
    public readonly Vector3 Origin;
    /// <summary>Normalized aim direction (QC w_shotdir).</summary>
    public readonly Vector3 Dir;
    /// <summary>Trueaim end point the shot is aimed at (QC w_shotend).</summary>
    public readonly Vector3 End;

    public ShotInfo(Vector3 origin, Vector3 dir, Vector3 end)
    {
        Origin = origin;
        Dir = dir;
        End = end;
    }
}

/// <summary>
/// Shared weapon firing helpers — the Godot-free core of the QC firing pipeline
/// (server/weapons/tracing.qc W_SetupShot*, FireRailgunBullet, fireBullet; common/weapons/calculations.qc).
///
/// These operate purely on <see cref="Entity"/> + the engine-services facade, so they unit-test headless.
/// Spread (deterministic PRNG), solid-penetration multi-hit, exponential distance falloff, knockback force,
/// railgun multi-pierce and the per-target headshot bbox (<see cref="Headshot"/>) are all implemented here.
/// Antilag take-back, warpzone transforms and accuracy hitplot bookkeeping are the remaining online/render-only gaps.
/// </summary>
public static class WeaponFiring
{
    /// <summary>QC max_shot_distance (constants.qh) — the default trueaim trace range.</summary>
    public const float MaxShotDistance = 32768f;

    /// <summary>
    /// Generic gun-muzzle offset in the actor's view frame, in Quake model-local coords (X = forward, Y = +left,
    /// Z = up) — the fallback for QC's per-model <c>movedir</c> (the weapon model's <c>tag_shot</c>, set in
    /// <c>CL_WeaponEntity_SetModel</c>) when a weapon has no registered per-model offset (a pure dedicated server
    /// with no models, or a weapon whose v_ model lacks a shot tag). Used by <see cref="SetupShot"/> to spawn the
    /// shot at the weapon rather than the camera. Hit-reg is unaffected (the shot dir is re-aimed at the trueaim
    /// point). Per-weapon offsets are registered via <see cref="RegisterMuzzleOffset"/> at weapon-model load.
    /// </summary>
    public static readonly Vector3 DefaultMuzzleOffset = new(12f, 0f, -8f);

    /// <summary>
    /// Per-weapon muzzle offset registry — the C# successor to QC's <c>ent.(weaponentity).movedir</c>, keyed by
    /// the weapon's RegistryId and holding the <c>tag_shot</c> position in MODEL-LOCAL Quake coords
    /// (X = forward, Y = +left, Z = up), exactly the value Base reads from the v_ model. The muzzle tag IS a
    /// parsed-model datum (MD3 tag origin / IQM-DPM bind bone, extracted by <c>XonoticGodot.Formats.MuzzleTag</c>),
    /// just not something Common can read itself (it has no model loader), so it is populated on the Godot side at
    /// weapon-model load and read here by <see cref="SetupShot"/> via the firing actor's
    /// <see cref="Entity.ActiveWeaponId"/>. Empty by default, so an un-registered weapon falls back to
    /// <see cref="DefaultMuzzleOffset"/> and today's behavior is kept.
    /// </summary>
    private static readonly Dictionary<int, Vector3> MuzzleOffsets = new();

    /// <summary>
    /// Register (or replace) the model-local <c>tag_shot</c> muzzle offset for weapon <paramref name="weaponId"/>
    /// (its RegistryId). Called by the host when a weapon's view model is loaded — mirrors Base setting
    /// <c>movedir</c> in <c>CL_WeaponEntity_SetModel</c>. The offset is in Quake model-local coords
    /// (X = forward, Y = +left, Z = up), fed unchanged into <see cref="SetupShot"/>'s view-basis formula.
    /// </summary>
    public static void RegisterMuzzleOffset(int weaponId, Vector3 modelLocalShotOffset)
    {
        if (weaponId < 0) return;
        MuzzleOffsets[weaponId] = modelLocalShotOffset;
    }

    /// <summary>Clear all registered per-weapon muzzle offsets (test isolation / a fresh content load).</summary>
    public static void ClearMuzzleOffsets() => MuzzleOffsets.Clear();

    /// <summary>True if weapon <paramref name="weaponId"/> has a registered per-model muzzle offset.</summary>
    public static bool TryGetMuzzleOffset(int weaponId, out Vector3 offset)
        => MuzzleOffsets.TryGetValue(weaponId, out offset);

    /// <summary>
    /// The muzzle offset <see cref="SetupShot"/> uses for <paramref name="actor"/>: the registered per-model
    /// <c>tag_shot</c> of the actor's active weapon (<see cref="Entity.ActiveWeaponId"/>) if any, else the
    /// generic <see cref="DefaultMuzzleOffset"/>. Model-local Quake coords (X = forward, Y = +left, Z = up).
    /// </summary>
    public static Vector3 MuzzleOffsetFor(Entity actor)
        => actor is not null && MuzzleOffsets.TryGetValue(actor.ActiveWeaponId, out Vector3 o)
            ? o : DefaultMuzzleOffset;

    /// <summary>QC Q3SURFACEFLAG_SKY — a shot hitting this surface stops (no impact/penetration).</summary>
    public const int Q3SurfaceFlagSky = 0x4;

    /// <summary>
    /// Port of W_SetupShot_Dir_ProjectileSize_Range (server/weapons/tracing.qc): from the actor's eye,
    /// trace forward along <paramref name="forward"/> to find the trueaim point, then nudge the muzzle
    /// origin out of any wall and recompute the shot direction toward the aim point.
    ///
    /// For penetrate-walls weapons (rifle/machinegun/okmg) QC deliberately trueaims only against bodies
    /// (DPCONTENTS_BODY|CORPSE), so the centered shot passes through glass/walls to hit a target behind
    /// them instead of stopping on the wall; we approximate that with a NoMonsters body trace when
    /// <paramref name="penetrateWalls"/> is set. Still deferred vs QC: antilag, warpzones, the trueaim
    /// minrange clamp and accuracy/hitplot bookkeeping.
    /// </summary>
    public static ShotInfo SetupShot(Entity actor, Vector3 forward, Vector3 mins, Vector3 maxs,
        float range = MaxShotDistance, bool penetrateWalls = false,
        Weapon? wep = null, float maxDamage = 0f, float recoil = 0f)
    {
        // [T57] track max damage — the accuracy FIRED credit (QC tracing.qc:64-66): each weapon passes the
        // shot's potential damage (its QC W_SetupShot maxdamage arg) so the accuracy% denominator grows.
        // Zero maxDamage (porto/seeker missile) is filtered by accuracy_add's all-zero guard.
        if (wep is not null && (actor.Flags & EntFlags.Client) != 0 && WeaponAccuracyEvents.CanBeGoodDamage(actor))
            WeaponAccuracyEvents.Fired(actor, wep, maxDamage);

        // QC W_SetupShot: `if (!autocvar_g_norecoil) ent.punchangle_x = -recoil;` — the view kicks UP on firing
        // (pitch −recoil°), then decays via PM_check_punch (PlayerPhysics.CheckPunch, −10°/s). Players only (a
        // non-client shooter has no view). The aim/shot direction is NOT punched — only the rendered view is.
        if (recoil != 0f && (actor.Flags & EntFlags.Client) != 0
            && !(Api.Services is not null && Api.Cvars.GetFloat("g_norecoil") != 0f))
            actor.PunchAngle = new Vector3(-recoil, 0f, 0f);

        Vector3 eye = actor.Origin + actor.ViewOfs;
        Vector3 aimEnd = eye + forward * range;

        // Trueaim: where does the centered shot actually land? For penetrate-walls weapons QC deliberately
        // trueaims only against bodies (so the shot aims at a target behind glass rather than stopping on the
        // glass); with no body-only trace filter here, we aim straight at the full-range point and let the
        // penetrating fireBullet pass through the wall to reach it.
        Vector3 shotEnd;
        if (penetrateWalls)
        {
            shotEnd = aimEnd;
        }
        else
        {
            // [T45] trueaim through warpzones (QC W_SetupShot uses WarpZone_TraceLine): the centered shot may aim
            // at a target seen through a portal. If the aim crossed a portal the endpoint lives in the far frame,
            // so the shot DIRECTION (computed in the firing frame below) keeps the straight aimEnd — the fired
            // bullet/projectile re-warps through the same portal itself. With no portal crossed this is the plain
            // trueaim trace exactly as before.
            WarpzoneTraceResult aim = Api.Trace.TraceLineWarpzone(eye, aimEnd, MoveFilter.NoMonsters, actor);
            shotEnd = aim.ZonesCrossed > 0 ? aimEnd : aim.Trace.EndPos;
        }

        // Nudge the muzzle origin so it never sits inside a wall (QC tracebox of the projectile size).
        TraceResult org = Api.Trace.Trace(eye, mins, maxs, eye, MoveFilter.Normal, actor);
        Vector3 shotOrg = org.StartSolid ? eye : org.EndPos;

        // Muzzle offset (QC W_SetupShot: ent.(weaponentity).movedir) — slide the origin from the eye to the gun
        // muzzle so the shot visibly leaves the WEAPON, not the camera. Only a player carries a view weapon. The
        // exact per-model offset is the weapon model's tag_shot, available from the parsed model data: the host
        // extracts it at weapon-model load (MuzzleTag/CL_WeaponEntity_SetModel) and registers it per weapon id, so
        // MuzzleOffsetFor returns that movedir; a weapon with no registered tag (dedicated server / tag-less model)
        // falls back to the generic DefaultMuzzleOffset. The offset is in model-local Quake coords (X=fwd, Y=+left,
        // Z=up) — Base's right*(-md.y) handles Quake +y=left — and the shot DIRECTION is recomputed to the trueaim
        // point below, so the round still lands on the crosshair. Tracebox each leg so the muzzle never ends up
        // inside a wall.
        Vector3 muzzle = MuzzleOffsetFor(actor);
        if (muzzle != Vector3.Zero && (actor.Flags & EntFlags.Client) != 0)
        {
            QMath.AngleVectors(actor.Angles, out _, out Vector3 right, out Vector3 up);
            Vector3 dv = right * -muzzle.Y + up * muzzle.Z;                         // sideways + vertical
            shotOrg = Api.Trace.Trace(shotOrg, mins, maxs, shotOrg + dv, MoveFilter.Normal, actor).EndPos;
            Vector3 fwd = QMath.Normalize(forward) * muzzle.X;                      // forward
            shotOrg = Api.Trace.Trace(shotOrg, mins, maxs, shotOrg + fwd, MoveFilter.Normal, actor).EndPos;
        }

        Vector3 shotDir = QMath.Normalize(shotEnd - shotOrg);
        if (shotDir == Vector3.Zero) shotDir = QMath.Normalize(forward);

        return new ShotInfo(shotOrg, shotDir, shotEnd);
    }

    /// <summary>W_SetupShot convenience overload (zero projectile size, v_forward direction).</summary>
    public static ShotInfo SetupShot(Entity actor, Vector3 forward, float range = MaxShotDistance,
        bool penetrateWalls = false, Weapon? wep = null, float maxDamage = 0f, float recoil = 0f)
        => SetupShot(actor, forward, Vector3.Zero, Vector3.Zero, range, penetrateWalls, wep, maxDamage, recoil);

    /// <summary>
    /// Hitscan bullet (back-compat single-arg overload) — keeps existing call sites working.
    /// Equivalent to <see cref="FireBullet(Entity,Vector3,Vector3,float,float,int,float,float,float,float,float,float,float)"/>
    /// with no spread, no penetration, no falloff, no force.
    /// </summary>
    public static Entity? FireBullet(Entity actor, Vector3 start, Vector3 dir, float range, float damage, int deathType)
        => FireBullet(actor, start, dir, range, damage, deathType, spread: 0f, solidPenetration: 0f);

    /// <summary>
    /// Full port of fireBullet_falloff (server/weapons/tracing.qc): spreads <paramref name="dir"/> with the
    /// hitscan spread style, then traces from <paramref name="start"/> repeatedly, passing THROUGH solids up
    /// to <paramref name="solidPenetration"/> world units (damage attenuating by the penetration fraction)
    /// so a single bullet can hit several entities behind a thin wall. Each victim takes
    /// <paramref name="damage"/> scaled by exponential distance falloff plus a knockback impulse of
    /// <paramref name="force"/> along the bullet. Returns the first damageable entity hit (or null).
    ///
    /// Faithful to QC: spread (W_CalculateSpread hitscan style), solid penetration loop with
    /// <c>damage_fraction = penFrac ^ exponent</c>, exponential falloff for damage + force, double-hit
    /// guard, sky/out-of-world stop, and the head-AABB headshot multiplier (QC tracing.qc:441-445 — when
    /// <paramref name="headshotMultiplier"/> is nonzero and the segment passes through the victim's
    /// <see cref="Headshot"/> box, the running <c>damage</c> is scaled and the shooter gets the
    /// ANNCE_HEADSHOT announce, QC tracing.qc:528-529). Deferred (render/online): antilag takeback,
    /// warpzones, EFFECT_BULLET tracer, accuracy bookkeeping.
    /// </summary>
    public static Entity? FireBullet(Entity actor, Vector3 start, Vector3 dir, float range, float damage,
        int deathType, float spread, float solidPenetration,
        float falloffHalflife = 0f, float falloffMinDist = 0f, float falloffMaxDist = 0f,
        float force = 0f, float falloffForceHalflife = 0f, float headshotMultiplier = 0f)
    {
        LagComp.Begin(actor); // rewind other players to the shooter's view-time for fair hit-reg (antilag.qc)
        try
        {
        dir = CalculateSpread(QMath.Normalize(dir), spread, mustNormalize: true);
        Vector3 end = start + dir * range;

        float solidPenetrationFraction = 1f;
        float damageFraction = 1f;
        float totalDamage = 0f; // QC total_damage — the running ballistic hit-credit cap (tracing.qc:378)
        Entity? lastHit = null;
        Entity? firstHit = null;
        bool headshot = false; // QC fireBullet_falloff: one of the hit targets was a headshot
        Vector3 cur = start;

        // QC g_ballistics_solidpenetration_exponent default, g_ballistics_mindistance default.
        const float ballisticsExponent = 1f;
        const float ballisticsMinDistance = 1f;

        for (int guard = 0; guard < 32; ++guard)
        {
            // [T45] warpzone-aware sweep (QC fireBullet uses WarpZone_TraceLine): the segment crosses any linked
            // portals, so a bullet fired at a portal mouth continues out of the linked portal and hits a far-side
            // target. On a non-warpzone map this is exactly the plain trace. After a crossing, the running aim
            // direction and the segment end are rotated into the far-side frame so the penetration loop continues
            // straight on the far side (the accumulated transform is the C# WarpZone_trace_transform).
            WarpzoneTraceResult wzr = Api.Trace.TraceLineWarpzone(cur, end, MoveFilter.Normal, actor);
            TraceResult tr = wzr.Trace;
            cur = tr.EndPos;
            Entity? hit = tr.Ent;
            if (wzr.ZonesCrossed > 0)
            {
                // carry the aim direction + the remaining segment end into the frame the trace ended in.
                dir = QMath.Normalize(wzr.Transform.TransformDirection(dir));
                end = wzr.Transform.TransformPoint(end);
            }

            if (tr.Fraction >= 1f) break;                          // hit nothing -> done
            if ((tr.DpHitQ3SurfaceFlags & Q3SurfaceFlagSky) != 0) break; // sky stops the bullet

            // Avoid hitting the same entity twice (engine re-hit) and self-damage.
            if (hit is not null && !ReferenceEquals(hit, actor) && !ReferenceEquals(hit, lastHit)
                && hit.TakeDamage != DamageMode.No)
            {
                lastHit = hit;
                firstHit ??= hit;

                // Head-AABB headshot (QC tracing.qc:441-445): scale the RUNNING damage so on a
                // multi-penetration shot the boosted damage carries to subsequent victims, faithful to QC
                // mutating its `damage` local. `cur` is this segment's start (== QC's post-trace `start`).
                if (headshotMultiplier != 0f && Headshot(hit, actor, cur, end))
                {
                    damage *= headshotMultiplier;
                    headshot = true;
                }

                // [T57] capture the accuracy gate BEFORE Damage (QC tracing.qc:446) so the kill shot counts.
                bool goodDamage = WeaponAccuracyEvents.IsGoodDamage(actor, hit);

                float dealt = damage * damageFraction;
                Vector3 dealtForce = dir * (force * damageFraction);
                if (falloffHalflife != 0f || falloffForceHalflife != 0f)
                {
                    float dist = (hit.Origin - actor.Origin).Length();
                    if (falloffHalflife != 0f)
                        dealt *= ExponentialFalloff(falloffMinDist, falloffMaxDist, falloffHalflife, dist);
                    if (falloffForceHalflife != 0f)
                        dealtForce *= ExponentialFalloff(falloffMinDist, falloffMaxDist, falloffForceHalflife, dist);
                }
                ApplyDamage(hit, actor, dealt, deathType, inflictor: actor, force: dealtForce, hitLoc: cur);

                // [T57] ballistic hit credit (QC tracing.qc:470-477): per-hit credit is the PENETRATION-scaled
                // damage WITHOUT the distance falloff, capped so a multi-target bullet never exceeds 100%.
                if (goodDamage)
                {
                    float addedDamage = MathF.Min(damage - totalDamage, damage * damageFraction);
                    totalDamage += damage * damageFraction;
                    WeaponAccuracyEvents.Hit(actor, Inventory.CurrentWeapon(actor), addedDamage); // add to hit
                }
            }

            // Penetrate the solid we just hit, if allowed. -1 means "no penetration ever".
            if (solidPenetration < 0f) break;

            float maxDist = solidPenetration * solidPenetrationFraction;
            if (maxDist <= ballisticsMinDistance) break;

            // Walk forward through the solid: advance to maxDist past the hit point.
            Vector3 throughStart = cur + dir * 0.03125f;  // tiny nudge into the solid
            Vector3 throughEnd = cur + dir * maxDist;
            TraceResult through = Api.Trace.Trace(throughEnd, Vector3.Zero, Vector3.Zero, throughStart,
                MoveFilter.Normal, actor);
            // through traces backward; if it never left solid (fraction 1) the wall is too thick -> stop.
            if (through.Fraction >= 1f) break;

            float distTaken = MathF.Max(ballisticsMinDistance, (through.EndPos - cur).Length());
            float fractionUsed = distTaken / maxDist;
            solidPenetrationFraction = MathF.Max(0f, solidPenetrationFraction - solidPenetrationFraction * fractionUsed);
            damageFraction = MathF.Pow(solidPenetrationFraction, ballisticsExponent);

            cur = through.EndPos;
        }

        // QC tracing.qc:528-529: announce the headshot to the shooter.
        if (headshot && (actor.Flags & EntFlags.Client) != 0)
            NotificationSystem.Announce(actor, "HEADSHOT");

        return firstHit;
        }
        finally { LagComp.End(); }
    }

    /// <summary>
    /// Back-compat railgun overload (single target, no force/falloff) — keeps existing call sites working.
    /// </summary>
    public static Entity? FireRailgunBullet(Entity actor, Vector3 start, Vector3 end, float damage, int deathType)
        => FireRailgunBullet(actor, start, end, damage, deathType, force: 0f);

    /// <summary>
    /// Port of FireRailgunBullet (server/weapons/tracing.qc): an instant beam from <paramref name="start"/>
    /// to <paramref name="end"/> that PIERCES every entity along the way (each is made non-solid for the
    /// next trace, like QC's railgunhit list), damaging them all with exponential distance falloff and a
    /// knockback impulse of <paramref name="force"/> along the beam. The beam stops at the first world
    /// surface. Restores solidity afterwards. When <paramref name="headshotNotify"/> is set (QC's
    /// <c>headshot_notify</c> arg — true for the Vaporizer, false for the Vortex) and the beam passes through
    /// a victim's <see cref="Headshot"/> box, the shooter gets the ANNCE_HEADSHOT announce (QC tracing.qc:
    /// 267-268 + 343-344). Deferred: warpzones, antilag, the nearby-whoosh sound, the beam particle.
    /// </summary>
    public static Entity? FireRailgunBullet(Entity actor, Vector3 start, Vector3 end, float damage, int deathType,
        float force, float falloffMinDist = 0f, float falloffMaxDist = 0f,
        float falloffHalflife = 0f, float falloffForceHalflife = 0f, bool headshotNotify = false)
    {
        LagComp.Begin(actor); // rewind other players to the shooter's view-time for fair hit-reg (antilag.qc)
        try
        {
        Vector3 dir = QMath.Normalize(end - start);
        end += dir; // go a little into the wall so the final trace registers it

        // Walk the beam, collecting hits and temporarily removing each so we can reach the next.
        var pierced = new List<(Entity ent, Vector3 loc, float dist, Solid solid)>();
        Entity? first = null;
        bool headshot = false; // QC FireRailgunBullet: one of the pierced targets was a headshot
        Vector3 cur = start;
        Vector3 beamStart = start; // QC headshot uses the original start/end; updated into the far frame on a cross
        Vector3 beamEnd = end;
        for (int guard = 0; guard < 64; ++guard)
        {
            // [T45] warpzone-aware beam sweep (QC FireRailgunBullet uses WarpZone_TraceLine): the beam continues
            // through linked portals so the rail can pierce a target on the far side. Plain trace on a non-warpzone
            // map. After a crossing the beam's running start/end + direction move into the far-side frame so the
            // headshot box test and the next pierce segment stay in one consistent frame.
            WarpzoneTraceResult wzr = Api.Trace.TraceLineWarpzone(cur, end, MoveFilter.Normal, actor);
            TraceResult tr = wzr.Trace;
            Entity? hit = tr.Ent;
            if (wzr.ZonesCrossed > 0)
            {
                dir = QMath.Normalize(wzr.Transform.TransformDirection(dir));
                end = wzr.Transform.TransformPoint(end);
                beamStart = wzr.Transform.TransformPoint(beamStart);
                beamEnd = wzr.Transform.TransformPoint(beamEnd);
            }

            if (hit is null || tr.Fraction >= 1f) break; // hit world / nothing -> stop

            // Head-AABB headshot against the full beam (QC tracing.qc:267-268 uses the original start/end —
            // in the hit's frame after any portal crossing).
            if (headshotNotify && !headshot && Headshot(hit, actor, beamStart, beamEnd))
                headshot = true;

            // Record and make non-solid so the next trace passes through. Distance is measured along the beam in
            // the hit's frame (beamStart), so a far-side pierce gets a sensible falloff distance.
            pierced.Add((hit, tr.EndPos, (tr.EndPos - beamStart).Length(), hit.Solid));
            first ??= hit;

            if (hit.Solid == Solid.Bsp) break; // a world brush ends the beam
            hit.Solid = Solid.Not;
            cur = tr.EndPos;
        }

        // Restore solidity, then apply damage + falloff to everyone we passed through.
        foreach (var p in pierced) p.ent.Solid = p.solid;
        float totalDmg = 0f; // QC totaldmg — the per-target falloff-scaled good damage (tracing.qc:323-324)
        foreach (var p in pierced)
        {
            float foff = (falloffHalflife != 0f)
                ? ExponentialFalloff(falloffMinDist, falloffMaxDist, falloffHalflife, p.dist) : 1f;

            // [T57] gate captured BEFORE Damage (QC tracing.qc:323): credit accumulates per pierced target.
            if (WeaponAccuracyEvents.IsGoodDamage(actor, p.ent))
                totalDmg += damage * foff;

            if (p.ent.TakeDamage == DamageMode.No) continue;
            float ffs = (falloffForceHalflife != 0f)
                ? ExponentialFalloff(falloffMinDist, falloffMaxDist, falloffForceHalflife, p.dist) : 1f;
            ApplyDamage(p.ent, actor, damage * foff, deathType, inflictor: actor,
                force: dir * (force * ffs), hitLoc: p.loc);
        }

        // [T57] ONE hit credit per shot, capped at one shot's damage (QC tracing.qc:346-348) — a beam through
        // two players still credits min(bdamage, totaldmg); the >100% case shows as byte 255 over fired.
        WeaponAccuracyEvents.Hit(actor, Inventory.CurrentWeapon(actor), MathF.Min(damage, totalDmg));

        // QC tracing.qc:343-344: announce the headshot to the shooter.
        if (headshot && (actor.Flags & EntFlags.Client) != 0)
            NotificationSystem.Announce(actor, "HEADSHOT");

        return first;
        }
        finally { LagComp.End(); }
    }

    // =========================================================================
    //  Headshot head-AABB (server/weapons/tracing.qc Headshot + common/util.qc trace_hits_box)
    // =========================================================================

    /// <summary>
    /// Port of <c>Headshot</c> (server/weapons/tracing.qc:220-229): does the shot segment
    /// <paramref name="start"/>→<paramref name="end"/> pass through the head box of <paramref name="targ"/>?
    /// Only living, unfrozen, damageable players have a head box. The box is QC's exact head AABB centred on
    /// the victim's origin: horizontally 60% of the body box, vertically from <c>1.3*view_ofs_z - 0.3*maxs_z</c>
    /// up to <c>maxs_z</c>. (QC's per-axis basis-vector multiply collapses to the component-wise form below.)
    /// </summary>
    public static bool Headshot(Entity targ, Entity attacker, Vector3 start, Vector3 end)
    {
        // QC: if (!IS_PLAYER(targ) || IS_DEAD(targ) || STAT(FROZEN, targ) || !targ.takedamage) return false;
        if ((targ.Flags & EntFlags.Client) == 0 || targ.IsCorpse) return false;        // IS_PLAYER
        if (targ.DeadState != DeadFlag.No) return false;                                 // IS_DEAD
        if (IsFrozen(targ)) return false;                                                // STAT(FROZEN)
        if (targ.TakeDamage == DamageMode.No) return false;

        Vector3 org = targ.Origin; // antilag already applied to the victim's origin upstream
        Vector3 headMins = new(
            org.X + 0.6f * targ.Mins.X,
            org.Y + 0.6f * targ.Mins.Y,
            org.Z + 1.3f * targ.ViewOfs.Z - 0.3f * targ.Maxs.Z);
        Vector3 headMaxs = new(
            org.X + 0.6f * targ.Maxs.X,
            org.Y + 0.6f * targ.Maxs.Y,
            org.Z + targ.Maxs.Z);

        return TraceHitsBox(start, end, headMins, headMaxs);
    }

    /// <summary>QC <c>STAT(FROZEN, e)</c> — the gametype freeze stat OR the Frozen status effect.</summary>
    private static bool IsFrozen(Entity e)
        => e.FrozenStat != 0
        || (StatusEffectsCatalog.Frozen is { } f && StatusEffectsCatalog.Has(e, f));

    /// <summary>
    /// Port of <c>trace_hits_box</c> (common/util.qc:2219-2237): pure ray-vs-AABB slab test — does the
    /// segment <paramref name="start"/>→<paramref name="end"/> intersect the axis-aligned box
    /// [<paramref name="thmi"/>, <paramref name="thma"/>]? This is a plain math helper in QC (NOT an engine
    /// builtin), so it ports with zero engine dependency. Rebases everything to a ray from the origin to
    /// <c>end-start</c>, then intersects the running [a0,a1] parametric interval per axis.
    /// </summary>
    public static bool TraceHitsBox(Vector3 start, Vector3 end, Vector3 thmi, Vector3 thma)
    {
        end -= start;
        thmi -= start;
        thma -= start;
        // now it is a trace from 0 to end

        float a0 = 0f, a1 = 1f;
        if (!TraceHitsBox1d(end.X, thmi.X, thma.X, ref a0, ref a1)) return false;
        if (!TraceHitsBox1d(end.Y, thmi.Y, thma.Y, ref a0, ref a1)) return false;
        if (!TraceHitsBox1d(end.Z, thmi.Z, thma.Z, ref a0, ref a1)) return false;
        return true;
    }

    /// <summary>Port of <c>trace_hits_box_1d</c> (common/util.qc:2197-2217): one-axis slab clip.</summary>
    private static bool TraceHitsBox1d(float end, float thmi, float thma, ref float a0, ref float a1)
    {
        if (end == 0f)
        {
            // just check if 0 is in range for this axis
            if (0f < thmi) return false;
            if (0f > thma) return false;
        }
        else
        {
            // 0 -> end must stay within thmi -> thma
            a0 = MathF.Max(a0, MathF.Min(thmi / end, thma / end));
            a1 = MathF.Min(a1, MathF.Max(thmi / end, thma / end));
            if (a0 > a1) return false;
        }
        return true;
    }

    /// <summary>
    /// Route a weapon hit through the real damage pipeline (<see cref="Combat.Damage"/> ->
    /// <see cref="Damage.DamageSystem"/>): armor/health split, knockback, godmode/dead gating, and the
    /// Killed/Death-hook path now all live there (port of server/damage.qc). The
    /// <c>PlayerDamage_SplitHealthArmor</c> mutator hook (vampire etc.) fires inside the pipeline.
    ///
    /// The signature is kept caller-stable: existing weapons (Blaster/Vortex/Machinegun) call
    /// <c>ApplyDamage(hit, actor, damage, deathType)</c>; <paramref name="inflictor"/>,
    /// <paramref name="force"/> and <paramref name="hitLoc"/> are optional so those call sites are
    /// unaffected. <paramref name="deathType"/> stays an int weapon id at the call sites and is mapped to
    /// the pipeline's string deathtype tag here.
    /// </summary>
    public static void ApplyDamage(Entity target, Entity attacker, float damage, int deathType = 0,
        Entity? inflictor = null, Vector3 force = default, Vector3 hitLoc = default)
    {
        if (damage <= 0f && force == Vector3.Zero) return;

        // Map the int deathtype id to the string tag the pipeline carries (DeathTypes stand-in for the QC
        // registry). The call sites pass the WEAPON RegistryId; 0 doubles as the "unattributed" sentinel
        // (which aliases weapon id 0 — Arc — whose beam therefore tags Generic; pre-existing convention).
        // [T57] FIX: this previously tagged FromWeapon(attacker.NetName) — the PLAYER's name — so every
        // weapon kill carried an unresolvable "weapon/<playername>" tag (generic kill feed, no per-weapon
        // attribution). Resolve the registry id to the weapon's NetName instead.
        string deathTag = deathType > 0 && deathType < Registry<Weapon>.Count
            ? Damage.DeathTypes.FromWeapon(Registry<Weapon>.ById(deathType).NetName)
            : Damage.DeathTypes.Generic;

        // inflictor defaults to the attacker for direct hitscan (QC passes the bullet's owner == attacker).
        Damage.Combat.Damage(target, inflictor ?? attacker, attacker, damage, deathTag, hitLoc, force);
    }

    // =========================================================================
    //  Shared spread / velocity / falloff math (common/weapons/calculations.qc,
    //  server/weapons/tracing.qc, lib/math.qh) — used by all weapons.
    // =========================================================================

    /// <summary>autocvar_g_weaponspreadfactor (default 1) — global spread scale.</summary>
    public static float WeaponSpreadFactor =>
        Api.Services is null ? 1f : (Api.Cvars.GetFloat("g_weaponspreadfactor") is var f && f > 0f ? f : 1f);

    /// <summary>
    /// Port of W_CalculateSpread (calculations.qc) — the default spread style 0 used by both projectile and
    /// hitscan paths: <c>dir + randomvec() * spread</c>, optionally re-normalized. Uses the deterministic
    /// <see cref="Prandom"/> (QC random()/randomvec()), so server and predicting client agree (ADR-0010).
    /// </summary>
    public static Vector3 CalculateSpread(Vector3 dir, float spread, bool mustNormalize)
    {
        spread *= WeaponSpreadFactor;
        if (spread <= 0f) return mustNormalize ? QMath.Normalize(dir) : dir;

        Vector3 v = dir + Prandom.Vec() * spread; // randomvec() == [-1,1)^3
        return mustNormalize ? QMath.Normalize(v) : v;
    }

    /// <summary>
    /// Port of W_CalculateSpreadPattern(pattern 1, …) (calculations.qc): lay <paramref name="total"/> shots
    /// out as a fan — shot 0 is dead center, the rest sweep an even arc. Returns an offset whose Y/Z map to
    /// the right/up axes (X unused). <paramref name="bias"/> pulls outer shots back toward center by up to
    /// that fraction (deterministic <see cref="Prandom"/>).
    /// </summary>
    public static Vector3 CalculateSpreadPattern(int counter, int total, float bias = 0f)
    {
        if (counter == 0 || total <= 1) return Vector3.Zero;
        // makevectors('0 360 0' * (0.75 + (counter - 0.5) / (total - 1))) then s.y=fwd.x, s.z=fwd.y.
        float yawDeg = 360f * (0.75f + (counter - 0.5f) / (total - 1));
        QMath.AngleVectors(new Vector3(0f, yawDeg, 0f), out Vector3 fwd, out _, out _);
        Vector3 s = new(0f, fwd.X, fwd.Y);
        if (bias != 0f) s *= (1f - bias) + Prandom.Float() * bias;
        return s;
    }

    /// <summary>
    /// Port of W_SetupProjVelocity_Explicit (server/weapons/tracing.qc) projectile-velocity core: fold the
    /// up/z launch speeds into <paramref name="dir"/>, renormalize, apply spread, then scale by speed. The
    /// QC Newtonian velocity-inheritance (g_projectiles_newton_style) defaults OFF, so the result is just
    /// <c>speed * spread(normalize(dir + up*(upSpeed/speed) + z*(zSpeed/speed)))</c>.
    /// </summary>
    public static Vector3 ProjectileVelocity(Vector3 dir, Vector3 up, float speed,
        float upSpeed = 0f, float zSpeed = 0f, float spread = 0f)
    {
        if (speed == 0f) return Vector3.Zero;
        dir += up * (upSpeed / speed);
        dir.Z += zSpeed / speed;
        speed *= dir.Length();
        dir = QMath.Normalize(dir);
        dir = CalculateSpread(dir, spread, mustNormalize: false);
        return QMath.Normalize(dir) * speed;
    }

    /// <summary>
    /// Port of ExponentialFalloff (lib/math.qh): a multiplier in (0,1] that halves every
    /// <paramref name="halflifeDist"/> units of distance, clamped to [<paramref name="minDist"/>,
    /// <paramref name="maxDist"/>]. <paramref name="halflifeDist"/> &lt;= 0 disables falloff (returns 1).
    /// </summary>
    public static float ExponentialFalloff(float minDist, float maxDist, float halflifeDist, float d)
    {
        if (halflifeDist > 0f)
            return MathF.Pow(0.5f, (QMath.Clamp(d, minDist, maxDist) - minDist) / halflifeDist);
        if (halflifeDist < 0f)
            return MathF.Pow(0.5f, (QMath.Clamp(d, minDist, maxDist) - maxDist) / halflifeDist);
        return 1f;
    }
}
