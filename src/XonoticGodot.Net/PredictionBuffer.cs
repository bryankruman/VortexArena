using System.Numerics;
using XonoticGodot.Common.Diagnostics;

namespace XonoticGodot.Net;

/// <summary>
/// Fixed-capacity ring buffer of sequence-numbered <see cref="InputCommand"/>s the client has issued
/// but the server has not yet acknowledged — the input-frame ring buffer called out in the networking
/// spec (analogue of the engine's <c>CL_MAX_USERCMDS</c> history that <c>getinputstate</c> walks in
/// <c>cl_player.qc</c>). Capacity is a power of two (mask indexing) and matches
/// <c>CL_MAX_USERCMDS = 128</c> by default.
///
/// Allocation discipline: one array allocated up front, commands stored by value, no per-frame
/// allocation. The client pushes one command per tick and sends a redundant tail of recent commands
/// (unreliable transport); the server dedups by <see cref="InputCommand.Seq"/>.
/// </summary>
public sealed class PredictionBuffer
{
    /// <summary>Must be a power of two and equal the engine's CL_MAX_USERCMDS (csqcmodel/common.qh).</summary>
    public const int Capacity = 128;
    private const int Mask = Capacity - 1;

    private readonly InputCommand[] _commands = new InputCommand[Capacity];

    /// <summary>Sequence number that will be assigned to the next pushed command (the client's
    /// <c>clientcommandframe</c> cursor). Starts at 1 so 0 reads as "no command".</summary>
    public uint NextSeq { get; private set; } = 1;

    /// <summary>The most recent sequence acknowledged by the server (its <c>servercommandframe</c>);
    /// commands with <c>Seq &lt;= AckedSeq</c> are confirmed and replayed-past.</summary>
    public uint AckedSeq { get; private set; }

    /// <summary>Stamp a command with the next sequence number, store it in the ring, and return the seq.
    /// Overwrites the oldest slot if the client outruns acks by more than <see cref="Capacity"/> frames
    /// (which only happens under extreme loss — those inputs are already lost to the server anyway).</summary>
    public uint Push(in InputCommand cmd)
    {
        uint seq = NextSeq++;
        InputCommand c = cmd;
        c.Seq = seq;
        _commands[seq & Mask] = c;
        return seq;
    }

    /// <summary>Record the server's ack of the last input it processed. Monotonic: stale/out-of-order acks
    /// (unreliable transport) are ignored.</summary>
    public void Acknowledge(uint ackedSeq)
    {
        if (ackedSeq > AckedSeq && ackedSeq < NextSeq) AckedSeq = ackedSeq;
    }

    /// <summary>Number of unacknowledged commands currently in flight (those that will be replayed).</summary>
    public int UnackedCount => (int)(NextSeq - 1 - AckedSeq);

    /// <summary>Fetch a stored command by sequence number. Returns false if <paramref name="seq"/> is out of
    /// range or has been overwritten by a newer command (ring wrap). The reconcile loop's
    /// <c>getinputstate(seq)</c>.</summary>
    public bool TryGet(uint seq, out InputCommand cmd)
    {
        if (seq == 0 || seq >= NextSeq || NextSeq - seq > Capacity)
        {
            cmd = default;
            return false;
        }
        cmd = _commands[seq & Mask];
        return cmd.Seq == seq; // guard against a wrapped slot whose seq no longer matches
    }

    /// <summary>Serialize the most recent <paramref name="redundancy"/> commands (oldest-first) into a C2S
    /// packet — the "redundant send" the spec mandates over unreliable transport, so a single dropped
    /// datagram doesn't strand an input. Writes a count byte then each command body.</summary>
    public void WriteRedundant(BitWriter w, int redundancy = 3)
    {
        uint newest = NextSeq - 1;
        if (newest == 0) { w.WriteByte(0); return; }

        int avail = (int)System.Math.Min((uint)redundancy, newest);
        w.WriteByte(avail);
        // oldest of the window first so the receiver applies them in order
        for (int i = avail - 1; i >= 0; i--)
        {
            uint seq = newest - (uint)i;
            if (TryGet(seq, out InputCommand c))
                c.Serialize(w);
        }
    }
}

