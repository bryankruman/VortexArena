using System;
using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Gameplay;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// The CSQC on-screen HUD root — the C# successor to QuakeC's <c>HUD_Main</c> / <c>HUD_Draw</c>
/// (Base/.../qcsrc/client/hud/hud.qc), which each frame walked the registered <c>hud_panels</c> and called
/// every panel's draw. Here a <see cref="CanvasLayer"/> hosts one child <see cref="HudPanel"/> per panel; Godot
/// composites them over the 3D view, so this manager creates the panels (now via reflection discovery —
/// <see cref="HudRegistry"/>), hands them the local <see cref="Player"/>, resolves each panel's cvar-driven
/// layout/skin each frame (<see cref="HudPanel.LoadConfig"/>), and pokes the dynamic ones to redraw.
///
/// Discovery replaces the old hand-maintained <c>new XxxPanel()</c> list: a panel exists by being a
/// <see cref="HudPanel"/> subclass, so new panels drop in without editing this file. Strongly-typed handles the
/// net/match layer feeds (<see cref="Scoreboard"/>, <see cref="CenterPrint"/>, …) are resolved from the
/// discovered set via <see cref="Get{T}"/>.
///
/// Layout/look come from the <c>hud_panel_&lt;id&gt;_*</c> cvars (luma defaults baked in <see cref="HudLayoutDefaults"/>)
/// resolved per-frame, NOT a hardcoded anchor table — so a console <c>set</c> or the menu HUD dialogs move/skin
/// panels live.
///
/// Port extras kept: <see cref="FpsPanel"/>/<see cref="PingPanel"/> (self-managing their own visibility — exempt
/// from the gating loop), <see cref="MinigameRenderer"/>/<see cref="MinigameMenu"/> (click-capturing, added
/// directly, not panels), the Pong key-drive, and the <c>FrameProfiler</c> scope.
/// </summary>
public partial class Hud : CanvasLayer
{
    /// <summary>The local player whose state the HUD reflects (QC <c>spectatee</c> / the view player).</summary>
    public Player? Player { get; private set; }

    // Strongly-typed handles the net/match layer commonly feeds data into (resolved from the registry).
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
    public RaceTimerPanel RaceTimer { get; private set; } = null!;
    public CheckpointsPanel Checkpoints { get; private set; } = null!;
    public VotePanel Vote { get; private set; } = null!;
    public ModIconsPanel ModIcons { get; private set; } = null!;
    public ItemsTimePanel ItemsTime { get; private set; } = null!;
    public PhysicsPanel Physics { get; private set; } = null!;
    public MapVotePanel MapVote { get; private set; } = null!;
    public FpsPanel Fps { get; private set; } = null!;
    public PingPanel Ping { get; private set; } = null!;

    /// <summary>The active-minigame board overlay (CSQC minigame board draw + click-to-move). Not a HudPanel.</summary>
    public MinigameRenderer Minigame { get; private set; } = null!;

    /// <summary>The in-game minigame menu (CSQC HUD_MinigameMenu_*). Not a HudPanel.</summary>
    public MinigameMenu MinigameMenu { get; private set; } = null!;

    /// <summary>Scoreboard cross-fade (0 = not shown … 1 = fully up). The net/match layer drives it; non-WITH_SB
    /// panels fade their alpha by <c>1 − ScoreboardFade</c> (QC <c>panel_fade_alpha</c>), without losing Visible.</summary>
    public float ScoreboardFade { get; set; }

    /// <summary>Global HUD fade (QC <c>hud_fade_alpha</c>; 1 = opaque). Settable by the host if it wants the HUD
    /// to dim under a menu; defaults to fully on.</summary>
    public float HudFadeAlpha { get; set; } = 1f;

    private readonly List<HudPanel> _panels = new();

    // Panels that drive their OWN Visible + redraw in their own _Process (port extras) — the manager only
    // refreshes their cvar layout, it never sets their Visible (else it would fight them frame-to-frame).
    private static readonly HashSet<Type> SelfManaged = new() { typeof(FpsPanel), typeof(PingPanel) };

