using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// eWheel Turret — port of common/turrets/turret/ewheel.{qh,qc} (+ ewheel_weapon.qc). A fast, MOBILE wheeled
/// turret (TUR_FLAG_MOVE | TUR_FLAG_ROAM) that rolls around and attacks targets in line of sight with rapid
/// blaster bolts from two cannons. Identity/hitbox from ewheel.qh; balance from turrets.cfg
/// (<c>g_turrets_unit_ewheel_*</c>).
///
/// Ported: the very fast blaster projectiles (shot_speed 9000, effectively near-hitscan) in a volley of 2, and
/// the wheeled locomotion — roll toward the enemy easing in at the optimal range (steerlib_arrive), back off
/// when too close, brake when idle, with the body yawing toward the steer direction at the head turnrate
/// (<c>ewheel_move_enemy</c>/<c>ewheel_move_idle</c> + the EWheel tr_think yaw-steer). The map-waypoint path
/// chase (turret_checkpoint graph) and the drive-frame animation are left out (no waypoint graph in this port;
/// frames are client render).
/// </summary>
[Turret]
public sealed class EWheelTurret : Turret
{
    // --- balance (turrets.cfg g_turrets_unit_ewheel_*) ---
    private const float ShotDamage = 30f;
    private const float ShotRadius = 50f;
    private const float ShotSpeed = 9000f;
    private const float ShotForce = 125f;
    private const float ShotSpread = 0.0125f;
    private const float ShotRefire = 0.1f;
    private const int ShotVolly = 2;
    private const float ShotVollyRefire = 1f;
    private const float TargetRange = 5000f;
    private const float TargetRangeMin = 0.1f;
    private const float TargetRangeOptimal = 1500f;
    private const float AmmoMax = 4000f;
    private const float AmmoRecharge = 50f;
    private const float AimSpeed = 90f;
    private const float AimMaxPitch = 20f;
    private const float AimMaxRot = 360f;
    private const float FireTolerance = 150f;

    // --- drive speeds (turrets.cfg g_turrets_unit_ewheel_speed_*) + body turn rate (ewheel_turnrate) ---
    private const float SpeedFast = 700f;
    private const float SpeedSlow = 500f;
    private const float SpeedSlower = 320f;
    private const float SpeedStop = 100f;
    private const float TurnRate = 90f;   // deg/sec body yaw

    // QC ewheel.qc tr_setup: players, range-limited, team-checked, LOS.
    private const int Select = TurretAI.SelectPlayers | TurretAI.SelectRangeLimits
                             | TurretAI.SelectTeamCheck | TurretAI.SelectLos | TurretAI.SelectAngleLimits;

    public EWheelTurret()
    {
        NetName = "ewheel";
        DisplayName = "eWheel Turret";
        Model = "models/turrets/ewheel-base2.md3";
        StartHealth = 200f;
        Range = TargetRange;
    }

    public override void Spawn(Entity e)
    {
        // Mobile creature: SOLID_SLIDEBOX + MOVETYPE_STEP, and damage can shove it (movable).
        TurretSpawn.Init(this, e, new Vector3(-32f, -32f, 0f), new Vector3(32f, 32f, 48f),
            AmmoMax, AmmoRecharge, ShotVolly, respawnTime: 60f, movable: true);
        e.Solid = Solid.SlideBox;
        e.MoveType = MoveType.Step;

        // Home pose + re-arm on respawn (QC tr_setup runs on first spawn and respawn).
        TurretState st = TurretAI.State(e);
        Vector3 home = e.Origin;
        st.OnRespawn = self =>
        {
            self.Velocity = Vector3.Zero;
            self.Enemy = null;
            if (Api.Services is not null) Api.Entities.SetOrigin(self, home);
            self.Solid = Solid.SlideBox;
            self.MoveType = MoveType.Step;
        };
    }