/// <summary>
/// Plugs the authoritative movement integrator into the reconcile loop. Implemented later by
/// <c>XonoticGodot.Engine</c> (the shared fixed-tick movement step, ADR-0004); the predictor here is
/// movement-agnostic. Takes the state to advance and the input for one frame and mutates the state
/// in place — exactly what <c>CSQC_ClientMovement_PlayerMove_Frame</c> + <c>Movetype_Physics</c> do per
/// <c>CSQCPlayer_Physics</c>.
/// </summary>
public interface IMovementStep
{
    /// <summary>Advance <paramref name="state"/> by one input frame. Pure w.r.t. its inputs (determinism
    /// is a hard prerequisite — ADR-0010): same (state, cmd, vars) ⇒ same result on client and server.</summary>
    void Step(ref PredictedState state, in InputCommand cmd, in PlayerState vars);
}

/// <summary>
/// The minimal predicted local-player state the reconcile loop snapshots and replays — origin/velocity
/// plus the onground flag (cl_player.qc <c>csqcplayer_origin</c>/<c>csqcplayer_velocity</c>/
/// <c>player_pmflags</c>). A small mutable struct, copied by value.
/// </summary>
public struct PredictedState
{
    public Vector3 Origin;
    public Vector3 Velocity;
    public bool OnGround;
}

/// <summary>
/// The client-side predict-and-reconcile loop, ported in structure from
/// <c>csqcmodel/cl_player.qc</c> (<c>CSQCPlayer_Unpredict</c> / <c>CSQCPlayer_PredictTo</c> /
/// <c>CSQCPlayer_SetPredictionError</c>). The valuable reuse the spec/ADR-0005 highlight — Godot offers
/// no equivalent.
///
/// Flow each client frame, given a fresh authoritative state for sequence <c>ackedSeq</c>:
/// <list type="number">
///   <item><b>Unpredict</b> — reset local state to the server's authoritative values (the last acked frame).</item>
///   <item><b>Replay</b> — re-run <see cref="IMovementStep"/> for every unacked <see cref="InputCommand"/>
///         (ackedSeq+1 .. newest), arriving at a fresh prediction.</item>
///   <item><b>Measure error</b> — compare the new prediction at the acked frame against the previous one;
///         feed it to the error-decay smoother (<see cref="SetPredictionError"/>), unless it is huge
///         (teleport/jumppad) in which case ignore it.</item>
///   <item><b>Smooth</b> — render adds the decaying error offset so corrections are visually gradual.</item>
/// </list>
///
/// The actual movement maths is injected via <see cref="IMovementStep"/>; this type owns the
/// sequencing, error measurement and decay, and the render-side <b>stair-smoothing</b> Z blend (the port of
/// <c>CSQCModel_ApplyStairSmoothing</c> / <c>cl_stairsmoothing</c>): when a replayed step snaps the predicted
/// origin up/down a stair, <see cref="NoteStep"/> records the jump and <see cref="GetStairSmoothOffset"/>
/// returns a decaying Z offset the camera adds so the view glides over steps instead of teleporting. The
/// first-person view-bob / weapon-sway blend (<c>CSQCPlayer_ApplySmoothing</c>) remains a pure render concern.
/// </summary>
public sealed class Reconciler
{
    private readonly PredictionBuffer _input;
    private readonly IMovementStep _movement;

    // --- error-decay smoothing state (cl_player.qc csqcplayer_predictionerror{o,v,time,factor}) ---
    private Vector3 _errorOrigin;
    private Vector3 _errorVelocity;
    private float _errorUntilTime;     // csqcplayer_predictionerrortime
    private float _errorFactor;        // csqcplayer_predictionerrorfactor

    // --- stair-smoothing state (csqcmodel CSQCModel_ApplyStairSmoothing) ---
    // The rendered camera Z LAGS the live predicted Z and catches up at StairSmoothSpeed, hard-clamped to within
    // one step height of the live Z every frame (so it can NEVER persist an offset on a slope — the bug the old
    // delta-accumulating/time-window model had). Advanced per RENDER FRAME by the REAL clamped frame dt (NOT the
    // server-synced/rebasing render clock, whose 1/72-quantized jumps made the catch-up jitter), exactly like the
    // reference's `smoothtime = bound(0, time - prevtime, 0.1)`.
    private float _renderedZ;          // absolute rendered camera Z (the smoothed eye height)
    private bool _stairInit;           // false until the first frame seeds _renderedZ to the live Z

    /// <summary>The current predicted state after the last <see cref="Reconcile"/> — what the camera/render
    /// reads (before adding the smoothing offset).</summary>
    public PredictedState Predicted { get; private set; }

