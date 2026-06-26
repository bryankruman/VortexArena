// Server-side item pickup + respawn — the C# successor to qcsrc/server/items/items.qc:
//   Item_GiveTo / Item_GiveAmmoTo (the type-agnostic give every item funnels through — QC Pickup.giveTo just
//   calls Item_GiveTo), Item_Touch (the FL_PICKUPITEMS / IS_DEAD / SOLID_TRIGGER / owner / spawnshield gate +
//   the give+respawn tail), Item_Show (the full model/solid/effects/STAYWEP + weapon-stay translucent path),
//   Item_Think (loot despawn), and the respawn scheduling (Item_ScheduleRespawn[In] / ScheduleInitialRespawn /
//   Respawn / RespawnThink / RespawnCountdown, with the player-count scaling + jitter).
//
// CSQC networking (the ENT_CLIENT_ITEM SendFlags, the waypoint-sprite countdown ping/sound) is out of scope;
// the respawn *timers*, the give *logic*, and the touch *gate* are ported faithfully. Resource gives flow
// through Resources.cs; weapon gives populate BOTH Entity.OwnedWeaponSet (Inventory) AND Player.OwnedWeapons
// (the NetName set — dual-rep).

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The item pickup + respawn rules (QC server/items/items.qc). Static, stateless apart from the shared
/// initial-respawn random (QC's <c>static float shared_random</c>); operates on world item entities and
/// the toucher.
/// </summary>
public static class ItemPickupRules
{
    /// <summary>QC ITEM_RESPAWN_TICKS — the waypoint countdown window before a long-respawn item reappears.</summary>
    public const float RespawnTicks = 10f;

    /// <summary>
    /// QC <c>IT_DESPAWNFX_TIME</c> (common/items/item.qh = 1.5) — the trailing window of a loot item's life during
    /// which it flags <c>ITS_EXPIRING</c> so the client plays the despawn animation (alpha fade + the accelerating
    /// <c>EFFECT_ITEM_DESPAWN</c> puffs). "enough to notice it's about to despawn and circle jump to grab it."
    /// </summary>
    public const float DespawnFxTime = 1.5f;

    /// <summary>QC DPCONTENTS_NODROP (the SUPERCONTENTS bit). Loot in a NODROP brush (lava) is deleted at spawn.</summary>
    public const int NoDropContents = unchecked((int)0x80000000);

    private static float Now => Api.Services != null ? Api.Clock.Time : 0f;

    /// <summary>
    /// QC Item_ScheduleInitialRespawn's <c>static float shared_random</c> — for the default
    /// g_pickup_respawntime_initial_random==1 mode, every powerup scheduled in the same second draws the SAME
    /// random so they all appear at one synchronized time. Holds <c>floor(time) + random()</c>; the integer part
    /// is the floor(time) it was seeded at, so it is reused until floor(time) advances past it. Process-static
    /// to mirror the QC function-static lifetime.
    /// </summary>
    private static float _initialRespawnSharedRandom;

    // =====================================================================================
    //  Item_GiveTo — the type-agnostic give (items.qc:522). Reads the world item's resources / weapons /
    //  powerup timers / held-item flags and applies them to the player. Returns true if anything was taken.
    //  QC's per-item Pickup.giveTo just calls THIS, so there is one give path for every item kind.
    // =====================================================================================

