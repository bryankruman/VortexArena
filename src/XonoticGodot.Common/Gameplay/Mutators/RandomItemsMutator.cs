// Port of common/mutators/mutator/random_items/sv_random_items.qc (+ sv_random_items.qh)

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Random Items mutator — port of common/mutators/mutator/random_items/sv_random_items.qc (Lyberta). Two
/// independent behaviors: <c>g_random_items</c> replaces every map item with a random one (and re-randomizes it
/// each respawn), and <c>g_random_loot</c> drops a handful of random loot items where a player dies. Enabled when
/// either cvar is set.
///
/// Ported here:
///  - the enable gate + the loot cvars (<c>g_random_loot_min/max/time/spread</c>);
///  - the item-type probability machinery scaffolding (<see cref="RandomItemType"/> +
///    <see cref="GetRandomItemClassName"/> / <see cref="GetRandomVanillaItemClassName"/>): a weighted pick over
///    the configured per-type probability cvars, then a per-item pick within the chosen type — faithfully mirroring
///    RandomItems_GetRandomVanillaItemClassName, restricted to the WEAPON type (the only item class the port's
///    registry currently enumerates; health/armor/resource/powerup item registries aren't enumerable here yet);
///  - the PlayerDies loot drop (<c>g_random_loot</c>): spawn floor(min + random()*max) loot items at the corpse,
///    each a random classname launched on a random spread — using the same generic item-entity drop idiom as the
///    Overkill loot drop.
///
/// BLOCKER (documented partial — map item-entity pipeline): the FilterItem / ItemTouched hooks
/// (RandomItems_ReplaceMapItem) and the per-respawn re-randomization need a map item-entity spawn pipeline
/// (Item_CopyFields / Item_Initialise / Item_ScheduleRespawn), which does not exist in the port — MapObjectsRegistry
/// registers no <c>item_*</c>/<c>weapon_*</c> spawnfuncs and nothing spawns world item entities from the map. So
/// the MAP-replacement half is inert (no items spawn to replace); it is flagged here and in crossTaskNeeds and
/// reactivates once an item pipeline lands. The LOOT half works against the generic item-entity drop, so it is
/// the live, faithful part. The class-name selection scaffolding is fully ported so both halves get correct
/// classnames the day the pipeline exists.
/// </summary>
[Mutator]
public sealed class RandomItemsMutator : MutatorBase
{
    /// <summary>QC RANDOM_ITEM_TYPE_* (sv_random_items.qh:11) — the item categories a random pick chooses among.</summary>
    [Flags]
    public enum RandomItemType
    {
        None = 0,
        Health = 1 << 0,
        Armor = 1 << 1,
        Resource = 1 << 2,
        Weapon = 1 << 3,
        Powerup = 1 << 4,
        All = (1 << 5) - 1,   // QC RANDOM_ITEM_TYPE_ALL = BITS(5)
    }

    /// <summary>QC autocvar_g_random_items.</summary>
    public bool RandomItems;

    /// <summary>QC autocvar_g_random_loot.</summary>
    public bool RandomLoot;

    /// <summary>QC autocvar_g_random_loot_min.</summary>
    public float LootMin;

    /// <summary>QC autocvar_g_random_loot_max.</summary>
    public float LootMax;

    /// <summary>QC autocvar_g_random_loot_time — seconds the dropped loot stays.</summary>
    public float LootTime = 20f;

    /// <summary>QC autocvar_g_random_loot_spread — how far loot can be thrown.</summary>
    public float LootSpread = 200f;

    public RandomItemsMutator() => NetName = "random_items";

    // QC: REGISTER_MUTATOR(random_items, (autocvar_g_random_items || autocvar_g_random_loot)).
    public override bool IsEnabled =>
        Api.Services is not null
        && (Api.Cvars.GetFloat("g_random_items") != 0f || Api.Cvars.GetFloat("g_random_loot") != 0f);

    private HookHandler<MutatorHooks.PlayerDiesArgs>? _onPlayerDies;

