using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The HLAC (Heavy Laser Assault Cannon) — port of common/weapons/weapon/hlac.{qh,qc}. A splash weapon
/// firing fast-moving energy bolts (like the Blaster's, but consuming cells). Primary rapid-fires single
/// bolts whose spread grows the longer fire is held; secondary fires a burst of <c>shots</c> randomly
/// scattered bolts at once. Each bolt flies straight (MOVETYPE_FLY) and bursts with radius damage on
/// contact or at end of lifetime.
///
/// Identity/attributes from hlac.qh; balance from bal-wep-xonotic.cfg (g_balance_hlac_*).
/// This port covers the primary single bolt with held-fire spread accumulation (misc_bulletcounter ->
/// spread_add, capped at spread_max), the random per-bolt secondary burst, recoil punchangle, and the
/// splash damage. Only the crouch spread modifier (needs the duck input flag) and client muzzle effects
/// are left out.
/// </summary>
[Weapon]
public sealed class Hlac : Weapon
{
    /// <summary>Primary-fire balance — QC WEP_CVAR_PRI(WEP_HLAC, *).</summary>
    public struct PrimaryBalance
    {
        public float Ammo;            // g_balance_hlac_primary_ammo (cells per bolt)
        public float Animtime;        // g_balance_hlac_primary_animtime
        public float Damage;          // g_balance_hlac_primary_damage
        public float EdgeDamage;      // g_balance_hlac_primary_edgedamage
        public float Force;           // g_balance_hlac_primary_force
        public float Lifetime;        // g_balance_hlac_primary_lifetime
        public float Radius;          // g_balance_hlac_primary_radius
        public float Refire;          // g_balance_hlac_primary_refire
        public float Speed;           // g_balance_hlac_primary_speed
        public float SpreadAdd;       // g_balance_hlac_primary_spread_add (per held shot)
        public float SpreadCrouchmod; // g_balance_hlac_primary_spread_crouchmod
        public float SpreadMax;       // g_balance_hlac_primary_spread_max
        public float SpreadMin;       // g_balance_hlac_primary_spread_min
    }

    /// <summary>Secondary-fire (burst) balance — QC WEP_CVAR_SEC(WEP_HLAC, *).</summary>
    public struct SecondaryBalance
    {
        public float Ammo;            // g_balance_hlac_secondary_ammo (cells for the whole burst)
        public float Animtime;        // g_balance_hlac_secondary_animtime
        public float Damage;          // g_balance_hlac_secondary_damage
        public float EdgeDamage;      // g_balance_hlac_secondary_edgedamage
        public float Force;           // g_balance_hlac_secondary_force
        public float Lifetime;        // g_balance_hlac_secondary_lifetime
        public float Radius;          // g_balance_hlac_secondary_radius
        public float Refire;          // g_balance_hlac_secondary_refire
        public int   Shots;           // g_balance_hlac_secondary_shots
        public float Speed;           // g_balance_hlac_secondary_speed
        public float Spread;          // g_balance_hlac_secondary_spread
        public float SpreadCrouchmod; // g_balance_hlac_secondary_spread_crouchmod
    }

    public PrimaryBalance Primary;
    public SecondaryBalance Secondary;

    /// <summary>g_balance_hlac_secondary — whether secondary fire is enabled.</summary>
    public bool SecondaryEnabled = true;


    public Hlac()
    {
        NetName = "hlac";
        AmmoType = ResourceType.Cells;   // QC ammo_type
        DisplayName = "Heavy Laser Assault Cannon";
        Impulse = 6;
        // WEP_FLAG_MUTATORBLOCKED | WEP_FLAG_RELOADABLE | WEP_TYPE_SPLASH
        SpawnFlags = WeaponFlags.MutatorBlocked | WeaponFlags.Reloadable | WeaponFlags.TypeSplash;
        Color = new Vector3(0.506f, 0.945f, 0.239f);
        ViewModel = "h_hlac.iqm";  // MDL_HLAC_VIEW
        WorldModel = "v_hlac.md3"; // MDL_HLAC_WORLD
        ItemModel = "g_hlac.md3";  // MDL_HLAC_ITEM
    }

