using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;
using GS = XonoticGodot.Common.Gameplay.Scoring.GameScores; // T7: alias the static score table for the per-mode ScoreRules

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Race CTS ("Complete The Stage") gametype — port of <c>CLASS(RaceCTS, Gametype)</c>
/// (common/gametypes/gametype/cts/cts.qh + sv_cts.qc).
///
/// CTS is a stripped-down Race: a single course from a <c>target_startTimer</c> to a <c>target_stopTimer</c>
/// (the finish), with no laps and no lap limit. Players run the stage repeatedly trying to beat their own
/// fastest time; ranking is purely by fastest completion time (QC SP_RACE_FASTEST,
/// SFL_LOWER_IS_BETTER | SFL_TIME). There is no frag scoring — self-damage is off by default
/// (g_cts_selfdamage) and only the Shotgun is given (QC WantWeapon → WEP_SHOTGUN). The match is bounded by
/// the time limit only; crossing the finish records a time and (optionally) re-teleports the player to start
/// after a kill delay (QC Race_FinalCheckpoint → ClientKill_Silent g_cts_finish_kill_delay).
///
/// Faithfully ported (the timing rule):
///  - per-player run timer + fastest-completion time (<see cref="CtsState"/>);
///  - start/finish timing via <see cref="StartTimer"/> / <see cref="FinishStage"/>;
///  - no lap limit, no frag scoring — purely fastest-time ranking, time-limit bounded.
///
/// Faithfully ported (objective layer):
///  - the start/stop timer trigger entities (<see cref="SpawnStartTimer"/>/<see cref="SpawnStopTimer"/>, QC
///    target_startTimer / target_stopTimer): crossing the start stamps the run timer, crossing the stop
///    folds the run time into the player's fastest time;
///  - per-player run timer + fastest-completion-time tracking (<see cref="CtsState"/>, QC race_* fields);
///  - record persistence (QC CTS_RECORD via <see cref="RaceRecords"/>): completing the stage files the run
///    time into the per-map top-99 ranking, classified for the host's INFO_RACE_* notification;
///  - the finish kill-delay re-teleport (QC g_cts_finish_kill_delay → Race_FinalCheckpoint): after completing
///    the stage the runner is sent back to start (<see cref="OnFinishRetract"/>) so they can run it again.
///
/// Deferred (NOTE — cross-boundary): forced-keyboard movement (PlayerPhysics), projectile removal on death
/// (g_cts_removeprojectiles), and the score networking/HUD — server-config or client concerns.
/// </summary>
[GameType]
public sealed class Cts : GameType
{
    private const string CvarFinishKillDelay   = "g_cts_finish_kill_delay"; // delay before the post-finish retract
    private const string CvarMapName           = "mapname";
    private const string CvarSelfDamage        = "g_cts_selfdamage";        // 1 = normal self/fall damage; 0 = suppressed
    private const string CvarRemoveProjectiles = "g_cts_removeprojectiles"; // delete a dead runner's live projectiles
    private const string CvarDropMonsterItems  = "g_cts_drop_monster_items";// keep monster-dropped items in CTS
    private const float  DefaultFinishKillDelay = 2f;                       // QC g_cts_finish_kill_delay default (sv_cts.qc)
    /// <summary>Per-player CTS run state (QC race_* fields; only fastest-completion-time matters here).</summary>
    public sealed class CtsState
    {
        /// <summary>Sim time the current run started (set when crossing the start timer). 0 = not running.</summary>
        public float RunStartTime;

        /// <summary>Best stage-completion time in seconds (QC SP_RACE_FASTEST), or 0 if never finished.</summary>
        public float FastestTime;

        /// <summary>Number of times this player has completed the stage (QC SP_RACE_LAPS, informational).</summary>
        public int Completions;
    }

    private readonly Dictionary<Player, CtsState> _states = new();

