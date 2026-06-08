using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;
using GS = XonoticGodot.Common.Gameplay.Scoring.GameScores; // T7: alias the static score table for the per-mode ScoreRules

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Last Man Standing (LMS) gametype — port of <c>CLASS(LastManStanding, Gametype)</c>
/// (common/gametypes/gametype/lms/lms.qh + sv_lms.qc).
///
/// Each player starts with a fixed pool of lives (cvar <c>lives</c>, default 5; legacy default 9). A death
/// costs the *victim* one life (QC MUTATOR_HOOKFUNCTION(lms, GiveFragsForKill): the target loses a life and
/// the frag score itself is zeroed — kills don't score points in LMS, lives are the currency). When a
/// player's lives hit 0 they are out of the game (QC <c>frags = FRAGS_PLAYER_OUT_OF_GAME</c>) and assigned a
/// finishing rank. The match ends when at most one player still has lives — that last player standing wins
/// (QC <c>WinningCondition_LMS</c>).
///
/// Faithfully ported (the win/lives rule):
///  - per-player lives state (<see cref="LmsState"/>), seeded to <see cref="StartingLives"/> on join;
///  - Combat.Death → victim loses a life (QC GiveFragsForKill), eliminated at 0 lives with a finishing rank;
///  - kills award no frag score (lives are the scoring currency, QC zeroes the frag score);
///  - last-player-standing win latch (QC WinningCondition_LMS: ≤1 player with lives ⇒ winner).
///
/// Faithfully ported (cont.):
///  - join-anytime gating (<see cref="NewPlayerLives"/>, QC LMS_NewPlayerLives / lms_lowest_lives): a late
///    joiner gets the current lowest life count (so they aren't at an advantage), and once the leader has
///    been out long enough nobody else may join;
///  - the dynamic respawn delay (<see cref="RespawnDelayFor"/>, QC the g_lms_dynamic_respawn_delay scaling).
///
/// Faithfully ported (cont. 2):
///  - extra-life pickups (QC ITEM_ExtraLife / lms_replace_with_extralife): an extra-life item grants
///    <see cref="ExtraLives"/> lives on touch (<see cref="GiveExtraLife"/> / <see cref="SpawnExtraLife"/>);
///  - leader computation (QC lms_UpdateLeaders): the max-lives players become leaders when their lead is large
///    enough and they're a small enough fraction of the field (<see cref="UpdateLeaders"/>); the waypoint
///    sprite that marks them is the only client-side remainder.
///
/// Deferred (NOTE — cross-boundary): the leader WAYPOINT SPRITE rendering, the dynamic-vampire damage modifier
/// (Damage_Calculate), and the tie/time-limit overtime path — client / damage-pipeline concerns.
/// </summary>
[GameType]
public sealed class LastManStanding : GameType
{
    // ----- lives cvars + default (gametype default lives=5; m_legacydefaults "9 20 0") -----
    private const string CvarLives        = "g_lms_lives";          // primary lives cvar
    private const string CvarLivesDefault = "g_lms_lives_override"; // menu override (m_configuremenu)
    private const string CvarFragLimit    = "fraglimit";            // QC LMS_NewPlayerLives caps lives at fraglimit
    private const int    DefaultLives     = 5;                      // gametype_init "lives=5"

    // ----- extra-life + leader cvars (QC g_lms_extra_lives / g_lms_leader_*) -----
    private const string CvarExtraLives        = "g_lms_extra_lives";        // lives granted by an extralife pickup
    private const string CvarLeaderLivesDiff   = "g_lms_leader_lives_diff";  // min lead over 2nd to be a leader (2)
    private const string CvarLeaderMinPercent  = "g_lms_leader_minpercent";  // leaders must be <= this fraction (0.5)

    /// <summary>Per-player LMS bookkeeping (QC LMS_LIVES / LMS_RANK scoring fields + FRAGS_PLAYER_OUT_OF_GAME).</summary>
    public sealed class LmsState
    {
        /// <summary>QC LMS_LIVES: remaining lives. Player is eliminated when this reaches 0.</summary>
        public int Lives;

        /// <summary>QC LMS_RANK: finishing place (1 = winner). 0 while still in the game.</summary>
        public int Rank;

        /// <summary>QC <c>frags == FRAGS_PLAYER_OUT_OF_GAME</c>: true once eliminated (no lives left).</summary>
        public bool OutOfGame => Lives <= 0;

        /// <summary>QC <c>.lms_leader</c>: this player currently leads on lives (gets a leader waypoint sprite).</summary>
        public bool IsLeader;
    }

    /// <summary>The lives/rank table, keyed by player (QC stored on the client edict's scoring fields).</summary>
    private readonly Dictionary<Player, LmsState> _states = new();

    private HookHandler<DeathEvent>? _deathHandler;

