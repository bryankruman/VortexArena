using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Walker Turret — port of common/turrets/turret/walker.{qh,qc} (+ walker_weapon.qc). A legged, MOBILE turret
/// (TUR_FLAG_MOVE) that hunts targets: it walks/runs toward them, fires a hitscan minigun, lobs homing rockets
/// at medium range, and melees up close. Identity/hitbox from walker.qh; balance from turrets.cfg
/// (<c>g_turrets_unit_walker_*</c>).
///
/// Ported: the full combat loop (acquire a player in LOS/range, aim, fire the minigun with spread + force), the
/// legged locomotion (move toward the enemy — running when far, walking when near — easing in via
/// steerlib_attract2, yawing the body toward the heading at the per-gait turnrate, braking when idle), the
/// guided ROCKET VOLLY at rocket range (4 homing rockets 0.2s apart then a 10s reload, each steered by
/// <see cref="GuidedProjectile.WalkerRocketThink"/>), and the MELEE swipe (a 100-dmg radius hit in front when
/// the enemy is within melee range). The 12-state animation machine, swim/jump gaits and map-waypoint roaming
/// are client/render + map-graph concerns left out.
/// </summary>
[Turret]
public sealed class WalkerTurret : Turret
{
    // --- minigun balance (turrets.cfg g_turrets_unit_walker_*) ---
    private const float ShotDamage = 5f;
    private const float ShotForce = 10f;
    private const float ShotSpread = 0.025f;
    private const float ShotSpeed = 34920f;       // near-hitscan minigun
    private const float ShotRefire = 0.05f;
    private const int ShotVolly = 10;
    private const float ShotVollyRefire = 1f;
    private const float TargetRange = 5000f;
    private const float TargetRangeMin = 0f;
    private const float TargetRangeOptimal = 100f;
    private const float AmmoMax = 4000f;
    private const float AmmoRecharge = 100f;
    private const float AimSpeed = 45f;
    private const float AimMaxPitch = 15f;
    private const float AimMaxRot = 90f;
    private const float FireTolerance = 100f;

    // --- locomotion (turrets.cfg) ---
    private const float SpeedRun = 300f, SpeedWalk = 200f, SpeedStop = 90f;
    private const float TurnWalk = 15f, TurnRun = 7f;

    // --- rocket volley (turrets.cfg g_turrets_unit_walker_rocket_*) ---
    private const float RocketDamage = 45f;
    private const float RocketRadius = 150f;
    private const float RocketForce = 150f;
    private const float RocketSpeed = 1000f;
    private const float RocketTurnRate = 0.05f;
    private const float RocketRange = 4000f;
    private const float RocketRangeMin = 500f;
    private const float RocketRefire = 10f;

    // --- melee (turrets.cfg g_turrets_unit_walker_melee_*) ---
    private const float MeleeDamage = 100f;
    private const float MeleeForce = 600f;
    private const float MeleeRange = 100f;

    /// <summary>Walker-specific scratch (QC tur_head.shot_volly rocket counter + the separate rocket refire clock + melee lock).</summary>
    private sealed class WalkerState
    {
        public int RocketVolly;       // QC tur_head.shot_volly: rockets left in the current burst
        public float RocketFinished;  // QC tur_head.attack_finished_single[0]: next rocket/burst time
        public float MeleeUntil;      // sim time the melee swing is locked (no movement/fire mid-swipe)
    }
    private static readonly Dictionary<Entity, WalkerState> _w = new();
    private static WalkerState W(Entity e) { if (!_w.TryGetValue(e, out var s)) { s = new(); _w[e] = s; } return s; }

    // QC walker.qc tr_setup: players, range-limited, team-checked, LOS, angle-limited. Hitscan minigun, lead aim.
    private const int Select = TurretAI.SelectPlayers | TurretAI.SelectRangeLimits
                             | TurretAI.SelectTeamCheck | TurretAI.SelectLos | TurretAI.SelectAngleLimits;

    public WalkerTurret()
    {
        NetName = "walker";
        DisplayName = "Walker Turret";
        Model = "models/turrets/walker_body.md3";
        StartHealth = 500f;
        Range = TargetRange;
    }

