using System.Numerics;
using XonoticGodot.Common;
using XonoticGodot.Common.Config;
using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;

namespace XonoticGodot.Server;

/// <summary>
/// A parsed map-entity dictionary — the key/value pairs of one entity lump from the BSP/.ent file
/// (QC the fields the engine sets on an edict before calling its spawnfunc). Only the handful the server
/// core needs are first-class (<c>classname</c>/<c>origin</c>/<c>angles</c>); everything else is carried in
/// <see cref="Fields"/> for the spawnfunc to read. A host parses the map into a list of these and hands them
/// to the <see cref="GameWorld"/> constructor.
/// </summary>
public sealed class EntityDict
{
    public string ClassName = "";
    public Vector3 Origin;
    public Vector3 Angles;
    public readonly Dictionary<string, string> Fields = new(StringComparer.OrdinalIgnoreCase);

    public EntityDict() { }
    public EntityDict(string className, Vector3 origin = default, Vector3 angles = default)
    {
        ClassName = className;
        Origin = origin;
        Angles = angles;
    }
}

/// <summary>
/// The headless game-world orchestrator — the C# successor to the QuakeC SERVER CORE that ties everything
/// together (server/main.qc <c>StartFrame</c>, server/world.qc <c>worldspawn</c> / the spawn-from-map loop /
/// <c>CheckRules_World</c>, server/client.qc <c>PlayerPreThink</c>/<c>PlayerPostThink</c>). It boots the
/// gameplay systems, spawns the map's entities, activates the chosen gametype, and runs the fixed-tick
/// <see cref="SimulationLoop"/> — driving every player through the ported movement + damage + gametype each
/// frame, and tracking scores.
///
/// One process hosts one world (the ambient <see cref="Api.Services"/> is per-process). Construct with a
/// built <see cref="CollisionWorld"/> (and optionally the parsed map entities), call <see cref="Boot"/>
/// once, then call <see cref="Frame"/> with the real elapsed time each server frame.
///
/// Wired here:
///  - <see cref="GameInit.Boot"/> installs movement/damage/map-object systems and builds the registries;
///  - the map entities are spawned via <see cref="SpawnFuncs.TrySpawn"/> (func_door, trigger_*, item_*, …);
///  - the chosen gametype (<see cref="GameTypes.ByName"/>, default "dm") is activated and a
///    <see cref="MatchController"/> created;
///  - the per-tick frame fires <see cref="MutatorHooks.PlayerPreThink"/> and runs <see cref="Movement.Move"/>
///    for each client, then drives the gametype + round + intermission + scores.
///
/// Now wired here too: the SV_StartFrame/EndFrame server hook bus (<see cref="ServerHooks"/>), the
/// cvar-default registry (<see cref="Cvars.RegisterDefaults"/>), the console-command framework
/// (<see cref="Commands"/>), the warmup/ready-restart flow (<see cref="WarmupController"/>), the call-vote
/// system (<see cref="VoteController"/>), the deep PlayerPostThink (drown/regen/contents+fall damage/status
/// tick), and the player entity-table fix (<see cref="ServerServices"/>).
///
/// Deferred: cheat/anticheat/playerstats plumbing, redirection, campaign, and the network entity broadcast.
/// </summary>
public sealed class GameWorld
{
    /// <summary>The default gametype NetName when none is requested (QC fallback to deathmatch).</summary>
    public const string DefaultGameType = "dm";

    private readonly IReadOnlyList<EntityDict> _mapEntities;

    /// <summary>The static map geometry this world traces against (QC the BSP collision hull).</summary>
    public CollisionWorld Collision { get; }

    /// <summary>The engine services facade this world owns (entity table, traces, cvars, clock).</summary>
    public EngineServices Services { get; }

    /// <summary>The fixed-tick simulation loop (72 Hz). Drives StartFrame/ClientMove/EndFrame.</summary>
    public SimulationLoop Simulation { get; }

    /// <summary>The active gametype descriptor (QC the registered gametype singleton), set by <see cref="Boot"/>.</summary>
    public GameType? GameType { get; private set; }

    /// <summary>The match-loop glue (roster + respawn driver), created by <see cref="Boot"/>.</summary>
    public MatchController Match { get; private set; } = null!;

    /// <summary>The unified score table (per-player + per-team), subscribed to the obituary bus at boot.</summary>
    public Scores Scores { get; } = new();

    /// <summary>The team manager (assignment + balance + team-count), created by <see cref="Boot"/>.</summary>
    public Teamplay Teamplay { get; private set; } = null!;

    /// <summary>The client roster + connect/spawn lifecycle, created by <see cref="Boot"/>.</summary>
    public ClientManager Clients { get; private set; } = null!;

    /// <summary>
    /// [T39] The live bot population + brains (QC bot_serverframe / bot_fixcount / the per-bot havocbot think),
    /// created by <see cref="Boot"/>. Its <see cref="Bot.BotPopulation.ServerFrame"/> runs each tick from
    /// <see cref="OnStartFrame"/> (main.qc:372's slot), and <see cref="OnClientMove"/> sources each bot's
    /// per-tick input from it instead of the net <see cref="InputProvider"/> (the sys_phys_ai seam).
    /// </summary>
    public Bot.BotPopulation Bots { get; private set; } = null!;

    /// <summary>The round flow state machine (round-based modes); spawned lazily via <see cref="EnableRounds"/>.</summary>
    public RoundHandler? Rounds { get; private set; }

    /// <summary>
    /// Per-frame round-prep step (set by <see cref="ActivateGameType"/> for round-based modes): feed the live
    /// roster to the active gametype so the round handler's CheckTeams/CheckWinner predicates see it. Runs each
    /// frame right before <see cref="RoundHandler.Think"/>.
    /// </summary>
    private Action? _roundPrep;

    /// <summary>
    /// Per-frame round-sync step (set by <see cref="ActivateGameType"/>): after <see cref="RoundHandler.Think"/>
    /// advances the live handler, mirror its <see cref="RoundHandler.RoundEndTime"/>/<see cref="RoundHandler.RoundsPlayed"/>
    /// into the gametype's own handler so its CheckWinner/overtime reads stay in lockstep (QC round_handler_GetEndTime).
    /// </summary>
    private Action? _roundSync;

    /// <summary>
    /// QC <c>default_player_alpha</c>: the spawn alpha every player loadout starts from, seeded at worldspawn by
    /// <see cref="MutatorHooks.FireSetDefaultAlpha"/> (1 = opaque; Cloaked lowers it to g_balance_cloaked_alpha;
    /// Running Guns sets it to 0). Read by the per-spawn player loadout (SpawnSystem). Default 1 until Boot seeds it.
    /// </summary>
    public float DefaultPlayerAlpha { get; private set; } = 1f;

    /// <summary>QC <c>default_weapon_alpha</c>: the spawn alpha for the held weapon model (see <see cref="DefaultPlayerAlpha"/>).</summary>
    public float DefaultWeaponAlpha { get; private set; } = 1f;

    /// <summary>End-of-match intermission state (winner, scoreboard freeze, map-change timer).</summary>
    public Intermission Intermission { get; } = new();

