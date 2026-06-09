using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Shared splash-damage helper for the projectile weapons — the Godot-free essence of
/// RadiusDamageForSource (server/g_damage.qc). Everything within <paramref name="radius"/> of
/// <paramref name="center"/> takes damage scaled linearly from the core <c>damage</c> at the center down
/// to <c>edgeDamage</c> at the rim, plus an outward knockback impulse scaled the same way, all routed
/// through the real damage pipeline via <see cref="WeaponFiring.ApplyDamage"/>.
///
/// This is a NEW shared helper (it deliberately does NOT touch WeaponFiring.cs) factored out so Mortar,
/// Devastator, Crylink, Electro and Hagar share one faithful blast model instead of each copying the
/// Blaster's private version. The Blaster keeps its own equivalent private method; behaviour matches.
///
/// Deferred vs QC (same gaps the Blaster flags): line-of-sight occlusion, the energy-conserving knockback
/// cubic (lives in the damage pipeline, DamageSystem.cs), self-damage radius scaling, force_zscale shaping,
/// and Damage_DamageInfo blast networking.
/// </summary>
public static class WeaponSplash
{
    /// <summary>
    /// Full headless port of RadiusDamageForSource (server/damage.qc). Everything within
    /// <paramref name="radius"/> of <paramref name="center"/> takes damage interpolated from
    /// <paramref name="damage"/> at the center to <paramref name="edgeDamage"/> at the rim, plus an outward
    /// knockback impulse. Faithful to QC:
    /// <list type="bullet">
    /// <item>knockback direction points from the blast toward the victim's bbox/view center (not from the
    /// victim's origin), and its magnitude is <c>(finaldmg / max(core, edge)) * force</c> — i.e. it tracks
    /// the damage falloff exactly, as QC does;</item>
    /// <item><paramref name="forceZScale"/> scales the vertical knockback component (QC force_xyzscale.z,
    /// e.g. the Blaster's force_zscale launch boost);</item>
    /// <item>line-of-sight: if a wall blocks the blast from the victim, damage and force are reduced by the
    /// through-floor factors (QC g_throughfloor_damage / _force) — a single LOS trace stands in for QC's
    /// multi-sample box test;</item>
    /// <item>self-damage is scaled by g_balance_selfdamagepercent when the attacker is the victim;</item>
    /// <item>the direct-hit entity takes the blast without the LOS reduction (QC skips the box test for it).</item>
    /// </list>
    /// The Damage_DamageInfo blast-effect networking is the only deferred piece (client render).
    /// </summary>
    public static void RadiusDamage(Entity inflictor, Vector3 center, float damage, float edgeDamage,
        float radius, Entity? attacker, int deathType, float force = 0f, float forceZScale = 1f,
        Entity? directHit = null)
    {
        if (Api.Services is null || radius <= 0f) return;
        Entity src = attacker ?? inflictor;

        float selfDamagePercent = Cvar("g_balance_selfdamagepercent", 0.65f);
        float throughFloorDmg = Cvar("g_throughfloor_damage", 0.5f);
        float throughFloorForce = Cvar("g_throughfloor_force", 0.7f);

        foreach (Entity e in Api.Entities.FindInRadius(center, radius))
        {
            if (e.TakeDamage == DamageMode.No) continue;

            // QC measures to the nearest point of the target's bbox; fall back to origin when it has no size.
            Vector3 targetCenter = e.Origin + (e.Mins + e.Maxs) * 0.5f;
            Vector3 delta = targetCenter - center;
            float dist = delta.Length();
            if (dist > radius) continue;

            float frac = 1f - dist / radius;                 // in [0,1] since dist <= radius
            float finalDmg = damage * frac + edgeDamage * (1f - frac);
            if (finalDmg <= 0f) continue;

            // Knockback toward the victim's center, magnitude scaled by the damage falloff (QC formula).
            Vector3 forceVec = Vector3.Zero;
            float denom = MathF.Max(damage, edgeDamage);
            if (force != 0f && denom > 0f)
            {
                Vector3 dir = dist > 0f ? delta / dist : Vector3.UnitZ;
                forceVec = dir * ((finalDmg / denom) * force);
                if (forceZScale != 1f) forceVec.Z *= forceZScale;
            }

            // Line of sight: the direct-hit target is always fully hit; for others, a blocked trace reduces
            // damage + force by the through-floor factors (QC's multi-sample box test collapsed to one ray).
            if (!ReferenceEquals(e, directHit))
            {
                TraceResult los = Api.Trace.Trace(center, Vector3.Zero, Vector3.Zero, targetCenter,
                    MoveFilter.NoMonsters, inflictor);
                bool blocked = los.Fraction < 1f && !ReferenceEquals(los.Ent, e);
                if (blocked)
                {
                    finalDmg *= throughFloorDmg;
                    forceVec *= throughFloorForce;
                }
            }

            // Self-damage scaling (rocket-jump etc.).
            if (ReferenceEquals(e, src))
                finalDmg *= selfDamagePercent;

            WeaponFiring.ApplyDamage(e, src, finalDmg, deathType, inflictor: inflictor, force: forceVec,
                hitLoc: targetCenter);
        }
    }

    private static float Cvar(string name, float fallback)
    {
        float v = Api.Cvars.GetFloat(name);
        return v != 0f ? v : fallback;
    }

    /// <summary>
    /// Play a weapon's impact/explosion sound at the blast — DP's per-weapon CSQC <c>wr_impacteffect</c>
    /// <c>sound(actor, CH_SHOTS, …)</c>. The port emits explosions SERVER-side (next to <c>EffectEmitter.Emit</c>),
    /// so this networks the cue to every client. <c>CH_SHOTS</c> (= <see cref="SoundChannel.ShotsAuto"/>) is an
    /// auto channel, so simultaneous blasts stack instead of cutting each other off; volume/attenuation default
    /// to VOL_BASE / ATTN_NORM. Use the <paramref name="emitter"/> overload for a projectile (the entity is at
    /// the blast point); use <see cref="ImpactSoundAt"/> for a hitscan trace endpoint (no entity there).
    /// No-op without services or with an empty sample.
    /// </summary>
    public static void ImpactSound(Entity emitter, string sample,
        float volume = SoundLevels.VolBase, float attenuation = SoundLevels.AttenNorm)
    {
        if (Api.Services is not null && !string.IsNullOrEmpty(sample))
            Api.Sound.Play(emitter, SoundChannel.ShotsAuto, sample, volume, attenuation);
    }

    /// <summary>Hitscan-impact variant of <see cref="ImpactSound"/>: play the impact sound at a world POINT (the
    /// trace endpoint, which has no entity) — fire-and-forget, staying put at the impact (DP plays these in
    /// wr_impacteffect at <c>w_org</c>).</summary>
    public static void ImpactSoundAt(Vector3 point, string sample,
        float volume = SoundLevels.VolBase, float attenuation = SoundLevels.AttenNorm)
    {
        if (Api.Services is not null && !string.IsNullOrEmpty(sample))
            Api.Sound.PlayAt(point, SoundChannel.ShotsAuto, sample, volume, attenuation);
    }
}
