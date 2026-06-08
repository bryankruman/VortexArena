using System;
using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Gameplay;

namespace XonoticGodot.Game.Menu;

/// <summary>
/// "Create Game" screen — pick a gametype, a map, and a bot count, then Start. C# port of the left/right
/// halves of <c>XonoticServerCreateTab</c> (qcsrc/menu/xonotic/dialog_multiplayer_create.qc): the QC has a
/// <c>GametypeList</c>, a <c>MapList</c>, and sliders for time/frag limit, teams, player slots, bot count
/// and bot skill. We keep the load-bearing controls and drive the gametype list straight from the C#
/// <see cref="GameTypes"/> registry — the same catalog the QC builds from <c>common/gametypes</c> — and a
/// real <see cref="MapList"/> scanned from the maps directory.
///
/// Start gathers the choices into a <see cref="MatchConfig"/> and fires <see cref="StartGameRequested"/>.
/// That decouples the menu from the engine: the host wires the callback once to the real client/server
/// bootstrap (the QC equivalent was <c>MapList_LoadMap</c> issuing the map change + bot cvars). Until a
/// handler is attached, Start just logs the config.
/// </summary>
public partial class CreateGameScreen : MenuScreen
{
    /// <summary>
    /// Fired when the user starts a match. The host subscribes once (e.g. at boot) and turns the
    /// <see cref="MatchConfig"/> into a real local server + client connect. Static so any instance of this
    /// screen — and the quick "Create" tab in <see cref="MultiplayerScreen"/> — shares one wiring point.
    /// </summary>
    public static event Action<MatchConfig>? StartGameRequested;

    /// <summary>
    /// Fire <see cref="StartGameRequested"/> from another menu screen (a C# event can only be invoked from
    /// its declaring type). Used by <see cref="SingleplayerScreen"/> so campaign levels share this wiring.
    /// </summary>
    public static void RaiseStartGame(MatchConfig config)
    {
        if (StartGameRequested is null)
            GD.Print($"[Menu] Start game ({config}) — no StartGame handler attached yet.");
        else
            StartGameRequested.Invoke(config);
    }

    private ItemList _gametypeList = null!;
    private ItemList _mapList = null!;
    private HSlider _botCount = null!;
    private Label _botCountValue = null!;
    private OptionButton _botSkill = null!;
    private SpinBox _timeLimit = null!;
    private SpinBox _fragLimit = null!;

    // Optional starting selections (used when the compact Create tab hands off to this screen). Set via
    // the init properties before the screen is pushed; BuildUi (run in _Ready) reads them. We use a
    // parameterless ctor + setters to match the menu's convention and keep Godot's node source-generator
    // happy (Godot node subclasses are instantiated parameterlessly).
    private string? _initialGametype;
    private string? _initialMap;

    /// <summary>Pre-select this gametype (NetName) when the screen builds. Set before pushing.</summary>
    public string? InitialGametype { get => _initialGametype; init => _initialGametype = value; }

    /// <summary>Pre-select this map when the screen builds. Set before pushing.</summary>
    public string? InitialMap { get => _initialMap; init => _initialMap = value; }

    // Bot skill rungs, copied from the QC "skill" mixed-slider labels (dialog_multiplayer_create.qc).
    private static readonly string[] BotSkillNames =
    {
        "Botlike", "Beginner", "You will win", "You can win", "You might win",
        "Advanced", "Expert", "Pro", "Assassin", "Unhuman", "Godlike",
    };

    protected override void BuildUi()
    {
        Name = "CreateGameScreen";

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Create Game"));

        // Three-column body: gametype list, map list, options.
        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 24);
        root.AddChild(body);

        body.AddChild(BuildGametypeColumn());
        body.AddChild(BuildMapColumn());
        body.AddChild(BuildOptionsColumn());

