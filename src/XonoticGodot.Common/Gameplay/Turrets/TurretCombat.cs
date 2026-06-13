using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Turret-side hitscan + beam helpers that add the pieces the shared <see cref="WeaponFiring"/> defers
/// (deterministic spread cone + knockback force), which the turret weapons need to match QC
/// <c>fireBullet</c> / <c>FireImoBeam</c>. Kept here in the turret folder (WeaponFiring.cs is off-limits)
/// and Godot-free. Damage routes through <see cref="Combat.Damage"/> so armor/knockback/kill all flow
/// through the real pipeline.
///
/// Deferred to match the WeaponFiring gaps (not turret-specific): solid penetration / multi-hit, distance
/// falloff, antilag/warpzones, accuracy logging, tracer effects.
/// </summary>
public static class TurretCombat
{
    /// <summary>
    /// Faithful turret <c>fireBullet</c> (server/weapons/tracing.qc): apply the deterministic spread cone to
    /// <paramref name="dir"/>, trace from <paramref name="start"/>, and damage the first hit with
    /// <paramref name="damage"/> + an outward <paramref name="force"/> knockback along the shot direction.
    /// Returns the entity hit (or null).
    /// </summary>
    public static Entity? FireBullet(Entity turret, Vector3 start, Vector3 dir, float spread,
        float damage, float force, string deathType)
    {
        if (Api.Services is null) return null;

        Vector3 shotDir = ApplySpread(dir, spread);
        Vector3 end = start + shotDir * TurretAI.MaxShotDistance;
        TraceResult tr = Api.Trace.Trace(start, Vector3.Zero, Vector3.Zero, end, MoveFilter.Normal, turret);

        Entity? hit = tr.Ent;
        if (hit is not null && hit.TakeDamage != DamageMode.No)
            Combat.Damage(hit, turret, turret, damage, deathType, tr.EndPos, shotDir * force);
        return hit;
    }

    /// <summary>
    /// W_CalculateSpread core (common/weapons/calculations.qc) — the deterministic spread cone (ADR-0010,
    /// <see cref="Prandom"/>). A zero spread returns the normalized direction unchanged.
    /// </summary>
    public static Vector3 ApplySpread(Vector3 dir, float spread)
    {
        Vector3 fwd = QMath.Normalize(dir);
        if (spread <= 0f) return fwd;
        QMath.AngleVectors(QMath.VecToAngles(fwd), out Vector3 f, out Vector3 r, out Vector3 u);
        return Prandom.Spread(f, r, u, spread);
    }
}
