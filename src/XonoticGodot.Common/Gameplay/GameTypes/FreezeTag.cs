using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Freeze Tag — port of the Freeze Tag mutator/gametype
/// (common/gametypes/gametype/freezetag/{freezetag.qh,sv_freezetag.qc}). A round-based team mode like
/// Clan Arena, but instead of dying a fragged player is FROZEN (encased in ice, HP=1) and counts as
/// eliminated; a living teammate standing in range thaws them. A team is eliminated when all its players
/// are frozen. The last team standing wins the round (QC ST_FT_ROUNDS +1); first team to the round limit
/// (fraglimit) wins the match.
///
/// QC defaults (registered via REGISTER_MUTATOR; round timelimit + teams from g_freezetag_* cvars).
///
/// Scoring (QC freezetag_Add_Score + PlayerPreThink revive): kills give no frags
/// (GiveFragsForKill → 0); freezing an enemy → attacker SCORE +1, victim −1; freezing a teammate or
/// self → −1; reviving a teammate → reviver SCORE +1 (and FREEZETAG_REVIVALS +1). Round wins are the
/// team score that drives the win condition.
///
/// Faithfully ported (Godot-free essence):
///  - smallest-team assignment on join (<see cref="TeamBalance"/>);
///  - the freeze model: a fragged player becomes <see cref="FrozenState.Frozen"/> rather than respawning,
///    and counts as not-alive for the round check (<see cref="CheckRound"/>);
///  - manual revive by a nearby teammate (<see cref="TryRevive"/>) clears the freeze;
///  - the freeze/revive SCORE matrix and round-limit win condition.
///
/// Faithfully ported (objective layer):
///  - the freeze applies the shared <see cref="StatusEffectsCatalog.Frozen"/> status effect (QC STAT(FROZEN));
///  - revive-progress accumulation + range geometry over the roster (<see cref="ReviveTick"/>, QC
///    PlayerPreThink IN_REVIVING_RANGE box-overlap) with the auto-revive timeout (g_freezetag_frozen_maxtime);
///  - round warmup/countdown timing via the shared <see cref="RoundHandler"/> (QC round_handler_Spawn with
///    freezetag_CheckTeams / freezetag_CheckWinner), driven by <see cref="Tick"/>;
///  - FT's start-loadout (<see cref="ApplyStartItems"/>, QC g_ft_start_health/armor 100/100 + ammo).
///
/// Deferred (NOTE — cross-boundary): the ice entity model + waypoint visuals (CSQC), and bot freeing roles.
/// </summary>
[GameType]
public sealed class FreezeTag : GameType
{
    // ----- round-limit cvars + default (FreezeTag uses fraglimit for rounds-to-win) -----
    private const string CvarRoundLimit = "fraglimit";
    private const string CvarLeadLimit  = "leadlimit";
    private const float  DefaultRoundLimit = 10f;

    // ----- team count cvars (g_freezetag_teams_override >= 2 ? override : g_freezetag_teams), 2..4 -----
    private const string CvarTeamsOverride = "g_freezetag_teams_override";
    private const string CvarTeams         = "g_freezetag_teams";
    private const int    DefaultTeams      = 2;

    // ----- revive geometry/score cvars (g_freezetag_*) -----
    private const string CvarReviveSpeed = "g_freezetag_revive_speed";       // progress/sec when a teammate is in range
    private const string CvarReviveClearSpeed = "g_freezetag_revive_clearspeed"; // progress/sec decay out of range
    private const string CvarReviveExtraSize = "g_freezetag_revive_extra_size";  // bbox expansion for the range test
    private const string CvarFrozenMaxtime = "g_freezetag_frozen_maxtime";   // auto-thaw after this many seconds (0 = off)
    private const string CvarStartHealth = "g_ft_start_health";
    private const string CvarStartArmor  = "g_ft_start_armor";
    private const string CvarWarmup = "g_freezetag_warmup";
    private const string CvarRoundTimelimit = "g_freezetag_round_timelimit";
    private const string CvarRoundEndDelay = "g_freezetag_round_enddelay";
    private const float  DefaultReviveSpeed = 0.4f;
    private const float  DefaultReviveClearSpeed = 0.4f;
    private const float  DefaultReviveExtraSize = 100f;
    private const float  DefaultStartHealth = 100f;
    private const float  DefaultStartArmor  = 100f;

