using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;
using GS = XonoticGodot.Common.Gameplay.Scoring.GameScores; // T7: alias the static score table for the per-mode ScoreRules

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Key Hunt — port of <c>CLASS(KeyHunt, Gametype)</c>
/// (common/gametypes/gametype/keyhunt/{keyhunt.qh,sv_keyhunt.qc}). There is one key per team; a team
/// scores a "capture" when it gathers ALL keys onto its own players simultaneously (QC
/// kh_Key_AllOwnedByWhichTeam → kh_FinishCapture). Captures and collect/push events add to the team's
/// SCORE; first team to the point limit (QC g_keyhunt_point_limit, default 1000) wins. Default 3 teams.
///
/// QC defaults (gametype_init): "timelimit=20 pointlimit=1000 teams=3 leadlimit=0" (legacydefaults "1000 20 3 0").
///
/// Faithfully ported (Godot-free essence):
///  - smallest-team assignment on join (<see cref="TeamBalance"/>);
///  - per-team keys with a carrier (<see cref="KeyState"/>), assigned by pickup
///    (<see cref="AssignKey"/>, QC kh_Key_AssignTo);
///  - the all-keys-on-one-team capture check (<see cref="CheckCapture"/>, QC kh_Key_AllOwnedByWhichTeam)
///    awarding the team the capture SCORE + KH_CAPS and resetting the keys;
///  - point-limit + lead-limit win condition (GameRules_limit_score → fraglimit).
///
/// Faithfully ported (objective layer):
///  - one key entity per team spawned at round start onto a random teammate (<see cref="StartRound"/>,
///    QC kh_StartRound → kh_Key_Spawn);
///  - the controller think loop with wait-for-players (<see cref="Tick"/>/<see cref="WaitForPlayers"/>,
///    QC kh_Controller_Think / kh_WaitForPlayers);
///  - drop on death + collect by an enemy + auto-return of a dropped key (<see cref="DropAllKeys"/>/
///    <see cref="KeyTouch"/>/<see cref="KeyThink"/>, QC kh_Key_DropAll / kh_Key_Collect / kh_Key_Think);
///  - the all-keys-on-one-team-AND-in-range capture (<see cref="CheckCaptureGeometry"/>, QC kh_Key_Think →
///    kh_WinnerTeam) distributing the capture SCORE, and the carrier-frag bonus.
///
/// Faithfully ported (presentation-feeding):
///  - key model + team coloring + glow + spin (QC kh_Key_Spawn: model "key", colormod = Team_ColorRGB × KH_KEY_BRIGHTNESS,
///    per-team netname): <see cref="SpawnKey"/> stamps the model/effects/colormap/netname + a slow spin so the
///    networked entity stream renders the key (the ServerNet picks up any entity carrying a model). The carried
///    key swaps to the carried model on attach (<see cref="SetKeyVisual"/>); the waypoint SPRITE is the only
///    client-only remainder.
///
/// Deferred (NOTE — cross-boundary): the key WAYPOINT SPRITE rendering, the push-off-map "destroyed" loser
/// bonus split (kh_LoserTeam), and the interfere/meet center-print notifies.
/// </summary>
[GameType]
public sealed class KeyHunt : GameType
{
    // ----- key presentation (QC kh_Key_Spawn model/colormod/netname; KH_KEY_BRIGHTNESS = 2) -----
    private const string KeyModel = "models/keyhunt/key.md3";   // QC MDL_KH_KEY (dropped) / MDL_KH_KEY_CARRIED
    private const float  KeyBrightness = 2f;                    // QC KH_KEY_BRIGHTNESS
    private static readonly Vector3 KeySpin = new(0f, 90f, 0f); // a slow yaw spin for a dropped key (cosmetic)
    // ----- point-limit cvars + default (g_keyhunt_point_limit → fraglimit; legacydefaults 1000) -----
    private const string CvarPointLimitKh = "g_keyhunt_point_limit";
    private const string CvarPointLimit   = "fraglimit";
    private const string CvarLeadLimitKh  = "g_keyhunt_point_leadlimit";
    private const string CvarLeadLimit    = "leadlimit";
    private const float  DefaultPointLimit = 1000f;

    // ----- capture/collect score cvars + defaults (g_balance_keyhunt_score_*) -----
    private const string CvarScoreCapture = "g_balance_keyhunt_score_capture";
    private const string CvarScoreCollect = "g_balance_keyhunt_score_collect";
    private const string CvarScoreCarrierFrag = "g_balance_keyhunt_score_carrierfrag";
    private const float  DefaultScoreCapture = 100f;
    private const float  DefaultScoreCollect = 1f;
    private const float  DefaultScoreCarrierFrag = 1f;

