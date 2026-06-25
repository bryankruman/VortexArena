using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The InstaGib mutator — port of common/mutators/mutator/instagib/sv_instagib.qc. Everyone spawns with
/// the Vaporizer and (almost) every weapon one-shots; armor acts as extra "lives", the blaster does no
/// damage, and there's no health/armor regen. Enabled by the <c>g_instagib</c> cvar.
///
/// This port wires the balance-defining hooks (the armor-as-lives subtraction with the lives notification,
/// the blaster damage/force nullification, always-gib-on-vaporizer death, regen disable, the
/// weapon-throw/arena forbids and the start loadout). It also drives the deep behaviour: a per-spawn
/// Vaporizer-only loadout applied through <see cref="Inventory"/>, the no-ammo countdown
/// (<c>instagib_ammocheck</c>) on PlayerPreThink that bleeds a cell-less player out, and the
/// extra-life / vaporizer-cells pickup effects (<see cref="OnExtraLifeTouch"/> / <see cref="OnCellsTouch"/>).
/// The CSQC random-powerup item replacement is a client/item-registry concern left to the item pipeline.
/// </summary>
[Mutator]
public sealed class InstagibMutator : MutatorBase
{
    /// <summary>QC autocvar_g_instagib_blaster_keepdamage — let the blaster deal real damage.</summary>
    public bool BlasterKeepDamage;

    /// <summary>QC autocvar_g_instagib_blaster_keepforce — let the blaster keep knockback.</summary>
    public bool BlasterKeepForce;

    /// <summary>QC autocvar_g_instagib_mirrordamage — support real mirror damage instead of the lives hack.</summary>
    public bool MirrorDamage;

    /// <summary>QC cvar("g_instagib_ammo_start") — vaporizer cells granted on spawn.</summary>
    public float AmmoStart = 10f;

    /// <summary>QC autocvar_g_instagib_extralives — armor "lives" granted by an ExtraLife pickup.</summary>
    public float ExtraLives = 1f;

    /// <summary>QC autocvar_g_instagib_damagedbycontents (default true) — lava/slime/drown still hurt.</summary>
    public bool DamagedByContents = true;

    /// <summary>QC autocvar_g_instagib_friendlypush (default true) — Vaporizer knockback affects teammates.</summary>
    public bool FriendlyPush = true;

    /// <summary>QC autocvar_g_instagib_allow_jetpacks — keep Jetpack/FuelRegen pickups on the map.</summary>
    public bool AllowJetpacks;

    /// <summary>QC autocvar_g_instagib_ammo_convert_cells — turn cell packs into Vaporizer cells.</summary>
    public bool AmmoConvertCells;

    /// <summary>QC autocvar_g_instagib_ammo_convert_rockets.</summary>
    public bool AmmoConvertRockets;

    /// <summary>QC autocvar_g_instagib_ammo_convert_shells.</summary>
    public bool AmmoConvertShells;

    /// <summary>QC autocvar_g_instagib_ammo_convert_bullets.</summary>
    public bool AmmoConvertBullets;

    public InstagibMutator() => NetName = "instagib";

    // QC: expr_evaluate(cvar_string("g_instagib")) — instagib is the headline arena mutator.
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_instagib") != 0f;

    private HookHandler<MutatorHooks.DamageCalculateArgs>? _onDamageCalc;
    private HookHandler<MutatorHooks.PlayerDiesArgs>? _onPlayerDies;
    private HookHandler<MutatorHooks.PlayerRegenArgs>? _onPlayerRegen;
    private HookHandler<MutatorHooks.PlayerSpawnArgs>? _onPlayerSpawn;
    private HookHandler<MutatorHooks.PlayerPreThinkArgs>? _onPreThink;
    private HookHandler<MutatorHooks.ForbidThrowCurrentWeaponArgs>? _onForbidThrow;
    private HookHandler<MutatorHooks.ForbidRandomStartWeaponsArgs>? _onForbidRandom;
    private HookHandler<MutatorHooks.SetWeaponArenaArgs>? _onSetWeaponArena;
    private HookHandler<MutatorHooks.SetStartItemsArgs>? _onSetStartItems;
    private HookHandler<GameHooks.PlayerDamageArgs>? _onSplitHealthArmor;
    private HookHandler<MutatorHooks.FilterItemDefinitionArgs>? _onFilterItemDef;
    private HookHandler<MutatorHooks.ItemTouchArgs>? _onItemTouch;

