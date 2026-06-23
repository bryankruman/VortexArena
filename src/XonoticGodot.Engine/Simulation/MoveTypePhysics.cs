using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;

namespace XonoticGodot.Engine.Simulation;

/// <summary>
/// MOVETYPE integrators for non-client entities — the per-entity movement dispatch from
/// Darkplaces' SV_Physics_Entity (sv_phys.c:2682). Each tick the loop calls
/// <see cref="RunEntity"/> for every non-client entity; it runs the entity's due think and then its
/// movetype integrator, ported from SV_Physics_Toss / SV_Physics_Step / SV_WalkMove + SV_FlyMove.
///
/// Player movement is intentionally NOT here: in Xonotic it lives in QuakeC (SV_PlayerPhysics) and is
/// driven through the loop's client-move callback (spec §"Why this is tractable"). The Walk/Step
/// integrators below are for monsters/AI and movers, not the local player's PM_ math.
/// </summary>
public static class MoveTypePhysics
{
    public const float StepHeight = 31f;          // sv_stepheight (Xonotic physicsX.cfg default)
    public const float FloorNormalZ = FlyMove.FloorNormalZ;

    /// <summary>
    /// Dispatch one non-client entity for this tick. <paramref name="runThink"/> fires the entity's
    /// due think (and reports whether it survived). Mirrors the switch in SV_Physics_Entity.
    /// </summary>
    public static void RunEntity(PhysicsContext ctx, Entity ent, Func<Entity, bool> runThink)
    {
        switch (ent.MoveType)
        {
            case MoveType.Push:
                // SV_Physics_Pusher (sv_phys.c:1908): move the pusher on its local time, carry riders,
                // and fire the think when ltime crosses nextthink. PushMove handles crush/blocked.
                ctx.PhysicsPusher(ent, runThink);
                break;

            case MoveType.Follow:
                // SV_Physics_Follow (sv_phys.c:2361): glue to the aiment, then run the due think.
                ctx.PhysicsFollow(ent);
                runThink(ent);
                break;

            case MoveType.None:
                // MOVETYPE_NONE only runs a think (inlined thinktime check in DP).
                if (ent.NextThink > 0f && ent.NextThink <= ctx.Time + ctx.FrameTime)
                    runThink(ent);
                break;

            case MoveType.Noclip:
                if (runThink(ent))
                {
                    // free flight, no collision (sv_phys.c:2710)
                    ent.Origin += ent.Velocity * ctx.FrameTime;
                    ent.Angles += ent.AVelocity * ctx.FrameTime;
                    ctx.LinkEdict(ent);
                }
                break;

            case MoveType.Step:
                // Base step.qc: _Movetype_Physics_Step IS a 1-line alias to _Movetype_Physics_Walk, so a
                // MOVETYPE_STEP body gets the full Walk slide + explicit stair stepping (not a freefall-only
                // path). Mirror the Walk dispatch: due think first (gated), then the Walk integrator, which
                // does its own CheckWater/CheckWaterTransition-equivalent gravity gating.
                if (runThink(ent))
                {
                    WalkMove(ctx, ent);
                    // SV_CheckWaterTransition after the step (watertype/waterlevel + splash event); DP's
                    // SV_Physics_Step ran this and the QC Walk path does the transition via CheckWater.
                    CheckWaterTransition(ctx, ent);
                }
                break;

            case MoveType.Walk:
                if (runThink(ent))
                    WalkMove(ctx, ent);
                break;

            case MoveType.Toss:
            case MoveType.Bounce:
            case MoveType.BounceMissile:
            case MoveType.FlyMissile:
            case MoveType.Fly:
            case MoveType.FlyWorldOnly:
                if (runThink(ent))
                    PhysicsToss(ctx, ent);
                break;

            default:
                // unknown / user movetypes: just think
                runThink(ent);
                break;
        }
    }

    // =============================================================================================
    // SV_Physics_Toss (sv_phys.c:2459) — toss/bounce/fly ballistic movement with ClipVelocity bounce.
    // =============================================================================================