    private HookHandler<DeathEvent>? _deathHandler;
    private HookHandler<MutatorHooks.DamageCalculateArgs>? _damageHandler;
    private HookHandler<MutatorHooks.ForbidThrowCurrentWeaponArgs>? _forbidThrowHandler;
    private HookHandler<MutatorHooks.FilterItemDefinitionArgs>? _filterItemHandler;
    private HookHandler<MutatorHooks.PlayerSpawnArgs>? _playerSpawnHandler;
    private HookHandler<MutatorHooks.PlayerPreThinkArgs>? _preThinkHandler;

    /// <summary>Optional sink for the host/controller to react to a death/finish.</summary>
    public IMatchEvents? Events;

    /// <summary>
    /// CTS has no win latch of its own — the match is decided by the time limit (gametype default
    /// timelimit=20) and ranked by fastest time. Kept for parity with the other gametypes' surface.
    /// </summary>
    public bool MatchEnded { get; private set; }

    /// <summary>The current map name (QC GetMapname) — the record-DB key segment.</summary>
    public string MapName => Api.Services is null ? "" : Api.Cvars.GetString(CvarMapName);

    /// <summary>This gametype's record-DB type segment (QC CTS_RECORD).</summary>
    public string RecordType => RaceRecords.CtsRecord;

    /// <summary>
    /// The host's finish-retract hook (QC Race_FinalCheckpoint's <c>g_cts_finish_kill_delay</c> ClientKill_Silent):
    /// after the stage is completed the runner is teleported back to start. The simulation resets the run timer
    /// (<see cref="RetractRunner"/>); the host implements the actual silent-kill + teleport. Fired from <see cref="Tick"/>.
    /// </summary>
    public System.Action<Player>? OnFinishRetract;

    /// <summary>The result of the most recent finish-time record attempt (for the host's INFO_RACE_* notification).</summary>
    public RaceRecordResult LastRecord { get; private set; }
    public Player? LastRecordPlayer { get; private set; }

    // Pending finish-retracts: runner -> absolute sim time to send them back to start (g_cts_finish_kill_delay).
    private readonly Dictionary<Player, float> _retractAt = new();

    public Cts()
    {
        NetName = "cts";
        DisplayName = "Race CTS";
        TeamGame = false;
    }

    /// <summary>The start/stop timer trigger entities on the map (QC g_race_targets).</summary>
    public readonly List<Entity> Timers = new();

    public override void OnInit()
    {
        // QC: the start/stop timers are spawned from the map's target_startTimer / target_stopTimer entities
        // (see SpawnStartTimer / SpawnStopTimer). CTS_RECORD persistence is a server-DB concern.
        Timers.Clear();
    }

    /// <summary>
    /// QC target_startTimer spawnfunc: a trigger volume that begins a player's stage run on touch (stamps
    /// the run start time). Returns the entity (null when no facade is wired).
    /// </summary>
    public Entity? SpawnStartTimer(Vector3 origin, Vector3 mins = default, Vector3 maxs = default)
    {
        if (maxs == default) { mins = new Vector3(-48f, -48f, -48f); maxs = new Vector3(48f, 48f, 48f); }
        Entity? e = GametypeEntities.SpawnObjective("target_startTimer", origin, Teams.None, mins, maxs,
            touch: StartTimerTouch);
        if (e is not null)
        {
            e.Flags = EntFlags.None;
            e.GtIsStartTimer = true;
            Timers.Add(e);
        }
        return e;
    }

    /// <summary>
    /// QC target_stopTimer spawnfunc: a trigger volume that finishes a player's stage run on touch (folds the
    /// run time into the fastest time). Returns the entity (null when no facade is wired).
    /// </summary>
    public Entity? SpawnStopTimer(Vector3 origin, Vector3 mins = default, Vector3 maxs = default)
    {
        if (maxs == default) { mins = new Vector3(-48f, -48f, -48f); maxs = new Vector3(48f, 48f, 48f); }
        Entity? e = GametypeEntities.SpawnObjective("target_stopTimer", origin, Teams.None, mins, maxs,
            touch: StopTimerTouch);
        if (e is not null)
        {
            e.Flags = EntFlags.None;
            e.GtIsStopTimer = true;
            Timers.Add(e);
        }
        return e;
    }