    public override void Hook()
    {
        _onDamageCalc ??= OnDamageCalculate;
        _onPlayerDies ??= OnPlayerDies;
        _onPlayerRegen ??= OnPlayerRegen;
        _onPlayerSpawn ??= OnPlayerSpawn;
        _onPreThink ??= OnPlayerPreThink;
        _onForbidThrow ??= OnForbidThrow;
        _onForbidRandom ??= OnForbidRandomStartWeapons;
        _onSetWeaponArena ??= OnSetWeaponArena;
        _onSetStartItems ??= OnSetStartItems;
        _onSplitHealthArmor ??= OnPlayerDamageSplitHealthArmor;
        _onFilterItemDef ??= OnFilterItemDefinition;
        _onItemTouch ??= OnItemTouch;

        MutatorHooks.DamageCalculate.Add(_onDamageCalc);
        MutatorHooks.PlayerDies.Add(_onPlayerDies);
        MutatorHooks.PlayerRegen.Add(_onPlayerRegen);
        MutatorHooks.PlayerSpawn.Add(_onPlayerSpawn);
        MutatorHooks.PlayerPreThink.Add(_onPreThink);
        MutatorHooks.ForbidThrowCurrentWeapon.Add(_onForbidThrow);
        MutatorHooks.ForbidRandomStartWeapons.Add(_onForbidRandom);
        MutatorHooks.SetWeaponArena.Add(_onSetWeaponArena);
        MutatorHooks.SetStartItems.Add(_onSetStartItems);
        // QC PlayerDamage_SplitHealthArmor: armor never absorbs damage in instagib (take = damage, save = 0)
        // — without this the port's 70% armor block corrupts the armor-as-lives model + the no-ammo bleed.
        GameHooks.PlayerDamageSplitHealthArmor.Add(_onSplitHealthArmor);
        // QC FilterItem: convert/replace/remove map weapons, powerups, ammo and jetpacks.
        MutatorHooks.FilterItemDefinition.Add(_onFilterItemDef);
        // QC ItemTouch: cells pickup full-heals; ExtraLife grants armor "lives".
        MutatorHooks.ItemTouch.Add(_onItemTouch);

        if (Api.Services is not null)
        {
            BlasterKeepDamage = Api.Cvars.GetFloat("g_instagib_blaster_keepdamage") != 0f;
            BlasterKeepForce = Api.Cvars.GetFloat("g_instagib_blaster_keepforce") != 0f;
            MirrorDamage = Api.Cvars.GetFloat("g_instagib_mirrordamage") != 0f;
            float start = Api.Cvars.GetFloat("g_instagib_ammo_start");
            if (start != 0f) AmmoStart = start;
            float xl = Api.Cvars.GetFloat("g_instagib_extralives");
            if (xl != 0f) ExtraLives = xl;
            // QC defaults (mutators.cfg): damagedbycontents/friendlypush both default 1, the rest default 0.
            // The cvars are registered with those defaults, so GetFloat returns the live/default value.
            DamagedByContents = Api.Cvars.GetFloat("g_instagib_damagedbycontents") != 0f;
            FriendlyPush = Api.Cvars.GetFloat("g_instagib_friendlypush") != 0f;
            AllowJetpacks = Api.Cvars.GetFloat("g_instagib_allow_jetpacks") != 0f;
            AmmoConvertCells = Api.Cvars.GetFloat("g_instagib_ammo_convert_cells") != 0f;
            AmmoConvertRockets = Api.Cvars.GetFloat("g_instagib_ammo_convert_rockets") != 0f;
            AmmoConvertShells = Api.Cvars.GetFloat("g_instagib_ammo_convert_shells") != 0f;
            AmmoConvertBullets = Api.Cvars.GetFloat("g_instagib_ammo_convert_bullets") != 0f;
        }
    }

