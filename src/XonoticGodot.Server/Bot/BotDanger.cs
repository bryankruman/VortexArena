// Port of server/bot/default/havocbot/havocbot.qc havocbot_checkdanger (:401-444)
// + the bot slice of tracebox_hits_trigger_hurt (server/bot/default/navigation.qh / t_swamp-style box test).
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Server.Bot;

/// <summary>
/// The "is the ground ahead of me deadly?" probe a bot runs every think while moving — the C# port of QC
/// <c>havocbot_checkdanger</c>. Looks from the eye toward a point ahead (<c>dst_ahead</c>), and if that line
/// is clear, traces 3000qu straight down from it and classifies what the bot would land on:
///
///   0 = safe; 1 = SKY below (a void fall); 2 = a drop &gt;100qu below both the feet and the goal (a cliff);
///   3 = LAVA or SLIME at the landing point; 4 = a <c>trigger_hurt</c> volume in the fall column.
///
/// The caller (<see cref="BotBrain"/>, mirroring havocbot.qc:1136-1182) treats 1-3 as "danger ahead" (brake),
/// and 4 as danger too unless the goal is above jump height — then the goal is unreachable (clear the route).
/// </summary>
public static class BotDanger
{
    /// <summary>QC <c>dst_down = dst_ahead - '0 0 3000'</c>: how far below the look-ahead point we probe.</summary>
    private const float DownProbe = 3000f;

    /// <summary>
    /// QC <c>havocbot_checkdanger(this, dst_ahead)</c>. <paramref name="eye"/> is origin + view_ofs;
    /// <paramref name="goalZ"/> the current goal's height (QC this.goalcurrent.origin.z);
    /// <paramref name="moving"/> stands in for QC's (AI_STATUS_RUNNING|AI_STATUS_ROAMING) "I'm under way"
    /// status bits; <paramref name="committed"/> for the jumppad/hardwired-link/JUMP-waypoint skips (the bot
    /// has deliberately committed to a flight it must not brake out of).
    /// </summary>
    public static int CheckDanger(Entity bot, Vector3 eye, Vector3 dstAhead, float goalZ,
        Vector3 mins, Vector3 maxs, bool onGround, bool jumpHeld, bool moving, bool committed)
    {
        Vector3 dstDown = dstAhead - new Vector3(0f, 0f, DownProbe);

        // QC traceline(origin + view_ofs, dst_ahead, true, NULL): only when the look-ahead line is CLEAR do we
        // ask what's below it (a wall ahead means no fall to worry about).
        var ahead = Api.Trace.Trace(eye, Vector3.Zero, Vector3.Zero, dstAhead, MoveFilter.NoMonsters, bot);
        if (ahead.Fraction < 1f || committed)
            return 0;
        if (!(onGround || moving || jumpHeld))
            return 0;

        // Look downwards (QC traceline(dst_ahead, dst_down, true, NULL)).
        var down = Api.Trace.Trace(dstAhead, Vector3.Zero, Vector3.Zero, dstDown, MoveFilter.NoMonsters, bot);
        float feetZ = bot.Origin.Z + mins.Z;
        if (down.EndPos.Z >= feetZ)
            return 0; // floor at/above foot level: walkable

        // 1) sky surface below = a void fall (QC trace_dphitq3surfaceflags & Q3SURFACEFLAG_SKY).
        if ((down.DpHitQ3SurfaceFlags & Q3SurfaceFlagSky) != 0)
            return 1;

        // 2) a drop more than 100qu below both the feet and the goal (QC min(feet, goalcurrent.origin.z) - 100).
        if (down.EndPos.Z < MathF.Min(feetZ, goalZ) - 100f)
            return 2;

        // 3) the landing point is in lava/slime (QC pointcontents(trace_endpos + '0 0 1')).
        int contents = Api.Trace.PointContents(down.EndPos + new Vector3(0f, 0f, 1f));
        if ((contents & Engine.Collision.SuperContents.Solid) == 0)
        {
            if ((contents & (Engine.Collision.SuperContents.Lava | Engine.Collision.SuperContents.Slime)) != 0)
                return 3;

            // 4) a trigger_hurt volume in the fall column (QC tracebox_hits_trigger_hurt(dst_ahead, mins, maxs,
            //    trace_endpos)). QC optimizes with a line test then a tracebox confirm; the port does ONE swept-
            //    AABB overlap of the fall column against each trigger_hurt's box — same accept set, no second
            //    trace (the tracebox in QC only tightens the endpoint, which the swept box already covers).
            if (HitsTriggerHurt(dstAhead, mins, maxs, down.EndPos))
                return 4;
        }
        return 0;
    }

    /// <summary>QC Q3SURFACEFLAG_SKY (matches WeaponFiring.Q3SurfaceFlagSky).</summary>
    private const int Q3SurfaceFlagSky = 0x4;

    /// <summary>
    /// QC <c>tracebox_hits_trigger_hurt(start, mins, maxs, end)</c>: does the box swept from
    /// <paramref name="start"/> to <paramref name="end"/> overlap any <c>trigger_hurt</c> volume? QC walks the
    /// g_hurttriggers list doing a box-vs-absbox overlap; the port finds them by classname (trigger_hurt brushes
    /// keep their edicts + brush bounds — MapObjectsRegistry registers the spawnfunc).
    /// </summary>
    public static bool HitsTriggerHurt(Vector3 start, Vector3 mins, Vector3 maxs, Vector3 end)
    {
        if (Api.Services is null)
            return false;
        Vector3 lo = Vector3.Min(start, end) + mins;
        Vector3 hi = Vector3.Max(start, end) + maxs;
        foreach (Entity e in Api.Entities.FindByClass("trigger_hurt"))
        {
            if (e.IsFreed) continue;
            if (e.AbsMin == e.AbsMax) continue; // unlinked/degenerate volume
            if (lo.X <= e.AbsMax.X && hi.X >= e.AbsMin.X
                && lo.Y <= e.AbsMax.Y && hi.Y >= e.AbsMin.Y
                && lo.Z <= e.AbsMax.Z && hi.Z >= e.AbsMin.Z)
                return true;
        }
        return false;
    }
}
