using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Overkill mutator — port of common/mutators/mutator/overkill/sv_overkill.qc. A loadout/arena mode:
/// players spawn with the Overkill weapon set, drop loot on death, and the secondary blaster is special
/// (no damage/force by default). Enabled by the <c>g_overkill</c> cvar.
///
/// Ported: the blaster damage/force nullification (Damage_Calculate, CBC_ORDER_LAST, with the frozen guard),
/// the throw/arena/random-weapon forbids, the start loadout (RPC/HMG conditional on their weaponstart cvars),
/// the per-spawn Overkill loadout applied through <see cref="Inventory"/>, the loot drop on death
/// (<c>ok_DropItem</c>, routed through the real <see cref="StartItem"/> loot pipeline so it is actually
/// collectible), the held-weapon capture + restore (<c>ok_lastwep[]</c> → PlayerWeaponSelect, folded into
/// PlayerSpawn, mapping HMG→Machinegun and RPC→Nex), and the FilterItem economy: normal big health/armor are
/// blocked per the <c>g_overkill_filter_*</c> cvars and Strength/Shield powerups are replaced by the HMG/RPC
/// superweapon pickups.
///
/// NOT yet ported (need seams this file can't reach): the PlayerPreThink countdown-blaster (no per-player
/// button/round-handler/secondary-fire reach from a mutator), MonsterDropItem loot (MonsterDropItem hook chain
/// absent in port), the item-respawn waypoints (Item_RespawnCountdown/Item_ScheduleRespawn hook chains absent),
/// ok_player model precache (no precache seam), and the client cl_overkill cvar_settemp sync — see the registry.
/// </summary>
[Mutator]
public sealed class OverkillMutator : MutatorBase
{
    /// <summary>QC autocvar_g_overkill_blaster_keepforce.</summary>
    public bool BlasterKeepForce;

    /// <summary>QC autocvar_g_overkill_blaster_keepdamage.</summary>
    public bool BlasterKeepDamage;

    /// <summary>QC autocvar_g_overkill_loot_player — loot item classname dropped when a player dies.</summary>
    public string LootPlayer = "armor_small";

    /// <summary>QC autocvar_g_overkill_loot_player_time — seconds the dropped loot lives (0 = no drop).</summary>
    public float LootPlayerTime = 5f;

    /// <summary>QC WEP_OVERKILL_RPC.weaponstart — start with the RPC in the loadout.</summary>
    public bool StartRpc;

    /// <summary>QC WEP_OVERKILL_HMG.weaponstart — start with the HMG in the loadout.</summary>
    public bool StartHmg;

    /// <summary>QC autocvar_g_overkill_powerups_replace — replace strength/shield pickups with HMG/RPC.</summary>
    public bool PowerupsReplace;

    // QC autocvar_g_overkill_filter_* — defaults from sv_overkill.qh (only medium/big armor filtered by default).
    /// <summary>QC autocvar_g_overkill_filter_healthmega (default 0).</summary>
    public bool FilterHealthMega;

    /// <summary>QC autocvar_g_overkill_filter_armormedium (default 1).</summary>
    public bool FilterArmorMedium = true;

    /// <summary>QC autocvar_g_overkill_filter_armorbig (default 1).</summary>
    public bool FilterArmorBig = true;

    /// <summary>QC autocvar_g_overkill_filter_armormega (default 0).</summary>
    public bool FilterArmorMega;

    public OverkillMutator() => NetName = "overkill";

    /// <summary>The base Overkill loadout (QC WEPSET(OVERKILL_MACHINEGUN|NEX|SHOTGUN)).</summary>
    private static readonly string[] BaseLoadout = { "okmachinegun", "oknex", "okshotgun" };

    // QC: REGISTER_MUTATOR(ok, expr_evaluate(autocvar_g_overkill)
    //   && !MUTATOR_IS_ENABLED(mutator_instagib)
    //   && !MapInfo_LoadedGametype.m_weaponarena
    //   && cvar_string("g_mod_balance") == "Overkill").
    // The instagib guard keeps Overkill from co-enabling alongside instagib (a console `g_overkill 1` can't
    // double-activate, not just the menu radio-group); the weaponarena guard mirrors the same stand-in
    // instagib/melee_only use (g_weaponarena == 0); the balance guard ensures the Overkill rules only run under
    // the Overkill balance/cfg, not on top of an arbitrary balance.
    public override bool IsEnabled =>
        Api.Services is not null
        && Api.Cvars.GetFloat("g_overkill") != 0f
        && !OtherEnabled("instagib")                                   // QC !MUTATOR_IS_ENABLED(mutator_instagib)
        && Api.Cvars.GetFloat("g_weaponarena") == 0f                   // QC !MapInfo_LoadedGametype.m_weaponarena
        && Api.Cvars.GetString("g_mod_balance") == "Overkill";        // QC cvar_string("g_mod_balance") == "Overkill"

