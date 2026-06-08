using System.Globalization;
using Godot;
using XonoticGodot.Engine.Simulation;

namespace XonoticGodot.Game.Menu;

/// <summary>
/// The multiplayer Profile dialog — a faithful C# port of <c>XonoticProfileTab_fill</c>
/// (qcsrc/menu/xonotic/dialog_multiplayer_profile.qc). The player's identity: name, model, the two
/// player colors (glowing/shirt and detail/pants), the statistics-tracking opt-ins, and a language
/// selector. Every control binds the same engine cvar the QC binds, through the shared
/// <see cref="MenuState.Cvars"/> store.
///
/// FAITHFUL UI NOW — three pieces are data-driven by engine systems XonoticGodot does not have yet and are
/// rendered inert (controls present, no fabricated data):
///   * the player <b>model preview</b> image and its <c>&lt;&lt;</c>/<c>&gt;&gt;</c> cycler are driven by
///     <c>XonoticPlayerModelSelector</c> globbing the model datafiles (playermodel.qc); here the model is a
///     plain text field bound to <c>_cl_playermodel</c> with a placeholder preview panel;
///   * the <b>statistics list</b> (<c>makeXonoticStatsList</c>) reads server-side stats — rendered as an
///     empty note;
///   * the colorpicker + charmap that the QC attaches to the name box are text-entry helpers — omitted.
///
/// The two color controls edit the single packed integer cvar <c>_cl_color</c> exactly as the QC
/// <c>XonoticColorButton</c> does: glowing color is the low nibble (<c>color &amp; 15</c>, cvarPart 0),
/// detail color the high nibble (<c>(color &amp; 240) / 16</c>, cvarPart 1). See <see cref="ProfileColorNibble"/>.
/// </summary>
public partial class DialogMultiplayerProfile : MenuScreen
{
    // QC profileApplyButton: makeXonoticCommandButton(_("Apply immediately"), ..., COMMANDBUTTON_APPLY).
    // Same command string the QC issues on apply (color -1 -1 re-applies _cl_color; name/model/skin).
    private const string ApplyCommand =
        "color -1 -1; name \"$_cl_name\"; playermodel $_cl_playermodel; playerskin $_cl_playerskin";

