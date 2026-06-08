using Godot;

namespace XonoticGodot.Game.Menu;

/// <summary>
/// Guide tab — a faithful C# port of <c>XonoticGuideTab_fill</c>
/// (qcsrc/menu/xonotic/dialog_media_guide.qc). The QC is a three-pane layout: a topic list (the
/// <c>TOPICS</c> macro in guide/guide.qh — Introduction, Movement, Gametypes, Weapons, Items,
/// Powerups, Buffs, Nades, Monsters, Vehicles, Turrets, Mutators, Mods), an entry list with a
/// keyword filter box, and a description pane fed by the selected entry's <c>describe()</c>.
///
/// FAITHFUL UI NOW: the topic list is real and complete (it's a fixed set, so we populate it
/// faithfully). The entry list and descriptions come from the in-game registries (weapons, items,
/// monsters, …) which XonoticGodot's menu can't yet enumerate, so the entry list is an empty
/// <see cref="ItemList"/> with an honest note, the filter box is inert, and the description pane
/// shows a short placeholder.
/// </summary>
public partial class DialogMediaGuide : Control
{
    // QC TOPICS(X) in guide/guide.qh — the fixed topic set, in order (labels are the _() strings).
    private static readonly string[] Topics =
    {
        "Introduction", "Movement", "Gametypes", "Weapons", "Items", "Powerups",
        "Buffs", "Nades", "Monsters", "Vehicles", "Turrets", "Mutators", "Mods",
    };

    private Label _description = null!;

    public override void _Ready()
    {
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 16);
        AddChild(margin);

        // Three side-by-side panes (QC widths 1.5 / 1.75 / 2.75).
        var columns = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 12);
        margin.AddChild(columns);

        columns.AddChild(BuildTopicPane());
        columns.AddChild(BuildEntryPane());
        columns.AddChild(BuildDescriptionPane());
    }

    /// <summary>Left pane: the topic list (QC me.topicList) — faithfully populated.</summary>
    private Control BuildTopicPane()
    {
        var pane = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        pane.AddThemeConstantOverride("separation", 6);
        pane.AddChild(Ui.Header("Topics"));

        var topicList = new ItemList
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(150, 240),
        };
        foreach (string t in Topics)
            topicList.AddItem(t);
        topicList.ItemSelected += OnTopicSelected;
        pane.AddChild(topicList);
        return pane;
    }

    /// <summary>Middle pane: the entry list (QC me.entryList) + keyword filter (QC stringFilterBox).</summary>
    private Control BuildEntryPane()
    {
        var pane = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        pane.AddThemeConstantOverride("separation", 6);
        pane.AddChild(Ui.Header("Entries"));

        var entryList = new ItemList
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(160, 200),
        };
        pane.AddChild(entryList);
        pane.AddChild(Ui.Label("(entry list — guide registry enumeration pending)"));

        // QC: stringFilterBox (keyword filter), inert without the registry.
        var filter = new LineEdit { PlaceholderText = "Filter…", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        pane.AddChild(Ui.Row("Filter:", filter, 60f));
        return pane;
    }

    /// <summary>Right pane: the description pane (QC me.descriptionPane).</summary>
    private Control BuildDescriptionPane()
    {
        var pane = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        pane.AddThemeConstantOverride("separation", 6);
        pane.AddChild(Ui.Header("Description"));

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        _description = Ui.Label("Select a topic, then an entry, to read its description.\n\n(entry descriptions — guide registry pending)");
        _description.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _description.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(_description);
        pane.AddChild(scroll);
        return pane;
    }

    // QC topicChangeNotify reloads the entry list from the topic's source; we can't enumerate the registry,
    // so reflect the chosen topic in the description placeholder to keep the panes visibly linked.
    private void OnTopicSelected(long index)
    {
        if (index >= 0 && index < Topics.Length)
            _description.Text = $"{Topics[index]}\n\n(entry list / descriptions for this topic — guide registry enumeration pending)";
    }
}
