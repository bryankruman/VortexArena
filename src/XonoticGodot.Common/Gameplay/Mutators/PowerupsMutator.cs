// Port of qcsrc/common/mutators/mutator/powerups/sv_powerups.qc (the consumer side of the powerups) +
// the per-powerup m_tick alpha from powerup/invisibility.qc:33-43.
//
// The powerup STATUS-EFFECT DEFS (strength/shield/speed/invisibility) and the PRODUCER (ItemPickupRules
// applying them on pickup) already exist; this is the CONSUMER side QC kept in sv_powerups.qc:
//   Damage_Calculate (30-59)            -> strength ×damage/×force, shield ×takedamage/×takeforce
//   PlayerPhysics_UpdateStats (179-186) -> speed ×highspeed (rides MutatorHooks.PlayerPhysics -> SpeedMultiplier)
//   WeaponRateFactor (188-194)          -> speed ×attack_time_multiplier (the new WeaponRateFactor hook)
//   invisibility m_tick (invisibility.qc:33) -> alpha = invisibility_alpha while active (PlayerPreThink)
//
// All keyed off StatusEffects being active on the entity (QC StatusEffects_active), NOT a g_powerups cvar:
// the powerups mutator is always-registered in QC; its hooks only fire when a player HAS the effect. So this
// mutator is always enabled (IsEnabled true) and every handler self-gates on StatusEffectsCatalog.Has.
//
// Deferred (consistent with the CloakedMutator precedent / the spec): the obituary item codes, the
// drop-on-death/use-key item drop, the strength-fire sound, and the radar/monster/bot invisibility hooks
// (CustomizeWaypoint/MonsterValidTarget/Bot_ForbidAttack — those chains aren't wired in the port yet).

using XonoticGodot.Common.Framework;
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

    // ---- hook handlers ----
    private HookHandler<MutatorHooks.DamageCalculateArgs>? _onDamageCalc;
    private HookHandler<MutatorHooks.PlayerPhysicsArgs>? _onPhysics;
    private HookHandler<MutatorHooks.WeaponRateFactorArgs>? _onRate;
    private HookHandler<MutatorHooks.PlayerPreThinkArgs>? _onPreThink;

    public override void Hook()
    {
        _onDamageCalc ??= OnDamageCalculate;
        _onPhysics ??= OnPlayerPhysics;
        _onRate ??= OnWeaponRateFactor;
        _onPreThink ??= OnPlayerPreThink;

        MutatorHooks.DamageCalculate.Add(_onDamageCalc);
        MutatorHooks.PlayerPhysics.Add(_onPhysics);
        MutatorHooks.WeaponRateFactor.Add(_onRate);
        MutatorHooks.PlayerPreThink.Add(_onPreThink);

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
        }
    }

    public override void Unhook()
    {
        if (_onDamageCalc is not null) MutatorHooks.DamageCalculate.Remove(_onDamageCalc);
        if (_onPhysics is not null) MutatorHooks.PlayerPhysics.Remove(_onPhysics);
        if (_onRate is not null) MutatorHooks.WeaponRateFactor.Remove(_onRate);
        if (_onPreThink is not null) MutatorHooks.PlayerPreThink.Remove(_onPreThink);
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

    // =====================================================================================
    //  invisibility m_tick (invisibility.qc:33) — alpha while active, restored on lapse
    // =====================================================================================
    private bool OnPlayerPreThink(ref MutatorHooks.PlayerPreThinkArgs args)
    {
        Entity player = args.Player;
        if ((player.Flags & EntFlags.Client) == 0) return false;

        // QC InvisibilityStatusEffect.m_tick sets actor.alpha = invisibility_alpha each frame while active and
        // m_remove restores default_player_alpha. The port has no per-effect tick, so do it here (mirrors the
        // BuffsMutator invisible-buff alpha handling): set the powerup alpha while held, restore to 1 on lapse.
        if (Active(player, "invisibility"))
        {
            player.Alpha = _invisibilityAlpha;
        }
        else if (player.Alpha == _invisibilityAlpha)
        {
            player.Alpha = 1f; // default_player_alpha
        }
        return false;
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
}