    public override void Unhook()
    {
        if (_onDamageCalc is not null) MutatorHooks.DamageCalculate.Remove(_onDamageCalc);
        if (_onPlayerDies is not null) MutatorHooks.PlayerDies.Remove(_onPlayerDies);
        if (_onPlayerRegen is not null) MutatorHooks.PlayerRegen.Remove(_onPlayerRegen);
        if (_onPlayerSpawn is not null) MutatorHooks.PlayerSpawn.Remove(_onPlayerSpawn);
        if (_onPreThink is not null) MutatorHooks.PlayerPreThink.Remove(_onPreThink);
        if (_onForbidThrow is not null) MutatorHooks.ForbidThrowCurrentWeapon.Remove(_onForbidThrow);
        if (_onForbidRandom is not null) MutatorHooks.ForbidRandomStartWeapons.Remove(_onForbidRandom);
        if (_onSetWeaponArena is not null) MutatorHooks.SetWeaponArena.Remove(_onSetWeaponArena);
        if (_onSetStartItems is not null) MutatorHooks.SetStartItems.Remove(_onSetStartItems);
        if (_onSplitHealthArmor is not null) GameHooks.PlayerDamageSplitHealthArmor.Remove(_onSplitHealthArmor);
        if (_onFilterItemDef is not null) MutatorHooks.FilterItemDefinition.Remove(_onFilterItemDef);
        if (_onItemTouch is not null) MutatorHooks.ItemTouch.Remove(_onItemTouch);
    }

    private static bool IsPlayer(Entity? e) => e is not null && (e.Flags & EntFlags.Client) != 0;

    // MUTATOR_HOOKFUNCTION(mutator_instagib, Damage_Calculate)
    private bool OnDamageCalculate(ref MutatorHooks.DamageCalculateArgs args)
    {
        Entity target = args.Target;
        Entity? attacker = args.Attacker;

        // QC FIRST branch: g_friendlyfire == 0 && SAME_TEAM && both players → no damage at all.
        if (Api.Services is not null
            && Api.Cvars.GetFloat("g_friendlyfire") == 0f
            && IsPlayer(target) && IsPlayer(attacker)
            && Teams.SameTeam(target, attacker!))
            args.Damage = 0f;

        if (!IsPlayer(target))
            return false;

        // QC: never count fall damage in instagib.
        if (args.DeathType == DeathTypes.Fall)
            args.Damage = 0f;

        // QC: lava/slime/drown damage only counts when g_instagib_damagedbycontents is set (default true).
        if (!DamagedByContents)
        {
            string baseDeath = DeathTypes.BaseOf(args.DeathType);
            if (baseDeath == DeathTypes.Drown || baseDeath == DeathTypes.Slime || baseDeath == DeathTypes.Lava)
                args.Damage = 0f;
        }

        string wep = DeathTypes.WeaponNetNameOf(args.DeathType);
        if (IsPlayer(attacker))
        {
            // QC: vaporizer hit on a player with armor → eat a "life" (armor point) and do no damage.
            if (wep == "vaporizer")
            {
                // QC: !friendlypush && SAME_TEAM → no knockback on teammates.
                if (!FriendlyPush && Teams.SameTeam(target, attacker!))
                    args.Force = Vector3.Zero;

                float armor = target.GetResource(ResourceType.Armor);
                if (armor > 0f)
                {
                    armor -= 1f;
                    target.SetResource(ResourceType.Armor, armor);
                    args.Damage = 0f;
                    // QC: ++hitsound_damage_dealt on both; CENTER_INSTAGIB_LIVES_REMAINING tells the
                    // target how many lives are left. (hitsound is a client-only feedback signal.)
                    target.HitsoundDamageDealtTotal += 1f;
                    attacker!.HitsoundDamageDealtTotal += 1f;
                    NotificationSystem.Center(target, "INSTAGIB_LIVES_REMAINING", armor);
                }
            }

            // QC: blaster generally does no damage/force in instagib (unless keep* cvars say otherwise).
            if (wep == "blaster")
            {
                if (!BlasterKeepDamage || ReferenceEquals(attacker, target))
                {
                    args.Damage = 0f;
                    if (!MirrorDamage)
                        args.MirrorDamage = 0f;
                }
                if (!ReferenceEquals(target, attacker) && !BlasterKeepForce)
                    args.Force = Vector3.Zero;
            }
        }

        // QC: mirror damage just costs the attacker extra LIVES rather than killing them.
        if (!MirrorDamage && IsPlayer(attacker) && args.MirrorDamage > 0f)
        {
            float armor = attacker!.GetResource(ResourceType.Armor);
            if (armor > 0f)
            {
                armor -= 1f;
                attacker.SetResource(ResourceType.Armor, armor);
                NotificationSystem.Center(attacker, "INSTAGIB_LIVES_REMAINING", armor);
                // QC: frag_attacker.hitsound_damage_dealt += frag_mirrordamage.
                attacker.HitsoundDamageDealtTotal += args.MirrorDamage;
            }
            args.MirrorDamage = 0f;
        }

        return false; // CBC_ORDER_ANY — not exclusive.
    }

