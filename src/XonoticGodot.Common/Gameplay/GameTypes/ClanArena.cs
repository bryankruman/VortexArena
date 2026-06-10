using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Clan Arena — port of <c>CLASS(ClanArena, Gametype)</c>
/// (common/gametypes/gametype/clanarena/{clanarena.qh,sv_clanarena.qc}). A round-based elimination
/// team mode: no respawns within a round; the last team with a living player wins the round and gains
/// one round point. First team to the round limit (QC fraglimit_override / GameRules_limit_score, default
/// 10) wins the match. Kills award NO individual frags (QC GiveFragsForKill → 0); damage dealt accrues to
/// the player's SCORE (g_ca_damage2score) for the scoreboard only, not the win condition.
///
/// QC defaults (gametype_init): "timelimit=20 pointlimit=10 teams=2 leadlimit=6" (legacydefaults "10 20 0").
///
/// Faithfully ported (Godot-free essence):
///  - smallest-team assignment on join (server/teamplay.qc, via <see cref="TeamBalance"/>);
///  - the round lifecycle: dead players stay dead (no respawn) until the round resolves
///    (CA_CheckWinner: when exactly one team has alive players, that team scores a round);
///  - round-limit win condition (GameRules_limit_score → fraglimit) on team round wins;
///  - kills give no frags (GiveFragsForKill → 0).
///
/// Faithfully ported (objective layer):
///  - per-round warmup + countdown timing via the shared <see cref="RoundHandler"/> (QC round_handler_Spawn
///    with CA_CheckTeams / CA_CheckWinner / CA_RoundStart), driven by <see cref="Tick"/>;
///  - stalemate prevention on the round time limit (<see cref="PreventStalemate"/>, QC CA_PreventStalemate by
///    survivor count and/or total team health);
///  - CA's start-loadout (<see cref="ApplyStartItems"/>, QC g_ca_start_health/armor 200/200 + ammo);
///  - the g_ca_damage2score scoreboard accrual (<see cref="AddDamageScore"/>).
///
/// Faithfully ported (damage rule):
///  - the no-friendly-fire damage filter (QC ca Damage_Calculate): a live player takes ZERO damage from a
///    teammate, from themselves, or from a fall (<see cref="OnDamageCalculate"/> on the shared DamageCalculate
///    hook); mirror damage is always zeroed. (The spectate-enemies rule is the server-side
///    <see cref="XonoticGodot.Server"/> SpectatorRules system, fed by g_ca_spectate_enemies.)
/// </summary>
[GameType]
public sealed class ClanArena : GameType
{
    // ----- round-limit cvars + default (CA uses fraglimit_override; legacydefaults rounds=10) -----
    private const string CvarRoundLimitCa = "g_ca_point_limit";      // CA-specific
    private const string CvarRoundLimit   = "fraglimit";            // GameRules_limit_score target
    private const string CvarLeadLimitCa  = "g_ca_point_leadlimit";
    private const string CvarLeadLimit    = "leadlimit";
    private const float  DefaultRoundLimit = 10f;
    private const float  DefaultLeadLimit  = 6f;

    // ----- team count cvars (g_ca_teams_override >= 2 ? override : g_ca_teams), clamped 2..4 -----
    private const string CvarTeamsOverride = "g_ca_teams_override";
    private const string CvarTeams         = "g_ca_teams";
    private const int    DefaultTeams      = 2;

    // Per-team round wins (QC ST_CA_ROUNDS, team slot 1 — the TEAM primary) now live in the unified GameScores
    // two-slot team store — the source of truth (common/scores.qh). CA's player primary is SP_SCORE (the
    // damage2score accrual); the team primary is ST_CA_ROUNDS. Read/written via GetTeamRounds / GameScores.AddToTeam.

    /// <summary>Round-elimination bookkeeping (QC round_handler / CA_count_alive_players).</summary>
    public readonly RoundState Round = new();

    /// <summary>The round-phase driver (QC round_handler) — created on <see cref="Activate"/>.</summary>
    public RoundHandler? Handler { get; private set; }