    /// <summary>Optional sink for the host/controller to react to a frag/elimination.</summary>
    public IMatchEvents? Events;

    /// <summary>QC checkrules end-of-match latch: true once ≤1 player still has lives.</summary>
    public bool MatchEnded { get; private set; }

    /// <summary>The last player standing once the match has ended (QC first_player.winning), else null.</summary>
    public Player? Winner { get; private set; }

    public LastManStanding()
    {
        NetName = "lms";
        DisplayName = "Last Man Standing";
        TeamGame = false;
    }

    public override void OnInit()
    {
        // QC INIT(LastManStanding): identity is set in the ctor. gametype_init flags (USEPOINTS|HIDELIMITS)
        // and legacy defaults are engine concerns; lives are seeded per join via StartingLives / NewPlayerLives.
    }

    /// <summary>The lowest live-life count across the active roster (QC lms_lowest_lives), 999 when empty.</summary>
    public int LowestLives()
    {
        int lowest = 999;
        foreach (LmsState st in _states.Values)
            if (!st.OutOfGame && st.Lives < lowest)
                lowest = st.Lives;
        return lowest;
    }

    /// <summary>
    /// QC LMS_NewPlayerLives: the lives a player joining now should get. A late joiner receives the current
    /// lowest life count (bounded to the fraglimit), so they don't start ahead; returns 0 (can't join) once
    /// the leader has lost enough lives and join-anytime is off, or once the first player is fully out.
    /// </summary>
    public int NewPlayerLives()
    {
        int fl = 999;
        if (TryCvar(CvarFragLimit, out float f)) { int v = (int)f; if (v > 0 && v <= 999) fl = v; }

        int lowest = LowestLives();
        if (lowest < 1)
            return 0; // a player already left for dying too much → nobody else can get in

        bool joinAnytime = TryCvar("g_lms_join_anytime", out float ja) && ja != 0f;
        if (!joinAnytime)
        {
            int lastJoin = TryCvar("g_lms_last_join", out float lj) ? (int)System.Math.Max(0f, System.MathF.Floor(lj)) : 0;
            if (lowest < fl - lastJoin)
                return 0; // too late to join
        }
        return System.Math.Clamp(lowest, 1, fl);
    }

    /// <summary>
    /// QC the g_lms_dynamic_respawn_delay scaling: a player with fewer lives than the leader respawns slower
    /// (base + increase * (maxLives - playerLives), capped). Returns the delay in seconds for a player with
    /// <paramref name="playerLives"/> lives. With the dynamic delay off, returns the base delay.
    /// </summary>
    public float RespawnDelayFor(int playerLives)
    {
        if (!(TryCvar("g_lms_dynamic_respawn_delay", out float dyn) && dyn != 0f))
            return TryCvar("g_lms_dynamic_respawn_delay_base", out float b0) ? b0 : 2f;

        float baseDelay = TryCvar("g_lms_dynamic_respawn_delay_base", out float b) ? b : 2f;
        float increase = TryCvar("g_lms_dynamic_respawn_delay_increase", out float i) ? i : 3f;
        float max = TryCvar("g_lms_dynamic_respawn_delay_max", out float m) ? m : 20f;

        int maxLives = 0;
        foreach (LmsState st in _states.Values)
            if (st.Lives > maxLives) maxLives = st.Lives;

        float delay = baseDelay + increase * System.Math.Max(0, maxLives - playerLives);
        return System.Math.Min(delay, max);
    }

    /// <summary>
    /// The number of lives a freshly joining player starts with (g_lms_lives_override, else g_lms_lives,
    /// else 5), capped at the engine fraglimit when one is set (QC LMS_NewPlayerLives bounds to fraglimit).
    /// </summary>
    public int StartingLives
    {
        get
        {
            int lives = DefaultLives;
            if (TryCvar(CvarLivesDefault, out float ov) && ov > 0f) lives = (int)ov;
            else if (TryCvar(CvarLives, out float l) && l > 0f) lives = (int)l;

            // QC LMS_NewPlayerLives: bound(1, lives, fraglimit) when a fraglimit (>0, <=999) is set.
            if (TryCvar(CvarFragLimit, out float fl))
            {
                int cap = (int)fl;
                if (cap > 0 && cap <= 999 && lives > cap) lives = cap;
            }
            return lives < 1 ? 1 : lives;
        }
    }

