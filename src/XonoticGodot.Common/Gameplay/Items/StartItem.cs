// Port of StartItem (qcsrc/server/items/items.qc:1007) + Item_Reset (items.qc:783) — the spawn driver every
// item_*/weapon_* funnels through. Seeds the world edict from a Pickup def: runs the per-item ItemInit, sets
// the IT_* items mask + weapon set, the FL_ITEM flag, the model + bbox, then branches on loot vs permanent:
//   LOOT       — MOVETYPE_TOSS, gravity, Item_Think despawn at g_items_dropped_lifetime, anti-instant-pick
//                spawnshield, takedamage, EXPIRING timer, NODROP-brush kill.
//   PERMANENT  — have_pickup_item gate (delete if mutator-blocked / g_pickup_items==0), respawntime default,
//                spawnflags&1/noalign suspension, drop-to-floor, the ###item### findnearest target.
// Tail: settouch(Item_Touch), Item_Reset (powerups + superweapons route to ScheduleInitialRespawn so they do
// NOT spawn at match start). The FilterItem / Item_Spawn / waypoint / Net_LinkEntity hooks are CSQC/mutator
// networking and are out of scope; the spawn STATE + the touch wiring are ported faithfully.

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The world-item spawn driver — QC <c>StartItem</c> + <c>Item_Reset</c>. <see cref="ItemSpawnFuncs"/> resolves
/// a classname to a <see cref="Pickup"/> def and calls <see cref="Spawn"/>; loot producers (thrown weapons,
/// death drops) call <see cref="SpawnLoot"/>. Static + stateless.
/// </summary>
public static class StartItem
{
    /// <summary>
    /// True if the LAST <see cref="Spawn"/> failed (QC <c>startitem_failed</c>): the item was deleted (mutator-
    /// blocked, g_pickup_items==0, or in a NODROP brush). The spawnfunc reads this to know whether to keep the edict.
    /// </summary>
    public static bool LastSpawnFailed { get; private set; }

    private static float Now => Api.Services != null ? Api.Clock.Time : 0f;

    /// <summary>
    /// Host seam: the engine time the match begins (QC <c>game_starttime</c>), so the initial powerup/superweapon
    /// respawn is offset past the countdown. The orchestrator wires this to GameWorld.GameStartTime; unset = 0
    /// (immediate, the headless default).
    /// </summary>
    public static System.Func<float>? GameStartTimeProvider;

    private static float GameStartTime => GameStartTimeProvider?.Invoke() ?? 0f;

    // =====================================================================================
    //  StartItem(this, def) — the spawn driver (items.qc:1007)
    // =====================================================================================

    /// <summary>
    /// QC <c>StartItem(this, def)</c>: initialise the world edict <paramref name="item"/> as a permanent map
    /// pickup of definition <paramref name="def"/>. Sets <see cref="LastSpawnFailed"/> and removes the edict on
    /// failure (mutator-blocked / g_pickup_items==0). Returns the spawned item, or null if it was deleted.
    /// </summary>
    public static Entity? Spawn(Entity item, Pickup def) => SpawnInternal(item, def, isLoot: false, lifetime: 0f);

    /// <summary>
    /// QC the loot path of StartItem (Item_Initialise with lifetime &gt;= 0): spawn <paramref name="def"/> as a
    /// dropped/tossed loot item at <paramref name="item"/> — MOVETYPE_TOSS, despawning after
    /// <paramref name="lifetime"/> seconds (0 = g_items_dropped_lifetime default), anti-instant-pick, never
    /// respawns. Used by thrown-weapon / death-drop producers. Returns the item, or null if killed (NODROP).
    /// </summary>
    public static Entity? SpawnLoot(Entity item, Pickup def, float lifetime = 0f)
        => SpawnInternal(item, def, isLoot: true, lifetime: lifetime);

