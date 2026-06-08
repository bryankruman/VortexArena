// Port of common/mutators/mutator/powerups/powerups.qh (CLASS(Powerup, Pickup)) + the powerup item defs in
// powerup/{strength,shield,speed,invisibility,jetpack,fuelregen}.qh and their m_iteminit functions.
//
// A Powerup is a Pickup with the large bbox (ITEM_L_MAXS), FL_POWERUP item flag, glow, and the
// g_pickup_respawntime_powerup respawn time (120s). Each concrete powerup's m_iteminit seeds the world
// item's powerup timer (strength_finished / invincible_finished / speed_finished / invisibility_finished)
// from g_balance_powerup_<name>_time (the .count override, else the cvar, else 30s), and — crucially — flags
// the def ITEM_FLAG_MUTATORBLOCKED when its powerup type is disabled (so have_pickup_item deletes it at spawn).
//
// The GIVE itself is the generic Item_GiveTo (ItemPickupRules.ItemGiveTo): it reads the world item's
// *_finished timers + applies the status effect (ApplyPowerupTimers), transfers the IT_* held-item bit
// (jetpack/fuelregen via IT_PICKUPMASK), and gives RES_FUEL (jetpack) through the ammo path. So these classes
// only need ItemInit + the def metadata — there is no per-item give override (QC Pickup.giveTo is shared).

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Base for powerup pickups — port of CLASS(Powerup, Pickup) (powerups.qh). Large bbox, glow, FL_POWERUP,
/// and the long powerup respawn time. Concrete powerups set their IT_* id / colour / timer-seed in ItemInit.
/// </summary>
public abstract class PowerupPickup : Pickup
{
    protected PowerupPickup()
    {
        // QC Powerup ATTRIBs: m_maxs = ITEM_L_MAXS (large), m_respawntime = g_pickup_respawntime_powerup (120),
        // m_itemflags = FL_POWERUP. The bbox + respawn live on the Pickup base; the def flags + glow are set here.
        Mins = ItemBoxes.DefaultMins;
        Maxs = ItemBoxes.LargeMaxs;
        RespawnTime = 120f;            // g_pickup_respawntime_powerup
        ItemDef.IsPowerup = true;      // QC instanceOfPowerup
        // NOTE: m_glow is set per-item in QC, NOT on the Powerup base — strength/shield/speed/invisibility glow,
        // but jetpack + fuelregen do NOT (their .qh has no m_glow ATTRIB). The glowing four set ItemDef.Glow below.
    }

    /// <summary>QC the per-powerup <c>g_powerups_&lt;name&gt;</c> toggle (e.g. g_powerups_strength). "" = always on.</summary>
    protected virtual string EnableCvar => "";

    /// <summary>
    /// QC the common head of every powerup_*_init: if <c>g_powerups</c> is off OR this powerup's
    /// <c>g_powerups_&lt;name&gt;</c> is off, flag the def MUTATORBLOCKED (have_pickup_item then deletes it).
    /// The shipped defaults (g_powerups=1, g_powerups_*=1) make powerups spawn; a host can disable them.
    /// Returns true if the def was just blocked (the subclass then skips seeding its timer, like QC's flow
    /// which still seeds but the item is deleted before use).
    /// </summary>
    protected void ApplyMutatorBlock()
    {
        bool powerupsOn = ItemPickupRules.CvarBoolOr("g_powerups", true);
        bool thisOn = EnableCvar == "" || ItemPickupRules.CvarBoolOr(EnableCvar, true);
        if (!powerupsOn || !thisOn)
            ItemDef.SpawnFlags |= GameItemSpawnFlag.MutatorBlocked;
    }
}

