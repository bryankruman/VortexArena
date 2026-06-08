using Godot;

namespace XonoticGodot.Game.Menu;

/// <summary>
/// Music Player tab — a faithful C# port of <c>XonoticMusicPlayerTab_fill</c>
/// (qcsrc/menu/xonotic/dialog_media_musicplayer.qc). The QC is a two-pane layout: a filterable
/// <c>XonoticSoundList</c> (engine cdtracks/*.ogg) on the left with Add / Add all / Set as menu
/// track / Reset default menu track, and an <c>XonoticPlayList</c> on the right with a "Random
/// order" toggle (<c>music_playlist_random0</c>) and Stop/Play/Pause/Prev/Next + Remove/Remove all
/// transport.
///
/// FAITHFUL UI NOW: XonoticGodot has no music/cdtracks playback backend, so both the sound list and the
/// playlist are rendered as empty <see cref="ItemList"/>s with honest notes and every transport /
/// list-management button is inert (routed through <see cref="MenuCommand"/> with representative
/// <c>cd …</c> commands, logged as having no backend). Live bindings: the "Random order" checkbox
/// (<c>music_playlist_random0</c>) and the Music volume slider (<c>bgmvolume</c>, per task) — both
/// bind the same cvars the engine uses.
/// </summary>
public partial class DialogMediaMusicPlayer : Control
{
    public override void _Ready()
    {
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 16);
        AddChild(margin);

        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 8);
        margin.AddChild(box);

        // Two side-by-side panes (QC: soundList in the left half, playList in the right half).
        var columns = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 16);
        box.AddChild(columns);

        columns.AddChild(BuildSoundPane());
        columns.AddChild(BuildPlaylistPane());

        // Music volume — not in the QC media dialog (it lives in audio settings) but requested for this port;
        // binds the same engine cvar (bgmvolume, linear 0..1 in DP) with a percent readout.
        box.AddChild(Ui.Spacer(6f));
        box.AddChild(Ui.Row("Music volume:", Widgets.Slider("bgmvolume", 0f, 1f, 0.05f, format: Percent)));
    }

    /// <summary>Left pane: filter + sound list + Add / Add all / Set as menu track / Reset default menu track.</summary>
    private static VBoxContainer BuildSoundPane()
    {
        var pane = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        pane.AddThemeConstantOverride("separation", 8);

        // QC: makeXonoticInputBox + SoundList_Filter_Change — no cvar, inert.
        var filter = new LineEdit { PlaceholderText = "Filter tracks…", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        pane.AddChild(Ui.Row("Filter:", filter, 70f));

        var sounds = new ItemList
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 200),
        };
        pane.AddChild(sounds);
        pane.AddChild(Ui.Label("(track list — cdtracks/music backend pending)"));

        // QC: Add / Add all (SoundList_Add / SoundList_Add_All) — append to the playlist (inert).
        var addRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        addRow.AddThemeConstantOverride("separation", 10);
        addRow.AddChild(Ui.Button("Add", () => MenuCommand.Run("menu_cmd music_add")));
        addRow.AddChild(Ui.Button("Add all", () => MenuCommand.Run("menu_cmd music_add_all")));
        pane.AddChild(addRow);

        // QC: Set as menu track / Reset default menu track (SoundList_Menu_Track_Change / _Reset) — inert.
        pane.AddChild(Ui.Button("Set as menu track", () => MenuCommand.Run("menu_cmd music_set_menu_track")));
        pane.AddChild(Ui.Button("Reset default menu track", () => MenuCommand.Run("menu_cmd music_reset_menu_track")));

        return pane;
    }

    /// <summary>Right pane: "Random order" toggle + playlist + Stop/Play/Pause/Prev/Next + Remove/Remove all.</summary>
    private static VBoxContainer BuildPlaylistPane()
    {
        var pane = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        pane.AddThemeConstantOverride("separation", 8);

        // QC: TextLabel _("Playlist:") + makeXonoticCheckBox(0, "music_playlist_random0", _("Random order")) — live.
        var header = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        header.AddThemeConstantOverride("separation", 10);
        var label = Ui.Label("Playlist:");
        label.CustomMinimumSize = new Vector2(70, 0);
        label.VerticalAlignment = VerticalAlignment.Center;
        header.AddChild(label);
        var random = Widgets.CheckBox("music_playlist_random0", "Random order");
        random.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(random);
        pane.AddChild(header);

        var playlist = new ItemList
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 200),
        };
        pane.AddChild(playlist);
        pane.AddChild(Ui.Label("(playlist — music playback backend pending)"));

        // QC transport: Stop / Play / Pause / Prev / Next (StopSound_Click … NextSound_Click) — inert.
        var transport = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        transport.AddThemeConstantOverride("separation", 6);
        transport.AddChild(Ui.Button("Stop", () => MenuCommand.Run("cd stop")));
        transport.AddChild(Ui.Button("Play", () => MenuCommand.Run("cd play")));
        transport.AddChild(Ui.Button("Pause", () => MenuCommand.Run("cd pause")));
        transport.AddChild(Ui.Button("Prev", () => MenuCommand.Run("cd prev")));
        transport.AddChild(Ui.Button("Next", () => MenuCommand.Run("cd next")));
        pane.AddChild(transport);

        // QC: Remove / Remove all (PlayList_Remove / PlayList_Remove_All) — inert.
        var removeRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        removeRow.AddThemeConstantOverride("separation", 10);
        removeRow.AddChild(Ui.Button("Remove", () => MenuCommand.Run("menu_cmd music_remove")));
        removeRow.AddChild(Ui.Button("Remove all", () => MenuCommand.Run("menu_cmd music_remove_all")));
        pane.AddChild(removeRow);

        return pane;
    }

    /// <summary>Render a linear 0..1 volume as a percentage (matches the audio-settings readout).</summary>
    private static string Percent(float v) => $"{Mathf.RoundToInt(v * 100f)}%";
}
