using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;

namespace XonoticGodot.Engine.Simulation;

/// <summary>
/// SV_LinkEdict_TouchAreaGrid (Base/darkplaces/sv_phys.c:727) — the "touch every overlapping trigger" pass.
///
/// After an entity moves, the engine fires the <c>.touch</c> of every SOLID_TRIGGER volume it now overlaps
/// (jump-pads, teleporters, trigger_hurt/heal/gravity, …). These volumes are non-solid to a movement sweep,
/// so they are NOT caught by the slide-move's collision touch — QC drives them with this separate area-grid
/// pass (SV_TouchTriggers). Both the per-tick simulation (<see cref="PhysicsContext.TouchAreaGrid"/>) and a
/// host that drives a player outside the sim loop (the walkable demo) run the SAME logic here so behavior is
/// identical: the launch a server computes is the launch the local player feels.
/// </summary>
public static class TriggerTouch
{
    /// <summary>
    /// Fire the <c>.touch</c> of every SOLID_TRIGGER in <paramref name="world"/> whose box overlaps
    /// <paramref name="mover"/>'s (with DP's 1-unit areagrid expansion). The trigger is <c>self</c> and the
    /// mover is <c>other</c>, matching SV_LinkEdict_TouchAreaGrid_Call's "fresh contact". A SOLID_NOT mover
    /// never triggers touches (DP early-out); the mover's <see cref="Entity.AbsMin"/>/<see cref="Entity.AbsMax"/>
    /// must be current (call after relinking).
    /// </summary>
    public static void Run(IReadOnlyList<Entity> world, Entity mover)
    {
        if (mover.IsFreed || mover.Solid == Solid.Not)
            return;

        // DP expands the areagrid link bounds by 1 unit (movement is clipped an epsilon from edges).
        Vector3 areaMin = mover.AbsMin - Vector3.One;
        Vector3 areaMax = mover.AbsMax + Vector3.One;

        // snapshot the count first (a touch can spawn/free entities mid-iteration).
        int n = world.Count;
        for (int i = 0; i < n; i++)
        {
            Entity touch = world[i];
            if (touch == mover || touch.IsFreed) continue;
            if (touch.Solid != Solid.Trigger || touch.Touch == null) continue;
            if (!CollisionWorld.BoxesOverlap(areaMin, areaMax, touch.AbsMin, touch.AbsMax)) continue;

            // self = trigger, other = mover (SV_LinkEdict_TouchAreaGrid_Call).
            touch.Touch(touch, mover);
            if (mover.IsFreed)
                return;
        }
    }

    /// <summary>
    /// Convenience for a host driving an entity OUTSIDE the sim loop (e.g. the demo's local player): run the
    /// touch-triggers pass against the ambient engine entity table. No-op if no <see cref="EngineServices"/>
    /// is installed as <see cref="Api.Services"/>. Relink <paramref name="mover"/> before calling so its
    /// AbsMin/AbsMax reflect its post-move position.
    /// </summary>
    public static void RunAmbient(Entity mover)
    {
        if (Api.Services is EngineServices es)
            Run(es.EntityTable.All, mover);
    }

    /// <summary>
    /// CLIENT-PREDICTION jump-pad pass — the local-player movement predictor's analogue of the server's
    /// post-move <see cref="Run"/>. Applies the launch velocity of every <c>trigger_push</c> /
    /// <c>trigger_push_velocity</c> volume <paramref name="mover"/> overlaps, in PREDICTED mode (velocity +
    /// UNSET_ONGROUND only; no sound / SUB_UseTargets / PUSH_ONCE removal). Faithful to Xonotic, whose
    /// <c>jumppad_push</c> runs on CSQC too — so the predicted local player feels a pad in lockstep with the
    /// server's authoritative launch, closing the predict/authority desync that otherwise jitters the camera
    /// (the "jump through the floor / bounce" felt when standing on a pad).
    ///
    /// Resolves pads through <see cref="Api"/>.Entities.FindByClass so it works whether the ambient facade is
    /// the raw EngineServices (demo) or the listen server's ServerServices wrapper — a plain
    /// <c>Api.Services is EngineServices</c> cast would FALSE-out on the wrapper and silently skip prediction.
    ///
    /// S5 (sv_threaded) thread note: this runs on the MAIN thread inside the client-prediction replay
    /// (EntityMovementStep.Step), so its <see cref="Api"/>.Entities.FindByClass scan walks whatever facade is
    /// ambient on the main thread. With the lock fallback (one shared world) it walks the live server table; the
    /// host serialises this whole prediction step against the server-sim worker's ServerNet.Tick via a single
    /// _simGate lock (taken only when sv_threaded 1), so the FindByClass iterator never races a concurrent
    /// spawn/remove on the worker thread. With sv_threaded 0 (default) it runs single-threaded as today.
    /// Two further faithful differences from <see cref="Run"/>: it does NOT early-out on a SOLID_NOT mover (the
    /// prediction carrier is deliberately SOLID_NOT, so the listen-server authority never collides with the
    /// ghost, yet it must still feel pads), and it touches ONLY jump-pads — teleport/hurt/conveyor stay
    /// server-authoritative and are corrected on reconcile, exactly the subset CSQC predicts.
    /// </summary>
    public static void PredictJumppadsAmbient(Entity mover)
    {
        if (Api.Services is null || mover.IsFreed)
            return;

        // Box from the mover's CURRENT origin (no relink dependency) + DP's 1-unit areagrid expansion.
        Vector3 areaMin = (mover.Origin + mover.Mins) - Vector3.One;
        Vector3 areaMax = (mover.Origin + mover.Maxs) + Vector3.One;

        PredictPadList(Api.Entities.FindByClass("trigger_push"), mover, areaMin, areaMax, isVelocityPad: false);
        PredictPadList(Api.Entities.FindByClass("trigger_push_velocity"), mover, areaMin, areaMax, isVelocityPad: true);
    }