/// <summary>StrengthItem — item_strength (IT_STRENGTH, '0 0 1', strength_finished = g_balance_powerup_strength_time).</summary>
[Item]
public sealed class StrengthItem : PowerupPickup
{
    protected override string EnableCvar => "g_powerups_strength";
    public StrengthItem()
    {
        NetName = "strength";
        DisplayName = "Strength";
        ItemModel = "g_strength.md3"; // MDL_Strength_ITEM
        Model = ItemModel;
        ItemDef.ItemId = ItemFlag.Strength;
        ItemDef.Color = new Vector3(0f, 0f, 1f);
        ItemDef.Glow = true;              // QC StrengthItem m_glow = true
        ItemDef.PickupSound = "Strength"; // SND_Strength ("powerup")
        ItemDef.RespawnSound = "STRENGTH_RESPAWN";
    }
    private string? ItemModel;

    /// <summary>QC powerup_strength_init: block when disabled; seed strength_finished from the balance cvar.</summary>
    public override void ItemInit(Entity item)
    {
        ApplyMutatorBlock();
        if (item.StrengthFinished == 0f)
            item.StrengthFinished = item.Count != 0 ? item.Count
                : ItemPickupRules.CvarOr("g_balance_powerup_strength_time", 30f);
    }
}

/// <summary>ShieldItem — item_shield / item_invincible (IT_INVINCIBLE, netname "invincible").</summary>
[Item]
public sealed class ShieldItem : PowerupPickup
{
    protected override string EnableCvar => "g_powerups_shield";
    public ShieldItem()
    {
        NetName = "invincible"; // QC ShieldItem netname is "invincible" (item_shield + item_invincible spawnfuncs)
        DisplayName = "Shield";
        ItemModel = "g_invincible.md3"; // MDL_Shield_ITEM
        Model = ItemModel;
        ItemDef.ItemId = ItemFlag.Invincible;
        ItemDef.Color = new Vector3(1f, 0f, 1f);
        ItemDef.Glow = true;            // QC ShieldItem m_glow = true
        ItemDef.PickupSound = "Shield"; // SND_Shield ("powerup_shield")
        ItemDef.RespawnSound = "SHIELD_RESPAWN";
    }
    private string? ItemModel;

    /// <summary>QC powerup_shield_init: block when disabled; seed invincible_finished from the balance cvar.</summary>
    public override void ItemInit(Entity item)
    {
        ApplyMutatorBlock();
        if (item.InvincibleFinished == 0f)
            item.InvincibleFinished = item.Count != 0 ? item.Count
                : ItemPickupRules.CvarOr("g_balance_powerup_invincible_time", 30f);
    }
}

/// <summary>SpeedItem — item_speed / item_buff_speed (IT_SPEED).</summary>
[Item]
public sealed class SpeedItem : PowerupPickup
{
    protected override string EnableCvar => "g_powerups_speed";
    public SpeedItem()
    {
        NetName = "speed";
        DisplayName = "Speed";
        ItemModel = "buff.md3"; // QC m_model = MDL_BUFF, m_skin 9 (no dedicated speed model yet)
        Model = ItemModel;
        ItemDef.ItemId = ItemFlag.Speed;
        ItemDef.Color = new Vector3(0.1f, 1f, 0.84f);
        ItemDef.Glow = true;           // QC SpeedItem m_glow = true
        ItemDef.PickupSound = "Speed"; // SND_Speed
        ItemDef.RespawnSound = "SHIELD_RESPAWN";
    }
    private string? ItemModel;

    /// <summary>QC powerup_speed_init: block when disabled; seed speed_finished from the balance cvar.</summary>
    public override void ItemInit(Entity item)
    {
        ApplyMutatorBlock();
        if (item.SpeedFinished == 0f)
            item.SpeedFinished = item.Count != 0 ? item.Count
                : ItemPickupRules.CvarOr("g_balance_powerup_speed_time", 30f);
    }
}