    /// <summary>The round-phase driver (QC round_handler) — created on <see cref="Activate"/>.</summary>
    public RoundHandler? Handler { get; private set; }

    /// <summary>The roster the handler + revive geometry evaluate (set by the host before the per-frame calls).</summary>
    private IReadOnlyList<Player> _roster = System.Array.Empty<Player>();

    /// <summary>Provide the roster used by the round handler + <see cref="ReviveTick"/>.</summary>
    public void SetRoster(IReadOnlyList<Player> roster) => _roster = roster;

    public float ReviveClearSpeed => TryCvar(CvarReviveClearSpeed, out float s) ? s : DefaultReviveClearSpeed;
    public float ReviveExtraSize  => TryCvar(CvarReviveExtraSize, out float s) ? s : DefaultReviveExtraSize;
    public float FrozenMaxtime    => TryCvar(CvarFrozenMaxtime, out float s) ? s : 0f;
    public float StartHealth      => TryCvar(CvarStartHealth, out float s) ? s : DefaultStartHealth;
    public float StartArmor       => TryCvar(CvarStartArmor, out float s) ? s : DefaultStartArmor;

    // Per-team round wins (QC ST_FT_ROUNDS, team slot 1 — the TEAM primary) now live in the unified GameScores
    // two-slot team store — the source of truth (common/scores.qh). FT's player primary is SP_SCORE (the
    // freeze/revive ±1); the team primary is ST_FT_ROUNDS. Read/written via GetTeamRounds / GameScores.AddToTeam.

    /// <summary>Per-player freeze + revive-progress state (QC STAT(FROZEN)/STAT(REVIVE_PROGRESS)).</summary>
    public readonly Dictionary<Player, FrozenState> Frozen = new();

    public readonly RoundState Round = new();

    public bool MatchEnded { get; private set; }
    public int LeaderTeam { get; private set; }

    private HookHandler<DeathEvent>? _deathHandler;

    public FreezeTag()
    {
        NetName = "freezetag";
        DisplayName = "Freeze Tag";
        TeamGame = true;
    }

    public override void OnInit()
    {
        // QC: GameRules_teams(true) + round_handler_Spawn(freezetag_CheckTeams, freezetag_CheckWinner) +
        // EliminatedPlayers_Init(freezetag_isEliminated). The round handler is created in Activate (needs the
        // roster); start-items are applied per spawn via ApplyStartItems.
    }

    /// <summary>
    /// QC sv_freezetag.qh: <c>GameRules_spawning_teams(autocvar_g_freezetag_team_spawns)</c> — FreezeTag gates
    /// team spawns on g_freezetag_team_spawns (stock default 1, so it uses team spawnpoints by default).
    /// </summary>
    public override bool RequestsTeamSpawns => TryCvar("g_freezetag_team_spawns", out float v) ? v != 0f : true;

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

    /// <summary>Round limit in force (fraglimit, else 10). 0 == unlimited.</summary>
    public float RoundLimit => TryCvar(CvarRoundLimit, out float fl) ? fl : DefaultRoundLimit;

    public float LeadLimit => TryCvar(CvarLeadLimit, out float l) ? l : 0f;

    /// <summary>Revive progress accrued per second while a teammate is in reviving range (g_freezetag_revive_speed).</summary>
    public float ReviveSpeed => TryCvar(CvarReviveSpeed, out float s) ? s : DefaultReviveSpeed;