    /// <summary>Apply the predicted launch of each candidate pad the mover's (expanded) box overlaps.</summary>
    private static void PredictPadList(IEnumerable<Entity> pads, Entity mover, Vector3 areaMin, Vector3 areaMax, bool isVelocityPad)
    {
        foreach (Entity t in pads)
        {
            if (ReferenceEquals(t, mover) || t.IsFreed || t.Solid != Solid.Trigger)
                continue;
            if (t.Active == XonoticGodot.Common.Gameplay.MapMover.ActiveNot)
                continue;
            // Mirror the server touch's gating EXACTLY or team pads desync (server launches, client doesn't):
            // a velocity pad is team-gated by a simple DIFF_TEAM (PushVelocityTouch); ballistic trigger_push is
            // NOT team-gated in PushTouch, so don't gate it here either.
            if (isVelocityPad && t.Team != 0f && mover.Team != t.Team)
                continue;
            if (!CollisionWorld.BoxesOverlap(areaMin, areaMax, t.AbsMin, t.AbsMax))
                continue;

            XonoticGodot.Common.Gameplay.Jumppads.JumppadPush(t, mover, isVelocityPad, predicted: true);
        }
    }

    /// <summary>
    /// CLIENT-PREDICTION teleporter pass — the local-player predictor's analogue of the server's post-move
    /// <see cref="Run"/> for <c>trigger_teleport</c>. Relocates <paramref name="mover"/> to the destination of
    /// any single-destination teleporter it overlaps (origin + speed reprojected along the dest facing +
    /// UNSET_ONGROUND + the <c>.fixangle</c> view-snap signal), in PREDICTED mode — NONE of the server-only side
    /// effects (sound, telefrag, SUB_UseTargets, kill-credit). Faithful to Xonotic, whose Teleport_Touch runs on
    /// CSQC too: predicting the relocation keeps the local origin in lockstep with authority (so a teleport no
    /// longer rubber-bands the camera through the <c>SetPredictionError</c> snap), and the <c>FixAngle</c> it
    /// stamps lets the host snap the local view to the exit facing immediately instead of a round-trip later.
    ///
    /// Only single-destination teleporters (<c>teleporter.enemy</c>) are predicted: a multi-dest teleporter picks
    /// a random destination, which can't be predicted deterministically (CSQC skips it too), so those stay
    /// server-authoritative and are corrected on reconcile. Mirrors <see cref="PredictJumppadsAmbient"/>: resolve
    /// via <see cref="Api"/>.Entities.FindByClass (works on the demo facade AND the listen-server wrapper) and do
    /// NOT early-out on a SOLID_NOT mover (the prediction carrier is deliberately non-solid). Fires at most one
    /// teleport per call — a relocation moves the mover off the (fixed) overlap box, so we return after it.
    /// </summary>
    public static void PredictTeleportsAmbient(Entity mover)
    {
        if (Api.Services is null || mover.IsFreed)
            return;

        Vector3 areaMin = (mover.Origin + mover.Mins) - Vector3.One;
        Vector3 areaMax = (mover.Origin + mover.Maxs) + Vector3.One;

        foreach (Entity t in Api.Entities.FindByClass("trigger_teleport"))
        {
            if (ReferenceEquals(t, mover) || t.IsFreed || t.Solid != Solid.Trigger)
                continue;
            if (t.Active != XonoticGodot.Common.Gameplay.MapMover.ActiveActive)
                continue;
            // OBSERVERS_ONLY teleporters never relocate a live player (Teleport_Active).
            if ((t.SpawnFlags & XonoticGodot.Common.Gameplay.Teleporters.ObserversOnly) != 0)
                continue;
            // Only a single cached destination is deterministic enough to predict (see SimpleTeleportPlayer).
            if (t.Enemy is null)
                continue;
            // Team-owned teleporter: only relocate a same-team mover (the common, non-INVERT_TEAMS case — matches
            // the velocity-pad gate in PredictJumppadsAmbient). Inverted-team teleporters fall back to authority.
            if (t.Team != 0f && mover.Team != t.Team)
                continue;
            if (!CollisionWorld.BoxesOverlap(areaMin, areaMax, t.AbsMin, t.AbsMax))
                continue;

            XonoticGodot.Common.Gameplay.Teleporters.SimpleTeleportPlayer(t, mover, predicted: true);
            return; // one teleport per tick — the mover has moved off this (fixed) overlap box
        }
    }

