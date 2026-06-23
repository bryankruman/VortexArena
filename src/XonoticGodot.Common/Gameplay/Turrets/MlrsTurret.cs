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
/// TFL_SHOOT_VOLLYALWAYS (a started burst always completes even if the target is lost — modeled by the
/// mid-burst branch in <see cref="Think"/>). The head ammo-gauge frame animation is cosmetic and deferred.
/// </summary>
[Turret]
public sealed class MlrsTurret : Turret
{
    // --- balance (turrets.cfg g_turrets_unit_mlrs_*) ---
    private const float ShotDamage = 50f;
    private const float ShotRadius = 125f;
    private const float ShotSpeed = 2000f;
    private const float ShotForce = 25f;
    private const float ShotSpread = 0.05f;
    private const float ShotRefire = 0.1f;
    private const int ShotVolly = 6;
    private const float ShotVollyRefire = 4f;
    private const float TargetRange = 3000f;
    private const float TargetRangeMin = 500f;   // won't fire on close targets
    private const float TargetRangeOptimal = 500f;
    private const float AmmoMax = 300f;
    private const float AmmoRecharge = 75f;
    private const float AimSpeed = 100f;
    private const float AimMaxPitch = 20f;
    private const float AimMaxRot = 360f;
    private const float FireTolerance = 120f;

    // target-select biases (turrets.cfg g_turrets_unit_mlrs_target_select_*bias)
    private const float RangeBias = 0.25f;
    private const float SameBias = 0.5f;
    private const float AngleBias = 0.5f;
    private const float MissileBias = 0f;
    private const float PlayerBias = 1f;

    // head track motor inertia (turrets.cfg g_turrets_unit_mlrs_track_*)
    private const float TrackAccelPitch = 0.5f;
    private const float TrackAccelRot = 0.7f;
    private const float TrackBlendRate = 0.2f;

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
        // QC mlrs aim_flags = TFL_AIM_LEAD | TFL_AIM_SHOTTIMECOMPENSATE (TFL_AIM_SPLASH is auto-added by the
        // framework for the TUR_FLAG_SPLASH unit flag, so aimSplash stays on; there is no TFL_AIM_ZPREDICT).
        // Splash aim lands the unguided rockets around the target; the full mlrs scoring biases + inertia track
        // params (turrets.cfg) are passed so target selection + head slew match Base.
        var p = new TurretParams(Select, TargetRangeMin, TargetRange, ShotDamage, ShotRefire,
            AimSpeed, FireTolerance, lead: true, ShotVolly, ShotVollyRefire,
            rangeOptimal: TargetRangeOptimal, shotSpeed: ShotSpeed, aimMaxPitch: AimMaxPitch, aimMaxRot: AimMaxRot,
            shotTimeCompensate: true, zPredict: false, aimSplash: true,
            rangeBias: RangeBias, sameBias: SameBias, angleBias: AngleBias,
            missileBias: MissileBias, playerBias: PlayerBias,
            trackType: TurretAI.TrackFluidInertia, trackAccelPitch: TrackAccelPitch,
            trackAccelRot: TrackAccelRot, trackBlendRate: TrackBlendRate);

        // TFL_SHOOT_VOLLYALWAYS: once a 6-rocket burst has started it must complete even if the target is lost.
        // RunCombat bails the instant Enemy is null (turret_think enemy-null branch), so guard that here first:
        // QC turret_think:1059 checks (volly_counter != shot_volly) BEFORE the enemy bail and turret_firecheck:889
        // early-returns true mid-burst. We finish the in-flight burst aiming at the last solution, then defer to
        // RunCombat once the counter is back to a full volley.
        TurretState st = TurretAI.State(e);
        if (st.Active && e.Enemy is null && st.VollyCounter != ShotVolly)
        {
            float now = Api.Services is not null ? Api.Clock.Time : 0f;
            st.ShotOrg = TurretAI.ShotOrigin(e);
            if (st.Ammo < st.AmmoMax)
                st.Ammo = System.Math.Min(st.Ammo + st.AmmoRecharge * (Api.Services is not null ? Api.Clock.FrameTime : 0f), st.AmmoMax);

            // Keep aiming/tracking the last firing solution (st.AimPos persists from the last enemy think).
            TurretAI.Track(e, in p);
            st.DistAimPos = (st.ShotOrg - st.AimPos).Length();

            // Fire to keep the burst going (turret_firecheck mid-burst path skips range/LOS re-checks; only the
            // cooldown + ammo gates remain). TurretAI.Fire advances the counter and applies the long volley refire
            // when the burst ends, exactly as the normal path does.
            if (st.AttackFinished <= now && st.Ammo >= p.ShotDamage)
                TurretAI.Fire(e, e, in p, Attack);
            return;
        }

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
        // For an AI turret the floor uses the per-shot shot_radius (125); the shot_radius=500 override is set
        // ONLY on the isPlayer fire branch (mlrs_weapon.qc:19), which does not apply here.
        float impactTime = ShotSpeed > 0f ? st.DistAimPos / ShotSpeed : 0f;
        float fuse = System.Math.Max(impactTime, ShotRadius * 2f / ShotSpeed);
        rocket.NextThink = (Api.Services is not null ? Api.Clock.Time : 0f) + fuse;

        if (Api.Services is not null)
            Api.Sound.Play(turret, SoundChannel.Weapon, "weapons/rocket_fire.wav");

        // NOTE (client-render): the head ammo-gauge frame (0 full..6 empty) + PROJECTILE_ROCKET CSQC trail +
        // muzzle flash. The server-side fire (mlrs.qc) is done above.
    }
}