    /// <summary>
    /// QC <c>Item_GiveTo(item, player)</c>: give the world item <paramref name="worldItem"/>'s contents to
    /// <paramref name="player"/> — the 7 resource gives (health/armor/shells/bullets/rockets/cells/fuel via
    /// <see cref="GiveAmmoTo"/>), the weapon block (each missing weapon granted, dual-rep), the IT_PICKUPMASK
    /// held-item transfer, and the powerup status-effect timers. Returns true if the player benefited
    /// (QC <c>pickedup</c>). Does NOT play the pickup sound or schedule the respawn — that is the Item_Touch tail.
    /// </summary>
    public static bool ItemGiveTo(Entity worldItem, Entity player)
    {
        int pickupAnyway = System.Math.Max(worldItem.PickupAnyway, worldItem.Pickup?.ItemDef.PickupAnyway ?? 0);
        bool pickedUp = false;

        // QC Item_GiveTo head (items.qc:525-545): if the player has cl_autoswitch on (and CTS-autoswitch is not
        // overriding it), remember whether they're currently on their best weapon so we can re-pick a better one
        // AFTER the give. Captured before any weapon is granted. CTS uses a separate forced-switch policy handled
        // in the tail. The port is single-slot (no MAX_WEAPONSLOTS loop) — slot 0 is the whole decision.
        bool useCtsAutoswitch = IsCtsActive() && worldItem.Pickup?.IsWeaponPickup == true && CtsAutoswitchCvar(player) != -1;
        bool wantSwitch = false;
        if (AutoswitchEnabled(player) && !useCtsAutoswitch)
        {
            int switchId = player.SwitchWeaponId >= 0 ? player.SwitchWeaponId : player.ActiveWeaponId;
            Weapon? best = Inventory.GetBestWeapon(player);
            // QC: switch if their switchweapon already IS the best (a better one may now exist), OR they somehow
            // don't even own their switchweapon.
            if (best is not null && switchId == best.RegistryId)
                wantSwitch = true;
            if (switchId < 0 || !player.OwnedWeaponSet.Has(switchId))
                wantSwitch = true;
        }

        // QC: the seven Item_GiveAmmoTo calls (resource limits from the per-item caps + the pickup-max cvars).
        pickedUp |= GiveAmmoTo(worldItem, player, ResourceType.Health, worldItem.GetResource(ResourceType.Health),
            worldItem.MaxHealth, pickupAnyway);
        pickedUp |= GiveAmmoTo(worldItem, player, ResourceType.Armor, worldItem.GetResource(ResourceType.Armor),
            worldItem.MaxArmorValue, pickupAnyway);
        pickedUp |= GiveAmmoTo(worldItem, player, ResourceType.Shells, worldItem.GetResource(ResourceType.Shells),
            CvarOr("g_pickup_shells_max", 60f), pickupAnyway);
        pickedUp |= GiveAmmoTo(worldItem, player, ResourceType.Bullets, worldItem.GetResource(ResourceType.Bullets),
            CvarOr("g_pickup_nails_max", 320f), pickupAnyway);
        pickedUp |= GiveAmmoTo(worldItem, player, ResourceType.Rockets, worldItem.GetResource(ResourceType.Rockets),
            CvarOr("g_pickup_rockets_max", 160f), pickupAnyway);
        pickedUp |= GiveAmmoTo(worldItem, player, ResourceType.Cells, worldItem.GetResource(ResourceType.Cells),
            CvarOr("g_pickup_cells_max", 180f), pickupAnyway);
        pickedUp |= GiveAmmoTo(worldItem, player, ResourceType.Fuel, worldItem.GetResource(ResourceType.Fuel),
            CvarOr("g_pickup_fuel_max", 100f), pickupAnyway);

        // QC weapon block: grant each weapon the player lacks (or re-grant when pickup_anyway + live).
        if (worldItem.Pickup?.IsWeaponPickup == true)
            pickedUp |= InventoryPickupItem(player, worldItem, worldItem.OwnedWeaponSet, pickupAnyway);

        // QC powerup-item center-prints (items.qc:581-587): a FuelRegen / Jetpack powerup the player doesn't yet
        // hold announces itself. QC keys these off the itemdef AND !(player.items & flag); the held-flag check is
        // exactly "this flag is newly granted", so we fold it into the IT_PICKUPMASK transfer below (its carries
        // only the not-yet-held flags). Done before the transfer so player.Items still lacks the flag here.
        if (worldItem.Pickup?.IsPowerup == true)
        {
            int newFlags = worldItem.Items & ~player.Items;
            if ((newFlags & (int)ItemFlag.FuelRegen) != 0)
                NotificationSystem.Center(player, "ITEM_FUELREGEN_GOT");
            else if ((newFlags & (int)ItemFlag.Jetpack) != 0)
                NotificationSystem.Center(player, "ITEM_JETPACK_GOT");
        }

        // QC IT_PICKUPMASK transfer: the held-item flags this item grants that the player lacks (jetpack /
        // fuelregen / unlimited*). strength/invincible are NOT in the mask — they go through the timers below.
        int its = worldItem.Items & ~player.Items & (int)ItemFlag.PickupMask;
        if (its != 0)
        {
            player.Items |= its;
            pickedUp = true;
        }

        // QC powerup timers (strength/invincible/speed/invisibility/superweapon -> StatusEffects_apply).
        pickedUp |= ApplyPowerupTimers(player, worldItem);

        // QC: always eat teamed entities (if(item.team) pickedup = true).
        if (worldItem.Team != 0f) pickedUp = true;

        if (!pickedUp)
            return false;

        // QC Item_GiveTo tail (items.qc:652-681): now that the give happened, perform the autoswitch.
        if (useCtsAutoswitch)
        {
            // CTS handling: cl_autoswitch_cts 1 = always switch to the picked-up weapon (if usable), -1/0 handled
            // above (use_cts_autoswitch false / never switch). Crude force-switch like QC.
            if (CtsAutoswitchCvar(player) == 1)
            {
                foreach (var w in worldItem.OwnedWeaponSet.Weapons())
                {
                    if (Inventory.ClientHasWeapon(player, w, andAmmo: true, complain: false))
                        Inventory.SwitchWeapon(player, w); // W_SwitchWeapon_Force
                }
            }
        }
        else if (wantSwitch)
        {
            // non-CTS: weaponpriority-based autoswitch to the (possibly new) best weapon.
            Weapon? best = Inventory.GetBestWeapon(player);
            int curSwitch = player.SwitchWeaponId >= 0 ? player.SwitchWeaponId : player.ActiveWeaponId;
            if (best is not null && curSwitch != best.RegistryId)
                Inventory.SwitchWeapon(player, best); // W_SwitchWeapon_Force
        }

        return true;
    }

    // =====================================================================================
    //  Autoswitch seams (QC CS_CVAR(player).cvar_cl_autoswitch / cvar_cl_autoswitch_cts + g_cts). These are
    //  per-client replicated cvars + a gametype query the host owns; mirror the Inventory.PriorityProvider
    //  pattern so the listen/local path works headless (no autoswitch) and the server wires the real source.
    // =====================================================================================

    /// <summary>
    /// QC <c>CS_CVAR(player).cvar_cl_autoswitch</c> — the per-client auto-weapon-switch flag. Set by the server's
    /// Commands (it owns the replicated per-client cvar table); null = no provider wired (headless/local), in
    /// which case autoswitch is off (matching a player who never replicated the cvar). Takes the entity so a
    /// future multi-actor host can disambiguate.
    /// </summary>
    public static Func<Entity, bool>? AutoswitchProvider;

    /// <summary>
    /// QC <c>CS_CVAR(player).cvar_cl_autoswitch_cts</c> — the per-client CTS autoswitch override (-1 = unset/use
    /// the normal autoswitch, 0 = never switch, 1 = always switch to the picked-up weapon). Null provider yields
    /// -1 (no override). Wired by the server alongside <see cref="AutoswitchProvider"/>.
    /// </summary>
    public static Func<Entity, int>? CtsAutoswitchProvider;