/// <summary>InvisibilityItem — item_invisibility / item_buff_invisibility (IT_INVISIBILITY).</summary>
[Item]
public sealed class InvisibilityItem : PowerupPickup
{
    protected override string EnableCvar => "g_powerups_invisibility";
    public InvisibilityItem()
    {
        NetName = "invisibility";
        DisplayName = "Invisibility";
        ItemModel = "buff.md3"; // QC m_model = MDL_BUFF, m_skin 12 (no dedicated model yet)
        Model = ItemModel;
        ItemDef.ItemId = ItemFlag.Invisibility;
        ItemDef.Color = new Vector3(0.5f, 0.5f, 1f);
        ItemDef.Glow = true;                  // QC InvisibilityItem m_glow = true
        ItemDef.PickupSound = "Invisibility"; // SND_Invisibility ("powerup")
        ItemDef.RespawnSound = "STRENGTH_RESPAWN";
    }
    private string? ItemModel;

    /// <summary>QC powerup_invisibility_init: block when disabled; seed invisibility_finished from the cvar.</summary>
    public override void ItemInit(Entity item)
    {
        ApplyMutatorBlock();
        if (item.InvisibilityFinished == 0f)
            item.InvisibilityFinished = item.Count != 0 ? item.Count
                : ItemPickupRules.CvarOr("g_balance_powerup_invisibility_time", 30f);
    }
}

/// <summary>
/// JetpackItem — item_jetpack (IT_JETPACK; NO status effect). The give transfers IT_JETPACK (IT_PICKUPMASK)
/// and refills RES_FUEL (g_pickup_fuel_jetpack). Powerup-blocked by g_powerups / g_powerups_jetpack.
/// </summary>
[Item]
public sealed class JetpackItem : PowerupPickup
{
    protected override string EnableCvar => "g_powerups_jetpack";
    public JetpackItem()
    {
        NetName = "jetpack";
        DisplayName = "Jetpack";
        ItemModel = "g_jetpack.md3"; // MDL_Jetpack_ITEM
        Model = ItemModel;
        ItemDef.ItemId = ItemFlag.Jetpack;
        ItemDef.Color = new Vector3(0.5f, 0.5f, 0.5f);
        ItemDef.PickupSound = "ITEMPICKUP"; // QC Jetpack inherits the default Pickup m_sound (SND_ITEMPICKUP)
        // QC Jetpack has NO m_glow ATTRIB → does not glow (left at the base default false).
    }
    private string? ItemModel;

    /// <summary>
    /// QC powerup_jetpack_init: block when disabled; seed RES_FUEL from g_pickup_fuel_jetpack
    /// (.count * g_jetpack_fuel override, else the cvar). NO statuseffect timer — the jetpack is a held bit.
    /// </summary>
    public override void ItemInit(Entity item)
    {
        ApplyMutatorBlock();
        if (item.GetResource(ResourceType.Fuel) == 0f)
        {
            float fuel = item.Count > 0
                ? item.Count * ItemPickupRules.CvarOr("g_jetpack_fuel", 100f)
                : ItemPickupRules.CvarOr("g_pickup_fuel_jetpack", 100f);
            item.SetResourceExplicit(ResourceType.Fuel, fuel);
        }
    }
}

/// <summary>
/// FuelRegen — item_fuel_regen (IT_FUEL_REGEN; NO status effect, NO resource). The give transfers the
/// IT_FUEL_REGEN held-item bit (IT_PICKUPMASK). Powerup-blocked by g_powerups / g_powerups_fuelregen.
/// </summary>
[Item]
public sealed class FuelRegenItem : PowerupPickup
{
    protected override string EnableCvar => "g_powerups_fuelregen";
    public FuelRegenItem()
    {
        NetName = "fuel_regen";
        DisplayName = "Fuel regenerator";
        ItemModel = "g_fuelregen.md3"; // MDL_FuelRegen_ITEM
        Model = ItemModel;
        ItemDef.ItemId = ItemFlag.FuelRegen;
        ItemDef.Color = new Vector3(1f, 0.5f, 0f);
        ItemDef.PickupSound = "ITEMPICKUP";
    }
    private string? ItemModel;

    /// <summary>QC powerup_fuelregen_init: only the mutator-block head — no resource/timer seeding.</summary>
    public override void ItemInit(Entity item) => ApplyMutatorBlock();
}
