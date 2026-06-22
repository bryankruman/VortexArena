using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Registry attribute for turrets — the C# stand-in for QC's <c>REGISTER_TURRET</c> (common/turrets/all.qh).
/// Derives from <see cref="GameRegistryAttribute"/> so the existing reflection bootstrap
/// (<c>GameRegistries.Bootstrap</c>) discovers it and routes the instance through
/// <c>case Turret tu: Registry&lt;Turret&gt;.Register(tu)</c>. The shipped Framework/Registry.cs only declares
/// Weapon/Item/Mutator/GameType/Monster attributes (no Turret one), and that file is off-limits here, so the
/// turret attribute is defined locally. Any attribute deriving from the base is honoured identically.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class TurretAttribute : GameRegistryAttribute { }

/// <summary>
/// Per-instance runtime state a live turret entity carries that does not exist on the base <see cref="Entity"/>.
/// In QuakeC these are flat <c>.float</c>/<c>.entity</c> fields on the turret edict (sv_turrets.qc,
/// common/turrets/sv_turrets.qh); the entity-model (ADR-0007) keeps them off the shared edict and on a
/// component bag instead. Attached lazily via <see cref="TurretAI.State"/>.
/// </summary>
public sealed class TurretState
{
    /// <summary>QC <c>.attack_finished_single[0]</c> — absolute sim time the next shot is allowed.</summary>
    public float AttackFinished;

    /// <summary>QC <c>.ammo</c> — current power/ammo pool (regenerates toward <see cref="AmmoMax"/>).</summary>
    public float Ammo;

    /// <summary>QC <c>.ammo_max</c>.</summary>
    public float AmmoMax;

    /// <summary>QC <c>.ammo_recharge</c> — ammo regenerated per second.</summary>
    public float AmmoRecharge;

    /// <summary>QC <c>.volly_counter</c> — shots remaining in the current burst (volley) before the long refire.</summary>
    public int VollyCounter;

    /// <summary>QC <c>.target_select_time</c> — last time a full radius target scan ran (rate-limits scanning).</summary>
    public float TargetSelectTime;

    /// <summary>QC <c>.target_validate_time</c> — throttles re-validating the current enemy.</summary>
    public float TargetValidateTime;

    /// <summary>QC <c>.tur_aimpos</c> — the predicted world point the head is aiming at this frame.</summary>
    public Vector3 AimPos;

    /// <summary>QC <c>.tur_shotorg</c> — the muzzle origin shots leave from (turret origin + barrel offset).</summary>
    public Vector3 ShotOrg;

    /// <summary>
    /// QC <c>.tur_head.angles</c> — the head bone's LOCAL angles (relative to the turret body), slewed toward
    /// the firing solution by the track motor. The world aim is <c>turret.Angles + HeadAngles</c>.
    /// </summary>
    public Vector3 HeadAngles;

    /// <summary>QC <c>.tur_head.avelocity</c> — head angular velocity (fluid-inertia / fluid-precise motors).</summary>
    public Vector3 HeadAVelocity;

    /// <summary>QC <c>.idle_aim</c> — the resting head pose (relative to the body) used when idle.</summary>
    public Vector3 IdleAim;

    /// <summary>QC <c>.lip</c> — sim time until which the head keeps aiming at the last firing solution after losing the target.</summary>
    public float AimIdleUntil;

    /// <summary>QC <c>.active</c> — whether the turret is awake and may acquire/track/fire (team-gated).</summary>
    public bool Active = true;

    /// <summary>QC <c>.respawntime</c> — seconds before a destroyed (respawning) turret reactivates.</summary>
    public float RespawnTime = 60f;

    /// <summary>QC TSL_NO_RESPAWN / TFL_DMG_DEATH_NORESPAWN — death removes the turret instead of respawning it.</summary>
    public bool NoRespawn;

    /// <summary>QC TUR_FLAG_MOVE — a mobile turret that damage knockback can shove (.velocity += vforce).</summary>
    public bool Movable;

    /// <summary>Per-turret death FX + respawn hook (set by the turret so the shared death handler can re-run its setup).</summary>
    public Action<Entity>? OnDeathFx;
    public Action<Entity>? OnRespawn;

    /// <summary>QC <c>.tur_dist_enemy</c> — muzzle→enemy distance, refreshed each think (turret_do_updates).</summary>
    public float DistEnemy;

    /// <summary>QC <c>.tur_dist_aimpos</c> — muzzle→aimpos distance, refreshed each think.</summary>
    public float DistAimPos;

    /// <summary>QC <c>.tur_shotdir_updated</c> — the actual muzzle forward (head world-forward) this think.</summary>
    public Vector3 ShotDir;
}