    /// <summary>cl_movement_errorcompensation: 0 disables smoothing (snap to truth). Mirrors the autocvar.</summary>
    public float ErrorCompensation = 0f;

    /// <summary>Server tick rate (Hz) used to scale the error-decay factor (QC <c>ticrate</c>).</summary>
    public float TickRate = 72f;

    /// <summary>cl_stairsmoothing ENABLE toggle: &gt;0 = stair smoothing on (the reference has no time window — the
    /// blend rate is <see cref="StairSmoothSpeed"/>); 0 = off (the camera follows the predicted Z exactly).</summary>
    public float StairSmoothTime = 1f;

    /// <summary>cl_stairsmoothspeed (Base default 200): the rate (u/s) at which the rendered Z catches up to the
    /// live predicted Z over a step. 0 disables the speed cap (instant follow).</summary>
    public float StairSmoothSpeed = 200f;

    // Thresholds from CSQCPlayer_SetPredictionError: errors larger than these are assumed to be a
    // teleport / jumppad / jump-timing disagreement and are ignored rather than smoothed.
    private const float MaxErrorOrigin = 32f;    // vdist(o, >, 32)
    private const float MaxErrorVelocity = 192f;  // vdist(v, >, 192)

    // A vertical jump in one replayed tick larger than this is a teleport / jumppad / fall, NOT a stair —
    // don't smooth it (QC: stairs are bounded by sv_stepheight ~ 34u; we allow a little slack).
    private const float MaxStairStep = 48f;

    public Reconciler(PredictionBuffer input, IMovementStep movement)
    {
        _input = input;
        _movement = movement;
    }

    /// <summary>
    /// Run one reconciliation: re-apply all unacked inputs on top of the authoritative
    /// <paramref name="serverState"/> (valid as of <paramref name="ackedSeq"/>), measure the prediction
    /// error against <paramref name="previousPredictionAtAck"/> (the state we had previously predicted for
    /// that same frame), and update the smoother. Returns the new predicted state.
    ///
    /// Mirrors <c>CSQCPlayer_PredictTo</c>: unpredict → replay; and the <c>CSQCPlayer_SetCamera</c> tail that
    /// calls <c>CSQCPlayer_SetPredictionError(e.origin - o, e.velocity - v0, …)</c>.
    /// </summary>
    public PredictedState Reconcile(
        in PredictedState serverState,
        uint ackedSeq,
        in PlayerState vars,
        float now,
        in PredictedState previousPredictionAtAck)
    {
        _input.Acknowledge(ackedSeq);

        // 1) Unpredict: start from the authoritative state.
        PredictedState s = serverState;

        // 2) Replay every unacked input (ackedSeq+1 .. newest), in order.
        uint newest = _input.NextSeq - 1;
        for (uint seq = ackedSeq + 1; seq <= newest; seq++)
        {
            if (!_input.TryGet(seq, out InputCommand cmd)) continue;
            _movement.Step(ref s, in cmd, in vars);
        }

        // 3) Measure the prediction error to smooth = the discontinuity AT THE RENDERED (newest) frame between
        //    what we were already showing and the freshly-corrected prediction. BOTH operands are at `newest`:
        //      previousPredictionAtAck = the OLD authoritative seed replayed to newest (captured before this
        //                                snapshot, i.e. the pose the camera was already rendering), and
        //      s                       = the NEW authoritative seed replayed to that SAME newest (just above).
        //    So this is ~0 whenever the corrected seed, replayed through the same inputs, reproduces the old
        //    prediction (deterministic agreement) and is nonzero ONLY by the genuine correction the new ack
        //    introduced — it does NOT scale with the player's travel across the in-flight window.
        //    Sign is (old − new) so UpdateCamera's `PredictedOrigin(=s, the new pose) + offset` starts at the
        //    OLD rendered position and glides to the new one (no pop).
        //
        //    HISTORY: the previous form `serverState − previousPredictionAtAck` diffed truth@ackedSeq against
        //    prediction@newest — two DIFFERENT timeline frames separated by the in-flight command depth — so even
        //    with a perfect predictor it injected ≈ −(displacement over the in-flight ticks) ≈ 5–15u every
        //    snapshot at running speed, a speed-proportional phantom correction (the "camera jumps while moving"
        //    bug). QC measures a same-frame residual (cl_player.qc CSQCPlayer_SetCamera: prediction@scf − truth@scf),
        //    ~0 when the sims agree; this restores that invariant.
        Vector3 oErr = previousPredictionAtAck.Origin - s.Origin;
        Vector3 vErr = previousPredictionAtAck.Velocity - s.Velocity;
        SetPredictionError(oErr, vErr, now);

        // Stair smoothing is no longer measured here per-tick (the old per-tick delta model perpetually re-armed
        // on slopes); it is driven per RENDER FRAME from the live predicted Z in GetStairSmoothOffset.
        Predicted = s;
        return s;
    }

