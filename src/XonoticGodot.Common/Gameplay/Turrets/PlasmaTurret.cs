using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Plasma Cannon Turret — port of common/turrets/turret/plasma.{qh,qc} (+ plasma_weapon.qc). Fires slow
/// electric plasma balls that burst on impact for splash damage (like the Electro), aiming for the ground at
/// the target's feet (TFL_AIM_SPLASH). Identity/hitbox from plasma.qh; balance from turrets.cfg
/// (<c>g_turrets_unit_plasma_*</c>).
///
/// Mechanic: a single splash projectile per shot (TUR_FLAG_SPLASH | TUR_FLAG_MEDPROJ) via
/// <see cref="TurretSpawn.Projectile"/>, OR — when mutator_instagib is enabled — an instant instakill
/// railgun beam (plasma.qc tr_attack, 1e10 dmg / 800 force). The muzzle / hit-beam / head-frame effects are client-render.
/// </summary>
[Turret]
public class PlasmaTurret : Turret  // non-sealed: PlasmaDualTurret derives from it (QC CLASS(DualPlasmaTurret, PlasmaTurret))
{
    // --- balance (turrets.cfg g_turrets_unit_plasma_*) ---
    protected const float ShotDamage = 80f;
    protected const float ShotRadius = 150f;
    protected const float ShotSpeed = 2000f;
    protected const float ShotForce = 100f;
    protected const float ShotSpread = 0.015f;  // turrets.cfg g_turrets_unit_plasma_shot_spread (was 0.0125 initparams fallback)
    private const float ShotRefire = 0.6f;
    protected const float TargetRange = 3500f;
    protected const float TargetRangeMin = 200f;
    protected const float TargetRangeOptimal = 500f;
    private const float AmmoMax = 640f;
    private const float AmmoRecharge = 40f;
    private const float AimSpeed = 200f;
    protected const float AimMaxPitch = 30f;
    protected const float AimMaxRot = 360f;
    private const float FireTolerance = 120f;

    // QC sv_turrets default target_select: LOS, team-checked, range + players + angle limits. TUR_FLAG_SPLASH
    // adds TFL_AIM_SPLASH (aim at the feet); the default aim is LEAD | SHOTTIMECOMPENSATE.
    protected const int Select = TurretAI.SelectLos | TurretAI.SelectPlayers | TurretAI.SelectRangeLimits
                               | TurretAI.SelectTeamCheck | TurretAI.SelectAngleLimits;

    // --- target_select biases + track motor tuning (turrets.cfg g_turrets_unit_plasma_*) ---
    // Base tr_setup aim_flags = LEAD | SHOTTIMECOMPENSATE | SPLASH (NO ZPREDICT — see MakeParams).
    protected const float SelectRangeBias = 0.5f;   // target_select_rangebias
    protected const float SelectSameBias = 0.01f;   // target_select_samebias
    protected const float SelectAngleBias = 0.25f;  // target_select_anglebias
    protected const float SelectPlayerBias = 1f;    // target_select_playerbias
    protected const float SelectMissileBias = 0f;   // target_select_missilebias
    protected const float TrackAccelPitch = 0.5f;   // track_accel_pitch
    protected const float TrackAccelRot = 0.7f;     // track_accel_rot
    protected const float TrackBlendRate = 0.2f;    // track_blendrate

    /// <summary>
    /// Build the shared plasma combat params (reused by the dual variant with overridden balance).
    /// The select-bias / track-tuning params default to the plasma cfg values; the dual variant overrides
    /// them (rangebias 0.2 / samebias 0.4 / anglebias 0.4) by passing its own.
    /// </summary>
    protected static TurretParams MakeParams(float rangeMin, float rangeMax, float refire, float aimSpeed,
        float fireTolerance, float rangeBias = SelectRangeBias, float sameBias = SelectSameBias,
        float angleBias = SelectAngleBias, float missileBias = SelectMissileBias, float playerBias = SelectPlayerBias,
        float trackAccelRot = TrackAccelRot, float trackBlendRate = TrackBlendRate)
        // QC plasma tr_setup aim_flags = LEAD | SHOTTIMECOMPENSATE | SPLASH; it does NOT set TFL_AIM_ZPREDICT
        // (zPredict stays false so airborne targets aren't mis-led down their gravity arc).
        => new(Select, rangeMin, rangeMax, ShotDamage, refire, aimSpeed, fireTolerance, lead: true,
            rangeOptimal: TargetRangeOptimal, shotSpeed: ShotSpeed, aimMaxPitch: AimMaxPitch, aimMaxRot: AimMaxRot,
            shotTimeCompensate: true, zPredict: false, aimSplash: true,
            rangeBias: rangeBias, sameBias: sameBias, angleBias: angleBias, missileBias: missileBias,
            playerBias: playerBias,
            trackType: TurretAI.TrackFluidInertia, trackAccelPitch: TrackAccelPitch, trackAccelRot: trackAccelRot,
            trackBlendRate: trackBlendRate);