    private void StartTimerTouch(Entity self, Entity other)
    {
        if (other is Player p && !p.IsDead)
            StartTimer(p);
    }

    private void StopTimerTouch(Entity self, Entity other)
    {
        if (other is Player p && !p.IsDead)
            FinishStage(p); // QC target_stopTimer → Race_FinalCheckpoint (kill-delay re-teleport deferred)
    }

    public void Activate()
    {
        if (_deathHandler is not null)
            return;
        MatchEnded = false;
        DeclareScoreRules();
        _deathHandler = OnDeath;
        Combat.Death.Add(_deathHandler);

        // QC sv_cts.qc mutator hooks — the CTS combat/loadout rules. These chains are dispatched globally
        // (DamageSystem / spawn / weapon-drop / item-filter), so subscribing here makes the CTS rules live.
        _damageHandler      = OnDamageCalculate;            // Damage_Calculate: self/fall-damage suppression
        _forbidThrowHandler = OnForbidThrowCurrentWeapon;   // ForbidThrowCurrentWeapon + ForbidDropCurrentWeapon
        _filterItemHandler  = OnFilterItemDefinition;       // FilterItem / MonsterDropItem (loot filtering)
        _playerSpawnHandler = OnPlayerSpawn;                // WantWeapon → WEP_SHOTGUN (force the loadout)
        _preThinkHandler    = OnPlayerPreThink;             // keep every live runner on the Shotgun each frame
        MutatorHooks.DamageCalculate.Add(_damageHandler);
        MutatorHooks.ForbidThrowCurrentWeapon.Add(_forbidThrowHandler);
        MutatorHooks.FilterItemDefinition.Add(_filterItemHandler);
        MutatorHooks.PlayerSpawn.Add(_playerSpawnHandler);
        MutatorHooks.PlayerPreThink.Add(_preThinkHandler);
    }

    /// <summary>
    /// QC <c>cts_ScoreRules</c> (sv_cts.qc): GameRules_score_enabled(false) — CTS has no SP_SCORE. CTS forces
    /// <c>g_race_qualifying = 1</c> (sv_cts.qh), so by default it ranks by SP_RACE_FASTEST (PRIMARY, lower-is-better
    /// TIME). A non-qualifying CTS instead ranks by SP_RACE_LAPS (PRIMARY) then SP_RACE_TIME (SECONDARY).
    /// </summary>
    private void DeclareScoreRules()
    {
        GS.ScoreRulesBasics(teams: false, scoreEnabled: false); // QC GameRules_score_enabled(false): no SP_SCORE
        if (Qualifying)
        {
            GS.DeclareColumn("RACE_FASTEST", Scoring.ScoreFlags.LowerIsBetter | Scoring.ScoreFlags.Time, "fastest");
            GS.SetSortKeys(GS.Field("RACE_FASTEST")!);
        }
        else
        {
            GS.DeclareColumn("RACE_LAPS", Scoring.ScoreFlags.None, "laps");
            GS.DeclareColumn("RACE_TIME", Scoring.ScoreFlags.LowerIsBetter | Scoring.ScoreFlags.Time, "time");
            GS.DeclareColumn("RACE_FASTEST", Scoring.ScoreFlags.LowerIsBetter | Scoring.ScoreFlags.Time, "fastest");
            GS.SetSortKeys(GS.Field("RACE_LAPS")!, GS.Field("RACE_TIME"));
        }
    }

    /// <summary>QC <c>g_race_qualifying</c> for CTS — defaults to 1 (sv_cts.qh sets it on add): solo time-trial.</summary>
    public bool Qualifying => !TryCvar("g_race_qualifying", out float v) || v != 0f;

