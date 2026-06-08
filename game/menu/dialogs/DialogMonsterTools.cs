using Godot;

namespace XonoticGodot.Game.Menu;

/// <summary>
/// The Monster-tools cheat dialog — a faithful C# port of <c>XonoticMonsterToolsDialog_fill</c>
/// (qcsrc/menu/xonotic/dialog_monstertools.qc). It is the small in-game tool that lets a host with cheats on
/// spawn/edit monsters: pick a monster type, Spawn/Remove it, choose its move-target behaviour, and recolor
/// its skin. Every control binds the same engine cvar the QC binds and every button issues the same console
/// command string through <see cref="Widgets.CommandButton"/>.
///
/// FAITHFUL UI NOW: the cvar bindings are real (they write the shared <see cref="MenuState.Cvars"/> store),
/// but the command buttons (<c>spawnmob</c>, <c>killmob</c>, <c>editmob …</c>) drive the server-side monster
/// cheat backend XonoticGodot does not have yet, so they route through <see cref="MenuCommand"/> and are logged
/// inert until that backend exists. The QC "OK" button just closed the dialog (<c>Dialog_Close</c>); here that
/// is the standard Back.
/// </summary>
public partial class DialogMonsterTools : MenuScreen
{
    // QC radio group 2 over menu_monsters_edit_spawn — the monster type to spawn (netname value).
    private static readonly (string Value, string Label)[] Monsters =
    {
        ("zombie", "Zombie"),
        ("spider", "Spider"),
        ("golem",  "Golem"),
        ("mage",   "Mage"),
        ("wyvern", "Wyvern"),
    };

    // QC radio group 3 over menu_monsters_edit_movetarget — what a spawned monster does.
    private static readonly (string Value, string Label)[] MoveTargets =
    {
        ("1", "Follow"),
        ("2", "Wander"),
        ("3", "Spawnpoint"),
        ("4", "No moving"),
    };

    protected override void BuildUi()
    {
        Name = "DialogMonsterTools"; // QC ATTRIB name "MonsterTools"

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Monster Tools"));

        // The dialog is short; a single scrolling column keeps it consistent with the other tool dialogs.
        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        root.AddChild(scroll);

        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(box);

        // ---- Monster: -----------------------------------------------------------------------------------
        box.AddChild(Ui.Label("Monster:")); // QC makeXonoticTextLabel(0, _("Monster:"))

        var monsterGroup = new ButtonGroup();
        var monsterRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        monsterRow.AddThemeConstantOverride("separation", 8);
        foreach ((string value, string label) in Monsters)
            monsterRow.AddChild(Widgets.RadioButton("menu_monsters_edit_spawn", value, label, monsterGroup));
        box.AddChild(monsterRow);

        // Spawn / Remove — QC "spawnmob $menu_monsters_edit_spawn $menu_monsters_edit_movetarget" / "killmob".
        var spawnRemove = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        spawnRemove.AddThemeConstantOverride("separation", 8);
        spawnRemove.AddChild(Widgets.CommandButton("Spawn",
            "spawnmob $menu_monsters_edit_spawn $menu_monsters_edit_movetarget"));
        spawnRemove.AddChild(Widgets.CommandButton("Remove", "killmob"));
        box.AddChild(spawnRemove);

        // ---- Move target: -------------------------------------------------------------------------------
        // QC: a "Move target:" command button (editmob movetarget ...) followed by the group-3 radio set.
        var moveRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        moveRow.AddThemeConstantOverride("separation", 8);
        var moveBtn = Widgets.CommandButton("Move target:", "editmob movetarget $menu_monsters_edit_movetarget");
        moveBtn.CustomMinimumSize = new Vector2(160, 36);
        moveBtn.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        moveRow.AddChild(moveBtn);

        var moveGroup = new ButtonGroup();
        foreach ((string value, string label) in MoveTargets)
            moveRow.AddChild(Widgets.RadioButton("menu_monsters_edit_movetarget", value, label, moveGroup));
        box.AddChild(moveRow);

        box.AddChild(Ui.Spacer());

        // ---- Colors: ------------------------------------------------------------------------------------
        box.AddChild(Ui.Label("Colors:")); // QC makeXonoticTextLabel(0, _("Colors:"))

        // Set skin: command button + skin slider (QC "editmob skin $menu_monsters_edit_skin", slider 0..99).
        var skinRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        skinRow.AddThemeConstantOverride("separation", 8);
        var skinBtn = Widgets.CommandButton("Set skin:", "editmob skin $menu_monsters_edit_skin");
        skinBtn.CustomMinimumSize = new Vector2(160, 36);
        skinBtn.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        skinRow.AddChild(skinBtn);
        skinRow.AddChild(Widgets.Slider("menu_monsters_edit_skin", 0f, 99f, 1f));
        box.AddChild(skinRow);

        // QC "OK" button (onClick = Dialog_Close). Here the universal Back closes the dialog.
        root.AddChild(MakeButtonBar(MakeButton("Back", GoBack)));
    }
}
