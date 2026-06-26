using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Fusion Reactor Turret — port of common/turrets/turret/fusionreactor.{qh,qc}. The one SUPPORT turret
/// (TUR_FLAG_SUPPORT | TUR_FLAG_AMMOSOURCE): it has no weapon and never attacks. Instead it generates power for
/// nearby FRIENDLY turrets, topping up their ammo pool so they can fire more often. Identity/hitbox from
/// fusionreactor.qh; balance from turrets.cfg (<c>g_turrets_unit_fusreac_*</c>).
///
/// Mechanic (faithful): TFL_SHOOT_HITALLVALID + own-team targeting — each think it loops over every same-team
/// turret in range that uses energy and isn't already full, and gives each <c>shot_dmg</c> ammo (capped at
/// their max), spending its own ammo per recipient (fusionreactor.qc tr_attack + turret_fusionreactor_firecheck).
/// No aiming/tracking (TFL_AIM_NO | TFL_TRACK_NO); the head just spins. The smallflash effect is deferred.
/// </summary>
[Turret]
public sealed class FusionReactorTurret : Turret
{
    // --- balance (turrets.cfg g_turrets_unit_fusreac_*) ---
    private const float ShotDamage = 20f;       // ammo given per recipient, and own ammo spent per recipient
    private const float ShotRefire = 0.2f;
    private const float TargetRange = 1024f;
    private const float TargetRangeMin = 1f;
    private const float AmmoMax = 100f;
    private const float AmmoRecharge = 100f;

    // QC fusionreactor.qc tr_setup: team-checked, OWN-team only, range-limited (support, not combat).
    private const int Select = TurretAI.SelectTeamCheck | TurretAI.SelectOwnTeam | TurretAI.SelectRangeLimits;

    public FusionReactorTurret()
    {
        NetName = "fusreac";
        DisplayName = "Fusion Reactor";
        Model = "models/turrets/base.md3";
        StartHealth = 700f;
        Range = TargetRange;
    }

    // QC turrets.cfg g_turrets_unit_fusreac_respawntime 90 (passed explicitly; TurretSpawn.Init default is 60).
    private const float RespawnTime = 90f;

    public override void Spawn(Entity e)
        => TurretSpawn.Init(this, e, new Vector3(-34f, -34f, 0f), new Vector3(34f, 34f, 90f),
            AmmoMax, AmmoRecharge, shotVolly: 0, respawnTime: RespawnTime);

    public override void Think(Entity e)
    {
        TurretState st = TurretAI.State(e);
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        float frameTime = Api.Services is not null ? Api.Clock.FrameTime : 0f;

        if (st.Ammo < st.AmmoMax)
            st.Ammo = System.Math.Min(st.Ammo + st.AmmoRecharge * frameTime, st.AmmoMax);

        // tr_think: the head's yaw angular velocity scales with how full our own ammo pool is (full = 250 deg/s,
        // empty = 0). Runs every think for every turret (called at the end of turret_think). Networked (TNSF_AVEL)
        // and integrated by the client each render frame; here it lives on the turret state for when the turret
        // client-render lands. (fusionreactor.qc tr_think: '0 250 0' * ammo/ammo_max.)
        st.HeadAVelocity = new Vector3(0f, st.AmmoMax > 0f ? 250f * (st.Ammo / st.AmmoMax) : 0f, 0f);

        if (!st.Active) return;   // inactive (team-gated) or dead reactors don't supply power
        if (st.AttackFinished > now) return;
        if (Api.Services is null) return;

        // TFL_SHOOT_HITALLVALID: the sv_turrets.qc turret_think loop calls turret_fire for every valid target,
        // but turret_fire advances attack_finished_single[0] = time + shot_refire after the FIRST recipient, and
        // turret_fusionreactor_firecheck's `attack_finished_single[0] > time` gate then rejects the rest that same
        // think — so Base tops up exactly ONE ally per shot_refire (0.2s), spending one shot_dmg. We match that:
        // find the first eligible ally, recharge it, and stop.
        foreach (Entity ally in Api.Entities.FindInRadius(e.Origin, TargetRange))
        {
            if (ReferenceEquals(ally, e)) continue;
            if (!IsRechargeableAlly(e, ally)) continue;

            // turret_fusionreactor_firecheck: same team, alive, in range (all in IsRechargeableAlly), own ammo
            // covers a top-up, and the recipient isn't already full.
            if (st.Ammo < ShotDamage) break;

            TurretState allyState = TurretAI.State(ally);
            if (allyState.Ammo >= allyState.AmmoMax) continue;

            // tr_attack: enemy.ammo = min(enemy.ammo + shot_dmg, enemy.ammo_max); a te_smallflash at the
            // recipient's bbox centre; then turret_fire spends our own ammo and arms the refire clock.
            allyState.Ammo = System.Math.Min(allyState.Ammo + ShotDamage, allyState.AmmoMax);
            EffectEmitter.TeSmallflash(0.5f * (ally.AbsMin + ally.AbsMax));
            st.Ammo -= ShotDamage;
            st.AttackFinished = now + ShotRefire;
            break;   // Base's per-fire refire reset rejects every later recipient this think (one ally per 0.2s).
        }
    }

    public override bool ValidTarget(Entity self, Entity target)
    {
        if (!IsRechargeableAlly(self, target)) return false;
        return TurretAI.ValidTarget(self, target, Select, TargetRangeMin, TargetRange);
    }

    /// <summary>
    /// A friendly (same-team) turret that carries a rechargeable ENERGY ammo pool — the only thing this reactor
    /// "targets". QC turret_fusionreactor_firecheck additionally requires <c>targ.ammo_flags &amp; TFL_AMMO_ENERGY</c>,
    /// so rocket/bullet turrets are NOT topped up even when same-team and in range.
    /// </summary>
    private static bool IsRechargeableAlly(Entity self, Entity ally)
    {
        if (ally.IsFreed) return false;
        if (!ally.ClassName.StartsWith("turret_", System.StringComparison.Ordinal)) return false;
        if (!TurretAI.SameTeam(self, ally)) return false;
        if (ally.Health <= 0f) return false;
        if (!TurretAI.State(ally).AmmoIsEnergy) return false;   // QC TFL_AMMO_ENERGY recipient gate
        return true;
    }
}
