// Port of common/mutators/mutator/hook/sv_hook.qc

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Grappling Hook mutator — port of common/mutators/mutator/hook/sv_hook.qc. Gives every player the
/// grappling hook as their OFFHAND weapon (so they keep their primary weapon out while reeling). Holding the
/// offhand-fire button fires/reels the hook (the Hook weapon's own grapple lifecycle); when
/// <c>g_grappling_hook_useammo</c> is off (the default) the hook costs no fuel. Enabled by the string cvar
/// <c>g_grappling_hook</c> (mutators.cfg default "0"; the instahook ruleset sets it 1).
///
/// Ported: the offhand assignment (PlayerSpawn → <see cref="Entity.OffhandWeapon"/> = "hook"), the
/// offhand-think that drives the Hook weapon's grapple each frame while the offhand button is held
/// (PlayerPreThink → <see cref="Hook.WrThink"/> on a dedicated high slot with the button mirrored), the
/// no-fuel mode when useammo is off (the offhand path reels without draining fuel), the SetStartItems fuel
/// grant when useammo IS on (FuelRegen + start fuel = g_balance_fuel_rotstable), and the FilterItem suppress
/// of the WEP_HOOK world pickup (so the offhand-hook isn't also lying on the map).
///
/// Precedence note (parity §11): both this and offhand_blaster set <see cref="Entity.OffhandWeapon"/> in
/// PlayerSpawn; the registry NetName sort makes "offhand_blaster" run AFTER "grappling_hook" so the blaster
/// wins when both are on — matching QC ("overridden by offhand_blaster"). Do not reorder.
/// </summary>
[Mutator]
public sealed class HookMutator : MutatorBase
{
    /// <summary>QC autocvar_g_grappling_hook_useammo — the hook costs fuel while reeling.</summary>
    public bool UseAmmo;

    public HookMutator() => NetName = "grappling_hook";

    // QC: REGISTER_MUTATOR(hook, expr_evaluate(cvar_string("g_grappling_hook"))) — g_grappling_hook is a string.
    public override bool IsEnabled =>
        Api.Services is not null && ExprEvaluate(Api.Cvars.GetString("g_grappling_hook"));

    // The offhand hook uses a dedicated high slot (outside the weapon-fire driver), like the offhand blaster.
    private static readonly WeaponSlot OffhandSlot = new(MutatorConstants.MaxWeaponSlots);

    private HookHandler<MutatorHooks.PlayerSpawnArgs>? _onSpawn;
    private HookHandler<MutatorHooks.PlayerPreThinkArgs>? _onPreThink;
    private HookHandler<MutatorHooks.SetStartItemsArgs>? _onSetStartItems;
    private HookHandler<MutatorHooks.FilterItemDefinitionArgs>? _onFilterItem;

    public override void Hook()
    {
        _onSpawn ??= OnPlayerSpawn;
        _onPreThink ??= OnPlayerPreThink;
        _onSetStartItems ??= OnSetStartItems;
        _onFilterItem ??= OnFilterItemDefinition;

        MutatorHooks.PlayerSpawn.Add(_onSpawn);
        MutatorHooks.PlayerPreThink.Add(_onPreThink);
        MutatorHooks.SetStartItems.Add(_onSetStartItems);
        MutatorHooks.FilterItemDefinition.Add(_onFilterItem);

        if (Api.Services is not null)
            UseAmmo = Api.Cvars.GetFloat("g_grappling_hook_useammo") != 0f;
        // QC MUTATOR_ONADD: if (!useammo) WEP_HOOK.ammo_factor = 0; — the offhand-think honours UseAmmo below
        // (it reels via the grapple lifecycle which only drains fuel when the cvar is on), so there is no
        // separate ammo_factor to set on the port's Hook weapon.
    }

    public override void Unhook()
    {
        if (_onSpawn is not null) MutatorHooks.PlayerSpawn.Remove(_onSpawn);
        if (_onPreThink is not null) MutatorHooks.PlayerPreThink.Remove(_onPreThink);
        if (_onSetStartItems is not null) MutatorHooks.SetStartItems.Remove(_onSetStartItems);
        if (_onFilterItem is not null) MutatorHooks.FilterItemDefinition.Remove(_onFilterItem);
    }

    // MUTATOR_HOOKFUNCTION(hook, PlayerSpawn) — player.offhand = OFFHAND_HOOK;
    private bool OnPlayerSpawn(ref MutatorHooks.PlayerSpawnArgs args)
    {
        args.Player.OffhandWeapon = "hook";
        return false;
    }

    // OFFHAND_HOOK.offhand_think: while the offhand button is held, drive the Hook weapon's grapple lifecycle
    // (FireGrapplingHook + reel) without switching away from the held weapon. The Hook's WrThink reads the
    // per-slot ButtonAttack to decide fire vs. reel-stop, so we mirror the offhand button onto the dedicated
    // offhand slot and call WrThink(Primary) every think (the hook is continuous — no refire gate).
    private bool OnPlayerPreThink(ref MutatorHooks.PlayerPreThinkArgs args)
    {
        Entity player = args.Player;
        if ((player.Flags & EntFlags.Client) == 0 || player.DeadState != DeadFlag.No) return false;
        if (player.OffhandWeapon != "hook") return false;
        if (Weapons.ByName("hook") is not Hook hook) return false;

        // Mirror the offhand fire button onto the offhand slot so the Hook's press/release reel logic works.
        WeaponSlotState st = player.WeaponState(OffhandSlot);
        st.ButtonAttack = player.OffhandFirePressed;

        // Drive the grapple every think (continuous). If useammo is off, top the fuel back up first so the
        // hook's hooked-fuel drain is a no-op (QC WEP_HOOK.ammo_factor = 0 → reeling is free).
        if (!UseAmmo)
            player.SetResource(ResourceType.Fuel, MathF.Max(player.GetResource(ResourceType.Fuel), hook.Primary.Ammo + 1f));

        hook.WrThink(player, OffhandSlot, FireMode.Primary);
        return false;
    }

    // MUTATOR_HOOKFUNCTION(hook, SetStartItems): grant fuel + FuelRegen when the hook uses ammo.
    private bool OnSetStartItems(ref MutatorHooks.SetStartItemsArgs args)
    {
        if (!UseAmmo) return false;
        StartLoadout l = args.Loadout;
        // start_items |= ITEM_FuelRegen.m_itemid; start_ammo_fuel = max(start_ammo_fuel, g_balance_fuel_rotstable);
        l.ItemFlags.Add("FUEL_REGEN");
        float rotstable = Api.Services is null ? 0f : Api.Cvars.GetFloat("g_balance_fuel_rotstable");
        l.AmmoFuel = MathF.Max(l.AmmoFuel, rotstable);
        return false;
    }

    // MUTATOR_HOOKFUNCTION(hook, FilterItem) — return true to suppress the WEP_HOOK world pickup while the
    // offhand-hook is on (QC: return item.weapon == WEP_HOOK.m_id). Reuses FilterItemDefinition; matched on
    // the pickup's weapon NetName / classname.
    private bool OnFilterItemDefinition(ref MutatorHooks.FilterItemDefinitionArgs args)
    {
        Entity def = args.Definition;
        return def.NetName == "hook" || def.ClassName == "weapon_hook";
    }

    /// <summary>QC <c>expr_evaluate(s)</c> for a cvar string: false for "" / "0" / "false", true otherwise.</summary>
    private static bool ExprEvaluate(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        s = s.Trim();
        if (s == "0" || string.Equals(s, "false", System.StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}
