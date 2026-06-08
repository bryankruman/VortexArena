using Godot;

namespace XonoticGodot.Game.Menu;

/// <summary>
/// The Settings dialog — a <see cref="TabContainer"/> of the seven setting categories, C# successor to
/// <c>XonoticSettingsDialog_fill</c> (qcsrc/menu/xonotic/dialog_settings.qc): Video, Effects, Audio, Game,
/// Input, User, Misc. Each tab is a <see cref="SettingsTab"/> subclass binding the same engine cvars its QC
/// counterpart does (the shared <see cref="MenuState.Cvars"/> store). Settings persist via
/// <see cref="MenuState.SaveUserConfig"/> on Back/Apply; many take effect immediately through each tab's
/// "Apply" command button (<c>vid_restart</c>/<c>snd_restart</c>).
/// </summary>
public partial class DialogSettings : MenuScreen
{
    private TabContainer _tabs = null!;

    /// <summary>Select a tab by its title (e.g. "Audio"); no-op if not found. Used for dev/CI capture.</summary>
    public void SelectTab(string title)
    {
        for (int i = 0; i < _tabs.GetTabCount(); i++)
            if (_tabs.GetTabTitle(i) == title) { _tabs.CurrentTab = i; return; }
    }

    protected override void BuildUi()
    {
        Name = "DialogSettings";

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        if (!HostProvidesTitle) root.AddChild(MakeTitle("Settings"));

        var tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        _tabs = tabs;
        root.AddChild(tabs);

        AddTab(tabs, "Video", new DialogSettingsVideo());
        AddTab(tabs, "Effects", new DialogSettingsEffects());
        AddTab(tabs, "Audio", new DialogSettingsAudio());
        AddTab(tabs, "Game", new DialogSettingsGame());
        AddTab(tabs, "Input", new DialogSettingsInput());
        AddTab(tabs, "User", new DialogSettingsUser());
        AddTab(tabs, "Misc", new DialogSettingsMisc());

        // HUD panels opens the per-panel config directory; Back persists the user's preferences
        // (DP archives changed cvars to config.cfg on exit).
        root.AddChild(MakeButtonBar(
            MakeButton("HUD panels...", () => Menu?.Push(new DialogHudPanels())),
            MakeButton("Back", OnBack)));
    }

    private static void AddTab(TabContainer tabs, string title, Control tab)
    {
        tab.Name = title;
        tabs.AddChild(tab);
    }

    private void OnBack()
    {
        MenuState.SaveUserConfig();
        GoBack();
    }
}
