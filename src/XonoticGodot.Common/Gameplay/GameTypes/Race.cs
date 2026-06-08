using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;
using GS = XonoticGodot.Common.Gameplay.Scoring.GameScores; // T7: alias the static score table for the per-mode ScoreRules

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Race gametype — port of <c>CLASS(Race, Gametype)</c>
/// (common/gametypes/gametype/race/race.qh + sv_race.qc, server/race.qc).
///
/// Players race a fixed sequence of <c>trigger_race_checkpoint</c> entities; crossing the final checkpoint
/// completes a lap. The match runs in one of two modes (QC <c>g_race_qualifying</c>):
///  - qualifying: every racer runs solo against the clock; ranking is by fastest single lap
///    (QC SP_RACE_FASTEST, SFL_LOWER_IS_BETTER);
///  - race: head-to-head; first to the lap limit (cvar <c>g_race_laps_limit</c>) wins, ties broken by time.
/// The match ends when a racer reaches the lap limit, OR (QC <c>WinningCondition_Race</c>) once every racer
/// has finished — at which point a sudden-death overtime is started unless all laps were raced.
///
/// Faithfully ported (the timing/scoring rule):
///  - per-player lap + checkpoint + fastest-lap state (<see cref="RaceState"/>);
///  - lap-limit win check (<see cref="LapLimit"/>, QC g_race_laps_limit / GameRules_limit_score);
///  - "everyone finished ⇒ match over" condition (QC WinningCondition_Race n == c).
///
/// Faithfully ported (objective layer):
///  - the checkpoint trigger entities + ordered crossing detection (<see cref="SpawnCheckpoint"/> /
///    <see cref="CheckpointTouch"/>, QC trigger_race_checkpoint + checkpoint_passed / race_NextCheckpoint):
///    a racer must cross checkpoints in sequence; the finish line (index 0) closes a lap;
///  - per-player lap/checkpoint timing + fastest-lap tracking (<see cref="RaceState"/>, QC race_* fields);
///  - record persistence (QC race_readTime/race_writeTime via <see cref="RaceRecords"/>): closing a lap files
///    the lap time into the per-map top-99 ranking, classified for the host's INFO_RACE_* notification;
///  - qualifying mode (QC g_race_qualifying): solo time-trial — closing a lap re-teleports the racer to start
///    (the finish kill-delay <see cref="OnFinishRetract"/>), ranking by fastest lap; in plain race mode the
///    racer keeps going until the lap limit, then is retracted.
///
/// Faithfully ported (depth):
///  - penalty zones (<see cref="SpawnPenaltyZone"/>/<see cref="PenaltyTouch"/>/<see cref="SetPenalty"/>, QC
///    trigger_race_penalty + race_ImposePenaltyTime): a racer entering one is frozen for the penalty time in a
///    plain race, or has it added to the lap time in qualifying, expired by <see cref="Tick"/>;
///  - sudden-death overtime (<see cref="SuddenDeath"/>, QC WinningCondition_Race): the lap limit alone never
///    ends the race — it runs on until everyone finishes;
///  - the qualifying==2 → race transition (<see cref="TransitionQualifyingToRace"/>, QC rc_SetLimits +
///    reset_map_global): clears qualifying, restores the saved fraglimit/leadlimit/timelimit, re-runs the
///    score rules, and resets every racer;
///  - team race (<see cref="RaceTeams"/>, QC g_race_teams): laps add up into ST_RACE_LAPS.
///
/// Deferred (NOTE — cross-boundary): the forced-keyboard movement quantization (rc PlayerPhysics — a fairness
/// tweak to the move command), and the score networking/HUD — physics-input / client concerns.
/// </summary>
[GameType]
public sealed class Race : GameType
{
    // ----- lap limit cvars + default (gametype default laplimit=7) -----
    private const string CvarLapLimit     = "g_race_laps_limit";
    private const string CvarFragLimit    = "fraglimit"; // GameRules_limit_score writes the lap limit here
    private const int    DefaultLapLimit  = 7;           // gametype_init "laplimit=7"
    private const string CvarQualifying   = "g_race_qualifying";        // 0 = race, !=0 = qualifying time-trial
    private const string CvarTeams        = "g_race_teams";             // 0 = FFA, 2..4 = team race (laps add up)
    private const string CvarSuddenDeath  = "timelimit_suddendeath";    // overtime window (minutes); unused for the latch
    private const string CvarMapName      = "mapname";

