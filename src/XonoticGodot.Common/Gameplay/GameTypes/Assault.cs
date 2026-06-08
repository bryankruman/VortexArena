using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;
using GS = XonoticGodot.Common.Gameplay.Scoring.GameScores; // T7: alias the static score table for the per-mode ScoreRules

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Assault gametype — port of <c>CLASS(Assault, Gametype)</c>
/// (common/gametypes/gametype/assault/assault.qh + sv_assault.qc).
///
/// An asymmetric attack/defend mode. One team attacks a chain of objectives leading to a final power core,
/// the other defends. The attackers win the round by destroying the final objective (QC
/// <c>target_assault_roundend</c> fires <c>winning=1</c>); if the time limit elapses first, the defenders win
/// (QC <c>WinningCondition_Assault</c>: default to the defending team, overridden only when the round-end was
/// triggered). Assault is played as two rounds with the roles swapped — the team that destroyed the core
/// faster wins overall (QC <c>assault_new_round</c> sets round two's timelimit to round one's destruction
/// time).
///
/// Faithfully ported (the attack/defend win rule + objective chain):
///  - which team attacks vs defends (<see cref="AttackerTeam"/>, QC assault_attacker_team);
///  - the objective chain entities + target graph (<see cref="AddObjective"/>/<see cref="AddDecreaser"/>/
///    <see cref="AddDestructible"/>/<see cref="AddRoundEnd"/>, QC target_objective / target_objective_decrease
///    / func_assault_destructible / target_assault_roundend) where shooting the active destructible runs its
///    decreaser to whittle the objective's health, and destroying an objective activates the next
///    (<see cref="DamageDestructible"/> → <see cref="DecreaseObjective"/>, QC assault_objective_decrease_use);
///  - objective destruction → attackers win the round (<see cref="DestroyFinalObjective"/>, QC roundend.winning);
///  - the two-round role swap (<see cref="StartSecondRound"/>) keyed on the attackers' destruction time.
///
/// Deferred (NOTE — cross-boundary): turret team-swap, the engine round-restart machinery (ReadyRestart_force), the
/// objective/wall models + waypoint sprites (CSQC), and the score networking/HUD.
/// </summary>
[GameType]
public sealed class Assault : GameType
{
    /// <summary>QC ASSAULT_VALUE_INACTIVE: the health sentinel for an objective that isn't yet active.</summary>
    public const float ObjectiveInactive = 1000000f;

    /// <summary>
    /// Round/role state for a two-round Assault match (QC assault_attacker_team + the round counter and the
    /// attackers' best destruction time used to set round two's clock).
    /// </summary>
    public sealed class AssaultState
    {
        /// <summary>The team currently attacking (QC assault_attacker_team), defaults to <see cref="Teams.Red"/>.</summary>
        public int AttackerTeam = Teams.Red;

        /// <summary>Which round this is: 0 = first, 1 = second (roles swapped).</summary>
        public int Round;

        /// <summary>
        /// Seconds the first-round attackers took to destroy the core (QC time − game_starttime), or 0 if they
        /// never did. Round two's time limit is set from this so the second attackers must beat it.
        /// </summary>
        public float FirstRoundDestroyTime;
    }

    /// <summary>The single shared round/role state.</summary>
    public AssaultState State { get; } = new();

    private HookHandler<DeathEvent>? _deathHandler;

    /// <summary>Optional sink for the host/controller to react to a kill.</summary>
    public IMatchEvents? Events;

    /// <summary>QC checkrules end-of-round latch: true once the round (or the whole match) has been decided.</summary>
    public bool MatchEnded { get; private set; }

    /// <summary>The winning team color code once decided, or 0 if none yet.</summary>
    public int WinningTeam { get; private set; }

    public Assault()
    {
        NetName = "as";
        DisplayName = "Assault";
        TeamGame = true;
    }

    // ============================================================================================
    //  Objective-chain ENTITIES (QC target_objective / _decrease / func_assault_destructible / roundend)
    // ============================================================================================

