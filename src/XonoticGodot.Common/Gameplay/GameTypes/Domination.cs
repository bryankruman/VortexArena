using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;
using GS = XonoticGodot.Common.Gameplay.Scoring.GameScores; // T7: alias the static score table for the per-mode ScoreRules

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Domination — port of <c>CLASS(Domination, Gametype)</c>
/// (common/gametypes/gametype/domination/{domination.qh,sv_domination.qc}). Teams fight over fixed
/// control points; a point captured by a team periodically ticks points to that team's score (and to the
/// capturing player). First team to the point limit (QC g_domination_point_limit, default 200) wins.
///
/// QC defaults (gametype_init): "timelimit=20 pointlimit=200 teams=2 leadlimit=0" (legacydefaults "200 20 0").
///
/// Faithfully ported (Godot-free essence):
///  - smallest-team assignment on join (<see cref="TeamBalance"/>);
///  - control points with an owning team (<see cref="ControlPoint"/>), each backed by a world
///    <see cref="Entity"/> spawned at the map's dom_controlpoint/team_dom_point spawns
///    (<see cref="SpawnControlPoint"/>, QC dom_controlpoint_setup), captured by a player touching them
///    (<see cref="CapturePoint"/>, QC dompointtouch → dompoint_captured → set goalentity.team) with the
///    capturer's DOM_TAKES credit;
///  - the periodic point tick (<see cref="Tick"/> + per-point <see cref="PointThink"/>, QC dompointthink →
///    TeamScore_AddToTeam ST_SCORE +amt every g_domination_point_rate seconds) crediting the owning team and
///    the capturing player, honoring each point's per-point amt/rate (point frags/wait from the entity);
///  - point-limit win condition (GameRules_limit_score → fraglimit, read here as g_domination_point_limit);
///  - the ROUND-BASED variant (g_domination_roundbased, QC dom_DelayedInit → round_handler_Spawn): a team
///    wins a round by owning ALL control points (<see cref="CheckRoundWinner"/> /
///    <see cref="CountAndFindRoundWinner"/>, QC Domination_CheckWinner + Team_GetWinnerTeam_WithOwnedItems),
///    banked as ST_DOM_CAPS — no per-tick scoring in this mode (PointThink early-returns when RoundBased).
///
/// Deferred (NOTE — cross-boundary): point models/animation/waypoint sprites (CSQC), the neutral capture-interim
/// state, and the pps HUD state — client/networking concerns.
/// </summary>
[GameType]
public sealed class Domination : GameType
{
    // ----- point-limit cvars + default (g_domination_point_limit → fraglimit; legacydefaults 200) -----
    private const string CvarPointLimitDom = "g_domination_point_limit";
    private const string CvarPointLimit    = "fraglimit";
    private const string CvarLeadLimitDom  = "g_domination_teams_override"; // (override slot; lead via leadlimit)
    private const string CvarLeadLimit     = "leadlimit";
    private const float  DefaultPointLimit = 200f;

    // ----- tick cvars + defaults (g_domination_point_amt / g_domination_point_rate) -----
    private const string CvarPointAmt  = "g_domination_point_amt";
    private const string CvarPointRate = "g_domination_point_rate";
    private const float  DefaultPointAmt  = 1f;   // QC: falls back to per-point .frags; default per-tick 1
    private const float  DefaultPointRate = 2f;   // QC: falls back to per-point .wait; default ~2s

    // ----- team count cvars (g_domination_teams_override >= 2 ? override : g_domination_default_teams) -----
    private const string CvarTeamsOverride = "g_domination_teams_override";
    private const string CvarTeams         = "g_domination_default_teams";
    private const int    DefaultTeams      = 2;

    // Per-team running point totals (QC ST_SCORE slot 0 + ST_DOM_TICKS slot 1) now live in the unified GameScores
    // two-slot team store — the source of truth (common/scores.qh). Dom adds each tick to BOTH slots (QC
    // dompoint_captured); the team primary is slot 0 (SCORE) normally, slot 1 (TICKS) when disable_frags.

    /// <summary>The control points on the map (QC g_dompoints).</summary>
    public readonly List<ControlPoint> Points = new();

    public bool MatchEnded { get; private set; }
    public int LeaderTeam { get; private set; }