    public override void Configure()
    {
        Primary.Ammo = Bal("g_balance_hlac_primary_ammo", 1f);
        Primary.Animtime = Bal("g_balance_hlac_primary_animtime", 0.03125f);
        Primary.Damage = Bal("g_balance_hlac_primary_damage", 10f);
        Primary.EdgeDamage = Bal("g_balance_hlac_primary_edgedamage", 5f);
        Primary.Force = Bal("g_balance_hlac_primary_force", 15f);
        Primary.Lifetime = Bal("g_balance_hlac_primary_lifetime", 5f);
        Primary.Radius = Bal("g_balance_hlac_primary_radius", 60f);
        Primary.Refire = Bal("g_balance_hlac_primary_refire", 0.078125f);
        Primary.Speed = Bal("g_balance_hlac_primary_speed", 6000f);
        Primary.SpreadAdd = Bal("g_balance_hlac_primary_spread_add", 0.001953125f);
        Primary.SpreadCrouchmod = Bal("g_balance_hlac_primary_spread_crouchmod", 0.25f);
        Primary.SpreadMax = Bal("g_balance_hlac_primary_spread_max", 0.03125f);
        Primary.SpreadMin = Bal("g_balance_hlac_primary_spread_min", 0f);

        Secondary.Ammo = Bal("g_balance_hlac_secondary_ammo", 6f);
        Secondary.Animtime = Bal("g_balance_hlac_secondary_animtime", 0.3f);
        Secondary.Damage = Bal("g_balance_hlac_secondary_damage", 15f);
        Secondary.EdgeDamage = Bal("g_balance_hlac_secondary_edgedamage", 7.5f);
        Secondary.Force = Bal("g_balance_hlac_secondary_force", 30f);
        Secondary.Lifetime = Bal("g_balance_hlac_secondary_lifetime", 5f);
        Secondary.Radius = Bal("g_balance_hlac_secondary_radius", 60f);
        Secondary.Refire = Bal("g_balance_hlac_secondary_refire", 1f);
        Secondary.Shots = BalInt("g_balance_hlac_secondary_shots", 6);
        Secondary.Speed = Bal("g_balance_hlac_secondary_speed", 6000f);
        Secondary.Spread = Bal("g_balance_hlac_secondary_spread", 0.15f);
        Secondary.SpreadCrouchmod = Bal("g_balance_hlac_secondary_spread_crouchmod", 0.5f);

        SecondaryEnabled = BalBool("g_balance_hlac_secondary", true);
    }