    /// <summary>An objective in the attack chain (QC target_objective: .health, .target = next objective).</summary>
    public sealed class Objective
    {
        public string Name = "";          // QC .targetname
        public string Target = "";        // QC .target — fired (SUB_UseTargets) when destroyed (activates next)
        public float Health = ObjectiveInactive; // QC RES_HEALTH (ASSAULT_VALUE_INACTIVE until activated)
        public bool Active => Health < ObjectiveInactive && Health > 0f;
        public bool Destroyed => Health <= 0f && Health > -ObjectiveInactive; // QC sets -1 when destroyed
    }

    /// <summary>A damage applicator (QC target_objective_decrease: links a destructible to its objective, .dmg).</summary>
    public sealed class Decreaser
    {
        public string Name = "";          // QC .targetname — the name a func_assault_destructible's .target fires
        public string Target = "";        // QC .target — the objective targetname it decreases (.enemy)
        public Objective? ObjectiveRef;   // resolved objective (QC this.enemy)
        public float Dmg = 100f;          // QC .dmg — health removed per activation
        public bool Spent;                // QC: a decreaser fires once per round (assault_sprite consumed)
    }

    /// <summary>A destructible wall the attackers shoot (QC func_assault_destructible: .health, triggers a decreaser).</summary>
    public sealed class Destructible
    {
        public string Target = "";        // QC .target — the decreaser targetname it triggers when destroyed
        public Decreaser? DecreaserRef;   // resolved decreaser
        public float Health = 100f;       // QC RES_HEALTH
        public float MaxHealth = 100f;
        public bool Active;               // QC: only the chain's current destructible is shootable
        public bool Destroyed => Health <= 0f;
        /// <summary>The world edict the BSP lump spawned for this wall (QC the func_assault_destructible entity), or
        /// null in a pure-logic/headless test. The live damage path (when wired) maps a damaged edict back to its
        /// Destructible via <see cref="DestructibleFor"/> and drives <see cref="DamageDestructible"/>.</summary>
        public Framework.Entity? WorldEntity;
    }

    /// <summary>The objectives, decreasers, and destructibles on the map, keyed by name where needed.</summary>
    public readonly List<Objective> Objectives = new();
    public readonly List<Decreaser> Decreasers = new();
    public readonly List<Destructible> Destructibles = new();

    // Deferred spawn staging (QC INITPRIO_FINDTARGET): the BSP entity lump spawns objectives, decreasers and
    // destructibles in ARBITRARY order, so a decreaser/destructible may be spawned before the objective/decreaser
    // it links to (its .target names them). QC defers the .target→.enemy resolution to assault_setenemytoobjective
    // run at INITPRIO_FINDTARGET (after the whole lump). We mirror that: the map spawnfunc Stage*s the raw fields,
    // then ResolveObjectiveGraph() builds the real Decreaser/Destructible objects with their links resolved once
    // every objective/decreaser name is known. (The deterministic tests can still use the direct
    // AddObjective/AddDecreaser/AddDestructible API where the spawn order is controlled.)
    private readonly List<(string name, string target, float dmg)> _pendingDecreasers = new();
    private readonly List<(string decreaserName, float health, Framework.Entity? world)> _pendingDestructibles = new();

    /// <summary>QC target_assault_roundend present (the final objective's target). True once a round-end exists.</summary>
    public bool HasRoundEnd { get; private set; }

    public override void OnInit()
    {
        // QC: the objective chain is built from the map's target_objective / target_objective_decrease /
        // func_assault_destructible / target_assault_roundend entities (see AddObjective etc).
        Objectives.Clear();
        Decreasers.Clear();
        Destructibles.Clear();
        _pendingDecreasers.Clear();
        _pendingDestructibles.Clear();
        HasRoundEnd = false;
    }

    /// <summary>QC spawnfunc target_objective: add an objective (initially inactive).</summary>
    public Objective AddObjective(string name, string target = "")
    {
        var o = new Objective { Name = name, Target = target };
        Objectives.Add(o);
        return o;
    }

    /// <summary>QC spawnfunc target_objective_decrease: add a decreaser linked to objective <paramref name="objectiveName"/>.</summary>
    public Decreaser AddDecreaser(string objectiveName, float dmg = 100f, string name = "")
    {
        var d = new Decreaser { Name = name, Target = objectiveName, Dmg = dmg };
        d.ObjectiveRef = Objectives.Find(o => o.Name == objectiveName); // QC assault_setenemytoobjective
        Decreasers.Add(d);
        return d;
    }