    // MUTATOR_HOOKFUNCTION(mutator_instagib, PlayerDies) — always gib on a vaporizer death.
    private bool OnPlayerDies(ref MutatorHooks.PlayerDiesArgs args)
    {
        if (DeathTypes.WeaponNetNameOf(args.DeathType) == "vaporizer")
            args.Damage = 1000f; // M_ARGV(4, float) = 1000
        return false;
    }

    // MUTATOR_HOOKFUNCTION(mutator_instagib, PlayerRegen) — no regeneration in instagib.
    private bool OnPlayerRegen(ref MutatorHooks.PlayerRegenArgs args) => true;

    // MUTATOR_HOOKFUNCTION(mutator_instagib, PlayerSpawn) — players glow fullbright.
    private bool OnPlayerSpawn(ref MutatorHooks.PlayerSpawnArgs args)
    {
        Entity player = args.Player;
        // QC: player.effects |= EF_FULLBRIGHT;
        player.Effects |= EffectFlags.FullBright;

        // QC's SetStartItems gives start_weapons = WEPSET(VAPORIZER); applied per-spawn through the
        // weapon inventory. Make the arena-loadout concrete: only the Vaporizer, and switch to it.
        Weapon? vaporizer = Weapons.ByName("vaporizer");
        if (vaporizer is not null)
        {
            Inventory.ClearWeapons(player);
            Inventory.GiveWeapon(player, vaporizer);
            Inventory.SwitchToBest(player);
        }

        // restart the no-ammo countdown bookkeeping for this life.
        player.InstagibNeedAmmo = false;
        player.InstagibNextThink = 0f;
        return false;
    }

    // MUTATOR_HOOKFUNCTION(mutator_instagib, PlayerPreThink) — instagib_ammocheck.
    private bool OnPlayerPreThink(ref MutatorHooks.PlayerPreThinkArgs args)
    {
        AmmoCheck(args.Player);
        return false;
    }

    // void instagib_ammocheck(entity this) — sv_instagib.qc
    private void AmmoCheck(Entity player)
    {
        if (Api.Services is null) return;
        float now = Api.Clock.Time;
        if (now < player.InstagibNextThink) return;
        if (!IsPlayer(player)) return;

        bool godmode = (player.Flags & EntFlags.GodMode) != 0;
        bool unlimitedAmmo = (player.Items & (int)ItemFlag.UnlimitedAmmo) != 0;
        // QC global game_stopped — host-driven freeze (warmup-over / match-ended / intermission). Read the real
        // host flag (VehicleCommon.GameStopped, set by the match loop), falling back to the cvar mirror — same
        // pattern as VehicleCommon.FreezeIfGameStopped. The bare cvar alone is never written, so it read as 0.
        bool gameStopped = VehicleCommon.GameStopped
            || Api.Cvars.GetFloat("g_game_stopped") != 0f;
        // QC autocvar_g_rm && autocvar_g_rm_laser — rocketminsta downgrade mode.
        bool rocketMinsta = Api.Cvars.GetFloat("g_rm") != 0f && Api.Cvars.GetFloat("g_rm_laser") != 0f;

        if (player.DeadState != DeadFlag.No || gameStopped)
            StopCountdown(player);
        else if (player.GetResource(ResourceType.Cells) > 0f || unlimitedAmmo || godmode)
            StopCountdown(player);
        else if (rocketMinsta)
        {
            // QC: under rocketminsta the player's weapon is downgraded instead of bleeding out.
            if (!player.InstagibNeedAmmo)
            {
                NotificationSystem.Center(player, "INSTAGIB_DOWNGRADE");
                player.InstagibNeedAmmo = true;
            }
        }
        else
        {
            player.InstagibNeedAmmo = true;
            Countdown(player);
        }
        player.InstagibNextThink = now + 1f;
    }

    // void instagib_stop_countdown(entity e)
    private static void StopCountdown(Entity e)
    {
        if (!e.InstagibNeedAmmo) return;
        // QC: Kill_Notification(... CPID_INSTAGIB_FINDAMMO) — clears the "find ammo" centerprint.
        e.InstagibNeedAmmo = false;
    }