    public void Activate()
    {
        if (_deathHandler is not null)
            return;
        MatchEnded = false;
        Winner = null;

        // QC sv_lms.qh GameRules_scoring(0, 0, 0, { field(SP_LMS_LIVES, "lives", SFL_SORT_PRIO_SECONDARY);
        // field(SP_LMS_RANK, "rank", SFL_LOWER_IS_BETTER | SFL_RANK | SFL_SORT_PRIO_PRIMARY | SFL_ALLOW_HIDE); }):
        // rank the standings by LMS_RANK ascending (1st place lowest), then by remaining LMS_LIVES. SP_SCORE is
        // present (sprio 0) but not a sort key. The LmsState mirror stays authoritative; columns mirror it for sorting.
        GS.ScoreRulesBasics(teams: false);
        GS.DeclareColumn("LMS_LIVES", Scoring.ScoreFlags.None, "lives");
        GS.DeclareColumn("LMS_RANK",
            Scoring.ScoreFlags.LowerIsBetter | Scoring.ScoreFlags.Rank | Scoring.ScoreFlags.AllowHide, "rank");
        GS.SetSortKeys(GS.Field("LMS_RANK")!, GS.Field("LMS_LIVES"));

        _deathHandler = OnDeath;
        Combat.Death.Add(_deathHandler);
    }

    /// <summary>QC <c>GameRules_scoring_add</c>: mirror the authoritative <see cref="LmsState"/> into the networked
    /// LMS_RANK + LMS_LIVES columns so the scoreboard shows + sorts by them. Called wherever lives/rank change.</summary>
    private static void SyncColumns(Player p, LmsState st)
    {
        Scoring.ScoreField? lives = Scoring.GameScores.Field("LMS_LIVES");
        Scoring.ScoreField? rank = Scoring.GameScores.Field("LMS_RANK");
        if (lives is not null) Scoring.GameScores.SetPlayer(p, lives, System.Math.Max(0, st.Lives));
        if (rank is not null) Scoring.GameScores.SetPlayer(p, rank, st.Rank);
    }

    public void Deactivate()
    {
        if (_deathHandler is null)
            return;
        Combat.Death.Remove(_deathHandler);
        _deathHandler = null;
    }

    /// <summary>Look up (or lazily create, seeded with <see cref="StartingLives"/>) a player's LMS state.</summary>
    public LmsState GetState(Player p)
    {
        if (!_states.TryGetValue(p, out LmsState? st))
        {
            st = new LmsState { Lives = StartingLives };
            _states[p] = st;
        }
        return st;
    }

    /// <summary>QC lms_AddPlayer: register a player with a fresh pool of lives (no-op if already tracked).</summary>
    public void AddPlayer(Player p) => SyncColumns(p, GetState(p)); // seed LMS_LIVES on the scoreboard

    public void RemovePlayer(Player p) => _states.Remove(p);

    /// <summary>The lives a player has left (0 if eliminated / unknown).</summary>
    public int LivesOf(Player p) => _states.TryGetValue(p, out LmsState? st) ? st.Lives : 0;

    /// <summary>
    /// The obituary handler — QC MUTATOR_HOOKFUNCTION(lms, GiveFragsForKill): the victim loses one life and
    /// the kill scores no frag points. On reaching 0 lives the victim is eliminated (out of game) and given
    /// a finishing rank below the players still alive. Then re-check the last-player-standing win condition.
    /// </summary>
    private bool OnDeath(ref DeathEvent ev)
    {
        if (ev.Victim is not Player victim)
            return false;
        if (MatchEnded)
            return false;

        LmsState st = GetState(victim);
        if (!st.OutOfGame)
        {
            st.Lives -= 1;                                  // QC: GameRules_scoring_add(target, LMS_LIVES, -1)
            if (st.OutOfGame)
                st.Rank = CountAlive() + 1;                 // QC: rank = (#players still in) + 1
            SyncColumns(victim, st);                        // mirror LMS_LIVES / LMS_RANK to the scoreboard
        }

        // QC zeroes the frag score (M_ARGV(2,float)=0): kills don't add points in LMS — lives are the score.

        Player? attacker = ev.Attacker as Player;
        Events?.OnFrag(attacker, victim, ev.DeathType);

        CheckWinningCondition();
        return false;
    }

    /// <summary>Number of tracked players that still have lives (QC IS_PLAYER &amp;&amp; frags == FRAGS_PLAYER).</summary>
    public int CountAlive()
    {
        int n = 0;
        foreach (LmsState st in _states.Values)
            if (!st.OutOfGame) n++;
        return n;
    }

    /// <summary>
    /// QC WinningCondition_LMS (reduced core): the match ends once at most one player still has lives. With
    /// exactly one survivor that player wins and is ranked first; with zero (mutual elimination) it's a draw.
    /// The richer QC rules (join-anytime, tie/time-limit overtime, campaign) are deferred.
    /// </summary>
    public void CheckWinningCondition()
    {
        if (_states.Count == 0)
            return; // nobody playing yet

        int alive = 0;
        Player? lastAlive = null;
        foreach (KeyValuePair<Player, LmsState> kv in _states)
        {
            if (!kv.Value.OutOfGame)
            {
                alive++;
                lastAlive = kv.Key;
            }
        }

        if (alive <= 1)
        {
            MatchEnded = true;
            if (alive == 1 && lastAlive is not null)
            {
                Winner = lastAlive;
                LmsState ws = GetState(lastAlive);
                ws.Rank = 1; // QC: first_player.winning = 1; LMS_RANK = 1
                SyncColumns(lastAlive, ws);
            }
        }
    }

