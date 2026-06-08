using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Fireball — port of common/weapons/weapon/fireball.{qh,qc}. A splash superweapon. Primary charges up
/// then launches a slow, large fireball (MOVETYPE_FLY) that deals heavy radius damage over a big radius on
/// impact, plus a BFG-style secondary blast on every visible enemy nearby. Secondary lobs gravity-affected
/// bouncing "firemines" (MOVETYPE_BOUNCE) that set players alight on contact. The fireball is shootable.
///
/// Identity/attributes from fireball.qh; balance from bal-wep-xonotic.cfg (g_balance_fireball_*).
/// This port covers both projectiles, the fireball's contact/lifetime explosion + the BFG secondary blast
/// on every visible enemy (LOS-gated, distance-scaled), the continuous laser scorch (W_Fireball_LaserPlay
/// applying a burning status effect), the firemine's bounce/ignite lifecycle (Fire_AddDamage burn over
/// damagetime), and shoot-down. Only the charge-up prefire animation frames are render-only. It uses no ammo.
/// </summary>
[Weapon]
public sealed class Fireball : Weapon
{
    /// <summary>Primary-fire (fireball) balance — QC WEP_CVAR_PRI(WEP_FIREBALL, *).</summary>
    public struct PrimaryBalance
    {
        public float Animtime;        // g_balance_fireball_primary_animtime
        public float BfgDamage;       // g_balance_fireball_primary_bfgdamage
        public float BfgForce;        // g_balance_fireball_primary_bfgforce
        public float BfgRadius;       // g_balance_fireball_primary_bfgradius
        public float Damage;          // g_balance_fireball_primary_damage
        public float DamageForceScale;// g_balance_fireball_primary_damageforcescale
        public float EdgeDamage;      // g_balance_fireball_primary_edgedamage
        public float Force;           // g_balance_fireball_primary_force
        public float Health;          // g_balance_fireball_primary_health (shootable; 0 = not)
        public float LaserBurnTime;   // g_balance_fireball_primary_laserburntime
        public float LaserDamage;     // g_balance_fireball_primary_laserdamage
        public float LaserEdgeDamage; // g_balance_fireball_primary_laseredgedamage
        public float LaserRadius;     // g_balance_fireball_primary_laserradius
        public float Lifetime;        // g_balance_fireball_primary_lifetime
        public float Radius;          // g_balance_fireball_primary_radius
        public float Refire;          // g_balance_fireball_primary_refire
        public float Refire2;         // g_balance_fireball_primary_refire2
        public float Speed;           // g_balance_fireball_primary_speed
        public float Spread;          // g_balance_fireball_primary_spread
    }

    /// <summary>Secondary-fire (firemine) balance — QC WEP_CVAR_SEC(WEP_FIREBALL, *).</summary>
    public struct SecondaryBalance
    {
        public float Animtime;        // g_balance_fireball_secondary_animtime
        public float Damage;          // g_balance_fireball_secondary_damage (fire-on-touch)
        public float DamageForceScale;// g_balance_fireball_secondary_damageforcescale
        public float DamageTime;      // g_balance_fireball_secondary_damagetime (burn duration)
        public float LaserBurnTime;   // g_balance_fireball_secondary_laserburntime
        public float LaserDamage;     // g_balance_fireball_secondary_laserdamage
        public float LaserEdgeDamage; // g_balance_fireball_secondary_laseredgedamage
        public float LaserRadius;     // g_balance_fireball_secondary_laserradius
        public float Lifetime;        // g_balance_fireball_secondary_lifetime
        public float Refire;          // g_balance_fireball_secondary_refire
        public float Speed;           // g_balance_fireball_secondary_speed (forward launch speed)
        public float SpeedUp;         // g_balance_fireball_secondary_speed_up (vertical launch speed)
        public float SpeedZ;          // g_balance_fireball_secondary_speed_z
        public float Spread;          // g_balance_fireball_secondary_spread
    }

    public PrimaryBalance Primary;
    public SecondaryBalance Secondary;

