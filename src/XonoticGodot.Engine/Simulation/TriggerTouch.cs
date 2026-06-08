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
}