    // sv_gameplayfix_noairborncorpse: a corpse resting on a mover keeps resting (and falls when the
    // mover leaves) instead of immediately free-falling. Xonotic runs with this ON. Tracked per-entity
    // (engine-side, off Entity) so a corpse "suspended in air" by a now-freed mover is handled like DP.
    private const bool NoAirborneCorpse = true;
    private static readonly HashSet<Entity> _suspendedInAir = new();

    public static void PhysicsToss(PhysicsContext ctx, Entity ent)
    {
        // if onground, do nothing (unless moving upward off the ground, or the mover slid out from under)
        if ((ent.Flags & EntFlags.OnGround) != 0)
        {
            Entity? ground = ent.GroundEntity;
            if (ent.Velocity.Z >= 1f / 32f)
            {
                // moving upward: leave the ground (sv_gameplayfix_upwardvelocityclearsongroundflag)
                ent.Flags &= ~EntFlags.OnGround;
            }
            else if (ground == null || !NoAirborneCorpse)
            {
                // groundentity is world (never moves) → trust FL_ONGROUND and stay put.
                return;
            }
            else if (_suspendedInAir.Contains(ent) && ground.IsFreed)
            {
                // the mover we rested on was freed: drop to world ground (suspended), keep resting.
                ent.GroundEntity = null;
                return;
            }
            else if (!ground.IsFreed && BoxesOverlap(ent.AbsMin, ent.AbsMax, ground.AbsMin, ground.AbsMax))
            {
                // still touching the (non-world) mover → don't slide.
                return;
            }
            // otherwise: the mover moved away — fall through and re-simulate (we'll free-fall).
        }
        _suspendedInAir.Remove(ent);

        CheckVelocity(ent);

        // gravity (Toss/Bounce only; Fly/Missile have none)
        if (ent.MoveType == MoveType.Toss || ent.MoveType == MoveType.Bounce)
            ent.Velocity.Z -= ctx.EntGravity(ent);

        // angular
        ent.Angles += ent.AVelocity * ctx.FrameTime;

        float moveTime = ctx.FrameTime;
        for (int bump = 0; bump < FlyMove.MaxClipPlanes && moveTime > 0f; bump++)
        {
            Vector3 move = ent.Velocity * moveTime;
            // Fly traps may move while stuck (DP passes checkstuck=false for FLY); our PushEntity
            // doesn't unstick yet, so doTouch is the only difference and we keep it on.
            if (!ctx.PushEntity(out TraceResult trace, ent, move, doTouch: true))
                return; // teleported
            if (ent.IsFreed) return;
            if (trace.Fraction == 1f) break;

            moveTime *= 1f - MathF.Min(1f, trace.Fraction);

            switch (ent.MoveType)
            {
                case MoveType.BounceMissile:
                {
                    // per-entity .bouncefactor (0 = engine default 1.0 for missiles); QC (!bouncefactor)?1.0:..
                    float bf = ent.BounceFactor == 0f ? 1f : ent.BounceFactor;
                    ent.Velocity = Clip.ClipVelocity(ent.Velocity, trace.PlaneNormal, 1f + bf);
                    ent.Flags &= ~EntFlags.OnGround;
                    moveTime = 0f; // no slide unless sv_gameplayfix_slidemoveprojectiles
                    break;
                }
                case MoveType.Bounce:
                {
                    // per-entity .bouncefactor/.bouncestop (0 = engine defaults 0.5 / 60/800).
                    float bf = ent.BounceFactor == 0f ? 0.5f : ent.BounceFactor;
                    float bouncestop = ent.BounceStop == 0f ? 60f / 800f : ent.BounceStop;
                    ent.Velocity = Clip.ClipVelocity(ent.Velocity, trace.PlaneNormal, 1f + bf);
                    float entGravity = ent.Gravity == 0f ? 1f : ent.Gravity;
                    // DP with grenadebouncedownslopes (Xonotic default 1) uses the SIGNED d = normal·velocity.
                    float d = Vector3.Dot(trace.PlaneNormal, ent.Velocity);
                    if (trace.PlaneNormal.Z > FloorNormalZ && d < ctx.Gravity * bouncestop * entGravity)
                    {
                        ent.Flags |= EntFlags.OnGround;
                        ent.GroundEntity = trace.Ent;
                        ent.Velocity = Vector3.Zero;
                        ent.AVelocity = Vector3.Zero;
                        moveTime = 0f;
                    }
                    else
                    {
                        ent.Flags &= ~EntFlags.OnGround;
                        moveTime = 0f;
                    }
                    break;
                }
                default: // Toss / Fly / FlyMissile / FlyWorldOnly
                {
                    ent.Velocity = Clip.ClipVelocity(ent.Velocity, trace.PlaneNormal, 1f);
                    if (trace.PlaneNormal.Z > FloorNormalZ)
                    {
                        ent.Flags |= EntFlags.OnGround;
                        ent.GroundEntity = trace.Ent;
                        ent.Velocity = Vector3.Zero;
                        ent.AVelocity = Vector3.Zero;
                        moveTime = 0f;
                    }
                    else
                    {
                        ent.Flags &= ~EntFlags.OnGround;
                        moveTime = 0f;
                    }
                    break;
                }
            }
        }

        // remember if we came to rest on a (non-world) mover so a later free of that mover suspends us.
        if ((ent.Flags & EntFlags.OnGround) != 0 && ent.GroundEntity != null)
            _suspendedInAir.Add(ent);

        // SV_CheckWaterTransition (sv_phys.c:2593) — watertype/waterlevel + splash event.
        CheckWaterTransition(ctx, ent);
    }

