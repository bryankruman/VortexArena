using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Blaster — port of common/weapons/weapon/blaster.{qh,qc}. A projectile weapon: primary fire spawns
/// a fast splash bolt that detonates on touch (or after a lifetime) dealing radius damage + knockback;
/// it has no real secondary (QC switches to the previous weapon).
///
/// Identity/attributes come from blaster.qh ATTRIBs; balance from bal-wep-xonotic.cfg
/// (g_balance_blaster_primary_*). The projectile is spawned through the entity-service facade.
/// </summary>
[Weapon]
public sealed class Blaster : Weapon
{
    /// <summary>Primary-fire balance block (QC WEP_CVAR_PRI(WEP_BLASTER, *)). Seeded by <see cref="Configure"/>.</summary>
    public struct Balance
    {
        public float Damage;       // g_balance_blaster_primary_damage
        public float EdgeDamage;   // g_balance_blaster_primary_edgedamage
        public float Radius;       // g_balance_blaster_primary_radius
        public float Force;        // g_balance_blaster_primary_force
        public float ForceZScale;  // g_balance_blaster_primary_force_zscale
        public float Speed;        // g_balance_blaster_primary_speed
        public float Spread;       // g_balance_blaster_primary_spread
        public float Refire;       // g_balance_blaster_primary_refire
        public float Animtime;     // g_balance_blaster_primary_animtime
        public float Delay;        // g_balance_blaster_primary_delay
        public float Lifetime;     // g_balance_blaster_primary_lifetime
        public float ShotAngle;    // g_balance_blaster_primary_shotangle (degrees)
    }

    public Balance Primary;

    public Blaster()
    {
        NetName = "blaster";
        DisplayName = "Blaster";
        Impulse = 1;
        // WEP_FLAG_NORMAL | WEP_FLAG_CANCLIMB | WEP_TYPE_SPLASH
        SpawnFlags = WeaponFlags.Normal | WeaponFlags.CanClimb | WeaponFlags.TypeSplash;
        Color = new Vector3(0.969f, 0.443f, 0.482f);
        ViewModel = "h_laser.iqm";   // MDL_BLASTER_VIEW
        WorldModel = "v_laser.md3";  // MDL_BLASTER_WORLD
        ItemModel = "g_laser.md3";   // MDL_BLASTER_ITEM
    }

    public override void Configure()
    {
        Primary.Damage      = Bal("g_balance_blaster_primary_damage", 20f);
        Primary.EdgeDamage  = Bal("g_balance_blaster_primary_edgedamage", 10f);
        Primary.Radius      = Bal("g_balance_blaster_primary_radius", 60f);
        Primary.Force       = Bal("g_balance_blaster_primary_force", 375f);
        Primary.ForceZScale = Bal("g_balance_blaster_primary_force_zscale", 1f);
        Primary.Speed       = Bal("g_balance_blaster_primary_speed", 6000f);
        Primary.Spread      = Bal("g_balance_blaster_primary_spread", 0f);
        Primary.Refire      = Bal("g_balance_blaster_primary_refire", 0.7f);
        Primary.Animtime    = Bal("g_balance_blaster_primary_animtime", 0.1f);
        Primary.Delay       = Bal("g_balance_blaster_primary_delay", 0f);
        Primary.Lifetime    = Bal("g_balance_blaster_primary_lifetime", 5f);
        Primary.ShotAngle   = Bal("g_balance_blaster_primary_shotangle", 0f);
    }

