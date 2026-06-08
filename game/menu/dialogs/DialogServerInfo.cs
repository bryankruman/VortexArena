using Godot;

namespace XonoticGodot.Game.Menu;

/// <summary>
/// Server-info popup — a faithful C# port of <c>XonoticServerInfoDialog</c>
/// (qcsrc/menu/xonotic/dialog_multiplayer_join_serverinfo.qc) together with its two tabs,
/// <c>XonoticServerInfoTab</c> (dialog_multiplayer_join_serverinfotab.qc) and <c>XonoticServerToSTab</c>
/// (dialog_multiplayer_join_termsofservice.qc).
///
/// The QC dialog shows the details of the server currently selected in the server browser: it is filled by
/// <c>XonoticServerInfoDialog_loadServerInfo</c>, which reads every field from the engine's host-cache
/// (<c>gethostcachestring</c>/<c>gethostcachenumber</c> over SLIST_FIELD_*) for the highlighted row. XonoticGodot
/// has no server browser / host-cache backend wired in yet, so there is no live "selected server" to read:
/// the labelled detail rows are rendered with empty placeholder values plus an honest note, and the player
/// list / ToS text are rendered empty. The layout, headers, tab split, and the Close / Join! buttons are
/// reproduced faithfully.
///
/// Bottom buttons mirror the QC: "Close" (QC <c>Dialog_Close</c>) and "Join!" (QC <c>Join_Click</c> →
/// <c>localcmd("connect ", me.currentServerCName)</c>). The favorite/bookmark button is commented out in the
/// QC ("// TODO: Add bookmark button here") so it is reproduced here as a disabled, noted placeholder.
/// This dialog binds no cvars (it is a static info layout).
/// </summary>
public partial class DialogServerInfo : MenuScreen
{
    // The address of the selected server. QC stores this in me.currentServerCName (host-cache SLIST_FIELD_CNAME)
    // and Join_Click runs `connect <that address>`. With no server browser wired in there is no live address,
    // so it stays empty and Join! is inert (noted). A future selection backend would set this before Push().
    private readonly string _serverAddress;

    public DialogServerInfo() : this("") { }

    /// <summary>Open the info popup for a specific server address (QC: the selected row's CNAME).</summary>
    public DialogServerInfo(string serverAddress)
    {
        _serverAddress = serverAddress ?? "";
    }

    protected override void BuildUi()
    {
        Name = "DialogServerInfo";

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Server Information"));

        // Honest note: there is no live selected-server data source behind these rows yet.
        var note = MakeLabel("(no server selected — details show placeholders; server browser / host-cache backend pending)");
        note.AddThemeColorOverride("font_color", new Color(0.70f, 0.72f, 0.78f));
        root.AddChild(note);

        // QC builds a tab controller with two tab buttons: "Status" and "Terms of Service".
        var tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        root.AddChild(tabs);

        var status = BuildStatusTab();
        status.Name = "Status";
        tabs.AddChild(status);

        var tos = BuildToSTab();
        tos.Name = "Terms of Service";
        tabs.AddChild(tos);

        // Bottom row: QC has "Close" (left half) and "Join!" (right half); a bookmark/favorite button is
        // present-but-commented-out in the QC, reproduced here as a disabled placeholder.
        var favorite = MakeButton("Add to favorites", AddFavorite);
        favorite.Disabled = true; // QC: button is commented out ("TODO: Add bookmark button here").
        favorite.TooltipText = "Bookmark this server (favorites backend pending)";

        var join = MakeButton("Join!", OnJoin);
        join.TooltipText = "Connect to this server (QC: connect <address>)";

        root.AddChild(MakeButtonBar(
            MakeButton("Close", GoBack),
            favorite,
            join));
    }

    // -----------------------------------------------------------------------------------------------------
    //  "Status" tab — port of XonoticServerInfoTab_fill (dialog_multiplayer_join_serverinfotab.qc).
    //  Each QC row is a "Label:" text-label in column 1 and an (empty) value text-label beside it. The right
    //  half of the QC tab carries the crypto/stats rows and a player list; we lay them in the same order.
    // -----------------------------------------------------------------------------------------------------
    private Control BuildStatusTab()
    {
        // The tab page is a plain Control hosting a ScrollContainer (the page itself is the bounded parent of
        // the scroll, NOT the other way round — we never put the TabContainer inside a ScrollContainer).
        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };

        var pad = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            pad.AddThemeConstantOverride(side, 16);
        scroll.AddChild(pad);

        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 6);
        pad.AddChild(box);

        // Left block of the QC tab: hostname, address, then a gap, then gametype/map/mod/version/settings,
        // a gap, then player counts.
        box.AddChild(DetailRow("Hostname:", "_nameLabel"));
        box.AddChild(DetailRow("Address:", "_cnameLabel"));
        box.AddChild(Ui.Spacer(8));
        box.AddChild(DetailRow("Gametype:", "_typeLabel"));
        box.AddChild(DetailRow("Map:", "_mapLabel"));
        box.AddChild(DetailRow("Mod:", "_modLabel"));
        box.AddChild(DetailRow("Version:", "_versionLabel"));
        box.AddChild(DetailRow("Settings:", "_pureLabel"));
        box.AddChild(Ui.Spacer(8));
        box.AddChild(DetailRow("Players:", "_numPlayersLabel"));
        box.AddChild(DetailRow("Bots:", "_numBotsLabel"));
        box.AddChild(DetailRow("Free slots:", "_numFreeSlotsLabel"));
        box.AddChild(Ui.Spacer(8));

        // Right block of the QC tab (encryption/identity/stats). Laid out as further rows here.
        box.AddChild(DetailRow("Encryption:", "_encryptLabel",
            "Use the `crypto_aeslevel` cvar to change your preferences")); // QC setZonedTooltip
        box.AddChild(DetailRow("ID:", "_keyLabel"));   // QC labels these crossed: keyLabel under "ID:"
        box.AddChild(DetailRow("Key:", "_idLabel"));   // and idLabel under "Key:" (faithful to the QC source).
        box.AddChild(DetailRow("Stats:", "_statsLabel"));

        // QC also shows a "Players:" header + a live player list (makeXonoticPlayerList, from SLIST_FIELD_PLAYERS).
        box.AddChild(Ui.Spacer(8));
        box.AddChild(Ui.Header("Players"));
        var playerList = MakeLabel("(player list — server browser / host-cache backend pending)");
        playerList.AddThemeColorOverride("font_color", new Color(0.70f, 0.72f, 0.78f));
        box.AddChild(playerList);

        return scroll;
    }

    /// <summary>
    /// A "Label:" + value row mirroring one QC <c>TR</c> of the info tab (a TextLabel followed by an
    /// initially-empty value TextLabel). The value label is empty (no live host-cache data); names match the
    /// QC field names in comments so the mapping is traceable. <paramref name="fieldComment"/> only documents
    /// which QC <c>me.*Label</c> this stands in for.
    /// </summary>
    private static HBoxContainer DetailRow(string label, string fieldComment, string tooltip = "")
    {
        // Empty value placeholder (QC fills this from gethostcache*; no backend → left blank).
        var value = MakeLabel("");
        value.Name = fieldComment.TrimStart('_'); // e.g. "nameLabel" — traceability to the QC entity field.
        value.TooltipText = tooltip;
        var row = MakeRow(label, value);
        return row;
    }

    // -----------------------------------------------------------------------------------------------------
    //  "Terms of Service" tab — port of XonoticServerToSTab_fill (dialog_multiplayer_join_termsofservice.qc).
    //  QC is a single full-size text box that color-renders the ToS text fetched from the server's download
    //  URL (url_single_fopen). With no host-cache / urllib backend the default QC text is shown.
    // -----------------------------------------------------------------------------------------------------
    private Control BuildToSTab()
    {
        var pad = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            pad.AddThemeConstantOverride(side, 16);

        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 8);
        pad.AddChild(box);

        // QC default when no ToS is specified by the server (XonoticServerInfoDialog_loadServerInfo).
        var text = new TextEdit
        {
            Text = "No Terms of Service specified",
            Editable = false,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            WrapMode = TextEdit.LineWrappingMode.Boundary,
        };
        box.AddChild(text);

        var note = MakeLabel("(Terms of Service text is fetched per-server over the network — fetch backend pending)");
        note.AddThemeColorOverride("font_color", new Color(0.70f, 0.72f, 0.78f));
        box.AddChild(note);

        return pad;
    }

    // -----------------------------------------------------------------------------------------------------
    //  Actions
    // -----------------------------------------------------------------------------------------------------

    /// <summary>QC <c>Join_Click</c>: <c>localcmd("connect ", me.currentServerCName, "\n")</c>.</summary>
    private void OnJoin()
    {
        // With no selected server there is no live address; only issue the command when one was supplied.
        if (!string.IsNullOrEmpty(_serverAddress))
            MenuCommand.Run($"connect {_serverAddress}");
        else
            GD.Print("[DialogServerInfo] Join!: no server address (server browser backend pending) — inert.");
    }

    /// <summary>QC bookmark button (commented out): would run ServerList_Favorite_Click on the selected row.</summary>
    private void AddFavorite()
    {
        // Inert: favorites backend pending; the QC button itself is commented out.
    }
}