    /// <summary>QC <c>g_cts</c> — is the CTS gametype active? Null provider = false (the common DM/other case).</summary>
    public static Func<bool>? CtsActiveProvider;

    private static bool AutoswitchEnabled(Entity player) => AutoswitchProvider?.Invoke(player) ?? false;
    private static int CtsAutoswitchCvar(Entity player) => CtsAutoswitchProvider?.Invoke(player) ?? -1;
    private static bool IsCtsActive() => CtsActiveProvider?.Invoke() ?? false;

    /// <summary>
    /// Legacy entry retained for callers that pass an explicit pickup def (target_give host seam): give the
    /// world item to the player via the canonical <see cref="ItemGiveTo"/>, then — if anything was taken — play
    /// the pickup sound and schedule the respawn (the Item_Touch tail). Prefer <see cref="ItemTouch"/> for the
    /// full touch path; this is the give+tail without the FL_PICKUPITEMS gate.
    /// </summary>
    public static bool GiveTo(Pickup pickup, Entity player, Entity worldItem)
    {
        if (worldItem.Pickup is null) worldItem.Pickup = pickup;
        bool pickedUp = ItemGiveTo(worldItem, player);
        if (!pickedUp) return false;
        PlayPickupSound(worldItem, player);
        ScheduleRespawnAfterPickup(worldItem);
        return true;
    }

    // QC Item_GiveAmmoTo (items.qc:485): give a resource toward a cap, honouring the live/stay marker +
    // pickup_anyway + the g_weapon_stay==2 stay-ammo branch.
    /// <summary>
    /// Give <paramref name="amount"/> of <paramref name="res"/> to <paramref name="player"/> up to
    /// <paramref name="cap"/> (QC Item_GiveAmmoTo). When the item is LIVE (<see cref="Entity.SpawnShieldExpire"/>
    /// != 0) and the player is at/over the cap, nothing is given unless <paramref name="pickupAnyway"/> &gt; 0.
    /// When the item is a STAY weapon (marker == 0) and <c>g_weapon_stay==2</c>, the cap collapses to the item's
    /// own amount (the stay-ammo refill); otherwise a stay weapon gives no ammo at all. Returns true if any was
    /// given. Supports a negative <paramref name="amount"/> (take) like QC.
    /// </summary>
    public static bool GiveAmmoTo(Entity worldItem, Entity player, ResourceType res, float amount, float cap,
        int pickupAnyway)
    {
        if (amount == 0f) return false;
        float playerAmount = player.GetResource(res);

        if (worldItem.SpawnShieldExpire != 0f)
        {
            // live item: refuse if already capped (unless pickup_anyway).
            if (cap != Resources.LimitNone && playerAmount >= cap && pickupAnyway <= 0)
                return false;
        }
        else if (WeaponStay == 2)
        {
            // QC g_weapon_stay==2 stay-ammo path: cap at min(item amount, cap); refuse if already that full.
            cap = System.MathF.Min(amount, cap == Resources.LimitNone ? amount : cap);
            if (playerAmount >= cap) return false;
        }
        else
        {
            // stay weapon (marker 0) and not g_weapon_stay==2: no ammo from a ghost weapon.
            return false;
        }

        if (amount < 0f)
            // QC Item_GiveAmmoTo (items.qc:507): TakeResourceWithLimit(player, res, -amount, ammomax) — the drain
            // is floored at -ammomax, matching the give branch's ceiling. `cap` is the (possibly stay-adjusted)
            // ammomax computed above, passed verbatim exactly like QC (no stock item carries a negative amount, so
            // this is a faithful-but-latent path; cap is never LimitNone here — only the give branch reaches that).
            player.TakeResourceWithLimit(res, -amount, cap);
        else
            player.GiveResourceWithLimit(res, amount, cap);
        return true;
    }

    // QC Item_GiveTo powerup block: each *_finished field on the world item applies the matching status
    // effect, stacking or refreshing per g_powerups_stack.
    private static bool ApplyPowerupTimers(Entity player, Entity item)
    {
        bool any = false;
        bool stack = Api.Services != null && Api.Cvars.GetFloat("g_powerups_stack") != 0f;

        any |= ApplyTimer(player, item.StrengthFinished, stack, StatusEffectsCatalog.ByName("strength"));
        any |= ApplyTimer(player, item.InvincibleFinished, stack, StatusEffectsCatalog.ByName("shield"));
        any |= ApplyTimer(player, item.SpeedFinished, stack, StatusEffectsCatalog.ByName("speed"));
        any |= ApplyTimer(player, item.InvisibilityFinished, stack, StatusEffectsCatalog.ByName("invisibility"));
        // QC superweapons always stack (no g_powerups_stack gate): t = existing + item.superweapons_finished.
        any |= ApplySuperweaponTimer(player, item.SuperweaponsFinished, StatusEffectsCatalog.ByName("superweapon"));
        return any;
    }

    private static bool ApplyTimer(Entity player, float duration, bool stack, StatusEffectDef? def)
    {
        if (duration <= 0f || def is null) return false;
        float existing = ExistingRemaining(player, def);
        // QC: stack => total = existing + duration; else => total = max(existing, duration). The catalog Apply
        // sets ExpireTime = now + total, so pass the resulting total duration.
        float total = stack ? existing + duration : System.MathF.Max(existing, duration);
        StatusEffectsCatalog.Apply(player, def, total);
        return true;
    }

