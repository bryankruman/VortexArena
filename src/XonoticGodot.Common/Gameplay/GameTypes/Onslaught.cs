using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;
using GS = XonoticGodot.Common.Gameplay.Scoring.GameScores; // T7: alias the static score table for the per-mode ScoreRules

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Onslaught gametype — port of <c>CLASS(Onslaught, Gametype)</c>
/// (common/gametypes/gametype/onslaught/onslaught.qh + sv_onslaught.qc).
///
/// A team objective mode: each team defends a generator and seizes a network of control points to extend its
/// reach toward — and ultimately destroy — the enemy generator. A generator only becomes vulnerable once the
/// attacking team controls a control point linked to it; reducing the enemy generator's health to zero
/// destroys it and wins the round for the attackers (QC: the team whose generator is destroyed loses; with
/// pointlimit=1 a single generator kill ends the match). Rounds are time-bounded (gametype default
/// timelimit=20, plus a per-round timelimit via the round handler).
///
/// Faithfully ported (the generator-destruction win rule + link graph):
///  - per-team generator health + alive flag (<see cref="GeneratorState"/>, QC g_onslaught_gen_health);
///  - the control-point + generator + link graph (<see cref="AddGenerator"/>/<see cref="AddControlPoint"/>/
///    <see cref="Link"/>, QC onslaught_generator / onslaught_controlpoint + ons_worldlinklist) with the
///    power-flow propagation that decides which nodes are shielded vs attackable
///    (<see cref="UpdateLinks"/>, QC onslaught_updatelinks);
///  - generator damage/destruction gated by the shield (<see cref="DamageGenerator"/>, QC ons_GeneratorDamage)
///    → the other team wins the round when one generator falls;
///  - the round handler + warmup (<see cref="Handler"/>, QC round_handler_Spawn) via <see cref="Tick"/>;
///  - the control-point CAPTURE-BY-BUILD combat (QC ons_ControlPoint_Touch → Icon_Spawn → BuildThink → Think /
///    Icon_Damage / Icon_Heal) + the generator damage pipeline (QC ons_GeneratorSetup / ons_GeneratorDamage),
///    both driven through the shared <see cref="Damage.DamageSystem"/> via <see cref="Entity.GtEventDamage"/>
///    (<see cref="CpCombat"/>): a point only flips once its build icon completes, and a generator only takes
///    real weapon damage once an enemy controls a linked CP;
///  - the overtime generator-decay (QC the stalemate branch of Onslaught_CheckWinner) via <see cref="Tick"/>.
///
/// Deferred (NOTE — cross-boundary): the generator/control-point/icon entity models + sprites (CSQC), the
/// proximity-decap (g_onslaught_cp_proximitydecap), vehicles/turrets, and the bot waypointing — client/AI.
/// </summary>
[GameType]
public sealed class Onslaught : GameType
{
    // ----- generator health cvar + default (QC autocvar_g_onslaught_gen_health) -----
    private const string CvarGenHealth    = "g_onslaught_gen_health";
    private const float  DefaultGenHealth = 2000f; // QC default g_onslaught_gen_health

    /// <summary>A team's generator (QC <c>onslaught_generator</c> entity: health + destroyed state).</summary>
    public sealed class GeneratorState
    {
        /// <summary>Remaining generator health (QC RES_HEALTH). Destroyed at 0.</summary>
        public float Health;

        /// <summary>Max/starting health (QC max_health = g_onslaught_gen_health).</summary>
        public float MaxHealth;

        /// <summary>True once health has reached 0 (QC generator dead → its team loses).</summary>
        public bool Destroyed => Health <= 0f;
    }

