using System;
using Godot;
using XonoticGodot.Formats.Vfs;
using XonoticGodot.Common;            // GameInit
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;   // Player (LocalServerPlayer)
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Console;
using XonoticGodot.Game.Loaders;
using XonoticGodot.Game.Client;
using XonoticGodot.Game.Console;
using XonoticGodot.Game.Hud;
using XonoticGodot.Net;
using XonoticGodot.Server;
using EngineServices = XonoticGodot.Engine.Simulation.EngineServices;
using GVec3 = Godot.Vector3;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Net;

/// <summary>
/// The networked-match node — the thing the menu (or a CLI flag) spawns to actually <b>join a server and
/// play</b>. It composes the real §2 netcode pieces the way <see cref="NetLoopback"/> does, but driven by
/// genuine player input + a first-person camera that follows the PREDICTED local player, so it is a playable
/// client rather than a headless wire exerciser:
///
/// <list type="bullet">
///   <item>a real <see cref="ClientNet"/> (build-parity + ECDSA handshake, delta snapshots, predict/reconcile
///         the local player, interpolate remotes) connected over ENet;</item>
///   <item>a <see cref="EntityMovementStep"/> driving the SAME deterministic <see cref="Movement"/> sim the
///         server runs over a carrier entity, so prediction stays in lockstep with authority;</item>
///   <item>a <see cref="ClientWorld"/> render layer + <see cref="ClientEntityView"/> net→render bridge so
///         remote players/projectiles/items render from the over-the-wire entity stream;</item>
///   <item>a first-person <see cref="Camera3D"/> placed each frame at the predicted eye, oriented with the
///         exact basis math <see cref="PlayerController.UpdateCamera"/> uses (copied here — we never touch
///         that class);</item>
///   <item>a minimal HUD (<see cref="NetHud"/> crosshair + health/armor) and the <see cref="RadarPanel"/>
///         ent_cs radar.</item>
/// </list>
///
/// <para><b>Two modes.</b> A <see cref="ConfigureClient"/> instance is a pure client: it connects to a remote
/// <c>host:port</c> and boots a minimal engine facade locally (a flat floor) so prediction has something to
/// trace against — replicating the server's real map collision on a remote client is a later layer (see the
/// note on <see cref="BootClientFacade"/>). A <see cref="ConfigureListenServer"/> instance is a host/listen
/// server: it boots a <see cref="GameWorld"/> + <see cref="ServerNet"/> in-process (optionally with bots),
/// then self-connects a local <see cref="ClientNet"/> to <c>127.0.0.1</c> — the listen-server case where the
/// client and server share the one ambient <see cref="Api.Services"/> collision world, so prediction is exact.</para>
///
/// <para>This is the bring-up the <c>OnConnect</c>/<c>StartMap</c> stubs deferred: the menu's server browser
/// (or <c>--connect &lt;addr&gt;</c>) builds a client NetGame; a host (Create Game / <c>--host [map]</c>)
/// builds a listen-server NetGame. The <c>--net-loopback</c>, <c>--map</c> (local <see cref="GameDemo"/>) and
/// menu flows are unchanged.</para>
/// </summary>
public sealed partial class NetGame : Node3D
{
    /// <summary>The default XonoticGodot/Xonotic game port (DP <c>port</c> 26000).</summary>
    public const int DefaultPort = 26000;

    // --- configuration (set via the factory helpers before the node enters the tree) ---
    private bool _isListenServer;
    private string _host = "127.0.0.1";
    private int _port = DefaultPort;
    private string _map = "";
    private string _gametype = "dm";
    private int _botCount;
    private int _botSkill = 5;
    private string _campaignName = "";          // non-empty → host this listen server as a campaign level
    private int _campaignIndex;
    private string _serverName = "XonoticGodot Listen Server";
    private string _playerName = "player";
    private VirtualFileSystem? _vfs;            // shared asset VFS (from the menu shell), for models/sounds/maps
    private XonoticGodot.Engine.Simulation.CvarService? _sharedCvars;

    // --- live pieces ---
    private GameWorld? _serverWorld;            // listen server only
    private ServerNet? _server;                 // listen server only
    private ClientNet? _client;
    private ClientWorld _render = null!;
    private ClientEntityView _entityView = null!;
    private Camera3D _camera = null!;
    private RadarPanel _radar = null!;
    private NetHud _hud = null!;                 // crosshair + health/armor readout (the always-on lightweight HUD)
    // The full CSQC HUD panel set (weapon bar / ammo / kill-feed / centerprint / timer) on the net play path —
    // the same Hud GameDemo uses (T34 SEAM). It hosts the networked panels fed from the net stream; its own
    // Scoreboard panel is left UNUSED (T9's standalone _scoreboard owns the networked scoreboard). On a listen
    // server its gameplay panels (health/ammo/weapons) read the local server Player; a pure client gets the
    // player-agnostic panels (centerprint/killfeed) + the NetHud crosshair/health.
    private XonoticGodot.Game.Hud.Hud _fullHud = null!;
    private XonoticGodot.Game.Client.DamageTextLayer? _damageText; // [T51] floating damage numbers (cl_damagetext)
    private XonoticGodot.Game.Client.HitSound? _hitSound;          // client-side hit-confirmation beep (cl_hitsound modes 0-3)
    private XonoticGodot.Game.Hud.ScoreboardPanel _scoreboard = null!; // the networked scoreboard (held while +showscores)
    private XonoticGodot.Game.Hud.HudNotifications? _notifications; // notification router (centerprint/killfeed/announcer) on the net path
    private MinigameClient? _minigame;          // client-side minigame coordinator (board overlay + menu + cmd forwarding)
    private bool _minigameUiOwnedCursor;        // tracks the cursor show/recapture edge while a minigame UI is active
    private ViewEffects _viewEffects = null!;   // SEAM: T4's reusable screen-effects layer, on the net play path
    private AssetLoader? _assets;
    private ViewModel _viewModel = null!;       // first-person weapon view-model (CSQC viewmodel / wepent)
    private int _equippedWeaponId = int.MinValue; // weapon id currently in the viewmodel; rebuild only on a change
    private bool _weaponsPrecached;             // PrecacheWeaponModels ran once (warm the per-weapon asset caches)
    private MusicPlayer? _musicPlayer;          // client-side map music (cdtrack / target_music / trigger_music)

    // The SHARED first-person view subsystem (zoom + FOV + chase/death cam + eye-contents) — the SAME component
    // PlayerController uses, ported once from qcsrc/client/view.qc (T34 SEAM). Fed a ClientNet-predicted
    // ViewState each frame; we read back EyeContents / ZoomFraction / SensitivityScale / ChaseActive.
    private readonly Client.FirstPersonView _view = new();

    // The listen server's parsed map + its gametype-filtered dropped submodels, kept so SetupRender can build the
    // world render mesh from the SAME BSP + filter the collision was built from (the client renders the worldmodel
    // locally — DP VF_DRAWWORLD; the server ships no geometry). Null on a pure --connect client (no BSP yet).
    private XonoticGodot.Formats.Bsp.BspData? _bsp;
    private System.Collections.Generic.IReadOnlySet<int>? _droppedSubmodels;

    // The carrier entity the prediction sim drives (the client's local player hull). Spawned into the ambient
    // engine world (the listen server's GameWorld, or the minimal client facade) so PlayerPhysics has a real
    // edict with a hull to slide-and-step. Mirrors PlayerController's spawned entity.
    private Entity? _carrier;
    private IEngineServices? _world; // the facade the carrier lives in (captured so teardown removes it correctly)

    // Accumulated view angles in DEGREES, Quake convention (X = pitch down-positive, Y = yaw, Z = roll) — the
    // same convention PlayerController keeps, sampled into each InputCommand and used to orient the camera.
    private NVec3 _viewAngles;
    private bool _attackHeld;
    private bool _loggedAccept;
    private bool _cameraReady;                   // C5: false until the first snapshot seeds the predicted eye
    private int _prevHealth = -1;               // previous networked local health, for the damage red-flash edge
    private bool _inputActive;                   // tracks the active→inactive edge to release held buttons (pause/console)

    // C2S impulse (QC usercmd.impulse): a one-shot weapon-switch/reload number a weapon bind set this frame.
    // Edge-triggered — SampleInput stamps it onto the next InputCommand then clears it, so it is sent (and
    // processed server-side) exactly once. 0 = nothing pending.
    private int _pendingImpulse;

    // Spawn-zoom edge (QC cl_spawnzoom): the net path has no Teleport, so derive the (re)spawn from a Health
    // 0→>0 transition and arm the shared view's spawn-zoom on that edge. -1 until the first health snapshot.
    private int _prevAliveHealth = -1;

    // True once the local player has been alive at least once. Gates the death-cam so the PRE-spawn Health 0
    // (observer window, before the first spawn) isn't read as a death (which would engage the event-chase at the
    // world origin under the connect overlay). After the first spawn, Health<=0 is a real death.
    private bool _everAlive;

    /// <summary>Runs a console command line for one-shot binds (weapon_next/reload/kill/…). Wired by
    /// <see cref="Shell"/> to the shared interpreter's <c>ExecuteLine</c> so a bound key routes through the same
    /// command path as the console.</summary>
    public Action<string>? RunCommand { get; set; }

    /// <summary>Raised with a line of server console output (a <c>clc_stringcmd</c> reply on a remote client) —
    /// <see cref="Shell"/> forwards it to the in-game console.</summary>
    public event Action<string>? ConsolePrint;

    /// <summary>The in-process listen-server world (host mode), or null on a pure <c>--connect</c> client. The
    /// console routes gameplay commands here directly when present (else over the remote string-command channel).</summary>
    public GameWorld? ServerWorld => _serverWorld;

    /// <summary>The local human <see cref="Player"/> on the listen-server world (the <c>caller</c> for console
    /// client-commands), resolved from the client's assigned net id with a single-human fallback. Null on a pure client.</summary>
    public Player? LocalServerPlayer
    {
        get
        {
            if (_serverWorld is null)
                return null;
            if (_server is not null && _client is not null)
            {
                Player? byId = _server.PlayerByNetId(_client.LocalNetId);
                if (byId is not null)
                    return byId;
            }
            foreach (Player p in _serverWorld.Clients.Players)
                if (!p.IsBot)
                    return p;
            return null;
        }
    }

    /// <summary>Send a console command line to the connected server (DP <c>clc_stringcmd</c>) — used by the
    /// console on a pure client where there is no in-process world. No-op if not connected.</summary>
    public void SendStringCommand(string line) => _client?.SendStringCommand(line);

    /// <summary>The loading screen (DP <c>SCR_DrawLoadingScreen</c>) shown during map load + handshake,
    /// set by <see cref="Shell"/> before the node enters the tree. Null when no loading screen is active
    /// (e.g. a bare CLI host).</summary>
    public LoadingScreen? LoadingScreen { get; set; }

    /// <summary>Callback to dismiss the loading screen layer (owned by <see cref="Shell"/>). Called when
    /// the player spawns (camera ready + health &gt; 0).</summary>
    public Action? DismissLoadingScreen { get; set; }

    /// <summary>
    /// Raised when this listen server must change level (DP <c>changelevel</c>) — carries (map, gametype,
    /// botCount, botSkill, campaignId, campaignIndex) captured from the live match so the host can reboot the
    /// listen server on the new map preserving the mode + bot fill, and — for a campaign auto-advance on win —
    /// staying in campaign mode at the next level (campaignId empty for a normal map change). <see cref="Shell"/>
    /// subscribes and reboots. Emitted deferred (idle) so the teardown happens OUTSIDE the server tick.
    /// </summary>
    public event Action<string, string, int, int, string, int>? MapChangeRequested;

    private string? _pendingMap; // a requested changelevel, emitted at the end of _Process (then deferred)

    /// <summary>
    /// Request a changelevel to <paramref name="map"/> (DP <c>map</c>/<c>changelevel</c> + the end-of-match
    /// rotation/gotomap). Wired as <c>Commands.ChangeLevelHandler</c> and called by the console's <c>map</c>
    /// builtin; only records the target — it's emitted (deferred) from <see cref="_Process"/> so a request made
    /// mid-tick (a clc_stringcmd <c>map</c>, or the intermission rotation) can't tear the server down under itself.
    /// </summary>
    public void RequestMapChange(string map)
    {
        if (!string.IsNullOrWhiteSpace(map))
            _pendingMap = map;
    }

    // Render clock used to drive the prediction (Predict/SendInput) AND read the decaying stair/error offsets.
    // The reconciler arms those decays stamped with the SERVER time (in HandleSnapshot), so the clock we read
    // them with must track server time — but advance smoothly BETWEEN snapshots (a snapshot only lands every
    // few frames). So we accumulate real delta each frame and rebase to LatestServerTime on each new snapshot.
    private float _renderClock;
    private float _lastSeenServerTime = -1f;

    // Fixed-timestep input accumulator: real time banked toward whole 1/72 s input ticks (so prediction speed
    // is frame-rate independent). Capped so a frame hitch can't queue a huge input burst (spiral-of-death).
    private float _inputAccum;
    private const float MaxInputBacklog = 0.25f; // ≤ ~18 catch-up input ticks per frame