    // =============================================================================================
    // SV_CheckWaterTransition (sv_phys.c:2410) — update watertype/waterlevel from point contents and
    // emit a splash sound on a water surface crossing.
    // =============================================================================================

    public static void CheckWaterTransition(PhysicsContext ctx, Entity ent)
    {
        int superContents = ctx.Trace.PointContents(ent.Origin);
        int cont = NativeContentsFromSuper(superContents);

        if (ent.WaterType == 0)
        {
            // just spawned here: take the contents, waterlevel 1 if liquid (fixedcheckwatertransition path).
            // fall through to the assignment below.
        }
        else if ((ent.WaterType == (int)Contents.Water || ent.WaterType == (int)Contents.Slime)
                 != (cont == (int)Contents.Water || cont == (int)Contents.Slime))
        {
            // crossed a water/slime surface → splash (SV_StartSound watersplash).
            ctx.PlaySound(ent, SoundChannel.Auto, "misc/water_in.wav");
        }

        if (cont <= (int)Contents.Water) // CONTENTS_WATER (-3) or lower (slime/lava) == in liquid
        {
            ent.WaterType = cont;
            ent.WaterLevel = 1;
        }
        else
        {
            ent.WaterType = (int)Contents.Empty;
            ent.WaterLevel = 0;
        }
    }

    /// <summary>Mod_Q1BSP_NativeContentsFromSuperContents: collapse a SUPERCONTENTS mask to a CONTENTS_* value.</summary>
    private static int NativeContentsFromSuper(int superContents)
    {
        if ((superContents & SuperContents.Lava) != 0) return (int)Contents.Lava;
        if ((superContents & SuperContents.Slime) != 0) return (int)Contents.Slime;
        if ((superContents & SuperContents.Water) != 0) return (int)Contents.Water;
        if ((superContents & SuperContents.Sky) != 0) return (int)Contents.Sky;
        if ((superContents & SuperContents.Solid) != 0) return (int)Contents.Solid;
        return (int)Contents.Empty;
    }

    private static bool BoxesOverlap(Vector3 amin, Vector3 amax, Vector3 bmin, Vector3 bmax)
        => CollisionWorld.BoxesOverlap(amin, amax, bmin, bmax);