    /// <summary>
    /// CLIENT-PREDICTION warpzone pass — the local-player predictor's analogue of the server's post-move
    /// <see cref="Run"/> for <c>trigger_warpzone</c> (the seamless-portal teleport, lib/warpzone/server.qc
    /// WarpZone_Touch → WarpZone_Teleport). Warps <paramref name="mover"/> through any LINKED warpzone whose
    /// trigger box it now overlaps — origin/velocity/angles/avelocity rotated by the zone's seam transform, ground
    /// cleared, OldOrigin pinned to cancel interpolation, and the <c>.fixangle</c> view-snap signal stamped — in
    /// PREDICTED mode (NONE of the server-only side effects: no SUB_UseTargets target relays, no projectile/stuck
    /// recovery, no f0/f1 re-trace). Faithful to Xonotic, whose warpzone crossing runs on CSQC too
    /// (WarpZone_FixPMove transforms pmove_org/input_angles); predicting the crossing keeps the local origin and
    /// view in lockstep with authority so a warpzone no longer rubber-bands the camera through the server's
    /// post-touch correction snap, exactly the way <see cref="PredictTeleportsAmbient"/> closes that gap for
    /// trigger_teleport.
    ///
    /// Mirrors the other predictors: resolves zones through the published ambient
    /// <see cref="XonoticGodot.Common.Gameplay.WarpzoneTrace.AmbientManager"/> (the QC global g_warpzones,
    /// wired per-match by TraceService.SetWarpzoneManager) so it works on the demo facade AND the listen-server
    /// wrapper; does NOT early-out on a SOLID_NOT mover (the prediction carrier is deliberately non-solid); fires
    /// at most one warp per call (the warp moves the mover off the fixed overlap box). The crossing gate matches
    /// the authoritative <see cref="XonoticGodot.Common.Gameplay.WarpzoneManager.Teleport"/> EXACTLY (only warp
    /// when moving INTO the IN plane) so the predicted warp fires on the same tick the server's touch does.
    /// </summary>
    public static void PredictWarpzonesAmbient(Entity mover)
    {
        if (Api.Services is null || mover.IsFreed)
            return;

        var manager = XonoticGodot.Common.Gameplay.WarpzoneTrace.AmbientManager;
        if (manager is null)
            return;

        // Box from the mover's CURRENT origin (no relink dependency) + DP's 1-unit areagrid expansion, matching
        // the jumppad/teleport predictors.
        Vector3 areaMin = (mover.Origin + mover.Mins) - Vector3.One;
        Vector3 areaMax = (mover.Origin + mover.Maxs) + Vector3.One;

        foreach (XonoticGodot.Common.Gameplay.Warpzone wz in manager.Zones)
        {
            if (!wz.Linked || wz.Trigger is not { } trig)
                continue;
            if (ReferenceEquals(trig, mover) || trig.IsFreed || trig.Solid != Solid.Trigger)
                continue;
            if (!CollisionWorld.BoxesOverlap(areaMin, areaMax, trig.AbsMin, trig.AbsMax))
                continue;
            // QC WarpZone_PlaneDist gate (server.qc:193) — MUST match WarpzoneManager.Teleport EXACTLY or the
            // predicted warp fires on a different tick than the server's touch and the reconcile re-introduces the
            // snap we are trying to remove. PlaneDist = (origin + view_ofs - warpzone_origin)·warpzone_forward;
            // the entity is only warped once it is on the FAR (negative) side of the IN plane — i.e. it has
            // actually crossed the surface — regardless of velocity sign. This is the plane-side test, NOT a
            // velocity-direction gate (the old velocity gate diverged from the authoritative path).
            var t = wz.Transform;
            Vector3 point = mover.Origin + mover.ViewOfs;
            if (Vector3.Dot(point - t.InOrigin, t.InForward) >= 0f)
                continue; // not yet across the seam

            Vector3 newOrigin = t.TransformOrigin(mover.Origin);
            mover.Velocity = t.TransformVelocity(mover.Velocity);
            mover.AVelocity = t.TransformVelocity(mover.AVelocity);
            Vector3 newAngles = t.TransformAngles(mover.Angles);
            mover.Angles = newAngles;
            Api.Entities.SetOrigin(mover, newOrigin);
            mover.OldOrigin = newOrigin;            // QC: cancel interpolation across the seam (a teleport, not a slide)
            mover.Flags &= ~EntFlags.OnGround;      // QC UNSET_ONGROUND

            // QC player.fixangle = true: snap the local view to the rotated facing across the seam this tick
            // (the host reads FixAngle/FixAngleAngles off the carrier after the prediction step). This is the
            // PREDICTED snap; the server stamps its own AUTHORITATIVE fixangle in WarpzoneManager.Teleport, which
            // is what the listen host actually relies on (this one-shot flag is cleared by every replayed tick).
            mover.FixAngle = true;
            mover.FixAngleAngles = newAngles;
            return; // one warp per tick — the mover has moved off this (fixed) overlap box
        }
    }
}