    public PlasmaTurret()
    {
        NetName = "plasma";
        DisplayName = "Plasma Cannon";
        Model = "models/turrets/base.md3";
        StartHealth = 500f;
        Range = TargetRange;
    }

    public override void Spawn(Entity e)
        => TurretSpawn.Init(this, e, new Vector3(-32f, -32f, 0f), new Vector3(32f, 32f, 64f),
            AmmoMax, AmmoRecharge, shotVolly: 0);

    public override void Think(Entity e)
    {
        var p = MakeParams(TargetRangeMin, TargetRange, ShotRefire, AimSpeed, FireTolerance);
        TurretAI.RunCombat(e, in p, Attack);
    }

    public override bool ValidTarget(Entity self, Entity target)
        => TurretAI.ValidTarget(self, target, Select, TargetRangeMin, TargetRange);

    // METHOD(PlasmaTurret, tr_attack) — plasma.qc: when instagib is on, fire an instant railgun beam
    // (FireRailgunBullet, 1e10 dmg / 800 force — an instakill) instead of the plasma ball; otherwise one
    // splash plasma ball forward (plasma_weapon.qc, MIF_SPLASH) with the deterministic shot_spread cone.
    protected virtual void Attack(Entity turret, Entity enemy)
    {
        TurretState st = TurretAI.State(turret);
        Vector3 dir = QMath.Normalize(st.AimPos - st.ShotOrg);
        if (dir == Vector3.Zero) dir = QMath.Forward(TurretAI.HeadWorldAngles(turret));

        if (InstagibEnabled())
        {
            // QC: FireRailgunBullet(it, ..., 1e10 damage, false, 800 force, ...) — an instant hitscan beam.
            // The instagib rail branch passes NO fire sound (Base FireRailgunBullet snd_fire is empty here).
            TurretCombat.FireBullet(turret, st.ShotOrg, dir, spread: 0f,
                damage: InstagibRailDamage, force: InstagibRailForce, DeathTypes.TurretPlasma);
            // NOTE (client-render): EFFECT_VORTEX_MUZZLEFLASH + the team-coloured EFFECT_VAPORIZER_BEAM hit beam.
        }
        else
        {
            TurretSpawn.Projectile(turret, st.ShotOrg, dir, ShotSpeed, size: 1f, health: 0f,
                ShotDamage, edgeDamage: 0f, ShotRadius, ShotForce, DeathTypes.TurretPlasma, spread: ShotSpread);

            if (Api.Services is not null)
                Api.Sound.Play(turret, SoundChannel.Weapon, "weapons/hagar_fire.wav");
        }

        // NOTE (client-render): PROJECTILE_ELECTRO_BEAM CSQC trail, EFFECT_BLASTER_MUZZLEFLASH, head spin frames.
    }

    /// <summary>QC FireRailgunBullet damage for the instagib plasma turret (plasma.qc tr_attack: 1e10, an
    /// instakill). Base passes 1e10 as the damage arg and 800 as the force arg.</summary>
    protected const float InstagibRailDamage = 10000000000f;  // 1e10

    /// <summary>QC FireRailgunBullet force for the instagib plasma turret (plasma.qc tr_attack: 800).</summary>
    protected const float InstagibRailForce = 800f;

    /// <summary>QC <c>MUTATOR_IS_ENABLED(mutator_instagib)</c> — resolves to <c>g_instagib != 0</c>.</summary>
    private protected static bool InstagibEnabled()
        => Api.Services is not null && Api.Cvars.GetFloat("g_instagib") != 0f;
}
