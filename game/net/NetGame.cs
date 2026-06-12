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
    private System.Action<string>? _sharedCvarBridge;   // mirrors console/menu cvar writes into the server's store

    // --- live pieces ---
    private GameWorld? _serverWorld;            // listen server only
    private ServerNet? _server;                 // listen server only
    // S5 (sv_threaded, default OFF): the dedicated server-sim worker thread + the shared serialisation gate it
    // and the main-thread prediction span lock against. BOTH null unless sv_threaded was 1 at StartListenServer on
    // a non-headless host. When null, _Process drives _server.Tick directly on the main thread exactly as today —
    // no thread, no lock — so the default path is byte-for-byte unchanged.
    private ServerThread? _serverThread;        // listen server only, sv_threaded 1 only
    private object? _simGate;                    // the shared lock; non-null iff _serverThread is non-null
    private ClientNet? _client;
    private ClientWorld _render = null!;
    // The background parse/build queue (S1): the idle player-model warm AND the live on-demand player-model
    // path (perf §9.4 Wave 1 — first sight of N bots streams one model build per frame instead of all at once).
    private XonoticGodot.Game.Client.BackgroundAssetStreamer? _streamer;
    // Player-model names whose async resolve settled as NOT skeletal (non-IQM / no skeleton): the resolver
    // returns null for these so ClientWorld's MD3/static fall-through owns them — exactly the old synchronous
    // path's outcome. Per session; model files can't change mid-run.
    private readonly System.Collections.Generic.HashSet<string> _nonSkeletalPlayerModels =
        new(System.StringComparer.OrdinalIgnoreCase);
    // FORCED player-model names+skins whose async resolve settled as NOT a poseable skeleton (non-IQM forced model
    // → render via the cached LoadModel node, FORCEMODEL parity) OR a true VFS miss (LoadModel also null → fall
    // through to the entity's own model). Keyed "model#skin" because a forced model is name+skin-specific. Checking
    // this BEFORE re-streaming is what makes the RebuildEntityModel-driven re-attach terminate instead of looping.
    private readonly System.Collections.Generic.HashSet<string> _unresolvableForcedModels =
        new(System.StringComparer.OrdinalIgnoreCase);
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
    private XonoticGodot.Game.Client.WaypointSpriteLayer? _waypointLayer; // 3D in-world waypoint/objective markers
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
    private bool _readyComplete;                // _Ready finished — _Process can run its full body (before this, fields are half-built)
    private HandshakeStage _handshakeStage;     // last sub-stage announced to the LoadingScreen (so we only BeginStage on a transition)

    /// <summary>Sub-stages of the post-_Ready handshake/spawn phase, used by <see cref="_Process"/> to drive
    /// the loading bar to BeginStage on each transition (not every frame).</summary>
    private enum HandshakeStage { None, Connecting, WaitingForServer, Joining, Spawned }
    private MusicPlayer? _musicPlayer;          // client-side map music (cdtrack / target_music / trigger_music)

    // The SHARED first-person view subsystem (zoom + FOV + chase/death cam + eye-contents) — the SAME component
    // PlayerController uses, ported once from qcsrc/client/view.qc (T34 SEAM). Fed a ClientNet-predicted
    // ViewState each frame; we read back EyeContents / ZoomFraction / SensitivityScale / ChaseActive.
    private readonly Client.FirstPersonView _view = new();

    // The zoom scope reticle overlay (QC crosshair.qc DrawReticle), fed each frame in _Process from the networked
    // active weapon (ClientNet.ActiveWeaponId) + the zoom/button state — the net-path twin of PlayerController's.
    private Client.ReticleOverlay _reticle = null!;

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
    private bool _attackHeld;                     // per-frame previous +attack state (local fire-feedback edge)
    private bool _attack2Held;                    // per-frame previous +attack2 state
    // Sub-tick fire latch: set on the per-frame press edge (UpdateLocalFireFeedback), OR'd into the next sampled
    // InputCommand and cleared, so a tap shorter than one 1/72 s input tick still reaches the server.
    private bool _attackLatch;
    private bool _attack2Latch;

    // Local fire prediction (cl_predictfire, default on): a client-side refire clock that fires the view-model
    // muzzle flash + a local fire SOUND on every shot the moment the player fires (not just the first), so
    // sustained fire feels instant; the local player's own networked fire-sound/muzzle-flash is then suppressed
    // so it isn't doubled. _fireClock is a monotonic frame accumulator; _nextFireTime is the next predicted shot.
    private bool _predictFire = true;
    private float _fireClock;
    private bool _firePredictActive;
    private float _nextFireTime;
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

    // Per-frame (variable-dt) input mode — cl_movement_perframe (DP-style: one command per rendered frame stamped
    // with the real frame dt, the server drains all pending commands that tick). DEFAULT 1 (on, user-approved per
    // PERFORMANCE_REPORT.md B2); `set cl_movement_perframe 0` = the legacy fixed 1/72 s cadence above (kept for
    // parity testing). Read live from the shared cvar store each frame so it A/B-toggles in-session.
    private bool _perFrameInput;
    // The DeltaTime stamped onto the next sampled InputCommand: TicRate in legacy mode, the clamped real frame dt
    // in per-frame mode. Bounds mirror DP's per-move msec clamp (kills 0-dt paused frames + hitch spikes); the
    // server re-clamps each command's dt since the client is untrusted.
    private float _inputDeltaTime = XonoticGodot.Engine.Simulation.SimulationLoop.TicRate;
    private const float MinInputDt = 0.0005f;
    private const float MaxInputDt = 0.05f;

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

    public override async void _Ready()
    {
        // The asset loader (models/sounds/maps) over the shared VFS, when the menu mounted one.
        if (_vfs is not null)
            _assets = new AssetLoader(_vfs);

        // The load sequence runs as a coroutine: each BeginStage sets the bar's target and per-stage expected
        // time (the LoadingScreen animates asymptotically from where it is now toward that target), then we
        // yield one frame so the bar can repaint with the new status text BEFORE the synchronous work begins.
        // After the work, we yield again so the bar can catch up to its now-elapsed-time-based position before
        // the next stage takes over. Net effect: the bar advances visibly through every sub-step and slows down
        // gracefully when a stage runs longer than expected, instead of one "Loading…" → snap-to-1 jump.
        // (Async void so a TeardownGame mid-load just leaves this coroutine to fall off the tree-check below.)

        if (_isListenServer)
        {
            LoadingScreen?.BeginStage("Loading map…", 0.30f, 4.0f);
            await YieldForLoadingFrame();
            if (!IsInsideTree()) return;

            StartListenServer();

            LoadingScreen?.BeginStage("Booting world…", 0.42f, 0.5f);
            await YieldForLoadingFrame();
            if (!IsInsideTree()) return;
        }
        else
        {
            LoadingScreen?.BeginStage("Preparing client…", 0.30f, 0.5f);
            await YieldForLoadingFrame();
            if (!IsInsideTree()) return;

            BootClientFacade();

            LoadingScreen?.BeginStage("Resolving server…", 0.42f, 0.5f);
            await YieldForLoadingFrame();
            if (!IsInsideTree()) return;
        }

        // The client connects to the chosen endpoint (a listen server is on 127.0.0.1; a pure client on _host).
        StartClient();

        LoadingScreen?.BeginStage("Setting up renderer…", 0.55f, 1.0f);
        await YieldForLoadingFrame();
        if (!IsInsideTree()) return;

        // Render layer + the net→render bridge + a basic HUD/camera. Built regardless of mode so a connect that
        // hasn't completed yet still has somewhere to draw once snapshots flow.
        SetupRender();
        SetupCameraAndHud();

        LoadingScreen?.BeginStage("Starting music…", 0.62f, 0.3f);
        await YieldForLoadingFrame();
        if (!IsInsideTree()) return;

        // Map music: read the cdtrack from the mapinfo file (VFS) and wire a MusicPlayer into the render tree.
        SetupMusic();

        LoadingScreen?.BeginStage("Precaching weapon models…", 0.82f, 3.0f);
        await YieldForLoadingFrame();
        if (!IsInsideTree()) return;

        // Warm every weapon's view model + hand rig into the asset caches now, under the loading screen, so the
        // first switch/pickup of a weapon in combat doesn't hitch reading+decoding the model and its textures on
        // the main thread (DP precaches weapon models at map load). Runs after _assets is built above.
        // The async variant yields every few weapons so the bar visibly ticks through this stage.
        await PrecacheWeaponModelsAsync();
        if (!IsInsideTree()) return;

        // Warm the combat-sound decode cache + common player models (A3) so the first shot/explosion and the
        // first player render don't stall on an OGG decode / skeletal-model texture build mid-combat.
        LoadingScreen?.BeginStage("Precaching sounds…", 0.87f, 1.5f);
        await YieldForLoadingFrame();
        if (!IsInsideTree()) return;
        await PrecacheCombatSoundsAndModelsAsync();
        if (!IsInsideTree()) return;

        // S3: once the loading screen drops, a low-priority idle warmer mops up the long tail (announcer/pickup
        // voices, the other stock player models) on a ~1.5 ms/frame budget so the WHOLE asset set goes hot within
        // the first minute of play, without ever spiking a frame.
        StartIdleWarmup();

        LoadingScreen?.BeginStage("Connecting…", 0.90f, 0.5f);

        // FPS mouse-look: capture the cursor (the Shell releases/recaptures it around the in-game menu).
        Input.MouseMode = Input.MouseModeEnum.Captured;

        GD.Print(_isListenServer
            ? $"[NetGame] listen server on 127.0.0.1:{_port} (map '{_map}', {_gametype}, {_botCount} bots) — self-connecting."
            : $"[NetGame] connecting to {_host}:{_port}.");

        // _Process can safely run its full body now (camera/HUD/render/client all built above).
        _readyComplete = true;
    }

    /// <summary>Yield one process_frame so the LoadingScreen's _Process can repaint with whatever stage
    /// is currently in flight. No-op (synchronously completes) if the node is no longer in the tree —
    /// guards against ToSignal throwing on a torn-down NetGame mid-load.</summary>
    private async System.Threading.Tasks.Task YieldForLoadingFrame()
    {
        if (!IsInsideTree()) return;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
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
        // B3: on the interactive client-hosted path (a windowed listen server), clamp the per-render-frame
        // catch-up so a hitch isn't followed by a frame running 16× the sim — that second spike would miss
        // another vblank. The small backlog drains over the next few frames instead. A headless dedicated host
        // (no vblank) keeps the full cap and catches up fast.
        if (DisplayServer.GetName() != "headless")
            _serverWorld.Simulation.MaxTicksPerFrame = 4;
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

        // S5: register sv_threaded (default 0) on the server store and resolve the host-side threading decision
        // ONCE, here at boot (changing it mid-match is out of scope — a rehost/map-change re-reads it). The actual
        // worker + gate are created further below, after ServerNet starts and the command sinks are wired. A
        // console/menu override in the SHARED store wins over the world default (the menu writes there). Threading
        // is GATED to non-headless hosts: a headless dedicated server already keeps the full catch-up cap and has
        // no render frame to unblock, so it stays on the stock single-threaded drive even with sv_threaded 1.
        _serverWorld.Services.Cvars.Register("sv_threaded", "0");
        bool wantThreaded =
            (_sharedCvars is not null && _sharedCvars.Has("sv_threaded")
                ? _sharedCvars.GetFloat("sv_threaded")
                : _serverWorld.Services.Cvars.GetFloat("sv_threaded")) != 0f
            && DisplayServer.GetName() != "headless";

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

        // Bridge runtime cvar changes from the shared (console/menu) store into the listen server's PRIVATE store.
        // The in-game console writes `set`/`seta`/bare assignments to the shared MenuState.Cvars, but GameWorld
        // keeps its own store (loaded from the cfg tree at Boot), so without this a console `set g_balance_…` — or
        // any server-cvar tweak — never reaches the live match. Mirror only cvars the server already knows (Has)
        // so we don't pollute its store with client-only cvars; the server's own Changed event then re-derives any
        // cached state (GameWorld re-runs Weapons.ConfigureAll on a g_balance_* change). One-way, idempotent
        // (Set skips no-op writes), so the campaign progress mirror above can't feed back into a loop.
        if (_sharedCvars is not null)
        {
            // INITIAL BACKFILL (fixes "console-set server cvar lost on map change / new server"): a new map boots a
            // fresh GameWorld whose private store reloads from the cfg tree, so a sv_*/g_* the user changed in the
            // console (shared store) reverts to its cfg default even though the console still shows the user's value
            // (the two-store split). The bridge below only forwards FUTURE Changed events, so re-apply the user's
            // CURRENT overrides now. Skip in campaign mode (the level file authors the cvars, like the limits copy
            // above). BackfillModifiedCvars only touches cvars the user actually changed AND the server Has, so it
            // never clobbers a map/ruleset value the user didn't set.
            if (string.IsNullOrEmpty(_campaignName))
                XonoticGodot.Engine.Simulation.CvarService.BackfillModified(
                    _sharedCvars, _serverWorld.Services.CvarsImpl, BootAuthoredCvars);

            _sharedCvarBridge = name =>
            {
                var server = _serverWorld?.Services.CvarsImpl;   // re-read the field so a map change is followed
                if (server is not null && server.Has(name) && _sharedCvars is not null)
                    server.Set(name, _sharedCvars.GetString(name));
            };
            _sharedCvars.Changed += _sharedCvarBridge;
        }

        // --- bots so a solo host has opponents to see/play (QC bot_number / minplayers fill). ---
        // [T39] Seed the SERVER store's bot_number/skill instead of direct ClientConnect calls: the live
        // BotPopulation fixcount (GameWorld.OnStartFrame → Bots.ServerFrame, QC bot_fixcount) fills one bot per
        // frame from time 2.5 — and, critically, a faithful fixcount REMOVES bots above the bot_number target
        // at its first recount, so host-added bots must raise the floor, not bypass it (QC bot.qc:682-683).
        if (_botCount > 0)
        {
            _serverWorld.Services.Cvars.Set("bot_number",
                _botCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _serverWorld.Services.Cvars.Set("skill",
                _botSkill.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        // any bot leaving (fixcount trim / removebots / intermission teardown) must clear ServerNet's
        // per-player id/antilag maps, exactly like the old explicit remove handler did.
        _serverWorld.Bots.BotRemoved += p => _server?.ForgetPlayer(p);
        _serverWorld.Bots.MaxClients = 16; // QC maxclients (mirrors ServerNet.Start's maxClients below)

        ServerNet? server = ServerNet.Start(_serverWorld, _port, maxClients: 16, serverName: _serverName);
        if (server is null)
        {
            GD.PrintErr($"[NetGame] could not start the listen server on UDP {_port} (port in use?).");
            return;
        }
        _server = server;
        // Answer server-browser getinfo probes so this host shows up in the LAN list (no master heartbeat —
        // the port's transport is ENet, so registering with the public DP masters would only mislead DP clients).
        server.EnableLanDiscovery(_port);

        // --- wire the server-command host sinks (QC the say bus / bprint + bot_cmd add/remove). Without these,
        //     console/clc_stringcmd `say`/`bot_add`/`setbots` no-op; with them, chat reaches every client's console
        //     and bots are added/removed live. Bots auto-join + spawn on connect (ClientManager.ClientConnect) and
        //     the per-tick snapshot delta networks/despawns them (NetIdFor is lazy), so no extra net plumbing is
        //     needed beyond cleaning ServerNet's per-player maps on removal (ForgetPlayer). ---
        Commands cmd = _serverWorld.Commands;

        if (!wantThreaded)
        {
            // STOCK single-threaded sinks — byte-for-byte the pre-S5 wiring (exact synchronous return values, which
            // the console feedback + the removebots loop depend on). This branch is what the default path uses.

            // player chat (say) → broadcast to everyone incl. the sender; server announcements (vote/ban/team) → same.
            cmd.ChatHandler = (caller, msg, teamOnly) =>
                _server?.BroadcastPrint($"{(teamOnly ? "(team) " : "")}{caller?.NetName ?? "server"}^7: {msg}");
            cmd.ChatBroadcast = msg => _server?.BroadcastPrint(msg);

            // bot_add / setbots: spawn a brained bot through the live population (raises bot_number so the faithful
            // fixcount keeps it); the snapshot loop networks it next tick. [T39]
            cmd.AddBotHandler = (name, skill) => _serverWorld?.Bots.AddBot(name, skill) is not null;

            // bot_remove / removebots: drop the newest matching bot via the population (lowers bot_number so
            // fixcount doesn't re-add); the disconnect chain fires Bots.BotRemoved → ForgetPlayer clears ServerNet's
            // id/antilag maps, and the next snapshot delta despawns it on clients. [T39]
            cmd.RemoveBotHandler = name => _serverWorld?.Bots.RemoveBot(name) ?? false;

            // map / gotomap / nextmap / map-vote / rotation / samelevel all funnel here (QC changelevel): record the
            // target; the deferred emit in _Process reboots the listen server on it (preserving gametype + bots).
            cmd.ChangeLevelHandler = RequestMapChange;
        }
        else
        {
            // S5 THREADED sinks: these fire on the MAIN thread (console/menu input) but touch _server/_serverWorld,
            // which the worker thread owns — route each through ServerNet.RunOnSimThread so it executes on the sim
            // thread under the gate at the next tick top. The bot handlers can no longer return a synchronous
            // success (the work runs later on the worker), so they report true and the result surfaces in the next
            // snapshot — faithful to how a remote add/remove would behave. This branch is reached ONLY when
            // sv_threaded 1 on a non-headless host, so it never affects the default path.
            ServerNet s = _server!; // non-null here (the null-check above returned early)

            cmd.ChatHandler = (caller, msg, teamOnly) =>
                s.RunOnSimThread(() => _server?.BroadcastPrint($"{(teamOnly ? "(team) " : "")}{caller?.NetName ?? "server"}^7: {msg}"));
            cmd.ChatBroadcast = msg => s.RunOnSimThread(() => _server?.BroadcastPrint(msg));
            cmd.AddBotHandler = (name, skill) => { s.RunOnSimThread(() => _serverWorld?.Bots.AddBot(name, skill)); return true; };
            cmd.RemoveBotHandler = name => { s.RunOnSimThread(() => _serverWorld?.Bots.RemoveBot(name)); return true; };
            cmd.ChangeLevelHandler = map => s.RunOnSimThread(() => RequestMapChange(map));
        }

        // S5: spin up the dedicated server-sim worker now that ServerNet + the world + the sinks are all wired.
        // Install the shared gate on ServerNet (its Tick locks it) and hand the SAME object to the main thread
        // (_simGate) so _Process serialises its prediction span against the worker. Start LAST so no tick runs
        // before the sinks/gate are in place. Headless hosts never reach here (wantThreaded gated it off).
        if (wantThreaded)
        {
            _simGate = new object();
            _server!.SimGate = _simGate;
            // The music player's _Process scans the live server entity list (wired in SetupMusic, called earlier)
            // on the main thread — hand it the SAME gate so that scan can't race the worker's spawn/relink. Stays
            // null on the non-threaded default path, so its scan remains lock-free.
            if (_musicPlayer is not null) _musicPlayer.SimGate = _simGate;
            _serverThread = new ServerThread(
                _serverWorld!, _server!,
                static () => XonoticGodot.Engine.Simulation.SimulationLoop.TicRate);
            _serverThread.Start();
            GD.Print("[NetGame] sv_threaded 1 — server simulation running on a dedicated worker thread (XG-ServerSim).");
        }
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
        // S5: SpawnCarrier runs AFTER StartListenServer started the worker (boot order), so Api.Entities.Spawn()
        // mutates the SAME entity table the worker ticks — take the gate around the spawn when threaded so it can't
        // race a concurrent server-tick spawn/relink. No-op (no lock) when not threaded. The field-sets below act
        // on the freshly-returned edict only; doing them under the gate too is harmless and keeps it simple.
        object? simGate = _simGate;
        bool taken = false;
        try
        {
            if (simGate is not null)
                System.Threading.Monitor.Enter(simGate, ref taken);
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
        finally
        {
            if (taken)
                System.Threading.Monitor.Exit(simGate!);
        }
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
            // cl_forceplayermodels swap). Streams the skeletal forced model through the same async placeholder path
            // as the live player resolve (a forced-model snapshot never re-introduces the sync build stall); a
            // non-IQM forced model still wins via the cached LoadModel node (FORCEMODEL parity), and only a genuine
            // VFS miss returns null (QC fexists-miss → the entity keeps its own model). Receives the Entity so the
            // async delivery can validate staleness + re-attach via RebuildEntityModel.
            _render.ForcedModelResolver = BuildForcedPlayerModel;
        }
        AddChild(_render);

        // The background asset streamer (S1) — created unconditionally (not just for the idle warm) because the
        // LIVE player-model resolve streams through it: parse on the thread pool, Godot build under the per-frame
        // budget. One node per NetGame; freed with the scene.
        _streamer = new XonoticGodot.Game.Client.BackgroundAssetStreamer { Name = "AssetStreamer" };
        AddChild(_streamer);

        // Networked projectiles draw their REAL model (rocket.md3 with its additive RocketThrust flame cone,
        // grenademodel.md3) through the same VFS loader the world/weapon models use. Set after AddChild since
        // ClientWorld._Ready (which built _render.Projectiles) ran synchronously on it.
        if (_assets is not null)
            _render.Projectiles.ModelFactory = m => _assets.LoadModel(m);

        // Pre-warm the effect catalog + particlefont atlas now (map-load), so the FIRST weapon shot doesn't hitch
        // parsing effectinfo.txt + decoding the atlas on its render frame (DP precaches these at client init,
        // cl_particles.c). The Assets setter above already wired the texture/text loaders via WireEffectAssets,
        // and _render.Effects is live (ClientWorld._Ready ran synchronously on AddChild). Idempotent + invisible.
        _render.Effects.Warmup();
        // Likewise pre-build the shared per-type projectile-trail Resources so the first rocket/plasma/grenade
        // doesn't construct its trail material on its render frame (see ProjectileRenderer.WarmupTrails).
        _render.Projectiles.WarmupTrails();
        // A2: render one hidden instance of every effect/projectile material family in a tiny offscreen viewport
        // so the GPU compiles their shader pipelines NOW (during load) — the first real explosion/rocket/gib in
        // play then hits a warm pipeline instead of stalling the frame. Self-frees after a few frames.
        XonoticGodot.Game.Client.GpuWarmPass.Run(_render, _render.Effects, _render.Projectiles);

        // CSQC appearance context (FORCEMODEL/FORCECOLORS need the local player + gametype): read live each frame.
        _render.AppearanceProvider = BuildAppearanceContext;

        // Render the world geometry on the listen server: the client draws the worldmodel it loaded locally
        // (DP VF_DRAWWORLD=1 + renderscene(); the server ships no geometry). Reuses the SAME BSP + gametype filter
        // the collision was built from in StartListenServer — identical to GameDemo.cs:181's MapLoader.BuildMap.
        // A pure --connect client has no BSP yet (see the map-name handshake follow-up), so this is gated on _bsp.
        if (_bsp is not null && _assets?.Assets is not null)
            // Pass the loaded map name so external lm_NNNN lightmaps resolve (stock maps have no internal lump).
            AddChild(MapLoader.BuildMap(_bsp, _assets.Assets, _map, _droppedSubmodels));

        // Client-side collision world for the particle systems: decal splats conform to the real brush faces
        // (DP R_DecalSystem — without it marks fall back to flat quads), and the chunked-SDF service builds
        // from the same world. One build per map load (~100 ms, hidden by the load screen; the server world's
        // collision is a separate instance inside GameWorld with no accessor — rebuilding keeps the seam clean).
        if (_bsp is not null && _assets?.Assets is not null)
        {
            CollisionWorld clientCollision = MapLoader.BuildCollision(_bsp, _assets.Assets);
            _render.Effects.SetCollisionWorld(clientCollision);
            // Splats clip against the RENDER triangles (DP's actual target) — marks roll over visible
            // trim/patch edges the collision brushes don't model.
            _render.Effects.SetDecalGeometry(_bsp);

            // Chunked-SDF collision field for modern particles (planning/particles-dual-system.md §A). Built
            // only in a modern-collision mode (cl_particles_modern 1/2) — mode 0 (the faithful default) needs
            // no SDF, so the default load pays nothing beyond the shared collision build above. Generation is
            // further gated/async inside the service (cl_particles_sdf_generate).
            if (_vfs is not null &&
                XonoticGodot.Game.Menu.MenuState.Cvars.GetFloat(XonoticGodot.Engine.Particles.ParticleCvars.Modern) != 0f)
            {
                string bspVpath = $"maps/{_map}.bsp";
                byte[]? bspBytes = _vfs.Exists(bspVpath) ? _vfs.ReadBytes(bspVpath) : null;
                if (bspBytes is not null)
                    _render.Effects.BuildSdfForMap(_map, bspBytes, clientCollision, _vfs);
            }
        }

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
        {
            XonoticGodot.Game.Hud.TextureCache.VfsResolver = _assets.LoadTexture;
            // Xolonium HUD font (the menu skin font), so HUD text matches Xonotic instead of Godot's fallback.
            XonoticGodot.Game.Hud.HudPanel.HudFont = _assets.GetFont("xolonium");
            XonoticGodot.Game.Hud.HudSkin.BoldFont = _assets.GetFont("xolonium-bold");
        }

        // The full CSQC HUD panel set on the net path (T34): weapon bar / ammo / kill-feed / centerprint / timer,
        // the SAME Hud GameDemo uses. It is its OWN CanvasLayer (Hud : CanvasLayer), added directly to the node
        // like GameDemo does — on a layer BELOW the lightweight hudLayer so the crosshair/radar/scoreboard draw on
        // top. Its gameplay panels read a local Player; on a listen server that is the local server Player
        // (resolved each frame in _Process as the spawn lands). Its OWN Scoreboard panel is left unfed — T9's
        // standalone _scoreboard owns the networked scoreboard, so we don't double it up.
        _fullHud = new XonoticGodot.Game.Hud.Hud { Name = "FullHud", Layer = 4 };
        AddChild(_fullHud);
        // The full HUD's skinned Crosshair + HealthArmor panels now render on the play path (goal 1). On a listen
        // server (--host) UpdateFullHudPlayer feeds them the local server Player so they show the skinned art and
        // suppresses NetHud's duplicate crosshair/health (NetHud.SuppressCrosshairAndHealth). On a pure --connect
        // client (no local Player) UpdateFullHudPlayer hides the skinned Crosshair (it would otherwise draw a
        // reticle even without a Player) and leaves NetHud un-suppressed as the fallback — so there's never a DOUBLE
        // crosshair/health. HealthArmorPanel self-blanks with no Player (reads its resources), so it's left visible.
        // Crosshair visibility is driven by UpdateFullHudPlayer (shown only when a local Player resolves). Start it
        // HIDDEN so the pure-client baseline (no Player ever resolves → UpdateFullHudPlayer's null==null early-out
        // never runs its body) leaves NetHud's crosshair as the sole reticle. The listen-server null→Player edge
        // then shows it.
        _fullHud.Crosshair.Visible = false;
        //
        // The full HUD's Timer panel IS now fed: the server pushes the global match clock (NetControl.MatchState →
        // ClientNet.HasMatchState/MatchStartTime/MatchTimeLimit/MatchWarmup), drained into the panel each frame by
        // UpdateMatchClock(). It self-blanks (TimerPanel draws nothing until fed) until the first MatchState lands.

        // Ping readout (next to the FPS counter): feed it the live ENet round-trip estimate to the server. The
        // panel self-gates on cl_showping/showping and stays hidden until the cvar is set; the provider returns
        // -1 before the link is up (the panel then draws nothing). On a loopback listen server this reads ~0.
        _fullHud.Ping.PingProvider = () => _client?.PingMs ?? -1;

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

            // Chat (QC HUD panel #12): the port has no dedicated chat net channel — server chat is broadcast via
            // ServerNet.BroadcastPrint and arrives on the SAME PrintReceived stream the minigame session-list
            // parser reads. Forward every print line to the ChatPanel's scrollback (it self-blanks until fed); the
            // panel honours con_chattime/con_chatsize and fades lines out. (Console output is also relayed to the
            // in-game console via ConsolePrint elsewhere, so chat shows in both — matching DP, where chat lands in
            // the console AND the chat HUD area.)
            _client.PrintReceived += OnServerPrintForChat;
        }

        // QuickMenu (QC HUD panel #QUICKMENU): point its command sink at the shared interpreter (RunCommand, wired
        // by Shell) — the analogue of QC localcmd, the SAME single channel a bound key uses (RunBoundCommand). A
        // picked toggle row (`toggle cl_hitsound`) hits the shared cvar store; a say/say_team/vcall row is forwarded
        // to the server by the interpreter's own forward path, exactly like typing it in the console. The panel is
        // toggled by the `quickmenu` bind (intercepted in RunBoundCommand → Toggle()); it self-blanks until opened.
        // It grabs keyboard focus on open so the 1-9/0/Esc number-key navigation works; mouse-click navigation
        // needs the cursor made visible (the gameplay path keeps it captured) — see the report's goal-8 note.
        if (_fullHud.GetPanel<XonoticGodot.Game.Hud.QuickMenuPanel>() is { } quick)
            quick.CommandSink = line => RunCommand?.Invoke(line);

        _radar = new RadarPanel
        {
            Name = "Radar",
            Net = _client,
            Size = new Vector2(256, 256),
            Position = new Vector2(24, 24),
        };
        // Feed the real minimap: the map name (resolves gfx/<map>_mini.jpg inside the map pk3 via the VFS) + the
        // map's world XY bounds (QC mi_min/mi_max = the BSP worldspawn model mins/maxs) so the image + blips align.
        _radar.MapName = _map;
        if (_bsp is { Models.Length: > 0 })
        {
            System.Numerics.Vector3 mins = _bsp.Models[0].Mins, maxs = _bsp.Models[0].Maxs;
            _radar.MapMinXY = new Vector2(mins.X, mins.Y);
            _radar.MapMaxXY = new Vector2(maxs.X, maxs.Y);
        }
        hudLayer.AddChild(_radar);

        // 3D in-world waypoint sprites (QC Draw_WaypointSprite): floating objective/ping markers projected through
        // the first-person camera, on their OWN layer BELOW the HUD panels (Layer 3 < the HUD's 4/5) so the
        // crosshair + panels draw over them. Fed the live waypoint list straight from ClientNet.
        var waypointLayer = new CanvasLayer { Name = "Waypoints", Layer = 3 };
        AddChild(waypointLayer);
        _waypointLayer = new XonoticGodot.Game.Client.WaypointSpriteLayer { Name = "WaypointSprites", Camera = _camera };
        _waypointLayer.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        if (_client is not null)
            _waypointLayer.Source = () => _client.Waypoints;
        waypointLayer.AddChild(_waypointLayer);

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

        // Screen-space vignette (cl_vignette_*): a soft darkened gradient framing the view edges, on its own
        // CanvasLayer above the world/ViewEffects tint but below the HUD. Self-contained — it registers its own
        // cvars, reads them live, and self-drives; no per-frame feeding needed here.
        AddChild(new XonoticGodot.Game.Client.VignetteOverlay { Name = "Vignette" });

        // Zoom scope reticle (QC crosshair.qc DrawReticle): a CanvasLayer below the HUD, fed each frame in
        // _Process from the networked active weapon + zoom state (see the zoom block there).
        _reticle = new XonoticGodot.Game.Client.ReticleOverlay { Name = "Reticle" };
        AddChild(_reticle);

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
        // Base-faithful selection (CL_WeaponEntity_SetModel): full-model DPM rigs (rl/gl/crylink/electro/hagar/
        // ok_*) render the h_ HAND RIG itself; invisible-hand IQM rigs render the v_ model attached to the rig's
        // "weapon" bone. BuildViewModelEquip is the single source of truth shared with the GameDemo equip path.
        XonoticGodot.Common.Gameplay.Weapon w = XonoticGodot.Common.Gameplay.Weapons.ById(id);
        string vModel = WeaponVModelPath(w);
        GameDemo.ViewModelEquip eq = GameDemo.BuildViewModelEquip(_assets, vModel);
        _viewModel.SetWeaponModel(eq.Model, MuzzleEffectFor(w), "tag_shot", eq.Attach);
        _viewModel.Visible = true;
        // Raise the new gun into view instead of popping the model in (Xonotic viewmodel_draw raise; pairs with
        // the keypress holster in RunBoundCommand). Confirmed switch → cancels any pending holster auto-recovery.
        _viewModel.PlayRaise();
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
    private async System.Threading.Tasks.Task PrecacheWeaponModelsAsync()
    {
        if (_assets is null || _weaponsPrecached)
            return;
        _weaponsPrecached = true;

        // Smart-precache: only warm the v_ model + hand rig for weapons we EXPECT to see this match (the
        // map's weapon_<netname> spawns, plus whatever the active gametype/mutators force). Anything else
        // is left to AssetLoader's lazy cache — first encounter takes a one-frame hitch and then it's
        // cached. The cost saving is real: a dm map averages ~9 distinct weapons; warming 24 is ~2.5× more
        // mesh/material/texture work than this match will ever use. Mutators that REPLACE the loadout
        // (instagib/overkill) shrink this further. Always register muzzle offsets, though — that uses a
        // header-only parser (LoadMuzzleModel), is cheap, and the in-process listen server needs them for
        // any weapon a player might fire (including the lazy ones).
        System.Collections.Generic.HashSet<string> expected = ComputeExpectedWeapons();

        // A3: warm EVERY weapon's v_ model by default (cl_precache_all_weapons 1), not just this match's
        // expected loadout — the extra load cost is hidden by the loading screen and it removes the 30–300 ms
        // stall the first time the player switches to / picks up / sees an unanticipated weapon. The smart
        // expected-only path is still available (set cl_precache_all_weapons 0) for memory-constrained machines.
        bool warmAll = (_sharedCvars?.GetFloat("cl_precache_all_weapons") ?? 1f) != 0f;

        // Spread the per-weapon load across frames so the loading bar visibly ticks instead of freezing on
        // this one stage. Yield every few weapons (not every one — the yield costs a frame each).
        const int YieldEveryNWeapons = 4;

        int warmed = 0, skipped = 0, muzzles = 0;
        int i = 0;
        foreach (XonoticGodot.Common.Gameplay.Weapon w in XonoticGodot.Common.Gameplay.Weapons.All)
        {
            string vModel = WeaponVModelPath(w);
            if (string.IsNullOrEmpty(vModel))
                continue;

            // Per-weapon shot-origin (QC CL_WeaponEntity_SetModel: movedir, all.qc:367-424). Computed via the
            // header-only MuzzleTag parser — cheap enough to do for EVERY weapon so server-side projectile
            // spawns stay correct even for weapons we didn't warm. Base prefers the v_ model's own shot tag
            // (transformed through the h_ rig's weapon-attach bone), falling back to the h_ rig's shot tag.
            string hModel = vModel.Replace("/v_", "/h_").Replace(".md3", ".iqm");
            System.Numerics.Vector3? shot = _assets.LoadMuzzleOffset(vModel, hModel != vModel ? hModel : null);
            if (shot is { } so)
            {
                XonoticGodot.Common.Gameplay.WeaponFiring.RegisterMuzzleOffset(w.RegistryId, so);
                muzzles++;
                // `set developer 1` to confirm the per-weapon shot origin (QC movedir) actually applied: forward
                // (x), +left (y), up (z) from the eye. A weapon that fires from screen-CENTER would show ~(0,0,0)
                // or be ABSENT here (fell back to the generic offset). Devastator should read ~(40.9,-9,-17).
                XonoticGodot.Common.Diagnostics.Log.Trace(
                    $"[muzzle] {w.NetName}: movedir=({so.X:0.0},{so.Y:0.0},{so.Z:0.0}) fwd/left/up from eye");
            }

            // The heavy bit: full mesh + material/texture build of the v_ model, plus the hand rig. Warmed for
            // every weapon by default (warmAll); the smart path restricts it to this match's expected loadout.
            // An unanticipated pickup under the smart path just lazy-loads (one-frame hitch, then cached).
            if (warmAll || expected.Contains(w.NetName))
            {
                // Build once to fill the parse + material/texture caches, then discard the orphan node
                // (never added to the tree → free it so it doesn't leak). A miss just caches the failure.
                _assets.LoadModel(vModel)?.QueueFree();
                // The hand rig is loaded by WeaponAttachTransform on each switch; warm it too (it builds +
                // frees its own throwaway node internally and caches the attach transform's model).
                GameDemo.WeaponAttachTransform(_assets, vModel);
                warmed++;
            }
            else
            {
                skipped++;
            }

            if (++i % YieldEveryNWeapons == 0)
            {
                await YieldForLoadingFrame();
                if (!IsInsideTree()) return;
            }
        }
        GD.Print($"[NetGame] precached {warmed} weapon models, skipped {skipped} (lazy), {muzzles} muzzle tags.");
    }

    /// <summary>
    /// Warm the combat-sound decode cache + the common player models at map load (A3) so the first weapon fire /
    /// impact / explosion doesn't stall the frame decoding its OGG (the report's 5–50 ms first-play cost), and
    /// the first sight of a player doesn't stall building its skeletal IQM + textures (20–150 ms). Precaches
    /// every registered sound under <c>sound/weapons/</c> (the per-weapon fire/impact/reload lists) and the local
    /// + stock-default player models. Everything else (announcer voices, item-pickup cues, other players' model
    /// picks) stays lazy — the idle-warmup queue (S3) mops up the long tail. Yields so the loading bar ticks.
    /// </summary>
    private async System.Threading.Tasks.Task PrecacheCombatSoundsAndModelsAsync()
    {
        if (_assets is null)
            return;

        int sounds = 0, i = 0;
        foreach (XonoticGodot.Common.Gameplay.GameSound s in XonoticGodot.Common.Gameplay.Sounds.All)
        {
            // Combat sounds live under sound/weapons/ (rocket_fire, rocket_impact, …) — the report's per-weapon
            // fire/impact lists. LoadSound fills _soundCache (and caches misses, so no re-probe on real play).
            if (string.IsNullOrEmpty(s.Sample)
                || !s.Sample.StartsWith("weapons/", System.StringComparison.OrdinalIgnoreCase))
                continue;
            _assets.LoadSound(s.Sample);
            sounds++;
            if (++i % 16 == 0)
            {
                await YieldForLoadingFrame();
                if (!IsInsideTree()) return;
            }
        }

        // Player models: warm the local player's chosen model (_cl_playermodel) + the stock default (erebus) so
        // the first player render doesn't stall building the skeletal IQM's textures/materials (cached in
        // _assets). The throwaway build node is freed — only the material/texture caches are the goal. Other
        // players' picks stay lazy; precaching each connected client's model at join is a follow-up.
        var models = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        {
            "models/player/erebus.iqm",
        };
        string local = _sharedCvars?.GetString("_cl_playermodel") ?? string.Empty;
        if (!string.IsNullOrEmpty(local))
            models.Add(local);
        int modelsWarmed = 0;
        foreach (string m in models)
        {
            AssetLoader.SkeletalModelParts? parts = _assets.LoadSkeletalModel(m, 0);
            if (parts is not null)
            {
                parts.Root.QueueFree(); // warm the texture/material caches; discard the throwaway build node
                modelsWarmed++;
            }
        }
        GD.Print($"[NetGame] precached {sounds} combat sounds, {modelsWarmed} player models.");
    }

    /// <summary>The stock player models the idle warmer warms after load (the local + erebus default are already
    /// eagerly precached by A3). Warming these in the background means the first sight of another player's chosen
    /// model doesn't stall building its skeletal IQM + textures.</summary>
    private static readonly string[] IdleWarmPlayerModels =
    {
        "models/player/megaerebus.iqm", "models/player/nyx.iqm",
        "models/player/pyria.iqm", "models/player/seraphina.iqm", "models/player/umbra.iqm",
    };

    /// <summary>Spin up the idle-time asset warmer (S3): queue the long tail of sounds + the other stock player
    /// models for background warming on a small per-frame budget, so the whole asset set is hot within the first
    /// minute of play. The loaders cache, so anything the eager precache already warmed is a cheap no-op here.</summary>
    private void StartIdleWarmup()
    {
        if (_assets is null)
            return;
        // `set cl_idle_warmup 0` disables the background warm (A/B switch — the concurrent player-model parse was
        // implicated in an early gen2 GC stall). Unset/unregistered → on (only an explicit "0" disables).
        if (_sharedCvars is not null && _sharedCvars.GetString("cl_idle_warmup") == "0")
            return;
        var warmer = new XonoticGodot.Game.Client.IdleWarmer { Name = "IdleWarmer" };
        AddChild(warmer);

        // Every registered sound (announcer/pickup/voice + the already-warm combat samples). LoadSound caches.
        foreach (XonoticGodot.Common.Gameplay.GameSound s in XonoticGodot.Common.Gameplay.Sounds.All)
        {
            string sample = s.Sample;
            if (!string.IsNullOrEmpty(sample))
                warmer.Enqueue(() => _assets.LoadSound(sample));
        }

        // The other stock player models — streamed (S1): the IQM parse + sidecar reads run OFF the main thread
        // (BackgroundAssetStreamer.Request), and only the Godot build (Skeleton3D + skinned mesh + materials) runs
        // on the main thread, budgeted. Build then free the throwaway node (the texture/material caches persist).
        // Low priority: a LIVE on-demand player-model resolve (ResolvePlayerModel) shares this streamer at High
        // priority, so a visible player's model always builds before warm-only work.
        if (_streamer is null)
            return;
        AssetLoader loader = _assets;
        foreach (string model in IdleWarmPlayerModels)
        {
            string m = model;
            _streamer.Request(
                () => loader.ParseSkeletalModel(m, 0),                 // off-thread: IQM + sidecars + anims
                // (§12.3-1) Stage the texture pipeline like the live path — the idle warm previously paid the
                // same multi-hundred-ms monolithic build; now its decode runs on the worker and each upload is
                // its own budgeted job. The final assembly warms the full build path, then discards the node.
                parse => EnqueueStagedSkeletalBuild(loader, parse,
                    XonoticGodot.Game.Client.BackgroundAssetStreamer.Priority.Low, $"idle-warm {m}",
                    () => loader.BuildSkeletalModel(parse)?.Root.QueueFree()),
                XonoticGodot.Game.Client.BackgroundAssetStreamer.Priority.Low,
                label: $"idle-warm {m} (parse)");
        }
    }

    /// <summary>
    /// Decide which weapons this match is likely to use, so <see cref="PrecacheWeaponModelsAsync"/> can warm
    /// just those instead of all 24. Combines (a) the map's <c>weapon_&lt;netname&gt;</c> spawn entities,
    /// (b) the universal blaster (granted on spawn in nearly every standard gametype), and (c) mutator/arena
    /// overrides that REPLACE the loadout (instagib → vaporizer only, overkill → ok* set, nix → all normal
    /// non-mutatorblocked weapons it rotates through, <c>g_weaponarena</c> → the arena list). Falls back to
    /// "all weapons" for the pure-client path where we have no map/cvar info yet; lazy <see cref="AssetLoader"/>
    /// caching catches anything we missed (one-frame hitch on first use, then cached).
    /// </summary>
    private System.Collections.Generic.HashSet<string> ComputeExpectedWeapons()
    {
        var set = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        // Pure client (we don't see the map's entities, mutators, or cvars locally yet) — fall back to
        // warming everything. Slightly wasteful, but keeps the first-use hitch off the connecting client.
        if (!_isListenServer || _serverWorld is null)
        {
            foreach (XonoticGodot.Common.Gameplay.Weapon w in XonoticGodot.Common.Gameplay.Weapons.All)
                if (!string.IsNullOrEmpty(w.NetName))
                    set.Add(w.NetName);
            return set;
        }

        XonoticGodot.Common.Services.ICvarService cv = _serverWorld.Services.Cvars;

        // --- Replacement mutators: these define the loadout outright, so the map's weapon_ pickups are
        //     gameplay-irrelevant (instagib/overkill replace the player's inventory each spawn; nix forces
        //     a cycling weapon). Return EARLY — don't union with map entities. ---

        if (cv.GetFloat("g_instagib") != 0f)
        {
            set.Add("vaporizer");
            return set;
        }

        if (cv.GetFloat("g_overkill") != 0f)
        {
            set.Add("okmachinegun");
            set.Add("oknex");
            set.Add("okshotgun");
            if (cv.GetFloat("g_weapon_overkill_rpc_weaponstart") != 0f) set.Add("okrpc");
            if (cv.GetFloat("g_weapon_overkill_hmg_weaponstart") != 0f) set.Add("okhmg");
            return set;
        }

        if (cv.GetFloat("g_nix") != 0f)
        {
            // NIX cycles through every "normal" non-mutator-blocked weapon — see NixMutator.CanChooseWeapon.
            foreach (XonoticGodot.Common.Gameplay.Weapon w in XonoticGodot.Common.Gameplay.Weapons.All)
            {
                if ((w.SpawnFlags & XonoticGodot.Common.Gameplay.WeaponFlags.MutatorBlocked) != 0) continue;
                if ((w.SpawnFlags & XonoticGodot.Common.Gameplay.WeaponFlags.Normal) == 0) continue;
                if (!string.IsNullOrEmpty(w.NetName))
                    set.Add(w.NetName);
            }
            if (cv.GetFloat("g_nix_with_blaster") != 0f) set.Add("blaster");
            return set;
        }

        // g_weaponarena: empty / "0" / "off" = disabled (use normal map pickups). Otherwise space-separated
        // list of netnames, or "all"/"most" shorthand. Arena REPLACES spawn loadout (map pickups still
        // physically there, but the carried loadout is the arena set — that's what we definitely render).
        string arena = cv.GetString("g_weaponarena");
        if (!string.IsNullOrWhiteSpace(arena) && arena != "0" && !arena.Equals("off", System.StringComparison.OrdinalIgnoreCase))
        {
            if (arena.Equals("all", System.StringComparison.OrdinalIgnoreCase)
             || arena.Equals("most", System.StringComparison.OrdinalIgnoreCase))
            {
                foreach (XonoticGodot.Common.Gameplay.Weapon w in XonoticGodot.Common.Gameplay.Weapons.All)
                    if ((w.SpawnFlags & XonoticGodot.Common.Gameplay.WeaponFlags.MutatorBlocked) == 0
                     && !string.IsNullOrEmpty(w.NetName))
                        set.Add(w.NetName);
            }
            else
            {
                foreach (string tok in arena.Split(new[] { ' ', '\t', ',' }, System.StringSplitOptions.RemoveEmptyEntries))
                    set.Add(tok.Trim());
            }
            // Arena weapons fully define the loadout — don't union with map pickups, but DO keep the blaster
            // since g_weaponarena_with_blaster (and stock modes) usually still grant it.
            set.Add("blaster");
            return set;
        }

        // --- Normal path: union of map weapon_ entity classnames + universal starter weapons. ---
        if (_bsp is not null)
        {
            foreach (System.Collections.Generic.IReadOnlyDictionary<string, string> dict in _bsp.Entities)
            {
                if (!dict.TryGetValue("classname", out string? cls) || string.IsNullOrEmpty(cls))
                    continue;
                const string prefix = "weapon_";
                if (cls.StartsWith(prefix, System.StringComparison.Ordinal))
                    set.Add(cls.Substring(prefix.Length));
            }
        }

        // The blaster is granted on spawn in essentially every stock gametype — always include it.
        set.Add("blaster");

        return set;
    }

    /// <summary>The muzzle-flash effect name for a weapon — the QC <c>m_muzzleeffect</c> attrib of each
    /// weapon's .qh (e.g. electro.qh:29 EFFECT_ELECTRO_MUZZLEFLASH, devastator.qh:28 EFFECT_ROCKET_MUZZLEFLASH,
    /// minelayer.qh:30 EFFECT_ROCKET_MUZZLEFLASH, vaporizer.qh:26 EFFECT_VORTEX_MUZZLEFLASH). Defaults to the
    /// blaster flash (an unregistered name simply yields no flash, never an error).</summary>
    private static string MuzzleEffectFor(XonoticGodot.Common.Gameplay.Weapon w) => w.NetName switch
    {
        "vortex" or "vaporizer" => "VORTEX_MUZZLEFLASH",
        "devastator" or "minelayer" => "ROCKET_MUZZLEFLASH",
        "mortar" => "GRENADE_MUZZLEFLASH",
        "machinegun" => "MACHINEGUN_MUZZLEFLASH",
        "electro" => "ELECTRO_MUZZLEFLASH",
        "crylink" => "CRYLINK_MUZZLEFLASH",
        "shotgun" => "SHOTGUN_MUZZLEFLASH",
        "hagar" => "HAGAR_MUZZLEFLASH",
        "arc" => "ARC_MUZZLEFLASH",
        "rifle" => "RIFLE_MUZZLEFLASH",
        "seeker" => "SEEKER_MUZZLEFLASH",
        "hook" => "HOOK_MUZZLEFLASH",
        "hlac" => "GREEN_HLAC_MUZZLEFLASH",
        "fireball" => "FIREBALL_MUZZLEFLASH",
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
            _client.PrintReceived -= OnServerPrintForChat;
        }
        if (_sharedCvarBridge is not null && _sharedCvars is not null)
        {
            _sharedCvars.Changed -= _sharedCvarBridge;   // shared store outlives this match; don't leak the hook
            _sharedCvarBridge = null;
        }
        if (_processCvarsHooked && _sharedCvars is not null)
            _sharedCvars.Changed -= OnProcessCvarChanged;   // same store-outlives-match reasoning (§11 R11)
        _minigame?.Dispose();
        // Drop any unconsumed predecoded texture images (a model whose staged build never completed —
        // entity died mid-stream / map change) so decoded pixels don't outlive the session.
        _assets?.Assets.ClearPredecodedImages();
        _client?.Dispose();
        // S5: join the server-sim worker BEFORE disposing ServerNet/the world, so the socket + world teardown is
        // single-threaded and no in-flight tick races the Dispose (the worker holds the gate around its whole
        // tick, so Join guarantees it's not mid-tick when we tear down). Clear the gate after the join so any
        // late main-thread _Process this frame takes no lock. No-op when not threaded (both null).
        _serverThread?.Dispose();
        _serverThread = null;
        if (_server is not null)
            _server.SimGate = null;
        if (_musicPlayer is not null && Godot.GodotObject.IsInstanceValid(_musicPlayer))
            _musicPlayer.SimGate = null;   // gate released; music scan reverts to lock-free
        _simGate = null;
        _server?.Dispose();
        // Free the carrier so a torn-down session doesn't leave a stray player edict in its facade. Use the
        // captured world (Api.Services may already have been swapped to the menu's empty facade by teardown).
        if (_carrier is not null && _world is not null && !_carrier.IsFreed)
            _world.Entities.Remove(_carrier);
    }

    // =====================================================================================
    //  Per-frame drive (the heart: server tick → client poll → send input → camera)
    // =====================================================================================

    // (§11 R11) The three cvars _Process consults every frame, cached so the hot path does zero dictionary
    // lookups: seeded on first frame, refreshed only when the shared store raises Changed for one of them.
    // With no shared store (bare CLI client) the seeds match the old per-frame defaults.
    private bool _processCvarsHooked;
    private float _bgmVolumeCv = 0.7f;
    private bool _predictFireCv = true;
    private bool _perFrameInputCv;

    private void EnsureProcessCvarCache()
    {
        if (_processCvarsHooked)
            return;
        _processCvarsHooked = true;
        RefreshProcessCvars();
        if (_sharedCvars is not null)
            _sharedCvars.Changed += OnProcessCvarChanged;
    }

    private void OnProcessCvarChanged(string name)
    {
        if (name.Equals("bgmvolume", StringComparison.OrdinalIgnoreCase)
            || name.Equals("cl_predictfire", StringComparison.OrdinalIgnoreCase)
            || name.Equals("cl_movement_perframe", StringComparison.OrdinalIgnoreCase))
            RefreshProcessCvars();
    }

    private void RefreshProcessCvars()
    {
        _bgmVolumeCv = _sharedCvars?.GetFloat("bgmvolume") ?? 0.7f;
        // cl_predictfire defaults ON: unset GetString reads "" → treat anything but "0" as on.
        _predictFireCv = (_sharedCvars?.GetString("cl_predictfire") ?? "") != "0";
        _perFrameInputCv = _sharedCvars?.GetFloat("cl_movement_perframe") is float pf && pf != 0f;
    }

    public override void _Process(double delta)
    {
        using var _ngScope = XonoticGodot.Game.Client.FrameProfiler.Scope("ng.process"); // [profiling] whole-method cost
        // _Ready runs as a coroutine (async void) so the loading screen can animate between sub-stages —
        // until it sets _readyComplete, many of the fields touched below (the HUD, the camera, _client,
        // _server) are partly built. Just sit out _Process during this window; the LoadingScreen drives
        // its own animation, and the handshake state machine doesn't matter yet (no _client).
        if (!_readyComplete)
            return;

        EnsureProcessCvarCache();   // (§11 R11) hot-path cvar values are cached; refreshed on Changed

        float dt = (float)delta;

        // Drive the listen server (if any) by real elapsed time — it runs its fixed ticks, pulls each client's
        // queued input, simulates, and broadcasts snapshots.
        // S5: when threaded, the dedicated worker (XG-ServerSim) owns ServerNet.Tick — the main thread must NOT
        // call it (that would double-drive the world AND race the worker). The "server.tick" main-thread scope
        // then measures ~0, which is exactly the A/B metric this item targets. When NOT threaded, drive it here
        // exactly as today.
        if (_serverThread is null)
            using (XonoticGodot.Game.Client.FrameProfiler.Scope("server.tick"))
                _server?.Tick(dt);

        // S5: from here to the end of _Process we read/mutate server-world state (the music/HUD feeds,
        // LocalServerPlayer, the carrier reconcile) AND run the client prediction replay (inside _client.SendInput
        // → EntityMovementStep.Step). When threaded, all of that must be serialised against the worker's
        // ServerNet.Tick, so take the SAME gate the worker locks. A single try/finally spans the whole remainder
        // (incl. the one early `return` at "_client is null") so the gate is always released. When NOT threaded
        // (_simGate null) this is a plain pass-through with no lock — byte-for-byte the old path.
        object? simGate = _simGate;
        bool simGateTaken = false;
        try
        {
            if (simGate is not null)
                System.Threading.Monitor.Enter(simGate, ref simGateTaken);

        // Feed the music player the current server time so trigger_music touch freshness works.
        if (_musicPlayer is not null && _serverWorld is not null)
        {
            _musicPlayer.ServerTime = _serverWorld.Time;
            // Keep bgmvolume in sync (cached; the Changed hook refreshes it on a menu/console set). Only the
            // no-shared-store path still reads the server store live (rare: bare CLI host).
            float bgm = _sharedCvars is not null ? _bgmVolumeCv : _serverWorld.Services.Cvars.GetFloat("bgmvolume");
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

                    // Hit confirmation when the LOCAL player dealt the damage (not self-damage): the hitsound
                    // beep AND the crosshair hitmarker flash (QC HitSound + the crosshair hit indication). The
                    // crosshair pulse is otherwise unfed — CrosshairPanel.HitFlash decays itself each frame.
                    if (localPlayer is not null && ev.Attacker == localPlayer && ev.Target != localPlayer)
                    {
                        _hitSound?.OnHit(ev.Health + ev.Armor);
                        _fullHud.Crosshair.HitFlash = 1f;
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

            // LISTEN-SERVER prediction parity: the predicted carrier and the authoritative host Player are TWO
            // distinct, co-located entities in the SAME world. The host Player is a SOLID_SLIDEBOX body in the
            // trace set, and the carrier's slide-move (PlayerPhysics.PushEntity, MoveFilter.Normal) clips Body —
            // so the carrier's sweep collides with the host player's own body, which the server player (ignore=
            // self) never does. That intermittent block makes the predicted origin drift sideways from authority,
            // and the prediction-error smoother drags the camera to the side then crawls it back. Link the carrier
            // to the host Player so TraceService.ClipToEntities skips it (it excludes ignore.Owner==touch); the
            // host player isn't known at SpawnCarrier time, so (re)assign here each frame once the spawn resolves.
            // Only the host body is skipped — projectiles/world still clip — so the projectile-passthrough fix
            // (carrier.DpHitContentsMask=PlayerClip) is unaffected.
            if (_carrier is not null && LocalServerPlayer is { } hostSelf && !ReferenceEquals(_carrier.Owner, hostSelf))
                _carrier.Owner = hostSelf;

            // F02 dead-movement gate: the carrier is a DISTINCT prediction entity that never learns it died, so
            // PlayerPhysics.Move's dead-gate would never fire for the local player and a dead host would keep
            // sliding under WASD. Mirror the authoritative dead state onto the carrier each frame: a listen
            // server reads the host Player; a pure client derives it from the networked health (only after the
            // first spawn, matching the IsDead gate used for the death-cam below). PlayerPhysics.Move then bails.
            if (_carrier is not null)
            {
                bool localDead = LocalServerPlayer is { } self ? self.IsDead : (_everAlive && _client.Health <= 0);
                _carrier.DeadState = localDead ? DeadFlag.Dead : DeadFlag.No;
            }

            // F03 (re)spawn view snap (QC PutPlayerInServer: self.fixangle = true; self.angles = spot.angles). The
            // server latches the spawn-spot facing in the host Player's FixAngle/FixAngleAngles channel (the same
            // QC .fixangle reused for teleporters). The client owns its view angles, so snap _viewAngles to the
            // latched facing here — BEFORE the input loop below samples/sends the view — then clear (one-shot). A
            // listen server reads it in-process; a pure client would need it networked (follow-up). This also
            // snaps the view out of any server-side (multi-destination) teleport that set the host Player's flag.
            if (LocalServerPlayer is { FixAngle: true } fixSelf)
            {
                _viewAngles.X = Mathf.Clamp(fixSelf.FixAngleAngles.X, -89f, 89f);
                _viewAngles.Y = fixSelf.FixAngleAngles.Y;
                _viewAngles.Z = 0f;
                fixSelf.FixAngle = false;
            }

            // Local fire feedback runs EVERY render frame (before the input drain below), so the muzzle flash,
            // recoil, sound and HUD pulse pop the same frame as the click rather than waiting for the next 1/72 s
            // input tick — the felt "snap" on high-refresh displays. It also latches the press for the next sampled
            // command so a sub-tick tap can't be dropped, and (cl_predictfire) drives the predicted refire clock.
            // cl_predictfire defaults ON (cached; refreshed via the Changed hook).
            _predictFire = _predictFireCv;
            // Tell the in-process effect mirror to drop the host's own muzzle flash (predicted locally) when on.
            if (_render is not null && GodotObject.IsInstanceValid(_render))
            {
                _render.SuppressOwnFireEffects = _predictFire;
                _render.LocalHostPlayer = LocalServerPlayer;
            }
            UpdateLocalFireFeedback(dt);

            // Input cadence. The client tells the server which mode it's in (PerFrameInput rides the input-frame
            // header) so the two ends agree without a shared cvar store. Cached value still A/B-toggles
            // in-session (the Changed hook refreshes it the frame the cvar is set).
            _perFrameInput = _perFrameInputCv;
            _client.PerFrameInput = _perFrameInput;
            if (_perFrameInput)
            {
                // PER-FRAME (DP-style variable-dt): emit ONE command per rendered frame stamped with the real
                // clamped frame dt; the server drains all pending commands that tick, running movement per command
                // with each dt. Removes the up-to-one-tick input quantization → snappier fire + aim. The player
                // still advances at wall-clock speed because each command carries the dt it represents.
                _inputDeltaTime = Mathf.Clamp(dt, MinInputDt, MaxInputDt);
                _renderClock += _inputDeltaTime;
                _client.SendInput(_renderClock);
                ConsumePredictedFixAngle();
                _inputAccum = 0f; // keep the legacy accumulator drained so a mid-session switch back can't burst
            }
            else
            {
                // LEGACY fixed-timestep: emit exactly one InputCommand per 1/72 s of REAL time, independent of the
                // display frame rate (DeltaTime = TicRate), so the server advances this player at true wall-clock
                // speed. Accumulate real delta and drain it in fixed quanta; cap the backlog so a hitch can't
                // trigger a spiral-of-death.
                const float tic = XonoticGodot.Engine.Simulation.SimulationLoop.TicRate;
                _inputDeltaTime = tic;
                _inputAccum += dt;
                if (_inputAccum > MaxInputBacklog) _inputAccum = MaxInputBacklog;
                float clock = _renderClock;
                while (_inputAccum >= tic)
                {
                    _inputAccum -= tic;
                    clock += tic;
                    // Sample WASD/mouse → InputCommand → push to the ring, predict forward, send the redundant tail.
                    _client.SendInput(clock);
                    ConsumePredictedFixAngle(); // teleporter view-snap, inside the loop so a 2nd tick samples it
                }
                // Read the camera at the clock the last prediction ran with, so eye and predicted origin agree.
                _renderClock = clock;
            }
        }

        // Loading screen progress during the async handshake + spawn phase (DP's progress bar filling up
        // while connecting/joining). The loading screen is owned by Shell; we BeginStage on each transition
        // (NOT every frame — that would reset the asymptote and stall the bar) and dismiss it via
        // DismissLoadingScreen when the player spawns. Final dismiss uses UpdateProgress to snap to 1.
        if (LoadingScreen is not null || _fallbackOverlay is not null)
        {
            HandshakeStage stage =
                (_cameraReady && _client.Health > 0) ? HandshakeStage.Spawned
                : !_client.Accepted                  ? HandshakeStage.Connecting
                : !_cameraReady                       ? HandshakeStage.WaitingForServer
                : HandshakeStage.Joining;

            if (stage != _handshakeStage)
            {
                _handshakeStage = stage;
                switch (stage)
                {
                    case HandshakeStage.Connecting:
                        LoadingScreen?.BeginStage("Connecting to server…", 0.94f, 1.0f);
                        break;
                    case HandshakeStage.WaitingForServer:
                        LoadingScreen?.BeginStage("Waiting for first snapshot…", 0.97f, 0.5f);
                        break;
                    case HandshakeStage.Joining:
                        LoadingScreen?.BeginStage("Joining game…", 0.99f, 0.5f);
                        break;
                    case HandshakeStage.Spawned:
                        LoadingScreen?.UpdateProgress(1f, "");
                        DismissLoadingScreen?.Invoke();
                        LoadingScreen = null;
                        DismissLoadingScreen = null;

                        if (_fallbackOverlay is not null && GodotObject.IsInstanceValid(_fallbackOverlay))
                            _fallbackOverlay.QueueFree();
                        _fallbackOverlay = null;
                        break;
                }
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

        // QC button_zoom on the net path: held while the +zoom bind's key is down (BindTable). PLUS the active
        // weapon's secondary-fire zoom (QC view.qc IsZooming folds in each weapon's wr_zoomdir): the Vortex zooms
        // while ATTACK2 is held and g_balance_vortex_secondary is 0 (the stock default — the networked active
        // weapon arrives as ClientNet.ActiveWeaponId). Suppressed when dead (QC cl_unpress_zoom_on_death — the
        // view zooms out on death), paused, or the console is open. Fed to the shared view, which lerps
        // current_viewzoom toward the target in UpdateView.
        bool zoomActive = !GetTree().Paused && !ConsoleState.IsOpen && _client.Accepted && _client.Health > 0;
        XonoticGodot.Common.Gameplay.Weapon? activeWep =
            _client.ActiveWeaponId >= 0 ? XonoticGodot.Common.Gameplay.Weapons.ById(_client.ActiveWeaponId) : null;
        bool weaponZoom = activeWep is not null && activeWep.ZoomOnSecondary && BindTable.Attack2Held;
        _view.ZoomHeld = zoomActive && (BindTable.ZoomHeld || weaponZoom);

        // Place the first-person camera at the predicted eye each frame (smooth even between snapshots, since
        // SendInput re-predicts every tick). C5: held until the first snapshot seeds the carrier — before that
        // the predicted origin is (0,0,0) and the camera would render a from-world-origin frame during the
        // handshake. The camera is first-placed in the firstSnapshot branch above. Drives the shared view (zoom
        // lerp + camera placement + eventchase + eye-contents), so it must run BEFORE the ViewEffects feed below
        // (which reads SampleEyeContents = _view.EyeContents).
        if (_cameraReady)
            UpdateCamera(dt);

        // Zoom scope reticle (QC DrawReticle): the generic +zoom reticle, or the active weapon's scope (the
        // Vortex's gfx/reticle_nex) while zooming with it. Fed after UpdateCamera so ZoomFraction is this frame's
        // value; reuses the active weapon resolved for the zoom above. Suppressed while dead / spectating / chase.
        _reticle?.UpdateReticle(activeWep, BindTable.ZoomHeld, BindTable.Attack2Held,
            _view.ZoomFraction, LocalDeadNow(), _client.SpectatingNetId != 0, _view.ChaseActive);

        // Feed the full HUD's player-bound panels (health/ammo/weapons/crosshair) the local server Player on a
        // listen server, so they reflect live local state as the spawn lands (QC the view player). A pure client
        // has no local Player actor — the NetHud crosshair/health covers it. Cheap: SetPlayer no-ops when same.
        UpdateFullHudPlayer();
        UpdateInfoMessages();

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
            // Observing/spectating (spectatee_status != 0) suppresses the death-fade like the pre-spawn window:
            // a free-fly observer has Health 0 (would otherwise ramp the death fade), and a follower's copied
            // health drives the HUD without a death overlay (QC spectatee_status guards the screen effects).
            bool observing = !_everAlive || _client.SpectateeStatus != 0;
            // "observing" = not yet spawned (the pre-spawn / connecting window right after Create) OR actively
            // observing/spectating: mirror QC's spectatee_status guard so health 0 here doesn't ramp the death
            // fade onto the screen. _everAlive flips true on the first spawn (line ~1212), so a genuine in-match
            // death (health<=0 after spawning) still shows the death fade. Matches the IsDead gate used below.
            _viewEffects.UpdateEffects(dt, health, SampleEyeContents(), observing);
        }

        // Keep the radar oriented to the player's facing.
        if (_radar is not null)
            _radar.LocalYawDegrees = _viewAngles.Y;

        // Scoreboard (QC +showscores): show while the scoreboard key is held, and feed it the networked rows +
        // team totals whenever a fresh LatestScoreboard arrives (the panel only repaints on data/toggle, so this
        // is cheap). BindTable.ShowScores is the held-button state set from the +showscores bind.
        UpdateScoreboard();
        UpdateScore();
        UpdateMatchClock();
        UpdatePickupFeed();
        UpdateModIcons();
        UpdateAccuracy();

        // Minigame cursor (QC hud_cursormode): while a minigame board/menu owns input, show the cursor so the
        // player can click TTT/C4 tiles + the menu; recapture for play on the edge back out. Skip while the
        // pause menu/console own the cursor (the Shell drives those).
        if (!GetTree().Paused && !ConsoleState.IsOpen)
        {
            bool ui = UiOwnsCursor;
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
        } // end try (S5 sim-gate span)
        finally
        {
            // S5: release the sim gate taken at the top of _Process (no-op when not threaded). Always runs, incl.
            // the early `_client is null` return and any throw in the body above.
            if (simGateTaken)
                System.Threading.Monitor.Exit(simGate!);
        }
    }

    /// <summary>The active gametype short code for a changelevel: the live <c>gametype</c> cvar if a mid-match
    /// <c>gametype</c> command set it, else the mode this match booted with.</summary>
    private string CurrentGametype()
    {
        string gt = _serverWorld?.Services.Cvars.GetString("gametype") ?? "";
        return string.IsNullOrWhiteSpace(gt) ? _gametype : gt;
    }

    // ---- pickup feed (QC HUD_Pickup / STAT(LAST_PICKUP)) ----
    // The port has no networked LAST_PICKUP stat, so on the listen server we detect pickups client-side off the
    // local Player: a NEW weapon in the owned set is an unambiguous pickup, and a per-frame resource JUMP above a
    // threshold regen never reaches is an item pickup. Baselines are (re)seeded silently on (re)spawn so the spawn
    // grant isn't reported. (Pure --connect clients have no local Player → the feed stays empty; a faithful
    // LAST_PICKUP stat would be the cross-client follow-up.)
    private bool _pickupInit;
    private XonoticGodot.Common.Gameplay.WepSet _pickupLastOwned;
    private float _pickupHealth, _pickupArmor, _pickupShells, _pickupBullets, _pickupRockets, _pickupCells;

    private void UpdatePickupFeed()
    {
        if (_fullHud is null)
            return;
        Player? p = LocalServerPlayer;
        if (p is null || p.IsDead || p.IsObserver)
        {
            _pickupInit = false; // re-baseline on (re)spawn so the spawn loadout isn't shown as a pickup
            return;
        }

        XonoticGodot.Common.Gameplay.WepSet owned = p.OwnedWeaponSet;
        float health = p.GetResource(ResourceType.Health);
        float armor = p.GetResource(ResourceType.Armor);
        float shells = p.GetResource(ResourceType.Shells);
        float bullets = p.GetResource(ResourceType.Bullets);
        float rockets = p.GetResource(ResourceType.Rockets);
        float cells = p.GetResource(ResourceType.Cells);

        if (_pickupInit)
        {
            XonoticGodot.Game.Hud.PickupPanel? feed = _fullHud.GetPanel<XonoticGodot.Game.Hud.PickupPanel>();
            if (feed is not null)
            {
                // New weapons — always a genuine pickup (never regen/spawn here, since spawn re-baselines).
                foreach (XonoticGodot.Common.Gameplay.Weapon w in XonoticGodot.Common.Gameplay.Weapons.All)
                    if (owned.Has(w) && !_pickupLastOwned.Has(w))
                        feed.Push(string.IsNullOrEmpty(w.DisplayName) ? w.NetName : w.DisplayName,
                                  XonoticGodot.Game.Hud.WeaponHud.IconName(w.NetName));

                // Resource pickups — a per-frame jump above a threshold regen never reaches in one frame
                // (health/armor regen is gradual; the 4 main ammo pools never regen).
                if (health - _pickupHealth >= 3f) feed.Push("Health", "health");
                if (armor - _pickupArmor >= 3f) feed.Push("Armor", "armor");
                if (shells - _pickupShells >= 1f) feed.Push("Shells", "ammo_shells");
                if (bullets - _pickupBullets >= 1f) feed.Push("Bullets", "ammo_bullets");
                if (rockets - _pickupRockets >= 1f) feed.Push("Rockets", "ammo_rockets");
                if (cells - _pickupCells >= 1f) feed.Push("Cells", "ammo_cells");
            }
        }

        _pickupInit = true;
        _pickupLastOwned = owned;
        _pickupHealth = health; _pickupArmor = armor;
        _pickupShells = shells; _pickupBullets = bullets; _pickupRockets = rockets; _pickupCells = cells;
    }

    /// <summary>Drain the networked match clock (NetControl.MatchState → ClientNet) into the TIMER panel each
    /// frame: GAMESTARTTIME / TIMELIMIT*60 / warmup, with "now" on the same server clock (LatestServerTime).
    /// The panel self-blanks until the first MatchState arrives, so this is safe to call every frame.</summary>
    private void UpdateMatchClock()
    {
        if (_fullHud is null || _client is null || !_client.HasMatchState)
            return;
        TimerPanel t = _fullHud.Timer;
        t.Now = _client.LatestServerTime;
        t.MatchStartTime = _client.MatchStartTime;
        t.TimeLimitSeconds = _client.MatchTimeLimit;
        t.WarmupStage = _client.MatchWarmup;
        t.WarmupTimeLimitSeconds = _client.MatchWarmupLimit;
        if (_client.MatchIntermission)
            t.IntermissionTime = _client.LatestServerTime;
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

        // With a local Player present the skinned Crosshair + HealthArmor panels render, so suppress NetHud's
        // duplicate vector crosshair + textual health/armor (goal 1: no double crosshair/health). On a pure client
        // (p == null) NetHud stays the always-on fallback.
        bool havePlayer = p is not null;
        if (_hud is not null && GodotObject.IsInstanceValid(_hud))
            _hud.SuppressCrosshairAndHealth = havePlayer;

        // HealthArmorPanel reads the Player's resources, so it self-blanks with no Player (safe to leave visible).
        // CrosshairPanel, however, draws its reticle even without a Player (it doesn't gate on Player) — so on a
        // pure client it would DOUBLE NetHud's crosshair. Show the skinned crosshair only when a local Player is
        // present; otherwise NetHud (un-suppressed above) owns the reticle. No double crosshair in either case.
        if (_fullHud.Crosshair.Visible != havePlayer)
            _fullHud.Crosshair.Visible = havePlayer;

        // StrafeHud (goal 3): point it at the local server Player (its velocity + view angles + onground drive the
        // strafe bar). It self-blanks without a Player, so on a pure client this leaves it null = no draw. The
        // optional WishDir/JumpHeld/OnSlick are not fed (the HUD degrades to the non-local W+A path), which keeps
        // it useful without exposing the carrier's per-tick move-values to the client HUD layer.
        if (_fullHud.GetPanel<XonoticGodot.Game.Hud.StrafeHudPanel>() is { } strafe)
            strafe.Player = p;
    }
    private Player? _lastHudPlayer;

    /// <summary>
    /// Feed the InfoMessages panel the networked dead/respawn + observing/spectating state each frame (QC the
    /// infomessages panel reads STAT(RESPAWN_TIME) + spectatee_status). Works on a pure remote client (no local
    /// Player) since it sources everything from <see cref="ClientNet"/>: the respawn countdown / "press fire"
    /// prompt from <see cref="ClientNet.RespawnTimeStat"/>, and the "Observing" / "Spectating: name" line from
    /// <see cref="ClientNet.SpectateeStatus"/> (the spectatee's name resolved on a listen server).
    /// </summary>
    private void UpdateInfoMessages()
    {
        if (_fullHud is null || _client is null)
            return;
        InfoMessagesPanel im = _fullHud.InfoMessages;
        im.RespawnStat = _client.RespawnTimeStat;
        im.NetServerTime = _client.LatestServerTime;

        bool spectating = _client.Accepted && _client.SpectateeStatus != 0;
        im.IsSpectating = spectating;
        if (spectating)
        {
            int sid = _client.SpectatingNetId;
            im.SpectatingName = sid != 0 && _server is not null
                ? (_server.PlayerByNetId(sid)?.NetName ?? "")
                : "";
        }
    }

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

        // feed rows only when the data changed (identity by reference — ClientNet replaces the object per
        // decode). A mode-status change (T53) also re-feeds: the eliminated grey-out must track freezes/deaths
        // that don't bump a score version.
        XonoticGodot.Net.ScoreboardWire? sb = _client.LatestScoreboard;
        XonoticGodot.Net.GametypeStatusBlock.Decoded? ms = _client.LatestModeStatus;
        if (sb is not null && (!ReferenceEquals(sb, _lastFedScoreboard) || !ReferenceEquals(ms, _lastFedModeStatus)))
        {
            _lastFedScoreboard = sb;
            _lastFedModeStatus = ms;
            _scoreboard.Title = ScoreboardTitle();
            _scoreboard.SetWireRows(sb, _client.LocalNetId, ms?.EliminatedNetIds); // grey-out (QC eliminatedPlayers)
            FeedScoreboardHeader();
        }
        else if (sb is null)
        {
            // No networked scoreboard yet (pre-first-snapshot): show an empty titled panel rather than nothing.
            _scoreboard.Title = ScoreboardTitle();
        }
    }
    private XonoticGodot.Net.ScoreboardWire? _lastFedScoreboard;
    private XonoticGodot.Net.GametypeStatusBlock.Decoded? _lastFedModeStatus;

    // [Score panel] last-fed scoreboard reference, so the in-game Score overlay rebuilds only on a data change
    // (the wire object is replaced per decode — identity by reference, like UpdateScoreboard).
    private XonoticGodot.Net.ScoreboardWire? _lastScorePanelFed;

    /// <summary>
    /// Feed the in-game Score OVERLAY (#7 — the corner standing readout, distinct from the full scoreboard) from
    /// the SAME networked source the scoreboard uses (<see cref="ClientNet.LatestScoreboard"/> +
    /// <see cref="ClientNet.LocalNetId"/>). Best-effort feed (goal 7): own primary score + place, the rankings
    /// leaderboard rows, and (teamplay) per-team scores. The panel self-blanks until fed (<c>HasData</c>), so a
    /// pre-first-snapshot client draws nothing. Rebuilds only when the wire object changes (cheap per frame).
    /// </summary>
    private void UpdateScore()
    {
        if (_fullHud is null || _client is null)
            return;
        XonoticGodot.Game.Hud.ScorePanel? panel = _fullHud.GetPanel<XonoticGodot.Game.Hud.ScorePanel>();
        if (panel is null)
            return;

        XonoticGodot.Net.ScoreboardWire? sb = _client.LatestScoreboard;
        if (sb is null)
            return; // no networked scores yet → panel self-blanks
        if (ReferenceEquals(sb, _lastScorePanelFed))
            return; // unchanged since last feed
        _lastScorePanelFed = sb;

        // Primary score column index within the wire's NetworkedFields order (QC scores_primary). Default 0
        // (SP_SCORE) when no gametype refined it. The wire's Columns array is in NetworkedFields order.
        var fields = XonoticGodot.Common.Gameplay.Scoring.GameScores.NetworkedFields;
        XonoticGodot.Common.Gameplay.Scoring.ScoreField? primary =
            XonoticGodot.Common.Gameplay.Scoring.GameScores.Primary;
        int primaryIdx = 0;
        if (primary is not null)
            for (int i = 0; i < fields.Count; i++)
                if (ReferenceEquals(fields[i], primary)) { primaryIdx = i; break; }
        XonoticGodot.Common.Gameplay.Scoring.ScoreFlags primaryFlags =
            primary?.Flags ?? XonoticGodot.Common.Gameplay.Scoring.ScoreFlags.None;

        bool teamplay = XonoticGodot.Common.Gameplay.Scoring.GameScores.Teamplay;
        bool spectating = _client.SpectateeStatus != 0 && _client.SpectateeStatus != _client.LocalNetId;

        // ---- build a sorted (primary desc) snapshot of the rows ----
        var sorted = new System.Collections.Generic.List<XonoticGodot.Net.ScoreRowWire>(sb.Rows);
        int PrimaryOf(XonoticGodot.Net.ScoreRowWire r) =>
            (r.Columns is not null && primaryIdx < r.Columns.Length) ? r.Columns[primaryIdx] : 0;
        sorted.Sort((a, b) => PrimaryOf(b).CompareTo(PrimaryOf(a)));

        // ---- own standing (place + primary score) ----
        int localId = _client.LocalNetId;
        int selfScoreVal = 0, selfPlace = 0;
        bool haveSelf = false;
        for (int i = 0; i < sorted.Count; i++)
        {
            if (sorted[i].NetId != localId) continue;
            haveSelf = true;
            selfPlace = i + 1; // 1-based
            selfScoreVal = PrimaryOf(sorted[i]);
            break;
        }
        if (haveSelf)
            panel.SetSelf(
                XonoticGodot.Common.Gameplay.Scoring.GameScores.ScoreString(primaryFlags, selfScoreVal),
                selfPlace);

        // FFA gap to the next player above/below (QC me.scores - pl.scores): the signed gap to the nearest rival.
        if (haveSelf && !teamplay && sorted.Count > 1)
        {
            // Gap to the player immediately ahead (negative = behind), or to the one behind if leading.
            int rivalVal = selfPlace > 1 ? PrimaryOf(sorted[selfPlace - 2]) : PrimaryOf(sorted[selfPlace]);
            panel.SetSelfGap(selfScoreVal - rivalVal, hasGap: true);
        }
        else
        {
            panel.SetSelfGap(0f, hasGap: false);
        }

        // ---- rankings leaderboard rows ----
        var rows = new System.Collections.Generic.List<XonoticGodot.Game.Hud.ScorePanel.RankRow>(sorted.Count);
        foreach (XonoticGodot.Net.ScoreRowWire r in sorted)
        {
            string scoreStr = XonoticGodot.Common.Gameplay.Scoring.GameScores.ScoreString(primaryFlags, PrimaryOf(r));
            rows.Add(new XonoticGodot.Game.Hud.ScorePanel.RankRow(r.Name, scoreStr, r.Team, r.NetId == localId));
        }
        panel.SetRankings(rows);

        // ---- team scores (teamplay) ----
        if (teamplay && sb.Teams.Count > 0)
        {
            int myTeam = LocalServerPlayer is { } lp ? (int)lp.Team : 0;
            var teams = new System.Collections.Generic.List<XonoticGodot.Game.Hud.ScorePanel.TeamScore>(sb.Teams.Count);
            foreach ((int team, int score) in sb.Teams)
                teams.Add(new XonoticGodot.Game.Hud.ScorePanel.TeamScore(
                    team, score.ToString(System.Globalization.CultureInfo.InvariantCulture), team == myTeam));
            panel.SetTeamScores(teams);
            panel.SetMode(XonoticGodot.Game.Hud.ScorePanel.ScoreMode.Team, spectating);
        }
        else
        {
            panel.SetMode(XonoticGodot.Game.Hud.ScorePanel.ScoreMode.FreeForAll, spectating);
        }
    }

    /// <summary>Feed + toggle the mod-icons panel (QC HUD_ModIcons_SetFunc → gametype.m_modicons) from the
    /// networked per-mode status (T53). Cheap: property writes + a Visible toggle; the panel repaints itself.</summary>
    private void UpdateModIcons()
    {
        if (_fullHud is null || _client is null)
            return;
        ModIconsPanel panel = _fullHud.ModIcons;
        XonoticGodot.Net.GametypeStatusBlock.Decoded? ms = _client.LatestModeStatus;
        if (ms is null)
        {
            if (panel.Visible) panel.Visible = false;
            return;
        }
        switch (ms.Mode)
        {
            case XonoticGodot.Net.GametypeStatusBlock.Kind.ClanArena:
                panel.Mode = ModIconsPanel.ModIconsMode.ClanArena;
                panel.SetAliveCounts(ms.Alive[0], ms.Alive[1], ms.Alive[2], ms.Alive[3]);
                break;
            case XonoticGodot.Net.GametypeStatusBlock.Kind.FreezeTag:
                panel.Mode = ModIconsPanel.ModIconsMode.FreezeTag;
                panel.SetAliveCounts(ms.Alive[0], ms.Alive[1], ms.Alive[2], ms.Alive[3]);
                break;
            case XonoticGodot.Net.GametypeStatusBlock.Kind.KeyHunt:
                panel.Mode = ModIconsPanel.ModIconsMode.Keyhunt;
                panel.ObjectiveStatus = unchecked((int)ms.KeyState);
                break;
            case XonoticGodot.Net.GametypeStatusBlock.Kind.Survival:
                panel.Mode = ModIconsPanel.ModIconsMode.Survival;
                panel.SurvivalStatus = ms.MyStatus;
                break;
            default:
                panel.Mode = ModIconsPanel.ModIconsMode.None;
                break;
        }
        panel.MyTeam = ms.MyTeamIndex;
        if (ms.TeamCount > 0) panel.TeamCount = ms.TeamCount;
        bool show = panel.Mode != ModIconsPanel.ModIconsMode.None;
        if (panel.Visible != show)
            panel.Visible = show;
    }

    // [T57] last-fed accuracy generation (rebuild the dictionaries only when the server's changes). -1 = none yet.
    private int _lastFedAccuracyGen = -1;
    private readonly System.Collections.Generic.Dictionary<int, int> _accuracyById = new();          // weapon registry id → hit% (-1 never fired)
    private readonly System.Collections.Generic.Dictionary<string, float> _accuracyByNetName = new(); // weapon NetName → hit% (0 never fired)

    /// <summary>
    /// [T57] Decode the networked owner accuracy bytes (QC ENT_CLIENT_ACCURACY, owner-only) into the two HUD
    /// accuracy grids when the server's change generation moves: the scoreboard's per-id grid
    /// (<see cref="ScoreboardPanel.SetAccuracy"/>, registry-id → hit% 0..100, -1 never fired) and the weapons
    /// panel's per-NetName grid (<see cref="WeaponsPanel.SetAccuracy"/>, NetName → %). QC accuracy_byte decode:
    /// 0 → never fired (-1 for the scoreboard's sentinel / omitted from the weapons map), 255 → 100% (capped),
    /// else byte−1 = the percentage.
    /// </summary>
    private void UpdateAccuracy()
    {
        if (_fullHud is null || _client is null)
            return;
        if (_client.LocalAccuracyGeneration == _lastFedAccuracyGen)
            return;
        _lastFedAccuracyGen = _client.LocalAccuracyGeneration;

        byte[] bytes = _client.LocalAccuracyBytes;
        _accuracyById.Clear();
        _accuracyByNetName.Clear();
        int count = System.Math.Min(bytes.Length, Registry<Weapon>.Count);
        for (int id = 0; id < count; id++)
        {
            int b = bytes[id];
            int pct = b == 0 ? -1 : (b >= 255 ? 100 : b - 1); // QC accuracy_byte: 0 never fired, 255 >100%, else b-1
            _accuracyById[id] = pct;
            if (b != 0) // only weapons actually fired get a weapons-panel tint (QC skips the unfired ones)
                _accuracyByNetName[Registry<Weapon>.ById(id).NetName] = pct;
        }
        _scoreboard.SetAccuracy(_accuracyById);
        _fullHud.Weapons.SetAccuracy(_accuracyByNetName);
    }

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

        // QuickMenu (goal 8): the `quickmenu` bind (BindInput maps a key to "quickmenu") toggles the panel — the
        // proper bind-driven hook (QC the quickmenu command). A bare `quickmenu` opens/closes; `quickmenu default
        // <submenu>` would descend (left to the panel's Open overload). The panel grabs cursor/focus while open.
        if (command.Equals("quickmenu", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("quickmenu ", StringComparison.OrdinalIgnoreCase))
        {
            _fullHud?.GetPanel<XonoticGodot.Game.Hud.QuickMenuPanel>()?.Toggle();
            return;
        }

        int imp = WeaponCommandToImpulse(command);
        if (imp != 0)
        {
            _pendingImpulse = imp; // edge-triggered: SampleInput consumes it on the next command, then clears it
            // Instant local feedback: begin lowering the gun the moment a weapon-SELECT key is pressed, so the
            // switch starts visibly the same frame instead of waiting for the server round-trip (EquipNetworkedWeapon
            // then raises the new gun when the change confirms). Only weapon-select impulses (group/next/prev/last/
            // best/by-id) — NOT drop (17) or reload (20). Auto-recovers if the server denies the switch.
            if (IsWeaponSwitchImpulse(imp) && _viewModel is not null && GodotObject.IsInstanceValid(_viewModel))
                _viewModel.PlayHolster();
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

    /// <summary>
    /// True when an impulse number is a weapon-SELECT action (so the viewmodel should lower-then-raise): the
    /// group keys + next/prev/last/best (1..14, matching <see cref="WeaponCommandToImpulse"/>: groups 1..9, 10
    /// next, 11 last, 12 prev, 13 best, 14 group-0) and the direct weapon_byid_N (230+). Excludes drop (17) and
    /// reload (20), which keep the current weapon shown.
    /// </summary>
    private static bool IsWeaponSwitchImpulse(int imp)
        => (imp >= 1 && imp <= 14) || (imp >= 230 && imp <= 253);

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

    /// <summary>The quick-chat menu is showing its list (it owns the cursor like the minigame menu — mouse hover +
    /// click select rows, so the cursor must be freed and gameplay look/fire suspended while open).</summary>
    private bool QuickMenuOpen
        => _fullHud?.GetPanel<XonoticGodot.Game.Hud.QuickMenuPanel>() is { IsOpen: true };

    /// <summary>Any in-world HUD UI that should free the mouse cursor + suspend look/fire (minigame board/menu or
    /// the quick-chat menu). The bind channel stays live so the toggling bind can still close the panel.</summary>
    private bool UiOwnsCursor => MinigameMenuOpen || QuickMenuOpen;

    /// <summary>
    /// Teleporter view-snap (QC player.fixangle): after a prediction tick re-derives the carrier's .fixangle —
    /// true exactly on the tick the local player is predicted through a single-dest teleporter — snap the
    /// accumulated view to the destination facing (CSQC setproperty(VF_CL_VIEWANGLES)) and consume the flag, so
    /// the camera exits looking the way the mapper aimed; later mouse-look accumulates on top. Multi-dest
    /// teleporters aren't predicted (random exit) and stay server-authoritative.
    /// </summary>
    private void ConsumePredictedFixAngle()
    {
        if (_carrier is not null && _carrier.FixAngle)
        {
            _viewAngles = _carrier.FixAngleAngles;
            _viewAngles.X = Mathf.Clamp(_viewAngles.X, -89f, 89f);
            _carrier.FixAngle = false;
        }
    }

    /// <summary>
    /// Per-render-frame local fire prediction + feedback, decoupled from the 1/72 s input cadence. With
    /// cl_predictfire (default on) a client-side REFIRE CLOCK fires the view-model muzzle flash + a local fire
    /// SOUND on every shot the instant the player fires (first on the press edge, then every <c>refire</c> s while
    /// held), gated on the active weapon / alive / ammo — so sustained fire feels instant; the local player's own
    /// NETWORKED fire-sound + muzzle-flash are suppressed elsewhere so they aren't doubled. With cl_predictfire 0
    /// it falls back to a single muzzle flash on the press edge (the networked sustained FX play). Either way each
    /// press edge sets a sub-tick latch (<see cref="_attackLatch"/>) so a tap shorter than one input tick still
    /// reaches the server. Inert while the in-game menu / console / minigame menu owns input.
    /// </summary>
    private void UpdateLocalFireFeedback(float dt)
    {
        _fireClock += dt;

        bool active = !GetTree().Paused && !ConsoleState.IsOpen && !UiOwnsCursor;
        if (!active)
        {
            // Input is owned elsewhere (in-game menu / console / minigame / quickmenu): drop the edge state, the tap
            // latch and the refire clock so nothing fires on resume (matches SampleInput's in_releaseall edge).
            _attackHeld = _attack2Held = false;
            _attackLatch = _attack2Latch = false;
            _firePredictActive = false;
            return;
        }

        bool a1 = BindTable.AttackHeld;
        if (a1 && !_attackHeld)
            _attackLatch = true; // sub-tick latch for the server (independent of FX prediction)

        if (_predictFire && a1 && TryActivePrimaryFire(out string fireSound, out float refire)
            && !LocalDeadNow() && HasAmmoNow())
        {
            // Refire clock: first shot the frame the button goes down (or whenever the clock catches up), then one
            // shot per `refire` s while held. Bound the catch-up so a frame hitch can't burst a pile of shots.
            if (!_firePredictActive)
            {
                _firePredictActive = true;
                _nextFireTime = _fireClock; // fire immediately this frame
            }
            if (_fireClock >= _nextFireTime)
            {
                PredictFireShot(fireSound);
                float step = Mathf.Max(refire, 0.02f);
                _nextFireTime += step;
                if (_nextFireTime < _fireClock - step) _nextFireTime = _fireClock; // don't accumulate a backlog
            }
        }
        else
        {
            _firePredictActive = false;
            // cl_predictfire off (or this weapon isn't predicted): a single muzzle flash on the press edge (Phase 1).
            if (!_predictFire && a1 && !_attackHeld)
            {
                _hud?.PulseFire();
                _viewModel?.Fire();
            }
        }
        _attackHeld = a1;

        // Secondary fire: latch the press so its tap reaches the server (secondary FX stay networked for now).
        bool a2 = BindTable.Attack2Held;
        if (a2 && !_attack2Held)
            _attack2Latch = true;
        _attack2Held = a2;
    }

    /// <summary>Play one predicted local shot: the view-model muzzle flash + recoil, the HUD crosshair pulse, and
    /// the weapon's primary fire sound at the predicted eye (auto-stack channel so rapid fire doesn't cut off).
    /// Source net-id = the local player so the matching networked copy is dropped in <see cref="OnSoundReceived"/>.</summary>
    private void PredictFireShot(string fireSound)
    {
        _hud?.PulseFire();
        _viewModel?.Fire();
        if (!string.IsNullOrEmpty(fireSound) && _render is not null && _client is not null)
            _render.OnSound(fireSound, _client.PredictedOrigin, 1f, 0.5f,
                (int)XonoticGodot.Common.Services.SoundChannel.WeaponAuto, _client.LocalNetId, 1f);
    }

    /// <summary>The active weapon's primary fire sound + refire interval, or false when it can't be predicted (no
    /// active weapon, or a loop/grapple weapon absent from <see cref="WeaponFireSounds"/> → its networked sound
    /// plays normally and is NOT suppressed).</summary>
    private bool TryActivePrimaryFire(out string fireSound, out float refire)
    {
        fireSound = "";
        refire = 0.1f;
        if (_client is null || _client.ActiveWeaponId < 0)
            return false;
        XonoticGodot.Common.Gameplay.Weapon w = XonoticGodot.Common.Gameplay.Weapons.ById(_client.ActiveWeaponId);
        if (w is null)
            return false;
        fireSound = WeaponFireSounds.PrimaryFor(w.NetName);
        if (string.IsNullOrEmpty(fireSound))
            return false;
        refire = w.RefireFor(XonoticGodot.Common.Gameplay.FireMode.Primary);
        if (refire <= 0f) refire = 0.1f;
        return true;
    }

    /// <summary>Whether the local player is dead (stop predicting fire). Listen server reads the host Player; a
    /// pure client derives it from networked health after the first spawn (matching the death-cam gate).</summary>
    private bool LocalDeadNow()
        => LocalServerPlayer is { } self ? self.IsDead : (_everAlive && _client is { } c && c.Health <= 0);

    /// <summary>Whether the active weapon has ammo to fire — else predicting would play phantom shots after the
    /// magazine empties. Uses the listen-server host player's live resources; a pure remote client has no server
    /// player here, so it skips the ammo gate (accepts a shot or two of over-prediction at ammo-out).</summary>
    private bool HasAmmoNow()
    {
        if (LocalServerPlayer is not { } p || _client is null || _client.ActiveWeaponId < 0)
            return true;
        XonoticGodot.Common.Gameplay.Weapon w = XonoticGodot.Common.Gameplay.Weapons.ById(_client.ActiveWeaponId);
        if (w is null || w.AmmoType == XonoticGodot.Common.Gameplay.ResourceType.None)
            return true; // infinite-ammo weapon (e.g. blaster)
        return XonoticGodot.Common.Gameplay.Resources.GetResource(p, w.AmmoType) > 0f;
    }

    private InputCommand SampleInput()
    {
        // Re-capture on click if the user released the mouse (e.g. after alt-tab); never while the tree is
        // paused (the in-game menu), the console is open, or the minigame menu is up — those own the cursor.
        if (!GetTree().Paused && !ConsoleState.IsOpen && !UiOwnsCursor
            && Input.MouseMode != Input.MouseModeEnum.Captured && Input.IsMouseButtonPressed(MouseButton.Left))
            Input.MouseMode = Input.MouseModeEnum.Captured;

        // Gameplay input is inert while the in-game menu is up (paused), the console is open, OR a cursor-owning
        // HUD UI (minigame menu/board or the quick-chat menu) is open. On the edge into inactive, drop all held
        // buttons (DP in_releaseall) so a key held at that moment doesn't stay down once input resumes. The view
        // angles hold their last value, so the camera stays put.
        bool active = !GetTree().Paused && !ConsoleState.IsOpen && !UiOwnsCursor;
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

            // Sub-tick fire latch (set per render frame in UpdateLocalFireFeedback): OR a press that landed since
            // the last sampled command into THIS command — so a tap shorter than one input tick still fires —
            // then clear it. The local muzzle flash / recoil / HUD pulse themselves are now driven per-frame in
            // UpdateLocalFireFeedback (CSQC W_MuzzleFlash on the local attack), no longer gated to this 1/72 s sample.
            if (_attackLatch) { buttons |= InputButtons.Attack; _attackLatch = false; }
            if (_attack2Latch) { buttons |= InputButtons.Attack2; _attack2Latch = false; }
        }

        // While FOLLOWING a player (spectatee_status > 0), the server keeps us MOVETYPE_NONE and glues us to the
        // spectatee — so suppress local movement (don't predict our own walk, which would fight the follow-cam),
        // but KEEP the buttons: +attack cycles the spectatee and +attack2 drops to free-fly on the server. (A
        // free-fly observer, spectatee_status == own id, still moves — server + client agree, no drift.)
        if (_client is not null && _client.SpectatingNetId != 0)
            forward = side = up = 0f;

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
            // TicRate in legacy mode; the real clamped frame dt in per-frame mode (set before SendInput above).
            DeltaTime = _inputDeltaTime,
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

        // Follow-cam: while spectating a specific player (spectatee_status > 0), render from THAT player's
        // interpolated pose + view angles, not the local predicted origin. The server glues us to the spectatee
        // (MOVETYPE_NONE + SpectateCopy) so the owner-state origin already tracks them, but the local predictor
        // would still replay our inputs and walk the camera off them; sourcing the followed entity's snapshot pose
        // directly (and slaving the view angles, QC SpectateCopy fixangle) is the robust fix. Falls through to the
        // predicted path if the entity isn't known yet (then the zeroed spectate input keeps it at the glued origin).
        if (_client.SpectatingNetId != 0
            && _client.SampleRemote(_client.SpectatingNetId, _client.LatestServerTime, out NVec3 specOrg, out NVec3 specAng))
        {
            var sst = new Client.FirstPersonView.ViewState
            {
                OriginQuake = specOrg,
                VelocityQuake = NVec3.Zero,
                ViewAnglesQuake = specAng,   // see what the spectated player sees
                IsDead = false,              // following a live player — no death-cam pullback
                EyeHeightZ = EyeHeight,
            };
            _view.UpdateView(_camera, sst, dt);
            return;
        }

        float now = _renderClock; // the clock the reconciler armed the prediction-error decay with (see _Process)
        NVec3 predicted = _client.PredictedOrigin + _client.PredictionErrorOffset(now);

        // A DEAD local player's body is frozen at the death spot (PlayerPhysics.Move bails on IS_DEAD). The server
        // still networks the velocity it died with, so over a void that's a large downward speed — extrapolating
        // the eye by it each render frame, then snapping back when the input tic drains, shakes the death-cam (the
        // "view shakes over a void" report). The corpse isn't predicting movement, so treat its velocity as zero
        // for all render purposes: no sub-tic eye extrapolation and no velocity-driven view effects while dead.
        // (net path has no MaxHealth, so dead = Health<=0 — but ONLY after the first spawn, so the pre-spawn
        // observer 0 doesn't engage the death-cam at the world origin.)
        bool localDead = _everAlive && _client.Health <= 0;
        NVec3 viewVelocity = localDead ? NVec3.Zero : _client.PredictedVelocity;

        // Sub-tic view smoothing (DP's partial final movement frame): the predicted origin only advances when an
        // input tic is drained, so it moves in discrete 1/72 s (~13.9 ms) steps while the camera renders at the
        // display rate. At any framerate that isn't a multiple of 72 the per-frame step count alternates (0/1, or
        // 1/2), which reads as a faint shimmer/stutter even on a locked-fps machine. DP hides this by simulating a
        // partial final frame each render; we approximate it by extrapolating the eye forward over the as-yet-
        // unsimulated remainder (_inputAccum, always < one tic) along the PREDICTED velocity. That velocity is the
        // post-slide-move value (its into-surface component already clipped by PushEntity), so extrapolating along
        // it slides parallel to walls/floors rather than poking through them, and the next real tic lands within a
        // few units of the extrapolated eye — so the error smoother never engages. Render-only: it does not touch
        // the authoritative predicted state, the input ring, or anything the server/reconciler sees. Suppressed
        // while dead (viewVelocity is zeroed above) so the frozen corpse's death-fall speed can't bob the eye.
        predicted += viewVelocity * _inputAccum;

        // Subtract the stair-smooth Z so the camera glides over steps (cl_movement stairsmooth). Driven by the
        // REAL frame delta (dt), NOT the server-synced render clock — the rebasing clock's 1/72-quantized jumps
        // made the catch-up jitter up/down (the reference advances stair smoothing by real frametime). Refresh the
        // tunables from the live cvars first so cl_stairsmoothspeed / the port anti-jitter knobs apply immediately
        // and the lag clamp tracks the live sv_stepheight (a non-stock physics preset can change it).
        XonoticGodot.Common.Services.ICvarService cv = Api.Cvars;
        _client.ConfigureStairSmoothing(
            smoothSpeed: CvarOr(cv, "cl_stairsmoothspeed", 200f),
            stepHeight:  CvarOr(cv, "sv_stepheight", 31f),
            snapSpeed:   CvarOr(cv, "cl_stairsmooth_snapspeed", 30f),
            catchupTime: CvarOr(cv, "cl_stairsmooth_catchuptime", 0.1f));
        predicted.Z -= _client.PredictedStairOffset(dt);

        var st = new Client.FirstPersonView.ViewState
        {
            OriginQuake = predicted,
            VelocityQuake = viewVelocity, // zeroed while dead (see localDead above) so a frozen corpse has no bob/sway
            // QC `view_angles += view_punchangle` (cl_player.qc): the recoil kick is added to the rendered VIEW
            // only — _viewAngles (the aim sent to the server) stays unpunched so shots still land on the crosshair.
            ViewAnglesQuake = _viewAngles + _client.PunchAngle,
            IsDead = localDead,
            // Eye drops while crouched: the predicted carrier carries the live view offset (PlayerPhysics.UpdateCrouch
            // sets ViewOfs to the crouch/standing value each predicted tick, QC STAT(PL_CROUCH_VIEW_OFS)/PL_VIEW_OFS).
            EyeHeightZ = _carrier?.ViewOfs.Z ?? EyeHeight,
        };
        _view.UpdateView(_camera, st, dt);
    }

    /// <summary>Read a float cvar, falling back to <paramref name="fallback"/> only when it is UNSET (empty string),
    /// so a deliberately-configured 0 (e.g. <c>cl_stairsmooth_catchuptime 0</c> = adaptive off) is honoured.</summary>
    private static float CvarOr(XonoticGodot.Common.Services.ICvarService c, string name, float fallback)
    {
        string s = c.GetString(name);
        return s.Length == 0 ? fallback : c.GetFloat(name);
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

        // Drop the local player's OWN primary fire sound (cl_predictfire): we already played it locally on the
        // refire clock, so the networked echo would double it. Keyed by source net-id + a predicted fire sample so
        // it never suppresses footsteps/pain/reload/secondary (other channels/samples), and remote players are
        // unaffected (they don't match LocalNetId). A non-predicted weapon (loops/grapple) isn't in the set → its
        // networked fire sound plays normally.
        if (_predictFire && _client is not null && e.SourceNetId == _client.LocalNetId
            && WeaponFireSounds.IsPredicted(e.Sample))
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
    /// + menu through the client coordinator, then refresh the MinigameHelp panel's active game (goal 4). The
    /// coordinator sets <see cref="MinigameClient.Active"/> from the envelope first; we read its netname (e.g.
    /// "ttt_3") into <see cref="XonoticGodot.Game.Hud.MinigameHelpPanel.ActiveMinigame"/>, which self-blanks the
    /// help panel when null (no active game). The help panel only enters the draw set under PanelShow.Minigame.</summary>
    private void OnMinigameStateReceived(MinigameNetState.Envelope env)
    {
        _minigame?.OnEnvelope(env);
        if (_fullHud?.GetPanel<XonoticGodot.Game.Hud.MinigameHelpPanel>() is { } help)
            help.ActiveMinigame = _minigame?.Active?.NetName;
    }

    /// <summary>Feed each ServerPrint line to the minigame coordinator's session-list parser (the
    /// <c>cmd minigame list-sessions</c> reply populates the Join menu). Harmless for non-minigame prints — the
    /// coordinator only keeps single-token "&lt;game&gt;_&lt;n&gt;" lines.</summary>
    private void OnServerPrintForMinigame(string line) => _minigame?.NoteSessionListLine(line);

    /// <summary>
    /// Feed each ServerPrint line into the ChatPanel scrollback (QC HUD panel #12). The port has no dedicated chat
    /// net channel: server chat (<c>say</c>/<c>say_team</c>) is broadcast via <c>ServerNet.BroadcastPrint</c> and
    /// arrives here on the SAME print stream. Empty lines are ignored by <see cref="XonoticGodot.Game.Hud.ChatPanel.AddLine"/>;
    /// the panel honours <c>con_chattime</c>/<c>con_chatsize</c> and self-blanks when empty. Drops the single-token
    /// minigame session-list replies ("ttt_3") so they don't clutter the chat area.
    /// </summary>
    private void OnServerPrintForChat(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        // Skip the bare "<game>_<n>" session-list reply tokens (handled by OnServerPrintForMinigame).
        string trimmed = line.Trim();
        if (!trimmed.Contains(' ') && _minigame is not null && trimmed.IndexOf('_') > 0)
            return;
        _fullHud?.GetPanel<XonoticGodot.Game.Hud.ChatPanel>()?.AddLine(line);
    }

    // =====================================================================================
    //  Asset resolution (shared with GameDemo's wiring; built per entity on first sight)
    // =====================================================================================

    private PlayerModel? ResolvePlayerModel(Entity e)
    {
        if (_assets is null || string.IsNullOrEmpty(e.Model)
            || e.Model.IndexOf("player", StringComparison.OrdinalIgnoreCase) < 0)
            return null;
        // A model the async resolve already settled as non-skeletal: behave like the old sync path's null
        // return so ClientWorld falls through to the MD3/static-prop attach.
        if (_nonSkeletalPlayerModels.Contains(e.Model))
            return null;

        // Wave 1 (perf report §9.4): do NOT parse+build synchronously here. The first snapshot of a bot match
        // carries EVERY player at once, and building all ~25 MB skeletal models in that one frame was a
        // ~154 MB allocation burst → a 100–150 ms blocking gen2 GC (the worst release-build hitch). Return a
        // placeholder shell immediately; the IQM+sidecar parse runs on the thread pool and the Godot build
        // lands under the streamer's per-frame budget — one model per frame, High priority (ahead of idle warm).
        return StreamSkeletalInto(e, e.Model, (int)e.Skin, $"player#{e.Index}", forced: false);
    }

    /// <summary>
    /// Shared async core for the live player-model path (the on-demand <see cref="ResolvePlayerModel"/> and the
    /// FORCEMODEL <see cref="BuildForcedPlayerModel"/>): return a gray placeholder <see cref="PlayerModel"/> shell
    /// immediately and stream the IQM parse off-thread + the Godot build under the streamer's per-frame budget,
    /// filling the shell in <see cref="DeliverPlayerModel"/>. <paramref name="forced"/> routes the delivery's
    /// fallback branches through the forced-miss memo (FORCEMODEL parity) instead of the own-model memo. When no
    /// streamer/assets are wired (should not happen on the live path) it runs the old synchronous load and returns
    /// the filled shell (or null on miss).
    /// </summary>
    private PlayerModel? StreamSkeletalInto(Entity e, string model, int skin, string shellName, bool forced)
    {
        var pm = new PlayerModel { Name = shellName };
        pm.ShowPlaceholder();
        if (_streamer is null || _assets is not { } assets)
        {
            // No streamer (shouldn't happen on the live path) — fall back to the old synchronous load.
            AssetLoader.SkeletalModelParts? parts = _assets?.LoadSkeletalModel(model, skin);
            if (parts is null) { pm.QueueFree(); return null; }
            pm.Setup(parts.Iqm, parts.Root, parts.Groups, parts.Info);
            if (!pm.Active) { pm.QueueFree(); return null; }
            return pm;
        }

        _streamer.Request(
            // Off-thread: pure-C# parse (now incl. the animation library, §12.3-1). Boxed in a non-null wrapper
            // because the streamer drops null results silently — but a FAILED parse must still reach the main
            // thread to trigger the fall-back attach.
            () => new SkeletalParseBox(assets.ParseSkeletalModel(model, skin)),
            box =>
            {
                if (box.Parse is null)
                {
                    DeliverPlayerModel(pm, e, model, skin, null, forced);   // miss → fall-back attach, as before
                    return;
                }
                // (§12.3-1) Stage the texture pipeline BEFORE the delivery: one streamer job per material
                // (worker pre-decodes its images, main thread only uploads), then the now-cheap assembly.
                // The old single delivery paid ~395 ms of synchronous decode+upload in one frame.
                EnqueueStagedSkeletalBuild(assets, box.Parse,
                    XonoticGodot.Game.Client.BackgroundAssetStreamer.Priority.High, $"player model {model}",
                    () => DeliverPlayerModel(pm, e, model, skin, box.Parse, forced));
            },
            XonoticGodot.Game.Client.BackgroundAssetStreamer.Priority.High,
            label: $"player model {model} (parse)");
        return pm;
    }

    /// <summary>Non-null off-thread wrapper for a (possibly failed) skeletal parse — see ResolvePlayerModel.</summary>
    private sealed record SkeletalParseBox(AssetLoader.SkeletalModelParse? Parse);

    /// <summary>
    /// (§12.3-1) Enqueue the staged main-thread half of a skeletal-model build: one streamer job per effective
    /// material — its worker phase pre-decodes the material's images (<see cref="AssetSystem.PredecodeMaterialTextures"/>),
    /// its main phase resolves the material (now upload-only) — then <paramref name="onAllMaterialsReady"/> runs
    /// the final (cheap) assembly. The streamer's budget naturally spreads the jobs across frames, so the old
    /// ~600-750 ms monolithic delivery becomes a series of ~5-30 ms stages. Job order is irrelevant
    /// (ResolveMaterial caches by name); the count-down gate fires the assembly exactly once, on the main thread.
    /// </summary>
    private void EnqueueStagedSkeletalBuild(AssetLoader loader, AssetLoader.SkeletalModelParse parse,
        XonoticGodot.Game.Client.BackgroundAssetStreamer.Priority priority, string label, Action onAllMaterialsReady)
    {
        List<string> mats = AssetLoader.EffectiveMaterials(parse);
        if (_streamer is null || mats.Count == 0)
        {
            onAllMaterialsReady();
            return;
        }
        int remaining = mats.Count;
        foreach (string mat in mats)
        {
            string m = mat;
            _streamer.Request(
                () => { loader.Assets.PredecodeMaterialTextures(m); return m; },   // worker: VFS read + decode
                _ =>
                {
                    using (XonoticGodot.Game.Client.FrameProfiler.Scope("iqm.materials"))
                        loader.Assets.ResolveMaterial(m);                          // main: upload + material build
                    if (--remaining == 0)
                        onAllMaterialsReady();
                },
                priority,
                label: $"{label} tex {m}");
        }
    }

    /// <summary>The "model#skin" memo key used by <see cref="_unresolvableForcedModels"/> for a forced model.</summary>
    private static string ForcedKey(string model, int skin) => model + "#" + skin;

    /// <summary>
    /// Main-thread delivery of a streamed player-model parse (budgeted by the streamer — one heavy build per
    /// frame): validate the request is still current, build the Godot scene, and fill the placeholder shell via
    /// <see cref="PlayerModel.Setup"/>. A stale/failed resolve re-runs the attach through
    /// <see cref="ClientWorld.RebuildEntityModel"/> so the entity ends up exactly where the old synchronous
    /// path would have put it (the MD3/static fall-through, or a resolve of the changed model).
    /// </summary>
    private void DeliverPlayerModel(PlayerModel pm, Entity e, string model, int skin, AssetLoader.SkeletalModelParse? parse, bool forced)
    {
        if (_assets is null)
            return;
        // The shell was torn down while the parse was in flight (entity removed / world reset / model rebuilt).
        if (!GodotObject.IsInstanceValid(pm) || pm.IsQueuedForDeletion())
            return;
        if (e.IsFreed)
            return; // entity gone — OnEntityRemove tears the node (and shell) down

        // Model/skin changed while parsing: re-attach against the CURRENT model (issues a fresh resolve). This gate
        // keys on the entity's NETWORKED model, which is the right staleness check for the OWN-model resolve but
        // intentionally differs from a FORCED name — so it is skipped for forced deliveries (whose live-ness is the
        // pm-valid + e.IsFreed gates above; a mid-flight cl_forceplayermodels toggle is handled by RebuildEntityModel
        // re-running TryAttachForcedModel, which re-reads the cvars).
        if (!forced && (!string.Equals(e.Model, model, StringComparison.OrdinalIgnoreCase) || (int)e.Skin != skin))
        {
            _render.RebuildEntityModel(e);
            return;
        }

        if (parse is null)
        {
            // Not an IQM (or unreadable): remember (so the rebuild's resolve doesn't re-stream), then fall back —
            // own-model path for the live resolve, or the cached non-IQM forced node (FORCEMODEL parity) for a
            // forced one. The memo is populated BEFORE RebuildEntityModel to terminate the re-attach loop.
            ForgetSkeletal(model, skin, forced);
            _render.RebuildEntityModel(e);
            return;
        }

        AssetLoader.SkeletalModelParts? parts = _assets.BuildSkeletalModel(parse);
        if (parts is null)
        {
            ForgetSkeletal(model, skin, forced);
            _render.RebuildEntityModel(e);
            return;
        }

        pm.Setup(parts.Iqm, parts.Root, parts.Groups, parts.Info);
        if (!pm.Active)
        {
            // Parsed as IQM but no poseable skeleton — match the sync path's fall-through.
            ForgetSkeletal(model, skin, forced);
            _render.RebuildEntityModel(e);
            return;
        }

        // Tint immediately (team colormap / forcecolors) — the per-frame appearance pass keeps it fresh, but
        // seeding now avoids one untinted frame, mirroring TryAttachModel's eager seed.
        _render.SeedAppearance(e);
        XonoticGodot.Common.Diagnostics.Log.Trace(
            $"[stream] {(forced ? "forced " : "")}player model '{model}' (skin {skin}) built for ent {e.Index} — placeholder swapped");
    }

    /// <summary>
    /// Record that a streamed skeletal resolve settled as non-skeletal/failed, in the memo the matching resolver
    /// checks BEFORE re-streaming — so the <see cref="ClientWorld.RebuildEntityModel"/> re-attach terminates. For a
    /// forced model the key is name+skin specific (the forced name differs from the entity's own model); for the
    /// own-model resolve the key is the entity's model name (the existing Wave-1 scheme). Always invoked before
    /// RebuildEntityModel in every forced fallback branch (the loop-prevention invariant).
    /// </summary>
    private void ForgetSkeletal(string model, int skin, bool forced)
    {
        if (forced)
            _unresolvableForcedModels.Add(ForcedKey(model, skin));
        else
            _nonSkeletalPlayerModels.Add(model);
    }

    /// <summary>
    /// Build a FORCED player model (QC <c>cl_forcemyplayermodel</c> / <c>cl_forceplayermodels</c> swap) for
    /// <see cref="ClientWorld.ForcedModelResolver"/>. Mirrors <see cref="ResolvePlayerModel"/>: a skeletal forced
    /// model returns a gray placeholder <see cref="PlayerModel"/> shell immediately and streams the IQM parse+build
    /// through the same High-priority <see cref="BackgroundAssetStreamer"/> path (no synchronous build stall on a
    /// forced-model snapshot). Once the async resolve has settled a model as non-skeletal or a true miss, its
    /// "model#skin" key lives in <see cref="_unresolvableForcedModels"/>: the next attach (driven by
    /// <see cref="ClientWorld.RebuildEntityModel"/>) takes the memo branch and returns the cached non-IQM node via
    /// <see cref="AssetLoader.LoadModel"/> (FORCEMODEL parity — a forced MD3/DPM still wins over the entity's own
    /// model), or null on a genuine VFS miss (QC <c>fexists</c>-miss → the client keeps the entity's own model).
    /// </summary>
    private Node3D? BuildForcedPlayerModel(Entity e, string model, int skin)
    {
        if (_assets is null || string.IsNullOrEmpty(model))
            return null;
        // Already settled as non-skeletal/miss: don't re-stream (that re-attach loop is the highest risk). Return
        // the cached non-IQM forced node (FORCEMODEL parity) or null (true miss → entity's own model). LoadModel is
        // cached (AssetLoader._modelCache), so the rebuild terminates here without rebuilding.
        if (_unresolvableForcedModels.Contains(ForcedKey(model, skin)))
            return _assets.LoadModel(model, skin);
        // Otherwise stream the skeletal forced model exactly like the live player path: placeholder shell now,
        // off-thread parse + budgeted main build, DeliverPlayerModel (forced) fills the shell or records the memo.
        return StreamSkeletalInto(e, model, skin, $"forced_{System.IO.Path.GetFileNameWithoutExtension(model)}", forced: true);
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

    /// <summary>Cvars the listen-server boot sequence (or the map's worldspawn) authors itself — a console override
    /// of these must NOT be backfilled across a map change, or it would clobber the new map's gravity, the host's
    /// spectate/bot setup, the chosen match limits, or the map name. The limits ride the campaign-guarded
    /// <see cref="CopyCvarIfSet"/> path above; sv_gravity is owned by each map's worldspawn.</summary>
    private static readonly System.Collections.Generic.HashSet<string> BootAuthoredCvars = new(System.StringComparer.Ordinal)
    {
        "mapname", "sv_gravity", "sv_spectate", "timelimit", "fraglimit", "bot_number", "skill",
    };

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

        // The map's real Xonotic skybox (DP R_LoadSkyBox semantics), falling back to the procedural sky when
        // the map declares none. Same path GameDemo.AddLighting uses; gated on the BSP the client loaded.
        Sky sky = XonoticGodot.Game.Loaders.SkyboxLoader.TryBuild(_bsp, _assets?.Assets)
                  ?? new Sky { SkyMaterial = new ProceduralSkyMaterial() };

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = sky,
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightEnergy = 0.6f,

            // Tonemap stays LINEAR. The world (LightmapShader) and skin shaders output colour hand-tuned to
            // round-trip with Godot's Linear mapper, and the content's dynamic range is low (overbright tops out
            // ~lightmap_scale 2.0). A filmic curve (ACES/Filmic) crushes the mid-tones and shadows here — verified
            // on stormkeep — so it's left off. Switch to ToneMapper.Aces (TonemapWhite ~1.5) for a punchier,
            // less faithful "modern" grade if desired.
            TonemapMode = Godot.Environment.ToneMapper.Linear,

            // Bloom on genuinely-bright pixels only (light fixtures, lava, _glow pages, additive particles,
            // muzzle/explosion flashes). Threshold ~1.0 keeps ordinary lit walls from washing out — this is
            // the equivalent of the r_bloom post pass Darkplaces ships.
            GlowEnabled = true,
            GlowIntensity = 0.8f,
            GlowStrength = 1.0f,
            GlowBloom = 0.05f,
            GlowHdrThreshold = 1.0f,
            GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Screen,
        };

        // Map-declared fog (worldspawn "fog"), restoring the mapper's intended atmosphere + depth cue.
        XonoticGodot.Game.MapLoader.ApplyFog(env, _bsp);

        // Map-declared colour tint baseline (worldspawn "_map_tint"/"_scene_tint"); identity if unset. Live cvars
        // and the C# API can override it at runtime (XonoticGodot.Game.WorldTint).
        XonoticGodot.Game.WorldTint.ApplyWorldspawn(_bsp);

        AddChild(new WorldEnvironment { Name = "WorldEnvironment", Environment = env });
    }

    // (the old hardcoded BotName table moved into BotPopulation's bots.txt fallback — T39)

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
