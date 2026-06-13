using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;
using GS = XonoticGodot.Common.Gameplay.Scoring.GameScores; // T7: alias the static score table for the per-mode ScoreRules

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Nexball gametype — port of <c>CLASS(NexBall, Gametype)</c>
/// (common/gametypes/gametype/nexball/nexball.qh + sv_nexball.qc).
///
/// A team sport: players carry/shoot a ball and try to put it through the enemy team's goal. Putting the ball
/// in the <em>enemy</em> goal scores a point for the scorer's team (QC <c>GoalTouch</c> → pscore=+1,
/// TeamScore_AddToTeam(ST_NEXBALL_GOALS)); an own-goal or a "fault" goal costs a point (pscore=−1, credited
/// to the other team in a two-team game). The first team to the goal limit (cvar <c>g_nexball_goallimit</c>,
/// default 5) wins (QC GameRules_limit_score). It is a weapon-arena gametype (only the ball-launcher weapon).
///
/// Faithfully ported (the goal-scoring rule):
///  - per-team goal tally (<see cref="GoalsFor"/>, routed through the unified GameScores team store);
///  - goal resolution via <see cref="ScoreGoal"/> (own-goal/fault → −1 to other team; enemy goal → +1);
///  - goal-limit win check (<see cref="GoalLimit"/>, QC g_nexball_goallimit) and winning team.
///
/// Faithfully ported (objective layer):
///  - the ball entity (basketball/football) with carry/drop/reset (<see cref="SpawnBall"/>/
///    <see cref="GiveBall"/>/<see cref="DropBall"/>, QC GiveBall / DropBall / ResetBall);
///  - the goal trigger entities (team goals + GOAL_FAULT + GOAL_OUT) and the GoalTouch dispatch that derives
///    the goal kind and scores it (<see cref="SpawnGoal"/>/<see cref="GoalTouch"/>, QC GoalTouch pscore).
///
/// Deferred (NOTE — cross-boundary): the ball physics tuning (bounce factors, basketball meter), the ball-launcher arena
/// weapon, the ball/goal models + waypoint sprites (CSQC), and score networking — client or weapon concerns.
/// </summary>
[GameType]
public sealed class Nexball : GameType
{
    // ----- goal limit cvars + default (gametype default pointlimit=5) -----
    private const string CvarGoalLimit    = "g_nexball_goallimit";
    private const string CvarFragLimit    = "fraglimit"; // GameRules_limit_score writes the goal limit here
    private const int    DefaultGoalLimit = 5;           // gametype_init "pointlimit=5"

    /// <summary>How a goal was scored, mirroring QC's GoalTouch pscore branches.</summary>
    public enum GoalKind
    {
        /// <summary>Ball entered the enemy goal: +1 to the scorer's team (QC pscore=+1).</summary>
        Score,
        /// <summary>Ball entered the scorer's own goal: −1 (QC own-goal / GOAL_FAULT, pscore=−1).</summary>
        OwnGoal,
    }

    // Per-team goal counts (QC ST_NEXBALL_GOALS, team slot 1 — the TEAM primary) now live in the unified
    // GameScores two-slot team store — the source of truth (common/scores.qh). Read/written via GoalsFor /
    // GameScores.AddToTeam (slot 1).

    private HookHandler<DeathEvent>? _deathHandler;

    /// <summary>Optional sink for the host/controller to react to a kill (no nexball-specific scoring on kills).</summary>
    public IMatchEvents? Events;

    /// <summary>QC checkrules end-of-match latch: true once a team reaches the goal limit.</summary>
    public bool MatchEnded { get; private set; }

    /// <summary>The team color code (see <see cref="Teams"/>) that reached the goal limit, or 0 if none yet.</summary>
    public int WinningTeam { get; private set; }

    public Nexball()
    {
        NetName = "nb";
        DisplayName = "Nexball";
        TeamGame = true;
    }

    // ----- goal-kind sentinels stored in a goal entity's GtHomeTeam (QC GOAL_FAULT / GOAL_OUT) -----
    /// <summary>QC GOAL_FAULT: a goal volume that docks the ball team a point (or credits the other team).</summary>
    public const int GoalFault = -2;
    /// <summary>QC GOAL_OUT: an out-of-bounds volume that just returns the ball (no scoring).</summary>
    public const int GoalOut = -3;

    /// <summary>The ball entity (QC nexball_basketball / nexball_football), or null (headless).</summary>
    public Entity? BallEntity { get; private set; }

    /// <summary>The team that currently "owns" the ball (QC ball.team) — set on pickup, used by GoalTouch.</summary>
    public int BallTeam { get; private set; } = Teams.None;

