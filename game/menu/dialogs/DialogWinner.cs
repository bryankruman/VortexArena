using Godot;

namespace XonoticGodot.Game.Menu;

/// <summary>
/// "Winner" popup — a faithful C# port of <c>XonoticWinnerDialog</c>
/// (qcsrc/menu/xonotic/dialog_singleplayer_winner.qc). Shown after winning a single-player campaign level: it
/// is just the <c>/gfx/winner</c> banner image filling the dialog, with an "OK" button beneath
/// (QC <c>Dialog_Close</c> — here the universal Back). The QC also plays MENU_SOUND_WINNER on focus
/// (<c>XonoticWinnerDialog_focusEnter</c>); XonoticGodot's menu has no focus-sound hook wired here, so that cue is
/// omitted (noted).
///
/// The banner is a content texture from the asset repo; we load <c>/gfx/winner</c> if a Godot-importable
/// resource for it exists, otherwise we show an honest placeholder note rather than fabricating the artwork.
/// Binds no cvars (a static image dialog). QC title "Winner".
/// </summary>
public partial class DialogWinner : MenuScreen
{
    // Candidate resource paths for the QC "/gfx/winner" banner (the asset repo is mounted under res://).
    private static readonly string[] WinnerImagePaths =
    {
        "res://assets/data/gfx/winner.png",
        "res://assets/data/gfx/winner.tga",
        "res://gfx/winner.png",
    };

    protected override void BuildUi()
    {
        Name = "DialogWinner"; // QC XonoticWinnerDialog

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Winner"));

        // QC: a single makeXonoticImage("/gfx/winner", -1) spanning the dialog above the OK button.
        Texture2D? banner = LoadWinnerBanner();
        if (banner is not null)
        {
            var image = new TextureRect
            {
                Texture = banner,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill,
            };
            root.AddChild(image);
        }
        else
        {
            // Honest placeholder: the banner artwork isn't available as an importable resource here.
            var placeholder = new CenterContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill,
            };
            var note = MakeLabel("(winner banner image — /gfx/winner asset pending)");
            note.AddThemeColorOverride("font_color", new Color(0.70f, 0.72f, 0.78f));
            note.HorizontalAlignment = HorizontalAlignment.Center;
            placeholder.AddChild(note);
            root.AddChild(placeholder);
        }

        // QC OK button (Dialog_Close) — the universal Back.
        root.AddChild(MakeButtonBar(MakeButton("OK", GoBack)));
    }

    /// <summary>Load the "/gfx/winner" banner if a resource for it exists; otherwise null (show the note).</summary>
    private static Texture2D? LoadWinnerBanner()
    {
        foreach (string path in WinnerImagePaths)
            if (ResourceLoader.Exists(path))
                return ResourceLoader.Load<Texture2D>(path);
        return null;
    }
}