    /// <summary>The roster the round handler evaluates (set by the host before <see cref="Tick"/>).</summary>
    private IReadOnlyList<Player> _roster = System.Array.Empty<Player>();

    public bool MatchEnded { get; private set; }
    public int LeaderTeam { get; private set; }

    // ----- start-loadout cvars + defaults (QC g_ca_start_*; 200/200 + full ammo) -----
    private const string CvarStartHealth = "g_ca_start_health";
    private const string CvarStartArmor  = "g_ca_start_armor";
    private const string CvarDamage2Score = "g_ca_damage2score";
    private const string CvarPreventStalemate = "g_ca_prevent_stalemate";
    private const string CvarWarmup = "g_ca_warmup";
    private const string CvarRoundTimelimit = "g_ca_round_timelimit";
    private const string CvarRoundEndDelay = "g_ca_round_enddelay";
    private const float  DefaultStartHealth = 200f;
    private const float  DefaultStartArmor  = 200f;
    private const float  DefaultDamage2Score = 100f; // QC: SCORE per 100 damage dealt

    public ClanArena()
    {
        NetName = "ca";
        DisplayName = "Clan Arena";
        TeamGame = true;
    }

    public override void OnInit()
    {
        // QC: GameRules_teams(true) + round_handler_Spawn(CA_CheckTeams, CA_CheckWinner, CA_RoundStart) +
        // EliminatedPlayers_Init. The round handler is created in Activate (it needs the live roster); OnInit
        // just clears state. Start-items are applied per spawn via ApplyStartItems.
    }

    /// <summary>
    /// QC sv_clanarena.qh: <c>GameRules_spawning_teams(autocvar_g_ca_team_spawns)</c> — CA gates team spawns on
    /// g_ca_team_spawns (stock default 1, so CA uses team spawnpoints by default).
    /// </summary>
    public override bool RequestsTeamSpawns => Cvar("g_ca_team_spawns", 1f) != 0f;

    /// <summary>QC g_ca_start_health (default 200): the CA spawn health.</summary>
    public float StartHealth => Cvar(CvarStartHealth, DefaultStartHealth);
    /// <summary>QC g_ca_start_armor (default 200): the CA spawn armor.</summary>
    public float StartArmor => Cvar(CvarStartArmor, DefaultStartArmor);
    /// <summary>QC g_ca_damage2score (default 100): SCORE awarded per this much damage dealt (scoreboard only).</summary>
    public float Damage2Score => Cvar(CvarDamage2Score, DefaultDamage2Score);
    /// <summary>QC g_ca_prevent_stalemate bitmask: bit0 = break ties by survivors, bit1 = by total team health.</summary>
    public int PreventStalemateMode => (int)Cvar(CvarPreventStalemate, 0f);

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

    /// <summary>Round limit in force (g_ca_point_limit, else fraglimit, else 10). 0 == unlimited.</summary>
    public float RoundLimit
    {
        get
        {
            if (TryCvar(CvarRoundLimitCa, out float rl)) return rl;
            if (TryCvar(CvarRoundLimit, out float fl)) return fl;
            return DefaultRoundLimit;
        }
    }

    public float LeadLimit
    {
        get
        {
            if (TryCvar(CvarLeadLimitCa, out float ll)) return ll;
            if (TryCvar(CvarLeadLimit, out float l)) return l;
            return DefaultLeadLimit;
        }
    }

    /// <summary>HookHandler so Deactivate can remove the exact instance (CA's PlayerDies path).</summary>
    private HookHandler<DeathEvent>? _deathHandler;
    private HookHandler<MutatorHooks.DamageCalculateArgs>? _damageHandler;

