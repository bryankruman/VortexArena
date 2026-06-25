using System.Numerics;
using XonoticGodot.Common.Framework;

namespace XonoticGodot.Common.Gameplay.Damage;

/// <summary>
/// A damage event (QC <c>Damage(targ, inflictor, attacker, damage, deathtype, hitloc, force)</c>,
/// server/damage.qc). <see cref="DeathType"/> is a string tag (e.g. the attacking weapon's NetName,
/// "fall", "drown", "void") — a lightweight stand-in for the QC deathtype registry for now.
/// </summary>
public struct DamageInfo
{
    public Entity Target;
    public Entity? Inflictor;   // the projectile/entity that dealt it
    public Entity? Attacker;    // the player credited
    public float Amount;
    public string DeathType;
    public Vector3 HitLocation;
    public Vector3 Force;       // knockback impulse
}

/// <summary>Fired when an entity is killed (QC obituary / frag scoring hook). Gametypes subscribe to score frags.</summary>
public struct DeathEvent
{
    public Entity Victim;
    public Entity? Attacker;
    public Entity? Inflictor;
    public string DeathType;
}

/// <summary>The damage pipeline (armor/health split, force/knockback, Killed handling). Installed onto <see cref="Combat"/>.</summary>
public interface IDamageSystem
{
    /// <summary>Apply damage. Returns the damage actually dealt after armor/modifiers.</summary>
    float Apply(in DamageInfo info);
}

/// <summary>
/// Ambient access to the damage pipeline + the death hook bus. Weapons/gametypes call
/// <see cref="Damage"/>; the installed <see cref="IDamageSystem"/> fires <see cref="Death"/> on a kill.
/// </summary>
public static class Combat
{
    private sealed class NoDamage : IDamageSystem
    {
        public float Apply(in DamageInfo info) => 0f;  // no-op until installed
    }

    public static IDamageSystem System { get; set; } = new NoDamage();

    /// <summary>The obituary/kill hook (gametypes subscribe to award frags). Fired by the damage system.</summary>
    public static readonly HookChain<DeathEvent> Death = new();

    public static float Damage(Entity target, Entity? inflictor, Entity? attacker,
        float amount, string deathType, Vector3 hitLocation, Vector3 force)
        => System.Apply(new DamageInfo
        {
            Target = target,
            Inflictor = inflictor,
            Attacker = attacker,
            Amount = amount,
            DeathType = deathType,
            HitLocation = hitLocation,
            Force = force,
        });

    /// <summary>
    /// QC <c>Heal(targ, inflictor, amount, limit)</c> (server/damage.qc:948): the generic heal entry — dispatches
    /// to <see cref="Entity.GtEventHeal"/> when the target carries one (an Onslaught generator / control-point
    /// icon sets it to ons_GeneratorHeal / ons_ControlPoint_Icon_Heal), else does nothing (QC returns false).
    /// Bails on a dead/frozen target like QC. Returns true if any health was added.
    /// </summary>
    public static bool Heal(Entity target, Entity? inflictor, float amount, float limit)
    {
        if (target.IsFreed || target.DeadState != DeadFlag.No || target.TakeDamage == DamageMode.No)
            return false; // QC: game_stopped / IS_DEAD(targ) / FROZEN guard
        return target.GtEventHeal?.Invoke(target, inflictor, amount, limit) ?? false;
    }
}
