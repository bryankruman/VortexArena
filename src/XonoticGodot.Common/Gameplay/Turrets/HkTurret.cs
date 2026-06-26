using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Hunter-Killer Turret — port of common/turrets/turret/hk.{qh,qc} (+ hk_weapon.qc). Fires a single, powerful
/// homing rocket that can navigate around obstacles to reach its target (similar to the Devastator). Slow
/// refire (5s) but heavy damage. Can receive targets from external sources (TUR_FLAG_RECIEVETARGETS) and
/// targets vehicles. Identity/hitbox from hk.qh; balance from turrets.cfg (<c>g_turrets_unit_hk_*</c>).
///
/// Mechanic: one heavy splash rocket (TUR_FLAG_SPLASH | TUR_FLAG_MEDPROJ) launched with TFL_AIM_SIMPLE (the
/// rocket guides itself). The obstacle-avoiding guidance + accel/decel/turnrate flight model is fully ported
/// via <see cref="GuidedProjectile"/> (hk_weapon.qc: a 5-ray forward funnel that steers around walls, speeds
/// up in the open and slows near obstacles/sharp turns, with a panic turn and a clear-path sprint).
/// TFL_SHOOT_CLEARTARGET drops the target after firing (one rocket per acquisition). The muzzle te_explosion
/// flash and the PROJECTILE_ROCKET smoke trail (via the networked NetName="rocket" classification) are present;
/// only the external target-reception hook (TUR_FLAG_RECIEVETARGETS), the separate hk.md3 head-bone model, and
/// the hidden player weapon WEP_HK are left out.
/// </summary>
[Turret]
public sealed class HkTurret : Turret
{
    // --- balance (turrets.cfg g_turrets_unit_hk_*) ---
    private const float ShotDamage = 120f;
    private const float ShotRadius = 200f;
    private const float ShotSpeed = 500f;
    private const float ShotSpeedMax = 1000f;
    private const float ShotTurnRate = 0.25f;    // hk_shot_speed_turnrate
    private const float ShotSpeedAccel = 1.025f; // hk_shot_speed_accel
    private const float ShotSpeedAccel2 = 1.05f; // hk_shot_speed_accel2
    private const float ShotSpeedDecel = 0.9f;   // hk_shot_speed_decel
    private const float ShotForce = 600f;
    private const float ShotRefire = 5f;
    private const float TargetRange = 6000f;
    private const float TargetRangeMin = 220f;
    private const float TargetRangeOptimal = 5000f;
    private const float AmmoMax = 240f;
    private const float AmmoRecharge = 16f;
    private const float AimSpeed = 100f;
    private const float AimMaxPitch = 20f;
    private const float AimMaxRot = 360f;
    private const float FireTolerance = 500f;
    private const float RespawnTime = 90f;       // g_turrets_unit_hk_respawntime

    // target-selection scoring biases (g_turrets_unit_hk_target_select_*)
    private const float RangeBias = 0.5f;
    private const float SameBias = 0.01f;
    private const float AngleBias = 0.1f;
    private const float MissileBias = 0f;
    private const float PlayerBias = 1f;

    // head-track motor rates (g_turrets_unit_hk_track_*)
    private const float TrackAccelPitch = 0.25f;
    private const float TrackAccelRot = 0.6f;
    private const float TrackBlendRate = 0.2f;

    // QC hk.qc tr_setup: LOS, vehicles, range-limited, team-checked (+ trigger targets, players via flags).
    private const int Select = TurretAI.SelectLos | TurretAI.SelectPlayers | TurretAI.SelectRangeLimits
                             | TurretAI.SelectTeamCheck | TurretAI.SelectAngleLimits;

    // QC hk.qc tr_setup firecheck_flags = TFL_FIRECHECK_DEAD | TEAMCHECK | REFIRE | AFF. Notably NO AIMDIST /
    // AMMO_OWN / DISTANCES / LOS — the HK only withholds a shot for a dead enemy, a teammate (incl. via the
    // avoid-friendly-fire impact trace), or its refire timer; the rocket's guidance does the rest.
    private const int FireCheck = TurretAI.FireCheckDead | TurretAI.FireCheckTeamCheck
                                | TurretAI.FireCheckRefire | TurretAI.FireCheckAff;

    public HkTurret()
    {
        NetName = "hk";
        DisplayName = "Hunter-Killer Turret";
        Model = "models/turrets/base.md3";
        StartHealth = 500f;
        Range = TargetRange;
    }