    // void instagib_countdown(entity this) — bleed the player out when they have no cells.
    private void Countdown(Entity player)
    {
        float hp = player.GetResource(ResourceType.Health);

        // QC: dmg = (hp <= 10) ? 5 : 10; Damage(... DEATH_NOAMMO ...).
        float dmg = hp <= 10f ? 5f : 10f;
        Combat.Damage(player, player, player, dmg, "noammo", player.Origin, System.Numerics.Vector3.Zero);

        // QC: annce = (hp <= 5) ? ANNCE_INSTAGIB_TERMINATED : Announcer_PickNumber(CNT_NORMAL, ceil(hp/10)).
        // Announcer_PickNumber(CNT_NORMAL, n) plays the NUM_<n> spoken-number announcer (the "1".."10" clips).
        if (hp <= 5f)
        {
            NotificationSystem.Announce(player, "INSTAGIB_TERMINATED");
        }
        else
        {
            int num = (int)System.Math.Ceiling(hp / 10f);
            if (num < 1) num = 1;
            if (num > 10) num = 10;
            NotificationSystem.Announce(player, $"NUM_{num}");
        }

        // QC "find ammo" centerprint past 80hp; the >90 variant is the louder MULTI ("get some ammo or you'll be dead").
        if (hp > 80f)
        {
            if (hp <= 90f)
                NotificationSystem.Center(player, "INSTAGIB_FINDAMMO");
            else
                NotificationSystem.Send(NotifBroadcast.OneOnly, player, MsgType.Multi, "MULTI_INSTAGIB_FINDAMMO");
        }
    }

    // MUTATOR_HOOKFUNCTION(mutator_instagib, ItemTouch) — sv_instagib.qc:379. Fired (notify-style) from the
    // real item-pickup path (ItemPickupRules.ItemTouch -> MutatorHooks.FireItemTouch) just before the give, so
    // a cells pickup full-heals the toucher and an ExtraLife pickup grants armor "lives".
    private bool OnItemTouch(ref MutatorHooks.ItemTouchArgs args)
    {
        Entity item = args.Item;
        Entity toucher = args.Toucher;

        // QC: if (GetResource(item, RES_CELLS)) — any item that carries cells (the VaporizerCells/cells pack).
        if (item.Pickup is AmmoPickup ammo && ammo.Resource == ResourceType.Cells)
        {
            OnCellsTouch(toucher);
            return false; // MUT_ITEMTOUCH_CONTINUE
        }

        // QC: if (item.itemdef == ITEM_ExtraLife).
        if (item.ClassName is "item_extralife" || item.NetName == "extralife"
            || (item.Pickup is not null && item.Pickup.NetName == "extralife"))
            OnExtraLifeTouch(toucher);

        return false;
    }

    /// <summary>
    /// QC ItemTouch for a vaporizer-cells item: refill to full health (the cells themselves are given by the
    /// item's normal resource path). Routed live from <see cref="OnItemTouch"/> on a cells pickup.
    /// </summary>
    public void OnCellsTouch(Entity toucher)
    {
        if (!IsPlayer(toucher)) return;
        float hp = toucher.GetResource(ResourceType.Health);
        if (hp <= 5f)
            NotificationSystem.Announce(toucher, "INSTAGIB_LASTSECOND");
        else if (hp < 50f)
            NotificationSystem.Announce(toucher, "INSTAGIB_NARROWLY");
        if (hp < 100f)
            toucher.SetResource(ResourceType.Health, 100f);
    }

    /// <summary>
    /// QC ItemTouch for ITEM_ExtraLife: grant <see cref="ExtraLives"/> armor "lives". Exposed so the item
    /// pipeline can route an instagib extra-life pickup here (returns true = item consumed).
    /// </summary>
    public bool OnExtraLifeTouch(Entity toucher)
    {
        if (!IsPlayer(toucher)) return false;
        toucher.GiveResource(ResourceType.Armor, ExtraLives);
        NotificationSystem.Center(toucher, "EXTRALIVES", ExtraLives);
        return true;
    }

    // MUTATOR_HOOKFUNCTION(mutator_instagib, PlayerDamage_SplitHealthArmor) — armor never absorbs damage:
    // take = damage, save = 0. (QC M_ARGV(4) = M_ARGV(7); M_ARGV(5) = 0.) This is what makes armor act as
    // discrete "lives" instead of a 70% damage sponge, and keeps the no-ammo bleed at its raw 10/5 per tick.
    private bool OnPlayerDamageSplitHealthArmor(ref GameHooks.PlayerDamageArgs args)
    {
        args.DamageTake = args.DamageSave + args.DamageTake; // = the full incoming damage (take + save split)
        args.DamageSave = 0f;
        return false; // CBC_ORDER_ANY
    }