    public override void Spawn(Entity e)
    {
        // Mobile, creature-like: SOLID_SLIDEBOX + MOVETYPE_STEP; damage shoves it.
        TurretSpawn.Init(this, e, new Vector3(-70f, -70f, 0f), new Vector3(70f, 70f, 95f),
            AmmoMax, AmmoRecharge, ShotVolly, respawnTime: 60f, movable: true);
        e.Solid = Solid.SlideBox;
        e.MoveType = MoveType.Step;

        // Home pose + re-arm on respawn (QC tr_setup pos1/pos2 home; runs on first spawn + respawn).
        TurretState st = TurretAI.State(e);
        Vector3 home = e.Origin;
        Vector3 homeAng = e.Angles;
        st.OnRespawn = self =>
        {
            self.Velocity = Vector3.Zero;
            self.Enemy = null;
            self.Angles = homeAng;
            if (Api.Services is not null) Api.Entities.SetOrigin(self, home);
            self.Solid = Solid.SlideBox;
            self.MoveType = MoveType.Step;
            W(self).RocketVolly = 0;
        };
    }

    public override void Think(Entity e)
    {
        var p = new TurretParams(Select, TargetRangeMin, TargetRange, ShotDamage, ShotRefire,
            AimSpeed, FireTolerance, lead: true, ShotVolly, ShotVollyRefire,
            rangeOptimal: TargetRangeOptimal, shotSpeed: ShotSpeed, aimMaxPitch: AimMaxPitch, aimMaxRot: AimMaxRot,
            shotTimeCompensate: true, zPredict: true, trackType: TurretAI.TrackFluidInertia);

        // Hold fire while mid-melee (QC walker_firecheck: ANIM_MELEE blocks firing).
        WalkerState ws = W(e);
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        if (ws.MeleeUntil > now)
            DoMovement(e, melee: true);
        else
        {
            TurretAI.RunCombat(e, in p, Attack);
            DoMovement(e, melee: false);
        }
    }

    public override bool ValidTarget(Entity self, Entity target)
        => TurretAI.ValidTarget(self, target, Select, TargetRangeMin, TargetRange);

    // WalkerTurret.tr_think: legged movement toward the enemy + the rocket volley + the melee swipe.
    private void DoMovement(Entity e, bool melee)
    {
        if (TurretAI.State(e).Active == false) return;
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        WalkerState ws = W(e);
        float vz = e.Velocity.Z;
        QMath.AngleVectors(new Vector3(0f, e.Angles.Y, 0f), out Vector3 fwd, out _, out _);

        if (melee)
        {
            TurretMath.BrakeSimple(e, SpeedStop);
            e.Velocity = new Vector3(e.Velocity.X, e.Velocity.Y, vz);
            return;
        }

        Vector3 steerTo = Vector3.Zero;
        Entity? enemy = e.Enemy;
        if (enemy is null)
        {
            if (e.Velocity != Vector3.Zero) TurretMath.BrakeSimple(e, SpeedStop);
        }
        else
        {
            TurretState st = TurretAI.State(e);
            float dist = st.DistEnemy;

            // Melee range + roughly facing it -> swing (QC: wish_angle.y < 15 deg).
            Vector3 wishAngle = TurretMath.AngleOfs(e.Origin, e.Angles, enemy.Origin);
            if (dist < MeleeRange && System.Math.Abs(wishAngle.Y) < 15f)
            {
                ws.MeleeUntil = now + 0.41f;          // QC defers walker_setnoanim by 0.41
                ScheduleMelee(e, now + 0.21f);        // QC defers walker_melee_do_dmg by 0.21
                TurretMath.BrakeSimple(e, SpeedStop);
                e.Velocity = new Vector3(e.Velocity.X, e.Velocity.Y, vz);
                return;
            }

            // Rocket volley logic (QC: separate tur_head.shot_volly clock).
            if (ws.RocketFinished < now)
            {
                if (ws.RocketVolly > 0)
                {
                    FireRocket(e, enemy);
                    ws.RocketVolly--;
                    ws.RocketFinished = ws.RocketVolly == 0 ? now + RocketRefire : now + 0.2f;
                }
                else if (dist > RocketRangeMin && dist < RocketRange)
                {
                    ws.RocketVolly = 4;               // QC arms a 4-rocket burst
                }
            }

            // Move toward the enemy (run when far, walk when near), easing in via attract2.
            steerTo = TurretMath.SteerAttract2(e, enemy.Origin, 0.5f, 500f, 0.95f);
            if (dist > 500f) TurretMath.MoveSimple(e, fwd, SpeedRun, 0.6f);
            else TurretMath.MoveSimple(e, fwd, SpeedWalk, 0.6f);
        }

        // Yaw the body toward the heading at the per-gait turnrate (QC: bound(-turny, real_angle.y, turny)).
        if (steerTo != Vector3.Zero)
        {
            float frameTime = Api.Services is not null ? Api.Clock.FrameTime : 0f;
            float turny = (e.Enemy is not null && TurretAI.State(e).DistEnemy > 500f ? TurnRun : TurnWalk);
            Vector3 wish = QMath.VecToAngles(QMath.Normalize(steerTo));
            float diff = TurretMath.ShortAngle(wish.Y - e.Angles.Y, e.Angles.Y);
            float step = turny * frameTime;
            e.Angles = new Vector3(e.Angles.X, e.Angles.Y + QMath.Bound(-step, diff, step), e.Angles.Z);
        }

        e.Velocity = new Vector3(e.Velocity.X, e.Velocity.Y, vz);
    }