    public override void Spawn(Entity e)
        => TurretSpawn.Init(this, e, new Vector3(-32f, -32f, 0f), new Vector3(32f, 32f, 64f),
            AmmoMax, AmmoRecharge, shotVolly: 0, respawnTime: RespawnTime, energyAmmo: false);

    public override void Think(Entity e)
    {
        // QC hk.qc tr_think: the launcher head's load/cycle animation. Once kicked off by a shot (frame != 0),
        // advance the frame each think and wrap 0..5 back to 0. (Base drives a separate tur_head entity's
        // frame; the port has no head bone entity so the cycle runs on the turret edict's own frame.)
        if (e.Frame != 0f)
            e.Frame += 1f;
        if (e.Frame > 5f)
            e.Frame = 0f;

        // TFL_AIM_SIMPLE -> aim at the current pos (the rocket guides itself). TFL_SHOOT_CLEARTARGET drops the
        // target after firing so it re-acquires for the next rocket (one heavy rocket per acquisition).
        var p = new TurretParams(Select, TargetRangeMin, TargetRange, ShotDamage, ShotRefire,
            AimSpeed, FireTolerance, lead: false,
            rangeOptimal: TargetRangeOptimal, shotSpeed: ShotSpeed, aimMaxPitch: AimMaxPitch, aimMaxRot: AimMaxRot,
            aimSimple: true, clearTarget: true,
            rangeBias: RangeBias, sameBias: SameBias, angleBias: AngleBias, missileBias: MissileBias, playerBias: PlayerBias,
            trackType: TurretAI.TrackFluidInertia,
            trackAccelPitch: TrackAccelPitch, trackAccelRot: TrackAccelRot, trackBlendRate: TrackBlendRate,
            fireCheckFlags: FireCheck);
        TurretAI.RunCombat(e, in p, Attack);
    }

    public override bool ValidTarget(Entity self, Entity target)
        => TurretAI.ValidTarget(self, target, Select, TargetRangeMin, TargetRange);

    // turret_hk_missile_think (hk_weapon.qc): one heavy obstacle-avoiding guided rocket — it funnel-traces to
    // steer around walls, accels in the open and decels near obstacles, and homes onto the target.
    private void Attack(Entity turret, Entity enemy)
    {
        TurretState st = TurretAI.State(turret);
        Vector3 dir = QMath.Normalize(st.AimPos - st.ShotOrg);
        if (dir == Vector3.Zero) dir = QMath.Forward(TurretAI.HeadWorldAngles(turret));

        // QC launches at shot_speed * 0.75; the guidance brings it up toward shot_speed_max.
        Entity missile = GuidedProjectile.Launch(turret, enemy, st.ShotOrg, dir, GuidedProjectile.Mode.Hk,
            launchSpeed: ShotSpeed * 0.75f, speedMax: ShotSpeedMax, speedGain: 1f, turnRate: ShotTurnRate,
            size: 6f, health: 10f, ShotDamage, ShotRadius, ShotForce, DeathTypes.TurretHk, ttl: 30f,
            accel: ShotSpeedAccel, accel2: ShotSpeedAccel2, decel: ShotSpeedDecel, fuelTime: 30f);

        if (Api.Services is not null)
        {
            // hk_weapon.qc:30: te_explosion(missile.origin) — the launch puff at the muzzle (the missile's spawn
            // origin = st.ShotOrg). A networked temp-entity emitted server-side so all viewers see it identically
            // (same convention HellionTurret.Attack / WalkerTurret.FireRocket use for their te_explosion flash).
            EffectEmitter.TeExplosion(missile.Origin);
            Api.Sound.Play(turret, SoundChannel.Weapon, "weapons/rocket_fire.wav");
        }

        // QC hk.qc tr_think drives the head-frame cycler, kicked off here: the non-player (turret) fire branch
        // does `if (tur_head.frame == 0) ++tur_head.frame` (hk_weapon.qc:40-42) to start the load animation.
        if (turret.Frame == 0f)
            turret.Frame += 1f;

        // NOTE — cross-boundary: turret_hk_addtarget external target reception (TUR_FLAG_RECIEVETARGETS) needs the
        // cross-turret target-broadcast system, which isn't modeled yet. The muzzle te_explosion (above) and the
        // PROJECTILE_ROCKET trail (the missile is stamped NetName="rocket" in GuidedProjectile.Launch so the client
        // gives it the rocket smoke trail) are now faithful; the server-side guided-rocket fire is done above.
    }
}
