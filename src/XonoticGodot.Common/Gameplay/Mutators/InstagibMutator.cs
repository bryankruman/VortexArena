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

        MutatorHooks.DamageCalculate.Add(_onDamageCalc);
        MutatorHooks.PlayerDies.Add(_onPlayerDies);
        MutatorHooks.PlayerRegen.Add(_onPlayerRegen);
        MutatorHooks.PlayerSpawn.Add(_onPlayerSpawn);
        MutatorHooks.PlayerPreThink.Add(_onPreThink);
        MutatorHooks.ForbidThrowCurrentWeapon.Add(_onForbidThrow);
        MutatorHooks.ForbidRandomStartWeapons.Add(_onForbidRandom);
        MutatorHooks.SetWeaponArena.Add(_onSetWeaponArena);
        MutatorHooks.SetStartItems.Add(_onSetStartItems);

        if (Api.Services is not null)
        {
            BlasterKeepDamage = Api.Cvars.GetFloat("g_instagib_blaster_keepdamage") != 0f;
            BlasterKeepForce = Api.Cvars.GetFloat("g_instagib_blaster_keepforce") != 0f;
            MirrorDamage = Api.Cvars.GetFloat("g_instagib_mirrordamage") != 0f;
            float start = Api.Cvars.GetFloat("g_instagib_ammo_start");
            if (start != 0f) AmmoStart = start;
            float xl = Api.Cvars.GetFloat("g_instagib_extralives");
            if (xl != 0f) ExtraLives = xl;
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
    }

    private static bool IsPlayer(Entity? e) => e is not null && (e.Flags & EntFlags.Client) != 0;

    // MUTATOR_HOOKFUNCTION(mutator_instagib, Damage_Calculate)
    private bool OnDamageCalculate(ref MutatorHooks.DamageCalculateArgs args)
    {
        Entity target = args.Target;
        if (!IsPlayer(target))
            return false;

        // QC: never count fall damage in instagib.
        if (args.DeathType == DeathTypes.Fall)
            args.Damage = 0f;

        Entity? attacker = args.Attacker;
        string wep = DeathTypes.WeaponNetNameOf(args.DeathType);
        if (IsPlayer(attacker))
        {
            // QC: vaporizer hit on a player with armor → eat a "life" (armor point) and do no damage.
            if (wep == "vaporizer")
            {
                float armor = target.GetResource(ResourceType.Armor);
                if (armor > 0f)
                {
                    armor -= 1f;
                    target.SetResource(ResourceType.Armor, armor);
                    args.Damage = 0f;
                    // QC: ++hitsound_damage_dealt on both; CENTER_INSTAGIB_LIVES_REMAINING tells the
                    // target how many lives are left. (hitsound is a client-only feedback signal.)
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
        if (player.DeadState != DeadFlag.No)
            StopCountdown(player);
        else if (player.GetResource(ResourceType.Cells) > 0f || godmode)
            StopCountdown(player);
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

        // QC announcer countdown + "find ammo" centerprint past 80hp.
        if (hp <= 5f)
            NotificationSystem.Announce(player, "INSTAGIB_TERMINATED");
        if (hp > 80f)
            NotificationSystem.Center(player, "INSTAGIB_FINDAMMO");
    }

    /// <summary>
    /// QC ItemTouch for a vaporizer-cells item: refill to full health (the cells themselves are given by the
    /// item's normal resource path). Exposed so the item pipeline can route an instagib cells pickup here.
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