    public void Activate()
    {
        if (_deathHandler is not null)
            return;
        MatchEnded = false;
        LeaderTeam = Teams.None;
        Round.Reset();
        Scoring.GameScores.ResetTeams(); // QC Score_ClearAll at match start: zero both team slots before declaring

        // QC sv_clanarena.qh GameRules_scoring(teamplay_bitmask, SFL_SORT_PRIO_PRIMARY, 0, {
        // field_team(ST_CA_ROUNDS, "rounds", PRIMARY); }): the player primary is SP_SCORE (damage2score accrual);
        // ST_SCORE (team slot 0) has no prio (stprio=0); ST_CA_ROUNDS (team slot 1) "rounds" is the TEAM primary
        // (round wins). No per-player gametype columns.
        Scoring.GameScores.ScoreRulesBasics(teams: true);
        Scoring.GameScores.TeamRulesBasics(scorePrio: Scoring.ScoreFlags.None); // ST_SCORE (slot 0) stprio = 0
        Scoring.GameScores.SetTeamLabel(Scoring.GameScores.TeamSlotSecondary, "rounds", Scoring.ScoreFlags.SortPrioPrimary); // ST_CA_ROUNDS (slot 1)
        Scoring.GameScores.SetSortKeys(Scoring.GameScores.Score); // player primary = SP_SCORE
        Scoring.GameScores.SeedTeams(TeamCount); // zero both team slots for the active teams (stable leader scan)

        // QC round_handler_Spawn(CA_CheckTeams, CA_CheckWinner, CA_RoundStart).
        Handler = new RoundHandler(() => Api.Services is not null ? Api.Clock.Time : 0f)
        {
            CanRoundStart = CheckTeams,
            CanRoundEnd = () => CheckWinner() != 0,
            OnRoundStart = () => { /* QC CA_RoundStart: snapshot teams; spawn-allow is the host's job */ },
        };
        Handler.Init(Cvar(CvarRoundEndDelay, 5f), Cvar(CvarWarmup, 5f), Cvar(CvarRoundTimelimit, 180f));

        _deathHandler = OnDeath;
        Combat.Death.Add(_deathHandler);

        // QC ca Damage_Calculate: install the no-friendly-fire filter (zero self/team/fall damage on live players).
        _damageHandler = OnDamageCalculate;
        MutatorHooks.DamageCalculate.Add(_damageHandler);
    }

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(ca, Damage_Calculate)</c>: a live player takes no damage from a teammate, from
    /// themselves, or from a fall — and CA never mirrors damage. Knockback (force) is left intact (QC only zeros
    /// the damage), so a teammate can still nudge you without hurting you.
    /// </summary>
    private bool OnDamageCalculate(ref MutatorHooks.DamageCalculateArgs a)
    {
        if (a.Target is not Player target || target.DeadState != DeadFlag.No)
        {
            a.MirrorDamage = 0f; // QC: frag_mirrordamage = 0 unconditionally in CA
            return false;
        }
        bool isFall = DeathTypes.BaseOf(a.DeathType) == DeathTypes.Fall;
        bool selfOrTeam = a.Attacker is Player atk && (ReferenceEquals(atk, target) || Teams.SameTeam(atk, target));
        if (isFall || selfOrTeam)
            a.Damage = 0f;
        a.MirrorDamage = 0f;
        return false;
    }

    /// <summary>Provide the round handler with the current roster (call before <see cref="Tick"/>).</summary>
    public void SetRoster(IReadOnlyList<Player> roster) => _roster = roster;

    /// <summary>
    /// Advance the CA round handler one frame (QC round_handler_Think). Resolves the round when CA_CheckWinner
    /// produces a result. Call each tick after <see cref="SetRoster"/>.
    /// </summary>
    public void Tick() => Handler?.Tick();

    public void Deactivate()
    {
        if (_deathHandler is null)
            return;
        Combat.Death.Remove(_deathHandler);
        _deathHandler = null;
        if (_damageHandler is not null)
        {
            MutatorHooks.DamageCalculate.Remove(_damageHandler);
            _damageHandler = null;
        }
    }

    public int AssignTeam(Player joiner, IReadOnlyList<Player> roster)
        => TeamBalance.JoinSmallestTeam(joiner, roster, TeamCount);

