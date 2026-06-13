using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Server;

/// <summary>
/// The context handed to a console-command handler — the C# successor to QuakeC's command argv/source
/// plumbing (server/command/sv_cmd.qc + common/command/generic.qc, the <c>argc</c>/<c>argv(i)</c> reads and
/// the rcon/client source distinction). Carries the parsed argument vector and a sink for command output so
/// a handler stays host-agnostic (a dedicated server writes to its console; a listen server to the client).
/// </summary>
public sealed class CommandContext
{
    private readonly string[] _argv;
    private readonly StringBuilder _out = new();

    /// <summary>True when the command came from rcon / the server console (QC <c>CMD_REQUEST_COMMAND</c> rcon).</summary>
    public bool IsServerConsole { get; }

    /// <summary>The issuing player, when a client typed the command (QC <c>caller</c>); null from the console.</summary>
    public Player? Caller { get; }

    /// <summary>QC <c>argc</c>: the token count, including the command name at index 0.</summary>
    public int ArgCount => _argv.Length;

    public CommandContext(string[] argv, bool isServerConsole, Player? caller)
    {
        _argv = argv;
        IsServerConsole = isServerConsole;
        Caller = caller;
    }

    /// <summary>QC <c>argv(i)</c>: the i-th token ("" if out of range). Index 0 is the command name.</summary>
    public string Arg(int i) => i >= 0 && i < _argv.Length ? _argv[i] : "";

    /// <summary>The full token vector (QC the argv array) — index 0 is the command name.</summary>
    public string[] Argv => _argv;

    /// <summary>QC <c>stof(argv(i))</c>: the i-th token as a float (0 if absent/unparsable).</summary>
    public float ArgFloat(int i)
        => float.TryParse(Arg(i), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 0f;

    /// <summary>QC <c>argv(i)</c> joined from <paramref name="first"/> to the end (e.g. a <c>say</c> message).</summary>
    public string ArgTail(int first)
    {
        if (first >= _argv.Length) return "";
        return string.Join(' ', _argv, first, _argv.Length - first);
    }

    /// <summary>Write a line of command output (QC <c>print()</c> / <c>sprint(caller, ...)</c>).</summary>
    public void Print(string line) => _out.AppendLine(line);

    /// <summary>The accumulated output so far (the host relays it to the console/client).</summary>
    public string Output => _out.ToString();
}

/// <summary>One registered console command (QC a <c>GENERIC_COMMAND</c>/<c>SERVER_COMMAND</c> entry).</summary>
public sealed class ConsoleCommand
{
    public string Name { get; }
    public string Help { get; }

    /// <summary>The handler. Returns true if it consumed the command (QC the request-COMMAND return).</summary>
    public Func<CommandContext, bool> Handler { get; }

    public ConsoleCommand(string name, string help, Func<CommandContext, bool> handler)
    {
        Name = name;
        Help = help;
        Handler = handler;
    }
}

/// <summary>
/// The server console-command registry — the C# successor to the server/command/ command bus
/// (sv_cmd.qc <c>SV_ParseServerCommand</c> / <c>GameCommand_*</c> + common/command/generic.qc): a
/// name → handler table plus a dispatcher that tokenizes a command line and routes it. Mirrors the QC
/// <c>SERVER_COMMAND(name, help) { … }</c> registrations: this class registers the common server admin
/// commands (<c>restart</c>, <c>endmatch</c>, <c>kick</c>, <c>say</c>, <c>vote</c>, <c>bot_add</c>/<c>bot_remove</c>,
/// <c>map</c>/<c>gotomap</c>) and the cvar reflection commands (<c>set</c>/<c>seta</c>/<c>cvar</c>/<c>toggle</c>),
/// all acting on a live <see cref="GameWorld"/>.
///
/// A host owns one of these per world, calls <see cref="RegisterBuiltins"/> once (done in the ctor), and
/// feeds it console/rcon/client input via <see cref="Execute"/>. Output is collected on the
/// <see cref="CommandContext"/> for the host to relay.
/// </summary>
public sealed class Commands
{
    private readonly Dictionary<string, ConsoleCommand> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly GameWorld _world;

    /// <summary>Optional sink the host wires to receive a "kick this player" request (QC dropclient).</summary>
    public Action<Player, string>? KickHandler { get; set; }

    /// <summary>Optional sink the host wires to receive chat lines for broadcast (QC the say bus).</summary>
    public Action<Player?, string, bool>? ChatHandler { get; set; }

    /// <summary>
    /// Optional sink the host wires for server announcements broadcast to everyone (QC <c>bprint</c>) — vote
    /// results, ban notices, timeout/teamplay messages. Distinct from <see cref="ChatHandler"/> (player chat).
    /// </summary>
    public Action<string>? ChatBroadcast { get; set; }

    /// <summary>
    /// [T46] Optional per-player chat delivery sink — QC <c>sprint(client, text)</c>. The chat engine
    /// (<see cref="Chat"/>) routes each recipient's line through this so team/private/spectator/ignore filtering
    /// happens BEFORE delivery (the routing is in <see cref="Chat.Say"/>, not the net layer). The host wires it to
    /// <c>ServerNet.SendChatToPlayer</c>. When unwired, <see cref="Chat.Say"/> still computes routing (and the
    /// public-say case falls back to <see cref="ChatHandler"/> so a bare host without the per-player wire keeps
    /// broadcasting public chat as before — see <see cref="DeliverChatLine"/>).
    /// </summary>
    public Action<Player, string>? ChatToPlayer { get; set; }

    /// <summary>[T46] Optional server-console echo sink — QC <c>dedicated_print(text)</c>. The chat engine echoes
    /// each delivered public/team/private line to the SERVER console through this (distinct from a client print).</summary>
    public Action<string>? ChatConsole { get; set; }

    /// <summary>
    /// [T46] The server chat engine (port of server/chat.qc <c>Say</c>) — owns formatmessage, per-say-type flood,
    /// recipient routing and ignore/mute handling. The say/say_team/tell/ignore/unignore/clear_ignores commands
    /// route through it. Created lazily in the ctor (it needs the live <see cref="GameWorld"/>).
    /// </summary>
    public Chat Chat { get; }

    /// <summary>Optional sink the host wires to add bots (QC bot_cmd add). Args: name, skill.</summary>
    public Func<string?, float?, bool>? AddBotHandler { get; set; }

    /// <summary>Optional sink the host wires to remove a bot by name (QC bot_remove).</summary>
    public Func<string?, bool>? RemoveBotHandler { get; set; }

    /// <summary>Optional sink the host wires to load a named map (QC changelevel). Args: map name.</summary>
    public Action<string>? ChangeLevelHandler { get; set; }

    /// <summary>
    /// The sim-clock deferred-command queue (DP <c>defer</c> / <c>Cbuf_Execute_Deferred</c>). Backs the
    /// <c>defer</c> + <c>nextframe</c> commands; pumped each server tick by <c>GameWorld.OnStartFrame</c>
    /// (<c>Commands.Deferred.Pump(Time, cmd =&gt; Commands.Execute(cmd, isServerConsole: true))</c>). The passed
    /// <c>restart</c> vote enqueues <c>defer 1 restart</c> here.
    /// </summary>
    public DeferredCommands Deferred { get; } = new();

    /// <summary>
    /// The precomputed common-command reply strings (QC getreplies.qc): printmaplist/lsmaps/records/rankings/
    /// ladder/the monster list + cvar_changes/cvar_purechanges. Rebuilt by <c>GameWorld</c> after boot/map-load
    /// via <see cref="CommandReplies.Recompute"/>; the reply commands read the cached fields.
    /// </summary>
    public CommandReplies Replies { get; }

    /// <summary>QC <c>CS_CVAR(caller).cvar_cl_autoswitch</c>: the per-client auto-weapon-switch flag, kept off the
    /// shared Player type (a small per-player table here). Set by the <c>autoswitch</c> command (and synced by a
    /// replicated <c>sentcvar cl_autoswitch</c>), read by the pickup→bestweapon logic when that consults it.
    /// Default unset == 0 (off) until the client sends it.</summary>
    private readonly Dictionary<Player, bool> _autoswitch = new();

    /// <summary>QC <c>CS(caller).version</c>: the client's reported game version (the internal <c>clientversion</c> command).</summary>
    private readonly Dictionary<Player, float> _clientVersion = new();

    /// <summary>
    /// The per-client replicated-cvar store — the C# successor to QC's <c>CS_CVAR(player).cvar_cl_*</c> fields
    /// (lib/replicate.qh: the SVQC side of REPLICATE keeps each client's pushed cvar values on its clientstate
    /// entity). Written ONLY by the caller-gated <c>sentcvar</c> command (and the <c>physics</c>/<c>autoswitch</c>
    /// verbs that share state with it) — NEVER the world cvar store, which would be the T47 privilege-separation
    /// hole. Keyed by Player; dropped in <see cref="ForgetPlayer"/> on disconnect.
    /// </summary>
    private readonly Dictionary<Player, Dictionary<string, string>> _clientCvars = new();

    /// <summary>
    /// The cvars a client may push via <c>cmd sentcvar</c> — the ported slice of the QC REPLICATE registrations
    /// (common/replicate.qh:15-23 + physics/player.qc:7-9) that have live consumers here. Anything else is
    /// silently ignored (QC stores only known REPLICATE fields; an unknown name writes nothing).
    /// </summary>
    private static readonly HashSet<string> SentCvarAllowlist = new(StringComparer.Ordinal)
    {
        "cl_weaponpriority",
        "cl_weaponpriority0", "cl_weaponpriority1", "cl_weaponpriority2", "cl_weaponpriority3",
        "cl_weaponpriority4", "cl_weaponpriority5", "cl_weaponpriority6", "cl_weaponpriority7",
        "cl_weaponpriority8", "cl_weaponpriority9",
        "cl_autoswitch", "cl_autoswitch_cts",
        "cl_noantilag",
        "cl_physics",
        "cl_movement_track_canjump",
        "cl_jetpack_jump",
    };

    /// <summary>QC <c>mapvote_suggestions</c>: maps players suggested for the end-of-match ballot (the <c>suggestmap</c>
    /// command). Kept here (the port's <see cref="MapVoting"/> defers the suggestion array); deduped, order kept.</summary>
    private readonly List<string> _mapSuggestions = new();

    /// <summary>QC the per-client auto-switch flag read (default off). Exposed so the pickup path can honor it.</summary>
    public bool GetAutoswitch(Player p) => _autoswitch.TryGetValue(p, out bool v) && v;

    /// <summary>QC <c>CS_CVAR(p).cvar_&lt;name&gt;</c>: the per-client replicated cvar value, or
    /// <paramref name="fallback"/> when the client never sent it.</summary>
    public string GetClientCvar(Player p, string name, string fallback = "")
        => _clientCvars.TryGetValue(p, out var t) && t.TryGetValue(name, out string? v) ? v : fallback;

    /// <summary>Boolean read of a per-client replicated cvar (QC <c>boolean(stoi(...))</c> semantics via
    /// <see cref="InterpretBoolean"/>).</summary>
    public bool GetClientCvarBool(Player p, string name, bool fallback = false)
        => _clientCvars.TryGetValue(p, out var t) && t.TryGetValue(name, out string? v) ? InterpretBoolean(v) : fallback;

