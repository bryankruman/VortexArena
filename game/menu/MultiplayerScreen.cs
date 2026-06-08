using Godot;
using XonoticGodot.Common.Gameplay;

namespace XonoticGodot.Game.Menu;

/// <summary>
/// Multiplayer screen: a server browser plus a compact "Create Game" sub-panel. C# successor to
/// <c>XonoticMultiplayerDialog</c> + its Servers/Create tabs (dialog_multiplayer.qc,
/// dialog_multiplayer_join.qc, dialog_multiplayer_create.qc).
///
/// Layout (a <see cref="TabContainer"/> standing in for the QC tab controller):
///  * "Servers" tab — an <see cref="ItemList"/> driven by a real <see cref="ServerBrowser"/> model
///    (<see cref="ServerEntry"/> rows from saved favorites + a LAN discovery sweep), with Refresh /
///    Connect / favorite controls and an address <see cref="LineEdit"/>. Connect parses the address and
///    raises the browser's <see cref="ServerBrowser.ConnectRequested"/> callback for the net layer to wire.
///  * "Create" tab — a quick gametype list (from the <see cref="GameTypes"/> registry) and a map field,
///    handing off to the full <see cref="CreateGameScreen"/> (pre-seeded with the picks) to start a match.
/// </summary>
public partial class MultiplayerScreen : MenuScreen
{
    // The browser model is process-wide so favorites persist across opening/closing the screen, and the
    // net layer can attach its ConnectRequested handler once.
    public static readonly ServerBrowser Browser = new();

    private ItemList _serverList = null!;
    private LineEdit _addressEdit = null!;
    private ItemList _createGametypeList = null!;
    private LineEdit _createMapEdit = null!;

    protected override void BuildUi()
    {
        Name = "MultiplayerScreen";

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        if (!HostProvidesTitle) root.AddChild(MakeTitle("Multiplayer"));

        var tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        root.AddChild(tabs);

        var servers = BuildServersTab();
        servers.Name = "Servers";
        tabs.AddChild(servers);

        var create = BuildCreateTab();
        create.Name = "Create";
        tabs.AddChild(create);

        // Back + Player setup (the QC multiplayer dialog has a Player Setup tab/button for the profile).
        root.AddChild(MakeButtonBar(
            MakeButton("Back", GoBack),
            MakeButton("Player setup...", () => Menu?.Push(new DialogMultiplayerProfile()))));
    }

    // -------------------------------------------------------------------------------------------------
    //  Servers tab
    // -------------------------------------------------------------------------------------------------

    private Control BuildServersTab()
    {
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 10);

        // Column header row mirroring the QC sort buttons (Name / Map / Type / Players / Ping).
        var header = new HBoxContainer();
        header.AddChild(ColumnLabel("Server", 3));
        header.AddChild(ColumnLabel("Map", 2));
        header.AddChild(ColumnLabel("Type", 2));
        header.AddChild(ColumnLabel("Players", 1));
        header.AddChild(ColumnLabel("Ping", 1));
        col.AddChild(header);