    /// <summary>
    /// QC CA PlayerDies: the kill gives no frags (GiveFragsForKill → 0). The victim is NOT scheduled to
    /// respawn — in CA the dead stay out until the round resets. We just leave the death to the round check.
    /// </summary>
    private bool OnDeath(ref DeathEvent ev)
    {
        // No per-kill scoring in CA: round wins are the only score that affects the limit (GiveFragsForKill → 0).
        // Damage-to-score accrual happens continuously in the damage pipeline (see AddDamageScore), not here.
        // The victim is already marked dead by the damage system; CheckWinner / CheckRound resolves the round.
        return false;
    }

    /// <summary>
    /// QC CA Damage_Calculate accrual (g_ca_damage2score): credit the scoreboard SCORE of an attacker for
    /// damage dealt to an enemy — <c>damage / 100 * g_ca_damage2score</c>. This is cosmetic (it does not feed
    /// the round-win limit). The damage pipeline calls this when CA is active. No-op for self/teamdamage.
    /// </summary>
    public void AddDamageScore(Player attacker, Player victim, float damageDealt)
    {
        if (MatchEnded || damageDealt <= 0f)
            return;
        if (ReferenceEquals(attacker, victim) || Teams.SameTeam(attacker, victim))
            return;
        float d2s = Damage2Score;
        if (d2s <= 0f)
            return;
        // QC GameRules_scoring_add_float2int(scorer, SCORE, damage, ..., g_ca_damage2score): the accumulator
        // adds 1 point per (100/d2s) damage. We model the integer SCORE gain directly.
        attacker.ScoreFrags += (int)(damageDealt * d2s / 100f);
    }

    /// <summary>
    /// QC PutPlayerInServer CA branch: give the round's start loadout — g_ca_start_health/armor (200/200) and
    /// full ammo. Called by the host when (re)spawning a player into a CA round.
    /// </summary>
    public void ApplyStartItems(Player p)
    {
        float h = StartHealth;
        p.MaxHealth = h;
        p.SetResource(ResourceType.Health, h);
        p.SetResource(ResourceType.Armor, StartArmor);
        p.SetResource(ResourceType.Shells,  Cvar("g_ca_start_ammo_shells", 60f));
        p.SetResource(ResourceType.Bullets, Cvar("g_ca_start_ammo_nails", 320f));
        p.SetResource(ResourceType.Rockets, Cvar("g_ca_start_ammo_rockets", 160f));
        p.SetResource(ResourceType.Cells,   Cvar("g_ca_start_ammo_cells", 180f));
        p.SetResource(ResourceType.Fuel,    Cvar("g_ca_start_ammo_fuel", 0f));
    }

    /// <summary>
    /// QC CA_CheckTeams (canRoundStart): the countdown may proceed once every active team has at least one
    /// live player (and at least one player is present). Mirrors Team_GetNumberOfAliveTeams == AVAILABLE_TEAMS.
    /// </summary>
    public bool CheckTeams()
    {
        int totalPlayers = 0;
        Round.AliveByTeam.Clear();
        foreach (int team in Teams.Active(TeamCount))
            Round.AliveByTeam[team] = 0;
        for (int i = 0; i < _roster.Count; i++)
        {
            Player p = _roster[i];
            int t = (int)p.Team;
            if (!Round.AliveByTeam.ContainsKey(t))
                continue;
            totalPlayers++;
            if (!p.IsDead)
                Round.AliveByTeam[t] += 1;
        }
        if (totalPlayers == 0)
            return false;
        foreach (var kv in Round.AliveByTeam)
            if (kv.Value == 0)
                return false; // a team has no living players → can't start yet
        return true;
    }

    /// <summary>
    /// QC CA_CheckWinner (canRoundEnd core): count living players per team; if the round time limit elapsed
    /// apply <see cref="PreventStalemate"/>, else award the round to the sole surviving team. Returns the
    /// winning team (&gt;0), -1 for a tie, -2 for an undecidable stalemate, or 0 if the round continues. When a
    /// winner is produced (&gt;0) it banks a round point and applies the match win condition.
    /// </summary>
    public int CheckWinner()
    {
        if (MatchEnded)
            return -2;

        // Recount alive teams over the roster.
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
            if (!p.IsDead)
                Round.AliveByTeam[t] += 1;
        }
        if (totalPlayers == 0)
            return 0;

