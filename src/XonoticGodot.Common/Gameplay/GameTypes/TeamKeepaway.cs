using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;
using GS = XonoticGodot.Common.Gameplay.Scoring.GameScores; // T7: alias the static score table for the per-mode ScoreRules

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Team Keepaway (TKA) gametype — port of <c>CLASS(TeamKeepaway, Gametype)</c>
/// (common/gametypes/gametype/tka/tka.qh + sv_tka.qc, which shares the keepaway ball mechanics in
/// common/gametypes/gametype/keepaway/sv_keepaway.qc).
///
/// The team version of Keepaway: there is one ball; the team that holds it earns points for kills its members
/// make while in possession. Concretely (QC keepaway scoring, applied per-team in TKA):
///  - a ball-carrier who frags an enemy scores <c>g_keepaway_score_killac</c> points for their team;
///  - fragging the enemy ball-carrier scores <c>g_keepaway_score_bckill</c> bonus points;
///  - frags themselves award no DM frag points (QC GiveFragsForKill zeroes the frag score).
/// The first team to the point limit (cvar <c>g_tka_point_limit</c>, default 50) wins.
///
/// Faithfully ported (the possession-scoring rule):
///  - which team currently holds the ball (<see cref="BallTeam"/>) and which player carries it;
///  - Combat.Death → carrier-team kill scoring (carrier-kill bonus + kill-while-carrying points);
///  - per-team score (<see cref="TeamScore"/>) and point-limit win check (<see cref="PointLimit"/>).
///
/// Faithfully ported (objective layer):
///  - the world ball entity with carry/drop/respawn (<see cref="SpawnBall"/>/<see cref="GiveBall"/>/
///    <see cref="DropBall"/>, QC ka_TouchEvent / ka_DropEvent / ka_RespawnBall);
///  - the possession-based damage scaling matrix (<see cref="DamageScale"/>, QC Damage_Calculate).
///
/// Deferred (NOTE — cross-boundary): the ball model/effects/waypoints (CSQC), the carrier-highspeed PlayerPhysics
/// modifier, and the timed-possession points (g_keepaway_score_timepoints, off by default in TKA).
/// </summary>
[GameType]
public sealed class TeamKeepaway : GameType
{
    // ----- point limit cvars + default (gametype default pointlimit=50) -----
    private const string CvarPointLimit    = "g_tka_point_limit";
    private const string CvarFragLimit     = "fraglimit"; // GameRules_limit_score writes the point limit here
    private const int    DefaultPointLimit = 50;          // gametype_init "pointlimit=50"

    // ----- kill-scoring cvars (shared keepaway scoring) -----
    private const string CvarScoreKillAc = "g_keepaway_score_killac"; // points for a kill while carrying
    private const string CvarScoreBcKill = "g_keepaway_score_bckill"; // bonus for killing the enemy carrier
    private const int    DefaultScoreKillAc = 0;
    private const int    DefaultScoreBcKill = 0;

    // ----- team count cvars (g_tka_teams_override >= 2 ? override : g_tka_teams), clamped 2..4 -----
    private const string CvarTeamsOverride = "g_tka_teams_override";
    private const string CvarTeams         = "g_tka_teams";
    private const int    DefaultTeams      = 2;

    // Per-team score (QC ST_SCORE, team slot 0 — the TEAM primary) now lives in the unified GameScores two-slot
    // team store — the source of truth (common/scores.qh). TKA's team primary IS ST_SCORE (stprio=PRIMARY, with no
    // slot-1 team field), like TDM. Read/written via ScoreFor / GameScores.AddToTeam (slot 0).

    private HookHandler<DeathEvent>? _deathHandler;

    /// <summary>Optional sink for the host/controller to react to a kill.</summary>
    public IMatchEvents? Events;

    /// <summary>The player currently carrying the ball (QC <c>.ballcarried</c> owner), or null if loose.</summary>
    public Player? Carrier { get; private set; }

    /// <summary>QC checkrules end-of-match latch: true once a team reaches the point limit.</summary>
    public bool MatchEnded { get; private set; }