    // ----- key timing cvars (g_balance_keyhunt_*) -----
    private const string CvarDelayReturn  = "g_balance_keyhunt_delay_return";  // seconds a dropped key auto-returns
    private const string CvarDelayCollect = "g_balance_keyhunt_delay_collect"; // dropper re-collect delay
    private const string CvarDelayRound   = "g_balance_keyhunt_delay_round";   // countdown between rounds
    private const string CvarMaxDist      = "g_balance_keyhunt_maxdist";       // carriers must be within this to capture
    private const float  DefaultDelayReturn  = 15f;
    private const float  DefaultDelayCollect = 1.5f;
    private const float  DefaultDelayRound   = 5f;
    private const float  DefaultMaxDist      = 4000f;

    /// <summary>Key bbox (QC KH_KEY_MIN/MAX).</summary>
    private static readonly Vector3 KeyMins = new(-10f, -10f, -46f);
    private static readonly Vector3 KeyMaxs = new(10f, 10f, 3f);

    public float ScoreCarrierFrag => TryCvar(CvarScoreCarrierFrag, out float v) ? v : DefaultScoreCarrierFrag;
    public float DelayReturn  => TryCvar(CvarDelayReturn, out float v) ? v : DefaultDelayReturn;
    public float DelayCollect => TryCvar(CvarDelayCollect, out float v) ? v : DefaultDelayCollect;
    public float DelayRound   => TryCvar(CvarDelayRound, out float v) ? v : DefaultDelayRound;
    public float MaxDist      => TryCvar(CvarMaxDist, out float v) ? v : DefaultMaxDist;

    /// <summary>QC kh_controller state: the round phase the controller is driving.</summary>
    public enum RoundPhase { WaitingForPlayers, Countdown, InProgress }

    /// <summary>The current round phase (QC kh_controller think state).</summary>
    public RoundPhase Phase { get; private set; } = RoundPhase.WaitingForPlayers;

    /// <summary>Absolute sim time the countdown ends and the round begins (QC kh_controller countdown).</summary>
    public float RoundStartTime { get; private set; }

    /// <summary>The roster the controller drives (set by the host before <see cref="Tick"/>).</summary>
    private IReadOnlyList<Player> _roster = System.Array.Empty<Player>();

    /// <summary>Provide the controller with the current roster (call before <see cref="Tick"/>).</summary>
    public void SetRoster(IReadOnlyList<Player> roster) => _roster = roster;

    // ----- team count cvars (g_keyhunt_teams_override >= 2 ? override : g_keyhunt_teams), 2..4, default 3 -----
    private const string CvarTeamsOverride = "g_keyhunt_teams_override";
    private const string CvarTeams         = "g_keyhunt_teams";
    private const int    DefaultTeams      = 3;

    // Per-team running totals (QC ST_SCORE slot 0 + ST_KH_CAPS slot 1) now live in the unified GameScores two-slot
    // team store — the source of truth (common/scores.qh). KH's team primary is ST_SCORE with ST_KH_CAPS
    // secondary. Read/written via GetTeamScore / AddTeamScore (slot 0) + CreditCapture (slot 1 caps).

    /// <summary>The key belonging to each team (QC FOR_EACH_KH_KEY), keyed by the key's home-team color code.</summary>
    public readonly Dictionary<int, KeyState> Keys = new();

    public bool MatchEnded { get; private set; }
    public int LeaderTeam { get; private set; }

    private HookHandler<DeathEvent>? _deathHandler;

    public KeyHunt()
    {
        NetName = "kh";
        DisplayName = "Key Hunt";
        TeamGame = true;
    }

    public override void OnInit()
    {
        // QC kh_Initialize: the controller starts in wait-for-players; the first round spawns one key per
        // team once every team has a live player (kh_WaitForPlayers → kh_StartRound). GameRules_teams(true)
        // and kh_ScoreRules are the engine's job; here we put the controller in its initial phase.
        Phase = RoundPhase.WaitingForPlayers;
        RoundStartTime = 0f;
    }

    /// <summary>
    /// QC sv_keyhunt.qh: <c>GameRules_spawning_teams(autocvar_g_keyhunt_team_spawns)</c> — KeyHunt gates team
    /// spawns on g_keyhunt_team_spawns (stock default 0, so it does NOT use team spawnpoints by default).
    /// </summary>
    public override bool RequestsTeamSpawns => TryCvar("g_keyhunt_team_spawns", out float v) ? v != 0f : false;

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

    /// <summary>Point limit in force (g_keyhunt_point_limit, else fraglimit, else 1000). 0 == unlimited.</summary>
    public float PointLimit
    {
        get
        {
            if (TryCvar(CvarPointLimitKh, out float pl)) return pl;
            if (TryCvar(CvarPointLimit, out float fl)) return fl;
            return DefaultPointLimit;
        }
    }

