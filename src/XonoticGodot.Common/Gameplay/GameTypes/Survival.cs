using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Gameplay.Scoring;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Survival gametype — port of <c>CLASS(Survival, Gametype)</c>
/// (common/gametypes/gametype/survival/survival.qh + sv_survival.qc).
///
/// A round-based hidden-role mode (built on the LMS family): at round start a fraction of the players
/// (cvar <c>g_survival_hunter_count</c>, default 0.25, ≥1 and &lt; total) are secretly assigned the
/// <see cref="SurvStatus.Hunter"/> role; everyone else is <see cref="SurvStatus.Prey"/>. Roles are concealed
/// (frags/obituaries are anonymized so hunters aren't outed). A death eliminates the player for the round
/// (no respawn). The round ends when one side is entirely gone (QC <c>WinningCondition</c>): if any hunter is
/// still alive the hunters win, otherwise the surviving prey win. Kills bank no points mid-round — score is
/// awarded to the winning side when the round ends.
///
/// Faithfully ported (the role/win rule):
///  - per-player role + alive state (<see cref="SurvState"/>) and the hunter-count assignment
///    (<see cref="AssignRoles"/>, QC g_survival_hunter_count);
///  - Combat.Death → eliminate the victim for the round (no respawn) and re-check the side-wipe condition;
///  - round-over win latch: hunters win if ≥1 hunter alive, else prey win (QC survivor/hunter counts).
///
/// Faithfully ported (objective layer):
///  - the round handler + warmup (<see cref="Handler"/>, QC round_handler_Spawn with survival_CheckTeams /
///    survival_CheckWinner / survival_RoundStart), driven by <see cref="Tick"/>: a new round assigns roles
///    (<see cref="AssignRoles"/>) and resolves on a side wipe (<see cref="CheckWinningCondition"/>).
///
/// Faithfully ported (scoring):
///  - banked validkills (QC GiveFragsForKill / survival_validkills): a kill during an active round banks its
///    frag value rather than scoring immediately (<see cref="SurvState.ValidKills"/>); at round end the banked
///    kills are awarded as SP_SCORE plus the survival/hunt bonuses (<see cref="UpdateScores"/>, QC Surv_UpdateScores);
///  - role anonymization (QC the AddPlayerScore hook): SP_KILLS/DEATHS/SUICIDES/DMG/DMGTAKEN are suppressed
///    while a survival round runs so the scoreboard doesn't out the hidden hunters.
///
/// Deferred (NOTE — cross-boundary): the obituary color anonymization (client), forced spectate of eliminated
/// players, and the score networking/HUD — client / host concerns.
/// </summary>
[GameType]
public sealed class Survival : GameType
{
    // ----- hunter-count cvar + default (QC g_survival_hunter_count = 0.25) -----
    private const string CvarHunterCount = "g_survival_hunter_count";
    private const float  DefaultHunterCount = 0.25f;

    /// <summary>QC <c>survival_status</c> (SURV_STATUS_*): which side a player is on for the round.</summary>
    public enum SurvStatus
    {
        None = 0,
        /// <summary>SURV_STATUS_PREY: a survivor who must outlast the hunters.</summary>
        Prey = 1,
        /// <summary>SURV_STATUS_HUNTER: secretly tasked with eliminating the prey.</summary>
        Hunter = 2,
    }

    /// <summary>Per-player survival role + alive flag (QC survival_status + IS_DEAD).</summary>
    public sealed class SurvState
    {
        public SurvStatus Status;
        /// <summary>True while the player is still in the round (set false on death — no respawn in Survival).</summary>
        public bool Alive = true;
        /// <summary>QC <c>.survival_validkills</c>: frags banked this round, awarded to the side at round end.</summary>
        public int ValidKills;
    }

    private readonly Dictionary<Player, SurvState> _states = new();

    private HookHandler<DeathEvent>? _deathHandler;

    /// <summary>Optional sink for the host/controller to react to an elimination.</summary>
    public IMatchEvents? Events;