    /// <summary>The team color code that reached the point limit, or 0 if none yet.</summary>
    public int WinningTeam { get; private set; }

    public TeamKeepaway()
    {
        NetName = "tka";
        DisplayName = "Team Keepaway";
        TeamGame = true;
    }

    /// <summary>The single world ball entity (QC keepawayball edict), or null (headless).</summary>
    public Entity? BallEntity { get; private set; }

    /// <summary>QC g_keepawayball_respawntime: seconds a loose ball waits before relocating.</summary>
    public float RespawnTime => TryCvar("g_keepawayball_respawntime", out float v) && v > 0f ? v : 10f;

    public override void OnInit()
    {
        // QC TKA shares keepaway's ball: one ball spawned at map start (see SpawnBall). gametype_init flags
        // (TEAMPLAY|USEPOINTS) and teams=2 are the engine's job; OnInit clears the ball reference.
        BallEntity = null;
        Carrier = null;
    }

    /// <summary>
    /// QC sv_tka.qh: <c>GameRules_spawning_teams(autocvar_g_tka_team_spawns)</c> — TKA gates team spawns on
    /// g_tka_team_spawns (stock default 0, so it does NOT use team spawnpoints by default).
    /// </summary>
    public override bool RequestsTeamSpawns => TryCvar("g_tka_team_spawns", out float v) ? v != 0f : false;

    /// <summary>
    /// QC ka_SpawnBalls: create the single world ball entity at <paramref name="origin"/> (touch = pickup,
    /// think = relocate when loose). Returns the entity (or null when no facade is wired).
    /// </summary>
    public Entity? SpawnBall(Vector3 origin)
    {
        Entity? e = GametypeEntities.SpawnObjective("keepawayball", origin, Teams.None,
            new Vector3(-24f, -24f, -24f), new Vector3(24f, 24f, 24f),
            touch: BallTouchEntity, think: RespawnBallThink);
        if (e is not null)
        {
            e.MoveType = MoveType.Bounce;
            e.TakeDamage = DamageMode.Yes;
            e.NextThink = GametypeEntities.Now + RespawnTime;
        }
        BallEntity = e;
        Carrier = null;
        return e;
    }

    /// <summary>Entity touch trampoline: a live player picks the loose ball up (QC ka_TouchEvent).</summary>
    private void BallTouchEntity(Entity self, Entity other)
    {
        if (other is Player p && !p.IsDead && Carrier is null)
            GiveBall(p);
    }

    private void RespawnBallThink(Entity self)
    {
        if (!ReferenceEquals(self, BallEntity) || Carrier is not null)
            return;
        self.Velocity = new Vector3(0f, 0f, 200f);
        self.NextThink = GametypeEntities.Now + RespawnTime;
    }

    /// <summary>
    /// QC Damage_Calculate (ka, applied in TKA): scale player-vs-player damage by ball possession. The
    /// x/y/z components of g_keepaway_ballcarrier_damage / g_keepaway_noncarrier_damage select the
    /// self/other-carrier/noncarrier case. Returns the scaled damage.
    /// </summary>
    public float DamageScale(Player attacker, Player target, float damage)
    {
        bool attackerCarries = ReferenceEquals(Carrier, attacker);
        bool targetCarries = ReferenceEquals(Carrier, target);
        string baseName = attackerCarries ? "g_keepaway_ballcarrier_damage" : "g_keepaway_noncarrier_damage";
        string suffix = ReferenceEquals(target, attacker) ? "_x" : (targetCarries ? "_y" : "_z");
        return damage * (TryCvar(baseName + suffix, out float v) ? v : 1f);
    }

    /// <summary>The team color code (see <see cref="Teams"/>) of the current ball-carrier, or 0 if loose.</summary>
    public int BallTeam => Carrier is null ? Teams.None : (int)Carrier.Team;

