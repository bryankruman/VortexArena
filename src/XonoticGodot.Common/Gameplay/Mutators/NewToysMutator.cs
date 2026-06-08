// Port of common/mutators/mutator/new_toys/sv_new_toys.qc (+ new_toys/new_toys.qc nt_IsNewToy/nt_GetReplacement)

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The New Toys mutator — port of common/mutators/mutator/new_toys/sv_new_toys.qc. Enabled by default in QC
/// (<c>expr_evaluate(cvar_string("g_new_toys"))</c>) but only when neither instagib nor overkill is active, it
/// lets a set of "gimmicky" weapons spawn — Seeker, Mine Layer, HLAC, Rifle, Arc — sometimes replacing a core
/// weapon (Hagar→Seeker, Devastator→Mine Layer, Machinegun→HLAC, Vortex→Rifle). The replacement runs on the
/// DEFAULT start-weapon selection and on map weapon entities.
///
/// Ported: the enable gate (with the instagib/overkill exclusions), the new-toy ↔ core mapping
/// (<see cref="GetFullReplacement"/> / <see cref="GetReplacement"/>), the new-toy weapon set
/// (<see cref="IsNewToy"/>), the MUTATOR_ONADD/REMOVE weapon-unblock (clear <see cref="WeaponFlags.MutatorBlocked"/>
/// on the new-toy weapons so they're givable, restore it when the mutator drops), and the SetStartItems rearrange
/// — for each start weapon with a replacement, swap it for the new-toy variant per <c>g_new_toys_autoreplace</c>
/// (NEVER keeps it, ALWAYS swaps, RANDOM keeps-or-swaps 50/50).
///
/// NOTE (deferred — map item pipeline): QC also hooks <c>SetWeaponreplace</c> and <c>FilterItem</c> to rewrite
/// the weapon a map <c>weapon_*</c> entity spawns as and to swap its pickup sound. The port has no map
/// item-entity spawn / weaponreplace pipeline yet (MapObjectsRegistry registers no <c>weapon_*</c> spawnfuncs),
/// so those two hooks have nothing to fire against — the SetStartItems half (the start loadout) is the live,
/// faithful part; the map-spawn half is flagged in crossTaskNeeds and reactivates for free once an item pipeline
/// lands (the mapping helpers below already encode all of nt_GetReplacement).
/// </summary>
[Mutator]
public sealed class NewToysMutator : MutatorBase
{
    /// <summary>QC NT_AUTOREPLACE_NEVER — keep the core weapon (no replacement).</summary>
    public const int AutoReplaceNever = 0;

    /// <summary>QC NT_AUTOREPLACE_ALWAYS — always replace the core weapon with its new-toy variant.</summary>
    public const int AutoReplaceAlways = 1;

    /// <summary>QC NT_AUTOREPLACE_RANDOM — replace or keep, chosen at random per weapon.</summary>
    public const int AutoReplaceRandom = 2;

    /// <summary>QC autocvar_g_new_toys_autoreplace.</summary>
    public int AutoReplace = AutoReplaceNever;

    public NewToysMutator() => NetName = "new_toys";

    // QC: REGISTER_MUTATOR(nt, expr_evaluate(cvar_string("g_new_toys")) && !instagib && !ok).
    public override bool IsEnabled =>
        Api.Services is not null
        && ExprEvaluate(Api.Cvars.GetString("g_new_toys"))
        && Api.Cvars.GetFloat("g_instagib") == 0f
        && Api.Cvars.GetFloat("g_overkill") == 0f;

    /// <summary>QC nt_IsNewToy(w): the weapons this mutator unlocks (by NetName — the port's m_id stand-in).</summary>
    private static readonly HashSet<string> NewToyNames = new(StringComparer.Ordinal)
    {
        "seeker", "minelayer", "mine_layer", "hlac", "rifle", "arc",
    };

    /// <summary>QC nt_IsNewToy(int w) — true for a new-toy weapon by NetName.</summary>
    public static bool IsNewToy(string netName) => NewToyNames.Contains(netName);

    private HookHandler<MutatorHooks.SetStartItemsArgs>? _onSetStartItems;

