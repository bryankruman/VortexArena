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
    // QC havocbot_goalrating_tkaball ratingscale_sameteam: chasing your OWN team's carrier rates lower (TKA only).
    private const float RatingBallSameTeam = 4000f;
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
    /// Onslaught role (QC havocbot_role_ons_offense — sv_onslaught.qc:1451). The QC defense/assistant roles are
    /// legacy no-ops that immediately reset back to offense, so offense is effectively the only role; this single
    /// function reproduces it faithfully:
    ///   - QC havocbot_goalrating_ons_generator_attack FIRST — rate every ENEMY generator that is NOT shielded
    ///     (an attackable, exposed generator). If at least one is rated, the control-point pass is SKIPPED
    ///     (QC: <c>if(!generator_attack(…)) controlpoints_attack(…)</c>).
    ///   - QC havocbot_goalrating_ons_controlpoints_attack otherwise — rate only control points that are NOT
    ///     shielded AND neighbor the bot's team (QC the <c>aregensneighbor|arecpsneighbor &amp; BIT(team)</c>
    ///     filter): a shielded / unreachable point is never a goal. A free (no-icon) point is worth DOUBLE
    ///     (QC <c>ratingscale * 2</c> for a touchable point vs an icon to attack).
    /// The shield/neighbor state comes from the live <see cref="Onslaught"/> power graph (<see cref="Onslaught.OnsNode"/>),
    /// matching what the QC reads off <c>.isshielded</c> / the neighbor bitmasks. Falls back to the coarse
    /// classname scan only when the gametype singleton isn't an Onslaught (headless/edge).
    /// </summary>
    public static void RoleOnslaught(BotBrain brain, GoalRater rater)
    {
        var bot = brain.Bot;
        int myTeam = (int)bot.Team;
        rater.Start();

        var ons = brain.GameType as Onslaught;
        if (ons is not null && Api.Services is not null)
        {
            // QC havocbot_goalrating_ons_generator_attack: rate enemy, UN-shielded generators first.
            bool ratedGenerator = false;
            Api.Entities.FindByClass("onslaught_generator", _scratch);
            for (int i = 0; i < _scratch.Count; i++)
            {
                Entity gen = _scratch[i];
                if (gen.IsFreed) continue;
                int genTeam = gen.GtHomeTeam != 0 ? gen.GtHomeTeam : (int)gen.Team;
                if (genTeam == myTeam) continue;                       // QC SAME_TEAM(g, this): skip ours
                Onslaught.OnsNode? node = ons.GeneratorNode(genTeam);
                if (node is null || node.Shielded) continue;           // QC g.isshielded: skip shielded
                rater.Rate(bot.Origin, gen, gen.Origin, RatingGenerator, 100000f);
                ratedGenerator = true;
            }

            // QC: only rate control points when no attackable generator was found.
            if (!ratedGenerator)
            {
                // The generator scan above is fully consumed before this one reuses _scratch.
                Api.Entities.FindByClass("onslaught_controlpoint", _scratch);
                for (int i = 0; i < _scratch.Count; i++)
                {
                    Entity cp = _scratch[i];
                    if (cp.IsFreed) continue;
                    Onslaught.OnsNode? node = ons.ControlPointNode(cp.GtPointId);
                    if (node is null || node.Shielded) continue;        // QC cp2.isshielded: skip shielded
                    if (node.Team == myTeam) continue;                  // already ours
                    if (!NeighborsTeam(node, myTeam)) continue;         // QC aregensneighbor|arecpsneighbor & BIT(team)
                    // QC: a free/touchable point (no build icon yet) rates DOUBLE; an enemy icon rates base.
                    bool touchable = ons.CpCombat.IconFor(node.Id) is null;
                    rater.Rate(bot.Origin, cp, cp.Origin, RatingControlPoint * (touchable ? 2f : 1f), 30000f);
                }
            }
        }
        else if (Api.Services is not null)
        {
            // Headless/edge fallback (no live Onslaught graph): the coarse classname scan.
            Api.Entities.FindByClass("onslaught_generator", _scratch);
            for (int i = 0; i < _scratch.Count; i++)
            {
                Entity gen = _scratch[i];
                if (gen.IsFreed) continue;
                if (gen.Team != 0f && (int)gen.Team == myTeam) continue;
                rater.Rate(bot.Origin, gen, gen.Origin, RatingGenerator, 100000f);
            }
            Api.Entities.FindByClass("onslaught_controlpoint", _scratch);
            for (int i = 0; i < _scratch.Count; i++)
            {
                Entity cp = _scratch[i];
                if (cp.IsFreed) continue;
                if (cp.Team != 0f && (int)cp.Team == myTeam) continue;
                rater.Rate(bot.Origin, cp, cp.Origin, RatingControlPoint, 30000f);
            }
        }

        BotRoles.GoalrateItems(brain, rater, bot.Origin, 8000f);
        BotRoles.GoalrateRoamWaypoints(brain, rater, bot.Origin, 3000f);
        rater.End();
    }

    /// <summary>
    /// QC the <c>aregensneighbor|arecpsneighbor &amp; BIT(team)</c> filter (sv_onslaught.qc:1311): true if the
    /// node is adjacent to a powered (linked) node owned by <paramref name="team"/> — i.e. the team can actually
    /// reach/attack it. Derived from the live power graph (the same data onslaught_updatelinks would set the bits from).
    /// </summary>
    private static bool NeighborsTeam(Onslaught.OnsNode node, int team)
    {
        foreach (Onslaught.OnsNode m in node.Neighbors)
            if (m.Linked && m.Captured && m.Team == team)
                return true;
        return false;
    }

    // ============================================================================================
    // KeyHunt (QC havocbot_role_kh_* + havocbot_goalrating_kh)
    // ============================================================================================

    /// <summary>
    /// KeyHunt role — a faithful behavioural collapse of the four QC roles (carrier / defense / offense /
    /// freelancer). Rather than a per-bot role-timeout state machine, this single role function adapts its
    /// ratingscales to the same state the QC roles branch on: whether the bot is a key carrier, and which team
    /// (if any) currently owns ALL keys (QC kh_Key_AllOwnedByWhichTeam). The ratingscale TRIPLES below are taken
    /// verbatim from the QC roles' havocbot_goalrating_kh(team, dropped, enemy) calls:
    ///   - carrier, my team owns all → (10, 0.1, 0.05)  bring the keys home
    ///   - carrier, otherwise        → (4, 4, 0.5)       play defensively while carrying
    ///   - on-foot, my team owns all → (10, 0.1, 0.05)  defend the gathering carriers
    ///   - on-foot, no team owns all → (1, 10, 2)        prefer dropped keys (freelancer)
    ///   - on-foot, ENEMY owns all   → (0.1, 0.1, 5)     attack the enemy carriers (emergency)
    /// Keys are the spawned <c>item_kh_key</c> entities; a carried key's carrier is <see cref="Entity.GtCarrier"/>.
    /// </summary>
    public static void RoleKeyHunt(BotBrain brain, GoalRater rater)
    {
        var bot = brain.Bot;
        rater.Start();

        int myTeam = (int)bot.Team;
        var kh = brain.GameType as KeyHunt;
        int allOwned = kh?.AllOwnedByWhichTeam() ?? Teams.None;
        bool botCarries = bot.GtCarried is not null; // QC this.kh_next — the bot is holding at least one key

        // Select the (team, dropped, enemy) ratingscale triple per the QC role/state matrix above.
        float rsTeam, rsDropped, rsEnemy;
        if (botCarries)
        {
            if (allOwned == myTeam) { rsTeam = 10f; rsDropped = 0.1f; rsEnemy = 0.05f; }
            else                    { rsTeam = 4f;  rsDropped = 4f;   rsEnemy = 0.5f;  }
        }
        else if (allOwned == myTeam) { rsTeam = 10f;  rsDropped = 0.1f; rsEnemy = 0.05f; }
        else if (allOwned == Teams.None) { rsTeam = 1f; rsDropped = 10f; rsEnemy = 2f; }
        else                         { rsTeam = 0.1f; rsDropped = 0.1f; rsEnemy = 5f;   }

        // QC havocbot_goalrating_kh: rate every key — toward an ally carrier (team), an enemy carrier (enemy),
        // or a dropped key (dropped). The base scales are *10000 in QC; RatingKey is the port's BOT_RATING_* anchor.
        if (Api.Services is not null)
        {
            Api.Entities.FindByClass("item_kh_key", _scratch);
            for (int i = 0; i < _scratch.Count; i++)
            {
                Entity key = _scratch[i];
                if (key.IsFreed) continue;
                Entity? carrier = key.GtCarrier;
                if (carrier is not null)
                {
                    if (ReferenceEquals(carrier, bot)) continue; // QC: head.owner == this → skip my own key
                    float scale = (int)carrier.Team == myTeam ? rsTeam : rsEnemy;
                    rater.Rate(bot.Origin, carrier, carrier.Origin, RatingKey * scale, 100000f);
                }
                else
                {
                    rater.Rate(bot.Origin, key, key.Origin, RatingKey * rsDropped, 100000f);
                }
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
    /// <c>keepawayball</c> entity; its <see cref="Entity.GtCarrier"/> marks the carrier while held
    /// (== Base ka_TouchEvent's <c>ball.owner = toucher</c>; the port reserves <see cref="Entity.Owner"/>
    /// for the previous-owner self-recapture lockout, set only on drop).
    /// </summary>
    public static void RoleKeepaway(BotBrain brain, GoalRater rater)
    {
        var bot = brain.Bot;
        rater.Start();

        Entity? ball = FindBall();
        bool carryingBall = ball is not null && ReferenceEquals(ball.GtCarrier, bot);

        if (carryingBall)
        {
            // Carrier (QC havocbot_role_ka_carrier / havocbot_role_tka_carrier, identical values): bank points by
            // surviving + fighting — items 10000, enemyplayers 10000, then roam waypoints (added below at 3000).
            BotRoles.GoalrateItems(brain, rater, bot.Origin, 10000f);
            BotRoles.GoalrateEnemyPlayers(brain, rater, bot.Origin, 10000f);
        }
        else if (ball is not null)
        {
            // Collector (QC havocbot_role_ka_collector / havocbot_role_tka_collector, identical values): items
            // 10000, enemyplayers 500 (don't get distracted fragging — go get the ball), then the ball rating.
            BotRoles.GoalrateItems(brain, rater, bot.Origin, 10000f);
            BotRoles.GoalrateEnemyPlayers(brain, rater, bot.Origin, 500f);
            // Go to the ball (or its carrier to take it). Base havocbot_goalrating_ball / _tkaball prefer a loose
            // ball, else the carrier (it.owner) — here GtCarrier when held.
            Entity? carrier = ball.GtCarrier;
            Entity target = carrier ?? ball;
            // Team Keepaway (havocbot_goalrating_tkaball): a loose ball or an ENEMY carrier rates 8000; a SAME-TEAM
            // carrier rates 4000 (chase your own carrier less). Plain (FFA) Keepaway has no teams, so always 8000.
            float scale = RatingBall;
            if (carrier is not null && (brain.GameType?.TeamGame ?? false) && (int)carrier.Team == (int)bot.Team)
                scale = RatingBallSameTeam;
            rater.Rate(bot.Origin, target, target.Origin, scale, 2000f);
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
        // The live ball is spawned with classname "keepawayball" (BallEntity.WithKindDefaults, no underscore) for
        // both Keepaway and Team Keepaway — querying the wrong class made the role inert (bots fell back to DM).
        Api.Entities.FindByClass("keepawayball", _scratch);
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
    /// Assault role (QC havocbot_role_ast_offense / havocbot_role_ast_defense + havocbot_goalrating_ast_targets):
    /// route to the objective destructible walls whose linked objective is currently ACTIVE (QC filters
    /// <c>0 &lt; hlth &lt; ASSAULT_VALUE_INACTIVE</c> — a not-yet-active or already-fallen wall isn't a goal), with
    /// the QC ast-targets ratingscale (20000). The attack/defend split only differs in the enemy-player radius (QC
    /// 650 offense vs 3000 defense) and the role timeout (120s); both rate items + enemies + the targets. The
    /// active-objective filter is read back through the Assault POJO chain (DestructibleFor → DecreaserRef →
    /// ObjectiveRef.Active), which is the port's authoritative objective health.
    /// </summary>
    public static void RoleAssault(BotBrain brain, GoalRater rater)
    {
        var bot = brain.Bot;
        rater.Start();

        var asl = brain.GameType as Assault;
        bool attacker = asl is not null && (int)bot.Team == asl.AttackerTeam;

        // QC havocbot_goalrating_ast_targets: rate EVERY destructible whose objective is active, not just the
        // nearest — the navigation rater picks the best reachable one. ratingscale = 20000 (RatingGenerator).
        if (asl is not null && Api.Services is not null)
        {
            Api.Entities.FindByClass("func_assault_destructible", _scratch);
            for (int i = 0; i < _scratch.Count; i++)
            {
                Entity wall = _scratch[i];
                if (wall.IsFreed) continue;
                Assault.Destructible? d = asl.DestructibleFor(wall);
                // QC: only rate a wall whose linked objective is active (0 < hlth < ASSAULT_VALUE_INACTIVE).
                if (d is null || d.DecreaserRef?.ObjectiveRef is not { Active: true })
                    continue;
                rater.Rate(bot.Origin, wall, wall.Origin, RatingGenerator, 100000f);
            }
        }
        else
        {
            // No POJO chain (headless/edge): fall back to the nearest wall so bots still converge on the objective.
            Entity? wall = NearestByClass(bot.Origin, "func_assault_destructible");
            if (wall is not null)
                rater.Rate(bot.Origin, wall, wall.Origin, RatingGenerator, 100000f);
        }

        // QC havocbot_goalrating_items(this, 30000, org, 10000); enemyplayers(this, 10000, org, 650|3000).
        BotRoles.GoalrateItems(brain, rater, bot.Origin, 10000f, 30000f);
        BotRoles.GoalrateEnemyPlayers(brain, rater, bot.Origin, attacker ? 650f : 3000f);
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