    /// <summary>The point limit in force (g_tka_point_limit, else fraglimit, else 50). 0 means no limit.</summary>
    public int PointLimit
    {
        get
        {
            if (TryCvar(CvarPointLimit, out float pl)) return (int)pl;
            if (TryCvar(CvarFragLimit, out float fl)) return (int)fl;
            return DefaultPointLimit;
        }
    }

    /// <summary>Number of teams in play (g_tka_teams_override if &gt;= 2, else g_tka_teams), clamped to 2..4.</summary>
    public int TeamCount
    {
        get
        {
            int n = DefaultTeams;
            if (TryCvar(CvarTeamsOverride, out float ov) && ov >= 2f) n = (int)ov;
            else if (TryCvar(CvarTeams, out float t)) n = (int)t;
            return System.Math.Clamp(n, 2, 4);
        }
    }

    public void Activate()
    {
        if (_deathHandler is not null)
            return;
        MatchEnded = false;
        WinningTeam = 0;
        Carrier = null;
        GS.ResetTeams(); // QC Score_ClearAll at match start: zero both team slots before declaring

        // QC sv_tka.qc GameRules_scoring(tka_teams, SFL_SORT_PRIO_PRIMARY, SFL_SORT_PRIO_PRIMARY, {
        // field(SP_TKA_PICKUPS, "pickups", 0); field(SP_TKA_CARRIERKILLS, "bckills", 0); field(SP_TKA_BCTIME,
        // "bctime", SFL_SORT_PRIO_SECONDARY); }): SP_SCORE is the player primary (team-scored kills); ST_SCORE
        // (team slot 0) is the TEAM primary (stprio=PRIMARY — no slot-1 team field); SP_TKA_BCTIME the player secondary.
        GS.ScoreRulesBasics(teams: true);
        GS.TeamRulesBasics(scorePrio: Scoring.ScoreFlags.SortPrioPrimary); // ST_SCORE (slot 0) is the team primary
        GS.DeclareColumn("TKA_PICKUPS", Scoring.ScoreFlags.None, "pickups");
        GS.DeclareColumn("TKA_CARRIERKILLS", Scoring.ScoreFlags.None, "bckills");
        GS.DeclareColumn("TKA_BCTIME", Scoring.ScoreFlags.None, "bctime");
        GS.SetSortKeys(GS.Score, GS.Field("TKA_BCTIME"));
        GS.SeedTeams(TeamCount); // zero both team slots for the active teams (stable leader scan)

        _deathHandler = OnDeath;
        Combat.Death.Add(_deathHandler);
    }

    /// <summary>QC <c>GameRules_scoring_add(player, SP_X, n)</c> for a TKA player column (no-op if unregistered).</summary>
    private static void AddCol(Player p, string field, int n)
    {
        Scoring.ScoreField? f = Scoring.GameScores.Field(field);
        if (f is not null) Scoring.GameScores.AddToPlayer(p, f, n);
    }

    public void Deactivate()
    {
        if (_deathHandler is null)
            return;
        Combat.Death.Remove(_deathHandler);
        _deathHandler = null;
    }

    /// <summary>QC ka_TouchEvent: a player picked up the ball and is now the carrier (attaches the entity).</summary>
    public void GiveBall(Player carrier)
    {
        if (MatchEnded || carrier.IsDead || Carrier is not null)
            return;
        Carrier = carrier;
        AddCol(carrier, "TKA_PICKUPS", 1); // QC ka_TouchEvent: GameRules_scoring_add(toucher, TKA_PICKUPS, 1)
        if (BallEntity is Entity e)
        {
            GametypeEntities.AttachToCarrier(e, carrier, Vector3.Zero);
            e.NextThink = 0f;
        }
    }

    /// <summary>QC ka_DropEvent: the ball was dropped / reset; detach it, drop it, and re-arm the respawn.</summary>
    public void DropBall()
    {
        Player? carrier = Carrier;
        Carrier = null;
        if (BallEntity is Entity e)
        {
            GametypeEntities.DetachFromCarrier(e);
            e.Solid = Solid.Trigger;
            e.MoveType = MoveType.Bounce;
            e.TakeDamage = DamageMode.Yes;
            if (carrier is not null)
                GametypeEntities.SetOrigin(e, carrier.Origin + new Vector3(0f, 0f, 10f));
            e.Velocity = new Vector3(0f, 0f, 200f);
            e.NextThink = GametypeEntities.Now + RespawnTime;
        }
    }

