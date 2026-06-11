using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Server.Bot;

/// <summary>
/// Per-gametype objective bot roles — the C# port of the havocbot_role_* / havocbot_goalrating_* objective
/// functions that live in each gametype's sv_*.qc (ctf/keyhunt/domination/onslaught/keepaway). Each role
/// rates the gametype's objective targets (the enemy flag, a dropped key, an unclaimed control point, the
/// enemy generator, the keepaway ball) alongside items and roam waypoints, then the brain routes to the best.
///
/// The objective <em>positions</em> come from two sources: the gametype singleton's own state (e.g. CTF's
/// <see cref="Ctf.Flags"/> for carriers/drops) and the spawned map-marker entities found by classname through
/// the (player-visible) entity table — control points (<c>dom_controlpoint</c>), generators
/// (<c>onslaught_generator</c>), flag bases (<c>item_flag_team*</c>) etc. A role with no resolvable objective
/// state gracefully falls back to <see cref="BotRoles.RoleGeneric"/> so the bot still plays.
/// </summary>
public static class BotObjectiveRoles
{
    // QC BOT_RATING_* (roles.qh) — objective goals weigh much higher than loose items.
    private const float RatingFlag = 30000f;
    private const float RatingControlPoint = 10000f;
    private const float RatingBall = 8000f;
    private const float RatingGenerator = 20000f;
    private const float RatingKey = 10000f;

    /// <summary>Scratch buffer reused across the objective FindByClass scans (alloc-free, replaces a per-call
    /// iterator). Bot roles run single-threaded under the strategy token and never re-enter, and every scan here
    /// is fully consumed (return-on-match / accumulate into the rater) before the next reuses it — so one shared
    /// static list is safe. Do NOT hold a reference to it across a second find.</summary>
    private static readonly List<Entity> _scratch = new();

    // ============================================================================================
    // Capture the Flag (QC havocbot_role_ctf_* + havocbot_goalrating_ctf_*)
    // ============================================================================================

    /// <summary>
    /// CTF role (QC the carrier/offense/defense/middle roles collapsed): if carrying the enemy flag, route to
    /// the team base to capture; otherwise route to the enemy flag (to grab) and to our own dropped/stolen
    /// flag (to return), plus items + roaming. A faithful behavioural collapse of the four QC CTF roles.
    /// </summary>
    public static void RoleCtf(BotBrain brain, GoalRater rater)
    {
        var bot = brain.Bot;
        rater.Start();

        if (brain.GameType is Ctf ctf)
        {
            int myTeam = (int)bot.Team;
            FlagState? carried = ctf.CarriedBy(bot);

            if (carried is not null)
            {
                // Carrying: head home to capture (the team's own base / flag position).
                if (TryFlagBasePosition(ctf, myTeam, out Vector3 home))
                    rater.Rate(bot.Origin, FindFlagBaseEntity(myTeam), home, RatingFlag, 10000f);
            }
            else
            {
                // Not carrying: grab the enemy flag (at its base or wherever dropped).
                foreach (var (team, flag) in ctf.Flags)
                {
                    if (team == myTeam) continue;
                    Vector3 pos = FlagPosition(ctf, team, flag);
                    rater.Rate(bot.Origin, flag.Carrier, pos, RatingFlag, 10000f);
                }
                // Return our own flag if it's away from base (dropped/carried).
                if (ctf.Flags.TryGetValue(myTeam, out FlagState? mine) && mine.Status != FlagStatus.AtBase)
                {
                    Vector3 pos = FlagPosition(ctf, myTeam, mine);
                    rater.Rate(bot.Origin, mine.Carrier, pos, RatingFlag * 0.8f, 10000f);
                }
            }
        }

        // Always also want nearby items + enemies + a roam fallback (QC the role's item/enemy goalrating).
        BotRoles.GoalrateItems(brain, rater, bot.Origin, 10000f);
        BotRoles.GoalrateEnemyPlayers(brain, rater, bot.Origin, 7000f);
        BotRoles.GoalrateRoamWaypoints(brain, rater, bot.Origin, 3000f);
        rater.End();
    }

