// Port of qcsrc/menu/command/menu_cmd.qc (the directmenu/m_goto "by-name dialog" lookup) — the C# stand-in for
// FOREACH_ENTITY_ORDERED(it.name != "", ...): a name→dialog map so `menu_cmd directmenu <name>` and the
// nexposee/servers/profile/settings nav verbs can resolve a dialog the way the QC menu resolves an entity by
// its .name field.
using System;
using System.Collections.Generic;
using Godot;

namespace XonoticGodot.Game.Menu;

/// <summary>
/// Maps the QuakeC dialog <c>name</c> strings (the identifiers <c>menu_cmd directmenu &lt;name&gt;</c> and the
/// <c>nexposee</c>/<c>servers</c>/<c>profile</c>/<c>settings</c>/… verbs reference) to a factory that builds the
/// corresponding port screen. In QC every dialog registers a <c>.name</c> and <c>m_goto</c> finds it by that
/// name; here we keep an explicit table because the port's screens are concrete Godot controls rather than a
/// uniform entity tree. Names absent from the table return <c>null</c> (the caller logs, mirroring QC's
/// <c>LOG_INFO "Invalid command"</c>).
///
/// A few QC names take a sub-tab: <c>inputsettings</c>/<c>videosettings</c> open the Settings dialog focused on
/// the Input/Video tab (QC has dedicated <c>inputsettings</c>/<c>videosettings</c> dialog entities; the port
/// folds them into the one Settings dialog + a tab select, which is the faithful visible result).
/// </summary>
public static class MenuDialogRegistry
{
    // QC dialog .name → screen factory. Names match the strings menu_cmd.qc / dialog_gamemenu.qc use.
    private static readonly Dictionary<string, Func<Control>> Factories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nexposee"]    = () => new MainMenu(),                       // the main-menu fan (QC nexposee)
        ["servers"]     = () => new MultiplayerScreen(),             // the server browser host (QC dialog_multiplayer w/ Servers tab)
        ["profile"]     = () => new DialogMultiplayerProfile(),      // QC dialog_multiplayer_profile
        ["settings"]    = () => new DialogSettings(),                // QC dialog_settings
        ["inputsettings"]  = () => MakeSettings("Input"),           // QC inputsettings → Settings on the Input tab
        ["videosettings"]  = () => MakeSettings("Video"),           // QC videosettings → Settings on the Video tab
        ["guide"]       = () => new DialogMediaGuide(),             // QC dialog_multiplayer_media_screenshot's guide → the entry guide
        ["quitdialog"]  = () => new QuitDialog(),                   // QC menu_showquitdialog
        ["hudpanels"]   = () => new DialogHudPanels(),              // HUD-panel config host (menu_showhudoptions)
        // QC `menu_cmd skinselect`/`languageselect` remap to the skinselector/languageselector dialogs. Those QC
        // dialogs are the skin list / language list; the port folds both into the Settings dialog's User tab
        // (dialog_settings_user.qc — the faithful home of both pickers), so the *select overlay verbs resolve there.
        ["skinselector"]     = () => MakeSettings("User"),
        ["languageselector"] = () => MakeSettings("User"),
    };

    /// <summary>
    /// Build the screen registered under <paramref name="name"/>, or <c>null</c> when the name isn't registered
    /// (the caller logs, like QC's invalid-command path). Case-insensitive on the name.
    /// </summary>
    public static Control? Create(string name)
        => Factories.TryGetValue(name, out Func<Control>? f) ? f() : null;

    /// <summary>True when <paramref name="name"/> resolves to a screen.</summary>
    public static bool Has(string name) => Factories.ContainsKey(name);

    private static Control MakeSettings(string tab)
    {
        var s = new DialogSettings();
        // SelectTab walks the TabContainer once it's in the tree; defer so _Ready has built the tabs.
        s.Ready += () => s.SelectTab(tab);
        return s;
    }
}
