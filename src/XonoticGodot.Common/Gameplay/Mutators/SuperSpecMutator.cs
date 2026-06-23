// Port of common/mutators/mutator/superspec/sv_superspec.qc

using System;
using System.Collections.Generic;
using System.Text;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Super Spectate mutator — port of common/mutators/mutator/superspec/sv_superspec.qc. Gives spectators
/// power-spectating conveniences: auto-switch the follow-cam onto whoever grabs a Strength/Shield powerup,
/// a Mega Health/Armor, or a CTF flag; auto-switch onto a victim's killer (followkiller); pickup-announcement
/// messages with a per-client item filter; and the <c>superspec</c>/<c>autospec</c>/<c>superspec_itemfilter</c>/
/// <c>followpowerup</c>/<c>followstrength</c>/<c>followshield</c> spectator console commands. Enabled by the
/// string cvar <c>g_superspectate</c> (expr_evaluate); Base default is "" (off).
///
/// WHAT IS PORTED IN THIS FILE (faithful to Base):
///   - REGISTER_MUTATOR predicate (<see cref="IsEnabled"/> = expr_evaluate(g_superspectate)) + registration.
///   - The ASF_* (autospec) and SSF_* (superspec) flag bit constants and ASF_ALL, bit-identical to Base 8-25.
///   - Per-client option state (autospec_flags / superspec_flags / superspec_itemfilter), held in a side-table
///     keyed by <see cref="Player"/> (the port's <see cref="Player"/> has no superspec fields and this file may
///     not edit Player.cs — see todos to promote these to real .fields for networking/persistence).
///   - The item-message classname filter (<see cref="FilterItem"/>, superspec_filteritem: empty = match all).
///   - The Spectate transfer helper (<see cref="SpectateTransfer"/>) built on the port's <see cref="Player.Spectatee"/>
///     / <see cref="Player.SpectateeStatus"/> follow-cam state (the port has no engine Spectate() primitive yet).
///   - The PlayerDies followkiller hook (ASF_FOLLOWKILLER): a spectator following the victim auto-switches to
///     the killer. Wired via the live <see cref="MutatorHooks.PlayerDies"/> chain.
///   - The SV_ParseClientCommand spectator verbs (<c>superspec</c>/<c>autospec</c>/<c>superspec_itemfilter</c>/
///     <c>followpowerup</c>/<c>followstrength</c>/<c>followshield</c>), via <see cref="HandleCommand"/> — routed
///     on the live path by the server command bus (Commands.CmdSuperspec, the same pattern as the sandbox verb).
///     This is what WRITES the per-client option store, so the followkiller hook + FilterItem + Msg are now
///     reachable in a real match. Console output goes through the host-wired <see cref="PrintTo"/> sprint seam.
///
/// CROSS-FILE SEAMS THAT DO NOT EXIST YET (left as todos, NOT ported here):
///   - ItemTouch shared hook (no <c>MutatorHooks.ItemTouch</c> chain): the pickup-message + auto-spectate-on-
///     powerup/mega/flag behavior (superspec ItemTouch) has nowhere to attach. The message helper + filter +
///     SpectateTransfer below are ready for it the moment the hook lands.
///   - A spectator centerprint-text server API for <c>superspec_msg</c>'s centered form (the console <c>sprint</c>
///     form is now wired through <see cref="PrintTo"/>; the centerprint form is still gated-but-stubbed).
///   - ClientConnect/ClientDisconnect hooks + a crypto_idfp-keyed options file (superspec-*.options, magic
///     "SUPERSPEC_OPTIONSFILE_V1") for persistence, plus the delayed superspec_hello firing the (currently dead)
///     INFO_SUPERSPEC_MISSING_UID notification.
///   - BuildMutatorsString / BuildMutatorsPrettyString hooks for the ":SS" / ", Super Spectators" tags.
/// </summary>
[Mutator]
public sealed class SuperSpecMutator : MutatorBase
{
    // QC autospec_flags bits (sv_superspec.qc:8-17).
    public const int ASF_STRENGTH = 1 << 0;
    public const int ASF_SHIELD = 1 << 1;
    public const int ASF_MEGA_AR = 1 << 2;
    public const int ASF_MEGA_HP = 1 << 3;
    public const int ASF_FLAG_GRAB = 1 << 4;
    public const int ASF_OBSERVER_ONLY = 1 << 5;
    public const int ASF_SHOWWHAT = 1 << 6;
    public const int ASF_SSIM = 1 << 7;
    public const int ASF_FOLLOWKILLER = 1 << 8;
    public const int ASF_ALL = 0xFFFFFF;