    public void Activate()
    {
        if (_deathHandler is not null)
            return;
        MatchEnded = false;
        LeaderTeam = Teams.None;
        Round.Reset();
        Frozen.Clear();
        Scoring.GameScores.ResetTeams(); // QC Score_ClearAll at match start: zero both team slots before declaring

        // QC sv_freezetag.qc GameRules_scoring(freezetag_teams, SFL_SORT_PRIO_PRIMARY, 0, {
        // field_team(ST_FT_ROUNDS, "rounds", PRIMARY); field(SP_FREEZETAG_REVIVALS, "revivals", 0); }): SP_SCORE
        // is the player primary (freeze/revive ±1); ST_SCORE (team slot 0) has no prio (stprio=0); ST_FT_ROUNDS
        // (team slot 1) "rounds" is the TEAM primary (round wins), plus the REVIVALS player column.
        Scoring.GameScores.ScoreRulesBasics(teams: true);
        Scoring.GameScores.TeamRulesBasics(scorePrio: Scoring.ScoreFlags.None); // ST_SCORE (slot 0) stprio = 0
        Scoring.GameScores.SetTeamLabel(Scoring.GameScores.TeamSlotSecondary, "rounds", Scoring.ScoreFlags.SortPrioPrimary); // ST_FT_ROUNDS (slot 1)
        Scoring.GameScores.DeclareColumn("FREEZETAG_REVIVALS", Scoring.ScoreFlags.None, "revivals");
        Scoring.GameScores.SetSortKeys(Scoring.GameScores.Score); // player primary = SP_SCORE
        Scoring.GameScores.SeedTeams(TeamCount); // zero both team slots for the active teams (stable leader scan)

        // QC round_handler_Spawn(freezetag_CheckTeams, freezetag_CheckWinner, NULL).
        Handler = new RoundHandler(() => Api.Services is not null ? Api.Clock.Time : 0f)
        {
            CanRoundStart = CheckTeams,
            CanRoundEnd = () => CheckWinner() != 0,
            OnRoundStart = () => { Frozen.Clear(); }, // QC: thaw everyone at the start of a new round
        };
        float warmup = TryCvar(CvarWarmup, out float w) ? w : 5f;
        float rtl = TryCvar(CvarRoundTimelimit, out float t) ? t : 180f;
        float edly = TryCvar(CvarRoundEndDelay, out float e) ? e : 5f;
        Handler.Init(edly, warmup, rtl);

        _deathHandler = OnDeath;
        Combat.Death.Add(_deathHandler);
    }

    /// <summary>
    /// Advance the FT round handler one frame (QC round_handler_Think). Resolves the round when
    /// freezetag_CheckWinner produces a result. Call each tick after <see cref="SetRoster"/>.
    /// </summary>
    public void Tick() => Handler?.Tick();

    public void Deactivate()
    {
        if (_deathHandler is null)
            return;
        Combat.Death.Remove(_deathHandler);
        _deathHandler = null;
    }

    public int AssignTeam(Player joiner, IReadOnlyList<Player> roster)
        => TeamBalance.JoinSmallestTeam(joiner, roster, TeamCount);

    /// <summary>True if <paramref name="p"/> is currently frozen (QC STAT(FROZEN)).</summary>
    public bool IsFrozen(Player p) => Frozen.TryGetValue(p, out var st) && st.IsFrozen;

    /// <summary>QC freezetag_isEliminated: a frozen OR dead player counts as out for the round check.</summary>
    public bool IsEliminated(Player p) => p.IsDead || IsFrozen(p);

    /// <summary>
    /// QC PlayerDies (round active): the fragged player is frozen instead of respawning. Applies the
    /// freeze/score matrix (<see cref="Freeze"/>). Returns false so other Death subscribers run.
    /// </summary>
    private bool OnDeath(ref DeathEvent ev)
    {
        if (ev.Victim is not Player victim)
            return false;
        if (MatchEnded)
            return false;

        Freeze(victim, ev.Attacker as Player);
        return false;
    }