    // =============================================================================================
    // SV_Physics_Step (sv_phys.c:2615) — DP-engine freefall-only step integrator.
    //
    // NO LONGER on the live dispatch path: Base's QC step.qc makes _Movetype_Physics_Step a 1-line alias
    // to _Movetype_Physics_Walk, so MOVETYPE_STEP now routes through WalkMove (full slide + stair stepping)
    // in RunEntity for parity. This method is retained as the DP-engine freefall reference (fly/swim guard,
    // upward-velocity unstick, land cue) and may still be called directly by a freefall-only body path.
    // =============================================================================================

    public static void PhysicsStep(PhysicsContext ctx, Entity ent)
    {
        EntFlags flags = ent.Flags;

        // fly/swim entities don't fall
        if ((flags & (EntFlags.Fly | EntFlags.Swim)) != 0)
            return;

        if ((flags & EntFlags.OnGround) != 0)
        {
            // freefall only if onground but moving upward (e.g. a lift pulled out)
            if (ent.Velocity.Z >= 1f / 32f)
            {
                ent.Flags &= ~EntFlags.OnGround;
                CheckVelocity(ent);
                FlyMove.Run(ctx, ent, ctx.FrameTime, applyGravity: true, out _, stepHeight: 0f);
                ctx.LinkEdict(ent);
            }
        }
        else
        {
            // freefall (entered this branch => was NOT onground)
            CheckVelocity(ent);
            int clip = FlyMove.Run(ctx, ent, ctx.FrameTime, applyGravity: true, out _, stepHeight: 0f);
            ctx.LinkEdict(ent);

            // land event: a freefalling step entity just made first ground contact (sv_phys.c:2653).
            // DP plays the entity's "land" sound on the transition to onground; emit it as a sound event.
            if ((clip & 1) != 0)
                ctx.PlaySound(ent, SoundChannel.Body, "misc/hitground.wav");
        }
    }

    // =============================================================================================
    // SV_WalkMove (sv_phys.c:2152) — slide move + stair stepping. Used by monster MOVETYPE_WALK.
    // =============================================================================================

    // SV_WalkMove gameplay-fix defaults (DP cvar defaults): jumpstep on, stepdown on, wallfriction a no-op,
    // nostep off. Match DP so monster stepping feels identical.
    private const bool JumpStep = true;       // sv_jumpstep
    private const int StepDown = 2;           // sv_gameplayfix_stepdown (Xonotic physicsX.cfg; ==2 glues to descending stairs)
    private const float StepDownMaxSpeed = 400f; // sv_gameplayfix_stepdown_maxspeed (skip down-step above this speed)
    private const bool WallFriction = false;  // sv_wallfriction (DP engine default 1, but the SV_WallFriction body is #if 0 — a no-op regardless)
    private const bool NoStep = false;        // sv_nostep
    private const bool DownTraceOnGround = true; // sv_gameplayfix_downtracesupportsongroundflag

