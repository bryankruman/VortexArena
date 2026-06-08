using System;
using System.Linq;
using System.Text;
using Godot;
using XonoticGodot.Formats.Vfs;
using XonoticGodot.Common.Config;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Game.Console;
using FileAccess = Godot.FileAccess;

namespace XonoticGodot.Game.Menu;

/// <summary>
/// Process-wide menu/client state — the C# stand-in for the engine's global cvar store + gamedir that the
/// QuakeC menu (menu.dat) shares with the running game. Xonotic's menu has no model of its own: every widget
/// reads/writes an <em>engine</em> cvar, and those same cvars drive the game. To reproduce that faithfully the
/// front-end needs ONE cvar store that (a) exists before any match (so the menu can bind to it), (b) is loaded
/// with authentic defaults from the stock config tree, (c) layers the user's saved preferences on top, and
/// (d) is the very store the match then runs on so a setting changed in the menu is live in-game.
///
/// <see cref="Boot"/> (called once at startup by <see cref="Shell"/>) mounts the asset VFS, builds that shared
/// <see cref="CvarService"/>, execs the client+server config chain into it, applies <c>user://config.cfg</c>,
/// and installs a process-wide <see cref="Api.Services"/> facade so <see cref="Api.Cvars"/> works at the menu.
/// The match (<see cref="GameDemo"/>) reuses both the <see cref="Vfs"/> and the <see cref="Cvars"/> store.
/// </summary>
public static class MenuState
{
    /// <summary>Where the menu persists the user's archived (DP <c>seta</c>) preferences.</summary>
    public const string UserConfigPath = "user://config.cfg";

    private static CvarService? _cvars;
    private static VirtualFileSystem? _vfs;
    private static ConfigInterpreter? _interp;
    private static bool _booted;

    /// <summary>
    /// The process-wide cvar store the whole front-end binds to (and the match runs on). Created lazily so a
    /// menu screen shown in isolation (a test, or before <see cref="Boot"/>) still has a live store to bind to.
    /// </summary>
    public static CvarService Cvars => _cvars ??= new CvarService();

    /// <summary>The mounted asset VFS (maps/models/configs/…), or null if the data dir didn't mount.</summary>
    public static VirtualFileSystem? Vfs => _vfs;

    /// <summary>
    /// The shared DP command interpreter (Cbuf/Cmd) the config tree loaded into — and the SAME buffer the
    /// in-game console runs typed lines through, so the console interprets commands exactly as a <c>.cfg</c>
    /// does (cvars/alias/exec/<c>$</c>-expansion + its boot-time aliases). Non-null after <see cref="Boot"/>.
    /// </summary>
    public static ConfigInterpreter? Interp => _interp;

    /// <summary>True once <see cref="Boot"/> has run (so callers can avoid a redundant boot).</summary>
    public static bool Booted => _booted;

