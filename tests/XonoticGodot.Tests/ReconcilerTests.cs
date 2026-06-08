using System.Numerics;
using XonoticGodot.Net;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Prediction-error smoother (<see cref="Reconciler.SetPredictionError"/>) tests — the view-smoothing half of
/// client-side reconciliation (port of csqcmodel <c>CSQCPlayer_SetPredictionError</c>).
///
/// A small in-bounds error arms a decaying offset that glides the camera onto the corrected position. But a
/// TELEPORT-sized origin jump (respawn / teleporter / the observer→spawn transition) must SNAP — and crucially
/// CLEAR any accumulated smoothing, or the offset the predictor built up (e.g. while a frozen observer, or
/// across a spawn fall) survives the teleport and floats the camera far from the player. A velocity-only spike
/// (jumppad / jump-time disagreement) is NOT a teleport and must be ignored WITHOUT clearing the residual
/// origin smoothing, faithful to QC's <c>vdist(o,&gt;,32) || vdist(v,&gt;,192)</c> split.
/// </summary>
public class ReconcilerTests
{
    private sealed class NoOpStep : IMovementStep
    {
        public void Step(ref PredictedState state, in InputCommand cmd, in PlayerState vars) { }
    }

    /// <summary>A perfectly deterministic step: advances origin by a fixed delta per tick (models a client and
    /// server whose sims agree exactly — so any measured prediction error must come from the MEASUREMENT, not the sim).</summary>
    private sealed class ConstantStep : IMovementStep
    {
        private readonly Vector3 _d;
        public ConstantStep(Vector3 d) { _d = d; }
        public void Step(ref PredictedState state, in InputCommand cmd, in PlayerState vars) => state.Origin += _d;
    }

    private static Reconciler NewReconciler() =>
        new(new PredictionBuffer(), new NoOpStep()) { ErrorCompensation = 100f, TickRate = 72f };

    [Fact]
    public void Reconcile_PerfectPrediction_WithInFlightCommands_InjectsNoError()
    {
        // The reconcile must measure the prediction error LIKE-FOR-LIKE (same timeline frame). When the client is
        // predicting ahead of the server (commands in flight) and the deterministic sims AGREE, the error fed to
        // the smoother must be ~0 — otherwise the camera is tugged by a phantom offset that scales with player
        // speed × in-flight depth every snapshot (the "view jumps while moving" bug). Regression guard for that.
        var buf = new PredictionBuffer();
        var rec = new Reconciler(buf, new ConstantStep(new Vector3(5f, 0f, 0f))) { ErrorCompensation = 1f, TickRate = 72f };

        // Client pushed 7 commands and predicted forward from origin@seq0 → predicted (35,0,0) at newest=7.
        for (int i = 0; i < 7; i++) buf.Push(new InputCommand { Forward = 1f });
        var seed0 = new PredictedState { Origin = Vector3.Zero, OnGround = true };
        rec.Predict(seed0, ackedSeq: 0, default, now: 0f);
        Assert.Equal(35f, rec.Predicted.Origin.X, 3);

        // A snapshot acks seq 3 (truth there = (15,0,0)); 4 commands are still in flight (a real prediction lead).
        // The old form diffed truth@3 (15) against prediction@newest (35) → a 20u phantom error (<32u, so it would
        // be smoothed, not discarded). The fixed form compares old-vs-new prediction at the same newest frame → 0.
        PredictedState prevAtNewest = rec.Predicted;
        var serverState = new PredictedState { Origin = new Vector3(15f, 0f, 0f), OnGround = true };
        rec.Reconcile(serverState, ackedSeq: 3, default, now: 0f, prevAtNewest);

        Assert.True(rec.GetPredictionErrorOrigin(0f).Length() < 1e-3f,
            $"perfect prediction with 4 in-flight commands must inject NO smoother error; got {rec.GetPredictionErrorOrigin(0f).Length():F3}");
        // The corrected prediction still lands at the right place (replay from truth@3 forward to newest).
        Assert.Equal(35f, rec.Predicted.Origin.X, 3);
    }

    [Fact]
    public void SetPredictionError_TeleportSizedOriginJump_SnapsAndClearsAccumulatedSmoothing()
    {
        Reconciler rec = NewReconciler();

        // A small in-bounds error arms the view smoother (a nonzero decaying offset).
        rec.SetPredictionError(new Vector3(0f, 0f, 5f), Vector3.Zero, now: 0f);
        Assert.True(rec.GetPredictionErrorOrigin(0f).Length() > 0.5f, "a small error should arm the smoother");

        // A teleport-sized ORIGIN jump (respawn / teleporter / observer→spawn) must SNAP: clear the accumulated
        // smoothing rather than fold the jump in or leave the old offset floating the view.
        rec.SetPredictionError(new Vector3(1000f, 0f, 0f), Vector3.Zero, now: 1f / 72f);
        Assert.True(rec.GetPredictionErrorOrigin(1f / 72f).Length() < 1e-3f, "a teleport must clear the smoother");
    }

    [Fact]
    public void SetPredictionError_VelocityOnlySpike_DoesNotClearResidualSmoothing()
    {
        Reconciler rec = NewReconciler();

        rec.SetPredictionError(new Vector3(0f, 0f, 5f), Vector3.Zero, now: 0f);
        float before = rec.GetPredictionErrorOrigin(0f).Length();
        Assert.True(before > 0.5f, "precondition: the smoother is armed");

        // A big VELOCITY-only spike (jumppad / jump-time disagreement) with a small origin delta is NOT a
        // teleport — ignore it (QC vdist(v,>,192)) and leave the residual origin smoothing intact (still decaying).
        rec.SetPredictionError(new Vector3(0f, 0f, 1f), new Vector3(0f, 0f, 500f), now: 0f);
        float after = rec.GetPredictionErrorOrigin(0f).Length();
        Assert.True(after >= before - 1e-3f, $"a velocity spike must not clear origin smoothing (before={before}, after={after})");
    }
}