    // METHOD(HLAC, wr_think) — common/weapons/weapon/hlac.qc
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        var st = actor.WeaponState(slot);
        if (fire == FireMode.Primary)
        {
            // QC resets misc_bulletcounter on the first press, then W_HLAC_Attack_Frame increments it each
            // held tick so spread grows; the held-fire loop is driven by per-tick button input, so we
            // accumulate the counter across actual shots instead (releasing primary lets it cool via reset
            // on the next first press). Gated by the primary refire (QC weapon_prepareattack).
            if (PrepareAttack(actor, slot, fire))
            {
                Attack(actor, slot, st);
                ++st.MiscBulletCounter;
            }
        }
        else if (fire == FireMode.Secondary && SecondaryEnabled)
        {
            if (PrepareAttack(actor, slot, fire))
            {
                st.MiscBulletCounter = 0; // pressing secondary breaks the primary hold streak
                Attack2(actor, slot);
            }
        }
    }

    // Refire/animtime from the (cvar-seeded) per-mode balance blocks.
    public override float RefireFor(FireMode fire) => fire == FireMode.Secondary ? Secondary.Refire : Primary.Refire;
    public override float AnimtimeFor(FireMode fire) => fire == FireMode.Secondary ? Secondary.Animtime : Primary.Animtime;

    // W_HLAC_Attack — one rapid bolt; spread grows with the held-fire counter. hlac.qc
    private void Attack(Entity actor, WeaponSlot slot, WeaponSlotState st)
    {
        actor.TakeResource(AmmoType, Primary.Ammo);

        // spread = spread_min + spread_add*misc_bulletcounter, capped at spread_max.
        float spread = MathF.Min(Primary.SpreadMin + Primary.SpreadAdd * st.MiscBulletCounter, Primary.SpreadMax);
        // (crouch reduces spread by spread_crouchmod when ducked+grounded — needs the crouch input flag.)

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward);
        Recoil(actor);

        SpawnBolt(actor, shot.Origin, shot.Dir, Primary.Speed, Primary.Lifetime,
            Primary.Damage, Primary.EdgeDamage, Primary.Radius, Primary.Force, spread);

        Api.Sound.Play(actor, SoundChannel.WeaponAuto, "weapons/lasergun_fire.wav");
    }

    // W_HLAC_Attack2 — fire a burst of `shots` randomly-scattered bolts at once. hlac.qc
    private void Attack2(Entity actor, WeaponSlot slot)
    {
        actor.TakeResource(AmmoType, Secondary.Ammo);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward);
        Recoil(actor);

        int shots = Secondary.Shots;
        if (shots < 1) shots = 1;
        for (int j = 0; j < shots; ++j)
        {
            // Each bolt is an independent W_SetupProjVelocity_Basic with the full secondary spread.
            SpawnBolt(actor, shot.Origin, shot.Dir, Secondary.Speed, Secondary.Lifetime,
                Secondary.Damage, Secondary.EdgeDamage, Secondary.Radius, Secondary.Force, Secondary.Spread);
        }

        Api.Sound.Play(actor, SoundChannel.WeaponAuto, "weapons/lasergun_fire.wav");
    }

    /// <summary>Spawn an HLAC laser bolt that bursts (radius damage) on touch or lifetime. hlac.qc.</summary>
    private void SpawnBolt(Entity actor, Vector3 origin, Vector3 dir, float speed, float lifetime,
        float damage, float edge, float radius, float force, float spread)
    {
        Entity missile = Api.Entities.Spawn();
        missile.ClassName = "hlacbolt";
        missile.Owner = actor;
        missile.NetName = NetName;
        missile.MoveType = MoveType.Fly;
        Projectiles.MakeTrigger(missile); // QC PROJECTILE_MAKETRIGGER (SOLID_CORPSE): transparent to the firer's movement
        missile.Flags = EntFlags.Item; // QC FL_PROJECTILE
        Api.Entities.SetSize(missile, Vector3.Zero, Vector3.Zero);
        Api.Entities.SetOrigin(missile, origin);

        // W_SetupProjVelocity_Basic(missile, speed, spread) — random per-bolt spread (deterministic PRNG).
        missile.Velocity = WeaponFiring.ProjectileVelocity(dir, Vector3.UnitZ, speed, 0f, 0f, spread);
        missile.Angles = QMath.VecToAngles(missile.Velocity);

        int deathType = RegistryId;
        missile.Touch = (self, other) => Explode(self, damage, edge, radius, force, deathType);
        missile.Think = self => Api.Entities.Remove(self); // SUB_Remove at lifetime
        missile.NextThink = Api.Clock.Time + lifetime;

        // MUTATOR_CALLHOOK(EditProjectile, actor, missile) — fired per bolt (hlac.qc W_HLAC_Attack / Attack2).
        var ep = new MutatorHooks.EditProjectileArgs(actor, missile);
        MutatorHooks.EditProjectile.Call(ref ep);
    }

    // QC recoil: punchangle gets a small random kick on each shot (g_norecoil default off).
    private static void Recoil(Entity actor)
    {
        Vector3 p = actor.PunchAngle;
        p.X = Prandom.Float() - 0.5f;
        p.Y = Prandom.Float() - 0.5f;
        actor.PunchAngle = p;
    }

    // W_HLAC_Touch — radius damage + knockback at the impact point, then remove. hlac.qc
    private void Explode(Entity self, float damage, float edge, float radius, float force, int deathType)
    {
        self.Touch = null;
        self.Think = null;
        self.TakeDamage = DamageMode.No;

        WeaponSplash.RadiusDamage(self, self.Origin, damage, edge, radius, self.Owner, deathType, force);

        Api.Entities.Remove(self);
    }

    // METHOD(HLAC, wr_checkammo1) — hlac.qc
    public bool CheckAmmoPrimary(Entity actor) => actor.GetResource(AmmoType) >= Primary.Ammo;

    // METHOD(HLAC, wr_checkammo2) — hlac.qc
    public bool CheckAmmoSecondary(Entity actor) => actor.GetResource(AmmoType) >= Secondary.Ammo;
}
