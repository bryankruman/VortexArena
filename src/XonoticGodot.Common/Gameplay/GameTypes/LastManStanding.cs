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
/// Faithfully ported (cont. 3 — Wave-2 mutator hooks, subscribed in <see cref="Activate"/>):
///  - the fixed LMS start loadout (QC SetStartItems: g_lms_start_health/armor 200/200 + ammo 60/320/160/180/0,
///    unlimited-ammo flag per g_use_ammunition);
///  - the forced weapon arena (QC SetWeaponArena: g_lms_weaponarena "most_available");
///  - no weapon dropping (QC ForbidThrowCurrentWeapon);
///  - regen/rot disabled (QC PlayerRegen: g_lms_regenerate / g_lms_rot, both off by default);
///  - the dynamic-vampire damage modifier (QC Damage_Calculate: under-dogs steal a scaled fraction of damage
///    dealt to leaders, base 0.1 + 0.1/life behind, capped 0.5, clamped at start_health);
///  - the map-item filter (QC FilterItemDefinition: suppress map items unless g_lms_items / g_pickup_items&gt;0).
///
/// Deferred (NOTE — cross-boundary, tracked in the lms registry shard): the leader WAYPOINT SPRITE rendering +
/// periodic visibility window + HUD mod-icon (client/SV_StartFrame), respawn denial wired off OutOfGame
/// (CalculateRespawnTime — currently the round gate substitutes), the late-join NewPlayerLives/AddPlayer +
/// forfeit RemovePlayer rank reshuffle + map/round reset forwarding from MatchController, and the tie/time-limit
/// overtime path — all need a host-side .Call site outside this file.
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

    // ----- loadout / regen / arena / vampire cvars (QC SetStartItems / PlayerRegen / SetWeaponArena / Damage_Calculate) -----
    private const string CvarWeaponArena = "g_lms_weaponarena"; // forced weapon arena ("most_available")

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
    private HookHandler<MutatorHooks.SetStartItemsArgs>? _startItemsHandler;
    private HookHandler<MutatorHooks.SetWeaponArenaArgs>? _weaponArenaHandler;
    private HookHandler<MutatorHooks.ForbidThrowCurrentWeaponArgs>? _forbidThrowHandler;
    private HookHandler<MutatorHooks.PlayerRegenArgs>? _regenHandler;
    private HookHandler<MutatorHooks.DamageCalculateArgs>? _damageCalcHandler;
    private HookHandler<MutatorHooks.FilterItemDefinitionArgs>? _filterItemHandler;

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

        // QC mutator hooks (LMS is a CA-like fixed-loadout survival mode): the fixed start loadout, the forced
        // weapon arena, no weapon-drop, no regen/rot, dynamic vampire, and the map-item filter. These are the
        // shared Wave-1 seams already wired into the spawn/damage/item pipelines (Mayhem/CA subscribe the same way).
        _startItemsHandler = OnSetStartItems;
        MutatorHooks.SetStartItems.Add(_startItemsHandler);

        _weaponArenaHandler = OnSetWeaponArena;
        MutatorHooks.SetWeaponArena.Add(_weaponArenaHandler);

        _forbidThrowHandler = OnForbidThrowCurrentWeapon;
        MutatorHooks.ForbidThrowCurrentWeapon.Add(_forbidThrowHandler);

        _regenHandler = OnPlayerRegen;
        MutatorHooks.PlayerRegen.Add(_regenHandler);

        _damageCalcHandler = OnDamageCalculate;
        MutatorHooks.DamageCalculate.Add(_damageCalcHandler);

        _filterItemHandler = OnFilterItemDefinition;
        MutatorHooks.FilterItemDefinition.Add(_filterItemHandler);
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

    public override void Deactivate()
    {
        if (_deathHandler is null)
            return;
        Combat.Death.Remove(_deathHandler);
        _deathHandler = null;

        if (_startItemsHandler is not null) { MutatorHooks.SetStartItems.Remove(_startItemsHandler); _startItemsHandler = null; }
        if (_weaponArenaHandler is not null) { MutatorHooks.SetWeaponArena.Remove(_weaponArenaHandler); _weaponArenaHandler = null; }
        if (_forbidThrowHandler is not null) { MutatorHooks.ForbidThrowCurrentWeapon.Remove(_forbidThrowHandler); _forbidThrowHandler = null; }
        if (_regenHandler is not null) { MutatorHooks.PlayerRegen.Remove(_regenHandler); _regenHandler = null; }
        if (_damageCalcHandler is not null) { MutatorHooks.DamageCalculate.Remove(_damageCalcHandler); _damageCalcHandler = null; }
        if (_filterItemHandler is not null) { MutatorHooks.FilterItemDefinition.Remove(_filterItemHandler); _filterItemHandler = null; }
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

    // ============================================================================================
    //  Mutator-style hooks (QC SetStartItems / SetWeaponArena / ForbidThrowCurrentWeapon /
    //  PlayerRegen / Damage_Calculate / FilterItem) — LMS is a CA-like fixed-loadout survival mode
    // ============================================================================================

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(lms, SetStartItems): give the fixed LMS spawn loadout
    /// (g_lms_start_health/armor 200/200 + ammo 60/320/160/180/0, balance-xonotic.cfg). Strip the unlimited
    /// flags, then grant unlimited ammo when g_use_ammunition is off (QC: <c>if(!cvar("g_use_ammunition"))
    /// start_items |= IT_UNLIMITED_AMMO</c>). Tag convention matches the other SetStartItems handlers.
    /// </summary>
    private bool OnSetStartItems(ref MutatorHooks.SetStartItemsArgs args)
    {
        StartLoadout l = args.Loadout;
        l.ItemFlags.Remove("UNLIMITED_AMMO");
        l.ItemFlags.Remove("UNLIMITED_SUPERWEAPONS");
        if (Cvar("g_use_ammunition", 1f) == 0f)
            l.ItemFlags.Add("UNLIMITED_AMMO");

        l.Health      = Cvar("g_lms_start_health", 200f);
        l.Armor       = Cvar("g_lms_start_armor", 200f);
        l.AmmoShells  = Cvar("g_lms_start_ammo_shells", 60f);
        l.AmmoBullets = Cvar("g_lms_start_ammo_nails", 320f);
        l.AmmoRockets = Cvar("g_lms_start_ammo_rockets", 160f);
        l.AmmoCells   = Cvar("g_lms_start_ammo_cells", 180f);
        l.AmmoFuel    = Cvar("g_lms_start_ammo_fuel", 0f);
        return false;
    }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(lms, SetWeaponArena): default the weapon arena to g_lms_weaponarena
    /// ("most_available") when the configured arena is unset / "0".
    /// </summary>
    private bool OnSetWeaponArena(ref MutatorHooks.SetWeaponArenaArgs args)
    {
        if (args.Arena == "0" || string.IsNullOrEmpty(args.Arena))
            args.Arena = CvarString(CvarWeaponArena, "most_available");
        return false;
    }

    /// <summary>QC MUTATOR_HOOKFUNCTION(lms, ForbidThrowCurrentWeapon): always forbid dropping the current weapon.</summary>
    private bool OnForbidThrowCurrentWeapon(ref MutatorHooks.ForbidThrowCurrentWeaponArgs args) => true;

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(lms, PlayerRegen): disable regen and rot unless g_lms_regenerate / g_lms_rot
    /// are set (both default 0). Returns true (regen fully off) when neither is enabled — matching QC's
    /// <c>return (!regenerate &amp;&amp; !rot)</c>, the common case (both off ⇒ no regen, no rot).
    /// </summary>
    private bool OnPlayerRegen(ref MutatorHooks.PlayerRegenArgs args)
    {
        bool regenerate = Cvar("g_lms_regenerate", 0f) != 0f;
        bool rot = Cvar("g_lms_rot", 0f) != 0f;
        return !regenerate && !rot;
    }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(lms, Damage_Calculate): the dynamic vampire — an under-dog (more lives behind)
    /// who hits a leader steals a fraction of the dealt damage as health. factor = base(0.1) + 0.1*lives_behind
    /// (over the 2-life threshold), capped at 0.5; health is clamped at start_health. ENABLED by default
    /// (g_lms_dynamic_vampire 1). Reads the live LmsState lives, not the (deferred) frags field.
    /// </summary>
    private bool OnDamageCalculate(ref MutatorHooks.DamageCalculateArgs args)
    {
        if (Cvar("g_lms_dynamic_vampire", 1f) == 0f)
            return false;

        if (args.Attacker is not Player attacker || args.Target is not Player target)
            return false;
        if (ReferenceEquals(attacker, target))
            return false;
        if (attacker.IsDead || target.IsDead)
            return false;

        float factorBase = Cvar("g_lms_dynamic_vampire_factor_base", 0.1f);
        float factorIncrease = Cvar("g_lms_dynamic_vampire_factor_increase", 0.1f);
        float factorMax = Cvar("g_lms_dynamic_vampire_factor_max", 0.5f);
        int minDiff = TryCvar("g_lms_dynamic_vampire_min_lives_diff", out float md) ? (int)md : 2;

        // Read lives through GetState so a not-yet-tracked combatant is lazily seeded with StartingLives
        // (mirrors QC, where every spawned player already has LMS_LIVES set), not treated as 0.
        int diff = GetState(target).Lives - GetState(attacker).Lives - minDiff;
        if (diff < 0)
            return false;

        float vampireFactor = factorBase + diff * factorIncrease;
        if (vampireFactor <= 0f)
            return false;
        vampireFactor = System.Math.Min(vampireFactor, factorMax);

        // QC clamps the steal at start_health (the LMS start health, 200 by default).
        float startHealth = Cvar("g_lms_start_health", 200f);
        float healed = attacker.GetResource(ResourceType.Health) + args.Damage * vampireFactor;
        attacker.SetResource(ResourceType.Health, System.Math.Min(healed, startHealth));
        return false;
    }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(lms, FilterItemDefinition): suppress map item spawns. Items are kept only when
    /// g_lms_items or g_pickup_items&gt;0; otherwise everything is filtered, EXCEPT ITEM_ExtraLife/ITEM_HealthMega
    /// when g_powerups + g_lms_extra_lives are on (those become the extra-life pickups). Returns true to FILTER OUT.
    /// Definitions are matched by ClassName (the item registry isn't fully ported — same stand-in Mayhem/NIX use).
    /// </summary>
    private bool OnFilterItemDefinition(ref MutatorHooks.FilterItemDefinitionArgs args)
    {
        if (Cvar("g_lms_items", 0f) != 0f || Cvar("g_pickup_items", -1f) > 0f)
            return false; // items allowed → don't filter

        string id = args.Definition.ClassName;
        bool isExtraLifeOrMega = id is "item_extralife" or "item_health_mega" or "item_healthmega";
        if (Cvar("g_powerups", -1f) != 0f && Cvar(CvarExtraLives, 0f) != 0f && isExtraLifeOrMega)
            return false; // kept: HealthMega is replaced with an extralife

        return true; // filter out all other map items
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

    private static string CvarString(string name, string fallback)
    {
        if (Api.Services is null) return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : s;
    }
}
