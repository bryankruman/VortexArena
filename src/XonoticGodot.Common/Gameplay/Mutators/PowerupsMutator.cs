// Port of qcsrc/common/mutators/mutator/powerups/sv_powerups.qc (the consumer side of the powerups) +
// the per-powerup m_tick/m_apply/m_remove from powerup/{strength,shield,speed,invisibility}.qc.
//
// The powerup STATUS-EFFECT DEFS (strength/shield/speed/invisibility) and the PRODUCER (ItemPickupRules
// applying them on pickup) already exist; this is the CONSUMER side QC kept in sv_powerups.qc:
//   Damage_Calculate (30-59)            -> strength ×damage/×force, shield ×takedamage/×takeforce
//   PlayerPhysics_UpdateStats (179-186) -> speed ×highspeed (rides MutatorHooks.PlayerPhysics -> SpeedMultiplier)
//   WeaponRateFactor (188-194)          -> speed ×attack_time_multiplier (the new WeaponRateFactor hook)
//   PlayerDies (149-161)                -> drop each active powerup as a pickup-able item (drop_ondeath)
//   invisibility m_tick (invisibility.qc:33) -> alpha = invisibility_alpha while active (PlayerPreThink)
// plus the per-effect m_tick/m_apply/m_remove the port has no per-effect tick for, folded into PlayerPreThink:
//   strength/shield m_tick (28-33)      -> actor.effects |= (EF_BLUE|EF_RED) | EF_ADDITIVE | EF_FULLBRIGHT glow
//   *.qc m_apply / m_remove(TIMEOUT)    -> INFO_POWERUP_X broadcast + CENTER_POWERUP_X / CENTER_POWERDOWN_X
//   *.qc m_tick play_countdown(SND_POWEROFF) -> the last-6-seconds countdown beep (client.qc:1532)
//
// All keyed off StatusEffects being active on the entity (QC StatusEffects_active), NOT a g_powerups cvar:
// the powerups mutator is always-registered in QC; its hooks only fire when a player HAS the effect. So this
// mutator is always enabled (IsEnabled true) and every handler self-gates on StatusEffectsCatalog.Has.
//
// Still deferred — these need cross-file seams that don't exist in the port yet (noted to the Wave-2/3 owners):
//   - the strength-fire sound (W_PlayStrengthSound — needs a fired-weapon hook, called from weapons/tracing.qc);
//   - the radar/monster/bot invisibility stealth (CustomizeWaypoint/MonsterValidTarget/Bot_ForbidAttack hooks
//     are not defined in MutatorHooks);
//   - the obituary item codes + server-browser mutator strings (LogDeath_AppendItemCodes/BuildMutatorsString
//     hooks are not defined);
//   - the +use drop (PlayerUseKey hook is being added in Wave-1 but isn't live for this file yet);
//   - the WP_Item waypoint sprite on a dropped powerup (no reachable WaypointSprite API here).

using System.Numerics;
using XonoticGodot.Common.Framework;
using static XonoticGodot.Common.Gameplay.Items;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Powerups consumer mutator — applies the gameplay effects of the strength / shield / speed /
/// invisibility powerups while a player holds them. Always enabled (the effects self-gate on the
/// status-effect being active, mirroring QC's always-registered powerups mutator).
/// </summary>
[Mutator]
public sealed class PowerupsMutator : MutatorBase
{
    public PowerupsMutator() => NetName = "powerups";

    // QC: the powerups mutator is REGISTER_MUTATOR(powerups, true) — always registered. Its hooks only have
    // an effect when a player actually holds a powerup, so "enabled" is simply "services available".
    public override bool IsEnabled => Api.Services is not null;

    // ---- balance defaults (balance-xonotic.cfg:228-240) ----
    private float _strengthDamage = 3f;        // g_balance_powerup_strength_damage
    private float _strengthForce = 3f;         // g_balance_powerup_strength_force
    private float _strengthSelfDamage = 1.5f;  // g_balance_powerup_strength_selfdamage
    private float _strengthSelfForce = 1.5f;   // g_balance_powerup_strength_selfforce
    private float _invincibleTakeDamage = 0.33f; // g_balance_powerup_invincible_takedamage
    private float _invincibleTakeForce = 0.33f;  // g_balance_powerup_invincible_takeforce
    private float _speedHighspeed = 1.5f;      // g_balance_powerup_speed_highspeed
    private float _speedAttackRate = 0.8f;     // g_balance_powerup_speed_attack_time_multiplier
    private float _invisibilityAlpha = 0.15f;  // g_balance_powerup_invisibility_alpha