    /// <summary>
    /// One-time client bootstrap. Idempotent. Mounts <paramref name="dataPath"/> as the asset VFS, loads the
    /// stock config chain (client + server + notifications) into the shared cvar store with authentic
    /// defaults, applies the user's saved preferences, and publishes the ambient <see cref="Api.Services"/>
    /// facade (empty collision world + the shared store) so the menu — and any gameplay code it reaches — can
    /// read/write cvars before a match exists. Tolerant: a missing data dir just leaves the store at its
    /// registered defaults and the menu still runs.
    /// </summary>
    public static void Boot(string dataPath)
    {
        if (_booted)
            return;
        _booted = true;

        _cvars ??= new CvarService();

        // --- mount the asset VFS (same gamedir the match uses; resolved identically) ---
        try
        {
            var vfs = new VirtualFileSystem();
            if (vfs.MountGameDir(GameDemo.ResolveDataPath(dataPath)))
                _vfs = vfs;
            else
                GD.PrintErr($"[MenuState] data dir '{dataPath}' not found — menu runs on registered cvar defaults only.");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[MenuState] failed to mount data dir '{dataPath}': {ex.Message}");
        }

        // --- publish the process-wide facade so Api.Cvars resolves to the shared store at the menu ---
        Api.Services = new EngineServices(new CollisionWorld(), _cvars);
        XonoticGodot.Server.Cvars.RegisterDefaults();

        // --- load authentic defaults: the client master cfg (settings/binds/hud/crosshairs/effects) + the
        //     server gameplay tree (balance/physics/gametypes/mutators) + the notification table. The interpreter
        //     is KEPT (MenuState.Interp) so the in-game console shares this command buffer — runtime `exec`, the
        //     boot-time aliases, and the shared cvar store are all already wired into it. The file reader resolves
        //     through the VFS (returns null when unmounted, so a stray runtime `exec` is a no-op, not a crash). ---
        Func<string, string?> reader = p => _vfs is not null && _vfs.Exists(p) ? _vfs.ReadText(p) : null;
        if (_vfs is not null)
        {
            try
            {
                _interp = ConfigLoader.Load(_cvars, reader,
                    "xonotic-client.cfg", "xonotic-server.cfg", "notifications.cfg");
                GD.Print($"[MenuState] config: {_interp.CvarsAssigned} cvars from {_interp.FilesExecuted} cfg files " +
                         $"({_interp.AliasesDefined} aliases, {_interp.FilesMissing} missing).");
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[MenuState] config load failed: {ex.Message}");
            }
        }
        // Even without a data dir (config skipped/failed) the console still needs a live command buffer.
        _interp ??= new ConfigInterpreter(_cvars, reader);

        // --- THE LINCHPIN: populate the canonical keybind table from binds-xonotic.cfg. ---
        // xonotic-client.cfg:603 already exec'd binds-xonotic.cfg above, but `bind` is on the interpreter's
        // NonCvarCommands denylist and no `bind` sink was registered yet, so those binds were dropped (counted
        // as unknown commands). Register the sink now (BindInput → BindTable, translating engine key names like
        // UPARROW/MOUSE1/SHIFT into the canonical strings a live event encodes to) and re-exec the cfg so the
        // FULL canonical bind set lands in BindTable with zero new parsing — the single source of truth both
        // gameplay input paths read. (ConsoleCommands re-registers an equivalent `bind` sink when the console
        // is built later; RegisterCommand overwrites by name, so that's harmless — both feed BindTable.)
        BindInput.RegisterBindCommands(_interp);
        if (_vfs is not null)
            _interp.ExecuteFile("binds-xonotic.cfg");

        // Fallback: if no data dir mounted (CI/bare run, so binds-xonotic.cfg never exec'd), seed the table from
        // the thin KeyBindings.Defaults so the game is still playable. With a data dir the cfg already filled it.
        if (!XonoticGodot.Engine.Console.BindTable.List().Any())
            BindInput.SeedFromActions(KeyBindings.Defaults);

        // --- the user's saved preferences win over the stock defaults (incl. their saved `bind` lines) ---
        LoadUserConfig();

        // --- i18n: load the active language's gettext catalog so the menu builds translated (menu.qc m_init +
        //     the engine's PRVM_PO_Load at progs load). prvm_language defaults to "en" (xonotic-client.cfg:91);
        //     it's on the interpreter's NonCvarCommands denylist (treated as a command, so it may not be stored as
        //     a cvar) — fall back to "en" when empty, and mirror it into _menu_prvm_language like m_init:74 so the
        //     language picker seeds its selection. en/""/dump => empty catalog (identity, the English baseline). ---
        string lang = _cvars.GetString("prvm_language");
        if (string.IsNullOrEmpty(lang))
        {
            lang = "en";
            _cvars.Set("prvm_language", lang);
        }
        if (string.IsNullOrEmpty(_cvars.GetString("_menu_prvm_language")))
            _cvars.Set("_menu_prvm_language", lang);
        Localization.SetLanguage(lang, _vfs);
    }

    /// <summary>
    /// Reload the runtime keybind table from the canonical <c>binds-xonotic.cfg</c> — the menu "Reset all"
    /// (keybinder.qc <c>KeyBinder_Bind_Reset_All</c> does <c>unbindall; exec binds-xonotic.cfg</c>). Uses a fresh
    /// interpreter wired to <see cref="BindInput"/>'s translating <c>bind</c> sink so engine key names map to the
    /// canonical strings — independent of whichever <c>bind</c> handler is currently on <see cref="Interp"/> (the
    /// console replaces it with its own non-translating one when it builds). The cfg's <c>seta</c> side-effects go
    /// to a throwaway store (the real values are already in <see cref="Cvars"/>); only the <c>bind</c> lines, which
    /// land in the process-global <c>BindTable</c>, matter here. Falls back to the thin defaults with no data dir.
    /// </summary>
    public static void ReloadDefaultBinds()
    {
        XonoticGodot.Engine.Console.BindTable.UnbindAll();
        Func<string, string?> reader = p => _vfs is not null && _vfs.Exists(p) ? _vfs.ReadText(p) : null;
        if (_vfs is not null)
        {
            var scratch = new ConfigInterpreter(new CvarService(), reader);
            BindInput.RegisterBindCommands(scratch);
            scratch.ExecuteFile("binds-xonotic.cfg");
        }
        else
        {
            BindInput.SeedFromActions(KeyBindings.Defaults);
        }
    }