    // QC MUTATOR_IS_ENABLED reads the other mutator's enable predicate (not its added state), so activation
    // order between them can't race (same helper MeleeOnly uses).
    private static bool OtherEnabled(string netName)
        => Mutators.ByName(netName) is { } m && m.IsEnabled;

    private HookHandler<MutatorHooks.DamageCalculateArgs>? _onDamageCalc;
    private HookHandler<MutatorHooks.PlayerDiesArgs>? _onPlayerDies;
    private HookHandler<MutatorHooks.PlayerSpawnArgs>? _onPlayerSpawn;
    private HookHandler<MutatorHooks.ForbidThrowCurrentWeaponArgs>? _onForbidThrow;
    private HookHandler<MutatorHooks.ForbidRandomStartWeaponsArgs>? _onForbidRandom;
    private HookHandler<MutatorHooks.SetWeaponArenaArgs>? _onSetWeaponArena;
    private HookHandler<MutatorHooks.SetStartItemsArgs>? _onSetStartItems;
    private HookHandler<MutatorHooks.FilterItemDefinitionArgs>? _onFilterItemDef;
    private HookHandler<MutatorHooks.RandomItemsClassNameArgs>? _onRandomItems;
    private HookHandler<MutatorHooks.NadeDamageArgs>? _onNadeDamage;

    public override void Hook()
    {
        _onDamageCalc ??= OnDamageCalculate;
        _onPlayerDies ??= OnPlayerDies;
        _onPlayerSpawn ??= OnPlayerSpawn;
        _onForbidThrow ??= OnForbidThrow;
        _onForbidRandom ??= OnForbidRandomStartWeapons;
        _onSetWeaponArena ??= OnSetWeaponArena;
        _onSetStartItems ??= OnSetStartItems;
        _onFilterItemDef ??= OnFilterItemDefinition;
        _onRandomItems ??= OnRandomItemsGetClassName;
        _onNadeDamage ??= OnNadeDamage;

        MutatorHooks.DamageCalculate.Add(_onDamageCalc, HookOrder.Last); // QC CBC_ORDER_LAST
        MutatorHooks.PlayerDies.Add(_onPlayerDies);
        MutatorHooks.PlayerSpawn.Add(_onPlayerSpawn);
        MutatorHooks.ForbidThrowCurrentWeapon.Add(_onForbidThrow);
        MutatorHooks.ForbidRandomStartWeapons.Add(_onForbidRandom);
        MutatorHooks.SetWeaponArena.Add(_onSetWeaponArena);
        MutatorHooks.SetStartItems.Add(_onSetStartItems, HookOrder.Last);
        MutatorHooks.FilterItemDefinition.Add(_onFilterItemDef);
        MutatorHooks.RandomItemsGetClassName.Add(_onRandomItems);
        MutatorHooks.NadeDamage.Add(_onNadeDamage);

        if (Api.Services is not null)
        {
            BlasterKeepForce = Api.Cvars.GetFloat("g_overkill_blaster_keepforce") != 0f;
            BlasterKeepDamage = Api.Cvars.GetFloat("g_overkill_blaster_keepdamage") != 0f;
            string lp = Api.Cvars.GetString("g_overkill_loot_player");
            if (!string.IsNullOrEmpty(lp)) LootPlayer = lp;
            float lpt = Api.Cvars.GetFloat("g_overkill_loot_player_time");
            if (lpt != 0f) LootPlayerTime = lpt;
            StartRpc = Api.Cvars.GetFloat("g_weapon_overkill_rpc_weaponstart") > 0f
                       || Api.Cvars.GetFloat("g_start_weapon_okrpc") > 0f;
            StartHmg = Api.Cvars.GetFloat("g_weapon_overkill_hmg_weaponstart") > 0f
                       || Api.Cvars.GetFloat("g_start_weapon_okhmg") > 0f;

            // QC MUTATOR_ONADD item-block cvars + powerup-replace (sv_overkill.qh defaults: only medium/big armor
            // are filtered by default). Read here so the FilterItem hook mirrors the Base item economy.
            PowerupsReplace = Api.Cvars.GetFloat("g_overkill_powerups_replace") != 0f;
            FilterHealthMega = Api.Cvars.GetFloat("g_overkill_filter_healthmega") != 0f;
            FilterArmorMedium = ReadFilterCvar("g_overkill_filter_armormedium", true);
            FilterArmorBig = ReadFilterCvar("g_overkill_filter_armorbig", true);
            FilterArmorMega = Api.Cvars.GetFloat("g_overkill_filter_armormega") != 0f;
        }
    }