    // ---- drop-on-death config (xonotic-server.cfg:208-209, 245) ----
    private int _dropOnDeath = 1;              // g_powerups_drop_ondeath (0=off, 1=timer continues, 2=freeze)
    private float _droppedLifetime = 20f;      // g_items_dropped_lifetime (frozen-timer drop lifetime)

    // QC EF_* render bits (dpextensions.qc) the strength/shield glow ORs into actor.effects. EF_ADDITIVE/
    // EF_FULLBRIGHT live in EntityMutatorState.EffectFlags; EF_BLUE/EF_RED are powerup-only here (the
    // client EF_BLUE/EF_RED -> dynamic-light path is in game/client/CsqcModelEffects.cs).
    private const int EfBlue = 64;   // EF_BLUE  (strength glow)
    private const int EfRed = 128;   // EF_RED   (shield glow)
    private const int StrengthGlow = EfBlue | EffectFlags.Additive | EffectFlags.FullBright;
    private const int ShieldGlow = EfRed | EffectFlags.Additive | EffectFlags.FullBright;

    // Per-player prior active-set, so PlayerPreThink can detect the apply/timeout EDGES the QC m_apply/m_remove
    // hooks fire on (the port has no per-effect tick). Keyed by entity; entries clear when the effect lapses.
    private readonly System.Collections.Generic.Dictionary<Entity, byte> _prevActive = new();

    // ---- hook handlers ----
    private HookHandler<MutatorHooks.DamageCalculateArgs>? _onDamageCalc;
    private HookHandler<MutatorHooks.PlayerPhysicsArgs>? _onPhysics;
    private HookHandler<MutatorHooks.WeaponRateFactorArgs>? _onRate;
    private HookHandler<MutatorHooks.PlayerPreThinkArgs>? _onPreThink;
    private HookHandler<MutatorHooks.PlayerDiesArgs>? _onPlayerDies;

    public override void Hook()
    {
        _onDamageCalc ??= OnDamageCalculate;
        _onPhysics ??= OnPlayerPhysics;
        _onRate ??= OnWeaponRateFactor;
        _onPreThink ??= OnPlayerPreThink;
        _onPlayerDies ??= OnPlayerDies;

        MutatorHooks.DamageCalculate.Add(_onDamageCalc);
        MutatorHooks.PlayerPhysics.Add(_onPhysics);
        MutatorHooks.WeaponRateFactor.Add(_onRate);
        MutatorHooks.PlayerPreThink.Add(_onPreThink);
        MutatorHooks.PlayerDies.Add(_onPlayerDies);

        if (Api.Services is not null)
        {
            R(ref _strengthDamage, "g_balance_powerup_strength_damage");
            R(ref _strengthForce, "g_balance_powerup_strength_force");
            R(ref _strengthSelfDamage, "g_balance_powerup_strength_selfdamage");
            R(ref _strengthSelfForce, "g_balance_powerup_strength_selfforce");
            R(ref _invincibleTakeDamage, "g_balance_powerup_invincible_takedamage");
            R(ref _invincibleTakeForce, "g_balance_powerup_invincible_takeforce");
            R(ref _speedHighspeed, "g_balance_powerup_speed_highspeed");
            R(ref _speedAttackRate, "g_balance_powerup_speed_attack_time_multiplier");
            R(ref _invisibilityAlpha, "g_balance_powerup_invisibility_alpha");
            R(ref _droppedLifetime, "g_items_dropped_lifetime");
            // g_powerups_drop_ondeath defaults to 1; a 0 is meaningful (off) so read it directly.
            _dropOnDeath = (int)Api.Cvars.GetFloat("g_powerups_drop_ondeath");
        }
    }

    public override void Unhook()
    {
        if (_onDamageCalc is not null) MutatorHooks.DamageCalculate.Remove(_onDamageCalc);
        if (_onPhysics is not null) MutatorHooks.PlayerPhysics.Remove(_onPhysics);
        if (_onRate is not null) MutatorHooks.WeaponRateFactor.Remove(_onRate);
        if (_onPreThink is not null) MutatorHooks.PlayerPreThink.Remove(_onPreThink);
        if (_onPlayerDies is not null) MutatorHooks.PlayerDies.Remove(_onPlayerDies);
        _prevActive.Clear();
    }