    public float LeadLimit
    {
        get
        {
            if (TryCvar(CvarLeadLimitKh, out float ll)) return ll;
            if (TryCvar(CvarLeadLimit, out float l)) return l;
            return 0f;
        }
    }

    public float ScoreCapture => TryCvar(CvarScoreCapture, out float v) ? v : DefaultScoreCapture;
    public float ScoreCollect => TryCvar(CvarScoreCollect, out float v) ? v : DefaultScoreCollect;

    public void Activate()
    {
        if (_deathHandler is not null)
            return;
        MatchEnded = false;
        LeaderTeam = Teams.None;
        Scoring.GameScores.ResetTeams(); // QC Score_ClearAll at match start: zero both team slots before declaring
        DeclareScoreRules();
        Scoring.GameScores.SeedTeams(TeamCount); // zero both team slots for the active teams (stable leader scan)
        foreach (int team in Teams.Active(TeamCount))
            if (!Keys.ContainsKey(team)) Keys[team] = new KeyState(team);
        _deathHandler = OnDeath;
        Combat.Death.Add(_deathHandler);
    }

    /// <summary>
    /// QC <c>kh_ScoreRules</c> (sv_keyhunt.qc): declare KeyHunt's columns + the two TEAM-score slots and pin the
    /// sort keys. QC: <c>GameRules_scoring(teams, PRIMARY, PRIMARY, { field_team(ST_KH_CAPS,"caps",SECONDARY);
    /// field(SP_KH_CAPS,"caps",SECONDARY); ... })</c> — so the TEAM primary is slot 0 (ST_SCORE, stprio=PRIMARY)
    /// with slot 1 (ST_KH_CAPS, "caps") SECONDARY; the PLAYER primary is SP_SCORE with SP_KH_CAPS secondary. The
    /// other columns (kckills/pushes/destructions/pickups/losses) are display stats.
    /// </summary>
    private static void DeclareScoreRules()
    {
        GS.ScoreRulesBasics(teams: true);
        // Team slots: ST_SCORE (slot 0) "score" PRIMARY; ST_KH_CAPS (slot 1) "caps" SECONDARY.
        GS.TeamRulesBasics(scorePrio: Scoring.ScoreFlags.SortPrioPrimary);
        GS.SetTeamLabel(GS.TeamSlotSecondary, "caps", Scoring.ScoreFlags.SortPrioSecondary);
        GS.DeclareColumn("KH_CAPS", Scoring.ScoreFlags.None, "caps");
        GS.DeclareColumn("KH_PUSHES", Scoring.ScoreFlags.None, "pushes");
        GS.DeclareColumn("KH_DESTRUCTIONS", Scoring.ScoreFlags.LowerIsBetter, "destructions");
        GS.DeclareColumn("KH_PICKUPS", Scoring.ScoreFlags.None, "pickups");
        GS.DeclareColumn("KH_KCKILLS", Scoring.ScoreFlags.None, "kckills");
        GS.DeclareColumn("KH_LOSSES", Scoring.ScoreFlags.LowerIsBetter, "losses");
        GS.SetSortKeys(GS.Score, GS.Field("KH_CAPS"));
    }

    /// <summary>QC <c>GameRules_scoring_add(player, SP_X, n)</c> for a KH player column (no-op if unregistered).</summary>
    private static void AddCol(Player p, string field, int n)
    {
        Scoring.ScoreField? f = Scoring.GameScores.Field(field);
        if (f is not null) Scoring.GameScores.AddToPlayer(p, f, n);
    }

    /// <summary>QC <c>GameRules_scoring_add_team(key.owner, KH_CAPS, 1)</c>: credit KH_CAPS to BOTH the team
    /// (ST_KH_CAPS, team slot 1 — the team secondary) and a key carrier on the capturing team (the port has no
    /// single "key.owner", so the first such carrier is credited the player SP_KH_CAPS column).</summary>
    private void CreditCapture(int team)
    {
        if (team != Teams.None)
            Scoring.GameScores.AddToTeam(team, Scoring.GameScores.TeamSlotSecondary, 1); // QC team ST_KH_CAPS
        foreach (var key in Keys.Values)
            if (key.Carrier is { } c && (int)c.Team == team) { AddCol(c, "KH_CAPS", 1); return; }
    }

    public void Deactivate()
    {
        if (_deathHandler is null)
            return;
        Combat.Death.Remove(_deathHandler);
        _deathHandler = null;
    }

    public int AssignTeam(Player joiner, IReadOnlyList<Player> roster)
        => TeamBalance.JoinSmallestTeam(joiner, roster, TeamCount);