    protected override void BuildUi()
    {
        Name = "DialogMultiplayerProfile";

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Profile"));

        // The QC lays Name/Model on the left half and Statistics/Country on the right half. We stack them
        // vertically inside a scroll region (single-column) — same controls, same order, same cvars.
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        root.AddChild(scroll);

        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(box);

        // ==============
        //  NAME SECTION
        // ==============
        box.AddChild(Ui.Header("Name"));

        // QC: a bold title-size text label that mirrors the typed name (allowColors). Bound preview, read-only.
        var namePreview = new ProfileNamePreview();
        box.AddChild(namePreview);

        // QC makeXonoticInputBox_T(1, "_cl_name", ...). Keep the box reference so the colorpicker + charmap
        // below can type ^-codes / glyphs into it (QC attaches both to this box).
        var nameBox = Widgets.InputBox("_cl_name", "Player", "Name under which you will appear in the game");
        box.AddChild(nameBox);

        // QC attaches makeXonoticColorpicker(box) + makeXonoticCharmap(box) here — the HSL name color picker
        // (inserts a ^xRGB code at the caret) + the character map (inserts a glyph at the caret).
        box.AddChild(HslColorPicker.ForNameBox(nameBox));
        box.AddChild(new CharmapPicker(nameBox));

        box.AddChild(Ui.Spacer());

        // ===============
        //  MODEL SECTION
        // ===============
        box.AddChild(Ui.Header("Model"));

        // QC: << / >> buttons flank makeXonoticPlayerModelSelector() (the model preview cycled by the
        // datafile glob). The selector writes _cl_playermodel (+ _cl_playerskin) deferred — NOT applied live
        // (the Apply button below applies). This replaces the old inert preview panel + raw text field.
        box.AddChild(new PlayerModelSelector());

        box.AddChild(Ui.Spacer());

        // QC: "Glowing color" — XonoticColorButton(group 1, cvarPart 0) editing the low nibble of _cl_color.
        box.AddChild(Ui.Header("Glowing color"));
        box.AddChild(new ProfileColorNibble(pants: false));

        // QC: "Detail color" — XonoticColorButton(group 2, cvarPart 1) editing the high nibble of _cl_color.
        box.AddChild(Ui.Header("Detail color"));
        box.AddChild(new ProfileColorNibble(pants: true));

        box.AddChild(Ui.Spacer());

        // ====================
        //  STATISTICS SECTION
        // ====================
        box.AddChild(Ui.Header("Statistics"));

        // QC makeXonoticCheckBox(0, "cl_allow_uidtracking", ...) (sendCvars).
        box.AddChild(Widgets.CheckBox("cl_allow_uidtracking",
            "Allow player statistics to track your client"));

        // QC: the next two depend on cl_allow_uidtracking == 1 (setDependent(...,1,1)).
        var uid2name = Widgets.CheckBox("cl_allow_uid2name",
            "Allow player statistics to use your nickname");
        box.AddChild(uid2name);
        Dependent.Bind(uid2name, "cl_allow_uidtracking", 1, 1);

        var uidranking = Widgets.CheckBox("cl_allow_uidranking",
            "Allow player statistics to rank you in leaderboards");
        box.AddChild(uidranking);
        Dependent.Bind(uidranking, "cl_allow_uidtracking", 1, 1);

        // QC makeXonoticStatsList() — server-fed stats list. Backend pending: honest empty note.
        box.AddChild(Ui.Label("(player statistics list — stats backend pending)"));

        box.AddChild(Ui.Spacer());

        // =================
        //  COUNTRY SECTION
        // =================
        // QC: "Select language..." button -> localcmd("menu_cmd languageselect"). Same command string.
        box.AddChild(Widgets.CommandButton("Select language...", "menu_cmd languageselect"));

        box.AddChild(Ui.Spacer());

        // QC: the profile Apply button spans the bottom row (re-applies colors/name/model/skin live).
        var apply = Widgets.CommandButton("Apply immediately", ApplyCommand);

        // Bottom bar: Apply (QC bottom-row apply button) + Back (the dialog's close).
        root.AddChild(MakeButtonBar(apply, MakeButton("Back", GoBack)));
    }
}

/// <summary>
/// A read-only colorized echo of the <c>_cl_name</c> cvar — the C# stand-in for the QC bold title label that
/// mirrors the typed name. A <see cref="RichTextLabel"/> rendering BBCode so the player's Quake <c>^</c> colour
/// codes show in colour, exactly like the engine's colour-coded name draw (via <see cref="MenuColorCodes"/>).
/// </summary>
public partial class ProfileNamePreview : RichTextLabel
{
    private static CvarService Cvars => MenuState.Cvars;

    public ProfileNamePreview()
    {
        BbcodeEnabled = true;
        FitContent = true;
        ScrollActive = false;
        AutowrapMode = TextServer.AutowrapMode.Off;
        CustomMinimumSize = new Vector2(0, 36);
        AddThemeFontSizeOverride("normal_font_size", 28);
        AddThemeColorOverride("default_color", MenuSkin.Bright);
        if (MenuSkin.BoldFont is { } bold) AddThemeFontOverride("normal_font", bold);
    }

    public override void _EnterTree() { Cvars.Changed += OnCvarChanged; Refresh(); }
    public override void _ExitTree() { Cvars.Changed -= OnCvarChanged; }

    private void OnCvarChanged(string name) { if (name == "_cl_name") Refresh(); }

    private void Refresh()
    {
        string raw = Cvars.GetString("_cl_name");
        if (string.IsNullOrEmpty(raw)) raw = "Player";
        Text = "[center]" + MenuColorCodes.ToBBCode(raw) + "[/center]";
    }
}