    // FPS eye height (Xonotic PL_VIEW_OFS '0 0 35'). Mouse-look sensitivity now reads the live `sensitivity`
    // cvar (LookSensitivity), not a hardcoded constant, so the input-settings dialog drives it.
    private const float EyeHeight = 35f;

    // Standing player hull (Xonotic sv_player_mins/maxs), Quake units — same as PlayerController.HullMins/Maxs.
    private static readonly NVec3 HullMins = new(-16f, -16f, -24f);
    private static readonly NVec3 HullMaxs = new(16f, 16f, 45f);

    // =====================================================================================
    //  Factory configuration (call before AddChild)
    // =====================================================================================

    /// <summary>
    /// Configure this node as a PURE CLIENT that connects to <paramref name="address"/> (<c>host[:port]</c>,
    /// default port <see cref="DefaultPort"/>). The optional <paramref name="vfs"/>/<paramref name="cvars"/>
    /// are the menu shell's shared asset VFS + cvar store (so models/sounds/crosshairs resolve and the
    /// predictor reads the user's physics cvars); pass null for a standalone/CLI client.
    /// </summary>
    public void ConfigureClient(string address, string playerName = "player",
        VirtualFileSystem? vfs = null, XonoticGodot.Engine.Simulation.CvarService? cvars = null)
    {
        _isListenServer = false;
        (_host, _port) = ParseAddress(address, DefaultPort);
        _playerName = string.IsNullOrWhiteSpace(playerName) ? "player" : playerName;
        _vfs = vfs;
        _sharedCvars = cvars;
    }

    /// <summary>
    /// Configure this node as a LISTEN SERVER (host): boot a <see cref="GameWorld"/> on <paramref name="map"/>
    /// + a <see cref="ServerNet"/> on <paramref name="port"/> (optionally filled with <paramref name="botCount"/>
    /// bots), then self-connect a local client to it. The shared <paramref name="vfs"/>/<paramref name="cvars"/>
    /// drive both the server's config + the client's rendering; pass null for a bare CLI host on a test floor.
    /// </summary>
    public void ConfigureListenServer(string map, string gametype = "dm", int botCount = 0, int botSkill = 5,
        int port = DefaultPort, string playerName = "player", string serverName = "XonoticGodot Listen Server",
        VirtualFileSystem? vfs = null, XonoticGodot.Engine.Simulation.CvarService? cvars = null,
        string campaignName = "", int campaignIndex = 0)
    {
        _isListenServer = true;
        _host = "127.0.0.1";
        _port = port;
        _map = map ?? "";
        _gametype = string.IsNullOrWhiteSpace(gametype) ? "dm" : gametype;
        _botCount = Math.Max(0, botCount);
        _botSkill = botSkill;
        _campaignName = campaignName ?? "";
        _campaignIndex = campaignIndex;
        _serverName = string.IsNullOrWhiteSpace(serverName) ? "XonoticGodot Listen Server" : serverName;
        _playerName = string.IsNullOrWhiteSpace(playerName) ? "player" : playerName;
        _vfs = vfs;
        _sharedCvars = cvars;
    }

    // =====================================================================================
    //  Lifecycle
    // =====================================================================================

    public override void _Ready()
    {
        // The asset loader (models/sounds/maps) over the shared VFS, when the menu mounted one.
        if (_vfs is not null)
            _assets = new AssetLoader(_vfs);

        // --- loading stages (DP SCR_PushLoadingScreen weighted hierarchy) ---
        // Progress is set here even though Godot won't re-render mid-_Ready; the loading screen
        // was already shown by Shell and these set the state that the first rendered frame sees.
        LoadingScreen?.UpdateProgress(0.05f, "Loading map...");

        if (_isListenServer)
            StartListenServer();
        else
            BootClientFacade();

        LoadingScreen?.UpdateProgress(0.40f, "Building world...");

        // The client connects to the chosen endpoint (a listen server is on 127.0.0.1; a pure client on _host).
        StartClient();

        LoadingScreen?.UpdateProgress(0.50f, "Setting up renderer...");

        // Render layer + the net→render bridge + a basic HUD/camera. Built regardless of mode so a connect that
        // hasn't completed yet still has somewhere to draw once snapshots flow.
        SetupRender();
        SetupCameraAndHud();

        LoadingScreen?.UpdateProgress(0.60f, "Starting music...");

        // Map music: read the cdtrack from the mapinfo file (VFS) and wire a MusicPlayer into the render tree.
        SetupMusic();

        LoadingScreen?.UpdateProgress(0.65f, "Precaching models...");

        // Warm every weapon's view model + hand rig into the asset caches now, under the loading screen, so the
        // first switch/pickup of a weapon in combat doesn't hitch reading+decoding the model and its textures on
        // the main thread (DP precaches weapon models at map load). Runs after _assets is built above.
        PrecacheWeaponModels();

        LoadingScreen?.UpdateProgress(0.80f, "Connecting...");

        // FPS mouse-look: capture the cursor (the Shell releases/recaptures it around the in-game menu).
        Input.MouseMode = Input.MouseModeEnum.Captured;

        GD.Print(_isListenServer
            ? $"[NetGame] listen server on 127.0.0.1:{_port} (map '{_map}', {_gametype}, {_botCount} bots) — self-connecting."
            : $"[NetGame] connecting to {_host}:{_port}.");
    }