    public override void Deactivate()
    {
        if (_deathHandler is null)
            return;
        Combat.Death.Remove(_deathHandler);
        _deathHandler = null;
        if (_damageHandler is not null)      { MutatorHooks.DamageCalculate.Remove(_damageHandler);            _damageHandler = null; }
        if (_forbidThrowHandler is not null) { MutatorHooks.ForbidThrowCurrentWeapon.Remove(_forbidThrowHandler); _forbidThrowHandler = null; }
        if (_filterItemHandler is not null)  { MutatorHooks.FilterItemDefinition.Remove(_filterItemHandler);    _filterItemHandler = null; }
        if (_playerSpawnHandler is not null) { MutatorHooks.PlayerSpawn.Remove(_playerSpawnHandler);            _playerSpawnHandler = null; }
        if (_preThinkHandler is not null)    { MutatorHooks.PlayerPreThink.Remove(_preThinkHandler);            _preThinkHandler = null; }
    }

    public CtsState GetState(Player p)
    {
        if (!_states.TryGetValue(p, out CtsState? st))
        {
            st = new CtsState();
            _states[p] = st;
        }
        return st;
    }

    public void AddPlayer(Player p) => GetState(p);
    public void RemovePlayer(Player p) => _states.Remove(p);

    /// <summary>QC target_startTimer: begin (or restart) a player's stage run, stamping the start time.</summary>
    public void StartTimer(Player p)
    {
        GetState(p).RunStartTime = Api.Services is not null ? Api.Clock.Time : 0f;
    }

    /// <summary>
    /// QC target_stopTimer / Race_FinalCheckpoint: the player crossed the finish. Compute the run time, fold
    /// it into the fastest time (lower is better), and return the completion time (0 if the run hadn't been
    /// started — e.g. the player spawned past the start line). The finish kill-delay re-teleport is deferred.
    /// </summary>
    public float FinishStage(Player p)
    {
        CtsState st = GetState(p);
        if (st.RunStartTime <= 0f)
            return 0f; // run never started

        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        float runTime = now - st.RunStartTime;
        st.RunStartTime = 0f;
        if (runTime <= 0f)
            return 0f;

        st.Completions += 1;
        if (st.FastestTime <= 0f || runTime < st.FastestTime)
            st.FastestTime = runTime; // QC SP_RACE_FASTEST (lower is better)

        // QC Race_FinalCheckpoint scoring: SP_RACE_FASTEST tracks the best (lowest) completion, TIME_ENCODE'd to
        // hundredths (SFL_TIME). Non-qualifying CTS also counts completions as SP_RACE_LAPS + cumulative SP_RACE_TIME.
        Scoring.ScoreField? fastest = Scoring.GameScores.Field("RACE_FASTEST");
        if (fastest is not null)
            Scoring.GameScores.SetBestTime(p, fastest, Scoring.GameScores.TimeEncode(runTime));
        if (!Qualifying)
        {
            Scoring.ScoreField? laps = Scoring.GameScores.Field("RACE_LAPS");
            if (laps is not null) Scoring.GameScores.AddToPlayer(p, laps, 1);
            Scoring.ScoreField? rtime = Scoring.GameScores.Field("RACE_TIME");
            if (rtime is not null) Scoring.GameScores.AddToPlayer(p, rtime, Scoring.GameScores.TimeEncode(runTime));
        }

        // QC race_setTime: file the completion time into the persistent per-map CTS ranking.
        LastRecord = RaceRecords.SetTime(MapName, RecordType, runTime, p.PersistentId, p.NetName);
        LastRecordPlayer = p;

        // QC Race_FinalCheckpoint: schedule the kill-delay re-teleport back to the start.
        ScheduleRetract(p, now);
        return runTime;
    }

    /// <summary>The current persistent server record (rank 1) CTS time for this map (0 if none).</summary>
    public float ServerRecord => RaceRecords.ServerRecord(MapName, RecordType);
    public string ServerRecordHolder => RaceRecords.ServerRecordHolder(MapName, RecordType);

    /// <summary>QC g_cts_finish_kill_delay: schedule the runner to be retracted to start after the kill delay.</summary>
    private void ScheduleRetract(Player p, float now)
    {
        // QC sv_cts.qc: g_cts_finish_kill_delay defaults to 2 s; only an explicit 0 disables the retract and -1
        // makes it instant. When the gametypes cfg isn't loaded the cvar reads empty → fall back to the 2 s default
        // (the old fallback-to-0 retracted the runner instantly without the cfg).
        float delay = TryCvar(CvarFinishKillDelay, out float v) ? v : DefaultFinishKillDelay;
        _retractAt[p] = now + System.MathF.Max(0f, delay);
    }