    // Panels conditionally shown by the net/match layer or by a gameplay event (kept hidden until shown), and
    // the newly-discovered Radar (no data wiring yet). Matches the old manager's `{ Visible = false }` set.
    private static readonly HashSet<string> StartHiddenIds = new()
    {
        "racetimer", "checkpoints", "vote", "modicons", "itemstime", "physics", "mapvote",
        "vehicle", "fps", "ping", "radar",
    };

    private string _skin = "luma";

    public override void _Ready()
    {
        // Discover + create every panel (QC registered them at load). Reflection runs once (HudRegistry).
        foreach (Type t in HudRegistry.PanelTypes)
            Add(HudRegistry.Create(t));

        // Resolve the strongly-typed handles the net/match layer feeds.
        HealthArmor  = Get<HealthArmorPanel>();
        Ammo         = Get<AmmoPanel>();
        Weapons      = Get<WeaponsPanel>();
        Powerups     = Get<PowerupsPanel>();
        Scoreboard   = Get<ScoreboardPanel>();
        CenterPrint  = Get<CenterPrintPanel>();
        Notify       = Get<NotifyPanel>();
        Crosshair    = Get<CrosshairPanel>();
        Timer        = Get<TimerPanel>();
        InfoMessages = Get<InfoMessagesPanel>();
        Vehicle      = Get<VehicleHud>();
        RaceTimer    = Get<RaceTimerPanel>();
        Checkpoints  = Get<CheckpointsPanel>();
        Vote         = Get<VotePanel>();
        ModIcons     = Get<ModIconsPanel>();
        ItemsTime    = Get<ItemsTimePanel>();
        Physics      = Get<PhysicsPanel>();
        MapVote      = Get<MapVotePanel>();
        Fps          = Get<FpsPanel>();
        Ping         = Get<PingPanel>();

        // Apply initial visibility (the conditionally-shown panels start hidden; net layer shows them).
        foreach (HudPanel p in _panels)
            if (StartHiddenIds.Contains(p.PanelId))
                p.Visible = false;

        // The minigame board overlay + menu aren't HudPanels (they capture clicks/keys) — add them directly.
        Minigame = new MinigameRenderer { Name = "Minigame" };
        AddChild(Minigame);
        MinigameMenu = new MinigameMenu { Name = "MinigameMenu" };
        AddChild(MinigameMenu);

        SyncSkin();
        ApplyPlayer();

        GetViewport().SizeChanged += OnViewportResized;
    }

    /// <summary>Point the HUD at the local player actor. Pass <c>null</c> to blank the HUD (pre-spawn/observing).</summary>
    public void SetPlayer(Player? player)
    {
        Player = player;
        ApplyPlayer();
    }

    /// <summary>Sink for Pong's paddle drive command (wired by the net layer). Bits = PONG_KEY bitset.</summary>
    public System.Action<int>? PongMoveSink { get; set; }

    private int _pongKeys;
    private int _pongKeysOld = -1;

    public override void _Process(double delta)
    {
        using var _hudScope = XonoticGodot.Game.Client.FrameProfiler.Scope("hud.mgr");

        SyncSkin();
        Vector2 vp = GetViewport().GetVisibleRect().Size;
        float fade = Mathf.Clamp(HudFadeAlpha, 0f, 1f);
        float sbFade = Mathf.Clamp(ScoreboardFade, 0f, 1f);

        foreach (HudPanel p in _panels)
        {
            // Port extras own their visibility/redraw — just keep their cvar layout fresh (full-viewport).
            if (SelfManaged.Contains(p.GetType()))
            {
                p.LoadConfig(vp, 1f, 1f);
                continue;
            }

            if (!p.Visible) continue;

            // Non-scoreboard panels fade out as the scoreboard fades in (QC panel_fade_alpha).
            float panelFade = p.ShowFlags.HasFlag(PanelShow.WithScoreboard) ? 1f : 1f - sbFade;
            p.LoadConfig(vp, fade, panelFade);

            // Re-record the canvas item only when the panel's displayed content actually changed (3.2-3) —
            // most dynamic panels return true (animate every frame), but the value readouts (timer/race timer)
            // gate on a once-per-second change so we don't re-run DrawPanel 160×/s for content that ticks 1×/s.
            if (p.IsDynamic && p.NeedsRedraw())
                p.QueueRedraw();
        }

        DrivePongKeys();
    }

