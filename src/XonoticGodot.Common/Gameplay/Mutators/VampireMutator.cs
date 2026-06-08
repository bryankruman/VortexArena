using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Vampire mutator — port of common/mutators/mutator/vampire/sv_vampire.qc. When enabled, an attacker
/// is healed for the damage they deal to other players. This is the sample that demonstrates the C# hook
/// system replacing QC's MUTATOR_HOOKFUNCTION + M_ARGV slots: it subscribes a <see cref="HookHandler{TArgs}"/>
/// to <see cref="GameHooks.PlayerDamageSplitHealthArmor"/> on enable and removes it on disable, reading and
/// (potentially) writing the <c>ref</c> args struct instead of global argv slots.
/// </summary>
[Mutator]
public sealed class VampireMutator : MutatorBase
{
    /// <summary>QC autocvar_g_vampire_factor — fraction of dealt damage returned as health.</summary>
    public float Factor = 1.0f;

    /// <summary>QC autocvar_g_vampire_use_total_damage — heal off health+armor damage, not just health.</summary>
    public bool UseTotalDamage = false;

    public VampireMutator() => NetName = "vampire";

    /// <summary>
    /// QC: expr_evaluate(autocvar_g_vampire) &amp;&amp; !MUTATOR_IS_ENABLED(mutator_instagib).
    /// Driven by the g_vampire cvar through the facade (name preserved, OPEN Q5).
    /// </summary>
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_vampire") != 0f;

    // The handler instance must be stored so Unhook removes the same delegate it added.
    private HookHandler<GameHooks.PlayerDamageArgs>? _onPlayerDamage;

    public override void Hook()
    {
        _onPlayerDamage ??= OnPlayerDamage;
        GameHooks.PlayerDamageSplitHealthArmor.Add(_onPlayerDamage);

        if (Api.Services is not null)
        {
            float f = Api.Cvars.GetFloat("g_vampire_factor");
            if (f != 0f) Factor = f;
            UseTotalDamage = Api.Cvars.GetFloat("g_vampire_use_total_damage") != 0f;
        }
    }

    public override void Unhook()
    {
        if (_onPlayerDamage is not null)
            GameHooks.PlayerDamageSplitHealthArmor.Remove(_onPlayerDamage);
    }

    // MUTATOR_HOOKFUNCTION(vampire, PlayerDamage_SplitHealthArmor) — sv_vampire.qc
    private bool OnPlayerDamage(ref GameHooks.PlayerDamageArgs args)
    {
        Entity attacker = args.Attacker;
        Entity target = args.Target;

        // QC: health_take = bound(0, M_ARGV(4), GetResource(target, RES_HEALTH));
        float healthTake = QMath.Bound(0f, args.DamageTake, target.GetResource(ResourceType.Health));
        float armorTake = QMath.Bound(0f, args.DamageSave, target.GetResource(ResourceType.Armor));
        float damageTake = UseTotalDamage ? healthTake + armorTake : healthTake;

        // QC guards: target not spawn-shielded, attacker != target, attacker is a player, target alive/unfrozen.
        bool attackerIsPlayer = (attacker.Flags & EntFlags.Client) != 0;
        bool targetAlive = target.DeadState == DeadFlag.No;
        bool targetFrozen = StatusEffectsCatalog.Frozen is { } fz && StatusEffectsCatalog.Has(target, fz);
        // QC also skips spawn-shielded targets (STATUSEFFECT_SpawnShield); that effect isn't in the catalog
        // yet, so the alive + unfrozen + self checks are the faithful available subset.

        if (target != attacker && attackerIsPlayer && targetAlive && !targetFrozen && damageTake > 0f)
        {
            // QC: GiveResource(attacker, RES_HEALTH, factor * damageTake);
            // Note: under vampire, QC lets health exceed the usual 200 cap — deferred until the limit
            // override hook (GetResourceLimit) lands; here it clamps to the normal health limit.
            attacker.GiveResource(ResourceType.Health, Factor * damageTake);
        }

        return false; // not "exclusive" — let other handlers run (QC CBC_ORDER_ANY).
    }
}
