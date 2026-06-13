using Godot;
using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Game.Console;
using XonoticGodot.Game.Menu;
using XonoticGodot.Server;

namespace XonoticGodot.Game;

/// <summary>
/// The application shell — the C# successor to the engine's menu/client lifecycle (DP <c>menu_restart</c> +
/// the <c>CL_</c> connect/disconnect flow). It owns the single front-end <see cref="MenuRoot"/> and the live
/// match (<see cref="XonoticGodot.Game.Net.NetGame"/> — a listen server or a remote client), and switches
/// between them: boot into the main menu, start a match from the Create/Singleplayer screens, drop the in-game
/// menu on Escape (Xonotic's behavior), and tear the match down on Disconnect. It also performs the one-time
/// client bootstrap (<see cref="MenuState.Boot"/>),
/// applies the saved settings to the engine, and wires the <see cref="MenuCommand"/> host hooks + the menu's
/// StartGame/Connect callbacks to real actions.
///
/// Pause model: opening the in-game menu pauses the scene tree. The shell and its menu layer are
/// <see cref="Node.ProcessModeEnum.Always"/> so they keep running (and the shell keeps receiving Escape) while
/// the match — created <see cref="Node.ProcessModeEnum.Pausable"/> — freezes. That cleanly stops movement,
/// the sim, and mouse-look without per-system pause flags.
/// </summary>
public partial class Shell : Node
{
    /// <summary>The game data dir to mount (forwarded to <see cref="MenuState.Boot"/> + each match).</summary>
    [Export] public string DataPath { get; set; } = "res://assets/data";

    /// <summary>If set at boot, skip the menu and start straight into this map (CI/dev; the smoke test uses it).</summary>
    public string? BootMap { get; set; }

    /// <summary>If set at boot (CLI <c>--model &lt;name&gt;</c>), skip the menu and open the no-net player-model
    /// viewer on that hero model (windowed visual-QA capture; <c>tools/visual-qa.sh</c> drives the sweep).</summary>
    public string? BootModel { get; set; }

    /// <summary>The gametype short code to boot <see cref="BootMap"/> with (drives the per-gametype map-entity
    /// filter — which conditional walls appear). Defaults to Deathmatch.</summary>
    public string BootGametype { get; set; } = "dm";

    /// <summary>If set at boot, open this menu sub-screen for visual capture (dev/CI screenshot of one dialog).</summary>
    public string? DebugScreen { get; set; }

    /// <summary>If set at boot (CLI <c>--connect &lt;addr&gt;</c>), skip the menu and join this server.</summary>
    public string? ConnectAddress { get; set; }

    /// <summary>If true at boot (CLI <c>--host [map]</c>), skip the menu and start a listen server on
    /// <see cref="BootMap"/> (a flat floor when empty) then self-connect a networked client.</summary>
    public bool BootHost { get; set; }

    /// <summary>Bot count for the <c>--host</c> listen server (CLI <c>--bots N</c>); 0 = no bots.</summary>
    public int BootBots { get; set; }

    private CanvasLayer _menuLayer = null!;
    private MenuRoot _menu = null!;
    private ModelViewer? _viewer;
    private XonoticGodot.Game.Net.NetGame? _netGame;
    private ConsoleOverlay _console = null!;
    private bool _paused;
    private CanvasLayer? _loadingLayer;
    private LoadingScreen? _loadingScreen;

    /// <summary>Apply any <c>--cvar NAME VALUE</c> command-line overrides into the shared store (repeatable; each
    /// <c>--cvar</c> token consumes the next two args). For test/automation/A-B runs that need to pin a cvar at
    /// boot — e.g. <c>vid_vsync</c> / <c>cl_frameprofiler</c> — without touching a config file.</summary>
    private static void ApplyCvarOverrides()
    {
        string[] args = OS.GetCmdlineArgs();
        for (int i = 0; i + 2 < args.Length; i++)
        {
            if (args[i] != "--cvar")
                continue;
            MenuState.Cvars.Set(args[i + 1], args[i + 2]);
            XonoticGodot.Common.Diagnostics.Log.Info($"[shell] --cvar {args[i + 1]} = \"{args[i + 2]}\"");
            i += 2;
        }
    }