    /// <summary>
    /// A node in the Onslaught power graph — a generator or a control point (QC onslaught_generator /
    /// onslaught_controlpoint share the iscaptured/islinked/isshielded flags + .team). Links between nodes
    /// (QC ons_worldlinklist) carry power outward from the generators.
    /// </summary>
    public sealed class OnsNode
    {
        /// <summary>True if this node is a generator (QC classname "onslaught_generator").</summary>
        public bool IsGenerator;
        /// <summary>The team owning this node (QC .team); a CP starts neutral.</summary>
        public int Team = Teams.None;
        /// <summary>QC .iscaptured: a generator is captured while alive; a CP is captured once a team owns it.</summary>
        public bool Captured;
        /// <summary>QC .islinked: powered (reachable from its team's generator through same-team links).</summary>
        public bool Linked;
        /// <summary>QC .isshielded: invulnerable — true unless an enemy-owned powered neighbor exposes it.</summary>
        public bool Shielded = true;
        /// <summary>Generator state when <see cref="IsGenerator"/> (health/destroyed), else null.</summary>
        public GeneratorState? Gen;
        /// <summary>Adjacent nodes via links (QC ons_worldlinklist neighbors).</summary>
        public readonly List<OnsNode> Neighbors = new();
        /// <summary>Stable id (control-point id for the legacy CaptureControlPoint API).</summary>
        public int Id;
    }

    /// <summary>Every node in the power graph (generators + control points), QC ons_worldgeneratorlist + ons_worldcplist.</summary>
    public readonly List<OnsNode> Nodes = new();

    /// <summary>Generators keyed by owning team color code (see <see cref="Teams"/>).</summary>
    private readonly Dictionary<int, GeneratorState> _generators = new();

    /// <summary>Generator nodes keyed by team, and control-point nodes keyed by id, for fast lookup.</summary>
    private readonly Dictionary<int, OnsNode> _genNodes = new();
    private readonly Dictionary<int, OnsNode> _cpNodes = new();

    /// <summary>The round-phase driver (QC round_handler) — created on <see cref="Activate"/>.</summary>
    public RoundHandler? Handler { get; private set; }

    /// <summary>
    /// The control-point + generator COMBAT layer (QC sv_onslaught.qc icon/generator funcs): the buildable
    /// capture icons, the generator damage entities, and the overtime decay. Owns the entity ↔ graph bridge so
    /// weapon damage routes through the shared <see cref="Damage.DamageSystem"/> via
    /// <see cref="Entity.GtEventDamage"/>. Created in the ctor (the graph is this <see cref="Onslaught"/>).
    /// (Named <c>CpCombat</c> — not <c>Combat</c> — so it doesn't shadow the static
    /// <see cref="Damage.Combat"/> bus this file uses for the death hook.)
    /// </summary>
    public OnslaughtControlPoint CpCombat { get; }

    private HookHandler<DeathEvent>? _deathHandler;

    /// <summary>Optional sink for the host/controller to react to a kill.</summary>
    public IMatchEvents? Events;

    /// <summary>QC checkrules end-of-round latch: true once a generator has been destroyed.</summary>
    public bool MatchEnded { get; private set; }

    /// <summary>The winning team color code (the team whose generator survived), or 0 if none yet.</summary>
    public int WinningTeam { get; private set; }

    public Onslaught()
    {
        NetName = "ons";
        DisplayName = "Onslaught";
        TeamGame = true;
        CpCombat = new OnslaughtControlPoint(this);
    }

    /// <summary>Absolute sim time this overtime decay last fired (one step per second, QC ons_overtime_damagedelay).</summary>
    private float _nextOvertimeDecay;

    public override void OnInit()
    {
        // QC: generators/control-points/links are built from the map's onslaught_* entities (see AddGenerator
        // / AddControlPoint / Link). gametype_init flags (TEAMPLAY) are the engine's job; OnInit clears state.
        Nodes.Clear();
        _generators.Clear();
        _genNodes.Clear();
        _cpNodes.Clear();
        CpCombat.Reset();
    }

    /// <summary>Starting generator health (g_onslaught_gen_health, else 2000), QC max_health.</summary>
    public float GeneratorHealth => TryCvar(CvarGenHealth, out float v) && v > 0f ? v : DefaultGenHealth;

    /// <summary>Starting control-point health (g_onslaught_cp_health, else 200), QC max_health.</summary>
    public float ControlPointHealth => TryCvar("g_onslaught_cp_health", out float v) && v > 0f ? v : 200f;