    private static Entity? SpawnInternal(Entity item, Pickup def, bool isLoot, float lifetime)
    {
        LastSpawnFailed = true; // QC: early return means failure

        item.Pickup = def;
        item.ClassName = def.NetName.Length > 0 ? CanonicalClassName(def) : item.ClassName;
        item.ItemIsLoot = isLoot;

        // QC: def.m_iteminit(def, this) — seed resources / powerup timers / per-item cap (may set MUTATORBLOCKED).
        def.ItemInit(item);

        // QC: this.items = def.m_itemid; weapon set if a weapon pickup; flags = FL_ITEM | def.m_itemflags.
        item.Items = (int)def.ItemDef.ItemId;
        // (a weapon pickup's OwnedWeaponSet is seeded by WeaponPickup.ItemInit, called just above via
        // def.ItemInit — here we leave the carried weapon set as ItemInit set it.)
        item.Flags |= EntFlags.Item;

        // QC: MUTATOR_CALLHOOK(FilterItem, this) (items.qc:1031) — after the items/weapon/flags seeding (1018-1026)
        // and BEFORE the have-pickup gate (1037). Handlers inspect the def (instanceOfHealth/Armor/Powerup) and
        // return true to forbid the spawn; on true the item is deleted and the function returns early (failure).
        //
        // DEVIATION (port mechanics, not Base behavior): QC sets this.netname = def.m_name LATER (items.qc:1190),
        // AFTER the hook, because its handlers read def.instanceOf* directly. The port has no item-class registry,
        // so its FilterItem subscribers (MeleeOnly/Hook) stand in via the edict's ClassName/NetName tags — so the
        // NetName must already be live when the hook fires. ClassName is set above (line 66); assign NetName here,
        // before the hook, so those classname/netname checks see the real value. (The QC tail still re-assigns the
        // same def.m_name; here it's simply hoisted to the hook seam — same end state, no Base-behavior change.)
        item.NetName = def.NetName;
        var filterArgs = new MutatorHooks.FilterItemDefinitionArgs(item);
        if (MutatorHooks.FilterItemDefinition.Call(ref filterArgs))
        {
            // QC: delete(this); return; — the item is forbidden, so remove the edict and report the spawn failed
            // (LastSpawnFailed is already true from the top of the method). Returns null like the QC early return.
            ItemPickupRules.RemoveItem(item);
            return null;
        }

        // QC: set model + bbox BEFORE droptofloor / spawnshield (so the touch-area-grid has a real volume).
        // The defs carry the BARE model name (the QC Item_Model/W_Model macro argument); build the full VFS
        // path here or the asset loader can't find it ("not found in any mount: item_armor_large.md3").
        item.ItemWorldModel = ResolveModelPath(def);
        item.Model = item.ItemWorldModel ?? "";
        if (Api.Services is not null && !string.IsNullOrEmpty(item.Model))
            Api.Entities.SetModel(item, item.Model);
        MapMover.SetSize(item, def.Mins, def.Maxs);

        // QC: the client bob/spin animation class (server/items/items.qc:1198-1213), unless spawnflag 1024
        // (no-animate) is set. Powerups & weapons spin +180°/s and bob high (ANIMATE1); health & armor spin
        // -90°/s and bob low (ANIMATE2); ammo/keys stay static. Networked (NetEntityFlags) and driven on the
        // client (EntityNode). ANIMATE1 takes priority when both would apply (matches the client's if/else if).
        if ((item.SpawnFlags & 1024) == 0)
        {
            if (def.IsPowerup || def.IsWeaponPickup)
                item.ItemAnimate = 1;
            else if (def.IsHealth || def.IsArmor)
                item.ItemAnimate = 2;
        }

        if (isLoot)
        {
            if (!SetupLoot(item, lifetime))
                return null; // deleted (NODROP brush)
        }
        else
        {
            if (!SetupPermanent(item, def))
                return null; // deleted (mutator-blocked / g_pickup_items off)
        }

        // QC tail: settouch(Item_Touch); netname; skin; colormap for weapons; then Item_Reset (or Item_FindTeam).
        item.Touch = ItemPickupRules.ItemTouch;
        item.NetName = def.NetName; // QC this.netname = def.m_name (items.qc:1190) — already hoisted before the
                                    // FilterItem hook above; re-assigning the same value here keeps the tail faithful.

        // QC: if(team) Item_FindTeam else Item_Reset(this). The port has no team-item random-select group here;
        // a non-teamed item resets normally. (Teamed map items are a gametype concern, wired separately.)
        Item_Reset(item);

        // QC: MUTATOR_CALLHOOK(Item_Spawn, this) — no hook chain; skip. precache/setItemGroup are asset-side.

        LastSpawnFailed = false;
        return item;
    }