    /// <summary>QC <c>g_race_teams</c> (sv_race.qc:14): 2/3/4 → the race is a team game (members add up laps to
    /// ST_RACE_LAPS). 0/1 = FFA. Bounded to 2..4 when set.</summary>
    public int RaceTeams
    {
        get
        {
            if (!TryCvar(CvarTeams, out float v) || v < 2f) return 0;
            return System.Math.Clamp((int)v, 2, 4);
        }
    }

    /// <summary>Per-player race timing state (QC race_* client fields + SP_RACE_* scores).</summary>
    public sealed class RaceState
    {
        /// <summary>QC <c>race_checkpoint</c>: index of the next checkpoint this racer must cross (-1 = none yet).</summary>
        public int NextCheckpoint = -1;

        /// <summary>Laps fully completed (QC SP_RACE_LAPS).</summary>
        public int LapsCompleted;

        /// <summary>Sim time the current lap started (QC race_movetime baseline). 0 = not started.</summary>
        public float LapStartTime;

        /// <summary>Best single-lap time in seconds so far (QC SP_RACE_FASTEST), or 0 if no lap finished.</summary>
        public float FastestLap;

        /// <summary>Cumulative time of all completed laps in seconds (QC SP_RACE_TIME ~ time − game_starttime).</summary>
        public float TotalTime;

        /// <summary>QC <c>race_completed</c>: this racer has reached the lap limit and finished the race.</summary>
        public bool Completed;

        /// <summary>
        /// QC <c>race_penalty</c> (sv_race.qc:141): absolute sim time until which the racer is frozen by a
        /// penalty zone (velocity zeroed + MOVETYPE_NONE). 0 = no active penalty.
        /// </summary>
        public float PenaltyUntil;

        /// <summary>QC <c>race_penalty_accumulator</c>: penalty seconds added to a qualifying lap time (no freeze).</summary>
        public float PenaltyAccumulator;

        /// <summary>QC <c>race_lastpenalty</c>: the penalty entity last triggered (so a zone only fires once per pass).</summary>
        public Entity? LastPenalty;
    }

    private readonly Dictionary<Player, RaceState> _states = new();

    private HookHandler<DeathEvent>? _deathHandler;

    /// <summary>Optional sink for the host/controller to react to a death/finish.</summary>
    public IMatchEvents? Events;

    /// <summary>QC checkrules end-of-match latch: true once a racer hits the lap limit or all have finished.</summary>
    public bool MatchEnded { get; private set; }

    /// <summary>The first racer to complete the race (QC winner), else null.</summary>
    public Player? Winner { get; private set; }

    /// <summary>
    /// QC sudden-death overtime latch (sv_race.qc WinningCondition_Race returns WINNING_STARTSUDDENDEATHOVERTIME):
    /// set once the score limit is reached but not everyone has finished, so the match runs on until a racer
    /// completes (rather than ending on a tie). Cleared at <see cref="Activate"/>.
    /// </summary>
    public bool SuddenDeath { get; private set; }

    /// <summary>QC <c>g_race_qualifying</c>: solo time-trial mode (rank by fastest lap, re-teleport after each lap).
    /// Note this can be the value <c>2</c> ("qualifying THEN race"), which <see cref="TransitionQualifyingToRace"/>
    /// collapses to 0 (plain race) once the qualifying session ends.</summary>
    public bool Qualifying => TryCvar(CvarQualifying, out float v) && v != 0f;

    /// <summary>The current map name (QC GetMapname) — the record-DB key segment.</summary>
    public string MapName => Api.Services is null ? "" : Api.Cvars.GetString(CvarMapName);

    /// <summary>This gametype's record-DB type segment (QC RACE_RECORD).</summary>
    public string RecordType => RaceRecords.RaceRecord;