    public override void Hook()
    {
        _onPlayerDies ??= OnPlayerDies;
        MutatorHooks.PlayerDies.Add(_onPlayerDies, HookOrder.Last); // QC CBC_ORDER_LAST

        if (Api.Services is not null)
        {
            RandomItems = Api.Cvars.GetFloat("g_random_items") != 0f;
            RandomLoot = Api.Cvars.GetFloat("g_random_loot") != 0f;
            LootMin = Api.Cvars.GetFloat("g_random_loot_min");
            LootMax = Api.Cvars.GetFloat("g_random_loot_max");
            float lt = Api.Cvars.GetFloat("g_random_loot_time");
            if (lt != 0f) LootTime = lt;
            float ls = Api.Cvars.GetFloat("g_random_loot_spread");
            if (ls != 0f) LootSpread = ls;
        }

        // NOTE (deferred): QC also subscribes FilterItem + ItemTouched (RandomItems_ReplaceMapItem) — wired only
        // once a map item-entity spawn pipeline exists (see the class doc / crossTaskNeeds). Nothing to add here.
    }

    public override void Unhook()
    {
        if (_onPlayerDies is not null) MutatorHooks.PlayerDies.Remove(_onPlayerDies);
    }

    // MUTATOR_HOOKFUNCTION(random_items, PlayerDies)
    private bool OnPlayerDies(ref MutatorHooks.PlayerDiesArgs args)
    {
        if (!RandomLoot) return false; // QC: if (!autocvar_g_random_loot) return;
        if (Api.Services is null) return false;

        Entity victim = args.Target;
        Vector3 lootPos = victim.Origin + new Vector3(0f, 0f, 32f);
        // QC: floor(min + random() * max).
        int num = (int)MathF.Floor(LootMin + Prandom.Float() * LootMax);
        for (int i = 0; i < num; i++)
            SpawnLootItem(lootPos);
        return false;
    }

    /// <summary>
    /// Port of <c>RandomItems_SpawnLootItem(position)</c>: pick a random loot classname and spawn it at the
    /// position with a random spread velocity, scheduled to expire after <see cref="LootTime"/>. Uses the generic
    /// item-entity drop (the same idiom as the Overkill loot drop) since the full Item_Initialise pipeline is absent.
    /// </summary>
    private void SpawnLootItem(Vector3 position)
    {
        if (Api.Services is null) return;
        string className = GetRandomItemClassName("random_loot");
        if (string.IsNullOrEmpty(className)) return;

        // QC: spread.z = spread/2; spread += randomvec() * spread.
        Vector3 spread = new(0f, 0f, LootSpread / 2f);
        spread += Prandom.Vec() * LootSpread;

        Entity item = Api.Entities.Spawn();
        item.ClassName = className;
        item.NetName = ClassNameToItemName(className);
        if (Api.Services is not null) Api.Entities.SetOrigin(item, position);
        else item.Origin = position;
        item.Velocity = spread;
        item.MoveType = MoveType.Toss;
        item.Solid = Solid.Trigger;
        item.Flags |= EntFlags.Item;
        // QC item.lifetime = autocvar_g_random_loot_time — schedule expiry (the item pipeline owns the real timer).
        item.NextThink = Api.Clock.Time + LootTime;
        item.Think = self => Api.Entities.Remove(self);
    }

    /// <summary>
    /// Port of <c>RandomItems_GetRandomItemClassName(prefix)</c>: the public entry. QC first fires the
    /// RandomItems_GetRandomItemClassName mutator hook (for mods to inject classnames) then falls back to the
    /// vanilla pick over ALL types. The mod-injection hook isn't modeled (no subscriber in the port), so this is
    /// the vanilla pick.
    /// </summary>
    public string GetRandomItemClassName(string prefix) =>
        GetRandomVanillaItemClassName(prefix, RandomItemType.All);