    /// <summary>
    /// QC round_handler_GetEndTime for the round-based variant: a source for the current round's end time (the
    /// host wires this to the world round handler's RoundEndTime; default 0 = no per-round time limit). When the
    /// returned end time has elapsed the round ends with no winner (<see cref="CheckRoundWinner"/>). Kept as a
    /// source (rather than owning a round handler) so the round flow is driven by the host's single round handler
    /// — see GameWorld.ActivateGameType wiring <c>EnableRounds(dom...)</c> — and stays headlessly testable.
    /// </summary>
    public Func<float>? RoundEndTimeSource { get; set; }

    /// <summary>The team that won the most recent round (round-based variant), or 0. Reset each round start.</summary>
    public int LastRoundWinner { get; private set; }

    public Domination()
    {
        NetName = "dom";
        DisplayName = "Domination";
        TeamGame = true;
    }

    public override void OnInit()
    {
        // QC dom_DelayedInit / dom_controlpoint_setup: the control points are spawned from the map's
        // dom_controlpoint entities (see SpawnControlPoint). GameRules_teams(true), the dom_team palette
        // entities, and the round-based variant are the engine's / a later phase's job; OnInit clears state.
        Points.Clear();
    }

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

    /// <summary>Point limit in force (g_domination_point_limit, else fraglimit, else 200). 0 == unlimited.</summary>
    public float PointLimit
    {
        get
        {
            if (TryCvar(CvarPointLimitDom, out float pl)) return pl;
            if (TryCvar(CvarPointLimit, out float fl)) return fl;
            return DefaultPointLimit;
        }
    }

    public float LeadLimit => TryCvar(CvarLeadLimit, out float l) ? l : 0f;

    /// <summary>Points granted to the owning team per tick (g_domination_point_amt, else 1).</summary>
    public float PointAmount => TryCvar(CvarPointAmt, out float v) ? v : DefaultPointAmt;

    /// <summary>Seconds between point ticks for an owned control point (g_domination_point_rate, else 2).</summary>
    public float PointRate => TryCvar(CvarPointRate, out float v) ? v : DefaultPointRate;

    /// <summary>QC autocvar_g_domination_disable_frags: when set, kills give no individual frags at all.</summary>
    public bool DisableFrags => Cvar("g_domination_disable_frags", 0f) != 0f;

    /// <summary>QC autocvar_g_domination_roundbased (domination_roundbased): the round-based Domination variant —
    /// a team wins a round by capturing all points, scored as CAPS (no per-tick TICKS column). The tick-based
    /// (non-roundbased) variant is the port's active path; this selects the matching ScoreRules schema (D4).</summary>
    public bool RoundBased => Cvar("g_domination_roundbased", 0f) != 0f;

    public void Activate()
    {
        MatchEnded = false;
        LeaderTeam = Teams.None;
        LastRoundWinner = Teams.None;
        Scoring.GameScores.ResetTeams(); // QC Score_ClearAll at match start: zero both team slots before declaring
        DeclareScoreRules();
        Scoring.GameScores.SeedTeams(TeamCount); // zero both team slots for the active teams (stable leader scan)
        // Domination scores via control-point ticks, not kills (frags are cosmetic, and g_domination_disable_frags
        // can suppress them entirely — see DisableFrags). No Combat.Death subscription is needed.
        //
        // QC dom_DelayedInit (sv_domination.qc:632): the ROUND-BASED variant spawns a round handler over
        // Domination_CheckPlayers / Domination_CheckWinner / Domination_RoundStart. The host drives that round
        // handler (GameWorld.ActivateGameType calls EnableRounds(CanRoundStart, CheckRoundWinner, RoundStart)
        // when RoundBased) and wires RoundEndTimeSource; a team wins a round by owning ALL control points
        // (banked as ST_DOM_CAPS). The tick variant has no round handler.
    }

    /// <summary>
    /// QC Domination_CheckPlayers (sv_domination.qc:362): the round-based gate to start a round. QC returns true
    /// unconditionally (the engine's player-presence check is upstream); mirrored here.
    /// </summary>
    public bool CanRoundStart() => true;