    // QC defaults differ per cvar (medium/big armor default 1, the rest 0): an UNSET cvar must keep its Base
    // default rather than reading as 0, so resolve the empty string to the supplied default.
    private static bool ReadFilterCvar(string name, bool dflt)
    {
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? dflt : Api.Cvars.GetFloat(name) != 0f;
    }

    public override void Unhook()
    {
        if (_onDamageCalc is not null) MutatorHooks.DamageCalculate.Remove(_onDamageCalc);
        if (_onPlayerDies is not null) MutatorHooks.PlayerDies.Remove(_onPlayerDies);
        if (_onPlayerSpawn is not null) MutatorHooks.PlayerSpawn.Remove(_onPlayerSpawn);
        if (_onForbidThrow is not null) MutatorHooks.ForbidThrowCurrentWeapon.Remove(_onForbidThrow);
        if (_onForbidRandom is not null) MutatorHooks.ForbidRandomStartWeapons.Remove(_onForbidRandom);
        if (_onSetWeaponArena is not null) MutatorHooks.SetWeaponArena.Remove(_onSetWeaponArena);
        if (_onSetStartItems is not null) MutatorHooks.SetStartItems.Remove(_onSetStartItems);
        if (_onFilterItemDef is not null) MutatorHooks.FilterItemDefinition.Remove(_onFilterItemDef);
        if (_onRandomItems is not null) MutatorHooks.RandomItemsGetClassName.Remove(_onRandomItems);
        if (_onNadeDamage is not null) MutatorHooks.NadeDamage.Remove(_onNadeDamage);
    }

    // QC the overkill g_overkill_items pool (sv_overkill.qh:35-39): the five overkill MAP items, by their
    // canonical spawnfunc classname (resolved through the random-items prob cvars below). HealthMega + the four
    // armors; the okhmg/okrpc superweapons are appended explicitly (QC adds them after the IL_EACH).
    private static readonly string[] OverkillItemNetNames =
        { "health_mega", "armor_small", "armor_medium", "armor_big", "armor_mega" };

    // MUTATOR_HOOKFUNCTION(ok, RandomItems_GetRandomItemClassName) (sv_overkill.qc:58) — substitute the Overkill
    // item pool. RandomItems_GetRandomOverkillItemClassName (sv_overkill.qc:21): weighted reservoir over
    // g_overkill_items (allowed) + weapon_okhmg + weapon_okrpc, keyed by g_{prefix}_{canonical}_probability.
    private bool OnRandomItemsGetClassName(ref MutatorHooks.RandomItemsClassNameArgs args)
    {
        if (Api.Services is null) { args.ClassName = ""; return true; }
        string prefix = args.Prefix;
        string chosen = "";
        float total = 0f;

        // QC IL_EACH(g_overkill_items, !(spawnflags & ITEM_FLAG_MUTATORBLOCKED) && Item_IsDefinitionAllowed(it)).
        foreach (string netName in OverkillItemNetNames)
        {
            Pickup? def = Items.ByName(netName);
            if (def is null) continue;
            if ((def.ItemDef.SpawnFlags & GameItemSpawnFlag.MutatorBlocked) != 0) continue;
            if (!def.ItemDef.IsAllowed) continue;
            string canonical = ItemSpawnFuncs.CanonicalSpawnFunc(def);
            float prob = Api.Cvars.GetFloat($"g_{prefix}_{canonical}_probability");
            if (prob <= 0f) continue;
            total += prob;
            if (Prandom.Float() * total <= prob) chosen = canonical;
        }

        // QC: the okhmg / okrpc superweapons appended after the loop.
        foreach (string okWep in new[] { "weapon_okhmg", "weapon_okrpc" })
        {
            float prob = Api.Cvars.GetFloat($"g_{prefix}_{okWep}_probability");
            if (prob <= 0f) continue;
            total += prob;
            if (Prandom.Float() * total <= prob) chosen = okWep;
        }

        args.ClassName = chosen; // QC M_ARGV(1, string) = RandomSelection_chosen_string (may be "").
        return true;             // QC returns true: the hook consumed the pick.
    }

