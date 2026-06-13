using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// FLAC Cannon Turret — port of common/turrets/turret/flac.{qh,qc} (+ flac_weapon.qc). An anti-projectile flak
/// turret: it targets ONLY enemy missiles/projectiles (mortar grenades, electro balls, rockets…) and lobs
/// timed flak shells that air-burst near them to shoot them down. Identity/hitbox from flac.qh; balance from
/// turrets.cfg (<c>g_turrets_unit_flac_*</c>).
///
/// Mechanic: a fast splash projectile (TUR_FLAG_SPLASH | TUR_FLAG_FASTPROJ | TUR_FLAG_MISSILE) that explodes
/// on a short fuse near the predicted intercept (flac_weapon.qc turret_flac_projectile_think_explode). The
/// timed-fuse air-burst nudge toward the target is summarized as an impact-radius blast here.
/// </summary>
[Turret]
public sealed class FlacTurret : Turret
{
    // --- balance (turrets.cfg g_turrets_unit_flac_*) ---
    private const float ShotDamage = 20f;
    private const float ShotRadius = 100f;
    private const float ShotSpeed = 9000f;
    private const float ShotForce = 25f;
    private const float ShotSpread = 0.0125f;
    private const float ShotRefire = 0.1f;
    private const float TargetRange = 4000f;
    private const float TargetRangeMin = 500f;
    private const float TargetRangeOptimal = 2000f;
    private const float AmmoMax = 1000f;
    private const float AmmoRecharge = 100f;
    private const float AimSpeed = 200f;
    private const float AimMaxPitch = 90f;
    private const float AimMaxRot = 360f;
    private const float FireTolerance = 150f;
    // QC flac sets tur_impacttime = 10 default; the fuse is the predicted traveltime + a small jitter.

    // QC flac.qc tr_setup adds NOTURRETS | MISSILESONLY on top of the sv_turrets default (range/team/missiles).
    private const int Select = TurretAI.SelectRangeLimits | TurretAI.SelectTeamCheck
                             | TurretAI.SelectMissiles | TurretAI.SelectMissilesOnly
                             | TurretAI.SelectNoTurrets;

    public FlacTurret()
    {
        NetName = "flac";
        DisplayName = "FLAC Cannon";
        Model = "models/turrets/base.md3";
        StartHealth = 700f;
        Range = TargetRange;
    }

    public override void Spawn(Entity e)
        => TurretSpawn.Init(this, e, new Vector3(-32f, -32f, 0f), new Vector3(32f, 32f, 64f),
            AmmoMax, AmmoRecharge, shotVolly: 0);

    public override void Think(Entity e)
    {
        // No splash AIM (the shell air-bursts itself at the intercept); just lead + shot-time compensate so the
        // fuse meets a fast projectile target. Missile/player biases stay default.
        var p = new TurretParams(Select, TargetRangeMin, TargetRange, ShotDamage, ShotRefire,
            AimSpeed, FireTolerance, lead: true,
            rangeOptimal: TargetRangeOptimal, shotSpeed: ShotSpeed, aimMaxPitch: AimMaxPitch, aimMaxRot: AimMaxRot,
            shotTimeCompensate: true, zPredict: true,
            trackType: TurretAI.TrackFluidInertia);
        TurretAI.RunCombat(e, in p, Attack);
    }

    public override bool ValidTarget(Entity self, Entity target)
        => TurretAI.ValidTarget(self, target, Select, TargetRangeMin, TargetRange);

    // METHOD(FlacAttack, wr_think) — flac_weapon.qc: a flak shell on a timed fuse that air-bursts near the
    // target. RadiusDamage(... shot_dmg, shot_dmg ...) means full damage to the rim (edge == core) vs missiles.
    private void Attack(Entity turret, Entity enemy)
    {
        TurretState st = TurretAI.State(turret);
        Vector3 dir = QMath.Normalize(st.AimPos - st.ShotOrg);
        if (dir == Vector3.Zero) dir = QMath.Forward(TurretAI.HeadWorldAngles(turret));

        Entity shell = TurretSpawn.Projectile(turret, st.ShotOrg, dir, ShotSpeed, size: 5f, health: 0f,
            ShotDamage, edgeDamage: ShotDamage, ShotRadius, ShotForce, DeathTypes.TurretFlac, spread: ShotSpread);

        // Timed fuse: detonate at the predicted intercept (QC nextthink = time + tur_impacttime + jitter). At
        // detonation, if the enemy is within shot_radius*3, snap the burst point onto it + a random offset so
        // the air-burst actually catches the dodging projectile (turret_flac_projectile_think_explode).
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        float impactTime = ShotSpeed > 0f ? st.DistAimPos / ShotSpeed : 0f;
        float jitter = Prandom.Float() * 0.01f - Prandom.Float() * 0.01f;
        shell.NextThink = now + impactTime + jitter;

        var owner = turret;
        shell.Think = self =>
        {
            if (self.Enemy is not null && !self.Enemy.IsFreed
                && (self.Origin - self.Enemy.Origin).Length() < ShotRadius * 3f
                && Api.Services is not null)
            {
                Api.Entities.SetOrigin(self, self.Enemy.Origin + Prandom.Vec() * ShotRadius);
            }
            self.Touch = null;
            self.TakeDamage = DamageMode.No;
            WeaponSplash.RadiusDamage(self, self.Origin, ShotDamage, ShotDamage, ShotRadius, owner,
                0, ShotForce, deathTag: DeathTypes.TurretFlac);
            if (Api.Services is not null) Api.Entities.Remove(self);
            TurretAI.Forget(self);
        };

        if (Api.Services is not null)
            Api.Sound.Play(turret, SoundChannel.Weapon, "weapons/hagar_fire.wav");

        // NOTE (client-render): PROJECTILE_HAGAR trail, EFFECT_BLASTER_MUZZLEFLASH, head frame cycle. The
        // server-side fire (flac_weapon.qc) is done above.
    }
}