    /// <summary>QC race_PreparePlayer: reset the runner's run timer so they restart the stage from the beginning.</summary>
    public void RetractRunner(Player p)
    {
        GetState(p).RunStartTime = 0f;
        OnFinishRetract?.Invoke(p);
    }

    /// <summary>Per-frame: fire any due finish-retracts (the deferred kill-delay re-teleport). Call once per server frame.</summary>
    public void Tick(float now)
    {
        if (_retractAt.Count == 0) return;
        List<Player>? due = null;
        foreach (var kv in _retractAt)
            if (now >= kv.Value) (due ??= new List<Player>()).Add(kv.Key);
        if (due is null) return;
        foreach (Player p in due) { _retractAt.Remove(p); RetractRunner(p); }
    }

    private static bool TryCvar(string name, out float value)
    {
        value = 0f;
        if (Api.Services is null) return false;
        string s = Api.Cvars.GetString(name);
        if (string.IsNullOrEmpty(s)) return false;
        value = Api.Cvars.GetFloat(name);
        return true;
    }

    /// <summary>The fastest stage-completion time recorded for a player (0 if none), QC SP_RACE_FASTEST.</summary>
    public float FastestTimeOf(Player p) => _states.TryGetValue(p, out CtsState? st) ? st.FastestTime : 0f;

    /// <summary>The current overall best time across all players (0 if nobody has finished). Used for ranking.</summary>
    public float BestTime()
    {
        float best = 0f;
        foreach (CtsState st in _states.Values)
            if (st.FastestTime > 0f && (best <= 0f || st.FastestTime < best))
                best = st.FastestTime;
        return best;
    }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(cts, PlayerDies): a death only forces a respawn; CTS never scores frags. The
    /// runner's respawn is FORCED (RESPAWN_FORCE) so the dead runner restarts the stage immediately instead of
    /// waiting the generic delay (QC g_cts_respawn_delay -1 = instant), and the in-progress run is abandoned.
    /// </summary>
    private bool OnDeath(ref DeathEvent ev)
    {
        if (ev.Victim is not Player victim)
            return false;

        // QC: frag_target.respawn_flags |= RESPAWN_FORCE — CTS forces an immediate respawn so the runner can
        // re-attempt the stage at once. This is now honored end-to-end: RespawnTiming.Calculate OR's in (no longer
        // clobbers) the flag, and it reads the respawn delay via GAMETYPE_DEFAULTED_SETTING so CTS's
        // g_cts_respawn_delay_small/large = -1 collapse to an instant respawn.
        victim.RespawnFlags |= RespawnFlag.Force;

        // Abandon the in-progress run (QC race_AbandonRaceCheck) so the next start re-stamps the timer.
        GetState(victim).RunStartTime = 0f;
        // Also cancel a pending finish-retract for the runner — a death already sends them to respawn.
        _retractAt.Remove(victim);
        Events?.OnFrag(ev.Attacker as Player, victim, ev.DeathType);
        return false;
    }