    private static void R(ref float field, string cvar)
    {
        float v = Api.Cvars.GetFloat(cvar);
        if (v != 0f) field = v;
    }

    // =====================================================================================
    //  Damage_Calculate (sv_powerups.qc:30) — strength on attacker, shield on target
    // =====================================================================================
    private bool OnDamageCalculate(ref MutatorHooks.DamageCalculateArgs args)
    {
        Entity? attacker = args.Attacker;
        Entity target = args.Target;

        // strength: scale the attacker's outgoing damage + force (self-hit uses the gentler self-multipliers).
        if (attacker is not null && Active(attacker, "strength"))
        {
            if (ReferenceEquals(target, attacker))
            {
                args.Damage *= _strengthSelfDamage;
                args.Force *= _strengthSelfForce;
            }
            else
            {
                args.Damage *= _strengthDamage;
                args.Force *= _strengthForce;
            }
        }

        // shield (QC "invincible"): the target takes reduced damage; reduced incoming force unless self-hit.
        if (Active(target, "shield"))
        {
            args.Damage *= _invincibleTakeDamage;
            if (!ReferenceEquals(target, attacker))
                args.Force *= _invincibleTakeForce;
        }
        return false;
    }

    // =====================================================================================
    //  PlayerPhysics_UpdateStats (sv_powerups.qc:179) — speed ×highspeed via SpeedMultiplier
    // =====================================================================================
    private bool OnPlayerPhysics(ref MutatorHooks.PlayerPhysicsArgs args)
    {
        Entity player = args.Player;
        // QC: STAT(MOVEVARS_HIGHSPEED, player) *= g_balance_powerup_speed_highspeed. PlayerPhysics.Move resets
        // SpeedMultiplier to 1 before this hook and applies it (ApplyHighSpeed) after, so a pure multiply here
        // composes with the buffs/entrap factors.
        if (Active(player, "speed"))
            player.SpeedMultiplier *= _speedHighspeed;
        return false;
    }

    // =====================================================================================
    //  WeaponRateFactor (sv_powerups.qc:188) — speed ×attack_time_multiplier
    // =====================================================================================
    private bool OnWeaponRateFactor(ref MutatorHooks.WeaponRateFactorArgs args)
    {
        // QC: if Speed active, M_ARGV(0, float) *= g_balance_powerup_speed_attack_time_multiplier (< 1 => faster).
        if (Active(args.Player, "speed"))
            args.Factor *= _speedAttackRate;
        return false;
    }

    // Bit per powerup in the prior-active bitmap (apply/timeout edge detection).
    private const byte AStrength = 1;
    private const byte AShield = 2;
    private const byte ASpeed = 4;
    private const byte AInvis = 8;

