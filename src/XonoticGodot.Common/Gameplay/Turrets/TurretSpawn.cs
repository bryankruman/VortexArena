using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Shared turret spawn + projectile helpers — the headless slice of <c>turret_initialize</c> and
/// <c>turret_projectile</c> (common/turrets/sv_turrets.qc). Centralises the boilerplate every turret's
/// <see cref="Turret.Spawn"/> repeats (health/model/solid/think wiring + seeding the runtime
/// <see cref="TurretState"/> + the damage/use lifecycle hooks) and the generic projectile spawn the
/// plasma/MLRS/flac turrets share. Godot-free: operates only on <see cref="Entity"/> + <see cref="Api"/>.
/// </summary>
public static class TurretSpawn
{
    /// <summary>Detonation closures for shootable turret projectiles, keyed by entity (stands in for the QC per-edict event_damage).</summary>
    private static readonly Dictionary<Entity, Action<Entity>> _shootdown = new();
    private static bool _projDeathHooked;

    /// <summary>Detonate a shootable turret projectile that the damage pipeline destroys (FLAC vs plasma/mlrs balls).</summary>
    private static void EnsureProjectileDeathHook()
    {
        if (_projDeathHooked) return;
        _projDeathHooked = true;
        Combat.Death.Add(static (ref DeathEvent ev) =>
        {
            if (_shootdown.TryGetValue(ev.Victim, out var explode)) explode(ev.Victim);
            return false;
        });
    }

    /// <summary>
    /// Common turret entity setup (<c>turret_initialize</c>/<c>turret_respawn</c>): set className, model,
    /// hitbox, solidity, full health, seed the <see cref="TurretState"/> ammo pool + volley, and wire the
    /// activation (<c>turret_use</c>) + damage/retaliation/death (<c>turret_damage</c>/<c>turret_die</c>)
    /// lifecycle. <paramref name="respawnTime"/> drives the post-death respawn; <paramref name="noRespawn"/>
    /// makes death permanent (TSL_NO_RESPAWN). <paramref name="movable"/> lets damage shove mobile turrets
    /// (TUR_FLAG_MOVE). Skips engine/networking pieces (link, tag_head attachment, manager entity) — those are
    /// client-render / server-frame concerns owned elsewhere.
    /// </summary>
    public static TurretState Init(Turret def, Entity e, Vector3 mins, Vector3 maxs,
        float ammoMax, float ammoRecharge, int shotVolly,
        float respawnTime = 60f, bool noRespawn = false, bool movable = false)
    {
        e.ClassName = "turret_" + def.NetName;
        e.NetName = def.NetName;
        e.MaxHealth = def.StartHealth;
        e.Health = def.StartHealth;
        e.SetResourceExplicit(ResourceType.Health, def.StartHealth);
        e.TakeDamage = DamageMode.Aim;          // QC DAMAGE_AIM
        e.Solid = Solid.BBox;                   // QC SOLID_BBOX
        e.MoveType = MoveType.Noclip;           // QC MOVETYPE_NOCLIP (fixed emplacement)
        e.DeadState = DeadFlag.No;

        // QC: turrets default to a nonzero team (FLOAT_MAX) so SAME_TEAM gating works without teamplay; they
        // are ACTIVE_ACTIVE on spawn. turret_use overrides team+active when triggered.
        if (e.Team == 0f) e.Team = float.MaxValue;

        if (Api.Services is not null)
        {
            if (def.Model is not null) Api.Entities.SetModel(e, def.Model);
            Api.Entities.SetSize(e, mins, maxs);
        }

        TurretState st = TurretAI.State(e);
        st.AmmoMax = ammoMax;
        st.AmmoRecharge = ammoRecharge;
        st.Ammo = ammoMax;
        st.VollyCounter = shotVolly > 1 ? shotVolly : 1;
        st.AttackFinished = 0f;
        st.Active = true;
        st.RespawnTime = respawnTime;
        st.NoRespawn = noRespawn;
        st.Movable = movable;
        st.IdleAim = Vector3.Zero;
        st.HeadAngles = Vector3.Zero;
        st.HeadAVelocity = Vector3.Zero;
        st.ShotOrg = TurretAI.ShotOrigin(e);

        // Lifecycle hooks (QC turret_use / turret_damage / turret_die). turret_use swaps the team+active state
        // on trigger. The headless DamageSystem has no per-entity event_damage hook, so death+respawn is driven
        // off the shared Combat.Death subscription (TurretAI.EnsureDeathHook); the pre-damage gating
        // (inactive/friendly-fire) + retaliation live in TurretAI.Damage, the entrypoint the server damage
        // router calls for turrets.
        e.Use = (self, activator) => TurretAI.Use(self, activator);
        TurretAI.EnsureDeathHook();

        return st;
    }

