using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Gameplay;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// The CSQC on-screen HUD root — the C# successor to QuakeC's <c>HUD_Main</c> / <c>HUD_Draw</c>
/// (Base/.../qcsrc/client/hud/hud.qc), which each frame walked the registered <c>hud_panels</c> in
/// <c>panel_order</c> and called every panel's <c>panel_draw</c>. Here a <see cref="CanvasLayer"/> hosts
/// one child <see cref="HudPanel"/> <see cref="Control"/> per panel; Godot composites them over the 3D
/// view automatically, so this manager only has to create the panels, lay them out against the viewport,
/// hand them the local <see cref="Player"/>, and poke the dynamic ones to redraw.
///
/// Attachment (mirrors how <see cref="PlayerController"/> owns the player <see cref="Common.Framework.Entity"/>):
/// the lead adds a <see cref="Hud"/> to the scene — e.g. in <c>GameDemo._Ready</c> after the
/// <see cref="PlayerController"/> is spawned — and calls <see cref="SetPlayer"/> with the
/// <c>PlayerController.Player</c> cast to <see cref="Player"/>:
/// <code>
///   var hud = new Hud();
///   AddChild(hud);
///   hud.SetPlayer(player.Player as Player);     // the local player actor
/// </code>
///
/// Net data-injection points (the panels read live local-player state themselves; everything that is a
/// networked/server concern is fed through these settable surfaces by the net/match layer):
/// <list type="bullet">
///   <item><see cref="ScoreboardPanel.SetRows"/> / <see cref="ScoreboardPanel.SetTeamScores"/> — other
///         players' name/score/deaths/ping + team totals (QC ent_cs + scores stats).</item>
///   <item><see cref="CenterPrintPanel.Push(Common.Gameplay.Notification, string, float, int)"/> — MSG_CENTER
///         notifications (countdowns/frag messages).</item>
///   <item><see cref="NotifyPanel.PushDeath"/> — MSG_INFO obituaries for the kill feed.</item>
///   <item><see cref="TimerPanel.MatchStartTime"/>/<see cref="TimerPanel.TimeLimitSeconds"/>/
///         <see cref="TimerPanel.WarmupStage"/>/<see cref="TimerPanel.Overtime"/>/
///         <see cref="TimerPanel.SetRound"/> — match clock + round timer (QC GAMESTARTTIME/TIMELIMIT).</item>
///   <item><see cref="InfoMessagesPanel.IsSpectating"/>/<see cref="InfoMessagesPanel.CountdownSeconds"/>/
///         <see cref="InfoMessagesPanel.SetSpectators"/> — spectator/warmup/countdown state.</item>
///   <item><see cref="WeaponsPanel.SetAccuracy"/> — per-weapon accuracy stats; and the optional
///         <see cref="CrosshairPanel.ChargeFraction"/>/<see cref="CrosshairPanel.HitFlash"/> for charge/hit
///         feedback (per-slot weapon state is networked).</item>
/// </list>
///
/// What is NOT ported: the in-game HUD editor and skin/cvar system (hud_config.qc), per-panel enable
/// cvars, the dynamic-follow shake, and the registry hash handshake — none are needed to draw the HUD.
///
/// The class is named <see cref="Hud"/> (the file is HudManager.cs); the lead instantiates
/// <c>new Hud()</c>.
/// </summary>
public partial class Hud : CanvasLayer
{
    /// <summary>The local player whose state the HUD reflects (QC <c>spectatee</c> / the view player).</summary>
    public Player? Player { get; private set; }

    // Strongly-typed handles to the panels that callers commonly feed data into.
    public HealthArmorPanel HealthArmor { get; private set; } = null!;
    public AmmoPanel Ammo { get; private set; } = null!;
    public WeaponsPanel Weapons { get; private set; } = null!;
    public PowerupsPanel Powerups { get; private set; } = null!;
    public ScoreboardPanel Scoreboard { get; private set; } = null!;
    public CenterPrintPanel CenterPrint { get; private set; } = null!;
    public NotifyPanel Notify { get; private set; } = null!;
    public CrosshairPanel Crosshair { get; private set; } = null!;
    public TimerPanel Timer { get; private set; } = null!;
    public InfoMessagesPanel InfoMessages { get; private set; } = null!;
    public VehicleHud Vehicle { get; private set; } = null!;