    /// <summary>QC checkrules end-of-round latch: true once one side has been entirely eliminated.</summary>
    public bool RoundOver { get; private set; }

    /// <summary>The side that won the round once <see cref="RoundOver"/> is set (None until then).</summary>
    public SurvStatus WinningSide { get; private set; }

    public Survival()
    {
        NetName = "surv";
        DisplayName = "Survival";
        TeamGame = false; // QC: GAMETYPE_FLAG_USEPOINTS only (roles are hidden, not visible teams)
    }

    /// <summary>The round-phase driver (QC round_handler) — created on <see cref="Activate"/>.</summary>
    public RoundHandler? Handler { get; private set; }

    /// <summary>The roster the round handler evaluates (set by the host before <see cref="Tick"/>).</summary>
    private IReadOnlyList<Player> _roster = System.Array.Empty<Player>();

    /// <summary>Provide the controller with the current roster (call before <see cref="Tick"/>).</summary>
    public void SetRoster(IReadOnlyList<Player> roster) => _roster = roster;

    public override void OnInit()
    {
        // QC: round_handler_Spawn drives the round (survival_RoundStart assigns roles, survival_CheckWinner
        // resolves the side wipe). The handler is created in Activate (it needs the roster).
    }

    public SurvState GetState(Player p)
    {
        if (!_states.TryGetValue(p, out SurvState? st))
        {
            st = new SurvState();
            _states[p] = st;
        }
        return st;
    }

    public void AddPlayer(Player p) => GetState(p);
    public void RemovePlayer(Player p) => _states.Remove(p);

    public void Activate()
    {
        if (_deathHandler is not null)
            return;
        RoundOver = false;
        WinningSide = SurvStatus.None;

        // QC round_handler_Spawn(survival_CheckTeams, survival_CheckWinner, survival_RoundStart).
        Handler = new RoundHandler(() => Api.Services is not null ? Api.Clock.Time : 0f)
        {
            CanRoundStart = () => CountActive() >= 2, // need at least 2 players for both sides
            CanRoundEnd = () => { CheckWinningCondition(); return RoundOver; },
            OnRoundStart = () => AssignRoles(_roster), // QC survival_RoundStart secretly assigns hunters
        };
        float warmup = TryCvar("g_survival_warmup", out float w) ? w : 5f;
        float rtl = TryCvar("g_survival_round_timelimit", out float t) ? t : 180f;
        float edly = TryCvar("g_survival_round_enddelay", out float e) ? e : 5f;
        Handler.Init(edly, warmup, rtl);

        // QC surv_Initialize GameRules_scoring(0, SFL_SORT_PRIO_PRIMARY, 0, { field(SP_SURV_SURVIVALS, "survivals",
        // 0); field(SP_SURV_HUNTS, "hunts", SFL_SORT_PRIO_SECONDARY); }): SP_SCORE is the player primary (banked
        // validkills awarded at round end), SP_SURV_HUNTS the secondary, plus the SURV_SURVIVALS stat column.
        GameScores.ScoreRulesBasics(teams: false);
        GameScores.DeclareColumn("SURV_SURVIVALS", ScoreFlags.None, "survivals"); // QC field(SP_SURV_SURVIVALS, "survivals", 0)
        GameScores.DeclareColumn("SURV_HUNTS", ScoreFlags.None, "hunts");         // QC field(SP_SURV_HUNTS, "hunts", SECONDARY)
        GameScores.SetSortKeys(GameScores.Score, GameScores.Field("SURV_HUNTS"));

        // QC the survival AddPlayerScore hook: anonymize kills/deaths so the scoreboard can't out the hunters.
        GameScores.AddPlayerScoreHook = AnonymizeScore;

        _deathHandler = OnDeath;
        Combat.Death.Add(_deathHandler);
    }

    /// <summary>
    /// Advance the Survival round handler one frame (QC round_handler_Think). Assigns roles at round start and
    /// resolves the round on a side wipe. Call each tick after <see cref="SetRoster"/>.
    /// </summary>
    public void Tick() => Handler?.Tick();