    public void Activate()
    {
        if (_deathHandler is not null)
            return;
        MatchEnded = false;
        WinningTeam = 0;
        _nextOvertimeDecay = 0f;
        CpCombat.ClearOvertime();
        GS.ResetTeams(); // QC Score_ClearAll at match start: zero both team slots before declaring

        // QC ons_ScoreRules (sv_onslaught.qc) GameRules_scoring(teams, SFL_SORT_PRIO_PRIMARY, 0, {
        // field_team(ST_ONS_GENS, "generators", PRIMARY); field(SP_ONS_CAPS, "caps", SECONDARY); field(SP_ONS_TAKES,
        // "takes", 0); }): the player primary is SP_SCORE with SP_ONS_CAPS secondary; ST_SCORE (team slot 0) has no
        // prio (stprio=0); ST_ONS_GENS (team slot 1) "generators" is the TEAM primary — credited +1 per generator
        // destroyed (round win, see BankRoundWin). Onslaught's team set is generator-derived (built later from the
        // map), so the team slots are seeded lazily by the first credit rather than SeedTeams here.
        GS.ScoreRulesBasics(teams: true);
        GS.TeamRulesBasics(scorePrio: Scoring.ScoreFlags.None); // ST_SCORE (slot 0) stprio = 0
        GS.SetTeamLabel(GS.TeamSlotSecondary, "generators", Scoring.ScoreFlags.SortPrioPrimary); // ST_ONS_GENS (slot 1)
        GS.DeclareColumn("ONS_CAPS", Scoring.ScoreFlags.None, "caps");   // QC field(SP_ONS_CAPS, "caps", SECONDARY)
        GS.DeclareColumn("ONS_TAKES", Scoring.ScoreFlags.None, "takes"); // QC field(SP_ONS_TAKES, "takes", 0)
        GS.SetSortKeys(GS.Score, GS.Field("ONS_CAPS"));

        // QC round_handler_Spawn(Onslaught_CheckTeams?, Onslaught_CheckWinner, ...). Onslaught rounds restart
        // when a generator falls; the handler drives warmup/countdown.
        Handler = new RoundHandler(() => Api.Services is not null ? Api.Clock.Time : 0f)
        {
            CanRoundStart = () => true,
            CanRoundEnd = () => CheckWinner() != 0,
            OnRoundStart = () => { },
        };
        Handler.Init(7f, Cvar("g_onslaught_warmup", 5f), Cvar("g_onslaught_round_timelimit", 0f));

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
    /// Advance the Onslaught round handler one frame (QC round_handler_Think) AND drive the overtime
    /// generator-decay (QC the stalemate branch of Onslaught_CheckWinner): once the timelimit / round end-time
    /// elapses, every generator self-damages each second (scaled by the enemy-linked control-point count) until
    /// one falls. Call each tick.
    /// </summary>
    public void Tick()
    {
        Handler?.Tick();
        DriveOvertime();
    }

    /// <summary>
    /// QC Onslaught_CheckWinner overtime gate (sv_onslaught.qc:1174): true once <c>timelimit</c> has elapsed
    /// since game start, or the round's end time has passed. While true, the generators decay
    /// (<see cref="OnslaughtControlPoint.OvertimeDecayTick"/>).
    /// </summary>
    public bool IsInOvertime()
    {
        if (MatchEnded || Api.Services is null)
            return false;
        float now = Api.Clock.Time;
        // QC: time > game_starttime + timelimit*60. The headless gametype runs the match from t=0 (the host's
        // game_starttime countdown is applied upstream by GameWorld); compare against the elapsed time directly.
        float timelimit = Cvar("timelimit", 0f);
        if (timelimit > 0f && now > GameStartTime + timelimit * 60f)
            return true;
        // QC: round_handler_GetEndTime() > 0 && endtime - time <= 0.
        if (Handler is { RoundEndTime: > 0f } h && h.RoundEndTime - now <= 0f && h.IsRoundStarted)
            return true;
        return false;
    }

    /// <summary>QC game_starttime — when the match begins. The host raises this for a countdown; default 0.</summary>
    public float GameStartTime { get; set; }

    /// <summary>Step the overtime decay at most once per second while in overtime (QC ons_overtime_damagedelay = time + 1).</summary>
    private void DriveOvertime()
    {
        if (!IsInOvertime())
        {
            CpCombat.ClearOvertime();
            return;
        }
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        if (now < _nextOvertimeDecay)
            return;
        _nextOvertimeDecay = now + 1f; // QC: ons_overtime_damagedelay = time + 1
        CpCombat.OvertimeDecayTick();
    }

    /// <summary>QC onslaught_generator spawn: register/reset a team's generator at full health (graph node).</summary>
    public GeneratorState RegisterGenerator(int team) => AddGenerator(team).Gen!;

    /// <summary>
    /// QC onslaught_generator spawn: add a generator node for <paramref name="team"/> at full health,
    /// captured + linked (a live generator is always powered). Returns the node.
    /// </summary>
    public OnsNode AddGenerator(int team)
    {
        float h = GeneratorHealth;
        var gen = new GeneratorState { Health = h, MaxHealth = h };
        var node = new OnsNode { IsGenerator = true, Team = team, Captured = true, Linked = true, Gen = gen };
        Nodes.Add(node);
        _generators[team] = gen;
        _genNodes[team] = node;
        return node;
    }

    /// <summary>
    /// QC onslaught_controlpoint spawn: add a (neutral) control-point node with a stable
    /// <paramref name="controlPointId"/>. CPs start neutral, unlinked, shielded until power reaches them.
    /// </summary>
    public OnsNode AddControlPoint(int controlPointId)
    {
        var node = new OnsNode { IsGenerator = false, Team = Teams.None, Captured = false, Id = controlPointId };
        Nodes.Add(node);
        _cpNodes[controlPointId] = node;
        return node;
    }

    /// <summary>QC ons_worldlinklist: connect two nodes with a power link (bidirectional).</summary>
    public void Link(OnsNode a, OnsNode b)
    {
        if (!a.Neighbors.Contains(b)) a.Neighbors.Add(b);
        if (!b.Neighbors.Contains(a)) b.Neighbors.Add(a);
    }

    /// <summary>The generator node owned by a team (or null).</summary>
    public OnsNode? GeneratorNode(int team) => _genNodes.TryGetValue(team, out var n) ? n : null;

    /// <summary>The control-point node with the given id (or null).</summary>
    public OnsNode? ControlPointNode(int id) => _cpNodes.TryGetValue(id, out var n) ? n : null;

    /// <summary>The generator owned by a team, or null if none registered.</summary>
    public GeneratorState? GeneratorFor(int team) => _generators.TryGetValue(team, out GeneratorState? g) ? g : null;

    /// <summary>The team's generators-destroyed score (QC teamscores(team, ST_ONS_GENS) — the round wins banked
    /// on each enemy-generator kill). Read through the unified GameScores two-slot team store (slot 1).</summary>
    public int GetTeamGenerators(int team) => GS.TeamScore(team, GS.TeamSlotSecondary);

    /// <summary>
    /// QC onslaught_updatelinks: propagate power from each (live) generator through same-team links, marking
    /// reachable nodes <see cref="OnsNode.Linked"/>; then a node is <see cref="OnsNode.Shielded"/> unless a
    /// powered enemy neighbor exposes it. Call after any capture or generator change; <see cref="DamageGenerator"/>
    /// and <see cref="CaptureControlPoint"/> call it for you.
    /// </summary>
    public void UpdateLinks()
    {
        // 1. Reset: generators are linked/shielded iff captured (alive); CPs start unlinked + shielded.
        foreach (var n in Nodes)
        {
            if (n.IsGenerator)
            {
                n.Captured = n.Gen is { Destroyed: false };
                n.Linked = n.Captured;
                n.Shielded = n.Captured;
            }
            else
            {
                n.Linked = false;
                n.Shielded = true;
            }
        }

        // 2. Flow power outward: a captured node adjacent (same team) to a powered captured node becomes powered.
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var n in Nodes)
            {
                foreach (var m in n.Neighbors)
                {
                    if (n.Captured && m.Captured && n.Team == m.Team && n.Linked && !m.Linked)
                    {
                        m.Linked = true;
                        changed = true;
                    }
                }
            }
        }