    private static Vector3 FlagPosition(Ctf ctf, int team, FlagState flag)
    {
        if (flag.Status == FlagStatus.Carried && flag.Carrier is not null)
            return flag.Carrier.Origin;
        if (flag.Status == FlagStatus.Dropped)
            return flag.DropOrigin;
        // at base: use the flag base marker entity if the map has one, else fall back to a team spawn.
        return TryFlagBasePosition(ctf, team, out Vector3 home) ? home : flag.DropOrigin;
    }

    private static bool TryFlagBasePosition(Ctf ctf, int team, out Vector3 pos)
    {
        Entity? e = FindFlagBaseEntity(team);
        if (e is not null) { pos = e.Origin; return true; }
        pos = Vector3.Zero;
        return false;
    }

    private static Entity? FindFlagBaseEntity(int team)
    {
        if (Api.Services is null) return null;
        // map flag markers: item_flag_team1..4 (QC) — index 1..4 by team color order.
        string cls = team switch
        {
            Teams.Red => "item_flag_team1",
            Teams.Blue => "item_flag_team2",
            Teams.Yellow => "item_flag_team3",
            Teams.Pink => "item_flag_team4",
            _ => "",
        };
        if (cls.Length == 0) return null;
        Api.Entities.FindByClass(cls, _scratch);
        for (int i = 0; i < _scratch.Count; i++)
            if (!_scratch[i].IsFreed) return _scratch[i];
        return null;
    }

    // ============================================================================================
    // Domination (QC havocbot_role_dom + havocbot_goalrating_controlpoints)
    // ============================================================================================

    /// <summary>
    /// Domination role (QC havocbot_role_dom): rate control points that are unclaimed, contested, or held by
    /// another team (the ones worth capturing), plus items + roaming. Control points are the spawned
    /// <c>dom_controlpoint</c> map entities, found by classname.
    /// </summary>
    public static void RoleDomination(BotBrain brain, GoalRater rater)
    {
        var bot = brain.Bot;
        rater.Start();
        GoalrateControlPoints(brain, rater, bot.Origin, 15000f);
        BotRoles.GoalrateItems(brain, rater, bot.Origin, 8000f);
        BotRoles.GoalrateRoamWaypoints(brain, rater, bot.Origin, 3000f);
        rater.End();
    }

    private static void GoalrateControlPoints(BotBrain brain, GoalRater rater, Vector3 org, float radius)
    {
        if (Api.Services is null) return;
        var bot = brain.Bot;
        float radius2 = radius * radius;
        Api.Entities.FindByClass("dom_controlpoint", _scratch);
        for (int i = 0; i < _scratch.Count; i++)
        {
            Entity cp = _scratch[i];
            if (cp.IsFreed) continue;
            Vector3 pos = (cp.AbsMin != cp.AbsMax) ? (cp.AbsMin + cp.AbsMax) * 0.5f : cp.Origin;
            if ((pos - org).LengthSquared() > radius2) continue;
            // QC: rate if contested (cnt > -1), unclaimed, or held by another team. We approximate "not mine"
            // via the point's Team field (0 = unclaimed); a point already on our team is skipped.
            if (cp.Team != 0f && (int)cp.Team == (int)bot.Team) continue;
            rater.Rate(org, cp, pos, RatingControlPoint, 5000f);
        }
    }

    // ============================================================================================
    // Onslaught (QC havocbot_role_ons_* — attack the enemy generator / defend ours)
    // ============================================================================================

    /// <summary>
    /// Onslaught role (QC havocbot_role_ons_offense): route to the enemy generator (to destroy it) and the
    /// nearest control point, plus items. Generators/control points are the spawned <c>onslaught_generator</c>
    /// / <c>onslaught_controlpoint</c> map entities.
    /// </summary>
    public static void RoleOnslaught(BotBrain brain, GoalRater rater)
    {
        var bot = brain.Bot;
        rater.Start();
        if (Api.Services is not null)
        {
            Api.Entities.FindByClass("onslaught_generator", _scratch);
            for (int i = 0; i < _scratch.Count; i++)
            {
                Entity gen = _scratch[i];
                if (gen.IsFreed) continue;
                if (gen.Team != 0f && (int)gen.Team == (int)bot.Team) continue; // ours: don't attack
                rater.Rate(bot.Origin, gen, gen.Origin, RatingGenerator, 100000f);
            }
            // The generator scan above is fully consumed before this one reuses _scratch.
            Api.Entities.FindByClass("onslaught_controlpoint", _scratch);
            for (int i = 0; i < _scratch.Count; i++)
            {
                Entity cp = _scratch[i];
                if (cp.IsFreed) continue;
                if (cp.Team != 0f && (int)cp.Team == (int)bot.Team) continue;
                rater.Rate(bot.Origin, cp, cp.Origin, RatingControlPoint, 30000f);
            }
        }
        BotRoles.GoalrateItems(brain, rater, bot.Origin, 8000f);
        BotRoles.GoalrateRoamWaypoints(brain, rater, bot.Origin, 3000f);
        rater.End();
    }