    /// <summary>QC spawnfunc func_assault_destructible: add a shootable wall that triggers <paramref name="decreaser"/>.</summary>
    public Destructible AddDestructible(Decreaser decreaser, float health = 100f, bool active = false)
    {
        var w = new Destructible { DecreaserRef = decreaser, Health = health, MaxHealth = health, Active = active };
        Destructibles.Add(w);
        return w;
    }

    /// <summary>QC spawnfunc target_assault_roundend: marks the chain's terminal target.</summary>
    public void AddRoundEnd() => HasRoundEnd = true;

    // ============================================================================================
    //  Deferred objective-graph spawn (QC INITPRIO_FINDTARGET) — used by the BSP-lump spawnfuncs
    // ============================================================================================

    /// <summary>
    /// QC spawnfunc target_objective_decrease (the deferred half): stage a decreaser by its own
    /// <paramref name="name"/> (.targetname — what a func_assault_destructible's .target fires) and the
    /// <paramref name="objectiveName"/> it decreases (.target → .enemy). The real <see cref="Decreaser"/> is built
    /// in <see cref="ResolveObjectiveGraph"/> after the whole lump has spawned (arbitrary order). QC defaults the
    /// damage to 101 (<c>if(!this.dmg) this.dmg = 101;</c>), so pass <paramref name="dmg"/> 0 to take that default.
    /// </summary>
    public void StageDecreaser(string name, string objectiveName, float dmg)
        => _pendingDecreasers.Add((name, objectiveName, dmg > 0f ? dmg : 101f));

    /// <summary>
    /// QC spawnfunc func_assault_destructible (the deferred half): stage a destructible wall whose .target names
    /// the decreaser (<paramref name="decreaserName"/>) it triggers on destruction, with <paramref name="health"/>
    /// from the breakable setup (default 100), and an optional <paramref name="worldEntity"/> (the func_assault_-
    /// destructible edict the lump spawned — the bot role finds it by classname, and the live damage bridge maps it
    /// back to this Destructible). Linked to its decreaser in <see cref="ResolveObjectiveGraph"/>.
    /// </summary>
    public void StageDestructible(string decreaserName, float health, Framework.Entity? worldEntity = null)
        => _pendingDestructibles.Add((decreaserName, health > 0f ? health : 100f, worldEntity));

    /// <summary>
    /// Map a damaged <c>func_assault_destructible</c> world edict back to its <see cref="Destructible"/> (QC the
    /// edict IS the destructible; the port keeps a POJO chain alongside the world entity). The live damage hook
    /// (a func_breakable event_damage / death bridge) calls this then <see cref="DamageDestructible"/>. Null if the
    /// edict isn't a tracked destructible.
    /// </summary>
    public Destructible? DestructibleFor(Framework.Entity worldEntity)
        => Destructibles.Find(w => ReferenceEquals(w.WorldEntity, worldEntity));

    /// <summary>
    /// QC INITPRIO_FINDTARGET pass (assault_setenemytoobjective + the destructible→decreaser SUB_UseTargets link):
    /// resolve every staged decreaser to its objective by name, and every staged destructible to its decreaser by
    /// name, now that the whole BSP entity lump has spawned. Idempotent (clears the staging once resolved); a
    /// staged entity whose target can't be found is dropped (QC objerror "fix the map" — we skip rather than fault).
    /// Call once after <c>SpawnMapEntities</c> (the host's post-spawn hook).
    /// </summary>
    public void ResolveObjectiveGraph()
    {
        // 1) decreasers: .target → the objective edict (QC assault_setenemytoobjective).
        foreach (var (name, target, dmg) in _pendingDecreasers)
            AddDecreaser(target, dmg, name);
        _pendingDecreasers.Clear();

        // 2) destructibles: .target → the decreaser by its targetname (QC the func_breakable SUB_UseTargets chain).
        foreach (var (decreaserName, health, world) in _pendingDestructibles)
        {
            Decreaser? dec = Decreasers.Find(d => d.Name == decreaserName);
            if (dec is not null)
            {
                Destructible w = AddDestructible(dec, health);
                w.Target = decreaserName;  // keep QC .target for inspection/round reset
                w.WorldEntity = world;     // the func_assault_destructible edict (bot role + live damage bridge)
            }
            // else: dangling .target (bad map) — QC objerrors; we skip so the rest of the chain still works.
        }
        _pendingDestructibles.Clear();

        // QC target_assault_roundstart (assault_roundstart_use_this, INITPRIO_FINDTARGET): once the graph is built,
        // arm the chain so the attackers have a live first objective to assault at round start.
        ResetObjectives();
    }