    // QC superweapons: StatusEffects_apply(t + item.superweapons_finished) — unconditional add to the existing.
    private static bool ApplySuperweaponTimer(Entity player, float duration, StatusEffectDef? def)
    {
        if (duration <= 0f || def is null) return false;
        float existing = ExistingRemaining(player, def);
        StatusEffectsCatalog.Apply(player, def, existing + duration);
        return true;
    }

    private static float ExistingRemaining(Entity player, StatusEffectDef def)
    {
        foreach (var s in player.StatusEffects)
            if (s.DefId == def.RegistryId) return System.MathF.Max(0f, s.ExpireTime - Now);
        return 0f;
    }

    // =====================================================================================
    //  Inventory_pickupitem — weapon pickups (items.qc Item_GiveTo weapon block, via the Inventory API).
    // =====================================================================================

    /// <summary>
    /// Give the weapons carried by world item <paramref name="worldItem"/> to <paramref name="player"/> —
    /// the C# successor to the weapon block of Item_GiveTo. Each weapon the player doesn't already own is
    /// granted; with m_pickupanyway set and the item live, owned weapons are re-granted too. Populates BOTH
    /// <see cref="Entity.OwnedWeaponSet"/> (via <see cref="Inventory.GiveWeapon"/>) AND <see cref="Player.OwnedWeapons"/>
    /// (the NetName set) — the dual-rep contract. Returns true if any weapon was granted.
    /// </summary>
    public static bool InventoryPickupItem(Entity player, Entity worldItem, WepSet itemWeapons, int pickupAnyway)
    {
        WepSet have = Inventory.GetWeapons(player);
        WepSet missing = itemWeapons & Complement(have);             // wp = w & ~player.weapons

        bool grant = !missing.IsEmpty || (worldItem.SpawnShieldExpire != 0f && pickupAnyway > 0);
        if (!grant) return false;

        foreach (var w in itemWeapons.Weapons())
        {
            if (missing.Has(w) || pickupAnyway > 0)
            {
                Inventory.GiveWeapon(player, w);                     // W_GiveWeapon (writes Entity.OwnedWeaponSet)
                // dual-rep: also add to Player.OwnedWeapons (the NetName set) — QC STAT(WEAPONS) is one bitset, but
                // the port keeps the player's NetName set separately (memory: must populate BOTH).
                if (player is Player p) p.OwnedWeapons.Add(w.NetName);
                // Notify the player they got the weapon (QC Item_NotifyWeapon -> INFO_ITEM_WEAPON_GOT).
                NotificationSystem.Send(NotifBroadcast.OneOnly, player, MsgType.Info, "ITEM_WEAPON_GOT", w.RegistryId);
            }
        }
        return true;
    }

    private static WepSet Complement(WepSet s)
    {
        // WepSet has no ~ operator; build the complement over the 64-bit space via the known weapon ids.
        var result = new WepSet();
        for (int i = 0; i < 64; i++)
            if (!s.Has(i)) result.Add(i);
        return result;
    }

    // =====================================================================================
    //  Item_Touch — the full touch gate + give + respawn tail (items.qc:686).
    // =====================================================================================

    /// <summary>
    /// QC <c>Item_Touch(this, toucher)</c>: the world-item touch handler set by <see cref="StartItem.Spawn"/>.
    /// Gate: the loot NODROP/sky-surface kill; then the FL_PICKUPITEMS / IS_DEAD / SOLID_TRIGGER /
    /// owner!=toucher / past-spawnshield checks. On pass: (optionally subtract <c>time</c> from an expiring
    /// item's timers), run the canonical <see cref="ItemGiveTo"/>, then — if anything was taken — fire the
    /// item's own targets, play the pickup sound, and either remove loot or schedule the (non-stay) respawn.
    /// </summary>
    public static void ItemTouch(Entity item, Entity toucher)
    {
        // (1) loot in a NODROP brush / on a sky surface is removed on touch (QC ITEM_TOUCH_NEEDKILL).
        if (item.ItemIsLoot && LootInNoDrop(item))
        {
            RemoveItem(item);
            return;
        }

        // (2) the QC gate: only a live, alive, non-owner picker, on a SOLID_TRIGGER item, past its spawnshield.
        if (!toucher.CanPickupItems
            || toucher.DeadState != DeadFlag.No
            || item.Solid != Solid.Trigger
            || ReferenceEquals(item.Owner, toucher)
            || Now < item.ItemSpawnShieldExpire)
        {
            return;
        }

        // (3) MUTATOR_CALLHOOK(ItemTouch, this, toucher) (items.qc:706) — fired here, after the gate and BEFORE
        //     the expiring-timer adjust + give, so a subscriber sees the item's raw powerup timers. The stock
        //     superspec hook always returns CONTINUE (never blocks the pickup), so the return is informational
        //     only and the common path proceeds regardless.
        MutatorHooks.FireItemTouch(item, toucher);

        // (4) an expiring loot item's powerup timers are stored absolute-from-now; subtract `time` so the give's
        //     max(t, time + finished) treats them as remaining (QC items.qc:714-721). Restored if nothing taken.
        bool expiring = item.ItemIsExpiring;
        if (expiring)
        {
            item.StrengthFinished = System.MathF.Max(0f, item.StrengthFinished - Now);
            item.InvincibleFinished = System.MathF.Max(0f, item.InvincibleFinished - Now);
            item.SpeedFinished = System.MathF.Max(0f, item.SpeedFinished - Now);
            item.InvisibilityFinished = System.MathF.Max(0f, item.InvisibilityFinished - Now);
            item.SuperweaponsFinished = System.MathF.Max(0f, item.SuperweaponsFinished - Now);
        }

        bool gave = ItemGiveTo(item, toucher);
        if (!gave)
        {
            if (expiring)
            {
                // undo the timer adjust (QC items.qc:725-733).
                item.StrengthFinished += Now;
                item.InvincibleFinished += Now;
                item.SpeedFinished += Now;
                item.InvisibilityFinished += Now;
                item.SuperweaponsFinished += Now;
            }
            return;
        }

        // ----- the give+respawn tail (LABEL pickup) -----

        // QC: fire the item's own targets (unless the ###item### findnearest sentinel).
        if (!string.IsNullOrEmpty(item.Target) && item.Target != "###item###")
            MapMover.UseTargets(item, toucher, null);

        PlayPickupSound(item, toucher);

        // QC client/items/items.qc: the pickup particle burst plays as the item goes unavailable — for a
        // respawning item via the ITS_AVAILABLE→off transition (:271), for loot via ISF_REMOVEFX (:338).
        // Both funnel through here, so a single emit before the loot/respawn split covers both.
        EmitItemEffect("ITEM_PICKUP", item);

        // QC (items.qc:746): MUTATOR_CALLHOOK(ItemTouched, this, toucher); if (wasfreed(this)) return; — fired
        // AFTER the give + pickup sound. random_items re-randomizes the picked-up MAP item here: it spawns a fresh
        // replacement, schedules the replacement's respawn, and deletes THIS item — so the re-check below bails.
        MutatorHooks.FireItemTouched(item, toucher);
        if (item.IsFreed)
            return;

        // QC: loot is removed (no respawn).
        if (item.ItemIsLoot)
        {
            RemoveItem(item);
            return;
        }

        // QC: a stay weapon (spawnshieldtime == 0) already gave only the weapon — no respawn.
        if (item.SpawnShieldExpire == 0f)
            return;

        // QC team-item: pick a random sibling of the same team to respawn (the others hide). The port's
        // gametype team-item handling isn't wired here; for an untemmed item just respawn this one.
        ScheduleRespawn(item);
    }