    public override void _Ready()
    {
        // The shell (and, by inheritance, the menu layer) keeps processing while the tree is paused, so Escape
        // still toggles the in-game menu and the menu UI stays interactive over the frozen match.
        ProcessMode = ProcessModeEnum.Always;

        // --- one-time client bootstrap: mount assets, load the cvar config tree + user prefs, publish Api ---
        MenuState.Boot(DataPath);
        // --cvar NAME VALUE (repeatable): pin a cvar at boot AFTER the config tree loads and BEFORE ApplyAll, so a
        // test/automation/A-B run can force e.g. `--cvar vid_vsync 2 --cvar cl_frameprofiler 2` without editing a
        // config. Overrides the loaded config.cfg value (last writer wins), exactly like a console `set` would.
        ApplyCvarOverrides();
        ClientSettings.ApplyAll();

        WireCommandHooks();

        // --- in-game developer console (backtick), on its own high CanvasLayer above the menu/HUD. Shares the
        //     boot command interpreter (MenuState.Interp) so typed lines interpret exactly like a .cfg, routes
        //     gameplay commands to the live world (in-process listen server) or the remote server, and recaptures
        //     the mouse on close only when a match is live and not paused. Created before any boot-into-match path
        //     below so an early `--host`/`--connect` already has a console. ---
        _console = new ConsoleOverlay { Name = "Console" };
        AddChild(_console);
        _console.Initialize(
            MenuState.Interp!,                       // non-null after MenuState.Boot
            MenuState.Cvars,
            LocalRouteCommand,                       // gameplay cmd → in-process listen-server world (null on a pure client)
            RouteRemoteCommand,                      // pure-client fallback: DP clc_stringcmd to the remote server (or print a hint at the menu)
            () => MatchRunning && !_paused);         // recapture the mouse on close only inside a live match

        // Keybind system: the runtime key→command table is already seeded by MenuState.Boot (above) from the
        // canonical binds-xonotic.cfg via the bind sink (BindInput.RegisterBindCommands), with the user's saved
        // `bind` lines layered on top — one source of truth shared by `bind`/gameplay input. Here we only wire
        // the runtime hook: release all held buttons whenever the console opens (DP in_releaseall).
        BindInput.Install();

        // Dev/CI: `--menu-screen nexposee:<Title>` opens that panel inside the nexposee on boot (vs the plain
        // `--menu-screen settings` which pushes a framed dialog). Consumed by MainMenu; clear it so the
        // OpenDebugScreen path below doesn't also try to push an unknown screen.
        if (DebugScreen is { } ds && ds.StartsWith("nexposee:"))
        {
            MainMenu.AutoOpen = ds["nexposee:".Length..];
            DebugScreen = null;
        }

        // --- the front-end menu, on a CanvasLayer above any 3D match ---
        _menuLayer = new CanvasLayer { Name = "MenuLayer", Layer = 10 };
        AddChild(_menuLayer);
        _menu = new MenuRoot();
        _menu.ResumeRequested += Resume;
        _menu.DisconnectRequested += ReturnToMainMenu;
        _menuLayer.AddChild(_menu); // MenuRoot._Ready shows the main menu

        Input.MouseMode = Input.MouseModeEnum.Visible;

        // Optional: boot straight into a match (smoke test / dev), bypassing the menu.
        if (!string.IsNullOrWhiteSpace(ConnectAddress))
            ConnectToServer(ConnectAddress!);               // --connect <addr>: join a real server
        else if (BootHost)
            StartListenServer(new MatchConfig { Map = BootMap ?? "", Gametype = BootGametype, BotCount = BootBots }); // --host [map]
        else if (!string.IsNullOrWhiteSpace(BootMap))
            // --map: a quick local match on the chosen map — a 0-bot listen server (the SAME bring-up Create
            // Game / --host use, just unattended + bot-free). The old no-net GameDemo path is gone; one entry
            // path now, so a test/capture run exercises exactly what real play does.
            StartListenServer(new MatchConfig { Map = BootMap!, Gametype = BootGametype, BotCount = 0 });
        else if (!string.IsNullOrWhiteSpace(BootModel))
            StartModelViewer(BootModel!);                   // --model: no-net player-model viewer (visual QA)
        else if (!string.IsNullOrWhiteSpace(DebugScreen))
            OpenDebugScreen(DebugScreen!);
    }