    /// <summary>
    /// QC assault_objective_use: activate an objective (set its health to <paramref name="health"/>, default
    /// 100) so its decreasers can whittle it. Called when the previous objective is destroyed (SUB_UseTargets).
    /// </summary>
    public void ActivateObjective(Objective o, float health = 100f)
    {
        o.Health = health;
        // QC target_objective_decrease_activate: re-arm the decreasers + destructibles aimed at this objective.
        foreach (var d in Decreasers)
            if (ReferenceEquals(d.ObjectiveRef, o))
            {
                d.Spent = false;
                foreach (var w in Destructibles)
                    if (ReferenceEquals(w.DecreaserRef, d))
                        w.Active = true;
            }
    }

    /// <summary>
    /// QC func_assault_destructible event_damage: an attacker damages the active destructible wall. When the
    /// wall's health reaches 0 it triggers its decreaser, whittling the linked objective (<see cref="DecreaseObjective"/>).
    /// Only the attacking team may damage it. Returns true if the wall was destroyed this call.
    /// </summary>
    public bool DamageDestructible(Destructible wall, int byTeam, float amount)
        => DamageDestructible(wall, byTeam, amount, null);

    /// <summary>
    /// QC func_assault_destructible event_damage with the attacker (overload of
    /// <see cref="DamageDestructible(Destructible,int,float)"/>): when the wall is destroyed the linked objective
    /// is whittled and <paramref name="actor"/> is credited the SCORE / ASSAULT_OBJECTIVES the chain awards
    /// (via <see cref="DecreaseObjective(Decreaser,int,Player?)"/>).
    /// </summary>
    public bool DamageDestructible(Destructible wall, int byTeam, float amount, Player? actor)
    {
        if (MatchEnded || !wall.Active || wall.Destroyed || amount <= 0f)
            return false;
        if (byTeam != State.AttackerTeam)
            return false; // QC: wrong team can't decrease objectives

        wall.Health -= amount;
        if (wall.Health > 0f)
            return false;

        wall.Health = 0f;
        wall.Active = false;
        if (wall.DecreaserRef is { } dec)
            DecreaseObjective(dec, byTeam, actor);
        return true;
    }

    /// <summary>
    /// QC assault_objective_decrease_use: a decreaser fires (once) and removes its <see cref="Decreaser.Dmg"/>
    /// from its objective's health. When that drops the objective to/below 0, the objective is destroyed —
    /// activate the next objective in the chain (its <see cref="Objective.Target"/>), or, if the chain's
    /// terminal target is the round-end, the attackers win the round (<see cref="DestroyFinalObjective"/>).
    /// </summary>
    public void DecreaseObjective(Decreaser dec, int byTeam) => DecreaseObjective(dec, byTeam, null);

