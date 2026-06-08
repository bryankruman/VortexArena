using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Simulation;

namespace XonoticGodot.Server;

/// <summary>
/// The client roster + connect/spawn lifecycle — the Godot-free essence of the relevant slice of
/// server/client.qc (<c>ClientConnect</c> / <c>PutClientInServer</c> / <c>ClientDisconnect</c>) plus the
/// <c>Join</c> path that turns an observer into a player. It owns the live <see cref="Player"/> list,
/// registers each player with the engine entity table and the <see cref="Scores"/> table, assigns a team
/// via <see cref="Teamplay"/>, spawns through <see cref="SpawnSystem"/>, and fires the
/// <see cref="MutatorHooks.PlayerSpawn"/> hook chain — keeping the gametype's roster (the
/// <see cref="MatchController"/>) in sync.
///
/// QC connection actually transmutes an edict Observer→Player on join; this port models a client as a
/// <see cref="Player"/> from the start (the entity model promotes player state onto the subclass, ADR-0007),
/// with an <see cref="ClientInfo.IsConnected"/> flag standing in for FL_CLIENT and a bot flag standing in
/// for IS_BOT_CLIENT. Clients are registered into the server-side entity registry
/// (<see cref="ServerEntities"/>) on connect so <c>FindByClass("player")</c> / <c>FindInRadius</c> see them
/// (the entity-table fix), and each carries server-only per-player state (<see cref="PlayerStates"/>) for
/// the regen/drown/contents timers.
///
/// The deep plumbing (netcode handshakes, cvar stuffing, spectator/observer states, playerstats, bans,
/// handicap) is deferred.
/// </summary>
public sealed class ClientManager
{
    /// <summary>Per-client bookkeeping (QC the ClientState component on the edict).</summary>
    public sealed class ClientInfo
    {
        public Player Player { get; }
        public bool IsBot { get; }

        /// <summary>QC FL_CLIENT: the slot is occupied by a connected client.</summary>
        public bool IsConnected { get; internal set; } = true;

        /// <summary>QC <c>.jointime</c>: sim time the client joined as a player (0 while observing).</summary>
        public float JoinTime { get; internal set; }

        public ClientInfo(Player player, bool isBot)
        {
            Player = player;
            IsBot = isBot;
            player.IsBot = isBot;
        }
    }

    private readonly List<ClientInfo> _clients = new();
    private readonly List<Player> _players = new();          // dense Player view (matches SimulationLoop.Clients)
    private readonly SimulationLoop _sim;
    private readonly Scores _scores;
    private readonly Teamplay _teamplay;
    private readonly MatchController _match;

    /// <summary>The connected clients (alive or awaiting respawn).</summary>
    public IReadOnlyList<ClientInfo> Clients => _clients;

    /// <summary>The connected players (the <see cref="Player"/> view of <see cref="Clients"/>).</summary>
    public IReadOnlyList<Player> Players => _players;

    /// <summary>QC <c>player_count</c>: how many clients are connected.</summary>
    public int PlayerCount => _clients.Count;