    /// <summary>Float read of a per-client replicated cvar (QC <c>stof</c> — unparsable → 0).</summary>
    public float GetClientCvarFloat(Player p, string name, float fallback = 0f)
        => _clientCvars.TryGetValue(p, out var t) && t.TryGetValue(name, out string? v)
            ? (float.TryParse(v, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 0f)
            : fallback;

    private void SetClientCvar(Player p, string name, string value)
    {
        if (!_clientCvars.TryGetValue(p, out var t))
            _clientCvars[p] = t = new Dictionary<string, string>(StringComparer.Ordinal);
        t[name] = value;
    }

    /// <summary>
    /// Drop every per-client table entry for a departed player (the QC clientstate entity being freed on
    /// disconnect). The net layer calls this from its disconnect/bot-removal paths so the dictionaries don't
    /// retain dead Player references across a long-running server.
    /// </summary>
    public void ForgetPlayer(Player p)
    {
        _clientCvars.Remove(p);
        _autoswitch.Remove(p);
        _clientVersion.Remove(p);
    }

    public Commands(GameWorld world)
    {
        _world = world;
        Replies = new CommandReplies(world);
        Chat = new Chat(world); // [T46] the chat engine (registers its g_chat_* cvar defaults on construction)
        RegisterBuiltins();

        // Per-client weapon priority (T54): point the selection code's per-player priority source at this
        // world's replicated-cvar table (QC w_getbestweapon reading CS_CVAR(this).cvar_cl_weaponpriority).
        // The stored value is already fixed-up NUMBER form (see CmdSentCvar); "" → Inventory falls back to the
        // global cvar read, so a player that never replicated behaves exactly as before. Static: the latest
        // world's Commands owns it (a torn-down world's players simply read "" → fallback).
        Inventory.PriorityProvider = e => e is Player p ? GetClientCvar(p, "cl_weaponpriority", "") : null;
    }

    /// <summary>The registered commands (read-only), e.g. for a <c>help</c>/completion dump.</summary>
    public IReadOnlyCollection<ConsoleCommand> All => _commands.Values;

    /// <summary>QC <c>SERVER_COMMAND(name, help)</c>: register (or replace) a command handler.</summary>
    public void Register(string name, string help, Func<CommandContext, bool> handler)
        => _commands[name] = new ConsoleCommand(name, help, handler);

    /// <summary>Remove a command (host teardown / replacing a builtin).</summary>
    public bool Unregister(string name) => _commands.Remove(name);

    public bool Has(string name) => _commands.ContainsKey(name);

    // =============================================================================================
    // dispatch (QC SV_ParseServerCommand: tokenize, look up, invoke)
    // =============================================================================================

    /// <summary>
    /// Tokenize and dispatch one command line (QC <c>tokenize_console</c> → command lookup → handler). The
    /// first token is the command name (case-insensitive). Returns a context carrying the handler output;
    /// <see cref="CommandContext.Output"/> reports "unknown command" if nothing matched. <paramref name="caller"/>
    /// is the issuing player for a client command (null = server console / rcon).
    /// </summary>
    public CommandContext Execute(string commandLine, bool isServerConsole = true, Player? caller = null)
    {
        string[] argv = Tokenize(commandLine);
        var ctx = new CommandContext(argv, isServerConsole, caller);
        if (argv.Length == 0)
            return ctx;

        if (_commands.TryGetValue(argv[0], out ConsoleCommand? cmd))
        {
            try { cmd.Handler(ctx); }
            catch (Exception ex) { ctx.Print($"command '{argv[0]}' failed: {ex.Message}"); }
        }
        else
        {
            ctx.Print($"Unknown command \"{argv[0]}\"");
        }
        return ctx;
    }

    /// <summary>
    /// QC <c>tokenize_console</c> (the relevant slice): split on whitespace, honoring double-quoted tokens so
    /// <c>say "hello world"</c> is two tokens. Backslash escapes are not handled (QC tokenize_console keeps
    /// them literal in most server commands; the few that need it read fullspawndata instead).
    /// </summary>
    public static string[] Tokenize(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return System.Array.Empty<string>();

        var tokens = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0)
            tokens.Add(sb.ToString());
        return tokens.ToArray();
    }

    // =============================================================================================
    // builtin commands (QC the common GameCommand_* + generic cvar commands)
    // =============================================================================================

    private void RegisterBuiltins()
    {
        // ---- cvar reflection (QC common/command/generic.qc set/seta/toggle + cvar) ----
        Register("set", "set <cvar> <value> — set a console variable", CmdSet);
        Register("seta", "seta <cvar> <value> — set + archive a console variable", CmdSet);
        Register("cvar", "cvar <name> [value] — get or set a console variable", CmdCvar);
        Register("toggle", "toggle <cvar> [a b ...] — cycle a cvar through values (default 0/1)", CmdToggle);

        // ---- match flow (QC GameCommand restart/endmatch/reducematchtime/extendmatchtime) ----
        Register("restart", "restart — restart the match (warmup → countdown → live)", CmdRestart);
        Register("endmatch", "endmatch — end the current match immediately (go to intermission)", CmdEndMatch);
        Register("reducematchtime", "reducematchtime — reduce the match time limit", ctx => ChangeMatchTime(ctx, -60f));
        Register("extendmatchtime", "extendmatchtime — extend the match time limit", ctx => ChangeMatchTime(ctx, +60f));

        // ---- players / chat (QC GameCommand kick + the say bus) ----
        Register("kick", "kick <player> [reason] — remove a player from the server", CmdKick);
        Register("say", "say <message> — broadcast a chat message", CmdSay);
        // [T46] the chat family (QC server/command/cmd.qc ClientCommand_say_team/tell/ignore/unignore/clear_ignores).
        Register("say_team", "say_team <message> — send a chat message to your teammates", CmdSayTeam);
        Register("tell", "tell <client> <message> — send a private message to a player", CmdTell);
        Register("ignore", "ignore <client> — keep a player's messages out of your chat log", CmdIgnore);
        Register("unignore", "unignore <client> — stop ignoring a player", CmdUnignore);
        Register("clear_ignores", "clear_ignores — remove all of your ignores", CmdClearIgnores);

        // ---- voting (QC server/command/vote.qc the vote command family) ----
        Register("vote", "vote <call|yes|no|abstain|stop|status|master|help> [args] — match/map voting", CmdVote);

        // ---- bots (QC bot_cmd add / bot_remove / setbots) ----
        Register("bot_add", "bot_add [name] [skill] — add an AI player", CmdBotAdd);
        Register("bot_remove", "bot_remove [name] — remove a bot", CmdBotRemove);
        Register("setbots", "setbots <n> — keep this many bots on the server", CmdSetBots);
        Register("removebots", "removebots — remove all bots", CmdRemoveBots);

        // ---- map change (QC changelevel / gotomap / nextmap) ----
        Register("map", "map <name> — change to a map immediately", CmdMap);
        Register("gotomap", "gotomap <name> — queue the next map (after intermission)", CmdGotoMap);
        Register("nextmap", "nextmap [name] — get or set the next map", CmdNextMap);

        // ---- match flow (QC GameCommand allready/resetmatch/gametype/cointoss) ----
        Register("allready", "allready — end warmup and start the match now", CmdAllReady);
        Register("resetmatch", "resetmatch — soft restart back to warmup, keeping teams", CmdResetMatch);
        Register("gametype", "gametype <mode> — change the active gametype", CmdGameType);
        Register("cointoss", "cointoss [a] [b] — broadcast a random coin flip", CmdCoinToss);

        // ---- teamplay admin (QC lockteams/unlockteams/shuffleteams/moveplayer/allspec/nospectators) ----
        Register("lockteams", "lockteams — prevent team joins/switches", CmdLockTeams);
        Register("unlockteams", "unlockteams — allow team joins/switches", CmdUnlockTeams);
        Register("shuffleteams", "shuffleteams — randomly redistribute players across teams", CmdShuffleTeams);
        Register("moveplayer", "moveplayer <player> <team|spec> — move a player to a team or spectate", CmdMovePlayer);
        Register("allspec", "allspec [reason] — force all players to spectate", CmdAllSpec);
        Register("nospectators", "nospectators — temporarily disallow spectating", CmdNoSpectators);

        // ---- weapon selection / reload (QC server/impulse.qc — the impulse → selection.qc routing) ----
        // The client weapon binds send `impulse N`; ImpulseCommands routes N onto the selection/reload API.
        Register("impulse", "impulse <n> — process a client impulse (weapon switch/reload/etc.)", CmdImpulse);
        // Convenience console aliases mirroring xonotic-client.cfg's weapon_* aliases (each maps to an impulse).
        Register("weapon_next", "weapon_next — switch to the next weapon", ctx => CmdWeaponImpulse(ctx, 10));
        Register("weapon_prev", "weapon_prev — switch to the previous weapon", ctx => CmdWeaponImpulse(ctx, 12));
        Register("weaplast", "weaplast — switch to the last used weapon", ctx => CmdWeaponImpulse(ctx, 11));
        Register("weapon_last", "weapon_last — switch to the last used weapon", ctx => CmdWeaponImpulse(ctx, 11));
        Register("weapon_best", "weapon_best — switch to the best weapon", ctx => CmdWeaponImpulse(ctx, 13));
        Register("weapon_drop", "weapon_drop — drop the current weapon", ctx => CmdWeaponImpulse(ctx, 17));
        Register("reload", "reload — reload the current weapon", ctx => CmdWeaponImpulse(ctx, 20));

        // ---- per-player commands (QC ClientCommand ready/join/spectate/selectteam/kill) ----
        Register("ready", "ready — toggle your ready state during warmup", CmdReady);
        Register("join", "join — join the game as a player", CmdJoin);
        Register("spectate", "spectate — become a spectator", CmdSpectate);
        Register("selectteam", "selectteam <red|blue|yellow|pink|auto> — choose a team", CmdSelectTeam);
        Register("kill", "kill — suicide (respawn)", CmdKill);
        Register("ready_up", "ready_up — alias of ready", CmdReady);

        // ---- minigames (QC server/command/cmd.qc ClientCommands "minigame" → ClientCommand_minigame) ----
        Register("minigame", "minigame <create|join|list|list-sessions|part|end|invite> [args] — play a minigame", CmdMinigame);

        // ---- timeout / timein (QC common/command timeout) ----
        Register("timeout", "timeout — pause the match", CmdTimeout);
        Register("timein", "timein — resume from a timeout", CmdTimein);

        // ---- bans (QC command/banning.qc) ----
        Register("ban", "ban <address> [bantime] [reason] — ban an address", CmdBan);
        Register("kickban", "kickban <player> [bantime] [masksize] [reason] — kick + ban a player", CmdKickBan);
        Register("unban", "unban <banid> — lift a ban by its banlist index", CmdUnban);
        Register("banlist", "banlist — list the active bans", CmdBanList);
        Register("mute", "mute <player> — mute a player in chat", CmdMute);
        Register("unmute", "unmute <player> — unmute a player", CmdUnmute);
        Register("playban", "playban <player> — force a player to spectate", CmdPlayBan);
        Register("unplayban", "unplayban <player> — allow a play-banned player to join", ctx => CmdListRemove(ctx, "g_playban_list", "unplaybanned"));
        Register("voteban", "voteban <player> — ban a player from voting", ctx => CmdListAdd(ctx, "g_voteban_list", "voteban"));
        Register("unvoteban", "unvoteban <player> — lift a voteban", ctx => CmdListRemove(ctx, "g_voteban_list", "unvoteban"));

        // ---- cheats (QC CheatCommand — routed for a client via cmd) ----
        Register("god", "god — toggle godmode (needs sv_cheats)", CmdCheat);
        Register("notarget", "notarget — toggle notarget (needs sv_cheats)", CmdCheat);
        Register("noclip", "noclip — toggle noclip (needs sv_cheats)", CmdCheat);
        Register("fly", "fly — toggle flymode (needs sv_cheats)", CmdCheat);
        Register("give", "give <all|weapon|resource amount> — give items (needs sv_cheats)", CmdCheat);

        // ---- campaign (QC sv_cmd warp) ----
        Register("warp", "warp [level] — campaign level warp", CmdWarp);

        // ---- settemp (QC generic settemp / settemp_restore) ----
        Register("settemp", "settemp <cvar> <value> — temporarily set a cvar (restored on map end)", CmdSettemp);
        Register("settemp_restore", "settemp_restore — restore all settemp cvars", CmdSettempRestore);

        // ---- introspection (QC GameCommand help / common/command help/who/teamstatus/time/info) ----
        Register("help", "help [command] — list commands or describe one", CmdHelp);
        Register("status", "status — print match/roster status", CmdStatus);
        Register("teamstatus", "teamstatus — print player/team scores", CmdTeamStatus);
        Register("who", "who — list connected clients", CmdWho);
        Register("time", "time — print the server time readouts", CmdTime);
        Register("info", "info <request> — print an admin info string", CmdInfo);
        Register("cvar_changes", "cvar_changes — list server cvars changed from their defaults", CmdCvarChanges);
        Register("cvar_purechanges", "cvar_purechanges — list gameplay cvars changed from their defaults", CmdCvarPureChanges);

        // ---- engine `defer` (DP cmd.c CF_SHARED) — run a command after a delay; the passed `restart` vote
        //      enqueues `defer 1 restart` so the announcer/result shows before the match restarts. ----
        Register("defer", "defer <seconds> <command> | defer clear — run a command after a delay", CmdDefer);

        // ---- generic commands (DP common/command/generic.qc + rpn.qc) — ALSO registered on the server bus so a
        //      client's `cmd rpn`/`cmd maplist`/… reaches them (QC routes the generic family through both
        //      programs). The console surface (ConsoleCommands) carries the same family for cfg/menu use. ----
        Register("rpn", "rpn <expression> — RPN calculator", CmdRpn);
        Register("addtolist", "addtolist <cvar> <value> — append a value to a list cvar", CmdAddToList);
        Register("removefromlist", "removefromlist <cvar> <value> — remove a value from a list cvar", CmdRemoveFromList);
        Register("maplist", "maplist <add|remove|shuffle|cleanup> [<map>] — edit g_maplist", CmdMaplist);
        Register("nextframe", "nextframe <command> — run a command on the next server tick", CmdNextFrame);

        // ---- server client commands (QC server/command/cmd.qc ClientCommand_*) ----
        Register("voice", "voice <voicetype> [message] — play a voice taunt", CmdVoice);
        Register("suggestmap", "suggestmap <map> — suggest a map for the end-of-match vote", CmdSuggestMap);
        Register("autoswitch", "autoswitch <on|off> — auto-switch to a better weapon on pickup", CmdAutoswitch);
        Register("physics", "physics <set> — select a client physics set", CmdPhysics);
        Register("clientversion", "clientversion <version> — (internal) report the client game version", CmdClientVersion);
        Register("sentcvar", "sentcvar <cvar> <value> — (internal) a client pushing a replicated cl_* cvar", CmdSentCvar);

        // ---- common reply commands (QC server/command/common.qc — cached reply strings) ----
        Register("records", "records [<page>] — show the gametype records", CmdRecords);
        Register("rankings", "rankings — show the race rankings for this map", CmdRankings);
        Register("lsmaps", "lsmaps — list the maps available on this server", CmdLsmaps);
        Register("printmaplist", "printmaplist — print the current map rotation", CmdPrintMapList);
        Register("ladder", "ladder — show the cross-map race ladder", CmdLadder);

        // ---- monster editor (QC server/command/common.qc editmob + the spawnmob/killmob cfg aliases) ----
        Register("editmob", "editmob <butcher|spawn|skin|movetarget|kill|name> [args] — edit/spawn monsters", CmdEditMob);
        Register("spawnmob", "spawnmob <type> [moveflag] — spawn a monster (alias of editmob spawn)", ctx => CmdEditMobAlias(ctx, "spawn"));
        Register("killmob", "killmob — kill the monster you're looking at (alias of editmob kill)", ctx => CmdEditMobAlias(ctx, "kill"));
    }

