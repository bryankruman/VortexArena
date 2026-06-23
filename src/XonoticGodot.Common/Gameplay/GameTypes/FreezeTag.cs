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
/// self → −1; reviving a teammate → reviver FREEZETAG_REVIVALS +1 plus SCORE — with revive_time_to_score
/// ON (the stock default) the SCORE accrues over time (+1 per 1.5 s reviving) rather than on completion,
/// and the revived player gets no SCORE. Round wins are the team score that drives the win condition.
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
///    PlayerPreThink IN_REVIVING_RANGE box-overlap) with the time-to-score model (revive_time_to_score 1.5 /
///    revive_speed_t2s 0.25, progress not cleared out of range, SCORE accrued over time), the auto-revive
///    timeout + smooth base-progress ramp (g_freezetag_frozen_maxtime / _revive_auto / _revive_auto_progress),
///    and the post-revive spawn shield (g_freezetag_revive_spawnshield);
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
    private const string CvarReviveSpeed = "g_freezetag_revive_speed";       // progress/sec when a teammate is in range (t2s OFF)
    private const string CvarReviveSpeedT2s = "g_freezetag_revive_speed_t2s"; // progress/sec when a teammate is in range (t2s ON)
    private const string CvarReviveClearSpeed = "g_freezetag_revive_clearspeed"; // progress/sec decay out of range
    private const string CvarReviveExtraSize = "g_freezetag_revive_extra_size";  // bbox expansion for the range test
    private const string CvarReviveTimeToScore = "g_freezetag_revive_time_to_score"; // seconds of reviving per +1 SCORE (0 = off)
    private const string CvarReviveAuto = "g_freezetag_revive_auto";         // auto-thaw on/off gate
    private const string CvarReviveAutoProgress = "g_freezetag_revive_auto_progress"; // ramp the thaw ring from freeze time
    private const string CvarReviveSpawnShield = "g_freezetag_revive_spawnshield"; // post-revive spawn-shield seconds
    private const string CvarFrozenMaxtime = "g_freezetag_frozen_maxtime";   // auto-thaw after this many seconds (0 = off)
    private const string CvarStartHealth = "g_ft_start_health";
    private const string CvarStartArmor  = "g_ft_start_armor";
    private const string CvarWarmup = "g_freezetag_warmup";
    private const string CvarRoundTimelimit = "g_freezetag_round_timelimit";
    private const string CvarRoundEndDelay = "g_freezetag_round_enddelay";
    private const float  DefaultReviveSpeed = 0.4f;
    private const float  DefaultReviveSpeedT2s = 0.25f;
    private const float  DefaultReviveClearSpeed = 1.6f;
    private const float  DefaultReviveExtraSize = 100f;
    private const float  DefaultReviveTimeToScore = 1.5f;
    private const float  DefaultReviveSpawnShield = 1f;
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
    public float FrozenMaxtime    => TryCvar(CvarFrozenMaxtime, out float s) ? s : 60f;
    public float StartHealth      => TryCvar(CvarStartHealth, out float s) ? s : DefaultStartHealth;
    public float StartArmor       => TryCvar(CvarStartArmor, out float s) ? s : DefaultStartArmor;

    /// <summary>QC g_freezetag_revive_speed_t2s: progress/sec used while time-to-score reviving is active.</summary>
    public float ReviveSpeedT2s => TryCvar(CvarReviveSpeedT2s, out float s) ? s : DefaultReviveSpeedT2s;

    /// <summary>QC g_freezetag_revive_time_to_score (default 1.5, ON): seconds of reviving per +1 SCORE; 0 = off.</summary>
    public float ReviveTimeToScore => TryCvar(CvarReviveTimeToScore, out float s) ? s : DefaultReviveTimeToScore;

    /// <summary>QC g_freezetag_revive_auto (default 1): whether frozen players auto-thaw at the maxtime timeout.</summary>
    public bool ReviveAuto => TryCvar(CvarReviveAuto, out float s) ? s != 0f : true;

    /// <summary>QC g_freezetag_revive_auto_progress (default 1): ramp the thaw ring smoothly from freeze time.</summary>
    public bool ReviveAutoProgress => TryCvar(CvarReviveAutoProgress, out float s) ? s != 0f : true;

    /// <summary>QC g_freezetag_revive_spawnshield (default 1 s): spawn-shield granted to a freshly revived player.</summary>
    public float ReviveSpawnShield => TryCvar(CvarReviveSpawnShield, out float s) ? s : DefaultReviveSpawnShield;

    // Per-team round wins (QC ST_FT_ROUNDS, team slot 1 — the TEAM primary) now live in the unified GameScores
    // two-slot team store — the source of truth (common/scores.qh). FT's player primary is SP_SCORE (the
    // freeze/revive ±1); the team primary is ST_FT_ROUNDS. Read/written via GetTeamRounds / GameScores.AddToTeam.

    /// <summary>Per-player freeze + revive-progress state (QC STAT(FROZEN)/STAT(REVIVE_PROGRESS)).</summary>
    public readonly Dictionary<Player, FrozenState> Frozen = new();

    /// <summary>
    /// Per-reviver fractional time-to-score accumulator (QC <c>.freezetag_revive_time</c>). While a reviver
    /// stands near a frozen teammate it accrues frametime/revive_time_to_score; each whole unit yields +1 SCORE.
    /// </summary>
    private readonly Dictionary<Player, float> _reviveTime = new();

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
        _reviveTime.Clear();
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
            OnRoundStart = () => { Frozen.Clear(); _reviveTime.Clear(); }, // QC reset_map_players: thaw everyone + clear revive-time accumulators at the start of a new round
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

    public override void Deactivate()
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
        // QC freezetag_Freeze: arm the auto-thaw timer only when revive_auto is on AND maxtime > 0.
        float maxtime = FrozenMaxtime;
        st.FrozenTimeout = (ReviveAuto && maxtime > 0f) ? now + maxtime : 0f;
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
    public void Unfreeze(Player targ, float spawnShieldSeconds = 0f)
    {
        if (Frozen.TryGetValue(targ, out var st))
        {
            st.IsFrozen = false;
            st.ReviveProgress = 0f;
            st.FrozenTimeout = 0f;
            st.FrozenTime = 0f;
        }
        targ.DeadState = DeadFlag.No;
        // QC freezetag_Unfreeze: clear the Frozen status effect and restore full start health.
        if (StatusEffectsCatalog.Frozen is { } frozenDef)
            StatusEffectsCatalog.Remove(targ, frozenDef);
        targ.SetResource(ResourceType.Health, StartHealth);
        // QC PlayerPreThink full-revive: spawnshieldtime = time + g_freezetag_revive_spawnshield (so a freshly
        // thawed player isn't instantly re-frozen). The damage pipeline reads Entity.SpawnShieldExpire.
        if (spawnShieldSeconds > 0f)
            targ.SpawnShieldExpire = (Api.Services is not null ? Api.Clock.Time : 0f) + spawnShieldSeconds;
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
        // QC PlayerPreThink: t2s ON uses revive_speed_t2s (0.25); t2s OFF uses revive_speed*(1-base_progress).
        // From a single explicit reviver there is no auto-progress floor, so base_progress is 0 here.
        bool t2s = ReviveTimeToScore > 0f;
        float spd = t2s ? ReviveSpeedT2s : ReviveSpeed;
        st.ReviveProgress = System.Math.Clamp(st.ReviveProgress + dt * System.Math.Max(1f / 60f, spd), 0f, 1f);

        // QC: with t2s active, score accrues over time (revive_time_to_score seconds per +1), not on completion.
        if (t2s)
        {
            float acc = (_reviveTime.TryGetValue(reviver, out float rt) ? rt : 0f) + dt / ReviveTimeToScore;
            while (acc > 1f) { reviver.ScoreFrags += 1; acc -= 1f; }
            _reviveTime[reviver] = acc;
        }

        if (st.ReviveProgress < 1f)
            return false;

        Unfreeze(frozen, ReviveSpawnShield);
        // QC freezetag PlayerPreThink full-revive: each nearby reviver gets FREEZETAG_REVIVALS +1 (and, with t2s
        // OFF, SCORE +1 on completion; with t2s ON the SCORE was already awarded by the time-to-score accrual).
        AddRevival(reviver);
        if (!t2s)
            reviver.ScoreFrags += 1;
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
    /// <see cref="ReviveExtraSize"/>). With time-to-score active (the stock default) progress advances at
    /// revive_speed_t2s, is NOT cleared when nobody is near (it sticks at max of the manual progress and the
    /// auto-thaw ramp), and each in-range reviver accrues SCORE over time (+1 per revive_time_to_score sec);
    /// with t2s off, progress advances at revive_speed*(1-base) and decays at clearspeed out of range, and the
    /// SCORE +1 is awarded on completion. The frozen-maxtime auto-thaw (gated on revive_auto) provides a
    /// smooth base-progress ramp (revive_auto_progress) and a last-resort thaw that credits nobody. On reaching
    /// 1.0 the player is unfrozen (with a spawn shield) and manual revivers get FREEZETAG_REVIVALS +1. Call once
    /// per frame with the frame delta after <see cref="SetRoster"/>. Returns the number of players thawed.
    /// </summary>
    public int ReviveTick(float dt)
    {
        if (MatchEnded || _roster.Count == 0)
            return 0;
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        float extra = ReviveExtraSize;
        float clear = ReviveClearSpeed;
        float maxtime = FrozenMaxtime;
        bool t2s = ReviveTimeToScore > 0f;
        float t2sTime = ReviveTimeToScore;
        bool autoRevive = ReviveAuto && maxtime > 0f;
        bool autoProgress = autoRevive && ReviveAutoProgress;
        float spawnShield = ReviveSpawnShield;
        int thawed = 0;

        for (int i = 0; i < _roster.Count; i++)
        {
            Player frozen = _roster[i];
            if (!IsFrozen(frozen) || frozen.IsDead)
                continue;
            var st = GetState(frozen);

            // Collect nearby living unfrozen teammates (QC IN_REVIVING_RANGE box-overlap) — the revivers chain.
            // n == -1 marks an automatic revive of last resort (no manual reviver but the maxtime timeout elapsed).
            int n = 0;
            for (int j = 0; j < _roster.Count; j++)
            {
                Player it = _roster[j];
                if (ReferenceEquals(it, frozen) || it.IsDead || IsFrozen(it))
                    continue;
                if (!Teams.SameTeam(it, frozen))
                    continue;
                if (InRevivingRange(frozen, it, extra))
                    n++;
            }
            if (n == 0 && st.FrozenTimeout > 0f && now >= st.FrozenTimeout)
                n = -1;

            // QC base_progress: with auto-revive-progress on, the thaw ring fills smoothly from freeze time so the
            // bar reflects the auto-thaw countdown even with no reviver near.
            float baseProgress = 0f;
            if (autoProgress && st.FrozenTimeout > 0f)
                baseProgress = System.Math.Clamp(1f - (st.FrozenTimeout - now) / maxtime, 0f, 1f);

            if (n == 0)
            {
                // No teammate nearby (and no auto-thaw): hold/clear the bar per the t2s mode.
                if (t2s)
                {
                    // With time-to-score active, progress is NOT cleared (it would let players stack points by
                    // entering/exiting the zone); it sticks at max(manual progress, auto-ramp floor). A higher
                    // manual progress also shortens the remaining auto-thaw time.
                    if (st.ReviveProgress > baseProgress)
                    {
                        baseProgress = st.ReviveProgress;
                        if (autoRevive)
                            st.FrozenTimeout = now + maxtime * (1f - st.ReviveProgress);
                    }
                    st.ReviveProgress = baseProgress;
                }
                else
                {
                    // t2s OFF: decay toward the auto-ramp floor at clearspeed (scaled by 1-base_progress).
                    float decayed = st.ReviveProgress - dt * clear * (1f - baseProgress);
                    st.ReviveProgress = System.Math.Clamp(decayed, baseProgress, 1f);
                }
                continue;
            }

            // At least one reviver (or the auto-thaw of last resort): advance progress.
            // QC: t2s ON uses revive_speed_t2s; t2s OFF uses revive_speed*(1-base_progress).
            float spd = t2s ? ReviveSpeedT2s : ReviveSpeed * (1f - baseProgress);
            st.ReviveProgress = System.Math.Clamp(st.ReviveProgress + dt * System.Math.Max(1f / 60f, spd), baseProgress, 1f);

            // QC: with t2s active each in-range reviver accrues time → +1 SCORE per revive_time_to_score seconds.
            if (t2s && n > 0)
            {
                for (int j = 0; j < _roster.Count; j++)
                {
                    Player it = _roster[j];
                    if (ReferenceEquals(it, frozen) || it.IsDead || IsFrozen(it))
                        continue;
                    if (!Teams.SameTeam(it, frozen) || !InRevivingRange(frozen, it, extra))
                        continue;
                    float acc = (_reviveTime.TryGetValue(it, out float rt) ? rt : 0f) + dt / t2sTime;
                    while (acc > 1f) { it.ScoreFrags += 1; acc -= 1f; }
                    _reviveTime[it] = acc;
                }
            }

            if (st.ReviveProgress >= 1f)
            {
                Unfreeze(frozen, spawnShield);
                thawed++;
                // QC: an auto-revive (n == -1) credits nobody; a manual revive credits EVERY nearby reviver with
                // FREEZETAG_REVIVALS +1 (and, with t2s OFF, SCORE +1 — t2s ON already scored via the accrual).
                if (n > 0)
                {
                    for (int j = 0; j < _roster.Count; j++)
                    {
                        Player it = _roster[j];
                        if (ReferenceEquals(it, frozen) || it.IsDead || IsFrozen(it))
                            continue;
                        if (Teams.SameTeam(it, frozen) && InRevivingRange(frozen, it, extra))
                        {
                            AddRevival(it);
                            if (!t2s)
                                it.ScoreFrags += 1;
                        }
                    }
                }
            }
        }

        // Mirror each roster player's per-player FrozenState.ReviveProgress onto the networkable entity field so
        // the player snapshot (NetEntityState.ReviveProgress) can ship the thaw ring to clients (QC STAT(REVIVE_
        // PROGRESS)). Frozen players carry their accumulated 0..1; everyone else reads 0 (Unfreeze reset it).
        for (int i = 0; i < _roster.Count; i++)
        {
            Player p = _roster[i];
            p.ReviveProgress = Frozen.TryGetValue(p, out var fs) ? fs.ReviveProgress : 0f;
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