    // =====================================================================================
    //  Per-frame powerup tick (the QC per-effect m_tick / m_apply / m_remove, which the port has no per-effect
    //  tick hook for, run here under PlayerPreThink): invisibility alpha, strength/shield glow bits, the
    //  pickup/powerdown notifications (on the active EDGE), and the last-seconds countdown beep.
    // =====================================================================================
    private bool OnPlayerPreThink(ref MutatorHooks.PlayerPreThinkArgs args)
    {
        Entity player = args.Player;
        if ((player.Flags & EntFlags.Client) == 0) return false;

        bool strength = Active(player, "strength");
        bool shield = Active(player, "shield");
        bool speed = Active(player, "speed");
        bool invis = Active(player, "invisibility");

        // ---- invisibility alpha (InvisibilityStatusEffect.m_tick / m_remove, invisibility.qc:33-43) ----
        // QC sets actor.alpha = invisibility_alpha each frame while active and m_remove restores
        // default_player_alpha. Mirrors the BuffsMutator invisible-buff alpha handling: set while held,
        // restore to 1 on lapse.
        if (invis)
            player.Alpha = _invisibilityAlpha;
        else if (player.Alpha == _invisibilityAlpha)
            player.Alpha = 1f; // default_player_alpha

        // ---- strength/shield glow (strength.qc:31 / shield.qc:31 m_tick + m_remove:14) ----
        // QC m_tick ORs (EF_BLUE/EF_RED | EF_ADDITIVE | EF_FULLBRIGHT) into actor.effects every frame while
        // active; m_remove clears the same bits. Idempotent, so set/clear here without edge tracking.
        if (strength) player.Effects |= StrengthGlow; else player.Effects &= ~StrengthGlow;
        if (shield) player.Effects |= ShieldGlow; else player.Effects &= ~ShieldGlow;

        // ---- pickup/powerdown notifications + countdown beep (m_apply / m_remove(TIMEOUT) / m_tick) ----
        byte now = (byte)((strength ? AStrength : 0) | (shield ? AShield : 0)
                        | (speed ? ASpeed : 0) | (invis ? AInvis : 0));
        _prevActive.TryGetValue(player, out byte prev);
        if (now != prev)
        {
            // QC !g_cts gate on the INFO broadcast (strength.qc:22). g_cts is the CTS gametype flag.
            bool notCts = !(Api.Services is not null && Api.Cvars.GetFloat("g_cts") != 0f);
            NotifyEdge(player, strength, (prev & AStrength) != 0, "STRENGTH", notCts);
            NotifyEdge(player, shield, (prev & AShield) != 0, "SHIELD", notCts);
            NotifyEdge(player, speed, (prev & ASpeed) != 0, "SPEED", notCts);
            NotifyEdge(player, invis, (prev & AInvis) != 0, "INVISIBILITY", notCts);
            if (now == 0) _prevActive.Remove(player);
            else _prevActive[player] = now;
        }

        // QC every powerup m_tick: play_countdown(actor, StatusEffects_gettime(this, actor), SND_POWEROFF).
        if (strength) PlayCountdown(player, "strength");
        if (shield) PlayCountdown(player, "shield");
        if (speed) PlayCountdown(player, "speed");
        if (invis) PlayCountdown(player, "invisibility");

        return false;
    }

    // QC m_apply / m_remove(STATUSEFFECT_REMOVE_TIMEOUT): broadcast the pickup + center-print on the rising
    // edge, center-print the powerdown on the falling edge (the INFO_POWERDOWN broadcast is commented out in
    // Base, so only the CENTER fires on removal). Names map to the registered POWERUP_/POWERDOWN_ notifs.
    private static void NotifyEdge(Entity player, bool active, bool wasActive, string name, bool notCts)
    {
        if (active && !wasActive)
        {
            if (notCts)
                NotificationSystem.Info("POWERUP_" + name, player.NetName);
            NotificationSystem.Center(player, "POWERUP_" + name);
        }
        else if (!active && wasActive)
        {
            NotificationSystem.Center(player, "POWERDOWN_" + name);
        }
    }

    // QC play_countdown(this, finished, samp) (server/client.qc:1532): beep once per integer second crossed in
    // the last 6 seconds. floor(time_left - frametime) != floor(time_left) is the "crossed a second" test.
    private static void PlayCountdown(Entity player, string name)
    {
        if (Api.Services is null) return;
        var def = StatusEffectsCatalog.ByName(name);
        if (def is null) return;
        float finished = StatusEffectExpire(player, def);
        if (finished <= 0f) return; // permanent / not active
        float now = Api.Clock.Time;
        float frametime = Api.Clock.FrameTime;
        float timeLeft = finished - now;
        if (timeLeft < 6f
            && System.MathF.Floor(timeLeft - frametime) != System.MathF.Floor(timeLeft))
        {
            // CH_INFO, VOL_BASE, ATTEN_NORM → SoundChannel.Auto, default vol/atten.
            Api.Sound.Play(player, SoundChannel.Auto, "misc/poweroff.wav");
        }
    }

    // =====================================================================================
    //  PlayerDies (sv_powerups.qc:149) — drop each active powerup as a pickup-able item (drop_ondeath)
    // =====================================================================================
    private bool OnPlayerDies(ref MutatorHooks.PlayerDiesArgs args)
    {
        // QC: if(!autocvar_g_powerups_drop_ondeath) return; (default 1 = on, timer continues; 2 = freeze).
        if (_dropOnDeath == 0 || Api.Services is null) return false;

        Entity target = args.Target;
        bool freezeTimer = _dropOnDeath == 2;

        // QC FOREACH(StatusEffects, it.instanceOfPowerupStatusEffect, …): drop the four powerups it holds.
        DropPowerup(target, "strength", freezeTimer);
        DropPowerup(target, "shield", freezeTimer);
        DropPowerup(target, "speed", freezeTimer);
        DropPowerup(target, "invisibility", freezeTimer);
        return false;
    }

