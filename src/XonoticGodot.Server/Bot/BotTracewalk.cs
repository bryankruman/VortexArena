using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Server.Bot;

/// <summary>
/// The bot reachability test — a faithful, Godot-free port of QuakeC's <c>tracewalk</c>
/// (server/bot/default/navigation.qc): "a rough simulation of walking from one point to another to test if a
/// path can be traveled", used for both waypoint auto-linking and the bot's straight-to-goal shortcut.
///
/// It steps a player-hull tracebox along the flat direction from start to end in 32-unit increments, and at
/// each step handles the three QC navigation actions:
///  - <b>WALK</b>: tracebox forward; on a wall, retry stepped up by <c>stepheight</c> (a stair) then by
///    <c>jumpstepheight</c> (a jumpable lip); on success, trace straight down to stand on the ground
///    (the Quake walkmove logic), so stairs and ledges are climbed/descended like a real player;
///  - <b>SWIM_ONWATER</b> / <b>SWIM_UNDERWATER</b>: step toward the (vertically-clamped) end, stepswimming
///    over obstacles and resurfacing when blocked, so water gaps are crossed.
///
/// It returns true once the walker arrives within 1 unit of the destination height (or anywhere in the
/// vertical band [end, end+endHeight] when a box waypoint is the target), false if it gets stuck. This is
/// the deep tracewalk the bot navigation TODO calls for — it replaces the old single straight hull sweep.
/// </summary>
public static class BotTracewalk
{
    // QC step constants (stepheightvec.z = sv_stepheight = 34; jumpstepheightvec adds a jump's worth).
    private const float StepHeight = 34f;
    private const float JumpStepHeight = 48f;  // QC jumpstepheightvec.z (stepheight + a small jump lift)
    private const float JumpHeight = 130f;     // QC jumpheight_vec.z (apparent jump apex)
    private const float StepDist = 32f;        // QC stepdist
    private const int MaxIterations = 256;     // safety cap (a long path is many 32u steps)

    private enum NavAction { Walk, SwimOnWater, SwimUnderwater }