    // =====================================================================================
    //  Item_Show — the full visibility/solidity/effects toggle (items.qc:130).
    // =====================================================================================

    /// <summary>
    /// QC <c>Item_Show(e, mode)</c>: <paramref name="mode"/> &gt; 0 = available (model + SOLID_TRIGGER, live
    /// marker set), &lt; 0 = hidden (no model, SOLID_NOT), == 0 = the weapon-stay case (a weapon pickup with
    /// g_weapon_stay becomes a translucent STILL-pickable ghost with the live marker CLEARED — picking it up
    /// gives only the weapon, not ammo; everything else becomes hidden). Relinks via setorigin so the area grid
    /// updates. The live/stay marker is <see cref="Entity.SpawnShieldExpire"/> (QC <c>.spawnshieldtime</c>).
    /// </summary>
    public static void Show(Entity e, int mode)
    {
        // QC clears EF_ADDITIVE|STARDUST|FULLBRIGHT|NODEPTHTEST + ITS_STAYWEP each call. (EF_NODEPTHTEST is a
        // pure render bit not mirrored in the port's EffectFlags; the gameplay-relevant clears are these three.)
        e.Effects &= ~(EffectFlags.Additive | EffectFlags.Stardust | EffectFlags.FullBright);
        e.ItemStayWeapon = false;

        if (mode > 0)
        {
            // normal, touchable.
            e.Model = e.ItemWorldModel ?? "";
            e.Solid = Solid.Trigger;
            e.SpawnShieldExpire = 1f;
            e.ItemAvailable = true;
        }
        else if (mode < 0)
        {
            // fully hidden.
            e.Model = "";
            e.Solid = Solid.Not;
            e.SpawnShieldExpire = 1f;
            e.ItemAvailable = false;
        }
        else
        {
            // mode == 0: the weapon-stay case.
            bool isWeapon = e.Pickup?.IsWeaponPickup == true;
            // QC items.qc:153-155 has an operator-precedence quirk: `?:` binds looser than `||`, so
            //   nostay = instanceOfWeaponPickup ? (isSuperweapon) : (false || e.team)
            // For a WEAPON pickup nostay is the superweapon bit ONLY — the `|| e.team` term is bound to the
            // non-weapon branch and is unreachable here (and the whole nostay value is only consumed when
            // isWeapon is true). Mirror the shipped behavior: drop the team term so teamed non-super weapons
            // still weapon-stay (as they do in Base).
            bool nostay = isWeapon && IsSuperWeaponItem(e);
            if (isWeapon && !nostay && WeaponStay != 0)
            {
                // translucent, STILL pickable, but the live marker is CLEARED (gives only the weapon, no ammo).
                e.Model = e.ItemWorldModel ?? "";
                e.Solid = Solid.Trigger;
                e.Effects |= EffectFlags.Stardust;
                e.SpawnShieldExpire = 0f; // 0 = stay marker (no ammo, no respawn)
                e.ItemAvailable = true;
                e.ItemStayWeapon = true;
            }
            else
            {
                e.Solid = Solid.Not;
                e.SpawnShieldExpire = 1f;
                e.ItemAvailable = false;
            }
        }

        // QC: relink (solid may have changed) — setorigin(e, e.origin) updates the area grid link.
        if (Api.Services is not null)
            Api.Entities.SetOrigin(e, e.Origin);
    }

    /// <summary>Convenience: Show(item, available?1:-1). Kept for the existing respawn-scheduling call sites.</summary>
    public static void Show(Entity item, bool available) => Show(item, available ? 1 : -1);

