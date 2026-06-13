using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// MLRS Turret — port of common/turrets/turret/mlrs.{qh,qc}. Fires a rapid burst of 6 unguided rockets
/// (similar to the Devastator) at its target, then a long reload. Won't fire if the target is too close (so it
/// doesn't splash itself). Identity/hitbox from mlrs.qh; balance from turrets.cfg
/// (<c>g_turrets_unit_mlrs_*</c>).
///
/// Mechanic: splash projectiles (TUR_FLAG_SPLASH | TUR_FLAG_MEDPROJ) via the generic
/// <see cref="TurretSpawn.Projectile"/> (QC uses the base <c>tr_attack</c>). Volley of 6, 4s volley refire,
/// VOLLYALWAYS (completes a started burst). The head ammo-gauge frame animation is cosmetic and deferred.
/// </summary>
[Turret]
public sealed class MlrsTurret : Turret
{
    // --- balance (turrets.cfg g_turrets_unit_mlrs_*) ---
    private const float ShotDamage = 50f;
    private const float ShotRadius = 125f;
    private const float ShotSpeed = 2000f;
    private const float ShotForce = 25f;
    private const float ShotSpread = 0.0125f;
    private const float ShotRefire = 0.1f;
    private const int ShotVolly = 6;
    private const float ShotVollyRefire = 4f;
    private const float TargetRange = 3000f;
    private const float TargetRangeMin = 500f;   // won't fire on close targets
    private const float TargetRangeOptimal = 1500f;
    private const float AmmoMax = 300f;
    private const float AmmoRecharge = 75f;
    private const float AimSpeed = 100f;
    private const float AimMaxPitch = 30f;
    private const float AimMaxRot = 360f;
    private const float FireTolerance = 120f;
    // QC mlrs sets shot_radius = 500 at fire time so the splash AIM predicts onto a wider footprint.
    private const float AimSplashRadius = 500f;

    private const int Select = TurretAI.SelectLos | TurretAI.SelectPlayers | TurretAI.SelectRangeLimits
                             | TurretAI.SelectTeamCheck | TurretAI.SelectAngleLimits;

    public MlrsTurret()
    {
        NetName = "mlrs";
        DisplayName = "MLRS Turret";
        Model = "models/turrets/base.md3";
        StartHealth = 500f;
        Range = TargetRange;
    }

    public override void Spawn(Entity e)
        => TurretSpawn.Init(this, e, new Vector3(-32f, -32f, 0f), new Vector3(32f, 32f, 64f),
            AmmoMax, AmmoRecharge, ShotVolly);

    public override void Think(Entity e)
    {
        // VOLLYALWAYS: a started burst always completes (handled by the volley counter). Splash aim onto a
        // 500u footprint (QC mlrs shot_radius override) so the unguided rockets land around the target.
        var p = new TurretParams(Select, TargetRangeMin, TargetRange, ShotDamage, ShotRefire,
            AimSpeed, FireTolerance, lead: true, ShotVolly, ShotVollyRefire,
            rangeOptimal: TargetRangeOptimal, shotSpeed: ShotSpeed, aimMaxPitch: AimMaxPitch, aimMaxRot: AimMaxRot,
            shotTimeCompensate: true, zPredict: true, aimSplash: true,
            trackType: TurretAI.TrackFluidInertia);
        TurretAI.RunCombat(e, in p, Attack);
    }

    public override bool ValidTarget(Entity self, Entity target)
        => TurretAI.ValidTarget(self, target, Select, TargetRangeMin, TargetRange);

    // MLRSTurretAttack.wr_think (base tr_attack) — an unguided splash rocket with the shot_spread cone and a
    // travel-time fuse so it self-destructs near the predicted impact (QC max(tur_impacttime, ...)).
    private void Attack(Entity turret, Entity enemy)
    {
        TurretState st = TurretAI.State(turret);
        Vector3 dir = QMath.Normalize(st.AimPos - st.ShotOrg);
        if (dir == Vector3.Zero) dir = QMath.Forward(TurretAI.HeadWorldAngles(turret));

        Entity rocket = TurretSpawn.Projectile(turret, st.ShotOrg, dir, ShotSpeed, size: 6f, health: 10f,
            ShotDamage, edgeDamage: 0f, ShotRadius, ShotForce, DeathTypes.TurretMlrs, spread: ShotSpread);

        // QC: nextthink = time + max(tur_impacttime, (shot_radius*2)/shot_speed) — detonate near the target.
        float impactTime = ShotSpeed > 0f ? st.DistAimPos / ShotSpeed : 0f;
        float fuse = System.Math.Max(impactTime, AimSplashRadius * 2f / ShotSpeed);
        rocket.NextThink = (Api.Services is not null ? Api.Clock.Time : 0f) + fuse;

        if (Api.Services is not null)
            Api.Sound.Play(turret, SoundChannel.Weapon, "weapons/rocket_fire.wav");

        // NOTE (client-render): the head ammo-gauge frame (0 full..6 empty) + PROJECTILE_ROCKET CSQC trail +
        // muzzle flash. The server-side fire (mlrs.qc) is done above.
    }
}
