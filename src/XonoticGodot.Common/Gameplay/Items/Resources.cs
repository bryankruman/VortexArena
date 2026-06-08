using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Resource type identifiers — the C# successor to QuakeC's RES_* registry
/// (common/resources/resources.qh + all.inc). Order mirrors the REGISTER_RESOURCE order so any
/// future content-hash agrees with the QC enumeration.
/// </summary>
public enum ResourceType
{
    None = 0,   // RES_NONE
    Health,     // RES_HEALTH    (.health)
    Armor,      // RES_ARMOR     (.armorvalue)
    Shells,     // RES_SHELLS    (.ammo_shells)
    Bullets,    // RES_BULLETS   (.ammo_nails)
    Rockets,    // RES_ROCKETS   (.ammo_rockets)
    Cells,      // RES_CELLS     (.ammo_cells)
    Fuel,       // RES_FUEL      (.ammo_fuel)
}

/// <summary>
/// The resource (health/armor/ammo) accessors — port of common/resources/{resources,sv_resources}.qc.
/// QC reached resources through a field-pointer indirection (<c>GetResourceField</c>); here we switch
/// on <see cref="ResourceType"/> over the typed members promoted onto <see cref="Entity"/>.
///
/// The mutator-hook path (GetResourceLimit/SetResource/GiveResource overrides plus the
/// ResourceAmountChanged / ResourceWasted reactions) is wired through <see cref="ResourceHooks"/>; per-
/// player rot timers remain a server-loop concern. The balance-relevant behavior — clamping to per-resource
/// limits, honouring a mutator forbid/override — is ported faithfully.
/// </summary>
public static class Resources
{
    /// <summary>QC RES_AMOUNT_HARD_LIMIT.</summary>
    public const float HardLimit = 999f;

    /// <summary>QC RES_LIMIT_NONE.</summary>
    public const float LimitNone = -1f;

    /// <summary>GetResource(e, res_type) — common/resources/sv_resources.qc.</summary>
    public static float GetResource(this Entity e, ResourceType res) => res switch
    {
        ResourceType.Health  => e.Health,
        ResourceType.Armor   => e.ArmorValue,
        ResourceType.Shells  => e.AmmoShells,
        ResourceType.Bullets => e.AmmoBullets,
        ResourceType.Rockets => e.AmmoRockets,
        ResourceType.Cells   => e.AmmoCells,
        ResourceType.Fuel    => e.AmmoFuel,
        _ => 0f,
    };

    /// <summary>SetResourceExplicit(e, res_type, amount) — writes with no limit/hook. Returns true if changed.</summary>
    public static bool SetResourceExplicit(this Entity e, ResourceType res, float amount)
    {
        if (GetResource(e, res) == amount) return false;
        switch (res)
        {
            case ResourceType.Health:  e.Health = amount; break;
            case ResourceType.Armor:   e.ArmorValue = amount; break;
            case ResourceType.Shells:  e.AmmoShells = amount; break;
            case ResourceType.Bullets: e.AmmoBullets = amount; break;
            case ResourceType.Rockets: e.AmmoRockets = amount; break;
            case ResourceType.Cells:   e.AmmoCells = amount; break;
            case ResourceType.Fuel:    e.AmmoFuel = amount; break;
            default: return false;
        }
        return true;
    }

    /// <summary>
    /// GetResourceLimit(e, res_type) — common/resources/sv_resources.qc. Reads the Xonotic balance cvars
    /// through the facade so names stay identical (OPEN Q5). Non-player entities have no limit.
    /// </summary>
    public static float GetResourceLimit(Entity e, ResourceType res)
    {
        // QC: if (!IS_PLAYER(e)) return RES_LIMIT_NONE; — approximated by the client flag.
        if ((e.Flags & EntFlags.Client) == 0)
            return LimitNone;

        float limit = res switch
        {
            ResourceType.Health  => Cvar("g_balance_health_limit", 200f),
            ResourceType.Armor   => Cvar("g_balance_armor_limit", 200f),
            ResourceType.Shells  => Cvar("g_pickup_shells_max", 60f),
            ResourceType.Bullets => Cvar("g_pickup_nails_max", 320f),
            ResourceType.Rockets => Cvar("g_pickup_rockets_max", 160f),
            ResourceType.Cells   => Cvar("g_pickup_cells_max", 180f),
            ResourceType.Fuel    => Cvar("g_balance_fuel_limit", 100f),
            _ => 0f,
        };
        // QC: MUTATOR_CALLHOOK(GetResourceLimit, e, res_type, limit) override.
        limit = ResourceHooks.CallGetResourceLimit(e, res, limit);
        if (limit > HardLimit) limit = HardLimit;
        return limit;
    }

    /// <summary>SetResource(e, res_type, amount) — clamps to the resource limit (waste is dropped).</summary>
    public static void SetResource(this Entity e, ResourceType res, float amount)
    {
        // QC: MUTATOR_CALLHOOK(SetResource, …) may forbid the change or rewrite the amount.
        if (ResourceHooks.CallSetResource(e, res, ref amount)) return;

        float max = GetResourceLimit(e, res);
        if (max != LimitNone && amount > max)
        {
            // QC: the excess over the cap is wasted — fire the ResourceWasted hook with the dropped amount.
            ResourceHooks.CallResourceWasted(e, res, amount - max);
            amount = max;
        }
        if (SetResourceExplicit(e, res, amount))
            ResourceHooks.CallResourceAmountChanged(e, res, amount); // QC ResourceAmountChanged
    }

    /// <summary>GiveResource(receiver, res_type, amount) — common/resources/sv_resources.qc. No-op for amount &lt;= 0.</summary>
    public static void GiveResource(this Entity e, ResourceType res, float amount)
    {
        if (amount <= 0f) return;
        // QC: MUTATOR_CALLHOOK(GiveResource, …) may rewrite the amount (e.g. resistance/vampire buffs).
        amount = ResourceHooks.CallGiveResource(e, res, amount);
        if (amount <= 0f) return;
        SetResource(e, res, GetResource(e, res) + amount);
    }

    /// <summary>GiveResourceWithLimit(receiver, res_type, amount, limit) — caps the *post-give* total at limit.</summary>
    public static void GiveResourceWithLimit(this Entity e, ResourceType res, float amount, float limit)
    {
        if (amount <= 0f) return;
        float current = GetResource(e, res);
        if (limit != LimitNone && current + amount > limit)
            amount = limit - current;
        GiveResource(e, res, amount);
    }

    /// <summary>TakeResource(receiver, res_type, amount) — common/resources/sv_resources.qc.</summary>
    public static void TakeResource(this Entity e, ResourceType res, float amount)
    {
        if (amount <= 0f) return;
        SetResource(e, res, GetResource(e, res) - amount);
    }

    private static float Cvar(string name, float fallback)
    {
        if (Api.Services is null) return fallback;
        float v = Api.Cvars.GetFloat(name);
        return v != 0f ? v : fallback;
    }
}
