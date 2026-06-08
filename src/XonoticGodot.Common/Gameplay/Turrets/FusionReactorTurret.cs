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

    public override void Spawn(Entity e)
        => TurretSpawn.Init(this, e, new Vector3(-34f, -34f, 0f), new Vector3(34f, 34f, 90f),
            AmmoMax, AmmoRecharge, shotVolly: 0);

    public override void Think(Entity e)
    {
        TurretState st = TurretAI.State(e);
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        float frameTime = Api.Services is not null ? Api.Clock.FrameTime : 0f;

        if (st.Ammo < st.AmmoMax)
            st.Ammo = System.Math.Min(st.Ammo + st.AmmoRecharge * frameTime, st.AmmoMax);

        if (!st.Active) return;   // inactive (team-gated) or dead reactors don't supply power
        if (st.AttackFinished > now) return;
        if (Api.Services is null) return;

        // TFL_SHOOT_HITALLVALID: recharge every valid friendly turret in range (sv_turrets.qc turret_think loop).
        bool gave = false;
        foreach (Entity ally in Api.Entities.FindInRadius(e.Origin, TargetRange))
        {
            if (ReferenceEquals(ally, e)) continue;
            if (!IsRechargeableAlly(e, ally)) continue;

            // turret_fusionreactor_firecheck: same team, alive, in range, not already full, and own ammo > shot_dmg.
            if (st.Ammo < ShotDamage) break;

            TurretState allyState = TurretAI.State(ally);
            if (allyState.Ammo >= allyState.AmmoMax) continue;

            // tr_attack: enemy.ammo = min(enemy.ammo + shot_dmg, enemy.ammo_max); spend our own ammo.
            allyState.Ammo = System.Math.Min(allyState.Ammo + ShotDamage, allyState.AmmoMax);
            st.Ammo -= ShotDamage;
            gave = true;
        }

        if (gave)
            st.AttackFinished = now + ShotRefire;

        // NOTE — client-render: te_smallflash at each recipient + the head spin (avelocity scaled by ammo
        // fraction). The only non-render extension, the FusionReactor_ValidTarget mutator hook, has no stock
        // mutator targeting it and depends on the mutator-hook system — cross-boundary. The recharge dispatch
        // (fusionreactor.qc) is done above.
    }

    public override bool ValidTarget(Entity self, Entity target)
    {
        if (!IsRechargeableAlly(self, target)) return false;
        return TurretAI.ValidTarget(self, target, Select, TargetRangeMin, TargetRange);
    }

    /// <summary>A friendly (same-team) turret that carries a rechargeable ammo pool — the only thing this reactor "targets".</summary>
    private static bool IsRechargeableAlly(Entity self, Entity ally)
    {
        if (ally.IsFreed) return false;
        if (!ally.ClassName.StartsWith("turret_", System.StringComparison.Ordinal)) return false;
        if (!TurretAI.SameTeam(self, ally)) return false;
        if (ally.Health <= 0f) return false;
        return true;
    }
}