    // walker_melee_do_dmg (walker_weapon.qc): a radius hit 128u in front of the walker.
    private void ScheduleMelee(Entity e, float when)
    {
        // The walker entity's Think is owned by the turret loop; schedule the swipe on a throwaway timer entity.
        if (Api.Services is null) return;
        Entity timer = Api.Entities.Spawn();
        timer.ClassName = "walker_melee_timer";
        timer.Owner = e;
        timer.NextThink = when;
        timer.Think = t =>
        {
            Entity w = t.Owner!;
            if (!w.IsFreed && w.DeadState == DeadFlag.No && Api.Services is not null)
            {
                QMath.AngleVectors(w.Angles, out Vector3 f, out _, out _);
                Vector3 at = w.Origin + f * 128f;
                foreach (Entity victim in Api.Entities.FindInRadius(at, 32f))
                {
                    if (ReferenceEquals(victim, w) || ReferenceEquals(victim.Owner, w)) continue;
                    if (!TurretAI.ValidTarget(w, victim, Select, TargetRangeMin, TargetRange)) continue;
                    // walker.qc: the melee bite is DEATH_TURRET_WALK_MELEE.
                    Combat.Damage(victim, w, w, MeleeDamage, DeathTypes.TurretWalkMelee,
                        victim.Origin, f * MeleeForce);
                }
            }
            Api.Entities.Remove(t);
        };
    }

    // walker_fire_rocket (walker_weapon.qc): launch a homing rocket from a forward/up direction with jitter.
    private void FireRocket(Entity turret, Entity enemy)
    {
        TurretState st = TurretAI.State(turret);
        QMath.AngleVectors(turret.Angles, out Vector3 fwd, out _, out Vector3 up);
        Vector3 dir = QMath.Normalize((fwd + up * 0.5f) + Prandom.Vec() * 0.2f);

        GuidedProjectile.Launch(turret, enemy, st.ShotOrg, dir, GuidedProjectile.Mode.WalkerRocket,
            launchSpeed: RocketSpeed, speedMax: RocketSpeed, speedGain: 1f, turnRate: RocketTurnRate,
            size: 6f, health: 25f, RocketDamage, RocketRadius, RocketForce, DeathTypes.TurretWalkRocket, ttl: 9f);

        if (Api.Services is not null)
            Api.Sound.Play(turret, SoundChannel.Weapon, "weapons/rocket_fire.wav");
    }

    // METHOD(WalkerTurretAttack, wr_think) — walker_weapon.qc: minigun bullet along the muzzle dir with spread
    // + knockback force. EFFECT_BULLET / muzzleflash / anim frames are client render.
    private void Attack(Entity turret, Entity enemy)
    {
        TurretState st = TurretAI.State(turret);
        Vector3 dir = QMath.Normalize(st.AimPos - st.ShotOrg);
        if (dir == Vector3.Zero) dir = QMath.Forward(TurretAI.HeadWorldAngles(turret));

        // walker_weapon.qc: fireBullet with DEATH_TURRET_WALK_GUN.
        TurretCombat.FireBullet(turret, st.ShotOrg, dir, ShotSpread, ShotDamage, ShotForce, DeathTypes.TurretWalkGun);

        if (Api.Services is not null)
            Api.Sound.Play(turret, SoundChannel.Weapon, "weapons/uzi_fire.wav");

        // NOTE (client-render): EFFECT_BULLET tracer, EFFECT_BLASTER_MUZZLEFLASH at the head tag, walk/run/
        // melee anim frames. The server-side fire (walker_weapon.qc) is done above.
    }
}