    /// <summary>Number of tracked players currently in the round (alive or not), for the start gate.</summary>
    private int CountActive()
    {
        int n = 0;
        for (int i = 0; i < _roster.Count; i++)
            if ((int)_roster[i].Team != Teams.None || _states.ContainsKey(_roster[i]))
                n++;
        return n == 0 ? _roster.Count : n;
    }

    public void Deactivate()
    {
        if (_deathHandler is null)
            return;
        Combat.Death.Remove(_deathHandler);
        _deathHandler = null;
        if (GameScores.AddPlayerScoreHook == AnonymizeScore)
            GameScores.AddPlayerScoreHook = null; // stop anonymizing once survival ends
    }

    /// <summary>The hunter role of a player (None if untracked).</summary>
    public SurvStatus StatusOf(Player p) => _states.TryGetValue(p, out SurvState? st) ? st.Status : SurvStatus.None;

    /// <summary>
    /// QC <c>Surv_RoundStart</c> (sv_survival.qc:160-193) role assignment. The hunter count is derived from the
    /// LIVE player count (FOREACH_CLIENT(IS_PLAYER &amp;&amp; !IS_DEAD) — observers and dead players don't count);
    /// the count comes from <c>g_survival_hunter_count</c> (≥1 = absolute, &lt;1 = a fraction of the live total)
    /// bounded to at least 1 and at most (live − 1). The hunter subset is a RANDOM selection over the live
    /// players (QC FOREACH_CLIENT_RANDOM — an inside-out Fisher–Yates shuffle via the seeded
    /// <see cref="XonoticGodot.Common.Math.Prandom"/> facade), NOT the first roster slots. Everyone starts as
    /// prey and alive; the chosen subset becomes hunters. Also clears any prior round result.
    /// </summary>
    public void AssignRoles(IReadOnlyList<Player> roster)
    {
        RoundOver = false;
        WinningSide = SurvStatus.None;

        // QC FOREACH_CLIENT: everyone resets to prey (a non-live client gets survival_status = 0, but the port's
        // roster is the in-round set; untracked/observer/dead players keep Prey and don't count for the split).
        foreach (Player p in roster)
        {
            SurvState st = GetState(p);
            st.Status = SurvStatus.Prey;
            st.Alive = true;
        }

        // QC: playercount only counts live players (IS_PLAYER && !IS_DEAD). Build that live subset.
        var live = new List<Player>(roster.Count);
        foreach (Player p in roster)
            if (IsLive(p))
                live.Add(p);

        int playercount = live.Count;
        if (playercount < 2)
            return; // need at least 2 live players to have both sides

        int hunters = HunterCount(playercount);

        // QC FOREACH_CLIENT_RANDOM(IS_PLAYER && !IS_DEAD): inside-out Knuth–Fisher–Yates shuffle of the live
        // players (server/utils.qh:54-79), then take the first hunter_count from the shuffled order. Faithful to
        // QC's floor(random() * (cnt + 1)) using the seeded deterministic Prandom (ADR-0010).
        var shuffled = new Player[playercount];
        for (int cnt = 0; cnt < playercount; cnt++)
        {
            int j = (int)System.MathF.Floor(XonoticGodot.Common.Math.Prandom.Float() * (cnt + 1));
            if (j > cnt) j = cnt; // guard: random() can theoretically reach the open bound after float rounding
            if (j != cnt)
                shuffled[cnt] = shuffled[j];
            shuffled[j] = live[cnt];
        }
        for (int i = 0; i < hunters && i < playercount; i++)
            GetState(shuffled[i]).Status = SurvStatus.Hunter;
    }

    /// <summary>QC IS_PLAYER(it) &amp;&amp; !IS_DEAD(it): a live, non-observer player counts for the role split.</summary>
    private static bool IsLive(Player p) => !p.IsObserver && !p.IsDead;