    public static void WalkMove(PhysicsContext ctx, Entity ent)
    {
        if (ctx.FrameTime <= 0f) return;

        // applygravity unless in water / waterjump. Base walk.qc:12 gates on WALK || STEP
        // (MOVETYPE_STEP aliases to _Movetype_Physics_Walk, so STEP bodies must get gravity too).
        bool inWater = CheckWater(ctx, ent);
        bool applyGravity = !inWater && (ent.MoveType == MoveType.Walk || ent.MoveType == MoveType.Step) && (ent.Flags & EntFlags.WaterJump) == 0;

        CheckVelocity(ent);

        bool oldOnGround = (ent.Flags & EntFlags.OnGround) != 0;
        Vector3 startOrigin = ent.Origin;
        Vector3 startVelocity = ent.Velocity;

        // primary slide move with in-loop stepping (sv_gameplayfix_stepmultipletimes default behavior)
        int clip = FlyMove.Run(ctx, ent, ctx.FrameTime, applyGravity, out Vector3 stepNormal, stepHeight: StepHeight);

        // DOWNTRACEONGROUND: probe straight down to re-acquire a floor the slide missed (sv_phys.c:2185).
        if (DownTraceOnGround && (clip & 1) == 0)
        {
            Vector3 up = ent.Origin + new Vector3(0f, 0f, 1f);
            Vector3 down = ent.Origin - new Vector3(0f, 0f, 1f);
            MoveFilter type = ent.Solid is Solid.Trigger or Solid.Not ? MoveFilter.NoMonsters : MoveFilter.Normal;
            TraceResult dtr = ctx.Trace.Trace(up, ent.Mins, ent.Maxs, down, type, ent);
            if (dtr.Fraction < 1f && dtr.PlaneNormal.Z > FloorNormalZ)
            {
                clip |= 1;
                ent.GroundEntity = dtr.Ent;
            }
        }

        // if the move never touched ground, clear ONGROUND
        if ((clip & 1) == 0)
            ent.Flags &= ~EntFlags.OnGround;

        CheckVelocity(ent);
        ctx.LinkEdict(ent);
        ctx.TouchAreaGrid(ent);

        if ((clip & 8) != 0) return;                      // teleported
        if ((ent.Flags & EntFlags.WaterJump) != 0) return;
        if (NoStep) return;

        Vector3 originalOrigin = ent.Origin;
        Vector3 originalVelocity = ent.Velocity;
        EntFlags originalFlags = ent.Flags;
        Entity? originalGround = ent.GroundEntity;

        if ((clip & 2) != 0)
        {
            // if not trying to move into the step, return
            if (MathF.Abs(startVelocity.X) < 0.03125f && MathF.Abs(startVelocity.Y) < 0.03125f)
                return;

            if (ent.MoveType != MoveType.Fly)
            {
                if (ent.MoveType != MoveType.Walk)
                    return; // gibbed by a trigger
                if (!JumpStep && !oldOnGround && ent.WaterLevel == 0)
                    return; // can't jump-step while airborne
            }

            // try moving up + forward to climb a step. Back to start pos first.
            ent.Origin = startOrigin;
            ent.Velocity = startVelocity;

            if (!ctx.PushEntity(out _, ent, new Vector3(0f, 0f, StepHeight), doTouch: true))
                return; // teleported on the up-step

            ent.Velocity = new Vector3(ent.Velocity.X, ent.Velocity.Y, 0f);
            clip = FlyMove.Run(ctx, ent, ctx.FrameTime, applyGravity, out stepNormal, stepHeight: 0f);
            ent.Velocity = new Vector3(ent.Velocity.X, ent.Velocity.Y, ent.Velocity.Z + startVelocity.Z);
            if ((clip & 8) != 0)
                return; // teleported on the forward move

            CheckVelocity(ent);
            ctx.LinkEdict(ent);
            ctx.TouchAreaGrid(ent);

            // no horizontal progress (float precision in the cliphull) → revert to the original move.
            if (clip != 0
                && MathF.Abs(originalOrigin.Y - ent.Origin.Y) < 0.03125f
                && MathF.Abs(originalOrigin.X - ent.Origin.X) < 0.03125f)
            {
                ent.Origin = originalOrigin;
                ent.Velocity = originalVelocity;
                ent.Flags = originalFlags;
                ent.GroundEntity = originalGround;
                return;
            }

            // extra wall friction by view angle (off by default; DP body is disabled).
            if ((clip & 2) != 0 && WallFriction)
            {
                // SV_WallFriction body is #if 0 in DP — intentionally a no-op here.
            }
        }
        // skip the down-move when stepdown is off / moving up / deep water / started offground / ended onground /
        // moving too fast (sv_gameplayfix_stepdown_maxspeed; no IS_ONSLICK modeling in the port so the !onslick
        // exception is always taken — a faithful skip above 400 u/s horizontal+vertical speed).
        else if (StepDown == 0 || ent.WaterLevel >= 3 || startVelocity.Z >= (1f / 32f)
                 || !oldOnGround || (ent.Flags & EntFlags.OnGround) != 0
                 || (StepDownMaxSpeed > 0f && startVelocity.Length() >= StepDownMaxSpeed))
        {
            return;
        }

        // move down to stay glued to descending stairs/slopes.
        Vector3 downmove = new(0f, 0f, -StepHeight + startVelocity.Z * ctx.FrameTime);
        if (!ctx.PushEntity(out TraceResult downTrace, ent, downmove, doTouch: true))
            return; // teleported on the down-step

        if (downTrace.Fraction < 1f && downTrace.PlaneNormal.Z > FloorNormalZ)
        {
            // good landing on a descending step. With sv_gameplayfix_stepdown == 2 (Xonotic default) Base
            // glues the entity to the stair: SET_ONGROUND + groundentity (walk.qc:171-175).
            if (StepDown == 2)
            {
                ent.Flags |= EntFlags.OnGround;
                ent.GroundEntity = downTrace.Ent;
            }
        }
        else
        {
            // didn't land on good ground: use the move without the step-up (avoids hopping up steep slopes).
            ent.Origin = originalOrigin;
            ent.Velocity = originalVelocity;
            ent.Flags = originalFlags;
            ent.GroundEntity = originalGround;
        }

        CheckVelocity(ent);
        ctx.LinkEdict(ent);
        ctx.TouchAreaGrid(ent);
        _ = stepNormal;
    }