    /// <summary>
    /// The host's finish-retract hook (QC the kill-delay re-teleport in race_PreparePlayer / Race_FinalCheckpoint):
    /// after a lap is closed (qualifying) or the race is finished (race mode) the racer is sent back to the start.
    /// The simulation resets the racer's lap state (<see cref="RetractPlayer"/>); the host implements the actual
    /// teleport-to-start + optional silent kill. Invoked from <see cref="Tick"/> when the kill delay elapses.
    /// </summary>
    public System.Action<Player>? OnFinishRetract;

    /// <summary>The result of the most recent finish-time record attempt (for the host's INFO_RACE_* notification).</summary>
    public RaceRecordResult LastRecord { get; private set; }
    public Player? LastRecordPlayer { get; private set; }

    // Pending finish-retracts: racer -> absolute sim time at which to send them back to start (kill delay).
    private readonly Dictionary<Player, float> _retractAt = new();

    public Race()
    {
        NetName = "rc";
        DisplayName = "Race";
        TeamGame = false;
    }

    /// <summary>The checkpoint entities on the map, indexed by their <see cref="Entity.GtCheckpointIndex"/> (QC g_race_targets).</summary>
    public readonly List<Entity> Checkpoints = new();

    /// <summary>The highest checkpoint index seen (QC race_highest_checkpoint); index 0 is the finish/start line.</summary>
    public int HighestCheckpoint { get; private set; }

    public override void OnInit()
    {
        // QC: checkpoints are spawned from the map's trigger_race_checkpoint entities (see SpawnCheckpoint).
        // gametype_init flags (USEPOINTS) and the qualifying/race mode are engine/server-config concerns.
        Checkpoints.Clear();
        PenaltyZones.Clear();
        HighestCheckpoint = 0;
    }

    /// <summary>
    /// QC trigger_race_checkpoint spawnfunc: register a checkpoint world entity at <paramref name="origin"/>
    /// with ordinal <paramref name="checkpointIndex"/> (0 = start/finish line). Touch dispatches the ordered
    /// crossing to <see cref="CrossCheckpoint"/>.
    /// </summary>
    public Entity? SpawnCheckpoint(Vector3 origin, int checkpointIndex, Vector3 mins = default, Vector3 maxs = default)
    {
        if (maxs == default) { mins = new Vector3(-48f, -48f, -48f); maxs = new Vector3(48f, 48f, 48f); }
        Entity? e = GametypeEntities.SpawnObjective("trigger_race_checkpoint", origin, Teams.None, mins, maxs,
            touch: CheckpointTouch);
        if (e is not null)
        {
            e.Flags = EntFlags.None; // a checkpoint is a pure trigger volume, not an item
            e.GtCheckpointIndex = checkpointIndex;
            e.GtIsFinishLine = checkpointIndex == 0;
            Checkpoints.Add(e);
            if (checkpointIndex > HighestCheckpoint)
                HighestCheckpoint = checkpointIndex;
        }
        return e;
    }

    /// <summary>The penalty-zone trigger entities on the map (QC trigger_race_penalty).</summary>
    public readonly List<Entity> PenaltyZones = new();

    /// <summary>
    /// QC trigger_race_penalty spawnfunc (server/race.qc:1322): a trigger volume that imposes
    /// <paramref name="penaltySeconds"/> (QC .race_penalty, default 5) on a racer who enters it. Touch
    /// dispatches to <see cref="PenaltyTouch"/>. Returns the world entity (or null headless).
    /// </summary>
    public Entity? SpawnPenaltyZone(Vector3 origin, float penaltySeconds = 5f, Vector3 mins = default, Vector3 maxs = default)
    {
        if (maxs == default) { mins = new Vector3(-48f, -48f, -48f); maxs = new Vector3(48f, 48f, 48f); }
        Entity? e = GametypeEntities.SpawnObjective("trigger_race_penalty", origin, Teams.None, mins, maxs,
            touch: PenaltyTouchEntity);
        if (e is not null)
        {
            e.Flags = EntFlags.None;                       // a pure trigger volume
            e.GtPointAmt = penaltySeconds > 0f ? penaltySeconds : 5f; // QC this.race_penalty
            PenaltyZones.Add(e);
        }
        return e;
    }