    private static bool IsPlayer(Entity? e) => e is not null && (e.Flags & EntFlags.Client) != 0;

    // MUTATOR_HOOKFUNCTION(ok, Damage_Calculate, CBC_ORDER_LAST)
    private bool OnDamageCalculate(ref MutatorHooks.DamageCalculateArgs args)
    {
        Entity? attacker = args.Attacker;
        Entity target = args.Target;

        if (IsPlayer(attacker) && IsPlayer(target)
            && DeathTypes.WeaponNetNameOf(args.DeathType) == "blaster")
        {
            // QC: zero force on a live enemy (not self, not frozen, not dead) unless keepforce.
            if (!ReferenceEquals(attacker, target)
                && target.DeadState == DeadFlag.No
                && !IsFrozen(target)
                && !BlasterKeepForce)
                args.Force = Vector3.Zero;

            if (!BlasterKeepDamage)
                args.Damage = 0f;
        }
        // QC also nullifies the blaster against vehicle/turret targets; IS_PLAYER covers the headless case
        // (vehicles/turrets route their own damage), so the player check above is the faithful subset.
        return false;
    }

    private static bool IsFrozen(Entity e)
    {
        var frozen = StatusEffectsCatalog.Frozen;
        return frozen is not null && StatusEffectsCatalog.Has(e, frozen);
    }

    // MUTATOR_HOOKFUNCTION(ok, PlayerDies) — drop loot + remember weapons for respawn.
    private bool OnPlayerDies(ref MutatorHooks.PlayerDiesArgs args)
    {
        Entity target = args.Target;

        // QC: attacker = IS_PLAYER(frag_attacker) ? frag_attacker : frag_target; loot flies toward them.
        Entity launcher = IsPlayer(args.Attacker) ? args.Attacker! : target;
        DropItem(target, launcher, LootPlayer, LootPlayerTime);

        // QC: frag_target.ok_lastwep[slot] = (weaponentity).m_switchweapon; — remember the held weapon per
        // slot so PlayerWeaponSelect (here folded into PlayerSpawn) can re-equip it on respawn.
        Weapon? held = Inventory.CurrentWeapon(target);
        target.OkLastWeapon[0] = held?.NetName;
        for (int slot = 1; slot < MutatorConstants.MaxWeaponSlots; slot++)
            target.OkLastWeapon[slot] = null;
        return false;
    }

    // MUTATOR_HOOKFUNCTION(ok, PlayerSpawn) — give the OK loadout, then restore the remembered weapon.
    private bool OnPlayerSpawn(ref MutatorHooks.PlayerSpawnArgs args)
    {
        Entity player = args.Player;

        // The arena loadout, applied through the weapon inventory (QC start_weapons = ok_start_items).
        Inventory.ClearWeapons(player);
        GiveLoadout(player);

        // QC PlayerWeaponSelect: re-select the weapon held at death (HMG→MG, RPC→Nex) if one was recorded.
        string? want = player.OkLastWeapon[0];
        if (want is not null)
        {
            want = want switch { "okhmg" => "okmachinegun", "okrpc" => "oknex", _ => want };
            Weapon? w = Weapons.ByName(want);
            if (w is not null && Inventory.HasWeapon(player, w))
                Inventory.SwitchWeapon(player, w);
            player.OkLastWeapon[0] = null;
        }
        else
        {
            Inventory.SwitchToBest(player);
        }
        return false;
    }

    private void GiveLoadout(Entity player)
    {
        foreach (string n in BaseLoadout)
        {
            Weapon? w = Weapons.ByName(n);
            if (w is not null) Inventory.GiveWeapon(player, w);
        }
        if (StartRpc) { Weapon? w = Weapons.ByName("okrpc"); if (w is not null) Inventory.GiveWeapon(player, w); }
        if (StartHmg) { Weapon? w = Weapons.ByName("okhmg"); if (w is not null) Inventory.GiveWeapon(player, w); }
    }