    /// <summary>
    /// Apply <c>user://config.cfg</c> (the menu's saved <c>seta</c> preferences) over the loaded defaults, and
    /// re-mark each as archived so it persists again on the next <see cref="SaveUserConfig"/>. The file is the
    /// menu's own output, so a small set/seta tokenizer is enough — no full interpreter needed.
    /// </summary>
    public static void LoadUserConfig()
    {
        if (_cvars is null)
            return;
        using FileAccess? f = FileAccess.Open(UserConfigPath, FileAccess.ModeFlags.Read);
        if (f is null)
            return; // first run / no saved prefs yet

        foreach (string raw in f.GetAsText().Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//"))
                continue;
            // The user's saved `bind`/`unbind`/`unbindall` lines (DP Key_WriteBindings dump, emitted by
            // SaveUserConfig) run through the interpreter's bind sink so a rebind overrides the binds-xonotic.cfg
            // default just exec'd above (DP applies config.cfg after the defaults). They don't parse as `set`.
            if (line.StartsWith("bind ") || line.StartsWith("unbind ") || line == "unbindall"
                || line.StartsWith("unbindall "))
            {
                _interp?.ExecuteLine(line);
                continue;
            }
            if (!TryParseSet(line, out string name, out string value))
                continue;
            _cvars.Set(name, value);
            _cvars.MarkArchived(name);
        }
    }

    /// <summary>
    /// Write every archived cvar to <c>user://config.cfg</c> as <c>seta NAME "VALUE"</c> (DP's config.cfg
    /// archive dump). Called when the user applies settings or leaves the menu. Never throws.
    /// </summary>
    public static void SaveUserConfig()
    {
        if (_cvars is null)
            return;
        var sb = new StringBuilder();
        sb.Append("// XonoticGodot — saved menu preferences (archived cvars + keybinds). Auto-generated; edits may be overwritten.\n");
        foreach (string name in _cvars.ArchivedNames.OrderBy(n => n, StringComparer.Ordinal))
            sb.Append($"seta {name} \"{Escape(_cvars.GetString(name))}\"\n");

        // Dump the runtime keybind table (DP Key_WriteBindings) as `bind "KEY" "cmd"` lines so the user's
        // rebinds persist alongside the archived cvars — replacing the legacy user://settings.cfg keybind
        // section (MenuSettings is quarantined). LoadUserConfig re-runs these through the bind sink at boot,
        // after binds-xonotic.cfg, so a rebind wins over the default.
        BindInput.WriteBindings(sb);

        using FileAccess? f = FileAccess.Open(UserConfigPath, FileAccess.ModeFlags.Write);
        if (f is null)
        {
            GD.PushWarning($"[MenuState] could not write {UserConfigPath}: {FileAccess.GetOpenError()}");
            return;
        }
        f.StoreString(sb.ToString());
    }

    /// <summary>Parse a single <c>set</c>/<c>seta</c> line into (name, value), handling a quoted value. </summary>
    private static bool TryParseSet(string line, out string name, out string value)
    {
        name = "";
        value = "";
        // tokens: set|seta  NAME  VALUE-or-"quoted value"
        int sp = line.IndexOf(' ');
        if (sp < 0)
            return false;
        string verb = line[..sp];
        if (verb != "set" && verb != "seta")
            return false;

        string rest = line[(sp + 1)..].TrimStart();
        int sp2 = rest.IndexOf(' ');
        if (sp2 < 0)
        {
            name = rest;
            return name.Length > 0; // bare "set name" → empty value
        }
        name = rest[..sp2];
        string v = rest[(sp2 + 1)..].Trim();
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
            v = v[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        value = v;
        return name.Length > 0;
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