    /// <summary>
    /// Replay-only prediction: re-run the unacked inputs (acked+1 .. newest) on top of the last authoritative
    /// state and refresh <see cref="Predicted"/>, WITHOUT re-measuring/arming the prediction-error smoother.
    /// Call this every client input tick after pushing the new command (so the local view reflects the latest
    /// input immediately); call <see cref="Reconcile"/> only when a fresh server snapshot/ack arrives (that is
    /// the moment the error is measured against what we had predicted). Stair smoothing IS updated here, since
    /// a step can be introduced by simply replaying one more input.
    ///
    /// This is the split QC makes between <c>CSQCPlayer_PredictTo</c> (run each frame) and the error capture
    /// in <c>CSQCPlayer_SetCamera</c> (only on a new server frame): keeping them separate is what lets the
    /// error decay smoothly between snapshots instead of being re-pinned to full magnitude every tick.
    /// </summary>
    public PredictedState Predict(in PredictedState serverState, uint ackedSeq, in PlayerState vars, float now)
    {
        _ = now; // stair smoothing now runs per-frame in GetStairSmoothOffset, not per replayed tick
        PredictedState s = serverState;
        uint newest = _input.NextSeq - 1;
        for (uint seq = ackedSeq + 1; seq <= newest; seq++)
        {
            if (!_input.TryGet(seq, out InputCommand cmd)) continue;
            _movement.Step(ref s, in cmd, in vars);
        }

        Predicted = s;
        return s;
    }

    /// <summary>
    /// Accumulate a prediction error into the decaying smoother, ignoring errors too large to be anything
    /// but a teleport/jumppad. Port of <c>CSQCPlayer_SetPredictionError</c>.
    /// </summary>
    public void SetPredictionError(Vector3 originError, Vector3 velocityError, float now)
    {
        // A position error too big to be anything but a TELEPORT / RESPAWN / spawn (origin discontinuity): SNAP,
        // and CLEAR any accumulated smoothing so the camera jumps cleanly to the destination instead of dragging
        // a stale offset that floats it away. This is the port's equivalent of csqcmodel's csqcmodel_teleported
        // clearing the view smoothing on a networked teleport (lib/csqcmodel/cl_player.qc) — QC gets that flag
        // from the server; we infer the teleport from the origin jump. Without this, the small per-snapshot error
        // the predictor accumulates while the local player is a FROZEN observer (the client predicts gravity, the
        // server holds it still) survives the observer→player spawn and floats the camera thousands of units up.
        if (originError.Length() > MaxErrorOrigin)
        {
            // Diagnostic: a position correction this large is a teleport/respawn — OR a genuine desync (the
            // client predicted somewhere the server didn't follow, e.g. a server-side stuck). Repeated lines
            // here while moving normally are the signature of a rubberband. Developer-gated (QC Con_DPrintf).
            if (Log.WillTrace)
                Log.Trace($"[reconcile] origin SNAP {originError.Length():0.0}u (>{MaxErrorOrigin}) — teleport/respawn or desync; smoothing reset");
            ResetError();
            return;
        }
        // A velocity-only spike with a small origin delta is a JUMPPAD / jump-time disagreement, NOT a teleport —
        // ignore it but DON'T reset (let any residual origin error keep decaying), faithful to QC's vdist(v,>,192).
        if (velocityError.Length() > MaxErrorVelocity)
        {
            if (Log.WillTrace)
                Log.Trace($"[reconcile] velocity spike {velocityError.Length():0}u/s (>{MaxErrorVelocity}) ignored (jumppad/jump-timing)");
            return;
        }

        if (ErrorCompensation == 0f)
        {
            _errorFactor = 0f;
            return;
        }

        // accumulate on top of any residual error still decaying, then (re)start the decay window.
        _errorOrigin = GetPredictionErrorOrigin(now) + originError;
        _errorVelocity = GetPredictionErrorVelocity(now) + velocityError;
        _errorFactor = ErrorCompensation / (TickRate > 0f ? TickRate : 1f);
        _errorUntilTime = now + 1f / _errorFactor;
    }