    // Match-critical panels (T10): race/checkpoints/vote/modicons/itemstime/physics/mapvote. Their gameplay
    // state is networked, so the net/match layer feeds them through their settable members (see each panel).
    public RaceTimerPanel RaceTimer { get; private set; } = null!;
    public CheckpointsPanel Checkpoints { get; private set; } = null!;
    public VotePanel Vote { get; private set; } = null!;
    public ModIconsPanel ModIcons { get; private set; } = null!;
    public ItemsTimePanel ItemsTime { get; private set; } = null!;
    public PhysicsPanel Physics { get; private set; } = null!;
    public MapVotePanel MapVote { get; private set; } = null!;

    /// <summary>The active-minigame board overlay (CSQC minigame board draw + click-to-move).</summary>
    public MinigameRenderer Minigame { get; private set; } = null!;

    /// <summary>The in-game minigame menu (CSQC HUD_MinigameMenu_* — Create/Join/Current Game/Quit).</summary>
    public MinigameMenu MinigameMenu { get; private set; } = null!;

    private readonly List<HudPanel> _panels = new();

    public override void _Ready()
    {
        // Create every panel once and parent it under this CanvasLayer (QC registered them at load).
        HealthArmor  = Add(new HealthArmorPanel());
        Ammo         = Add(new AmmoPanel());
        Weapons      = Add(new WeaponsPanel());
        Powerups     = Add(new PowerupsPanel());
        Scoreboard   = Add(new ScoreboardPanel());
        CenterPrint  = Add(new CenterPrintPanel());
        Notify       = Add(new NotifyPanel());
        Crosshair    = Add(new CrosshairPanel());
        Timer        = Add(new TimerPanel());
        InfoMessages = Add(new InfoMessagesPanel());
        Vehicle      = Add(new VehicleHud { Visible = false }); // shown only while driving a vehicle

        // Match-critical panels (T10). RaceTimer/Checkpoints/MapVote/Vote are off by default — the net/match
        // layer shows them when their gametype/event is active; ModIcons/ItemsTime/Physics follow suit.
        RaceTimer    = Add(new RaceTimerPanel  { Visible = false }); // race/CTS only
        Checkpoints  = Add(new CheckpointsPanel { Visible = false }); // race/CTS only
        Vote         = Add(new VotePanel       { Visible = false }); // shown while a callvote is active
        ModIcons     = Add(new ModIconsPanel   { Visible = false }); // team-objective gametypes only
        ItemsTime    = Add(new ItemsTimePanel  { Visible = false }); // shown when item timers are available
        Physics      = Add(new PhysicsPanel    { Visible = false }); // speedo: off unless enabled
        MapVote      = Add(new MapVotePanel    { Visible = false }); // intermission map/gametype vote

        // The minigame board overlay isn't a HudPanel (it captures clicks + manages its own redraw), so add it
        // directly rather than through Add() (which forces MouseFilter.Ignore).
        Minigame = new MinigameRenderer { Name = "Minigame" };
        AddChild(Minigame);

        // The minigame menu (Create/Join/Current Game) — likewise a click-capturing Control, not a HudPanel.
        // Added ABOVE the board so its entries sit on top of the board overlay (QC the MINIGAMEMENU panel).
        MinigameMenu = new MinigameMenu { Name = "MinigameMenu" };
        AddChild(MinigameMenu);

        // Push the current player into the freshly-created panels.
        ApplyPlayer();

        // Initial layout, and re-layout whenever the window resizes (QC rebuilt on vid_conwidth change).
        GetViewport().SizeChanged += Layout;
        Layout();
    }

    /// <summary>
    /// Point the HUD at the local player actor (QC: the entity referenced by <c>spectatee_status</c> /
    /// the view player). Pass <c>null</c> to blank the HUD (e.g. before spawn or while observing).
    /// </summary>
    public void SetPlayer(Player? player)
    {
        Player = player;
        ApplyPlayer();
    }

    /// <summary>
    /// Sink for Pong's paddle drive command (QC pong_hud_board: <c>minigame_cmd("move "+bits)</c> on a key
    /// change). Wired by the net layer to send <c>cmd minigame move &lt;bits&gt;</c>. The bits are the QC
    /// PONG_KEY bitset (1 = increase/down-right, 2 = decrease/up-left). Null = no Pong drive (no net path).
    /// </summary>
    public System.Action<int>? PongMoveSink { get; set; }