    /// <summary>
    /// Port of <c>turret_projectile</c> (sv_turrets.qc): spawn a generic turret missile from the muzzle along
    /// <paramref name="dir"/> at <paramref name="speed"/>, with a deterministic <c>shot_spread</c> cone
    /// (<see cref="Prandom"/>), a touch-explode that does radius damage, and (if <paramref name="health"/> &gt; 0)
    /// a shootable hull that explodes when destroyed (turret_projectile_damage). Used by the plasma / MLRS /
    /// flac / hellion / hk / ewheel turrets (each tweaks the result). Returns the projectile entity.
    /// </summary>
    public static Entity Projectile(Entity turret, Vector3 origin, Vector3 dir, float speed, float size,
        float health, float damage, float edgeDamage, float radius, float force, int deathType,
        float spread = 0f)
    {
        Entity proj = Api.Entities.Spawn();
        proj.ClassName = "turret_projectile";
        proj.Owner = turret;
        proj.Enemy = turret.Enemy;
        proj.MoveType = MoveType.FlyMissile;     // QC MOVETYPE_FLYMISSILE
        proj.Solid = Solid.BBox;
        proj.Flags = EntFlags.Item;              // QC FL_PROJECTILE stand-in
        proj.Team = turret.Team;                 // inherit team so it doesn't read as an enemy missile to allies

        // Deterministic spread cone (QC: normalize(dir + randomvec() * shot_spread) * shot_speed, ADR-0010).
        Vector3 fireDir = QMath.Normalize(dir);
        if (spread > 0f)
            fireDir = QMath.Normalize(fireDir + Prandom.Vec() * spread);
        proj.Velocity = fireDir * speed;
        proj.Angles = QMath.VecToAngles(proj.Velocity);

        Vector3 half = new Vector3(0.5f, 0.5f, 0.5f) * size;
        Api.Entities.SetSize(proj, -half, half);
        Api.Entities.SetOrigin(proj, origin);

        void Explode(Entity self)
        {
            self.Touch = null;
            self.Think = null;
            self.TakeDamage = DamageMode.No;
            WeaponSplash.RadiusDamage(self, self.Origin, damage, edgeDamage, radius, self.Owner, deathType, force);
            _shootdown.Remove(self);
            Api.Entities.Remove(self);
            TurretAI.Forget(self);
        }

        if (health > 0f)
        {
            // Shootable hull (FLAC shoots these down). The headless DamageSystem has no per-entity
            // event_damage hook, so detonation-when-destroyed flows through the shared death hook
            // (turret_projectile_damage -> W_PrepareExplosionByDamage).
            proj.TakeDamage = DamageMode.Yes;
            proj.Health = health;
            proj.SetResourceExplicit(ResourceType.Health, health);
            _shootdown[proj] = Explode;
            EnsureProjectileDeathHook();
        }
        else
        {
            proj.TakeDamage = DamageMode.No;
            proj.Flags |= EntFlags.NoTarget;     // QC FL_NOTARGET
        }

        proj.Touch = (self, _) => Explode(self);
        proj.Think = Explode;                    // lifetime fallback
        proj.NextThink = (Api.Services is not null ? Api.Clock.Time : 0f) + 9f; // QC nextthink = time + 9

        return proj;
    }
}