    /// <summary>QC Domination_RoundStart (sv_domination.qc:367): unblock players at round start; clear the
    /// per-round winner latch so the next <see cref="CheckRoundWinner"/> can credit again.</summary>
    public void RoundStart() => LastRoundWinner = Teams.None;

    /// <summary>
    /// QC Domination_count_controlpoints (sv_domination.qc:311) + Team_GetWinnerTeam_WithOwnedItems: count how
    /// many points each team owns; a team owning ALL of them is the round winner. Returns the winner team
    /// (&gt;0), -1 for a tie/over-time-out, or 0 while the round is still contested. (Neutral points mean no
    /// team owns all, so the round continues.)
    /// </summary>
    public int CountAndFindRoundWinner()
    {
        if (Points.Count == 0)
            return 0;
        int total = Points.Count;
        var owned = new Dictionary<int, int>();
        foreach (ControlPoint cp in Points)
            if (cp.OwnerTeam != Teams.None)
                owned[cp.OwnerTeam] = (owned.TryGetValue(cp.OwnerTeam, out int c) ? c : 0) + 1;

        foreach (var kv in owned)
            if (kv.Value >= total)
                return kv.Key; // this team owns every point → it wins the round
        return 0; // still contested
    }

    /// <summary>
    /// QC Domination_CheckWinner (sv_domination.qc:332): the round handler's canRoundEnd. The round ends when a
    /// team owns every control point (bank ST_DOM_CAPS +1 for it) OR the round time limit elapses (no winner).
    /// Returns true when the round is decided this frame; idempotent within a round via <see cref="LastRoundWinner"/>.
    /// </summary>
    public bool CheckRoundWinner()
    {
        // QC: round_handler_GetEndTime() > 0 && endtime - time <= 0 → round over, no winner (time ran out).
        float endTime = RoundEndTimeSource?.Invoke() ?? 0f;
        if (endTime > 0f)
        {
            float now = Api.Services is not null ? Api.Clock.Time : 0f;
            if (endTime - now <= 0f)
                return true;
        }

        int winner = CountAndFindRoundWinner();
        if (winner <= 0)
            return false;

        if (LastRoundWinner == Teams.None)
        {
            LastRoundWinner = winner;
            // QC: TeamScore_AddToTeam(winner_team, ST_DOM_CAPS, +1). The round-based schema makes slot 1 the
            // "caps" team primary (DeclareScoreRules roundbased arm).
            Scoring.GameScores.AddToTeam(winner, Scoring.GameScores.TeamSlotSecondary, 1);
        }
        return true;
    }

    /// <summary>QC teamscores(team, ST_DOM_CAPS) — the round wins banked by a team in the round-based variant.</summary>
    public int GetTeamCaps(int team) => Scoring.GameScores.TeamScore(team, Scoring.GameScores.TeamSlotSecondary);