        int aliveTeams = 0, soleTeam = Teams.None;
        foreach (var kv in Round.AliveByTeam)
            if (kv.Value > 0) { aliveTeams++; soleTeam = kv.Key; }

        int winner = 0;
        // Round time limit elapsed → resolve by stalemate-prevention (or declare a stalemate).
        bool timeUp = Handler is not null && Handler.RoundEndTime > 0f
                      && (Api.Services is not null ? Api.Clock.Time : 0f) >= Handler.RoundEndTime;
        if (timeUp)
            winner = (PreventStalemateMode != 0) ? PreventStalemate() : -2;

        if (winner == 0)
        {
            if (aliveTeams > 1)
                return 0;                 // round still live
            winner = aliveTeams == 1 ? soleTeam : -1; // one team left → winner; zero → tie
        }

        if (winner > 0)
        {
            Scoring.GameScores.AddToTeam(winner, Scoring.GameScores.TeamSlotSecondary, 1); // QC TeamScore_AddToTeam(winner, ST_CA_ROUNDS, +1)
            Round.Number++;
            UpdateLeaderAndCheckLimit();
        }
        else
        {
            Round.Number++; // tie / stalemate: a round elapsed, no team score
        }
        return winner;
    }

    /// <summary>
    /// QC CA_PreventStalemate: at the round time limit, break the tie by the configured criteria — bit0 picks
    /// the team with more survivors, bit1 the team with more total (health+armor). Returns the winning team,
    /// or -2 when even those are equal (a true stalemate). Survivor counts come from <see cref="Round.AliveByTeam"/>.
    /// </summary>
    public int PreventStalemate()
    {
        int mode = PreventStalemateMode;

        if ((mode & 1) != 0) // by survivor count
        {
            int best = Teams.None, second = Teams.None, bestN = -1, secondN = -1;
            foreach (var kv in Round.AliveByTeam)
            {
                if (kv.Value > bestN) { second = best; secondN = bestN; best = kv.Key; bestN = kv.Value; }
                else if (kv.Value > secondN) { second = kv.Key; secondN = kv.Value; }
            }
            if (bestN != secondN && best != Teams.None)
                return best;
        }

        if ((mode & 2) != 0) // by total team health + armor
        {
            var health = new Dictionary<int, int>();
            foreach (int team in Teams.Active(TeamCount)) health[team] = 0;
            for (int i = 0; i < _roster.Count; i++)
            {
                Player p = _roster[i];
                int t = (int)p.Team;
                if (p.IsDead || !health.ContainsKey(t))
                    continue;
                health[t] += (int)p.GetResource(ResourceType.Health) + (int)p.GetResource(ResourceType.Armor);
            }
            int best = Teams.None, second = Teams.None, bestH = -1, secondH = -1;
            foreach (var kv in health)
            {
                if (kv.Value > bestH) { second = best; secondH = bestH; best = kv.Key; bestH = kv.Value; }
                else if (kv.Value > secondH) { second = kv.Key; secondH = kv.Value; }
            }
            if (bestH != secondH && best != Teams.None)
                return best;
        }

        return -2; // genuinely equal — can't break the stalemate
    }

    private static float Cvar(string name, float fallback) => TryCvar(name, out float v) ? v : fallback;

    /// <summary>
    /// Count living players per team over the current roster (QC CA_count_alive_players), then resolve the
    /// round: if exactly one team has any living player, that team wins the round (QC CA_CheckWinner →
    /// Team_GetWinnerAliveTeam → TeamScore_AddToTeam ST_CA_ROUNDS +1) and a new round is armed. Call each
    /// tick. Returns the winning team color code if a round just resolved, else <see cref="Teams.None"/>.
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
            if (!p.IsDead && Round.AliveByTeam.ContainsKey(t))
                Round.AliveByTeam[t] += 1;
        }

        // Need at least two players present for a round to be live (QC: total_players == 0 → no round yet).
        if (totalPlayers == 0)
            return Teams.None;

        int aliveTeams = 0, winner = Teams.None;
        foreach (var (team, alive) in Round.AliveByTeam)
            if (alive > 0) { aliveTeams++; winner = team; }

        // Round is still in progress while 2+ teams have survivors.
        if (aliveTeams > 1)
            return Teams.None;

        // Exactly one (or zero) team has survivors → round over. One survivor team scores a round.
        if (aliveTeams == 1)
        {
            Scoring.GameScores.AddToTeam(winner, Scoring.GameScores.TeamSlotSecondary, 1); // QC TeamScore_AddToTeam(winner, ST_CA_ROUNDS, +1)
            Round.Number++;
            UpdateLeaderAndCheckLimit();
            return winner;
        }

        // Zero alive (mutual elimination / draw) — start a fresh round, no score (QC ROUND_TIED/OVER).
        Round.Number++;
        return Teams.None;
    }

    public int GetTeamRounds(int team) => Scoring.GameScores.TeamScore(team, Scoring.GameScores.TeamSlotSecondary);

    /// <summary>
    /// QC STAT(REDALIVE..PINKALIVE) source (<c>CA_count_alive_players</c>, sv_clanarena.qc:17-43): living
    /// players on <paramref name="teamCode"/> (a <see cref="Teams"/> color code) per the last recount.
    /// <see cref="CheckTeams"/>/<see cref="CheckWinner"/>/<see cref="CheckRound"/> all refresh
    /// <see cref="RoundState.AliveByTeam"/>, and the live path (GameWorld.DriveGametypeFrame → CheckRound)
    /// runs per frame — matching QC's per-frame recount (CA_CheckWinner is the round_handler canRoundEnd
    /// callback, polled every server frame while a round is live). Inactive/unknown teams read 0.
    /// </summary>
    public int AliveCount(int teamCode) => Round.AliveByTeam.TryGetValue(teamCode, out int n) ? n : 0;

    /// <summary>
    /// QC <c>ca_isEliminated</c> (sv_clanarena.qc:243-250) approximated: QC = INGAME_JOINED &amp;&amp;
    /// (IS_DEAD || frags == FRAGS_PLAYER_OUT_OF_GAME), or INGAME_JOINING. The port has no INGAME_JOINING /
    /// FRAGS_PLAYER_OUT_OF_GAME state yet, so eliminated = dead — a late joiner waiting for the next round
    /// won't grey on the scoreboard until a spectate/INGAME pass adds that state.
    /// </summary>
    public bool IsEliminatedPlayer(Player p) => p.IsDead;

    public void UpdateLeaderAndCheckLimit()
    {
        // QC: CA teams rank by the team primary slot ST_CA_ROUNDS (round wins). LeaderTeam / SecondTeam read the
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
/// Round-elimination state shared by the round-based team modes (CA, FreezeTag) — the Godot-free essence
/// of QC's round_handler globals + CA_count_alive_players. Tracks the current round number and the live
/// player count per team. The wait/countdown/end-delay timing now lives in the shared
/// <see cref="RoundHandler"/>; this struct holds the per-round survivor bookkeeping.
/// </summary>
public sealed class RoundState
{
    /// <summary>1-based round counter (incremented when a round resolves).</summary>
    public int Number;

    /// <summary>Absolute sim time the round's timelimit expires (QC round_handler_GetEndTime); 0 = none.</summary>
    public float EndTime;

    /// <summary>Living players per team, keyed by <see cref="Teams"/> color code (recomputed each check).</summary>
    public readonly Dictionary<int, int> AliveByTeam = new();

    public void Reset()
    {
        Number = 0;
        EndTime = 0f;
        AliveByTeam.Clear();
    }
}