    /// <summary>
    /// Freeze <paramref name="targ"/> (QC freezetag_Freeze + freezetag_Add_Score): mark frozen with HP 1,
    /// reset revive progress, and apply the score matrix — self freeze → victim −1; teammate freeze →
    /// attacker −1 + victim −1; enemy freeze → attacker +1 + victim −1; and a NULL/non-player attacker
    /// (frozen by the gametype rules themselves) → NO score change. No-op if already frozen.
    /// </summary>
    public void Freeze(Player targ, Player? attacker)
    {
        if (IsFrozen(targ))
            return;

        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        var st = GetState(targ);
        st.IsFrozen = true;
        st.ReviveProgress = 0f;
        st.FrozenTime = now;
        // QC freezetag_Freeze: auto-thaw timer (g_freezetag_frozen_maxtime), 0 = never.
        float maxtime = FrozenMaxtime;
        st.FrozenTimeout = maxtime > 0f ? now + maxtime : 0f;
        // QC sets RES_HEALTH = 1 on freeze; the entity stays "dead" for combat but counts via IsFrozen.
        targ.SetResource(ResourceType.Health, 1f);
        // QC STAT(FROZEN): apply the shared Frozen status effect so the rest of the sim sees the freeze.
        if (StatusEffectsCatalog.Frozen is { } frozenDef)
            StatusEffectsCatalog.Apply(targ, frozenDef, maxtime > 0f ? maxtime : 0f, 1f, attacker);

        // ----- score matrix (QC freezetag_Add_Score, sv_freezetag.qc:162-181) -----
        if (ReferenceEquals(attacker, targ))
        {
            // QC (attacker == targ): froze your own dumb self — victim −1 (counted as suicide already).
            targ.ScoreFrags -= 1;
        }
        else if (attacker is not null) // QC IS_PLAYER(attacker): froze a teammate or an enemy
        {
            if (Teams.SameTeam(attacker, targ))
                attacker.ScoreFrags -= 1; // froze a teammate
            else
                attacker.ScoreFrags += 1; // froze an enemy
            targ.ScoreFrags -= 1;
        }
        // QC else: NULL / non-player attacker — got frozen by the gametype rules themselves → NO score change.
    }

    /// <summary>
    /// QC freezetag_Unfreeze: clear the freeze and restore the player to alive. Used both by a successful
    /// revive and by round reset. Does not award score by itself (the revive path does that).
    /// </summary>
    public void Unfreeze(Player targ)
    {
        if (Frozen.TryGetValue(targ, out var st))
        {
            st.IsFrozen = false;
            st.ReviveProgress = 0f;
            st.FrozenTimeout = 0f;
        }
        targ.DeadState = DeadFlag.No;
        // QC freezetag_Unfreeze: clear the Frozen status effect and restore full start health.
        if (StatusEffectsCatalog.Frozen is { } frozenDef)
            StatusEffectsCatalog.Remove(targ, frozenDef);
        targ.SetResource(ResourceType.Health, StartHealth);
    }

    /// <summary>
    /// Advance revive of <paramref name="frozen"/> by a nearby living teammate <paramref name="reviver"/>
    /// (QC PlayerPreThink reviving loop, distilled). Accumulates <see cref="ReviveSpeed"/> * <paramref name="dt"/>
    /// into the freeze's progress; on reaching 1.0 the player is thawed and BOTH get SCORE +1 (reviver also
    /// FREEZETAG_REVIVALS, modeled here just as the score). The caller supplies the range test. Returns true
    /// if the player was fully revived this call.
    /// </summary>
    public bool TryRevive(Player frozen, Player reviver, float dt)
    {
        if (!IsFrozen(frozen) || frozen.IsDead)
            return false;
        if (!Teams.SameTeam(frozen, reviver) || reviver.IsDead || IsFrozen(reviver))
            return false;

        var st = GetState(frozen);
        st.ReviveProgress = System.Math.Clamp(st.ReviveProgress + dt * ReviveSpeed, 0f, 1f);
        if (st.ReviveProgress < 1f)
            return false;

        Unfreeze(frozen);
        // QC freezetag PlayerPreThink full-revive: each nearby reviver gets FREEZETAG_REVIVALS +1 and SCORE +1;
        // the revived player also returns to play.
        AddRevival(reviver);
        reviver.ScoreFrags += 1;
        frozen.ScoreFrags += 1;
        return true;
    }

    /// <summary>QC <c>GameRules_scoring_add(it, FREEZETAG_REVIVALS, +1)</c>: credit a reviver one revival (the
    /// scoreboard "revivals" column). Routed through the unified score table so it networks + sorts.</summary>
    private static void AddRevival(Player reviver)
    {
        Scoring.ScoreField? revivals = Scoring.GameScores.Field("FREEZETAG_REVIVALS");
        if (revivals is not null) Scoring.GameScores.AddToPlayer(reviver, revivals, 1);
    }