    // QC superspec_flags bits (sv_superspec.qc:20-22).
    public const int SSF_SILENT = 1 << 0;
    public const int SSF_VERBOSE = 1 << 1;
    public const int SSF_ITEMMSG = 1 << 2;

    /// <summary>QC <c>_SSMAGIX</c> options-file magic (used by the not-yet-ported persistence path).</summary>
    public const string OptionsFileMagic = "SUPERSPEC_OPTIONSFILE_V1";

    public SuperSpecMutator() => NetName = "superspec";

    // ===== host seams (wired by the server; null in tests / headless) =====

    /// <summary>QC <c>sprint(to, ..)</c> — one console line to a spectator (the host routes it to the client).
    /// Wired by the server (NetGame.WireSuperspec). When null (tests/headless) <see cref="Msg"/> is a no-op,
    /// exactly as before. The centered <c>centerprint</c> form has no seam yet (still gated-but-stubbed).</summary>
    public Action<Player, string>? PrintTo { get; set; }

    /// <summary>QC <c>FOREACH_CLIENT</c> — the live client roster. The follow* commands and the followkiller scan
    /// pull from this; the host wires it (<c>() =&gt; Clients.Players</c>). Falls back to the option-store keys
    /// (tests) so the unit stays exercisable headless.</summary>
    public Func<IReadOnlyList<Player>>? RosterProvider { get; set; }

    private IReadOnlyList<Player> Roster()
    {
        if (RosterProvider is not null) return RosterProvider();
        var list = new List<Player>(_options.Count);
        foreach (Player p in _options.Keys) list.Add(p);
        return list;
    }

    // QC: REGISTER_MUTATOR(superspec, expr_evaluate(autocvar_g_superspectate)). g_superspectate is a STRING.
    public override bool IsEnabled =>
        Api.Services is not null && ExprEvaluate(Api.Cvars.GetString("g_superspectate"));

    // ===== per-client option state (QC .autospec_flags / .superspec_flags / .superspec_itemfilter) =====
    // Held off-Player because this file may not edit Player.cs. SSF_VERBOSE is the QC ClientConnect default
    // (sv_superspec.qc:387); itemfilter defaults to "" (match-all). See todos: promote to real Player fields.
    private sealed class Options
    {
        public int AutospecFlags;
        public int SuperspecFlags = SSF_VERBOSE;
        public string ItemFilter = "";
    }

    private static readonly Dictionary<Player, Options> _options = new();

    private static Options Opt(Player p)
    {
        if (!_options.TryGetValue(p, out Options? o))
        {
            o = new Options();
            _options[p] = o;
        }
        return o;
    }

    private HookHandler<MutatorHooks.PlayerDiesArgs>? _onPlayerDies;

    public override void Hook()
    {
        _onPlayerDies ??= OnPlayerDies;
        MutatorHooks.PlayerDies.Add(_onPlayerDies);
    }

    public override void Unhook()
    {
        if (_onPlayerDies is not null) MutatorHooks.PlayerDies.Remove(_onPlayerDies);
        _options.Clear();
    }

    // QC superspec_Spectate(this, targ) -> Spectate(this, targ). The port has no engine Spectate() target-switch
    // primitive, so this sets the follow-cam state directly (.enemy = targ, spectatee_status = targ's id), which
    // is the faithful observable result. Returns true like Base.
    private static bool SpectateTransfer(Player spectator, Player target)
    {
        spectator.Spectatee = target;
        spectator.SpectateeStatus = target.PlayerId;
        return true;
    }