    /// <summary>
    /// Boot the listen server: build the map (or a test floor), stand up a <see cref="GameWorld"/> on it,
    /// activate the gametype, fill it with bots, and start a <see cref="ServerNet"/>. The world's collision
    /// becomes the ambient <see cref="Api.Services"/> (via <see cref="GameWorld.Boot"/>), which the client's
    /// prediction then traces against — the listen-server property that makes local prediction exact.
    /// </summary>
    private void StartListenServer()
    {
        // --- collision: load the map's BSP collision if we can resolve it, else a flat test floor. ---
        CollisionWorld collision;
        XonoticGodot.Formats.Bsp.BspData? bsp = TryLoadMapBsp(_map);
        BspCollisionBuilder.Result? built = null;
        if (bsp is not null)
        {
            // Keep the BSP + the gametype-filtered dropped submodels so SetupRender can render the worldmodel
            // (the client draws the world it loaded locally) AND so collision drops the SAME conditional "*N"
            // brushes the render does — exactly like GameDemo (render + collision MUST agree, GameDemo.cs:134/181).
            _bsp = bsp;
            _droppedSubmodels = GameMapView.ComputeDroppedSubmodels(bsp, _gametype);
            built = BspCollisionBuilder.Build(bsp, _droppedSubmodels);
            collision = built.World;
        }
        else
        {
            collision = BuildTestFloor();
            if (!string.IsNullOrWhiteSpace(_map))
                GD.PrintErr($"[NetGame] map '{_map}' not found in the VFS — listen server runs on a flat floor.");
        }

        // --- the headless world + gametype. Pass the map's parsed entity lump so the server spawns the real
        //     spawn points + gameplay entities (jump-pads/items/doors) — without these a player would spawn at
        //     the world origin with no items. Reuse the menu's shared cvar store via the config reader so the
        //     server reads authentic balance/physics; a bare CLI host (no preloaded store) loads them too. ---
        System.Collections.Generic.IReadOnlyList<EntityDict>? mapEntities = bsp is not null ? BuildEntityDicts(bsp) : null;
        _serverWorld = new GameWorld(collision, mapEntities) { MapName = _map };
        if (built is not null)
            _serverWorld.BrushModels = built.Submodels; // moving SOLID_BSP brushes (doors/plats) clip correctly
        if (bsp is not null)
        {
            _serverWorld.MapBsp = bsp;                   // inline-model render surfaces for the getsurface* builtins
            _serverWorld.Pvs = new XonoticGodot.Formats.Bsp.BspPvs(bsp); // checkpvs culling (bot LOS / sound / net)
        }
        if (_vfs is not null)
            _serverWorld.ConfigReader = path => _vfs.Exists(path) ? _vfs.ReadText(path) : null;

        // Campaign: the menu picked a campaign level. Hand the id + index to the world BEFORE Boot so its
        // CampaignPreInit (run inside Boot) resolves this level's gametype/bots/skill/limits/mutators from the
        // campaign file — QC CampaignSetup set the same cvars before the map change. (Must be a pre-Boot
        // property, not a cvar Set here: the world's cvar store is private and is created during Boot.)
        if (!string.IsNullOrEmpty(_campaignName))
        {
            _serverWorld.CampaignName = _campaignName;
            _serverWorld.CampaignIndex = _campaignIndex;
        }

        // Boot the world: publishes Api.Services, activates the gametype, loads the cfg tree, spawns the map's
        // entities (spawn points + jump-pads/items/doors), registers brush models + surfaces + PVS.
        _serverWorld.Boot(_gametype);

        // Expose the map name as a cvar so the server-browser infostring (ServerNet.BuildServerInfo) and the
        // pure-client map-name handshake (follow-up) have the real value; GameWorld.MapName is set, but the cvar
        // is independent (DP keeps mapname as an engine cvar available to serverinfo).
        _serverWorld.Services.Cvars.Set("mapname", _map);

        // Create-a-match / `--host` is "host AND play": the local host should spawn straight into the match, not
        // sit as an observer. xonotic-server.cfg ships `sv_spectate 1` (spectating allowed) which — faithful to QC
        // PlayerPreThink (server/client.qc:2715: autojoin only when `!(sv_spectate||g_campaign||forced_spectator)`)
        // — means a passive real client NEVER delayed-autojoins; it waits for a +fire/+jump join. On a self-hosted
        // listen server that left the player a permanent observer at world origin under the black connect overlay.
        // `sv_spectate 0` = "clients spawn as players immediately" (the cvar's own definition) restores the intended
        // ~1s autojoin so Create→Start drops you into the game, like real Xonotic. (Join-on-fire still works.)
        _serverWorld.Services.Cvars.Set("sv_spectate", "0");

        // A single-player campaign is "host AND play" too, but g_campaign re-arms the spectator-hold sv_spectate
        // just cleared; opt the local host out so it spawns straight into the loaded level (Create-Game UX).
        if (!string.IsNullOrEmpty(_campaignName))
        {
            _serverWorld.Clients.CampaignHostAutojoin = true;

            // Campaign progress lives in the SHARED (menu) cvar store as a seta cvar; bridge it across the
            // private/shared store split (QC keeps progress in one engine store the menu + server share):
            //  - SEED the world's frontier from the menu so PreIntermission only advances at the frontier level
            //    (and a replay of an earlier level can't regress it — Level == g_campaign<id>_index gate).
            //  - MIRROR each saved progress cvar back to the menu store (+ archive it) so the campaign list sees
            //    the new frontier immediately and SaveUserConfig persists it (QC CampaignSaveCvar's seta side).
            string indexVar = $"g_campaign{_campaignName}_index";
            if (_sharedCvars is not null)
            {
                _serverWorld.Services.Cvars.Set(indexVar,
                    ((int)_sharedCvars.GetFloat(indexVar)).ToString(System.Globalization.CultureInfo.InvariantCulture));
                _serverWorld.Campaign.OnProgressSaved = (name, value) =>
                {
                    _sharedCvars.Set(name, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    _sharedCvars.MarkArchived(name);
                };
            }
        }

        // Carry the chosen match limits onto the hosted world's own cvar store (GameWorld keeps a private store,
        // so the values the menu wrote into MenuState.Cvars don't reach it automatically). Read them from the
        // shared store the menu populated; only override when set (>0), else the cfg-loaded defaults stand.
        // In campaign mode the limits are authored by the file (CampaignPostInit set them during Boot), so the
        // menu's leftover timelimit/fraglimit must NOT clobber them.
        if (_sharedCvars is not null && string.IsNullOrEmpty(_campaignName))
        {
            CopyCvarIfSet(_sharedCvars, _serverWorld.Services.Cvars, "timelimit");
            CopyCvarIfSet(_sharedCvars, _serverWorld.Services.Cvars, "fraglimit");
        }

        // --- bots so a solo host has opponents to see/play (QC bot_number / minplayers fill). ---
        for (int i = 0; i < _botCount; i++)
        {
            ClientManager.ClientInfo info = _serverWorld.Clients.ClientConnect(isBot: true, netName: $"[BOT] {BotName(i)}");
            info.Player.BotSkill = _botSkill;
        }

        ServerNet? server = ServerNet.Start(_serverWorld, _port, maxClients: 16, serverName: _serverName);
        if (server is null)
        {
            GD.PrintErr($"[NetGame] could not start the listen server on UDP {_port} (port in use?).");
            return;
        }
        _server = server;

        // --- wire the server-command host sinks (QC the say bus / bprint + bot_cmd add/remove). Without these,
        //     console/clc_stringcmd `say`/`bot_add`/`setbots` no-op; with them, chat reaches every client's console
        //     and bots are added/removed live. Bots auto-join + spawn on connect (ClientManager.ClientConnect) and
        //     the per-tick snapshot delta networks/despawns them (NetIdFor is lazy), so no extra net plumbing is
        //     needed beyond cleaning ServerNet's per-player maps on removal (ForgetPlayer). ---
        Commands cmd = _serverWorld.Commands;

        // player chat (say) → broadcast to everyone incl. the sender; server announcements (vote/ban/team) → same.
        cmd.ChatHandler = (caller, msg, teamOnly) =>
            _server?.BroadcastPrint($"{(teamOnly ? "(team) " : "")}{caller?.NetName ?? "server"}^7: {msg}");
        cmd.ChatBroadcast = msg => _server?.BroadcastPrint(msg);

        // bot_add / setbots: connect a bot (auto-joins + spawns); the snapshot loop networks it next tick.
        cmd.AddBotHandler = (name, skill) =>
        {
            if (_serverWorld is null)
                return false;
            string botName = string.IsNullOrWhiteSpace(name) ? $"[BOT] {BotName(_serverWorld.Clients.BotCount)}" : name!;
            ClientManager.ClientInfo info = _serverWorld.Clients.ClientConnect(isBot: true, netName: botName);
            info.Player.BotSkill = skill ?? _botSkill;
            return true;
        };

        // bot_remove / removebots: drop a bot (by name, else the last) — leaves the client list so the next snapshot
        // delta despawns it on clients; ForgetPlayer clears ServerNet's id/antilag maps so they don't retain it.
        cmd.RemoveBotHandler = name =>
        {
            if (_serverWorld is null)
                return false;
            Player? bot = null;
            foreach (Player p in _serverWorld.Clients.Players)
                if (p.IsBot && (string.IsNullOrWhiteSpace(name) || p.NetName.Contains(name!, StringComparison.OrdinalIgnoreCase)))
                    bot = p; // last match, so repeated removes peel bots off the end
            if (bot is null)
                return false;
            _serverWorld.Clients.ClientDisconnect(bot);
            _server?.ForgetPlayer(bot);
            return true;
        };

        // map / gotomap / nextmap / map-vote / rotation / samelevel all funnel here (QC changelevel): record the
        // target; the deferred emit in _Process reboots the listen server on it (preserving gametype + bots).
        cmd.ChangeLevelHandler = RequestMapChange;
    }

    /// <summary>
    /// Boot a minimal ambient engine facade for a PURE CLIENT so the prediction sim has a world to trace
    /// against before/while connected. We use a flat floor: a remote client does not yet replicate the
    /// server's real map collision (that needs the map name in the handshake + a client-side BSP load — a
    /// follow-up). Prediction, the camera, input and remote-entity rendering all work over this floor; only
    /// the local player's collision against real geometry is approximate until that lands.
    /// </summary>
    private void BootClientFacade()
    {
        var services = new EngineServices(BuildTestFloor(), _sharedCvars);
        GameInit.Boot(services);                 // Api.Services = services; + movement/registries
        // Seed weapon balance from whatever cvars are loaded (the menu preloaded the tree into _sharedCvars).
        XonoticGodot.Common.Gameplay.Weapons.ConfigureAll();
    }

    /// <summary>
    /// Build the predicted <see cref="ClientNet"/> and connect. The local player is predicted by driving the
    /// shared <see cref="Movement"/> sim over a carrier entity (<see cref="EntityMovementStep"/>) — the same
    /// deterministic sim the server runs — and the input is sampled live each tick from WASD/mouse.
    /// </summary>
    private void StartClient()
    {
        // The carrier the predictor integrates: a real player edict in the ambient world (the listen server's,
        // or the client facade's), set up exactly like PlayerController's player entity so PlayerPhysics has a
        // proper hull + client flags to slide-and-step.
        _carrier = SpawnCarrier();

        IMovementStep step = _carrier is not null
            ? new EntityMovementStep(_carrier)
            : new NullMovementStep();

        ClientNet? client = ClientNet.Connect(_host, _port, step, SampleInput);
        if (client is null)
        {
            GD.PrintErr($"[NetGame] could not create the client socket for {_host}:{_port}.");
            return;
        }
        _client = client;
        _client.LocalPlayerName = _playerName;
        // Relay server console output (clc_stringcmd replies / notices) to the in-game console via the Shell.
        _client.PrintReceived += s => ConsolePrint?.Invoke(s);
    }

    /// <summary>Spawn the carrier player entity (the local hull the predictor moves), mirroring PlayerController._Ready.</summary>
    private Entity? SpawnCarrier()
    {
        if (Api.Services is null)
            return null;
        _world = Api.Services; // remember the facade so teardown removes the carrier from the right table
        Entity e = Api.Entities.Spawn();
        e.ClassName = "client_predict";
        e.MoveType = MoveType.Walk;
        // IMPORTANT: the carrier is the client-side PREDICTION copy, not an authoritative actor. Keep it
        // SOLID_NOT so it never enters the trace/solid set — on a listen server the real authoritative Player
        // lives in this same entity table, and a solid prediction ghost would let the server collide with it.
        // The movement sim sweeps the carrier's hull against the world regardless of its own Solid value, so
        // prediction still slides-and-steps correctly.
        e.Solid = Solid.Not;
        // The carrier is kept SOLID_NOT (above) so the listen-server authority never collides with the
        // prediction ghost — but for MOVEMENT it IS a SOLID_SLIDEBOX player and must clip the SAME content mask
        // the authoritative server Player does (SpawnSystem sets the real player SOLID_SLIDEBOX → Solid|Body|
        // PlayerClip). Without this, GenericHitMask derives the SOLID_NOT default (Solid|Body|CORPSE), so the
        // carrier collides with our OWN projectiles: PROJECTILE_MAKETRIGGER makes them SOLID_CORPSE precisely so
        // a player's PlayerClip move passes through them, but the predicted carrier is a DISTINCT entity from the
        // projectile's server-side .Owner, so the owner trace-exception can't protect it — and the CORPSE bit in
        // the default mask let the rocket/orb block (and detonate on) the firer anyway. Match the server player's
        // mask via the DP dphitcontentsmask override so prediction and authority stay in lockstep. (See
        // Projectiles.MakeTrigger / TraceService.GenericHitMask.)
        e.DpHitContentsMask = SuperContents.Solid | SuperContents.Body | SuperContents.PlayerClip;
        e.Flags |= EntFlags.Client | EntFlags.JumpReleased;
        e.Mins = HullMins;
        e.Maxs = HullMaxs;
        e.ViewOfs = new NVec3(0f, 0f, EyeHeight);
        return e;
    }

    private void SetupRender()
    {
        _render = new ClientWorld { Name = "Render" };
        // Resolve models/sounds straight from the mounted content VFS so remote players render as real skeletal
        // IQM models + positional sounds resolve (else the placeholder box + the res:// fallback).
        if (_assets is not null)
        {
            _render.AudioLoader = _assets.LoadSound;
            _render.Assets = _assets.Assets;   // material facade → textured world/vehicle models (ModelResolver path)
            // Remote players render as skeletal IQM models (models/player/*.iqm); non-player networked entities
            // (items/gibs/monsters/props) render their real model by name — MD3 via the frame-driven
            // ModelResolver, any other format (IQM/DPM) via the EntityModelFactory fallback.
            _render.PlayerModelResolver = ResolvePlayerModel;
            _render.ModelResolver = ResolveEntityModel;
            _render.EntityModelFactory = BuildEntityModel;
            // FORCEMODEL: resolve a forced player-model name+skin to a render node (the cl_forcemyplayermodel /
            // cl_forceplayermodels swap). Reuses the skeletal path (LoadSkeletalModel), falling back to the
            // format-agnostic loader (IQM/DPM/MD3) — null = the forced model doesn't exist (QC fexists-miss).
            _render.ForcedModelResolver = BuildForcedPlayerModel;
        }
        AddChild(_render);

        // Pre-warm the effect catalog + particlefont atlas now (map-load), so the FIRST weapon shot doesn't hitch
        // parsing effectinfo.txt + decoding the atlas on its render frame (DP precaches these at client init,
        // cl_particles.c). The Assets setter above already wired the texture/text loaders via WireEffectAssets,
        // and _render.Effects is live (ClientWorld._Ready ran synchronously on AddChild). Idempotent + invisible.
        _render.Effects.Warmup();

        // CSQC appearance context (FORCEMODEL/FORCECOLORS need the local player + gametype): read live each frame.
        _render.AppearanceProvider = BuildAppearanceContext;

        // Render the world geometry on the listen server: the client draws the worldmodel it loaded locally
        // (DP VF_DRAWWORLD=1 + renderscene(); the server ships no geometry). Reuses the SAME BSP + gametype filter
        // the collision was built from in StartListenServer — identical to GameDemo.cs:181's MapLoader.BuildMap.
        // A pure --connect client has no BSP yet (see the map-name handshake follow-up), so this is gated on _bsp.
        if (_bsp is not null && _assets?.Assets is not null)
            // Pass the loaded map name so external lm_NNNN lightmaps resolve (stock maps have no internal lump).
            AddChild(MapLoader.BuildMap(_bsp, _assets.Assets, _map, _droppedSubmodels));

        if (_client is not null)
        {
            _entityView = new ClientEntityView(_client, _render);
            // Third-person carried weapons: build each remote player's held weapon from the asset pipeline
            // (RC6) instead of the gray placeholder box — same v_ model the first-person viewmodel uses.
            _entityView.WeaponModelFactory = BuildWeaponWorldModel;
            AddChild(_entityView);

            // Surface decoded effects/sounds to the renderer as packets arrive: effects → particles, sounds →
            // spatial AudioStreamPlayer3D (DP SV_StartSound). Announcer voices are wired separately in
            // SetupCameraAndHud (they need a HUD host); other notification text still goes to the console.
            _client.EffectReceived += OnEffectReceived;
            _client.SoundReceived += OnSoundReceived;

            // Looping positional sounds (Arc beam, vehicle engines) follow their emitter each frame: resolve a
            // net id → its live origin (the local player from prediction, remotes from their interpolated pose).
            _render.EntityOriginResolver = ResolveEntityOrigin;
        }

        AddLight();
    }

    /// <summary>
    /// Read the map's cdtrack from its .mapinfo file (or worldspawn fallback) and instantiate the client-side
    /// <see cref="MusicPlayer"/> that drives background music for the match. The MusicPlayer evaluates the
    /// priority stack (trigger_music > target_music > cdtrack) each frame and crossfades between tracks.
    /// </summary>
    private void SetupMusic()
    {
        // Resolve the cdtrack: first from the mapinfo file, then from worldspawn (already parsed by GameWorld).
        string cdTrack = "";
        if (_vfs is not null && !string.IsNullOrEmpty(_map))
        {
            // Try reading maps/<map>.mapinfo from the VFS (the pk3-mounted search path).
            string mapinfoPath = $"maps/{_map}.mapinfo";
            if (_vfs.Exists(mapinfoPath))
            {
                try
                {
                    string text = _vfs.ReadText(mapinfoPath);
                    cdTrack = ParseMapinfoCdTrack(text);
                }
                catch { /* mapinfo read failed — fall through to worldspawn fallback */ }
            }
        }

        // Fallback: GameWorld may have read "music" or "noise" from the worldspawn entity.
        if (string.IsNullOrEmpty(cdTrack) && _serverWorld is not null && !string.IsNullOrEmpty(_serverWorld.CdTrack))
            cdTrack = _serverWorld.CdTrack;

        // Resolve the raw cdtrack value to a VFS sample path (handles numbers, bare names, full paths).
        string resolvedTrack = MusicPlayer.ResolveMusicPath(cdTrack);

        // Store it on the server world too (so other code can query it).
        if (_serverWorld is not null && !string.IsNullOrEmpty(resolvedTrack))
            _serverWorld.CdTrack = resolvedTrack;

        // Create and configure the music player node.
        _musicPlayer = new MusicPlayer
        {
            Name = "MusicPlayer",
            CdTrack = resolvedTrack,
            AudioLoader = _assets is not null ? _assets.LoadSound : null,
            BgmVolume = _sharedCvars?.GetFloat("bgmvolume") is float bv and > 0f ? bv : 0.7f,
        };

        // On a listen server, give the music player direct access to the entity list so it can scan
        // target_music / trigger_music state each frame without networking.
        if (_serverWorld is not null)
        {
            _musicPlayer.EntityList = _serverWorld.Services.EntityTable.All;
            // The server time is needed for trigger_music touch freshness.
            // Updated each frame in _Process via the music player's ServerTime property.
        }

        AddChild(_musicPlayer);

        if (!string.IsNullOrEmpty(resolvedTrack))
            GD.Print($"[NetGame] map music: '{resolvedTrack}' (from {(string.IsNullOrEmpty(cdTrack) ? "none" : "mapinfo/worldspawn")})");
    }

    /// <summary>Parse the cdtrack line from a .mapinfo file's text content.</summary>
    private static string ParseMapinfoCdTrack(string mapinfoText)
    {
        if (string.IsNullOrEmpty(mapinfoText))
            return "";

        // The mapinfo format is line-based: "cdtrack <value>"
        foreach (string rawLine in mapinfoText.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("cdtrack ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("cdtrack\t", StringComparison.OrdinalIgnoreCase))
            {
                string value = line.Substring(8).Trim();
                // Strip quotes if present (QC cvar_value_issafe check — we just take the bare value).
                if (value.Length > 1 && value[0] == '"' && value[^1] == '"')
                    value = value[1..^1];
                return value;
            }
        }
        return "";
    }

    private void SetupCameraAndHud()
    {
        // Seed the zoom / eventchase cvars so the shared FirstPersonView reads authentic values even when a
        // particular boot path didn't load the full client cfg tree (Register is idempotent — it never clobbers a
        // loaded value). Mirrors PlayerController._Ready's seeding so the net path zooms identically.
        SeedViewCvars();

        // The shared first-person view (T34 SEAM): the SAME zoom/FOV/death-cam component PlayerController drives.
        _view.EyeHeight = EyeHeight;
        _view.BaseFov = 100f; // Xonotic `fov 100`; the `fov` cvar overrides it live (FirstPersonView reads it).

        // Seed the camera fov from the view's *0.75 frustum (fov 100 → ~83.6° vertical), then UpdateView keeps it
        // live (incl. the zoom). Xonotic `fov` is HORIZONTAL at a 4:3 reference; Godot Camera3D.Fov is VERTICAL.
        float vFov = Client.FirstPersonView.ComputeVerticalFov(_view.BaseFov, 1f);
        _camera = new Camera3D { Name = "Camera3D", Fov = vFov, Near = 1f, Current = true };
        AddChild(_camera);

        // First-person weapon view-model hung off the camera (CSQC viewmodel rendered at the view origin). Built
        // here because SetupRender ran first, so _render.Effects is live (ClientWorld._Ready set it synchronously
        // on AddChild). The weapon model is installed/swapped from the networked active weapon each frame in
        // EquipNetworkedWeapon(); the ViewStateProvider feeds the follow/lean/bob sway from the predicted view.
        _viewModel = new ViewModel { Name = "ViewModel", Effects = _render.Effects };
        _viewModel.ViewStateProvider = BuildViewState;
        _camera.AddChild(_viewModel);
        _render.ViewModel = _viewModel;

        // Bridge the HUD's texture cache to the mounted game data so weapon icons / crosshairs / kill-notify
        // icons draw the REAL Xonotic art instead of colored-box fallbacks (mirrors GameDemo.SetupHud).
        if (_assets is not null)
            XonoticGodot.Game.Hud.TextureCache.VfsResolver = _assets.LoadTexture;

        // The full CSQC HUD panel set on the net path (T34): weapon bar / ammo / kill-feed / centerprint / timer,
        // the SAME Hud GameDemo uses. It is its OWN CanvasLayer (Hud : CanvasLayer), added directly to the node
        // like GameDemo does — on a layer BELOW the lightweight hudLayer so the crosshair/radar/scoreboard draw on
        // top. Its gameplay panels read a local Player; on a listen server that is the local server Player
        // (resolved each frame in _Process as the spawn lands). Its OWN Scoreboard panel is left unfed — T9's
        // standalone _scoreboard owns the networked scoreboard, so we don't double it up.
        _fullHud = new XonoticGodot.Game.Hud.Hud { Name = "FullHud", Layer = 4 };
        AddChild(_fullHud);
        // NetHud (below) is the always-on crosshair + health/armor on the net path; hide the full HUD's own copies
        // so they don't render on top of each other (they default Visible=true and draw even with no local Player).
        _fullHud.Crosshair.Visible = false;
        _fullHud.HealthArmor.Visible = false;
        // The full HUD's Timer panel isn't fed the networked match clock yet, so it would show a wrong climbing
        // time — hide it until it's wired (the scoreboard header already shows the time/frag limits).
        _fullHud.Timer.Visible = false;

        // [T51] Floating damage-number layer (QC cl_damagetext). Full-rect overlay; fed each frame in _Process
        // from the server-side DamagetextMutator's drained events, projected via the first-person _camera.
        _damageText = new XonoticGodot.Game.Client.DamageTextLayer { Name = "DamageText" };
        _damageText.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _fullHud.AddChild(_damageText);

        // Hit-confirmation sound (QC HitSound): non-positional beep on the SFX bus, pitch varies by cl_hitsound mode.
        _hitSound = new XonoticGodot.Game.Client.HitSound(_sharedCvars);
        if (_assets is not null)
            _hitSound.AudioLoader = _assets.LoadSound;
        _hitSound.Attach(_fullHud);

        // The lightweight crosshair + health/armor readout + radar + networked scoreboard, on a layer ABOVE the
        // full HUD so the crosshair sits on top.
        var hudLayer = new CanvasLayer { Name = "Hud", Layer = 5 };
        AddChild(hudLayer);

        // The crosshair + health/armor readout. Kept because it needs no local Player (it reads the networked
        // ClientNet stats directly), so the crosshair + health show even on a pure --connect client where the full
        // HUD's player-bound panels have no actor.
        _hud = new NetHud { Name = "NetHud", Net = _client };
        _hud.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        hudLayer.AddChild(_hud);

        // Notifications (QC Local_Notification): centerprint + kill-feed + announcer voice on the net path, routed
        // into the full HUD's panels. Previously only MSG_ANNCE was handled (via a hidden host Hud); now the full
        // HUD lets HudNotifications render center/info text too. OnNotificationReceived forwards EVERY type.
        _notifications = new XonoticGodot.Game.Hud.HudNotifications(_fullHud);
        if (_assets is not null)
        {
            _notifications.AudioLoader = _assets.LoadSound; // sound/announcer/<voice>/<snd>.ogg from the mounted VFS
            _notifications.AnnouncerResolver = name =>
                string.IsNullOrWhiteSpace(name) ? null : $"res://sound/announcer/default/{name}.ogg";
        }
        if (_client is not null)
            _client.NotificationReceived += OnNotificationReceived;

        // Minigames (QC cl_minigames.qc): the client-side coordinator drives the minigame board overlay + the
        // in-game menu off the S2C session snapshot and forwards board clicks / menu actions back as
        // `cmd minigame …` lines (the QC minigame_cmd localcmd). All wiring is in-process — the menu/renderer
        // live on _fullHud (the same Hud GameDemo uses), and the command + snapshot ride the existing
        // ClientCommand / MinigameState net channels.
        if (_client is not null)
        {
            _minigame = new MinigameClient(_fullHud, _fullHud.MinigameMenu, SendStringCommand);
            _client.MinigameStateReceived += OnMinigameStateReceived;
            // The list-sessions reply prints as ServerPrint lines; feed them to the Join menu's session list.
            _client.PrintReceived += OnServerPrintForMinigame;
            // Pong's paddle drive (QC pong_hud_board: minigame_cmd("move "+bits) on an arrow-key change). The
            // line is the SERVER command ("minigame move <bits>") — SendStringCommand IS the clc_stringcmd
            // forward channel, so it carries no extra "cmd " prefix (that's the DP local-console convention).
            _fullHud.PongMoveSink = bits => SendStringCommand($"minigame move {bits}");
        }

        _radar = new RadarPanel
        {
            Name = "Radar",
            Net = _client,
            Size = new Vector2(200, 200),
            Position = new Vector2(24, 24),
        };
        hudLayer.AddChild(_radar);

        // Networked scoreboard (QC HUD panel #25 + the +showscores toggle). The real play path is NetGame, whose
        // NetHud has no scoreboard — so the per-player columns + team totals that ClientNet decodes
        // (LatestScoreboard) had nowhere to go. Add the panel here, sized to a centered slab, hidden until the
        // scoreboard key is held; _Process feeds it from LatestScoreboard each frame the data changes.
        _scoreboard = new XonoticGodot.Game.Hud.ScoreboardPanel { Name = "Scoreboard", Visible = false };
        hudLayer.AddChild(_scoreboard);
        LayoutScoreboard();

        // Screen-effects layer (damage red-flash + liquid tint) — the SAME reusable ViewEffects node T4 built for
        // the local match, so the networked "play from menu" path also reacts to damage + underwater. It is its
        // own CanvasLayer (below the HUD), fed each frame in _Process from the networked stats + predicted eye.
        _viewEffects = new ViewEffects { Name = "ViewEffects" };
        AddChild(_viewEffects);

        // The loading screen (Shell's CanvasLayer 100) covers the viewport during the handshake and the
        // connect-as-observer window — the DP gfx/loading.tga + progress bar, replacing the old plain black
        // overlay. It is owned by Shell (above this node) and dismissed via DismissLoadingScreen once the camera
        // is seeded AND the local player is live (health > 0). If no loading screen was set (bare CLI), create
        // a minimal black fallback so the from-origin flash is still hidden.
        if (LoadingScreen is null)
        {
            var fallback = new CanvasLayer { Name = "ConnectOverlay", Layer = 100 };
            var cover = new ColorRect { Name = "Cover", Color = Colors.Black };
            cover.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            fallback.AddChild(cover);
            AddChild(fallback);
            _fallbackOverlay = fallback;
        }
    }
    private CanvasLayer? _fallbackOverlay;

    /// <summary>The view state the first-person <see cref="ViewModel"/> sway reads (QC viewmodel_animate inputs):
    /// the predicted local velocity + the live view angles + the predicted onground flag.</summary>
    private ViewModel.ViewState BuildViewState() => new()
    {
        VelocityQuake = _client?.PredictedVelocity ?? default,
        ViewAnglesQuake = _viewAngles,
        OnGround = _carrier?.OnGround ?? false,
    };

    /// <summary>Seed the zoom / spawn-zoom / eventchase cvars (xonotic-client.cfg defaults) so the shared
    /// <see cref="Client.FirstPersonView"/> reads authentic values even when a boot path didn't load the full
    /// client cfg tree (a bare CLI host / pure client). Register is idempotent — it never clobbers a value the
    /// loaded cfg/menu store already set. Mirrors <c>PlayerController._Ready</c>'s seeding.</summary>
    private static void SeedViewCvars()
    {
        if (Api.Services is null)
            return;
        Api.Cvars.Register("cl_zoomfactor", "5");      // xonotic-client.cfg:59
        Api.Cvars.Register("cl_zoomspeed", "8");       // xonotic-client.cfg:60
        Api.Cvars.Register("cl_zoomsensitivity", "0");
        Api.Cvars.Register("cl_spawnzoom", "1");       // xonotic-client.cfg:56-58 (spawn-zoom default ON)
        Api.Cvars.Register("cl_spawnzoom_speed", "1");
        Api.Cvars.Register("cl_spawnzoom_factor", "2");
        Api.Cvars.Register("fov", "100");
        Api.Cvars.Register("cl_eventchase_death", "2");
        Api.Cvars.Register("cl_eventchase_distance", "140");
        Api.Cvars.Register("cl_eventchase_speed", "1.3");
    }

    /// <summary>
    /// Install or swap the first-person weapon model to match the networked active weapon — QC view.qc:305-332
    /// picks the viewmodel's model from the active weapon's v_ model and rebuilds only on a weapon change. The
    /// owner's active weapon arrives in the snapshot owner block (<see cref="ClientNet.ActiveWeaponId"/>).
    /// Holstered / dead (id &lt; 0 or health &le; 0) hides the gun, mirroring view.qc masking it when dead.
    /// </summary>
    private void EquipNetworkedWeapon()
    {
        if (_viewModel is null || !GodotObject.IsInstanceValid(_viewModel) || _client is not { Accepted: true })
            return;

        int id = _client.ActiveWeaponId;
        // Hide the gun when holstered (id<0), dead (Health<=0), OR the event/death chase camera is active — QC
        // view.qc viewmodel_draw masks the viewmodel when STAT(HEALTH)<=0 || chase_active (the shared view tracks
        // ChaseActive after UpdateCamera ran this frame), so the third-person death-cam doesn't show a floating gun.
        bool hidden = id < 0 || id >= XonoticGodot.Common.Gameplay.Weapons.Count || _client.Health <= 0 || _view.ChaseActive;

        if (id == _equippedWeaponId)
        {
            _viewModel.Visible = !hidden; // no weapon change; just track the dead/holstered visibility edge
            return;
        }
        _equippedWeaponId = id;

        if (hidden)
        {
            _viewModel.Visible = false;
            return;
        }

        // Map the active weapon → its first-person v_ model: W_Model = "models/weapons/" + v_*.md3 (all.qc:233/367).
        // Weapon.WorldModel is the bare "v_laser.md3", so prefix the directory; a missing model → placeholder bar.
        XonoticGodot.Common.Gameplay.Weapon w = XonoticGodot.Common.Gameplay.Weapons.ById(id);
        string vModel = WeaponVModelPath(w);
        Node3D? built = string.IsNullOrEmpty(vModel) ? null : _assets?.LoadModel(vModel);
        Transform3D attach = string.IsNullOrEmpty(vModel)
            ? Transform3D.Identity
            : GameDemo.WeaponAttachTransform(_assets, vModel);
        _viewModel.SetWeaponModel(built, MuzzleEffectFor(w), "tag_shot", attach);
        _viewModel.Visible = true;
    }

    /// <summary>
    /// The first-/third-person <c>v_*</c> model vpath for a weapon (<c>"models/weapons/" + WorldModel</c>), or
    /// <c>""</c> when the weapon has no world model. The single source of truth shared by
    /// <see cref="EquipNetworkedWeapon"/>, <see cref="BuildWeaponWorldModel"/>, and
    /// <see cref="PrecacheWeaponModels"/> so they all hit the SAME asset-cache key.
    /// </summary>
    private static string WeaponVModelPath(XonoticGodot.Common.Gameplay.Weapon w)
        => string.IsNullOrEmpty(w.WorldModel) ? "" : "models/weapons/" + w.WorldModel;

    /// <summary>
    /// Warm every registered weapon's view model (and its sibling <c>h_*</c> hand rig) into the asset caches
    /// once, up-front. <see cref="EquipNetworkedWeapon"/> / <see cref="BuildWeaponWorldModel"/> otherwise read
    /// + decode the model and ALL its textures synchronously on the main thread the first time each weapon is
    /// switched to or picked up — the brief stutter on a never-seen-before weapon. Building each model once here
    /// (under the connect overlay, where a load pause is expected) populates <see cref="AssetLoader"/>'s parse
    /// cache and <see cref="AssetSystem"/>'s material/texture caches; the throwaway build node is freed
    /// immediately (it only exists to drive the cache-filling side effects). The later real load rebuilds a
    /// cheap mesh from the cached parse and reuses the cached materials, so the in-combat swap no longer hitches.
    ///
    /// <para>All weapons are warmed (not just this match's loadout): the cost is small and one-time, and it
    /// covers anything the player later picks up, switches to, or sees another player carry — faithful to
    /// DarkPlaces precaching weapon models at map load. Runs once (guarded by <see cref="_weaponsPrecached"/>).</para>
    /// </summary>
    private void PrecacheWeaponModels()
    {
        if (_assets is null || _weaponsPrecached)
            return;
        _weaponsPrecached = true;

        int warmed = 0, muzzles = 0;
        foreach (XonoticGodot.Common.Gameplay.Weapon w in XonoticGodot.Common.Gameplay.Weapons.All)
        {
            string vModel = WeaponVModelPath(w);
            if (string.IsNullOrEmpty(vModel))
                continue;

            // Build once to fill the parse + material/texture caches, then discard the orphan node (never added
            // to the tree → free it so it doesn't leak). A miss just caches the failure (no node to free).
            _assets.LoadModel(vModel)?.QueueFree();
            // The hand rig is loaded by WeaponAttachTransform on each switch; warm it too (it builds + frees its
            // own throwaway node internally and caches the attach transform's model).
            GameDemo.WeaponAttachTransform(_assets, vModel);

            // Per-weapon shot-origin (QC CL_WeaponEntity_SetModel: movedir = gettaginfo(weapon, "shot")). Extract
            // the weapon model's tag_shot in model-local Quake coords and register it so the (in-process, listen-
            // server) WeaponFiring.SetupShot spawns each weapon's shot from its real muzzle. The shot tag lives on
            // the h_ HAND RIG (the v_ visual model is attached to it), exactly as Base reads movedir from the
            // weaponchild rig (all.qc:412) — so prefer h_<name>.iqm, falling back to the v_ model. A model with no
            // shot tag leaves the weapon on the generic fallback. Runs once here at connect alongside the warm-up.
            string hModel = vModel.Replace("/v_", "/h_").Replace(".md3", ".iqm");
            System.Numerics.Vector3? shot =
                (hModel != vModel && _assets.Vfs.Exists(hModel) ? _assets.LoadMuzzleOffset(hModel) : null)
                ?? _assets.LoadMuzzleOffset(vModel);
            if (shot is { } so)
            {
                XonoticGodot.Common.Gameplay.WeaponFiring.RegisterMuzzleOffset(w.RegistryId, so);
                muzzles++;
            }
            warmed++;
        }
        GD.Print($"[NetGame] precached {warmed} weapon models ({muzzles} muzzle tags).");
    }

    /// <summary>The muzzle-flash effect name for a weapon (QC m_muzzleeffect = EFFECT_&lt;WEP&gt;_MUZZLEFLASH),
    /// derived from the weapon NetName to mirror the EffectsList registrations; defaults to the blaster flash
    /// (an unregistered name simply yields no flash, never an error).</summary>
    private static string MuzzleEffectFor(XonoticGodot.Common.Gameplay.Weapon w) => w.NetName switch
    {
        "vortex" => "VORTEX_MUZZLEFLASH",
        "devastator" => "ROCKET_MUZZLEFLASH",
        "mortar" => "GRENADE_MUZZLEFLASH",
        "machinegun" => "MACHINEGUN_MUZZLEFLASH",
        _ => "BLASTER_MUZZLEFLASH",
    };

    /// <summary>
    /// Build the third-person world model for a carried weapon id (RC6 — wired into
    /// <see cref="ClientEntityView.WeaponModelFactory"/>). Uses the same <c>v_*</c> model + asset pipeline as
    /// the first-person viewmodel so other players' weapons render textured instead of a placeholder box.
    /// Returns null (→ placeholder) for an out-of-range id, no asset loader, or a model that won't load.
    /// </summary>
    private Node3D? BuildWeaponWorldModel(int weaponId)
    {
        if (_assets is null || weaponId < 0 || weaponId >= XonoticGodot.Common.Gameplay.Weapons.Count)
            return null;
        XonoticGodot.Common.Gameplay.Weapon w = XonoticGodot.Common.Gameplay.Weapons.ById(weaponId);
        string vModel = WeaponVModelPath(w);
        return string.IsNullOrEmpty(vModel) ? null : _assets.LoadModel(vModel);
    }

    /// <summary>
    /// Resolve a non-player networked entity to its parsed MD3 for <see cref="ClientWorld.ModelResolver"/>.
    /// The server networks each entity's <c>.model</c> name (ServerNet), copied to <see cref="Entity.Model"/>
    /// on the client; world items/gibs/monsters then load+render it (textured, via the MD3 morph/snapshot
    /// path). Returns null for an empty / inline (<c>*N</c> brush) model or a non-MD3 model (IQM/DPM handled
    /// elsewhere) → the entity falls back to the placeholder box.
    /// </summary>
    private XonoticGodot.Formats.Md3.Md3Data? ResolveEntityModel(Entity e)
    {
        if (_assets is null || string.IsNullOrEmpty(e.Model) || e.Model.StartsWith('*'))
            return null;
        return _assets.LoadMd3(e.Model);
    }

    /// <summary>
    /// Format-agnostic fallback for <see cref="ClientWorld.EntityModelFactory"/>: build a fully-textured,
    /// animated node for a non-player world entity of ANY model format (IQM/DPM/MD3) via the asset pipeline
    /// (<see cref="AssetLoader.LoadModel"/>). Used when <see cref="ResolveEntityModel"/> returns null (non-MD3),
    /// so IQM/DPM props/items/monsters render their real model instead of the placeholder box. Null → placeholder.
    /// </summary>
    private Node3D? BuildEntityModel(Entity e)
    {
        if (_assets is null || string.IsNullOrEmpty(e.Model) || e.Model.StartsWith('*'))
            return null;
        return _assets.LoadModel(e.Model);
    }

    public override void _ExitTree() => Shutdown();

    private bool _shutDown;

    /// <summary>
    /// Dispose the client + (listen) server transports and free the prediction carrier — idempotent. Call this
    /// SYNCHRONOUSLY before <see cref="Node.QueueFree"/> when tearing a session down, so the server's UDP port is
    /// released immediately: a deferred <see cref="_ExitTree"/> would otherwise still hold the port when the next
    /// listen server (re-host) tries to bind it in the same frame. Also runs from <see cref="_ExitTree"/> as a
    /// safety net.
    /// </summary>
    public void Shutdown()
    {
        if (_shutDown)
            return;
        _shutDown = true;

        if (_client is not null)
        {
            _client.EffectReceived -= OnEffectReceived;
            _client.SoundReceived -= OnSoundReceived;
            _client.NotificationReceived -= OnNotificationReceived;
            _client.MinigameStateReceived -= OnMinigameStateReceived;
            _client.PrintReceived -= OnServerPrintForMinigame;
        }
        _minigame?.Dispose();
        _client?.Dispose();
        _server?.Dispose();
        // Free the carrier so a torn-down session doesn't leave a stray player edict in its facade. Use the
        // captured world (Api.Services may already have been swapped to the menu's empty facade by teardown).
        if (_carrier is not null && _world is not null && !_carrier.IsFreed)
            _world.Entities.Remove(_carrier);
    }

    // =====================================================================================
    //  Per-frame drive (the heart: server tick → client poll → send input → camera)
    // =====================================================================================

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        // Drive the listen server (if any) by real elapsed time — it runs its fixed ticks, pulls each client's
        // queued input, simulates, and broadcasts snapshots.
        _server?.Tick(dt);

        // Feed the music player the current server time so trigger_music touch freshness works.
        if (_musicPlayer is not null && _serverWorld is not null)
        {
            _musicPlayer.ServerTime = _serverWorld.Time;
            // Keep bgmvolume in sync (the cvar may change at runtime via the menu or console).
            float bgm = _sharedCvars?.GetFloat("bgmvolume") ?? _serverWorld.Services.Cvars.GetFloat("bgmvolume");
            if (bgm > 0f) _musicPlayer.BgmVolume = bgm;
        }

        // [T51] HUD feeds produced by the server-side mutators (host path only — a pure remote client has no
        // local server mutators). damagetext: drain the floating-number events to the client draw layer,
        // projecting the victim's Quake-space origin into Godot via Coords.ToGodot and the first-person camera.
        // itemstime: push the item-respawn-time table (with the negative "available now" encoding the panel
        // already handles) to the existing ItemsTimePanel.
        if (_server is not null)
        {
            if (_damageText is not null && Mutators.ByName("damagetext") is DamagetextMutator dtm)
            {
                _damageText.Camera = _camera;
                Player? localPlayer = LocalServerPlayer;
                foreach (DamageTextEvent ev in dtm.DrainPending())
                {
                    string wn = XonoticGodot.Common.Gameplay.Damage.DeathTypes.WeaponNetNameOf(ev.DeathType);
                    int colorKey = Weapons.ByName(wn) is { } w ? w.RegistryId : -1;
                    _damageText.Add(ev, Coords.ToGodot(ev.Target.Origin), colorKey);

                    // Hitsound: fire the hit-confirmation beep when the LOCAL player dealt the damage (not self-damage).
                    if (_hitSound is not null && localPlayer is not null
                        && ev.Attacker == localPlayer && ev.Target != localPlayer)
                    {
                        _hitSound.OnHit(ev.Health + ev.Armor);
                    }
                }
            }
            if (Mutators.ByName("itemstime") is ItemstimeMutator itm && itm.IsEnabled)
            {
                _fullHud.ItemsTime.Visible = true;
                _fullHud.ItemsTime.SetItemTimes(itm.CurrentTimes);
            }
        }

        // Advance the announcer queue (play the next queued voice if the current one finished).
        _notifications?.ProcessAnnouncerQueue();

        // Pump the client transport (handshake + snapshots + event bundles) before reading predicted state.
        if (_client is null)
            return;
        _client.Poll();

        // Advance the render clock: accumulate this frame, then rebase to the server time on a fresh snapshot so
        // it stays aligned with the clock the reconciler armed the stair/error decays with (see _renderClock).
        _renderClock += dt;
        if (_client.LatestServerTime != _lastSeenServerTime)
        {
            // A fresh snapshot landed: rebase the clock, and (on the very first one) seed the carrier from the
            // server's authoritative owner state so the predictor's first replay starts at the real spawn.
            bool firstSnapshot = _lastSeenServerTime < 0f;
            _lastSeenServerTime = _client.LatestServerTime;
            _renderClock = _client.LatestServerTime;
            if (firstSnapshot)
            {
                SeedCarrierFromServer();
                // C5: the camera now has an authoritative owner state to sit at — start placing it (the per-frame
                // UpdateCamera below is gated on _cameraReady until this runs).
                _cameraReady = true;
                UpdateCamera();
            }
        }

        if (_client.Accepted)
        {
            if (!_loggedAccept)
            {
                _loggedAccept = true;
                GD.Print($"[NetGame] handshake accepted by '{_client.ServerName}': netId {_client.LocalNetId}.");
            }

            // Fixed-timestep input: emit exactly one InputCommand per 1/72 s of REAL time, independent of the
            // display frame rate, so each command represents the dt it claims (DeltaTime = TicRate) and the
            // server advances this player at true wall-clock speed (sending one-per-render-frame would make the
            // player run faster at higher fps). Accumulate real delta and drain it in fixed quanta; cap the
            // backlog so a hitch can't trigger a spiral-of-death.
            const float tic = XonoticGodot.Engine.Simulation.SimulationLoop.TicRate;
            _inputAccum += dt;
            if (_inputAccum > MaxInputBacklog) _inputAccum = MaxInputBacklog;
            float clock = _renderClock;
            while (_inputAccum >= tic)
            {
                _inputAccum -= tic;
                clock += tic;
                // Sample WASD/mouse → InputCommand → push to the ring, predict forward, send the redundant tail.
                _client.SendInput(clock);

                // Teleporter view-snap (QC player.fixangle): the prediction tick above re-derives the carrier's
                // .fixangle — true exactly on the tick the local player is predicted through a single-dest
                // teleporter. Snap the accumulated view to the destination facing so the camera exits looking the
                // way the mapper aimed (CSQC setproperty(VF_CL_VIEWANGLES)), then consume the flag; later mouse-look
                // accumulates on top. Done INSIDE the tick loop so a second tick this frame samples the snapped
                // view. Multi-dest teleporters aren't predicted (random exit), so they stay server-authoritative.
                if (_carrier is not null && _carrier.FixAngle)
                {
                    _viewAngles = _carrier.FixAngleAngles;
                    _viewAngles.X = Mathf.Clamp(_viewAngles.X, -89f, 89f);
                    _carrier.FixAngle = false;
                }
            }
            // Read the camera at the clock the last prediction ran with, so the eye and the predicted origin agree.
            _renderClock = clock;
        }

        // Loading screen progress during the async handshake + spawn phase (DP's progress bar filling up
        // while connecting/joining). The loading screen is owned by Shell; we update it here and dismiss it
        // via DismissLoadingScreen when the player spawns.
        if (LoadingScreen is not null || _fallbackOverlay is not null)
        {
            if (_cameraReady && _client.Health > 0)
            {
                // Player is live — dismiss the loading screen (or the fallback black overlay).
                LoadingScreen?.UpdateProgress(1f, "");
                DismissLoadingScreen?.Invoke();
                LoadingScreen = null;
                DismissLoadingScreen = null;

                if (_fallbackOverlay is not null && GodotObject.IsInstanceValid(_fallbackOverlay))
                    _fallbackOverlay.QueueFree();
                _fallbackOverlay = null;
            }
            else if (LoadingScreen is not null)
            {
                // Still loading — update the progress bar for the async phase.
                if (!_client.Accepted)
                    LoadingScreen.UpdateProgress(0.85f, "Connecting...");
                else if (!_cameraReady)
                    LoadingScreen.UpdateProgress(0.90f, "Waiting for server...");
                else
                    LoadingScreen.UpdateProgress(0.95f, "Joining game...");
            }
        }

        // Spawn-zoom edge (QC cl_spawnzoom): the net path has no Teleport, so derive (re)spawn from a Health 0→>0
        // transition and arm the shared view's spawn-zoom on that edge (cl_spawnzoom eases the fov back out).
        if (_client.Accepted)
        {
            int h = _client.Health;
            if (_prevAliveHealth <= 0 && h > 0)
            {
                _view.TriggerSpawnZoom();
                _everAlive = true; // first/again alive: now Health<=0 means a real death (enables the death-cam)
            }
            _prevAliveHealth = h;
        }

        // QC button_zoom on the net path: held while the +zoom bind's key is down (BindTable). Suppressed when
        // dead (QC cl_unpress_zoom_on_death — the view zooms out on death), paused, or the console is open. Fed to
        // the shared view, which lerps current_viewzoom toward the target in UpdateView.
        bool zoomActive = !GetTree().Paused && !ConsoleState.IsOpen && _client.Accepted && _client.Health > 0;
        _view.ZoomHeld = zoomActive && BindTable.ZoomHeld;

        // Place the first-person camera at the predicted eye each frame (smooth even between snapshots, since
        // SendInput re-predicts every tick). C5: held until the first snapshot seeds the carrier — before that
        // the predicted origin is (0,0,0) and the camera would render a from-world-origin frame during the
        // handshake. The camera is first-placed in the firstSnapshot branch above. Drives the shared view (zoom
        // lerp + camera placement + eventchase + eye-contents), so it must run BEFORE the ViewEffects feed below
        // (which reads SampleEyeContents = _view.EyeContents).
        if (_cameraReady)
            UpdateCamera(dt);

        // Feed the full HUD's player-bound panels (health/ammo/weapons/crosshair) the local server Player on a
        // listen server, so they reflect live local state as the spawn lands (QC the view player). A pure client
        // has no local Player actor — the NetHud crosshair/health covers it. Cheap: SetPlayer no-ops when same.
        UpdateFullHudPlayer();

        // Install / swap the first-person weapon model when the networked active weapon changes (CSQC view.qc:305
        // picks the v_ model from the active weapon, rebuilding only on a swap).
        EquipNetworkedWeapon();

        // Screen reactions (QC HUD_Damage red-flash + HUD_Contents liquid tint) on the live client: feed the
        // reusable view-effects layer from the networked local health (flash on a drop) + the predicted eye
        // contents. Gated on Accepted so the pre-spawn default (health 0) doesn't read as death.
        if (_viewEffects is not null && _client is { Accepted: true })
        {
            int health = _client.Health;
            if (_prevHealth >= 0 && health < _prevHealth)
                _viewEffects.ReportDamage(_prevHealth - health);
            _prevHealth = health;
            // "observing" = not yet spawned (the pre-spawn / connecting window right after Create): mirror QC's
            // spectatee_status == -1 so health 0 here doesn't ramp the death fade onto the screen. _everAlive flips
            // true on the first spawn (line ~1212), so a genuine in-match death (health<=0 after spawning) still
            // shows the death fade. Matches the IsDead gate used for the death-cam below.
            _viewEffects.UpdateEffects(dt, health, SampleEyeContents(), !_everAlive);
        }

        // Keep the radar oriented to the player's facing.
        if (_radar is not null)
            _radar.LocalYawDegrees = _viewAngles.Y;

        // Scoreboard (QC +showscores): show while the scoreboard key is held, and feed it the networked rows +
        // team totals whenever a fresh LatestScoreboard arrives (the panel only repaints on data/toggle, so this
        // is cheap). BindTable.ShowScores is the held-button state set from the +showscores bind.
        UpdateScoreboard();

        // Minigame cursor (QC hud_cursormode): while a minigame board/menu owns input, show the cursor so the
        // player can click TTT/C4 tiles + the menu; recapture for play on the edge back out. Skip while the
        // pause menu/console own the cursor (the Shell drives those).
        if (!GetTree().Paused && !ConsoleState.IsOpen)
        {
            bool ui = MinigameMenuOpen;
            if (ui != _minigameUiOwnedCursor)
            {
                _minigameUiOwnedCursor = ui;
                Input.MouseMode = ui ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
            }
        }

        // A changelevel was requested this frame (map / gotomap / nextmap / rotation / vote / samelevel): emit it
        // DEFERRED so the actual teardown+reboot (Shell) runs at idle, never inside this server tick. Capture the
        // live gametype + bot fill so the new map comes up in the same mode with the same bots (DP changelevel).
        if (_pendingMap is not null)
        {
            string map = _pendingMap;
            _pendingMap = null;
            string gametype = CurrentGametype();
            int bots = _serverWorld?.Clients.BotCount ?? _botCount;
            int skill = _botSkill;
            // Campaign auto-advance (win → next level): the world store carries g_campaign + the NEXT
            // _campaign_index (Campaign.Setup set them just before this transition). Pass them so the rebooted
            // listen server stays in campaign mode at the next level. Empty for a normal map/gotomap change.
            string campId = "";
            int campIdx = 0;
            if (_serverWorld is not null && _serverWorld.Services.Cvars.GetFloat("g_campaign") != 0f)
            {
                campId = _serverWorld.Services.Cvars.GetString("_campaign_name");
                campIdx = (int)_serverWorld.Services.Cvars.GetFloat("_campaign_index");
            }
            Callable.From(() => MapChangeRequested?.Invoke(map, gametype, bots, skill, campId, campIdx)).CallDeferred();
        }
    }

    /// <summary>The active gametype short code for a changelevel: the live <c>gametype</c> cvar if a mid-match
    /// <c>gametype</c> command set it, else the mode this match booted with.</summary>
    private string CurrentGametype()
    {
        string gt = _serverWorld?.Services.Cvars.GetString("gametype") ?? "";
        return string.IsNullOrWhiteSpace(gt) ? _gametype : gt;
    }

    /// <summary>
    /// Point the full HUD's player-bound panels (health/ammo/weapons/crosshair/speedo) at the local actor: on a
    /// listen server that is <see cref="LocalServerPlayer"/> (resolved once the spawn lands); on a pure client
    /// there is no local Player so it stays null (the player-agnostic panels — centerprint/killfeed — still draw,
    /// and the NetHud crosshair/health covers the rest). Only re-applies on a change so the per-frame call is cheap.
    /// </summary>
    private void UpdateFullHudPlayer()
    {
        if (_fullHud is null)
            return;
        Player? p = LocalServerPlayer; // null on a pure client (no in-process world)
        if (ReferenceEquals(p, _lastHudPlayer))
            return;
        _lastHudPlayer = p;
        _fullHud.SetPlayer(p);
    }
    private Player? _lastHudPlayer;

    /// <summary>Size the scoreboard panel to a centered slab of the viewport (QC HUD_Panel_UpdatePosSize for the
    /// scoreboard, simplified). Called once at setup; the panel reads its own rect via <c>Configure</c>.</summary>
    private void LayoutScoreboard()
    {
        if (_scoreboard is null) return;
        Vector2 vp = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1280, 720);
        float w = Mathf.Min(vp.X * 0.8f, 1100f);
        float h = vp.Y * 0.85f;
        _scoreboard.Configure(new Rect2((vp.X - w) * 0.5f, vp.Y * 0.06f, w, h));
    }