    /// <summary>
    /// Build the full VFS model path for a world item from the def's bare model name — the port's equivalent of
    /// QC <c>Item_Model</c> (<c>"models/items/" + m</c>, common/items/all.qc) and, for weapon pickups,
    /// <c>W_Model</c> (<c>"models/weapons/" + m</c>). The item/weapon defs store the bare macro argument
    /// (e.g. <c>item_armor_large.md3</c>, <c>v_laser.md3</c>); without the directory the asset loader reports
    /// "not found in any mount". A model that already contains a path separator is returned unchanged.
    /// (Public so the client GPU warm pass can build the IDENTICAL vpath a live spawn uses — engine-perf 2026-06-16.)
    /// </summary>
    public static string? ResolveModelPath(Pickup def)
    {
        string? m = def.Model;
        if (string.IsNullOrEmpty(m) || m.Contains('/'))
            return m;
        return (def.ItemDef.IsWeaponPickup ? "models/weapons/" : "models/items/") + m;
    }

    // QC StartItem loot branch (items.qc:1062-1098).
    private static bool SetupLoot(Entity item, float lifetime)
    {
        item.MoveType = MoveType.Toss;
        item.Gravity = 1f;

        item.Think = ItemPickupRules.ItemThink;
        item.NextThink = Now + ItemPickupRules.ItemUpdateInterval; // QC IT_UPDATE_INTERVAL
        float life = lifetime > 0f ? lifetime : ItemPickupRules.CvarOr("g_items_dropped_lifetime", 20f);
        item.ItemWait = Now + life;

        item.Owner = null;                       // anyone can pick it up...
        item.ItemSpawnShieldExpire = Now + 0.5f; // ...but not straight away (anti-instant-pick)
        item.SpawnShieldExpire = 1f;             // live marker (loot is a normal live item, just non-respawning)

        item.TakeDamage = DamageMode.Yes;        // QC: takedamage = DAMAGE_YES; event_damage = Item_Damage

        // QC: if EXPIRING, nextthink = max(strength/invincible/superweapons_finished) — the timers tick down on
        // the ground. (Set ItemIsExpiring on the edict before SpawnLoot to opt in; default off.)
        if (item.ItemIsExpiring)
        {
            float t = System.MathF.Max(item.StrengthFinished,
                System.MathF.Max(item.InvincibleFinished, item.SuperweaponsFinished));
            if (t > 0f) item.NextThink = t;
        }

        // QC: don't drop loot in a NODROP zone (lava) — traceline at the origin, delete if NODROP.
        if (Api.Services is not null)
        {
            TraceResult tr = Api.Trace.Trace(item.Origin, System.Numerics.Vector3.Zero, System.Numerics.Vector3.Zero,
                item.Origin, MoveFilter.Normal, item);
            if ((tr.DpHitContents & ItemPickupRules.NoDropContents) != 0)
            {
                ItemPickupRules.RemoveItem(item);
                return false;
            }
        }
        return true;
    }

    // QC StartItem permanent branch (items.qc:1099-1185).
    private static bool SetupPermanent(Entity item, Pickup def)
    {
        // QC: have_pickup_item(this) — delete a mutator-blocked item, or (g_pickup_items==0) any item, or
        // weaponarena weapons/ammo. Done AFTER ItemInit (which may have set MUTATORBLOCKED).
        if (!HavePickupItem(item, def))
        {
            ItemPickupRules.RemoveItem(item);
            return false;
        }

        // QC: default respawntime from def.m_respawntime if the edict didn't set one.
        if (item.ItemRespawnTime == 0f)
            item.ItemRespawnTime = def.RespawnTime;
        if (item.ItemRespawnTimeJitter == 0f)
            item.ItemRespawnTimeJitter = def.RespawnTimeJitter;

        // QC: spawnflags&1 => noalign (suspended). The port's Entity.NoAlign is a bool (set by the map "noalign"
        // key); QC's float noalign>0 (suspended) maps to true here, <=0 (drop to floor) maps to false. (The QC
        // noalign<0 "skip drop AND skip filter" case is map-rare and collapses to drop-to-floor here.)
        if ((item.SpawnFlags & 1) != 0)
            item.NoAlign = true;

        if (item.NoAlign)
        {
            item.MoveType = MoveType.None; // suspended in the air
        }
        else
        {
            item.MoveType = MoveType.Toss;
            // QC: DropToFloor_QC_DelayedInit(this) — settle onto the floor. The deterministic sim's TOSS
            // integrator settles it next frame; an explicit immediate drop isn't required for touchability
            // (the bbox is already linked at the placed origin). (Waypoint spawn is bot-nav, skipped.)
        }

        item.SpawnShieldExpire = 1f; // live marker

        // QC: target = "###item###" (findnearest) for powerups, weapons, non-small health/armor, and keys.
        bool wantsTarget = def.IsPowerup || def.IsWeaponPickup
            || (def.IsHealth && !def.IsSmall)
            || (def.IsArmor && !def.IsSmall)
            || (def.ItemDef.ItemId & (ItemFlag.Key1 | ItemFlag.Key2)) != 0;
        if (wantsTarget && string.IsNullOrEmpty(item.Target))
            item.Target = "###item###";

        return true;
    }