        // Bottom bar: Back + Mutators + Start (the QC create dialog opens the Mutators sub-dialog from here).
        root.AddChild(MakeButtonBar(
            MakeButton("Back", GoBack),
            MakeButton("Mutators...", () => Menu?.Push(new DialogMutators())),
            MakeButton("Start", OnStart)));
    }

    private Control BuildGametypeColumn()
    {
        var col = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(220, 0),
        };
        col.AddThemeConstantOverride("separation", 8);
        col.AddChild(MakeHeader("Gametype"));

        _gametypeList = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill };
        col.AddChild(_gametypeList);

        // Populate from the live GameTypes registry. Each row stores its NetName in metadata so Start can
        // look the gametype back up regardless of display order.
        int selectIdx = 0;
        foreach (var gt in GameTypes.All)
        {
            string label = string.IsNullOrEmpty(gt.DisplayName) ? gt.NetName : gt.DisplayName;
            if (gt.TeamGame)
                label += "  (team)";
            int idx = _gametypeList.AddItem(label);
            _gametypeList.SetItemMetadata(idx, gt.NetName);
            if (_initialGametype is not null && gt.NetName == _initialGametype)
                selectIdx = idx;
        }

        if (GameTypes.All.Count == 0)
        {
            // Registries may not be bootstrapped when the menu is shown in isolation.
            _gametypeList.AddItem("(no gametypes registered)");
            _gametypeList.SetItemDisabled(0, true);
        }
        else
        {
            _gametypeList.Select(selectIdx);
        }

        return col;
    }

    private Control BuildMapColumn()
    {
        var col = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(220, 0),
        };
        col.AddThemeConstantOverride("separation", 8);
        col.AddChild(MakeHeader("Map"));

        _mapList = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill };
        col.AddChild(_mapList);

        int selectIdx = 0;
        bool seenInitial = false;
        IReadOnlyList<string> maps = MapList.Available();
        for (int i = 0; i < maps.Count; i++)
        {
            _mapList.AddItem(maps[i]);
            if (_initialMap is not null && maps[i] == _initialMap)
            {
                selectIdx = i;
                seenInitial = true;
            }
        }

        // A custom map typed in the compact Create tab might not be in the scanned list — add it so the
        // hand-off carries through and it can be selected/started.
        if (!seenInitial && !string.IsNullOrWhiteSpace(_initialMap))
        {
            selectIdx = _mapList.AddItem(_initialMap);
        }

        if (_mapList.ItemCount > 0)
            _mapList.Select(selectIdx);

        return col;
    }

    private Control BuildOptionsColumn()
    {
        var col = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(300, 0),
        };
        col.AddThemeConstantOverride("separation", 10);

        col.AddChild(MakeHeader("Settings"));

        // Time limit (minutes).
        _timeLimit = new SpinBox { MinValue = 0, MaxValue = 60, Step = 1, Value = 10 };
        col.AddChild(MakeRow("Time limit (min):", _timeLimit, 150f));

        // Frag limit.
        _fragLimit = new SpinBox { MinValue = 0, MaxValue = 200, Step = 5, Value = 30 };
        col.AddChild(MakeRow("Frag limit:", _fragLimit, 150f));

        // Bot count slider 0..15 with a live readout (QC "bot_number" slider).
        var botRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _botCount = new HSlider
        {
            MinValue = 0, MaxValue = 15, Step = 1, Value = 4,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        _botCountValue = MakeLabel("4");
        _botCountValue.CustomMinimumSize = new Vector2(28, 0);
        _botCount.ValueChanged += v => _botCountValue.Text = ((int)v).ToString();
        botRow.AddChild(_botCount);
        botRow.AddChild(_botCountValue);
        col.AddChild(MakeRow("Number of bots:", botRow, 150f));

        // Bot skill (QC "skill" mixed-slider, presented as a dropdown).
        _botSkill = new OptionButton();
        for (int i = 0; i < BotSkillNames.Length; i++)
            _botSkill.AddItem($"{i} - {BotSkillNames[i]}", i);
        _botSkill.Select(4); // "You might win", the QC-ish default feel
        col.AddChild(MakeRow("Bot skill:", _botSkill, 150f));

        return col;
    }

    /// <summary>Gather the controls into a <see cref="MatchConfig"/> (null if no gametype is selectable).</summary>
    private MatchConfig? BuildConfig()
    {
        int gtSel = _gametypeList.IsAnythingSelected() ? _gametypeList.GetSelectedItems()[0] : -1;
        string gametype = "";
        if (gtSel >= 0)
        {
            Variant meta = _gametypeList.GetItemMetadata(gtSel);
            if (meta.VariantType == Variant.Type.String)
                gametype = meta.AsString();
        }
        if (string.IsNullOrEmpty(gametype))
            return null;

        string map = _mapList.IsAnythingSelected()
            ? _mapList.GetItemText(_mapList.GetSelectedItems()[0])
            : "";

        return new MatchConfig
        {
            Gametype = gametype,
            Map = map,
            BotCount = (int)_botCount.Value,
            BotSkill = _botSkill.Selected,
            TimeLimit = (int)_timeLimit.Value,
            FragLimit = (int)_fragLimit.Value,
        };
    }

    private void OnStart()
    {
        MatchConfig? config = BuildConfig();
        if (config is null)
        {
            GD.Print("[Menu] Start game: no gametype selected.");
            return;
        }

        // Hand off to the host's bootstrap. The QC path was MapList_LoadMap -> a "map" change plus
        // bot_number / skill / *_override cvars + makeServerSingleplayer(); here we just fire the callback.
        RaiseStartGame(config);
    }
}