    /// <summary>Dev/CI: push a named sub-screen so a screenshot can capture that one dialog.</summary>
    private void OpenDebugScreen(string id)
    {
        // "settings" or "settings:TabName" (e.g. settings:Audio) to capture a specific tab.
        string baseId = id.Contains(':') ? id[..id.IndexOf(':')] : id;
        string? tab = id.Contains(':') ? id[(id.IndexOf(':') + 1)..] : null;
        Control? screen = baseId.ToLowerInvariant() switch
        {
            "settings" => new DialogSettings(),
            "media" => new MediaScreen(),
            "multiplayer" => new MultiplayerScreen(),
            "singleplayer" => new SingleplayerScreen(),
            "create" => new CreateGameScreen(),
            "credits" => new CreditsScreen(),
            "quit" => new QuitDialog(),
            "pause" => new PauseMenu(),
            "profile" => new DialogMultiplayerProfile(),
            "mutators" => new DialogMutators(),
            "serverinfo" => new DialogServerInfo(),
            "teamselect" => new DialogTeamSelect(),
            "firstrun" => new DialogFirstRun(),
            "tos" => new DialogTermsOfService(),
            "welcome" => new DialogWelcome(),
            "hudpanels" => new DialogHudPanels(),
            "hudweapons" => new DialogHudPanelWeapons(),
            "cvarlist" => new DialogCvarList(),
            "sandbox" => new DialogSandboxTools(),
            _ => null,
        };
        if (screen is not null)
        {
            _menu.Push(screen);
            if (tab is not null && screen is DialogSettings ds)
                ds.SelectTab(tab);
            else if (tab is not null && screen is MediaScreen mediaScreen)
                mediaScreen.SelectTab(tab);
            else if (tab is not null && screen is MultiplayerScreen mp)
                mp.SelectTab(tab);
        }
        else
        {
            Log.Warn($"[Shell] unknown --menu-screen '{id}'.");
        }
    }

    /// <summary>Point the menu's StartGame/Connect callbacks and the command dispatcher at real shell actions.</summary>
    private void WireCommandHooks()
    {
        CreateGameScreen.StartGameRequested += OnStartGame;
        MultiplayerScreen.Browser.ConnectRequested += OnConnect;

        MenuCommand.Quit = () => GetTree().Quit();
        MenuCommand.Disconnect = ReturnToMainMenu;
        MenuCommand.ToggleMenu = HandleToggleMenu;
        MenuCommand.VideoRestart = ClientSettings.ApplyVideo;
        MenuCommand.AudioRestart = ClientSettings.ApplyAudio;
        // QC `map`/`devmap`: in a running match this is a changelevel (keep mode + bots); at the menu it starts a
        // fresh listen server on the map then self-connects (the real "start a game" path).
        MenuCommand.StartMap = ChangeLevel;
        MenuCommand.Connect = OnConnect;

        // --- T50: the menu nav verbs + the live-match gameplay-command channel ---
        // The menu→match channel: route through the SAME path the console uses — the in-process listen-server
        // world as a CLIENT command (so join/spec/ready act on the local player), else the remote string-command
        // channel for a pure --connect client. This is what makes the pause-menu Join!/Spectate/Leave buttons
        // (and the team-select `cmd ...` buttons) actually reach the running match.
        MenuCommand.SendGameCommand = line =>
        {
            if (LocalRouteCommand(line) is null)
                _netGame?.SendStringCommand(line);
        };
        // QC gamestatus & (GAME_ISSERVER|GAME_CONNECTED): drives the Leave-match button's disabled state.
        MenuCommand.InMatch = () => MatchRunning;

        // QC m_goto(name, false): open OVER the menu, keep the menu open on close (nexposee/servers/profile/…).
        MenuCommand.OpenDialogOverlay = name => OpenMenuDialog(name, resumeOnClose: false);
        // QC m_goto(name, true): open + hide-the-menu-on-close (directmenu → drop back into the match on Back).
        MenuCommand.OpenDialog = name => OpenMenuDialog(name, resumeOnClose: true);
        // QC closemenu: pop the menu back one level (the dialog is the top screen when closemenu fires).
        MenuCommand.CloseDialog = _ => { if (_menu.CanPop) _menu.Pop(); };

        // [T28] QC menu_restart: rebuild the front-end (re-apply skin + re-translate) so a skin/language change shows.
        MenuCommand.MenuRestart = () => _menu.Restart();
    }

