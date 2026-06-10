using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Scoring;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The player actor — the C# successor to a QuakeC client edict transmuted into <c>CLASS(Player)</c>
/// (server/client.qc <c>PutPlayerInServer</c>, common/state.qh PlayerState). In QC a "player" is just an
/// edict with <c>FL_CLIENT</c> set and a pile of flat fields; the entity-model (ADR-0007) promotes the
/// player-specific gameplay state onto this dedicated subclass while the engine-shared fields
/// (Origin/Velocity/Health/Frags/…) stay on <see cref="Entity"/>.
///
/// Scope for this phase: the fields the DM match loop needs — frag score (kept on the base
/// <see cref="Entity.Frags"/>, exposed here as <see cref="ScoreFrags"/>), the respawn timer, the owned-weapon
/// set, and the bot flag. NOTE (cross-boundary): the full PlayerState component — weapon view-entities
/// (client), input buttons (net/client input), and spectator bookkeeping (client) — is deferred to those layers.
/// </summary>
public sealed class Player : Entity
{
    /// <summary>QC FRAGS_PLAYER (common/constants.qh): a live player's <c>.frags</c> STATUS baseline (0).</summary>
    public const int FragsPlayer = 0;

    /// <summary>QC FRAGS_SPECTATOR (common/constants.qh): the sentinel <c>.frags</c> STATUS for a spectator.</summary>
    public const int FragsSpectator = -666;

    /// <summary>QC FRAGS_PLAYER_OUT_OF_GAME (-616): a player who is in-game but eliminated (LMS/CA/round wait).</summary>
    public const int FragsOutOfGame = -616;

    /// <summary>QC FRAGS_LMS_LOSER (-616 family): an LMS player out of lives.</summary>
    public const int FragsLmsLoser = -616;

    public Player()
    {
        ClassName = "player";
    }

    /// <summary>
    /// The player's match score — QC <c>GameRules_scoring_add(..., SCORE, f)</c>. Now backed by the real
    /// per-column score table (<see cref="GameScores"/>'s SP_SCORE column), NOT the engine <see cref="Entity.Frags"/>
    /// field (which reverts to its QC role as the player STATUS sentinel — see <see cref="FragsStatus"/>). The
    /// gametypes keep writing this property; it routes their ±1 into the networked scoreboard. Whole frags only.
    /// </summary>
    public int ScoreFrags
    {
        get => GameScores.Get(this, GameScores.Score);
        set => GameScores.SetPlayer(this, GameScores.Score, value);
    }

    /// <summary>
    /// QC <c>.frags</c> as the player STATUS sentinel (FRAGS_PLAYER / FRAGS_SPECTATOR / FRAGS_OUT_OF_GAME),
    /// backed by the engine <see cref="Entity.Frags"/> field. Reset to <see cref="FragsPlayer"/> on every
    /// (re)spawn (PutPlayerInServer); the running match score lives in <see cref="ScoreFrags"/>.
    /// </summary>
    public int FragsStatus
    {
        get => (int)Frags;
        set => Frags = value;
    }

    /// <summary>
    /// QC <c>.respawn_time</c> (server/client.qc): absolute sim time (<see cref="Services.IGameClock.Time"/>)
    /// at which this player becomes eligible to respawn. 0 means "not waiting to respawn" (alive, or never
    /// scheduled). Set when the player dies; consumed by the match loop. <c>new</c>: intentionally distinct
    /// from the vehicle-entity <c>Entity.RespawnTime</c> (VehicleCommon) — a player is never a vehicle.
    /// </summary>
    public new float RespawnTime;

    /// <summary>
    /// QC <c>STAT(WEAPONS, this)</c> — the owned-weapon set (a WEPSET bitfield in QC). Modeled here as a set
    /// of weapon NetNames ("blaster", "machinegun", …) so it reads against <see cref="Weapons"/> without a
    /// bit-index registry. Repopulated from the starting loadout on every spawn (QC <c>start_weapons</c>).
    /// </summary>
    public readonly HashSet<string> OwnedWeapons = new(StringComparer.Ordinal);

    /// <summary>QC IS_BOT_CLIENT(this) — true for an AI-controlled player (affects spawn restriction filtering).</summary>
    public bool IsBot;

    /// <summary>
    /// QC the bot's <c>skill</c> level (0..10), set when the bot is created. Feeds the skill-weighted team
    /// balance (Teamplay.SkillProvider) so a strong bot counts as "more" than a weak one. Meaningless for humans
    /// (they use a reference rating until real TrueSkill mu/variance ratings are modeled).
    /// </summary>
    public float BotSkill = 5f;

    /// <summary>QC <c>.maycheat</c>: a per-player override that always permits cheats regardless of <c>sv_cheats</c>.</summary>
    public bool MayCheat;

    /// <summary>
    /// QC <c>.winning</c>: set at match end for the player(s) who won (the scoreboard leader, or each member of
    /// the winning team). Read by the campaign win/lose check and the end-of-match flow. Cleared on (re)spawn.
    /// </summary>
    public bool Winning;

    /// <summary>
    /// QC <c>.crypto_idfp</c>: the player's stable persistent identity (the SessionAuth public-key fingerprint),
    /// set by the host on connect. "" for an anonymous/unauthenticated player — race records can't be stored for
    /// those (the ranking table is keyed by this UID). See <see cref="RaceRecords"/>.
    /// </summary>
    public string PersistentId = "";