    private bool CmdSet(CommandContext ctx)
    {
        if (ctx.ArgCount < 3)
        {
            ctx.Print($"usage: {ctx.Arg(0)} <cvar> <value>");
            return true;
        }
        // QC: 'seta' implies the Save (archive) flag; ensure the cvar exists with that flag, then set.
        if (string.Equals(ctx.Arg(0), "seta", StringComparison.OrdinalIgnoreCase))
            Cvars.Register(ctx.Arg(1), ctx.Arg(2), CvarFlags.Save);
        Cvars.Set(ctx.Arg(1), ctx.Arg(2));
        return true;
    }

    private bool CmdCvar(CommandContext ctx)
    {
        if (ctx.ArgCount < 2)
        {
            ctx.Print("usage: cvar <name> [value]");
            return true;
        }
        string name = ctx.Arg(1);
        if (ctx.ArgCount >= 3)
        {
            Cvars.Set(name, ctx.Arg(2));
            return true;
        }
        ctx.Print($"\"{name}\" is \"{Cvars.String(name)}\"");
        return true;
    }

    private bool CmdToggle(CommandContext ctx)
    {
        if (ctx.ArgCount < 2)
        {
            ctx.Print("usage: toggle <cvar> [value1 value2 ...]");
            return true;
        }
        string name = ctx.Arg(1);
        // QC toggle: with an explicit value list, advance to the next; otherwise flip 0<->1.
        if (ctx.ArgCount > 2)
        {
            string cur = Cvars.String(name);
            int idx = -1;
            for (int i = 2; i < ctx.ArgCount; i++)
                if (ctx.Arg(i) == cur) { idx = i; break; }
            int next = idx < 0 ? 2 : (idx + 1 >= ctx.ArgCount ? 2 : idx + 1);
            Cvars.Set(name, ctx.Arg(next));
        }
        else
        {
            Cvars.Set(name, Cvars.Bool(name) ? "0" : "1");
        }
        return true;
    }

    private bool CmdRestart(CommandContext ctx)
    {
        _world.RestartMatch();
        ctx.Print("match restarted");
        return true;
    }

    private bool CmdEndMatch(CommandContext ctx)
    {
        _world.EndMatch();
        ctx.Print("match ended");
        return true;
    }

    private bool ChangeMatchTime(CommandContext ctx, float deltaSeconds)
    {
        // QC changematchtime: adjust the timelimit cvar (minutes). 0 means unlimited; don't extend that.
        float limMinutes = Cvars.Float("timelimit");
        if (limMinutes <= 0f && deltaSeconds > 0f)
        {
            ctx.Print("timelimit is unlimited; cannot extend");
            return true;
        }
        float newMinutes = System.Math.Max(0f, limMinutes + deltaSeconds / 60f);
        Cvars.Set("timelimit", newMinutes);
        ctx.Print($"timelimit is now {newMinutes} minutes");
        return true;
    }

    private bool CmdKick(CommandContext ctx)
    {
        if (ctx.ArgCount < 2)
        {
            ctx.Print("usage: kick <player> [reason]");
            return true;
        }
        Player? target = FindPlayerByName(ctx.Arg(1));
        if (target is null)
        {
            ctx.Print($"no player matching \"{ctx.Arg(1)}\"");
            return true;
        }
        string reason = ctx.ArgTail(2);
        if (KickHandler is not null)
        {
            KickHandler(target, reason);
            ctx.Print($"kicked {target.NetName}{(string.IsNullOrEmpty(reason) ? "" : $" ({reason})")}");
        }
        else
        {
            // no host kick pipeline wired: drop them through the client manager directly.
            _world.Clients.ClientDisconnect(target);
            ctx.Print($"removed {target.NetName}");
        }
        return true;
    }

    private bool CmdSay(CommandContext ctx)
    {
        // QC ClientCommand_say: argc >= 2 → Say(caller, false, NULL, msg, 1). [T46]
        string msg = ctx.ArgTail(1);
        if (string.IsNullOrEmpty(msg))
            return true;

        // Prefer the per-player chat engine (faithful routing/flood/ignore). When the host has NOT wired the
        // per-player delivery sink (ChatToPlayer) — a bare host or a test with only the legacy broadcast — fall
        // back to the old ChatHandler broadcast so a public `say` still reaches every client's console as before.
        if (ChatToPlayer is not null)
        {
            Chat.Say(ctx.Caller, /*teamsay*/ 0, /*privatesay*/ null, msg, /*floodcontrol*/ true);
        }
        else if (ChatHandler is not null)
        {
            // A broadcast pipeline is wired: the message reaches EVERYONE (incl. the sender) through it, so do
            // NOT also echo to ctx.Output — on a listen server the host is its own client and would see it twice.
            ChatHandler.Invoke(ctx.Caller, msg, /*teamOnly*/ false);
        }
        else
        {
            // No pipeline (a bare test / a host with no chat relay): echo locally so `say` isn't silent.
            ctx.Print($"{(ctx.Caller?.NetName ?? "server")}: {msg}");
        }
        return true;
    }

    /// <summary>QC <c>ClientCommand_say_team</c> (cmd.qc:661): argc >= 2 → Say(caller, true, NULL, msg, 1). [T46]</summary>
    private bool CmdSayTeam(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("say_team is a client command"); return true; }
        string msg = ctx.ArgTail(1);
        if (string.IsNullOrEmpty(msg))
            return true;
        Chat.Say(ctx.Caller, /*teamsay*/ 1, /*privatesay*/ null, msg, /*floodcontrol*/ true);
        return true;
    }