    /// <summary>Entity touch trampoline for a penalty zone: a live racer entering it is penalized.</summary>
    private void PenaltyTouchEntity(Entity self, Entity other)
    {
        if (other is Player p && !p.IsDead)
            PenaltyTouch(self, p);
    }

    /// <summary>
    /// QC checkpoint_passed: a racer touched a checkpoint. The crossing only counts if it is the racer's
    /// expected next checkpoint (QC player.race_checkpoint == this.race_checkpoint, with -1 → expect 0);
    /// then advance to the next checkpoint (wrapping the finish line) and update lap timing.
    /// </summary>
    public void CheckpointTouch(Entity self, Entity other)
    {
        if (other is not Player p || p.IsDead)
            return;
        int cpIndex = self.GtCheckpointIndex;
        RaceState st = GetState(p);

        // Expected next checkpoint: -1 means "haven't started, expect the start line (0)".
        int expected = st.NextCheckpoint < 0 ? 0 : st.NextCheckpoint;
        if (cpIndex != expected)
            return; // out of order — ignore (QC also damages on spawnflag 4, deferred)

        bool isFinish = self.GtIsFinishLine || cpIndex == 0;
        CrossCheckpoint(p, cpIndex, isFinish);
    }

    /// <summary>The lap limit in force (g_race_laps_limit, else fraglimit, else 7). 0 means no lap limit.</summary>
    public int LapLimit
    {
        get
        {
            if (TryCvar(CvarLapLimit, out float ll)) return (int)ll;
            if (TryCvar(CvarFragLimit, out float fl)) return (int)fl;
            return DefaultLapLimit;
        }
    }

    public void Activate()
    {
        if (_deathHandler is not null)
            return;
        MatchEnded = false;
        Winner = null;
        SuddenDeath = false;
        // QC rc_SetLimits: g_race_teams 2..4 makes race a team game (members add up laps to ST_RACE_LAPS).
        TeamGame = RaceTeams >= 2;
        if (TeamGame)
            Scoring.GameScores.ResetTeams();
        DeclareScoreRules();
        if (TeamGame)
            Scoring.GameScores.SeedTeams(RaceTeams);
        _deathHandler = OnDeath;
        Combat.Death.Add(_deathHandler);
    }

    /// <summary>
    /// QC <c>race_ScoreRules</c> (sv_race.qc): GameRules_score_enabled(false) — race has no SP_SCORE — then declare
    /// the lap/time columns and pin the sort keys. In a non-team race: qualifying ranks by SP_RACE_FASTEST
    /// (PRIMARY, lower-is-better TIME); a plain race ranks by SP_RACE_LAPS (PRIMARY) then SP_RACE_TIME (SECONDARY,
    /// lower-is-better TIME), with SP_RACE_FASTEST shown as a stat. Team race ranks teams by ST_RACE_LAPS.
    /// </summary>
    private void DeclareScoreRules()
    {
        GS.ScoreRulesBasics(teams: TeamGame, scoreEnabled: false); // QC GameRules_score_enabled(false): no SP_SCORE
        bool qualifying = Qualifying;
        if (!TeamGame && qualifying)
        {
            // qualifying solo time-trial: fastest single lap, ascending.
            GS.DeclareColumn("RACE_FASTEST", Scoring.ScoreFlags.LowerIsBetter | Scoring.ScoreFlags.Time, "fastest");
            GS.SetSortKeys(GS.Field("RACE_FASTEST")!);
        }
        else
        {
            // QC: field(SP_RACE_LAPS, "laps", PRIMARY); field(SP_RACE_TIME, "time", SECONDARY|LOWER|TIME); + the
            // fastest stat. In a TEAM race the team primary is ST_RACE_LAPS (team slot 1, "laps").
            if (TeamGame)
            {
                GS.TeamRulesBasics(scorePrio: Scoring.ScoreFlags.None);
                GS.SetTeamLabel(GS.TeamSlotSecondary, "laps", Scoring.ScoreFlags.SortPrioPrimary); // ST_RACE_LAPS
            }
            GS.DeclareColumn("RACE_LAPS", Scoring.ScoreFlags.None, "laps");
            GS.DeclareColumn("RACE_TIME", Scoring.ScoreFlags.LowerIsBetter | Scoring.ScoreFlags.Time, "time");
            GS.DeclareColumn("RACE_FASTEST", Scoring.ScoreFlags.LowerIsBetter | Scoring.ScoreFlags.Time, "fastest");
            GS.SetSortKeys(GS.Field("RACE_LAPS")!, GS.Field("RACE_TIME"));
        }
    }