    // =====================================================================================
    //  Item_Think — loot despawn (items.qc:192). Permanent items just relink on move.
    // =====================================================================================

    /// <summary>
    /// QC <c>Item_Think(this)</c> (server/items/items.qc:192): a loot item counts down to its
    /// <see cref="Entity.ItemWait"/> despawn time. While more than <see cref="DespawnFxTime"/> seconds of life
    /// remain it just re-ticks (never overshooting the window start, "ensuring full time for effects"); during the
    /// final <see cref="DespawnFxTime"/> seconds it raises <see cref="Entity.ItemExpiringFx"/> (QC
    /// <c>ItemStatus |= ITS_EXPIRING; SendFlags |= ISF_STATUS</c>) so the client runs the despawn animation, then
    /// hands off to <see cref="RemoveItem"/> at the wait time. A permanent item just keeps its think alive (the
    /// CSQC slow-update is delta-driven networking, handled by the snapshot system). Set as a loot item's think by
    /// <see cref="StartItem.Spawn"/>.
    /// </summary>
    public static void ItemThink(Entity item)
    {
        if (item.ItemIsLoot)
        {
            // QC items.qc:196 — still outside the despawn-fx window: re-tick at IT_UPDATE_INTERVAL but never past
            // the window start, so the client gets the full DespawnFxTime for the fade + puffs.
            if (Now < item.ItemWait - DespawnFxTime)
            {
                item.NextThink = System.MathF.Min(Now + ItemUpdateInterval, item.ItemWait - DespawnFxTime);
            }
            else
            {
                // QC items.qc:200 — despawning soon: flag ITS_EXPIRING (networked via the item snapshot's
                // NetEntityFlags.ItemExpiring, picked up by the delta compressor) so the client begins the
                // alpha fade + the accelerating EFFECT_ITEM_DESPAWN puffs (client/items/items.qc:191-210).
                item.ItemExpiringFx = true;

                if (Now < item.ItemWait - ItemUpdateInterval)
                {
                    item.NextThink = Now + ItemUpdateInterval;
                }
                else
                {
                    // QC: setthink(this, RemoveItem); nextthink = this.wait. Remove exactly at the wait time; if a
                    // late/direct think has already reached it, remove now (no spurious extra tick).
                    item.Think = RemoveItem;
                    item.NextThink = item.ItemWait;
                    if (Now >= item.ItemWait)
                        RemoveItem(item);
                }
            }
        }
        else
        {
            item.NextThink = Now;
        }
    }

    /// <summary>
    /// QC <c>IT_UPDATE_INTERVAL</c> (common/items/item.qh = 0.0625) — the loot-think cadence: "2hz probably enough
    /// to correct a desync caused by serious lag" (the comment is stale; it's 16hz). Also the boundary the despawn
    /// window's final-removal branch uses (<c>wait - IT_UPDATE_INTERVAL</c>).
    /// </summary>
    public const float ItemUpdateInterval = 0.0625f;

    // =====================================================================================
    //  Respawn scheduling (items.qc Item_ScheduleRespawn[In] / Item_ScheduleInitialRespawn).
    // =====================================================================================

    /// <summary>QC adjust_respawntime: scale the base respawn time by the player-count curve cvars.</summary>
    public static float AdjustRespawnTime(float normalRespawnTime, int playerCount)
    {
        float r = CvarOr("g_pickup_respawntime_scaling_reciprocal", 0f);
        float o = CvarOr("g_pickup_respawntime_scaling_offset", 0f);
        float l = CvarOr("g_pickup_respawntime_scaling_linear", 1f);
        if (r == 0f && l == 1f) return normalRespawnTime;
        if (playerCount >= 2) return normalRespawnTime * (r / (playerCount + o) + l);
        return normalRespawnTime;
    }

    /// <summary>
    /// Schedule <paramref name="item"/> to respawn in <paramref name="t"/> seconds (QC Item_ScheduleRespawnIn):
    /// long respawns (&gt; ITEM_RESPAWN_TICKS) start a countdown think; short ones respawn directly.
    /// </summary>
    public static void ScheduleRespawnIn(Entity item, float t)
    {
        if (t - RespawnTicks > 0f)
        {
            item.NextThink = Now + System.MathF.Max(0f, t - RespawnTicks);
            item.ScheduledRespawnTime = item.NextThink + RespawnTicks;
            item.ItemRespawnCounter = 0;
            item.Think = RespawnCountdown;
        }
        else
        {
            item.NextThink = Now;
            item.ScheduledRespawnTime = Now + t;
            item.ItemWait = Now + t;
            item.Think = RespawnThink;
        }
    }

    /// <summary>
    /// QC Item_ScheduleRespawn: hide the item and queue a respawn at base±jitter scaled by player count. A
    /// respawn time of -1 (or 0 in the port's "use default" convention) means "never respawns".
    /// </summary>
    public static void ScheduleRespawn(Entity item, int playerCount = 0)
    {
        if (item.ItemRespawnTime > 0f)
        {
            Show(item, 0); // QC Item_Show(e, 0): the weapon-stay-aware hide
            float adjusted = AdjustRespawnTime(item.ItemRespawnTime, playerCount);
            float respawnIn = adjusted + Prandom.Signed() * item.ItemRespawnTimeJitter; // crandom() == [-1,1)
            ScheduleRespawnIn(item, respawnIn);
        }
        else
        {
            Show(item, -1); // -1 => never respawns
        }
    }