    // void ok_DropItem(entity this, entity attacker, string itemlist, float itemlifetime) — sv_overkill.qc
    private void DropItem(Entity victim, Entity launcher, string itemList, float itemLifetime)
    {
        if (itemLifetime <= 0f || Api.Services is null) return;

        // QC: entity loot_itemdef = Item_RandomFromList(itemlist); if (!loot_itemdef) return; — itemlist is a
        // space-separated list of item names; an empty/"" list disables the drop, "random" picks any allowed
        // normal item. RandomFromList tokenises and rolls one entry.
        Pickup? def = RandomFromList(itemList);
        if (def is null) return;

        // QC spawns an item entity 32u above the corpse, launched up and away from the killer, then runs the
        // FULL item pipeline (Item_Initialise) so the loot has a model and a working Touch handler — i.e. it is
        // actually collectible. The port routes through StartItem.SpawnLoot, the same loot path thrown weapons use.
        Entity e = Api.Entities.Spawn();
        e.OkItem = true; // QC e.ok_item = true — loot is an Overkill item, passes the recursive FilterItem.
        Vector3 org = victim.Origin + new Vector3(0f, 0f, 32f);
        Api.Entities.SetOrigin(e, org);
        e.Origin = org;
        Vector3 away = QMath.Normalize(launcher.Origin - victim.Origin);
        e.Velocity = new Vector3(0f, 0f, 200f) + away * 500f;

        // QC e.lifetime = itemlifetime; Item_Initialise(e) — SpawnLoot owns MOVETYPE_TOSS, the despawn timer, the
        // anti-instant-pick shield, the model/bbox, and wires Item_Touch. A NODROP-brush spawn is killed inside.
        StartItem.SpawnLoot(e, def, itemLifetime);
    }

    // QC Item_RandomFromList: pick a random item def from a space-separated classname list. "" (empty) disables
    // the drop (returns null); "random" yields a random allowed normal item. Each token is an item NetName
    // (the QC list stores "armor_small" etc.); resolve it through the Item registry.
    private static Pickup? RandomFromList(string itemList)
    {
        if (string.IsNullOrEmpty(itemList)) return null;
        string[] choices = itemList.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (choices.Length == 0) return null;
        string chosen = choices[Prandom.RangeInt(0, choices.Length)];
        // QC's "random" keyword (drop any allowed normal item) needs the full normal-item pool, which the port's
        // item registry doesn't expose as a filtered group; fall back to the default armor_small loot for now.
        if (chosen == "random") chosen = "armor_small";
        return Items.ByName(chosen);
    }

    // MUTATOR_HOOKFUNCTION(ok, FilterItem) — block normal health/armor pickups (per the filter cvars) and
    // replace the Strength/Shield powerups with the HMG/RPC superweapon pickups. Returns true to FORBID the
    // original item's spawn. The item-class registry isn't fully ported, so the item kind is matched on the
    // edict's ClassName (the same stand-in Nix/Mayhem use); the live item entity carries the placed Origin.
    private bool OnFilterItemDefinition(ref MutatorHooks.FilterItemDefinitionArgs args)
    {
        Entity item = args.Definition;

        // QC: if (item.ok_item) return false; — an Overkill item (Overkill's own ok_DropItem loot, or a
        // random-items spawn/replace/loot item tagged ok_item under Overkill) is always allowed through. Both the
        // explicit Entity.OkItem flag (set by the loot/replace paths) and ItemIsLoot (the loot stand-in) qualify.
        if (item.OkItem || item.ItemIsLoot)
            return false;

        // QC: per-itemdef filter for the four normal big-health/armor pickups (defaults: only medium/big armor on).
        switch (item.ClassName)
        {
            case "item_health_mega": return FilterHealthMega;
            case "item_armor_medium": return FilterArmorMedium;
            case "item_armor_big": return FilterArmorBig;
            case "item_armor_mega": return FilterArmorMega;
        }

        // QC (sv_overkill.qc:234): the powerups-off / replace-off gate returns true (forbid) for EVERY remaining
        // item, not just powerups — under Overkill all non-ok map items are deleted. Placed before the strength/
        // shield replacement so no replacement is spawned when powerups are off.
        bool powerups = Api.Services is null || Api.Cvars.GetFloat("g_powerups") != 0f;
        if (!powerups || !PowerupsReplace)
            return true; // QC: powerups off / replace off — drop the item.

        // QC: replace item_strength -> WEP_OVERKILL_HMG, item_shield -> WEP_OVERKILL_RPC.
        bool isStrength = item.ClassName == "item_strength";
        bool isShield = item.ClassName == "item_shield";

        // QC: spawn(); Item_CopyFields(item, wep); wep.ok_item = true; wep.respawntime = superweapon respawn;
        // wep.pickup_anyway = true; wep.itemdef = WEP; wep.lifetime = -1; Item_Initialise(wep). The port spawns
        // the weapon pickup at the SAME origin via the normal StartItem path, then blocks the original below.
        if (isStrength || isShield)
        {
            Weapon? wpn = Weapons.ByName(isStrength ? "okhmg" : "okrpc");
            if (wpn is not null && Api.Services is not null)
            {
                Entity wep = Api.Entities.Spawn();
                Api.Entities.SetOrigin(wep, item.Origin);
                wep.Origin = item.Origin;
                wep.OkItem = true;                       // QC wep.ok_item = true — pass the recursive FilterItem
                wep.PickupAnyway = 1;                    // QC wep.pickup_anyway = true
                wep.ItemRespawnTime = Api.Cvars.GetFloat("g_pickup_respawntime_superweapon");
                StartItem.Spawn(wep, ItemSpawnFuncs.PickupFor(wpn));
            }
        }

        // QC sv_overkill.qc:262 — the FilterItem default: anything not an ok_item and not one of the matched
        // health/armor cases is forbidden (deleted). Under Overkill, normal map items don't survive; the OK
        // loadout/loot/random-items pipeline is the only item source. (Was incorrectly `return false`/allow.)
        return true;
    }