    public override void Hook()
    {
        _onSetStartItems ??= OnSetStartItems;
        MutatorHooks.SetStartItems.Add(_onSetStartItems);

        if (Api.Services is not null)
            AutoReplace = (int)Api.Cvars.GetFloat("g_new_toys_autoreplace");

        // MUTATOR_ONADD: mark the new-toy guns ok to spawn / give (clear WEP_FLAG_MUTATORBLOCKED).
        SetNewToyBlocked(false);
    }

    public override void Unhook()
    {
        if (_onSetStartItems is not null) MutatorHooks.SetStartItems.Remove(_onSetStartItems);

        // MUTATOR_ONROLLBACK_OR_REMOVE: re-block the new-toy guns. (QC also returns -1 from ONREMOVE because it
        // "cannot be removed at runtime"; the port has no live-remove guard, so just restore the flags.)
        SetNewToyBlocked(true);
    }

    /// <summary>
    /// QC MUTATOR_ONADD/ONROLLBACK FOREACH(Weapons, nt_IsNewToy(it.m_id), it.spawnflags ^= WEP_FLAG_MUTATORBLOCKED).
    /// Sets or clears <see cref="WeaponFlags.MutatorBlocked"/> on every new-toy weapon so it can / can't be given.
    /// </summary>
    private static void SetNewToyBlocked(bool blocked)
    {
        foreach (Weapon w in Weapons.All)
        {
            if (!IsNewToy(w.NetName)) continue;
            if (blocked) w.SpawnFlags |= WeaponFlags.MutatorBlocked;
            else w.SpawnFlags &= ~WeaponFlags.MutatorBlocked;
        }
    }

    /// <summary>QC nt_GetFullReplacement(w): the core→new-toy mapping (string_null = no replacement).</summary>
    public static string? GetFullReplacement(string w) => w switch
    {
        "hagar" => "seeker",
        "devastator" => "minelayer",
        "machinegun" => "hlac",
        "vortex" => "rifle",
        _ => null,
    };

    /// <summary>
    /// QC nt_GetReplacement(w, m): the replacement token list for a weapon under autoreplace mode <paramref name="m"/>.
    /// NEVER → the weapon itself; no mapping → itself; ALWAYS → the new-toy variant; RANDOM → "w newtoy" (pick one).
    /// </summary>
    public static string GetReplacement(string w, int m)
    {
        if (m == AutoReplaceNever) return w;
        string? s = GetFullReplacement(w);
        if (s is null) return w;
        if (m == AutoReplaceRandom) return w + " " + s;   // QC strcat(w, " ", s)
        return s;
    }

    // MUTATOR_HOOKFUNCTION(nt, SetStartItems) — rearrange the default start weapons through GetReplacement.
    private bool OnSetStartItems(ref MutatorHooks.SetStartItemsArgs args)
    {
        StartLoadout l = args.Loadout;

        // QC walks every weapon, tokenizes its replacement, and ORs the resolved weapons into newdefault, applying
        // them under start_weapons_defaultmask. The port's loadout is a NetName set, so for each currently-default
        // weapon with a replacement, swap it for the resolved token (one chosen at random for the RANDOM list).
        var add = new List<string>();
        var remove = new List<string>();
        foreach (string have in l.Weapons)
        {
            string repl = GetReplacement(have, AutoReplace);
            if (repl == have) continue; // no change (NEVER, or no mapping)

            // RANDOM yields "core newtoy" — pick one token; ALWAYS yields the single new-toy token.
            string[] toks = repl.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string chosen = toks.Length <= 1 ? repl : toks[Prandom.RangeInt(0, toks.Length)];
            if (chosen == have) continue;

            remove.Add(have);
            add.Add(chosen);
        }
        foreach (string r in remove) l.Weapons.Remove(r);
        foreach (string a in add) l.Weapons.Add(a);
        return false;
    }

    /// <summary>QC <c>expr_evaluate(s)</c> for a cvar string: false for "" / "0" / "false", true otherwise.</summary>
    private static bool ExprEvaluate(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        s = s.Trim();
        if (s == "0" || string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}