    /// <summary>
    /// SV_CheckWater (movetypes): set <see cref="Entity.WaterLevel"/>/<see cref="Entity.WaterType"/> from
    /// the entity's hull (feet/waist) and return true if SWIMMING (waterlevel > WETFEET). Used by WalkMove
    /// to skip gravity while submerged. Distilled from DP SV_CheckWater (a 3-point hull probe).
    /// Note: Base returns <c>waterlevel > 1</c> (>= SWIMMING), NOT just wet-feet, so a Walk/Step entity
    /// wading with only its feet wet still gets gravity.
    /// </summary>
    public static bool CheckWater(PhysicsContext ctx, Entity ent)
    {
        Vector3 point = ent.Origin;
        point.Z = ent.Origin.Z + ent.Mins.Z + 1f;
        int cont = ctx.Trace.PointContents(point);

        ent.WaterLevel = 0;
        ent.WaterType = (int)Contents.Empty;
        if ((cont & SuperContents.LiquidsMask) != 0)
        {
            ent.WaterType = NativeContentsFromSuper(cont);
            ent.WaterLevel = 1;
            point.Z = ent.Origin.Z + (ent.Mins.Z + ent.Maxs.Z) * 0.5f;
            if ((ctx.Trace.PointContents(point) & SuperContents.LiquidsMask) != 0)
            {
                ent.WaterLevel = 2;
                point.Z = ent.Origin.Z + ent.ViewOfs.Z;
                if ((ctx.Trace.PointContents(point) & SuperContents.LiquidsMask) != 0)
                    ent.WaterLevel = 3;
            }
        }
        // Base SV_CheckWater returns `waterlevel > WATERLEVEL_WETFEET` (>= SWIMMING), not wet-feet.
        return ent.WaterLevel > 1;
    }

    // =============================================================================================
    // SV_CheckVelocity (sv_phys.c:965) — NaN scrub + max-velocity clamp.
    // =============================================================================================

    public const float MaxVelocity = 2000f; // sv_maxvelocity default

    public static void CheckVelocity(Entity ent)
    {
        Vector3 v = ent.Velocity;
        if (float.IsNaN(v.X)) v.X = 0f;
        if (float.IsNaN(v.Y)) v.Y = 0f;
        if (float.IsNaN(v.Z)) v.Z = 0f;
        Vector3 o = ent.Origin;
        if (float.IsNaN(o.X)) o.X = 0f;
        if (float.IsNaN(o.Y)) o.Y = 0f;
        if (float.IsNaN(o.Z)) o.Z = 0f;
        ent.Origin = o;

        // denormal scrub
        if (v.LengthSquared() < 0.0000001f)
            v = Vector3.Zero;

        // clamp to max speed (DP scales the whole vector by maxvel/len when over)
        float speed2 = v.LengthSquared();
        if (speed2 > MaxVelocity * MaxVelocity)
            v *= MaxVelocity / MathF.Sqrt(speed2);

        ent.Velocity = v;
    }
}