    // ============================================================================================
    // KeyHunt (QC havocbot_role_kh_* + havocbot_goalrating_kh)
    // ============================================================================================

    /// <summary>
    /// KeyHunt role (QC havocbot_role_kh_freelancer): rate the key entities — go to a teammate carrier to
    /// support, an enemy carrier to kill, or a dropped key to grab — plus items + roaming. Keys are the
    /// spawned <c>keyhunt_key</c> map/state entities found by classname; carriers come from the gametype.
    /// </summary>
    public static void RoleKeyHunt(BotBrain brain, GoalRater rater)
    {
        var bot = brain.Bot;
        rater.Start();
        if (Api.Services is not null)
        {
            Api.Entities.FindByClass("keyhunt_key", _scratch);
            for (int i = 0; i < _scratch.Count; i++)
            {
                Entity key = _scratch[i];
                if (key.IsFreed) continue;
                // A carried key (key.Owner set) rates toward the carrier (ally to support, enemy to kill);
                // a loose key rates toward its drop position.
                Entity target = key.Owner ?? key;
                Vector3 pos = target.Origin;
                float scale = RatingKey;
                if (key.Owner is not null && (int)key.Owner.Team == (int)bot.Team)
                    scale *= 0.5f; // supporting an ally carrier is lower priority than grabbing/killing
                rater.Rate(bot.Origin, target, pos, scale, 100000f);
            }
        }
        BotRoles.GoalrateItems(brain, rater, bot.Origin, 8000f);
        BotRoles.GoalrateEnemyPlayers(brain, rater, bot.Origin, 7000f);
        BotRoles.GoalrateRoamWaypoints(brain, rater, bot.Origin, 3000f);
        rater.End();
    }

    // ============================================================================================
    // Keepaway (QC havocbot_role_ka_* + havocbot_goalrating_ball)
    // ============================================================================================

    /// <summary>
    /// Keepaway role (QC havocbot_role_ka_collector/carrier): if carrying the ball, fight enemies for points;
    /// otherwise chase the loose ball (or the enemy carrier to take it). The ball is the spawned
    /// <c>keepaway_ball</c> entity; its <see cref="Entity.Owner"/> marks the carrier.
    /// </summary>
    public static void RoleKeepaway(BotBrain brain, GoalRater rater)
    {
        var bot = brain.Bot;
        rater.Start();

        Entity? ball = FindBall();
        bool carryingBall = ball is not null && ReferenceEquals(ball.Owner, bot);

        if (carryingBall)
        {
            // Carrier: bank points by surviving + fighting; seek enemies and items, avoid the open.
            BotRoles.GoalrateEnemyPlayers(brain, rater, bot.Origin, 6000f);
            BotRoles.GoalrateItems(brain, rater, bot.Origin, 8000f);
        }
        else if (ball is not null)
        {
            // Collector: go to the ball (or its carrier to take it).
            Entity target = ball.Owner ?? ball;
            rater.Rate(bot.Origin, target, target.Origin, RatingBall, 2000f);
            BotRoles.GoalrateItems(brain, rater, bot.Origin, 8000f);
        }
        else
        {
            // No ball found: play like DM.
            BotRoles.GoalrateItems(brain, rater, bot.Origin, 8000f);
            BotRoles.GoalrateEnemyPlayers(brain, rater, bot.Origin, 7000f);
        }
        BotRoles.GoalrateRoamWaypoints(brain, rater, bot.Origin, 3000f);
        rater.End();
    }