    /// <summary>
    /// Port of <c>RandomItems_GetRandomVanillaItemClassName(prefix, types)</c>: weighted-pick an item TYPE from the
    /// configured per-type probability cvars (<c>g_{prefix}_{type}_probability</c>), then pick a concrete classname
    /// within that type; on an empty type, drop it from the mask and retry — matching the QC loop.
    ///
    /// NOTE: only the WEAPON type resolves to concrete classnames here (the port's weapon registry is enumerable;
    /// the health/armor/resource/powerup item registries are not exposed to a mutator yet). The probability machinery
    /// for all five types is ported, so the day those registries are enumerable this fills in for free.
    /// </summary>
    public string GetRandomVanillaItemClassName(string prefix, RandomItemType types)
    {
        if (types == RandomItemType.None) return "";

        while (types != RandomItemType.None)
        {
            // Weighted reservoir over the present types by their probability cvar (QC RandomSelection_AddFloat).
            RandomItemType chosenType = RandomItemType.None;
            float total = 0f;
            foreach (RandomItemType t in EachType(types))
            {
                float prob = Api.Services is null ? 0f : Api.Cvars.GetFloat(ProbCvar(prefix, t));
                if (prob <= 0f) continue;
                total += prob;
                if (Prandom.Float() * total <= prob) chosenType = t;
            }

            string className = "";
            if (chosenType == RandomItemType.Weapon)
            {
                // QC: weighted pick over the non-mutatorblocked weapons by g_{prefix}_{spawnfunc}_probability.
                Weapon? chosenWep = null;
                float wtotal = 0f;
                foreach (Weapon w in Weapons.All)
                {
                    if ((w.SpawnFlags & WeaponFlags.MutatorBlocked) != 0) continue;
                    float prob = Api.Services is null ? 0f : Api.Cvars.GetFloat($"g_{prefix}_{w.NetName}_probability");
                    if (prob <= 0f) continue;
                    wtotal += prob;
                    if (Prandom.Float() * wtotal <= prob) chosenWep = w;
                }
                if (chosenWep is not null) className = "weapon_" + chosenWep.NetName;
            }
            // else: health/armor/resource/powerup — the item registry isn't enumerable from a mutator yet (NOTE);
            // those types resolve to "" and are dropped from the mask below, exactly as an empty type would in QC.

            if (className != "") return className;
            types &= ~chosenType;            // QC: types &= ~item_type;
            if (chosenType == RandomItemType.None) break; // no probable type at all → stop (avoid infinite loop)
        }
        return "";
    }

    /// <summary>QC <c>sprintf("g_%s_%s_probability", prefix, type)</c> — the per-type probability cvar name.</summary>
    private static string ProbCvar(string prefix, RandomItemType t) => t switch
    {
        RandomItemType.Health => $"g_{prefix}_health_probability",
        RandomItemType.Armor => $"g_{prefix}_armor_probability",
        RandomItemType.Resource => $"g_{prefix}_resource_probability",
        RandomItemType.Weapon => $"g_{prefix}_weapon_probability",
        RandomItemType.Powerup => $"g_{prefix}_powerup_probability",
        _ => "",
    };

    private static IEnumerable<RandomItemType> EachType(RandomItemType mask)
    {
        if ((mask & RandomItemType.Health) != 0) yield return RandomItemType.Health;
        if ((mask & RandomItemType.Armor) != 0) yield return RandomItemType.Armor;
        if ((mask & RandomItemType.Resource) != 0) yield return RandomItemType.Resource;
        if ((mask & RandomItemType.Weapon) != 0) yield return RandomItemType.Weapon;
        if ((mask & RandomItemType.Powerup) != 0) yield return RandomItemType.Powerup;
    }

    /// <summary>Strip the "weapon_"/"item_" classname prefix to the bare item NetName (best-effort, for networking).</summary>
    private static string ClassNameToItemName(string className)
    {
        if (className.StartsWith("weapon_", StringComparison.Ordinal)) return className["weapon_".Length..];
        if (className.StartsWith("item_", StringComparison.Ordinal)) return className["item_".Length..];
        return className;
    }
}