    /// <summary>
    /// Toggle + feed the networked scoreboard. THE consumer of <see cref="ClientNet.LatestScoreboard"/> on the
    /// real play path (it was decoded but never read). Shows the panel while <c>+showscores</c> is held; when a
    /// new scoreboard frame has arrived it hydrates the panel rows from the wire (resolving netId→local via the
    /// client's <see cref="ClientNet.LocalNetId"/>; name/team ride the wire entcs slice) + sets the title and the
    /// fraglimit/timelimit/map header from the live cvars.
    /// </summary>
    private void UpdateScoreboard()
    {
        if (_scoreboard is null || _client is null)
            return;

        bool show = XonoticGodot.Engine.Console.BindTable.ShowScores;
        if (_scoreboard.Visible != show)
            _scoreboard.Visible = show;
        if (!show)
            return;

        // feed rows only when the data changed (identity by reference — ClientNet replaces the object per decode).
        XonoticGodot.Net.ScoreboardWire? sb = _client.LatestScoreboard;
        if (sb is not null && !ReferenceEquals(sb, _lastFedScoreboard))
        {
            _lastFedScoreboard = sb;
            _scoreboard.Title = ScoreboardTitle();
            _scoreboard.SetWireRows(sb, _client.LocalNetId);
            FeedScoreboardHeader();
        }
        else if (sb is null)
        {
            // No networked scoreboard yet (pre-first-snapshot): show an empty titled panel rather than nothing.
            _scoreboard.Title = ScoreboardTitle();
        }
    }
    private XonoticGodot.Net.ScoreboardWire? _lastFedScoreboard;

