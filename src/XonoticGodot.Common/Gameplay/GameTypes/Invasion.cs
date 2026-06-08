using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Invasion gametype — port of <c>CLASS(Invasion, Gametype)</c>
/// (common/gametypes/gametype/invasion/invasion.qh + sv_invasion.qc).
///
/// A co-op survival mode: players fight successive waves ("rounds") of monsters. Each round spawns an
/// increasing number of monsters (QC <c>inv_maxspawned</c> scales with the round count and player count);
/// the round advances once every monster in it has been killed (QC <c>inv_numkilled &gt;= inv_maxspawned</c>).
/// Across rounds, killing monsters banks points and the match ends when the point limit (cvar
/// <c>g_invasion_point_limit</c> / pointlimit, default 50) is reached, or after the configured number of
/// rounds. (QC also supports STAGE/HUNT variants via <c>g_invasion_type</c>; the default co-op survival type
/// is modeled here.)
///
/// Faithfully ported (the wave/scoring rule):
///  - wave bookkeeping (<see cref="WaveState"/>: round counter, spawned/killed counts);
///  - Combat.Death → a killed monster advances the wave and banks a point toward the limit;
///  - point-limit / round-limit win check (<see cref="PointLimit"/>, QC pointlimit; <see cref="MaxRounds"/>).
///
/// Faithfully ported (objective layer):
///  - monster spawning from <c>invasion_spawnpoint</c> entities via the Monsters catalog + the shared
///    <see cref="MonsterAI"/> setup (<see cref="SpawnWaveMonster"/>/<see cref="SpawnMonsterDef"/>, QC
///    invasion_SpawnMonsters / invasion_PickMonster / invasion_SpawnChosenMonster);
///  - the wave fill loop (<see cref="Tick"/>, QC Invasion_CheckWinner: keep spawning until the wave is full,
///    advance the round when it's cleared);
///  - wave bookkeeping + point/round-limit win (<see cref="WaveState"/>, QC inv_* counters);
///  - the three g_invasion_type variants (QC INV_TYPE_*): ROUND (endless point-banked waves), HUNT (clear all
///    placed monsters), STAGE (reach the level end — an <c>invasion_roundend</c> trigger fires);
///  - per-wave monster lists (QC invasion_wave entities' <c>spawnmob</c>: <see cref="AddWave"/> keys a monster
///    list to a round; <see cref="PickMonster"/> draws from it when present, else picks at random);
///  - monster-skill scaling (QC inv_monsterskill = round + player factor) stamped on each spawned monster.
/// </summary>
[GameType]
public sealed class Invasion : GameType
{
    /// <summary>QC g_invasion_type (sv_invasion.qh): which invasion variant is active.</summary>
    public enum InvasionType
    {
        /// <summary>INV_TYPE_ROUND (0): endless point-banked rounds of scaling waves (the default).</summary>
        Round = 0,
        /// <summary>INV_TYPE_HUNT (1): clear the map of the placed/spawned enemies; win when none remain.</summary>
        Hunt = 1,
        /// <summary>INV_TYPE_STAGE (2): reach the end of the level — an <c>invasion_roundend</c> trigger ends it.</summary>
        Stage = 2,
    }

    private const string CvarType = "g_invasion_type";
    /// <summary>The active invasion variant (QC autocvar_g_invasion_type), default ROUND.</summary>
    public InvasionType Type => TryCvar(CvarType, out float v) ? (InvasionType)(int)v : InvasionType.Round;
    // ----- point/round limit cvars + defaults (gametype default pointlimit=50) -----
    private const string CvarPointLimit    = "g_invasion_point_limit";
    private const string CvarFragLimit     = "fraglimit"; // GameRules_limit_score writes the point limit here
    private const int    DefaultPointLimit = 50;          // gametype_init "pointlimit=50"

    private const string CvarMaxRounds    = "g_invasion_rounds";
    private const int    DefaultMaxRounds = 15;           // QC inv_maxrounds default

    private const string CvarMonsterCount    = "g_invasion_monster_count";
    private const int    DefaultMonsterCount = 10;        // QC autocvar_g_invasion_monster_count default

    /// <summary>Wave/round progress (QC inv_roundcnt / inv_numspawned / inv_numkilled / inv_maxspawned).</summary>
    public sealed class WaveState
    {
        /// <summary>Current round number (QC <c>inv_roundcnt</c>), starting at 1.</summary>
        public int Round = 1;

