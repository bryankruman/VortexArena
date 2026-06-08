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
/// (<c>ok_DropItem</c>), and the held-weapon capture + restore (<c>ok_lastwep[]</c> → PlayerWeaponSelect,
/// folded into PlayerSpawn, mapping HMG→Machinegun and RPC→Nex). The concrete okweapon classes and the
/// powerup→superweapon item replacement are item/weapon-registry concerns owned elsewhere; the loadout
/// gives whatever of those weapons the registry resolves.
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

    public OverkillMutator() => NetName = "overkill";

    /// <summary>The base Overkill loadout (QC WEPSET(OVERKILL_MACHINEGUN|NEX|SHOTGUN)).</summary>
    private static readonly string[] BaseLoadout = { "okmachinegun", "oknex", "okshotgun" };

    // QC enable: expr_evaluate(autocvar_g_overkill) && !instagib && !... — the real cvar is g_overkill.
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_overkill") != 0f;

    private HookHandler<MutatorHooks.DamageCalculateArgs>? _onDamageCalc;
    private HookHandler<MutatorHooks.PlayerDiesArgs>? _onPlayerDies;
    private HookHandler<MutatorHooks.PlayerSpawnArgs>? _onPlayerSpawn;
    private HookHandler<MutatorHooks.ForbidThrowCurrentWeaponArgs>? _onForbidThrow;
    private HookHandler<MutatorHooks.ForbidRandomStartWeaponsArgs>? _onForbidRandom;
    private HookHandler<MutatorHooks.SetWeaponArenaArgs>? _onSetWeaponArena;
    private HookHandler<MutatorHooks.SetStartItemsArgs>? _onSetStartItems;

    public override void Hook()
    {
        _onDamageCalc ??= OnDamageCalculate;
        _onPlayerDies ??= OnPlayerDies;
        _onPlayerSpawn ??= OnPlayerSpawn;
        _onForbidThrow ??= OnForbidThrow;
        _onForbidRandom ??= OnForbidRandomStartWeapons;
        _onSetWeaponArena ??= OnSetWeaponArena;
        _onSetStartItems ??= OnSetStartItems;

        MutatorHooks.DamageCalculate.Add(_onDamageCalc, HookOrder.Last); // QC CBC_ORDER_LAST
        MutatorHooks.PlayerDies.Add(_onPlayerDies);
        MutatorHooks.PlayerSpawn.Add(_onPlayerSpawn);
        MutatorHooks.ForbidThrowCurrentWeapon.Add(_onForbidThrow);
        MutatorHooks.ForbidRandomStartWeapons.Add(_onForbidRandom);
        MutatorHooks.SetWeaponArena.Add(_onSetWeaponArena);
        MutatorHooks.SetStartItems.Add(_onSetStartItems, HookOrder.Last);

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
        }
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
    private static void DropItem(Entity victim, Entity launcher, string itemList, float itemLifetime)
    {
        if (itemLifetime <= 0f || Api.Services is null) return;

        // QC: Item_RandomFromList(itemlist) — itemlist is a space-separated list of item names; pick one.
        string[] choices = itemList.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (choices.Length == 0) return;
        string chosen = choices[Prandom.RangeInt(0, choices.Length)];

        // QC spawns an item entity 32u above the corpse, launched up and away from the killer.
        Entity e = Api.Entities.Spawn();
        e.ClassName = "item_" + chosen;
        e.NetName = chosen;
        Vector3 org = victim.Origin + new Vector3(0f, 0f, 32f);
        Api.Entities.SetOrigin(e, org);
        Vector3 away = QMath.Normalize(launcher.Origin - victim.Origin);
        e.Velocity = new Vector3(0f, 0f, 200f) + away * 500f;
        e.MoveType = MoveType.Toss;
        e.Solid = Solid.Trigger;
        e.Flags |= EntFlags.Item;
        // QC e.lifetime = itemlifetime — schedule the loot to expire (the item pipeline owns the timer).
        e.NextThink = Api.Clock.Time + itemLifetime;
        e.Think = self => Api.Entities.Remove(self);
    }

    private bool OnForbidThrow(ref MutatorHooks.ForbidThrowCurrentWeaponArgs args) => true;
    private bool OnForbidRandomStartWeapons(ref MutatorHooks.ForbidRandomStartWeaponsArgs args) => true;

    private bool OnSetWeaponArena(ref MutatorHooks.SetWeaponArenaArgs args)
    {
        args.Arena = "off";
        return false;
    }

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
}