    /// <summary>
    /// Resolve a QC dialog name through <see cref="MenuDialogRegistry"/> and push it. <paramref name="resumeOnClose"/>
    /// reproduces QC <c>m_goto(name, true)</c> (directmenu): closing the dialog while a match is live resumes the
    /// match instead of returning to the pause menu. The <c>nexposee</c> name is special — it shows the main-menu
    /// fan as a full root screen rather than a framed sub-dialog, matching QC's "Main menu" button (the match
    /// stays connected; the nexposee just shows over it). Unknown names log (QC's invalid-command path).
    /// </summary>
    private void OpenMenuDialog(string name, bool resumeOnClose)
    {
        if (string.Equals(name, "nexposee", System.StringComparison.OrdinalIgnoreCase))
        {
            // QC keeps the match connected and shows the nexposee over it; ShowScreen replaces the stack with the
            // fan (Push'd dialogs from here layer on top, and Back collapses the fan — never tears the match down).
            _menu.ShowScreen(new MainMenu());
            _menu.Visible = true;
            return;
        }

        Control? screen = MenuDialogRegistry.Create(name);
        if (screen is null)
        {
            Log.Warn($"[Shell] menu_cmd: no dialog named '{name}'.");
            return;
        }
        if (resumeOnClose) _menu.PushResumeOnClose(screen);
        else _menu.Push(screen);
        _menu.Visible = true;
    }

    public override void _ExitTree()
    {
        // DP saves config.cfg at engine shutdown (Host_SaveConfig) — persist the archived cvars here so
        // settings edits survive a quit even without passing through a Back button.
        MenuState.SaveUserConfig();

        // Detach the static menu callbacks so a re-created shell (or test) doesn't double-fire.
        CreateGameScreen.StartGameRequested -= OnStartGame;
        MultiplayerScreen.Browser.ConnectRequested -= OnConnect;
    }