    /// <summary>
    /// QC assault_objective_decrease_use with the attacker (overload of
    /// <see cref="DecreaseObjective(Decreaser,int)"/>): credits <paramref name="actor"/> the QC scoreboard awards —
    /// SCORE += the health removed (GameRules_scoring_add_team(actor, SCORE, dmg/hlth)) and, when the objective is
    /// destroyed, ASSAULT_OBJECTIVES +1 (GameRules_scoring_add_team(actor, ASSAULT_OBJECTIVES, 1)).
    /// </summary>
    public void DecreaseObjective(Decreaser dec, int byTeam, Player? actor)
    {
        if (MatchEnded || dec.Spent || byTeam != State.AttackerTeam)
            return;
        Objective? o = dec.ObjectiveRef;
        if (o is null || o.Health >= ObjectiveInactive)
            return; // not active

        dec.Spent = true; // QC: a decreaser can't activate again this round
        float removed = System.Math.Min(o.Health, dec.Dmg); // the health this decreaser actually strips
        o.Health = DamageObjective(o.Health, dec.Dmg);

        if (actor is not null)
            actor.ScoreFrags += (int)removed; // QC GameRules_scoring_add_team(actor, SCORE, dmg|hlth)

        if (o.Health > 0f && o.Health < ObjectiveInactive)
            return; // objective damaged but still standing

        // QC: the objective is destroyed → the actor banks ASSAULT_OBJECTIVES +1.
        if (actor is not null)
        {
            Scoring.ScoreField? f = Scoring.GameScores.Field("ASSAULT_OBJECTIVES");
            if (f is not null) Scoring.GameScores.AddToPlayer(actor, f, 1);
        }

        // Objective destroyed (QC SetResourceExplicit(enemy, -1)). Fire its target to activate the next one.
        Objective? next = Objectives.Find(n => !ReferenceEquals(n, o) && n.Name == o.Target);
        if (next is not null)
        {
            ActivateObjective(next);
            return;
        }

        // No next objective: the chain's terminal target is the round-end → attackers win the round.
        if (HasRoundEnd || string.IsNullOrEmpty(o.Target))
        {
            float elapsed = Api.Services is not null ? Api.Clock.Time : 0f; // host supplies game_starttime offset
            DestroyFinalObjective(elapsed, State.Round >= 1);
        }
    }

    /// <summary>The team currently attacking (QC assault_attacker_team).</summary>
    public int AttackerTeam => State.AttackerTeam;

    /// <summary>The team currently defending (the other of the two Assault teams).</summary>
    public int DefenderTeam => State.AttackerTeam == Teams.Red ? Teams.Blue : Teams.Red;

    /// <summary>The team's objectives score (QC teamscores(team, ST_ASSAULT_OBJECTIVES) — SET to the 666 sentinel
    /// for the team that destroyed the final objective). Read through the unified GameScores two-slot team store.</summary>
    public int GetTeamObjectives(int team) => GS.TeamScore(team, GS.TeamSlotSecondary);