    /// <summary>The key whose home team is <paramref name="team"/>, or null.</summary>
    public KeyState? KeyOf(int team) => Keys.TryGetValue(team, out var k) ? k : null;

    /// <summary>
    /// QC kh_Key_AssignTo (pickup/collect): <paramref name="player"/> picks up <paramref name="key"/>,
    /// becoming its carrier. Awards the collect SCORE to the player's team (QC kh_Scores_Event "collect").
    /// Then checks whether this completes an all-keys capture for the player's team.
    /// Returns the captured team color code if a capture just happened, else <see cref="Teams.None"/>.
    /// </summary>
    public int AssignKey(Player player, KeyState key)
    {
        if (MatchEnded || player.IsDead)
            return Teams.None;

        key.Carrier = player;
        key.DropTime = 0f;
        AddTeamScore(player.Team, (int)ScoreCollect);
        AddCol(player, "KH_PICKUPS", 1); // QC kh_Key_Collect: GameRules_scoring_add(player, KH_PICKUPS, 1)
        // Attach the world entity to the new carrier (QC kh_Key_Attach) when the entity layer is live.
        if (key.Entity is not null)
            GametypeEntities.AttachToCarrier(key.Entity, player, new Vector3(0f, 0f, 20f));

        return CheckCapture();
    }

    /// <summary>QC kh_Key_AssignTo(key, NULL) on drop/death: the key becomes loose (no carrier).</summary>
    public void DropKey(KeyState key) => key.Carrier = null;

    /// <summary>
    /// QC kh_Key_AllOwnedByWhichTeam → kh_FinishCapture: if every key is currently carried by a player and
    /// all those carriers are on the SAME team, that team scores a capture (SCORE += ScoreCapture, KH_CAPS
    /// +1) and the keys are reset (dropped). Returns the capturing team, or <see cref="Teams.None"/>.
    /// </summary>
    public int CheckCapture()
    {
        if (Keys.Count == 0)
            return Teams.None;

        int owner = Teams.None;
        foreach (var key in Keys.Values)
        {
            Player? c = key.Carrier;
            if (c is null || c.IsDead)
                return Teams.None; // a key is loose → no capture
            int t = (int)c.Team;
            if (owner == Teams.None)
                owner = t;
            else if (owner != t)
                return Teams.None; // keys split across teams → no capture
        }

        if (owner == Teams.None)
            return Teams.None;

        // ----- capture! (QC kh_FinishCapture) -----
        AddTeamScore(owner, (int)ScoreCapture);
        // QC GameRules_scoring_add_team(key.owner, KH_CAPS, 1): credit the capture column to a carrier on the
        // capturing team. Reset all keys to loose for the next round.
        CreditCapture(owner);
        foreach (var key in Keys.Values)
            key.Carrier = null;

        UpdateLeaderAndCheckLimit();
        return owner;
    }

    private bool OnDeath(ref DeathEvent ev)
    {
        if (ev.Victim is not Player victim)
            return false;
        if (MatchEnded)
            return false;

        Player? attacker = ev.Attacker as Player;

        // QC kh_HandleFrags / kh_Key_DropAll: killing an enemy key-carrier earns a carrier-frag bonus.
        bool victimCarried = false;
        foreach (var key in Keys.Values)
            if (ReferenceEquals(key.Carrier, victim)) { victimCarried = true; break; }
        if (victimCarried && attacker is not null && !ReferenceEquals(attacker, victim)
            && !Teams.SameTeam(attacker, victim))
        {
            attacker.ScoreFrags += (int)ScoreCarrierFrag; // QC kh_Scores_Event "carrierfrag"
            AddCol(attacker, "KH_KCKILLS", 1);            // QC GameRules_scoring_add(attacker, KH_KCKILLS, 1)
        }

        // The victim drops every key they were carrying (QC kh_Key_DropAll) — they become loose pickups.
        DropAllKeys(victim);

        // KH respawns normally; arm the respawn timer (QC calculate_respawntime).
        GametypeEntities.ScheduleRespawn(victim);
        return false;
    }

    /// <summary>QC <c>GameRules_scoring_add_team(player, SCORE, delta)</c>'s team side: add to a team's ST_SCORE
    /// total (GameScores team slot 0 — the team primary). No-op for the neutral team.</summary>
    public void AddTeamScore(float team, int delta)
    {
        int t = (int)team;
        if (t == Teams.None)
            return;
        Scoring.GameScores.AddToTeam(t, Scoring.GameScores.TeamSlotScore, delta);
    }

    public int GetTeamScore(int team) => Scoring.GameScores.TeamScore(team, Scoring.GameScores.TeamSlotScore);

    // ============================================================================================
    //  Controller + round loop (QC kh_Controller_Think / kh_WaitForPlayers / kh_StartRound)
    // ============================================================================================