    /// <summary>The player carrying / who last touched the ball (QC ball.pusher), credited on goals.</summary>
    public Player? BallPusher { get; private set; }

    /// <summary>The goal trigger entities on the map (QC nexball_*goal / fault / out).</summary>
    public readonly List<Entity> Goals = new();

    public override void OnInit()
    {
        // QC: the ball + goals are spawned from the map's nexball_* entities (see SpawnBall / SpawnGoal).
        // gametype_init flags (TEAMPLAY|USEPOINTS|WEAPONARENA) are the engine's job; OnInit clears state.
        BallEntity = null;
        BallTeam = Teams.None;
        BallPusher = null;
        Goals.Clear();
    }

    // ============================================================================================
    //  Ball ENTITY layer (QC SpawnBall / GiveBall / DropBall / ResetBall)
    // ============================================================================================

    /// <summary>
    /// QC SpawnBall: create the world ball entity at <paramref name="origin"/> (touch = pickup). Returns the
    /// entity (null when no facade is wired).
    /// </summary>
    public Entity? SpawnBall(Vector3 origin)
    {
        Entity? e = GametypeEntities.SpawnObjective("nexball_basketball", origin, Teams.None,
            new Vector3(-16f, -16f, -16f), new Vector3(16f, 16f, 16f),
            touch: BallTouchEntity);
        if (e is not null)
            e.MoveType = MoveType.Bounce;
        BallEntity = e;
        BallTeam = Teams.None;
        BallPusher = null;
        BallHome = origin; // QC SpawnBall: this.spawnorigin = this.origin — ResetBall returns the ball here
        return e;
    }

    /// <summary>The ball's home/reset position (QC the relocated ball origin), defaults to the spawn origin.</summary>
    public Vector3 BallHome { get; set; }

    /// <summary>QC GiveBall: <paramref name="player"/> picks up the ball, becoming its carrier + team owner.</summary>
    public void GiveBall(Player player)
    {
        if (MatchEnded || player.IsDead)
            return;
        BallTeam = (int)player.Team; // QC ball.team = plyr.team
        BallPusher = player;          // QC ball.pusher = plyr
        if (BallEntity is Entity e)
        {
            GametypeEntities.AttachToCarrier(e, player, new Vector3(0f, 0f, 0f));
            e.Team = BallTeam;
        }
    }

    /// <summary>QC DropBall: the carrier loses the ball; it drops where they stood, keeping the last-toucher team.</summary>
    public void DropBall()
    {
        Player? carrier = BallEntity?.GtCarrier as Player ?? BallPusher;
        if (BallEntity is Entity e)
        {
            GametypeEntities.DetachFromCarrier(e);
            e.Solid = Solid.Trigger;
            e.MoveType = MoveType.Bounce;
            if (carrier is not null)
                GametypeEntities.SetOrigin(e, carrier.Origin);
            e.Touch = BallTouchEntity;
        }
        // QC keeps ball.team + ball.pusher after a drop (the last toucher still "owns" it for goal scoring).
    }

    /// <summary>QC ResetBall: return the ball to its home position and clear team ownership (after a goal/out).</summary>
    public void ResetBall()
    {
        if (BallEntity is Entity e)
        {
            GametypeEntities.DetachFromCarrier(e);
            e.Solid = Solid.Trigger;
            e.MoveType = MoveType.Bounce;
            e.Velocity = Vector3.Zero;
            e.Touch = BallTouchEntity;
            GametypeEntities.SetOrigin(e, BallHome);
        }
        BallTeam = Teams.None;
        BallPusher = null;
    }

    private void BallTouchEntity(Entity self, Entity other)
    {
        if (other is Player p && !p.IsDead)
            GiveBall(p);
    }

    // ============================================================================================
    //  Goal ENTITIES (QC nexball_*goal / nexball_fault / nexball_out + GoalTouch)
    // ============================================================================================

    /// <summary>
    /// QC SpawnGoal (nexball_redgoal/.../fault/out): create a goal trigger volume owned by
    /// <paramref name="goalTeam"/> (a team color, or <see cref="GoalFault"/>/<see cref="GoalOut"/>). Touch
    /// dispatches scoring via <see cref="GoalTouch"/>.
    /// </summary>
    public Entity? SpawnGoal(int goalTeam, Vector3 origin, Vector3 mins = default, Vector3 maxs = default)
    {
        if (maxs == default) { mins = new Vector3(-64f, -64f, -64f); maxs = new Vector3(64f, 64f, 64f); }
        Entity? e = GametypeEntities.SpawnObjective("nexball_goal", origin, Teams.None, mins, maxs,
            touch: GoalTouchEntity);
        if (e is not null)
        {
            e.Flags = EntFlags.None;
            e.GtHomeTeam = goalTeam; // the goal's "team"/kind sentinel
            Goals.Add(e);
        }
        return e;
    }