        /// <summary>How many monsters have been spawned in the current round (QC <c>inv_numspawned</c>).</summary>
        public int Spawned;

        /// <summary>How many monsters have been killed in the current round (QC <c>inv_numkilled</c>).</summary>
        public int Killed;

        /// <summary>Target number of monsters for the current round (QC <c>inv_maxspawned</c>).</summary>
        public int MaxSpawned;
    }

    /// <summary>The single shared wave state (co-op: all players fight the same monsters).</summary>
    public WaveState Wave { get; } = new();

    /// <summary>
    /// Per-wave monster lists (QC invasion_wave entities): a monster-netname list keyed by wave number. When a
    /// wave has a list, <see cref="PickMonster"/> draws from it (QC wave_ent.spawnmob) instead of picking at
    /// random. <see cref="GetWaveMonsters"/> resolves a round to the best matching list (exact, else the last
    /// list with a lower-or-equal number — QC invasion_GetWaveEntity).
    /// </summary>
    private readonly List<(int round, string[] monsters)> _waves = new();

    /// <summary>QC g_invasion_roundends: STAGE objective triggers. Set <see cref="RoundEndReached"/> when one fires.</summary>
    public bool RoundEndReached { get; private set; }
    private int _roundEnds;            // count of registered invasion_roundend triggers
    private int _monsterSkill = 1;     // QC inv_monsterskill, scaled per round + player count

    /// <summary>QC spawnfunc invasion_wave: define the monster list (space-separated netnames) for a wave number.</summary>
    public void AddWave(int waveNumber, string spawnmob)
    {
        if (string.IsNullOrWhiteSpace(spawnmob)) return;
        string[] list = spawnmob.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
        if (list.Length > 0) _waves.Add((waveNumber, list));
    }

    /// <summary>QC invasion_GetWaveEntity: the monster list governing <paramref name="round"/> (null = random pick).</summary>
    public string[]? GetWaveMonsters(int round)
    {
        string[]? exact = null, best = null; int bestNum = int.MinValue;
        foreach (var (num, list) in _waves)
        {
            if (num == round) { exact = list; break; }
            if (num <= round && num > bestNum) { best = list; bestNum = num; }
        }
        return exact ?? best;
    }

    /// <summary>QC spawnfunc invasion_roundend: register a STAGE objective trigger (reaching it ends the level).</summary>
    public void AddRoundEnd() => _roundEnds++;

    /// <summary>QC target_invasion_roundend_use: a STAGE objective trigger fired — the players reached the end.</summary>
    public void TriggerRoundEnd()
    {
        if (Type == InvasionType.Stage) RoundEndReached = true;
    }

    private HookHandler<DeathEvent>? _deathHandler;

    /// <summary>Optional sink for the host/controller to react to a kill.</summary>
    public IMatchEvents? Events;

    /// <summary>Total monster kills banked toward the point limit across all rounds (QC team/score points).</summary>
    public int MonstersKilled { get; private set; }

    /// <summary>QC checkrules end-of-match latch: true once the point limit or final round is reached.</summary>
    public bool MatchEnded { get; private set; }

    public Invasion()
    {
        NetName = "inv";
        DisplayName = "Invasion";
        TeamGame = false; // QC: GAMETYPE_FLAG_USEPOINTS (co-op survival, default type)
    }

    /// <summary>The invasion_spawnpoint origins monsters spawn at (QC g_invasion_spawns). Empty → random.</summary>
    public readonly List<Vector3> SpawnPoints = new();

    /// <summary>The monsters currently alive this wave (QC g_monsters), tracked for the fill loop.</summary>
    public readonly List<Entity> LiveMonsters = new();

    /// <summary>Seconds between wave-fill spawn attempts (QC g_invasion_spawn_delay).</summary>
    private const string CvarSpawnDelay = "g_invasion_spawn_delay";
    private float _nextSpawnTime;

    public override void OnInit()
    {
        // QC: monsters spawn at the map's invasion_spawnpoint entities (see SpawnPoints / AddSpawnPoint).
        // gametype_init flags (USEPOINTS) and the STAGE/HUNT type are engine/server-config concerns.
        SpawnPoints.Clear();
        LiveMonsters.Clear();
        // QC the per-map wave lists (g_invasion_waves) + round-end triggers (g_invasion_roundends) are rebuilt
        // from the map entities each load — clear them so a map (re)load doesn't inherit the previous map's waves.
        // This is also the test-isolation guarantee: the gametype is a registry SINGLETON (GameTypes.ByName),
        // reused across GameWorld.Boot calls, so without this clear a prior boot's waves leak into the next.
        _waves.Clear();
        _roundEnds = 0;
        RoundEndReached = false;
    }