    /// <summary>The current score for a team color code (QC teamscores(team, ST_SCORE); 0 if none).</summary>
    public int ScoreFor(int team) => GS.TeamScore(team, GS.TeamSlotScore);

    /// <summary>QC team equality (server/scores.qc:500): the top two teams are tied on team points (ST_SCORE),
    /// so a tied timed Team Keepaway enters overtime instead of drawing (server/world.qc).</summary>
    public override bool ReportsTie(IReadOnlyList<Player> roster)
        => TeamTie.TopTwoTied(GS.LeaderTeam(), GS.SecondTeam(), ScoreFor);

    /// <summary>
    /// The obituary handler — QC keepaway PlayerDies scoring, applied per-team for TKA. A frag only scores if
    /// the attacker's team currently holds the ball: the attacker's team gains <c>g_keepaway_score_killac</c>
    /// for the kill, plus <c>g_keepaway_score_bckill</c> if the victim was the (enemy) ball-carrier. DM frag
    /// points are never awarded (QC GiveFragsForKill zeroes them). Then re-check the point limit.
    /// </summary>
    private bool OnDeath(ref DeathEvent ev)
    {
        if (ev.Victim is not Player victim)
            return false;
        if (MatchEnded)
            return false;

        Player? attacker = ev.Attacker as Player;
        bool realKill = attacker is not null && !ReferenceEquals(attacker, victim);

        // QC: the bonus for fragging the enemy ball-carrier is checked BEFORE the ball drops below.
        bool victimWasCarrier = ReferenceEquals(Carrier, victim);

        if (realKill && attacker is not null && ReferenceEquals(Carrier, attacker))
        {
            // Attacker's team holds the ball → score for kills made while carrying.
            int team = (int)attacker.Team;
            AddTeamScore(team, ScoreKillAc);
            if (victimWasCarrier)
                AddTeamScore(team, ScoreBcKill); // killed the enemy ball-carrier (bonus)
        }

        // QC sv_tka.qc: TKA_CARRIERKILLS is credited whenever the VICTIM was the ball-carrier (independent of
        // whether the attacker carries), GameRules_scoring_add(frag_attacker, TKA_CARRIERKILLS, 1).
        if (realKill && attacker is not null && victimWasCarrier)
            AddCol(attacker, "TKA_CARRIERKILLS", 1);

        // If the carrier died, the ball drops where they fell (QC ka_DropEvent on PlayerDies).
        if (victimWasCarrier)
            DropBall();

        Events?.OnFrag(attacker, victim, ev.DeathType);
        CheckPointLimit();
        return false;
    }

    private void AddTeamScore(int team, int delta)
    {
        if (team == Teams.None || delta == 0)
            return;
        GS.AddToTeam(team, GS.TeamSlotScore, delta); // QC TeamScore_AddToTeam(team, ST_SCORE, delta)
    }

    /// <summary>QC GameRules_limit_score: latch the match once a team reaches the point limit.</summary>
    public void CheckPointLimit()
    {
        int limit = PointLimit;
        if (limit <= 0)
            return;

        // QC GameRules_limit_score: the leading team (the flag-aware ST_SCORE leader) reaching the limit wins.
        int leader = GS.LeaderTeam();
        if (leader != Teams.None && ScoreFor(leader) >= limit)
        {
            MatchEnded = true;
            WinningTeam = leader;
        }
    }

    private int ScoreKillAc
        => TryCvar(CvarScoreKillAc, out float v) ? (int)v : DefaultScoreKillAc;

    private int ScoreBcKill
        => TryCvar(CvarScoreBcKill, out float v) ? (int)v : DefaultScoreBcKill;

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