/// <summary>
/// Shared, Godot-free turret AI — the headless core of common/turrets/sv_turrets.qc. This is the
/// acquire → aim → fire pipeline every <see cref="Turret"/> reuses, factored out so the per-turret files only
/// describe identity + balance + their specific weapon. It operates purely on <see cref="Entity"/> and the
/// engine-services facade (<see cref="Api"/>), so it unit-tests without an engine.
///
/// Ported in full here: the radius scan + multi-flag <c>turret_validate_target</c> gate
/// (team/LOS/range/owner/dead/FOV/per-axis aim limits), the bias-weighted <c>turret_targetscore_generic</c>
/// scoring (range_optimal killzone + angular + missile + player biases, defendmode), lead-aim prediction
/// with shot-traveltime + z-gravity compensation (<c>turret_aim_generic</c> TFL_AIM_*), separate head-angle
/// steering with stepmotor / fluid-inertia / fluid-precise motors and per-axis clamps (<c>turret_track</c>),
/// the refire/volley gate (<c>turret_firecheck</c>/<c>turret_fire</c>), ammo reg/spend, and the
/// activation/damage/death/respawn lifecycle (<c>turret_use</c>/<c>turret_damage</c>/<c>turret_die</c>/
/// <c>turret_respawn</c>).
///
/// Deferred (client-render only): CSQC effects, head-bone model attachment, muzzle flashes.
/// </summary>
public static class TurretAI
{
    // ---- target-select flags (QC TFL_TARGETSELECT_*, turret.qh) used by ValidTarget gating ----
    public const int SelectLos        = 1 << 0;  ///< require line of sight
    public const int SelectPlayers    = 1 << 1;  ///< may target players
    public const int SelectMissiles   = 1 << 2;  ///< may target projectiles
    public const int SelectMissilesOnly = 1 << 3; ///< ONLY projectiles
    public const int SelectTeamCheck  = 1 << 4;  ///< don't attack same team
    public const int SelectOwnTeam    = 1 << 5;  ///< ONLY same team (support units)
    public const int SelectRangeLimits = 1 << 6; ///< honour min/max range
    public const int SelectNoTurrets  = 1 << 7;  ///< don't attack other turrets
    public const int SelectAngleLimits = 1 << 8; ///< honour per-axis aim_maxpitch/aim_maxrot at acquisition

    // ---- track motor types (QC TFL_TRACKTYPE_*, sv_turrets.qh) ----
    public const int TrackStepMotor     = 1; ///< hard angle increments, best accuracy
    public const int TrackFluidPrecise  = 2; ///< smooth absolute movement
    public const int TrackFluidInertia  = 3; ///< simulated inertia ("wobbly")

    /// <summary>QC max_shot_distance (constants.qh) — the hitscan/aim trace ceiling.</summary>
    public const float MaxShotDistance = 32768f;

    /// <summary>QC autocvar_sv_gravity default — used by the z-predict gravity arc lead.</summary>
    public const float Gravity = 800f;

    private static readonly Dictionary<Entity, TurretState> _states = new();

    /// <summary>Fetch (creating on first use) the per-entity turret runtime state (QC flat turret fields).</summary>
    public static TurretState State(Entity e)
    {
        if (!_states.TryGetValue(e, out var s))
        {
            s = new TurretState();
            _states[e] = s;
        }
        return s;
    }

    /// <summary>Drop a turret's state (call on remove/free). Keeps the table from leaking across a match.</summary>
    public static void Forget(Entity e) => _states.Remove(e);

    /// <summary>
    /// The world-space forward of the head: the turret body angles plus the slewed local head angles. This is
    /// what shots travel along and what the on-target/angle checks use (QC <c>this.angles + this.tur_head.angles</c>).
    /// </summary>
    public static Vector3 HeadWorldAngles(Entity turret)
    {
        TurretState st = State(turret);
        return turret.Angles + st.HeadAngles;
    }

    /// <summary>
    /// The muzzle origin a shot leaves from. QC offsets via the <c>tag_fire</c> head tag
    /// (<c>turret_tag_fire_update</c>); here we use the model tag if the model service exposes one, else fall
    /// back to the QC default barrel offset (<c>tur_shotorg='50 0 50'</c>) rotated by the head world angles.
    /// </summary>
    public static Vector3 ShotOrigin(Entity turret)
    {
        if (Api.Services is not null
            && Api.Models.TryGetTag(turret, "tag_fire", out Vector3 tagOrg, out _, out _, out _))
            return tagOrg;

        // turret_tag_fire_update: real tag_fire muzzle from the tur_head model is a client-render detail; here
        // approximate with the QC default '50 0 50' barrel offset rotated into the head's facing.
        QMath.AngleVectors(HeadWorldAngles(turret), out Vector3 fwd, out _, out Vector3 up);
        return turret.Origin + fwd * 50f + up * 50f;
    }

    /// <summary>
    /// Port of <c>turret_validate_target</c> (sv_turrets.qc): is <paramref name="target"/> a legal thing for
    /// <paramref name="turret"/> to shoot, under <paramref name="selectFlags"/>? Returns true only if every
    /// applicable gate passes (alive, damageable, not self/owner, team rule, range, FOV/aim-angle limits, LOS,
    /// missile rule). Mirrors the QC reject-cascade (which returns a negative reason code on reject, >0 accept).
    /// </summary>
    public static bool ValidTarget(Entity turret, Entity? target, int selectFlags, in TurretParams p)
        => ValidTarget(turret, target, selectFlags, p.RangeMin, p.RangeMax, p.AimMaxPitch, p.AimMaxRot);

    /// <summary>Overload without per-axis angle limits (chain hops, support scans) — angle gating only when SelectAngleLimits is set with sane defaults.</summary>
    public static bool ValidTarget(Entity turret, Entity? target, int selectFlags, float rangeMin, float rangeMax)
        => ValidTarget(turret, target, selectFlags, rangeMin, rangeMax, 90f, 360f);