    /// <summary>
    /// QC Item_ScheduleInitialRespawn: an item that doesn't start spawned (a powerup or superweapon) is hidden
    /// and queued for its first appearance after a randomized delay, offset by the game start time. Powerups
    /// route here from Item_Reset (do NOT spawn powerups at match start).
    /// </summary>
    public static void ScheduleInitialRespawn(Entity item, float gameStartTime, int playerCount = 0)
    {
        Show(item, 0);

        // QC autocvar_g_pickup_respawntime_initial_random (shipped default 1): 0 = respawntime + random*jitter;
        // 1 = ITEM_RESPAWN_TICKS .. respawntime+jitter via a SHARED random (all items scheduled the same second
        // appear together); 2 = same range but an independent random per item.
        int initialRandom = (int)CvarOr("g_pickup_respawntime_initial_random", 1f);
        float spawnIn;
        if (initialRandom == 0)
        {
            // range: respawntime .. respawntime + respawntimejitter
            spawnIn = item.ItemRespawnTime + Prandom.Float() * item.ItemRespawnTimeJitter;
        }
        else
        {
            float rnd;
            if (initialRandom == 1)
            {
                // QC: this works only if items are scheduled at the same time (the normal case). random() can't
                // return exactly 1, so floor(time) > shared_random correctly fires once floor(time) advances.
                float flo = System.MathF.Floor(Now);
                if (_initialRespawnSharedRandom == 0f || flo > _initialRespawnSharedRandom)
                    _initialRespawnSharedRandom = flo + Prandom.Float();
                rnd = _initialRespawnSharedRandom - flo;
            }
            else
            {
                rnd = Prandom.Float();
            }

            // range (prevents powerups spawning unexpectedly without waypoints):
            //   respawntime >= ITEM_RESPAWN_TICKS: ITEM_RESPAWN_TICKS .. respawntime + respawntimejitter
            //   else: 0 .. ITEM_RESPAWN_TICKS
            spawnIn = RespawnTicks + rnd * (item.ItemRespawnTime + item.ItemRespawnTimeJitter - RespawnTicks);
        }

        float delay = System.MathF.Max(0f, gameStartTime - Now)
            + (item.ItemRespawnTimeStart != 0f ? item.ItemRespawnTimeStart : spawnIn);
        ScheduleRespawnIn(item, System.MathF.Max(0f, delay));
    }

    /// <summary>QC Item_Respawn: make the item available again and clear the schedule.</summary>
    public static void Respawn(Entity item)
    {
        Show(item, 1);
        // QC client/items/items.qc:258 — the respawn sparkle on the ITS_AVAILABLE→on transition. Only real
        // respawns (and a powerup's first scheduled appearance) reach Respawn; a normal item's initial spawn
        // shows via Show(item, 1) directly in StartItem, so this never double-fires on map load.
        EmitItemEffect("ITEM_RESPAWN", item);
        // Drop any respawn-countdown waypoint sprite (QC kills it in Item_RespawnCountdown before Respawn; this
        // also covers the rare paths that reach Respawn without finishing the countdown tick).
        if (item.WaypointAttached is Waypoints.WaypointSprite wp)
        {
            Waypoints.WaypointSprites.Kill(wp);
            item.WaypointAttached = null;
        }
        item.ScheduledRespawnTime = 0f;
        // a short spawn-shield so it can't be insta-grabbed the same frame it appears.
        item.ItemSpawnShieldExpire = Now;
        item.NextThink = Now;
        item.Think = ItemThink;
    }

    // QC Item_RespawnThink: respawn once the wait time elapses.
    private static void RespawnThink(Entity item)
    {
        item.NextThink = Now;
        if (Now >= item.ItemWait) Respawn(item);
    }

    // QC Item_RespawnCountdown: tick the countdown, then respawn when the scheduled time arrives. On the first
    // tick a respawn-countdown waypoint sprite is attached (killed on respawn); the per-second ITEMRESPAWNCOUNTDOWN
    // sound is client/networking-side.
    private static void RespawnCountdown(Entity item)
    {
        if (item.ItemRespawnCounter >= (int)RespawnTicks)
        {
            // QC: WaypointSprite_Kill(this.waypointsprite_attached) before Item_Respawn.
            if (item.WaypointAttached is Waypoints.WaypointSprite kw)
                Waypoints.WaypointSprites.Kill(kw);
            item.WaypointAttached = null;
            Respawn(item);
            return;
        }
        item.NextThink = Now + 1f;
        item.ItemRespawnCounter++;
        // QC Item_RespawnCountdown (items.qc:269-296): on the first countdown tick spawn the WP_Item respawn
        // waypoint sprite attached to the item, and for the SpectatorOnly items (Mega/Big Health+Armor) set
        // SPRITERULE_SPECTATOR so only spectators (and everyone in warmup / when sv_itemstime==2) see the
        // respawn countdown marker — the server per-peer gate (ServerNet.WaypointVisible) reproduces QC's
        // g_waypointsprite_itemstime mode 1/2 logic via sv_itemstime. WaypointSprite_UpdateBuildFinished drives
        // the build-progress bar to full at the scheduled respawn time. Scoped to the itemstime spectator set
        // (the generic non-timed item respawn waypoint is the items-pickups unit's concern, not ported here).
        if (item.ItemRespawnCounter == 1 && ItemstimeMutator.IsSpectatorOnlyItem(item.ClassName))
        {
            Waypoints.WaypointSprite wp = Waypoints.WaypointSprites.Spawn(
                "Item", 0f, 0f, item, System.Numerics.Vector3.Zero, item.Origin,
                0, new System.Numerics.Vector3(1f, 0f, 1f), radarIcon: 1,
                rule: Waypoints.SpriteRule.Spectator, hideable: true);
            Waypoints.WaypointSprites.UpdateBuildFinished(wp, item.ScheduledRespawnTime);
            item.WaypointAttached = wp;
        }
    }

