using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
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
/// the wheeled locomotion — roll toward the enemy easing in at the optimal range (steerlib_arrive) with the
/// chase speed gated by the body yaw error (fast/slow/slower = Base tur_head.spawnshieldtime &lt;1/&lt;2/else),
/// back off when too close, brake when idle, with the body yawing toward the steer direction at the turnrate
/// (<c>ewheel_move_enemy</c>/<c>ewheel_move_idle</c> + the EWheel tr_think yaw-steer). The map-waypoint path
/// chase (turret_checkpoint graph), the drive-frame animation (frames 0..4) and the CSQC rolling draw + low-HP
/// sparks are left out (no waypoint graph in this port; frames/draw are client render).
/// </summary>
[Turret]
public sealed class EWheelTurret : Turret
{
    // --- balance (turrets.cfg g_turrets_unit_ewheel_*) ---
    private const float ShotDamage = 30f;
    private const float ShotRadius = 50f;
    private const float ShotSpeed = 9000f;
    private const float ShotForce = 125f;
    private const float ShotSpread = 0.025f;
    private const float ShotRefire = 0.1f;
    private const int ShotVolly = 2;
    private const float ShotVollyRefire = 1f;
    private const float TargetRange = 5000f;
    private const float TargetRangeMin = 0.1f;
    private const float TargetRangeOptimal = 900f;
    private const float AmmoMax = 4000f;
    private const float AmmoRecharge = 50f;
    private const float AimSpeed = 90f;
    private const float AimMaxPitch = 45f;
    private const float AimMaxRot = 20f;
    private const float FireTolerance = 150f;
    private const float RespawnTime = 30f;

    // --- drive speeds (turrets.cfg g_turrets_unit_ewheel_speed_*) + body turn rate (ewheel_turnrate) ---
    private const float SpeedFast = 500f;
    private const float SpeedSlow = 150f;
    private const float SpeedSlower = 50f;
    private const float SpeedStop = 25f;
    private const float TurnRate = 200f;   // deg/sec body yaw (tur_head.aim_speed in tr_think)

    // QC ewheel.qc tr_setup: players, range-limited, team-checked, LOS (no ANGLELIMITS — it turns the body to face).
    private const int Select = TurretAI.SelectPlayers | TurretAI.SelectRangeLimits
                             | TurretAI.SelectTeamCheck | TurretAI.SelectLos;

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
            AmmoMax, AmmoRecharge, ShotVolly, respawnTime: RespawnTime, movable: true);
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
            shotTimeCompensate: true, zPredict: true, trackType: TurretAI.TrackStepMotor);
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

        // QC tr_think: anglemods the body angles, then steer the body yaw toward the steer direction clamped to
        // the turnrate. The body yaw ERROR (fabs of the short-way yaw delta) gates the chase speed below — this
        // is QC's tur_head.spawnshieldtime, computed here BEFORE the yaw step so a well-aligned wheel goes fast.
        e.Angles = new Vector3(TurretMath.AngleMods(e.Angles.X), TurretMath.AngleMods(e.Angles.Y), e.Angles.Z);

        // steerto for this frame (mirrors ewheel_move_enemy: steerlib_arrive toward the enemy at optimal range).
        Vector3 steerTo = e.Enemy is not null
            ? TurretMath.SteerArrive(e, e.Enemy.Origin, TargetRangeOptimal)
            : Vector3.Zero;

        float yawError = 0f;
        if (steerTo != Vector3.Zero)
        {
            float frameTime = Api.Services is not null ? Api.Clock.FrameTime : 0f;
            Vector3 wishAngle = QMath.VecToAngles(QMath.Normalize(steerTo));
            // real_angle = wish_angle - angles, then shortangle_vxy toward the head angles (here the body's own).
            float diff = TurretMath.ShortAngle(wishAngle.Y - e.Angles.Y, e.Angles.Y);
            yawError = MathF.Abs(diff);                                   // tur_head.spawnshieldtime
            float step = TurnRate * frameTime;                           // f = aim_speed * frametime
            e.Angles = new Vector3(e.Angles.X, e.Angles.Y + QMath.Bound(-step, diff, step), e.Angles.Z);
        }

        QMath.AngleVectors(new Vector3(0f, e.Angles.Y, 0f), out Vector3 fwd, out _, out _);

        if (e.Enemy is not null)
        {
            // ewheel_move_enemy: speed gated by the body yaw error (well-aligned -> fast, off-axis -> slow/slower).
            float dist = (e.Enemy.Origin - e.Origin).Length();
            if (dist > TargetRangeOptimal)
            {
                if (yawError < 1f)
                    TurretMath.MoveSimple(e, fwd, SpeedFast, 0.4f);
                else if (yawError < 2f)
                    TurretMath.MoveSimple(e, fwd, SpeedSlow, 0.4f);
                else
                    TurretMath.MoveSimple(e, fwd, SpeedSlower, 0.4f);
            }
            else if (dist < TargetRangeOptimal * 0.5f)
            {
                TurretMath.MoveSimple(e, -fwd, SpeedSlow, 0.4f);         // back off (kiting)
            }
            else
            {
                TurretMath.BrakeSimple(e, SpeedStop);                    // hold the optimal range
            }
        }
        else
        {
            // ewheel_move_idle: brake to a stop (no waypoint path graph in this port).
            if (e.Velocity != Vector3.Zero)
                TurretMath.BrakeSimple(e, SpeedStop);
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
            ShotDamage, edgeDamage: 0f, ShotRadius, ShotForce, DeathTypes.TurretEwheel, spread: ShotSpread);

        if (Api.Services is not null)
            Api.Sound.Play(turret, SoundChannel.Weapon, "weapons/lasergun_fire.wav");

        // NOTE (client-render): PROJECTILE_BLASTER trail, EFFECT_BLASTER_MUZZLEFLASH, the two-cannon-tag
        // alternation + head frame step. The server-side fire (ewheel_weapon.qc) is done above.
    }
}
