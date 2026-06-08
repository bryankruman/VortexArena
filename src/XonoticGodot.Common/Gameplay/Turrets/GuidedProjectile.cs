using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The guided-missile think chains the homing turrets share — faithful ports of
/// <c>turret_hellion_missile_think</c> (hellion_weapon.qc), <c>turret_hk_missile_think</c> (hk_weapon.qc) and
/// <c>walker_rocket_think</c> (walker_weapon.qc). Each is a per-frame steering+acceleration step run off the
/// projectile's <see cref="Entity.Think"/>; on intercept (or fuel-out) it detonates via the explode action
/// the spawner installed (the radius-damage closure from <see cref="TurretSpawn.Projectile"/> /
/// <see cref="TurretMissiles"/>).
///
/// Per-projectile guidance scratch (QC flat fields on the missile edict) lives on
/// <see cref="GuidanceState"/>, attached lazily. Godot-free; operates only on <see cref="Entity"/> +
/// <see cref="Api"/>. Only the CSQC trail/smoke is left to the client.
/// </summary>
public static class GuidedProjectile
{
    /// <summary>Per-missile guidance scratch (QC .tur_aimpos jitter, .cnt fuel/guide timers, .max_health TTL, .shot_* speed band).</summary>
    public sealed class GuidanceState
    {
        public float ExpireTime;     // QC .max_health / .cnt — absolute time the missile self-destructs
        public float GuideJitterAt;  // QC .cnt for walker — next time to re-roll the aim jitter
        public Vector3 AimJitter;    // QC .tur_shotorg / .tur_aimpos — random offset added to the enemy aim point
        public float Speed;          // launch speed (for the accel band)
        public float SpeedMax;       // QC shot_speed_max
        public float SpeedGain;      // QC shot_speed_gain (hellion accel multiplier)
        public float TurnRate;       // QC shot_speed_turnrate / rocket_turnrate
        public float Radius;         // QC owner.shot_radius (proximity-detonate distance basis)
        public float FuelTime;       // QC .cnt for hk — time the boosted accel band lasts
        public Action<Entity>? Explode;  // the detonation closure (radius damage + remove)
    }

    private static readonly Dictionary<Entity, GuidanceState> _g = new();

    public static GuidanceState Guidance(Entity e)
    {
        if (!_g.TryGetValue(e, out var s)) { s = new GuidanceState(); _g[e] = s; }
        return s;
    }

    public static void Forget(Entity e) => _g.Remove(e);

    /// <summary>How a guided missile steers (selects the per-frame think).</summary>
    public enum Mode { Hellion, Hk, WalkerRocket }