    /// <summary>
    /// QC <c>tracewalk(e, start, m1, m2, end, end_height, movemode)</c>: can a player hull
    /// (<paramref name="mins"/>/<paramref name="maxs"/>) walk/step/swim from <paramref name="start"/> to
    /// <paramref name="end"/>? <paramref name="endHeight"/> &gt; 0 makes the destination the vertical segment
    /// [end, end + endHeight·z] (a box-waypoint target). Ignores <paramref name="ignore"/> in the traces.
    /// </summary>
    public static bool CanWalk(Vector3 start, Vector3 end, Vector3 mins, Vector3 maxs,
        float endHeight = 0f, Entity? ignore = null)
    {
        if (Api.Services is null)
            return true; // no collision world: optimistically reachable (offline graph build)

        // Bad start: the hull is stuck in solid where it begins.
        TraceResult t0 = Box(start, mins, maxs, start, ignore);
        if (t0.StartSolid)
            return false;

        Vector3 org = start;
        Vector3 flatDir = end - start;
        flatDir.Z = 0f;
        float flatDist = flatDir.Length();
        flatDir = flatDist > 0f ? flatDir / flatDist : Vector3.Zero;

        Vector3 end2 = end;
        if (endHeight > 0f) end2.Z += endHeight;
        Vector3 fixedEnd = end;

        var stepVec = new Vector3(0f, 0f, StepHeight);
        var jumpStepVec = new Vector3(0f, 0f, JumpStepHeight);
        var jumpVec = new Vector3(0f, 0f, JumpHeight);

        // Pick the initial nav action from the start's water state.
        NavAction action = WetFeet(org) ? (Submerged(org) ? NavAction.SwimUnderwater : NavAction.Walk) : NavAction.Walk;

        for (int iter = 0; iter < MaxIterations; iter++)
        {
            // --- arrival check (the flatdist<=0 block in QC) ---
            if (flatDist <= 0f)
            {
                bool success = true;
                if (org.Z > end2.Z + 1f)
                {
                    TraceResult t = Box(org, mins, maxs, end2, ignore);
                    org = t.EndPos;
                    if (org.Z > end2.Z + 1f) success = false;
                }
                else if (org.Z < end.Z - 1f)
                {
                    TraceResult t = Box(org, mins, maxs, org - jumpVec, ignore);
                    org = t.EndPos;
                    if (org.Z < end.Z - 1f) success = false;
                }
                if (success)
                    return true;
                if (flatDist <= 0f)
                    break; // can't advance further and not arrived
            }

            // compute the next step target.
            Vector3 move;
            if (action == NavAction.SwimUnderwater || (action == NavAction.SwimOnWater && org.Z > end2.Z))
            {
                fixedEnd.Z = Clamp(org.Z, end.Z, end2.Z);
                float seg = MathF.Min(StepDist, flatDist);
                if (seg >= flatDist) { move = fixedEnd; flatDist = 0f; }
                else
                {
                    move = org + (fixedEnd - org) * (StepDist / flatDist);
                    var rem = new Vector3(fixedEnd.X - move.X, fixedEnd.Y - move.Y, 0f);
                    flatDist = rem.Length();
                }
            }
            else
            {
                float seg = MathF.Min(StepDist, flatDist);
                flatDist -= seg;
                move = org + flatDir * seg;
            }

            // --- WALK ---
            if (action == NavAction.Walk)
            {
                TraceResult t = Box(org, mins, maxs, move, ignore);
                if (t.Fraction < 1f)
                {
                    // wall: try stepping up by stepheight (a stair).
                    TraceResult ts = Box(org + stepVec, mins, maxs, move + stepVec, ignore);
                    if (ts.Fraction < 1f || ts.StartSolid)
                    {
                        // try a bigger jumpstep lip.
                        TraceResult tj = Box(org + jumpStepVec, mins, maxs, move + jumpStepVec, ignore);
                        if (tj.Fraction < 1f && !tj.StartSolid)
                            return false; // genuinely blocked (no ladder/door handling in this slice)
                        move = tj.StartSolid ? ts.EndPos : tj.EndPos;
                    }
                    else move = ts.EndPos;
                }
                else move = t.EndPos;

                // stand on the ground: trace straight down as far as possible (QC walkmove logic).
                TraceResult down = Box(move, mins, maxs, move - new Vector3(0f, 0f, 65536f), ignore);
                org = down.EndPos;

                // entered water while walking? switch to swimming.
                if (WetFeet(org))
                    action = Submerged(org) ? NavAction.SwimUnderwater : NavAction.SwimOnWater;
                continue;
            }

            // --- SWIM (on/under water): step toward the clamped target, stepswim over small obstacles ---
            TraceResult sw = Box(org, mins, maxs, move, ignore);
            if (sw.Fraction < 1f)
            {
                TraceResult ss = Box(org + stepVec, mins, maxs, move + stepVec, ignore);
                if (ss.Fraction < 1f || ss.StartSolid)
                    return false; // can't jump the obstacle out of water
                org = ss.EndPos;
            }
            else org = sw.EndPos;

            // resolve the new water state after the swim step.
            action = WetFeet(org) ? (Submerged(org) ? NavAction.SwimUnderwater : NavAction.SwimOnWater)
                                   : NavAction.Walk;
            if (flatDist <= 0f && Approximately(org, end, end2))
                return true;
        }
        return false;
    }

    // ---- water helpers (QC WETFEET / SUBMERGED via PointContents) ----

    private static bool WetFeet(Vector3 org)
    {
        // QC WETFEET: the point a little above the feet is in water (pointcontents <= CONTENT_WATER).
        int c = Api.Trace.PointContents(org + new Vector3(0f, 0f, 1f));
        return IsWater(c);
    }

    private static bool Submerged(Vector3 org)
    {
        // QC SUBMERGED: the head (eye level) is in water too.
        int c = Api.Trace.PointContents(org + new Vector3(0f, 0f, 40f));
        return IsWater(c);
    }

    private static bool IsWater(int contents)
    {
        // Engine SUPERCONTENTS water bit OR the legacy CONTENT_WATER/SLIME/LAVA range.
        const int superContentsWater = 0x00000020; // SUPERCONTENTS_WATER
        const int superContentsLiquids = 0x00000020 | 0x00000010 | 0x00000008; // water|slime|lava
        if ((contents & superContentsLiquids) != 0) return true;
        return contents <= (int)Contents.Water && contents >= (int)Contents.Lava;
    }

    private static bool Approximately(Vector3 org, Vector3 end, Vector3 end2)
        => org.Z <= end2.Z + 1f && org.Z >= end.Z - 1f
           && new Vector3(end.X - org.X, end.Y - org.Y, 0f).LengthSquared() < 4f;

    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

    private static TraceResult Box(Vector3 from, Vector3 mins, Vector3 maxs, Vector3 to, Entity? ignore)
        => Api.Trace.Trace(from, mins, maxs, to, MoveFilter.NoMonsters, ignore);
}