    private static Entity? FindBall()
    {
        if (Api.Services is null) return null;
        Api.Entities.FindByClass("keepaway_ball", _scratch);
        for (int i = 0; i < _scratch.Count; i++)
            if (!_scratch[i].IsFreed) return _scratch[i];
        return null;
    }

    // ----------------------------------------------------------------------------------------
    // Nexball (QC havocbot_role_nexball) — carry/shoot the ball into the ENEMY goal
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// Nexball role (QC havocbot_role_nexball): if carrying the ball, route to the enemy goal to score;
    /// otherwise chase the loose ball (or its carrier). Always also wants items/enemies/roam (a behavioural
    /// collapse of the QC carrier/support roles). Falls back to generic if no ball is on the map.
    /// </summary>
    public static void RoleNexball(BotBrain brain, GoalRater rater)
    {
        var bot = brain.Bot;
        rater.Start();

        if (brain.GameType is Nexball nb && nb.BallEntity is Entity ball && !ball.IsFreed)
        {
            if (ReferenceEquals(ball.Owner, bot))
            {
                Entity? goal = FindEnemyNexballGoal(bot);          // carrying → drive to the enemy goal
                if (goal is not null)
                    rater.Rate(bot.Origin, goal, goal.Origin, RatingControlPoint, 100000f);
            }
            else
            {
                Entity target = ball.Owner ?? ball;                // loose / enemy-carried → go get it
                rater.Rate(bot.Origin, target, target.Origin, RatingBall, 100000f);
            }
        }

        BotRoles.GoalrateItems(brain, rater, bot.Origin, 8000f);
        BotRoles.GoalrateEnemyPlayers(brain, rater, bot.Origin, 6000f);
        BotRoles.GoalrateRoamWaypoints(brain, rater, bot.Origin, 3000f);
        rater.End();
    }

    private static Entity? FindEnemyNexballGoal(Player bot)
    {
        if (Api.Services is null) return null;
        Api.Entities.FindByClass("nexball_goal", _scratch);
        for (int i = 0; i < _scratch.Count; i++)
        {
            Entity g = _scratch[i];
            if (!g.IsFreed && g.GtHomeTeam != (int)bot.Team) return g; // score in the enemy team's goal
        }
        return null;
    }

    // ----------------------------------------------------------------------------------------
    // Assault (QC havocbot_role_ass) — attack / defend the objective destructible walls
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// Assault role (QC havocbot_role_ass_offense/defense): route to the objective destructible walls — the
    /// attacking team to destroy the active one, the defending team to guard it. The active-wall distinction
    /// isn't networked onto the world entity here, so the bot heads for the nearest <c>func_assault_destructible</c>
    /// (attackers push forward to shoot it; defenders hold near it), plus items/enemies/roam.
    /// </summary>
    public static void RoleAssault(BotBrain brain, GoalRater rater)
    {
        var bot = brain.Bot;
        rater.Start();

        bool attacker = brain.GameType is Assault asl && (int)bot.Team == asl.AttackerTeam;
        Entity? wall = NearestByClass(bot.Origin, "func_assault_destructible");
        if (wall is not null)
            rater.Rate(bot.Origin, wall, wall.Origin, attacker ? RatingGenerator : RatingGenerator * 0.7f, 100000f);

        BotRoles.GoalrateItems(brain, rater, bot.Origin, 8000f);
        BotRoles.GoalrateEnemyPlayers(brain, rater, bot.Origin, attacker ? 6000f : 7000f);
        BotRoles.GoalrateRoamWaypoints(brain, rater, bot.Origin, 3000f);
        rater.End();
    }

    private static Entity? NearestByClass(Vector3 from, string className)
    {
        if (Api.Services is null) return null;
        Entity? best = null; float bestD2 = float.MaxValue;
        Api.Entities.FindByClass(className, _scratch);
        for (int i = 0; i < _scratch.Count; i++)
        {
            Entity e = _scratch[i];
            if (e.IsFreed) continue;
            float d2 = (e.Origin - from).LengthSquared();
            if (d2 < bestD2) { bestD2 = d2; best = e; }
        }
        return best;
    }
}