    /// <summary>
    /// [T42] The global overtime / sudden-death win layer (QC the CheckRules_World overtime cascade). Holds the
    /// per-match checkrules state and the InitiateSuddenDeath/InitiateOvertime/GetWinningCode logic. Driven by
    /// <see cref="CheckRulesAndIntermission"/>. The sinks below route QC's <c>cvar_set("timelimit", ...)</c> and
    /// the CENTER_OVERTIME_* notifications into the live cvar store + NotificationSystem.
    /// </summary>
    public OverTimeManager OverTime { get; } = new()
    {
        SetTimeLimitCvar = newLimit => Cvars.Set("timelimit", newLimit),                 // QC cvar_set("timelimit", ...)
        OnOvertimeStarted = seconds =>                                                    // QC CENTER_OVERTIME_TIME (seconds)
            NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Center, "OVERTIME_TIME", seconds),
        OnSuddenDeathStarted = () =>                                                      // QC CENTER_OVERTIME_FRAG
            NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Center, "OVERTIME_FRAG"),
    };

    /// <summary>
    /// [T41] The server-side announcer driver (QC client/announcer.qc <c>Announcer_Time</c>). Fires the
    /// "5 minutes remain" / "1 minute remains" map-time announcements with the QC hysteresis latches. Ticked
    /// from <see cref="OnStartFrame"/>; its context sinks are wired in <see cref="Boot"/>. The pre-match /
    /// round-start countdown announcer is T40's job (<see cref="WarmupController.OnCountdownTick"/>).
    /// </summary>
    public AnnouncerController Announcer { get; } = new();

    /// <summary>
    /// The <c>kill</c> / team-change death countdown (QC server/clientkill.qc ClientKill_TeamChange +
    /// KillIndicator_Think): defers the suicide by <c>g_balance_kill_delay</c> seconds and drives the spoken
    /// kill-countdown announcer (ANNCE_NUM_KILL_n) + the CENTER_TEAMCHANGE_* countdown print. Context sinks wired
    /// in <see cref="Boot"/>; ticked once per frame from <see cref="OnStartFrame"/>.
    /// </summary>
    public KillCountdown KillCountdown { get; } = new();

    /// <summary>
    /// The warmup + ready-restart flow (QC warmup_stage / ReadyRestart). Drives the pre-match phase and the
    /// match restart countdown; created at <see cref="Boot"/>. <see cref="GameStartTime"/> is kept in sync.
    /// </summary>
    public WarmupController Warmup { get; } = new();

    /// <summary>The in-match call-vote system (QC server/command/vote.qc). Wired to <see cref="Commands"/>.</summary>
    public VoteController Voting { get; } = new();

    /// <summary>The timeout / timein pause system (QC the timeout slice of server/command/common.qc).</summary>
    public TimeoutController Timeout { get; } = new();

    /// <summary>The IP/identity ban subsystem (QC server/ipban.qc + command/banning.qc).</summary>
    public Bans Bans { get; } = new();

    /// <summary>The cheat system (QC server/cheats.qc): sv_cheats gating + god/noclip/give/etc.</summary>
    public Cheats Cheats { get; } = new();

    /// <summary>The statistical anticheat (QC server/anticheat.qc).</summary>
    public AntiCheat AntiCheat { get; } = new();

    /// <summary>The structured event log (QC server/gamelog.qc).</summary>
    public GameLog GameLog { get; } = new();

    /// <summary>The player-stats game report accumulator (QC common/playerstats.qc).</summary>
    public PlayerStats PlayerStats { get; } = new();

    /// <summary>The single-player campaign core (QC server/campaign.qc); active only when <c>g_campaign</c>.</summary>
    public Campaign Campaign { get; } = new();

    /// <summary>
    /// When set before <see cref="Boot"/>, boot in campaign mode at <see cref="CampaignIndex"/> — the host's
    /// menu selection of a campaign level. <see cref="Boot"/> turns this into <c>g_campaign 1</c> /
    /// <c>_campaign_name</c> / <c>_campaign_index</c> just before <see cref="Campaign"/>'s PreInit, reproducing
    /// QC <c>CampaignSetup</c> (which sets the same cvars via <c>localcmd</c> before the map change). It must be
    /// injected here rather than copied into the world's cvar store from outside, because that store is private
    /// and only exists once <see cref="Boot"/> has loaded the cfg tree (see the note in the listen-server host).
    /// </summary>
    public string? CampaignName { get; set; }

    /// <summary>The absolute campaign level index for <see cref="CampaignName"/> (QC <c>_campaign_index</c>).</summary>
    public int CampaignIndex { get; set; }

    /// <summary>Server-side demo recording control (engine <c>sv_autodemo</c>; host-driven).</summary>
    public DemoControl Demo { get; } = new();

    /// <summary>The end-of-match map ballot (QC server/mapvoting.qc).</summary>
    public MapVoting MapVote { get; } = new();

    /// <summary>The map rotation + next-map selection (QC the maplist machinery in server/intermission.qc).</summary>
    public MapRotation Rotation { get; } = new();

    /// <summary>The seamless-portal manager (QC lib/warpzone): map warpzones + Porto-weapon portals.</summary>
    public WarpzoneManager Warpzones { get; } = new();

    /// <summary>The current map name (set by the host before <see cref="Boot"/>); drives the rotation cursor.</summary>
    public string MapName { get; set; } = "";

    /// <summary>
    /// The map's background music track (QC cdtrack / worldspawn .music / worldspawn .noise). Populated by
    /// <see cref="ApplyWorldspawn"/> from the BSP entity lump, or set by the host from the mapinfo file before
    /// or after Boot. The client music player reads this as the lowest-priority (default) music source.
    /// </summary>
    public string CdTrack { get; set; } = "";

    /// <summary>QC <c>lockteams</c>: when set (team game), players can't join or switch teams (admin lock).</summary>
    public bool TeamsLocked { get => _teamsLocked; set { _teamsLocked = value; if (Clients != null) Clients.TeamsLocked = value; } }
    bool _teamsLocked;

    /// <summary>The console-command registry (QC server/command/). Created at <see cref="Boot"/>.</summary>
    public Commands Commands { get; private set; } = null!;

    /// <summary>[T38] QC the minigame session spine (common/minigames/sv_minigames.qc) — live minigame
    /// sessions + the player→session map, driven by the <c>minigame</c> command. Built in WireCommandsWarmupVoting.</summary>
    public XonoticGodot.Common.Gameplay.MinigameSessionManager Minigames { get; private set; } = null!;

    /// <summary>
    /// The server-side entity facade that makes clients visible to find/radius (the entity-table fix). Set
    /// at <see cref="Boot"/>; players are registered/unregistered here from <see cref="ClientManager"/>.
    /// </summary>
    public ServerServices ServerServices { get; private set; } = null!;

    /// <summary>Server-only per-player state (regen/drown/contents timers), keyed by player.</summary>
    public ServerPlayerStates PlayerStates { get; } = new();

    /// <summary>The map queued for after intermission (QC the gotomap target), or "" for the rotation default.</summary>
    public string QueuedNextMap { get; set; } = "";

    /// <summary>
    /// Optional config-file reader (a config path relative to the gamedir root → its text, or null if absent).
    /// When set before <see cref="Boot"/>, the world loads the real Xonotic <c>.cfg</c> tree
    /// (<see cref="ConfigLoader.LoadServerConfig"/>) over the hand-curated defaults, so the ~460 live cvar reads
    /// across gameplay get authentic balance/physics/gametype/mutator values. Left null on a host without mounted
    /// assets (a bare unit test), in which case only <see cref="Cvars.RegisterDefaults"/> seeds the store.
    /// </summary>
    public Func<string, string?>? ConfigReader { get; set; }

    /// <summary>Diagnostics from the last config load (null until <see cref="Boot"/> runs with a <see cref="ConfigReader"/>).</summary>
    public XonoticGodot.Common.Config.ConfigInterpreter? LoadedConfig { get; private set; }

    /// <summary>
    /// The map's inline <c>"*N"</c> brush models (from <see cref="BspCollisionBuilder.Build"/>), registered on
    /// the model catalog at <see cref="Boot"/> so moving SOLID_BSP entities (func_door/plat/…) resolve real
    /// bounds and clip against their actual brushes. Set by the host that built the collision from a BSP; empty
    /// for a non-BSP/test world (entities then fall back to the AABB box, as before).
    /// </summary>
    public IReadOnlyList<BspCollisionBuilder.Submodel> BrushModels { get; set; }
        = System.Array.Empty<BspCollisionBuilder.Submodel>();

    /// <summary>
    /// The map's compiled PVS (<c>new BspPvs(bsp)</c>), wired onto the trace service at <see cref="Boot"/> so
    /// <c>checkpvs</c> culls bot line-of-sight / sound / networking. Set by the host that loaded the BSP; null
    /// for a non-BSP/test world (every PVS query is then conservatively visible).
    /// </summary>
    public XonoticGodot.Formats.Bsp.BspPvs? Pvs { get; set; }

    /// <summary>
    /// The parsed map, used at <see cref="Boot"/> to attach each inline model's render surfaces for the
    /// <c>getsurface*</c> builtins (so a <c>trigger_warpzone</c> brush can auto-derive its portal plane, and
    /// surface/decal queries work). Set by the host that loaded the BSP; null for a non-BSP/test world.
    /// </summary>
    public XonoticGodot.Formats.Bsp.BspData? MapBsp { get; set; }

    /// <summary>Whether <see cref="Boot"/> has run.</summary>
    public bool Booted { get; private set; }

    /// <summary>
    /// True once the active gametype's win condition has tripped (QC the gametype's checkrules latch). Wired
    /// across every bundled gametype that exposes a <c>MatchEnded</c> latch; the round-based-only modes
    /// (e.g. Survival) report their state through the round handler instead.
    /// </summary>
    public bool MatchEnded => GameType switch
    {
        Deathmatch dm => dm.MatchEnded,
        Mayhem m => m.MatchEnded,
        TeamMayhem tm => tm.MatchEnded,
        Tdm tdm => tdm.MatchEnded,
        ClanArena ca => ca.MatchEnded,
        Ctf ctf => ctf.MatchEnded,
        Domination dom => dom.MatchEnded,
        KeyHunt kh => kh.MatchEnded,
        FreezeTag ft => ft.MatchEnded,
        Onslaught ons => ons.MatchEnded,
        TeamKeepaway tka => tka.MatchEnded,
        Nexball nb => nb.MatchEnded,
        Assault aslt => aslt.MatchEnded,
        Keepaway ka => ka.MatchEnded,
        Duel duel => duel.MatchEnded,
        LastManStanding lms => lms.MatchEnded,
        Invasion inv => inv.MatchEnded,
        Race race => race.MatchEnded,
        Cts cts => cts.MatchEnded,
        _ => false,
    };

    /// <summary>Current simulation time (QC <c>time</c>), proxied from the sim clock.</summary>
    public float Time => Simulation.Time;

    /// <summary>
    /// Absolute sim time the match begins (QC <c>game_starttime</c>) — before this, the world runs but the
    /// match is in pre-game. Default 0 (start immediately). The host can set a countdown by raising this.
    /// </summary>
    public float GameStartTime { get; set; }

    /// <summary>
    /// How many map entities the spawn loop successfully handed to a registered spawnfunc (QC the count of
    /// edicts that got a spawnfunc_CLASSNAME). Useful for a host sanity check after <see cref="Boot"/>.
    /// </summary>
    public int SpawnedEntityCount { get; private set; }

    /// <summary>Map entity dicts whose classname had no registered spawnfunc (QC SV_OnEntityNoSpawnFunction).</summary>
    public IReadOnlyList<string> UnhandledClasses => _unhandledClasses;
    private readonly List<string> _unhandledClasses = new();

    /// <summary>
    /// Per-player movement input source (QC the received move command / bot input). Defaults to zero input
    /// (players stand still) so the core is runnable with no net layer; the net/bot layer replaces this to
    /// feed real commands. Returns the input for a given client this tick.
    /// </summary>
    public Func<Player, IMovementInput> InputProvider { get; set; } = static _ => ZeroInput;

    /// <summary>
    /// Per-frame (variable-dt) mode: the per-command movement batch for this player THIS tick — one entry per
    /// client render frame, each carrying its own dt — or null for the legacy one-command-per-tick path. When
    /// non-null <see cref="OnClientMove"/> runs one <c>Movement.Move</c> per entry (DP's process-queued-client-
    /// moves); null → a single Move with the <see cref="InputProvider"/> command. Installed by the net layer;
    /// null on a host with no net layer (and for bots, which produce one command per tick).
    /// </summary>
    public Func<Player, IReadOnlyList<IMovementInput>?>? TickMovementBatch { get; set; }

    private static readonly MovementInput ZeroInput = new() { FrameTime = SimulationLoop.TicRate };

    /// <summary>
    /// True when this world runs on the process-wide SHARED cvar store (the menu/console/client store handed in
    /// by the listen-server host) instead of a private one — DP's single engine cvar table, so a setting changed
    /// anywhere is live in the match. The menu has already exec'd the full stock cfg tree into that store, so
    /// <see cref="Boot"/> must NOT re-load the tree (it would reset every server cvar to its cfg default and wipe
    /// the user's overrides). False (default) → a private per-world store Boot loads from the cfg tree itself
    /// (tests, the loopback path, and the sv_threaded host which keeps store isolation across threads).
    /// </summary>
    private readonly bool _usingSharedStore;

    /// <summary>
    /// Construct the world around a built collision world and (optionally) the parsed map entities. Does not
    /// boot — call <see cref="Boot"/>. When <paramref name="sharedCvars"/> is non-null the simulation runs on
    /// THAT store (the shared menu cvar store — DP's one engine table); otherwise it owns a fresh private store.
    /// <see cref="Boot"/> publishes the facade as the ambient and, for a private store only, loads the cfg tree.
    /// </summary>
    public GameWorld(CollisionWorld collision, IReadOnlyList<EntityDict>? mapEntities = null,
        CvarService? sharedCvars = null)
    {
        Collision = collision;
        _mapEntities = mapEntities ?? System.Array.Empty<EntityDict>();
        _usingSharedStore = sharedCvars is not null;
        Services = new EngineServices(collision, sharedCvars);
        Simulation = new SimulationLoop(Services, collision);
    }

    /// <summary>
    /// Boot the world (QC the worldspawn → gametype init → spawn-from-map sequence). Publishes the engine
    /// facade, installs the gameplay systems, spawns the map entities, activates the requested gametype
    /// (default <see cref="DefaultGameType"/>), creates the match/team/client managers, wires the frame
    /// callbacks, and subscribes the score table to the obituary bus. Idempotent.
    /// </summary>
    /// <param name="gameTypeName">The gametype NetName to run (e.g. "dm", "tdm", "ca"); falls back to "dm".</param>
    /// <param name="installAmbient">
    /// S5 (sv_threaded): whether this Boot LEAVES <see cref="Api.Services"/> set to this world's
    /// <see cref="ServerServices"/> as the PROCESS-WIDE ambient (the default, true — every caller today). The whole
    /// Boot body reads <see cref="Api"/>.Cvars/Services, so the ambient is installed for the DURATION of Boot
    /// regardless; the flag only decides whether the process-wide value is RESTORED to its prior value when Boot
    /// returns. A host that wants the main thread to keep a DIFFERENT ambient (the two-world prediction split)
    /// passes false; the lock-fallback host (one shared world) keeps the default true. Inert for every current
    /// caller (defaults to the old behavior: install and leave installed).
    /// </param>
    public void Boot(string? gameTypeName = null, bool installAmbient = true)
    {
        if (Booted)
            return;

        // S5: capture the prior process-wide ambient so we can restore it when installAmbient is false (the
        // two-world split keeps the main thread on its own facade). GameInit.Boot below unconditionally sets
        // Api.Services = ServerServices, which the rest of this Boot body REQUIRES (its Api.Cvars/Services reads),
        // so we always install during Boot and only optionally revert at the end. Inert when installAmbient is true.
        IEngineServices? priorAmbient = installAmbient ? null : Api.Services;

        // 1) publish the ambient facade and install the gameplay systems + registries (QC progs init).
        //    We publish a ServerServices wrapper (not the bare engine facade) so the find/radius builtins
        //    also see client edicts — the entity-table fix (QC the client list joins the global entity list).
        //    GameInit.Boot sets Api.Services itself, so we hand IT the wrapper (not the bare facade) to keep
        //    the player-visible entity service ambient.
        ServerServices = new ServerServices(Services);
        GameInit.Boot(ServerServices);              // Api.Services = ServerServices; + movement/damage/registries

        // Wire the Porto weapon's portal placement to the warpzone manager (QC the lib/warpzone portal subsystem):
        // each landed portal becomes a warpzone, the in/out pair linked two-way so a player can walk through.
        XonoticGodot.Common.Gameplay.Porto.PortalSpawner = r =>
            Warpzones.PlacePortoPortal(r.Origin, r.SurfaceNormal, r.IsInPortal, r.PortalId, r.Owner);
        // QC Portal_ClearWithID: when a combined cnt<0 porto shot soft-fails after already placing its in-portal,
        // tear that orphaned in-portal (+ any linked partner) back out so it doesn't outlive the failed shot.
        XonoticGodot.Common.Gameplay.Porto.PortalClearWithId = (owner, id) =>
            Warpzones.ClearPortoPortal(owner, id);

        // Bridge the (stateless) trigger_warpzone(/_position) spawnfuncs to THIS match's warpzone manager, so a
        // map's warpzone brushes register here; the planes are derived + the pairs linked in InitMapZones below.
        XonoticGodot.Common.Gameplay.WarpzoneSpawns.Sink = Warpzones.OnMapEntity;

        // 1b) seed the server cvar defaults so the many GetFloat(...) reads return sane values (QC autocvars).
        Cvars.RegisterDefaults();

        // 1c) if a config reader is wired (assets are mounted), load the REAL Xonotic cfg tree over the defaults
        //     (QC the engine exec'ing default.cfg → xonotic-server.cfg → balance/physics/gametypes/...). This
        //     replaces hardcoded baselines with authentic values for the many live cvar reads. Never fatal.
        //     ...but ONLY for a PRIVATE store. When this world runs on the SHARED store (listen-server host) the
        //     menu already exec'd the same tree into it at boot AND layered the user's saved/console overrides on
        //     top; re-loading here would reset every server cvar to its cfg default and silently wipe those
        //     overrides — the very thing the old two-store build's BackfillModified pass existed to undo, now moot
        //     because the store is never reloaded out from under the user.
        bool loadedTree = false;
        if (ConfigReader is not null && !_usingSharedStore)
        {
            LoadedConfig = ConfigLoader.LoadServerConfig(Api.Cvars, ConfigReader);
            loadedTree = true;
        }
        // Re-derive the cached weapon-balance block whenever the store holds authentic g_balance_* values: either
        // we just loaded the cfg tree, or we're on the shared store the menu already loaded it into. (A private
        // store with no ConfigReader — most tests — keeps the registration-time fallback balance, as before.)
        if (loadedTree || _usingSharedStore)
            Weapons.ConfigureAll();

        // QC autocvars are read live; our weapons cache their balance block in a struct (Weapon.Configure), so a
        // runtime change to a g_balance_* cvar (a console `set`, a script/cfg `exec`, or a ruleset vote) must
        // re-run ConfigureAll or the live match keeps firing with the old numbers. Watch this world's cvar store
        // and re-derive on the next tick (OnStartFrame), coalesced so a whole balance cfg — hundreds of sets —
        // costs one reconfigure, and so ConfigureAll runs with Api.Services pointing at THIS world's store.
        Services.CvarsImpl.Changed += OnServerCvarChanged;

        // 1d) register the map's inline "*N" brush models BEFORE spawning entities, so a func_door/plat's
        //     setmodel("*N") resolves real bounds and the SOLID_BSP trace clips against its real moving brushes
        //     (instead of an AABB). The builder hands these alongside the static CollisionWorld; the host wires
        //     them onto GameWorld.BrushModels (empty when collision came from a non-BSP source).
        if (BrushModels.Count > 0)
            BspCollisionBuilder.RegisterSubmodels(BrushModels, Services.ModelsImpl);
        // Attach inline-model render surfaces for the getsurface* builtins (warpzone brush auto-plane, decals).
        if (MapBsp is not null)
            BspSurfaceBuilder.BuildAndAttach(MapBsp, Services.ModelsImpl);
        if (Pvs is not null)
            Services.Pvs = Pvs;

        // 1d′) precache the default player model so a player/bot's setmodel resolves a stable, deterministic
        //      modelindex (QC server/player.qc precache_playermodels). Players network their model by NAME, so
        //      this is mainly belt-and-suspenders for any index-based read; the configured default
        //      (sv_defaultplayermodel → "models/player/erebus.iqm") is registered once here at boot.
        string defaultPlayerModel = Api.Cvars.GetString("sv_defaultplayermodel");
        if (string.IsNullOrEmpty(defaultPlayerModel))
            defaultPlayerModel = "models/player/erebus.iqm";
        Services.ModelsImpl.Register(defaultPlayerModel);

        // 1e) campaign: when in a campaign, configure the current level (gametype/bots/skill/limits/mutators)
        //     BEFORE the gametype is resolved, so the campaign file's gametype choice drives the boot
        //     (QC CampaignPreInit, called from worldspawn before InitGameplayMode).
        //     The host's menu picks a level via CampaignName/Index (QC CampaignSetup's `set g_campaign 1` +
        //     `_campaign_name` + `_campaign_index`); apply it now — after the cfg load above, before the check —
        //     since the world's cvar store is private and didn't exist to receive these from outside.
        if (!string.IsNullOrEmpty(CampaignName))
        {
            Cvars.Set("g_campaign", "1");
            Cvars.Set("_campaign_name", CampaignName);
            Cvars.Set("_campaign_index", CampaignIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (Cvars.Bool("g_campaign"))
        {
            if (ConfigReader is not null) Campaign.FileReader = ConfigReader;
            if (Campaign.PreInit() && !string.IsNullOrEmpty(Campaign.CurrentGametype))
                gameTypeName = Campaign.CurrentGametype;
        }

        // 2) resolve + activate the gametype (QC MapInfo_LoadedGametype → REGISTER_GAMETYPE singleton).
        GameType = ResolveGameType(gameTypeName);
        GameType?.OnInit();

        // [T52] wire the CTS gate the Q3-compat target_score / target_fragsFilter spawnfuncs read (QC g_cts):
        //       outside CTS both entities self-delete (quake3.qc:198,231). Unwired this defaults to false, so
        //       those entities would be deleted even on a CTS map — point it at the live gametype here.
        CompatRemaps.IsCtsActive = () => GameType is Cts;

        // QC the global `q3compat` int (server/compat/quake3.qh:3), set during worldspawn from
        // _MapInfo_FindArenaFile(mapname, ".arena"/".defi") (world.qc:964-965) — i.e. the existence of a sibling
        // .arena/.defi file in the map pack. We probe the same companion files through the map ConfigReader so a
        // Q3/Q3DF import flips q3compat on for the spawnfuncs that read it (func_plat defaults, plat_target_use, the
        // CompatRemaps target_print Q3DF spawnflag reading). Computed once and cached for this boot; a stock map (no
        // companion file, or no ConfigReader at all) leaves it false — matching QC's q3compat==0 default.
        bool q3compat = ConfigReader is not null && !string.IsNullOrEmpty(MapName)
            && (ConfigReader($"maps/{MapName}.arena") is not null || ConfigReader($"maps/{MapName}.defi") is not null);
        CompatRemaps.Q3CompatProvider = () => q3compat;
        // The items layer reads the same global q3compat flag (item-origin sits mid-bbox on Q3 maps, items.qc:1133).
        XonoticGodot.Common.Gameplay.StartItem.Q3CompatProvider = () => q3compat;

        // 3) match-loop glue + team manager + client roster.
        Match = new MatchController();
        Teamplay = new Teamplay(GameType?.TeamGame ?? false, TeamCountFor(GameType), Scores);
        // Feed real per-bot skills into the skill-weighted team balance (QC m_skill_mu): a stronger bot counts
        // as "more" than a weaker one. Humans use the flat reference rating (no TrueSkill ratings modeled).
        Teamplay.SkillProvider = p => p.IsBot ? p.BotSkill : 5f;
        Clients = new ClientManager(Simulation, Scores, Teamplay, Match) { ServerEntities = ServerServices.ServerEntities, PlayerStates = PlayerStates, TeamsLocked = TeamsLocked };

        // [T39] the bot population (QC bot_serverframe): brains are created for EVERY bot connect path via the
        // ClientManager hook (fixcount fill, console bot_add, a host's direct ClientConnect(isBot:true)).
        Bots = new Bot.BotPopulation(this);
        Clients.OnBotConnected = Bots.RegisterBot;

        // 4) subscribe the unified score table to the obituary bus. The bundled gametypes are the
        //    authoritative frag-scorers (they write Player.ScoreFrags + their own team totals), so the table
        //    runs in READ-THROUGH mode (ownsScore: false) and only records the aux columns
        //    (kills/deaths/suicides/teamkills) — no double-counting. For team modes it reads team totals
        //    through the gametype via TeamScoreSource (wired just below in ActivateGameType).
        Scores.SubscribeToDeaths(GameType?.TeamGame ?? false, ownsScore: false);

        // 5) activate the gametype's own scoring/round handler (FFA: Deathmatch; team: TDM/CA/...).
        // Clear any waypoint sprites from a previous map (QC the level-change reset of the WaypointSprite list).
        XonoticGodot.Common.Gameplay.Waypoints.WaypointSprites.Reset();
        ActivateGameType();

        // 5a′) bridge the gametype OBJECTIVE spawnfuncs (item_flag_*, dom_controlpoint, trigger_race_*,
        //      onslaught_*) to the now-active gametype, so the BSP entity lump (step 6) registers flags/points/
        //      checkpoints/generators on the right mode (QC each spawnfunc dispatches to its gametype). No-op for
        //      a non-objective gametype.
        WireObjectiveSpawns();

        // 5b) activate the enabled mutators (QC STATIC_INIT_LATE(Mutators): FOREACH(Mutators, enabled, Mutator_Add)).
        //     Each registered mutator's IsEnabled cvar (g_instagib/g_dodging/…) is now meaningful — the defaults
        //     are seeded and the real cfg is loaded — so subscribe every enabled mutator's hooks to the chains
        //     BEFORE the map's entities spawn (a mutator may filter items via the spawn/loadout hooks). Without
        //     this loop the mutators are registered but inert: their Hook() is never called and none of their
        //     handlers run. Idempotent (MutatorBase.Added guard), so a later ruleset change can re-converge.
        MutatorActivation.Apply();

        // 5b′) QC worldspawn SetDefaultAlpha(): seed default_player_alpha / default_weapon_alpha now that the
        // enabled mutators are subscribed (Cloaked lowers the player alpha to g_balance_cloaked_alpha 0.25;
        // Running Guns makes the player invisible but the gun visible). The per-spawn player loadout reads
        // DefaultPlayerAlpha (see SpawnSystem) so cloaked/running-guns invisibility composes from this seed.
        (DefaultPlayerAlpha, DefaultWeaponAlpha) = MutatorHooks.FireSetDefaultAlpha(1f, 1f);

        // 5c) [T35] wire the world-item pipeline seams BEFORE the map entities spawn:
        //  - StartItem.GameStartTimeProvider feeds the powerup/superweapon initial-respawn offset (QC
        //    game_starttime) so powerups don't spawn during the countdown.
        //  - TargetUtilities.GiveItemHandler routes target_give's "hand item N to the player" through the
        //    canonical Item_GiveTo (QC ITEM_HANDLE(Pickup,…)); the SUB_UseTargets tail stays in TargetUtilities.
        // (CanPickupItems is tagged on each (re)spawn in ClientManager.Spawn; buff pickups self-spawn via the
        //  BuffsMutator hook in MutatorActivation.Apply above — no extra wiring needed here.)
        XonoticGodot.Common.Gameplay.StartItem.GameStartTimeProvider = () => GameStartTime;
        XonoticGodot.Common.Gameplay.TargetUtilities.GiveItemHandler = (worldItem, actor) =>
        {
            // QC target/give.qc:16-19: on a successful give, play the item's pickup sound on the actor. The
            // Item_Touch path plays it in its OWN tail, so this is ONLY for the target_give route (don't double-play).
            bool gave = XonoticGodot.Common.Gameplay.ItemPickupRules.ItemGiveTo(worldItem, actor);
            if (gave) XonoticGodot.Common.Gameplay.ItemPickupRules.PlayPickupSound(worldItem, actor);
            return gave;
        };
        // QC autocvar_g_campaign: the campaign master switch, read live. Wires the TargetUtilities.IsCampaign seam
        // that target_levelwarp / target_changelevel and Assault's WinningCondition_Assault (the campaign single-
        // round end in DestroyFinalObjective) consult. Assigned here (every Boot) so it never goes stale across a
        // map/gametype change. Was declared but never assigned, leaving the campaign branches dead.
        XonoticGodot.Common.Gameplay.TargetUtilities.IsCampaign = () => Cvars.Bool("g_campaign");

        // QC target_changelevel / target_levelwarp host seams (common/mapobjects/target/{changelevel,levelwarp}.qc).
        // The Common layer can't reach the server's match-end / campaign / changelevel plumbing, so it fires these
        // delegates. Wired here (every Boot) so they never go stale across a map/gametype change; previously null,
        // leaving the entities inert.
        //
        // empty-chmap target_changelevel → QC NextLevel(): end the match now. QC also flags campaign_forcewin when a
        // REAL client triggered it (IS_REAL_CLIENT && g_campaign) so the stage counts as beaten — set Campaign.ForceWin
        // before EndMatch so the campaign win/lose decision (CampaignPreIntermission, fired in NextLevel) credits it.
        XonoticGodot.Common.Gameplay.TargetUtilities.NextLevelHandler = actor =>
        {
            if (Cvars.Bool("g_campaign") && actor is Player rp && !rp.IsBot)
                Campaign.ForceWin = true; // QC: this counts as beating the map in a campaign stage
            EndMatch();
        };
        // named-chmap target_changelevel → QC changelevel(chmap): switch to that map. Route through the same
        // host changelevel pipeline the console `map`/`changelevel` command uses (resolved lazily — Commands is
        // built at the tail of Boot).
        XonoticGodot.Common.Gameplay.TargetUtilities.ChangeLevelHandler = chmap => Commands?.ChangeLevelHandler?.Invoke(chmap);
        // CHANGELEVEL_MULTIPLAYER fraction (QC FOREACH_CLIENT IS_PLAYER && IS_REAL_CLIENT, counting chlevel_targ):
        // supply the real-player count + how many voted for THIS changelevel target from the live roster.
        XonoticGodot.Common.Gameplay.TargetUtilities.RealPlayerVoteCount = target =>
        {
            int real = 0, voted = 0;
            foreach (Player p in Clients.Players)
            {
                if (p.IsBot || p.IsObserver) continue; // QC IS_PLAYER (joined, incl. dead) && IS_REAL_CLIENT (not bot)
                real++;
                if (ReferenceEquals(p.ChLevelTarg, target)) voted++;
            }
            return (real, voted);
        };
        // QC MapInfo_SwitchGameType(MapInfo_Type_FromString(this.gametype)): set the live `gametype` cvar so the
        // next changelevel boots that mode (the port's switch equivalent — see Commands.CmdGameType).
        XonoticGodot.Common.Gameplay.TargetUtilities.SwitchGameTypeHandler = gt => Cvars.Set("gametype", gt);
        // QC target_levelwarp_use → CampaignLevelWarp(n): jump to a campaign level (n>=0 specific, -1 next).
        XonoticGodot.Common.Gameplay.TargetUtilities.CampaignLevelWarpHandler = n => Campaign.LevelWarp(n);

        // [A2-review F13] Zero the process-global monster counters BEFORE the entity lump spawns any monster
        // (QC monsters_total/monsters_killed are server globals cleared when a new map spawns). Without this the
        // scoreboard map-stats row accumulates across every Invasion map played in one process run.
        XonoticGodot.Common.Gameplay.MonsterAI.ResetCounters();

        // 6) spawn the map's entities (QC the BSP entity-lump loop → spawnfunc_CLASSNAME).
        SpawnMapEntities();

        // 6a′) [T36] post-spawn pass once the whole BSP entity lump has spawned (QC the INITPRIO_LINKDOORS /
        //      INITPRIO_FINDTARGET deferred-init batch that DP runs after every spawnfunc). This was previously
        //      MISSING from the server boot (only GameDemo called it), so func-door double/quad links never wired
        //      headlessly and Assault's objective chain could not link across arbitrary spawn order:
        //   - MapObjectsRegistry.RunPostSpawn(): link double/quad doors into their owner/enemy groups.
        //   - Assault.ResolveObjectiveGraph(): resolve the staged decreaser→objective + destructible→decreaser
        //     links by name (QC assault_setenemytoobjective) and arm the first objective (QC roundstart).
        MapObjectsRegistry.RunPostSpawn();
        if (GameType is Assault assaultMode)
        {
            assaultMode.ResolveObjectiveGraph();
            // QC target_assault_roundstart → assault_roundstart_use_this at INITPRIO_FINDTARGET (sv_assault.qc:393):
            // once every turret has spawned, run the roundstart turret pass (seed unteamed turrets to the attacker
            // team, then swap NUM_TEAM_1↔2 + respawn). For round 1 the attacker is Red, so defending turrets end up
            // Blue (the defender side), matching Base.
            AssaultTurretRoundstart(assaultMode.AttackerTeam);
        }
        // QC ons_DelayedLinkSetup (INITPRIO_FINDTARGET): now that every onslaught_generator/controlpoint has
        // spawned + name-indexed its graph node, resolve the staged onslaught_link name pairs into power-graph
        // edges and propagate power (UpdateLinks). Without this the graph is edgeless and nothing ever unshields.
        if (GameType is Onslaught onsMode)
            onsMode.ResolveLinks();

        // QC ka_SpawnBalls (sv_keepaway.qc) — the single world ball is spawned PROCEDURALLY at map start (unlike
        // Nexball, which places balls from map entities). SpawnBall had no caller, so no ball ever existed and no
        // (Team)Keepaway scoring could fire. Spawn it now that the spawnpoints exist (RandomMapLocation samples them),
        // so the host-side ball lifecycle (touch=pickup, think=relocate) is live.
        switch (GameType)
        {
            case Keepaway ka:
                ka.SpawnBall(XonoticGodot.Common.Gameplay.BallEntity.RandomMapLocation(System.Numerics.Vector3.Zero));
                break;
            case TeamKeepaway tka:
                tka.SpawnBall(XonoticGodot.Common.Gameplay.BallEntity.RandomMapLocation(System.Numerics.Vector3.Zero));
                break;
        }

        // 6a) finalize warpzones now that every entity exists (QC WarpZone_StartFrame, deferred to frame 1):
        //     derive each trigger_warpzone brush's plane from its geometry (+ any trigger_warpzone_position
        //     orientation) and link the pairs. No-op when the map has no warpzones.
        Warpzones.InitMapZones();

        // 6a″) [T45] wire this match's warpzone manager onto the trace service (QC global g_warpzones) so
        //       hitscan/projectile traces and radius-damage queries recurse through linked portals — a rocket or
        //       bullet fired at one portal mouth continues out of the linked portal and damages a far-side target
        //       (lib/warpzone/common.qc WarpZone_TraceBox/_FindRadius). No-op on a map with no warpzones.
        Services.TraceImpl.SetWarpzoneManager(Warpzones);

        // 6b) campaign: apply the per-level frag/time limits now the gametype + map are validated
        //     (QC CampaignPostInit, called later in worldspawn).
        if (Cvars.Bool("g_campaign") && !Campaign.Aborted)
            Campaign.PostInit();

        // 7) wire the fixed-tick frame callbacks (QC StartFrame / per-client PreThink-move-PostThink / world end).
        Simulation.StartFrame = OnStartFrame;
        Simulation.ClientMove = OnClientMove;
        Simulation.EndFrame = OnEndFrame;

        // 8) console commands + warmup + voting (QC server/command/ + warmup_stage + the vote bus).
        WireCommandsWarmupVoting();

        // [T56] Precompute the common-command reply caches at world init (QC server/world.qc:1022-1038:
        // maplist_reply/lsmaps_reply/monsterlist_reply/…). The reply commands also recompute lazily on read,
        // so this is a faithful warm-up matching QC's "precompute once".
        Commands.Replies.Recompute();

        // S5: the two-world prediction split wants the MAIN thread's ambient left untouched — restore the value
        // we captured before GameInit.Boot clobbered it. No-op for every current caller (installAmbient defaults
        // to true → priorAmbient is null → Api.Services stays this world's ServerServices, as today). The
        // server-sim worker installs ServerServices via Api.SetThreadServices on its own thread instead.
        if (!installAmbient)
            Api.Services = priorAmbient!;

        Booted = true;
    }

    /// <summary>
    /// Wire the console-command framework, the warmup/ready-restart flow and the call-vote system together
    /// (QC the server command bus + ReadyRestart + the vote command family). Called at the end of
    /// <see cref="Boot"/>; afterwards a host feeds console/rcon input through <see cref="Commands"/> and the
    /// match honors warmup + votes.
    /// </summary>
    private void WireCommandsWarmupVoting()
    {
        Commands = new Commands(this);

        // [A5 #8] Resolve each frag/typefrag centerprint's MSG_CHOICE option (terse A / verbose B) from the
        // RECIPIENT's own replicated notification_CHOICE_* preference (QC CS(recipient).msg_choice_choices). The
        // per-client selections land in Commands via the sentcvar receive leg; point the obituary dispatch at them.
        Scores.ChoiceStateProvider = Commands.GetChoiceState;

        // [T38] QC sv_minigames.qc: the minigame session manager (create/join/part/end). The `minigame` command
        // (Commands.cs) reads this. Cvar gates use the shared facade; observer-forcing is left unwired for P0
        // (sv_minigames_observer ships 0 = don't force, so it's a no-op; the spectator hooks are T44's domain).
        Minigames = new XonoticGodot.Common.Gameplay.MinigameSessionManager(Cvars.Bool, Cvars.Int);
        // [T38] wire the host-side play-ban membership check (g_playban_list lives in the server layer); the
        // g_playban_minigames cvar gate is applied inside the manager. Ships 0 → no-op by default.
        Minigames.PlayBanned = p => Bans.PlayerInList(p, "g_playban_list");

        // Warmup: the ready-majority is computed over the live roster, and a restart re-runs reset_map.
        Warmup.Roster = () => Clients.Players;
        Warmup.ResetMap = ResetMap;
        Warmup.OnCountdownTick = BroadcastGameStartCountdown; // [T40] game-start countdown announcer/center

        // QC readlevelcvars/ReadyCount seams that were coded-but-dead: wire them to the live host state so the
        // minplayers/badteams countdown-abort, the campaign 3s countdown, the teamplay_lockonrestart team-lock,
        // the g_warmup<0 map-minplayers lower bound, and the timeout ready-count guard all actually fire.
        // - CampaignActive (autocvar_g_campaign): read live, like TargetUtilities.IsCampaign (GameWorld.cs:563).
        Warmup.CampaignActive = Cvars.Bool("g_campaign");
        // - map_minplayers (world.qc:697): the worldspawn-resolved map minimum, consulted when g_warmup<=1.
        Warmup.MapMinPlayers = () => Cvars.Int("map_minplayers");
        // - badteams (ReadyCount): teams unbalanced by >= sv_teamnagger; the size compute lives in Teamplay.
        Warmup.BadTeams = () => Teamplay.TeamsUnbalancedForNag(Clients.Players);
        // - timeout_status (ReadyCount top): cannot reset the game while a timeout is active/pending.
        Warmup.TimeoutActive = () => Timeout.Active;
        // - teamplay_lockonrestart (ReadyRestart_force): lockteams = !warmup_stage on each restart.
        Warmup.OnLockTeams = locked => TeamsLocked = locked;
        // - COUNTDOWN_STOP_MINPLAYERS / COUNTDOWN_STOP_BADTEAMS (ReadyCount abort): broadcast on abort-to-warmup.
        Warmup.OnCountdownStop = arg =>
        {
            // QC Announcer_Gamestart (announcer.qc:137): when the game-start countdown aborts back to warmup, the
            // CPID_ROUND centerprint group is retracted with centerprint_Kill(CPID_ROUND) and the title cleared
            // (Announcer_ClearTitle). The COUNTDOWN_STOP notifications below live under CPID_MISSING_PLAYERS, so
            // they do NOT replace the running countdown line — without this kill the "Game starts in ^COUNT" line
            // would hang until its own timer.
            NotificationSystem.SendCenterKill(NotifBroadcast.All, null, "CPID_ROUND");
            if (_gametypeTitleShown || _duelTitleLeft != "" || _duelTitleRight != "")
            {
                NotificationSystem.SendCenterTitle(NotifBroadcast.All, null, "");
                _gametypeTitleShown = false;
                _duelTitleLeft = _duelTitleRight = "";
            }
            if (arg >= 0) // minplayers: arg = the required minimum count (the QC f1 argument)
                NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Multi, "COUNTDOWN_STOP_MINPLAYERS", arg);
            else // -1 = bad teams (no argument)
                NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Multi, "COUNTDOWN_STOP_BADTEAMS");
        };

        // [T41] wire the time-remaining announcer's context to the live match state (QC Announcer_Time reads
        // STAT(GAMESTARTTIME)/warmup_stage/STAT(WARMUP_TIMELIMIT)/STAT(TIMELIMIT)/intermission + the per-client
        // cl_announcer_maptime; the port broadcasts globally off the server config value, default 3 = both).
        Announcer.Now = () => Time;
        Announcer.GameStartTime = () => GameStartTime;
        Announcer.WarmupStage = () => Warmup.WarmupStage;
        Announcer.WarmupTimeLimitSeconds = () => Warmup.WarmupLimit > 0f ? Warmup.WarmupLimit : 0f;
        Announcer.TimeLimitMinutes = () => Cvars.TimeLimitMinutes;
        Announcer.Intermission = () => Intermission.Running;
        Announcer.AnnouncerMapTime = () => (int)Cvars.FloatOr("cl_announcer_maptime", 3f);

        // QC server/clientkill.qc ClientKill_TeamChange/KillIndicator_Think — the deferred `kill` countdown that
        // drives the spoken kill announcer + the CENTER_TEAMCHANGE countdown print. The death itself is the same
        // self-kill the old instant CmdKill ran (QC ClientKill_Now → Damage(this,this,this,100000,DEATH_KILL)).
        KillCountdown.Now = () => Time;
        KillCountdown.KillDelay = () => Cvars.FloatOr("g_balance_kill_delay", 2f);
        KillCountdown.RoundActiveNotStarted = () => Rounds is { IsRoundStarted: false };
        KillCountdown.GameStopped = () => GameStopped;
        KillCountdown.StateOf = PlayerStates.Of;
        KillCountdown.PerformKill = static p =>
            XonoticGodot.Common.Gameplay.Damage.Combat.Damage(p, p, p, 100000f,
                XonoticGodot.Common.Gameplay.Damage.DeathTypes.Kill, p.Origin, System.Numerics.Vector3.Zero);

        // Honor a host-set start time: if the host raised GameStartTime before Boot (a custom countdown) or
        // warmup is enabled, run the warmup/countdown flow; otherwise keep the immediate-start default (0) so
        // headless callers that step the sim straight away aren't frozen by a default countdown.
        if (Cvars.WarmupStage || GameStartTime > 0f)
        {
            Warmup.GameStartTime = GameStartTime;
            Warmup.Begin();                       // enter warmup (if g_warmup) or arm the start countdown
            GameStartTime = Warmup.GameStartTime; // mirror onto the world's start time (gates movement/scoring)
        }

        // Voting: a passed vote runs its command back through the command bus; voter count = connected humans.
        Voting.SetVoterCountSource(() => Clients.PlayerCount);
        Voting.VotePassed = cmd => Commands.Execute(cmd, isServerConsole: true);
        Voting.Roster = () => Clients.Players;
        Voting.FindPlayer = FindPlayerByNameOrIndex;
        Voting.Broadcast = line => Commands.ChatBroadcast?.Invoke(line);
        Voting.WarmupOrIntermission = () => Warmup.WarmupStage || Intermission.Running;
        // QC vote.qc:911 — the `allready` vote is only votable during warmup (warmup_stage). Without this the
        // predicate defaults to () => false and an allready vote is always rejected with "Game already started".
        Voting.WarmupStage = () => Warmup.WarmupStage;

        WireServerInfrastructure();
    }

    /// <summary>
    /// Wire the §5 server-infrastructure systems (bans, cheats, anticheat, event log, player stats, campaign,
    /// demo control, map vote/rotation, timeout) into the world's lifecycle. Called at the tail of
    /// <see cref="WireCommandsWarmupVoting"/>. Each subsystem is otherwise self-contained (Godot-free); this is
    /// the glue that feeds them the live roster + kick/broadcast pipelines and arms their per-match state.
    /// </summary>
    private void WireServerInfrastructure()
    {
        // Bans: enforce on connect (the host calls Bans.MaybeEnforceBan), kick via the client manager.
        Bans.Roster = () => Clients.Players;
        Bans.DropClient = (p, reason) => { Commands.KickHandler?.Invoke(p, reason); Clients.ClientDisconnect(p); };
        Bans.Log = line => Commands.ChatBroadcast?.Invoke(line);
        Bans.Load();

        // QC Join_Try play-ban gate (server/client.qc): a client on g_playban_list (forced-spectate, e.g. set by
        // the kick_teamkiller default branch or the `playban` command) may not join — so it can't press fire /
        // autojoin back into the match. Bans lives in the Server layer; ClientManager consults it via this seam.
        Clients.ForcedSpectate = Bans.IsPlayBanned;

        // kick_teamkiller: the mutator detects on the Common PlayerDies path but its three punitive actions
        // (force-spectate, kick, IP-ban) live in the Server layer. Inject them like Minigames.ObserverForcer so
        // every severity branch runs the real action (QC PutObserverInServer / dropclient_schedule /
        // Ban_KickBanClient). Safe even when the mutator is disabled (the delegates only fire on a threshold trip).
        if (Mutators.ByName("kick_teamkiller") is KickTeamkillerMutator ktk)
        {
            ktk.ForceObserver = e => { if (e is Player p) Clients.PutObserverInServer(p); };
            ktk.KickClient = e =>
            {
                if (e is not Player p) return false;
                Bans.DropClient?.Invoke(p, "Team Killing");
                return true; // QC dropclient_schedule returns true once the drop is scheduled
            };
            ktk.BanClient = (e, bantime, masksize) =>
            {
                if (e is Player p) Bans.KickBanClient(p, bantime, masksize, "Team Killing");
            };
        }

        // Cheats: snapshot sv_cheats for the map.
        Cheats.Init();
        Cheats.Log = line => Commands.ChatBroadcast?.Invoke(line);

        // Anticheat: ping provider for the snap-aim suppression window.
        AntiCheat.PingProvider = _ => 0f;

        // Event log: console sink → broadcast; gated by sv_eventlog at the call sites.
        GameLog.ConsoleSink = line => Commands.ChatBroadcast?.Invoke(line);
        GameLog.MatchId = MapName;
        if (Cvars.Bool("sv_eventlog"))
        {
            // QC GameLogInit (server/gamelog.qc:50): MUTATOR_CALLHOOK(BuildMutatorsString, ":gameinfo:mutators:LIST")
            // appends each active mutator's ":Tag" token. The port's GameLog.Init re-prefixes ':' per item, so
            // pass the bare tags (split the chain output, which is ":Vampire:Foo", on ':').
            string mutTokens = MutatorActivation.BuildMutatorsString("");
            string[] mutators = mutTokens.Split(':', StringSplitOptions.RemoveEmptyEntries);
            GameLog.Init(GameType?.NetName ?? DefaultGameType, MapName, mutators);
        }

        // Player stats: warmup gate + the score/anticheat/winner feeds.
        PlayerStats.IsWarmup = () => Warmup.WarmupStage;
        PlayerStats.AnticheatReporter = (p, add) => AntiCheat.ReportToPlayerStats(p, Time, add);
        PlayerStats.RankProvider = p => RankOf(p);
        PlayerStats.ScoreboardPosProvider = p => ScoreboardPosOf(p);
        PlayerStats.WinnerPredicate = p => p.Winning;
        PlayerStats.HandicapReportProvider = p => Scores.HandicapReportAverages(p);
        PlayerStats.Init();

        // Campaign: file reader from the config reader; transition through the change-level pipeline.
        if (ConfigReader is not null)
            Campaign.FileReader = ConfigReader;
        Campaign.Log = line => Commands.ChatBroadcast?.Invoke(line);
        Campaign.OnLevelTransition = (name, index, map) => { QueuedNextMap = map; };

        // Timeout: gates wired to the live world state.
        Timeout.VoteActive = () => Voting.Active;
        Timeout.Warmup = () => Warmup.WarmupStage;
        Timeout.GameStartTime = () => GameStartTime;
        Timeout.IsPlayerOf = p => !p.IsDead || true; // any connected client may call (faithful: IS_PLAYER)
        Timeout.Broadcast = line => Commands.ChatBroadcast?.Invoke(line);

        // Map rotation + vote: the change-level pipeline applies the winner; current map seeds the cursor.
        MapVote.Reseed(unchecked((int)(uint)MapName.GetHashCode()));
        Rotation.Reseed(unchecked((int)(uint)MapName.GetHashCode()) ^ 0x1234);

        // QC clears + re-links the spawnpoint list per map (relocate_spawnpoint recomputes have_team_spawns at
        // link time). SpawnSystem holds those globals statically, so reset them on this map-init — otherwise a
        // "found" team-spawn latch / request flag from a prior match could leak across a map change that reuses
        // the same gametype object. (The forteams mask is recomputed from the live spots each selection.)
        SpawnSystem.ResetTeamSpawns();

        // Demo: drive the engine recorder via the host hooks (left null until a host wires them).
        if (Cvars.Bool("sv_autodemo"))
            Demo.OnMatchStart(MapName, GameType?.NetName ?? DefaultGameType, Clients.Players);

        // Client lifecycle: feed the connect/spawn/disconnect hooks into the §5 systems.
        Clients.OnClientConnect = InfraClientConnect;
        Clients.OnClientSpawn = InfraClientSpawn;
        Clients.IsWarmup = () => Warmup.WarmupStage; // QC warmup_stage → the warmup gear-up loadout (SPAWN3)
        Clients.OnClientDisconnect = InfraClientDisconnect;
        // QC anticheat_spectatecopy (server/client.qc:1837): a following observer's body angle inherits the
        // spectatee's evade-tracked view angle. Wired here so the live SpectateCopy tick applies it.
        Clients.SpectateAngleCopy = (viewer, spectatee) => AntiCheat.SpectateCopy(viewer, spectatee);

        // QC LMS lms_AddPlayer / ForbidSpawn: gate the late-join + seed the joiner's lives. Wired only for an LMS
        // match; for every other gametype this branch returns true (no-op).
        // QC nJoinAllowed (server/client.qc:2192): the free-slot cap — refuse the join when the gametype player
        // limit is full. Duel (GetPlayerLimit → 2) is the live case: a 3rd+ client can't +jump-join a 1v1.
        Clients.GametypeJoinGate = p =>
            (GameType is not LastManStanding lms || lms.CanJoin(p, LmsPreStart))
            && GametypeHasFreeSlot(p);
        Clients.GametypeOnJoin = p => { if (GameType is LastManStanding lms) lms.AddPlayer(p, !LmsPreStart); };
    }

    /// <summary>QC the LMS pre-start window (<c>warmup_stage || time &lt;= game_starttime</c>): before this passes,
    /// LMS treats joins as free (full starting lives, no lockout) and disconnects as a no-op rank-wise.</summary>
    private bool LmsPreStart => Warmup.WarmupStage || Time <= GameStartTime;

    /// <summary>
    /// QC <c>GetPlayerLimit</c> (server/client.qc:2155): the hard cap on simultaneous players. Duel short-circuits
    /// to 2 (<see cref="Duel.PlayerLimit"/>) — its defining 1v1 rule. 0 ⇒ no gametype cap (use g_maxplayers/maxclients).
    /// </summary>
    private int GetPlayerLimit() => GameType is Duel ? Duel.PlayerLimit : 0;

    /// <summary>
    /// QC <c>nJoinAllowed</c> free-slot test (server/client.qc:2192: <c>free_slots = max(0, player_limit -
    /// currentlyPlaying)</c>): is there room for <paramref name="joiner"/> to become a live player? Counts the
    /// clients already in the game (non-observer) other than the joiner and refuses once the gametype player
    /// limit is reached. Returns true when no gametype cap applies (player_limit 0).
    /// </summary>
    private bool GametypeHasFreeSlot(Player joiner)
    {
        int limit = GetPlayerLimit();
        if (limit <= 0)
            return true; // no gametype cap (g_maxplayers/maxclients handled elsewhere)
        int currentlyPlaying = 0;
        IReadOnlyList<Player> roster = Clients.Players;
        for (int i = 0; i < roster.Count; i++)
        {
            Player it = roster[i];
            if (ReferenceEquals(it, joiner)) // QC FOREACH_CLIENT(it != this, …)
                continue;
            if (!it.IsObserver) // QC IS_PLAYER(it) || INGAME(it)
                currentlyPlaying++;
        }
        return currentlyPlaying < limit;
    }

    /// <summary>QC the ClientConnect tail: enforce bans, init anticheat/timeout, log connect/join, register stats.</summary>
    private void InfraClientConnect(Player p)
    {
        AntiCheat.Init(p, Time);
        Timeout.ResetAllowance(p);

        // QC Ban_MaybeEnforceBanOnce: a banned client is dropped on connect.
        if (Bans.MaybeEnforceBan(p))
            return;

        if (Cvars.Bool("sv_eventlog"))
        {
            GameLog.Connect(p);
            GameLog.Join(p);
        }

        // QC ClientConnect: Send_Notification(NOTIF_ALL, NULL, MSG_INFO, INFO_JOIN_CONNECT, this.netname) — the
        // "<name> connected" kill-feed line (server/client.qc:1153). The port's registered analogue is CHAT_CONNECT
        // (s1 = name); the NotificationSystem NET sink broadcasts it (it was registered but never emitted).
        NotificationSystem.Info("CHAT_CONNECT", p.NetName);

        // QC SendWelcomeMessage (server/client.qc:1077, sent per-client from scores.qc:229): when g_campaign the
        // welcome channel carries Campaign_GetLevelNum() (campaign_level + 1) and the CSQC builds the "Level N:"
        // line for the Welcome dialog. On the in-process listen server the equivalent is to surface that line to
        // the connecting real client; a bot has no CSQC/dialog so it's skipped (matches the human-only effect).
        if (!p.IsBot && Cvars.Bool("g_campaign"))
            SendCampaignWelcome(p);

        PlayerStats.AddPlayer(p);
        if (PlayerStats.Enabled)
            PlayerStats.AddEvent($"kills-{p.PlayerId}"); // QC the per-player kills-<id> event slot
        Demo.OnClientConnect(p, MapName, GameType?.NetName ?? DefaultGameType);

        // QC MUTATOR_HOOKFUNCTION(superspec, ClientConnect): seed the connect defaults, schedule the missing-UID
        // hello, and load the per-client options file. Gated on the mutator being added (g_superspectate on).
        if (Mutators.ByName("superspec") is SuperSpecMutator superspecConnect && superspecConnect.Added)
            superspecConnect.OnClientConnect(p);
    }

    /// <summary>
    /// QC the campaign branch of <c>SendWelcomeMessage</c> (server/client.qc:1079) + the CSQC build in
    /// <c>net_handle_ServerWelcome</c> (client/main.qc:1384): in a campaign the welcome message is just the
    /// 1-based level number (<see cref="Campaign.LevelNum"/> = <c>Campaign_GetLevelNum</c> = campaign_level + 1)
    /// rendered as "Level N:". The in-process listen server has no networked CSQC welcome channel, so the line is
    /// surfaced to the connecting real client through the same print path the rest of the campaign uses
    /// (<see cref="Campaign.Log"/> → ChatBroadcast). This is the live reader of <see cref="Campaign.LevelNum"/>.
    /// </summary>
    private void SendCampaignWelcome(Player p)
    {
        // QC sprintf(_("Level %d:"), campaign_level) — campaign_level here is Campaign_GetLevelNum (1-based).
        Campaign.Log?.Invoke($"^F1Level {Campaign.LevelNum}:^7 ^BG{p.NetName}^7, ^BGpress ^F2jump^BG to enter the game.");
    }

    /// <summary>QC the PutPlayerInServer tail: start the alivetime clock + reset the anticheat view window.</summary>
    private void InfraClientSpawn(Player p)
    {
        PlayerStats.BeginAlivetime(p, Time);
        AntiCheat.FixAngle(p, Time, 0f); // a spawn forcibly sets the view → suppress snap-aim briefly

        // [T39] QC PutClientInServer: campaign_bots_may_start = true once a real player spawns (the campaign's
        // bots hold position until then); a bot re-applies its bots.txt model (FixPlayermodel analogue).
        if (p.IsBot)
            Bots.OnBotSpawned(p);
        else
            Campaign.BotsMayStart = true;
    }

    /// <summary>QC the ClientDisconnect head: finalize stats, log :part:, and drop the client from the subsystems.</summary>
    private void InfraClientDisconnect(Player p)
    {
        // QC ClientDisconnect: if (IS_PLAYER(this)) Send_Effect(EFFECT_SPAWN, this.origin, '0 0 0', 1) — the
        // teleport-out puff at the live player's position (server/client.qc:1292). Only a JOINED player (not an
        // observer) has a body to vanish; the EffectEmitter NET sink networks the EFFECT_SPAWN ("SPAWN") burst.
        if (!p.IsObserver)
            EffectEmitter.Emit("SPAWN", p.Origin, Vector3.Zero, 1);

        if (Cvars.Bool("sv_eventlog"))
            GameLog.Part(p);

        // QC ClientDisconnect: Send_Notification(NOTIF_ALL, NULL, MSG_INFO, INFO_QUIT_DISCONNECT, this.netname) —
        // the "<name> disconnected" kill-feed line (server/client.qc:1297); CHAT_DISCONNECT (s1 = name) is the
        // registered port analogue, broadcast by the NotificationSystem NET sink (registered but never emitted).
        NotificationSystem.Info("CHAT_DISCONNECT", p.NetName);

        // QC MUTATOR_HOOKFUNCTION(lms, ClientDisconnect) → lms_RemovePlayer: rank the leaver + reshuffle the other
        // ranks (and record last-forfeiter health/armor + broadcast INFO_LMS_NOLIVES). A disconnect sets
        // lms_spectate=true in Base, so it's the voluntary path; a no-op during the LMS pre-start window.
        if (GameType is LastManStanding lmsLeave)
            lmsLeave.RemovePlayer(p, voluntary: true, warmupOrPreStart: LmsPreStart);

        PlayerStats.FinalizePlayer(p, Time, Teamplay.IsTeamGame);

        // QC ClientState_detach (common/state.qc:88): anticheat_report_to_eventlog(this) — emit the per-player
        // :anticheat: verdict lines on disconnect. The QC method self-gates on autocvar_sv_eventlog; the port's
        // GameLog.Echo does NOT gate, so gate here at the call site (as the other disconnect log lines do above).
        if (Cvars.Bool("sv_eventlog"))
            AntiCheat.ReportToEventLog(p, Time, GameLog.Echo);
        AntiCheat.Remove(p);
        Timeout.Remove(p);
        Voting.RemoveVoter(p);
        MapVote.RemoveVoter(p);
        Demo.OnClientDisconnect(p);

        // QC MUTATOR_HOOKFUNCTION(superspec, ClientDisconnect) → superspec_save_client_conf: persist this client's
        // superspec/autospec flags + itemfilter to its options file. Gated on the mutator being added.
        if (Mutators.ByName("superspec") is SuperSpecMutator superspecDisc && superspecDisc.Added)
            superspecDisc.OnClientDisconnect(p);

        // [T39] every bot removal path (fixcount trim, console remove, intermission teardown, a host kick)
        // funnels through this disconnect chain: drop the brain + notify the net host (BotRemoved).
        if (p.IsBot)
            Bots.OnBotDisconnected(p);
    }

    /// <summary>QC the scoreboard rank (1-based) of a player by SP_SCORE (0 if not registered).</summary>
    private int RankOf(Player p)
    {
        var sorted = Scores.Sorted();
        for (int i = 0; i < sorted.Count; i++)
            if (ReferenceEquals(sorted[i].Player, p)) return i + 1;
        return 0;
    }

    /// <summary>QC the scoreboard position (same as rank here; 0 = not on the scoreboard / spectator).</summary>
    private int ScoreboardPosOf(Player p) => RankOf(p);

    /// <summary>Resolve a player by exact/prefix name or <c>#index</c> (QC GetIndexedEntity, leniently).</summary>
    private Player? FindPlayerByNameOrIndex(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        if (token.StartsWith("#", StringComparison.Ordinal)
            && int.TryParse(token.AsSpan(1), out int idx))
        {
            foreach (Player p in Clients.Players) if (p.Index == idx) return p;
        }
        foreach (Player p in Clients.Players)
            if (string.Equals(p.NetName, token, StringComparison.OrdinalIgnoreCase)) return p;
        foreach (Player p in Clients.Players)
            if (p.NetName.StartsWith(token, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    /// <summary>
    /// Enable the round flow for a round-based mode (QC round_handler_Spawn). The predicates default to the
    /// CA-style "needs ≥2 players on ≥2 teams to start; round ends when ≤1 team has survivors", but a
    /// gametype can pass its own. Safe to call once after <see cref="Boot"/>.
    /// </summary>
    public RoundHandler EnableRounds(Func<bool>? canStart = null, Func<bool>? canEnd = null, Action? onRoundStart = null,
        float? endDelay = null, float? countdown = null, float? roundTimeLimit = null)
    {
        Rounds ??= new RoundHandler { GameStartTime = GameStartTime };
        // QC weaponUseForbidden (weaponsystem.qc:426): round_handler_IsActive() && !round_handler_IsRoundStarted()
        // — block weapon fire during the pre-round grace window (warmup/countdown/end-delay). The headless
        // WeaponFireDriver can't reach the live handler, so wire the predicate here for every round-based mode.
        WeaponFireDriver.RoundFireForbidden = _ => Rounds is { IsRoundStarted: false };
        // QC reset_map(false) on the next round: re-spawn players + reset map objects, preserving the score.
        Rounds.OnRoundReset = () => ResetMap(fakeRoundStart: true);
        Rounds.OnCountdownTick = n => BroadcastRoundStartCountdown(Rounds.RoundsPlayed, n); // [T40] round-start countdown
        Rounds.Spawn(
            canStart ?? DefaultCanRoundStart,
            canEnd ?? DefaultCanRoundEnd,
            onRoundStart);
        // QC round_handler_Init(the_delay, the_count, the_round_timelimit): a round-based gametype overrides the
        // 5/5/180 default with its own cvars (e.g. CA's 7s end-delay + 10s warmup, FreezeTag's, …).
        if (endDelay is float d && countdown is float c && roundTimeLimit is float rtl)
            Rounds.Init(d, c, rtl);
        return Rounds;
    }

    // =============================================================================================
    // the per-frame loop (the heart of the server core)
    // =============================================================================================

    /// <summary>
    /// Advance the world by <paramref name="realDelta"/> real seconds (QC SV_Frame): runs as many fixed
    /// 72 Hz ticks as have accumulated, each firing StartFrame → (per client: PreThink → move → PostThink)
    /// → non-client integrators → EndFrame (which drives the gametype/round/intermission). The match logic
    /// runs inside the tick via the wired callbacks; call this once per host frame.
    /// </summary>
    /// <returns>The number of fixed 72 Hz ticks that ran this call (0 when the host renders faster than the tick
    /// rate) — lets the network layer skip a redundant broadcast on a frame where the world didn't advance.</returns>
    public int Frame(float realDelta) => Simulation.Advance(realDelta);

    /// <summary>
    /// QC StartFrame top — fired once per tick before any entity moves (self/other = world). Drives the
    /// warmup timer, the per-frame creature damage (contents + fall), the call-vote think, and fires the
    /// SV_StartFrame mutator hook — matching the QC StartFrame order (warmup check → CreatureFrame_All →
    /// bot/anticheat → MUTATOR_CALLHOOK(SV_StartFrame)). PlayerPreThink for each client runs per-client in
    /// <see cref="OnClientMove"/> (the engine calls ClientMove right after StartFrame), matching the QC
    /// StartFrame FOREACH_CLIENT(IS_FAKE_CLIENT) PreThink.
    /// </summary>
    /// <summary>Set when a <c>g_balance_*</c> cvar changed since the last tick; drives the coalesced weapon
    /// balance re-derive at the top of <see cref="OnStartFrame"/> (see <see cref="OnServerCvarChanged"/>).</summary>
    private bool _weaponBalanceDirty;

    /// <summary>This world's cvar-store <see cref="CvarService.Changed"/> hook: a runtime <c>g_balance_*</c> change
    /// marks the weapon balance dirty so the next tick re-runs <see cref="Weapons.ConfigureAll"/> (the QC autocvar
    /// re-read). Only flags here — the actual reconfigure runs in <see cref="OnStartFrame"/> where this world is
    /// the ambient facade.</summary>
    private void OnServerCvarChanged(string name)
    {
        if (name.StartsWith("g_balance", System.StringComparison.Ordinal))
            _weaponBalanceDirty = true;
    }

    /// <summary>
    /// Tear down this world's external subscriptions. Critical when running on the SHARED cvar store: the
    /// <see cref="OnServerCvarChanged"/> hook is attached to a store that OUTLIVES the world (a map change builds
    /// a fresh world on the same store), so it must be detached or every retired world keeps re-deriving balance
    /// on every cvar change. Idempotent and harmless for a private store (it dies with the world anyway). The
    /// listen-server host calls this from its teardown (NetGame.Shutdown) before building the next map's world.
    /// </summary>
    public void Shutdown()
    {
        Services.CvarsImpl.Changed -= OnServerCvarChanged;
    }

    /// <summary>Cached deferred-command executor (see OnStartFrame — avoids a per-tick closure).</summary>
    private System.Action<string>? _deferredExec;

    private void OnStartFrame()
    {
        // Apply any runtime balance change (g_balance_* set via console/script/vote) before the tick reads weapon
        // stats: re-seed every weapon's cached balance block from the now-current cvars (QC autocvars are live).
        if (_weaponBalanceDirty)
        {
            _weaponBalanceDirty = false;
            Weapons.ConfigureAll();
        }

        // [T40] mirror the warmup stage onto the notification gate each tick, so the obituary's MSG_CHOICE
        // allow-gate + the first-blood !warmup test (server/damage.qc) see the right stage.
        NotificationSystem.WarmupStage = Warmup.WarmupStage;

        // Mirror sv_gentle onto the notification gentle-mode selector each tick (QC GENTLE == autocvar_sv_gentle
        // in the SVQC branch of util.qh). Because the port formats notification text server-side, sv_gentle is
        // the authoritative source: when set, SelectTemplate prefers each notification's gentle variant so the
        // violent kill/centerprint strings are replaced for everyone (matches Create_Notification_Entity GENTLE).
        NotificationSystem.GentleMode = Api.Cvars.GetFloat("sv_gentle") != 0f;

        // keep the world's start time synced with the warmup controller (it may have armed a countdown) —
        // but only when the warmup/countdown flow is actually driving (else a host-set GameStartTime stands).
        if (Warmup.IsStarted)
            GameStartTime = Warmup.GameStartTime;

        // QC StartFrame: the warmup limit check that ends warmup and restarts the match.
        using (Prof.Sample("start.warmup"))
            Warmup.Think();

        // QC the timeout/timein pause state machine (server/command/common.qc timeout_handler_think).
        Timeout.Think();

        // [T39] QC bot_serverframe() (main.qc:372): population fill/trim, waypoint load-once, the strategy
        // token, skill resync — after the warmup check, before anticheat_startframe, matching the QC order.
        using (Prof.Sample("start.bots"))
            Bots.ServerFrame();

        // QC anticheat_startframe: advance the global evade phase walk (server/anticheat.qc).
        AntiCheat.StartFrame(Simulation.FrameTime);

        // QC CreatureFrame_All: contents (water/lava/slime) + fall damage for every player, gated by the
        // game_stopped / pre-game window (QC: returns if game_stopped || time < game_starttime).
        if (!GameStopped && Time >= GameStartTime)
            CreatureFrameAll();

        // QC the in-match vote bus think (server/command/vote.qc VoteThink).
        using (Prof.Sample("start.vote"))
            Voting.Think();

        // [T56] DP Cbuf_Execute_Deferred: fire any `defer`-queued commands whose delay has elapsed (a passed
        // `restart` vote enqueues `defer 1 restart`). Pumped on the sim clock — without this it silently no-ops.
        // The executor delegate is cached: an inline `cmd => Commands.Execute(...)` captures `this` and would
        // allocate a fresh closure every tick.
        _deferredExec ??= cmd => Commands.Execute(cmd, isServerConsole: true);
        using (Prof.Sample("start.defer"))
            Commands.Deferred.Pump(Time, _deferredExec);

        // QC MUTATOR_CALLHOOK(SV_StartFrame) — the per-frame server-mutator event point (round modes,
        // powerup respawn schedulers, etc. subscribe via ServerHooks.SvStartFrame).
        using (Prof.Sample("start.hooks"))
        {
            ServerHooks.FireStartFrame(Time);
            // The gameplay-layer (XonoticGodot.Common) mutators can't reach ServerHooks, so they subscribe to the
            // mirrored MutatorHooks.SvStartFrame chain (T19 random_gravity et al.) — pump it from the same loop.
            XonoticGodot.Common.Gameplay.MutatorHooks.FireStartFrame(Time);
        }

        // [T41] QC client/announcer.qc Announcer_Time: the "5/1 minutes remain" map-time announcements with the
        // hysteresis latches, driven server-side (the port has no CSQC announcer timer). Self-gates on
        // intermission + the per-minute latches, so it costs one cheap pass per frame and fires once per crossing.
        Announcer.Tick();

        // QC server/clientkill.qc KillIndicator_Think: advance any armed `kill`/team-change countdown(s), playing
        // the spoken kill number + decrementing, and firing the real self-kill when the countdown reaches 0.
        var killCountdownPlayers = Clients.Players;
        for (int i = 0; i < killCountdownPlayers.Count; i++)
            KillCountdown.Tick(killCountdownPlayers[i]);
    }

    /// <summary>True when the match is frozen (intermission, ended, or a timeout pause) — QC <c>game_stopped</c>.
    /// Internal so the bot population's serverframe can honor the same gate (QC bot.qc:704).</summary>
    internal bool GameStopped => Intermission.Running || MatchEnded || Timeout.IsPaused;

    /// <summary>
    /// QC <c>CreatureFrame_All</c> (player slice): run the per-frame contents + fall damage for each player.
    /// In QC this iterates the g_damagedbycontents list; here it iterates the live player roster (the
    /// damaged-by-contents set is the players in this port). Skips noclip players (QC MOVETYPE_NOCLIP guard).
    /// </summary>
    private void CreatureFrameAll()
    {
        var players = Clients.Players;
        for (int i = 0; i < players.Count; i++)
        {
            Player p = players[i];
            if (p.MoveType == MoveType.Noclip || p.IsObserver) // QC: observers aren't in g_damagedbycontents
                continue;
            ServerPlayerState st = PlayerStates.Of(p);
            PlayerFrameLogic.ContentsDamage(p, st);
            PlayerFrameLogic.FallDamage(p, st);
        }
    }

    /// <summary>
    /// QC per-client physics step (SV_Physics_ClientEntity): PreThink → movement → PostThink. The engine
    /// calls this for each entity in <see cref="SimulationLoop.Clients"/>, in order, each tick.
    /// </summary>
    private void OnClientMove(Entity e)
    {
        if (e is not Player p)
            return; // only players are driven here (the Clients list holds Players)

        // ---- PlayerPreThink (QC client.qc PlayerPreThink) ----
        // Fire the per-frame player hook (dodging/multijump/instagib drive their state machines here).
        var pre = new MutatorHooks.PlayerPreThinkArgs(p);
        using (Prof.Sample("move.pre"))
            MutatorHooks.PlayerPreThink.Call(ref pre);

        // Dead players don't move (QC: PlayerThink bails for IS_DEAD); they await the respawn driver. The
        // pre-match countdown freezes players (QC: time < game_starttime) — but WARMUP is freely playable, so
        // movement is allowed during warmup even though scoring/creature-damage are off.
        bool preMatchFreeze = Time < GameStartTime && !Warmup.WarmupStage;
        // QC: an un-joined observer/spectator never runs PM_Main — its hull isn't simulated (no gravity).
        // (The connect-as-observer lifecycle sets MOVETYPE_NONE too, but gate movement explicitly.)
        // [T44] spectator free-flight: a free-fly spectator (MOVETYPE_NOCLIP/FLY, as QC PutSpectatorInServer)
        // runs PM_Main and flies (scaled by SPECTATORSPEED); a connect-as-observer (MOVETYPE_NONE) still must not
        // move (QC sys_phys_update returns on MOVETYPE_NONE). INERT today (observers are always MoveType.None
        // until a `spectate` transition exists) so behavior is identical to the prior build.
        bool spectatorFly = p.IsObserver
            && p.MoveType is MoveType.Noclip or MoveType.Fly or MoveType.FlyWorldOnly;
        bool canMove = !p.IsDead && !MatchEnded && !Intermission.Running && !preMatchFreeze
            && (!p.IsObserver || spectatorFly);

        // ---- movement (QC SV_PlayerPhysics → PM_Main, via the installed IPlayerPhysics) ----
        // CONSUME + ACK this tick's queued input EVERY tick the client is sending — even while observing / dead /
        // frozen / between rounds — and only APPLY the move when the player is live. QC advances servercommandframe
        // every server frame (the observer's/dead player's command stream is still processed), so the ack must keep
        // pace with what the client sends. If we skip the dequeue while !canMove — notably the ~1s connect-as-
        // observer window before autojoin — the client's inputs pile up UN-ACKED: the prediction lead grows ~1
        // input/tick across the whole window (~72 ticks) and then NEVER drains (the server acks 1/tick while the
        // client adds 1/tick), leaving the client predicting ~1 s ahead of authority forever, which turns every
        // later reconcile correction into a deep, jarring snap. So always pull the input (advancing LastProcessedSeq
        // via InputProvider); just don't run PM_Main when the player isn't live.
        //
        // [T39] BOTS source their command from the brain instead of the net InputProvider — the produce-only
        // BotBrain.ThinkProduce runs HERE, inside the bot's own client step right before its movement, exactly
        // like QC's sys_phys_ai (ecs/systems/physics.qc:28 → sv_physics.qc:41-46 → bot_think): the same tick's
        // PM_Main, DeadPlayerThink and weapon driver all consume the one produced command (cached per tick in
        // BotPopulation, mirroring ServerNet's per-tick cache for humans).
        IMovementInput input;
        using (Prof.Sample("move.in"))
            input = p.IsBot ? Bots.InputFor(p, Simulation.FrameTime) : InputProvider(p);

        // QC IntermissionThink (server/intermission.qc:498-505): once the match is over and the hold has elapsed,
        // a player pressing fire/jump/atck2/hook/use during the +10s grace skips the scoreboard immediately
        // (and with no players present the map only advances on such a request, exittime=-1). This is the
        // per-player half of that check — Intermission.Think() consumes the request world-side in
        // CheckRulesAndIntermission. Mapvote-initialized exists only after MapVote_Start, so this is harmless
        // once the vote owns the screen (ReadyToChangeLevel is already latched by then).
        if (Intermission.Running && !Intermission.ReadyToChangeLevel
            && (input.ButtonAttack1 || input.ButtonJump || input.ButtonAttack2 || input.ButtonHook || input.ButtonUse))
            Intermission.RequestExit();

        // ---- dead-player respawn state machine (QC PlayerThink dead branch) ----
        // A dead (non-observer) player doesn't move; it runs the DEAD_DYING→DEAD→RESPAWNABLE→RESPAWNING machine
        // each tick off this tick's fire/jump buttons, so the player respawns by pressing fire after the delay
        // (or is force-respawned with g_forced_respawn). The input was already dequeued/acked above.
        if (p.IsDead && !p.IsObserver)
        {
            DeadPlayerThink(p, input);
            return; // dead players don't run movement/PostThink (PostThink is a no-op while dead anyway)
        }

        // QC: apply the usercmd view angles to the player entity every frame. Without this the entity keeps its
        // SPAWN angles forever, so everything server-side that reads the player's facing — the weapon fire
        // direction (W_SetupShot via actor.Angles) and rocket guiding (owner.Angles) — aims at the spawn yaw
        // instead of where the player is looking. Angles carries the full view (pitch in X drives the render
        // aim-pose, yaw in Y the body — see PlayerModel); ViewAngles mirrors it for v_angle readers (nades/hook).
        // Bots steer by writing their OWN Angles in BotBrain and have no input stream (ViewAngles would be 0), so
        // never clobber a bot's aim here.
        if (!p.IsBot && canMove)
        {
            p.Angles = input.ViewAngles;
            p.ViewAngles = input.ViewAngles;
        }

        // [T37] Seated-in-a-vehicle drive gate (QC: PlayerPhysplug = veh.PlayerPhysplug replaces SV_PlayerPhysics).
        // Stash the resolved input on BOTH the player (gunner: GunnerFrame reads gunner.VehInput) and the body
        // (pilot: the descriptor Frame reads body.VehInput); the vehicle's own Think consumes it this same tick.
        // The seated player does NOT run PM_Main (else it fights the vehicle's player-glue → jitter); PostThink still runs.
        if (p.Vehicle is not null && canMove)
        {
            XonoticGodot.Common.Physics.MovementInput vehInput = VehicleBoarding.ToInput(input);
            p.VehInput = vehInput;
            p.Vehicle.VehInput = vehInput;
            OnPlayerPostThink(p);
            return;
        }

        if (canMove)
        using (Prof.Sample("move.pm"))
        {
            // Per-frame mode: the player advances by EXACTLY the real client commands drained this tick (each with
            // its own dt) — DP's process-queued-client-moves. A non-null batch is authoritative even when EMPTY: on
            // a soft-capped extra world tick (or a starved frame) there are no real commands, so the player runs
            // ZERO moves rather than a FABRICATED starve-repeat. That fabrication advanced the player more than the
            // client predicted and, below the sim rate, desynced client vs server every frame (the catharsis
            // rubberband — Path B; ListenServerDiagnosisTests proves command-driven drives the per-frame reconcile
            // correction to 0 at any fps). Legacy mode (or bot, or no net layer) → null batch → a single Move with
            // this tick's one merged command.
            IReadOnlyList<IMovementInput>? batch = p.IsBot ? null : TickMovementBatch?.Invoke(p);
            if (batch is not null)
            {
                // QC anticheat_physics (ecs/systems/sv_physics): run the statistical detectors PER COMMAND, not once
                // per tick. Under Path A (variable-dt per-frame prediction) a tick's batch holds 0..N real commands
                // each with its OWN dt; feeding the detectors one fixed FrameTime per tick mis-weights the snap-aim
                // angle-speed and, worse, makes the speedhack `movetime` track the assumed 1/72 instead of the
                // player's actual movement time (DP runs its speedhack detector per usercmd). So we accumulate each
                // command's dt and stagger the serverTime across the batch's real-time span [Time-total, Time], so
                // movetime tracks serverTime exactly and each command's true angle-delta/dt is measured. An empty
                // batch (soft-capped extra tick / starved frame) = no movement → no detector call. Bots never cheat.
                float total = 0f;
                for (int i = 0; i < batch.Count; i++)
                    total += batch[i].FrameTime > 0f ? batch[i].FrameTime : Simulation.FrameTime;
                float acc = 0f;
                for (int i = 0; i < batch.Count; i++)
                {
                    Movement.Move(p, batch[i]);
                    float ft = batch[i].FrameTime > 0f ? batch[i].FrameTime : Simulation.FrameTime;
                    acc += ft;
                    AntiCheat.Physics(p, batch[i], Time - total + acc, ft, ft, Simulation.TimeScale);
                }
            }
            else
            {
                // Legacy fixed-tick (or bot, or no net layer): one merged command at the sim FrameTime. sysFrametime
                // mirrors frametime; slowmo arg carries the live time scale (1 = real time) so the speed detectors
                // stay correct under slowmo.
                Movement.Move(p, input);
                if (!p.IsBot)
                    AntiCheat.Physics(p, input, Time, Simulation.FrameTime, Simulation.FrameTime, Simulation.TimeScale);
            }
        }

        // ---- PlayerPostThink (QC client.qc PlayerPostThink) ----
        using (Prof.Sample("move.post"))
            OnPlayerPostThink(p);
    }

    /// <summary>
    /// QC <c>PlayerPostThink</c> + <c>player_regen</c> (the Godot-free slice): per-player post-move
    /// bookkeeping run every tick — drowning (air timer + drown damage), health/armor/fuel regen+rot,
    /// active status-effect expiry/burn, and the active weapon's per-frame think. Mirrors the QC order
    /// (DrownPlayer in PlayerPostThink; player_regen + StatusEffects + the weapon frame in PlayerFrame).
    /// </summary>
    private void OnPlayerPostThink(Player p)
    {
        // QC: an un-joined observer runs ObserverOrSpectatorThink, NOT PlayerPostThink — no drown/regen/
        // statuseffects/weapon frame. Critically, running Regen here would climb the observer's Health off 0
        // and (via the owner-state snapshot) drop the connect overlay before the player actually spawns.
        if (p.IsObserver)
            return;

        ServerPlayerState st = PlayerStates.Of(p);
        bool gameStopped = GameStopped || Time < GameStartTime;

        // QC DrownPlayer: maintain the air timer and deal drown damage when submerged too long.
        PlayerFrameLogic.DrownPlayer(p, st, gameStopped);

        if (!gameStopped && !p.IsDead)
        {
            // QC player_regen: regenerate/rot health, armor, fuel toward their stable values.
            PlayerFrameLogic.Regen(p, st, Simulation.FrameTime);

            // QC StatusEffects tick: expire timed effects and apply periodic burn damage.
            using (Prof.Sample("mp.fx"))
                StatusEffectsCatalog.Tick(p, Time);

            // QC player_powerups(): the superweapon countdown (strip held superweapons once their timer lapses,
            // after the tick expires it) + the MUTATOR_CALLHOOK(PlayerPowerups) tail hook (instagib/overkill
            // manipulate the per-frame powerup values here).
            PlayerFrameLogic.PlayerPowerups(p);

            // QC the per-frame weapon driver (W_WeaponFrame): run the full fire state machine for the player.
            using (Prof.Sample("mp.weapon"))
                WeaponThink(p);
        }

        // QC PlayerPreThink tail (client.qc:2762): target_voicescript_next(this) — advance the player's active
        // scripted voice-line sequence one step per tick (play the next line, schedule the following). QC calls
        // this unconditionally at the end of the per-client frame; VoiceScript.Next carries its own IS_PLAYER /
        // game_stopped / voiceend gates, so it is safe to drive here regardless of the dead/gameStopped state
        // above (a latched script on a now-dead player simply no-ops until it re-arms). Without this pump a
        // target_voicescript latches on .use (first line plays) but never advances — the feature was dead.
        VoiceScript.Next(p);
    }

    /// <summary>
    /// QC the per-frame weapon driver (<c>W_WeaponFrame</c> → the active weapon's <c>wr_think</c>): run the
    /// raise/ready/fire/drop state machine for the player's weapon slot(s) so refire/animtime timing gates the
    /// fire rate, BOTH fire modes are routed from the player's attack buttons, and an out-of-ammo weapon
    /// dry-fires + auto-switches. The current attack buttons are sourced from this tick's input (the same
    /// <see cref="InputProvider"/> the movement step used), matching QC's <c>PHYS_INPUT_BUTTON_ATCK/ATCK2</c>.
    /// </summary>
    private void WeaponThink(Player p)
    {
        // [A2-review F1/F3] A seated player must NOT fire their personal hand weapon. In QC each vehicle
        // *_frame zeroes the player's attack buttons in place (racer.qc:181/385 etc.) BEFORE W_WeaponFrame
        // reads them, and vehicles_enter swaps the weaponentities to a temp_wepent — so the held weapon never
        // fires from the cockpit. The port drives the vehicle Frame from the vehicle's Think (a later tick
        // step), so it can't zero the live buttons first; skip the on-foot weapon driver while seated instead.
        if (p.Vehicle is not null)
            return;

        // Source the held attack buttons from the player's current input (QC reads them straight off the
        // applied move command). InputProvider is the per-tick command feed; a host with no net layer returns
        // ZeroInput (no buttons), so the driver simply advances timers and fires nothing. [T39] A bot's buttons
        // come from its brain via the SAME per-tick cache the movement step used (one command, two readers) —
        // ButtonAttack1/2 reach WeaponFireDriver through exactly the human path.
        IMovementInput input = p.IsBot ? Bots.InputFor(p, Simulation.FrameTime) : InputProvider(p);
        WeaponFireDriver.Frame(p, input);
    }

    /// <summary>
    /// QC EndFrame + the world-side per-frame match logic (CheckRules_World): drive the gametype's per-frame
    /// resolution (respawns, round checks, win conditions), then the round handler and intermission. Runs
    /// once per tick after every entity has moved.
    /// </summary>
    private void OnEndFrame()
    {
        // [T37] SEAM E: publish the world's frozen state to the vehicle subsystem (QC game_stopped) so vehicles
        // park during intermission/match-end/timeout and the boarding path blocks board/exit while stopped.
        XonoticGodot.Common.Gameplay.VehicleCommon.GameStopped = GameStopped;

        // 1) gametype per-frame step (QC the gametype's StartFrame/CheckRules slice). MatchController.Tick
        //    respawns due players (off Player.RespawnTime, set by the gametype's obituary handler) and keeps
        //    the leader/limit authoritative. We respawn through ClientManager so PlayerSpawn fires.
        DriveGametypeFrame();

        // 2) round flow (QC round_handler think) — only for round-based modes that called EnableRounds.
        if (Rounds is not null)
        {
            Rounds.GameStartTime = GameStartTime;
            Rounds.IntermissionRunning = Intermission.Running;
            // QC: the round's canRoundStart/canRoundEnd callbacks (the gametype's CheckTeams/CheckWinner) read the
            // live roster each frame — feed it to the active round-based gametype BEFORE the handler polls them.
            _roundPrep?.Invoke();
            Rounds.Think();
            // QC Announcer_Countdown:62-69 — when the round can't start (round_starttime == -1) show "Round cannot
            // start" once, then kill the countdown line + title. Detect the rising edge of the -1 state here (the
            // announcer is server-side) and re-arm once the handler leaves it (a fresh countdown re-arms).
            BroadcastRoundStop(Rounds.RoundStartTime == -1f);
            // Mirror the live handler's round timing into the gametype's own RoundHandler so the gametype's
            // CheckWinner / overtime reads (e.g. CA's round-timelimit stalemate, Onslaught's overtime decay) see
            // the same round_endtime / rounds_played the live handler advanced (QC round_handler_GetEndTime()).
            _roundSync?.Invoke();
        }

        // 3) end-of-match check + intermission (QC CheckRules_World → NextLevel, then IntermissionThink).
        CheckRulesAndIntermission();

        // 4) QC anticheat_endframe: advance the global evade phase walk (server/anticheat.qc).
        AntiCheat.EndFrame(Simulation.FrameTime, Time);

        // 5) the post-resolution server hook (QC the tail of the server frame).
        ServerHooks.FireEndFrame(Time);

        // [T38] QC pong_ball_think on sys_ticrate: advance real-time minigames (Pong) each frame, independent
        // of the match's game_stopped gate (a minigame runs regardless of the match state).
        Minigames.Tick(Simulation.FrameTime);

        // Waypoint sprites (QC WaypointSprite_Think): expire deployed-ping lifetimes + advance build-progress bars.
        XonoticGodot.Common.Gameplay.Waypoints.WaypointSprites.Think();
    }

    // === [T40] countdown announcer broadcasts (QC client/announcer.qc Announcer_Countdown), emitted server-side
    // because the port has no CSQC announcer timer. Gated on the registry Enabled flag, which encodes the SHIPPED
    // defaults (NUM_GAMESTART enabled n<=5, NUM_ROUNDSTART n<=3); NotificationSystem.Send does NOT skip a disabled
    // notification, so we gate here. ===
    private static void AnnceIfEnabled(string bareName)
    {
        var n = Notifications.ByName(MsgType.Annce, bareName);
        if (n is { Enabled: true })
            NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Annce, bareName);
    }

    // [T40] tracks the highest game-start countdown second handed to BroadcastGameStartCountdown for the current
    // arming, so PREPARE fires ONCE per armed countdown (QC Announcer_Gamestart's previous_game_starttime!=startTime
    // one-shot) and not on every >5s tick. 0 = no countdown armed; a tick with secondsLeft > this value is a fresh
    // arming (the countdown otherwise only decrements), and the terminal 0 (BEGIN) re-arms it.
    private int _gameStartCountdownArmed;

    // [W5] the last duel-title pair broadcast for the current countdown, so the duel title is re-sent only when
    // the two duelers change (QC Announcer_Duel: "if players haven't changed, stop here"). Cleared on BEGIN.
    private string _duelTitleLeft = "";
    private string _duelTitleRight = "";
    private bool _gametypeTitleShown;

    // [W5] QC Announcer_Countdown (announcer.qc:62-69): when the round handler reports it can't start the round
    // (RoundStartTime == -1, e.g. a team emptied), CSQC shows CENTER_COUNTDOWN_ROUNDSTOP ("Round cannot start"),
    // tears down the countdown and clears the title. The port drives the announcer server-side, so we detect the
    // rising edge of RoundStartTime == -1 here and broadcast once (re-armed once it leaves the -1 state).
    private bool _roundStopShown;

    private void BroadcastGameStartCountdown(int secondsLeft)
    {
        if (secondsLeft <= 0)
        {
            _gameStartCountdownArmed = 0; // BEGIN: re-arm so the next countdown speaks PREPARE again.
            // QC Announcer_Countdown:80 clears the title once the match begins (Announcer_ClearTitle).
            if (_gametypeTitleShown || _duelTitleLeft != "" || _duelTitleRight != "")
            {
                NotificationSystem.SendCenterTitle(NotifBroadcast.All, null, "");
                _gametypeTitleShown = false;
                _duelTitleLeft = _duelTitleRight = "";
            }
            NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Multi, "BEGIN");
            return;
        }

        // QC Announcer_Gamestart:148-167 plays ANNCE_PREPARE exactly once, on countdown arming
        // (previous_game_starttime != startTime), and only when time + 5.0 < startTime (i.e. countdown > 5s). The
        // descending tick only re-arms when it jumps up to a higher value, so detect arming as secondsLeft rising.
        bool arming = secondsLeft > _gameStartCountdownArmed;
        if (arming)
        {
            _gameStartCountdownArmed = secondsLeft;
            if (secondsLeft > 5) AnnceIfEnabled("PREPARE");
        }

        // QC Announcer_Gamestart:155-160: while the game-start countdown runs (not a round restart), show the
        // gametype name as the centerprint title — OR, in duel (m_1v1), the "P1 vs P2" duel title (refreshed when
        // the two duelers change, like Announcer_Duel). The port drives this server-side (no CSQC announcer timer).
        BroadcastCountdownTitle();

        // QC Announcer_Countdown:104 gates the game-start number on `!roundstarttime` — round-based modes (a
        // RoundHandler is active) do NOT speak NUM_GAMESTART (only the COUNTDOWN_GAMESTART center is shown); the
        // round path speaks NUM_ROUNDSTART instead (BroadcastRoundStartCountdown).
        if (Rounds is null) AnnceIfEnabled("NUM_GAMESTART_" + secondsLeft);
        NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Center, "COUNTDOWN_GAMESTART", secondsLeft);
    }

    private void BroadcastRoundStartCountdown(int round, int secondsLeft)
    {
        if (secondsLeft <= 0) { NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Multi, "BEGIN"); return; }
        AnnceIfEnabled("NUM_ROUNDSTART_" + secondsLeft);
        NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Center, "COUNTDOWN_ROUNDSTART", round + 1, secondsLeft);
    }

    /// <summary>
    /// Set the centerprint title for the game-start countdown (QC client/announcer.qc Announcer_Gamestart:155-160
    /// + Announcer_Duel). Duel (m_1v1) shows the two duelers' names ("P1 vs P2"), refreshed when they change; every
    /// other gametype shows its display name once ("^BG&lt;Gametype&gt;"). Driven server-side as the port has no CSQC
    /// announcer timer.
    /// </summary>
    private void BroadcastCountdownTitle()
    {
        if (GameType is Duel)
        {
            // QC Announcer_Duel: the top two non-spectator players (scoreboard order); "???" until they exist.
            var duelers = new List<string>(2);
            foreach (Player p in Clients.Players)
            {
                if (p.IsObserver || p.FragsStatus == Player.FragsSpectator) continue;
                duelers.Add(string.IsNullOrEmpty(p.NetName) ? "???" : p.NetName);
                if (duelers.Count == 2) break;
            }
            string left = duelers.Count > 0 ? duelers[0] : "???";
            string right = duelers.Count > 1 ? duelers[1] : "???";
            // Re-send only when the pair changes (QC: "Players haven't changed, stop here").
            if (left == _duelTitleLeft && right == _duelTitleRight) return;
            _duelTitleLeft = left;
            _duelTitleRight = right;
            NotificationSystem.SendCenterDuelTitle(NotifBroadcast.All, null, left, right);
            return;
        }

        // QC: centerprint_SetTitle(strcat("^BG", MapInfo_Type_ToText(gametype))) — once per armed countdown.
        if (_gametypeTitleShown) return;
        _gametypeTitleShown = true;
        string name = GameType?.DisplayName ?? "";
        NotificationSystem.SendCenterTitle(NotifBroadcast.All, null, "^BG" + name);
    }

    /// <summary>
    /// Broadcast the "Round cannot start" centerprint once when the round handler enters the can't-start state
    /// (QC Announcer_Countdown:62-69, <c>roundstarttime == -1</c>): show CENTER_COUNTDOWN_ROUNDSTOP, retract the
    /// running countdown line (CPID_ROUND) and clear any title (Announcer_ClearTitle). Re-arms once the handler
    /// leaves the -1 state (a fresh countdown). Driven server-side as the port has no CSQC announcer timer.
    /// </summary>
    private void BroadcastRoundStop(bool cannotStart)
    {
        if (cannotStart == _roundStopShown)
            return;
        _roundStopShown = cannotStart;
        if (!cannotStart)
            return; // left the can't-start state; nothing to send, just re-armed.

        NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Center, "COUNTDOWN_ROUNDSTOP");
        // QC Announcer_ClearTitle: clear the title once the round is stopped.
        if (_gametypeTitleShown || _duelTitleLeft != "" || _duelTitleRight != "")
        {
            NotificationSystem.SendCenterTitle(NotifBroadcast.All, null, "");
            _gametypeTitleShown = false;
            _duelTitleLeft = _duelTitleRight = "";
        }
        // The countdown line itself is replaced by the ROUNDSTOP center (both under CPID_ROUND), so no kill needed.
    }

    // =============================================================================================
    // gametype activation + per-frame driving (every registered gametype wired here)
    // =============================================================================================

    /// <summary>
    /// Activate the chosen gametype's own scoring/round handler and wire its team-score read-through
    /// (QC each gametype's <c>m_setTeams</c>/init + round_handler_Spawn). DM/TDM/CA keep their original
    /// wiring; the remaining bundled modes (CTF/FreezeTag/Domination/KeyHunt/Keepaway/TeamKeepaway/Onslaught/
    /// Nexball/Assault/Duel/LMS/Survival/Invasion/Race/CTS) each expose a public <c>Activate()</c>, an optional
    /// team-score getter, and a round-based flag — all centralized here so a host need only pass the
    /// gametype NetName to <see cref="Boot"/>.
    /// </summary>
    private void ActivateGameType()
    {
        // Reset any per-frame round hooks from a prior Boot (campaign level change re-activates a gametype on the
        // same world); only the active round-based gametype's case below re-installs them.
        _roundPrep = null;
        _roundSync = null;
        // Clear the round-grace weapon-fire block (QC round_handler_IsActive => round_handler != NULL): only a
        // round-based gametype's EnableRounds re-installs it, so a non-round mode never forbids fire.
        WeaponFireDriver.RoundFireForbidden = null;
        // Clear any prior gametype's bot attack-veto (only the incoming gametype's arm below re-installs it).
        Bot.BotBrain.ForbidAttackHook = null;
        // Tear down EVERY gametype's global hook subscriptions before activating the new one, so a live
        // gametype switch (campaign level change, a gametype vote) doesn't leave the old mode's handlers
        // (e.g. CTS's Shotgun-only PlayerSpawn/PlayerPreThink) running on the new mode. Deactivate is
        // idempotent per gametype (each guards on its own "was I subscribed?" flag), so this is safe.
        //
        // The INCOMING gametype is deactivated too — gametypes are process-wide Registry<GameType> SINGLETONS
        // (the same Assault/CTF/… instance is re-resolved by every Boot), so re-booting the same mode finds the
        // instance still "active" from the previous match. Each gametype's Activate() short-circuits its full
        // state reset when it sees its hook still subscribed (`if (_deathHandler is not null) return;`), so we
        // must clear that flag here first — otherwise stale match state (e.g. Assault's swapped attacker_team
        // after a round-2 swap, the round counter, MatchEnded) survives into the fresh match. Deactivating the
        // incoming one makes its Activate() below run the clean re-arm. Mirrors MutatorActivation reconciling
        // the mutator hook set.
        foreach (GameType gt in Registry<GameType>.All)
            gt.Deactivate();
        switch (GameType)
        {
            case Deathmatch dm:
                Match.ActivateDeathmatch(dm);          // subscribes DM scoring; spawns roster (empty at boot)
                break;
            case Mayhem m:
                // Mayhem is FFA but NOT a Deathmatch subclass (MatchController hard-casts to Deathmatch), so it
                // activates standalone like Duel. Its damage+frag scoring handler subscribes here; the per-frame
                // respawn + leader recompute run via DriveGametypeFrame.
                m.Activate();
                break;
            case TeamMayhem tm:
                tm.Activate();
                Scores.TeamScoreSource = tm.GetTeamScore;    // read team totals through the gametype (ST_SCORE)
                break;
            case Tdm tdm:
                tdm.Activate();
                Scores.TeamScoreSource = tdm.GetTeamScore;   // read team totals through the gametype
                break;
            case ClanArena ca:
                ca.Activate();
                Scores.TeamScoreSource = ca.GetTeamRounds;   // CA's "team score" is its round wins
                // QC round_handler_Spawn(CA_CheckTeams, CA_CheckWinner, CA_RoundStart): drive the LIVE handler off
                // CA's real predicates (CheckTeams gate, CheckWinner → round-timelimit stalemate via PreventStalemate)
                // instead of the generic defaults, and use CA's grace/timelimit/end-delay cvars (10s warmup etc).
                {
                    RoundHandler caRounds = EnableRounds(
                        ca.CheckTeams,
                        () => ca.CheckWinner() != 0,
                        onRoundStart: null,
                        endDelay: Cvars.FloatOr("g_ca_round_enddelay", 5f),
                        countdown: Cvars.FloatOr("g_ca_warmup", 5f),
                        roundTimeLimit: Cvars.FloatOr("g_ca_round_timelimit", 180f));
                    // QC round_handler_Think cnt==0: GameRules_scoring_add(it, ROUNDS_PL, 1) for every present
                    // player as the round begins. _roundPrep refreshes ca's roster before Rounds.Think() fires
                    // OnRoundCounted, so AwardRoundStartScore reads the current roster.
                    caRounds.OnRoundCounted = ca.AwardRoundStartScore;
                    // CA's CheckWinner reads ca.Handler.RoundEndTime for the round-timelimit stalemate branch; mirror
                    // the live handler's timing into CA's own handler each frame, and feed it the live roster.
                    _roundPrep = () => ca.SetRoster(Clients.Players);
                    _roundSync = () => MirrorRoundTiming(caRounds, ca.Handler);
                }
                break;

            // ---- the remaining team / objective modes ----
            case Ctf ctf:
                ctf.Activate();
                Scores.TeamScoreSource = ctf.GetTeamCaps;     // capture count is the team score
                break;
            case Domination dom:
                dom.Activate();
                if (dom.RoundBased)
                {
                    // QC dom_DelayedInit round_handler_Spawn: the round-based variant runs the world round handler
                    // over Domination's own canStart/canEnd/roundStart (a team owning ALL points wins the round →
                    // ST_DOM_CAPS). The team score is the round-win count (caps) in this variant.
                    RoundHandler domRounds = EnableRounds(dom.CanRoundStart, dom.CheckRoundWinner, dom.RoundStart);
                    dom.RoundEndTimeSource = () => domRounds.RoundEndTime; // QC round_handler_GetEndTime()
                    Scores.TeamScoreSource = dom.GetTeamCaps;
                }
                else
                {
                    Scores.TeamScoreSource = dom.GetTeamScore; // tick-based: the running point total
                }
                break;
            case KeyHunt kh:
                kh.Activate();
                kh.SetRoster(Clients.Players);               // KH spawns keys onto roster members at round start
                Scores.TeamScoreSource = kh.GetTeamScore;
                EnableRounds();                              // KH is round-based
                break;
            case FreezeTag ft:
                ft.Activate();
                Scores.TeamScoreSource = ft.GetTeamRounds;
                // QC round_handler_Spawn(freezetag_CheckTeams, freezetag_CheckWinner, …): drive the live handler off
                // FT's real predicates + its warmup/timelimit/end-delay cvars (so the round ends when a team is fully
                // frozen, not via the generic dead-team default), with the thaw-everyone OnRoundStart.
                {
                    RoundHandler ftRounds = EnableRounds(
                        ft.CheckTeams,
                        () => ft.CheckWinner() != 0,
                        // QC freezetag round_handler_Spawn roundStart (Frozen.Clear): thaw everyone at the start of a
                        // new round. FreezeTag exposes the per-player Unfreeze; iterate the roster (no public bulk thaw).
                        onRoundStart: () =>
                        {
                            var roster = Clients.Players;
                            for (int i = 0; i < roster.Count; i++) ft.Unfreeze(roster[i]);
                        },
                        endDelay: Cvars.FloatOr("g_freezetag_round_enddelay", 5f),
                        countdown: Cvars.FloatOr("g_freezetag_warmup", 5f),
                        roundTimeLimit: Cvars.FloatOr("g_freezetag_round_timelimit", 180f));
                    _roundPrep = () => ft.SetRoster(Clients.Players);
                    _roundSync = () => MirrorRoundTiming(ftRounds, ft.Handler);
                }
                break;
            case Onslaught ons:
                ons.Activate();
                EnableRounds();                              // Onslaught rounds end on generator destruction
                break;
            case TeamKeepaway tka:
                tka.Activate();
                // QC MUTATOR_HOOKFUNCTION(tka, Bot_ForbidAttack) (sv_tka.qc:564-580): a bot won't attack unless it or
                // the target carries the ball, the ball is loose, or (g_tka_score_team && its team holds the ball) —
                // so bots cluster the carrier instead of fragging bystanders. Non-player targets are never vetoed here.
                Bot.BotBrain.ForbidAttackHook = (self, targ) =>
                    self is Player bot && targ is Player victim && tka.ForbidBotAttack(bot, victim);
                break;
            case Nexball nb:
                nb.Activate();
                break;
            case Assault aslt:
                aslt.Activate();
                // QC MUTATOR_HOOKFUNCTION(as, ReadLevelCvars) (sv_assault.qc:606): Assault is incompatible with
                // warmup — it forces warmup_stage = 0 and sv_ready_restart_after_countdown = 0 (the match is run as
                // two engine-restarted rounds, so the warmup/ready-restart-after-countdown flow must not run). Force
                // those cvars here, before WireCommandsWarmupVoting → Warmup.Begin reads g_warmup (Boot order:
                // ActivateGameType @545 precedes WireCommandsWarmupVoting @686). Skip in campaign — QC's
                // ReadyRestart_Deny permits the single-round campaign flow (which never enters the round-restart path
                // anyway), matching the engine warmup-off-in-campaign default.
                if (!(TargetUtilities.IsCampaign?.Invoke() ?? false))
                {
                    Cvars.Set("g_warmup", 0f);                          // QC warmup_stage = 0
                    Cvars.Set("sv_ready_restart_after_countdown", 0f);  // QC sv_ready_restart_after_countdown = 0
                }
                // QC mapinfo default args "timelimit=20" (assault.qh CLASS(Assault) gametype default): Assault maps
                // default to a 20-minute defender timelimit. The port reads the live `timelimit` cvar in DriveFrame,
                // so if nothing configured one the defender-timelimit win could never fire (the round would be
                // decidable only by core destruction). Assert the Base default when no timelimit is set.
                if (Cvars.FloatOr("timelimit", 0f) <= 0f)
                    Cvars.Set("timelimit", 20f);
                // QC Assault has NO generic round_handler: it runs entirely off WinningCondition_Assault() called
                // per-frame from CheckRules_World (driven below in DriveGametypeFrame → aslt.DriveFrame). It manages
                // its own two rounds via the as_round entity + the timelimit cvar (assault_new_round). So we DON'T
                // give it the generic round machinery (which would freeze play in pre-round countdowns and gate
                // mid-round respawns — both un-Base for Assault); the per-frame win check is driven below and the
                // campaign predicate (TargetUtilities.IsCampaign) is wired globally in Boot.
                aslt.GameStartTime = GameStartTime;
                // QC assault_new_round → ReadyRestart_force(true) (sv_assault.qc:219): when round 2 begins, perform
                // the engine round restart — reset every player + map object (incl. re-arming the objective chain's
                // world entities) and re-stamp game_starttime so the round-2 clock runs from now. Assault's
                // FirstRoundDestroyTime already drives DriveFrame's round-2 timelimit; here we just do the map reset.
                // fakeRoundStart:true keeps the team objective scores (the 666 round-1 sentinel) across the swap.
                aslt.OnSecondRoundRestart = () =>
                {
                    GameStartTime = Time;
                    aslt.GameStartTime = GameStartTime;
                    ResetMap(fakeRoundStart: true);
                    // QC assault_new_round (sv_assault.qc:207): swap the saved spawn-point teams with the roles so the
                    // new attackers spawn at the geographic attacker location (not the defender one).
                    AssaultSwapSpawnTeams();
                    // QC assault_new_round → ReadyRestart_force → roundstart .reset2 = assault_roundstart_use_this
                    // (sv_assault.qc:392): re-run the turret roundstart with the NOW-swapped attacker team, so a
                    // turret that defended for the round-1 defenders flips to defend for the round-2 defenders. The
                    // attacker team was already swapped by StartSecondRound before this callback fires.
                    AssaultTurretRoundstart(aslt.AttackerTeam);
                };
                break;

            // ---- FFA / non-team modes that still own a scoring handler ----
            case Keepaway ka:
                ka.Activate();
                // QC MUTATOR_HOOKFUNCTION(ka, Bot_ForbidAttack) (sv_keepaway.qc:542-556): a bot won't attack
                // unless it or the target carries the ball, or the ball is loose — so bots contest the carrier
                // instead of fragging bystanders. Non-player targets are never vetoed here.
                Bot.BotBrain.ForbidAttackHook = (self, targ) =>
                    self is Player kbot && targ is Player kvictim && ka.ForbidBotAttack(kbot, kvictim);
                break;
            case Duel duel:
                duel.Activate();
                // QC INIT(Duel) gametype_init default args "timelimit=10 pointlimit=0 leadlimit=0" (duel.qh:9):
                // duel's match-limit defaults differ from the generic 20-minute timelimit — a duel runs to a
                // 10-minute clock and is decided by time (pointlimit=0 / leadlimit=0, both already the generic
                // defaults Duel.FragLimit/leadlimit read). Assert the Base mapinfo default for the one divergent
                // limit when nothing configured it (same pattern as the Assault/Survival timelimit defaults above).
                if (Cvars.FloatOr("timelimit", 0f) <= 0f)
                    Cvars.Set("timelimit", 10f);   // QC timelimit=10 (minutes); pointlimit=0/leadlimit=0 are the generic defaults
                break;
            // QC LMS is NOT round-based (sv_lms.qc has no round_handler): it is a SINGLE continuous match where
            // living players respawn throughout and only running out of lives takes you out. So do NOT call
            // EnableRounds() — the round gate (DeadPlayerThink line 2341) would hold a mid-match dead player out
            // until a "round" reset and the degenerate FFA DefaultCanRoundEnd team scan would mis-resolve the win.
            // With no rounds, respawn timing is owned by LMS per-player lives (ApplyRespawnTiming via the
            // CalculateRespawnTime seam: 0-lives ⇒ denied, else the dynamic delay) and the win is latched by the
            // per-frame CheckWinningCondition — exactly the Base continuous lives model.
            case LastManStanding lms: lms.Activate(); break;
            case Survival surv:
                surv.Activate();
                // QC survival.qh CLASS(Survival) gametype_init default args "timelimit=20 pointlimit=12": the
                // mapinfo registration defaults a Survival match to a 20-minute timelimit and a 12-point limit.
                // Like Assault's timelimit default above, assert the Base mapinfo defaults when nothing configured
                // one (the shipped cfg/menu may set them, but an unconfigured boot would otherwise run unlimited).
                if (Cvars.FloatOr("timelimit", 0f) <= 0f)
                    Cvars.Set("timelimit", 20f);
                if (Cvars.FloatOr("fraglimit", 0f) <= 0f)
                    Cvars.Set("fraglimit", 12f); // QC mapinfo pointlimit=12 → GameRules_limit_score (fraglimit)
                // QC surv_Initialize round_handler_Spawn(Surv_CheckPlayers, Surv_CheckWinner, Surv_RoundStart):
                // drive the LIVE (map-reset/respawn-gate/countdown) handler off Survival's OWN predicates — the
                // round starts when ≥2 players are present (Surv_CheckPlayers), assigns hidden roles at cnt==0
                // (Surv_RoundStart → AssignRoles), and ends on the side-wipe/timeout (Surv_CheckWinner →
                // CheckWinningCondition latches RoundOver). Without this the generic DefaultCanRoundEnd ran (team
                // based, degenerate for FFA Survival) and AssignRoles never rode the live handler. The end-delay
                // arg is a hardcoded 5 in QC (NOT g_survival_round_enddelay); warmup/round-timelimit from cvars.
                {
                    RoundHandler survRounds = EnableRounds(
                        surv.HandlerCanRoundStart,
                        () => { surv.CheckWinningCondition(); return surv.RoundOver; },
                        onRoundStart: () => surv.AssignRoles(Clients.Players),
                        endDelay: 5f,
                        countdown: Cvars.FloatOr("g_survival_warmup", 10f),
                        roundTimeLimit: Cvars.FloatOr("g_survival_round_timelimit", 120f));
                    // Survival's CheckWinningCondition / RoundTimedOut read surv.Handler.RoundEndTime + the live
                    // round phase; mirror the live handler's timing + phase into surv.Handler each frame and feed
                    // it the live roster (matching CA/FreezeTag, fixing the prior unmirrored dual-handler desync).
                    _roundPrep = () => surv.SetRoster(Clients.Players);
                    _roundSync = () => MirrorRoundTiming(survRounds, surv.Handler);
                }
                // QC MUTATOR_HOOKFUNCTION(surv, Bot_ForbidAttack) (sv_survival.qc:484-490): a bot never attacks a
                // same-status player — `targ.survival_status == bot.survival_status`. Preserves the hidden-role
                // dynamic and, with teamkill punishment live, stops an ally-fragging bot from mirror-killing itself.
                // Faithful to QC: the equality also holds pre-round (both status 0/None), forbidding bot attacks in
                // warmup; non-player targets have no survival_status so they are never vetoed here.
                Bot.BotBrain.ForbidAttackHook = (self, targ) =>
                    self is Player bot && targ is Player victim
                    && surv.StatusOf(bot) == surv.StatusOf(victim);
                // QC sv_survival.qc Send_Notification(...): the gametype decides WHAT to send (each player's secret
                // role at round start, the round result, the last-of-side "alone" prompt); the host wires those to
                // the live broadcast NotificationSystem. Without this the Role/RoundResult/Alone calls reached a null
                // sink and the player never saw their role nor a win/tie/alone banner.
                surv.Notifications = new SurvivalNotifyHost();
                break;
            case Invasion inv:
                inv.Activate();
                EnableRounds();
                // QC sv_invasion.qc Send_Notification(NOTIF_ALL, ...): the gametype decides WHAT to send
                // (round over / round winner / supermonster arrival); the host wires those to the live
                // broadcast NotificationSystem (the MSG_CENTER/MSG_INFO routing). Without this the decisions
                // (CheckWinner / SpawnMonsterDef) reached a null sink and the prints never went out.
                inv.Notifications = new InvasionNotifyHost();
                break;
            case Race race:
                race.Activate();
                // QC the finish kill-delay re-teleport: re-place the racer at a (race-start) spawn point so they
                // run again. Clients.Spawn goes through PutPlayerInServer, the host analogue of race_PreparePlayer.
                race.OnFinishRetract = p => Clients.Spawn(p);
                // QC trigger_race_checkpoint_spawn_evalfunc / race_respawn_spotref (server/race.qc:1055/808): a
                // respawning racer is returned to the checkpoint they last passed, not a random grid start. Race
                // sets the one-shot forced spawn (Player.SpawnPointTarg) here, before SelectSpawnPoint reads it.
                Clients.OnPreSpawn = p => race.ApplyRespawnSpot(p);
                // QC rc_SetLimits (sv_race.qc:407-438): the qualifying-then-race mode is DERIVED from the
                // qualifying-timelimit cvar — if g_race_qualifying_timelimit(_override) > 0 the session becomes
                // g_race_qualifying = 2, the real fraglimit/leadlimit/timelimit are STASHED, and the ACTIVE
                // timelimit is replaced by the qualifying timelimit (so the warmup-qualifying phase runs on the
                // qualifying clock; when it elapses TransitionQualifyingToRace restores the stashed limits).
                {
                    float qOverride = Cvars.FloatOr("g_race_qualifying_timelimit_override", -1f);
                    float qBase = Cvars.FloatOr("g_race_qualifying_timelimit", 0f);
                    float qTimelimit = qOverride >= 0f ? qOverride : qBase; // minutes
                    if (qTimelimit > 0f)
                    {
                        // QC: g_race_qualifying = 2; stash the real limits; active timelimit := qualifying timelimit.
                        Cvars.Set("g_race_qualifying", "2");
                        race.SaveRaceLimits(Cvars.FloatOr("fraglimit", -1f), Cvars.FloatOr("leadlimit", -1f),
                            Cvars.FloatOr("timelimit", -1f));
                        Cvars.Set("timelimit",
                            qTimelimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else if (Cvars.FloatOr("g_race_qualifying", 0f) == 2f)
                    {
                        // g_race_qualifying was set to 2 directly (no qualifying-timelimit source): still stash the
                        // live limits so the transition can restore them when the plain timelimit elapses.
                        race.SaveRaceLimits(Cvars.FloatOr("fraglimit", -1f), Cvars.FloatOr("leadlimit", -1f),
                            Cvars.FloatOr("timelimit", -1f));
                    }
                }
                break;
            case Cts cts:
                cts.Activate();
                cts.OnFinishRetract = p => Clients.Spawn(p);
                break;

            default:
                // An unrecognized custom gametype: nothing to wire beyond the shared score table (already
                // subscribed in Boot). Movement + frags still work via the obituary bus.
                break;
        }
    }

    /// <summary>
    /// Wire <see cref="GametypeObjectiveSpawns.Sink"/> to the active gametype so the BSP entity lump's objective
    /// classnames (item_flag_*, dom_controlpoint, trigger_race_*, onslaught_*) register on the right mode (QC
    /// each objective spawnfunc dispatches to its gametype's setup). The lump pre-spawns a placeholder edict and
    /// hands it here; we read its origin/team and call the gametype's own Spawn API (which spawns the real
    /// objective entity), then retire the placeholder. A non-objective gametype clears the sink (no-op).
    /// </summary>
    private void WireObjectiveSpawns()
    {
        // Each arm is an explicit Action<Entity>? so the switch-expression has a single common type (lambdas
        // have no natural type, which would otherwise fail to infer alongside the null default arm).
        GametypeObjectiveSpawns.Sink = GameType switch
        {
            Ctf ctf => (Action<Entity>?)(e =>
            {
                if ((int)e.Team == Teams.None) ctf.SpawnNeutralFlag(e.Origin, e.Angles);
                else ctf.SpawnFlag((int)e.Team, e.Origin, e.Angles);
                RetirePlaceholder(e);
            }),
            Domination dom => e =>
            {
                // QC dom_controlpoint_setup: .frags = per-point amount, .wait = per-point rate (defaults 1 / 5).
                float amt = e.Count > 0 ? e.Count : 1f;
                dom.SpawnControlPoint(e.Origin, (int)e.Team, amt);
                RetirePlaceholder(e);
            },
            Race race => e =>
            {
                if (e.ClassName == "trigger_race_penalty")
                    race.SpawnPenaltyZone(e.Origin, e.Count > 0 ? e.Count : 5f);
                else
                    // QC spawnfunc(trigger_race_checkpoint): .cnt → race_checkpoint index (0 = start line);
                    // spawnflag 4 kills a wrong-order crossing (DEATH_HURTTRIGGER 10000); spawnflag 8 on the
                    // highest CP marks the timed finish line (race_timed_checkpoint) for start!=finish tracks.
                    race.SpawnCheckpoint(e.Origin, e.Count,
                        punishWrongWay: (e.SpawnFlags & 4) != 0,
                        timed: (e.SpawnFlags & 8) != 0);
                RetirePlaceholder(e);
            },
            Onslaught ons => e =>
            {
                switch (e.ClassName)
                {
                    case "onslaught_generator":
                        // QC spawnfunc(onslaught_generator): the node is indexed by its .targetname so an
                        // onslaught_link can resolve it in the post-spawn pass (ons.ResolveLinks below).
                        ons.CpCombat.SpawnGenerator((int)e.Team, e.Origin, e.TargetName);
                        break;
                    case "onslaught_controlpoint":
                        ons.CpCombat.SpawnControlPoint(_nextOnsCpId++, e.Origin, e.TargetName);
                        break;
                    case "onslaught_link":
                        // QC spawnfunc(onslaught_link) + ons_DelayedLinkSetup (INITPRIO_FINDTARGET): a link names
                        // two nodes via .target/.target2; resolution is DEFERRED until every onslaught_* node has
                        // spawned (spawn-order-independent, like Assault's objective chain). Stage the name pair
                        // now; ons.ResolveLinks() turns them into graph edges in the post-spawn pass (Boot).
                        ons.StageLink(e.Target, e.Target2);
                        break;
                }
                RetirePlaceholder(e);
            },
            // [T36] Assault objective chain (common/gametypes/gametype/assault/sv_assault.qc). Each spawnfunc
            // tagged its classname; we route by it into the Assault POJO chain. The destructible→decreaser→objective
            // links are spawn-order-independent (QC INITPRIO_FINDTARGET), so decreasers/destructibles are STAGED
            // here and resolved by Assault.ResolveObjectiveGraph() in the post-spawn pass (Boot, after the lump).
            Assault aslt => e =>
            {
                switch (e.ClassName)
                {
                    case "target_objective":
                        // QC target_objective: .targetname = this objective's name, .target = next objective.
                        aslt.AddObjective(e.TargetName, e.Target);
                        break;
                    case "target_objective_decrease":
                        // QC target_objective_decrease: .targetname = the decreaser's name (a destructible's .target
                        // fires it), .target = the OBJECTIVE it decreases (.enemy), .dmg = damage (QC default 101).
                        aslt.StageDecreaser(e.TargetName, e.Target, e.Dmg);
                        break;
                    case "func_assault_destructible":
                        // QC func_assault_destructible: .target = the decreaser it triggers when destroyed, .health
                        // from the breakable setup (default 100). KEEP the edict alive as a shootable world wall:
                        // the bot Assault role finds it by classname (BotObjectiveRoles.NearestByClass), and its
                        // damage is driven through the SAME pipeline QC uses for non-player objectives — the entity's
                        // .event_damage callback (DamageSystem.EventDamage routes a non-player target with a
                        // GtEventDamage delegate straight to it, exactly like the Onslaught generator). The callback
                        // maps the hit's attacker team and drives the objective chain (QC the breakable damage →
                        // assault_objective_decrease_use path). Link the POJO to this edict, then RETURN (do NOT
                        // retire — unlike flags/checkpoints, the wall stays in the world).
                        e.Solid = Solid.Bsp;
                        e.MoveType = MoveType.Push;
                        e.TakeDamage = DamageMode.Aim;
                        if (e.Health <= 0f) e.Health = 100f;
                        e.MaxHealth = e.Health;
                        if (!string.IsNullOrEmpty(e.Model))
                            Api.Entities.SetModel(e, e.Model); // resolve the "*N" brush bounds so traces clip it
                        aslt.StageDestructible(e.Target, e.Health, e);
                        e.GtEventDamage = (self, inflictor, attacker, deathType, dmg, hitLoc, force) =>
                        {
                            // QC func_assault_destructible event_damage: only the attacking team may damage it, and a
                            // destroyed wall fires its decreaser (DamageDestructible → DecreaseObjective). Mirror the
                            // health onto the world edict so the wall reads as broken once the POJO is destroyed.
                            Assault.Destructible? pojo = aslt.DestructibleFor(self);
                            if (pojo is null) return;
                            int byTeam = attacker is Player ap ? (int)ap.Team : Teams.None;
                            aslt.DamageDestructible(pojo, byTeam, dmg, attacker as Player);
                            self.Health = pojo.Health;
                            if (pojo.Destroyed) { self.Solid = Solid.Not; self.TakeDamage = DamageMode.No; }
                        };
                        // QC func_assault_destructible.event_heal = destructible_heal (sv_assault.qc:353): a friendly
                        // heal source (Arc heal-beam / heal nade / mage healgun) tops a partially-shot wall back up to
                        // its max_health. Damage.Heal(targ, ...) routes a non-player target with GtEventHeal straight
                        // here (same path as the Onslaught generator/icon). Mirror the healed POJO health onto the
                        // edict so the world wall reads back up.
                        e.GtEventHeal = (self, inflictor, amount, limit) =>
                        {
                            Assault.Destructible? pojo = aslt.DestructibleFor(self);
                            if (pojo is null || !aslt.HealDestructible(pojo, amount, limit)) return false;
                            self.Health = pojo.Health; // QC GiveResourceWithLimit + WaypointSprite_UpdateHealth
                            return true;
                        };
                        return; // keep the world wall; skip RetirePlaceholder.
                    case "func_assault_wall":
                        // QC func_assault_wall (sv_assault.qc:364): a SOLID_BSP wall keyed to an objective (.target).
                        // It is solid + visible while the objective lives and hides (model="" + SOLID_NOT) once the
                        // objective is destroyed — opening a path the attackers earn by destroying that objective.
                        // KEEP the edict alive (like a destructible) so DriveWalls can toggle its solid/model each
                        // frame; stage the objective link (resolved spawn-order-independently in ResolveObjectiveGraph,
                        // QC INITPRIO_FINDTARGET / assault_setenemytoobjective), then RETURN (skip RetirePlaceholder).
                        e.Solid = Solid.Bsp;          // QC this.solid = SOLID_BSP;
                        e.MoveType = MoveType.Push;
                        if (!string.IsNullOrEmpty(e.Model))
                            Api.Entities.SetModel(e, e.Model); // QC _setmodel(this, this.mdl): resolve "*N" brush bounds
                        aslt.StageWall(e.Target, e.Model, e);
                        return; // keep the world wall; skip RetirePlaceholder.
                    case "target_assault_roundend":
                        aslt.AddRoundEnd(); // QC target_assault_roundend: marks the chain's terminal target.
                        break;
                    case "target_assault_roundstart":
                        // QC target_assault_roundstart: sets assault_attacker_team = NUM_TEAM_1 (already the port's
                        // Activate() default) and arms the round. Arming happens in ResolveObjectiveGraph (post-spawn).
                        break;
                }
                RetirePlaceholder(e);
            },
            // [T36] Nexball goals + balls (common/gametypes/gametype/nexball/sv_nexball.qc). Goals carry their team
            // or a GOAL_FAULT/GOAL_OUT sentinel in e.Team (the spawnfunc stamped it, incl. the ball_redgoal swap);
            // the ball spawnfuncs place the world ball (BallHome is set inside SpawnBall = QC spawnorigin).
            Nexball nb => e =>
            {
                switch (e.ClassName)
                {
                    case "nexball_basketball":
                    case "nexball_football":
                        nb.SpawnBall(e.Origin); // QC SpawnBall: relocates + sets bounce/think; sets BallHome
                        break;
                    default: // "nexball_goal" (every goal/fault/out funnels here)
                        nb.SpawnGoal((int)e.Team, e.Origin); // team or Nexball.GoalFault/GoalOut sentinel
                        break;
                }
                RetirePlaceholder(e);
            },
            // [T36] Invasion spawnpoints / waves / round-end (common/gametypes/gametype/invasion/sv_invasion.qc).
            Invasion inv => e =>
            {
                switch (e.ClassName)
                {
                    case "invasion_spawnpoint":
                        inv.AddSpawnPoint(e.Origin); // QC g_invasion_spawns
                        break;
                    case "invasion_wave":
                        inv.AddWave(e.Cnt, e.Spawnmob); // QC .cnt = wave number, .spawnmob = monster list
                        break;
                    case "target_invasion_roundend":
                        // QC target_invasion_roundend: counts a STAGE level-end objective; touching it (in STAGE)
                        // fires target_invasion_roundend_use → TriggerRoundEnd. Give the placeholder a touch volume
                        // so a STAGE map's end-trigger works, then keep it (do NOT retire — it must stay touchable).
                        inv.AddRoundEnd();
                        // QC target_invasion_roundend_use(actor): pass the touching player so Invasion can require
                        // >= ceil(realplayers * count) distinct REAL players (default count=0.7) to have reached
                        // the end before winning — a single touch no longer ends a multi-player STAGE map.
                        e.Touch = (self, other) => { if (other is Player p && !p.IsDead) inv.TriggerRoundEnd(p); };
                        e.Solid = Solid.Trigger;
                        return; // keep the edict alive (STAGE round-end trigger); skip RetirePlaceholder.
                }
                RetirePlaceholder(e);
            },
            // [T36] CTS start/stop timers (server/race.qc target_checkpoint_setup, gated !g_race && !g_cts).
            Cts cts => e =>
            {
                switch (e.ClassName)
                {
                    case "target_startTimer":
                        cts.SpawnStartTimer(e.Origin); // QC race_checkpoint = 0 (start); touch begins the run
                        break;
                    case "target_stopTimer":
                        cts.SpawnStopTimer(e.Origin);  // QC race_checkpoint = -2 (finish); touch folds the run time
                        break;
                    // target_checkpoint (defrag intermediate) + trigger_race_checkpoint are Race features; the port's
                    // CTS is a single start→stop course, so they're consumed as no-ops here (recon: LOW priority).
                }
                RetirePlaceholder(e);
            },
            _ => null, // non-objective gametype: ignore objective placements
        };
    }

    /// <summary>Stable control-point id generator for Onslaught map placements (the POJO graph keys by id).</summary>
    private int _nextOnsCpId;

    /// <summary>Retire the placeholder edict the lump spawned for an objective (the gametype spawned the real one).</summary>
    private static void RetirePlaceholder(Entity e)
    {
        if (Api.Services is not null)
            Api.Entities.Remove(e);
    }

    private float _nextTeamBalanceTime;

    /// <summary>
    /// QC <c>TeamBalance_AutoBalanceBots</c> on a timer: in a team game with autobalance enabled
    /// (<c>g_balance_teams</c>), periodically move the lowest-scoring bot from the largest team to the smallest
    /// when the teams are uneven (≥2 gap), force-killing it (DEATH_AUTOTEAMCHANGE) so it respawns on the new
    /// team. Uses the skill-weighted <see cref="Teamplay"/> helpers. Throttled to once every few seconds.
    /// </summary>
    private void BalanceTeamsTick()
    {
        if (!Teamplay.IsTeamGame || GameStopped)
            return;
        if (Cvars.Float("g_balance_teams") == 0f && Cvars.Float("g_balance_teams_prevent_imbalance") == 0f)
            return;
        if (Time < _nextTeamBalanceTime)
            return;
        _nextTeamBalanceTime = Time + 3f; // QC re-checks balance periodically, not every frame

        if (!Teamplay.TeamsAreUneven(Clients.Players))
            return;
        Player? moved = Teamplay.AutoBalanceBots(Clients.Players);
        if (moved is not null)
            Teamplay.KillPlayerForTeamChange(moved); // respawns on the new team via the normal respawn path
    }

    private void DriveGametypeFrame()
    {
        // The MatchController owns the FFA respawn loop + leader recompute for Deathmatch.
        if (GameType is Deathmatch)
        {
            // Respawn due players via ClientManager so the PlayerSpawn hook fires (MatchController.Spawn
            // would bypass it). We mirror MatchController.Tick's "respawn when due, unless match ended".
            RespawnDuePlayers();
            Match.Deathmatch?.RecomputeLeader(Clients.Players);
            return;
        }

        // Team / objective modes: respawn due players (non-round modes) and run each gametype's per-frame
        // resolution (QC the gametype's StartFrame/CheckRules slice). Round-based modes gate their own
        // respawns via RespawnDuePlayers (skipped while a round is live).
        RespawnDuePlayers();
        BalanceTeamsTick();
        switch (GameType)
        {
            case Duel duel: duel.RecomputeLeader(Clients.Players); break; // 1v1 FFA: per-frame leader + frag-limit checkrules (QC WinningConditionHelper)
            case Mayhem m: m.RecomputeLeader(Clients.Players); break;   // FFA: leader + point/lead limit
            case TeamMayhem tm: tm.UpdateLeaderAndCheckLimit(); break;  // team: ST_SCORE leader + limit
            case Tdm tdm: tdm.UpdateLeaderAndCheckLimit(); break;
            // CA: the round is now resolved on the LIVE round handler path (OnEndFrame → Rounds.Think → CA.CheckWinner),
            // so DON'T also call CheckRound here (that would double-award the round). The roster is fed via _roundPrep.
            case ClanArena: break;
            case Ctf ctf: ctf.Tick(); ctf.UpdateLeaderAndCheckLimit(); ctf.UpdateCaptureShields(Clients.Players); break;
            case Domination dom: dom.Tick(); break;                     // tick variant scores; round variant no-ops here
            case Onslaught ons:
                ons.GameStartTime = GameStartTime;                      // sync game_starttime for the overtime gate
                ons.Tick();                                             // QC: drive the round handler + overtime generator-decay
                break;
            case KeyHunt kh: kh.Tick(); break;
            case FreezeTag ft:
                // QC PlayerPreThink revive loop: feed the live roster, then accumulate/decay revive progress and
                // auto-thaw frozen players each frame (without this the thaw ring never fills + nobody is revived).
                // The round itself is now resolved on the LIVE round handler path (OnEndFrame → Rounds.Think →
                // FT.CheckWinner), so DON'T also call CheckRound here (that would double-award the round).
                ft.SetRoster(Clients.Players);
                ft.ReviveTick(Simulation.FrameTime);
                break;
            case TeamKeepaway tka: tka.Tick(Simulation.FrameTime); break;
            case Nexball nb:
                // QC MUTATOR_HOOKFUNCTION(nb, PlayerPreThink) + nexball_setstatus: position the carried view-ball
                // in front of the carrier and enforce the forteam auto-return, then re-check the goal/lead limit.
                nb.GameStartTime = GameStartTime; // QC game_starttime — first ball release fires at match start
                nb.SeedTeamsFromGoals();          // QC nb_spawnteams: derive ranked teams from the goal entities
                nb.CarryFrame();
                nb.CheckGoalLimit();
                break;
            case Assault aslt:
                // QC WinningCondition_Assault(), run per-frame from CheckRules_World: if the attackers already
                // destroyed the core this frame (DestroyFinalObjective latched WinningTeam off the live damage
                // chain) the match ends; otherwise once the round's time limit elapses with the core intact the
                // defenders win (TimeLimitReached). Sync game_starttime first so the timelimit gate is correct.
                aslt.GameStartTime = GameStartTime;
                aslt.DriveFrame(Time);
                break;
            case Keepaway ka: ka.Tick(Simulation.FrameTime); break;
            case LastManStanding lms:
                // QC game_starttime / warmup_stage mirrors: the win condition + life-loss are frozen pre-match
                // (WinningCondition_LMS / GiveFragsForKill both early-return while warmup || time <= game_starttime).
                lms.GameStartTime = GameStartTime;
                lms.InWarmup = Warmup.WarmupStage;
                lms.UpdateLeaders();
                // QC SV_StartFrame: the leader radar-visibility window + VISIBLE_LEADER/OTHER centerprint edges
                // (suppressed during intermission, matching the QC early-return).
                if (!Intermission.Running)
                    lms.DriveLeaderVisibility();
                lms.CheckWinningCondition();
                break;
            case Survival surv:
                // QC: the round is now resolved on the LIVE round handler path (OnEndFrame → Rounds.Think →
                // canEnd → CheckWinningCondition, role-assign at cnt==0 → AssignRoles), with surv.Handler timing
                // mirrored via _roundSync. So DON'T tick surv.Handler here (that would double-drive its own
                // countdown). The roster is fed via _roundPrep. The side-wipe latch is also checked on every
                // death (OnDeath → CheckWinningCondition); re-run it here as an idempotent per-frame safety net
                // (the RoundOver guard makes a second call within a round a no-op).
                surv.SetRoster(Clients.Players);
                surv.CheckWinningCondition();
                break;
            case Invasion inv:
                inv.PlayerCount = Clients.Players.Count;   // QC numplayers — scales the monster skill
                inv.Tick();                                // QC SV_StartFrame: drive the wave fill / win check
                inv.CheckPointLimit();
                break;
            case Race race: race.Tick(Time); race.CheckWinningCondition(); break;
            case Cts cts: cts.Tick(Time); break;
        }
    }

    /// <summary>
    /// Respawn every player whose <see cref="Player.RespawnTime"/> has elapsed (QC the StartFrame respawn
    /// check), going through <see cref="ClientManager.Spawn"/> so PlayerSpawn fires. Blocked once the match
    /// ended or during intermission (QC game_stopped gate). In a round mode, dead players stay out until the
    /// round resets, so we skip the respawn while a round is live.
    /// </summary>
    private void RespawnDuePlayers()
    {
        // The per-player DEAD_* respawn machine (DeadPlayerThink, run from OnClientMove each tick) now owns the
        // normal respawn — button-to-respawn at stock defaults (humans press fire, bots press jump while
        // DEAD_DEAD), and forced respawn with g_forced_respawn.
        // This stays only as a SAFETY net for a dead player that somehow isn't being driven by OnClientMove
        // (e.g. not in the sim client list): force it back in after its forced ceiling elapses so it can't get
        // stuck dead. Skipped for round modes and once the match ended (QC game_stopped gate).
        if (MatchEnded || Intermission.Running)
            return;
        if (Rounds is { IsRoundStarted: true })
            return; // round-based: no mid-round respawns

        float now = Time;
        var players = Clients.Players;
        for (int i = 0; i < players.Count; i++)
        {
            Player p = players[i];
            // Only the safety case: a FORCED dead player whose ceiling has well passed but the DEAD_* machine
            // never advanced (no OnClientMove driver). Gating on RESPAWN_FORCE is critical — a human at stock
            // defaults (g_forced_respawn 0) deliberately waits on the kill-cam until they press fire, and must
            // NOT be auto-respawned here. Live/forced players are normally respawned by DeadPlayerThink.
            if (p.IsDead && !p.IsObserver && (p.RespawnFlags & RespawnFlag.Force) != 0
                && p.RespawnTimeMax > 0f && now >= p.RespawnTimeMax + 1f)
                Clients.Spawn(p);
        }
    }

    /// <summary>
    /// QC <c>PlayerThink</c> dead branch (server/client.qc:2359-2438): the per-tick respawn state machine for a
    /// dead player. On the death edge it computes the respawn timing (<see cref="RespawnTiming.Calculate"/>),
    /// then advances <see cref="DeadFlag"/> by this tick's fire/jump/secondary/use buttons —
    /// DYING→DEAD→RESPAWNABLE→RESPAWNING — respawning via <see cref="ClientManager.Spawn"/> once
    /// <see cref="DeadFlag.Respawning"/> and the respawn time has passed. At stock defaults
    /// (<c>g_forced_respawn 0</c>) the player must press+release fire after the delay; with the
    /// <see cref="RespawnFlag.Force"/> flag it auto-respawns at <see cref="Player.RespawnTimeMax"/>. A BOT runs
    /// this same machine but advances it through its own input (BotBrain presses jump while DEAD_DEAD, QC
    /// bot.qc:147) — it is not specially forced (matching QC client.qc:1483-1484).
    /// Also maintains the networked <see cref="Player.RespawnTimeStat"/> (QC STAT(RESPAWN_TIME)).
    /// </summary>
    private void DeadPlayerThink(Player p, IMovementInput input)
    {
        // Don't respawn while the match is over / between rounds (QC game_stopped + round gate). The kill-cam
        // still holds; STAT(RESPAWN_TIME) is suppressed so the client doesn't show a bogus countdown.
        bool respawnAllowed = !MatchEnded && !Intermission.Running && !(Rounds is { IsRoundStarted: true });
        if (!respawnAllowed)
        {
            p.RespawnTimeStat = 0f;
            return;
        }

        float now = Time;

        // Death edge: compute the respawn timing once (RespawnTimeMax is reset to 0 on spawn, and Calculate sets
        // it > 0, so this runs exactly once per death). Overrides any flat delay a gametype obituary set.
        if (p.RespawnTimeMax <= 0f)
        {
            // QC MUTATOR_HOOKFUNCTION(lms, CalculateRespawnTime): an eliminated (0-lives) LMS player is denied a
            // respawn (RESPAWN_SILENT), and a living player gets the dynamic delay (scaled by lives behind the
            // leader). LMS owns the timing entirely (true) → skip the generic schedule; otherwise fall through.
            bool handled = GameType is LastManStanding lmsRt && lmsRt.ApplyRespawnTiming(p);
            if (!handled)
                RespawnTiming.Calculate(p, Clients.Players, Teamplay.IsTeamGame, GameType?.NetName);
        }

        // QC RESPAWN_DENY: respawning is blocked entirely; the player stays dead (e.g. eliminated in a round).
        if ((p.RespawnFlags & RespawnFlag.Deny) != 0)
        {
            p.RespawnTimeStat = 0f;
            return;
        }

        bool forced = (p.RespawnFlags & RespawnFlag.Force) != 0;
        // QC button_pressed = ATCK | JUMP | ATCK2 | HOOK | USE. (Hook is impulse-driven here, so omitted.) A bot
        // has no input stream, so it relies on the Force flag (set for bots in RespawnTiming) to advance.
        bool button = input.ButtonAttack1 || input.ButtonJump || input.ButtonAttack2 || input.ButtonUse;

        switch (p.DeadState)
        {
            case DeadFlag.Dying:
                if (forced && !(p.RespawnTime < p.RespawnTimeMax))
                    p.DeadState = DeadFlag.Respawning;
                else if (!button || (now >= p.RespawnTimeMax && forced))
                    p.DeadState = DeadFlag.Dead;
                break;
            case DeadFlag.Dead:
                if (button)
                    p.DeadState = DeadFlag.Respawnable;
                else if (now >= p.RespawnTimeMax && forced)
                    p.DeadState = DeadFlag.Respawning;
                break;
            case DeadFlag.Respawnable:
                if (!button || forced)
                    p.DeadState = DeadFlag.Respawning;
                break;
            case DeadFlag.Respawning:
                if (now > p.RespawnTime)
                {
                    p.RespawnTime = now + 1f;       // QC: only retry once a second
                    p.RespawnTimeMax = p.RespawnTime;
                    Clients.Spawn(p);
                    return;
                }
                break;
        }

        // QC STAT(RESPAWN_TIME) (client.qc:2421-2436): the client shows the countdown / "press fire" prompt off
        // this. Negated while RESPAWNING so the client knows a respawn is imminent; 0 while SILENT.
        if ((p.RespawnFlags & RespawnFlag.Silent) != 0)
            p.RespawnTimeStat = 0f;
        else
            p.RespawnTimeStat = p.DeadState == DeadFlag.Respawning ? -p.RespawnTime : p.RespawnTime;
    }

    // =============================================================================================
    // win condition + intermission (QC CheckRules_World → NextLevel)
    // =============================================================================================

    private bool _nextLevelFired;
    private bool _mapFlowStarted;
    private bool _mapChangeApplied;

    /// <summary>The map the end-of-match flow selected to switch to (QC the changelevel target), or "" until chosen.</summary>
    public string SelectedNextMap { get; private set; } = "";

    private void CheckRulesAndIntermission()
    {
        // [T42] QC CheckRules_World (server/world.qc:1725-1861): the global overtime / sudden-death win layer.
        // A tied timed match enters overtime/sudden-death instead of drawing; a decided match (or an expired
        // sudden death) ends. Runs only while the match is live (QC returns early if intermission_running).
        if (!Intermission.Running)
            RunCheckRulesWorld();

        // QC NextLevel: fire the once-per-match end hooks the moment intermission begins (winners, event log,
        // player-stats report, campaign decision, demo stop).
        if (Intermission.Running && !_nextLevelFired)
        {
            _nextLevelFired = true;
            NextLevel();
        }

        // advance the intermission timer (QC IntermissionThink). When ReadyToChangeLevel flips we run the
        // end-of-match map flow (vote / rotate / override), then apply the chosen map.
        Intermission.Think();

        if (Intermission.ReadyToChangeLevel)
            DriveEndOfMatchMapFlow();
    }

    /// <summary>
    /// [T42] QC <c>CheckRules_World</c> body (server/world.qc:1741-1860): the overtime / sudden-death cascade.
    /// Computes the effective timelimit (warmup/campaign/start-aware), then drives the timelimit→overtime→
    /// sudden-death decision via <see cref="OverTime"/>, resolving the gametype's limit-reached latch
    /// (<see cref="MatchHasEnded"/>) × tie report (<see cref="GameType.ReportsTie"/>) into a
    /// <see cref="WinningCode"/>. Enters intermission only once a winner is truly decided (a tie keeps playing).
    /// </summary>
    private void RunCheckRulesWorld()
    {
        // --- compute the effective limits (QC world.qc:1741-1763) -------------------------------------
        // timelimit/fraglimit are disabled during warmup or before game start; campaign uses the same reads.
        float timelimitMinutes = Cvars.TimeLimitMinutes;   // QC autocvar_timelimit (minutes)
        float timelimit = timelimitMinutes * 60f;          // QC timelimit = autocvar_timelimit * 60 (seconds)

        if (Warmup.WarmupStage || Time <= GameStartTime)   // QC: <= to avoid a glitch on the very start tic
            timelimit = 0f;                                // timelimit is not made for warmup / the pre-game window

        // QC: endmatch / negative timelimit ends the match immediately (handled by EndMatch elsewhere); a
        // positive timelimit is offset by game_starttime so it counts from the real match start.
        if (timelimit > 0f)
            timelimit += GameStartTime;

        // --- the cascade (QC world.qc:1765-1860) ------------------------------------------------------
        int overtimesPrev = OverTime.Overtimes;            // QC overtimes_prev
        bool wantOvertime = false;                         // QC wantovertime

        if (OverTime.InSuddenDeath)
        {
            // QC: emit the one-shot "Overtime has begun!" warning the first tick sudden death is armed.
            OverTime.TickSuddenDeathWarning();
        }
        else if (timelimit != 0f && Time >= timelimit)
        {
            // QC CheckRules_World rc hook (sv_race.qc:360-378): in the "qualifying THEN race" mode the elapsed
            // qualifying timelimit does NOT end the match — it CONVERTS the warmup-qualifying session into a real
            // race (WinningCondition_QualifyingThenRace → reset_map → reset_map_global). Drive that transition
            // here: TransitionQualifyingToRace clears g_race_qualifying, restores the stashed race limits, re-runs
            // the score rules and resets every racer; then re-arm the match clock from now so the race phase runs
            // its restored timelimit. Returns false (and falls through) when it's not a qualifying==2 race.
            if (GameType is Race rcQual && rcQual.TransitionQualifyingToRace())
            {
                GameStartTime = Time;          // the race phase starts now (re-offsets the restored timelimit)
                Warmup.GameStartTime = Time;
                return;                        // skip the overtime/intermission cascade this tick
            }
            // QC: otherwise the time limit elapsed with no latched winner → try to add an overtime (true) or arm
            // sudden death (false).
            wantOvertime |= OverTime.InitiateSuddenDeath(Time);
        }

        // QC: sudden death ran out → the match is decided; end it now.
        if (OverTime.InSuddenDeath && Time >= OverTime.SuddenDeathEnd)
        {
            EnterIntermissionWithLeader();
            return;
        }

        // QC WinningCondition_Scores: limit_reached (the gametype's MatchEnded latch) × equality (its tie
        // report) → a winning code. RanOutOfSpawns / the CheckRules_World mutator hook are not ported here.
        bool limitReached = MatchHasEnded(out Player? winner, out int winnerTeam);
        bool equality = GameType is not null && GameType.ReportsTie(Clients.Players);
        WinningCode status = OverTimeManager.ResolveWinningCode(limitReached, equality);

        // QC world.qc:1827-1832: a tie AT the limit starts sudden-death overtime now.
        if (status == WinningCode.StartSuddenDeathOvertime)
        {
            status = WinningCode.Never;
            OverTime.ArmSuddenDeathDecision();             // QC checkrules_overtimesadded = -1
            wantOvertime |= OverTime.InitiateSuddenDeath(Time);
        }

        // QC world.qc:1838-1844: if an overtime was requested, either extend the timelimit (still tied) or
        // declare the win (a decisive condition fired this tick).
        if (wantOvertime)
        {
            if (status == WinningCode.Never)
                OverTime.InitiateOvertime(timelimitMinutes); // QC InitiateOvertime() (extends timelimit)
            else
                status = WinningCode.Yes;
        }

        // QC world.qc:1846-1848: while in sudden death, any non-tie (or the timer expiring) ends the match.
        if (OverTime.InSuddenDeath)
            if (status != WinningCode.Never || Time >= OverTime.SuddenDeathEnd)
                status = WinningCode.Yes;

        // QC world.qc:1850-1860: a decided match ends. If sudden death had only just begun this tick, revert
        // it so the win lands cleanly; then go to intermission.
        if (status == WinningCode.Yes)
        {
            OverTime.RevertSuddenDeathIfJustBegun(overtimesPrev);
            EnterIntermission(winner, winnerTeam);
        }
    }

    /// <summary>Enter intermission with the gametype's decided winner/team (QC NextLevel's winner latch).</summary>
    private void EnterIntermission(Player? winner, int winnerTeam)
    {
        int playerCount = Clients.PlayerCount;
        if (winnerTeam != Teams.None)
            Intermission.BeginTeam(winnerTeam, playerCount);
        else
            Intermission.Begin(winner, playerCount);
    }

    /// <summary>Enter intermission crediting the current score leader / leading team (QC the timelimit-end path,
    /// where no fraglimit winner is latched — the top of the board wins).</summary>
    private void EnterIntermissionWithLeader()
    {
        if (MatchHasEnded(out Player? winner, out int winnerTeam) && (winner is not null || winnerTeam != Teams.None))
        {
            EnterIntermission(winner, winnerTeam);
            return;
        }
        Player? leader = Scores.Leader;
        int leadTeam = Teamplay.IsTeamGame ? Scores.LeaderTeam : Teams.None;
        EnterIntermission(leadTeam != Teams.None ? null : leader, leadTeam);
    }

    /// <summary>
    /// QC <c>NextLevel</c>: the once-per-match end-of-match bookkeeping fired when intermission begins — mark the
    /// winners (so campaign/playerstats see <see cref="Player.Winning"/>), emit the <c>:gameover</c> event-log
    /// line, build the player-stats game report, run the campaign win/lose decision, and stop demo recording.
    /// </summary>
    private void NextLevel()
    {
        MarkWinners();

        if (Cvars.Bool("sv_eventlog"))
        {
            GameLog.GameOver();
            GameLog.Close();
        }

        // QC PlayerStats_GameReport(true): build the per-player/per-team report (upload is an engine concern).
        PlayerStats.GameReport(finished: true, Clients.Players, Time, Teamplay.IsTeamGame);

        // QC CampaignPreIntermission: decide won/lost + persist progress (only when in campaign).
        if (Cvars.Bool("g_campaign") && !Campaign.Aborted)
            Campaign.PreIntermission(
                Clients.Players.Where(p => !p.IsBot),
                p => p.Winning,
                checkrulesEquality: false,
                cheatCount: Cheats.CheatCountTotal,
                timeNow: Time);

        // QC the demo stop at match end.
        Demo.OnMatchEnd();
    }

    /// <summary>QC the winner latch: set <see cref="Player.Winning"/> for the FFA leader / each winning-team member.</summary>
    private void MarkWinners()
    {
        foreach (Player p in Clients.Players)
            p.Winning = false;
        if (Intermission.WinnerTeam != Teams.None)
        {
            foreach (Player p in Clients.Players)
                if ((int)p.Team == Intermission.WinnerTeam) p.Winning = true;
        }
        else if (Intermission.Winner is not null)
        {
            Intermission.Winner.Winning = true;
        }
    }

    /// <summary>
    /// QC the post-intermission map change (DoNextMapOverride → MapVote_Init/GotoNextMap → Map_Goto). Runs once
    /// the intermission timer elapses: campaign / queued-nextmap / samelevel overrides first; else a map vote
    /// (when votable + players present), else a silent rotation. The chosen map is published on
    /// <see cref="SelectedNextMap"/> and routed to the host's change-level pipeline.
    /// </summary>
    private void DriveEndOfMatchMapFlow()
    {
        if (_mapChangeApplied)
            return;

        // QC PlayerStats_GameReport_DelayMapVote: don't start the vote until the report is done.
        if (PlayerStats.DelayMapVote)
            return;

        if (!_mapFlowStarted)
        {
            _mapFlowStarted = true;

            // ---- DoNextMapOverride (QC the override priority) ----
            // campaign: advance/replay through the campaign transition (sets QueuedNextMap).
            if (Cvars.Bool("g_campaign") && !Campaign.Aborted)
            {
                if (!Campaign.PostIntermission())
                {
                    ApplyMapChange(""); // campaign complete: no level change
                    return;
                }
            }
            // an explicit queued map (gotomap / nextmap vote / campaign) wins.
            if (!string.IsNullOrEmpty(QueuedNextMap))
            {
                ApplyMapChange(QueuedNextMap);
                return;
            }
            // samelevel: restart the same map.
            if (Cvars.Bool("samelevel"))
            {
                ApplyMapChange(MapName);
                return;
            }

            // ---- otherwise: a map vote, or a silent rotation ----
            if (Cvars.Int("g_maplist_votable") > 0 && Clients.PlayerCount > 0)
            {
                Rotation.Init(MapName);
                var ballot = Rotation.BuildBallot(Cvars.Int("g_maplist_votable"));
                if (ballot.Count > 1)
                {
                    // QC MapVote_AddVotableMaps seeds player suggestions ahead of the rotation maps.
                    var suggestions = Commands.MapSuggestions
                        .Select(m => (Map: m, Suggester: ""))
                        .ToList();
                    MapVote.Start(ballot, Clients.PlayerCount, Cvars.FloatOr("g_maplist_votable_timeout", 30f), suggestions);
                    return; // the vote runs; Tick resolves it below
                }
                // a degenerate ballot (0/1 candidate) — fall through to the rotation pick.
            }

            // no vote: silent rotation (QC GotoNextMap).
            Rotation.Init(MapName);
            string next = Rotation.GetNextMap();
            ApplyMapChange(string.IsNullOrEmpty(next) ? MapName : next);
            return;
        }

        // ---- the map vote is running: tick it, apply the winner when it finishes ----
        if (MapVote.Running)
        {
            MapVote.Tick();
            return;
        }
        // QC MapVote_Think: hold the result for the 1 s winner-reveal delay before the level actually changes.
        if (MapVote.Finished && MapVote.ReadyToChangeLevel && !_mapChangeApplied)
            ApplyMapChange(string.IsNullOrEmpty(MapVote.WinningMap) ? MapName : MapVote.WinningMap);
    }

    /// <summary>Apply the chosen next map (QC Map_Goto): mark it recent + route it to the host's changelevel.</summary>
    private void ApplyMapChange(string map)
    {
        _mapChangeApplied = true;
        SelectedNextMap = map;
        if (!string.IsNullOrEmpty(map))
        {
            Rotation.MarkAsRecent(map);
            Commands.ChangeLevelHandler?.Invoke(map);
        }
    }

    /// <summary>True once the active gametype's win condition has tripped; reports the winner (player or team).</summary>
    private bool MatchHasEnded(out Player? winner, out int winnerTeam)
    {
        winner = null;
        winnerTeam = Teams.None;
        switch (GameType)
        {
            // FFA modes: report the player leader/winner.
            case Deathmatch dm:
                if (!dm.MatchEnded) return false;
                winner = dm.Leader; return true;
            case Mayhem m:
                if (!m.MatchEnded) return false;
                winner = m.Leader; return true;
            case Keepaway ka:
                if (!ka.MatchEnded) return false;
                winner = ka.Leader; return true;
            case Duel duel:
                if (!duel.MatchEnded) return false;
                winner = duel.Leader; return true;
            case LastManStanding lms:
                if (!lms.MatchEnded) return false;
                winner = lms.Winner; return true;
            case Race race:
                if (!race.MatchEnded) return false;
                winner = race.Winner; return true;
            case Invasion inv:
                return inv.MatchEnded; // co-op: no single winner
            case Cts cts:
                return cts.MatchEnded;

            // team modes with a per-team total: report the leading team.
            case TeamMayhem tm:
                if (!tm.MatchEnded) return false;
                winnerTeam = tm.LeaderTeam; return true;
            case Tdm tdm:
                if (!tdm.MatchEnded) return false;
                winnerTeam = tdm.LeaderTeam; return true;
            case ClanArena ca:
                if (!ca.MatchEnded) return false;
                winnerTeam = ca.LeaderTeam; return true;
            case Ctf ctf:
                if (!ctf.MatchEnded) return false;
                winnerTeam = ctf.LeaderTeam; return true;
            case Domination dom:
                if (!dom.MatchEnded) return false;
                winnerTeam = dom.LeaderTeam; return true;
            case KeyHunt kh:
                if (!kh.MatchEnded) return false;
                winnerTeam = kh.LeaderTeam; return true;
            case FreezeTag ft:
                if (!ft.MatchEnded) return false;
                winnerTeam = ft.LeaderTeam; return true;

            // objective modes that latch a winning team directly.
            case Onslaught ons:
                if (!ons.MatchEnded) return false;
                winnerTeam = ons.WinningTeam; return true;
            case TeamKeepaway tka:
                if (!tka.MatchEnded) return false;
                winnerTeam = tka.WinningTeam; return true;
            case Nexball nb:
                if (!nb.MatchEnded) return false;
                winnerTeam = nb.WinningTeam; return true;
            case Assault aslt:
                if (!aslt.MatchEnded) return false;
                winnerTeam = aslt.WinningTeam; return true;

            default:
                return false;
        }
    }

    // =============================================================================================
    // map-entity spawning (QC worldspawn → BSP entity lump → spawnfunc_CLASSNAME)
    // =============================================================================================

    /// <summary>
    /// Spawn each parsed map entity (QC SV_OnEntityPreSpawnFunction → callfunction spawnfunc_CLASSNAME). For
    /// each dict: mint an engine edict, copy classname/origin/angles (+ the recognized fields) onto it, then
    /// dispatch to the registered spawnfunc. A class WITHOUT a registered spawnfunc keeps its edict alive
    /// (classname/origin set) and is recorded in <see cref="UnhandledClasses"/> — matching DP's
    /// SV_OnEntityNoSpawnFunction, which leaves the entity in place rather than deleting it. This is what lets
    /// passive marker entities still be found by classname/targetname: spawnpoints (info_player_deathmatch /
    /// info_player_start / info_player_team*) have no active spawnfunc in this port, yet
    /// <see cref="SpawnSystem.SelectSpawnPoint"/> finds them via <c>FindByClass</c> precisely because we keep
    /// them here. "worldspawn" is handled specially via <see cref="ApplyWorldspawn"/> (it configures globals).
    /// </summary>
    private void SpawnMapEntities()
    {
        SpawnedEntityCount = 0;
        _unhandledClasses.Clear();

        // QC SV_OnEntityPreSpawnFunction gate: an entity whose gametypefilter (or Q3/QL compat keys) excludes
        // it for the active gametype is deleted before its spawnfunc runs. The context (gametype short name,
        // teamplay, have_team_spawns) is the same trio DP passes to isGametypeInFilter.
        XonoticGodot.Engine.Collision.MapEntityFilter.GametypeContext gtFilter = BuildGametypeContext();

        foreach (EntityDict dict in _mapEntities)
        {
            string cls = dict.ClassName;
            if (string.IsNullOrEmpty(cls))
                continue;
            if (cls == "worldspawn")
            {
                ApplyWorldspawn(dict);  // QC SVC worldspawn: gravity / fog / map cvar overrides
                continue;
            }

            // Drop a gametype-filtered entity (e.g. a Race-only func_wall in a DM match) — QC delete(this).
            if (!XonoticGodot.Engine.Collision.MapEntityFilter.ShouldKeepEntity(dict.Fields, gtFilter))
                continue;

            Entity e = Api.Entities.Spawn();
            e.ClassName = cls;
            ApplyDictFields(e, dict);

            // QC SV_OnEntityPreSpawnFunction: set_movetype(this, this.movetype) before the spawnfunc runs.
            // (We leave MoveType at its default None unless a field set it; the spawnfunc usually sets it.)

            if (SpawnFuncs.TrySpawn(cls, e))
            {
                SpawnedEntityCount++;
            }
            else
            {
                // No spawnfunc — KEEP the edict (it stays findable by classname/targetname, e.g. spawnpoints)
                // and note the class for diagnostics. DP's SV_OnEntityNoSpawnFunction does not delete it.
                if (!_unhandledClasses.Contains(cls))
                    _unhandledClasses.Add(cls);
            }
        }
    }

    /// <summary>
    /// Build the gametype context the map-entity filter (QC <c>isGametypeInFilter</c>) needs: the active
    /// gametype's short name + teamplay, plus <c>have_team_spawns</c> derived from a cheap pre-pass over the
    /// map entities (any <c>info_player_*</c> carrying a non-zero <c>team</c> — QC sets this while spawning
    /// spawnpoints). Computed once per <see cref="SpawnMapEntities"/>.
    /// </summary>
    private XonoticGodot.Engine.Collision.MapEntityFilter.GametypeContext BuildGametypeContext()
    {
        string shortName = GameType?.NetName ?? DefaultGameType;
        bool teamplay = Teamplay?.IsTeamGame ?? (GameType?.TeamGame ?? false);

        bool haveTeamSpawns = false;
        foreach (EntityDict d in _mapEntities)
        {
            if (!d.ClassName.StartsWith("info_player_", System.StringComparison.Ordinal))
                continue;
            if (d.Fields.TryGetValue("team", out string? tm) && float.TryParse(tm,
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                    out float tv) && tv != 0f)
            {
                haveTeamSpawns = true;
                break;
            }
        }

        return new XonoticGodot.Engine.Collision.MapEntityFilter.GametypeContext(shortName, teamplay, haveTeamSpawns);
    }

    /// <summary>Copy the recognized map-dict fields onto the edict (origin/angles + a few common keys).</summary>
    private static void ApplyDictFields(Entity e, EntityDict dict)
    {
        e.Origin = dict.Origin;
        e.OldOrigin = dict.Origin;
        e.Angles = dict.Angles;

        // A handful of common keys the spawnfuncs read off the edict (QC parses these generically). The full
        // key→field map is the engine's job (ED_ParseEdict); we wire the ones the ported map objects need.
        var f = dict.Fields;

        // DarkPlaces "anglehack" (PRVM_ED_ParseEdict): the QuakeEd convention writes a single-float `angle "X"`
        // for yaw, which the engine rewrites to `angles "0 X 0"`. Real maps orient teleport destinations, spawn
        // points, doors and directional triggers with `angle`, NOT the `angles` vector — without honoring it they
        // all default to yaw 0 (facing +X), so a teleporter flings the player due-east regardless of the dest's
        // facing and spawns face east. Apply it only when no explicit `angles` vector was given (the vector form,
        // when present, wins — matching DP, where real maps never carry both).
        if (e.Angles == Vector3.Zero && f.TryGetValue("angle", out var angleStr) && float.TryParse(angleStr,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float yaw))
            e.Angles = new Vector3(0f, yaw, 0f);

        if (f.TryGetValue("targetname", out var tn)) e.TargetName = tn;
        if (f.TryGetValue("target", out var tg)) e.Target = tg;
        if (f.TryGetValue("target2", out var tg2)) e.Target2 = tg2; // QC .target2 (onslaught_link's second node ref)
        if (f.TryGetValue("killtarget", out var kt)) e.KillTarget = kt;
        if (f.TryGetValue("message", out var msg)) e.Message = msg;
        if (f.TryGetValue("model", out var mdl)) e.Model = mdl;
        if (f.TryGetValue("spawnflags", out var sf) && int.TryParse(sf, out int sfi)) e.SpawnFlags = sfi;
        if (f.TryGetValue("team", out var tm) && float.TryParse(tm,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float tmf))
            e.Team = tmf;
        // [T52] target_fragsFilter's gate threshold (QC reads this.frags; quake3.qc:233-234 defaults it to 1 when
        //       unset). Without promoting the map's `frags` key, a mapper-set custom threshold (e.g. `frags "5"`)
        //       is silently lost and the filter falls back to 1, breaking CTS frag gates.
        if (f.TryGetValue("frags", out var fr) && float.TryParse(fr,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float frf))
            e.Frags = frf;

        // NPC keys (T14): the monster/turret/vehicle spawnfuncs + the monster_spawner read these off the edict.
        // `.skin` drives the random monster-skin override; `.spawnmob`/`.count`/`.monster_moveflags` configure a
        // monster_spawner (common/monsters/sv_spawner.qc).
        if (f.TryGetValue("skin", out var sk) && float.TryParse(sk,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float skf))
            e.Skin = skf;
        if (f.TryGetValue("spawnmob", out var smb)) e.Spawnmob = smb;
        if (f.TryGetValue("count", out var cnt) && int.TryParse(cnt, out int cnti)) e.Count = cnti;
        if (f.TryGetValue("monster_moveflags", out var mmf) && int.TryParse(mmf, out int mmfi)) e.MonsterMoveFlags = mmfi;

        // [T36] objective-mode keys read off the placeholder edict by the GametypeObjectiveSpawns sink (the sink
        // only sees the Entity, not the dict, so these must be promoted here): QC .cnt (invasion_wave wave number;
        // distinct from `count` → e.Count), .health (target_objective / func_assault_destructible objective health),
        // and .dmg (target_objective_decrease damage per activation; QC defaults it to 101 when unset/0).
        if (f.TryGetValue("cnt", out var cv) && int.TryParse(cv, out int cvi)) e.Cnt = cvi;
        if (f.TryGetValue("health", out var hp) && float.TryParse(hp,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float hpf))
            e.Health = hpf;
        if (f.TryGetValue("dmg", out var dg) && float.TryParse(dg,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float dgf))
            e.Dmg = dgf;
        // QC spawnfunc fields a mapper sets on a hand-placed monster (sv_monsters.qc / sv_spawner.qc): `.noalign`
        // skips drop-to-floor, `.monster_skill` overrides g_monsters_skill for that monster.
        if (f.TryGetValue("noalign", out var na) && float.TryParse(na,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float naf))
            e.NoAlign = naf != 0f;
        if (f.TryGetValue("monster_skill", out var msk) && int.TryParse(msk, out int mski)) e.MonsterSkill = mski;

        // --- sound / mover scalars that map objects read off the edict (target_speaker, doors, plats, etc.) ---
        if (f.TryGetValue("noise", out var ns)) e.Noise = ns;
        if (f.TryGetValue("noise1", out var ns1)) e.Noise1 = ns1;
        if (f.TryGetValue("noise2", out var ns2)) e.Noise2 = ns2;
        if (f.TryGetValue("noise3", out var ns3)) e.Noise3 = ns3;
        if (f.TryGetValue("volume", out var vol) && float.TryParse(vol,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float volf))
            e.Volume = volf;
        if (f.TryGetValue("atten", out var att) && float.TryParse(att,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float attf))
            e.Atten = attf;
        if (f.TryGetValue("speed", out var spd) && float.TryParse(spd,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float spdf))
            e.Speed = spdf;
        if (f.TryGetValue("wait", out var wt) && float.TryParse(wt,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float wtf))
            e.Wait = wtf;
        if (f.TryGetValue("lip", out var lp) && float.TryParse(lp,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float lpf))
            e.Lip = lpf;
        if (f.TryGetValue("height", out var ht) && float.TryParse(ht,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float htf))
            e.Height = htf;

        // [T48] content-tail keys (misc_laser / models.qc props / func_pointparticles emitter mdl+jitter+count /
        // func_rain/snow fall velocity+wind / target_music+trigger_music lifetime+fade_time+fade_rate /
        // func_wall/func_static solid override + distance-fade). The MapObjects-owned helper parses the same
        // extra key set onto the edict that the offline GameDemo.ApplyMapFields path already applies, so on the
        // live --host path props get their model, func_pointparticles gets Entity.Mdl set, weather keeps its
        // direction and music keeps its fades — instead of all of it being silently dropped. Runs BEFORE the
        // spawnfunc dispatch (SpawnMapEntities), so PointParticles/RainSnow/TargetMusic read the values.
        // Idempotent with the keys promoted above (angle anglehack, count, cnt) — same guards.
        XonoticGodot.Common.Gameplay.MapObjectFieldsExtra.Apply(e, f);

        // Re-link after setting origin so traces/find see the final placement.
        if (Api.Services is not null)
            Api.Entities.SetOrigin(e, e.Origin);
    }

    /// <summary>
    /// QC the <c>worldspawn</c> spawnfunc globals (server/world.qc): a map's worldspawn entity carries
    /// world-wide settings as fields. The Godot-free, gameplay-relevant slice is applied here — the
    /// <c>gravity</c> override (QC <c>cvar_set("sv_gravity")</c> / the per-map gravity), and any
    /// <c>{cvar} {value}</c>-style overrides the map ships. Visual-only keys (fog/skybox/sky) are recorded as
    /// cvars for the host/renderer; nothing here blocks the headless sim.
    /// </summary>
    private void ApplyWorldspawn(EntityDict dict)
    {
        var f = dict.Fields;

        // QC worldspawn gravity: a non-zero "gravity" key overrides sv_gravity for the map.
        if (f.TryGetValue("gravity", out var gs) && float.TryParse(gs,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float g)
            && g > 0f)
        {
            Cvars.Set("sv_gravity", g);
            Simulation.Gravity = g;   // mirror into the physics context (QC PRVM_serveredictfloat gravity)
        }
        else
        {
            // no per-map gravity: keep the physics context in sync with the (default-seeded) cvar.
            Simulation.Gravity = Cvars.Gravity;
        }

        // QC: worldspawn may carry presentation keys; stash them as cvars for the host (no sim effect).
        if (f.TryGetValue("fog", out var fog)) Cvars.Set("sv_fog", fog);
        if (f.TryGetValue("message", out var title)) Cvars.Set("sv_worldmessage", title);
        if (f.TryGetValue("author", out var author)) Cvars.Set("sv_worldauthor", author);

        // QC worldspawn music (server/world.qc line 970-973): the "music" key takes priority over "noise";
        // either provides the cdtrack when no mapinfo cdtrack was set by the host. This is the Q3/Nexuiz compat
        // path — real Xonotic maps use the mapinfo cdtrack line, but legacy/Q3 maps embed it in worldspawn.
        if (string.IsNullOrEmpty(CdTrack))
        {
            if (f.TryGetValue("music", out var wsMusic) && !string.IsNullOrEmpty(wsMusic))
                CdTrack = wsMusic;
            else if (f.TryGetValue("noise", out var wsNoise) && !string.IsNullOrEmpty(wsNoise))
                CdTrack = wsNoise;
        }
    }

    // =============================================================================================
    // match lifecycle commands (QC restart / endmatch / reset_map / ready)
    // =============================================================================================

    /// <summary>
    /// QC the <c>restart</c> command → <c>ReadyRestart</c>: restart the match — leave intermission, clear the
    /// scoreboard, re-arm the start countdown, reset every player + map object, and start a fresh match. The
    /// console <c>restart</c> command and a passed restart vote route here.
    /// </summary>
    public void RestartMatch()
    {
        Intermission.Reset();
        OverTime.Reset(); // [T42] clear the overtime / sudden-death checkrules state for the fresh match
        // re-arm the end-of-match flow + the per-match infrastructure so a fresh match runs clean.
        _nextLevelFired = _mapFlowStarted = _mapChangeApplied = false;
        SelectedNextMap = "";
        PlayerStats.ResetAll(Clients.Players, Teamplay.IsTeamGame ? Teams.Active(Teamplay.TeamCount) : null);
        Warmup.ReadyRestart(forceWarmupEnd: true);   // ends warmup, arms the countdown, runs ResetMap
        GameStartTime = Warmup.GameStartTime;
    }

    /// <summary>
    /// QC the <c>endmatch</c> command (<c>autocvar__endmatch</c> → CheckRules_World): end the current match
    /// immediately, sending it to intermission regardless of the score. The console <c>endmatch</c>/<c>gotomap</c>
    /// commands and a passed endmatch vote route here.
    /// </summary>
    public void EndMatch()
    {
        if (Intermission.Running)
            return;
        // Latch intermission with the current leader (FFA) or leading team, like a natural win.
        if (MatchHasEnded(out Player? winner, out int winnerTeam) && (winner is not null || winnerTeam != Teams.None))
        {
            if (winnerTeam != Teams.None) Intermission.BeginTeam(winnerTeam, Clients.PlayerCount);
            else Intermission.Begin(winner, Clients.PlayerCount);
        }
        else
        {
            // forced end with no decided winner: pick the current score leader (QC reports the top of the board).
            Player? leader = Scores.Leader;
            int leadTeam = Teamplay.IsTeamGame ? Scores.LeaderTeam : Teams.None;
            if (leadTeam != Teams.None) Intermission.BeginTeam(leadTeam, Clients.PlayerCount);
            else Intermission.Begin(leader, Clients.PlayerCount);
        }
    }

    /// <summary>
    /// QC <c>reset_map</c> (the Godot-free core): clear scores (unless a fake round-start), reset the round
    /// handler, re-spawn every player away from the living, and run each map object's reset. Wired to
    /// <see cref="WarmupController.ResetMap"/> so a ready-restart performs a full map reset.
    /// </summary>
    /// <param name="fakeRoundStart">QC <c>is_fake_round_start</c>: a round re-arm that must NOT wipe scores.</param>
    public void ResetMap(bool fakeRoundStart)
    {
        if (Time <= GameStartTime && !fakeRoundStart)
            Scores.ClearAll();                       // QC Score_ClearAll on a real restart

        // QC mayhem/tmayhem reset_map_players: zero each player's .total_damage_dealt on a round/map reset
        // (Score_ClearAll wipes SP_SCORE, but the damage accrual is a plain field). The other modes have no
        // analogue here.
        switch (GameType)
        {
            case Mayhem m: m.ResetMapPlayers(Clients.Players); break;
            case TeamMayhem tm: tm.ResetMapPlayers(Clients.Players); break;
            // QC MUTATOR_HOOKFUNCTION(cts, reset_map_global) → race_ClearRecords + race_PreparePlayer per client:
            // re-prepare every CTS runner (drop any in-progress run so they restart from the start timer). The
            // persistent top-99 records survive (QC keeps the ServerProgsDB table); only the in-memory run state
            // is cleared. The qualifying==2→race collapse is moot (CTS pins g_race_qualifying=1).
            case Cts c: c.ResetMapGlobal(Clients.Players); break;
            // QC MUTATOR_HOOKFUNCTION(lms, reset_map_players) + reset_map_global (sv_lms.qc:217-243): on a map/match
            // restart, re-liven every eliminated player (FRAGS_PLAYER_OUT_OF_GAME → FRAGS_PLAYER), zero + re-seed
            // LMS_RANK/LMS_LIVES, clear the leader flag/waypoint, and reset lms_lowest_lives=999. Without this the
            // per-player LmsState carried stale lives/rank/leader across matches (the round resets never touched it).
            case LastManStanding lms: lms.ResetMapPlayers(Clients.Players); break;
        }

        if (Rounds is not null)
            Rounds.Reset(GameStartTime);             // QC round_handler_Reset(game_starttime)

        // QC: run each (non-client) map entity's .reset callback, then re-spawn the players.
        ResetMapObjects();

        var players = Clients.Players;
        for (int i = 0; i < players.Count; i++)
        {
            Player p = players[i];
            p.Velocity = System.Numerics.Vector3.Zero;
            p.AVelocity = System.Numerics.Vector3.Zero;
            // QC status_effects reset_map_global hook (sv_status_effects.qc:114-123): removeall(NORMAL) "just to
            // get rid of the pickup sound" then clearall, so no effect timer survives a map/round reset. (The
            // following Clients.Spawn -> PutClientInServer also clearall's, but Base plays the removal sounds here.)
            StatusEffectsCatalog.RemoveAll(p, StatusEffectRemoval.Normal);
            PlayerStates.Of(p).OnSpawn();            // clear the regen/air/contents timers
            Clients.Spawn(p);                        // QC PutClientInServer per client
        }

        // [T38] QC end_minigames(): drop any live minigame sessions on a full map reset (not a fake round-start),
        // so a stale session doesn't leak across a restart on this persistent in-process world.
        if (!fakeRoundStart)
        {
            Minigames.EndAll();
            // QC Destroy_All_Notifications (all.qc:110-121) → MSG_CENTER_KILL CPID_Null → centerprint_KillAll():
            // a real map/match restart retracts every lingering centerprint group so stale lines (a held countdown,
            // a sticky message) don't survive the reset. CPID_Null (empty cpid) = kill all on the client.
            NotificationSystem.SendCenterKill(NotifBroadcast.All, null, null);
        }
    }

    /// <summary>
    /// QC the entity <c>.reset</c>/<c>.reset2</c> callback pass of reset_map: fire each non-client map
    /// entity's Use→reset via its think, returning movers to their spawn state. In this port the map objects
    /// expose their reset through the engine <see cref="Entity.Think"/>; we invoke any registered reset hook.
    /// </summary>
    private void ResetMapObjects()
    {
        // QC reset_map_global (server/world.qc): on a round/match restart, clear projectiles (FL_PROJECTILE
        // entities are deleted) and fire every NON-client map entity's .reset callback. The map objects
        // (doors/plats/buttons/breakables/rotating/secret-doors, plus logic gates / target relays / monsters)
        // install their own Entity.Reset at spawn; this single sweep returns each mover to its spawn state and
        // re-arms it (door_reset / plat_reset / button_reset / func_breakable_reset / func_rotating_reset /
        // secret_reset), so a mover caught mid-cycle at a restart snaps home instead of staying put.
        if (Api.Services is null)
            return;
        foreach (Entity e in new List<Entity>(ServerServices.ServerEntities.Inner.All))
        {
            if (e.IsFreed || e is Player) continue;
            if (e.ClassName == "projectile" || e.ClassName.StartsWith("weapon_proj", System.StringComparison.Ordinal))
            {
                Api.Entities.Remove(e);
                continue;
            }
            e.Reset?.Invoke(e);
        }
    }

    /// <summary>
    /// QC <c>assault_roundstart_use</c> (sv_assault.qc:153) + <c>MUTATOR_HOOKFUNCTION(as, TurretSpawn)</c>:
    /// the Assault per-round turret team swap. First seed any unteamed turret to the current attacker team (QC
    /// TurretSpawn: <c>if(!turret.team || turret.team == FLOAT_MAX) turret.team = assault_attacker_team</c> —
    /// done lazily here since the port spawns turrets with FLOAT_MAX), then swap every turret's team NUM_TEAM_1 ↔
    /// NUM_TEAM_2 and <c>turret_respawn</c> it (which doubles as the team change). Called once at round start
    /// (the FINDTARGET <c>assault_roundstart_use_this</c>) and again on the round-2 restart. Skipped in campaign
    /// (QC <c>assault_turrets_teamswap_forbidden</c> = true via ReadyRestart_Deny — campaign is a single round so
    /// the defenders must not be flipped). <paramref name="attackerTeam"/> is the live attacker team for the seed.
    /// </summary>
    private void AssaultTurretRoundstart(int attackerTeam)
    {
        if (Api.Services is null)
            return;
        // QC: campaign forbids the swap (single-round game — turrets keep their authored side).
        if (TargetUtilities.IsCampaign?.Invoke() ?? false)
            return;
        foreach (Entity e in new List<Entity>(ServerServices.ServerEntities.Inner.All))
        {
            if (e.IsFreed || e is Player)
                continue;
            if (!e.ClassName.StartsWith("turret_", System.StringComparison.Ordinal))
                continue;
            if (e.ClassName == "turret_projectile")
                continue; // projectiles aren't emplacements (QC g_turrets holds only the turret bases)

            // QC TurretSpawn hook: an unteamed turret (0 / FLOAT_MAX sentinel) joins the attacker team first.
            if (e.Team == 0f || e.Team == float.MaxValue)
                e.Team = attackerTeam;

            // QC assault_roundstart_use: swap NUM_TEAM_1 <-> NUM_TEAM_2, then turret_respawn (the team change).
            e.Team = ((int)e.Team == Teams.Red) ? Teams.Blue : Teams.Red;
            TurretAI.Respawn(e);
        }
    }

    /// <summary>
    /// QC <c>assault_new_round</c> (sv_assault.qc:207): swap every saved non-client team — the spawn-point edicts'
    /// <c>team_saved</c> — NUM_TEAM_1 ↔ NUM_TEAM_2 when the roles swap. In the port the info_player_attacker/
    /// defender spawns are retagged to <c>info_player_deathmatch</c> carrying an explicit Red/Blue
    /// <see cref="Entity.Team"/> (read live by SpawnSystem), so on round 2 we flip that team on each red/blue
    /// spawn edict — the new (blue) attackers then spawn at the geographic attacker spots instead of the
    /// defender ones. Only exact red/blue spots are touched (an FFA / no-team spawn is left alone).
    /// </summary>
    private void AssaultSwapSpawnTeams()
    {
        if (Api.Services is null)
            return;
        foreach (Entity e in new List<Entity>(ServerServices.ServerEntities.Inner.All))
        {
            if (e.IsFreed || e is Player)
                continue;
            if (e.ClassName != "info_player_deathmatch")
                continue;
            int team = (int)e.Team;
            if (team == Teams.Red) e.Team = Teams.Blue;        // QC team_saved NUM_TEAM_1 -> NUM_TEAM_2
            else if (team == Teams.Blue) e.Team = Teams.Red;   // QC team_saved NUM_TEAM_2 -> NUM_TEAM_1
        }
    }

    /// <summary>
    /// QC <c>ClientCommand_ready</c> (F4): toggle a player's ready flag during warmup. A ready majority ends
    /// warmup and restarts the match. Returns the new ready state. Exposed for a host/UI to call.
    /// </summary>
    public bool ToggleReady(Player p) => Warmup.ToggleReady(p);

    // =============================================================================================
    // helpers
    // =============================================================================================

    /// <summary>
    /// [T39] Load the map's bot waypoint graph (QC waypoint_loadall + waypoint_load_links +
    /// waypoint_load_hardwiredlinks, waypoints.qc:1837+/1324+/1472+): try
    /// <c>maps/&lt;map&gt;&lt;gt_ext&gt;.waypoints</c> (gt_ext = ".race" in Race mode — QC
    /// GET_GAMETYPE_EXTENSION, waypoints.qc:1314), falling back to the base name; same fallback for the
    /// <c>.cache</c> (skips the O(N²) AutoLink) and <c>.hardwired</c> companions. With no file (or no
    /// ConfigReader at all) <see cref="Bot.WaypointNetwork.ForMap"/> auto-generates a graph from the spawned
    /// map entities so bots still roam. Called ONCE by the population on the first frame with bots present.
    /// </summary>
    internal Bot.WaypointNetwork LoadWaypointNetwork()
    {
        string ext = GameType is Race ? ".race" : "";
        string? Read(string suffix)
        {
            if (ConfigReader is null || string.IsNullOrEmpty(MapName))
                return null;
            string? text = ext.Length > 0 ? ConfigReader($"maps/{MapName}{ext}{suffix}") : null;
            return text ?? ConfigReader($"maps/{MapName}{suffix}");
        }

        string? wp = Read(".waypoints");
        string? cache = Read(".waypoints.cache");
        string? hardwired = Read(".waypoints.hardwired");
        // Seed the shared graph's skill BEFORE its link costs are baked (QC sets the global `skill` before the
        // waypoint costs are computed), so LinearCost's 1.25x bunnyhop speedup at skill >= bot_ai_bunnyhop_skilloffset
        // actually scales the route costs instead of always baking at skill 0.
        int skill = (int)Cvars.Skill;
        int bunnyOffset = (int)Cvars.FloatOr("bot_ai_bunnyhop_skilloffset", 7f);
        var net = Bot.WaypointNetwork.ForMap(wp, Services.EntityTable.All, cache, hardwired, skill, bunnyOffset);
        // Info, not Trace: this is the one-shot "bots are live on this map" boot diagnostic — a dedicated
        // server's log should show it at default verbosity, like [MapLoader] does for the world.
        XonoticGodot.Common.Diagnostics.Log.Info(
            $"[bots] waypoints for '{MapName}': nodes={net.Count} (file={(wp is null ? "none/auto" : "loaded")}, cache={(cache is null ? "no" : "yes")})");
        return net;
    }

    private static GameType? ResolveGameType(string? name)
    {
        GameType? gt = GameTypes.ByName(string.IsNullOrEmpty(name) ? DefaultGameType : name!);
        gt ??= GameTypes.ByName(DefaultGameType);   // fall back to DM if the requested type isn't registered
        return gt;
    }

    /// <summary>The team count a gametype wants (reads its TeamCount property where it has one; else 2).</summary>
    private static int TeamCountFor(GameType? gt) => gt switch
    {
        TeamMayhem tm => tm.TeamCount,
        Tdm tdm => tdm.TeamCount,
        ClanArena ca => ca.TeamCount,
        Ctf ctf => ctf.TeamCount,
        Domination dom => dom.TeamCount,
        KeyHunt kh => kh.TeamCount,
        FreezeTag ft => ft.TeamCount,
        _ => 2,
    };

    /// <summary>
    /// Mirror the live (Server) round handler's round timing onto a gametype-owned (Common) round handler so the
    /// gametype's CheckWinner / overtime reads (which consult their OWN <c>Handler.RoundEndTime</c>) match the
    /// round the live handler advanced (QC round_handler_GetEndTime()). No-op if the gametype handler is null.
    /// </summary>
    private static void MirrorRoundTiming(RoundHandler live, XonoticGodot.Common.Gameplay.RoundHandler? owned)
    {
        if (owned is null)
            return;
        owned.RoundEndTime = live.RoundEndTime;
        owned.RoundsPlayed = live.RoundsPlayed;
        // Mirror the live round PHASE + pre-game hold so the gametype's own round-phase gates
        // (QC round_handler_IsRoundStarted: CA damage2score accrual, "you are now alone" notify) read the live
        // state. Without this the owned handler is never ticked, so IsRoundStarted would be permanently false and
        // the gated features stay dead despite being wired (parity: clanarena.scoring.damage2score).
        owned.GameStartTime = live.GameStartTime;
        owned.MirrorRoundStarted(live.IsRoundStarted);
    }

    /// <summary>Default round-start predicate (QC CA CheckTeams): ≥2 players spread over ≥2 active teams.</summary>
    private bool DefaultCanRoundStart()
    {
        if (!Teamplay.IsTeamGame)
            return Clients.PlayerCount >= 2;
        int teamsWithPlayers = 0;
        foreach (int team in Teams.Active(Teamplay.TeamCount))
            if (Teamplay.CountTeam(team, Clients.Players) > 0)
                teamsWithPlayers++;
        return teamsWithPlayers >= 2;
    }

    /// <summary>Default round-end predicate (QC CA CheckWinner): ≤1 team has a living player.</summary>
    private bool DefaultCanRoundEnd()
    {
        int aliveTeams = 0;
        foreach (int team in Teams.Active(Teamplay.TeamCount))
        {
            bool anyAlive = false;
            var players = Clients.Players;
            for (int i = 0; i < players.Count; i++)
            {
                Player p = players[i];
                if ((int)p.Team == team && !p.IsDead) { anyAlive = true; break; }
            }
            if (anyAlive) aliveTeams++;
        }
        return aliveTeams <= 1;
    }

    /// <summary>
    /// Host sink for the Invasion broadcast notifications (QC sv_invasion.qc <c>Send_Notification(NOTIF_ALL, ...)</c>).
    /// The gametype only decides WHAT to send; this routes those decisions to the live broadcast
    /// <see cref="NotificationSystem"/> exactly as Base does — CENTER + INFO for the round-over / round-winner
    /// pair, CENTER for the supermonster arrival. Assigned to <c>inv.Notifications</c> in <see cref="ActivateGameType"/>.
    /// </summary>
    private sealed class InvasionNotifyHost : Invasion.IInvasionNotifications
    {
        // QC: Send_Notification(NOTIF_ALL, NULL, MSG_CENTER, CENTER_ROUND_OVER);
        //     Send_Notification(NOTIF_ALL, NULL, MSG_INFO,   INFO_ROUND_OVER);
        public void RoundOver()
        {
            NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Center, "ROUND_OVER");
            NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Info, "ROUND_OVER");
        }

        // QC: Send_Notification(NOTIF_ALL, NULL, MSG_CENTER, CENTER_ROUND_PLAYER_WIN, winner.netname);
        //     Send_Notification(NOTIF_ALL, NULL, MSG_INFO,   INFO_ROUND_PLAYER_WIN,   winner.netname);
        public void RoundPlayerWin(Player winner)
        {
            NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Center, "ROUND_PLAYER_WIN", winner.NetName);
            NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Info, "ROUND_PLAYER_WIN", winner.NetName);
        }

        // QC: Send_Notification(NOTIF_ALL, NULL, MSG_CENTER, CENTER_INVASION_SUPERMONSTER, mon.m_name);
        public void Supermonster(string name)
            => NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Center, "INVASION_SUPERMONSTER", name);
    }

    /// <summary>
    /// Host sink for the Survival notifications (QC sv_survival.qc <c>Send_Notification(...)</c> in
    /// <c>Surv_RoundStart</c> / <c>Surv_CheckWinner</c> / <c>surv_LastPlayerForTeam_Notify</c>). The gametype only
    /// decides WHAT to send (each player's secret role, the round result, the last-of-side "alone" prompt); this
    /// routes those decisions to the live <see cref="NotificationSystem"/> exactly as Base does. Assigned to
    /// <c>surv.Notifications</c> in <see cref="ActivateGameType"/>.
    /// </summary>
    private sealed class SurvivalNotifyHost : Survival.ISurvivalNotifications
    {
        // QC Surv_RoundStart (sv_survival.qc:186-192): Send_Notification(NOTIF_ONE_ONLY, it, MSG_CENTER,
        // hunter ? CENTER_SURVIVAL_HUNTER : CENTER_SURVIVAL_SURVIVOR) — each live player privately told their role.
        public void Role(Player p, bool hunter)
            => NotificationSystem.Send(NotifBroadcast.OneOnly, p, MsgType.Center,
                hunter ? "SURVIVAL_HUNTER" : "SURVIVAL_SURVIVOR");

        // QC Surv_CheckWinner (sv_survival.qc:130-144): broadcast the round result to everyone — CENTER + INFO for
        // the hunters/survivors win, or ROUND_TIED when both sides reached zero on the same frame.
        public void RoundResult(Survival.SurvStatus winningSide)
        {
            string name = winningSide switch
            {
                Survival.SurvStatus.Hunter => "SURVIVAL_HUNTER_WIN",
                Survival.SurvStatus.Prey => "SURVIVAL_SURVIVOR_WIN",
                _ => "ROUND_TIED",
            };
            NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Center, name);
            NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Info, name);
        }

        // QC surv_LastPlayerForTeam_Notify (sv_survival.qc:366): Send_Notification(NOTIF_ONE_ONLY, pl, MSG_CENTER,
        // CENTER_ALONE) — the now-sole survivor of a status is told their last same-status ally just left.
        public void Alone(Player p)
            => NotificationSystem.Send(NotifBroadcast.OneOnly, p, MsgType.Center, "ALONE");
    }
}