/// <summary>
/// One of the two player-color controls — the C# successor to a row of QC <c>XonoticColorButton</c>s. Both
/// edit the single packed integer cvar <c>_cl_color</c>: the glowing/shirt color lives in the low nibble
/// (<c>color &amp; 15</c>), the detail/pants color in the high nibble (<c>(color &amp; 240) / 16</c>). Presented
/// as a dropdown of the 16 palette colors with a live swatch beside it; selecting rewrites just that
/// nibble (preserving the other), exactly as <c>XonoticColorButton_saveCvars</c> does.
/// </summary>
public partial class ProfileColorNibble : HBoxContainer
{
    // The 16 Xonotic palette entries (qcsrc/lib/color.qh colormapPaletteColor_); index 15 is the animated
    // "rainbow" — shown here as a representative static swatch.
    private static readonly (string Name, Color Swatch)[] Palette =
    {
        ("White",       new Color(1.00f, 1.00f, 1.00f)),
        ("Orange",      new Color(1.00f, 0.33f, 0.00f)),
        ("Sea green",   new Color(0.00f, 1.00f, 0.50f)),
        ("Green",       new Color(0.00f, 1.00f, 0.00f)),
        ("Red",         new Color(1.00f, 0.00f, 0.00f)),
        ("Sky blue",    new Color(0.00f, 0.67f, 1.00f)),
        ("Cyan",        new Color(0.00f, 1.00f, 1.00f)),
        ("Lime",        new Color(0.50f, 1.00f, 0.00f)),
        ("Violet",      new Color(0.50f, 0.00f, 1.00f)),
        ("Magenta",     new Color(1.00f, 0.00f, 1.00f)),
        ("Pink",        new Color(1.00f, 0.00f, 0.50f)),
        ("Blue",        new Color(0.00f, 0.00f, 1.00f)),
        ("Yellow",      new Color(1.00f, 1.00f, 0.00f)),
        ("Royal blue",  new Color(0.00f, 0.33f, 1.00f)),
        ("Gold",        new Color(1.00f, 0.67f, 0.00f)),
        ("Rainbow",     new Color(0.70f, 0.70f, 0.70f)),
    };

    private const string Cvar = "_cl_color";

    private static CvarService Cvars => MenuState.Cvars;

    private readonly bool _pants;
    private readonly OptionButton _choices;
    private readonly ColorRect _swatch;
    private bool _updating;

    /// <param name="pants">true = detail color (high nibble); false = glowing color (low nibble).</param>
    public ProfileColorNibble(bool pants)
    {
        _pants = pants;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 10);

        _swatch = new ColorRect { CustomMinimumSize = new Vector2(28, 28), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        AddChild(_swatch);

        _choices = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        for (int i = 0; i < Palette.Length; i++)
            _choices.AddItem(Palette[i].Name, i);
        _choices.ItemSelected += OnItemSelected;
        AddChild(_choices);
    }

    public override void _EnterTree() { Cvars.Changed += OnCvarChanged; Refresh(); }
    public override void _ExitTree() { Cvars.Changed -= OnCvarChanged; }

    private void OnCvarChanged(string name) { if (name == Cvar) Refresh(); }

    /// <summary>Extract this control's nibble from the packed _cl_color value.</summary>
    private int CurrentIndex()
    {
        int packed = (int)Cvars.GetFloat(Cvar);
        return _pants ? (packed & 240) / 16 : packed & 15;
    }

    private void Refresh()
    {
        if (_updating) return;
        _updating = true;
        int idx = Mathf.Clamp(CurrentIndex(), 0, Palette.Length - 1);
        _choices.Selected = idx;
        _swatch.Color = Palette[idx].Swatch;
        _updating = false;
    }

    private void OnItemSelected(long index)
    {
        if (_updating || index < 0 || index >= Palette.Length) return;
        int packed = (int)Cvars.GetFloat(Cvar);
        // Rewrite only our nibble, preserving the other (mirrors XonoticColorButton_saveCvars).
        packed = _pants
            ? (packed & 15) + (int)index * 16
            : (packed & 240) + (int)index;
        Cvars.Set(Cvar, packed.ToString(CultureInfo.InvariantCulture));
        Cvars.MarkArchived(Cvar);
        _swatch.Color = Palette[(int)index].Swatch;
    }
}
