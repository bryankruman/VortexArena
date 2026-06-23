// Port of common/mutators/mutator/superspec/sv_superspec.qc

using System.Collections.Generic;
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
///
/// CROSS-FILE SEAMS THAT DO NOT EXIST YET (left as todos, NOT ported here):
///   - ItemTouch shared hook (no <c>MutatorHooks.ItemTouch</c> chain): the pickup-message + auto-spectate-on-
///     powerup/mega/flag behavior (superspec ItemTouch) has nowhere to attach. The message helper + filter +
///     SpectateTransfer below are ready for it the moment the hook lands.
///   - SV_ParseClientCommand mutator hook chain: the superspec/autospec/itemfilter/follow* spectator commands.
///   - A spectator console-print (sprint) / centerprint server API for <c>superspec_msg</c> output.
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
    // SSF_SILENT, or (spamlevel > 1 && !SSF_VERBOSE). The port lacks a spectator sprint/centerprint server API,
    // so this resolves the gating (so the mix of flags is faithful) and is ready to drive that API when it lands.
    public static void Msg(Player to, string centerTitle, string conTitle, string msg, float spamLevel)
    {
        int flags = Opt(to).SuperspecFlags;

        // sprint(_to, strcat(conTitle, msg)) — always. (No port sprint seam yet; see todos.)
        _ = conTitle;

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

        foreach (KeyValuePair<Player, Options> kv in _options)
        {
            Player it = kv.Key;
            if (!IsSpectator(it))
                continue;
            if ((kv.Value.AutospecFlags & ASF_FOLLOWKILLER) != 0 && ReferenceEquals(it.Spectatee, victim))
            {
                if ((kv.Value.AutospecFlags & ASF_SHOWWHAT) != 0)
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

    /// <summary>QC <c>expr_evaluate(s)</c> for a cvar string: false for "" / "0" / "false", true otherwise.</summary>
    private static bool ExprEvaluate(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        s = s.Trim();
        if (s == "0" || string.Equals(s, "false", System.StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}