    public Fireball()
    {
        NetName = "fireball";
        DisplayName = "Fireball";
        Impulse = 9;
        // WEP_FLAG_SUPERWEAPON | WEP_TYPE_SPLASH | WEP_FLAG_NODUAL
        SpawnFlags = WeaponFlags.SuperWeapon | WeaponFlags.TypeSplash | WeaponFlags.NoDual;
        Color = new Vector3(0.941f, 0.522f, 0.373f);
        ViewModel = "h_fireball.iqm";  // MDL_FIREBALL_VIEW
        WorldModel = "v_fireball.md3"; // MDL_FIREBALL_WORLD
        ItemModel = "g_fireball.md3";  // MDL_FIREBALL_ITEM
    }

    public override void Configure()
    {
        Primary.Animtime = Bal("g_balance_fireball_primary_animtime", 0.4f);
        Primary.BfgDamage = Bal("g_balance_fireball_primary_bfgdamage", 100f);
        Primary.BfgForce = Bal("g_balance_fireball_primary_bfgforce", 0f);
        Primary.BfgRadius = Bal("g_balance_fireball_primary_bfgradius", 1000f);
        Primary.Damage = Bal("g_balance_fireball_primary_damage", 200f);
        Primary.DamageForceScale = Bal("g_balance_fireball_primary_damageforcescale", 0f);
        Primary.EdgeDamage = Bal("g_balance_fireball_primary_edgedamage", 50f);
        Primary.Force = Bal("g_balance_fireball_primary_force", 600f);
        Primary.Health = Bal("g_balance_fireball_primary_health", 0f);
        Primary.LaserBurnTime = Bal("g_balance_fireball_primary_laserburntime", 0.5f);
        Primary.LaserDamage = Bal("g_balance_fireball_primary_laserdamage", 80f);
        Primary.LaserEdgeDamage = Bal("g_balance_fireball_primary_laseredgedamage", 20f);
        Primary.LaserRadius = Bal("g_balance_fireball_primary_laserradius", 256f);
        Primary.Lifetime = Bal("g_balance_fireball_primary_lifetime", 15f);
        Primary.Radius = Bal("g_balance_fireball_primary_radius", 200f);
        Primary.Refire = Bal("g_balance_fireball_primary_refire", 2f);
        Primary.Refire2 = Bal("g_balance_fireball_primary_refire2", 0f);
        Primary.Speed = Bal("g_balance_fireball_primary_speed", 1200f);
        Primary.Spread = Bal("g_balance_fireball_primary_spread", 0f);

        Secondary.Animtime = Bal("g_balance_fireball_secondary_animtime", 0.3f);
        Secondary.Damage = Bal("g_balance_fireball_secondary_damage", 40f);
        Secondary.DamageForceScale = Bal("g_balance_fireball_secondary_damageforcescale", 4f);
        Secondary.DamageTime = Bal("g_balance_fireball_secondary_damagetime", 5f);
        Secondary.LaserBurnTime = Bal("g_balance_fireball_secondary_laserburntime", 0.5f);
        Secondary.LaserDamage = Bal("g_balance_fireball_secondary_laserdamage", 50f);
        Secondary.LaserEdgeDamage = Bal("g_balance_fireball_secondary_laseredgedamage", 20f);
        Secondary.LaserRadius = Bal("g_balance_fireball_secondary_laserradius", 110f);
        Secondary.Lifetime = Bal("g_balance_fireball_secondary_lifetime", 7f);
        Secondary.Refire = Bal("g_balance_fireball_secondary_refire", 1.5f);
        Secondary.Speed = Bal("g_balance_fireball_secondary_speed", 900f);
        Secondary.SpeedUp = Bal("g_balance_fireball_secondary_speed_up", 100f);
        Secondary.SpeedZ = Bal("g_balance_fireball_secondary_speed_z", 0f);
        Secondary.Spread = Bal("g_balance_fireball_secondary_spread", 0f);
    }