    /// <summary>The scoreboard title: the active gametype's display name from the networked ScoreInfo (else the
    /// configured gametype). QC the scoreboard header gametype name.</summary>
    private string ScoreboardTitle()
    {
        string gt = XonoticGodot.Common.Gameplay.Scoring.GameScores.Gametype;
        return string.IsNullOrEmpty(gt) ? "Scoreboard" : gt.ToUpperInvariant();
    }

    /// <summary>Feed the scoreboard's fraglimit/timelimit/map-name header from the live cvar store (QC
    /// Scoreboard_Fraglimit_Draw reads STAT(FRAGLIMIT)/TIMELIMIT; we read the cvars the server replicated /
    /// the host set). Best-effort: a pure client without those cvars simply shows no header line.</summary>
    private void FeedScoreboardHeader()
    {
        _scoreboard.MapName = _map;
        var cvars = Api.Services?.Cvars;
        if (cvars is null) return;
        _scoreboard.FragLimit = (int)cvars.GetFloat("fraglimit");
        _scoreboard.TimeLimitMinutes = (int)cvars.GetFloat("timelimit");
        // [T43] Feed the scoreboard map-stats row (QC monsters_setstatus → STAT(MONSTERS_TOTAL/KILLED)).
        // -1 hides the row when there are no monsters (DrawMapStats gates on MonstersTotal > 0).
        _scoreboard.MonstersTotal  = XonoticGodot.Common.Gameplay.MonsterAI.MonstersTotal > 0 ? XonoticGodot.Common.Gameplay.MonsterAI.MonstersTotal : -1;
        _scoreboard.MonstersKilled = XonoticGodot.Common.Gameplay.MonsterAI.MonstersKilled;
    }

