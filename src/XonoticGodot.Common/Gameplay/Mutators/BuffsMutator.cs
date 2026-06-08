using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
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
    private float _vengeanceMult = 0.4f;         // g_buffs_vengeance_damage_multiplier
    private float _luckChance = 0.15f;           // g_buffs_luck_chance
    private float _luckMult = 2f;                // g_buffs_luck_damagemultiplier
    private float _magnetRangeItem = 250f;       // g_buffs_magnet_range_item
    private float _magnetRangeBuff = 100f;       // g_buffs_magnet_range_buff
    private float _pickupDelay = 0.7f;           // g_buffs_pickup_delay

    // ---- hook handlers ----
    private HookHandler<MutatorHooks.DamageCalculateArgs>? _onDamageCalc;
    private HookHandler<GameHooks.PlayerDamageArgs>? _onSplit;
    private HookHandler<MutatorHooks.PlayerRegenArgs>? _onRegen;
    private HookHandler<MutatorHooks.PlayerPhysicsArgs>? _onPhysics;
    private HookHandler<MutatorHooks.PlayerPreThinkArgs>? _onPreThink;

    public override void Hook()
    {
        _onDamageCalc ??= OnDamageCalculate;
        _onSplit ??= OnSplitHealthArmor;
        _onRegen ??= OnPlayerRegen;
        _onPhysics ??= OnPlayerPhysics;
        _onPreThink ??= OnPlayerPreThink;

        MutatorHooks.DamageCalculate.Add(_onDamageCalc);
        GameHooks.PlayerDamageSplitHealthArmor.Add(_onSplit);
        MutatorHooks.PlayerRegen.Add(_onRegen);
        MutatorHooks.PlayerPhysics.Add(_onPhysics);
        MutatorHooks.PlayerPreThink.Add(_onPreThink);

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
            R(ref _vengeanceMult, "g_buffs_vengeance_damage_multiplier");
            R(ref _luckChance, "g_buffs_luck_chance");
            R(ref _luckMult, "g_buffs_luck_damagemultiplier");
            R(ref _magnetRangeItem, "g_buffs_magnet_range_item");
            R(ref _magnetRangeBuff, "g_buffs_magnet_range_buff");
            R(ref _pickupDelay, "g_buffs_pickup_delay");

            // QC buffs_Initialize: spawn the configured number of buffs if none exist yet.
            SpawnInitialBuffs();
        }
    }

    public override void Unhook()
    {
        if (_onDamageCalc is not null) MutatorHooks.DamageCalculate.Remove(_onDamageCalc);
        if (_onSplit is not null) GameHooks.PlayerDamageSplitHealthArmor.Remove(_onSplit);
        if (_onRegen is not null) MutatorHooks.PlayerRegen.Remove(_onRegen);
        if (_onPhysics is not null) MutatorHooks.PlayerPhysics.Remove(_onPhysics);
        if (_onPreThink is not null) MutatorHooks.PlayerPreThink.Remove(_onPreThink);
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
    private static StatusEffectDef? Buff(string shortName) => StatusEffectsCatalog.ByName("buff_" + shortName);

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
        if (n == "ammo" && Api.Cvars.GetFloat("g_melee_only") != 0f) return false;
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
            SpawnBuff(RandomBuff());
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

    // void buff_Touch(entity this, entity toucher) — apply the buff to a touching player.
    private void BuffTouch(Entity item, Entity toucher)
    {
        if (Api.Services is null || VehicleCommon.GameStopped) return;
        if (!item.BuffActive || item.BuffDef is null) return;
        if (!IsPlayer(toucher) || toucher.DeadState != DeadFlag.No) return;
        if (Api.Clock.Time < toucher.BuffShield) return; // recently dropped/replaced a buff

        StatusEffectDef thebuff = item.BuffDef;

        // QC: if the player already holds a (different) buff, replace it; same buff → do nothing.
        StatusEffectDef? held = HeldBuff(toucher);
        if (held is not null)
        {
            if (held == thebuff) return;
            RemoveAllBuffs(toucher);
        }

        item.BuffActive = false;
        item.Owner = toucher;

        // QC: bufftime = this.buffs_finished ? ... : thebuff.m_time(thebuff); fall back to 999 / cvar time.
        float bufftime = BuffTime(thebuff);
        ApplyBuff(toucher, thebuff, bufftime);

        Api.Sound.Play(toucher, SoundChannel.Item, "misc/shield_respawn.wav");
        // QC Send_Notification(ITEM_BUFF_GOT, thebuff.m_id). The CENTER variant takes one float (the buff id)
        // and tells the picker "You got the <buff> buff!". (The buff id is the status-effect registry id.)
        NotificationSystem.Center(toucher, "ITEM_BUFF_GOT", thebuff.RegistryId);

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
        float cooldown = Cvar("g_buffs_cooldown_respawn", 3f);
        item.NextThink = Api.Clock.Time + cooldown;
        item.Think = self =>
        {
            self.Owner = null;
            self.BuffDef = RandomBuff();
            if (self.BuffDef?.Model is { } m) Api.Entities.SetModel(self, m);
            self.BuffActive = true;
        };
    }

    // =====================================================================================
    //  Apply / remove (the buff-specific m_apply/m_remove side effects)
    // =====================================================================================

    private void ApplyBuff(Entity actor, StatusEffectDef buff, float duration)
    {
        string n = ShortName(buff);

        // QC AmmoBuff.m_apply: grant unlimited ammo, remembering the previous state.
        if (n == "ammo")
        {
            actor.BuffAmmoPrevInfItems = (actor.Items & ItUnlimitedAmmo) != 0;
            actor.Items |= ItUnlimitedAmmo;
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
                actor.Items = BitSet(actor.Items, ItUnlimitedAmmo, actor.BuffAmmoPrevInfItems);
            if (n == "flight")
                actor.Gravity = actor.BuffFlightOldGravity;

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

        // QC: no recurrence for vengeance's own reflected damage.
        if (dt == "buff_vengeance") { return false; }

        // resistance — flat damage reduction (also on self-damage).
        if (Active(target, "resistance"))
            damage = QMath.Bound(0f, damage * (1f - _resistanceBlock), damage);

        // medic — survive a fatal hit with a sliver of health, by chance.
        if (Active(target, "medic") && attacker is not null
            && target.GetResource(ResourceType.Health) - damage <= 0f)
        {
            if (Prandom.Float() <= _medicSurviveChance)
                damage = MathF.Max(5f, target.GetResource(ResourceType.Health) - _medicSurviveHealth);
        }

        // jump — immune to fall damage.
        if (Active(target, "jump") && dt == DeathTypes.Fall)
            damage = 0f;

        // vengeance — reflect a share back at the attacker (after a short delay, like QC).
        if (Active(target, "vengeance") && attacker is not null && !ReferenceEquals(attacker, target))
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

        // luck — chance to crit for a damage multiplier.
        if (!ReferenceEquals(attacker, target) && Active(attacker, "luck") && _luckMult > 0f)
        {
            if (Prandom.Float() <= _luckChance)
                damage *= _luckMult;
        }

        args.Damage = damage;
        args.Force = force;
        return false;
    }

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

    // MagnetBuff: pull nearby items into the player by triggering their touch (QC m_tick).
    private void MagnetTick(Entity player)
    {
        float reach = MathF.Max(_magnetRangeItem, _magnetRangeBuff);
        foreach (Entity it in Api.Entities.FindInRadius(player.Origin, reach))
        {
            if ((it.Flags & EntFlags.Item) == 0 || it.IsFreed || ReferenceEquals(it, player)) continue;
            float range = it.ClassName == "item_buff" ? _magnetRangeBuff : _magnetRangeItem;
            if ((it.Origin - player.Origin).Length() > range) continue;
            it.Touch?.Invoke(it, player);
        }
    }

    // AmmoBuff: keep ammo topped up each frame (the unlimited-ammo flag is set on apply; this refills the
    // current weapon's pools so reload-based weapons never run dry).
    private static void AmmoTick(Entity player)
    {
        player.SetResource(ResourceType.Shells, MathF.Max(player.GetResource(ResourceType.Shells), 1f));
        player.SetResource(ResourceType.Bullets, MathF.Max(player.GetResource(ResourceType.Bullets), 1f));
        player.SetResource(ResourceType.Rockets, MathF.Max(player.GetResource(ResourceType.Rockets), 1f));
        player.SetResource(ResourceType.Cells, MathF.Max(player.GetResource(ResourceType.Cells), 1f));
        player.SetResource(ResourceType.Fuel, MathF.Max(player.GetResource(ResourceType.Fuel), 1f));
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
}