    private int _pongKeys;        // current PONG_KEY bitset held this frame
    private int _pongKeysOld = -1; // last sent bitset (-1 = nothing sent yet) — QC pong_keys_pressed_old

    public override void _Process(double delta)
    {
        // QC redrew the entire HUD every frame; we only invalidate panels whose contents are live so
        // static panels (scoreboard) repaint solely when their data is pushed via QueueRedraw.
        foreach (HudPanel p in _panels)
            if (p.Visible && p.IsDynamic)
                p.QueueRedraw();

        DrivePongKeys();
    }

    /// <summary>
    /// QC pong_hud_board's key drive: while a Pong board is shown, sample the arrow keys into the PONG_KEY
    /// bitset (UP/LEFT → DECREASE 0x02, DOWN/RIGHT → INCREASE 0x01) and, on a change, send <c>move &lt;bits&gt;</c>
    /// to the server (the paddle moves while the key is held). No-op when no Pong board is shown or the menu
    /// captures input. Grid games self-handle clicks in <see cref="MinigameRenderer"/>, so they need nothing here.
    /// </summary>
    private void DrivePongKeys()
    {
        bool pongShown = PongMoveSink is not null
            && Minigame is { Visible: true, Session: { } s } && s.Game.NetName == "pong"
            && (MinigameMenu is null || !MinigameMenu.Visible); // the open menu owns the cursor/keys

        int keys = 0;
        if (pongShown)
        {
            // PONG_KEY_DECREASE (up/left), PONG_KEY_INCREASE (down/right) — Godot physical-key state.
            if (Input.IsKeyPressed(Key.Up) || Input.IsKeyPressed(Key.Left)) keys |= 0x02;
            if (Input.IsKeyPressed(Key.Down) || Input.IsKeyPressed(Key.Right)) keys |= 0x01;
        }
        _pongKeys = keys;
        if (_pongKeys != _pongKeysOld)
        {
            // Only emit once the board is shown (so hiding the board sends a final "0" to stop the paddle).
            if (pongShown || _pongKeysOld > 0)
                PongMoveSink?.Invoke(_pongKeys);
            _pongKeysOld = _pongKeys;
        }
    }

    // -------------------------------------------------------------------------------------------------
    //  Internals
    // -------------------------------------------------------------------------------------------------

    private T Add<T>(T panel) where T : HudPanel
    {
        panel.MouseFilter = Control.MouseFilterEnum.Ignore; // HUD never eats input (QC hud_cursormode off)
        AddChild(panel);
        _panels.Add(panel);
        return panel;
    }

    private void ApplyPlayer()
    {
        // The gameplay-data panels read live state off the local player; centerprint/notify are fed by the
        // notification layer (player-agnostic). The crosshair now also follows the player so it can tint to
        // the active weapon and draw the charge ring.
        if (HealthArmor is null) return; // not built yet (SetPlayer called before _Ready)
        HealthArmor.Player = Player;
        Ammo.Player        = Player;
        Weapons.Player     = Player;
        Powerups.Player    = Player;
        InfoMessages.Player = Player;
        Crosshair.Player   = Player;
        // The speedo reads live velocity off the view player (QC csqcplayer.velocity).
        Physics.Player     = Player;
        // The scoreboard's local-player highlight also follows the view player.
        Scoreboard.LocalPlayer = Player;
    }

