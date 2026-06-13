using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Tesla Coil Turret — port of common/turrets/turret/tesla.{qh,qc} (+ tesla_weapon.qc). A short-range area
/// defense turret (TUR_FLAG_HITSCAN) that electrocutes everything near it: it strikes the nearest valid target
/// with a high-voltage arc, which then CHAINS to nearby targets, each hop dealing 75% of the previous damage
/// over 85% of the previous range, up to 10 hops. No aiming/tracking (TFL_AIM_NO | TFL_TRACK_NO) — it's
/// omnidirectional. Identity/hitbox from tesla.qh; balance from turrets.cfg (<c>g_turrets_unit_tesla_*</c>).
///
/// Mechanic implemented faithfully: the chain-lightning (tesla_weapon.qc <c>toast</c> loop) — nearest LOS
/// target first, then jump to the closest not-yet-hit valid target within the shrinking range, applying the
/// decaying damage, never hitting the same entity twice per discharge. Custom firecheck (TFL_SHOOT_CUSTOM):
/// fires whenever a target is in range and it's cooled down. The visual lightning arcs are deferred.
/// </summary>
[Turret]
public sealed class TeslaTurret : Turret
{
    // --- balance (turrets.cfg g_turrets_unit_tesla_*) ---
    private const float ShotDamage = 200f;
    private const float ShotForce = 400f;
    private const float ShotRefire = 1.5f;
    private const float TargetRange = 1000f;
    private const float TargetRangeMin = 0f;
    private const float AmmoMax = 1000f;
    private const float AmmoRecharge = 15f;
    private const int MaxChainHops = 10;
    private const float ChainDamageFalloff = 0.75f; // damage *= 0.75 per hop
    private const float ChainRangeFalloff = 0.85f;  // range  *= 0.85 per hop

    // QC tesla.qc tr_setup: players + missiles, range-limited, team-checked. No range-limit on the chain hops.
    private const int Select = TurretAI.SelectPlayers | TurretAI.SelectMissiles
                             | TurretAI.SelectRangeLimits | TurretAI.SelectTeamCheck;
    // Chain hops use the validate flags WITHOUT range limits (QC drops RANGELIMITS for the chain).
    private const int ChainSelect = TurretAI.SelectPlayers | TurretAI.SelectMissiles | TurretAI.SelectTeamCheck;

    public TeslaTurret()
    {
        NetName = "tesla";
        DisplayName = "Tesla Coil";
        Model = "models/turrets/tesla_base.md3";
        StartHealth = 1000f;
        Range = TargetRange;
    }

    public override void Spawn(Entity e)
        => TurretSpawn.Init(this, e, new Vector3(-60f, -60f, 0f), new Vector3(60f, 60f, 128f),
            AmmoMax, AmmoRecharge, shotVolly: 0);

    public override void Think(Entity e)
    {
        TurretState st = TurretAI.State(e);
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        float frameTime = Api.Services is not null ? Api.Clock.FrameTime : 0f;

        if (st.Ammo < st.AmmoMax)
            st.Ammo = System.Math.Min(st.Ammo + st.AmmoRecharge * frameTime, st.AmmoMax);

        if (!st.Active) return;   // inactive (team-gated) or dead turrets don't discharge

        st.ShotOrg = TurretAI.ShotOrigin(e);

        // Custom firecheck (tesla.qc turret_tesla_firecheck): rescan, then require cooldown + ammo + a target.
        e.Enemy = TurretAI.SelectTarget(e, Select, TargetRangeMin, TargetRange);
        if (e.Enemy is null) return;
        if (st.AttackFinished > now) return;
        if (st.Ammo < ShotDamage) return;

        Discharge(e);
        st.AttackFinished = now + ShotRefire;
        st.Ammo -= ShotDamage;
    }

    public override bool ValidTarget(Entity self, Entity target)
        => TurretAI.ValidTarget(self, target, Select, TargetRangeMin, TargetRange);

    // tesla_weapon.qc toast-loop: arc to the nearest target, then chain outward with decaying damage/range.
    private void Discharge(Entity turret)
    {
        var hitAlready = new HashSet<Entity>();
        Vector3 from = TurretAI.State(turret).ShotOrg;

        float damage = ShotDamage;
        float range = TargetRange;

        // First hop uses the full target_range (matching the enemy we already selected).
        Entity? current = Toast(turret, from, range, damage, hitAlready);

        for (int i = 0; i < MaxChainHops && current is not null; i++)
        {
            damage *= ChainDamageFalloff;
            range *= ChainRangeFalloff;
            current = Toast(turret, current.Origin, range, damage, hitAlready);
        }

        if (Api.Services is not null)
            Api.Sound.Play(turret, SoundChannel.Weapon, "weapons/electro_fire.wav");

        // NOTE (client-render): the te_csqc_lightningarc visual between each from→target, the head avelocity
        // spin-up, and the idle random arc effect. The server-side chain damage (tesla_weapon.qc) is done above.
    }

    /// <summary>
    /// Port of <c>toast()</c> (tesla_weapon.qc): find the closest not-yet-hit valid target within
    /// <paramref name="range"/> of <paramref name="from"/> that has clear line of sight, damage it, mark it hit,
    /// and return it (so the next hop chains from there). Returns null if the arc has nowhere to jump.
    /// </summary>
    private Entity? Toast(Entity turret, Vector3 from, float range, float damage, HashSet<Entity> hitAlready)
    {
        if (Api.Services is null) return null;

        Entity? target = null;
        float nearest = range + 1f;

        foreach (Entity e in Api.Entities.FindInRadius(from, range))
        {
            if (hitAlready.Contains(e)) continue;
            if (!TurretAI.ValidTarget(turret, e, ChainSelect, 0f, range)) continue;

            // LOS from the arc's current node to the target center (QC traceline MOVE_WORLDONLY).
            Vector3 center = TurretAI.TargetCenter(e);
            TraceResult tr = Api.Trace.Trace(from, Vector3.Zero, Vector3.Zero, center, MoveFilter.WorldOnly, turret);
            if (tr.Fraction < 1f && !ReferenceEquals(tr.Ent, e)) continue;

            float d = (e.Origin - from).Length();
            if (d < nearest)
            {
                nearest = d;
                target = e;
            }
        }

        if (target is not null)
        {
            // te_csqc_lightningarc(from, target.origin) — the visible high-voltage arc to this hop's target.
            EffectEmitter.TeCsqcLightningArc(from, target.Origin);
            // tesla_weapon.qc: each arc hop is DEATH_TURRET_TESLA.
            Combat.Damage(target, turret, turret, damage, DeathTypes.TurretTesla,
                target.Origin, dir(from, target) * ShotForce);
            hitAlready.Add(target);
        }
        return target;

        static Vector3 dir(Vector3 a, Entity b) => QMath.Normalize(b.Origin - a);
    }
}
