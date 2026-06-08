using System.Numerics;
using XonoticGodot.Common.Framework;
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
/// <see cref="TurretSpawn.Projectile"/>, OR — when mutator_instagib is enabled — an instant 800-damage
/// railgun beam (plasma.qc tr_attack). The muzzle / hit-beam / head-frame effects are client-render.
/// </summary>
[Turret]
public class PlasmaTurret : Turret  // non-sealed: PlasmaDualTurret derives from it (QC CLASS(DualPlasmaTurret, PlasmaTurret))
{
    // --- balance (turrets.cfg g_turrets_unit_plasma_*) ---
    protected const float ShotDamage = 80f;
    protected const float ShotRadius = 150f;
    protected const float ShotSpeed = 2000f;
    protected const float ShotForce = 100f;
    protected const float ShotSpread = 0.0125f;
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

    /// <summary>Build the shared plasma combat params (reused by the dual variant with overridden balance).</summary>
    protected static TurretParams MakeParams(float rangeMin, float rangeMax, float refire, float aimSpeed,
        float fireTolerance)
        => new(Select, rangeMin, rangeMax, ShotDamage, refire, aimSpeed, fireTolerance, lead: true,
            rangeOptimal: TargetRangeOptimal, shotSpeed: ShotSpeed, aimMaxPitch: AimMaxPitch, aimMaxRot: AimMaxRot,
            shotTimeCompensate: true, zPredict: true, aimSplash: true,
            trackType: TurretAI.TrackFluidInertia);

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
    // (FireRailgunBullet, 800 dmg) instead of the plasma ball; otherwise one splash plasma ball forward
    // (plasma_weapon.qc, MIF_SPLASH) with the deterministic shot_spread cone.
    protected virtual void Attack(Entity turret, Entity enemy)
    {
        TurretState st = TurretAI.State(turret);
        Vector3 dir = QMath.Normalize(st.AimPos - st.ShotOrg);
        if (dir == Vector3.Zero) dir = QMath.Forward(TurretAI.HeadWorldAngles(turret));

        if (InstagibEnabled())
        {
            // QC: FireRailgunBullet(it, ..., 800 damage, 0 force, no spread) — an instant hitscan beam.
            TurretCombat.FireBullet(turret, st.ShotOrg, dir, spread: 0f,
                damage: InstagibRailDamage, force: 0f, RegistryId);
            if (Api.Services is not null)
                Api.Sound.Play(turret, SoundChannel.Weapon, "weapons/electro_fire.wav");
            // NOTE (client-render): EFFECT_VORTEX_MUZZLEFLASH + the team-coloured EFFECT_VAPORIZER_BEAM hit beam.
        }
        else
        {
            TurretSpawn.Projectile(turret, st.ShotOrg, dir, ShotSpeed, size: 1f, health: 0f,
                ShotDamage, edgeDamage: 0f, ShotRadius, ShotForce, RegistryId, spread: ShotSpread);

            if (Api.Services is not null)
                Api.Sound.Play(turret, SoundChannel.Weapon, "weapons/hagar_fire.wav");
        }

        // NOTE (client-render): PROJECTILE_ELECTRO_BEAM CSQC trail, EFFECT_BLASTER_MUZZLEFLASH, head spin frames.
    }

    /// <summary>QC FireRailgunBullet damage for the instagib plasma turret (plasma.qc tr_attack: 800).</summary>
    protected const float InstagibRailDamage = 800f;

    /// <summary>QC <c>MUTATOR_IS_ENABLED(mutator_instagib)</c> — resolves to <c>g_instagib != 0</c>.</summary>
    private protected static bool InstagibEnabled()
        => Api.Services is not null && Api.Cvars.GetFloat("g_instagib") != 0f;
}