    public override void Think(Entity e)
    {
        var p = new TurretParams(Select, TargetRangeMin, TargetRange, ShotDamage, ShotRefire,
            AimSpeed, FireTolerance, lead: true, ShotVolly, ShotVollyRefire,
            rangeOptimal: TargetRangeOptimal, shotSpeed: ShotSpeed, aimMaxPitch: AimMaxPitch, aimMaxRot: AimMaxRot,
            shotTimeCompensate: true, zPredict: true, trackType: TurretAI.TrackFluidInertia);
        TurretAI.RunCombat(e, in p, Attack);

        Drive(e);
    }

    public override bool ValidTarget(Entity self, Entity target)
        => TurretAI.ValidTarget(self, target, Select, TargetRangeMin, TargetRange);

    // EWheel.tr_think: roll toward / circle the enemy (or brake when idle) and yaw the body toward the steer
    // direction at the head turnrate. (ewheel.qc ewheel_move_enemy / ewheel_move_idle + the yaw-steer block.)
    private void Drive(Entity e)
    {
        if (TurretAI.State(e).Active == false) return;
        float vz = e.Velocity.Z;

        QMath.AngleVectors(new Vector3(0f, e.Angles.Y, 0f), out Vector3 fwd, out _, out _);

        Vector3 steerTo;
        if (e.Enemy is not null)
        {
            float dist = (e.Enemy.Origin - e.Origin).Length();
            steerTo = TurretMath.SteerArrive(e, e.Enemy.Origin, TargetRangeOptimal);

            if (dist > TargetRangeOptimal)
                TurretMath.MoveSimple(e, fwd, SpeedFast, 0.4f);          // close the gap
            else if (dist < TargetRangeOptimal * 0.5f)
                TurretMath.MoveSimple(e, -fwd, SpeedSlow, 0.4f);         // back off (kiting)
            else
                TurretMath.BrakeSimple(e, SpeedStop);                    // hold the optimal range
        }
        else
        {
            steerTo = Vector3.Zero;
            if (e.Velocity != Vector3.Zero)
                TurretMath.BrakeSimple(e, SpeedStop);
        }

        // Yaw the body toward the steer direction, clamped to the turnrate (QC: bound(-f, real_angle.y, f)).
        if (steerTo != Vector3.Zero)
        {
            float frameTime = Api.Services is not null ? Api.Clock.FrameTime : 0f;
            Vector3 wishAngle = QMath.VecToAngles(QMath.Normalize(steerTo));
            float diff = TurretMath.ShortAngle(wishAngle.Y - e.Angles.Y, e.Angles.Y);
            float step = TurnRate * frameTime;
            e.Angles = new Vector3(e.Angles.X, e.Angles.Y + QMath.Bound(-step, diff, step), e.Angles.Z);
        }

        e.Velocity = new Vector3(e.Velocity.X, e.Velocity.Y, vz);
    }

    // METHOD(EWheelAttack, wr_think) — ewheel_weapon.qc: a fast blaster bolt (MIF_SPLASH, near-hitscan) with
    // the shot_spread cone. Alternating cannon tags + drive-frame step are client render.
    private void Attack(Entity turret, Entity enemy)
    {
        TurretState st = TurretAI.State(turret);
        Vector3 dir = QMath.Normalize(st.AimPos - st.ShotOrg);
        if (dir == Vector3.Zero) dir = QMath.Forward(TurretAI.HeadWorldAngles(turret));

        TurretSpawn.Projectile(turret, st.ShotOrg, dir, ShotSpeed, size: 1f, health: 0f,
            ShotDamage, edgeDamage: 0f, ShotRadius, ShotForce, RegistryId, spread: ShotSpread);

        if (Api.Services is not null)
            Api.Sound.Play(turret, SoundChannel.Weapon, "weapons/lasergun_fire.wav");

        // NOTE (client-render): PROJECTILE_BLASTER trail, EFFECT_BLASTER_MUZZLEFLASH, the two-cannon-tag
        // alternation + head frame step. The server-side fire (ewheel_weapon.qc) is done above.
    }
}