    /// <summary>QC <c>GameRules_scoring_add(player, SP_X, n)</c> for a Race player column (no-op if unregistered).</summary>
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

    public RaceState GetState(Player p)
    {
        if (!_states.TryGetValue(p, out RaceState? st))
        {
            st = new RaceState();
            _states[p] = st;
        }
        return st;
    }

    public void AddPlayer(Player p) => GetState(p);
    public void RemovePlayer(Player p) => _states.Remove(p);

    /// <summary>
    /// Record a racer crossing a checkpoint (QC race_CheckpointTouch). This is the core timing rule: crossing
    /// the start/finish line (<paramref name="isFinishLine"/>) closes the current lap — updating the fastest
    /// lap and the lap counter — and either starts the next lap or, at the lap limit, finishes the race.
    /// This is invoked by <see cref="CheckpointTouch"/> when a checkpoint entity is crossed in order; the host
    /// may also call it directly. Advances <see cref="RaceState.NextCheckpoint"/> (wrapping at the finish).
    /// </summary>
    public void CrossCheckpoint(Player p, int checkpointIndex, bool isFinishLine)
    {
        if (MatchEnded)
            return;

        RaceState st = GetState(p);
        float now = Api.Services is not null ? Api.Clock.Time : 0f;

        if (isFinishLine)
        {
            // Close the lap: a lap time exists only once we've crossed the line at least once before.
            if (st.LapStartTime > 0f)
            {
                float lapTime = now - st.LapStartTime;
                if (lapTime > 0f)
                {
                    if (st.FastestLap <= 0f || lapTime < st.FastestLap)
                        st.FastestLap = lapTime; // QC SP_RACE_FASTEST (lower is better)
                    st.LapsCompleted += 1;       // QC SP_RACE_LAPS
                    st.TotalTime += lapTime;     // cumulative race time (QC SP_RACE_TIME ~ time - game_starttime)

                    // QC Race_FinalCheckpoint scoring (server/race.qc): SP_RACE_FASTEST tracks the best (lowest)
                    // lap; in a plain race SP_RACE_TIME is the cumulative time and SP_RACE_LAPS counts laps. Times
                    // are TIME_ENCODE'd to hundredths (SFL_TIME). Qualifying ranks by fastest only.
                    Scoring.ScoreField? fastest = Scoring.GameScores.Field("RACE_FASTEST");
                    if (fastest is not null)
                        Scoring.GameScores.SetBestTime(p, fastest, Scoring.GameScores.TimeEncode(lapTime));
                    if (!Qualifying)
                    {
                        AddCol(p, "RACE_LAPS", 1);
                        // QC g_race_teams (sv_race.qc:399): in a team race the members' laps add up into
                        // ST_RACE_LAPS (team slot 1), the team primary.
                        if (TeamGame && (int)p.Team != Teams.None)
                            Scoring.GameScores.AddToTeam((int)p.Team, Scoring.GameScores.TeamSlotSecondary, 1);
                        Scoring.ScoreField? rtime = Scoring.GameScores.Field("RACE_TIME");
                        if (rtime is not null)
                        {
                            // set RACE_TIME to the cumulative encoded time (delta from current value).
                            int target = Scoring.GameScores.TimeEncode(st.TotalTime);
                            int cur = Scoring.GameScores.Get(p, rtime);
                            if (target != cur) Scoring.GameScores.AddToPlayer(p, rtime, target - cur);
                        }
                    }

                    // QC race_setTime: file the lap time into the persistent per-map ranking (top-99 by UID).
                    RecordFinishTime(p, lapTime);
                }
            }
            st.LapStartTime = now;           // start the next lap
            // After the finish/start line (index 0), the next required checkpoint is 1 (QC race_NextCheckpoint(0)).
            st.NextCheckpoint = HighestCheckpoint >= 1 ? 1 : 0;

            int limit = LapLimit;
            if (limit > 0 && st.LapsCompleted >= limit)
                FinishRacer(p, st);
            else if (Qualifying)
                // QC qualifying: solo time-trial — after each lap the racer is sent back to start to run again.
                ScheduleRetract(p, now);
        }
        else
        {
            // QC race_NextCheckpoint: advance to the next index, wrapping back to the finish line (0) after
            // the highest checkpoint so the racer must cross the finish to close the lap.
            st.NextCheckpoint = checkpointIndex >= HighestCheckpoint ? 0 : checkpointIndex + 1;
        }
    }