    /// <summary>
    /// QC <c>ScoreRules_dom</c> (sv_domination.qc): declare Domination's columns + the two TEAM-score slots and
    /// pin the sort keys, branching on <see cref="RoundBased"/> exactly like QC's two arms (D4):
    /// <list type="bullet">
    /// <item>roundbased → <c>field_team(ST_DOM_CAPS,"caps",PRIMARY) + field(SP_DOM_TAKES,"takes")</c> — caps is the
    /// sole score, NO ticks column.</item>
    /// <item>non-roundbased → <c>field_team(ST_DOM_TICKS,"ticks",sp_domticks) + field(SP_DOM_TICKS,"ticks") +
    /// field(SP_DOM_TAKES,"takes")</c>; ST_SCORE (slot 0) carries stprio=sp_score.</item>
    /// </list>
    /// The PLAYER/TEAM primary is SP_SCORE/ST_SCORE normally, but SP_DOM_TICKS/ST_DOM_TICKS when
    /// <c>g_domination_disable_frags</c> is set (kills give no frags, so ticks become the sole score).
    /// </summary>
    private void DeclareScoreRules()
    {
        GS.ScoreRulesBasics(teams: true);
        if (RoundBased)
        {
            // QC roundbased arm: GameRules_scoring(teams, PRIMARY, 0, { field_team(ST_DOM_CAPS,"caps",PRIMARY);
            // field(SP_DOM_TAKES,"takes",0); }) — caps is the team primary (slot 1), ST_SCORE (slot 0) no-prio,
            // and there is NO ticks column.
            GS.TeamRulesBasics(scorePrio: Scoring.ScoreFlags.None);
            GS.SetTeamLabel(GS.TeamSlotSecondary, "caps", Scoring.ScoreFlags.SortPrioPrimary); // ST_DOM_CAPS
            GS.DeclareColumn("DOM_TAKES", Scoring.ScoreFlags.None, "takes");
            GS.SetSortKeys(GS.Score); // player primary = SP_SCORE (sprio=PRIMARY in the QC roundbased arm)
            return;
        }

        // QC non-roundbased arm. sp_domticks/sp_score select which gets PRIMARY (disable_frags ⇒ ticks).
        Scoring.ScoreFlags spDomTicks = DisableFrags ? Scoring.ScoreFlags.SortPrioPrimary : Scoring.ScoreFlags.None;
        Scoring.ScoreFlags spScore    = DisableFrags ? Scoring.ScoreFlags.None : Scoring.ScoreFlags.SortPrioPrimary;
        GS.TeamRulesBasics(scorePrio: spScore); // ST_SCORE (slot 0) "score" with stprio = sp_score
        GS.SetTeamLabel(GS.TeamSlotSecondary, "ticks", spDomTicks); // ST_DOM_TICKS (slot 1)
        GS.DeclareColumn("DOM_TICKS", spDomTicks, "ticks");
        GS.DeclareColumn("DOM_TAKES", Scoring.ScoreFlags.None, "takes");
        Scoring.ScoreField primary = DisableFrags ? GS.Field("DOM_TICKS")! : GS.Score;
        GS.SetSortKeys(primary);
    }

    public void Deactivate()
    {
        // No hook to remove (point ticks are driven by Tick()).
    }

    public int AssignTeam(Player joiner, IReadOnlyList<Player> roster)
        => TeamBalance.JoinSmallestTeam(joiner, roster, TeamCount);

    /// <summary>Register a control point (QC IL_PUSH g_dompoints during dom_controlpoint_setup).</summary>
    public ControlPoint AddControlPoint(Vector3 origin, int initialTeam = Teams.None)
    {
        var cp = new ControlPoint(origin) { OwnerTeam = initialTeam };
        Points.Add(cp);
        return cp;
    }

    /// <summary>
    /// QC dom_controlpoint_setup (spawnfunc dom_controlpoint / team_dom_point): register a control point AND
    /// spawn its world entity at <paramref name="origin"/> (touch = capture, think = tick). Per-point
    /// <paramref name="pointAmt"/>/<paramref name="pointRate"/> mirror the QC entity .frags/.wait fields
    /// (defaults 1 point / 5s) and are used unless the global g_domination_point_amt/rate overrides them.
    /// </summary>
    public ControlPoint SpawnControlPoint(Vector3 origin, int initialTeam = Teams.None,
        float pointAmt = 1f, float pointRate = 5f)
    {
        var cp = new ControlPoint(origin)
        {
            OwnerTeam = initialTeam,
            PerPointAmt = pointAmt <= 0f ? 1f : pointAmt,
            PerPointRate = pointRate <= 0f ? 5f : pointRate,
        };
        Points.Add(cp);

        Entity? e = GametypeEntities.SpawnObjective("dom_controlpoint", origin, Teams.None,
            new Vector3(-48f, -48f, -32f), new Vector3(48f, 48f, 32f),
            touch: PointTouchEntity, think: PointThinkEntity);
        if (e is not null)
        {
            e.GtPointTeam = initialTeam;
            e.GtPointAmt = cp.PerPointAmt;
            e.GtPointRate = cp.PerPointRate;
            e.NextThink = GametypeEntities.Now + 0.1f; // QC dompointthink rate
            cp.Entity = e;
            e.GtSpawnOrigin = origin;
        }
        return cp;
    }

    /// <summary>The <see cref="ControlPoint"/> bound to a world dom_controlpoint <see cref="Entity"/>, or null.</summary>
    public ControlPoint? PointForEntity(Entity e)
    {
        for (int i = 0; i < Points.Count; i++)
            if (ReferenceEquals(Points[i].Entity, e)) return Points[i];
        return null;
    }