        // 3. Unshield nodes adjacent to a powered ENEMY node (those are attackable / capturable).
        foreach (var n in Nodes)
        {
            foreach (var m in n.Neighbors)
            {
                if (m.Linked && m.Captured && (n.Team != m.Team || !n.Captured))
                    n.Shielded = false;
            }
        }
    }

    /// <summary>
    /// QC ons_ControlPoint_Attackable / the unshielded flag: whether a node may currently be damaged/captured
    /// by <paramref name="byTeam"/> — it must be unshielded (an enemy-owned powered neighbor exposed it).
    /// </summary>
    public bool IsAttackable(OnsNode node, int byTeam)
        => !node.Shielded && node.Team != byTeam;

    /// <summary>
    /// QC control-point capture: a control point captured by <paramref name="team"/> (0 = neutralized). The
    /// CP must be attackable (unshielded) to flip; capturing it re-propagates power so it can in turn expose
    /// the next link toward the enemy generator.
    /// </summary>
    public void CaptureControlPoint(int controlPointId, int team) => CaptureControlPoint(controlPointId, team, null);

    /// <summary>
    /// QC control-point capture with the capturing player (ons_ControlPoint_Icon: GameRules_scoring_add(toucher,
    /// ONS_CAPS, 1) + SCORE 10). Overload of <see cref="CaptureControlPoint(int,int)"/> that also credits the
    /// scoreboard columns when a non-neutral capture by a real player succeeds. The port collapses the QC
    /// "take" (ONS_TAKES) and "fully captured" (ONS_CAPS) into this single flip, so the capturer is credited
    /// both columns with one SCORE +10 (avoids the double-credit the two separate QC events would otherwise add).
    /// </summary>
    public void CaptureControlPoint(int controlPointId, int team, Player? by)
    {
        OnsNode node = ControlPointNode(controlPointId) ?? AddControlPoint(controlPointId);
        // A CP can only be (re)captured when attackable by the capturing team (or when neutralizing).
        if (team != Teams.None && node.Captured && !IsAttackable(node, team))
            return;
        bool realCapture = team != Teams.None && node.Team != team;
        node.Team = team;
        node.Captured = team != Teams.None;
        UpdateLinks();

        if (realCapture && by is not null && (int)by.Team == team)
        {
            // QC: completing a BUILD credits only ONS_CAPS + team SCORE. ONS_TAKES is awarded for DESTROYING an
            // enemy control-point icon (ons_ControlPoint_Icon_Damage), not for a build-capture — so it is NOT added here.
            AddCol(by, "ONS_CAPS", 1);  // QC GameRules_scoring_add(toucher, ONS_CAPS, 1)
            by.ScoreFrags += 10;        // QC GameRules_scoring_add_team(toucher, SCORE, 10)
        }
    }

    /// <summary>QC <c>GameRules_scoring_add(player, SP_X, n)</c> for an ONS player column (no-op if unregistered).</summary>
    private static void AddCol(Player p, string field, int n)
    {
        Scoring.ScoreField? f = Scoring.GameScores.Field(field);
        if (f is not null) Scoring.GameScores.AddToPlayer(p, f, n);
    }

    /// <summary>The team that owns a control point (0 = neutral/unknown).</summary>
    public int ControlPointOwner(int controlPointId)
        => _cpNodes.TryGetValue(controlPointId, out var n) ? n.Team : Teams.None;

    /// <summary>
    /// Apply damage to a team's generator (QC ons_GeneratorDamage). Damage is ignored while the generator is
    /// shielded (no enemy controls a linked CP). When health reaches 0 it is destroyed, the graph is updated,
    /// and the round ends: the winner is any other team that still has a standing generator. Returns true if
    /// this damage destroyed the generator.
    /// </summary>
    public bool DamageGenerator(int team, float amount) => DamageGenerator(team, amount, null);

    /// <summary>
    /// QC ons_GeneratorDamage with the attacker (overload of <see cref="DamageGenerator(int,float)"/>): when the
    /// damage destroys the generator the attacker is awarded SCORE +100 (QC GameRules_scoring_add(attacker, SCORE,
    /// 100)). Returns true if this damage destroyed the generator.
    /// </summary>
    public bool DamageGenerator(int team, float amount, Player? by)
    {
        if (MatchEnded || amount <= 0f)
            return false;

        OnsNode? node = GeneratorNode(team);
        GeneratorState? gen = node?.Gen ?? GeneratorFor(team);
        if (gen is null || gen.Destroyed)
            return false;

        // QC: a shielded generator ignores damage entirely.
        if (node is not null && node.Shielded)
            return false;

        gen.Health -= amount;
        if (gen.Health < 0f)
            gen.Health = 0f;

        if (gen.Destroyed)
        {
            if (node is not null) { node.Captured = false; node.Linked = false; node.Shielded = false; }
            UpdateLinks();
            if (by is not null && (int)by.Team != team)
                by.ScoreFrags += 100; // QC GameRules_scoring_add(attacker, SCORE, 100): generator kill bonus
            EndRoundAfterGeneratorLoss(team);
            return true;
        }
        return false;
    }

    /// <summary>
    /// QC Onslaught_CheckWinner (Team_GetWinnerTeam_WithOwnedItems): the round is decided when at most one
    /// team still has a standing generator. Returns that team (&gt;0), -1 for a tie (none left), or 0 while
    /// two or more generators stand. A win banks ST_ONS_GENS +1 for the winner (<see cref="BankRoundWin"/>).
    /// </summary>
    public int CheckWinner()
    {
        int standing = 0, soleTeam = Teams.None;
        foreach (var kv in _generators)
            if (!kv.Value.Destroyed) { standing++; soleTeam = kv.Key; }
        if (_generators.Count < 2 || standing > 1)
            return 0; // round still live (or not enough generators set up yet)
        int winner = standing == 1 ? soleTeam : -1;
        if (winner > 0)
            BankRoundWin(winner); // QC TeamScore_AddToTeam(winner_team, ST_ONS_GENS, +1) (idempotent via MatchEnded)
        return winner;
    }

    /// <summary>The team <paramref name="losingTeam"/>'s generator fell — award the round to a surviving team
    /// (QC Onslaught round-over: TeamScore_AddToTeam(winner_team, ST_ONS_GENS, +1)).</summary>
    private void EndRoundAfterGeneratorLoss(int losingTeam)
    {
        int winner = Teams.None;
        foreach (KeyValuePair<int, GeneratorState> kv in _generators)
        {
            if (kv.Key != losingTeam && !kv.Value.Destroyed)
            {
                winner = kv.Key; // the (last) team with a standing generator wins
                break;
            }
        }
        BankRoundWin(winner);
    }

    /// <summary>
    /// QC the Onslaught round-over credit (sv_onslaught.qc): latch the round winner and bank ST_ONS_GENS +1 for
    /// it (the team slot-1 score = generators destroyed). Idempotent per round via the <see cref="MatchEnded"/>
    /// latch, so the two win paths — a generator destroyed in <see cref="DamageGenerator"/> and the round
    /// handler's <see cref="CheckWinner"/> — credit exactly once. A neutral winner just latches the end.
    /// </summary>
    private void BankRoundWin(int winner)
    {
        if (MatchEnded)
            return;
        MatchEnded = true;
        WinningTeam = winner;
        if (winner != Teams.None)
            GS.AddToTeam(winner, GS.TeamSlotSecondary, 1); // QC TeamScore_AddToTeam(winner_team, ST_ONS_GENS, +1)
    }

    /// <summary>Player kills don't decide Onslaught (the generators do); the handler just notifies the host.</summary>
    private bool OnDeath(ref DeathEvent ev)
    {
        if (ev.Victim is not Player victim)
            return false;
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

    private static float Cvar(string name, float fallback) => TryCvar(name, out float v) ? v : fallback;
}