    /// <summary>Mark a racer as having completed the race and re-check the win condition.</summary>
    private void FinishRacer(Player p, RaceState st)
    {
        st.Completed = true; // QC race_completed
        Winner ??= p;        // first to finish is the race winner (qualifying ranks by FastestLap instead)
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        ScheduleRetract(p, now); // QC: a finished racer is retracted to start (kept out of the running pack)
        CheckWinningCondition();
    }

    /// <summary>
    /// QC <c>race_setTime</c>: file a finished lap/run time into the persistent per-map ranking and stash the
    /// classified result (<see cref="LastRecord"/>) so the host can fire the right INFO_RACE_* notification.
    /// </summary>
    private void RecordFinishTime(Player p, float time)
    {
        LastRecord = RaceRecords.SetTime(MapName, RecordType, time, p.PersistentId, p.NetName);
        LastRecordPlayer = p;
    }

    /// <summary>The current persistent server record (rank 1) lap time for this map (0 if none).</summary>
    public float ServerRecord => RaceRecords.ServerRecord(MapName, RecordType);
    public string ServerRecordHolder => RaceRecords.ServerRecordHolder(MapName, RecordType);

    /// <summary>QC the finish kill-delay: schedule the racer to be retracted to start after <paramref name="now"/> + delay.</summary>
    private void ScheduleRetract(Player p, float now)
    {
        float delay = Cvar("g_race_respawn_delay", 0f); // 0 = retract on the next Tick (race_PreparePlayer is immediate)
        _retractAt[p] = now + System.MathF.Max(0f, delay);
    }

    /// <summary>
    /// QC <c>race_PreparePlayer</c> + <c>race_RetractPlayer</c>: reset a racer's lap/checkpoint state so they
    /// restart from the start line. The host's <see cref="OnFinishRetract"/> does the actual teleport (+ optional
    /// silent kill); this clears the simulation-side timing.
    /// </summary>
    public void RetractPlayer(Player p)
    {
        RaceState st = GetState(p);
        st.LapStartTime = 0f;
        st.NextCheckpoint = -1; // re-expect the start line
        OnFinishRetract?.Invoke(p);
    }

    /// <summary>
    /// Per-frame: expire penalty-zone freezes (QC the rc PlayerPhysics race_penalty slice) and fire any due
    /// finish-retracts (QC the deferred kill-delay re-teleport). Call once per server frame with the sim time.
    /// </summary>
    public void Tick(float now)
    {
        // QC rc PlayerPhysics (sv_race.qc:141): a racer with an active race_penalty is frozen (velocity 0,
        // MOVETYPE_NONE) until time > penalty; then it clears. We drive both the expiry and the freeze here.
        foreach (var kv in _states)
        {
            RaceState st = kv.Value;
            if (st.PenaltyUntil <= 0f)
                continue;
            if (now > st.PenaltyUntil)
            {
                st.PenaltyUntil = 0f;
                if (kv.Key.MoveType == MoveType.None)
                    kv.Key.MoveType = MoveType.Walk; // release the freeze (QC restores normal movement)
            }
            else
            {
                kv.Key.Velocity = Vector3.Zero;       // QC: player.velocity = '0 0 0'
                kv.Key.MoveType = MoveType.None;      // QC: set_movetype(player, MOVETYPE_NONE)
            }
        }

        if (_retractAt.Count == 0) return;
        List<Player>? due = null;
        foreach (var kv in _retractAt)
            if (now >= kv.Value) (due ??= new List<Player>()).Add(kv.Key);
        if (due is null) return;
        foreach (Player p in due) { _retractAt.Remove(p); RetractPlayer(p); }
    }