    /// <summary>QC spawnfunc invasion_spawnpoint: register a monster spawn origin.</summary>
    public void AddSpawnPoint(Vector3 origin) => SpawnPoints.Add(origin);

    /// <summary>Seconds between wave-fill spawn attempts (g_invasion_spawn_delay, else 2).</summary>
    public float SpawnDelay => TryCvar(CvarSpawnDelay, out float v) && v > 0f ? v : 2f;

    /// <summary>The point limit in force (g_invasion_point_limit, else fraglimit, else 50). 0 = no limit.</summary>
    public int PointLimit
    {
        get
        {
            if (TryCvar(CvarPointLimit, out float pl)) return (int)pl;
            if (TryCvar(CvarFragLimit, out float fl)) return (int)fl;
            return DefaultPointLimit;
        }
    }

    /// <summary>The number of rounds before the match ends (QC inv_maxrounds, default 15).</summary>
    public int MaxRounds => TryCvar(CvarMaxRounds, out float v) && v > 0f ? (int)v : DefaultMaxRounds;

    /// <summary>Base monster count per round (QC autocvar_g_invasion_monster_count, default 10).</summary>
    public int BaseMonsterCount => TryCvar(CvarMonsterCount, out float v) && v > 0f ? (int)v : DefaultMonsterCount;

    /// <summary>The number of active players (QC numplayers) — drives the per-round monster-skill scaling. Host-set; default 1.</summary>
    public int PlayerCount { get; set; } = 1;

    /// <summary>QC <c>inv_monsterskill = inv_roundcnt + max(1, numplayers * 0.3)</c>: the wave's monster skill.</summary>
    public int ComputeMonsterSkill(int round)
        => round + (int)System.MathF.Max(1f, PlayerCount * 0.3f);