    // METHOD(Fireball, wr_think) — common/weapons/weapon/fireball.qc
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        // The primary charge-up prefire (W_Fireball_Attack1_Frame0..4) is a weapon-frame animation sequence
        // played over fireball_primarytime before the launch — a client/render cadence; the launch itself
        // (gameplay) happens here on the primary edge.
        if (fire == FireMode.Primary)
        {
            if (PrepareAttack(actor, slot, fire))
                Attack1(actor, slot);
        }
        else if (fire == FireMode.Secondary)
        {
            if (PrepareAttack(actor, slot, fire))
                Attack2(actor, slot);
        }
    }

    // Refire/animtime from the (cvar-seeded) per-mode balance blocks.
    public override float RefireFor(FireMode fire) => fire == FireMode.Secondary ? Secondary.Refire : Primary.Refire;
    public override float AnimtimeFor(FireMode fire) => fire == FireMode.Secondary ? Secondary.Animtime : Primary.Animtime;

    // W_Fireball_Attack1 — launch a large, slow, shootable fireball that bursts on impact. fireball.qc
    private void Attack1(Entity actor, WeaponSlot slot)
    {
        // Fireball uses no ammo (wr_checkammo always true).
        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, new Vector3(-16, -16, -16), new Vector3(16, 16, 16));

        Entity proj = Api.Entities.Spawn();
        proj.ClassName = "plasma_prim";
        proj.Owner = actor;
        proj.NetName = NetName;
        proj.MoveType = MoveType.Fly;
        Projectiles.MakeTrigger(proj); // QC PROJECTILE_MAKETRIGGER (SOLID_CORPSE): transparent to the firer's movement
        proj.Flags = EntFlags.Item; // QC FL_PROJECTILE
        proj.Team = actor.Team;
        Api.Entities.SetSize(proj, new Vector3(-16, -16, -16), new Vector3(16, 16, 16));
        Api.Entities.SetOrigin(proj, shot.Origin);

        // Shootable fireball (event_damage -> W_Fireball_Damage) when health > 0.
        if (Primary.Health > 0f)
        {
            proj.TakeDamage = DamageMode.Yes;
            proj.Health = Primary.Health;
        }

        // W_SetupProjVelocity_PRI: velocity = w_shotdir * speed (spread normally 0).
        proj.Velocity = WeaponFiring.ProjectileVelocity(shot.Dir, Vector3.UnitZ, Primary.Speed, 0f, 0f, Primary.Spread);
        proj.Angles = QMath.VecToAngles(proj.Velocity);

        // pushltime = time + lifetime is the explode-at-end-of-life deadline (W_Fireball_Think).
        float deathTime = Api.Clock.Time + Primary.Lifetime;
        proj.Touch = (self, other) => Explode(self, other);
        proj.Think = self => OnFireballThink(self, deathTime);
        proj.NextThink = Api.Clock.Time;
        // W_Fireball_Damage: a shootable fireball (when health > 0) explodes when its HP is depleted.
        if (Primary.Health > 0f)
            proj.ProjectileDamage = (self, attacker) => Explode(self, null);

        // MUTATOR_CALLHOOK(EditProjectile, actor, proj) (fireball.qc W_Fireball_Attack1).
        var ep = new MutatorHooks.EditProjectileArgs(actor, proj);
        MutatorHooks.EditProjectile.Call(ref ep);

        Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/fireball_fire2.wav");
        EffectEmitter.Emit("FIREBALL_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);
    }

    // W_Fireball_Think — tick the fireball; scorch a nearby enemy each tick; explode at end of lifetime. fireball.qc
    private void OnFireballThink(Entity self, float deathTime)
    {
        if (Api.Clock.Time > deathTime)
        {
            Explode(self, null);
            return;
        }
        // Periodic laser scorch on a nearby enemy (sets them alight).
        LaserPlay(self, Primary.LaserRadius, Primary.LaserDamage, Primary.LaserEdgeDamage, Primary.LaserBurnTime);
        self.NextThink = Api.Clock.Time + 0.1f;
    }

    /// <summary>
    /// Port of W_Fireball_LaserPlay (fireball.qc): pick one nearby damageable enemy (weighted toward closer
    /// targets, preferring ones not already burning) within <paramref name="dist"/> and set it alight for
    /// <paramref name="burnTime"/> seconds, with the burn rate scaled by distance (damage at the core down
    /// to edgedamage at the rim).
    /// </summary>
    private void LaserPlay(Entity self, float dist, float damage, float edgeDamage, float burnTime)
    {
        if (damage <= 0f || Api.Services is null) return;

        Entity? chosen = null;
        float chosenDist = 0f;
        float bestWeight = -1f;
        bool chosenBurning = true;
        var burning = StatusEffectsCatalog.Burning;
        foreach (Entity e in Api.Entities.FindInRadius(self.Origin, dist).ToList())
        {
            if (e.TakeDamage != DamageMode.Aim || ReferenceEquals(e, self.Owner)) continue;
            Vector3 p = e.Origin + new Vector3(
                e.Mins.X + Prandom.Float() * (e.Maxs.X - e.Mins.X),
                e.Mins.Y + Prandom.Float() * (e.Maxs.Y - e.Mins.Y),
                e.Mins.Z + Prandom.Float() * (e.Maxs.Z - e.Mins.Z));
            float d = (self.Origin - p).Length();
            if (d >= dist) continue;

            // RandomSelection: weight 1/(1+d), and strongly prefer targets that aren't already burning.
            bool isBurning = burning is not null && StatusEffectsCatalog.Has(e, burning);
            float weight = 1f / (1f + d);
            // not-yet-burning targets win over burning ones regardless of weight.
            bool better = (!isBurning && chosenBurning) || (isBurning == chosenBurning && weight > bestWeight);
            if (chosen is null || better)
            {
                chosen = e; chosenDist = d; bestWeight = weight; chosenBurning = isBurning;
            }
        }

        if (chosen is not null && burning is not null)
        {
            float rate = damage + (edgeDamage - damage) * (chosenDist / dist);
            StatusEffectsCatalog.Apply(chosen, burning, burnTime, strength: rate, source: self.Owner ?? self);
        }
    }

    // W_Fireball_Explode — heavy radius damage + a BFG-style secondary blast on every visible enemy nearby,
    // then remove. fireball.qc
    private void Explode(Entity self, Entity? directHit)
    {
        self.Touch = null;
        self.Think = null;
        self.TakeDamage = DamageMode.No;

        Entity owner = self.Owner ?? self;
        bool ownerSurvived = owner.DeadState == DeadFlag.No;

        WeaponSplash.RadiusDamage(self, self.Origin, Primary.Damage, Primary.EdgeDamage, Primary.Radius,
            self.Owner, RegistryId, Primary.Force, directHit: directHit);

        // BFG secondary blast: every visible damageable target within bfgradius takes
        // bfgdamage * (1 - sqrt(dist/bfgradius)) + bfgforce, but only if the owner survived the direct blast.
        if (Primary.BfgRadius > 0f && ownerSurvived && Api.Services is not null)
        {
            Vector3 center = self.Origin;
            foreach (Entity e in Api.Entities.FindInRadius(center, Primary.BfgRadius).ToList())
            {
                if (e.TakeDamage == DamageMode.No || ReferenceEquals(e, owner)) continue;
                Vector3 targ = e.Origin + (e.Mins + e.Maxs) * 0.5f;
                float dist = (targ - center).Length();
                if (dist > Primary.BfgRadius) continue;
                // LOS gate: skip targets the fireball can't see.
                TraceResult los = Api.Trace.Trace(center, Vector3.Zero, Vector3.Zero, targ, MoveFilter.NoMonsters, self);
                if (los.Fraction < 1f && !ReferenceEquals(los.Ent, e)) continue;

                float points = 1f - MathF.Sqrt(dist / Primary.BfgRadius);
                Vector3 force = QMath.Normalize(targ - center) * (Primary.BfgForce * points);
                WeaponFiring.ApplyDamage(e, owner, Primary.BfgDamage * points, RegistryId, inflictor: self,
                    force: force, hitLoc: targ);
            }
        }

        EffectEmitter.Emit("FIREBALL_EXPLODE", self.Origin);
        Api.Entities.Remove(self);
    }

    // W_Fireball_Attack2 — lob a gravity-affected bouncing firemine that ignites players. fireball.qc
    private void Attack2(Entity actor, WeaponSlot slot)
    {
        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out Vector3 up);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, new Vector3(-4, -4, -4), new Vector3(4, 4, 4));

        Entity proj = Api.Entities.Spawn();
        proj.ClassName = "grenade";
        proj.Owner = actor;
        proj.NetName = NetName;
        proj.MoveType = MoveType.Bounce;
        Projectiles.MakeTrigger(proj); // QC PROJECTILE_MAKETRIGGER (SOLID_CORPSE): transparent to the firer's movement
        proj.Flags = EntFlags.Item; // QC FL_PROJECTILE
        proj.Gravity = 1f;
        Api.Entities.SetSize(proj, new Vector3(-4, -4, -4), new Vector3(4, 4, 4));
        Api.Entities.SetOrigin(proj, shot.Origin);

        // W_SetupProjVelocity_UP_SEC: velocity = normalize(w_shotdir + up*(speed_up/speed)) * speed.
        proj.Velocity = WeaponFiring.ProjectileVelocity(shot.Dir, up, Secondary.Speed, Secondary.SpeedUp, Secondary.SpeedZ, Secondary.Spread);
        proj.Angles = QMath.VecToAngles(proj.Velocity);

        float deathTime = Api.Clock.Time + Secondary.Lifetime;
        proj.Touch = (self, other) => FiremineTouch(self, other);
        proj.Think = self => FiremineThink(self, deathTime);
        proj.NextThink = Api.Clock.Time;

        // MUTATOR_CALLHOOK(EditProjectile, actor, proj) (fireball.qc W_Fireball_Attack2).
        var ep = new MutatorHooks.EditProjectileArgs(actor, proj);
        MutatorHooks.EditProjectile.Call(ref ep);

        Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/fireball_fire.wav");
        EffectEmitter.Emit("FIREBALL_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);
    }

    // W_Fireball_Firemine_Think — scorch a nearby enemy each tick; self-destruct at end of lifetime. fireball.qc
    private void FiremineThink(Entity self, float deathTime)
    {
        if (Api.Clock.Time > deathTime)
        {
            Api.Entities.Remove(self);
            return;
        }
        LaserPlay(self, Secondary.LaserRadius, Secondary.LaserDamage, Secondary.LaserEdgeDamage, Secondary.LaserBurnTime);
        self.NextThink = Api.Clock.Time + 0.1f;
    }

    // W_Fireball_Firemine_Touch — set the player it lands on alight (Fire_AddDamage burn over damagetime),
    // else bounce. fireball.qc
    private void FiremineTouch(Entity self, Entity other)
    {
        bool hitPlayer = other.TakeDamage == DamageMode.Aim || (other.Flags & EntFlags.Client) != 0;
        if (hitPlayer)
        {
            // Fire_AddDamage: apply `damage` over `damagetime` seconds as a burning status effect (the
            // status-effects tick deals the periodic burn). Credit the firemine's owner for the kill.
            var burning = StatusEffectsCatalog.Burning;
            if (burning is not null)
                StatusEffectsCatalog.Apply(other, burning, Secondary.DamageTime,
                    strength: Secondary.Damage, source: self.Owner ?? self);
            else // fall back to a direct hit if the burning effect isn't registered
                WeaponFiring.ApplyDamage(other, self.Owner ?? self, Secondary.Damage, RegistryId, inflictor: self);
            EffectEmitter.Emit("GRENADE_EXPLODE", self.Origin);
            Api.Entities.Remove(self);
            return;
        }
        // bounce off the world (engine MOVETYPE_BOUNCE reflects the velocity).
        self.Angles = QMath.VecToAngles(self.Velocity);
    }

    // METHOD(Fireball, wr_checkammo1/2) — fireball.qc (infinite ammo).
    public bool CheckAmmoPrimary(Entity actor) => true;
    public bool CheckAmmoSecondary(Entity actor) => true;
}