    // ============================================================================================
    //  CTS combat / loadout rules (QC sv_cts.qc mutator hooks)
    // ============================================================================================

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(cts, Damage_Calculate)</c>: when <c>g_cts_selfdamage</c> is 0 (e.g. ruleset-XDF),
    /// zero self-inflicted damage and all fall damage so a runner can rocket-jump/fall without losing the run.
    /// With <c>g_cts_selfdamage 1</c> (the stock default) damage is left intact.
    /// </summary>
    private bool OnDamageCalculate(ref MutatorHooks.DamageCalculateArgs a)
    {
        // QC autocvar_g_cts_selfdamage defaults to 1 (normal damage); only an explicit 0 suppresses it.
        bool selfDamage = !TryCvar(CvarSelfDamage, out float v) || v != 0f;
        if (selfDamage)
            return false;

        bool isSelf = a.Attacker is not null && ReferenceEquals(a.Attacker, a.Target);
        bool isFall = DeathTypes.BaseOf(a.DeathType) == DeathTypes.Fall;
        if (isSelf || isFall)
            a.Damage = 0f; // QC: frag_damage = 0
        return false;
    }

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(cts, ForbidThrowCurrentWeapon)</c> + <c>ForbidDropCurrentWeapon</c>: no weapon
    /// dropping or throwing in CTS (this port folds both QC hooks into the single ForbidThrowCurrentWeapon chain).
    /// </summary>
    private bool OnForbidThrowCurrentWeapon(ref MutatorHooks.ForbidThrowCurrentWeaponArgs a) => true;

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(cts, FilterItem)</c> + <c>MonsterDropItem</c>: drop (delete) loot/monster-dropped
    /// items in CTS unless <c>g_cts_drop_monster_items</c> keeps the monster ones. Non-loot map items are kept.
    /// </summary>
    private bool OnFilterItemDefinition(ref MutatorHooks.FilterItemDefinitionArgs a)
    {
        // QC: if (ITEM_IS_LOOT(item)) { if (item.monster_item && g_cts_drop_monster_items) return false; return true; }
        // The item-class registry doesn't expose the loot/monster_item flags yet, so we key off the classname tag:
        // dropped weapons/ammo (the loot in weapon-light CTS maps) carry the "droppedweapon" / "dropped_*" class.
        string id = a.Definition.ClassName;
        bool isLoot = id.StartsWith("dropped", System.StringComparison.Ordinal)
                   || id == "droppedweapon";
        if (!isLoot)
            return false; // a fixed map item — keep it

        bool isMonsterDrop = id.Contains("monster", System.StringComparison.Ordinal);
        bool keepMonsterItems = TryCvar(CvarDropMonsterItems, out float v) && v != 0f;
        if (isMonsterDrop && keepMonsterItems)
            return false; // QC: monster_item && g_cts_drop_monster_items → keep
        return true;      // QC: delete the loot
    }

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(cts, WantWeapon)</c> (<c>want = (weapon == WEP_SHOTGUN)</c>, mutator-blocked):
    /// CTS gives only the Shotgun. On spawn, force the runner's owned set to exactly the Shotgun and select it.
    /// </summary>
    private bool OnPlayerSpawn(ref MutatorHooks.PlayerSpawnArgs a)
    {
        ForceShotgunOnly(a.Player);
        return false;
    }

    /// <summary>QC keeps the Shotgun-only loadout enforced each frame (the WantWeapon set is re-evaluated on respawn).</summary>
    private bool OnPlayerPreThink(ref MutatorHooks.PlayerPreThinkArgs a)
    {
        Entity player = a.Player;
        if ((player.Flags & EntFlags.Client) != 0 && player.DeadState == DeadFlag.No)
            ForceShotgunOnly(player);
        return false;
    }

    /// <summary>Force the player's owned weapon set to exactly {Shotgun} and switch to it (QC WantWeapon → WEP_SHOTGUN).</summary>
    private static void ForceShotgunOnly(Entity player)
    {
        Weapon? shotgun = Weapons.ByName("shotgun");
        if (shotgun is null)
            return;
        if (player.OwnedWeaponSet.Has(shotgun)
            && ReferenceEquals(Inventory.CurrentWeapon(player), shotgun)
            && CountOwned(player) == 1)
            return; // already shotgun-only with it active — nothing to do this frame
        player.OwnedWeaponSet.Clear();
        Inventory.GiveWeapon(player, shotgun);
        Inventory.SwitchWeapon(player, shotgun);
    }

    /// <summary>Number of weapons in the player's owned set (cheap guard so the per-frame force is idempotent).</summary>
    private static int CountOwned(Entity player)
    {
        int n = 0;
        foreach (Weapon w in Weapons.All)
            if (player.OwnedWeaponSet.Has(w)) n++;
        return n;
    }
}