    private bool OnForbidThrow(ref MutatorHooks.ForbidThrowCurrentWeaponArgs args) => true;
    private bool OnForbidRandomStartWeapons(ref MutatorHooks.ForbidRandomStartWeaponsArgs args) => true;

    private bool OnSetWeaponArena(ref MutatorHooks.SetWeaponArenaArgs args)
    {
        args.Arena = "off";
        return false;
    }

    // MUTATOR_HOOKFUNCTION(ok, BuildMutatorsString) — sv_overkill.qc:283-286: append ":OK" to the machine-readable
    // server-browser mutator token list.
    public override string BuildMutatorsString(string s) => s + ":OK";

    // MUTATOR_HOOKFUNCTION(ok, BuildMutatorsPrettyString) — sv_overkill.qc:288-291: append ", Overkill" to the
    // human-readable scoreboard/server-browser mutator list.
    public override string BuildMutatorsPrettyString(string s) => s + ", Overkill";

    // MUTATOR_HOOKFUNCTION(ok, SetModname) — sv_overkill.qc:293-296: set the server modname to "Overkill" so
    // the server browser, connect banner, and any mod-detection consumers reflect the Overkill mode.
    // Returns overridden=true so the chain stops here (QC: return true from CBC_ORDER_ANY hook).
    public override (string name, bool overridden) SetModname(string name) => ("Overkill", true);

    // MUTATOR_HOOKFUNCTION(ok, SetStartItems, CBC_ORDER_LAST)
    private bool OnSetStartItems(ref MutatorHooks.SetStartItemsArgs args)
    {
        StartLoadout l = args.Loadout;
        // QC: WEPSET(OVERKILL_MACHINEGUN) | WEPSET(OVERKILL_NEX) | WEPSET(OVERKILL_SHOTGUN), plus the RPC
        // and/or HMG when their weaponstart cvar is set.
        l.Weapons.Clear();
        foreach (string n in BaseLoadout) l.Weapons.Add(n);
        if (StartRpc) l.Weapons.Add("okrpc");
        if (StartHmg) l.Weapons.Add("okhmg");
        l.ItemFlags.Add("UNLIMITED_AMMO");
        return false;
    }

    // MUTATOR_HOOKFUNCTION(okhmg_nadesupport, Nade_Damage) — port of okhmg.qc:5-12. Scale nade self-damage
    // to 10% of max health when the nade is damaged by the overkill HMG (e.g., throwing a nade while holding
    // the HMG and taking splash damage from the nade's own explosion).
    private bool OnNadeDamage(ref MutatorHooks.NadeDamageArgs args)
    {
        // QC: if (M_ARGV(1, entity) != WEP_OVERKILL_HMG) return;
        // The args.Weapon is already the weapon NetName (resolved by the hook caller from the deathtype).
        if (args.Weapon != "okhmg")
            return false;

        // QC: return = true; M_ARGV(2, float) /* damage */ = (M_ARGV(0, entity)).max_health * 0.1;
        // Scale the damage to 10% of the nade's max health.
        args.Damage = args.Nade.MaxHealth * 0.1f;
        return true; // Consumed: the hook handled the damage scaling.
    }
}