    /// <summary>
    /// QC hunter-count formula (sv_survival.qc:175): bound(1, (cvar≥1 ? cvar : floor(playercount*cvar)),
    /// playercount−1) — over the LIVE player count.
    /// </summary>
    public int HunterCount(int playercount)
    {
        float c = TryCvar(CvarHunterCount, out float v) ? v : DefaultHunterCount;
        int count = c >= 1f ? (int)c : (int)System.MathF.Floor(playercount * c);
        int max = playercount - 1;
        if (count < 1) count = 1;
        if (count > max) count = max;
        return count;
    }

    /// <summary>Number of prey still alive this round.</summary>
    public int AlivePrey() => CountAlive(SurvStatus.Prey);

    /// <summary>Number of hunters still alive this round.</summary>
    public int AliveHunters() => CountAlive(SurvStatus.Hunter);

    private int CountAlive(SurvStatus side)
    {
        int n = 0;
        foreach (SurvState st in _states.Values)
            if (st.Alive && st.Status == side) n++;
        return n;
    }

    /// <summary>
    /// The obituary handler — eliminate the victim for the round (no respawn in Survival, QC forces
    /// spectate). Kills bank no points here (QC GiveFragsForKill zeroes the score and stashes it for the
    /// round winner). After the elimination, re-check the side-wipe win condition.
    /// </summary>
    private bool OnDeath(ref DeathEvent ev)
    {
        if (ev.Victim is not Player victim)
            return false;
        if (RoundOver)
            return false;

        // QC GiveFragsForKill: bank the kill into the attacker's validkills instead of scoring it now (the score
        // goes to the winning side at round end). A valid kill is by a live, distinct attacker.
        if (ev.Attacker is Player attacker && !ReferenceEquals(attacker, victim) && GetState(attacker).Alive)
            GetState(attacker).ValidKills += 1;

        GetState(victim).Alive = false; // eliminated for the round
        Events?.OnFrag(ev.Attacker as Player, victim, ev.DeathType);

        CheckWinningCondition();
        return false;
    }

    /// <summary>
    /// QC Survival WinningCondition (core): while both sides still have a living member the round continues.
    /// Once one side is wiped, the round is over — hunters win if any hunter is still alive, otherwise the
    /// surviving prey win. The round handler's warmup/end-delay timing is deferred.
    /// </summary>
    public void CheckWinningCondition()
    {
        if (_states.Count == 0)
            return;

        int prey = AlivePrey();
        int hunters = AliveHunters();

        if (prey > 0 && hunters > 0)
            return; // both sides alive — round continues

        if (RoundOver)
            return; // already resolved (avoid double-awarding the round score)

        RoundOver = true;
        WinningSide = hunters > 0 ? SurvStatus.Hunter : SurvStatus.Prey; // QC: hunters win if any remain
        UpdateScores(timedOut: false); // a side wipe ends the round → award the banked scores
    }

    /// <summary>
    /// QC <c>Surv_UpdateScores</c>: with the round over, award every player their banked validkills as SP_SCORE,
    /// then the per-side bonuses — surviving prey get SURV_SURVIVALS (+ a SCORE point if the round timed out and
    /// g_survival_reward_survival is set), surviving hunters get SURV_HUNTS. Clears the banked kills. Idempotent
    /// per round (CheckWinningCondition guards re-entry).
    /// </summary>
    public void UpdateScores(bool timedOut)
    {
        ScoreField? survivals = GameScores.Field("SURV_SURVIVALS");
        ScoreField? hunts = GameScores.Field("SURV_HUNTS");
        bool rewardSurvival = !TryCvar("g_survival_reward_survival", out float rs) || rs != 0f;

        foreach (var (player, st) in _states)
        {
            if (st.ValidKills != 0)
            {
                GameScores.AddToPlayer(player, GameScores.Score, st.ValidKills); // banked frags → SP_SCORE
                st.ValidKills = 0;
            }
            if (!st.Alive)
                continue; // bonuses are only for players who survived the round

            if (st.Status == SurvStatus.Prey)
            {
                if (timedOut && rewardSurvival)
                    GameScores.AddToPlayer(player, GameScores.Score, 1); // reward outlasting the round timer
                if (survivals is not null) GameScores.AddToPlayer(player, survivals, 1);
            }
            else if (st.Status == SurvStatus.Hunter)
            {
                if (hunts is not null) GameScores.AddToPlayer(player, hunts, 1);
            }
        }
    }