    // METHOD(Blaster, wr_think) — common/weapons/weapon/blaster.qc
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        if (fire == FireMode.Primary)
        {
            // OFFHAND path: the offhand-blaster mutator drives a dedicated high slot
            // (>= MutatorConstants.MaxWeaponSlots) outside the weapon-fire driver and gates the refire itself
            // (OffhandNextThink). That slot is never raised by the driver, so it has no READY/button state for
            // PrepareAttack — fire it directly. The normal in-hand Blaster (slot 0, driven by the fire driver)
            // goes through the refire gate (QC weapon_prepareattack).
            if (slot.Index >= MutatorConstants.MaxWeaponSlots)
                Attack(actor, slot);
            else if (PrepareAttack(actor, slot, fire))
                Attack(actor, slot);
        }
        else if (fire == FireMode.Secondary)
        {
            // QC swaps back to the previously selected weapon (W_LastWeapon) — but only when the Blaster is
            // actually the active/switch weapon (i.e. not when it's the offhand laser). This is a weapon-switch
            // action on the secondary press, not a refire-gated shot.
            if (actor.ActiveWeaponId == RegistryId)
                LastWeapon(actor);
        }
    }

    /// <summary>
    /// Port of W_LastWeapon (server/weapons/selection.qc): switch back to the weapon used before this one.
    /// QC tracks the previous selection in <c>cnt</c>; here it lives in <see cref="Entity.LastWeaponId"/>
    /// (populated by the weapon-switch system). If no valid previous weapon is recorded, fall back to the
    /// best owned weapon other than the Blaster (so the secondary still "puts the laser away").
    /// </summary>
    private void LastWeapon(Entity actor)
    {
        int last = actor.LastWeaponId;
        if (last >= 0 && last != RegistryId && last < Registry<Weapon>.Count
            && actor.OwnedWeaponSet.Has(last))
        {
            Inventory.SwitchWeapon(actor, Registry<Weapon>.ById(last));
            return;
        }
        // No recorded previous weapon: pick the best owned weapon that isn't us.
        Weapon? best = null;
        foreach (var w in actor.OwnedWeaponSet.Weapons())
            if (w.RegistryId != RegistryId && (best is null || w.Impulse > best.Impulse))
                best = w;
        if (best is not null) Inventory.SwitchWeapon(actor, best);
    }

    // Refire/animtime from the (cvar-seeded) balance block — the Blaster has a single primary fire mode.
    public override float RefireFor(FireMode fire) => Primary.Refire;
    public override float AnimtimeFor(FireMode fire) => Primary.Animtime;

    /// <summary>
    /// Fire one Blaster primary bolt directly (ungated) — for other weapons that reuse the Blaster as a
    /// secondary/offhand (Vaporizer Rocket-Minsta laser, the offhand blaster), which own their own refire
    /// gate. The in-hand Blaster should go through <see cref="WrThink"/> (the refire-gated path) instead.
    /// </summary>
    public void FirePrimaryDirect(Entity actor, WeaponSlot slot) => Attack(actor, slot);

    // W_Blaster_Attack — common/weapons/weapon/blaster.qc
    private void Attack(Entity actor, WeaponSlot slot)
    {
        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out Vector3 up);

        // s_forward = v_forward * cos(shotangle) + v_up * sin(shotangle)
        float shotAngle = Primary.ShotAngle * QMath.Deg2Rad;
        Vector3 sForward = forward * MathF.Cos(shotAngle) + up * MathF.Sin(shotAngle);

        ShotInfo shot = WeaponFiring.SetupShot(actor, sForward);

        // entity missile = new(blasterbolt);
        Entity missile = Api.Entities.Spawn();
        missile.ClassName = "blasterbolt";
        missile.Owner = actor;
        missile.NetName = NetName;
        Api.Entities.SetOrigin(missile, shot.Origin);
        Api.Entities.SetSize(missile, Vector3.Zero, Vector3.Zero);

        // W_SetupProjVelocity_Explicit(missile, w_shotdir, v_up, speed, 0, 0, spread, false)
        missile.Velocity = WeaponFiring.ProjectileVelocity(shot.Dir, up, Primary.Speed, 0f, 0f, Primary.Spread);
        missile.Angles = QMath.VecToAngles(missile.Velocity);
        missile.MoveType = MoveType.Fly;
        Projectiles.MakeTrigger(missile); // QC PROJECTILE_MAKETRIGGER (SOLID_CORPSE): transparent to the firer's movement
        missile.Flags = EntFlags.Item; // QC FL_PROJECTILE (no dedicated flag in EntFlags yet)

        // settouch(missile, W_Blaster_Touch); — detonate + radius damage on contact.
        missile.Touch = (self, other) => OnTouch(self, other);

        // setthink(missile, W_Blaster_Think) at +delay; the think arms MOVETYPE_FLY then SUB_Remove at +lifetime.
        missile.Think = self => OnThink(self);
        missile.NextThink = Api.Clock.Time + Primary.Delay;

        // MUTATOR_CALLHOOK(EditProjectile, actor, missile) — lets mutators edit the just-spawned bolt
        // (invincibleproj zeroes its health, etc.). QC fires this before the immediate-think dispatch below.
        var ep = new MutatorHooks.EditProjectileArgs(actor, missile);
        MutatorHooks.EditProjectile.Call(ref ep);

        if (Api.Clock.Time >= missile.NextThink)
            missile.Think(missile);

        Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/lasergun_fire.wav");

        // Deferred (client render/networking): MIF_SPLASH projectile flag, CSQCProjectile/PROJECTILE_BLASTER
        // spawn, muzzle flash effect.
    }

    // W_Blaster_Think — schedules removal after the bolt's lifetime.
    private void OnThink(Entity self)
    {
        self.MoveType = MoveType.Fly;
        self.Think = s => Api.Entities.Remove(s); // SUB_Remove
        self.NextThink = Api.Clock.Time + Primary.Lifetime;
    }

    // W_Blaster_Touch — radius damage + knockback (with the laser's signature z-launch boost) at the impact
    // point, then remove the bolt. Hits other projectiles directly too (QC g_projectiles_interact path).
    private void OnTouch(Entity self, Entity other)
    {
        self.Touch = null; // event_damage = func_null; prevent re-entry

        Vector3 center = self.Origin + (self.Mins + self.Maxs) * 0.5f;
        WeaponSplash.RadiusDamage(self, center, Primary.Damage, Primary.EdgeDamage, Primary.Radius,
            self.Owner, RegistryId, Primary.Force, forceZScale: Primary.ForceZScale, directHit: other);

        Api.Entities.Remove(self);
    }
}