    /// <summary>
    /// QC PlayerPreThink revive loop, ported over the roster: for every frozen player, accumulate revive
    /// progress while a living teammate is within reviving range (box-overlap expanded by
    /// <see cref="ReviveExtraSize"/>), decay it when nobody is near, and auto-thaw at the frozen-maxtime
    /// timeout. On reaching 1.0 the player is unfrozen and each nearby reviver gets SCORE +1. Call once per
    /// frame with the frame delta after <see cref="SetRoster"/>. Returns the number of players thawed.
    /// </summary>
    public int ReviveTick(float dt)
    {
        if (MatchEnded || _roster.Count == 0)
            return 0;
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        float extra = ReviveExtraSize;
        float speed = ReviveSpeed;
        float clear = ReviveClearSpeed;
        int thawed = 0;

        for (int i = 0; i < _roster.Count; i++)
        {
            Player frozen = _roster[i];
            if (!IsFrozen(frozen) || frozen.IsDead)
                continue;
            var st = GetState(frozen);

            // Count nearby living unfrozen teammates (QC IN_REVIVING_RANGE box-overlap).
            int revivers = 0;
            for (int j = 0; j < _roster.Count; j++)
            {
                Player it = _roster[j];
                if (ReferenceEquals(it, frozen) || it.IsDead || IsFrozen(it))
                    continue;
                if (!Teams.SameTeam(it, frozen))
                    continue;
                if (InRevivingRange(frozen, it, extra))
                    revivers++;
            }

            // Auto-thaw timeout (QC freezetag_frozen_timeout): counts as a reviver of last resort.
            bool autoThaw = st.FrozenTimeout > 0f && now >= st.FrozenTimeout;

            if (revivers == 0 && !autoThaw)
            {
                // decay progress when nobody is near (QC clearspeed)
                st.ReviveProgress = System.Math.Clamp(st.ReviveProgress - dt * clear, 0f, 1f);
                continue;
            }

            st.ReviveProgress = System.Math.Clamp(st.ReviveProgress + dt * System.Math.Max(1f / 60f, speed), 0f, 1f);
            if (autoThaw)
                st.ReviveProgress = 1f;

            if (st.ReviveProgress >= 1f)
            {
                Unfreeze(frozen);
                thawed++;
                // award each nearby reviver (QC: every reviver in range gets FREEZETAG_REVIVALS +1 and +1 SCORE)
                for (int j = 0; j < _roster.Count; j++)
                {
                    Player it = _roster[j];
                    if (ReferenceEquals(it, frozen) || it.IsDead || IsFrozen(it))
                        continue;
                    if (Teams.SameTeam(it, frozen) && InRevivingRange(frozen, it, extra))
                    {
                        AddRevival(it);
                        it.ScoreFrags += 1;
                    }
                }
            }
        }
        return thawed;
    }

    /// <summary>
    /// QC IN_REVIVING_RANGE: the reviver's bbox (expanded by <paramref name="extra"/>) overlaps the frozen
    /// player's bbox. Uses the entities' absolute bounds (falls back to a sphere test if bounds are unset).
    /// </summary>
    public static bool InRevivingRange(Player frozen, Player reviver, float extra)
    {
        Vector3 e = new(extra, extra, extra);
        Vector3 aMin = reviver.AbsMin - e, aMax = reviver.AbsMax + e;
        Vector3 bMin = frozen.AbsMin, bMax = frozen.AbsMax;
        // If bounds are degenerate (headless without a physics link), fall back to an origin distance test.
        if (aMin == aMax && bMin == bMax)
            return Vector3.Distance(frozen.Origin, reviver.Origin) <= extra;
        return aMin.X <= bMax.X && aMax.X >= bMin.X
            && aMin.Y <= bMax.Y && aMax.Y >= bMin.Y
            && aMin.Z <= bMax.Z && aMax.Z >= bMin.Z;
    }