    public void Activate()
    {
        if (_deathHandler is not null)
            return;
        MatchEnded = false;
        RoundEndReached = false;
        MonstersKilled = 0;
        Wave.Round = 1;
        Wave.Spawned = 0;
        Wave.Killed = 0;
        Wave.MaxSpawned = ComputeWaveSize(Wave.Round);
        _monsterSkill = ComputeMonsterSkill(Wave.Round);

        // QC invasion_ScoreRules (sv_invasion.qc): GameRules_score_enabled(false); GameRules_scoring(0, 0, 0, {
        // field(SP_KILLS, "kills", SFL_SORT_PRIO_PRIMARY); }). Invasion is co-op vs monsters — the only score is
        // monster kills, so SP_KILLS is the primary sort key (no SP_SCORE). independent_players disables the rest.
        Scoring.GameScores.ScoreRulesBasics(teams: false, scoreEnabled: false);
        Scoring.GameScores.DeclareColumn("KILLS", Scoring.ScoreFlags.None, "kills");
        Scoring.GameScores.SetSortKeys(Scoring.GameScores.Kills);

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

    /// <summary>
    /// QC inv_maxspawned: the number of monsters to spawn in a given round —
    /// <c>round(max(base, base * round * 0.5))</c>. Gives the wave its size target; <see cref="SpawnWaveMonster"/>
    /// fills it from the spawn points.
    /// </summary>
    public int ComputeWaveSize(int round)
    {
        int b = BaseMonsterCount;
        float scaled = System.MathF.Max(b, b * round * 0.5f);
        int n = (int)System.MathF.Round(scaled);
        return n < 1 ? 1 : n;
    }

    /// <summary>QC invasion spawn path: record that a monster spawned into the current round.</summary>
    public void NotifyMonsterSpawned()
    {
        Wave.Spawned += 1;
    }

    /// <summary>
    /// Advance Invasion one frame (QC Invasion_CheckWinner fill loop): while the current wave isn't fully
    /// spawned, spawn one monster every <see cref="SpawnDelay"/> seconds; once the wave is cleared (all killed)
    /// roll over to the next round. Call each tick. Safe after <see cref="MatchEnded"/>.
    /// </summary>
    public void Tick()
    {
        if (MatchEnded)
            return;
        float now = Api.Services is not null ? Api.Clock.Time : 0f;

        // Prune dead/removed monsters from the live list.
        for (int i = LiveMonsters.Count - 1; i >= 0; i--)
        {
            Entity m = LiveMonsters[i];
            if (m.IsFreed || m.DeadState != DeadFlag.No || m.Health <= 0f)
                LiveMonsters.RemoveAt(i);
        }

        // STAGE: the level-end objective trigger fired → the players win (QC WinningCondition_Invasion STAGE).
        if (Type == InvasionType.Stage && RoundEndReached)
        {
            MatchEnded = true;
            return;
        }

        // QC: keep spawning until the wave's full quota has been spawned (alive + already-killed < max).
        if (Wave.Spawned < Wave.MaxSpawned)
        {
            if (now >= _nextSpawnTime)
            {
                SpawnWaveMonster();
                _nextSpawnTime = now + SpawnDelay;
            }
            return;
        }

        // HUNT: clear the map — once the whole spawned set is dead, the players win (QC g_monsters empty check).
        if (Type == InvasionType.Hunt)
        {
            if (LiveMonsters.Count == 0 && Wave.Killed >= Wave.MaxSpawned)
                MatchEnded = true;
            return;
        }

        // ROUND (and STAGE while waiting for the objective): once the wave is dead, advance to the next round.
        if (Wave.Spawned >= 1 && Wave.Killed >= Wave.MaxSpawned)
            AdvanceWaveIfCleared();
    }

    /// <summary>
    /// QC invasion_SpawnChosenMonster: spawn one monster for the current wave at a random spawn point (or the
    /// origin when none are registered), via <see cref="invasion_PickMonster"/>. Increments the spawn count.
    /// Returns the monster entity (null when no facade or no monsters are registered).
    /// </summary>
    public Entity? SpawnWaveMonster()
    {
        Monster? def = PickMonster();
        if (def is null)
            return null;
        Vector3 origin = SpawnPoints.Count > 0
            ? SpawnPoints[XonoticGodot.Common.Math.Prandom.RangeInt(0, SpawnPoints.Count)]
            : Vector3.Zero;
        Entity? m = SpawnMonsterDef(def, origin);
        if (m is not null)
        {
            m.GtWaveMonster = true;
            LiveMonsters.Add(m);
            NotifyMonsterSpawned();
        }
        return m;
    }

    /// <summary>
    /// QC invasion_PickMonster / the per-wave spawnmob draw: if the current round has a configured monster list
    /// (<see cref="AddWave"/>), pick a monster by netname from it; otherwise pick uniformly over the registered
    /// Monsters catalog. Deterministic. Returns null if nothing is available.
    /// </summary>
    public Monster? PickMonster()
    {
        // QC: a wave entity's spawnmob list overrides the random pick for that round.
        string[]? list = GetWaveMonsters(Wave.Round);
        if (list is { Length: > 0 })
        {
            // try a handful of draws so a stray bad name doesn't abort the spawn.
            for (int attempt = 0; attempt < list.Length; attempt++)
            {
                string name = list[XonoticGodot.Common.Math.Prandom.RangeInt(0, list.Length)];
                Monster? m = Monsters.ByName(name);
                if (m is not null) return m;
            }
        }
        int n = Monsters.Count;
        if (n <= 0)
            return null;
        return Monsters.All[XonoticGodot.Common.Math.Prandom.RangeInt(0, n)];
    }

    /// <summary>
    /// QC invasion_SpawnChosenMonster (sv_invasion.qc): spawn a concrete monster of <paramref name="def"/> at
    /// <paramref name="origin"/> through the shared <see cref="MonsterAI.SpawnMonster"/> driver — the port of QC
    /// <c>spawnmonster()</c> that invasion's QC equivalent calls. The driver runs the descriptor's
    /// <see cref="Monster.Spawn"/> (= <see cref="MonsterAI.Setup"/> + per-type specifics) exactly once, stamps the
    /// SPAWNED|NORESPAWN spawnflags, and — crucially — wires the per-frame think (<c>e.Think</c>/<c>e.NextThink</c>)
    /// so the simulation loop's SV_RunThink actually drives the monster brain each tick. Then stamps the wave's
    /// monster skill. Returns the new monster entity, or null when no facade is wired or no valid monster spawned.
    /// </summary>
    public Entity? SpawnMonsterDef(Monster def, Vector3 origin)
    {
        if (Api.Services is null)
            return null;

        // QC: spawnmonster(spawn(), tospawn, mon, spawn_point, spawn_point, spawn_point.origin, /*respwn*/ false,
        // /*removeifinvalid*/ false, /*moveflag*/ MONSTER_MOVE_WANDER). respwn=false adds MONSTERFLAG_NORESPAWN —
        // invasion monsters don't respawn (a kill advances the wave). The port has no spawn-point entity, so the
        // spawnedBy/follow args (only meaningful for player summons) are null; the resolved descriptor is passed
        // as monsterId (PickMonster already drew it, mirroring the wave's spawnmob list).
        Entity e = Api.Entities.Spawn();
        Entity? m = MonsterAI.SpawnMonster(e, monster: "", monsterId: def, spawnedBy: null, follow: null,
            origin: origin, respawn: false, removeIfInvalid: false, moveFlags: MonsterAI.MonsterMove_Wander);
        if (m is null)
            return null;

        // QC MUTATOR_HOOKFUNCTION(inv, MonsterSpawn): mon.monster_skill = inv_monsterskill. That hook fires at the
        // END of Monster_Spawn_Setup — after the skill-scaled health roll — so, like QC, this only scales the
        // monster's runtime speed/damage (via MONSTER_SKILLMOD), not its spawn health.
        if (MonsterAI.StateOf(m) is { } st)
            st.Skill = _monsterSkill;
        return m;
    }

    /// <summary>
    /// QC SV_StartFrame round logic (core): once every monster in the current round has been killed
    /// (<c>Killed &gt;= MaxSpawned</c> and at least one spawned), advance to the next round and size its wave.
    /// Returns true if the match should end because the final round was cleared.
    /// </summary>
    public bool AdvanceWaveIfCleared()
    {
        if (Wave.Spawned < 1 || Wave.Killed < Wave.MaxSpawned)
            return false;

        // HUNT doesn't run rounds — clearing the placed set is the win (handled in Tick), not a round rollover.
        if (Type == InvasionType.Hunt)
            return false;

        if (Wave.Round >= MaxRounds)
        {
            MatchEnded = true; // survived all rounds
            return true;
        }

        Wave.Round += 1;
        Wave.Spawned = 0;
        Wave.Killed = 0;
        Wave.MaxSpawned = ComputeWaveSize(Wave.Round);
        _monsterSkill = ComputeMonsterSkill(Wave.Round); // QC: skill scales with the round + player count
        return false;
    }

    /// <summary>
    /// The obituary handler — when a monster is killed, advance the current wave's kill count, bank a point,
    /// check the point limit, and roll over to the next round if the wave is cleared. Player deaths don't
    /// score (this is co-op vs monsters); a non-monster victim is ignored here.
    /// </summary>
    private bool OnDeath(ref DeathEvent ev)
    {
        if (MatchEnded)
            return false;

        // A player victim doesn't score in Invasion (co-op); only monster kills matter.
        if (ev.Victim is Player victim)
        {
            Events?.OnFrag(ev.Attacker as Player, victim, ev.DeathType);
            return false;
        }

        // Victim is a monster (QC IS_MONSTER): the killer banks a point and clears one of the wave.
        if (IsMonster(ev.Victim))
        {
            Wave.Killed += 1;        // QC inv_numkilled
            MonstersKilled += 1;     // banked toward the point limit

            // QC invasion MonsterDies/PlayerScore: a player killing a monster gets SP_KILLS +1 (the primary key).
            // The shared Scores table only credits player victims, so Invasion writes the monster-kill column here.
            if (ev.Attacker is Player killer)
                Scoring.GameScores.AddToPlayer(killer, Scoring.GameScores.Kills, 1);

            CheckPointLimit();
            if (!MatchEnded)
                AdvanceWaveIfCleared();
        }

        return false;
    }

    /// <summary>QC IS_MONSTER(ent): a monster victim — carries the FL_MONSTER flag (set by <see cref="MonsterAI.Setup"/>).</summary>
    private static bool IsMonster(Entity e) => (e.Flags & EntFlags.Monster) != 0 || e.ClassName == "monster";

    /// <summary>QC GameRules_limit_score: latch the match once the banked kills reach the point limit.</summary>
    public void CheckPointLimit()
    {
        int limit = PointLimit;
        if (limit > 0 && MonstersKilled >= limit)
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