    // QC superspec_filteritem(_for, _item): empty filter = match all; else tokenized classname allowlist.
    public static bool FilterItem(Player forPlayer, Entity item)
    {
        string filter = Opt(forPlayer).ItemFilter;
        if (filter.Length == 0)
            return true;

        foreach (string token in filter.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries))
        {
            if (token == item.ClassName)
                return true;
        }
        return false;
    }

    // QC superspec_msg(center_title, con_title, to, msg, spamlevel): always console-print; centerprint unless
    // SSF_SILENT, or (spamlevel > 1 && !SSF_VERBOSE). The console (sprint) form is now wired through PrintTo; the
    // centered (centerprint) form has no server seam yet, so its gating is resolved faithfully but the text is
    // still discarded (see todos).
    public void Msg(Player to, string centerTitle, string conTitle, string msg, float spamLevel)
    {
        int flags = Opt(to).SuperspecFlags;

        // sprint(_to, strcat(conTitle, msg)) — always.
        PrintTo?.Invoke(to, conTitle + msg);

        if ((flags & SSF_SILENT) != 0)
            return;

        if (spamLevel > 1f && (flags & SSF_VERBOSE) == 0)
            return;

        // centerprint(_to, strcat(centerTitle, msg)) — gated. (No port centerprint-text seam yet; see todos.)
        _ = centerTitle;
        _ = msg;
    }

    // MUTATOR_HOOKFUNCTION(superspec, PlayerDies) — followkiller (sv_superspec.qc:421-435). Any spectator
    // following the victim auto-switches to the killer when ASF_FOLLOWKILLER is set and the killer is a player.
    private bool OnPlayerDies(ref MutatorHooks.PlayerDiesArgs args)
    {
        if (args.Attacker is not Player attacker || !IsLivePlayer(attacker))
            return false;
        if (args.Target is not Player victim)
            return false;

        foreach (Player it in Roster())
        {
            if (!IsSpectator(it))
                continue;
            Options o = Opt(it);
            if ((o.AutospecFlags & ASF_FOLLOWKILLER) != 0 && ReferenceEquals(it.Spectatee, victim))
            {
                if ((o.AutospecFlags & ASF_SHOWWHAT) != 0)
                    Msg(it, "", "", $"^7Following {attacker.NetName}^7 due to followkiller\n", 2);

                SpectateTransfer(it, attacker);
            }
        }
        return false;
    }

    // QC IS_SPEC(it): an observer that is currently following a live player (.enemy set, spectatee_status > 0).
    private static bool IsSpectator(Player p) => p.IsObserver && p.Spectatee is not null;

    // QC IS_PLAYER(e): a live, joined, non-observer client.
    private static bool IsLivePlayer(Player p) => !p.IsObserver && (p.Flags & EntFlags.Client) != 0;

    /// <summary>QC <c>StatusEffects_active(STATUSEFFECT_Strength, it)</c> — has the Strength powerup active.</summary>
    private static bool HasStrength(Player p)
        => StatusEffectsCatalog.ByName("strength") is { } d && StatusEffectsCatalog.Has(p, d);

    /// <summary>QC <c>StatusEffects_active(STATUSEFFECT_Shield, it)</c> — has the Shield (invincible) powerup active.</summary>
    private static bool HasShield(Player p)
        => StatusEffectsCatalog.ByName("shield") is { } d && StatusEffectsCatalog.Has(p, d);

    // =================================================================================================
    // MUTATOR_HOOKFUNCTION(superspec, SV_ParseClientCommand) — sv_superspec.qc:141-358. The spectator-only
    // verbs superspec_itemfilter / superspec / autospec / followpowerup / followstrength / followshield. The
    // server command bus (Commands.CmdSuperspec) routes a client `cmd <verb> …` here when the mutator is added,
    // mirroring the sandbox g_sandbox routing. argv[0] is the verb; argv[1..] the options. Returns true when the
    // command was consumed (QC `return true`). QC's IS_PLAYER(player) early-return makes these spectator-only.
    // =================================================================================================
    public bool HandleCommand(Player player, string[] argv)
    {
        if (argv is null || argv.Length == 0)
            return false;

        // QC: if(IS_PLAYER(player)) return;  — these are spectator/observer-only commands.
        if (IsLivePlayer(player))
            return false;

        string cmdName = argv[0];
        string Argv(int i) => i >= 0 && i < argv.Length ? argv[i] : "";
        int argc = argv.Length;
        Options opt = Opt(player);

        if (cmdName == "superspec_itemfilter")
        {
            if (Argv(1) == "help")
            {
                Msg(player, "^3superspec_itemfilter help:\n\n\n", "\n^3superspec_itemfilter help:\n",
                    "^7 superspec_itemfilter ^3\"item_classname1 item_classname2\"^7 only show thise items when ^2superspec ^3item_message^7 is on\n"
                    + "^3 clear^7 Remove the filter (show all pickups)\n"
                    + "^3 show ^7 Display current filter\n", 1);
            }
            else if (Argv(1) == "clear")
            {
                opt.ItemFilter = "";
            }
            else if (Argv(1) == "show" || Argv(1) == "")
            {
                if (opt.ItemFilter.Length == 0)
                {
                    Msg(player, "^3superspec_itemfilter^7 is ^1not^7 set", "\n^3superspec_itemfilter^7 is ^1not^7 set\n", "", 1);
                    return true;
                }
                string[] toks = opt.ItemFilter.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                var sb = new StringBuilder();
                for (int i = 0; i < toks.Length; ++i)
                    sb.Append("^3#").Append(i).Append(" ^7").Append(toks[i]).Append('\n');
                sb.Append('\n');
                Msg(player, "^3superspec_itemfilter is:\n\n\n", "\n^3superspec_itemfilter is:\n", sb.ToString(), 1);
            }
            else
            {
                opt.ItemFilter = Argv(1);
            }
            return true;
        }

        if (cmdName == "superspec")
        {
            if (argc > 1)
            {
                if (Argv(1) == "help")
                {
                    Msg(player, "^2Available Super Spectate ^3options:\n\n\n", "\n^2Available Super Spectate ^3options:\n",
                        "use cmd superspec [option] [on|off] to set options\n\n"
                        + "^3 silent ^7(short^5 si^7) supresses ALL messages from superspectate.\n"
                        + "^3 verbose ^7(short^5 ve^7) makes superspectate print some additional information.\n"
                        + "^3 item_message ^7(short^5 im^7) makes superspectate print items that were picked up.\n"
                        + "^7    Use cmd superspec_itemfilter \"item_class1 item_class2\" to set up a filter of what to show with ^3item_message.\n", 1);
                    return true;
                }

                int bits = 0, start = 1;
                if (Argv(1) == "clear")
                {
                    opt.SuperspecFlags = 0;
                    start = 2;
                }

                for (int i = start; i < argc; ++i)
                {
                    string s = Argv(i);
                    if (s == "on" || s == "1")
                    {
                        opt.SuperspecFlags |= bits;
                        bits = 0;
                    }
                    else if (s == "off" || s == "0")
                    {
                        if (start == 1)
                            opt.SuperspecFlags &= ~bits;
                        bits = 0;
                    }
                    else
                    {
                        if (s == "silent" || s == "si") bits |= SSF_SILENT;
                        if (s == "verbose" || s == "ve") bits |= SSF_VERBOSE;
                        if (s == "item_message" || s == "im") bits |= SSF_ITEMMSG;
                    }
                }
            }

            var info = new StringBuilder();
            OptionInfo(info, opt.SuperspecFlags, SSF_SILENT, "Silent", "silent", "si");
            OptionInfo(info, opt.SuperspecFlags, SSF_VERBOSE, "Verbose", "verbose", "ve");
            OptionInfo(info, opt.SuperspecFlags, SSF_ITEMMSG, "Item pickup messages", "item_message", "im");
            Msg(player, "^3Current Super Spectate options are:\n\n\n\n\n", "\n^3Current Super Spectate options are:\n", info.ToString(), 1);
            return true;
        }

        if (cmdName == "autospec")
        {
            if (argc > 1)
            {
                if (Argv(1) == "help")
                {
                    Msg(player, "^2Available Auto Spectate ^3options:\n\n\n", "\n^2Available Auto Spectate ^3options:\n",
                        "use cmd autospec [option] [on|off] to set options\n\n"
                        + "^3 strength ^7(short^5 st^7) for automatic spectate on strength powerup\n"
                        + "^3 shield ^7(short^5 sh^7) for automatic spectate on shield powerup\n"
                        + "^3 mega_health ^7(short^5 mh^7) for automatic spectate on mega health\n"
                        + "^3 mega_armor ^7(short^5 ma^7) for automatic spectate on mega armor\n"
                        + "^3 flag_grab ^7(short^5 fg^7) for automatic spectate on CTF flag grab\n"
                        + "^3 observer_only ^7(short^5 oo^7) for automatic spectate only if in observer mode\n"
                        + "^3 show_what ^7(short^5 sw^7) to display what event triggered autospectate\n"
                        + "^3 item_msg ^7(short^5 im^7) to autospec when item_message in superspectate is triggered\n"
                        + "^3 followkiller ^7(short ^5fk^7) to autospec the killer/off\n"
                        + "^3 all ^7(short ^5aa^7) to turn everything on/off\n", 1);
                    return true;
                }

                int bits = 0, start = 1;
                if (Argv(1) == "clear")
                {
                    opt.AutospecFlags = 0;
                    start = 2;
                }

                for (int i = start; i < argc; ++i)
                {
                    string s = Argv(i);
                    if (s == "on" || s == "1")
                    {
                        opt.AutospecFlags |= bits;
                        bits = 0;
                    }
                    else if (s == "off" || s == "0")
                    {
                        if (start == 1)
                            opt.AutospecFlags &= ~bits;
                        bits = 0;
                    }
                    else
                    {
                        if (s == "strength" || s == "st") bits |= ASF_STRENGTH;
                        if (s == "shield" || s == "sh") bits |= ASF_SHIELD;
                        if (s == "mega_health" || s == "mh") bits |= ASF_MEGA_HP;
                        if (s == "mega_armor" || s == "ma") bits |= ASF_MEGA_AR;
                        if (s == "flag_grab" || s == "fg") bits |= ASF_FLAG_GRAB;
                        if (s == "observer_only" || s == "oo") bits |= ASF_OBSERVER_ONLY;
                        if (s == "show_what" || s == "sw") bits |= ASF_SHOWWHAT;
                        if (s == "item_msg" || s == "im") bits |= ASF_SSIM;
                        if (s == "followkiller" || s == "fk") bits |= ASF_FOLLOWKILLER;
                        if (s == "all" || s == "aa") bits |= ASF_ALL;
                    }
                }
            }

            var info = new StringBuilder();
            OptionInfo(info, opt.AutospecFlags, ASF_STRENGTH, "Strength", "strength", "st");
            OptionInfo(info, opt.AutospecFlags, ASF_SHIELD, "Shield", "shield", "sh");
            OptionInfo(info, opt.AutospecFlags, ASF_MEGA_HP, "Mega Health", "mega_health", "mh");
            OptionInfo(info, opt.AutospecFlags, ASF_MEGA_AR, "Mega Armor", "mega_armor", "ma");
            OptionInfo(info, opt.AutospecFlags, ASF_FLAG_GRAB, "Flag grab", "flag_grab", "fg");
            OptionInfo(info, opt.AutospecFlags, ASF_OBSERVER_ONLY, "Only switch if observer", "observer_only", "oo");
            OptionInfo(info, opt.AutospecFlags, ASF_SHOWWHAT, "Show what item triggered spectate", "show_what", "sw");
            OptionInfo(info, opt.AutospecFlags, ASF_SSIM, "Switch on superspec item message", "item_msg", "im");
            OptionInfo(info, opt.AutospecFlags, ASF_FOLLOWKILLER, "Followkiller", "followkiller", "fk");
            Msg(player, "^3Current auto spectate options are:\n\n\n\n\n", "\n^3Current auto spectate options are:\n", info.ToString(), 1);
            return true;
        }

        if (cmdName == "followpowerup")
        {
            foreach (Player it in Roster())
                if (IsLivePlayer(it) && (HasStrength(it) || HasShield(it)))
                    return SpectateTransfer(player, it);
            Msg(player, "", "", "No active powerup\n", 1);
            return true;
        }

        if (cmdName == "followstrength")
        {
            foreach (Player it in Roster())
                if (IsLivePlayer(it) && HasStrength(it))
                    return SpectateTransfer(player, it);
            Msg(player, "", "", "No active Strength\n", 1);
            return true;
        }

        if (cmdName == "followshield")
        {
            foreach (Player it in Roster())
                if (IsLivePlayer(it) && HasShield(it))
                    return SpectateTransfer(player, it);
            Msg(player, "", "", "No active Shield\n", 1);
            return true;
        }

        return false;
    }

    // QC OPTIONINFO(flag, msg, test, text, long, short): "[ON]/[OFF] text (^3 long | ^3 short )\n".
    private static void OptionInfo(StringBuilder sb, int flag, int test, string text, string longName, string shortName)
        => sb.Append((flag & test) != 0 ? "^2[ON]  ^7" : "^1[OFF] ^7")
             .Append(text).Append(" ^7(^3 ").Append(longName).Append("^7 | ^3").Append(shortName).Append(" ^7)\n");

    /// <summary>QC <c>expr_evaluate(s)</c> for a cvar string: false for "" / "0" / "false", true otherwise.</summary>
    private static bool ExprEvaluate(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        s = s.Trim();
        if (s == "0" || string.Equals(s, "false", System.StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}
