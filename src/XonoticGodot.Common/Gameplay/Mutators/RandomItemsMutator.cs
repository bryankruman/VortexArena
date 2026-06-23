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
///  - the full item-type probability machinery (<see cref="RandomItemType"/> +
///    <see cref="GetRandomItemClassName"/> / <see cref="GetRandomVanillaItemClassName"/>): a weighted pick over
///    the configured per-type probability cvars, then a per-item pick within the chosen type — faithfully mirroring
///    RandomItems_GetRandomVanillaItemClassName over ALL five types (health/armor/resource/weapon/powerup), each
///    resolved against the live <see cref="Items"/> / <see cref="Weapons"/> registries using the canonical
///    spawnfunc classnames (<see cref="ItemSpawnFuncs.CanonicalSpawnFunc"/>);
///  - the PlayerDies loot drop (<c>g_random_loot</c>): spawn floor(min + random()*max) loot items at the corpse,
///    each a random classname launched on a random spread, as a real touch-pickupable loot pickup via
///    <see cref="StartItem.SpawnLoot"/> (the same path the thrown-weapon / Overkill drops use).
///
/// BLOCKER (documented partial — map item-entity replacement): the FilterItem / ItemTouched hooks
/// (RandomItems_ReplaceMapItem) and the per-respawn re-randomization need an entity-level FilterItem mutator-hook
/// chain + an ItemTouch hook chain, neither of which exists in the port yet (the only item filter is the
/// definition-level FilterItemDefinition, which cannot swap one classname for another, and ItemPickupRules.ItemTouch
/// has no mutator dispatch). So the MAP-replacement half (<c>g_random_items</c>) is still inert; it is flagged in
/// the cross-task TODOs. The LOOT half (<c>g_random_loot</c>) is fully live + faithful. The class-name selection
/// engine is fully ported, so the map half gets correct classnames the day those hook seams land.
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
    /// position with a random spread velocity, as a real touch-pickupable loot item that despawns after
    /// <see cref="LootTime"/> seconds. Resolves the chosen classname to its <see cref="Pickup"/> def and spawns it
    /// through <see cref="StartItem.SpawnLoot"/> (QC <c>Item_Initialise</c> with lifetime &gt;= 0) — the same loot
    /// path the thrown-weapon / Overkill drops use, so the dropped item is MOVETYPE_TOSS and collectable on touch.
    /// </summary>
    private void SpawnLootItem(Vector3 position)
    {
        if (Api.Services is null) return;
        string className = GetRandomItemClassName("random_loot");
        if (string.IsNullOrEmpty(className)) return;

        Pickup? def = ResolvePickup(className);
        if (def is null) return; // QC SPAWNFUNC_BODY else-branch: nothing to spawn.

        // QC: spread.z = spread/2; spread += randomvec() * spread.
        Vector3 spread = new(0f, 0f, LootSpread / 2f);
        spread += Prandom.Vec() * LootSpread;

        Entity item = Api.Entities.Spawn();
        Api.Entities.SetOrigin(item, position);
        item.Velocity = spread;

        // QC item.lifetime = autocvar_g_random_loot_time; Item_Initialise (loot path: MOVETYPE_TOSS, touch-pickup,
        // despawn after lifetime). SpawnLoot owns the toss/despawn/touch; on a failed spawn it removes the edict.
        StartItem.SpawnLoot(item, def, LootTime);
    }

    /// <summary>
    /// Resolve a random-items classname (QC <c>m_canonical_spawnfunc</c>: <c>weapon_*</c> or <c>item_*</c>) back to
    /// its <see cref="Pickup"/> def for <see cref="StartItem.SpawnLoot"/>. Weapon classnames map through the synthetic
    /// per-weapon <see cref="WeaponPickup"/> (<see cref="ItemSpawnFuncs.PickupFor"/>); item classnames are matched by
    /// scanning the live <see cref="Items"/> registry for the def whose canonical spawnfunc equals the classname.
    /// </summary>
    private static Pickup? ResolvePickup(string className)
    {
        if (className.StartsWith("weapon_", StringComparison.Ordinal))
        {
            Weapon? w = Weapons.ByName(className["weapon_".Length..]);
            return w is null ? null : ItemSpawnFuncs.PickupFor(w);
        }
        foreach (Pickup def in Items.All)
        {
            if (def.IsWeaponPickup) continue;
            if (ItemSpawnFuncs.CanonicalSpawnFunc(def) == className)
                return def;
        }
        return null;
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
    /// within that type; on an empty type, drop it from the mask and retry — matching the QC loop. All five types
    /// (health/armor/resource/weapon/powerup) resolve against the live <see cref="Items"/> / <see cref="Weapons"/>
    /// registries, keyed by the canonical spawnfunc classname.
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

            string className = chosenType == RandomItemType.Weapon
                ? GetRandomWeaponClassName(prefix)
                : GetRandomItemClassNameWithProperty(prefix, chosenType);

            if (className != "") return className;
            types &= ~chosenType;            // QC: types &= ~item_type;
            if (chosenType == RandomItemType.None) break; // no probable type at all → stop (avoid infinite loop)
        }
        return "";
    }

    /// <summary>
    /// Port of the WEAPON case of RandomItems_GetRandomVanillaItemClassName: weighted pick over the non-mutatorblocked
    /// weapons by <c>g_{prefix}_{m_canonical_spawnfunc}_probability</c> (the canonical spawnfunc is <c>weapon_{netname}</c>),
    /// returning that canonical classname.
    /// </summary>
    private string GetRandomWeaponClassName(string prefix)
    {
        if (Api.Services is null) return "";
        string chosen = "";
        float total = 0f;
        foreach (Weapon w in Weapons.All)
        {
            if ((w.SpawnFlags & WeaponFlags.MutatorBlocked) != 0) continue;
            string spawnFunc = "weapon_" + w.NetName; // QC it.m_canonical_spawnfunc
            float prob = Api.Cvars.GetFloat($"g_{prefix}_{spawnFunc}_probability");
            if (prob <= 0f) continue;
            total += prob;
            if (Prandom.Float() * total <= prob) chosen = spawnFunc;
        }
        return chosen;
    }

    /// <summary>
    /// Port of <c>RandomItems_GetRandomItemClassNameWithProperty(prefix, item_property)</c>: weighted pick over the
    /// <see cref="Items"/> registry restricted to the def matching <paramref name="type"/> (health/armor/resource/
    /// powerup), normal-spawnflag and allowed (QC <c>Item_IsDefinitionAllowed</c>), keyed by
    /// <c>g_{prefix}_{m_canonical_spawnfunc}_probability</c>, returning that canonical classname.
    /// </summary>
    private string GetRandomItemClassNameWithProperty(string prefix, RandomItemType type)
    {
        if (Api.Services is null) return "";
        string chosen = "";
        float total = 0f;
        foreach (Pickup def in Items.All)
        {
            if (!MatchesType(def, type)) continue;
            // QC: (it.spawnflags & ITEM_FLAG_NORMAL) && Item_IsDefinitionAllowed(it).
            if ((def.ItemDef.SpawnFlags & GameItemSpawnFlag.Normal) == 0) continue;
            if (!def.ItemDef.IsAllowed) continue;
            string spawnFunc = ItemSpawnFuncs.CanonicalSpawnFunc(def); // QC it.m_canonical_spawnfunc
            float prob = Api.Cvars.GetFloat($"g_{prefix}_{spawnFunc}_probability");
            if (prob <= 0f) continue;
            total += prob;
            if (Prandom.Float() * total <= prob) chosen = spawnFunc;
        }
        return chosen;
    }

    // QC the instanceOf* predicate selected per type in the WithProperty switch (Health/Armor/Ammo/Powerup).
    private static bool MatchesType(Pickup def, RandomItemType type) => type switch
    {
        RandomItemType.Health => def.IsHealth,
        RandomItemType.Armor => def.IsArmor,
        RandomItemType.Resource => def.IsAmmo,
        RandomItemType.Powerup => def.IsPowerup && !def.IsWeaponPickup,
        _ => false,
    };

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
}