    /// <summary>
    /// Advance the KeyHunt controller one step (QC kh_Controller_Think): wait until every team has a live
    /// player, then count a round down and spawn the keys; while a round runs, evaluate the capture geometry
    /// and auto-return any dropped key whose timer elapsed. Call each tick after <see cref="SetRoster"/>.
    /// </summary>
    public void Tick()
    {
        if (MatchEnded)
            return;
        float now = Api.Services is not null ? Api.Clock.Time : 0f;

        switch (Phase)
        {
            case RoundPhase.WaitingForPlayers:
                if (!AnyTeamMissing())
                {
                    Phase = RoundPhase.Countdown;
                    RoundStartTime = now + DelayRound; // QC CENTER_KEYHUNT_ROUNDSTART countdown
                }
                break;

            case RoundPhase.Countdown:
                if (AnyTeamMissing()) { Phase = RoundPhase.WaitingForPlayers; break; }
                if (now >= RoundStartTime)
                    StartRound();
                break;

            case RoundPhase.InProgress:
                TickKeys(now);
                break;
        }
    }

    /// <summary>QC kh_GetMissingTeams: true if any active team has no live, non-chatting player.</summary>
    public bool AnyTeamMissing()
    {
        foreach (int team in Teams.Active(TeamCount))
        {
            bool has = false;
            for (int i = 0; i < _roster.Count; i++)
            {
                Player p = _roster[i];
                if ((int)p.Team == team && !p.IsDead) { has = true; break; }
            }
            if (!has)
                return true;
        }
        return false;
    }

    /// <summary>
    /// QC kh_StartRound: spawn one key per active team, each initially assigned to a random live player on
    /// that team (and positioned on them). Transitions the controller to <see cref="RoundPhase.InProgress"/>.
    /// </summary>
    public void StartRound()
    {
        if (AnyTeamMissing())
        {
            Phase = RoundPhase.WaitingForPlayers;
            return;
        }
        // Clear any leftover keys from a prior round.
        foreach (var k in Keys.Values)
        {
            if (k.Entity is not null && Api.Services is not null)
                Api.Entities.Remove(k.Entity);
            k.Entity = null;
            k.Carrier = null;
        }

        foreach (int team in Teams.Active(TeamCount))
        {
            Player? owner = PickRandomLivePlayer(team);
            KeyState key = SpawnKey(team, owner);
            if (owner is not null)
                AssignKeyNoScore(owner, key); // QC kh_Key_Spawn → kh_Key_AssignTo(key, initial_owner)
        }
        Phase = RoundPhase.InProgress;
        Round.Number++;
    }

    /// <summary>Round bookkeeping (reused from the round-based modes for the round counter).</summary>
    public readonly RoundState Round = new();

    private Player? PickRandomLivePlayer(int team)
    {
        // QC reservoir pick: random()*players <= 1. Deterministic via the shared spawn RNG analogue.
        Player? chosen = null;
        int seen = 0;
        for (int i = 0; i < _roster.Count; i++)
        {
            Player p = _roster[i];
            if ((int)p.Team != team || p.IsDead)
                continue;
            seen++;
            if (XonoticGodot.Common.Math.Prandom.Float() * seen <= 1f)
                chosen = p;
        }
        return chosen;
    }

    // ============================================================================================
    //  Key ENTITY layer (QC kh_Key_Spawn / kh_Key_Touch / kh_Key_Think / kh_Key_DropAll)
    // ============================================================================================

    /// <summary>
    /// QC kh_Key_Spawn: create the world key entity for <paramref name="team"/> (touch = collect, think =
    /// drop/return/capture geometry). When no facade is wired the <see cref="KeyState"/> still tracks the
    /// carrier so the headless capture logic works.
    /// </summary>
    public KeyState SpawnKey(int team, Player? initialOwner)
    {
        if (!Keys.TryGetValue(team, out KeyState? key))
        {
            key = new KeyState(team);
            Keys[team] = key;
        }
        Vector3 origin = initialOwner?.Origin ?? Vector3.Zero;
        Entity? e = GametypeEntities.SpawnObjective("item_kh_key", origin, team, KeyMins, KeyMaxs,
            touch: KeyTouchEntity, think: KeyThinkEntity);
        if (e is not null)
        {
            e.TakeDamage = DamageMode.Yes; // a loose key can be returned by damage (QC)
            e.NextThink = GametypeEntities.Now + 0.05f; // QC kh_Key_Think rate
            SetKeyVisual(e, team, carried: false);       // QC kh_Key_Spawn: model + team color + glow + spin
        }
        key.Entity = e;
        key.Carrier = null;
        return key;
    }

