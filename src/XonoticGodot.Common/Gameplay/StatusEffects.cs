using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// A status effect kind (QC common/mutators/mutator/status_effects + buffs/powerups). The full QC
/// <c>StatusEffects</c> registry: the core debuffs (frozen/burning/spawnshield/stunned/superweapon/webbed),
/// the powerups (strength/shield/speed/invisibility — <c>Buff</c> derives from <c>StatusEffect</c>), and the
/// buff set. Registered into <see cref="StatusEffectsCatalog"/> via RegisterAll (self-registering).
/// </summary>
public sealed class StatusEffectDef : IRegistered
{
    public int RegistryId { get; set; }
    public string Name = "";
    public string RegistryName => Name;
    public bool IsBuff;            // a buff (BuffsMutator-offerable) vs a core effect/powerup
    public bool Hidden;            // QC m_hidden — no HUD element (spawnshield/stunned/webbed)
    public float Lifetime;         // QC m_lifetime — default duration cap (0 = none)
    public string? Model;          // buff model / drop model
    public StatusEffectDef(string name, bool isBuff = false) { Name = name; IsBuff = isBuff; }
}

/// <summary>One active effect instance on an entity (with expiry + optional source/strength).</summary>
public struct ActiveStatusEffect
{
    public int DefId;
    public float ExpireTime;       // engine time when it ends (<=0 => permanent)
    public float Strength;         // effect magnitude (e.g. burn dps, buff level)
    public Entity? Source;         // who applied it (for kill credit)
}

/// <summary>The status-effect system: apply/remove/query + a per-frame tick (burn damage, expiry, buff timers).</summary>
public static class StatusEffectsCatalog
{
    public static IReadOnlyList<StatusEffectDef> All => Registry<StatusEffectDef>.All;
    public static StatusEffectDef? ByName(string name) => Registry<StatusEffectDef>.ByName(name);
    public static int Count => Registry<StatusEffectDef>.Count;

    // Well-known effects (resolved after RegisterAll).
    public static StatusEffectDef? Frozen => ByName("frozen");
    public static StatusEffectDef? Burning => ByName("burning");
    public static StatusEffectDef? SpawnShield => ByName("spawnshield");
    public static StatusEffectDef? Stunned => ByName("stunned");
    public static StatusEffectDef? Superweapon => ByName("superweapon");
    public static StatusEffectDef? Webbed => ByName("webbed");

    /// <summary>
    /// Mirror of the QC <c>StatusEffects</c> registry (REGISTER_STATUSEFFECT across status_effect/, powerups/,
    /// buffs/, monsters/spider). The buffs carry <see cref="StatusEffectDef.IsBuff"/> so BuffsMutator offers
    /// only those; powerups + core debuffs do not. Names match the XonoticGodot consumers (ItemPickupRules powerup
    /// timers use "shield"/"superweapon"; BuffsMutator + DeathTypes use the "buff_" prefix; MonsterFramework
    /// uses "webbed"/"shield"/"spawnshield").
    /// </summary>
    public static void RegisterAll()
    {
        void R(StatusEffectDef d) => Registry<StatusEffectDef>.Register(d);

        // --- core debuffs / status (status_effect/*.qh) ---
        R(new StatusEffectDef("frozen") { Hidden = true });
        R(new StatusEffectDef("burning") { Hidden = true });
        // STATUSEFFECT_SpawnShield (spawnshield.qh): hidden, 10s cap — post-spawn damage protection.
        R(new StatusEffectDef("spawnshield") { Hidden = true, Lifetime = 10f });
        // STATUSEFFECT_Stunned (stunned.qh): hidden, 10s cap — shockwave/onslaught stun.
        R(new StatusEffectDef("stunned") { Hidden = true, Lifetime = 10f });
        // STATUSEFFECT_Superweapon (superweapons.qh): superweapon ammo window (QC netname "superweapons";
        // XonoticGodot consumers use the singular "superweapon").
        R(new StatusEffectDef("superweapon"));
        // STATUSEFFECT_Webbed (monsters/monster/spider.qh): spider-web movement slow.
        R(new StatusEffectDef("webbed") { Hidden = true });

        // --- powerups (mutators/mutator/powerups/powerup/*.qh — Buff:StatusEffect, but not BuffsMutator buffs) ---
        R(new StatusEffectDef("strength"));
        R(new StatusEffectDef("shield"));      // QC ShieldStatusEffect netname "invincible"; consumers use "shield"
        R(new StatusEffectDef("speed"));
        R(new StatusEffectDef("invisibility"));

        // --- buffs (mutators/mutator/buffs/buff/*.qh): the exact QC set, offered by BuffsMutator ---
        foreach (var b in new[] { "ammo", "bash", "disability", "flight", "inferno", "jump", "luck",
                                  "magnet", "medic", "resistance", "swapper", "vampire", "vengeance" })
            R(new StatusEffectDef("buff_" + b, isBuff: true));

        Registry<StatusEffectDef>.Sort();
    }

    public static bool Has(Entity e, StatusEffectDef def)
    {
        foreach (var s in e.StatusEffects) if (s.DefId == def.RegistryId) return true;
        return false;
    }

    public static void Apply(Entity e, StatusEffectDef def, float duration, float strength = 1f, Entity? source = null)
    {
        float now = Api.Services != null ? Api.Clock.Time : 0f;
        // refresh if already present
        for (int i = 0; i < e.StatusEffects.Count; i++)
        {
            if (e.StatusEffects[i].DefId == def.RegistryId)
            {
                var cur = e.StatusEffects[i];
                cur.ExpireTime = duration > 0 ? now + duration : 0f;
                cur.Strength = strength;
                cur.Source = source;
                e.StatusEffects[i] = cur;
                return;
            }
        }
        e.StatusEffects.Add(new ActiveStatusEffect
        {
            DefId = def.RegistryId,
            ExpireTime = duration > 0 ? now + duration : 0f,
            Strength = strength,
            Source = source,
        });
    }

    public static void Remove(Entity e, StatusEffectDef def)
        => e.StatusEffects.RemoveAll(s => s.DefId == def.RegistryId);

    /// <summary>Per-frame tick: expire effects and apply periodic burn damage. Called by the server loop.</summary>
    public static void Tick(Entity e, float now)
    {
        for (int i = e.StatusEffects.Count - 1; i >= 0; i--)
        {
            var s = e.StatusEffects[i];
            if (s.ExpireTime > 0f && now >= s.ExpireTime) { e.StatusEffects.RemoveAt(i); continue; }
            // burning: periodic damage (full burn logic layered on by the status-effects subsystem)
            if (Burning != null && s.DefId == Burning.RegistryId)
            {
                Damage.Combat.Damage(e, null, s.Source, s.Strength * 0.05f, "burning", e.Origin, System.Numerics.Vector3.Zero);
            }
        }
    }
}