    /// <summary>
    /// QC PutPlayerInServer FT branch: give the round's start loadout — g_ft_start_health/armor (100/100) and
    /// full ammo. Called by the host when (re)spawning a player into an FT round.
    /// </summary>
    public void ApplyStartItems(Player p)
    {
        float h = StartHealth;
        p.MaxHealth = h;
        p.SetResource(ResourceType.Health, h);
        p.SetResource(ResourceType.Armor, StartArmor);
        p.SetResource(ResourceType.Shells,  TryCvar("g_ft_start_ammo_shells", out float s) ? s : 60f);
        p.SetResource(ResourceType.Bullets, TryCvar("g_ft_start_ammo_nails", out float n) ? n : 320f);
        p.SetResource(ResourceType.Rockets, TryCvar("g_ft_start_ammo_rockets", out float r) ? r : 160f);
        p.SetResource(ResourceType.Cells,   TryCvar("g_ft_start_ammo_cells", out float c) ? c : 180f);
        p.SetResource(ResourceType.Fuel,    TryCvar("g_ft_start_ammo_fuel", out float f) ? f : 0f);
    }

    /// <summary>
    /// QC freezetag_CheckTeams (canRoundStart): a round may begin once every active team has a live,
    /// non-frozen player (and at least one player is present).
    /// </summary>
    public bool CheckTeams()
    {
        int totalPlayers = 0;
        var alive = new Dictionary<int, int>();
        foreach (int team in Teams.Active(TeamCount))
            alive[team] = 0;
        for (int i = 0; i < _roster.Count; i++)
        {
            Player p = _roster[i];
            int t = (int)p.Team;
            if (!alive.ContainsKey(t))
                continue;
            totalPlayers++;
            if (!IsEliminated(p))
                alive[t] += 1;
        }
        if (totalPlayers == 0)
            return false;
        foreach (var kv in alive)
            if (kv.Value == 0)
                return false;
        return true;
    }

    /// <summary>
    /// QC freezetag_CheckWinner (canRoundEnd): a team is alive while it has a non-frozen, non-dead player.
    /// When exactly one team is alive (or the round time limit elapses with one team left) it wins the round
    /// (ST_FT_ROUNDS +1). Returns the winning team (&gt;0), -1 for a tie, or 0 if the round continues.
    /// </summary>
    public int CheckWinner()
    {
        if (MatchEnded)
            return -1;

        Round.AliveByTeam.Clear();
        int totalPlayers = 0;
        foreach (int team in Teams.Active(TeamCount))
            Round.AliveByTeam[team] = 0;
        for (int i = 0; i < _roster.Count; i++)
        {
            Player p = _roster[i];
            int t = (int)p.Team;
            if (!Round.AliveByTeam.ContainsKey(t))
                continue;
            totalPlayers++;
            if (!IsEliminated(p))
                Round.AliveByTeam[t] += 1;
        }
        if (totalPlayers == 0)
            return 0;

        int aliveTeams = 0, soleTeam = Teams.None;
        foreach (var kv in Round.AliveByTeam)
            if (kv.Value > 0) { aliveTeams++; soleTeam = kv.Key; }

        if (aliveTeams > 1)
            return 0; // round still live

        int winner = aliveTeams == 1 ? soleTeam : -1;
        if (winner > 0)
        {
            Scoring.GameScores.AddToTeam(winner, Scoring.GameScores.TeamSlotSecondary, 1); // QC TeamScore_AddToTeam(winner, ST_FT_ROUNDS, +1)
            Round.Number++;
            UpdateLeaderAndCheckLimit();
        }
        else
        {
            Round.Number++; // mutual elimination → tie, no score
        }
        return winner;
    }