    // The give+respawn tail of Item_Touch for the legacy GiveTo entry: schedule the respawn for the taken item.
    private static void ScheduleRespawnAfterPickup(Entity item)
    {
        if (item.ItemIsLoot) { RemoveItem(item); return; }
        if (item.SpawnShieldExpire == 0f) return; // stay weapon: no respawn
        ScheduleRespawn(item);
    }

    // =====================================================================================
    //  helpers
    // =====================================================================================

    /// <summary>
    /// QC: play the world item's pickup sound on <paramref name="player"/> — a non-resource powerup on
    /// CH_TRIGGER_SINGLE, everything else on CH_TRIGGER; both via the item's pickup sound (the single-vs-auto
    /// distinction is presentation-side, so the port plays on the Item channel). Public so the target_give host
    /// seam (QC target_give_use plays item_pickupsound after ITEM_HANDLE(Pickup,…), give.qc:18-19) can play the
    /// sound the silent <see cref="ItemGiveTo"/> intentionally omits, without duplicating the sound logic.
    /// </summary>
    public static void PlayPickupSound(Entity worldItem, Entity player)
    {
        // QC .item_pickupsound_ent override (a FilterItem hook may stamp a per-item pickup sound, e.g. New Toys'
        // SND_WEAPONPICKUP_NEW_TOYS roflsound) wins over the def's default sound.
        string snd = worldItem.ItemPickupSoundOverride
            ?? worldItem.Pickup?.PickupSoundName ?? "ITEMPICKUP";
        if (Api.Services is not null && !string.IsNullOrEmpty(snd))
            // CH_TRIGGER (auto) so two quick pickups (e.g. armor + health in one pass) stack instead of the
            // second one cutting off the first — DP plays item_pickupsound on the auto trigger channel.
            Api.Sound.Play(player, SoundChannel.TriggerAuto, snd);
    }

    /// <summary>
    /// Spawn the CSQC item particle burst (<c>EFFECT_ITEM_PICKUP</c> / <c>_RESPAWN</c>) at the item's bbox
    /// centre. QC <c>client/items/items.qc</c> fires these in CSQC on the networked <c>ITS_AVAILABLE</c>
    /// transition — pickup at items.qc:271 (and the loot ISF_REMOVEFX path at :338), respawn at :258 — always
    /// at <c>(absmin + absmax) * 0.5</c>. The port emits them server-side on the authoritative pickup/respawn
    /// event over the same Send_Effect channel as <c>EFFECT_SPAWN</c>; this is equivalent to the client-side
    /// transition test but free of its <c>isnew</c>/PVS guards (we only fire on the real gameplay event, never
    /// when an item merely streams into a client's view). <c>origin + (mins + maxs) * 0.5</c> equals the QC
    /// bbox centre and stays correct headless (where the engine link doesn't maintain AbsMin/AbsMax).
    /// </summary>
    private static void EmitItemEffect(string effectName, Entity item)
        => EffectEmitter.Emit(effectName, item.Origin + (item.Mins + item.Maxs) * 0.5f);

    /// <summary>QC RemoveItem: remove the world item (the ISF_REMOVEFX delayed-removal is networking-only here).</summary>
    public static void RemoveItem(Entity item)
    {
        MapMover.RemoveEntity(item);
    }

    // QC ITEM_TOUCH_NEEDKILL(): the loot's spot is a NODROP brush (lava) or a sky surface. We trace a zero-length
    // line at the item and test the hit contents (the port has no separate dpstartcontents global).
    private static bool LootInNoDrop(Entity item)
    {
        if (Api.Services is null) return false;
        TraceResult tr = Api.Trace.Trace(item.Origin, System.Numerics.Vector3.Zero, System.Numerics.Vector3.Zero,
            item.Origin, MoveFilter.Normal, item);
        return (tr.DpHitContents & NoDropContents) != 0;
    }

    // QC: a superweapon weapon pickup never weapon-stays. The port has no per-item weapon link here; approximate
    // via the weapon registry (any owned-weapon-set bit that is a superweapon).
    private static bool IsSuperWeaponItem(Entity item)
    {
        foreach (var w in item.OwnedWeaponSet.Weapons())
            if (w.IsSuperWeapon) return true;
        return false;
    }

    /// <summary>QC <c>g_weapon_stay</c> (items.qh: cvar("g_weapon_stay")). 0 = off, 1 = ghost no-ammo, 2 = stay-ammo.</summary>
    private static int WeaponStay => Api.Services is null ? 0 : (int)Api.Cvars.GetFloat("g_weapon_stay");

    /// <summary>
    /// Read a float cvar, or <paramref name="fallback"/> (the stock value) if it's unset/empty. The port
    /// convention (Weapon.Bal / TargetUtilities.CvarOr): "unset" is the empty-string case, distinct from "0".
    /// Public so the item defs' <c>ItemInit</c> can seed amounts/timers with the same fallback semantics.
    /// </summary>
    public static float CvarOr(string name, float fallback)
    {
        if (Api.Services is null) return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : Api.Cvars.GetFloat(name);
    }

    /// <summary>Read a bool cvar (<c>!= 0</c>), or <paramref name="fallback"/> if unset (the g_powerups toggles).</summary>
    public static bool CvarBoolOr(string name, bool fallback)
    {
        if (Api.Services is null) return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : Api.Cvars.GetFloat(name) != 0f;
    }
}