    /// <summary>
    /// QC dom_controlpoint_touch → dompoint_captured: <paramref name="player"/> captures
    /// <paramref name="point"/> for their team. Sets the owner team and records the capturing player so
    /// subsequent ticks can credit them (QC point.enemy / enemy_playerid). No-op if already owned by the
    /// player's team. Arms the next tick after the capture.
    /// </summary>
    public void CapturePoint(Player player, ControlPoint point)
    {
        if (MatchEnded || player.IsDead)
            return;
        int team = (int)player.Team;
        if (team == Teams.None || point.OwnerTeam == team)
            return;

        point.OwnerTeam = team;
        point.Capturer = player;
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        point.NextTickTime = now + PointRateFor(point);
        if (point.Entity is not null)
        {
            point.Entity.GtPointTeam = team;
            point.Entity.GtCapturer = player;
            point.Entity.GtNextTick = point.NextTickTime;
        }
        // QC dompoint_captured: the capturing player gets DOM_TAKES +1 (a captures-made scoreboard stat).
        player.GtPointTakes += 1; // DOM_TAKES (entity-side counter, kept for compatibility)
        AddCol(player, "DOM_TAKES", 1); // QC GameRules_scoring_add(this.enemy, DOM_TAKES, 1)
    }

    /// <summary>QC <c>GameRules_scoring_add(player, SP_X, n)</c> for a DOM player column (no-op if unregistered).</summary>
    private static void AddCol(Player p, string field, int n)
    {
        Scoring.ScoreField? f = Scoring.GameScores.Field(field);
        if (f is not null) Scoring.GameScores.AddToPlayer(p, f, n);
    }

    /// <summary>Entity touch trampoline: a live player on a valid team captures the control point.</summary>
    private void PointTouchEntity(Entity self, Entity other)
    {
        if (other is not Player p || p.IsDead)
            return;
        ControlPoint? cp = PointForEntity(self);
        if (cp is not null)
            CapturePoint(p, cp);
    }

    /// <summary>Per-point think trampoline (QC dompointthink): tick this owned point if its timer elapsed.</summary>
    private void PointThinkEntity(Entity self)
    {
        self.NextThink = GametypeEntities.Now + 0.1f; // QC dompointthink rate
        ControlPoint? cp = PointForEntity(self);
        if (cp is not null)
            PointThink(cp);
    }

    /// <summary>
    /// QC dompointthink for one point: if owned and its tick timer elapsed, grant the per-point amount to the
    /// owning team (and the capturing player while present), then re-arm. Applies the win condition.
    /// </summary>
    public void PointThink(ControlPoint cp)
    {
        // QC dompointthink only grants per-tick score in the non-roundbased mode (`if (!domination_roundbased)`);
        // roundbased Domination scores via round wins into ST_DOM_CAPS, not ticks.
        if (MatchEnded || RoundBased || cp.OwnerTeam == Teams.None)
            return;
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        if (now < cp.NextTickTime)
            return;

        int amt = (int)PointAmountFor(cp);
        AddTeamScore(cp.OwnerTeam, amt); // QC TeamScore_AddToTeam(team, ST_SCORE, amt) + (team, ST_DOM_TICKS, amt)
        if (cp.Capturer is { IsFreed: false } cap && (int)cap.Team == cp.OwnerTeam)
        {
            cap.ScoreFrags += amt;            // QC GameRules_scoring_add(enemy, SCORE, fragamt)
            AddCol(cap, "DOM_TICKS", amt);    // QC GameRules_scoring_add(enemy, DOM_TICKS, fragamt)
        }
        cp.NextTickTime = now + PointRateFor(cp);
        if (cp.Entity is not null)
            cp.Entity.GtNextTick = cp.NextTickTime;
        UpdateLeaderAndCheckLimit();
    }

    /// <summary>Per-tick amount for a point: g_domination_point_amt override, else the point's own amount.</summary>
    public float PointAmountFor(ControlPoint cp)
        => TryCvar(CvarPointAmt, out float v) ? v : cp.PerPointAmt;

    /// <summary>Per-tick interval for a point: g_domination_point_rate override, else the point's own rate.</summary>
    public float PointRateFor(ControlPoint cp)
        => TryCvar(CvarPointRate, out float v) ? v : cp.PerPointRate;