    /// <summary>
    /// QC GoalTouch: the ball entered goal <paramref name="goalEnt"/>. Derive the goal kind from the goal's
    /// team vs the ball's team — own-goal/fault → −1 (credited to the other team in 2-team play), enemy goal
    /// → +1, out → no score (just reset) — and apply it via <see cref="ScoreGoal"/>. Then reset the ball.
    /// </summary>
    public void GoalTouch(Entity goalEnt)
    {
        if (MatchEnded || BallTeam == Teams.None)
            return; // QC: no pusher / no ball team → no goal
        int goalTeam = goalEnt.GtHomeTeam;

        if (goalTeam == GoalOut)
        {
            ResetBall(); // QC GOAL_OUT: ball returned, no score
            return;
        }

        int otherTeam = TeamCountIsTwo() ? OtherTeam(BallTeam) : Teams.None;

        if (goalTeam == GoalFault || goalTeam == BallTeam)
        {
            // QC fault or own-goal: pscore = -1.
            ScoreGoal(BallTeam, GoalKind.OwnGoal, otherTeam);
        }
        else
        {
            // QC enemy goal: pscore = +1 for the ball's team.
            ScoreGoal(BallTeam, GoalKind.Score, otherTeam);
        }
        ResetBall();
    }

    private void GoalTouchEntity(Entity self, Entity other)
    {
        // The ball (or a ball-carrier) hit the goal.
        if (ReferenceEquals(other, BallEntity) || (other is Player p && ReferenceEquals(p.GtCarried, BallEntity)))
            GoalTouch(self);
    }

    /// <summary>QC OtherTeam (two-team only): the opposing team color.</summary>
    private static int OtherTeam(int team) => team == Teams.Red ? Teams.Blue : Teams.Red;

    private static bool TeamCountIsTwo() => true; // Nexball is two-team by default (AVAILABLE_TEAMS == 2)

    /// <summary>The goal limit in force (g_nexball_goallimit, else fraglimit, else 5). 0 means no limit.</summary>
    public int GoalLimit
    {
        get
        {
            if (TryCvar(CvarGoalLimit, out float gl)) return (int)gl;
            if (TryCvar(CvarFragLimit, out float fl)) return (int)fl;
            return DefaultGoalLimit;
        }
    }

    public void Activate()
    {
        if (_deathHandler is not null)
            return;
        MatchEnded = false;
        WinningTeam = 0;
        GS.ResetTeams(); // QC Score_ClearAll at match start: zero both team slots before declaring

        // QC nb_ScoreRules (sv_nexball.qc) GameRules_scoring(teams, 0, 0, { field_team(ST_NEXBALL_GOALS, "goals",
        // PRIMARY); field(SP_NEXBALL_GOALS, "goals", PRIMARY); field(SP_NEXBALL_FAULTS, "faults", SECONDARY |
        // LOWER_IS_BETTER); }): BOTH SP_SCORE (spprio=0) and ST_SCORE (stprio=0) are non-primary; the player
        // primary is SP_NEXBALL_GOALS (secondary FAULTS, fewer is better); the TEAM primary is ST_NEXBALL_GOALS
        // (slot 1, goal totals).
        GS.ScoreRulesBasics(teams: true);
        GS.TeamRulesBasics(scorePrio: Scoring.ScoreFlags.None); // ST_SCORE (slot 0) stprio = 0 (non-primary)
        GS.SetTeamLabel(GS.TeamSlotSecondary, "goals", Scoring.ScoreFlags.SortPrioPrimary); // ST_NEXBALL_GOALS (slot 1)
        GS.DeclareColumn("NEXBALL_GOALS", Scoring.ScoreFlags.None, "goals");
        GS.DeclareColumn("NEXBALL_FAULTS", Scoring.ScoreFlags.LowerIsBetter, "faults"); // QC: SECONDARY | LOWER_IS_BETTER
        GS.SetSortKeys(GS.Field("NEXBALL_GOALS")!, GS.Field("NEXBALL_FAULTS"));
        GS.SeedTeams(2); // Nexball is two-team (red vs blue; AVAILABLE_TEAMS == 2)

        _deathHandler = OnDeath;
        Combat.Death.Add(_deathHandler);
    }

    public void Deactivate()
    {
        if (_deathHandler is null)
            return;
        Combat.Death.Remove(_deathHandler);
        _deathHandler = null;
    }