    // MUTATOR_HOOKFUNCTION(mutator_instagib, FilterItem) — convert/replace/remove map items.
    // The port's item registry is still classname/netname-based (no GameItem itemdef on the spawn def yet),
    // so the switch on item.itemdef / item.weapon is approximated by matching the definition's classname /
    // NetName tags (the same stand-in NIX/melee_only use). Returns true to delete/suppress the spawn.
    private bool OnFilterItemDefinition(ref MutatorHooks.FilterItemDefinitionArgs args)
    {
        Entity def = args.Definition;
        string cls = def.ClassName;
        string net = def.NetName;

        // QC: big powerups (Strength/Shield/HealthMega/ArmorMega) → random instagib powerup deck under
        // g_powerups, else deleted. The instagib powerup item economy (deck + spawnfuncs) isn't ported yet,
        // so we faithfully DELETE them here (the replace-with-random-powerup deck is a cross-file TODO).
        if (cls is "item_strength" or "item_shield" or "item_health_mega" or "item_armor_mega"
            || net is "strength" or "shield" or "health_mega" or "armor_mega")
            return true;

        // QC: Invisibility / ExtraLife / Speed are kept (return false = allowed).
        if (cls is "item_invisible" or "item_invisibility" or "item_extralife" or "item_speed"
            || net is "invisibility" or "extralife" or "speed")
            return false;

        // QC (sv_instagib.qc:326-341): cell/rocket/shell/bullet packs are ALWAYS removed (return true). When
        // the matching g_instagib_ammo_convert_* cvar is set they're REPLACED with vaporizer cells first; the
        // port has no item-spawn replace seam (the VaporizerCells economy isn't ported), so for now the
        // original pack is unconditionally deleted either way. (Replace-with-VaporizerCells = cross-file TODO.)
        if (cls is "item_cells" or "item_rockets" or "item_shells" or "item_bullets"
            || net is "cells" or "rockets" or "shells" or "bullets")
            return true;

        // QC: Jetpack / FuelRegen removed unless g_instagib_allow_jetpacks.
        if (cls is "item_jetpack" or "item_fuel_regen" || net is "jetpack" or "fuel_regen")
            return !AllowJetpacks;

        // QC: Devastator / Vortex weapon pickups → vaporizer cells (replaced, so delete the original).
        if (cls is "weapon_devastator" or "weapon_vortex" || net is "devastator" or "vortex")
            return true;

        // QC: the VaporizerCells economy item (cells-carrier, not a weapon) is kept; loot Vaporizer is kept with
        // a cells seed (sv_instagib.qc:349-355, 361-366) — not yet ported. Keep it explicitly here for forward
        // compatibility once the economy lands.
        if (cls is "item_vaporizer_cells" or "item_minst_cells" || net == "vaporizer_cells")
            return false;

        // QC fallthrough (sv_instagib.qc:368): everything else (stock weapons/items that don't carry cells) is
        // DELETED by default — a normal DM map under instagib must not spawn its stock weapons or ammo.
        return true;
    }

    private bool OnForbidThrow(ref MutatorHooks.ForbidThrowCurrentWeaponArgs args) => true;
    private bool OnForbidRandomStartWeapons(ref MutatorHooks.ForbidRandomStartWeaponsArgs args) => true;

    // MUTATOR_HOOKFUNCTION(mutator_instagib, SetWeaponArena) — turn weapon arena off.
    private bool OnSetWeaponArena(ref MutatorHooks.SetWeaponArenaArgs args)
    {
        args.Arena = "off";
        return false;
    }

    // MUTATOR_HOOKFUNCTION(mutator_instagib, SetStartItems, CBC_ORDER_LAST)
    private bool OnSetStartItems(ref MutatorHooks.SetStartItemsArgs args)
    {
        StartLoadout l = args.Loadout;
        l.Health = 100f;
        l.Armor = 0f;
        l.AmmoShells = 0f;
        l.AmmoBullets = 0f;
        l.AmmoRockets = 0f;
        l.AmmoCells = AmmoStart;
        l.SetWeapons("vaporizer");
        // QC: start_items |= IT_UNLIMITED_SUPERWEAPONS; (the vaporizer never runs out of "super" charge).
        // StartLoadout has no separate warmup_* twins — its live values already mirror QC's warmup_start_*.
        l.ItemFlags.Add("UNLIMITED_SUPERWEAPONS");
        return false;
    }
}