    // Port of powerups_DropItem (sv_powerups.qc:93): spawn a pickup-able item carrying the powerup's REMAINING
    // (freeze, mode 2) or ABSOLUTE (continue, mode 1) timer. ItemTouch re-applies it on pickup — for an
    // expiring (continue) drop it stores the absolute finish time and ItemTouch subtracts `time`; for a frozen
    // drop expiring is off and the stored value is the remaining seconds (max(existing,dur) on pickup).
    // Deferred vs QC: the WP_Item countdown waypoint sprite (no reachable WaypointSprite API in this file).
    private void DropPowerup(Entity owner, string name, bool freezeTimer)
    {
        var def = StatusEffectsCatalog.ByName(name);
        if (def is null) return;
        float finishedAbs = StatusEffectExpire(owner, def);
        if (finishedAbs <= 0f) return; // not active / permanent

        float now = Api.Clock.Time;
        float timeLeft = finishedAbs - now;
        if (timeLeft <= 1f) return; // QC: if(timeleft <= 1) return;

        // QC: finished = freezeTimer ? timeleft : t (remaining vs absolute end time).
        float finished = freezeTimer ? timeLeft : finishedAbs;
        // QC: e.lifetime = freezeTimer ? g_items_dropped_lifetime : timeleft.
        float lifetime = freezeTimer ? _droppedLifetime : timeLeft;

        Entity e = Api.Entities.Spawn();
        switch (name)
        {
            case "strength":     e.ClassName = "item_strength";     e.NetName = "strength";   e.StrengthFinished = finished;     break;
            case "shield":       e.ClassName = "item_invincible";   e.NetName = "invincible"; e.InvincibleFinished = finished;   break;
            case "speed":        e.ClassName = "item_speed";        e.NetName = "speed";      e.SpeedFinished = finished;        break;
            case "invisibility": e.ClassName = "item_invisibility"; e.NetName = "invisibility"; e.InvisibilityFinished = finished; break;
            default: Api.Entities.Remove(e); return;
        }

        Vector3 org = owner.Origin;
        Api.Entities.SetOrigin(e, org);
        e.Origin = org;
        // QC W_CalculateProjectileVelocity(this, velocity, v_forward * 750, false) — toss forward off the corpse.
        e.Velocity = owner.Velocity + QMath.Forward(owner.Angles) * 750f;
        e.MoveType = MoveType.Toss;
        e.Solid = Solid.Trigger;
        e.Flags |= EntFlags.Item;
        e.ItemIsLoot = true;

        // QC: if(!freezeTimer) ITEM_SET_EXPIRING(e, true). The continue-timer drop expires itself; ItemTouch
        // converts the stored ABSOLUTE finish to remaining by subtracting `time` on pickup.
        if (!freezeTimer)
            e.ItemIsExpiring = true;

        // The dropped item is picked up through the canonical Item_Touch path.
        e.Touch = (self, other) => ItemPickupRules.ItemTouch(self, other);

        // QC e.lifetime drives the dropped-item think; expire the loot when it runs out.
        e.NextThink = now + lifetime;
        e.Think = self => Api.Entities.Remove(self);
    }

    // =====================================================================================
    //  helpers
    // =====================================================================================

    // QC StatusEffects_active(STATUSEFFECT_X, e) -> StatusEffectsCatalog.Has(e, ByName("x")).
    private static bool Active(Entity? e, string name)
    {
        if (e is null) return false;
        var def = StatusEffectsCatalog.ByName(name);
        return def is not null && StatusEffectsCatalog.Has(e, def);
    }

    // QC StatusEffects_gettime(def, e): the ABSOLUTE expire time, 0 when not active/permanent.
    private static float StatusEffectExpire(Entity e, StatusEffectDef def)
    {
        foreach (var s in e.StatusEffects)
            if (s.DefId == def.RegistryId) return s.ExpireTime;
        return 0f;
    }
}