    /// <summary>
    /// Anchor every panel to a screen edge and size it relative to the viewport — the modernized stand-in
    /// for the normalized <c>hud_panel_*_pos/_size</c> cvars (QC defaults roughly reproduced here).
    /// </summary>
    private void Layout()
    {
        Vector2 vp = GetViewport().GetVisibleRect().Size;
        float w = vp.X, h = vp.Y;

        // Bottom bar: health/armor (left) and ammo (right), like the default Xonotic layout.
        var haSize = new Vector2(w * 0.22f, h * 0.10f);
        HealthArmor.Configure(new Rect2(new Vector2(w * 0.02f, h - haSize.Y - h * 0.03f), haSize));

        var ammoSize = new Vector2(w * 0.16f, h * 0.08f);
        Ammo.Configure(new Rect2(new Vector2(w - ammoSize.X - w * 0.02f, h - ammoSize.Y - h * 0.03f), ammoSize));

        // Weapons strip: bottom center.
        var wepSize = new Vector2(w * 0.40f, h * 0.06f);
        Weapons.Configure(new Rect2(new Vector2((w - wepSize.X) * 0.5f, h - wepSize.Y - h * 0.005f), wepSize));

        // Powerups: just above the health bar, bottom-left.
        var powSize = new Vector2(w * 0.22f, h * 0.04f);
        Powerups.Configure(new Rect2(new Vector2(w * 0.02f, h - haSize.Y - h * 0.03f - powSize.Y - h * 0.01f), powSize));

        // Timer: top center.
        var timerSize = new Vector2(w * 0.12f, h * 0.06f);
        Timer.Configure(new Rect2(new Vector2((w - timerSize.X) * 0.5f, h * 0.02f), timerSize));

        // Killfeed (notify): top right.
        var notifySize = new Vector2(w * 0.28f, h * 0.30f);
        Notify.Configure(new Rect2(new Vector2(w - notifySize.X - w * 0.01f, h * 0.10f), notifySize));

        // Info messages: top left.
        var infoSize = new Vector2(w * 0.30f, h * 0.20f);
        InfoMessages.Configure(new Rect2(new Vector2(w * 0.02f, h * 0.10f), infoSize));

        // Center print: middle of the screen, upper third.
        var cpSize = new Vector2(w * 0.60f, h * 0.35f);
        CenterPrint.Configure(new Rect2(new Vector2((w - cpSize.X) * 0.5f, h * 0.18f), cpSize));

        // Scoreboard: large centered overlay (shown on demand).
        var sbSize = new Vector2(w * 0.60f, h * 0.70f);
        Scoreboard.Configure(new Rect2(new Vector2((w - sbSize.X) * 0.5f, h * 0.12f), sbSize));

        // Crosshair: fills the screen; it draws at the exact center itself.
        Crosshair.Configure(new Rect2(Vector2.Zero, vp));

        // Vehicle HUD: the frame sits bottom-center (QC Vehicles_drawHUD); the panel doesn't clip, so its
        // auxiliary lock-on crosshairs still project across the whole screen.
        var vehSize = new Vector2(w * 0.42f, h * 0.16f);
        Vehicle.Configure(new Rect2(new Vector2((w - vehSize.X) * 0.5f, h - vehSize.Y), vehSize));

        // --- match-critical panels (T10), placed against the default Xonotic layout edges ---

        // Race timer: top center, just under the match timer (QC default hud_panel_racetimer pos).
        var raceSize = new Vector2(w * 0.40f, h * 0.10f);
        RaceTimer.Configure(new Rect2(new Vector2((w - raceSize.X) * 0.5f, h * 0.10f), raceSize));

        // Checkpoints list: left side, mid-height (a vertical stack of stored splits).
        var cpSize2 = new Vector2(w * 0.22f, h * 0.30f);
        Checkpoints.Configure(new Rect2(new Vector2(w * 0.02f, h * 0.32f), cpSize2));

        // Vote panel: bottom-left above the health bar (QC default mostly centered-left).
        var voteSize = new Vector2(w * 0.26f, h * 0.16f);
        Vote.Configure(new Rect2(new Vector2(w * 0.02f, h * 0.55f), voteSize));

        // Mod icons (flags/keys/dom): top center, below the timer (QC default hud_panel_modicons).
        var modSize = new Vector2(w * 0.18f, h * 0.10f);
        ModIcons.Configure(new Rect2(new Vector2((w - modSize.X) * 0.5f, h * 0.10f), modSize));

        // Items time: right edge, mid-height (a vertical column of respawn timers).
        var itSize = new Vector2(w * 0.10f, h * 0.40f);
        ItemsTime.Configure(new Rect2(new Vector2(w - itSize.X - w * 0.01f, h * 0.28f), itSize));

        // Physics/speedo: bottom center, above the weapons strip.
        var phySize = new Vector2(w * 0.16f, h * 0.08f);
        Physics.Configure(new Rect2(new Vector2((w - phySize.X) * 0.5f, h - phySize.Y - h * 0.10f), phySize));

        // Map vote: large centered overlay shown at intermission (QC spans most of the screen).
        var mvSize = new Vector2(w * 0.84f, h * 0.80f);
        MapVote.Configure(new Rect2(new Vector2((w - mvSize.X) * 0.5f, h * 0.10f), mvSize));
    }
}