    /// <summary>The current goal count for a team color code (QC teamscores(team, ST_NEXBALL_GOALS); 0 if none).</summary>
    public int GoalsFor(int team) => GS.TeamScore(team, GS.TeamSlotSecondary);

    /// <summary>QC team equality (server/scores.qc:500): the top two teams are tied on goals
    /// (ST_NEXBALL_GOALS, Nexball's ranking primary), so a tied timed Nexball enters overtime instead of
    /// drawing (server/world.qc).</summary>
    public override bool ReportsTie(IReadOnlyList<Player> roster)
        => TeamTie.TopTwoTied(GS.LeaderTeam(), GS.SecondTeam(), GoalsFor);

    /// <summary>
    /// Resolve a ball-into-goal event (QC GoalTouch). For a normal <see cref="GoalKind.Score"/> the scorer's
    /// team gains a point; for an <see cref="GoalKind.OwnGoal"/> a point is taken away — in a two-team game by
    /// crediting the <paramref name="otherTeam"/> instead (QC: pscore&lt;0 ⇒ TeamScore_AddToTeam(otherteam)),
    /// otherwise by docking the scorer's own team. Then re-check the goal limit. The ball/goal geometry that
    /// decides which branch applies is deferred; the host calls this when it detects a goal.
    /// </summary>
    /// <param name="scorerTeam">The team color code of the player who last touched the ball (QC ball.team).</param>
    /// <param name="otherTeam">The opposing team in a two-team game, or 0 if not applicable.</param>
    public void ScoreGoal(int scorerTeam, GoalKind kind, int otherTeam = 0)
    {
        if (MatchEnded || scorerTeam == Teams.None)
            return;

        if (kind == GoalKind.Score)
        {
            AddTeamGoal(scorerTeam, +1);
            // QC GoalTouch: pscore > 0 → GameRules_scoring_add(ball.pusher, NEXBALL_GOALS, pscore).
            if (BallPusher is not null) AddCol(BallPusher, "NEXBALL_GOALS", 1);
        }
        else // OwnGoal / fault: −1
        {
            if (otherTeam != Teams.None)
                AddTeamGoal(otherTeam, +1);   // QC two-team: the point goes to the other team
            else
                AddTeamGoal(scorerTeam, -1);  // otherwise dock the scorer's team
            // QC GoalTouch: pscore < 0 → GameRules_scoring_add(ball.pusher, NEXBALL_FAULTS, -pscore).
            if (BallPusher is not null) AddCol(BallPusher, "NEXBALL_FAULTS", 1);
        }

        CheckGoalLimit();
    }

    private void AddTeamGoal(int team, int delta)
        => GS.AddToTeam(team, GS.TeamSlotSecondary, delta); // QC TeamScore_AddToTeam(team, ST_NEXBALL_GOALS, delta)

    /// <summary>QC <c>GameRules_scoring_add(player, SP_X, n)</c> for a Nexball player column (no-op if unregistered).</summary>
    private static void AddCol(Player p, string field, int n)
    {
        Scoring.ScoreField? f = Scoring.GameScores.Field(field);
        if (f is not null) Scoring.GameScores.AddToPlayer(p, f, n);
    }

    /// <summary>QC GameRules_limit_score: latch the match once a team reaches the goal limit.</summary>
    public void CheckGoalLimit()
    {
        int limit = GoalLimit;
        if (limit <= 0)
            return;

        // QC GameRules_limit_score: the leading team (the flag-aware ST_NEXBALL_GOALS leader) reaching the limit wins.
        int leader = GS.LeaderTeam();
        if (leader != Teams.None && GoalsFor(leader) >= limit)
        {
            MatchEnded = true;
            WinningTeam = leader;
        }
    }

    /// <summary>Kills don't score in Nexball (the ball does); a carrier who dies drops the ball.</summary>
    private bool OnDeath(ref DeathEvent ev)
    {
        if (ev.Victim is not Player victim)
            return false;
        // QC nb: a carrier who dies drops the ball where they fell (DropBall).
        if (BallEntity is not null && ReferenceEquals(victim.GtCarried, BallEntity))
            DropBall();
        Events?.OnFrag(ev.Attacker as Player, victim, ev.DeathType);
        return false;
    }

    private static bool TryCvar(string name, out float value)
    {
        value = 0f;
        if (Api.Services is null)
            return false;
        string s = Api.Cvars.GetString(name);
        if (string.IsNullOrEmpty(s))
            return false;
        value = Api.Cvars.GetFloat(name);
        return true;
    }
}