    /// <summary>
    /// QC kh_Key_Spawn's presentation block: give the key entity its model, fullbright glow, team colormap and
    /// per-team netname so the server's networked-entity stream renders it. A dropped key slowly spins; a carried
    /// key rides the carrier (no spin, swapped to the carried model). The model/animation playback is the client's.
    /// </summary>
    public void SetKeyVisual(Entity e, int team, bool carried)
    {
        if (Api.Services is not null) Api.Entities.SetModel(e, KeyModel);
        else e.Model = KeyModel;
        e.Effects |= EffectFlags.FullBright;     // QC KH_KEY_BRIGHTNESS glow
        e.Skin = TeamKeySkin(team);
        e.Team = team;                           // colormap = team palette (Team_ColorRGB × brightness)
        e.NetName = TeamKeyName(team);
        e.AVelocity = carried ? Vector3.Zero : KeySpin; // a loose key spins; a carried one is fixed to the carrier
    }

    /// <summary>QC the per-team key skin (the key.md3 carries a skin per team color).</summary>
    private static float TeamKeySkin(int team) => team switch
    {
        Teams.Red => 0f, Teams.Blue => 1f, Teams.Yellow => 2f, Teams.Pink => 3f, _ => 0f,
    };

    /// <summary>QC the per-team colored key name ("^1red key" / "^4blue key" / …).</summary>
    private static string TeamKeyName(int team) => team switch
    {
        Teams.Red => "^1red key", Teams.Blue => "^4blue key",
        Teams.Yellow => "^3yellow key", Teams.Pink => "^6pink key", _ => "^7key",
    };

    /// <summary>The <see cref="KeyState"/> bound to a world key <see cref="Entity"/>, or null.</summary>
    public KeyState? KeyForEntity(Entity e)
    {
        foreach (var k in Keys.Values)
            if (ReferenceEquals(k.Entity, e)) return k;
        return null;
    }

    /// <summary>
    /// QC kh_Key_AssignTo at round start (no collect score): give a key to its initial owner and attach the
    /// world entity to them. Distinct from <see cref="AssignKey"/> which awards the enemy-collect score.
    /// </summary>
    public void AssignKeyNoScore(Player player, KeyState key)
    {
        key.Carrier = player;
        key.DropTime = 0f;
        if (key.Entity is not null)
            GametypeEntities.AttachToCarrier(key.Entity, player, new Vector3(0f, 0f, 20f));
    }

    /// <summary>
    /// QC kh_Key_Collect: a player picks up a loose key. If it isn't their own dropped key, award the collect
    /// score (handled by <see cref="AssignKey"/>); then attach it and re-check the capture. The dropper can't
    /// instantly re-collect their own key (delay_collect).
    /// </summary>
    public int CollectKey(Player player, KeyState key)
    {
        if (MatchEnded || player.IsDead || key.Carrier is not null)
            return Teams.None;
        // QC: a player can't re-collect a key they just dropped within delay_collect.
        if (ReferenceEquals(key.Dropper, player)
            && GametypeEntities.Now < key.DropTime + DelayCollect)
            return Teams.None;

        bool wasOwnDrop = key.DropperTeam == (int)player.Team;
        key.DropperTeam = Teams.None;
        key.Dropper = null;
        key.DropTime = 0f;
        key.Carrier = player;
        if (key.Entity is not null)
            GametypeEntities.AttachToCarrier(key.Entity, player, new Vector3(0f, 0f, 20f));

        // Only an enemy collecting earns the collect score (QC kh_dropperteam != player.team).
        if (!wasOwnDrop)
            AddTeamScore(player.Team, (int)ScoreCollect); // QC kh_Scores_Event "collect"
        AddCol(player, "KH_PICKUPS", 1); // QC kh_Key_Collect: GameRules_scoring_add(player, KH_PICKUPS, 1)

        // QC: the actual capture is decided by kh_Key_Think with the maxdist geometry, not at pickup time.
        return CheckCaptureGeometry();
    }

    /// <summary>Entity touch trampoline for a loose key: a live player collects it (QC kh_Key_Touch).</summary>
    private void KeyTouchEntity(Entity self, Entity other)
    {
        if (other is not Player p || p.IsDead)
            return;
        KeyState? key = KeyForEntity(self);
        if (key is not null && key.Carrier is null)
            CollectKey(p, key);
    }