    /// <summary>
    /// QC freezetag_CheckWinner via Team_GetWinnerAliveTeam: a team is alive while it has a non-frozen,
    /// non-dead player. If exactly one team is alive, it wins the round (ST_FT_ROUNDS +1) and a new round
    /// is armed. Call each tick. Returns the winning team color code, or <see cref="Teams.None"/>.
    /// </summary>
    public int CheckRound(IReadOnlyList<Player> roster)
    {
        if (MatchEnded)
            return Teams.None;

        Round.AliveByTeam.Clear();
        int totalPlayers = 0;
        foreach (int team in Teams.Active(TeamCount))
            Round.AliveByTeam[team] = 0;

        for (int i = 0; i < roster.Count; i++)
        {
            Player p = roster[i];
            int t = (int)p.Team;
            if (t == Teams.None)
                continue;
            totalPlayers++;
            if (!IsEliminated(p) && Round.AliveByTeam.ContainsKey(t))
                Round.AliveByTeam[t] += 1;
        }

        if (totalPlayers == 0)
            return Teams.None;

        int aliveTeams = 0, winner = Teams.None;
        foreach (var (team, alive) in Round.AliveByTeam)
            if (alive > 0) { aliveTeams++; winner = team; }

        if (aliveTeams > 1)
            return Teams.None;

        if (aliveTeams == 1)
        {
            Scoring.GameScores.AddToTeam(winner, Scoring.GameScores.TeamSlotSecondary, 1); // QC TeamScore_AddToTeam(winner, ST_FT_ROUNDS, +1)
            Round.Number++;
            UpdateLeaderAndCheckLimit();
            return winner;
        }

        Round.Number++; // mutual elimination → tied round, no score
        return Teams.None;
    }

    public int GetTeamRounds(int team) => Scoring.GameScores.TeamScore(team, Scoring.GameScores.TeamSlotSecondary);

    /// <summary>
    /// QC STAT(REDALIVE..PINKALIVE) source (<c>freezetag_count_alive_players</c>, sv_freezetag.qc:22-50):
    /// living (unfrozen, undead — QC <c>GetResource(RES_HEALTH) &gt;= 1 &amp;&amp; !STAT(FROZEN)</c>) players on
    /// <paramref name="teamCode"/> (a <see cref="Teams"/> color code) per the last recount. QC recounts on
    /// freeze/revive/death/spawn EVENTS; the port's per-frame <see cref="CheckRound"/>
    /// (GameWorld.DriveGametypeFrame) recomputes the same pure function of the Frozen/IsDead state —
    /// value-identical, so no second event-driven recount path is added. Inactive/unknown teams read 0.
    /// </summary>
    public int AliveCount(int teamCode) => Round.AliveByTeam.TryGetValue(teamCode, out int n) ? n : 0;

    public void UpdateLeaderAndCheckLimit()
    {
        // QC: FT teams rank by the team primary slot ST_FT_ROUNDS (round wins). LeaderTeam / SecondTeam read the
        // flag-aware two-slot compare from GameScores (the source of truth); the round/lead-limit check uses it.
        int bestTeam = Scoring.GameScores.LeaderTeam();
        LeaderTeam = bestTeam;
        if (bestTeam == Teams.None)
            return;

        int bestScore = GetTeamRounds(bestTeam);
        float limit = RoundLimit;
        if (limit > 0f && bestScore >= limit)
            MatchEnded = true;

        int secondTeam = Scoring.GameScores.SecondTeam();
        float leadLimit = LeadLimit;
        if (leadLimit > 0f && secondTeam != Teams.None && (bestScore - GetTeamRounds(secondTeam)) >= leadLimit)
            MatchEnded = true;
    }

    private FrozenState GetState(Player p)
    {
        if (!Frozen.TryGetValue(p, out var st))
        {
            st = new FrozenState();
            Frozen[p] = st;
        }
        return st;
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

/// <summary>
/// Per-player Freeze Tag state — the Godot-free essence of QC's STAT(FROZEN) + STAT(REVIVE_PROGRESS) and
/// the freeze timing fields (including the auto-revive timeout). The ice entity model and waypoint sprite
/// remain client concerns.
/// </summary>
public sealed class FrozenState
{
    /// <summary>QC STAT(FROZEN): the player is encased in ice and counts as eliminated.</summary>
    public bool IsFrozen;

    /// <summary>QC STAT(REVIVE_PROGRESS): 0..1 thaw progress from nearby teammates.</summary>
    public float ReviveProgress;

    /// <summary>Sim time the player was frozen (QC freezetag_frozen_time).</summary>
    public float FrozenTime;

    /// <summary>Sim time the freeze auto-thaws (QC freezetag_frozen_timeout); 0 = no auto-thaw.</summary>
    public float FrozenTimeout;
}