    /// <summary>
    /// Spawn a guided turret missile: a projectile that flies under one of the guidance models toward
    /// <paramref name="enemy"/> and detonates (radius damage) on intercept, touch, or fuel-out. Wires the
    /// <see cref="GuidanceState"/> speed band + TTL and the detonation closure. Returns the missile.
    /// </summary>
    public static Entity Launch(Entity turret, Entity? enemy, Vector3 origin, Vector3 dir, Mode mode,
        float launchSpeed, float speedMax, float speedGain, float turnRate, float size, float health,
        float damage, float radius, float force, int deathType, float ttl)
    {
        Entity m = Api.Entities.Spawn();
        m.ClassName = "turret_guided";
        m.Owner = turret;
        m.Enemy = enemy;
        m.Team = turret.Team;
        m.MoveType = mode == Mode.Hk ? MoveType.BounceMissile : MoveType.FlyMissile;
        m.Solid = Solid.BBox;
        m.Flags = EntFlags.Item;
        m.Velocity = QMath.Normalize(dir) * launchSpeed;
        m.Angles = QMath.VecToAngles(m.Velocity);

        Vector3 half = new Vector3(0.5f, 0.5f, 0.5f) * size;
        Api.Entities.SetSize(m, -half, half);
        Api.Entities.SetOrigin(m, origin);

        float now = Api.Services is not null ? Api.Clock.Time : 0f;

        // Detonation: radius damage + remove (QC turret_projectile_explode / *_rocket_explode).
        void Explode(Entity self)
        {
            self.Touch = null;
            self.Think = null;
            self.TakeDamage = DamageMode.No;
            WeaponSplash.RadiusDamage(self, self.Origin, damage, 0f, radius, self.Owner, deathType, force);
            Forget(self);
            Api.Entities.Remove(self);
        }

        if (health > 0f)
        {
            // Shootable hull (FLAC shoots these down). The headless DamageSystem has no per-entity
            // event_damage hook, so detonation-when-destroyed flows through the shared death hook below.
            m.TakeDamage = DamageMode.Yes;
            m.Health = health;
            m.SetResourceExplicit(ResourceType.Health, health);
            EnsureDeathHook();
        }
        else
        {
            m.TakeDamage = DamageMode.No;
            m.Flags |= EntFlags.NoTarget;
        }

        GuidanceState g = Guidance(m);
        g.Speed = launchSpeed;
        g.SpeedMax = speedMax;
        g.SpeedGain = speedGain;
        g.TurnRate = turnRate;
        g.Radius = radius;
        g.ExpireTime = now + ttl;
        g.FuelTime = now + 30f;            // QC hk: cnt = time + 30 (boosted accel window)
        g.GuideJitterAt = now + 1f;        // QC walker: cnt = time + 1
        g.AimJitter = Prandom.Vec() * 512f;
        g.Explode = Explode;

        m.Touch = (self, _) => Explode(self);
        EntityThink think = mode switch
        {
            Mode.Hellion => HellionThink,
            Mode.Hk => HkThink,
            _ => self => WalkerRocketThink(self, launchSpeed, turnRate),
        };
        m.Think = think;
        m.NextThink = now;
        return m;
    }

    // ------------------------------------------------------------------------------------------------
    // Hellion: heat-seeking missile that predicts the lead point and accelerates toward it each frame.
    // ------------------------------------------------------------------------------------------------
    public static void HellionThink(Entity missile)
    {
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        float dt = Api.Services is not null ? Api.Clock.FrameTime : 0.05f;
        GuidanceState g = Guidance(missile);

        missile.NextThink = now + 0.05f;
        Vector3 olddir = QMath.Normalize(missile.Velocity);
        float speed = missile.Velocity.Length();

        if (g.ExpireTime < now) { Detonate(missile); return; }

        // Enemy dead/gone? keep current heading and accelerate; explode if we stray too far from the owner.
        if (missile.Enemy is null || missile.Enemy.Health <= 0f || missile.Enemy.DeadState != DeadFlag.No)
        {
            missile.Enemy = null;
            missile.Angles = QMath.VecToAngles(missile.Velocity);
            if (missile.Owner is not null && (missile.Origin - missile.Owner.Origin).Length() > g.Radius * 5f)
            { Detonate(missile); return; }
            missile.Velocity = olddir * System.Math.Min(speed * g.SpeedGain, g.SpeedMax);
            return;
        }

        // Close enough to deal good damage?
        if ((missile.Origin - missile.Enemy.Origin).Length() < g.Radius * 0.2f) { Detonate(missile); return; }

        // Predict enemy position by traveltime, average it with the current pos (QC smoothing).
        float itime = speed > 0f ? (missile.Enemy.Origin - missile.Origin).Length() / speed : 0f;
        Vector3 prePos = missile.Enemy.Origin + missile.Enemy.Velocity * itime;
        prePos = (prePos + missile.Enemy.Origin) * 0.5f;

        Vector3 newdir = QMath.Normalize(prePos - missile.Origin);
        newdir = QMath.Normalize(olddir + newdir * 0.35f);          // turn (limited blend)
        missile.Velocity = newdir * System.Math.Min(speed * g.SpeedGain, g.SpeedMax);  // accelerate
        missile.Angles = QMath.VecToAngles(missile.Velocity);

        if (itime < 0.05f) Detonate(missile);
    }