    /// <summary>
    /// QC kh_Key_Think (per-key): a carried key keeps the carrier in sync and, if all keys are on one team,
    /// evaluates the in-range capture; a loose key auto-returns once its return timer elapses. Re-arms.
    /// </summary>
    private void KeyThinkEntity(Entity self)
    {
        self.NextThink = GametypeEntities.Now + 0.05f;
        KeyState? key = KeyForEntity(self);
        if (key is null)
            return;

        if (key.Carrier is null)
        {
            // dropped: auto-return after delay_return (QC pain_finished timeout → kh_LoserTeam → respawn)
            if (key.DropTime > 0f && GametypeEntities.Now > key.DropTime + DelayReturn)
                AutoReturnKey(key);
            return;
        }
        // carried: drive the capture geometry check (only one key needs to evaluate it).
        CheckCaptureGeometry();
    }

    private void TickKeys(float now)
    {
        // Headless think: auto-return loose keys, then evaluate the in-range capture.
        foreach (var key in Keys.Values)
            if (key.Carrier is null && key.DropTime > 0f && now > key.DropTime + DelayReturn)
                AutoReturnKey(key);
        CheckCaptureGeometry();
    }

    /// <summary>QC kh_Key_DropAll: a dying player drops every key they carry; each becomes a loose pickup.</summary>
    public void DropAllKeys(Player player)
    {
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        foreach (var key in Keys.Values)
        {
            if (!ReferenceEquals(key.Carrier, player))
                continue;
            key.Carrier = null;
            key.Dropper = player;
            key.DropperTeam = (int)player.Team; // QC: suicide marks the dropper team so it isn't a free collect
            key.DropTime = now;
            AddCol(player, "KH_LOSSES", 1); // QC kh_Key_DropAll: GameRules_scoring_add(player, KH_LOSSES, 1)
            if (key.Entity is Entity e)
            {
                GametypeEntities.DetachFromCarrier(e);
                e.Solid = Solid.Trigger;
                e.MoveType = MoveType.Toss;
                e.TakeDamage = DamageMode.Yes;
                GametypeEntities.SetOrigin(e, player.Origin);
                SetKeyVisual(e, key.HomeTeam, carried: false); // dropped: restore the spin + dropped model
            }
        }
    }

    /// <summary>QC kh_Key_Remove on return: the key vanishes from the field and re-spawns at next round.</summary>
    public void AutoReturnKey(KeyState key)
    {
        key.Carrier = null;
        key.Dropper = null;
        key.DropperTeam = Teams.None;
        key.DropTime = 0f;
        if (key.Entity is not null && Api.Services is not null)
            Api.Entities.Remove(key.Entity);
        key.Entity = null;
        // QC kh_LoserTeam ends the round when a key is lost off-map; here a returned key simply restarts the round.
        Phase = RoundPhase.WaitingForPlayers;
    }

    /// <summary>
    /// QC kh_Key_Think → kh_WinnerTeam: a capture requires every key carried by the SAME team AND all those
    /// carriers within <see cref="MaxDist"/> of each other. When satisfied, score the capture (distributed
    /// like QC DistributeEvenly) and start the next round. Returns the capturing team or <see cref="Teams.None"/>.
    /// </summary>
    public int CheckCaptureGeometry()
    {
        if (MatchEnded || Keys.Count == 0)
            return Teams.None;

        int owner = Teams.None;
        Player? anchor = null;
        foreach (var key in Keys.Values)
        {
            Player? c = key.Carrier;
            if (c is null || c.IsDead)
                return Teams.None; // a key is loose → no capture
            int t = (int)c.Team;
            if (owner == Teams.None) { owner = t; anchor = c; }
            else if (owner != t) return Teams.None; // split across teams
        }
        if (owner == Teams.None || anchor is null)
            return Teams.None;

        // QC maxdist: every carrier must be within maxdist of the anchor (the first key's carrier).
        float maxDist = MaxDist;
        if (maxDist > 0f)
        {
            foreach (var key in Keys.Values)
            {
                Player c = key.Carrier!;
                if (Vector3.Distance(c.Origin, anchor.Origin) > maxDist)
                    return Teams.None; // carriers too far apart → no capture yet
            }
        }

        // ----- capture! (QC kh_WinnerTeam) -----
        // DistributeEvenly: total = (teams-1)*score_capture spread over the keys; here we credit the team.
        int teams = TeamCount;
        int total = (teams - 1) * (int)ScoreCapture;
        AddTeamScore(owner, total > 0 ? total : (int)ScoreCapture);
        CreditCapture(owner); // QC GameRules_scoring_add_team(key.owner, KH_CAPS, 1)
        UpdateLeaderAndCheckLimit();

        // reset keys + arm the next round (QC kh_FinishRound)
        foreach (var key in Keys.Values)
        {
            key.Carrier = null;
            key.DropTime = 0f;
            if (key.Entity is not null && Api.Services is not null)
                Api.Entities.Remove(key.Entity);
            key.Entity = null;
        }
        Phase = RoundPhase.WaitingForPlayers;
        return owner;
    }