    /// <summary>
    /// QC <c>ClientCommand_tell</c> (cmd.qc:931): argc >= 3 → resolve the target client, then
    /// Say(caller, false, tell_to, msg, true). A connecting caller, a self-tell, or an unresolvable target each
    /// print the QC error string. The message is argv(2..) (the target token is argv(1)). [T46]
    /// </summary>
    private bool CmdTell(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("tell is a client command"); return true; }
        if (ctx.ArgCount < 3)
        {
            ctx.Print("Usage:^3 cmd tell <client> <message>");
            return true;
        }
        Player? target = FindPlayerByNameOrId(ctx.Arg(1));
        if (target is null)
        {
            ctx.Print($"tell: could not find a player matching \"{ctx.Arg(1)}\".");
            return true;
        }
        if (ReferenceEquals(target, ctx.Caller))
        {
            ctx.Print("You can't ^2tell^7 a message to yourself.");
            return true;
        }
        Chat.Say(ctx.Caller, /*teamsay*/ 0, /*privatesay*/ target, ctx.ArgTail(2), /*floodcontrol*/ true);
        return true;
    }

    /// <summary>
    /// QC <c>ClientCommand_ignore</c> (cmd.qc:452): add the named/numbered target to the caller's ignore list. The
    /// QC gate cascade: empty arg, unresolvable target, self-ignore, already-ignored, then add (and the
    /// list-full case). Keyed by PersistentId in the port — a target without one can't be ignored. [T46]
    /// </summary>
    private bool CmdIgnore(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("ignore is a client command"); return true; }
        if (ctx.ArgCount < 2 || ctx.Arg(1) == "")
        {
            ctx.Print("This command requires an argument. Use a player's name or their ID from the ^2status^7 command.");
            return true;
        }
        Player? target = FindPlayerByNameOrId(ctx.Arg(1));
        if (target is null || target.IsBot)
        {
            ctx.Print($"ignore: could not find a real player matching \"{ctx.Arg(1)}\".");
            return true;
        }
        if (ReferenceEquals(target, ctx.Caller))
        {
            ctx.Print("You can't ^2ignore^7 yourself.");
            return true;
        }
        if (Chat.IgnorePlayerInList(ctx.Caller, target))
        {
            ctx.Print($"{target.NetName} ^7is already ignored!");
            return true;
        }
        int r = Chat.IgnoreAddPlayer(ctx.Caller, target);
        if (r == 0)
            ctx.Print($"You may only ignore up to {Chat.IgnoreMaxPlayers} players, remove one before trying again.");
        else
            ctx.Print($"You will no longer receive messages from {target.NetName}^7 for this match, use ^2unignore^7 to hear them again.");
        return true;
    }

    /// <summary>QC <c>ClientCommand_unignore</c> (cmd.qc:988): remove the named/numbered target from the caller's
    /// ignore list. [T46]</summary>
    private bool CmdUnignore(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("unignore is a client command"); return true; }
        if (ctx.ArgCount < 2 || ctx.Arg(1) == "")
        {
            ctx.Print("This command requires an argument. Use a player's name or their ID from the ^2status^7 command.");
            return true;
        }
        Player? target = FindPlayerByNameOrId(ctx.Arg(1));
        if (target is null || target.IsBot)
        {
            ctx.Print($"unignore: could not find a real player matching \"{ctx.Arg(1)}\".");
            return true;
        }
        if (ReferenceEquals(target, ctx.Caller))
        {
            ctx.Print("You can't ^2unignore^7 yourself.");
            return true;
        }
        Chat.IgnoreRemovePlayer(ctx.Caller, target);
        ctx.Print($"You can now receive messages from {target.NetName} ^7again.");
        return true;
    }

    /// <summary>QC <c>ClientCommand_clear_ignores</c> (cmd.qc:226): clear the caller's whole ignore list. [T46]</summary>
    private bool CmdClearIgnores(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("clear_ignores is a client command"); return true; }
        Chat.IgnoreClearAll(ctx.Caller);
        ctx.Print("All ignores cleared!");
        return true;
    }

    private bool CmdVote(CommandContext ctx) => _world.Voting.Execute(ctx);

    private bool CmdBotAdd(CommandContext ctx)
    {
        string? name = ctx.ArgCount >= 2 ? ctx.Arg(1) : null;
        float? skill = ctx.ArgCount >= 3 ? ctx.ArgFloat(2) : null;
        bool ok = AddBotHandler?.Invoke(name, skill) ?? false;
        ctx.Print(ok ? $"added bot {name ?? "(auto)"}" : "no bot pipeline wired (set Commands.AddBotHandler)");
        return true;
    }

    private bool CmdBotRemove(CommandContext ctx)
    {
        string? name = ctx.ArgCount >= 2 ? ctx.Arg(1) : null;
        bool ok = RemoveBotHandler?.Invoke(name) ?? false;
        ctx.Print(ok ? "removed bot" : "no bot to remove");
        return true;
    }

    private bool CmdMap(CommandContext ctx)
    {
        if (ctx.ArgCount < 2)
        {
            ctx.Print("usage: map <name>");
            return true;
        }
        ChangeLevelHandler?.Invoke(ctx.Arg(1));
        ctx.Print($"changing to {ctx.Arg(1)}");
        return true;
    }

    private bool CmdGotoMap(CommandContext ctx)
    {
        if (ctx.ArgCount < 2)
        {
            ctx.Print("usage: gotomap <name>");
            return true;
        }
        _world.QueuedNextMap = ctx.Arg(1);
        _world.EndMatch();
        ctx.Print($"next map: {ctx.Arg(1)}");
        return true;
    }

    private bool CmdHelp(CommandContext ctx)
    {
        if (ctx.ArgCount >= 2 && _commands.TryGetValue(ctx.Arg(1), out var cmd))
        {
            ctx.Print(cmd.Help);
            return true;
        }
        var names = new List<string>(_commands.Keys);
        names.Sort(StringComparer.OrdinalIgnoreCase);
        ctx.Print("commands: " + string.Join(", ", names));
        return true;
    }

    private bool CmdStatus(CommandContext ctx)
    {
        ctx.Print($"gametype: {_world.GameType?.NetName ?? "(none)"}  time: {_world.Time:0.0}");
        ctx.Print($"players: {_world.Clients.PlayerCount} ({_world.Clients.BotCount} bots)");
        foreach (var row in _world.Scores.Sorted())
            ctx.Print($"  {row.Player.NetName,-16} score {row.Score,4}  k/d {row.Kills}/{row.Deaths}");
        if (_world.Intermission.Running)
            ctx.Print("** intermission **");
        return true;
    }

    // =============================================================================================
    // bots / match flow (QC setbots/removebots/allready/resetmatch/gametype/cointoss/nextmap)
    // =============================================================================================

    private bool CmdSetBots(CommandContext ctx)
    {
        int n = (int)ctx.ArgFloat(1);
        SettempCvars.Set("bot_number", n);
        ctx.Print($"bot_number set to {n}");
        // top up / trim via the host's bot pipeline if wired.
        int have = _world.Clients.BotCount;
        for (int i = have; i < n; i++) AddBotHandler?.Invoke(null, null);
        for (int i = have; i > n; i--) RemoveBotHandler?.Invoke(null);
        return true;
    }

    private bool CmdRemoveBots(CommandContext ctx)
    {
        SettempCvars.Set("bot_number", 0);
        int removed = 0;
        while (_world.Clients.BotCount > 0 && (RemoveBotHandler?.Invoke(null) ?? false)) removed++;
        ctx.Print($"removed {removed} bot(s)");
        return true;
    }

    private bool CmdNextMap(CommandContext ctx)
    {
        if (ctx.ArgCount >= 2) { _world.QueuedNextMap = ctx.Arg(1); ctx.Print($"next map: {ctx.Arg(1)}"); }
        else ctx.Print($"next map: {(string.IsNullOrEmpty(_world.QueuedNextMap) ? "(rotation)" : _world.QueuedNextMap)}");
        return true;
    }

    private bool CmdAllReady(CommandContext ctx)
    {
        if (!_world.Warmup.WarmupStage) { ctx.Print("Not in warmup."); return true; }
        _world.RestartMatch();
        ctx.Print("all ready — starting match");
        return true;
    }

    private bool CmdResetMatch(CommandContext ctx)
    {
        _world.RestartMatch();
        ctx.Print("match reset");
        return true;
    }

    private bool CmdGameType(CommandContext ctx)
    {
        if (ctx.ArgCount < 2) { ctx.Print("usage: gametype <mode>"); return true; }
        // a gametype change takes effect on the next map; queue a restart on the same map under the new type.
        Cvars.Set("gametype", ctx.Arg(1));
        _world.QueuedNextMap = string.IsNullOrEmpty(_world.MapName) ? _world.QueuedNextMap : _world.MapName;
        ctx.Print($"gametype will change to {ctx.Arg(1)} on the next map");
        return true;
    }

    private bool CmdCoinToss(CommandContext ctx)
    {
        string a = ctx.ArgCount >= 2 ? ctx.Arg(1) : "HEADS";
        string b = ctx.ArgCount >= 3 ? ctx.Arg(2) : "TAILS";
        // deterministic-ish flip off the sim clock (no Math.random in the headless core).
        bool first = ((int)(Api.Services is not null ? Api.Clock.Time * 1000f : 0f) & 1) == 0;
        string result = first ? a : b;
        ChatBroadcast?.Invoke($"^2* Coin toss: ^3{result}");
        ctx.Print($"coin toss: {result}");
        return true;
    }

    // =============================================================================================
    // teamplay admin (QC lockteams/unlockteams/shuffleteams/moveplayer/allspec/nospectators)
    // =============================================================================================

    private bool CmdLockTeams(CommandContext ctx)
    {
        if (!_world.Teamplay.IsTeamGame) { ctx.Print("Teams can only be locked in a team game."); return true; }
        _world.TeamsLocked = true;
        ChatBroadcast?.Invoke("^2* Teams are now locked.");
        return true;
    }

    private bool CmdUnlockTeams(CommandContext ctx)
    {
        if (!_world.Teamplay.IsTeamGame) { ctx.Print("Teams can only be locked in a team game."); return true; }
        _world.TeamsLocked = false;
        ChatBroadcast?.Invoke("^2* Teams are now unlocked.");
        return true;
    }

    private bool CmdShuffleTeams(CommandContext ctx)
    {
        if (!_world.Teamplay.IsTeamGame) { ctx.Print("Can't shuffle teams in a non-team game."); return true; }
        var players = new List<Player>(_world.Clients.Players);
        int teamCount = _world.Teamplay.TeamCount;
        int idx = 0;
        // round-robin assignment in a stable order (QC FOREACH_CLIENT_RANDOM; deterministic here).
        foreach (Player p in players)
        {
            int target = Teams.All[idx % teamCount];
            if ((int)p.Team != target)
            {
                p.Team = target;
                _world.Teamplay.KillPlayerForTeamChange(p);
            }
            idx++;
        }
        ChatBroadcast?.Invoke("^2* Teams have been shuffled.");
        return true;
    }

    private bool CmdMovePlayer(CommandContext ctx)
    {
        if (ctx.ArgCount < 3) { ctx.Print("usage: moveplayer <player> <red|blue|yellow|pink|auto|spec>"); return true; }
        Player? target = FindPlayerByName(ctx.Arg(1));
        if (target is null) { ctx.Print($"no player matching \"{ctx.Arg(1)}\""); return true; }
        string dest = ctx.Arg(2).ToLowerInvariant();
        if (dest is "spec" or "spectator" or "spectate")
        {
            _world.Clients.PutObserverInServer(target); // QC: real observer transition, not just the scoreboard flag
            ctx.Print($"moved {target.NetName} to spectators");
            return true;
        }
        if (!_world.Teamplay.IsTeamGame) { ctx.Print("not a team game"); return true; }
        int team = dest == "auto" ? _world.Teamplay.AssignBestTeam(target, _world.Clients.Players) : TeamFromName(dest);
        if (team == Teams.None) { ctx.Print($"invalid team \"{dest}\""); return true; }
        target.Team = team;
        _world.Teamplay.KillPlayerForTeamChange(target);
        ctx.Print($"moved {target.NetName} to {Teams.Name(team)}");
        return true;
    }

    private bool CmdAllSpec(CommandContext ctx)
    {
        string reason = ctx.ArgTail(1);
        int n = 0;
        // Snapshot the roster: PutObserverInServer may mutate roster-adjacent state, so iterate a copy.
        foreach (Player p in new List<Player>(_world.Clients.Players))
            if (!p.IsObserver) { _world.Clients.PutObserverInServer(p); n++; }
        ChatBroadcast?.Invoke($"^2* All players moved to spectators{(string.IsNullOrEmpty(reason) ? "" : $" ({reason})")}");
        ctx.Print($"moved {n} player(s) to spectators");
        return true;
    }

    private bool CmdNoSpectators(CommandContext ctx)
    {
        SettempCvars.Set("sv_spectate", "0");
        ChatBroadcast?.Invoke("^2* Spectating is now disabled.");
        return true;
    }

    // =============================================================================================
    // per-player commands (QC ClientCommand ready/join/spectate/selectteam/kill)
    // =============================================================================================

    private bool CmdReady(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("ready is a client command"); return true; }
        if (!_world.Warmup.WarmupStage) { ctx.Print("Not in warmup."); return true; }
        bool ready = _world.ToggleReady(ctx.Caller);
        ChatBroadcast?.Invoke($"^2* {ctx.Caller.NetName} is {(ready ? "ready" : "NOT ready")}");
        return true;
    }

    private bool CmdJoin(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("join is a client command"); return true; }
        Player p = ctx.Caller;
        if (p.FragsStatus == Player.FragsSpectator)
            p.FragsStatus = Player.FragsPlayer;
        if (_world.Teamplay.IsTeamGame && (int)p.Team == Teams.None && !_world.TeamsLocked)
            _world.Teamplay.AssignBestTeam(p, _world.Clients.Players);
        // QC Join: an OBSERVER goes through the observer→player transition (clears IsObserver/Spectatee, restores
        // MOVETYPE_WALK/SOLID/DAMAGE via PutPlayerInServer); a live (dead) player just respawns.
        if (p.IsObserver)
            _world.Clients.Join(p);
        else
            _world.Clients.Spawn(p);
        ctx.Print("joined the game");
        return true;
    }

    private bool CmdSpectate(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("spectate is a client command"); return true; }
        if (!Cvars.Bool("sv_spectate")) { ctx.Print("Spectating is not allowed."); return true; }
        Player p = ctx.Caller;
        // QC ClientCommand_spectate: run the REAL observer transition (free-fly, non-solid, model hidden,
        // weapons stripped) — not just flip the scoreboard sentinel as before, which left the player a solid,
        // shootable, still-scoring actor (SPEC4/LOOP2).
        _world.Clients.PutObserverInServer(p);
        // optional <client> arg → follow that player (QC Spectate(this, GetFilteredEntity(argv(1)))).
        if (ctx.ArgCount >= 2 && ctx.Arg(1) != "0")
        {
            Player? tgt = FindPlayerByName(ctx.Arg(1));
            if (tgt is not null && !tgt.IsObserver && !tgt.IsDead && !ReferenceEquals(tgt, p))
                _world.Clients.Spectate(p, tgt);
            else
                ctx.Print($"no player matching \"{ctx.Arg(1)}\"");
        }
        ctx.Print("now spectating");
        return true;
    }

    private bool CmdSelectTeam(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("selectteam is a client command"); return true; }
        if (!_world.Teamplay.IsTeamGame) { ctx.Print("Not a team game."); return true; }
        if (_world.TeamsLocked) { ctx.Print("Teams are locked."); return true; }
        if (ctx.ArgCount < 2) { ctx.Print("usage: selectteam <red|blue|yellow|pink|auto>"); return true; }
        string sel = ctx.Arg(1).ToLowerInvariant();
        int team = sel == "auto" ? _world.Teamplay.AssignBestTeam(ctx.Caller, _world.Clients.Players) : TeamFromName(sel);
        if (team == Teams.None) { ctx.Print($"invalid team \"{sel}\""); return true; }
        if ((int)ctx.Caller.Team == team) { ctx.Print("already on that team"); return true; }
        ctx.Caller.Team = team;
        _world.Teamplay.KillPlayerForTeamChange(ctx.Caller);
        ctx.Print($"joined {Teams.Name(team)}");
        return true;
    }

    private bool CmdKill(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("kill is a client command"); return true; }
        Player p = ctx.Caller;
        if (p.FragsStatus == Player.FragsSpectator) return true;
        if (p.IsDead) { ctx.Print("already dead"); return true; }
        XonoticGodot.Common.Gameplay.Damage.Combat.Damage(p, null, null, 100000f,
            XonoticGodot.Common.Gameplay.Damage.DeathTypes.Kill, p.Origin, System.Numerics.Vector3.Zero);
        return true;
    }

    // =============================================================================================
    // minigames (QC common/minigames/sv_minigames.qc ClientCommand_minigame)
    // =============================================================================================

    /// <summary>
    /// QC <c>ClientCommand_minigame(caller, request, argc, command)</c>: the per-player <c>cmd minigame …</c>
    /// dispatcher. Gated on <c>sv_minigames</c> + a real client caller (a server-console invocation is rejected
    /// — minigames are a per-player command). The subcommand tokens are the QC MINIGAME_COMMON_CMD literals:
    /// <c>create</c>/<c>join</c>/<c>list</c>/<c>list-sessions</c>/<c>part</c>/<c>end</c>/<c>invite</c>; any other
    /// first token while the caller is in a game is forwarded to the game as a move (QC the "cmd" event). A bad
    /// verb prints the multi-line usage block. Always returns true (consumed).
    /// </summary>
    private bool CmdMinigame(CommandContext ctx)
    {
        // QC: if(!autocvar_sv_minigames){ sprint "Minigames are not enabled!\n"; return; }
        if (!Cvars.Bool("sv_minigames"))
        {
            ctx.Print("Minigames are not enabled!");
            return true;
        }
        // QC entry point is a CLIENT command (per-player, caller-gated), NOT a SERVER_COMMAND. A console/rcon
        // invocation has no caller to seat in a session, so reject it (mirrors the QC caller dependency).
        if (ctx.Caller is null)
        {
            ctx.Print("minigame is a client command");
            return true;
        }
        // QC playban gate (sv_minigames.qc:318-323): a play-banned caller can't create/join/move while
        // g_playban_minigames is set. Ships 0 (xonotic-server.cfg:436) so this is inactive by default; only
        // diverges when an admin opts in. Reuses the same g_playban_list membership the playban command writes.
        if (Cvars.Bool("g_playban_minigames") && Bans.PlayerInList(ctx.Caller, "g_playban_list"))
        {
            // QC also fires CENTER_JOIN_PLAYBAN here; the port surfaces the sprint text (the center-print notif
            // is the HUD layer's job, not the command output channel).
            ctx.Print("You aren't allowed to play minigames because you are banned from them in this server.");
            return true;
        }

        MinigameSessionManager mg = _world.Minigames;
        Player caller = ctx.Caller;
        string verb = ctx.Arg(1);

        // QC: "create" <game> (argc>2) → start_minigame.
        if (verb == "create" && ctx.ArgCount > 2)
        {
            MinigameSession? session = mg.Create(caller, ctx.Arg(2));
            ctx.Print(session is not null
                ? $"Created minigame session: {session.NetName}"
                : "Cannot start minigame session!");
            return true;
        }
        // QC: "join" <session> (argc>2) → join_minigame.
        if (verb == "join" && ctx.ArgCount > 2)
        {
            MinigameSession? session = mg.JoinByName(caller, ctx.Arg(2));
            ctx.Print(session is not null
                ? $"Joined: {session.NetName}"
                : "Cannot join given minigame session!");
            return true;
        }
        // QC: "list" → FOREACH(Minigames) sprint "<netname> (<message>) ".
        if (verb == "list")
        {
            foreach (Minigame m in Minigames.All)
                ctx.Print($"{m.NetName} ({m.DisplayName}) ");
            return true;
        }
        // QC: "list-sessions" → walk minigame_sessions sprint each .netname.
        if (verb == "list-sessions")
        {
            foreach (MinigameSession s in mg.Sessions)
                ctx.Print(s.NetName);
            return true;
        }
        // QC: "end"|"part" → if active part_minigame + "Left minigame session", else "You aren't playing…".
        if (verb is "end" or "part")
        {
            if (mg.ActiveSessionOf(caller) is not null)
            {
                mg.Part(caller);
                ctx.Print("Left minigame session");
            }
            else
            {
                ctx.Print("You aren't playing any minigame...");
            }
            return true;
        }
        // QC: "invite" <player> (argc>2) → resolve the target + invite_minigame + success/err sprint.
        if (verb == "invite" && ctx.ArgCount > 2)
        {
            if (mg.ActiveSessionOf(caller) is null)
            {
                ctx.Print("You aren't playing any minigame...");
                return true;
            }
            Player? target = FindPlayerByNameOrId(ctx.Arg(2));
            string error = mg.Invite(caller, target, out _);
            if (error.Length == 0)
                ctx.Print($"You have invited {target!.NetName} to join your game of {mg.ActiveSessionOf(caller)!.Game.DisplayName}");
            else
                ctx.Print($"Could not invite: {error}.");
            return true;
        }
        // QC fall-through: an unrecognized first token while in a game is the game's "cmd" event (a move). The
        // port forwards the WHOLE tail (verb + args) so Pong's "throw"/"move 1"/"pong_aimore" and the grid
        // games' bare tile/column reach Minigame.Move (ForwardMove marks the session dirty on acceptance).
        if (mg.ActiveSessionOf(caller) is not null && verb.Length > 0)
        {
            if (mg.ForwardMove(caller, ctx.ArgTail(1)))
                return true;
        }
        else if (verb.Length > 0)
        {
            ctx.Print($"Wrong command:^1 {ctx.ArgTail(0)}");
        }

        // QC: the multi-line Usage dump (printed on a bad verb / no active game).
        ctx.Print("");
        ctx.Print("Usage:^3 cmd minigame create <minigame>");
        ctx.Print("  Start a new minigame session");
        ctx.Print("Usage:^3 cmd minigame join <session>");
        ctx.Print("  Join an exising minigame session");
        ctx.Print("Usage:^3 cmd minigame list");
        ctx.Print("  List available minigames");
        ctx.Print("Usage:^3 cmd minigame list-sessions");
        ctx.Print("  List available minigames sessions");
        ctx.Print("Usage:^3 cmd minigame part|end");
        ctx.Print("  Leave the current minigame");
        ctx.Print("Usage:^3 cmd minigame invite <player>");
        ctx.Print("  Invite the given player to join you in a minigame");
        return true;
    }

    /// <summary>QC <c>GetIndexedEntity(argc, n)</c> (the minigame invite target): resolve a "#&lt;id&gt;" token or
    /// a name to a connected player. Reuses <see cref="FindPlayerByName"/> for the name path.</summary>
    private Player? FindPlayerByNameOrId(string token)
    {
        if (token.StartsWith('#') && int.TryParse(token.AsSpan(1), out int idx))
        {
            foreach (Player p in _world.Clients.Players)
                if (p.Index == idx || p.PlayerId == idx)
                    return p;
            return null;
        }
        return FindPlayerByName(token);
    }

    // =============================================================================================
    // weapon impulses (QC server/impulse.qc ImpulseCommands → selection.qc)
    // =============================================================================================

    /// <summary>
    /// QC <c>ImpulseCommands</c> (server/impulse.qc): a client sent <c>impulse N</c>. Route the weapon-related
    /// impulse numbers onto the selection/reload API (<see cref="WeaponImpulses.Handle"/>). Non-weapon impulses
    /// (waypoints/cheats/etc.) aren't handled here — they're owned by other tasks — so they no-op with a note.
    /// QC also gates impulses on game_stopped / timeout / round-not-started; the game-stopped + timeout gates are
    /// honored, the round-start gate is left to the round handler.
    /// </summary>
    private bool CmdImpulse(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("impulse is a client command"); return true; }
        int imp = (int)ctx.ArgFloat(1);
        return DispatchImpulse(ctx, ctx.Caller, imp);
    }

    /// <summary>A console alias (weapon_next/prev/best/last/drop/reload) resolved to its impulse number.</summary>
    private bool CmdWeaponImpulse(CommandContext ctx, int imp)
    {
        if (ctx.Caller is null) { ctx.Print("weapon commands are client commands"); return true; }
        return DispatchImpulse(ctx, ctx.Caller, imp);
    }

    private bool DispatchImpulse(CommandContext ctx, Player caller, int imp)
    {
        if (imp <= 0) return true;
        // QC ImpulseCommands: no impulses while the match is stopped (game_stopped) or while the game is FROZEN
        // by an active timeout (server/impulse.qc gates only on timeout_status == TIMEOUT_ACTIVE). The lead-time
        // COUNTDOWN before the pause (timeout_status == TIMEOUT_LEADTIME) must NOT block impulses, so gate on the
        // active-pause predicate (IsPaused == Status==ActivePause), not the broader .Active (Status != Inactive).
        if (_world.Intermission.Running) return true;
        if (_world.Timeout.IsPaused) return true;
        // [T37] SEAM C: a seated pilot's impulse routes to the per-vehicle mode switch/cycle BEFORE weapon
        // impulses (QC vehicle_impulse runs first). The existing C2S impulse path then makes the weapon-group
        // keys (1/2/3, weapnext/prev) switch vehicle modes when seated with zero extra net work.
        if (caller.Vehicle is not null && !VehicleCommon.IsDead(caller.Vehicle) && VehicleBoarding.Impulse(caller, imp))
            return true;
        if (!WeaponImpulses.Handle(caller, imp))
            ctx.Print($"impulse {imp} not handled");
        return true;
    }

    // =============================================================================================
    // timeout / timein (QC common/command timeout)
    // =============================================================================================

    private bool CmdTimeout(CommandContext ctx)
    {
        if (_world.Timeout.CallTimeout(ctx.Caller, out string err)) ctx.Print("timeout called");
        else ctx.Print(err);
        return true;
    }

    private bool CmdTimein(CommandContext ctx)
    {
        if (_world.Timeout.CallTimein(ctx.Caller, out string err)) ctx.Print("timein");
        else ctx.Print(err);
        return true;
    }

    // =============================================================================================
    // bans (QC command/banning.qc)
    // =============================================================================================

    private bool CmdBan(CommandContext ctx)
    {
        if (ctx.ArgCount < 2) { ctx.Print("usage: ban <address> [bantime] [reason]"); return true; }
        string ip = ctx.Arg(1);
        float bantime = ctx.ArgCount >= 3 && ctx.ArgFloat(2) > 0f ? ctx.ArgFloat(2) : Cvars.FloatOr("g_ban_default_bantime", 5400f);
        int reasonArg = ctx.ArgCount >= 3 && ctx.ArgFloat(2) > 0f ? 3 : 2;
        string reason = ctx.ArgCount > reasonArg ? ctx.ArgTail(reasonArg) : "No reason provided";
        _world.Bans.Insert(ip, bantime, reason);
        ctx.Print($"banned {ip} for {bantime:0}s");
        return true;
    }

    private bool CmdKickBan(CommandContext ctx)
    {
        if (ctx.ArgCount < 2) { ctx.Print("usage: kickban <player> [bantime] [masksize] [reason]"); return true; }
        Player? target = FindPlayerByName(ctx.Arg(1));
        if (target is null) { ctx.Print($"no player matching \"{ctx.Arg(1)}\""); return true; }
        float bantime = ctx.ArgCount >= 3 && ctx.ArgFloat(2) > 0f ? ctx.ArgFloat(2) : Cvars.FloatOr("g_ban_default_bantime", 5400f);
        int masksize = ctx.ArgCount >= 4 ? (int)ctx.ArgFloat(3) : Cvars.Int("g_ban_default_masksize");
        string reason = ctx.ArgCount >= 5 ? ctx.ArgTail(4) : "No reason provided";
        _world.Bans.KickBanClient(target, bantime, masksize, reason);
        ctx.Print($"kickbanned {target.NetName}");
        return true;
    }

    private bool CmdUnban(CommandContext ctx)
    {
        string arg = ctx.Arg(1).TrimStart('#');
        if (!int.TryParse(arg, out int id) || id < 0) { ctx.Print("usage: unban <banid>"); return true; }
        ctx.Print(_world.Bans.Delete(id) ? $"unbanned #{id}" : $"no ban #{id}");
        return true;
    }

    private bool CmdBanList(CommandContext ctx)
    {
        // Bans.View routes through Bans.Log → ChatBroadcast; also echo to the caller.
        var captured = new List<string>();
        Action<string>? prev = _world.Bans.Log;
        _world.Bans.Log = s => captured.Add(s);
        _world.Bans.View();
        _world.Bans.Log = prev;
        foreach (string s in captured) ctx.Print(s);
        return true;
    }

    private bool CmdPlayBan(CommandContext ctx)
    {
        Player? target = FindPlayerByName(ctx.Arg(1));
        if (target is null) { ctx.Print($"no player matching \"{ctx.Arg(1)}\""); return true; }
        Bans.AddToList(target, "g_playban_list");
        target.FragsStatus = Player.FragsSpectator; // force spectate now (QC PutObserverInServer)
        ctx.Print($"play-banned {target.NetName}");
        return true;
    }

    private bool CmdListAdd(CommandContext ctx, string listCvar, string verb)
    {
        Player? target = FindPlayerByName(ctx.Arg(1));
        if (target is null) { ctx.Print($"no player matching \"{ctx.Arg(1)}\""); return true; }
        Bans.AddToList(target, listCvar);
        ctx.Print($"{verb} {target.NetName}");
        return true;
    }

    /// <summary>QC <c>mute</c> (the chatban admin verb): add the player to the chatban list AND set the live
    /// <see cref="Player.Muted"/> fake-accept flag (server/chat.qc:255 — a muted player's chat is faked). [T46]</summary>
    private bool CmdMute(CommandContext ctx)
    {
        Player? target = FindPlayerByName(ctx.Arg(1));
        if (target is null) { ctx.Print($"no player matching \"{ctx.Arg(1)}\""); return true; }
        Bans.AddToList(target, "g_chatban_list");
        target.Muted = true;
        ctx.Print($"muted {target.NetName}");
        return true;
    }

    /// <summary>QC <c>unmute</c>: remove from the chatban list AND clear <see cref="Player.Muted"/>. [T46]</summary>
    private bool CmdUnmute(CommandContext ctx)
    {
        Player? target = FindPlayerByName(ctx.Arg(1));
        if (target is null) { ctx.Print($"no player matching \"{ctx.Arg(1)}\""); return true; }
        Bans.RemoveFromList(target, "g_chatban_list");
        target.Muted = false;
        ctx.Print($"unmuted {target.NetName}");
        return true;
    }

    private bool CmdListRemove(CommandContext ctx, string listCvar, string verb)
    {
        Player? target = FindPlayerByName(ctx.Arg(1));
        if (target is null) { ctx.Print($"no player matching \"{ctx.Arg(1)}\""); return true; }
        Bans.RemoveFromList(target, listCvar);
        ctx.Print($"{verb} {target.NetName}");
        return true;
    }

    // =============================================================================================
    // cheats (QC CheatCommand) + campaign warp + settemp
    // =============================================================================================

    private bool CmdCheat(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("cheat commands are client-only"); return true; }
        if (!_world.Cheats.Command(ctx.Caller, ctx.Argv))
            ctx.Print("Cheats are not enabled on this server (sv_cheats).");
        return true;
    }

    private bool CmdWarp(CommandContext ctx)
    {
        if (!Cvars.Bool("g_campaign")) { ctx.Print("Not in a campaign."); return true; }
        if (ctx.ArgCount >= 2) _world.Campaign.LevelWarp((int)ctx.ArgFloat(1));
        else _world.Campaign.LevelWarp(-1);
        ctx.Print("campaign warp");
        return true;
    }

    private bool CmdSettemp(CommandContext ctx)
    {
        if (ctx.ArgCount < 3) { ctx.Print("usage: settemp <cvar> <value>"); return true; }
        SettempCvars.Set(ctx.Arg(1), ctx.Arg(2));
        ctx.Print($"settemp {ctx.Arg(1)} = {ctx.Arg(2)}");
        return true;
    }

    private bool CmdSettempRestore(CommandContext ctx)
    {
        int n = SettempCvars.Restore();
        ctx.Print($"restored {n} cvar(s)");
        return true;
    }

    // =============================================================================================
    // introspection (QC teamstatus/who/time/info/cvar_changes)
    // =============================================================================================

    private bool CmdTeamStatus(CommandContext ctx)
    {
        if (_world.Teamplay.IsTeamGame)
            foreach (int t in Teams.Active(_world.Teamplay.TeamCount))
                ctx.Print($"{Teams.Name(t),-8} score {_world.Scores.TeamScore(t),4}  players {_world.Teamplay.CountTeam(t, _world.Clients.Players)}");
        foreach (var row in _world.Scores.Sorted())
            ctx.Print($"  {row.Player.NetName,-16} {Teams.Name((int)row.Player.Team),-8} score {row.Score,4}  k/d {row.Kills}/{row.Deaths}");
        return true;
    }

    private bool CmdWho(CommandContext ctx)
    {
        ctx.Print($"{"#",-4} {"name",-18} {"team",-8} {"bot",-4} address");
        foreach (Player p in _world.Clients.Players)
            ctx.Print($"{p.Index,-4} {p.NetName,-18} {Teams.Name((int)p.Team),-8} {(p.IsBot ? "yes" : "no"),-4} {(ctx.IsServerConsole ? p.NetAddress : "")}");
        return true;
    }

    private bool CmdTime(CommandContext ctx)
    {
        ctx.Print($"time: {_world.Time:0.000}");
        return true;
    }

    private bool CmdInfo(CommandContext ctx)
    {
        if (ctx.ArgCount < 2) { ctx.Print("usage: info <request>"); return true; }
        string s = Cvars.String($"sv_info_{ctx.Arg(1)}");
        ctx.Print(string.IsNullOrEmpty(s) ? $"no info for \"{ctx.Arg(1)}\"" : s);
        return true;
    }

    private bool CmdCvarChanges(CommandContext ctx)
    {
        // QC CommonCommand_cvar_changes: print_to(caller, cvar_changes). The log is rebuilt lazily so it
        // reflects any cvars changed after boot (QC builds it once at init; rebuilding on read is a superset).
        _world.Commands.Replies.Recompute();
        ctx.Print(_world.Commands.Replies.CvarChanges);
        return true;
    }

    private bool CmdCvarPureChanges(CommandContext ctx)
    {
        // QC CommonCommand_cvar_purechanges: print_to(caller, cvar_purechanges).
        _world.Commands.Replies.Recompute();
        ctx.Print(_world.Commands.Replies.CvarPurechanges);
        return true;
    }

    // =============================================================================================
    // engine defer (DP cmd.c Cmd_Defer_f) + the generic command family on the server bus
    // =============================================================================================

    /// <summary>
    /// DP <c>Cmd_Defer_f</c>: <c>defer</c> (list pending), <c>defer clear</c> (drop all), <c>defer &lt;s&gt;
    /// &lt;command&gt;</c> (enqueue). The port enqueues <c>ArgTail(2)</c> (so an unquoted multi-word
    /// <c>defer 1 say hi</c> also works — a harmless superset of DP's single-token <c>argv(2)</c>). The pump
    /// fires queued commands back through <see cref="Execute"/> from the server tick (GameWorld.OnStartFrame).
    /// </summary>
    private bool CmdDefer(CommandContext ctx)
    {
        if (ctx.ArgCount == 1)
        {
            foreach (string line in Deferred.Describe(_world.Time))
                ctx.Print(line);
            return true;
        }
        if (ctx.ArgCount == 2 && string.Equals(ctx.Arg(1), "clear", StringComparison.OrdinalIgnoreCase))
        {
            Deferred.Clear();
            return true;
        }
        if (ctx.ArgCount >= 3 && ctx.ArgTail(2).Length > 0)
        {
            Deferred.Defer(ctx.ArgFloat(1), ctx.ArgTail(2), _world.Time);
            return true;
        }
        ctx.Print("usage: defer <seconds> <command>");
        ctx.Print("       defer clear");
        return true;
    }

    /// <summary>QC GENERIC_COMMAND(rpn): run the RPN VM against the server cvar store, printing diagnostics.</summary>
    private bool CmdRpn(CommandContext ctx)
    {
        XonoticGodot.Engine.Console.Rpn.Run(ctx.Argv, _world.Services.CvarsImpl, ctx.Print);
        return true;
    }

    /// <summary>QC GenericCommand_addtolist: append a value to a list cvar (deduped, at the END).</summary>
    private bool CmdAddToList(CommandContext ctx)
    {
        if (ctx.ArgCount < 3) { ctx.Print($"Usage: {ctx.Arg(0)} <cvar> <value>"); return true; }
        string cvar = ctx.Arg(1), value = ctx.Arg(2);
        string cur = Cvars.String(cvar);
        if (cur == "") { Cvars.Set(cvar, value); return true; }
        foreach (string w in XonoticGodot.Engine.Console.WordList.Words(cur))
            if (w == value) return true; // already present
        Cvars.Set(cvar, XonoticGodot.Engine.Console.WordList.Cons(cur, value));
        return true;
    }

    /// <summary>QC GenericCommand_removefromlist: rebuild a list cvar keeping only words != value.</summary>
    private bool CmdRemoveFromList(CommandContext ctx)
    {
        if (ctx.ArgCount != 3) { ctx.Print($"Usage: {ctx.Arg(0)} <cvar> <value>"); return true; }
        string cvar = ctx.Arg(1), removal = ctx.Arg(2);
        string rebuilt = "";
        foreach (string w in XonoticGodot.Engine.Console.WordList.Words(Cvars.String(cvar)))
            if (w != removal) rebuilt = XonoticGodot.Engine.Console.WordList.Cons(rebuilt, w);
        Cvars.Set(cvar, rebuilt);
        return true;
    }

    /// <summary>QC GenericCommand_maplist: add (PREPEND) / remove / shuffle / cleanup g_maplist.</summary>
    private bool CmdMaplist(CommandContext ctx)
    {
        switch (ctx.Arg(1))
        {
            case "add" when ctx.ArgCount == 3:
            {
                string cur = Cvars.String("g_maplist");
                // QC PREPENDS for maplist add (unlike addtolist's append).
                Cvars.Set("g_maplist", cur == "" ? ctx.Arg(2) : ctx.Arg(2) + " " + cur);
                return true;
            }
            case "remove" when ctx.ArgCount == 3:
            {
                string rebuilt = "";
                foreach (string w in XonoticGodot.Engine.Console.WordList.Words(Cvars.String("g_maplist")))
                    if (w != ctx.Arg(2)) rebuilt = XonoticGodot.Engine.Console.WordList.Cons(rebuilt, w);
                Cvars.Set("g_maplist", rebuilt);
                return true;
            }
            case "shuffle":
                Cvars.Set("g_maplist", XonoticGodot.Engine.Console.WordList.Shuffle(Cvars.String("g_maplist"), _maplistRng));
                return true;
            case "cleanup":
                // No reachable map catalog here → identity (keep all words), matching the console surface (R4).
                return true;
        }
        ctx.Print("Usage: maplist <action> [<map>] — actions: add, cleanup, remove, shuffle");
        return true;
    }

    private readonly Random _maplistRng = new();

    /// <summary>QC GenericCommand_nextframe: run a command on the next server tick (defer with delay 0).</summary>
    private bool CmdNextFrame(CommandContext ctx)
    {
        if (ctx.ArgCount < 2) { ctx.Print("Usage: nextframe <command>"); return true; }
        Deferred.Defer(0f, ctx.ArgTail(1), _world.Time);
        return true;
    }

    // =============================================================================================
    // server client commands (QC server/command/cmd.qc ClientCommand_*)
    // =============================================================================================

    /// <summary>
    /// QC <c>ClientCommand_voice</c>: play a voice taunt. Validates the voice type against the known set, then
    /// (unless dead/spectating, which silently no-op) emits the taunt sound from the caller. The taunt sample
    /// registry (<c>allvoicesamples</c>/<c>GetVoiceMessage</c>) isn't ported, so this validates against a
    /// built-in name list and emits a by-name sound on the Voice channel (deviation R7 — not full asset parity).
    /// </summary>
    private bool CmdVoice(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("voice is a client command"); return true; }
        if (ctx.ArgCount < 2)
        {
            ctx.Print($"Usage:^3 cmd voice <voicetype> [<message>]");
            ctx.Print($"  one of: {string.Join(" ", VoiceTypes)}");
            return true;
        }
        string voiceType = ctx.Arg(1).ToLowerInvariant();
        if (System.Array.IndexOf(VoiceTypes, voiceType) < 0)
        {
            // QC sprints the invalid warning even to dead/spec callers (so they can learn the names in peace).
            ctx.Print($"Invalid voice. Use one of: {string.Join(" ", VoiceTypes)}");
            return true;
        }
        Player p = ctx.Caller;
        if (p.IsDead) return true;                                   // QC: dead can't taunt (silent)
        if (p.IsObserver || p.FragsStatus == Player.FragsSpectator) return true; // no body to play from (silent)

        // QC VoiceMessage(caller, e, msg): play the taunt. The port emits a by-name sound from the player on the
        // Voice channel (the HUD/announcer text + per-taunt sample selection are a follow-up).
        if (Api.Services is not null)
            Api.Sound.Play(p, XonoticGodot.Common.Services.SoundChannel.Voice, $"sound/player/voice/{voiceType}", 1f, 1f);
        return true;
    }

    /// <summary>The voice-taunt types (QC the registered voice messages / <c>allvoicesamples</c> names).</summary>
    private static readonly string[] VoiceTypes =
    {
        "death", "drown", "fall", "gasp", "jump", "pain25", "pain50", "pain75", "pain100",
        "attack", "attackinfo", "needhelp", "seenflag", "taunt", "teamshoot",
        "coverme", "defend", "freelance", "incoming", "meet", "offense", "onmyway", "roger", "yes", "no",
    };

    /// <summary>
    /// QC <c>ClientCommand_suggestmap</c> → <c>MapVote_Suggest</c> (mapvoting.qc:130-166): record a player's map
    /// suggestion for the end-of-match vote, returning the QC status string. The gate cascade ported here (in QC
    /// order): empty arg (the command's usage, handled below), the <c>g_maplist_votable_suggestions</c> disable
    /// switch (ships 2; 0 == off → "Suggestions are not accepted on this server."), the map-availability check
    /// (QC <c>GameTypeVote_MapInfo_FixName</c> → null = not available → "...is not available on this server."),
    /// the already-suggested dedup, then accept.
    ///
    /// <para>Deferred (a separate seam, per T56): QC's <c>mapvote_initialized</c> "voting already in progress"
    /// gate, the <c>Map_IsRecent</c> recent-map gate, the <c>MapInfo_CheckMap</c> gametype-support gate, and the
    /// downstream ballot fill (<c>mapvote_maps_suggestions[]</c>). The port keeps its own deduped suggestion list
    /// (<see cref="_mapSuggestions"/>) — the ballot wiring is the <see cref="MapVoting"/> task.</para>
    /// </summary>
    private bool CmdSuggestMap(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("suggestmap is a client command"); return true; }
        if (ctx.ArgCount < 2 || ctx.Arg(1) == "")
        {
            // QC ClientCommand_suggestmap: empty arg → the usage block (MapVote_Suggest's "" → usage path).
            ctx.Print("Usage:^3 cmd suggestmap <map>");
            return true;
        }
        string map = ctx.Arg(1);
        // QC mapvoting.qc:134 — the disable switch (0 == suggestions off; ships 2).
        if (Cvars.Float("g_maplist_votable_suggestions") == 0f)
        {
            ctx.Print("Suggestions are not accepted on this server.");
            return true;
        }
        // QC mapvoting.qc:138-140 — GameTypeVote_MapInfo_FixName(m) returns null when the map isn't on the
        // server. The port mirrors that with the same map-existence check the console maplist side uses
        // (Rotation.MapExists / MapInfo_CheckMap), which a host wires to the asset catalog.
        if (!_world.Rotation.MapExists(map))
        {
            ctx.Print("The map you suggested is not available on this server.");
            return true;
        }
        // QC mapvoting.qc:147-149 — already-suggested dedup.
        if (_mapSuggestions.Contains(map, StringComparer.OrdinalIgnoreCase))
        {
            ctx.Print("This map was already suggested.");
            return true;
        }
        _mapSuggestions.Add(map);
        // QC mapvoting.qc:165 — strcat("Suggestion of ", m, " accepted.").
        ctx.Print($"Suggestion of {map} accepted.");
        return true;
    }

    /// <summary>The maps players have suggested for the next end-of-match ballot (QC mapvote_suggestions).</summary>
    public IReadOnlyList<string> MapSuggestions => _mapSuggestions;

    /// <summary>Clear the suggestion list (the host calls this when a new ballot is built).</summary>
    public void ClearMapSuggestions() => _mapSuggestions.Clear();

    /// <summary>QC <c>ClientCommand_autoswitch</c>: set the per-client auto-switch flag and report it.</summary>
    private bool CmdAutoswitch(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("autoswitch is a client command"); return true; }
        if (ctx.ArgCount < 2)
        {
            ctx.Print("Usage:^3 cmd autoswitch <selection>");
            return true;
        }
        bool on = InterpretBoolean(ctx.Arg(1));
        _autoswitch[ctx.Caller] = on;
        ctx.Print($"^1autoswitch is currently turned {(on ? "on" : "off")}.");
        return true;
    }

    /// <summary>
    /// QC <c>ClientCommand_physics</c>: client physics-set selection. Gated on <c>g_physics_clientselect</c>,
    /// which SHIPS 0 — so the common path is the "disabled" print. <c>list</c>/<c>help</c> prints the options;
    /// a valid set (or <c>default</c>) stuffs <c>seta cl_physics &lt;set&gt;</c> (recorded here) + a success line.
    /// </summary>
    private bool CmdPhysics(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("physics is a client command"); return true; }
        string command = ctx.Arg(1).ToLowerInvariant();

        if (!Cvars.Bool("g_physics_clientselect"))
        {
            ctx.Print("Client physics selection is currently disabled.");
            return true;
        }
        if (command == "list" || command == "help")
        {
            ctx.Print("Available physics sets: ");
            ctx.Print($"{Cvars.String("g_physics_clientselect_options")} default");
            return true;
        }
        string options = Cvars.String("g_physics_clientselect_options");
        bool valid = command == "default"
            || System.Array.IndexOf(options.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries), command) >= 0;
        if (valid)
        {
            // QC stuffcmd(caller, "seta cl_physics <set>") — recorded server-side in the per-client cvar table
            // (the same slot a replicated `sentcvar cl_physics` writes), which the snapshot's per-peer preset
            // resolution reads each tick (T54).
            SetClientCvar(ctx.Caller, "cl_physics", command);
            ctx.Print($"^2Physics set successfully changed to ^3{command}");
            return true;
        }
        // QC default branch: report the current set, then fall through to the usage block (QC's default: → usage).
        ctx.Print($"Current physics set: ^3{GetClientCvar(ctx.Caller, "cl_physics", "")}");
        ctx.Print("Usage:^3 cmd physics <physics>");
        ctx.Print("  See 'cmd physics list' for available physics sets.");
        ctx.Print("  Argument 'default' resets to standard physics.");
        return true;
    }

    /// <summary>
    /// QC <c>ClientCommand_sentcvar</c> (server/command/cmd.qc:804-836) + the SVQC REPLICATE receive leg
    /// (lib/replicate.qh:79-100): a client pushing one of its replicated <c>cl_*</c> cvars to the server's
    /// per-client store. Caller-gated (never the server console) and allowlisted — the value is an
    /// attacker-controlled wire string, so it must NEVER reach the world cvar store (the T47
    /// privilege-separation class). The QC fixup funcs run on the RECEIVED value (replicate.qh:63-73):
    /// cl_weaponpriority → number + force-complete; cl_weaponpriority0..9 → fix without completing. The
    /// deprecated server→client GetCvars f==0 "request" direction (getreplies.qc:392 warns it's deprecated)
    /// is intentionally not ported — clients push via the sendcvar watcher instead.
    /// </summary>
    private bool CmdSentCvar(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("sentcvar is a client command"); return true; }
        if (ctx.ArgCount < 3)
        {
            // QC cmd.qc:827-832 — the default/usage block ("Incorrect parameters" then usage).
            ctx.Print($"Incorrect parameters for ^2{ctx.Arg(0)}^7");
            ctx.Print("Usage:^3 cmd sentcvar <cvar> <arguments>");
            return true;
        }

        string name = ctx.Arg(1);
        if (!SentCvarAllowlist.Contains(name))
            return true; // unknown REPLICATE field → stored nowhere (QC writes no field), silently consumed

        string value = ctx.Arg(2);
        if (value.Length > 1024)
            value = value[..1024]; // clamp the wire string (defensive; QC strings are engine-bounded)

        // QC REPLICATE fixups, applied to the received value before it lands in the per-client field.
        if (name == "cl_weaponpriority")
        {
            // W_FixWeaponOrder_ForceComplete_AndBuildImpulseList (weapons/all.qc:799-818 registration): the
            // client may send NAME or NUMBER form; number it, then force-complete. Stored in NUMBER form —
            // exactly what Inventory.GetCycleWeapon consumes (the impulse-list half is the client's own concern).
            value = Common.Gameplay.WeaponOrder.FixWeaponOrderForceComplete(
                Common.Gameplay.WeaponOrder.NumberWeaponOrder(value),
                _world.Services.CvarsImpl.GetDefault("cl_weaponpriority"));
        }
        else if (name.StartsWith("cl_weaponpriority", StringComparison.Ordinal) && name.Length == "cl_weaponpriority".Length + 1)
        {
            // cl_weaponpriorityN — W_FixWeaponOrder_AllowIncomplete (fix, do NOT complete).
            value = Common.Gameplay.WeaponOrder.FixWeaponOrder(
                Common.Gameplay.WeaponOrder.NumberWeaponOrder(value), complete: false);
        }

        SetClientCvar(ctx.Caller, name, value);

        // Bridges into the pre-existing T56 per-client state, so both write paths share one source of truth.
        if (name == "cl_autoswitch")
            _autoswitch[ctx.Caller] = InterpretBoolean(value);

        // QC prints nothing on the happy path.
        return true;
    }

    /// <summary>
    /// QC <c>ClientCommand_clientversion</c> (internal — the client sends it on connect): record the reported
    /// game version. The port's handshake already enforces build parity (NetProtocol.BuildParity), so this is
    /// largely vestigial — it records the value and never crashes (the team/observe seat logic is the handshake's).
    /// </summary>
    private bool CmdClientVersion(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("clientversion is a client command"); return true; }
        if (ctx.ArgCount < 2) return true; // QC: empty arg → no-op (falls to usage, but it's internal)
        // QC: ($gameversion → 1, else stof). The literal "$gameversion" is the unexpanded macro from the client.
        float version = ctx.Arg(1) == "$gameversion" ? 1f : ctx.ArgFloat(1);
        _clientVersion[ctx.Caller] = version;
        return true;
    }

    /// <summary>The version a client reported via <c>clientversion</c> (0 if none); the handshake supersedes it.</summary>
    public float GetClientVersion(Player p) => _clientVersion.TryGetValue(p, out float v) ? v : 0f;

    // =============================================================================================
    // common reply commands (QC server/command/common.qc — print the cached reply strings)
    // =============================================================================================

    private bool CmdRecords(CommandContext ctx)
    {
        // QC precomputes the reply caches at world init; the port rebuilds lazily on read so they reflect the
        // live g_maplist/monster registry without depending on the Boot seam (a cheap superset of QC).
        Replies.Recompute();
        // QC CommonCommand_records: a page arg prints records_reply[num-1] (1..10); else all non-empty pages.
        int num = (int)ctx.ArgFloat(1);
        if (num > 0 && num <= 10 && Replies.RecordsReply[num - 1] != "")
        {
            ctx.Print(Replies.RecordsReply[num - 1]);
            return true;
        }
        bool any = false;
        for (int i = 0; i < 10; i++)
            if (Replies.RecordsReply[i] != "") { ctx.Print(Replies.RecordsReply[i]); any = true; }
        if (!any) ctx.Print("No records available"); // honest empty (no race-records store at this seam — R8)
        return true;
    }

    private bool CmdRankings(CommandContext ctx) { Replies.Recompute(); ctx.Print(Replies.RankingsReply); return true; }
    private bool CmdLsmaps(CommandContext ctx) { Replies.Recompute(); ctx.Print(Replies.LsmapsReply); return true; }
    private bool CmdPrintMapList(CommandContext ctx) { Replies.Recompute(); ctx.Print(Replies.MaplistReply); return true; }
    private bool CmdLadder(CommandContext ctx) { Replies.Recompute(); ctx.Print(Replies.LadderReply); return true; }

    // =============================================================================================
    // monster editor (QC server/command/common.qc CommonCommand_editmob)
    // =============================================================================================

    /// <summary>The spawnmob/killmob cfg aliases: rewrite the argv so editmob sees the subcommand at argv(1).</summary>
    private bool CmdEditMobAlias(CommandContext ctx, string sub)
    {
        // Build an argv that looks like "editmob <sub> <rest...>" so CmdEditMob's argv(1)/argv(2)/argv(3) line up.
        var argv = new List<string> { "editmob", sub };
        for (int i = 1; i < ctx.ArgCount; i++) argv.Add(ctx.Arg(i));
        return CmdEditMob(new CommandContext(argv.ToArray(), ctx.IsServerConsole, ctx.Caller), ctx);
    }

    private bool CmdEditMob(CommandContext ctx) => CmdEditMob(ctx, ctx);

    /// <summary>
    /// QC <c>CommonCommand_editmob</c> (server/command/common.qc): the monster editor. <paramref name="outCtx"/>
    /// is where output goes (so the spawnmob/killmob aliases print to the caller's real context). Subcommands:
    /// <c>spawn</c> (trace-spawn via <see cref="MonsterAI.SpawnMonster"/>; <c>spawn list</c> → the monster reply),
    /// <c>kill</c> (Damage the looked-at monster), <c>skin</c>/<c>movetarget</c>/<c>name</c> (edit the looked-at
    /// monster), <c>butcher</c> (server-only: remove ALL monsters + zero counts). Gated like QC (campaign off,
    /// g_monsters, g_monsters_edit, ownership, alive/not-spectating, max counts).
    /// </summary>
    private bool CmdEditMob(CommandContext ctx, CommandContext outCtx)
    {
        // QC: disabled in singleplayer; no g_monsters check here (it may be toggled mid-match).
        if (Cvars.Bool("g_campaign")) { outCtx.Print("Monster editing is disabled in singleplayer"); return true; }
        Replies.Recompute(); // so `editmob spawn list` reflects the live monster registry (QC the cached reply)

        Player? caller = ctx.Caller;
        string verb = ctx.Arg(1);
        string argument = ctx.Arg(2);

        // QC: trace from the caller's eyes to find the monster being looked at (WarpZone_TraceLine + v_forward*100).
        Entity? mon = caller is not null ? TraceLookedAtMonster(caller, 100f) : null;
        bool isVisible = mon is not null;

        switch (verb)
        {
            case "name":
            {
                if (caller is null) { outCtx.Print("Only players can edit monsters"); return true; }
                if (argument == "") break; // escape to usage
                if (!Cvars.Bool("g_monsters_edit")) { outCtx.Print("Monster editing is disabled"); return true; }
                if (!OwnsOrAdmin(mon, caller)) { outCtx.Print("This monster does not belong to you"); return true; }
                if (!isVisible) { outCtx.Print("You must look at your monster to edit it"); return true; }
                string oldName = mon!.NetName;
                mon.NetName = argument;
                outCtx.Print($"Your pet '{oldName}' is now known as '{mon.NetName}'");
                return true;
            }
            case "spawn":
            {
                if (caller is null) { outCtx.Print("Only players can spawn monsters"); return true; }
                if (argument == "") break; // escape to usage
                string argLower = argument.ToLowerInvariant();
                int moveflag = ctx.Arg(3) != "" ? (int)ctx.ArgFloat(3) : 1; // QC: follow owner if not defined

                if (argLower == "list") { outCtx.Print(Replies.MonsterlistReply); return true; }

                int monCount = CountMonstersOwnedBy(caller);

                if (!MonsterAI.MasterSwitchEnabled("g_monsters")) { outCtx.Print("Monsters are disabled"); return true; }
                if (Cvars.FloatOr("g_monsters_max", 0f) <= 0f || Cvars.FloatOr("g_monsters_max_perplayer", 0f) <= 0f)
                { outCtx.Print("Monster spawning is disabled"); return true; }
                if (caller.FragsStatus == Player.FragsSpectator || caller.IsObserver) { outCtx.Print("You must be playing to spawn a monster"); return true; }
                // QC common.qc:369 MUTATOR_CALLHOOK(AllowMobSpawning) — no port analog (no AllowMobSpawning hook); skipped.
                // QC common.qc:370: can't spawn while seated in a vehicle (gate goes after IS_PLAYER, before the dead check).
                if (caller.Vehicle is not null) { outCtx.Print("You can't spawn monsters while driving a vehicle"); return true; }
                if (caller.IsDead) { outCtx.Print("You can't spawn monsters while dead"); return true; }
                if (monCount >= (int)Cvars.Float("g_monsters_max")) { outCtx.Print("The maximum monster count has been reached"); return true; }
                if (monCount >= (int)Cvars.Float("g_monsters_max_perplayer")) { outCtx.Print("You can't spawn any more monsters"); return true; }

                bool found = false;
                foreach (Monster m in Monsters.All)
                    if (m.NetName == argLower) { found = true; break; }
                if (!found && argLower != "random" && argLower != "anyrandom") { outCtx.Print("Invalid monster"); return true; }

                if (Api.Services is null) { outCtx.Print("Cannot spawn monsters right now"); return true; }
                // QC: WarpZone_TraceBox(view, mins, maxs, view + v_forward*150, true, caller) → spawnmonster(...).
                Vector3 origin = TraceSpawnPoint(caller, 150f);
                Entity? spawned = MonsterAI.SpawnMonster(Api.Entities.Spawn(), argLower, null, caller, caller,
                    origin, respawn: false, removeIfInvalid: false, moveFlags: moveflag);
                outCtx.Print(spawned is not null ? $"Spawned {spawned.NetName}" : "Failed to spawn monster");
                return true;
            }
            case "kill":
            {
                if (caller is null) { outCtx.Print("Only players can kill monsters"); return true; }
                if (!OwnsOrAdmin(mon, caller)) { outCtx.Print("This monster does not belong to you"); return true; }
                if (!isVisible) { outCtx.Print("You must look at your monster to edit it"); return true; }
                // QC: Damage(mon, NULL, NULL, health + max_health + 200, DEATH_KILL, ...).
                float lethal = mon!.Health + mon.MaxHealth + 200f;
                XonoticGodot.Common.Gameplay.Damage.Combat.Damage(mon, null, null, lethal,
                    XonoticGodot.Common.Gameplay.Damage.DeathTypes.Kill, mon.Origin, System.Numerics.Vector3.Zero);
                outCtx.Print($"Your pet '{mon.NetName}' has been brutally mutilated");
                return true;
            }
            case "skin":
            {
                if (caller is null) { outCtx.Print("Only players can edit monsters"); return true; }
                if (argument == "") break;
                if (!Cvars.Bool("g_monsters_edit")) { outCtx.Print("Monster editing is disabled"); return true; }
                if (!isVisible) { outCtx.Print("You must look at your monster to edit it"); return true; }
                if (!OwnsOrAdmin(mon, caller)) { outCtx.Print("This monster does not belong to you"); return true; }
                mon!.Skin = ctx.ArgFloat(2);
                outCtx.Print($"Monster skin successfully changed to {Ftos(mon.Skin)}");
                return true;
            }
            case "movetarget":
            {
                if (caller is null) { outCtx.Print("Only players can edit monsters"); return true; }
                if (argument == "") break;
                if (!Cvars.Bool("g_monsters_edit")) { outCtx.Print("Monster editing is disabled"); return true; }
                if (!isVisible) { outCtx.Print("You must look at your monster to edit it"); return true; }
                if (!OwnsOrAdmin(mon, caller)) { outCtx.Print("This monster does not belong to you"); return true; }
                if (MonsterAI.StateOf(mon!) is { } mst) mst.MoveFlags = (int)ctx.ArgFloat(2);
                outCtx.Print($"Monster move target successfully changed to {Ftos(ctx.ArgFloat(2))}");
                return true;
            }
            case "butcher":
            {
                // QC: SERVER-ONLY (caller must be null / the console).
                if (caller is not null) { outCtx.Print("This command is not available to players"); return true; }
                int removed = RemoveAllMonsters();
                outCtx.Print(removed > 0
                    ? $"Killed {removed} monster{(removed == 1 ? "" : "s")}"
                    : "No monsters to kill");
                return true;
            }
        }

        // QC usage block.
        outCtx.Print("Usage:^3 editmob <command> [<arguments>]");
        outCtx.Print("  Where <command> can be butcher spawn skin movetarget kill name");
        outCtx.Print("  spawn, skin, movetarget and name require <arguments>");
        outCtx.Print("  spawn also takes arguments list and random");
        outCtx.Print("  Monster will follow owner if third argument of spawn command is not defined");
        return true;
    }

    /// <summary>QC <c>mon.realowner == caller || autocvar_g_monsters_edit &gt;= 2</c>: the editmob ownership gate.</summary>
    private static bool OwnsOrAdmin(Entity? mon, Player caller)
        => mon is not null && (ReferenceEquals(mon.Owner, caller) || Cvars.Float("g_monsters_edit") >= 2f);

    /// <summary>Count the live monsters owned by <paramref name="caller"/> (QC IL_EACH(g_monsters, realowner == caller)).</summary>
    private static int CountMonstersOwnedBy(Player caller)
    {
        if (Api.Services is null) return 0;
        int n = 0;
        foreach (Entity e in Api.Entities.FindByClass("monster"))
            if (MonsterAI.StateOf(e) is not null && ReferenceEquals(e.Owner, caller))
                n++;
        return n;
    }

    /// <summary>QC editmob butcher: Monster_Remove every monster + zero the global counts. Returns the count removed.</summary>
    private int RemoveAllMonsters()
    {
        if (Api.Services is null) return 0;
        var victims = new List<Entity>();
        foreach (Entity e in Api.Entities.FindByClass("monster"))
            if (MonsterAI.StateOf(e) is not null)
                victims.Add(e);
        foreach (Entity e in victims)
        {
            MonsterAI.Forget(e);       // drop the MonsterState (QC the edict deletion bookkeeping)
            Api.Entities.Remove(e);
        }
        // QC: monsters_total = monsters_killed = totalspawned = 0 — reset the spawn budget counters.
        MonsterAI.ResetCounters();
        return victims.Count;
    }

    /// <summary>
    /// QC editmob's look-at: trace from the caller's eyes forward and return the monster hit, or null. The port
    /// uses <c>Api.Trace</c> with a forward vector from the player's view angles (the eyes = origin + view_ofs).
    /// </summary>
    private static Entity? TraceLookedAtMonster(Player caller, float dist)
    {
        if (Api.Services is null) return null;
        Vector3 eyes = caller.Origin + caller.ViewOfs;
        // QC makevectors(caller.v_angle): aim along the VIEW angles (fall back to body angles when unset).
        Vector3 aim = caller.ViewAngles != Vector3.Zero ? caller.ViewAngles : caller.Angles;
        XonoticGodot.Common.Math.QMath.AngleVectors(aim, out Vector3 forward, out _, out _);
        Vector3 end = eyes + forward * dist;
        XonoticGodot.Common.Services.TraceResult tr = Api.Trace.Trace(
            eyes, System.Numerics.Vector3.Zero, System.Numerics.Vector3.Zero, end,
            XonoticGodot.Common.Framework.MoveFilter.Normal, caller);
        if (tr.Ent is { } hit && MonsterAI.StateOf(hit) is not null)
            return hit;
        return null;
    }

    /// <summary>QC editmob spawn's trace box: the spawn endpos in front of the caller (origin + v_forward*dist).</summary>
    private static Vector3 TraceSpawnPoint(Player caller, float dist)
    {
        Vector3 eyes = caller.Origin + caller.ViewOfs;
        Vector3 aim = caller.ViewAngles != Vector3.Zero ? caller.ViewAngles : caller.Angles;
        XonoticGodot.Common.Math.QMath.AngleVectors(aim, out Vector3 forward, out _, out _);
        Vector3 end = eyes + forward * dist;
        if (Api.Services is null) return end;
        XonoticGodot.Common.Services.TraceResult tr = Api.Trace.Trace(
            eyes, caller.Mins, caller.Maxs, end, XonoticGodot.Common.Framework.MoveFilter.Normal, caller);
        return tr.EndPos;
    }

    /// <summary>QC <c>ftos</c>: integer floats print with no decimal point.</summary>
    private static string Ftos(float v)
        => v == System.MathF.Floor(v)
            ? ((long)v).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>QC <c>InterpretBoolean</c> (lib/bool.qh): yes/true/on → true; no/false/off → false; else stof != 0.</summary>
    private static bool InterpretBoolean(string input) => input.ToLowerInvariant() switch
    {
        "yes" or "true" or "on" => true,
        "no" or "false" or "off" => false,
        _ => float.TryParse(input, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float f) && f != 0f,
    };

    private static int TeamFromName(string name) => name.ToLowerInvariant() switch
    {
        "red" or "1" => Teams.Red,
        "blue" or "2" => Teams.Blue,
        "yellow" or "3" => Teams.Yellow,
        "pink" or "4" => Teams.Pink,
        _ => Teams.None,
    };

    private Player? FindPlayerByName(string name)
    {
        // exact match first, then case-insensitive prefix (QC GetFilteredNumber-ish leniency).
        foreach (Player p in _world.Clients.Players)
            if (string.Equals(p.NetName, name, StringComparison.OrdinalIgnoreCase))
                return p;
        foreach (Player p in _world.Clients.Players)
            if (p.NetName.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                return p;
        return null;
    }
}
