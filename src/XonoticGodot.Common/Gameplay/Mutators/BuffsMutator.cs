using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Gameplay.Scoring;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Buffs mutator — port of common/mutators/mutator/buffs/sv_buffs.qc + buff/*.qc, built on the shared
/// <see cref="StatusEffectsCatalog"/>. Buff pickups spawn around the map; touching one applies a buff
/// status effect (a <see cref="StatusEffectDef"/> with <see cref="StatusEffectDef.IsBuff"/>) for a timed
/// duration, replacing any buff already held. Each buff's effect is driven through the gameplay hooks and a
/// per-frame tick:
/// <list type="bullet">
/// <item>resistance — reduces damage taken (Damage_Calculate)</item>
/// <item>medic — boosts health regen + can survive a fatal hit (PlayerRegen / Damage_Calculate / tick)</item>
/// <item>vampire — converts dealt damage into health (PlayerDamage_SplitHealthArmor)</item>
/// <item>jump — higher jump velocity + fall-damage immunity (PlayerPhysics / Damage_Calculate)</item>
/// <item>speed — scales movement + weapon rate (PlayerPhysics)</item>
/// <item>invisible — lowers the carrier's alpha (PlayerPreThink)</item>
/// <item>flight — crouch in midair flips gravity (PlayerPreThink)</item>
/// <item>bash — more knockback dealt, immune to knockback (Damage_Calculate)</item>
/// <item>disability — attacks slow the victim (Damage_Calculate + the Stunned effect)</item>
/// <item>vengeance — reflects a share of damage taken (Damage_Calculate)</item>
/// <item>luck — chance of a critical hit (Damage_Calculate)</item>
/// <item>ammo — unlimited ammo while held (apply/remove)</item>
/// <item>magnet — auto-collects nearby items (tick)</item>
/// </list>
/// Enabled by the <c>g_buffs</c> cvar.
/// </summary>
[Mutator]
public sealed class BuffsMutator : MutatorBase
{
    public BuffsMutator() => NetName = "buffs";

    // QC REGISTER_MUTATOR(buffs, autocvar_g_buffs).
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_buffs") != 0f;

    // ---- cvar-backed balance (defaults from bal/defaults; see *_time + the per-buff cvars) ----
    private float _resistanceBlock = 0.5f;       // g_buffs_resistance_blockpercent
    private float _medicSurviveChance = 0.6f;    // g_buffs_medic_survive_chance
    private float _medicSurviveHealth = 5f;      // g_buffs_medic_survive_health
    private float _medicRegen = 1.7f;            // g_buffs_medic_regen
    private float _vampireSteal = 0.4f;          // g_buffs_vampire_damage_steal
    private float _jumpVelocity = 600f;          // g_buffs_jump_velocity
    private float _speedSpeed = 1.5f;            // g_buffs_speed_speed
    private float _speedRate = 0.8f;             // g_buffs_speed_rate (weapon fire-rate factor)
    private float _invisibleAlpha = 0.5f;        // g_buffs_invisible_alpha
    private float _bashForce = 2f;               // g_buffs_bash_force
    private float _bashForceSelf = 1.2f;         // g_buffs_bash_force_self
    private float _disabilitySlowTime = 3f;      // g_buffs_disability_slowtime
    private float _disabilitySpeed = 0.7f;       // g_buffs_disability_speed
    private float _disabilityAttackTime = 1.5f;  // g_buffs_disability_attack_time_multiplier (weapon fire-time slow)
    private float _vengeanceMult = 0.4f;         // g_buffs_vengeance_damage_multiplier
    private float _luckChance = 0.15f;           // g_buffs_luck_chance
    private float _luckMult = 2f;                // g_buffs_luck_damagemultiplier
    private float _magnetRangeItem = 250f;       // g_buffs_magnet_range_item
    private float _magnetRangeBuff = 100f;       // g_buffs_magnet_range_buff
    private float _pickupDelay = 0.7f;           // g_buffs_pickup_delay

    // ---- inferno (burn-on-hit + fire/lava resistance) ----
    private float _infernoDamageMult = 0.3f;     // g_buffs_inferno_damagemultiplier
    private float _infernoBurnMinTime = 0.5f;    // g_buffs_inferno_burntime_min_time
    private float _infernoBurnTargetDamage = 150f; // g_buffs_inferno_burntime_target_damage
    private float _infernoBurnTargetTime = 5f;   // g_buffs_inferno_burntime_target_time
    private float _infernoBurnFactor = 2f;       // g_buffs_inferno_burntime_factor

    // ---- swapper (swap places with the nearest enemy on drop-weapon) ----
    private float _swapperRange = 1500f;         // g_buffs_swapper_range

    // ---- hook handlers ----
    private HookHandler<MutatorHooks.DamageCalculateArgs>? _onDamageCalc;
    private HookHandler<GameHooks.PlayerDamageArgs>? _onSplit;
    private HookHandler<MutatorHooks.PlayerRegenArgs>? _onRegen;
    private HookHandler<MutatorHooks.PlayerPhysicsArgs>? _onPhysics;
    private HookHandler<MutatorHooks.PlayerPreThinkArgs>? _onPreThink;
    private HookHandler<MutatorHooks.WeaponRateFactorArgs>? _onRate;
    private HookHandler<MutatorHooks.ForbidThrowCurrentWeaponArgs>? _onForbidThrow;
    private HookHandler<MutatorHooks.PlayerUseKeyArgs>? _onUseKey;

    public override void Hook()
    {
        _onDamageCalc ??= OnDamageCalculate;
        _onSplit ??= OnSplitHealthArmor;
        _onRegen ??= OnPlayerRegen;
        _onPhysics ??= OnPlayerPhysics;
        _onPreThink ??= OnPlayerPreThink;
        _onRate ??= OnWeaponRateFactor;
        _onForbidThrow ??= OnForbidThrowCurrentWeapon;
        _onUseKey ??= OnPlayerUseKey;

        MutatorHooks.DamageCalculate.Add(_onDamageCalc);
        GameHooks.PlayerDamageSplitHealthArmor.Add(_onSplit);
        MutatorHooks.PlayerRegen.Add(_onRegen);
        MutatorHooks.PlayerPhysics.Add(_onPhysics);
        MutatorHooks.PlayerPreThink.Add(_onPreThink);
        MutatorHooks.WeaponRateFactor.Add(_onRate);
        MutatorHooks.ForbidThrowCurrentWeapon.Add(_onForbidThrow);
        MutatorHooks.PlayerUseKey.Add(_onUseKey);

        if (Api.Services is not null)
        {
            R(ref _resistanceBlock, "g_buffs_resistance_blockpercent");
            R(ref _medicSurviveChance, "g_buffs_medic_survive_chance");
            R(ref _medicSurviveHealth, "g_buffs_medic_survive_health");
            R(ref _medicRegen, "g_buffs_medic_regen");
            R(ref _vampireSteal, "g_buffs_vampire_damage_steal");
            R(ref _jumpVelocity, "g_buffs_jump_velocity");
            R(ref _speedSpeed, "g_buffs_speed_speed");
            R(ref _speedRate, "g_buffs_speed_rate");
            R(ref _invisibleAlpha, "g_buffs_invisible_alpha");
            R(ref _bashForce, "g_buffs_bash_force");
            R(ref _bashForceSelf, "g_buffs_bash_force_self");
            R(ref _disabilitySlowTime, "g_buffs_disability_slowtime");
            R(ref _disabilitySpeed, "g_buffs_disability_speed");
            R(ref _disabilityAttackTime, "g_buffs_disability_attack_time_multiplier");
            R(ref _vengeanceMult, "g_buffs_vengeance_damage_multiplier");
            R(ref _luckChance, "g_buffs_luck_chance");
            R(ref _luckMult, "g_buffs_luck_damagemultiplier");
            R(ref _magnetRangeItem, "g_buffs_magnet_range_item");
            R(ref _magnetRangeBuff, "g_buffs_magnet_range_buff");
            R(ref _pickupDelay, "g_buffs_pickup_delay");
            R(ref _infernoDamageMult, "g_buffs_inferno_damagemultiplier");
            R(ref _infernoBurnMinTime, "g_buffs_inferno_burntime_min_time");
            R(ref _infernoBurnTargetDamage, "g_buffs_inferno_burntime_target_damage");
            R(ref _infernoBurnTargetTime, "g_buffs_inferno_burntime_target_time");
            R(ref _infernoBurnFactor, "g_buffs_inferno_burntime_factor");
            R(ref _swapperRange, "g_buffs_swapper_range");

            // QC buffs_Initialize: spawn the configured number of buffs if none exist yet.
            SpawnInitialBuffs();
        }
    }

    // QC MUTATOR_HOOKFUNCTION(buffs, BuildMutatorsString / BuildMutatorsPrettyString): only report Buffs as an
    // active mutator when g_buffs > 0 (the auto-spawn/replace-powerups mode), matching Base exactly.
    public override string BuildMutatorsString(string s)
        => (Api.Services is not null && Api.Cvars.GetFloat("g_buffs") > 0f) ? s + ":Buffs" : s;

    public override string BuildMutatorsPrettyString(string s)
        => (Api.Services is not null && Api.Cvars.GetFloat("g_buffs") > 0f) ? s + ", Buffs" : s;

    public override void Unhook()
    {
        if (_onDamageCalc is not null) MutatorHooks.DamageCalculate.Remove(_onDamageCalc);
        if (_onSplit is not null) GameHooks.PlayerDamageSplitHealthArmor.Remove(_onSplit);
        if (_onRegen is not null) MutatorHooks.PlayerRegen.Remove(_onRegen);
        if (_onPhysics is not null) MutatorHooks.PlayerPhysics.Remove(_onPhysics);
        if (_onPreThink is not null) MutatorHooks.PlayerPreThink.Remove(_onPreThink);
        if (_onRate is not null) MutatorHooks.WeaponRateFactor.Remove(_onRate);
        if (_onForbidThrow is not null) MutatorHooks.ForbidThrowCurrentWeapon.Remove(_onForbidThrow);
        if (_onUseKey is not null) MutatorHooks.PlayerUseKey.Remove(_onUseKey);
    }

    private static void R(ref float field, string cvar)
    {
        float v = Api.Cvars.GetFloat(cvar);
        if (v != 0f) field = v;
    }

    private static bool IsPlayer(Entity? e) => e is not null && (e.Flags & EntFlags.Client) != 0;

    private static bool IsFrozen(Entity e)
    {
        var fz = StatusEffectsCatalog.Frozen;
        return fz is not null && StatusEffectsCatalog.Has(e, fz);
    }

    // Resolve a buff def by its short name (e.g. "resistance" -> the "buff_resistance" StatusEffectDef).
    // The full name is memoized: Active() runs five times per player per tick from the PreThink handler, and
    // a per-call "buff_" + shortName concat was a steady ~2.5 KB/tick of GC churn at 12 players.
    private static readonly Dictionary<string, string> _buffNameCache = new(StringComparer.Ordinal);

    private static StatusEffectDef? Buff(string shortName)
    {
        if (!_buffNameCache.TryGetValue(shortName, out string? full))
            _buffNameCache[shortName] = full = "buff_" + shortName;
        return StatusEffectsCatalog.ByName(full);
    }

    private static bool Active(Entity? e, string shortName)
    {
        if (e is null) return false;
        var def = Buff(shortName);
        return def is not null && StatusEffectsCatalog.Has(e, def);
    }

    // =====================================================================================
    //  Pickup / spawning
    // =====================================================================================

    /// <summary>
    /// QC buff_Available: a buff can be offered when its g_buffs_&lt;name&gt; cvar is set, ammo is excluded
    /// when ammo is already unlimited or in melee_only, and vampire is excluded when the vampire mutator runs.
    /// </summary>
    private static bool BuffAvailable(StatusEffectDef buff)
    {
        if (Api.Services is null) return false;
        string n = buff.Name.StartsWith("buff_", StringComparison.Ordinal) ? buff.Name[5..] : buff.Name;
        // QC: ammo is excluded when start_items already grants IT_UNLIMITED_AMMO (the dominant source of that
        // start flag is g_use_ammunition 0 — readplayerstartcvars) OR in g_melee_only.
        if (n == "ammo" && (StartItemsHasUnlimitedAmmo() || Api.Cvars.GetFloat("g_melee_only") != 0f)) return false;
        if (n == "vampire" && Api.Cvars.GetFloat("g_vampire") != 0f) return false;
        return Api.Cvars.GetFloat("g_buffs_" + n) != 0f;
    }

    private static IReadOnlyList<StatusEffectDef> AvailableBuffs()
    {
        List<StatusEffectDef> list = new();
        foreach (var d in StatusEffectsCatalog.All)
            if (d.IsBuff && BuffAvailable(d))
                list.Add(d);
        return list;
    }

    // void buff_NewType(entity) — pick a random available buff (QC weights rarer-seen ones higher; a flat
    // random pick is the faithful core once seen-counts aren't networked).
    private static StatusEffectDef? RandomBuff()
    {
        var avail = AvailableBuffs();
        return avail.Count == 0 ? null : avail[Prandom.RangeInt(0, avail.Count)];
    }

    // void buffs_DelayedInit / buff_Init — spawn g_buffs_spawn_count buff items if none exist.
    private void SpawnInitialBuffs()
    {
        if (Api.Cvars.GetFloat("g_buffs") <= 0f) return;
        int count = (int)Api.Cvars.GetFloat("g_buffs_spawn_count");
        if (count <= 0) return;

        // QC: only seed if no item_buff already exists.
        foreach (Entity _ in Api.Entities.FindByClass("item_buff"))
            return;

        for (int i = 0; i < count; i++)
        {
            Entity e = SpawnBuff(RandomBuff());
            // QC buffs_DelayedInit: each auto-seeded buff gets spawnflags|=64 (always randomize/relocate), so it
            // relocates on every reset even with g_buffs_random_location=0. Then arm the per-frame lifetime think.
            e.BuffAlwaysRelocate = true;
            MaybeRandomize(e);
            float lifetime = Api.Cvars.GetFloat("g_buffs_random_lifetime");
            e.BuffLifetime = lifetime > 0f ? Api.Clock.Time + lifetime : 0f;
            BuffThink(e);
        }
    }

    /// <summary>
    /// Spawn a buff pickup entity (QC buff_Init): a touchable item carrying a buff type, launched a little so
    /// it scatters. The buff type randomizes on respawn. Returns the spawned entity.
    /// </summary>
    public Entity SpawnBuff(StatusEffectDef? type)
    {
        Entity e = Api.Entities.Spawn();
        e.ClassName = "item_buff";
        e.Solid = Solid.Trigger;
        e.Flags |= EntFlags.Item;
        e.MoveType = MoveType.Toss;
        e.Effects |= EffectFlags.FullBright | EffectFlags.Stardust | EffectFlags.NoShadow;
        e.BuffDef = type ?? RandomBuff();
        if (e.BuffDef?.Model is { } m) Api.Entities.SetModel(e, m);
        e.Velocity = Prandom.Vec() * 250f; // QC buffs_DelayedInit
        e.BuffActive = true;
        e.Touch = (self, toucher) => BuffTouch(self, toucher);
        return e;
    }

    // QC ITEM_TOUCH_NEEDKILL(): the buff settled in lava/slime (NODROP) or on a sky surface; a zero-length trace
    // at the item reports the surface it's resting on. NoDropContents (DPCONTENTS_NODROP) covers lava brushes.
    private const int NoDropContents = unchecked((int)0x80000000);
    private const int Q3SurfaceFlagSky = 0x4;

    private static bool BuffInNeedKill(Entity item)
    {
        if (Api.Services is null) return false;
        TraceResult tr = Api.Trace.Trace(item.Origin, Vector3.Zero, Vector3.Zero, item.Origin, MoveFilter.Normal, item);
        return (tr.DpHitContents & NoDropContents) != 0 || (tr.DpHitQ3SurfaceFlags & Q3SurfaceFlagSky) != 0;
    }

    // void buff_Touch(entity this, entity toucher) — apply the buff to a touching player.
    private void BuffTouch(Entity item, Entity toucher)
    {
        if (Api.Services is null || VehicleCommon.GameStopped) return;

        // QC buff_Touch: a buff item that touches lava/slime/void/sky relocates instead of being picked up.
        if (BuffInNeedKill(item))
        {
            BuffRespawn(item);
            return;
        }

        if (!item.BuffActive || item.BuffDef is null) return;
        if (!IsPlayer(toucher) || toucher.DeadState != DeadFlag.No) return;
        // QC buff_Touch: a team-forced buff item can only be picked up by its team; a vehicle occupant can't.
        if ((item.TeamForced != 0 && (int)toucher.Team != item.TeamForced) || toucher.Vehicle is not null) return;
        if (Api.Clock.Time < toucher.BuffShield) return; // recently dropped/replaced a buff

        StatusEffectDef thebuff = item.BuffDef;

        // QC: if the player already holds a buff, replace it ONLY when cl_buffs_autoreplace is set AND it's a
        // *different* buff; otherwise do nothing (keep the held buff). cl_buffs_autoreplace is REPLICATE'd to the
        // server with default 1 (mutators.cfg); the port reads it as the server-side value (no per-client store).
        StatusEffectDef? held = HeldBuff(toucher);
        if (held is not null)
        {
            bool autoreplace = CvarBool("cl_buffs_autoreplace", true);
            if (!autoreplace || held == thebuff) return; // do nothing

            // QC: notify the replaced buff is lost (to others, MSG_INFO INFO_ITEM_BUFF_LOST) + SND_BUFF_LOST.
            NotificationSystem.Send(NotifBroadcast.AllExcept, toucher, MsgType.Info,
                "ITEM_BUFF_LOST", toucher.NetName, held.RegistryId);
            RemoveAllBuffs(toucher);
        }

        item.BuffActive = false;
        item.BuffLifetime = 0f; // QC: this.lifetime = 0 (a picked-up item no longer auto-relocates)
        item.Owner = toucher;

        // QC: bufftime = this.buffs_finished ? ... : thebuff.m_time(thebuff); fall back to 999 / cvar time.
        float bufftime = BuffTime(thebuff);
        ApplyBuff(toucher, thebuff, bufftime);

        Api.Sound.Play(toucher, SoundChannel.Item, "misc/shield_respawn.wav");
        // QC Send_Notification(ITEM_BUFF_GOT, thebuff.m_id). The CENTER variant takes one float (the buff id)
        // and tells the picker "You got the <buff> buff!". (The buff id is the status-effect registry id.)
        NotificationSystem.Center(toucher, "ITEM_BUFF_GOT", thebuff.RegistryId);
        // QC: INFO_ITEM_BUFF to everyone else — "<name> got the <buff> buff!".
        NotificationSystem.Send(NotifBroadcast.AllExcept, toucher, MsgType.Info,
            "ITEM_BUFF", toucher.NetName, thebuff.RegistryId);

        // QC: the item goes on cooldown then respawns (re-randomizing its type). Keep the entity alive.
        ScheduleRespawn(item);
    }

    // float Buff.m_time — cvar g_buffs_<name>_time, default 60s; 0 → "permanent-ish" 999.
    private static float BuffTime(StatusEffectDef buff)
    {
        if (Api.Services is null) return 60f;
        string n = buff.Name.StartsWith("buff_", StringComparison.Ordinal) ? buff.Name[5..] : buff.Name;
        float t = Api.Cvars.GetFloat("g_buffs_" + n + "_time");
        return t > 0f ? t : 999f;
    }

    private void ScheduleRespawn(Entity item)
    {
        // QC buff_Think respawn branch: after the respawn cooldown, clear the owner, optionally re-randomize the
        // type (only when g_buffs_randomize is on for this gametype), optionally relocate (g_buffs_random_location
        // or spawnflags&64), and re-activate. The cooldown is counted by a recurring per-frame think so the
        // lifetime timer (buff_Respawn) can also fire while the buff sits untouched.
        float cooldown = Cvar("g_buffs_cooldown_respawn", 3f);
        float ready = Api.Clock.Time + cooldown;
        item.Owner = null;
        item.NextThink = ready;
        item.Think = self =>
        {
            // Cooldown elapsed: re-arm the buff (QC buff_Think + buff_Reset gating).
            MaybeRandomize(self);
            MaybeRelocate(self);
            if (self.BuffDef?.Model is { } m) Api.Entities.SetModel(self, m);
            self.BuffActive = true;
            BuffThink(self);
        };
    }

    // QC buff_Think (the steady per-frame think on an active buff): re-randomize/relocate the buff when its
    // random_lifetime elapses without anyone touching it. Recurs every frame (QC sets nextthink = time).
    private void BuffThink(Entity item)
    {
        item.NextThink = Api.Clock.Time;
        item.Think = self =>
        {
            if (self.BuffActive && self.BuffLifetime != 0f && Api.Clock.Time >= self.BuffLifetime)
                BuffRespawn(self);
            BuffThink(self);
        };
    }

    // QC buff_Reset randomize gate: re-roll the buff type only when g_buffs_randomize is enabled for the current
    // gametype (bit 1 = non-team, bit 2 = team). Default 0 → never randomize (the port previously always did).
    private static void MaybeRandomize(Entity item)
    {
        if (Api.Services is null) return;
        int randomize = (int)Api.Cvars.GetFloat("g_buffs_randomize");
        int gate = GameScores.Teamplay ? 2 : 1;
        if ((randomize & gate) != 0)
            item.BuffDef = RandomBuff();
    }

    // QC buff_Reset / buff_Think relocate gate: relocate only when g_buffs_random_location is set or the buff
    // entity carries the always-relocate spawnflag (64). buffs_DelayedInit sets spawnflag 64 on each auto-seeded
    // buff; the port marks auto-spawned buffs the same way via BuffActive seeding (see SpawnBuff).
    private void MaybeRelocate(Entity item)
    {
        if (Api.Services is null) return;
        bool alwaysRelocate = item.BuffAlwaysRelocate;
        if (Api.Cvars.GetFloat("g_buffs_random_location") != 0f || alwaysRelocate)
            BuffRespawn(item);
    }

    // QC buff_Respawn: relocate an untouched/expired buff to a fresh random map spot (with the SelectSpawnPoint
    // fallback), launch it upward, unstick it, arm the lifetime timer, and play the relocation sound. The
    // EFFECT_ELECTRO_COMBO x2 visual and WaypointSprite_Ping are omitted (no effect-spawn / buff-waypoint
    // service yet — same omissions documented for the swapper swap and the buffs.waypoint feature).
    private void BuffRespawn(Entity item)
    {
        if (Api.Services is null || VehicleCommon.GameStopped) return;

        // QC: velocity = '0 0 200'; MoveToRandomMapLocation, else SelectSpawnPoint fallback (randomvec*100 + up).
        // RandomMapLocation already folds in the SelectSpawnPoint fallback (it samples a player spawnpoint, or
        // jitters around the home origin when the map has none).
        Vector3 dest = BallEntity.RandomMapLocation(item.Origin);
        Api.Entities.SetOrigin(item, dest);
        item.Velocity = (Prandom.Vec() * 100f) + new Vector3(0f, 0f, 200f);

        // QC: tracebox(origin, mins*1.5, maxs*1.5, origin, MOVE_NOMONSTERS, this) → setorigin(trace_endpos) to unstick.
        TraceResult tr = Api.Trace.Trace(dest, item.Mins * 1.5f, item.Maxs * 1.5f, dest, MoveFilter.NoMonsters, item);
        Api.Entities.SetOrigin(item, tr.EndPos);

        item.MoveType = MoveType.Toss;
        item.Angles = Vector3.Zero;

        // QC: lifetime = time + g_buffs_random_lifetime (re-respawn if still untouched after this).
        float lifetime = Api.Cvars.GetFloat("g_buffs_random_lifetime");
        item.BuffLifetime = lifetime > 0f ? Api.Clock.Time + lifetime : 0f;

        // QC: sound(this, CH_TRIGGER, SND_KA_RESPAWN, VOL_BASE, ATTEN_NONE).
        SoundSystem.PlayOn(item, "KA_RESPAWN");
    }

    // =====================================================================================
    //  Apply / remove (the buff-specific m_apply/m_remove side effects)
    // =====================================================================================

    private void ApplyBuff(Entity actor, StatusEffectDef buff, float duration)
    {
        string n = ShortName(buff);

        // QC AmmoBuff.m_apply: grant unlimited ammo, remembering the previous state, and force every reloadable
        // slot's clip to full (saving the previous clip load) so reload weapons never run dry while held.
        if (n == "ammo")
        {
            actor.BuffAmmoPrevInfItems = (actor.Items & ItUnlimitedAmmo) != 0;
            actor.Items |= ItUnlimitedAmmo;
            for (int slot = 0; slot < MutatorConstants.MaxWeaponSlots; slot++)
            {
                var st = actor.WeaponState(new WeaponSlot(slot));
                if (st.ClipLoad != 0) st.BuffAmmoPrevClipLoad = st.ClipLoad;
                FillSlotClip(st);
            }
        }
        // QC FlightBuff.m_apply: remember gravity (so flipping it back works), default it if zero.
        if (n == "flight")
        {
            actor.BuffFlightOldGravity = actor.Gravity;
            if (actor.Gravity == 0f) actor.Gravity = 1f;
        }
        // QC Buff.m_apply: EF_NOSHADOW while a buff is held (so the buff icon reads cleanly).
        actor.Effects |= EffectFlags.NoShadow;

        StatusEffectsCatalog.Apply(actor, buff, duration);
    }

    // void buff_RemoveAll(actor) — strip every buff and run the per-buff cleanup, then set the pickup delay.
    private void RemoveAllBuffs(Entity actor)
    {
        foreach (var d in StatusEffectsCatalog.All)
        {
            if (!d.IsBuff || !StatusEffectsCatalog.Has(actor, d)) continue;
            string n = ShortName(d);

            if (n == "ammo")
            {
                actor.Items = BitSet(actor.Items, ItUnlimitedAmmo, actor.BuffAmmoPrevInfItems);
                RestoreAmmoClips(actor); // QC AmmoBuff.m_remove: put back the pre-buff clip loads
            }
            if (n == "flight")
            {
                // QC FlightBuff.m_remove: if the player is still standing in a gravity zone, restore THAT zone's
                // gravity (trigger_gravity_check.enemy.gravity); otherwise restore the saved pre-buff gravity.
                actor.Gravity = actor.GravityCheck?.Enemy is { } zone
                    ? zone.Gravity
                    : actor.BuffFlightOldGravity;
                actor.BuffFlightOldGravity = 0f;
            }

            StatusEffectsCatalog.Remove(actor, d);
        }
        actor.BuffAmmoPrevInfItems = false;
        actor.Effects &= ~EffectFlags.NoShadow;
        if (Api.Services is not null)
            actor.BuffShield = Api.Clock.Time + MathF.Max(0f, _pickupDelay);
    }

    private static StatusEffectDef? HeldBuff(Entity actor)
    {
        foreach (var d in StatusEffectsCatalog.All)
            if (d.IsBuff && StatusEffectsCatalog.Has(actor, d)) return d;
        return null;
    }

    private static string ShortName(StatusEffectDef d)
        => d.Name.StartsWith("buff_", StringComparison.Ordinal) ? d.Name[5..] : d.Name;

    // =====================================================================================
    //  Damage_Calculate — resistance / medic / jump / vengeance / bash / disability / luck
    // =====================================================================================

    // MUTATOR_HOOKFUNCTION(buffs, Damage_Calculate)
    private bool OnDamageCalculate(ref MutatorHooks.DamageCalculateArgs args)
    {
        Entity? attacker = args.Attacker;
        Entity target = args.Target;
        string dt = args.DeathType;
        float damage = args.Damage;
        Vector3 force = args.Force;

        // QC: no recurrence for inferno's or vengeance's own dealt damage.
        if (dt == DeathTypes.BuffInferno || dt == DeathTypes.BuffVengeance) { return false; }

        bool needKill = IsNeedKill(dt);

        // resistance — flat damage reduction (also on self-damage).
        if (Active(target, "resistance"))
            damage = QMath.Bound(0f, damage * (1f - _resistanceBlock), damage);

        // medic — survive a fatal hit with a sliver of health, by chance. QC guards with !ITEM_DAMAGE_NEEDKILL
        // so lava/slime/swamp/void hits are never wrongly survived.
        if (Active(target, "medic") && attacker is not null && !needKill
            && target.GetResource(ResourceType.Health) - damage <= 0f)
        {
            if (Prandom.Float() <= _medicSurviveChance)
                damage = MathF.Max(5f, target.GetResource(ResourceType.Health) - _medicSurviveHealth);
        }

        // jump — immune to fall damage.
        if (Active(target, "jump") && dt == DeathTypes.Fall)
            damage = 0f;

        // vengeance — reflect a share back at the attacker (after a short delay, like QC). QC guards with
        // !ITEM_DAMAGE_NEEDKILL so a lava/slime/void kill doesn't reflect.
        if (Active(target, "vengeance") && attacker is not null && !ReferenceEquals(attacker, target) && !needKill)
        {
            float reflected = damage * _vengeanceMult;
            ScheduleVengeance(target, attacker, reflected);
        }

        // bash — target takes no knockback; attacker deals scaled knockback.
        if (Active(target, "bash") && !ReferenceEquals(attacker, target))
            force = Vector3.Zero;
        if (Active(attacker, "bash") && force != Vector3.Zero)
            force *= ReferenceEquals(attacker, target) ? _bashForceSelf : _bashForce;

        // disability — the attacker's hit slows the target down for a while.
        if (Active(attacker, "disability") && attacker is not null && !ReferenceEquals(attacker, target))
        {
            var stunned = StatusEffectsCatalog.ByName("stunned");
            if (stunned is not null)
                StatusEffectsCatalog.Apply(target, stunned, _disabilitySlowTime);
        }

        // inferno (carrier as target) — immune to plain fire, half damage from lava.
        if (Active(target, "inferno"))
        {
            if (dt == DeathTypes.Fire) damage = 0f;
            if (dt == DeathTypes.Lava) damage *= 0.5f; // QC: TODO cvarize
        }

        if (!ReferenceEquals(attacker, target))
        {
            // luck — chance to crit for a damage multiplier.
            if (Active(attacker, "luck") && _luckMult > 0f)
            {
                if (Prandom.Float() <= _luckChance)
                    damage *= _luckMult;
            }

            // inferno (carrier as attacker) — set the target alight, burning for a log-curve time.
            if (Active(attacker, "inferno"))
            {
                float btime = InfernoBurnTime(damage);
                MonsterFramework.AddFireDamage(target, attacker, damage * _infernoDamageMult, btime,
                    DeathTypes.BuffInferno);
            }
        }

        args.Damage = damage;
        args.Force = force;
        return false;
    }

    // float buff_Inferno_CalculateTime(dmg) — burn duration grows logarithmically with the hit's damage:
    //   offset_y + (target_time - offset_y) * logn((dmg) * ((factor-1)/target_damage) + 1, factor)
    private float InfernoBurnTime(float dmg)
    {
        float offsetY = _infernoBurnMinTime;
        float intersectX = _infernoBurnTargetDamage;
        float intersectY = _infernoBurnTargetTime;
        float b = _infernoBurnFactor;
        if (b <= 0f || b == 1f || intersectX == 0f) return offsetY;
        // logn(x, base) = log(x) / log(base)
        float arg = dmg * ((b - 1f) / intersectX) + 1f;
        if (arg <= 0f) return offsetY;
        float logn = MathF.Log(arg) / MathF.Log(b);
        return offsetY + (intersectY - offsetY) * logn;
    }

    // QC ITEM_DAMAGE_NEEDKILL(dt): DEATH_HURTTRIGGER (= void) / DEATH_SLIME / DEATH_LAVA / DEATH_SWAMP.
    private static bool IsNeedKill(string dt)
        => dt == DeathTypes.Void || dt == DeathTypes.Slime || dt == DeathTypes.Lava || dt == DeathTypes.Swamp;

    // buff_Vengeance_DelayedDamage — reflect after 0.1s so it doesn't recurse into this hook.
    private static void ScheduleVengeance(Entity owner, Entity enemy, float dmg)
    {
        if (Api.Services is null || dmg <= 0f) return;
        Entity e = Api.Entities.Spawn();
        e.ClassName = "buff_vengeance_dmg";
        e.NextThink = Api.Clock.Time + 0.1f;
        e.Think = self =>
        {
            if (!enemy.IsFreed && enemy.DeadState == DeadFlag.No)
                Combat.Damage(enemy, owner, owner, dmg, "buff_vengeance", enemy.Origin, Vector3.Zero);
            Api.Entities.Remove(self);
        };
    }

    // =====================================================================================
    //  PlayerDamage_SplitHealthArmor — vampire heal-on-damage
    // =====================================================================================

    private bool OnSplitHealthArmor(ref GameHooks.PlayerDamageArgs args)
    {
        Entity attacker = args.Attacker;
        Entity target = args.Target;
        if (!Active(attacker, "vampire")) return false;

        float healthTake = QMath.Bound(0f, args.DamageTake, target.GetResource(ResourceType.Health));
        if (!ReferenceEquals(target, attacker) && IsPlayer(attacker)
            && target.DeadState == DeadFlag.No && !IsFrozen(target))
        {
            attacker.GiveResource(ResourceType.Health, _vampireSteal * healthTake);
        }
        return false;
    }

    // =====================================================================================
    //  PlayerRegen — medic boosts health regen
    // =====================================================================================

    // QC MedicBuff PlayerRegen tunes the regen/limit/rot factors; the headless PlayerRegen hook only models
    // "disable regen", so returning false (don't disable) keeps regen running — the actual boosted heal is
    // applied in the per-frame tick (MedicTick). No-op otherwise.
    private bool OnPlayerRegen(ref MutatorHooks.PlayerRegenArgs args) => false;

    // =====================================================================================
    //  PlayerPhysics — jump height, speed, disability slow
    // =====================================================================================

    private bool OnPlayerPhysics(ref MutatorHooks.PlayerPhysicsArgs args)
    {
        Entity player = args.Player;

        // QC movement stats auto-reset each frame; reset the overrides first so they stay frame-local.
        player.JumpVelocityOverride = 0f;
        player.SpeedMultiplier = 1f;

        // QC JumpBuff PlayerPhysics: raise the jump velocity while held.
        if (Active(player, "jump"))
            player.JumpVelocityOverride = _jumpVelocity;

        // speed — scale the player's top speed (QC speed buff / disability use the highspeed stat).
        if (Active(player, "speed"))
            player.SpeedMultiplier *= _speedSpeed;

        // disability (Stunned) — slow the affected player down.
        var stunned = StatusEffectsCatalog.ByName("stunned");
        if (stunned is not null && StatusEffectsCatalog.Has(player, stunned))
            player.SpeedMultiplier *= _disabilitySpeed;

        return false;
    }

    // =====================================================================================
    //  WeaponRateFactor — disability (Stunned) slows the victim's weapon fire-rate
    // =====================================================================================

    // QC disability WeaponRateFactor: a stunned player's refire/animtime factor is multiplied by
    // g_buffs_disability_attack_time_multiplier (1.5 → slower firing).
    private bool OnWeaponRateFactor(ref MutatorHooks.WeaponRateFactorArgs args)
    {
        var stunned = StatusEffectsCatalog.ByName("stunned");
        if (stunned is not null && StatusEffectsCatalog.Has(args.Player, stunned))
            args.Factor *= _disabilityAttackTime;
        return false;
    }

    // =====================================================================================
    //  ForbidThrowCurrentWeapon — swapper buff: drop-weapon swaps you with the nearest enemy
    // =====================================================================================

    // QC swapper ForbidThrowCurrentWeapon: pressing drop-weapon while the swapper buff is held teleports you to
    // swap places with the nearest enemy in range (origin/velocity/angles exchanged), consuming the buff.
    // Returning true suppresses the normal weapon drop.
    private bool OnForbidThrowCurrentWeapon(ref MutatorHooks.ForbidThrowCurrentWeaponArgs args)
    {
        if (Api.Services is null || VehicleCommon.GameStopped) return false;
        Entity player = args.Player;
        if (!Active(player, "swapper")) return false;

        // Find the nearest valid enemy within g_buffs_swapper_range.
        Entity? closest = null;
        float bestDist2 = _swapperRange * _swapperRange;
        foreach (Entity head in Api.Entities.FindInRadius(player.Origin, _swapperRange))
        {
            if (!IsPlayer(head) || ReferenceEquals(head, player)) continue;
            if (Teams.SameTeam(head, player)) continue;       // QC DIFF_TEAM
            if (head.DeadState != DeadFlag.No || IsFrozen(head) || head.Vehicle is not null) continue;
            float d2 = (head.Origin - player.Origin).LengthSquared();
            if (d2 < bestDist2) { bestDist2 = d2; closest = head; }
        }
        if (closest is null) return false;

        Vector3 myOrg = player.Origin, myVel = player.Velocity, myAng = player.Angles;
        Vector3 theirOrg = closest.Origin, theirVel = closest.Velocity, theirAng = closest.Angles;

        // QC: Drop_Special_Items(closest) + MUTATOR_CALLHOOK(PortalTeleport, player) — both omitted here (the
        // DropSpecialItems / PortalTeleport hooks are not ported; see todos). Core position/velocity swap:
        Api.Entities.SetOrigin(player, theirOrg);
        Api.Entities.SetOrigin(closest, myOrg);
        player.Velocity = theirVel;
        player.Angles = theirAng;
        player.OldOrigin = theirOrg;
        closest.Velocity = myVel;
        closest.Angles = myAng;
        closest.OldOrigin = myOrg;

        // QC: SND_KA_RESPAWN at both ends (+ EFFECT_ELECTRO_COMBO x2 — no effect-spawn service yet; see todos).
        SoundSystem.PlayOn(player, "KA_RESPAWN");
        SoundSystem.PlayOn(closest, "KA_RESPAWN");

        // QC: buff_RemoveAll(player) — the swap consumes the buff.
        RemoveAllBuffs(player);
        return true;
    }

    // =====================================================================================
    //  PlayerUseKey — drop the held buff (g_buffs_drop)
    // =====================================================================================

    // QC MUTATOR_HOOKFUNCTION(buffs, PlayerUseKey, CBC_ORDER_FIRST): pressing +use with g_buffs_drop on drops the
    // held buff — notify the picker (ITEM_BUFF_DROP) + others (INFO_ITEM_BUFF_LOST), strip the buff, arm the
    // pickup-shield delay, and play SND_BUFF_LOST. Returns true to consume the press.
    private bool OnPlayerUseKey(ref MutatorHooks.PlayerUseKeyArgs args)
    {
        if (Api.Services is null || VehicleCommon.GameStopped) return false;
        if (Api.Cvars.GetFloat("g_buffs_drop") == 0f) return false;

        Entity player = args.Player;
        StatusEffectDef? held = HeldBuff(player);
        if (held is null) return false;

        NotificationSystem.Center(player, "ITEM_BUFF_DROP", held.RegistryId);
        NotificationSystem.Send(NotifBroadcast.AllExcept, player, MsgType.Info,
            "ITEM_BUFF_LOST", player.NetName, held.RegistryId);

        RemoveAllBuffs(player); // sets buff_shield = time + g_buffs_pickup_delay
        player.BuffShield = Api.Clock.Time + MathF.Max(0f, _pickupDelay);
        SoundSystem.PlayOn(player, "BUFF_LOST");
        return true;
    }

    // =====================================================================================
    //  PlayerPreThink — flight gravity flip, invisible alpha, per-frame buff ticks
    // =====================================================================================

    private bool OnPlayerPreThink(ref MutatorHooks.PlayerPreThinkArgs args)
    {
        Entity player = args.Player;
        if (Api.Services is null || VehicleCommon.GameStopped) return false;
        if (!IsPlayer(player) || player.DeadState != DeadFlag.No) return false;

        // QC FlightBuff PlayerPreThink: crouch in midair flips gravity once per press (toggle flight).
        if (Active(player, "flight"))
        {
            if (!player.ButtonCrouch)
                player.BuffFlightCrouchHeld = false;
            else if (!player.BuffFlightCrouchHeld)
            {
                player.BuffFlightCrouchHeld = true;
                player.Gravity *= -1f;
            }
        }

        // invisible — keep the carrier's alpha lowered while held; restore when it lapses.
        if (Active(player, "invisible"))
            player.Alpha = _invisibleAlpha;
        else if (player.Alpha == _invisibleAlpha)
            player.Alpha = 1f;

        // Per-frame buff ticks (QC StatusEffect m_tick equivalents):
        if (Active(player, "medic")) MedicTick(player);
        if (Active(player, "magnet")) MagnetTick(player);
        if (Active(player, "ammo")) AmmoTick(player);

        return false;
    }

    // MedicBuff: boosted health regeneration toward the (raised) medic max, paced by frame time. QC's
    // MedicBuff PlayerRegen scales the regen rate + raises the stable-health ceiling; modelled here as a
    // per-frame fractional heal (health is a float, so no accumulator/shared-state is needed).
    private void MedicTick(Entity player)
    {
        float baseMax = Cvar("g_balance_health_regenstable", 100f);
        float medicMax = baseMax * Cvar("g_buffs_medic_max", 1.5f);
        if (player.GetResource(ResourceType.Health) >= medicMax) return;

        float regenPerSec = Cvar("g_balance_health_regen", 0.1f) * baseMax * _medicRegen;
        player.GiveResourceWithLimit(ResourceType.Health, regenPerSec * Api.Clock.FrameTime, medicMax);
    }

    // MagnetBuff.m_tick: pull nearby items in. QC uses boxesoverlap(player.absmin/absmax expanded by the reach,
    // item.absmin/absmax) — a box-vs-box test (so large/off-centre items are caught), with the reach selected by
    // whether the item is a buff (range_buff) or a normal item (range_item). The FindInRadius is just a cheap
    // broadphase over the larger of the two reaches plus the item half-extent; the per-item gate is the box test.
    private void MagnetTick(Entity player)
    {
        float broad = MathF.Max(_magnetRangeItem, _magnetRangeBuff) + 256f;
        foreach (Entity it in Api.Entities.FindInRadius(player.Origin, broad))
        {
            if ((it.Flags & EntFlags.Item) == 0 || it.IsFreed || ReferenceEquals(it, player)) continue;
            float reach = it.ClassName == "item_buff" ? _magnetRangeBuff : _magnetRangeItem;
            if (!BoxesOverlapExpanded(player, it, reach)) continue;
            it.Touch?.Invoke(it, player);
        }
    }

    // QC boxesoverlap(a.absmin - r, a.absmax + r, b.absmin, b.absmax). Falls back to an origin distance test when
    // the bounds are degenerate (headless, no physics link maintaining AbsMin/AbsMax).
    private static bool BoxesOverlapExpanded(Entity a, Entity b, float r)
    {
        Vector3 e = new(r, r, r);
        Vector3 aMin = a.AbsMin - e, aMax = a.AbsMax + e;
        Vector3 bMin = b.AbsMin, bMax = b.AbsMax;
        if (aMin == aMax && bMin == bMax)
            return Vector3.Distance(a.Origin, b.Origin) <= r;
        return aMin.X <= bMax.X && aMax.X >= bMin.X
            && aMin.Y <= bMax.Y && aMax.Y >= bMin.Y
            && aMin.Z <= bMax.Z && aMax.Z >= bMin.Z;
    }

    // AmmoBuff.m_tick: the unlimited-ammo flag (set on apply) covers the resource pools; QC's per-tick work only
    // tops up each reloadable slot's clip so reload weapons (rifle / OK machinegun) never need reloading.
    private static void AmmoTick(Entity player)
    {
        if (!IsPlayer(player)) return;
        for (int slot = 0; slot < MutatorConstants.MaxWeaponSlots; slot++)
            FillSlotClip(player.WeaponState(new WeaponSlot(slot)));
    }

    // QC: actor.(weaponentity).clip_load = actor.(weaponentity).(weapon_load[switchweapon]) = clip_size — fill the
    // slot's live magazine AND its persistent per-weapon store for the weapon currently selected in that slot.
    private static void FillSlotClip(WeaponSlotState st)
    {
        if (st.ClipSize == 0) return;
        st.ClipLoad = st.ClipSize;
        if (st.SwitchWeaponId >= 0)
            Weapon.SetWeaponLoad(st, st.SwitchWeaponId, st.ClipSize);
    }

    // QC AmmoBuff.m_remove: restore each slot's pre-buff clip load (saved at apply), then clear the saved value.
    private static void RestoreAmmoClips(Entity actor)
    {
        for (int slot = 0; slot < MutatorConstants.MaxWeaponSlots; slot++)
        {
            var st = actor.WeaponState(new WeaponSlot(slot));
            if (st.BuffAmmoPrevClipLoad != 0)
            {
                st.ClipLoad = st.BuffAmmoPrevClipLoad;
                st.BuffAmmoPrevClipLoad = 0;
            }
        }
    }

    // =====================================================================================
    //  Helpers
    // =====================================================================================

    private const int ItUnlimitedAmmo = 1 << 0; // QC IT_UNLIMITED_AMMO = BIT(0) (common/items/item.qh)

    private static int BitSet(int bits, int mask, bool set) => set ? bits | mask : bits & ~mask;

    private static float Cvar(string name, float fallback)
    {
        if (Api.Services is null) return fallback;
        float v = Api.Cvars.GetFloat(name);
        return v != 0f ? v : fallback;
    }

    // QC: start_items & IT_UNLIMITED_AMMO. The dominant (gametype-independent) source of that start flag is
    // g_use_ammunition 0 (readplayerstartcvars). Read it default-1-aware so an unset cvar means "ammo used".
    private static bool StartItemsHasUnlimitedAmmo() => !CvarBool("g_use_ammunition", true);

    // Read a boolean cvar default-aware: an unset cvar uses the fallback (distinguishes "unset" from explicit 0,
    // unlike the float Cvar helper which treats 0 as "use the fallback").
    private static bool CvarBool(string name, bool fallback)
    {
        if (Api.Services is null) return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : Api.Cvars.GetFloat(name) != 0f;
    }
}