    // =====================================================================================
    //  Item_Reset (items.qc:783) — (re)initialise an item's visible state at spawn / map reset.
    // =====================================================================================

    /// <summary>
    /// QC <c>Item_Reset(this)</c>: a targeted (use-spawned) item starts hidden; otherwise the item is shown
    /// (or hidden) per its current state. A loot item just returns. A powerup or a superweapon weapon routes to
    /// <see cref="ItemPickupRules.ScheduleInitialRespawn"/> — they do NOT spawn at match start.
    /// </summary>
    public static void Item_Reset(Entity item)
    {
        // QC: a targetname'd item (not spawnflags&16) starts hidden, waiting to be triggered.
        if (!string.IsNullOrEmpty(item.TargetName) && (item.SpawnFlags & 16) == 0)
        {
            ItemPickupRules.Show(item, -1);
            item.NextThink = 0f;
            return;
        }

        // QC Item_Show(this, !this.state). StartItem sets this.state = 0 right before Item_Reset, so at spawn
        // !state == 1 (show). The port has no separate item .state field (the reset-infra re-call path is dormant
        // — see TargetUtilities), so the spawn path always shows; powerups are then re-hidden below.
        ItemPickupRules.Show(item, 1);

        if (item.ItemIsLoot)
            return;

        item.Think = ItemPickupRules.ItemThink;
        item.NextThink = Now;

        // QC: do NOT spawn powerups (or superweapon weapons) initially — schedule their first appearance instead.
        bool isPowerup = item.Pickup?.IsPowerup == true;
        bool isSuperWeapon = OwnsSuperWeapon(item);
        if (isPowerup || isSuperWeapon)
            ItemPickupRules.ScheduleInitialRespawn(item, GameStartTime);
    }

    // =====================================================================================
    //  helpers
    // =====================================================================================

    // QC have_pickup_item (items.qc:112).
    private static bool HavePickupItem(Entity item, Pickup def)
    {
        // QC: mutator-blocked def never spawns.
        if ((def.ItemDef.SpawnFlags & GameItemSpawnFlag.MutatorBlocked) != 0)
            return false;

        if (!def.IsPowerup)
        {
            // QC: g_pickup_items > 0 forces spawn; == 0 removes all; -1 = gametype default (proceed).
            int pickupItems = PickupItemsCvar();
            if (pickupItems > 0) return true;
            if (pickupItems == 0) return false;
            // QC weaponarena gate: no weapon/ammo pickups in an arena (the port reads g_weaponarena as a bool).
            if (Api.Services is not null && Api.Cvars.GetFloat("g_weaponarena") != 0f)
            {
                if (def.IsWeaponPickup || def.IsAmmo)
                    return false;
            }
        }
        return true;
    }

    // QC autocvar_g_pickup_items: shipped default -1 (gametype default). In a bare port with no gametype-default
    // computation, an UNSET cvar is treated as -1 (proceed), and the seeded default (Cvars.cs sets 1) forces spawn.
    private static int PickupItemsCvar()
    {
        if (Api.Services is null) return -1;
        string s = Api.Cvars.GetString("g_pickup_items");
        if (string.IsNullOrEmpty(s)) return -1;
        return (int)Api.Cvars.GetFloat("g_pickup_items");
    }

    private static bool OwnsSuperWeapon(Entity item)
    {
        foreach (var w in item.OwnedWeaponSet.Weapons())
            if (w.IsSuperWeapon) return true;
        return false;
    }

    // QC: this.classname = def.m_canonical_spawnfunc — the canonical "item_health_small" style classname. We map
    // each NetName to its spawnfunc classname; an unknown NetName keeps the def's NetName (defensive).
    private static string CanonicalClassName(Pickup def) => ItemSpawnFuncs.CanonicalSpawnFunc(def);
}