    public void Activate()
    {
        if (_deathHandler is not null)
            return;
        MatchEnded = false;
        WinningTeam = 0;
        State.Round = 0;
        State.AttackerTeam = Teams.Red; // QC: NUM_TEAM_1 attacks first (swapped every round)
        State.FirstRoundDestroyTime = 0f;
        GS.ResetTeams(); // QC Score_ClearAll at match start: zero both team slots before declaring

        // QC sv_assault.qh GameRules_scoring(teamplay_bitmask, SFL_SORT_PRIO_SECONDARY, SFL_SORT_PRIO_SECONDARY, {
        // field_team(ST_ASSAULT_OBJECTIVES, "objectives", PRIMARY); field(SP_ASSAULT_OBJECTIVES, "objectives",
        // PRIMARY); }): the player primary is SP_ASSAULT_OBJECTIVES with SP_SCORE secondary (sprio = SECONDARY);
        // ST_SCORE (team slot 0) is the team secondary (stprio = SECONDARY); ST_ASSAULT_OBJECTIVES (team slot 1)
        // is the TEAM primary. Players rank by objectives destroyed, then score.
        GS.ScoreRulesBasics(teams: true);
        GS.TeamRulesBasics(scorePrio: Scoring.ScoreFlags.SortPrioSecondary); // ST_SCORE (slot 0) stprio = SECONDARY
        GS.SetTeamLabel(GS.TeamSlotSecondary, "objectives", Scoring.ScoreFlags.SortPrioPrimary); // ST_ASSAULT_OBJECTIVES (slot 1)
        GS.DeclareColumn("ASSAULT_OBJECTIVES", Scoring.ScoreFlags.None, "objectives");
        GS.SetSortKeys(GS.Field("ASSAULT_OBJECTIVES")!, GS.Score);
        GS.SeedTeams(2); // Assault is always red vs blue (teamplay_bitmask = BITS(2))

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
    /// QC target_objective_decrease_use: an attacker damaged an active objective. Subtract <paramref name="amount"/>
    /// from its health and return the remaining health. When it would drop to (or below) ~0 the objective is
    /// considered destroyed — the caller then advances to the next objective or, for the final one, calls
    /// <see cref="DestroyFinalObjective"/>. Only the attacking team may decrease objectives (QC actor.team check).
    /// </summary>
    public float DamageObjective(float currentHealth, float amount)
    {
        if (currentHealth >= ObjectiveInactive)
            return currentHealth; // not active yet
        float h = currentHealth - amount;
        return h < 0f ? -1f : h; // QC sets health to -1 when destroyed
    }

    /// <summary>
    /// QC target_assault_roundend_use: the attackers destroyed the final objective and win the round. In round
    /// one this records their destruction time and (in a full match) hands off to a second round with swapped
    /// roles; in round two (or campaign) it ends the match. The host passes the elapsed attack time so round
    /// two's clock can be set from it.
    /// </summary>
    public void DestroyFinalObjective(float elapsedAttackSeconds, bool isFinalRound)
    {
        if (MatchEnded)
            return;

        // The attacking team won this round.
        WinningTeam = State.AttackerTeam;
        // QC target_assault_roundend_use: the round-winning attackers' team score is SET to the 666 sentinel
        // (TeamScore_AddToTeam(attacker, ST_ASSAULT_OBJECTIVES, 666 - current)) so they top the team scoreboard.
        GS.SetTeamScore(State.AttackerTeam, GS.TeamSlotSecondary, 666);

        if (isFinalRound || State.Round >= 1)
        {
            MatchEnded = true; // QC: second round (or campaign single round) ends the match
            return;
        }

        // First round won by attackers: remember their time; a second round will swap roles.
        State.FirstRoundDestroyTime = elapsedAttackSeconds;
        // Round is not yet over for the match — the host should call StartSecondRound to flip sides.
    }

    /// <summary>
    /// QC assault_new_round: begin the second round with the attack/defend roles swapped. Round two's time
    /// limit should be set by the host to <see cref="AssaultState.FirstRoundDestroyTime"/> so the new
    /// attackers must beat the first team's destruction time. Resets the per-round win latch.
    /// </summary>
    public void StartSecondRound()
    {
        State.Round = 1;
        State.AttackerTeam = DefenderTeam; // swap (QC NUM_TEAM_1 <-> NUM_TEAM_2)
        MatchEnded = false;
        WinningTeam = 0;
        ResetObjectives(); // QC assault_objective_reset on each new round
    }

    /// <summary>
    /// QC assault_objective_reset (per round start): reset every objective to inactive, un-spend the
    /// decreasers, and reset the destructible walls — then activate the first objective in the chain (the one
    /// no other objective targets) so the attackers have something to assault.
    /// </summary>
    public void ResetObjectives()
    {
        foreach (var o in Objectives)
            o.Health = ObjectiveInactive;
        foreach (var d in Decreasers)
            d.Spent = false;
        foreach (var w in Destructibles)
        {
            w.Health = w.MaxHealth;
            w.Active = false;
        }
        // The first objective is the one that nothing else targets (QC the roundstart-activated head).
        Objective? first = Objectives.Find(o => !Objectives.Exists(p => p.Target == o.Name));
        if (first is not null)
            ActivateObjective(first);
    }

    /// <summary>
    /// QC WinningCondition_Assault (the defender branch): the round's time limit elapsed without the attackers
    /// destroying the core, so the defenders win. In the final round this ends the match; otherwise the host
    /// then swaps roles for round two.
    /// </summary>
    public void TimeLimitReached(bool isFinalRound)
    {
        if (MatchEnded)
            return;
        WinningTeam = DefenderTeam; // QC: assume the defending team wins if the timelimit passes
        if (isFinalRound || State.Round >= 1)
            MatchEnded = true;
    }

    /// <summary>Player kills don't decide Assault (the objectives do); the handler just notifies the host.</summary>
    private bool OnDeath(ref DeathEvent ev)
    {
        if (ev.Victim is not Player victim)
            return false;
        Events?.OnFrag(ev.Attacker as Player, victim, ev.DeathType);
        return false;
    }
}