    /// <summary>QC <c>currentbots</c>: how many connected clients are bots.</summary>
    public int BotCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < _clients.Count; i++)
                if (_clients[i].IsBot) n++;
            return n;
        }
    }

    /// <summary>How many connected clients are humans (not bots).</summary>
    public int HumanCount => _clients.Count - BotCount;

    public ClientManager(SimulationLoop sim, Scores scores, Teamplay teamplay, MatchController match)
    {
        _sim = sim;
        _scores = scores;
        _teamplay = teamplay;
        _match = match;
    }

    /// <summary>
    /// The server-side entity registry that makes clients visible to find/radius (the entity-table fix). Set
    /// by <see cref="GameWorld"/> at boot. When set, every connecting client is registered here so
    /// <c>FindByClass("player")</c> / <c>FindInRadius</c> return it (QC the client edict joins the entity list);
    /// when null, clients stay out of those queries (the legacy behavior).
    /// </summary>
    public ServerEntityService? ServerEntities { get; set; }

    /// <summary>Server-only per-player state table (regen/drown/contents timers). Set by <see cref="GameWorld"/>.</summary>
    public ServerPlayerStates? PlayerStates { get; set; }

    /// <summary>
    /// On a single-player listen server hosting a campaign level, let the local host auto-spawn instead of
    /// being held as an observer by <c>g_campaign</c>. The listen server is "host AND play" and already forces
    /// the auto-join for Create Game via <c>sv_spectate 0</c>; campaign adds <c>g_campaign 1</c> (needed for
    /// PreInit/limits/win detection), whose spectator-hold (QC PlayerPreThink) would otherwise leave the host
    /// spectating the loaded level. Set by the host only for the single-player campaign case.
    /// </summary>
    public bool CampaignHostAutojoin { get; set; }

    /// <summary>
    /// Fired after a client is registered (QC ClientConnect tail) — the host wires it to the §5 connect hooks
    /// (ban enforcement, the <c>:connect:</c>/<c>:join:</c> event-log lines, player-stats AddPlayer, anticheat
    /// init, timeout-allowance reset). Fires before the initial <see cref="Spawn"/>.
    /// </summary>
    public Action<Player>? OnClientConnect { get; set; }

    /// <summary>Fired after a successful (re)spawn (QC PutPlayerInServer tail) — alivetime start, view reset.</summary>
    public Action<Player>? OnClientSpawn { get; set; }

    /// <summary>Fired before a client is removed (QC ClientDisconnect head) — finalize stats, <c>:part:</c>, cleanup.</summary>
    public Action<Player>? OnClientDisconnect { get; set; }

    private int _nextPlayerId = 1;

    /// <summary>
    /// QC <c>ClientConnect</c> + <c>Join</c>, reduced: create the player edict, register it everywhere
    /// (entity table, sim client list, score table, gametype roster), assign a team for a team game, then
    /// spawn it via <see cref="Spawn"/> (which fires the PlayerSpawn hook). Returns the new client's info.
    /// <paramref name="isBot"/> sets the simple bot-vs-human flag (QC IS_BOT_CLIENT).
    /// </summary>
    public ClientInfo ClientConnect(bool isBot = false, string? netName = null)
    {
        // Build the player edict through the engine entity table so it gets a slot/index, then re-tag it as
        // a Player (the table's Spawn() yields a bare Entity; in this port a client IS a Player subclass, so
        // we construct the Player and register it into the same dense list the table manages).
        Player p = new() { NetName = netName ?? (isBot ? "bot" : "player") };
        p.PlayerId = _nextPlayerId++;        // QC .playerid: a per-match unique id for the event log / stats
        if (isBot) p.NetAddress = "bot";
        RegisterEntity(p);

        var info = new ClientInfo(p, isBot);
        _clients.Add(info);
        _players.Add(p);

        // engine sim: clients are simulated first each tick (SimulationLoop.Clients / ClientMove).
        p.Flags |= EntFlags.Client;
        if (!_sim.Clients.Contains(p))
            _sim.Clients.Add(p);

        // score row (QC PlayerScore_Attach).
        _scores.Register(p);

        // gametype roster (QC the player joins the active gametype's player set).
        _match.AddPlayer(p);

        // QC the gametype's GameRules_spawning_teams(...) call (sv_rules / each mode's init): tell the spawn
        // system whether this mode requests team-only spawnpoints, so SelectSpawnPoint filters spots to the
        // player's team. Idempotent — runs the first time the gametype is known.
        EnsureTeamSpawnsRequested();

        // team assignment for a team game (QC TeamBalance_JoinBestTeam); FFA leaves Team = None.
        if (_teamplay.IsTeamGame)
            _teamplay.AssignBestTeam(p, _players);

        info.JoinTime = Now;

        // QC ClientConnect ends by TRANSMUTE(Observer, this) (server/client.qc:1164): a connecting client is an
        // OBSERVER, not yet a live player — it only enters the match via Join() (on +jump/+attack, a delayed
        // autojoin, or a bot's auto-join). Mark the observer phase here; the loadout/spawn happen in Join().
        p.IsObserver = true;
        p.WantsJoin = 0;            // QC this.wants_join = 0 (no team chosen yet; 0 = autoselect on Join)
        p.AutoJoinChecked = 0;      // QC .autojoin_checked: not yet attempted
        p.JoinJumpReleased = true;  // re-armed so the first +jump/+attack press fires Join once
        // QC TRANSMUTE(Observer) leaves the client with the spectator .frags sentinel so IS_PLAYER-style checks
        // exclude it from the live roster; PutPlayerInServer resets it to FRAGS_PLAYER on Join.
        p.FragsStatus = Player.FragsSpectator;

        // An observer is connected (FL_CLIENT) but inert: it must not move, take damage, or be picked as a spawn
        // target until joined. Keep it non-solid + MOVETYPE_NONE (QC PutObserverInServer sets these) so the
        // per-tick physics step is a no-op on the un-spawned hull. (See the GameWorld OnClientMove note: the
        // canMove gate should also exclude observers — reported as the one external change.)
        p.MoveType = MoveType.None;
        p.Solid = Solid.Not;
        p.TakeDamage = DamageMode.No;

        // QC ClientConnect tail: the §5 connect hooks (ban enforce, event log, stats, anticheat, timeout).
        OnClientConnect?.Invoke(p);
        // a ban enforcement may have dropped the client already — don't spawn a removed client.
        if (!info.IsConnected)
            return info;

        // QC ObserverOrSpectatorThink: a bot auto-joins ONCE on its first observer think (it never spectates).
        // Mirror that here so a bot drops into the match immediately, exactly as before this lifecycle landed.
        // A human stays an observer until it presses fire / the delayed autojoin trips (ObserverOrSpectatorThink
        // / PlayerPreThink), both driven per-tick by the net layer (ServerNet) for real clients.
        if (isBot)
        {
            p.AutoJoinChecked = 1; // QC CS(this).autojoin_checked = 1
            Join(p);
        }

        return info;
    }

    /// <summary>
    /// QC <c>Join</c> (server/client.qc:2074): transmute an observing client into a live <see cref="Player"/> and
    /// put it in the server (the loadout/placement happens in <see cref="Spawn"/> → PutPlayerInServer). Clears the
    /// observer phase + join intent. Team selection / queue balancing is deferred; the core path autoselects the
    /// team (already assigned at connect for a team game) and spawns. Returns whether the spawn succeeded.
    /// </summary>
    public bool Join(Player p)
    {
        // QC Join: TRANSMUTE(Player, this) — leave the observer phase. Spawn() (PutPlayerInServer) resets MoveType
        // WALK / Solid SLIDEBOX / DAMAGE_AIM, so we only need to clear the observer marker + intent here.
        p.IsObserver = false;
        p.WantsJoin = 0;          // QC this.wants_join = 0
        p.JoinJumpReleased = true;

        bool spawned = Spawn(p); // QC PutClientInServer
        if (!spawned)
            // no spawnpoint yet (Spawn armed a retry); fall back to the observer phase so the player isn't a
            // half-live actor with no placement. The retry path will re-Join via the respawn driver.
            p.IsObserver = true;
        return spawned;
    }

    /// <summary>QC <c>MIN_SPEC_TIME</c> (server/client.qh:403): the minimum observer dwell before a join is allowed.</summary>
    public const float MinSpecTime = 1f;

    /// <summary>
    /// QC <c>joinAllowed</c> (server/client.qc:2258), reduced core: a client may join once it has observed for at
    /// least <see cref="MinSpecTime"/> and teams aren't admin-locked. The version-mismatch / forced-spectator /
    /// playban / g_maxping / queue-balance gates are deferred (those subsystems aren't modeled on this path).
    /// </summary>
    public bool JoinAllowed(Player p, float now)
    {
        ClientInfo? info = InfoOf(p);
        if (info is null) return false;
        if (now < info.JoinTime + MinSpecTime) // QC: time < jointime + MIN_SPEC_TIME
            return false;
        if (_teamplay.IsTeamGame && TeamsLocked) // QC: teamplay && lockteams
            return false;
        return true;
    }

    /// <summary>QC <c>lockteams</c>: admin team-lock; mirrored from <see cref="GameWorld.TeamsLocked"/> by the host.</summary>
    public bool TeamsLocked { get; set; }

    /// <summary>
    /// QC <c>ObserverOrSpectatorThink</c> + the delayed-autojoin slice of <c>PlayerPreThink</c>
    /// (server/client.qc:2501 / :2708) — the per-tick observer lifecycle for ONE client. Drives the
    /// join-on-fire gate and the real-client delayed autojoin; the net layer calls this each tick for every
    /// connected client (passing that client's current attack/jump buttons). A no-op once the client has joined
    /// (it's then a live player driven by the normal movement step). The +jump/+attack join and the delayed
    /// autojoin are NOT blocked by warmup/countdown (QC lets players join during warmup — a join during the
    /// pre-match countdown simply spawns the player frozen until game_starttime).
    /// </summary>
    /// <param name="p">The client to think for.</param>
    /// <param name="jumpHeld">QC PHYS_INPUT_BUTTON_JUMP this tick.</param>
    /// <param name="attackHeld">QC PHYS_INPUT_BUTTON_ATCK this tick.</param>
    public void ObserverOrSpectatorThink(Player p, bool jumpHeld, bool attackHeld)
    {
        ClientInfo? info = InfoOf(p);
        if (info is null || !info.IsConnected || !p.IsObserver)
            return;

        float now = Now;
        bool withinGrace = now < info.JoinTime + MinSpecTime; // QC: time < jointime + MIN_SPEC_TIME

        // QC ObserverOrSpectatorThink: the +jump/+attack JOIN edge. FL_JUMPRELEASED gates it so a HELD key fires
        // once. QC arms FL_SPAWNING on the press, then commits Join on release; we collapse that to "fire on the
        // rising edge once joinAllowed (or still inside the MIN_SPEC_TIME grace, matching QC's jointime window)".
        bool firePressed = jumpHeld || attackHeld;
        if (p.JoinJumpReleased)
        {
            if (firePressed && (JoinAllowed(p, now) || withinGrace))
            {
                p.JoinJumpReleased = false;
                if (JoinAllowed(p, now))
                {
                    Join(p);
                    return;
                }
                // inside the grace window: keep trying (QC autojoin_checked = -1) so the queued press lands.
                p.AutoJoinChecked = -1;
            }
        }
        else if (!firePressed)
        {
            p.JoinJumpReleased = true; // QC: re-arm once the key is released
        }

        // QC PlayerPreThink delayed autojoin (server/client.qc:2708): a REAL client that hasn't joined autojoins
        // once MIN_SPEC_TIME has elapsed, UNLESS sv_spectate / g_campaign hold it as a spectator. (g_maxping /
        // forced-team gates are deferred.) Bots autojoined at connect so this is the human path.
        if (p.IsBot || p.AutoJoinChecked > 0 || withinGrace)
            return;

        bool earlyJoinRequested = p.AutoJoinChecked < 0; // QC: a queued press during the grace window
        // QC holds an unjoined real client as a spectator while sv_spectate OR g_campaign is set; the listen
        // server clears the former (sv_spectate 0) and, for a single-player campaign, opts the host out of the
        // latter so it spawns straight into the level instead of spectating it (CampaignHostAutojoin).
        bool campaignHold = Api.Cvars.GetFloat("g_campaign") != 0f && !CampaignHostAutojoin;
        bool spectateOnly = Api.Services is not null
            && (Api.Cvars.GetFloat("sv_spectate") != 0f || campaignHold);
        // QC: autojoin unless held as a spectator (sv_spectate / g_campaign). Not gated on warmup (joinable).
        if (earlyJoinRequested || !spectateOnly)
        {
            p.AutoJoinChecked = 1; // QC CS(this).autojoin_checked = 1
            if (JoinAllowed(p, now))
            {
                Join(p);
                // Join can still fail (e.g. no usable spawn point yet) and re-set IsObserver — keep retrying
                // rather than latching off permanently with AutoJoinChecked == 1.
                if (p.IsObserver && !spectateOnly)
                    p.AutoJoinChecked = -1;
            }
            else if (!spectateOnly)
                p.AutoJoinChecked = -1; // QC: keep trying for MIN_SPEC_TIME (brief blockers)
        }
    }

    /// <summary>
    /// QC <c>GameRules_spawning_teams</c> wiring: tell <see cref="SpawnSystem"/> whether the active gametype
    /// requests team-only spawnpoints, derived from <see cref="GameType.RequestsTeamSpawns"/> (per-mode; the
    /// cvar-gated team modes read their <c>g_*_team_spawns</c> cvar). Runs once the gametype is known, and
    /// re-syncs if the gametype changes. A no-op when no gametype is set yet (the FFA-style default of 0 stands).
    /// </summary>
    private GameType? _teamSpawnsSyncedFor;
    private void EnsureTeamSpawnsRequested()
    {
        GameType? gt = _match.GameType;
        if (gt is null || ReferenceEquals(gt, _teamSpawnsSyncedFor))
            return;
        _teamSpawnsSyncedFor = gt;
        SpawnSystem.RequestTeamSpawns(gt.RequestsTeamSpawns);
    }

    /// <summary>
    /// QC <c>PutClientInServer</c> + the PlayerSpawn mutator event: pick a spawnpoint away from the living,
    /// place + load out the player (<see cref="SpawnSystem.PutPlayerInServer"/>), then fire
    /// <see cref="MutatorHooks.PlayerSpawn"/>. Used for both the first spawn and every respawn. On a map with
    /// no spawnpoints, arms a short retry (QC: retry once a second) and returns false.
    /// </summary>
    public bool Spawn(Player p)
    {
        // QC SelectSpawnPoint(this, false): the player's team (set at connect via TeamBalance_JoinBestTeam) drives
        // the team-spawn filter — SelectSpawnPoint derives teamcheck from p.Team + the have_team_spawns globals, so
        // a team player spawns on a spawnpoint tagged with their own team, while an FFA player uses any spot.
        // QC's normal (non-anypoint) pass runs Spawn_FilterOutBadSpots(..., targetcheck=TRUE) (spawnpoints.qc:419),
        // which applies the ACTIVE_ACTIVE spawnpoint gate — so pass targetCheck:true here (inert on stock maps where
        // every spot is active; needed once Onslaught control-point deactivation toggles spot.active).
        EnsureTeamSpawnsRequested();
        SpawnPoint? sp = SpawnSystem.SelectSpawnPoint(p, LivePlayers(), targetCheck: true);
        if (sp is null)
        {
            p.RespawnTime = Now + 1f; // QC: no spawnpoint yet, retry next second
            return false;
        }

        SpawnSystem.PutPlayerInServer(p, sp.Value);

        // [T35] QC PutClientInServer: this.flags = FL_CLIENT | FL_PICKUPITEMS. A spawned, live player can pick
        // up world items — Item_Touch's first gate (CanPickupItems) only passes for a flagged player, so an
        // observer/projectile never collects items. Set here on every spawn path; cleared on disconnect/observe.
        p.CanPickupItems = true;

        // A successful spawn means the client is now a LIVE player (QC TRANSMUTE(Player) in Join), so leave the
        // observer phase here too — this covers every spawn path (initial Join, the no-spawnpoint retry, and the
        // gametype respawn driver which calls Spawn directly), keeping the live/observer state consistent.
        p.IsObserver = false;

        // entity-table fix: keep the player's AbsMin/AbsMax current so radius queries hit it post-spawn.
        ServerEntities?.LinkPlayer(p);

        // reset the server-only per-player timers (QC PutPlayerInServer clears air/regen/contents state).
        PlayerStates?.Of(p).OnSpawn();

        // QC: MUTATOR_CALLHOOK(PlayerSpawn, spot, this) — fired after the shared spawn setup.
        var args = new MutatorHooks.PlayerSpawnArgs(p, sp.Value.Source);
        MutatorHooks.PlayerSpawn.Call(ref args);

        p.Winning = false;          // QC: a fresh spawn clears the end-of-match winner latch
        OnClientSpawn?.Invoke(p);   // §5 spawn hooks (alivetime start, anticheat view reset)

        return true;
    }

    /// <summary>
    /// QC <c>ClientDisconnect</c>: remove the client everywhere it was registered (gametype roster, score
    /// table, sim client list, entity table) and clear FL_CLIENT. Returns true if the client was present.
    /// </summary>
    public bool ClientDisconnect(Player p)
    {
        int idx = _clients.FindIndex(c => ReferenceEquals(c.Player, p));
        if (idx < 0)
            return false;

        ClientInfo info = _clients[idx];
        info.IsConnected = false;

        OnClientDisconnect?.Invoke(p); // §5 disconnect hooks (finalize stats, :part:, voter/timeout cleanup)

        _clients.RemoveAt(idx);
        _players.Remove(p);
        _sim.Clients.Remove(p);
        _match.RemovePlayer(p);
        _scores.Unregister(p);
        ServerEntities?.UnregisterPlayer(p); // drop from find/radius (QC the client edict leaves the list)
        PlayerStates?.Remove(p);             // drop the server-side per-player timers

        p.Flags &= ~EntFlags.Client; // QC: this.flags &= ~FL_CLIENT
        p.CanPickupItems = false;    // [T35] QC drops FL_PICKUPITEMS with FL_CLIENT — a gone client can't pick up
        if (Api.Services is not null)
            Api.Entities.Remove(p);
        return true;
    }

    /// <summary>Find a connected client's info by its player (null if not connected).</summary>
    public ClientInfo? InfoOf(Player p) => _clients.Find(c => ReferenceEquals(c.Player, p));

    // ----- helpers -----

    private readonly List<Player> _liveScratch = new();

    /// <summary>The currently-alive players (spawn selection keeps new spawns away from these).</summary>
    private IReadOnlyList<Player> LivePlayers()
    {
        _liveScratch.Clear();
        for (int i = 0; i < _players.Count; i++)
            if (!_players[i].IsDead)
                _liveScratch.Add(_players[i]);
        return _liveScratch;
    }

    /// <summary>Next client-entity index (negative space, distinct from the engine table's slots).</summary>
    private int _nextClientIndex = -1;

    /// <summary>
    /// Initialize a freshly-constructed client edict's engine state and register it for find/radius queries.
    /// The public <see cref="IEntityService"/> can only mint a bare <see cref="Entity"/>, not a
    /// <see cref="Player"/> subclass, so a client does not live in the engine table's dense list — it is
    /// simulated through the host-owned <see cref="SimulationLoop.Clients"/> list instead, and given a
    /// negative index so it never collides with a table slot.
    ///
    /// The entity-table fix: when a <see cref="ServerEntities"/> registry is wired, the player is registered
    /// there so <c>FindByClass("player")</c> / <c>FindInRadius</c> return it exactly like QuakeC's global
    /// entity list — so spawn selection, splash damage, item triggers and bot target discovery all see
    /// clients. (Spawn placement is still done by <see cref="Spawn"/> via SpawnSystem.)
    /// </summary>
    private void RegisterEntity(Player p)
    {
        p.Index = _nextClientIndex--;
        p.IsFreed = false;
        ServerEntities?.RegisterPlayer(p); // entity-table fix: clients become findable by class/radius
    }

    private static float Now => Api.Services is not null ? Api.Clock.Time : 0f;
}