    // ------------------------------------------------------------------------------------------------
    // Hunter-Killer: obstacle-avoiding guided rocket. Funnel-traces forward/L/R/U/D, biases toward the
    // clearest direction, accels in the open and decels near walls/sharp turns, with a panic turn.
    // ------------------------------------------------------------------------------------------------
    public static void HkThink(Entity missile)
    {
        if (Api.Services is null) { Detonate(missile); return; }
        float now = Api.Clock.Time;
        GuidanceState g = Guidance(missile);
        missile.NextThink = now;

        // Drop a dead/invalid target so we can re-seek.
        if (missile.Enemy is not null && (missile.Enemy.Health <= 0f || missile.Enemy.DeadState != DeadFlag.No))
            missile.Enemy = null;

        if (g.ExpireTime < now) { Detonate(missile); return; }

        // Build the missile's facing basis (QC negates pitch around makevectors == fixedvectoangles).
        Vector3 ang = QMath.FixedVecToAngles(missile.Velocity);
        QMath.AngleVectors(ang, out Vector3 vfwd, out Vector3 vright, out Vector3 vup);

        Vector3 ve = Vector3.Zero; float fe = 0f;
        if (missile.Enemy is not null)
        {
            if ((missile.Origin - missile.Enemy.Origin).Length() <= g.Radius * 0.25f) { Detonate(missile); return; }
            float lead = System.Math.Min(missile.Enemy.Velocity.Length() > 0f
                ? (missile.Enemy.Origin - missile.Origin).Length() / System.Math.Max(missile.Velocity.Length(), 1f)
                : 0f, 0.5f);
            Vector3 prePos = missile.Enemy.Origin + missile.Enemy.Velocity * lead;
            TraceResult t = Api.Trace.Trace(missile.Origin, Vector3.Zero, Vector3.Zero, prePos, MoveFilter.NoMonsters, missile.Enemy);
            ve = QMath.Normalize(prePos - missile.Origin);
            fe = t.Fraction;
        }

        float myspeed = missile.Velocity.Length();
        Vector3 wishdir;

        bool clearToTarget = missile.Enemy is not null && fe == 1f
                             && (missile.Origin - missile.Enemy!.Origin).Length() <= 1000f;

        if (!clearToTarget)
        {
            float ltFor = myspeed * 3f;
            float ltSeek = myspeed * 2.95f;

            float ff = Ray(missile, missile.Origin, missile.Origin + vfwd * ltFor);

            float ad = missile.Enemy is not null
                ? (QMath.VecToAngles(QMath.Normalize(missile.Enemy.Origin - missile.Origin)) - ang).Length()
                : 0f;

            // Too close to a wall or a sharp turn? slow down. Fairly clear? speed up.
            if ((ff < 0.7f || ad > 4f) && myspeed > g.Speed)
                myspeed = System.Math.Max(myspeed * 0.85f, g.Speed);   // decel
            if (ff > 0.7f && myspeed < g.SpeedMax)
                myspeed = System.Math.Min(myspeed * 1.05f, g.SpeedMax);// accel

            float ptSeek = QMath.Bound(0.15f, 1f - ff, 0.8f);
            if (ff < 0.5f) ptSeek = 1f;

            float fl = Ray(missile, missile.Origin, missile.Origin + (-(vright * ptSeek) + vfwd * ff) * ltSeek, out Vector3 vl);
            float fr = Ray(missile, missile.Origin, missile.Origin + ((vright * ptSeek) + vfwd * ff) * ltSeek, out Vector3 vr);
            float fu = Ray(missile, missile.Origin, missile.Origin + ((vup * ptSeek) + vfwd * ff) * ltSeek, out Vector3 vu2);
            float fd = Ray(missile, missile.Origin, missile.Origin + (-(vup * ptSeek) + vfwd * ff) * ltSeek, out Vector3 vd);

            vl = QMath.Normalize(vl - missile.Origin);
            vr = QMath.Normalize(vr - missile.Origin);
            vu2 = QMath.Normalize(vu2 - missile.Origin);
            vd = QMath.Normalize(vd - missile.Origin);

            if (ptSeek == 1f)
            {
                // Panic: pick a single clearest cardinal and turn hard.
                wishdir = vright;
                if (fl > fr) wishdir = -vright;
                if (fu > fl) wishdir = vup;
                if (fd > fu) wishdir = -vup;
            }
            else
            {
                wishdir = QMath.Normalize(vl * fl + vr * fr + vu2 * fu + vd * fd);
            }

            if (missile.Enemy is not null)
            {
                if (fe < 0.1f) fe = 0.1f;   // always bias slightly toward the target
                wishdir = wishdir * (1f - fe) + ve * fe;
            }
        }
        else
        {
            // Clear path: boost toward full speed and go straight at the target.
            if (myspeed < g.SpeedMax) myspeed = System.Math.Min(myspeed * 1.1f, g.SpeedMax);
            wishdir = ve;
        }

        // Boosted accel while fuel lasts (QC: accel2 while cnt > time), then sputter (drop think a beat).
        if (myspeed > g.Speed && g.FuelTime > now)
            myspeed = System.Math.Min(myspeed * 1.1f, g.SpeedMax);

        Vector3 olddir = QMath.Normalize(missile.Velocity);
        Vector3 newdir = QMath.Normalize(olddir + wishdir * g.TurnRate);
        missile.Velocity = newdir * myspeed;
        missile.Angles = QMath.VecToAngles(missile.Velocity);
    }

