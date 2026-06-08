using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Physics;
using XonoticGodot.Net;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Net;

/// <summary>
/// Bridges the prediction loop's abstract <see cref="IMovementStep"/> to the shared, deterministic
/// <see cref="Movement"/> sim (QC PM_Main) by driving a real <see cref="Entity"/>. The
/// <see cref="Reconciler"/> hands us a <see cref="PredictedState"/> (origin/velocity/onground) and one
/// <see cref="InputCommand"/>; we load that onto the carrier entity, run one authoritative movement tick
/// against the same <see cref="XonoticGodot.Engine.Collision.CollisionWorld"/> the server uses, and read the
/// result back. Because it is the SAME sim the server runs, client prediction and server authority stay in
/// lockstep (ADR-0010 determinism), which is what keeps reconciliation corrections tiny.
///
/// The carrier entity is the client's local player entity (so its hull/flags/state persist across ticks);
/// pass the one <see cref="PlayerController"/> already spawned. The engine services / collision world must be
/// booted (<see cref="GameInit.Boot"/>) before this runs — on a listen server that is already the case, and a
/// remote client boots the same stack over the loaded map's collision world.
/// </summary>
public sealed class EntityMovementStep : IMovementStep
{
    private readonly Entity _carrier;

    public EntityMovementStep(Entity carrier) => _carrier = carrier;

    public void Step(ref PredictedState state, in InputCommand cmd, in PlayerState vars)
    {
        // load the predicted state onto the carrier entity.
        _carrier.Origin = state.Origin;
        _carrier.Velocity = state.Velocity;
        SetOnGround(_carrier, state.OnGround);

        // Clear the per-tick teleport view-snap signal (QC .fixangle) before this tick's trigger passes. After the
        // full reconcile replay it reflects ONLY the newest replayed input, so it is true exactly on the tick the
        // local player crosses a teleporter — which is when NetGame snaps the view (one-shot, replay-safe).
        _carrier.FixAngle = false;

        // build the per-tick movement input from the command (the same conversion the server applies).
        InputButtons b = cmd.TypedButtons;
        // The InputCommand carries the wish-move normalized to ±1 (see NetGame.SampleInput); the move code must
        // rescale it to wish-velocity units. Darkplaces builds the usercmd from cl_forwardspeed/cl_sidespeed/
        // cl_upspeed (cl_input.c:502-515), NOT from the live sv_maxspeed — so scale by those fixed client speeds
        // (a remote client may lack the replicated maxspeed; PlayerPhysics then clamps wishspeed to live MaxSpeed,
        // WishDir2D + MathF.Min(wishspeed, mp.MaxSpeed)). Scaling by live maxspeed instead capped the true top
        // speed when maxspeed>360. CRITICAL: keep these identical to ServerNet.ToMovementInput so the client
        // predictor and the server converter stay in lockstep.
        var input = new MovementInput
        {
            ViewAngles = cmd.ViewAngles,
            MoveValues = WishMoveScaling.Scale(cmd.Forward, cmd.Side, cmd.Up),
            FrameTime = cmd.DeltaTime > 0f ? cmd.DeltaTime : XonoticGodot.Engine.Simulation.SimulationLoop.TicRate,
            ButtonJump = (b & InputButtons.Jump) != 0,
            ButtonCrouch = (b & InputButtons.Crouch) != 0,
            ButtonAttack1 = (b & InputButtons.Attack) != 0,
            ButtonAttack2 = (b & InputButtons.Attack2) != 0,
            ButtonUse = (b & InputButtons.Use) != 0,
        };

        // run one authoritative movement tick (gravity + friction/accel + slide/step collision).
        Movement.Move(_carrier, input);

        // Client-side jump-pad prediction (CSQC trigger_push): after the move, apply the launch of any jump-pad
        // the carrier now overlaps — exactly as the server does in its post-move TouchAreaGrid pass
        // (SimulationLoop) — so the predicted local player feels pads IN LOCKSTEP with authority. Without this
        // the server launches but the client predicts ordinary jump/fall, and reconciliation jitters the camera
        // (the "jump through the floor / bounce" felt on a pad). Velocity-only, no side effects (see TriggerTouch).
        XonoticGodot.Engine.Simulation.TriggerTouch.PredictJumppadsAmbient(_carrier);

        // Client-side teleporter prediction (CSQC Teleport_Touch): relocate + reproject the carrier through any
        // single-destination trigger_teleport it now overlaps, IN LOCKSTEP with the server's post-move teleport,
        // so a teleport doesn't rubber-band the camera (the reconcile would otherwise measure a teleport-sized
        // origin error and hard-snap). It also stamps the carrier's .fixangle so the host can snap the local view
        // to the exit facing this tick. Predicted mode = no sound/telefrag/targets (server-authoritative).
        XonoticGodot.Engine.Simulation.TriggerTouch.PredictTeleportsAmbient(_carrier);

        // read the result back into the predicted state.
        state.Origin = _carrier.Origin;
        state.Velocity = _carrier.Velocity;
        state.OnGround = _carrier.OnGround;
    }

    private static void SetOnGround(Entity e, bool onGround)
    {
        if (onGround) e.Flags |= EntFlags.OnGround;
        else e.Flags &= ~EntFlags.OnGround;
    }
}

/// <summary>
/// The Darkplaces client wish-move scaling (cl_input.c CL_Input): the normalized ±1 WASD axes are scaled to
/// wish-velocity units by the FIXED client input-speed cvars — forward/back by <c>cl_forwardspeed</c>/
/// <c>cl_backspeed</c> (400), strafe by <c>cl_sidespeed</c> (350), up/down by <c>cl_upspeed</c> (400) — NOT by
/// the live sv_maxspeed (a remote client may not hold the replicated value). The server physics then clamps the
/// resulting wishspeed to the live MaxSpeed. The client predictor (<see cref="EntityMovementStep"/>) and the
/// server converter (<see cref="ServerNet"/>) BOTH call this so prediction stays in lockstep with authority.
/// </summary>
internal static class WishMoveScaling
{
    /// <summary>cl_forwardspeed / cl_backspeed (Base/darkplaces/cl_input.c:365-366), default 400.</summary>
    public const float ForwardSpeed = 400f;

    /// <summary>cl_sidespeed (Base/darkplaces/cl_input.c:367), default 350.</summary>
    public const float SideSpeed = 350f;

    /// <summary>cl_upspeed (Base/darkplaces/cl_input.c:364), default 400.</summary>
    public const float UpSpeed = 400f;

    /// <summary>Scale a normalized (±1) wish-move into wish-velocity units (forward/side/up → DP cmd move).</summary>
    public static NVec3 Scale(float forward, float side, float up)
        => new(forward * ForwardSpeed, side * SideSpeed, up * UpSpeed);
}