    // ============================================================================================
    //  Extra-life pickups (QC ITEM_ExtraLife / lms_replace_with_extralife / lms_extralife touch)
    // ============================================================================================

    /// <summary>The number of lives an extra-life pickup grants (QC g_lms_extra_lives, default 0 = off).</summary>
    public int ExtraLives => TryCvar(CvarExtraLives, out float v) && v > 0f ? (int)v : 0;

    /// <summary>
    /// QC the lms_extralife touch: grant <see cref="ExtraLives"/> lives to a still-in-game player who picks up
    /// an extra-life item. No effect once the player is out (you can't pick up extra lives after elimination)
    /// or when extra lives are disabled. Returns the lives added.
    /// </summary>
    public int GiveExtraLife(Player p)
    {
        int extra = ExtraLives;
        if (extra <= 0) return 0;
        LmsState st = GetState(p);
        if (st.OutOfGame) return 0; // QC: eliminated players can't gain lives
        st.Lives += extra;          // QC GameRules_scoring_add(toucher, LMS_LIVES, g_lms_extra_lives)
        SyncColumns(p, st);
        return extra;
    }

    /// <summary>
    /// QC lms_replace_with_extralife / item_extralife spawnfunc: spawn an extra-life pickup at
    /// <paramref name="origin"/> whose touch grants lives to a still-in-game player. Returns the item entity
    /// (null when no facade is wired or extra lives are off).
    /// </summary>
    public Entity? SpawnExtraLife(System.Numerics.Vector3 origin)
    {
        if (ExtraLives <= 0)
            return null;
        Entity? e = GametypeEntities.SpawnObjective("item_extralife", origin, Teams.None,
            new System.Numerics.Vector3(-16, -16, -16), new System.Numerics.Vector3(16, 16, 48),
            touch: ExtraLifeTouch);
        if (e is not null)
            e.Flags = EntFlags.Item;
        return e;
    }

    private void ExtraLifeTouch(Entity item, Entity other)
    {
        if (other is not Player p || p.IsDead) return;
        if (GiveExtraLife(p) > 0)
        {
            NotificationSystem.Center(p, "EXTRALIVES", ExtraLives); // QC CENTER_EXTRALIVES
            Api.Entities?.Remove(item);                              // consumed
        }
    }

    // ============================================================================================
    //  Leader computation (QC lms_UpdateLeaders) — mark the max-lives players as leaders
    // ============================================================================================

    /// <summary>The lives lead the current leader(s) hold over the next-best player (QC lms_leaders_lives_diff).</summary>
    public int LeadersLivesDiff { get; private set; }

    /// <summary>The players currently marked as leaders (QC .lms_leader). Recomputed by <see cref="UpdateLeaders"/>.</summary>
    public IEnumerable<Player> Leaders
    {
        get { foreach (var kv in _states) if (kv.Value.IsLeader) yield return kv.Key; }
    }

    /// <summary>
    /// QC <c>lms_UpdateLeaders</c>: the in-game players with the most lives become leaders — but only when their
    /// lead over the second-best is at least <c>g_lms_leader_lives_diff</c> AND they are at most
    /// <c>g_lms_leader_minpercent</c> of the in-game field (so a tight pack has no "leader"). Sets each player's
    /// <see cref="LmsState.IsLeader"/>; the host renders the leader waypoint sprite. Call per frame.
    /// </summary>
    public void UpdateLeaders()
    {
        int maxLives = 0, plCnt = 0;
        foreach (var kv in _states)
            if (!kv.Value.OutOfGame) { plCnt++; if (kv.Value.Lives > maxLives) maxLives = kv.Value.Lives; }

        int secondMax = 0, cntWithMax = 0;
        foreach (var kv in _states)
        {
            if (kv.Value.OutOfGame) continue;
            if (kv.Value.Lives == maxLives) cntWithMax++;
            else if (kv.Value.Lives > secondMax) secondMax = kv.Value.Lives;
        }

        LeadersLivesDiff = maxLives - secondMax;

        int livesDiff = TryCvar(CvarLeaderLivesDiff, out float ld) ? (int)ld : 2;
        float minPercent = TryCvar(CvarLeaderMinPercent, out float mp) ? mp : 0.5f;

        bool haveLeaders = LeadersLivesDiff >= livesDiff && cntWithMax <= plCnt * minPercent;
        foreach (var kv in _states)
        {
            if (kv.Value.OutOfGame) { kv.Value.IsLeader = false; continue; }
            kv.Value.IsLeader = haveLeaders && kv.Value.Lives == maxLives;
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