    /// <summary>
    /// Advance domination one step (QC dompointthink, evaluated per point): every owned control point whose
    /// tick timer has elapsed grants its per-point amount (<see cref="PointAmountFor"/>) to its owning team
    /// (and the capturing player), then re-arms. Delegates to <see cref="PointThink"/>. Call each tick; safe
    /// after <see cref="MatchEnded"/>. Applies the point-limit win condition after granting.
    /// </summary>
    public void Tick()
    {
        if (MatchEnded)
            return;
        // Delegate to the per-point think so each point honors its own amt/rate (QC dompointthink per entity).
        for (int i = 0; i < Points.Count; i++)
            PointThink(Points[i]);
    }

    /// <summary>
    /// QC <c>dompoint_captured</c>'s team credit: a tick adds <paramref name="delta"/> to BOTH the team's
    /// ST_SCORE (slot 0) and ST_DOM_TICKS (slot 1) — they track the same running tick total
    /// (<c>TeamScore_AddToTeam(team, ST_SCORE, fragamt); TeamScore_AddToTeam(team, ST_DOM_TICKS, fragamt);</c>).
    /// </summary>
    public void AddTeamScore(float team, int delta)
    {
        int t = (int)team;
        if (t == Teams.None)
            return;
        Scoring.GameScores.AddToTeam(t, Scoring.GameScores.TeamSlotScore, delta);     // QC ST_SCORE
        Scoring.GameScores.AddToTeam(t, Scoring.GameScores.TeamSlotSecondary, delta); // QC ST_DOM_TICKS
    }

    public int GetTeamScore(int team) => Scoring.GameScores.TeamScore(team, Scoring.GameScores.TeamSlotScore);

    public void UpdateLeaderAndCheckLimit()
    {
        // QC: Dom teams rank by the team primary slot (ST_SCORE, or ST_DOM_TICKS when disable_frags). LeaderTeam /
        // SecondTeam read the flag-aware two-slot compare from GameScores (the source of truth). The point-limit
        // check uses that primary slot's value too (ticks == score, so GetTeamScore reads it correctly either way).
        int bestTeam = Scoring.GameScores.LeaderTeam();
        LeaderTeam = bestTeam;
        if (bestTeam == Teams.None)
            return;

        int bestScore = GetTeamScore(bestTeam);
        float pointLimit = PointLimit;
        if (pointLimit > 0f && bestScore >= pointLimit)
            MatchEnded = true;

        int secondTeam = Scoring.GameScores.SecondTeam();
        float leadLimit = LeadLimit;
        if (leadLimit > 0f && secondTeam != Teams.None && (bestScore - GetTeamScore(secondTeam)) >= leadLimit)
            MatchEnded = true;
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

    private static float Cvar(string name, float fallback) => TryCvar(name, out float v) ? v : fallback;
}

/// <summary>
/// A Domination control point — the Godot-free essence of the QC dom_controlpoint edict
/// (.goalentity.team owner, .enemy capturer, .delay next-tick time, per-point .frags/.wait). Tracks its
/// world position, owning team, capturer, next-tick time, per-point amt/rate, and — when a facade is wired —
/// the world <see cref="Entity"/>. The point model/animation, neutral interim state, and waypoint sprites
/// remain client concerns.
/// </summary>
public sealed class ControlPoint
{
    /// <summary>World position of the point (QC entity origin).</summary>
    public readonly Vector3 Origin;

    /// <summary>The team currently owning the point (QC goalentity.team), or <see cref="Teams.None"/>.</summary>
    public int OwnerTeam = Teams.None;

    /// <summary>The player who last captured the point (QC .enemy), credited on ticks while still present.</summary>
    public Player? Capturer;

    /// <summary>Absolute sim time the next point tick is due (QC .delay).</summary>
    public float NextTickTime;

    /// <summary>Per-point score amount per tick (QC entity .frags), used unless g_domination_point_amt overrides.</summary>
    public float PerPointAmt = 1f;

    /// <summary>Per-point tick interval (QC entity .wait), used unless g_domination_point_rate overrides.</summary>
    public float PerPointRate = 5f;

    /// <summary>The world entity representing this point (QC the dom_controlpoint edict), or null (headless).</summary>
    public Entity? Entity;

    public ControlPoint(Vector3 origin) => Origin = origin;
}