    /// <summary>
    /// Seed the carrier's origin/velocity from the latest authoritative snapshot once the handshake lands, so
    /// the predictor's first replay starts at the server's spawn position. (Without this the carrier would
    /// predict from the world origin until the first reconcile pulled it across — a visible snap.)
    /// </summary>
    private void SeedCarrierFromServer()
    {
        if (_carrier is null || _client is null)
            return;
        _carrier.Origin = _client.PredictedOrigin;
        _carrier.OldOrigin = _carrier.Origin;
        _carrier.Velocity = _client.PredictedVelocity;
    }

    // =====================================================================================
    //  Input sampling (WASD/mouse → InputCommand) — mirrors PlayerController's gather
    // =====================================================================================

    public override void _UnhandledInput(InputEvent @event)
    {
        // Minigame menu toggle (QC the +minigamemenu bind — no default key in Base; we bind 'M'). Opens/closes
        // the in-game Create/Join/Current-Game menu. Active even during play (so you can start a game), but not
        // while the console is open. When the menu is open it captures the cursor so the player can click it.
        if (!ConsoleState.IsOpen && _minigame is not null
            && @event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.M })
        {
            _minigame.ToggleMenu();
            // The cursor show/recapture is driven each frame in _Process from MinigameMenuOpen (which now reflects
            // the new menu state), so it stays correct even when a board is still active under a closed menu.
            GetViewport().SetInputAsHandled();
            return;
        }