    // ------------------------------------------------------------------------------------------------
    // Walker rocket: crude steerlib_pull guidance toward the enemy + jitter, movelib-driven turn.
    // ------------------------------------------------------------------------------------------------
    public static void WalkerRocketThink(Entity rocket, float speed, float turnRate)
    {
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        GuidanceState g = Guidance(rocket);
        rocket.NextThink = now;

        float edist = rocket.Enemy is not null ? (rocket.Enemy.Origin - rocket.Origin).Length() : 0f;

        // Re-roll the aim jitter periodically to "simulate crude guidance".
        if (g.GuideJitterAt < now)
        {
            g.AimJitter = Prandom.Vec() * System.Math.Min(edist, edist < 1000f ? 64f : 256f);
            g.GuideJitterAt = now + 0.5f;
        }
        if (edist < 128f) g.AimJitter = Vector3.Zero;

        if (g.ExpireTime < now) { Detonate(rocket); return; }

        if (rocket.Enemy is not null && (rocket.Enemy.Health <= 0f || rocket.Enemy.DeadState != DeadFlag.No))
            rocket.Enemy = null;

        Vector3 newdir = rocket.Enemy is not null
            ? TurretMath.SteerPull(rocket, rocket.Enemy.Origin + g.AimJitter)
            : QMath.Normalize(rocket.Velocity);

        // movelib_move_simple((rocket), newdir, speed, turnrate).
        TurretMath.MoveSimple(rocket, newdir, speed, turnRate);
        rocket.Angles = QMath.VecToAngles(rocket.Velocity);
    }

    private static void Detonate(Entity missile)
    {
        GuidanceState g = Guidance(missile);
        Action<Entity>? boom = g.Explode;
        Forget(missile);
        if (boom is not null) boom(missile);
        else if (missile.Think is not null) { var t = missile.Think; missile.Think = null; t(missile); }
    }

    private static bool _deathHooked;

    /// <summary>Detonate a guided missile the damage pipeline shoots down (FLAC vs hellion/hk rockets).</summary>
    public static void EnsureDeathHook()
    {
        if (_deathHooked) return;
        _deathHooked = true;
        Damage.Combat.Death.Add(static (ref Damage.DeathEvent ev) =>
        {
            Entity v = ev.Victim;
            if (v.ClassName == "turret_guided" && _g.TryGetValue(v, out var g) && g.Explode is not null)
                g.Explode(v);
            return false;
        });
    }

    /// <summary>Trace fraction toward <paramref name="to"/> (QC traceline, no monsters), discarding the endpoint.</summary>
    private static float Ray(Entity self, Vector3 from, Vector3 to)
    {
        TraceResult tr = Api.Trace.Trace(from, Vector3.Zero, Vector3.Zero, to, MoveFilter.NoMonsters, self);
        return tr.Fraction;
    }

    /// <summary>Trace fraction toward <paramref name="to"/>, also returning the trace endpoint.</summary>
    private static float Ray(Entity self, Vector3 from, Vector3 to, out Vector3 endPos)
    {
        TraceResult tr = Api.Trace.Trace(from, Vector3.Zero, Vector3.Zero, to, MoveFilter.NoMonsters, self);
        endPos = tr.EndPos;
        return tr.Fraction;
    }
}