    // ============================================================================================
    //  Penalty zones (QC trigger_race_penalty / race_ImposePenaltyTime, sv_race.qc + server/race.qc)
    // ============================================================================================

    /// <summary>
    /// QC race_ImposePenaltyTime (server/race.qc:1275): apply a <paramref name="seconds"/> penalty to a racer.
    /// In QUALIFYING the penalty is added to the lap time (race_penalty_accumulator, no freeze); in a plain RACE
    /// the racer is FROZEN (velocity 0 + MOVETYPE_NONE) until <c>time + seconds</c> (driven by <see cref="Tick"/>).
    /// </summary>
    public void SetPenalty(Player p, float seconds)
    {
        if (seconds <= 0f)
            return;
        RaceState st = GetState(p);
        if (Qualifying)
        {
            st.PenaltyAccumulator += seconds; // QC qualifying: accumulate into the lap time, no freeze
        }
        else
        {
            float now = Api.Services is not null ? Api.Clock.Time : 0f;
            st.PenaltyUntil = now + seconds;  // QC race: freeze until time > penalty
            p.Velocity = Vector3.Zero;
            p.MoveType = MoveType.None;
        }
    }

    /// <summary>
    /// QC penalty_touch (server/race.qc:1307): a racer entered a trigger_race_penalty zone. Each zone only fires
    /// once per pass (QC race_lastpenalty), then imposes its penalty time/reason. The zone's penalty seconds are
    /// read from the trigger entity's <see cref="Entity.GtPointAmt"/> (set by the spawnfunc, default 5).
    /// </summary>
    public void PenaltyTouch(Entity zone, Player toucher)
    {
        RaceState st = GetState(toucher);
        if (ReferenceEquals(st.LastPenalty, zone))
            return; // already penalized by this zone on this pass
        st.LastPenalty = zone;
        float seconds = zone.GtPointAmt > 0f ? zone.GtPointAmt : 5f; // QC: this.race_penalty default 5
        SetPenalty(toucher, seconds);
    }

    /// <summary>Whether a racer is currently frozen by a penalty zone (QC race_penalty &gt; 0 in a plain race).</summary>
    public bool IsPenalized(Player p)
        => _states.TryGetValue(p, out RaceState? st)
           && st.PenaltyUntil > 0f
           && (Api.Services is null ? 0f : Api.Clock.Time) <= st.PenaltyUntil;

    /// <summary>The penalty seconds accumulated onto a qualifying racer's time (QC race_penalty_accumulator).</summary>
    public float PenaltyAccumulatorOf(Player p)
        => _states.TryGetValue(p, out RaceState? st) ? st.PenaltyAccumulator : 0f;

    /// <summary>Read a float cvar with a fallback (QC autocvar with default).</summary>
    private static float Cvar(string name, float fallback)
        => TryCvar(name, out float v) ? v : fallback;

    /// <summary>The fastest single-lap time recorded for a racer (0 if none), QC SP_RACE_FASTEST.</summary>
    public float FastestLapOf(Player p) => _states.TryGetValue(p, out RaceState? st) ? st.FastestLap : 0f;