    // ============================================================================================
    //  HUD/wire disclosure surface (QC SurvivalStatuses_SendEntity + surv_isEliminated)
    // ============================================================================================

    /// <summary>
    /// True while the round's hidden roles are live (assigned by <see cref="AssignRoles"/> when
    /// <see cref="Handler"/> starts the round). Gates the networked own-role stat: pre-round the server
    /// sends <see cref="SurvStatus.None"/>, standing in for QC's <c>STAT(GAMESTARTTIME)/STAT(ROUNDSTARTTIME)
    /// &gt; time</c> HUD hide (cl_survival.qc:60).
    /// </summary>
    public bool RoleAssigned => Handler?.IsRoundStarted ?? false;

    /// <summary>QC the SurvivalStatuses hunter bit (sv_survival.qc:31-37): <c>INGAME(e) &amp;&amp;
    /// e.survival_status == SURV_STATUS_HUNTER</c>. A DEAD hunter is still a hunter — the bitfield carries the
    /// role, not aliveness (the round-end scoreboard outs every hunter, dead or not).</summary>
    public bool IsHunter(Player p) => StatusOf(p) == SurvStatus.Hunter;

    /// <summary>
    /// QC <c>surv_isEliminated</c> (sv_survival.qc:228-235; same shape as ca_isEliminated) approximated like
    /// CA's: the port lacks INGAME_JOINING / FRAGS_PLAYER_OUT_OF_GAME, so eliminated = tracked-and-not-alive
    /// (<see cref="SurvState.Alive"/> goes false on death — no respawn in Survival — and resets at the next
    /// <see cref="AssignRoles"/>, matching QC where the dead stay out until the round resets).
    /// </summary>
    public bool IsEliminatedPlayer(Player p) => _states.TryGetValue(p, out SurvState? st) && !st.Alive;

    /// <summary>
    /// QC SurvivalStatuses_SendEntity's visibility rule (sv_survival.qc:19-20 + Surv_CheckWinner sendflags at
    /// lines 93/151): a HUNTER destination always receives the hunter set ("hunters know all hunters"); everyone
    /// else only receives it once the round is over (so the round-end scoreboard can out the hunters). The
    /// anti-cheat invariant: hunter identities must NEVER reach a prey/observer recipient mid-round.
    /// </summary>
    public bool DisclosesHuntersTo(Player viewer) => RoundOver || StatusOf(viewer) == SurvStatus.Hunter;

    /// <summary>QC the timed-out round end (Surv_CheckWinner timeout branch): survivors win, award scores.</summary>
    public void EndRoundTimedOut()
    {
        if (RoundOver) return;
        RoundOver = true;
        WinningSide = SurvStatus.Prey; // QC: if the match times out, survivors win
        UpdateScores(timedOut: true);
    }

    /// <summary>
    /// QC the survival AddPlayerScore hook: while a survival round is active, suppress the kills/deaths/suicides/
    /// damage columns so the scoreboard can't out the hidden hunters. Installed on <see cref="Activate"/> into
    /// the shared <see cref="GameScores.AddPlayerScoreHook"/>, removed on <see cref="Deactivate"/>.
    /// </summary>
    private static (bool allow, int delta, bool claimed) AnonymizeScore(Entity _, ScoreField field, int delta)
    {
        // QC MUTATOR_HOOKFUNCTION(surv, AddPlayerScore) only rewrites M_ARGV(1) and falls through (returns
        // false), so claimed=false: the hook never claims the write, leaving the game_stopped clamp intact.
        switch (field.Name)
        {
            case "KILLS": case "DEATHS": case "SUICIDES": case "DMG": case "DMGTAKEN":
                return (true, 0, false); // allow the call, but contribute nothing (anonymized)
            default:
                return (true, delta, false);
        }
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
