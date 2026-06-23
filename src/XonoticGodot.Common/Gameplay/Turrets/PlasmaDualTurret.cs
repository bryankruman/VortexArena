using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Dual Plasma Cannon Turret — port of common/turrets/turret/plasma_dual.{qh,qc}. The stronger sibling of the
/// <see cref="PlasmaTurret"/> (QC <c>CLASS(DualPlasmaTurret, PlasmaTurret)</c>): same electric-ball splash
/// projectile and damage, but two cannons firing at nearly double the rate (refire 0.35 vs 0.6). Identity from
/// plasma_dual.qh; balance from turrets.cfg (<c>g_turrets_unit_plasma_dual_*</c>). Inherits the plasma weapon
/// (<c>SUPER(PlasmaTurret).tr_attack</c>) but overrides the per-unit tunables that diverge from single plasma.
/// </summary>
[Turret]
public sealed class PlasmaDualTurret : PlasmaTurret
{
    // --- balance overrides (turrets.cfg g_turrets_unit_plasma_dual_*, lines 359-394) ---
    // Same shot_dmg/radius/speed/force as single Plasma (reuse the base consts); these per-unit values differ:
    private const float DualRefire = 0.35f;           // shot_refire
    private const float DualShotSpread = 0.015f;      // shot_spread (single plasma uses 0.0125)
    private const float DualTargetRange = 3000f;      // target_range
    private const float DualTargetRangeMin = 80f;     // target_range_min
    private const float DualTargetRangeOptimal = 1000f; // target_range_optimal (single plasma's is 500)
    private const float DualAmmoMax = 640f;           // ammo_max
    private const float DualAmmoRecharge = 40f;       // ammo_recharge
    private const float DualAimSpeed = 100f;          // aim_speed
    private const float DualFireTolerance = 200f;     // aim_firetolerance_dist

    // target_select_*bias (single plasma leaves these at the 1/1/1/1/1 ctor defaults; dual sets them explicitly).
    private const float DualRangeBias = 0.2f;
    private const float DualSameBias = 0.4f;
    private const float DualAngleBias = 0.4f;
    private const float DualPlayerBias = 1f;
    private const float DualMissileBias = 0f;

    // track motor (track_type 3 = FLUIDINERTIA). Single plasma leaves accel_rot/blendrate at ctor defaults;
    // dual overrides accel_rot (0.7 vs default 0.5) and blendrate (0.2 vs default 0.35). accel_pitch matches.
    private const float DualTrackAccelPitch = 0.5f;
    private const float DualTrackAccelRot = 0.7f;
    private const float DualTrackBlendRate = 0.2f;

    public PlasmaDualTurret()
    {
        NetName = "plasma_dual";
        DisplayName = "Dual Plasma Cannon";
        Model = "models/turrets/base.md3";
        StartHealth = 500f;
        Range = DualTargetRange;
    }

    public override void Spawn(Entity e)
        => TurretSpawn.Init(this, e, new Vector3(-32f, -32f, 0f), new Vector3(32f, 32f, 64f),
            DualAmmoMax, DualAmmoRecharge, shotVolly: 0);

    public override void Think(Entity e)
    {
        // Build the dual params directly (rather than the shared single-plasma MakeParams) so the per-unit
        // overrides above are honored: target_range_optimal 1000, the scoring biases, and the FLUIDINERTIA
        // track accel/blend — all of which single plasma leaves at defaults that diverge from plasma_dual.cfg.
        var p = new TurretParams(Select, DualTargetRangeMin, DualTargetRange, ShotDamage, DualRefire,
            DualAimSpeed, DualFireTolerance, lead: true,
            rangeOptimal: DualTargetRangeOptimal, shotSpeed: ShotSpeed,
            aimMaxPitch: AimMaxPitch, aimMaxRot: AimMaxRot,
            // QC plasma tr_setup aim_flags = LEAD | SHOTTIMECOMPENSATE | SPLASH; it does NOT set TFL_AIM_ZPREDICT,
            // and DualPlasmaTurret has no tr_setup override so it inherits those flags (zPredict stays false —
            // matches the single plasma's MakeParams; airborne targets aren't mis-led down a gravity arc).
            shotTimeCompensate: true, zPredict: false, aimSplash: true,
            rangeBias: DualRangeBias, sameBias: DualSameBias, angleBias: DualAngleBias,
            missileBias: DualMissileBias, playerBias: DualPlayerBias,
            trackType: TurretAI.TrackFluidInertia,
            trackAccelPitch: DualTrackAccelPitch, trackAccelRot: DualTrackAccelRot,
            trackBlendRate: DualTrackBlendRate);
        TurretAI.RunCombat(e, in p, Attack);
    }

    public override bool ValidTarget(Entity self, Entity target)
        => TurretAI.ValidTarget(self, target, Select, DualTargetRangeMin, DualTargetRange);

    // METHOD(DualPlasmaTurret, tr_attack) — plasma_dual.qc. The instagib branch is identical to single plasma
    // (FireRailgunBullet 800 dmg); the plasma-ball branch is the same SUPER(PlasmaTurret).tr_attack but fires
    // with the dual unit's wider shot_spread (0.015 vs single 0.0125). Re-emit it here with the dual spread so
    // the cone matches plasma_dual.cfg, then advance the head wheel (++tur_head.frame), as the QC does.
    protected override void Attack(Entity turret, Entity enemy)
    {
        TurretState st = TurretAI.State(turret);
        Vector3 dir = QMath.Normalize(st.AimPos - st.ShotOrg);
        if (dir == Vector3.Zero) dir = QMath.Forward(TurretAI.HeadWorldAngles(turret));

        if (InstagibEnabled())
        {
            // SUPER(PlasmaTurret).tr_attack instagib path: an instant 800-damage railgun beam (force/spread 0).
            base.Attack(turret, enemy);
        }
        else
        {
            // Plasma ball with the dual unit's shot_spread (0.015). Same damage/radius/speed/force as single.
            TurretSpawn.Projectile(turret, st.ShotOrg, dir, ShotSpeed, size: 1f, health: 0f,
                ShotDamage, edgeDamage: 0f, ShotRadius, ShotForce, DeathTypes.TurretPlasma, spread: DualShotSpread);

            if (Api.Services is not null)
                Api.Sound.Play(turret, SoundChannel.Weapon, "weapons/hagar_fire.wav");
        }

        // NOTE (client-render, presentation): plasma_dual.qc does `++it.tur_head.frame` here and cycles the head
        // wheel (0..6, idling at 0 and 3) in tr_think; the muzzle/beam/two-barrel-tag effects are all CSQC. The
        // server-side head bone (tur_head.frame) is not modeled in the port, so the wheel anim is cross-file work.
    }
}