    /// <summary>Current decaying origin offset to add to the rendered position. Port of
    /// <c>CSQCPlayer_GetPredictionErrorO</c>: linearly decays to zero by <c>_errorUntilTime</c>.</summary>
    public Vector3 GetPredictionErrorOrigin(float now)
    {
        if (now >= _errorUntilTime) return Vector3.Zero;
        return _errorOrigin * ((_errorUntilTime - now) * _errorFactor);
    }

    /// <summary>Current decaying velocity offset. Port of <c>CSQCPlayer_GetPredictionErrorV</c>.</summary>
    public Vector3 GetPredictionErrorVelocity(float now)
    {
        if (now >= _errorUntilTime) return Vector3.Zero;
        return _errorVelocity * ((_errorUntilTime - now) * _errorFactor);
    }

    // ---------------------------------------------------------------------
    // Stair smoothing (csqcmodel CSQCModel_ApplyStairSmoothing / cl_stairsmoothing)
    // ---------------------------------------------------------------------

    /// <summary>
    /// The vertical offset to SUBTRACT from the predicted origin Z before rendering, so the camera glides over
    /// stair-steps instead of popping. Faithful port of <c>CSQCModel_ApplyStairSmoothing</c> (cl_player.qc): the
    /// rendered Z (<see cref="_renderedZ"/>) catches up to the LIVE predicted Z at <see cref="StairSmoothSpeed"/>
    /// u/s and is HARD-CLAMPED to within one step height of it every frame — so it can never lag the true Z by
    /// more than a step (the structural fix for the old delta-accumulating / time-window model that perpetually
    /// re-armed on any grounded Z change and pinned a persistent offset on slopes/ramps). Snaps (no smoothing)
    /// while airborne, so jumps/falls follow the real Z.
    ///
    /// <para>Advanced once per RENDER FRAME. <paramref name="frameDt"/> is the REAL wall-clock frame delta (NOT
    /// the server-synced render clock, whose snapshot-rebased 1/72-quantized jumps made the catch-up jitter up/
    /// down); it is clamped to <c>[0, 0.1]</c> exactly like the reference's <c>smoothtime</c>. A placement-only
    /// call (frameDt 0) returns the current offset without advancing. Callers do
    /// <c>renderZ = predicted.Z - GetStairSmoothOffset(frameDt)</c>, i.e. the camera sits at <c>_renderedZ</c>.</para>
    /// </summary>
    public float GetStairSmoothOffset(float frameDt)
    {
        float trueZ = Predicted.Origin.Z;

        // Disabled or first frame: (re)seed the rendered Z to the live Z, no offset.
        if (StairSmoothTime <= 0f || !_stairInit)
        {
            _renderedZ = trueZ;
            _stairInit = true;
            return 0f;
        }

        // Airborne (jump/fall) / teleport: no stair smoothing — the view follows the real Z (snap).
        if (!Predicted.OnGround)
        {
            _renderedZ = trueZ;
            return 0f;
        }

        // Slide the rendered Z toward the live true Z at StairSmoothSpeed (smoothtime = bound(0, dt, 0.1))...
        float smoothtime = frameDt < 0f ? 0f : (frameDt > 0.1f ? 0.1f : frameDt);
        float maxMove = (StairSmoothSpeed > 0f ? StairSmoothSpeed : float.MaxValue) * smoothtime;
        if (_renderedZ < trueZ) _renderedZ = MathF.Min(_renderedZ + maxMove, trueZ);
        else                    _renderedZ = MathF.Max(_renderedZ - maxMove, trueZ);
        // ...then HARD-CLAMP to one step height of the live Z (PHYS_STEPHEIGHT) so a slope can't pin an
        // ever-growing offset: the clamp re-pins the lag to <= one step every frame.
        _renderedZ = QMathClamp(_renderedZ, trueZ - MaxStairStep, trueZ + MaxStairStep);

        return trueZ - _renderedZ;
    }

    /// <summary>Reset all smoothing state (e.g. on respawn / hard teleport).</summary>
    public void ResetError()
    {
        _errorOrigin = _errorVelocity = Vector3.Zero;
        _errorUntilTime = 0f;
        // Re-seed stair smoothing to the live Z next frame (a teleport/respawn must not drag a stale lag).
        _stairInit = false;
        _errorFactor = 0f;
    }

    // Local clamp (keep this file leaf-level / Common-Math-free on the hot path, like Quantize).
    private static float QMathClamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
}