    // -------------------------------------------------------------------------------------------------
    //  Internals
    // -------------------------------------------------------------------------------------------------

    /// <summary>Apply the <c>hud_skin</c> cvar live: on change, point the skin folder at it + clear the texture
    /// cache so panels reload art, and re-resolve every panel's config.</summary>
    private void SyncSkin()
    {
        string skin = global::XonoticGodot.Game.Menu.MenuState.Cvars.GetString("hud_skin");
        if (string.IsNullOrWhiteSpace(skin)) skin = "luma";
        if (skin == _skin) return;
        _skin = skin;
        HudSkin.SkinName = skin;
        TextureCache.Clear();
        foreach (HudPanel p in _panels) p.InvalidateConfig();
    }

    private void OnViewportResized()
    {
        foreach (HudPanel p in _panels) p.InvalidateConfig();
    }

    private T Get<T>() where T : HudPanel
    {
        foreach (HudPanel p in _panels)
            if (p is T match) return match;
        return null!;
    }

    /// <summary>Public accessor for a discovered panel by type (the net/match layer feeds the newer panels —
    /// Score/Pickup/Chat/StrafeHud/QuickMenu/PressedKeys/MinigameHelp — that have no dedicated typed handle).
    /// Returns null if no such panel was discovered.</summary>
    public T? GetPanel<T>() where T : HudPanel
    {
        foreach (HudPanel p in _panels)
            if (p is T match) return match;
        return null;
    }

    /// <summary>Force a panel visible/hidden regardless of the StartHidden default (net/match layer gate). The
    /// per-frame loop preserves whatever Visible state callers set (it never seizes Visible for normal panels).</summary>
    public void SetPanelVisible<T>(bool visible) where T : HudPanel
    {
        T? p = GetPanel<T>();
        if (p is not null) p.Visible = visible;
    }

    private void Add(HudPanel panel)
    {
        panel.MouseFilter = Control.MouseFilterEnum.Ignore; // HUD never eats input (QC hud_cursormode off)
        AddChild(panel);
        _panels.Add(panel);
    }

    private void ApplyPlayer()
    {
        if (HealthArmor is null) return; // not built yet (SetPlayer called before _Ready)
        HealthArmor.Player = Player;
        Ammo.Player        = Player;
        Weapons.Player     = Player;
        Powerups.Player    = Player;
        InfoMessages.Player = Player;
        Crosshair.Player   = Player;
        Physics.Player     = Player;
        Scoreboard.LocalPlayer = Player;
    }

    private void DrivePongKeys()
    {
        bool pongShown = PongMoveSink is not null
            && Minigame is { Visible: true, Session: { } s } && s.Game.NetName == "pong"
            && (MinigameMenu is null || !MinigameMenu.Visible);

        int keys = 0;
        if (pongShown)
        {
            if (Input.IsKeyPressed(Key.Up) || Input.IsKeyPressed(Key.Left)) keys |= 0x02;
            if (Input.IsKeyPressed(Key.Down) || Input.IsKeyPressed(Key.Right)) keys |= 0x01;
        }
        _pongKeys = keys;
        if (_pongKeys != _pongKeysOld)
        {
            if (pongShown || _pongKeysOld > 0)
                PongMoveSink?.Invoke(_pongKeys);
            _pongKeysOld = _pongKeys;
        }
    }
}