    /// <summary>
    /// QC <c>.netaddress</c>: the client's remote address ("ip:port", or "local"/"bot" for a non-networked
    /// client), set by the host on connect. Used for IP banning, the mute/playban/voteban prefix lists, and
    /// the event log. "" / "bot" / "local" for a non-remote client. See the server ban + stats systems.
    /// </summary>
    public string NetAddress = "";

    /// <summary>
    /// QC <c>.playerid</c>: a per-match unique integer id for this client (distinct from the net id), used as
    /// the stable key in the event log and player-stats reports. Assigned by the host on connect (0 = unset).
    /// </summary>
    public int PlayerId;

    // ===== observer / join lifecycle (server/client.qc ClientConnect → TRANSMUTE(Observer) → Join) =====
    // In QC a connecting client is transmuted to an Observer edict and only becomes a live Player on Join
    // (on +jump/+attack, or a delayed autojoin). This port keeps a client as a Player subclass throughout
    // (ADR-0007) and models the Observer phase with these flags; the ClientManager drives the transition.

    /// <summary>
    /// QC the Observer phase after ClientConnect's <c>TRANSMUTE(Observer)</c>: the client is connected
    /// (FL_CLIENT) but is NOT yet a live, spawned player — it has no loadout and doesn't move/take damage/score.
    /// Cleared by <c>Join</c> (server/client.qc) when the player actually enters the match. A connecting human
    /// starts an observer; a bot auto-joins immediately so it never observes (ObserverOrSpectatorThink).
    /// </summary>
    public bool IsObserver;

    /// <summary>
    /// QC <c>.wants_join</c> (server/client.qc): the player's join intent while observing — 0 = none, &gt;0 = a
    /// chosen team index, -1 = autoselect. Pressing +jump/+attack as an observer sets this and gates
    /// <c>joinAllowed</c>; <c>Join</c> clears it. (Team selection UX is deferred; the core uses 0 = autoselect.)
    /// </summary>
    public int WantsJoin;

    /// <summary>
    /// QC <c>.autojoin_checked</c> (server/client.qc): the one-shot autojoin latch. A bot sets it to 1 and joins
    /// once in ObserverOrSpectatorThink; a real client's delayed autojoin (PlayerPreThink, after MIN_SPEC_TIME)
    /// sets it to 1 after trying, or -1 to keep retrying briefly. 0 = not yet attempted.
    /// </summary>
    public int AutoJoinChecked;

    /// <summary>
    /// QC FL_JUMPRELEASED as used by ObserverOrSpectatorThink: the observer-phase edge tracker so a HELD
    /// +jump/+attack fires Join only once (true once the key is released, re-armed for the next press). Distinct
    /// from the physics <see cref="EntFlags.JumpReleased"/> bit, which PlayerPhysics owns for live players.
    /// </summary>
    public bool JoinJumpReleased = true;

    /// <summary>QC IS_DEAD(this): the player is dying or already dead (<see cref="Entity.DeadState"/> != No).</summary>
    public bool IsDead => DeadState != DeadFlag.No;

    // ===== respawn state machine (server/client.qc PlayerThink dead branch + calculate_player_respawn_time) =====

    /// <summary>
    /// QC <c>.respawn_time_max</c>: the latest sim time the player may stay dead — once reached (and
    /// <see cref="RespawnFlag.Force"/> is set) the respawn is forced even without a button press. Set by
    /// <c>calculate_player_respawn_time</c>; 0 = unset.
    /// </summary>
    public float RespawnTimeMax;

    /// <summary>QC <c>.respawn_flags</c> (RESPAWN_FORCE/SILENT/DENY). Set on death, cleared on (re)spawn.</summary>
    public RespawnFlag RespawnFlags;

    /// <summary>QC <c>.respawn_countdown</c>: the next announcer number to count down from (-1 = no countdown).</summary>
    public int RespawnCountdown;

    /// <summary>
    /// The respawn timer the client should display (QC <c>STAT(RESPAWN_TIME)</c>): the absolute sim time the
    /// player becomes/became respawnable, NEGATED while <see cref="DeadFlag.Respawning"/> so the client can show
    /// "respawning" vs "press fire", and 0 while <see cref="RespawnFlag.Silent"/>. Networked in the owner snapshot.
    /// </summary>
    public float RespawnTimeStat;

    // ===== spectate / follow-cam (server/client.qc SetSpectatee / SpectateCopy / spectatee_status) =====

    /// <summary>
    /// QC <c>.enemy</c> while observing/spectating: the live player this observer is currently following
    /// (null = free-fly, not following anyone). Set by <c>SpectateNext</c>/<c>SpectatePrev</c>/<c>Spectate</c>.
    /// </summary>
    public Player? Spectatee;

    /// <summary>
    /// QC <c>spectatee_status</c> (server/client.qc:1904): 0 = a live player; this player's own net id =
    /// observing (free-fly, the client maps it to -1); another player's net id (&gt;0) = spectating that
    /// player (the client renders from their eyes). Networked in the owner snapshot (QC ClientData BIT(1)).
    /// </summary>
    public int SpectateeStatus;

    /// <summary>True once the player is dead and the respawn delay has elapsed (the match loop should respawn it).</summary>
    public bool IsAwaitingRespawn(float now) => IsDead && RespawnTime > 0f && now >= RespawnTime;

    /// <summary>Convenience: does this player currently own the given weapon (by NetName)?</summary>
    public bool HasWeapon(string weaponNetName) => OwnedWeapons.Contains(weaponNetName);
}