        _serverList = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill };
        _serverList.ItemActivated += _ => OnConnect(); // double-click a row to join
        col.AddChild(_serverList);

        // Initial population (favorites; LAN sweep happens on explicit Refresh to avoid a startup stall).
        RenderServers();

        // Address + favorite row (QC bottom row of the servers tab).
        _addressEdit = new LineEdit { PlaceholderText = "address (ip or ip:port)" };
        col.AddChild(MakeRow("Address:", _addressEdit, 90f));

        // Action buttons: Refresh / Connect / favorite add-remove.
        col.AddChild(MakeButtonBar(
            MakeButton("Refresh", OnRefresh),
            MakeButton("Connect", OnConnect),
            MakeButton("Add favorite", OnAddFavorite),
            MakeButton("Remove favorite", OnRemoveFavorite)));

        return col;
    }

    private static Label ColumnLabel(string text, int stretch)
    {
        var l = MakeLabel(text);
        l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        l.SizeFlagsStretchRatio = stretch;
        return l;
    }

    /// <summary>Render the browser's current <see cref="ServerEntry"/> list into the ItemList.</summary>
    private void RenderServers()
    {
        int previous = _serverList.IsAnythingSelected() ? _serverList.GetSelectedItems()[0] : -1;

        _serverList.Clear();
        var servers = Browser.Servers;
        _renderedRevision = Browser.Revision;
        for (int i = 0; i < servers.Count; i++)
        {
            ServerEntry s = servers[i];
            string star = s.Favorite ? "* " : "  ";
            // Column-aligned text row (the QC renders real columns; we pad text to fake them).
            string row = $"{star}{Trunc(s.Name, 26),-26} {Trunc(s.Map, 14),-14} " +
                         $"{Trunc(s.Gametype, 18),-18} {s.PlayersText,7}  {s.PingText,6}";
            int idx = _serverList.AddItem(row);
            _serverList.SetItemMetadata(idx, s.Address);
        }

        if (servers.Count == 0)
        {
            int idx = _serverList.AddItem("(no servers — Refresh to scan the LAN, or type an address below)");
            _serverList.SetItemDisabled(idx, true);
        }
        else
        {
            _serverList.Select(previous >= 0 && previous < servers.Count ? previous : 0);
        }
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>
    /// Pump the browser's async master/server replies each frame and re-render so internet rows (and their
    /// details) fill in over the frames following a Refresh. Cheap when idle: Poll is a non-blocking socket
    /// drain that no-ops with no query in flight, and the revision check skips the relayout unless a row was
    /// added or a row's fields changed.
    /// </summary>
    public override void _Process(double delta)
    {
        Browser.Poll();
        if (Browser.Revision != _renderedRevision)
            RenderServers();
    }

    /// <summary>Browser revision last rendered into the ItemList — skips needless relayouts in <see cref="_Process"/>.</summary>
    private int _renderedRevision = -1;

    private void OnRefresh()
    {
        GD.Print("[Menu] Refreshing server list (favorites + LAN + internet master query).");
        Browser.Refresh();
        RenderServers();
    }

    /// <summary>The address to act on: the typed field if non-empty, else the selected row's address.</summary>
    private string SelectedAddress()
    {
        if (!string.IsNullOrWhiteSpace(_addressEdit.Text))
            return _addressEdit.Text;
        if (_serverList.IsAnythingSelected())
        {
            Variant meta = _serverList.GetItemMetadata(_serverList.GetSelectedItems()[0]);
            if (meta.VariantType == Variant.Type.String)
                return meta.AsString();
        }
        return "";
    }

    private void OnConnect()
    {
        string address = SelectedAddress();
        string? target = Browser.Connect(address);
        if (target is null)
            GD.Print("[Menu] Connect: no address entered or selected.");
        else
            GD.Print($"[Menu] Connecting to {target}.");
    }

    private void OnAddFavorite()
    {
        string address = SelectedAddress();
        if (string.IsNullOrWhiteSpace(address))
        {
            GD.Print("[Menu] Add favorite: no address entered or selected.");
            return;
        }
        Browser.AddFavorite(address);
        Browser.Refresh();
        RenderServers();
    }

    private void OnRemoveFavorite()
    {
        string address = SelectedAddress();
        if (string.IsNullOrWhiteSpace(address))
            return;
        Browser.RemoveFavorite(address);
        Browser.Refresh();
        RenderServers();
    }

    // -------------------------------------------------------------------------------------------------
    //  Create tab (compact) — opens the full CreateGameScreen for the rest of the options.
    // -------------------------------------------------------------------------------------------------

    private Control BuildCreateTab()
    {
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 10);

        col.AddChild(MakeHeader("Host your own game"));

        // Quick gametype list from the registry (the QC Create tab leads with a GametypeList). Each row
        // stores its NetName so the hand-off to CreateGameScreen can pre-select the same gametype.
        _createGametypeList = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill };
        col.AddChild(MakeRow("Gametype:", _createGametypeList));
        foreach (var gt in GameTypes.All)
        {
            string label = string.IsNullOrEmpty(gt.DisplayName) ? gt.NetName : gt.DisplayName;
            int idx = _createGametypeList.AddItem(label);
            _createGametypeList.SetItemMetadata(idx, gt.NetName);
        }
        if (GameTypes.All.Count == 0)
        {
            _createGametypeList.AddItem("(no gametypes registered)");
            _createGametypeList.SetItemDisabled(0, true);
        }
        else
        {
            _createGametypeList.Select(0);
        }

        _createMapEdit = new LineEdit { Text = "dm_example", PlaceholderText = "map name" };
        col.AddChild(MakeRow("Map:", _createMapEdit));

        // Hand off to the full Create screen (with bot count/skill, limits, …), pre-seeded with the picks.
        col.AddChild(MakeButtonBar(MakeButton("More options / Start...", OpenFullCreate)));

        return col;
    }

    private void OpenFullCreate()
    {
        string? gametype = null;
        if (_createGametypeList.IsAnythingSelected())
        {
            Variant meta = _createGametypeList.GetItemMetadata(_createGametypeList.GetSelectedItems()[0]);
            if (meta.VariantType == Variant.Type.String)
                gametype = meta.AsString();
        }
        string? map = string.IsNullOrWhiteSpace(_createMapEdit.Text) ? null : _createMapEdit.Text;

        Menu?.Push(new CreateGameScreen { InitialGametype = gametype, InitialMap = map });
    }
}