        // Bind-driven gameplay input (DP key bindings): a keyboard/mouse-button press/release drives the +/-
        // held-button state and runs one-shot bound commands. Routed through RunBoundCommand, which converts the
        // WEAPON-SWITCH / RELOAD binds into a C2S impulse (the QC usercmd.impulse channel — the real way a human
        // switches weapons on the net path) and forwards everything else (kill/say/team/…) to the shared
        // interpreter. Frozen while the console is open or the match is paused (a key the console didn't consume
        // still reaches here, so the explicit ConsoleState gate is what stops binds firing under the open console).
        if (!ConsoleState.IsOpen && !GetTree().Paused && !MinigameMenuOpen
            && @event is InputEventKey or InputEventMouseButton)
            BindInput.HandleEvent(@event, RunBoundCommand);

        // Accumulate mouse-look while the cursor is captured (the Shell owns Escape + the mouse around the
        // in-game menu). Mouse right → yaw decreases (Quake CCW yaw); mouse down → pitch increases (down-positive).
        // Sensitivity is the live `sensitivity` cvar (the value the input-settings dialog binds), not a hardcoded
        // constant; the `m_pitch` SIGN gives invert-Y (the dialog's "Invert aiming" flips m_pitch < 0). The shared
        // view's SensitivityScale folds in (QC setsensitivityscale) so zoomed aim is finer on the net path too.
        if (@event is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            float sens = LookSensitivity() * _view.SensitivityScale;
            _viewAngles.Y -= motion.Relative.X * sens;
            _viewAngles.X += motion.Relative.Y * sens * PitchSign();
            _viewAngles.X = Mathf.Clamp(_viewAngles.X, -89f, 89f);
        }
    }

    /// <summary>
    /// Run a one-shot console command issued by a bind on the NET path. Weapon-switch / reload binds are turned
    /// into a C2S impulse number (QC usercmd.impulse — the faithful way the weapon command rides the move
    /// command): the number is stamped into <see cref="_pendingImpulse"/>, which <see cref="SampleInput"/> stamps
    /// onto the next <see cref="InputCommand"/> (edge-triggered, one-shot). The server dispatches it through the
    /// gated <c>impulse</c> path (<c>WeaponImpulses.Handle</c>). Everything else (kill/say/team/+showscores side
    /// effects/…) forwards to the shared interpreter (<see cref="RunCommand"/>, wired by the Shell). Mirrors the
    /// command set in <c>PlayerController.RunBoundCommand</c> + <c>BindInput</c>'s weapon bind strings.
    /// </summary>
    private void RunBoundCommand(string command)
    {
        if (string.IsNullOrEmpty(command))
            return;

        int imp = WeaponCommandToImpulse(command);
        if (imp != 0)
        {
            _pendingImpulse = imp; // edge-triggered: SampleInput consumes it on the next command, then clears it
            return;
        }

        // Not a weapon command — forward to the shared interpreter (kill/say/team/…). Unknown commands there are
        // just counted (DP Cmd_ForwardToServer is not wired for the net path), so this is a no-op for them.
        RunCommand?.Invoke(command);
    }

    /// <summary>
    /// Map a bound command STRING to its Xonotic impulse number (common/impulses/all.qh — the same numbers
    /// <see cref="WeaponImpulses"/> dispatches). Covers the weapon binds BindInput emits: <c>weapon_group_N</c>
    /// (1..9 → N, 0 → 14), <c>weapnext</c>/<c>weapprev</c> (10/12), <c>weapon_last</c>/<c>weaplast</c> (11),
    /// <c>weapon_best</c>/<c>weapbest</c> (13), <c>weapon_drop</c>/<c>dropweapon</c> (17),
    /// <c>weapon_reload</c>/<c>reload</c> (20), and <c>weapon_byid_N</c> (230+N). Returns 0 for a non-weapon
    /// command. Case-insensitive.
    /// </summary>
    private static int WeaponCommandToImpulse(string command)
    {
        // weapon_group_N (the 1..9 keys via binds-xonotic.cfg) → impulse N (group 0 → 14).
        if (command.StartsWith("weapon_group_", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(command.AsSpan("weapon_group_".Length), out int group) && group is >= 0 and <= 9)
            return group == 0 ? 14 : group;

        // weapon_byid_N → 230+N (direct weapon by unique id).
        if (command.StartsWith("weapon_byid_", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(command.AsSpan("weapon_byid_".Length), out int byid) && byid is >= 0 and <= 23)
            return 230 + byid;

        return command.ToLowerInvariant() switch
        {
            "weapnext" => 10,
            "weapprev" => 12,
            "weapon_last" or "weaplast" => 11,
            "weapon_best" or "weapbest" => 13,
            "weapon_drop" or "dropweapon" => 17,
            "weapon_reload" or "reload" => 20,
            _ => 0,
        };
    }

    /// <summary>The live look sensitivity (DP `sensitivity` cvar) folded with the base feel — the value the
    /// input-settings dialog writes. Falls back to the previous hardcoded feel when the cvar is unset.</summary>
    private float LookSensitivity()
    {
        float s = Api.Services is not null ? Api.Cvars.GetFloat("sensitivity") : 0f;
        // The `sensitivity` cvar is ~1..9 (xonotic default 6); scale it into the deg/pixel feel the prior
        // constant (0.15 ≈ sensitivity 6 × 0.025) gave, so existing aim is unchanged at the default.
        return s > 0f ? s * 0.025f : 0.15f;
    }

    /// <summary>Invert-Y sign from `m_pitch` (DP: negative pitch inverts the Y axis). +1 normal, −1 inverted.</summary>
    private static float PitchSign()
    {
        if (Api.Services is null) return 1f;
        return Api.Cvars.GetFloat("m_pitch") < 0f ? -1f : 1f;
    }

    /// <summary>
    /// Sample one tick of live input into an <see cref="InputCommand"/> (the unit the client predicts on and
    /// the server applies). WASD → Quake-space forward/side (normalised to ±1; the move code rescales against
    /// sv_maxspeed), Space → jump, Ctrl → crouch, mouse buttons → attack. The accumulated view angles ride
    /// along so the server applies the exact aim the client predicted with. Called by <see cref="ClientNet"/>
    /// each <see cref="ClientNet.SendInput"/>.
    /// </summary>
    /// <summary>True while the in-game minigame UI owns input — the Create/Join menu is open OR a minigame board
    /// is active (so the player can click TTT/C4 tiles / read the board). The cursor is shown and gameplay input
    /// (move/look/binds) is suppressed, exactly like the pause menu/console. QC: the minigame menu/board run with
    /// hud_cursormode. (Pong's paddle arrow keys are still read directly by HudManager.DrivePongKeys, so Pong
    /// plays even while gameplay input is suppressed.)</summary>
    private bool MinigameMenuOpen
        => _fullHud is not null
        && ((_fullHud.MinigameMenu is { IsOpen: true }) || (_minigame is not null && _minigame.Active is not null));

    private InputCommand SampleInput()
    {
        // Re-capture on click if the user released the mouse (e.g. after alt-tab); never while the tree is
        // paused (the in-game menu), the console is open, or the minigame menu is up — those own the cursor.
        if (!GetTree().Paused && !ConsoleState.IsOpen && !MinigameMenuOpen
            && Input.MouseMode != Input.MouseModeEnum.Captured && Input.IsMouseButtonPressed(MouseButton.Left))
            Input.MouseMode = Input.MouseModeEnum.Captured;

        // Gameplay input is inert while the in-game menu is up (paused), the console is open, OR the minigame
        // menu is open. On the edge into inactive, drop all held buttons (DP in_releaseall) so a key held at that
        // moment doesn't stay down once input resumes. The view angles hold their last value, so the camera stays put.
        bool active = !GetTree().Paused && !ConsoleState.IsOpen && !MinigameMenuOpen;
        if (active != _inputActive)
        {
            _inputActive = active;
            if (!active) { BindTable.ReleaseAll(); _attackHeld = false; }
        }

        float forward = 0f, side = 0f, up = 0f;
        InputButtons buttons = InputButtons.None;
        if (active)
        {
            // Bind-driven button state (set from key events by BindInput → BindTable). Move axes are normalised
            // to ±1 here; the movement code rescales against sv_maxspeed. Jump/crouch both drive the up axis
            // (via BindTable.Up) and set their button bit.
            forward = BindTable.Forward;
            side = BindTable.Side;
            up = BindTable.Up;
            if (BindTable.AttackHeld) buttons |= InputButtons.Attack;
            if (BindTable.Attack2Held) buttons |= InputButtons.Attack2;
            if (BindTable.JumpHeld) buttons |= InputButtons.Jump;
            if (BindTable.CrouchHeld) buttons |= InputButtons.Crouch;
            if (BindTable.ZoomHeld) buttons |= InputButtons.Zoom;
            if (BindTable.UseHeld) buttons |= InputButtons.Use;

            // Client fire hook (muzzle flash) on the rising edge of primary fire.
            bool attack1 = BindTable.AttackHeld;
            if (attack1 && !_attackHeld)
            {
                _hud?.PulseFire();
                _viewModel?.Fire(); // local viewmodel muzzle flash + recoil (CSQC W_MuzzleFlash on the local attack)
            }
            _attackHeld = attack1;
        }

        // C2S impulse (QC usercmd.impulse): consume the one-shot weapon-switch/reload number a bind set this
        // frame (RunBoundCommand stamped it into _pendingImpulse, edge-triggered). Stamp it onto THIS command and
        // clear the pending value so it rides exactly one InputCommand — the redundant input tail re-sends the
        // same Seq (carrying the impulse), but the server's Seq-dedup processes it once, and the server zeroes it
        // on the cached command so the starve-repeat can't re-fire it. Suppressed while input is inactive.
        int impulse = active ? _pendingImpulse : 0;
        _pendingImpulse = 0;

        // Seq is stamped by the prediction ring buffer (PredictionBuffer.Push), so we leave it 0 here.
        return new InputCommand
        {
            ViewAngles = _viewAngles,
            Forward = forward,
            Side = side,
            Up = up,
            Buttons = (int)buttons,
            Impulse = impulse,
            DeltaTime = XonoticGodot.Engine.Simulation.SimulationLoop.TicRate,
        };
    }

    // =====================================================================================
    //  Camera (driven by the SHARED FirstPersonView — same component PlayerController uses)
    // =====================================================================================

    /// <summary>
    /// Drive the shared <see cref="Client.FirstPersonView"/> from the PREDICTED local state: the predicted origin
    /// (with the decaying prediction-error + stair-smooth offsets folded in) + predicted velocity + the dead test
    /// (Health&lt;=0 — the net path has no MaxHealth). FirstPersonView places + orients the camera with the proven
    /// AngleVectors → Coords.ToGodot columns → Basis(right, up, -forward) path (a negated-yaw Euler silently flips
    /// handedness), runs the death/event-chase pull-back, lerps the zoom, applies the *0.75 fov, and samples the
    /// eye contents at the FINAL render origin — exactly as on the local path. <paramref name="dt"/> 0 = a
    /// placement-only call (the first-snapshot seed); the per-frame call in <see cref="_Process"/> passes real dt.
    /// </summary>
    private void UpdateCamera(float dt = 0f)
    {
        if (_client is null || _camera is null)
            return;

        float now = _renderClock; // the clock the reconciler armed the stair/error decays with (see _Process)
        NVec3 predicted = _client.PredictedOrigin + _client.PredictionErrorOffset(now);
        // Subtract the decaying stair-smooth Z so the camera glides over steps (cl_movement stairsmooth).
        predicted.Z -= _client.PredictedStairOffset(now);

        var st = new Client.FirstPersonView.ViewState
        {
            OriginQuake = predicted,
            VelocityQuake = _client.PredictedVelocity,
            ViewAnglesQuake = _viewAngles,
            // net path has no MaxHealth, so dead = Health<=0 — but ONLY after the first spawn (_everAlive), so the
            // pre-spawn observer 0 doesn't engage the death-cam at the world origin (the demo path uses
            // MaxHealth>0 for the same fresh-spawn-at-0 guard).
            IsDead = _everAlive && _client.Health <= 0,
            // Eye drops while crouched: the predicted carrier carries the live view offset (PlayerPhysics.UpdateCrouch
            // sets ViewOfs to the crouch/standing value each predicted tick, QC STAT(PL_CROUCH_VIEW_OFS)/PL_VIEW_OFS).
            EyeHeightZ = _carrier?.ViewOfs.Z ?? EyeHeight,
        };
        _view.UpdateView(_camera, st, dt);
    }

    /// <summary>SUPERCONTENTS at the FINAL render origin this frame — read back from the shared view after
    /// <see cref="UpdateCamera"/> ran (QC pointcontents(view_origin) at the pulled-back cam during a chase). A
    /// listen server samples the real map collision; a pure client's flat facade reports empty until client-side
    /// map collision lands.</summary>
    private int SampleEyeContents() => _view.EyeContents;

    // =====================================================================================
    //  Effect rendering (decoded EFF_NET_* → particles)
    // =====================================================================================

    private void OnEffectReceived(ClientNet.EffectEvent e)
    {
        if (_render is null || !GodotObject.IsInstanceValid(_render))
            return;
        string name = e.Effect?.Name ?? "";
        if (string.IsNullOrEmpty(name))
            return;
        Color? tint = (e.ColorMin != default || e.ColorMax != default)
            ? new Color((e.ColorMin.X + e.ColorMax.X) * 0.5f, (e.ColorMin.Y + e.ColorMax.Y) * 0.5f, (e.ColorMin.Z + e.ColorMax.Z) * 0.5f)
            : (Color?)null;
        _render.OnEffect(name, e.Origin, e.Velocity, Math.Max(1, e.Count), tint);
    }

    /// <summary>A decoded positional sound (DP SV_StartSound) → play it on the renderer. Routes by the wire flags:
    /// a STOP ends the looping sound on the emitter+channel; a LOOP starts/refreshes a persistent source keyed by
    /// (emitter, channel) that follows the entity (Arc beam, vehicle engines); otherwise a one-shot on the pool.
    /// Calls <see cref="ClientWorld"/> directly with volume+attenuation (NOT the legacy 2-arg SoundHook).</summary>
    private void OnSoundReceived(ClientNet.SoundEvent e)
    {
        if (_render is null || !GodotObject.IsInstanceValid(_render))
            return;
        if (e.Stop)
        {
            // DP sound(e, ch, SND_Null): end the loop on this emitter+channel (the Arc beam on release/overheat).
            _render.OnStopSound(e.SourceNetId, e.Channel);
            return;
        }
        if (string.IsNullOrEmpty(e.Sample))
            return;
        if (e.Loop)
        {
            // A persistent loop keyed by (emitter, channel) that follows the entity (QC loopsound).
            _render.OnLoopingSound(e.SourceNetId, e.Channel, e.Sample, e.Origin, e.Volume, e.Attenuation);
            return;
        }
        _render.OnSound(e.Sample, e.Origin, e.Volume, e.Attenuation, e.Channel, e.SourceNetId, e.Pitch);
    }

    /// <summary>
    /// The current Quake-space origin of a networked entity by net id (<see cref="ClientWorld.EntityOriginResolver"/>),
    /// so a looping positional sound follows its emitter each frame: the LOCAL player tracks the predicted origin,
    /// a remote entity its interpolated snapshot pose. Null when the id isn't known yet (the loop holds its spot).
    /// </summary>
    private NVec3? ResolveEntityOrigin(int netId)
    {
        if (_client is null)
            return null;
        if (netId == _client.LocalNetId)
            return _client.PredictedOrigin;
        if (_client.SampleRemote(netId, _client.LatestServerTime, out NVec3 origin, out _))
            return origin;
        return null;
    }

    /// <summary>A decoded notification → drive the announcer voice (MSG_ANNCE only) through the hidden HUD host.
    /// Center/info text need the full panel HUD (NetHud lacks it), so they're skipped on the net path for now.</summary>
    /// <summary>A decoded notification → drive the full HUD (T34): MSG_CENTER → centerprint, MSG_INFO →
    /// kill-feed / info line, MSG_ANNCE → announcer voice. Previously only MSG_ANNCE was handled (the net path
    /// had no panel HUD); now the full <see cref="XonoticGodot.Game.Hud.Hud"/> lets <see cref="XonoticGodot.Game.Hud.HudNotifications"/>
    /// render center/info text too. HudNotifications routes by type internally.</summary>
    private void OnNotificationReceived(ClientNet.NotificationEvent e)
    {
        if (_notifications is null)
            return;
        _notifications.OnNotification(e.Notification, e.Type, e.Text, e.StringArgs, e.FloatArgs);
    }

    /// <summary>A decoded minigame session snapshot (QC activate/deactivate_minigame) → drive the board overlay
    /// + menu through the client coordinator.</summary>
    private void OnMinigameStateReceived(MinigameNetState.Envelope env) => _minigame?.OnEnvelope(env);

    /// <summary>Feed each ServerPrint line to the minigame coordinator's session-list parser (the
    /// <c>cmd minigame list-sessions</c> reply populates the Join menu). Harmless for non-minigame prints — the
    /// coordinator only keeps single-token "&lt;game&gt;_&lt;n&gt;" lines.</summary>
    private void OnServerPrintForMinigame(string line) => _minigame?.NoteSessionListLine(line);

    // =====================================================================================
    //  Asset resolution (shared with GameDemo's wiring; built per entity on first sight)
    // =====================================================================================

    private PlayerModel? ResolvePlayerModel(Entity e)
    {
        if (_assets is null || string.IsNullOrEmpty(e.Model)
            || e.Model.IndexOf("player", StringComparison.OrdinalIgnoreCase) < 0)
            return null;
        AssetLoader.SkeletalModelParts? parts = _assets.LoadSkeletalModel(e.Model, (int)e.Skin);
        if (parts is null)
            return null;
        var pm = new PlayerModel { Name = $"player#{e.Index}" };
        pm.Setup(parts.Iqm, parts.Root, parts.Groups, parts.Info);
        if (!pm.Active) { pm.QueueFree(); return null; }
        return pm;
    }

    /// <summary>
    /// Build a FORCED player model (QC <c>cl_forcemyplayermodel</c> / <c>cl_forceplayermodels</c> swap) for
    /// <see cref="ClientWorld.ForcedModelResolver"/>: resolve the forced model NAME+skin to a skeletal
    /// <see cref="PlayerModel"/> (the IQM player path) when it parses, else fall back to the format-agnostic
    /// loader (a plain MD3/DPM node). Returns null when the forced model doesn't exist (QC <c>fexists</c>-miss),
    /// so the client keeps the entity's own model.
    /// </summary>
    private Node3D? BuildForcedPlayerModel(string model, int skin)
    {
        if (_assets is null || string.IsNullOrEmpty(model))
            return null;
        // Prefer the skeletal player path (matches ResolvePlayerModel) so the forced model still poses/animates.
        AssetLoader.SkeletalModelParts? parts = _assets.LoadSkeletalModel(model, skin);
        if (parts is not null)
        {
            var pm = new PlayerModel { Name = $"forced_{System.IO.Path.GetFileNameWithoutExtension(model)}" };
            pm.Setup(parts.Iqm, parts.Root, parts.Groups, parts.Info);
            if (pm.Active)
                return pm;
            pm.QueueFree();
        }
        // Non-IQM forced model (or skeletal setup failed): a plain textured/animated node.
        return _assets.LoadModel(model, skin);
    }

    /// <summary>
    /// Assemble the live CSQC appearance context (QC <c>CSQCPlayer_ModelAppearance_Apply</c> needs the local
    /// player + gametype) for <see cref="ClientWorld.AppearanceProvider"/>. Prefers the listen-server world (it
    /// holds authoritative team/gametype/teamplay + team count); a pure client falls back to the networked
    /// <see cref="ClientNet.LatestScoreInfo"/> (gametype/teamplay) with an unknown local team. Returns null only
    /// before the client is accepted (no force model/colors yet).
    /// </summary>
    private ClientWorld.AppearanceContext? BuildAppearanceContext()
    {
        if (_client is null || !_client.Accepted)
            return null;

        var ctx = new ClientWorld.AppearanceContext { LocalNetId = _client.LocalNetId };

        // Listen server: authoritative gametype/teamplay/team-count + the local player's team.
        if (_serverWorld is not null)
        {
            ctx.Teamplay = _serverWorld.Teamplay is { IsTeamGame: true };
            ctx.TeamCount = _serverWorld.Teamplay?.TeamCount ?? 0;
            ctx.Is1v1 = string.Equals(_serverWorld.GameType?.NetName, "duel", StringComparison.OrdinalIgnoreCase);
            Player? me = LocalServerPlayer;
            if (me is not null)
                ctx.MyTeam = (int)me.Team;
            return ctx;
        }

        // Pure client: gametype/teamplay from the networked ScoreInfo block (team count + local team unknown here).
        if (_client.LatestScoreInfo is { } si)
        {
            ctx.Teamplay = si.Teamplay;
            ctx.Is1v1 = string.Equals(si.Gametype, "duel", StringComparison.OrdinalIgnoreCase);
            ctx.TeamCount = si.Teamplay ? 2 : 0; // best-effort: the 2-team force-color gate (the common case)
        }
        return ctx;
    }

    // =====================================================================================
    //  Map / world helpers
    // =====================================================================================

    private XonoticGodot.Formats.Bsp.BspData? TryLoadMapBsp(string map)
    {
        if (_assets is null || string.IsNullOrWhiteSpace(map))
            return null;
        foreach (string vpath in MapVPathCandidates(map))
        {
            if (!_assets.Vfs.Exists(vpath))
                continue;
            XonoticGodot.Formats.Bsp.BspData? bsp = _assets.ReadBsp(vpath);
            if (bsp is not null)
                return bsp;
        }
        return null;
    }

    /// <summary>
    /// Convert the BSP entity lump (a list of key/value dicts) into the <see cref="EntityDict"/> list the
    /// <see cref="GameWorld"/> constructor takes, so the server's spawn loop (QC the entity-lump →
    /// spawnfunc_CLASSNAME) mints the map's spawn points, jump-pads, items, doors, etc. Origin/angles are
    /// parsed into the struct's first-class fields (the world reads them there); every key (incl. origin/angles)
    /// is also kept in <see cref="EntityDict.Fields"/> for the generic spawnfunc field reads.
    /// </summary>
    private static System.Collections.Generic.List<EntityDict> BuildEntityDicts(XonoticGodot.Formats.Bsp.BspData bsp)
    {
        var list = new System.Collections.Generic.List<EntityDict>(bsp.Entities.Count);
        foreach (System.Collections.Generic.IReadOnlyDictionary<string, string> dict in bsp.Entities)
        {
            if (!dict.TryGetValue("classname", out string? cls) || string.IsNullOrEmpty(cls))
                continue;
            var ed = new EntityDict
            {
                ClassName = cls,
                Origin = ParseVec(dict, "origin"),
                Angles = ParseVec(dict, "angles"),
            };
            foreach (System.Collections.Generic.KeyValuePair<string, string> kv in dict)
                ed.Fields[kv.Key] = kv.Value;
            list.Add(ed);
        }
        return list;
    }

    private static NVec3 ParseVec(System.Collections.Generic.IReadOnlyDictionary<string, string> f, string key)
    {
        if (!f.TryGetValue(key, out string? s) || string.IsNullOrWhiteSpace(s))
            return NVec3.Zero;
        string[] p = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 3) return NVec3.Zero;
        return new NVec3(ParseF(p[0]), ParseF(p[1]), ParseF(p[2]));
    }

    private static float ParseF(string s)
        => float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0f;

    /// <summary>The VFS vpaths a configured map name is probed at (bare → maps/, +.bsp) — mirrors GameDemo.</summary>
    private static System.Collections.Generic.IEnumerable<string> MapVPathCandidates(string mapPath)
    {
        string p = mapPath.Replace('\\', '/').Trim();
        bool hasBsp = p.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase);
        bool underMaps = p.StartsWith("maps/", StringComparison.OrdinalIgnoreCase);
        yield return p;
        if (!hasBsp) yield return p + ".bsp";
        if (!underMaps)
        {
            yield return "maps/" + p;
            if (!hasBsp) yield return "maps/" + p + ".bsp";
        }
    }

    /// <summary>Copy a cvar from <paramref name="from"/> to <paramref name="to"/> only when it holds a non-zero
    /// numeric value (so an unset limit leaves the destination's cfg-loaded default in place).</summary>
    private static void CopyCvarIfSet(XonoticGodot.Engine.Simulation.CvarService from, ICvarService to, string name)
    {
        if (from.Has(name) && from.GetFloat(name) > 0f)
            to.Set(name, from.GetString(name));
    }

    private static CollisionWorld BuildTestFloor()
    {
        var world = new CollisionWorld();
        world.AddBrush(Brush.FromBox(
            new NVec3(-4096f, -4096f, -64f),
            new NVec3(4096f, 4096f, 0f), SuperContents.Solid));
        world.BuildGrid();
        return world;
    }

    private void AddLight()
    {
        AddChild(new DirectionalLight3D { Name = "Sun", RotationDegrees = new GVec3(-50f, -30f, 0f), ShadowEnabled = true });
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = new ProceduralSkyMaterial() },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightEnergy = 0.6f,
        };
        AddChild(new WorldEnvironment { Name = "WorldEnvironment", Environment = env });
    }

    private static string BotName(int i)
    {
        string[] names = { "Eureka", "Rampage", "Hellfire", "Specter", "Razor", "Cipher", "Vortex", "Havoc" };
        return names[i % names.Length];
    }

    // =====================================================================================
    //  Address parsing
    // =====================================================================================

    /// <summary>Parse <c>host</c> or <c>host:port</c> into (host, port), defaulting the port. IPv6 in [..] is
    /// left to the transport; we only split on the LAST colon when it's followed by a port number.</summary>
    public static (string Host, int Port) ParseAddress(string address, int defaultPort)
    {
        string a = (address ?? "").Trim();
        if (a.Length == 0)
            return ("127.0.0.1", defaultPort);
        int colon = a.LastIndexOf(':');
        if (colon > 0 && colon < a.Length - 1
            && int.TryParse(a[(colon + 1)..], out int port) && port is > 0 and <= 65535)
            return (a[..colon], port);
        return (a, defaultPort);
    }

    /// <summary>A no-op prediction step used only if the carrier couldn't be spawned (no ambient world).</summary>
    private sealed class NullMovementStep : IMovementStep
    {
        public void Step(ref PredictedState state, in InputCommand cmd, in PlayerState vars) { }
    }
}
