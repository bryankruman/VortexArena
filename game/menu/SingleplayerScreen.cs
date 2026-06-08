using System;
using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Server; // CampaignCatalog / CampaignLevel

namespace XonoticGodot.Game.Menu;

/// <summary>
/// Singleplayer screen: the campaign level list + an "Instant Action" shortcut. C# successor to
/// <c>XonoticSingleplayerDialog</c> + <c>XonoticCampaignList</c> (qcsrc/menu/xonotic/dialog_singleplayer.qc +
/// campaign.qc). The list is read from the real campaign definition the game ships —
/// <c>maps/campaign&lt;id&gt;.txt</c> (default <c>xonoticbeta</c>) — via <see cref="CampaignCatalog"/>, the
/// same file the server's <see cref="Campaign"/> reads, so every level's map/gametype/bots are authentic.
///
/// Unlock model is faithful to <c>XonoticCampaignList_loadCvars</c>/<c>drawListBoxItem</c>: the saved
/// frontier <c>g_campaign&lt;id&gt;_index</c> is the highest unlocked level; the list reveals exactly
/// <c>frontier+2</c> levels — completed ones marked done, the current one playable, and ONE locked peek shown
/// as "???" (not selectable). Winning the current level advances + persists the frontier (server-side
/// <see cref="Campaign"/> → mirrored to the menu store), so the next level unlocks. Starting a level fires the
/// shared <see cref="CreateGameScreen.StartGameRequested"/> callback with the campaign id + the level index;
/// the host boots a listen server in campaign mode where PreInit/PostInit resolve the authentic
/// gametype/bots/skill/limits/mutators. Instant Action hands off to the full <see cref="CreateGameScreen"/>.
/// </summary>
public partial class SingleplayerScreen : MenuScreen
{
    private ItemList _levelList = null!;
    private Label _description = null!;

    private readonly List<CampaignLevel> _levels = new();
    private string _campaignId = CampaignCatalog.DefaultName;
    private string _campaignTitle = "Campaign";
    private int _frontier;    // QC campaignIndex: highest unlocked level (the current playable one)
    private int _shownCount;  // QC nItems: min(frontier + 2, level count)

    protected override void BuildUi()
    {
        Name = "SingleplayerScreen";

        LoadCampaign();

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        if (!HostProvidesTitle) root.AddChild(MakeTitle("Singleplayer"));
        root.AddChild(MakeHeader(_campaignTitle));

        _levelList = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill };
        _levelList.ItemActivated += _ => OnStartLevel();   // double-click / Enter to play
        _levelList.ItemSelected += OnLevelSelected;        // refresh the briefing text
        root.AddChild(_levelList);

        // Per-level briefing (QC campaign_longdesc), updated as the selection changes.
        _description = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(0, 120),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Top,
        };
        root.AddChild(_description);

        PopulateList();

        // Bottom bar: Back / Instant Action / Play.
        root.AddChild(MakeButtonBar(
            MakeButton("Back", GoBack),
            MakeButton("Instant Action...", OnInstantAction),
            MakeButton("Play level", OnStartLevel)));
    }

    /// <summary>The saved-progress cvar (QC <c>strcat("g_campaign", name, "_index")</c>).</summary>
    private string ProgressCvar => $"g_campaign{_campaignId}_index";

    /// <summary>Read the shipped campaign definition (default <c>xonoticbeta</c>) + the saved frontier.</summary>
    private void LoadCampaign()
    {
        _campaignId = MenuState.Cvars.GetString("g_campaign_name");
        if (string.IsNullOrWhiteSpace(_campaignId))
            _campaignId = CampaignCatalog.DefaultName;

        string path = CampaignCatalog.FileName(_campaignId);
        string? text = MenuState.Vfs is { } vfs && vfs.Exists(path) ? vfs.ReadText(path) : null;

        _levels.Clear();
        _levels.AddRange(CampaignCatalog.Parse(text, out _campaignTitle));
        if (string.IsNullOrWhiteSpace(_campaignTitle))
            _campaignTitle = "Campaign";

        // QC: campaignIndex = bound(0, cvar(g_campaign<id>_index), campaign_entries).
        _frontier = Math.Clamp((int)MenuState.Cvars.GetFloat(ProgressCvar), 0, _levels.Count);

        if (_levels.Count == 0)
            GD.PrintErr($"[Singleplayer] campaign '{path}' not found or empty in the VFS — the level list is empty.");
    }

    /// <summary>QC <c>drawListBoxItem</c>: reveal frontier+2 levels — completed (✓), current, and one locked peek.</summary>
    private void PopulateList()
    {
        _levelList.Clear();
        _shownCount = Math.Min(_frontier + 2, _levels.Count); // QC nItems = min(campaignIndex + 2, campaign_entries)

        for (int i = 0; i < _shownCount; i++)
        {
            CampaignLevel lvl = _levels[i];
            bool unlocked = i <= _frontier;                             // QC: shortdesc shown for i <= campaignIndex
            string mark = i < _frontier ? "✓ " : "";               // QC checkmark for a completed level
            string title = unlocked
                ? (string.IsNullOrWhiteSpace(lvl.ShortDesc) ? lvl.MapName : lvl.ShortDesc)
                : "???";                                                // QC: future level masked
            int idx = _levelList.AddItem($"{mark}Level {lvl.Index + 1}:  {title}");
            if (!unlocked)
                _levelList.SetItemDisabled(idx, true);                  // the locked peek: not selectable (QC clamp)
        }

        if (_shownCount > 0)
        {
            int sel = Math.Min(_frontier, _shownCount - 1);             // QC setSelected(min(campaignIndex, nItems-1))
            _levelList.Select(sel);
            OnLevelSelected(sel);
        }
        else
        {
            _description.Text = "";
        }
    }

    private void OnLevelSelected(long index)
    {
        // QC: longdesc is shown only for unlocked levels (i <= campaignIndex).
        if (index < 0 || index > _frontier || index >= _levels.Count)
        {
            _description.Text = "";
            return;
        }
        // The longdesc stores line breaks as the literal two characters "\n"; render them as real breaks.
        _description.Text = _levels[(int)index].LongDesc.Replace("\\n", "\n");
    }

    /// <summary>Start the selected campaign level via the shared StartGame callback, in campaign mode.</summary>
    private void OnStartLevel()
    {
        if (!_levelList.IsAnythingSelected())
            return;
        int i = _levelList.GetSelectedItems()[0];
        if (i < 0 || i > _frontier || i >= _levels.Count) // can't play a locked level (QC selectedItem <= campaignIndex)
            return;

        CampaignLevel lvl = _levels[i];
        var config = new MatchConfig
        {
            // The menu's pre-resolved copy (loads the BSP + fills bots client-side); the server re-resolves the
            // authoritative gametype/skill/limits/mutators from the campaign file via g_campaign + the index.
            Gametype = lvl.Gametype,
            Map = lvl.MapName,
            BotCount = lvl.Bots,
            BotSkill = lvl.BotSkill,
            TimeLimit = 0,   // campaign limits come from the file (CampaignPostInit), not the menu
            FragLimit = 0,
            CampaignId = _campaignId,
            CampaignIndex = lvl.Index,
        };

        // Reuse the Create screen's wiring point so the host has a single StartGame entry.
        CreateGameScreen.RaiseStartGame(config);
    }

    /// <summary>Instant Action = the full Create-Game flow (pick gametype/map/bots and start).</summary>
    private void OnInstantAction() => Menu?.Push(new CreateGameScreen());
}