    // -------------------------------------------------------------------------------------------------
    //  Escape — toggle the in-game menu (only while a match is running)
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// The single owner of the Escape→pause-menu toggle. Handled in <see cref="_UnhandledKeyInput"/> (Godot
    /// dispatches this BEFORE <c>_unhandled_input</c>) and the event is CONSUMED, so the gameplay bind path in
    /// <see cref="XonoticGodot.Game.Net.NetGame"/> — which runs in <c>_unhandled_input</c> and would otherwise
    /// also fire the <c>togglemenu</c> bind — never sees this Escape.
    /// Earlier-stage handlers still win: the console (<c>_Input</c>) eats Escape while open, and the key-rebind
    /// capture button (<c>_GuiInput</c>) eats it while capturing.
    ///
    /// <para><b>Why we toggle on the RELEASE edge, not the press.</b> While the mouse is
    /// <see cref="Input.MouseModeEnum.Captured"/>, the OS/Godot swallows the Escape <em>press</em> (it's used to
    /// break the pointer grab) — only the release reaches the app. A press-based toggle therefore silently
    /// dropped the very first Escape (mouse still captured), so the menu only opened from the second press on.
    /// The release edge always arrives, so toggling on it makes the first Escape open the menu reliably.</para>
    /// </summary>
    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Echo: false, Keycode: Key.Escape } key)
            return;
        if (!MatchRunning || ConsoleState.IsOpen)
            return; // at the main menu Escape does nothing; an open console owns its own Escape

        // Own BOTH edges so the gameplay bind path never also reacts to Escape, but only act on release.
        GetViewport().SetInputAsHandled();
        if (key.Pressed)
            return;

        if (_paused)
        {
            // Inside a pushed sub-screen (Settings, …) Escape backs out one level; at the pause root it resumes.
            if (_menu.CanPop) _menu.Pop();
            else Resume();
        }
        else
        {
            OpenPauseMenu();
        }
    }

    /// <summary>DP <c>togglemenu</c> engine command (console / aliases like <c>team_auto</c>): toggle the
    /// in-game pause menu. <paramref name="mode"/> 0 = force close; anything else = toggle (open if closed,
    /// close/pop if open). The Escape KEY is handled directly in <see cref="_UnhandledKeyInput"/>, so this
    /// path is only reached by an explicit <c>togglemenu</c> command, never by the Escape bind.</summary>
    private void HandleToggleMenu(int mode)
    {
        if (!MatchRunning)
            return;
        if (mode == 0)
        {
            if (_paused) Resume();
            return;
        }
        if (_paused)
        {
            if (_menu.CanPop) _menu.Pop();
            else Resume();
        }
        else
        {
            OpenPauseMenu();
        }
    }

    // -------------------------------------------------------------------------------------------------
    //  Match lifecycle
    // -------------------------------------------------------------------------------------------------

    /// <summary>Menu "Start" / campaign / Instant Action handler — host a listen server for the chosen config
    /// (a real networked match the local client joins), so Create Game actually starts a playable server.</summary>
    private void OnStartGame(MatchConfig config)
    {
        Log.Info($"[Shell] start game: {config}");
        StartListenServer(config);
    }

    /// <summary>
    /// Open the no-net <see cref="ModelViewer"/> on a hero player model (CLI <c>--model &lt;name&gt;</c>): a thin
    /// scene that loads the model through the real skeletal path and lays out a turntable contact sheet for a
    /// windowed visual-QA screenshot. Reuses the menu's shared VFS (no second mount). Pair with
    /// <c>--screenshot</c> to capture and quit; <c>tools/visual-qa.sh</c> drives the per-model sweep.
    /// </summary>
    public void StartModelViewer(string modelName)
    {
        TeardownGame();

        _viewer = new ModelViewer
        {
            Name = "ModelViewer",
            ProcessMode = ProcessModeEnum.Pausable,
            ModelName = string.IsNullOrWhiteSpace(modelName) ? "erebus" : modelName,
            SharedVfs = MenuState.Vfs,
        };
        AddChild(_viewer);

        // The viewer builds synchronously in _Ready; just reveal the stage (mouse stays free — there's no
        // gameplay to capture the pointer for).
        _menu.Visible = false;
        _paused = false;
        GetTree().Paused = false;
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    /// <summary>Open the in-game menu: show the pause screen over the frozen match and free the mouse.</summary>
    private void OpenPauseMenu()
    {
        _menu.ShowScreen(new PauseMenu());
        _menu.Visible = true;
        _paused = true;
        GetTree().Paused = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    /// <summary>Resume the match: hide the menu, recapture the mouse, unfreeze the tree.</summary>
    private void Resume()
    {
        if (!MatchRunning)
            return;
        _menu.Visible = false;
        _paused = false;
        GetTree().Paused = false;
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    /// <summary>True while a networked match (<see cref="XonoticGodot.Game.Net.NetGame"/> — listen server or
    /// remote client) is live. The no-net <see cref="ModelViewer"/> is intentionally NOT a "match" (no pause menu).</summary>
    private bool MatchRunning => _netGame is not null;

    /// <summary>Disconnect: tear the match down, persist preferences, and show the main menu again.</summary>
    private void ReturnToMainMenu()
    {
        TeardownGame();
        DismissLoadingScreen();
        MenuState.SaveUserConfig();

        // Reinstall a clean menu-time facade (empty world) so no stale match entities linger behind the menu;
        // the shared cvar store is preserved so settings persist across the round-trip.
        Api.Services = new EngineServices(new CollisionWorld(), MenuState.Cvars);

        _menu.ShowScreen(new MainMenu());
        _menu.Visible = true;
        _paused = false;
        GetTree().Paused = false;
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    private void TeardownGame()
    {
        if (_viewer is not null)
        {
            _viewer.QueueFree();
            _viewer = null;
        }
        if (_netGame is not null)
        {
            // Dispose the transports synchronously (releases the listen server's UDP port now) BEFORE the
            // deferred free, so an immediate re-host can bind the same port this frame.
            _netGame.Shutdown();
            _netGame.QueueFree();
            _netGame = null;
        }
    }

    // -------------------------------------------------------------------------------------------------
    //  Networked play — join a server, or host a listen server and self-connect (the real bring-up)
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Server-browser / address connect: build a REAL networked client (<see cref="XonoticGodot.Game.Net.NetGame"/>
    /// — ClientNet + prediction + the ClientWorld render bridge + a first-person camera following the predicted
    /// local player + a basic HUD/crosshair/radar) and connect to <paramref name="address"/>. Parses
    /// <c>host[:port]</c> (default 26000) and reuses the menu's shared VFS + cvar store so models/sounds resolve
    /// and prediction reads the user's physics cvars.
    /// </summary>
    private void OnConnect(string address) => ConnectToServer(address);

    /// <summary>Tear down any match and spin up a client NetGame connected to <paramref name="address"/>.</summary>
    public async void ConnectToServer(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            Log.Warn("[Shell] connect: empty address.");
            return;
        }
        Log.Info($"[Shell] connecting to {address}.");
        TeardownGame();
        ShowLoadingScreen(address);

        // Hide the menu (and unfreeze the tree, capture the mouse) NOW so the loading screen is the only
        // thing on screen during the blocking connect — not the menu frozen behind the overlay.
        EnterMatchView();

        var net = new XonoticGodot.Game.Net.NetGame
        {
            Name = "NetClient",
            // Keep processing while the in-game menu pauses the tree, so the netcode keeps pumping (the link
            // would otherwise time out and prediction freeze). NetGame gates movement input on the pause itself.
            ProcessMode = ProcessModeEnum.Always,
        };
        net.ConfigureClient(address, ResolvePlayerName(), MenuState.Vfs, MenuState.Cvars);
        net.LoadingScreen = _loadingScreen;
        net.DismissLoadingScreen = DismissLoadingScreen;
        WireConsoleToNet(net);
        _netGame = net;

        // Wait for the loading screen to actually be PAINTED before kicking off the blocking connect.
        await WaitForFramePainted();
        if (_netGame != net) return; // abandoned: TeardownGame was called while we awaited

        AddChild(net);
    }

    /// <summary>
    /// Host a LISTEN SERVER for the chosen config — boot a <see cref="XonoticGodot.Server.GameWorld"/> + a
    /// <see cref="XonoticGodot.Game.Net.ServerNet"/> in-process (filled with the config's bots), then self-connect a
    /// networked client to 127.0.0.1. This is the "Create Game" / <c>map</c> path, and also the boot path for
    /// <c>--map</c> (a 0-bot listen server — the consolidated local-match path). Reuses the menu's shared VFS + cvar store.
    /// </summary>
    public async void StartListenServer(MatchConfig config)
    {
        Log.Info($"[Shell] hosting listen server: {config}");
        TeardownGame();
        ShowLoadingScreen(config.Map ?? "");

        // Hide the menu (and unfreeze + capture mouse) NOW so the loading screen is the only thing on
        // screen during the blocking load, not the menu frozen behind it.
        EnterMatchView();

        // Apply the chosen match limits to the shared cvars so the hosted world reads them.
        if (config.TimeLimit > 0) MenuState.Cvars.Set("timelimit", config.TimeLimit.ToString());
        if (config.FragLimit > 0) MenuState.Cvars.Set("fraglimit", config.FragLimit.ToString());

        var net = new XonoticGodot.Game.Net.NetGame
        {
            Name = "ListenServer",
            ProcessMode = ProcessModeEnum.Always, // the hosted server must keep ticking under the pause menu
        };
        net.ConfigureListenServer(
            map: config.Map ?? "",
            gametype: string.IsNullOrWhiteSpace(config.Gametype) ? "dm" : config.Gametype,
            botCount: config.BotCount,
            botSkill: config.BotSkill,
            port: XonoticGodot.Game.Net.NetGame.DefaultPort,
            playerName: ResolvePlayerName(),
            serverName: MenuState.Cvars.GetString("hostname") is { Length: > 0 } hn ? hn : "XonoticGodot Listen Server",
            vfs: MenuState.Vfs,
            cvars: MenuState.Cvars,
            campaignName: config.CampaignId ?? "",   // non-empty → the server boots this as a campaign level
            campaignIndex: config.CampaignIndex);
        net.LoadingScreen = _loadingScreen;
        net.DismissLoadingScreen = DismissLoadingScreen;
        WireConsoleToNet(net);
        _netGame = net;

        // Wait for the loading screen to actually be PAINTED before kicking off the blocking map load.
        await WaitForFramePainted();
        if (_netGame != net) return; // abandoned: TeardownGame was called while we awaited

        AddChild(net);
    }

    /// <summary>
    /// Yield until the GPU has actually painted a frame (so any UI changes — e.g. the loading screen —
    /// are on screen before we hand control back to a synchronous, blocking caller). Uses
    /// <c>RenderingServer.frame_post_draw</c>, which fires after the renderer submits the frame.
    /// Falls back to two <see cref="SceneTree.ProcessFrame"/> ticks under <c>--headless</c>: the
    /// RenderingServer singleton still EXISTS there (dummy renderer), but the main loop never calls
    /// <c>RenderingServer.draw()</c> when no window can draw — so <c>frame_post_draw</c> never fires
    /// and awaiting it would hang the boot forever (the old "headless --host never loads the map" bug).
    /// </summary>
    private async System.Threading.Tasks.Task WaitForFramePainted()
    {
        var rs = RenderingServer.Singleton;
        if (rs is not null && DisplayServer.GetName() != "headless")
        {
            // Two cycles to be safe: the first may resume during the same iteration that scheduled the
            // await (depending on where in the main loop we were called from); the second is a guaranteed
            // post-draw fence.
            await ToSignal(rs, RenderingServer.SignalName.FramePostDraw);
            await ToSignal(rs, RenderingServer.SignalName.FramePostDraw);
        }
        else
        {
            // Headless: nothing is ever painted. Two process_frame ticks at least flushes one full iteration.
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    /// <summary>The local player's display name (QC <c>_cl_name</c>/<c>name</c> cvar), defaulting to "player".</summary>
    private static string ResolvePlayerName()
    {
        string n = MenuState.Cvars.GetString("_cl_name");
        if (string.IsNullOrWhiteSpace(n)) n = MenuState.Cvars.GetString("name");
        return string.IsNullOrWhiteSpace(n) ? "player" : n;
    }

    /// <summary>Hide the menu, capture the mouse, and unfreeze the tree for an active (networked) match.</summary>
    private void EnterMatchView()
    {
        _menu.Visible = false;
        _paused = false;
        GetTree().Paused = false;
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    // -------------------------------------------------------------------------------------------------
    //  Loading screen (DP SCR_DrawLoadingScreen)
    // -------------------------------------------------------------------------------------------------

    /// <summary>Show the Darkplaces-style loading screen (gfx/loading.tga + progress bar + status text)
    /// on a high CanvasLayer above the HUD, before a match enters the tree.</summary>
    private void ShowLoadingScreen(string mapName)
    {
        DismissLoadingScreen();

        _loadingScreen = new LoadingScreen();
        _loadingScreen.UpdateProgress(0f, "Loading...");

        // CanvasLayer above everything (same layer 100 the old black connect overlay used)
        _loadingLayer = new CanvasLayer { Name = "LoadingLayer", Layer = 100 };
        _loadingLayer.AddChild(_loadingScreen);
        AddChild(_loadingLayer);

        _loadingScreen.SetMapName(mapName);
    }

    /// <summary>Tear down the loading screen layer (idempotent).</summary>
    internal void DismissLoadingScreen()
    {
        if (_loadingLayer is not null && GodotObject.IsInstanceValid(_loadingLayer))
        {
            _loadingLayer.QueueFree();
        }
        _loadingLayer = null;
        _loadingScreen = null;
    }

    // -------------------------------------------------------------------------------------------------
    //  Console ↔ match wiring
    // -------------------------------------------------------------------------------------------------

    /// <summary>Point a freshly-created match's input + console-output hooks at the shared console: bound keys run
    /// one-shot commands through the shared interpreter, and the server's console replies print in the overlay.</summary>
    private void WireConsoleToNet(XonoticGodot.Game.Net.NetGame net)
    {
        net.RunCommand = MenuState.Interp!.ExecuteLine;
        net.ConsolePrint += _console.Print;
        // A listen-server changelevel (map/gotomap/rotation) reboots the server on the new map, preserving the
        // mode + bots — and, on a campaign auto-advance (win → next level), the campaign id + index so the next
        // level comes up in campaign mode. The event is emitted deferred by NetGame, so this restart runs at idle.
        net.MapChangeRequested += (map, gametype, bots, skill, campId, campIdx) =>
            StartListenServer(new MatchConfig
            {
                Map = map, Gametype = gametype, BotCount = bots, BotSkill = skill,
                CampaignId = campId, CampaignIndex = campIdx,
            });
    }

    /// <summary>
    /// DP <c>map</c>/<c>changelevel</c>: while a listen server is running, change to <paramref name="map"/> keeping
    /// the current gametype + bot fill (routed through the server's deferred change-level path); at the menu (no
    /// match), start a fresh listen server on it. A pure <c>--connect</c> client has no local server to changelevel
    /// (the real server owns the map), so this no-ops there.
    /// </summary>
    private void ChangeLevel(string map)
    {
        if (string.IsNullOrWhiteSpace(map))
            return;
        if (_netGame is { ServerWorld: not null })
            _netGame.RequestMapChange(map);                                  // in a match → server-side changelevel
        else if (_netGame is null)
            StartListenServer(new MatchConfig { Map = map, Gametype = "dm" }); // at the menu → start a game on it
    }

    /// <summary>
    /// The console's gameplay-command router: run <paramref name="line"/> on the in-process listen-server world
    /// as a CLIENT command (so kill/say/team act on the local player) and return its output. Returns null when
    /// there is no local world (a pure <c>--connect</c> client or no match) so the console falls back to the
    /// remote string-command channel.
    /// </summary>
    private string? LocalRouteCommand(string line)
    {
        GameWorld? world = _netGame?.ServerWorld;
        if (world is null)
            return null;
        // T47 integration wire-up: the listen-server operator's in-game console is the HOST, so it runs as the
        // server console (isServerConsole: true) — without this flag the new client-command privilege gate would
        // reject the host's own kick/map/set/endmatch/etc. (a regression vs pre-T47). caller stays LocalServerPlayer
        // so kill/say/team still act on the host's player; the remote-client path (ServerNet.cs) stays gated.
        return world.Commands.Execute(line, isServerConsole: true, caller: _netGame?.LocalServerPlayer).Output;
    }

    /// <summary>
    /// Fallback for the console's gameplay-command router when there is no in-process server: forward to the
    /// connected remote server (DP <c>clc_stringcmd</c>) on a pure <c>--connect</c> client, otherwise PRINT a
    /// hint so the user gets feedback instead of silent acceptance — the menu has no live game to route to,
    /// but the cvar/exec/bind side of the console always processes regardless.
    /// </summary>
    private void RouteRemoteCommand(string line)
    {
        if (_netGame is not null)
            _netGame.SendStringCommand(line);
        else
            XonoticGodot.Common.Diagnostics.Log.Help($"\"{line}\": no server — start a match (`map <name>`) or `connect <addr>` first.");
    }
}