    // ============================================================================================
    //  HUD stat pack (QC kh_update_state → STAT(OBJECTIVE_STATUS))
    // ============================================================================================

    /// <summary>
    /// QC <c>kh_update_state</c> (sv_keyhunt.qc:126-148): pack one 5-bit slot per key at bits [5i..5i+4]
    /// (key i = the i-th team's key in red,blue,yellow,pink order — QC <c>key.count</c>). Slot values:
    /// 0 = no such key in play (also the whole state between rounds, when every key is removed);
    /// 30 = dropped (QC "no owner"); 31 = carried by <paramref name="viewer"/> (QC's per-recipient
    /// <c>STAT |= 31</c> self override — this makes the pack PERSONALIZED, so the server serializes it per
    /// peer); otherwise the CARRIER's team as (team index + 1), i.e. 2=red .. 5=pink.
    ///
    /// DELIBERATE wire deviation: QC writes the SVQC team code (<c>f = key.team</c>, the carrier's — 5/14/13/10)
    /// and the CSQC decode's −1 bridges to the NUM_TEAM codes (cl_keyhunt.qc:25); the port's panel decode
    /// (ModIconsPanel.DrawKeyhunt, the same <c>((s &gt;&gt; (5i)) &amp; 31) − 1</c> expression) expects 1..4 team
    /// INDICES, so we write index+1 — observationally identical icons, one index convention on the wire.
    /// </summary>
    public uint PackKeyState(Player? viewer)
    {
        if (Phase != RoundPhase.InProgress)
            return 0u; // between rounds kh_Key_Remove deletes every key → QC state 0

        uint s = 0;
        for (int i = 0; i < Teams.All.Length; i++)
        {
            if (!Keys.TryGetValue(Teams.All[i], out KeyState? key))
                continue; // no key for that team (slot 0) — only the first TeamCount teams have keys
            uint f;
            if (key.Carrier is { } c)
                f = ReferenceEquals(c, viewer) ? 31u : (uint)(TeamIndex((int)c.Team) + 1); // QC f = key.team (the CARRIER's)
            else if (key.Entity is not null)
                f = 30u; // QC: key exists with no owner → dropped
            else
                continue; // out of play (returned/removed) → slot 0
            s |= f << (5 * i);
        }
        return s;
    }

    /// <summary>Team color code → 1-based team index (1 red, 2 blue, 3 yellow, 4 pink; 0 unknown).</summary>
    private static int TeamIndex(int team) => team switch
    {
        Teams.Red => 1, Teams.Blue => 2, Teams.Yellow => 3, Teams.Pink => 4, _ => 0,
    };

    public void UpdateLeaderAndCheckLimit()
    {
        // QC: KH teams rank by the team primary slot ST_SCORE, then ST_KH_CAPS. LeaderTeam / SecondTeam read the
        // flag-aware two-slot compare from GameScores (the source of truth). The point-limit check uses ST_SCORE.
        int bestTeam = Scoring.GameScores.LeaderTeam();
        LeaderTeam = bestTeam;
        if (bestTeam == Teams.None)
            return;

        int bestScore = GetTeamScore(bestTeam);
        float pointLimit = PointLimit;
        if (pointLimit > 0f && bestScore >= pointLimit)
            MatchEnded = true;

        int secondTeam = Scoring.GameScores.SecondTeam();
        float leadLimit = LeadLimit;
        if (leadLimit > 0f && secondTeam != Teams.None && (bestScore - GetTeamScore(secondTeam)) >= leadLimit)
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
/// One Key Hunt key — the Godot-free essence of the QC key edict (one per team; .owner carrier,
/// .kh_dropperteam, the worldkey list). Tracks the key's home team, current carrier, drop bookkeeping, and
/// — when a facade is wired — the world <see cref="Entity"/> that physically represents it. The key model,
/// attachment tag, and CSQC networking remain client concerns.
/// </summary>
public sealed class KeyState
{
    /// <summary>The team this key belongs to (a <see cref="Teams"/> color code).</summary>
    public readonly int HomeTeam;

    /// <summary>The player currently carrying the key (QC key.owner), or null when loose.</summary>
    public Player? Carrier;

    /// <summary>The world entity representing this key (QC the item_kh_key edict), or null (headless).</summary>
    public Entity? Entity;

    /// <summary>The player who last dropped this key (QC key.enemy), gating quick re-collect.</summary>
    public Player? Dropper;

    /// <summary>The team that owns the drop (QC kh_dropperteam): a same-team collect earns no score.</summary>
    public int DropperTeam = Teams.None;

    /// <summary>Sim time the key was dropped (QC kh_droptime / pain_finished baseline) for the return timer.</summary>
    public float DropTime;

    public KeyState(int homeTeam) => HomeTeam = homeTeam;
}