    public static bool ValidTarget(Entity turret, Entity? target, int selectFlags, float rangeMin, float rangeMax,
        float aimMaxPitch, float aimMaxRot)
    {
        if (target is null || target.IsFreed) return false;
        if (ReferenceEquals(target, turret)) return false;
        if (ReferenceEquals(target.Owner, turret)) return false;     // don't shoot own projectiles/parts

        // checkpvs / alpha-cloak: a target faded to <=0.3 alpha is invisible to the turret (QC alpha cull).
        if (target.Alpha != 0f && target.Alpha <= 0.3f) return false;

        // Untargetable / dead / not damageable.
        if ((target.Flags & EntFlags.NoTarget) != 0) return false;
        if (target.TakeDamage == DamageMode.No) return false;
        if (target.Health <= 0f) return false;

        bool isClient = IsPlayer(target);
        bool isMissile = IsMissile(target);

        // Players require the players flag; projectiles require the missiles flag.
        if (isClient && (selectFlags & SelectPlayers) == 0) return false;
        if ((selectFlags & SelectMissilesOnly) != 0 && !isMissile) return false;
        if (isMissile && (selectFlags & SelectMissiles) == 0 && (selectFlags & SelectMissilesOnly) == 0)
            return false;

        // Don't attack other turrets (support units etc.).
        if ((selectFlags & SelectNoTurrets) != 0 && target.ClassName.StartsWith("turret", StringComparison.Ordinal)
            && DiffTeam(turret, target))
            return false;

        // Team rules. Cover the target itself AND its owner/aiment (portals, projectile owners) like QC.
        if ((selectFlags & SelectTeamCheck) != 0)
        {
            if ((selectFlags & SelectOwnTeam) != 0)
            {
                if (DiffTeam(turret, target)) return false;          // support: same-team only
                if (target.Owner is not null && DiffTeam(turret, target.Owner)) return false;
                if (target.Aiment is not null && DiffTeam(turret, target.Aiment)) return false;
            }
            else
            {
                if (SameTeam(turret, target)) return false;          // combat: never same team
                if (target.Owner is not null && SameTeam(turret, target.Owner)) return false;
                if (target.Aiment is not null && SameTeam(turret, target.Aiment)) return false;
            }
        }

        // Range.
        Vector3 toTarget = TargetCenter(target) - turret.Origin;
        float dist = toTarget.Length();
        if ((selectFlags & SelectRangeLimits) != 0)
        {
            if (dist < rangeMin) return false;
            if (dist > rangeMax) return false;
        }

        // Per-axis aim-angle limits: can the head physically point at it? (QC tvt_tadv vs aim_maxpitch/rot.)
        if ((selectFlags & SelectAngleLimits) != 0)
        {
            Vector3 tadv = TurretMath.ShortAngleVxy(
                TurretMath.AngleOfs(turret.Origin, turret.Angles, TargetCenter(target)), turret.Angles);
            if (System.Math.Abs(tadv.X) > aimMaxPitch) return false;
            if (System.Math.Abs(tadv.Y) > aimMaxRot) return false;
        }

        // grapplinghook is never a turret target (QC explicit reject).
        if (target.ClassName == "grapplinghook") return false;

        // Line of sight.
        if ((selectFlags & SelectLos) != 0 && Api.Services is not null)
        {
            Vector3 eye = turret.Origin + new Vector3(0f, 0f, 16f);
            Vector3 aimAt = TargetCenter(target);
            TraceResult tr = Api.Trace.Trace(eye, Vector3.Zero, Vector3.Zero, aimAt, MoveFilter.Normal, turret);
            // Blocked if the trace stopped well short of the target (QC aim_firetolerance_dist test).
            if ((aimAt - tr.EndPos).Length() > 64f && !ReferenceEquals(tr.Ent, target))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Port of <c>turret_targetscore_generic</c> (sv_turrets.qc): the bias-weighted desirability of
    /// <paramref name="target"/>. Combines a distance score (normalized against the optimal killzone, or the
    /// defend-point distance in defendmode), an angular score (how little the head must swing), and the
    /// missile / player biases. A far-out-of-range target is heavily penalized (QC <c>score *= 0.001</c>).
    /// </summary>
    public static float ScoreTarget(Entity turret, Entity target, in TurretParams p)
    {
        TurretState st = State(turret);
        float tvtDist = (turret.Origin - TargetCenter(target)).Length();

        // Distance score.
        float dScore;
        if (p.DefendPoint is { } defend)
        {
            float dDist = (TargetCenter(target) - defend).Length();
            dScore = 1f - dDist / p.RangeMax;
        }
        else
        {
            float ikr = p.RangeOptimal > 0f ? p.RangeOptimal : p.RangeMax * 0.5f;
            dScore = System.Math.Min(ikr, tvtDist) / System.Math.Max(ikr, tvtDist);
        }

        // Angular score: head angle diff vs the max rotation (QC tvt_thadf / aim_maxrot).
        Vector3 thadv = TurretMath.AngleOfs(turret.Origin, HeadWorldAngles(turret), TargetCenter(target));
        float thadf = new Vector3(thadv.X, thadv.Y, 0f).Length();
        float aimMaxRot = p.AimMaxRot > 0f ? p.AimMaxRot : 90f;
        float aScore = 1f - thadf / aimMaxRot;

        float mScore = (p.MissileBias > 0f && IsMissile(target)) ? 1f : 0f;
        float pScore = (p.PlayerBias > 0f && IsPlayer(target)) ? 1f : 0f;

        dScore = System.Math.Max(dScore, 0f);
        aScore = System.Math.Max(aScore, 0f);

        float score = dScore * p.RangeBias
                    + aScore * p.AngleBias
                    + mScore * p.MissileBias
                    + pScore * p.PlayerBias;

        if ((st.ShotOrg - TargetCenter(target)).Length() > p.RangeMax)
            score *= 0.001f;

        return score;
    }

    /// <summary>
    /// Port of <c>turret_select_target</c> (sv_turrets.qc): scan everything in range and return the
    /// best-scoring legal target. The current <see cref="Entity.Enemy"/> gets a samebias bump to stay sticky.
    /// </summary>
    public static Entity? SelectTarget(Entity turret, int selectFlags, in TurretParams p)
    {
        if (Api.Services is null) return null;

        Entity? best = null;
        float bestScore = 0f;

        // Sticky: seed with the existing enemy scored * samebias (QC m_score init).
        if (turret.Enemy is not null && turret.Enemy.TakeDamage != DamageMode.No
            && ValidTarget(turret, turret.Enemy, selectFlags, in p))
        {
            best = turret.Enemy;
            bestScore = ScoreTarget(turret, turret.Enemy, in p) * p.SameBias;
        }

        foreach (Entity e in Api.Entities.FindInRadius(turret.Origin, p.RangeMax))
        {
            if (e.TakeDamage == DamageMode.No) continue;             // QC inlines the takedamage check
            if (!ValidTarget(turret, e, selectFlags, in p)) continue;

            float score = ScoreTarget(turret, e, in p);
            if (score > bestScore && score > 0f)
            {
                best = e;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>Legacy nearest-valid overload (used by the omnidirectional turrets Tesla/FusionReactor that build their own params-free scan).</summary>
    public static Entity? SelectTarget(Entity turret, int selectFlags, float rangeMin, float rangeMax)
    {
        if (Api.Services is null) return null;
        Entity? best = null;
        float bestDist = float.MaxValue;
        if (turret.Enemy is not null && ValidTarget(turret, turret.Enemy, selectFlags, rangeMin, rangeMax))
        {
            best = turret.Enemy;
            bestDist = (TargetCenter(turret.Enemy) - turret.Origin).Length();
        }
        foreach (Entity e in Api.Entities.FindInRadius(turret.Origin, rangeMax))
        {
            if (e.TakeDamage == DamageMode.No) continue;
            if (!ValidTarget(turret, e, selectFlags, rangeMin, rangeMax)) continue;
            float d = (TargetCenter(e) - turret.Origin).Length();
            if (d < bestDist) { best = e; bestDist = d; }
        }
        return best;
    }

    /// <summary>
    /// Port of <c>turret_aim_generic</c> (sv_turrets.qc): where to aim, given the target's motion. Implements
    /// the full lead pipeline: TFL_AIM_LEAD leads by the refire delay; TFL_AIM_SHOTTIMECOMPENSATE additionally
    /// leads by the shot's traveltime <c>vlen(target-muzzle)/shot_speed</c>; TFL_AIM_ZPREDICT integrates the
    /// gravity arc of an airborne walking target over that traveltime; TFL_AIM_SPLASH traces to the ground at
    /// the target's feet.
    /// </summary>
    public static Vector3 AimPoint(Entity turret, Entity target, in TurretParams p)
    {
        Vector3 prePos = TargetCenter(target);
        if (p.AimSimple) return prePos;

        TurretState st = State(turret);
        float frameTime = Api.Services is not null ? Api.Clock.FrameTime : 0f;
        float now = Api.Services is not null ? Api.Clock.Time : 0f;

        if (p.Lead)
        {
            float minTime = System.Math.Max(st.AttackFinished - now, 0f) + frameTime;

            if (p.ShotTimeCompensate && p.ShotSpeed > 0f)
            {
                Vector3 prep = prePos;
                float impactTime = (prep - st.ShotOrg).Length() / p.ShotSpeed;
                prep += target.Velocity * (impactTime + minTime);

                // Gravity arc: integrate the target's z fall over the shot traveltime when it's airborne and
                // ground-affected (QC TFL_AIM_ZPREDICT for WALK/STEP/TOSS/BOUNCE movetypes).
                if (p.ZPredict && !target.OnGround && IsGroundAffected(target) && frameTime > 0f)
                {
                    prep.Z = prePos.Z;
                    float vz = target.Velocity.Z;
                    for (float t = 0f; t < impactTime; t += frameTime)
                    {
                        vz -= Gravity * frameTime;
                        prep.Z += vz * frameTime;
                    }
                }
                prePos = prep;
            }
            else
            {
                prePos += target.Velocity * minTime;
            }
        }

        if (p.AimSplash && Api.Services is not null)
        {
            // Aim for the ground around the target's feet (QC TFL_AIM_SPLASH worldonly downtrace).
            TraceResult tr = Api.Trace.Trace(prePos + new Vector3(0f, 0f, 32f), Vector3.Zero, Vector3.Zero,
                prePos - new Vector3(0f, 0f, 64f), MoveFilter.WorldOnly, target);
            if (tr.Fraction != 1f) prePos = tr.EndPos;
        }

        return prePos;
    }

    /// <summary>
    /// Port of <c>turret_track</c> (sv_turrets.qc): slew the head's LOCAL angles toward the firing solution,
    /// honouring the track motor model (stepmotor / fluid-precise / fluid-inertia) and clamping per axis to
    /// <c>aim_maxpitch</c> / <c>aim_maxrot</c>. The head angles are stored on the <see cref="TurretState"/>
    /// (there is no separate bone entity); the body <see cref="Entity.Angles"/> is left for the mobile turrets
    /// to steer.
    /// </summary>
    public static void Track(Entity turret, in TurretParams p)
    {
        TurretState st = State(turret);
        float frameTime = Api.Services is not null ? Api.Clock.FrameTime : 0f;
        float now = Api.Services is not null ? Api.Clock.Time : 0f;

        // Where do we want to aim (world angles)?
        Vector3 targetAngle;
        if (!st.Active)
            targetAngle = st.IdleAim - new Vector3(p.AimMaxPitch, 0f, 0f) + turret.Angles;
        else if (turret.Enemy is null)
            targetAngle = now > st.AimIdleUntil
                ? st.IdleAim + turret.Angles
                : QMath.VecToAngles(QMath.Normalize(st.AimPos - st.ShotOrg));
        else
            targetAngle = QMath.VecToAngles(QMath.Normalize(st.AimPos - st.ShotOrg));

        st.HeadAngles = new Vector3(TurretMath.AngleMods(st.HeadAngles.X), TurretMath.AngleMods(st.HeadAngles.Y), st.HeadAngles.Z);

        // The desired head-local move (QC: target relative to body, minus current head, shortened).
        Vector3 moveAngle = TurretMath.ShortAngleVxy(targetAngle - turret.Angles - st.HeadAngles, st.HeadAngles);

        float aimMaxPitch = p.AimMaxPitch > 0f ? p.AimMaxPitch : 20f;
        float aimMaxRot = p.AimMaxRot > 0f ? p.AimMaxRot : 90f;
        float aimSpeed = p.AimSpeed > 0f ? p.AimSpeed : 36f;

        switch (p.TrackType)
        {
            case TrackStepMotor:
            {
                float fStep = aimSpeed * frameTime;
                st.HeadAngles.X += QMath.Bound(-fStep, moveAngle.X, fStep);
                st.HeadAngles.X = QMath.Bound(-aimMaxPitch, st.HeadAngles.X, aimMaxPitch);
                st.HeadAngles.Y += QMath.Bound(-fStep, moveAngle.Y, fStep);
                st.HeadAngles.Y = QMath.Bound(-aimMaxRot, st.HeadAngles.Y, aimMaxRot);
                return;
            }

            case TrackFluidInertia:
            {
                float fTmp = aimSpeed * frameTime;
                moveAngle.X = QMath.Bound(-aimSpeed, moveAngle.X * p.TrackAccelPitch * fTmp, aimSpeed);
                moveAngle.Y = QMath.Bound(-aimSpeed, moveAngle.Y * p.TrackAccelRot * fTmp, aimSpeed);
                moveAngle = st.HeadAVelocity * p.TrackBlendRate + moveAngle * (1f - p.TrackBlendRate);
                break;
            }

            case TrackFluidPrecise:
            default:
                moveAngle.X = QMath.Bound(-aimSpeed, moveAngle.X, aimSpeed);
                moveAngle.Y = QMath.Bound(-aimSpeed, moveAngle.Y, aimSpeed);
                break;
        }

        // Fluid motors integrate via head avelocity with the per-axis clamp braking it at the limit.
        st.HeadAVelocity.X = moveAngle.X;
        if (st.HeadAngles.X + st.HeadAVelocity.X * frameTime > aimMaxPitch) { st.HeadAVelocity.X = 0f; st.HeadAngles.X = aimMaxPitch; }
        if (st.HeadAngles.X + st.HeadAVelocity.X * frameTime < -aimMaxPitch) { st.HeadAVelocity.X = 0f; st.HeadAngles.X = -aimMaxPitch; }

        st.HeadAVelocity.Y = moveAngle.Y;
        if (st.HeadAngles.Y + st.HeadAVelocity.Y * frameTime > aimMaxRot) { st.HeadAVelocity.Y = 0f; st.HeadAngles.Y = aimMaxRot; }
        if (st.HeadAngles.Y + st.HeadAVelocity.Y * frameTime < -aimMaxRot) { st.HeadAVelocity.Y = 0f; st.HeadAngles.Y = -aimMaxRot; }

        st.HeadAngles += st.HeadAVelocity * frameTime;
        st.HeadAngles = new Vector3(QMath.Bound(-aimMaxPitch, st.HeadAngles.X, aimMaxPitch),
                                    QMath.Bound(-aimMaxRot, st.HeadAngles.Y, aimMaxRot), st.HeadAngles.Z);
    }

    /// <summary>
    /// The acquire → aim → fire driver shared by every combat turret's <c>Think</c>. Regenerates ammo, rescans
    /// for a target on a delay, leads + tracks it (separate head angle), and invokes <paramref name="fire"/>
    /// once per refire when the muzzle is roughly on target and ammo allows. <paramref name="fire"/> is the
    /// per-turret weapon (the hitscan/projectile body). Returns the current target (or null).
    /// </summary>
    public static Entity? RunCombat(Entity turret, in TurretParams p, Action<Entity, Entity> fire)
    {
        TurretState st = State(turret);
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        float frameTime = Api.Services is not null ? Api.Clock.FrameTime : 0f;

        // Ammo regen (QC turret_think: ammo += ammo_recharge * frametime, capped).
        if (st.Ammo < st.AmmoMax)
            st.Ammo = System.Math.Min(st.Ammo + st.AmmoRecharge * frameTime, st.AmmoMax);

        st.ShotOrg = ShotOrigin(turret);

        // Inactive turrets still slew the head to idle and bail (QC: !active -> turret_track; return).
        if (!st.Active)
        {
            Track(turret, in p);
            return null;
        }

        // (Re)acquire a target. QC: scan when the current one is invalid, on the validate throttle, but never
        // more often than the mindelay.
        const float minDelay = 0.1f; // autocvar_g_turrets_targetscan_mindelay
        const float maxDelay = 0.6f; // autocvar_g_turrets_targetscan_maxdelay
        bool doScan = st.TargetSelectTime + maxDelay < now;

        if (st.TargetValidateTime < now && !ValidTarget(turret, turret.Enemy, p.SelectFlags, in p))
        {
            turret.Enemy = null;
            st.TargetValidateTime = now + 0.5f;
            doScan = true;
        }
        if (st.TargetSelectTime + minDelay > now) doScan = false;

        if (doScan)
        {
            turret.Enemy = SelectTarget(turret, p.SelectFlags, in p);
            st.TargetSelectTime = now;
        }

        Entity? enemy = turret.Enemy;
        if (enemy is null)
        {
            Track(turret, in p);  // keep slewing toward idle
            return null;
        }

        st.AimIdleUntil = now + 5f;   // autocvar_g_turrets_aimidle_delay — hold aim briefly after losing target

        // Aim + track (separate head bone).
        st.AimPos = AimPoint(turret, enemy, in p);
        Track(turret, in p);

        // Refresh the muzzle distances (QC turret_do_updates).
        st.ShotDir = QMath.Forward(HeadWorldAngles(turret));
        st.DistEnemy = (st.ShotOrg - TargetCenter(enemy)).Length();
        st.DistAimPos = (st.ShotOrg - st.AimPos).Length();

        // Fire gate (QC turret_firecheck + turret_fire): cooled down, has ammo, not too close, muzzle on target.
        if (st.AttackFinished > now) return enemy;
        if (st.Ammo < p.ShotDamage) return enemy;
        if (st.DistAimPos < p.RangeMin) return enemy;
        if (!OnTarget(st.ShotOrg, HeadWorldAngles(turret), st.AimPos, p.FireToleranceDist)) return enemy;

        Fire(turret, enemy, in p, fire);
        return enemy;
    }

    /// <summary>
    /// Port of <c>turret_fire</c> (sv_turrets.qc): run the turret's weapon once, then advance the refire clock,
    /// spend ammo, and tick the volley counter (applying the long volley-refire and optional target-clear at
    /// the end of a burst). Exposed so the custom turrets (Tesla/Phaser/FusionReactor) can reuse the bookkeeping.
    /// </summary>
    public static void Fire(Entity turret, Entity enemy, in TurretParams p, Action<Entity, Entity> fire)
    {
        TurretState st = State(turret);
        float now = Api.Services is not null ? Api.Clock.Time : 0f;

        fire(turret, enemy);

        st.AttackFinished = now + p.Refire;
        st.Ammo -= p.ShotDamage;

        if (p.ShotVolly > 1)
        {
            st.VollyCounter--;
            if (st.VollyCounter <= 0)
            {
                st.VollyCounter = p.ShotVolly;
                if (p.ClearTarget) turret.Enemy = null;
                st.AttackFinished = now + p.VollyRefire;
            }
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Lifecycle: activation, damage retaliation, death + respawn (QC turret_use/damage/die/respawn).
    // ----------------------------------------------------------------------------------------------------

    private static bool _deathHooked;

    /// <summary>
    /// Subscribe (once) to the shared <see cref="Combat.Death"/> bus so a turret the generic
    /// <c>DamageSystem</c> kills (health &lt; 1) runs <see cref="Die"/> — the headless pipeline has no
    /// per-entity <c>event_damage</c> hook, so this is how death+respawn fires automatically (the same pattern
    /// Breakable.cs uses). The pre-damage gating + retaliation live in <see cref="Damage"/>.
    /// </summary>
    public static void EnsureDeathHook()
    {
        if (_deathHooked) return;
        _deathHooked = true;
        Combat.Death.Add(OnAnyDeath);
    }

    private static bool OnAnyDeath(ref DeathEvent ev)
    {
        Entity v = ev.Victim;
        if (v.ClassName.StartsWith("turret_", StringComparison.Ordinal) && v.DeadState != DeadFlag.Respawning)
            Die(v);
        return false;  // non-exclusive: other death subscribers still run
    }

    /// <summary>
    /// Port of <c>turret_use</c> (sv_turrets.qc): on trigger, the turret adopts the activator's team and goes
    /// active (or inactive + teamless if the activator is teamless). Wire to <see cref="Entity.Use"/>.
    /// </summary>
    public static void Use(Entity turret, Entity? activator)
    {
        turret.Team = activator?.Team ?? 0f;
        State(turret).Active = turret.Team != 0f;
    }

    /// <summary>
    /// Port of <c>turret_damage</c> (sv_turrets.qc): the pre-damage gate + retaliation a turret applies on a
    /// hit. Returns the (possibly friendly-fire-scaled) damage the caller should actually inflict, or 0 to
    /// reject it entirely (dead/inactive/teammate with friendlyfire off). Also shoves a movable turret and
    /// picks the attacker as a target (TFL_DMG_RETALIATE). The server damage router calls this before applying
    /// damage; death itself flows through <see cref="OnAnyDeath"/>.
    /// </summary>
    public static float Damage(Entity turret, Entity? attacker, float damage, Vector3 force)
    {
        TurretState st = State(turret);
        if (turret.DeadState == DeadFlag.Dead) return 0f;
        if (!st.Active) return 0f;                                    // inactive turrets take no damage

        // Friendly fire: QC scales by g_friendlyfire (default 0 => no team damage).
        if (attacker is not null && SameTeam(turret, attacker))
        {
            float ff = Api.Services is not null ? Api.Cvars.GetFloat("g_friendlyfire") : 0f;
            if (ff <= 0f) return 0f;
            damage *= ff;
        }

        if (st.Movable) turret.Velocity += force;

        // Retaliate: shooting back makes the attacker a target (QC TFL_DMG_RETALIATE picks the attacker enemy).
        if (attacker is not null && turret.Enemy is null && DiffTeam(turret, attacker))
            turret.Enemy = attacker;

        return damage;
    }

    /// <summary>
    /// The turret's installed <c>.event_damage</c> (QC <c>turret_damage</c>, sv_turrets.qc) — the
    /// <see cref="Entity.GtEventDamage"/> shim wired in <see cref="TurretSpawn.Init"/>. The headless
    /// <see cref="Damage.DamageSystem.EventDamage"/> routes every non-player edict with a <c>GtEventDamage</c>
    /// here (and returns), exactly as it does for monsters / Onslaught objectives — so a turret victim runs the
    /// turret pre-damage gate + retaliation (<see cref="Damage"/>) and its OWN health subtract, instead of being
    /// treated as a player by <c>PlayerDamage</c>.
    ///
    /// This is the seam Wave-2 turrets depend on: it makes <see cref="Damage"/> (which was bit-faithful but had
    /// no live caller) actually run on the live damage path. It applies the gated damage to RES_HEALTH and, when
    /// the turret drops to 0, fires the shared <see cref="Combat.Death"/> bus — which <see cref="OnAnyDeath"/>
    /// already turns into <see cref="Die"/> (blast + respawn schedule). Signature mirrors <c>GtEventDamage</c>:
    /// <c>(self, inflictor, attacker, deathtype, damage, hitloc, force)</c>.
    /// </summary>
    public static void EventDamage(Entity turret, Entity? inflictor, Entity? attacker, string deathType,
        float damage, Vector3 hitLoc, Vector3 force)
    {
        _ = inflictor; _ = hitLoc; // (parity slots: QC turret_damage uses attacker/force; hitloc is unused)

        // Pre-damage gate (QC turret_damage): rejects dead/inactive/friendly hits (returns 0), scales friendly
        // fire, shoves a movable turret, and picks the attacker for retaliation.
        float take = Damage(turret, attacker, damage, force);
        if (take <= 0f)
            return;

        // QC turret_damage: TakeResource(this, RES_HEALTH, damage) then `if (health <= 0) turret_die`. A turret
        // is not a player, so there is no armor split here — the raw gated damage hits health.
        turret.TakeResource(ResourceType.Health, take);
        turret.Health = turret.GetResource(ResourceType.Health);

        if (turret.Health <= 0f && turret.DeadState == DeadFlag.No)
        {
            // Fire the shared obituary/death bus (gametypes score, OnAnyDeath -> Die runs the blast + respawn).
            var death = new DeathEvent { Victim = turret, Attacker = attacker, Inflictor = inflictor, DeathType = deathType };
            Combat.Death.Call(ref death);
        }
    }

    /// <summary>
    /// Port of <c>turret_die</c> (sv_turrets.qc): unsolidify, stop taking damage, do the death blast
    /// (RadiusDamage scaled by leftover ammo), and either remove (no-respawn) or schedule a respawn after
    /// <see cref="TurretState.RespawnTime"/>. Driven from <see cref="OnAnyDeath"/>.
    /// </summary>
    public static void Die(Entity turret)
    {
        TurretState st = State(turret);
        turret.DeadState = DeadFlag.Dead;
        turret.Solid = Solid.Not;
        turret.TakeDamage = DamageMode.No;
        turret.Health = 0f;
        turret.SetResourceExplicit(ResourceType.Health, 0f);
        turret.Enemy = null;
        st.Active = false;          // a dead turret runs no combat/movement think until respawn

        // Go boom: a blast scaled by the remaining ammo (QC turret_die commented variant; applied as death FX).
        float boom = System.Math.Min(st.Ammo, 50f);
        if (boom > 0f)
            // QC turret_die death blast (sv_turrets.qc:182): DEATH_TURRET (the generic turret deathtype).
            WeaponSplash.RadiusDamage(turret, turret.Origin, boom, boom * 0.25f, 250f, null,
                0, boom * 5f, deathTag: DeathTypes.Turret);

        st.OnDeathFx?.Invoke(turret);

        if (st.NoRespawn)
        {
            if (Api.Services is not null) Api.Entities.Remove(turret);
            Forget(turret);
            return;
        }

        // Schedule respawn (QC turret_hide -> turret_respawn after respawntime).
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        turret.DeadState = DeadFlag.Respawning;
        turret.NextThink = now + st.RespawnTime;
        turret.Think = Respawn;
    }

    /// <summary>
    /// Port of <c>turret_respawn</c> (sv_turrets.qc): re-solidify, restore full health/ammo/volley, clear the
    /// enemy, reset the head, and resume thinking. <see cref="TurretState.OnRespawn"/> lets a turret re-run its
    /// per-unit setup (model frame, movetype, home pose) on respawn. Shaped as an <see cref="EntityThink"/>.
    /// </summary>
    public static void Respawn(Entity turret)
    {
        TurretState st = State(turret);
        turret.DeadState = DeadFlag.No;
        turret.Solid = Solid.BBox;
        turret.TakeDamage = DamageMode.Aim;
        turret.Health = turret.MaxHealth;
        turret.SetResourceExplicit(ResourceType.Health, turret.MaxHealth);
        turret.Enemy = null;
        turret.AVelocity = Vector3.Zero;
        st.HeadAVelocity = Vector3.Zero;
        st.HeadAngles = st.IdleAim;
        st.VollyCounter = st.AmmoMax > 0f ? st.VollyCounter : 1;
        st.Ammo = st.AmmoMax;
        st.AttackFinished = 0f;
        st.Active = turret.Team != 0f;
        turret.Think = null;
        turret.NextThink = 0f;

        st.OnRespawn?.Invoke(turret);
    }

    /// <summary>Is the muzzle, pointing along <paramref name="angles"/>, aimed within <paramref name="tol"/> of <paramref name="aimPos"/>?</summary>
    private static bool OnTarget(Vector3 muzzle, Vector3 angles, Vector3 aimPos, float tol)
    {
        Vector3 toAim = aimPos - muzzle;
        float dist = toAim.Length();
        if (dist <= 0.001f) return true;
        Vector3 fwd = QMath.Forward(angles);
        Vector3 closest = muzzle + fwd * dist;       // where the barrel line is at the target's range
        return (aimPos - closest).Length() <= tol;
    }

    /// <summary>The target's bbox center (QC real_origin + 0.5*(mins+maxs)); origin if it has no size.</summary>
    public static Vector3 TargetCenter(Entity e) => e.Origin + (e.Mins + e.Maxs) * 0.5f;

    /// <summary>QC IS_CLIENT/IS_PLAYER: a real player actor (the <see cref="Player"/> subclass or a client edict).</summary>
    public static bool IsPlayer(Entity e) => e is Player || (e.Flags & EntFlags.Client) != 0;

    /// <summary>QC FL_PROJECTILE stand-in: an owned item-flagged projectile (turret/weapon missile).</summary>
    public static bool IsMissile(Entity e) => (e.Flags & EntFlags.Item) != 0 && e.Owner is not null;

    /// <summary>QC: gravity-affected movetypes whose z arc the lead must integrate (WALK/STEP/TOSS/BOUNCE).</summary>
    private static bool IsGroundAffected(Entity e)
        => e.MoveType is MoveType.Walk or MoveType.Step or MoveType.Toss or MoveType.Bounce;

    /// <summary>QC SAME_TEAM: both on a (nonzero) team and equal. Turrets default team is nonzero when team play is on.</summary>
    public static bool SameTeam(Entity a, Entity b) => a.Team != 0f && a.Team == b.Team;

    /// <summary>QC DIFF_TEAM: on different teams (treating either being teamless as "different").</summary>
    public static bool DiffTeam(Entity a, Entity b) => a.Team != b.Team;
}

/// <summary>
/// The balance + behaviour-flag bundle a turret hands to <see cref="TurretAI.RunCombat"/>. Mirrors the QC
/// per-unit cvars (turrets.cfg <c>g_turrets_unit_*</c>) the AI core reads each think — now carrying the full
/// scoring biases, aim-angle limits, lead flags, shot speed and head track motor. Per-turret extras (homing
/// turn rates, melee, etc.) stay on the individual turret class.
/// </summary>
public readonly struct TurretParams
{
    public readonly int SelectFlags;        // QC target_select_flags
    public readonly float RangeMin;         // QC target_range_min
    public readonly float RangeMax;         // QC target_range
    public readonly float RangeOptimal;     // QC target_range_optimal (killzone for the distance score)
    public readonly float ShotDamage;       // QC shot_dmg (also the per-shot ammo cost)
    public readonly float ShotSpeed;        // QC shot_speed (projectile speed; used by lead compensation)
    public readonly float Refire;           // QC shot_refire
    public readonly float AimSpeed;         // QC aim_speed (deg/sec head slew)
    public readonly float AimMaxPitch;      // QC aim_maxpitch
    public readonly float AimMaxRot;        // QC aim_maxrot
    public readonly float FireToleranceDist;// QC aim_firetolerance_dist (how close the muzzle must be on target)

    // aim flags (QC TFL_AIM_*)
    public readonly bool Lead;              // TFL_AIM_LEAD
    public readonly bool ShotTimeCompensate;// TFL_AIM_SHOTTIMECOMPENSATE
    public readonly bool ZPredict;          // TFL_AIM_ZPREDICT
    public readonly bool AimSplash;         // TFL_AIM_SPLASH
    public readonly bool AimSimple;         // TFL_AIM_SIMPLE (aim at current pos, no lead)

    // volley (QC shot_volly / shot_volly_refire / TFL_SHOOT_CLEARTARGET)
    public readonly int ShotVolly;
    public readonly float VollyRefire;
    public readonly bool ClearTarget;

    // target scoring biases (QC target_select_*bias)
    public readonly float RangeBias;
    public readonly float SameBias;
    public readonly float AngleBias;
    public readonly float MissileBias;
    public readonly float PlayerBias;

    // head track motor (QC track_type / track_accel_* / track_blendrate)
    public readonly int TrackType;
    public readonly float TrackAccelPitch;
    public readonly float TrackAccelRot;
    public readonly float TrackBlendRate;

    // defendmode (QC tur_defend.origin) — scores targets by closeness to a point to defend
    public readonly Vector3? DefendPoint;

    public TurretParams(int selectFlags, float rangeMin, float rangeMax, float shotDamage, float refire,
        float aimSpeed, float fireToleranceDist, bool lead,
        int shotVolly = 0, float vollyRefire = 0f,
        float rangeOptimal = 0f, float shotSpeed = 0f, float aimMaxPitch = 20f, float aimMaxRot = 90f,
        bool shotTimeCompensate = false, bool zPredict = false, bool aimSplash = false, bool aimSimple = false,
        bool clearTarget = false,
        float rangeBias = 1f, float sameBias = 1f, float angleBias = 1f, float missileBias = 1f, float playerBias = 1f,
        int trackType = TurretAI.TrackFluidInertia, float trackAccelPitch = 0.5f, float trackAccelRot = 0.5f,
        float trackBlendRate = 0.35f, Vector3? defendPoint = null)
    {
        SelectFlags = selectFlags;
        RangeMin = rangeMin;
        RangeMax = rangeMax;
        RangeOptimal = rangeOptimal;
        ShotDamage = shotDamage;
        ShotSpeed = shotSpeed;
        Refire = refire;
        AimSpeed = aimSpeed;
        AimMaxPitch = aimMaxPitch;
        AimMaxRot = aimMaxRot;
        FireToleranceDist = fireToleranceDist;
        Lead = lead;
        ShotTimeCompensate = shotTimeCompensate;
        ZPredict = zPredict;
        AimSplash = aimSplash;
        AimSimple = aimSimple;
        ShotVolly = shotVolly;
        VollyRefire = vollyRefire;
        ClearTarget = clearTarget;
        RangeBias = rangeBias;
        SameBias = sameBias;
        AngleBias = angleBias;
        MissileBias = missileBias;
        PlayerBias = playerBias;
        TrackType = trackType;
        TrackAccelPitch = trackAccelPitch;
        TrackAccelRot = trackAccelRot;
        TrackBlendRate = trackBlendRate;
        DefendPoint = defendPoint;
    }
}