    /// <summary>
    /// QC WinningCondition_Race (sv_race.qc:77): the match ends only once EVERY tracked racer has completed the
    /// race (n == c). Otherwise, if the score limit (lap limit) has been reached by anyone, QC returns
    /// WINNING_STARTSUDDENDEATHOVERTIME — i.e. it does NOT end on the lap limit alone but runs on in sudden death
    /// until everyone finishes (no equality/tie when laps are all raced). We latch <see cref="SuddenDeath"/> in
    /// that case rather than ending the match.
    /// </summary>
    public void CheckWinningCondition()
    {
        if (_states.Count == 0)
            return;

        int total = 0, completed = 0;
        foreach (RaceState st in _states.Values)
        {
            total++;
            if (st.Completed) completed++;
        }

        // QC: if (n && n == c) return WINNING_YES — everyone finished, the match is over.
        if (total > 0 && total == completed)
        {
            MatchEnded = true;
            return;
        }

        // QC: wc = WinningCondition_Scores(fraglimit). If the lap limit was reached but not everyone finished,
        // START SUDDEN DEATH (run on) instead of ending. We model the score-limit reach as "any racer has
        // completed" (a finished racer reached the lap limit, FinishRacer).
        if (!SuddenDeath && Qualifying == false && LapLimit > 0 && completed > 0 && completed < total)
            SuddenDeath = true;
    }

    /// <summary>
    /// QC rc_SetLimits + reset_map_global (sv_race.qc:199/394): the <c>g_race_qualifying == 2</c> "qualifying
    /// THEN race" transition — once the qualifying session ends, clear qualifying (cvar → 0), restore the saved
    /// fraglimit/leadlimit/timelimit, re-run the score rules for the plain-race schema, and reset every racer.
    /// Returns true if a transition happened (it was qualifying==2). Invoke from the host when the qualifying
    /// timelimit elapses; the shared CheckRules path is left untouched (this is a Race-local swap).
    /// </summary>
    public bool TransitionQualifyingToRace()
    {
        if (Api.Services is null)
            return false;
        // QC: if(g_race_qualifying == 2) { g_race_qualifying = 0; ... }
        if ((int)Cvar(CvarQualifying, 0f) != 2)
            return false;

        Api.Cvars.Set(CvarQualifying, "0"); // g_race_qualifying = 0
        // QC restores the saved race_fraglimit/leadlimit/timelimit; the host owns those cvars, so we restore
        // any saved values it stashed (default: leave the configured fraglimit in place).
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        if (_savedFragLimit >= 0f) Api.Cvars.Set("fraglimit", _savedFragLimit.ToString(ic));
        if (_savedLeadLimit >= 0f) Api.Cvars.Set("leadlimit", _savedLeadLimit.ToString(ic));
        if (_savedTimeLimit >= 0f) Api.Cvars.Set("timelimit", _savedTimeLimit.ToString(ic));

        DeclareScoreRules(); // QC race_ScoreRules() — re-declare for the now-plain race
        SuddenDeath = false;

        // QC FOREACH_CLIENT race_PreparePlayer: reset every racer to the start.
        foreach (Player p in new List<Player>(_states.Keys))
        {
            RaceState st = _states[p];
            st.LapStartTime = 0f;
            st.NextCheckpoint = -1;
            st.Completed = false;
            st.PenaltyAccumulator = 0f;
            st.PenaltyUntil = 0f;
        }
        return true;
    }

    /// <summary>Stash the race fraglimit/leadlimit/timelimit to restore on the qualifying→race transition (QC
    /// race_fraglimit/race_leadlimit/race_timelimit captured in rc_SetLimits). -1 = unset (don't restore).</summary>
    public void SaveRaceLimits(float fragLimit, float leadLimit, float timeLimit)
    {
        _savedFragLimit = fragLimit;
        _savedLeadLimit = leadLimit;
        _savedTimeLimit = timeLimit;
    }
    private float _savedFragLimit = -1f, _savedLeadLimit = -1f, _savedTimeLimit = -1f;

    /// <summary>QC MUTATOR_HOOKFUNCTION(rc, PlayerDies): a death just forces a respawn; no scoring in race.</summary>
    private bool OnDeath(ref DeathEvent ev)
    {
        if (ev.Victim is not Player victim)
            return false;

        // Death never scores in race and never ends the race — the racer is simply sent back to respawn.
        // QC race_AbandonRaceCheck: drop the in-progress lap so the next start line re-stamps the timer and
        // the racer must re-cross checkpoints in order from the start.
        RaceState st = GetState(victim);
        st.LapStartTime = 0f;
        st.NextCheckpoint = -1;
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
