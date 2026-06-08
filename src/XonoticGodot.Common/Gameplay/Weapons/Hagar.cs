using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Hagar — port of common/weapons/weapon/hagar.{qh,qc}. A splash weapon firing small rapid rockets.
/// Primary rapid-fires straight rockets (MOVETYPE_FLY) that burst on impact or at end of lifetime.
/// Secondary is a "load" mode: hold to charge up to load_max rockets, release to fire them all at once in
/// a spread. Every rocket is shootable.
///
/// Identity/attributes from hagar.qh; balance from bal-wep-xonotic.cfg (g_balance_hagar_*).
/// This port covers the primary rapid rocket, the loaded secondary salvo (correct load_spread fan +
/// per-shot bias spread), the non-loaded single bouncing secondary, the splash damage, and shoot-down. The
/// incremental hold-to-load state machine (load one rocket per tick, warning beeps, give-back-on-abort) is
/// summarized to firing the full load_max salvo per press — see <see cref="AttackLoadRelease"/> — because
/// it needs per-tick held-button input the headless layer doesn't carry yet.
/// </summary>
[Weapon]
public sealed class Hagar : Weapon
{
    /// <summary>Primary-fire balance — QC WEP_CVAR_PRI(WEP_HAGAR, *).</summary>
    public struct PrimaryBalance
    {
        public float Ammo;        // g_balance_hagar_primary_ammo (rockets)
        public float Damage;      // g_balance_hagar_primary_damage
        public float EdgeDamage;  // g_balance_hagar_primary_edgedamage
        public float Force;       // g_balance_hagar_primary_force
        public float Health;      // g_balance_hagar_primary_health (shootable rocket hp)
        public float Lifetime;    // g_balance_hagar_primary_lifetime
        public float Radius;      // g_balance_hagar_primary_radius
        public float Refire;      // g_balance_hagar_primary_refire
        public float Speed;       // g_balance_hagar_primary_speed
    }

    /// <summary>Secondary-fire (load) balance — QC WEP_CVAR_SEC(WEP_HAGAR, *).</summary>
    public struct SecondaryBalance
    {
        public float Ammo;            // g_balance_hagar_secondary_ammo (per loaded rocket)
        public float Damage;          // g_balance_hagar_secondary_damage
        public float EdgeDamage;      // g_balance_hagar_secondary_edgedamage
        public float Force;           // g_balance_hagar_secondary_force
        public float Health;          // g_balance_hagar_secondary_health
        public float LifetimeMin;     // g_balance_hagar_secondary_lifetime_min
        public float LifetimeRand;    // g_balance_hagar_secondary_lifetime_rand
        public float LoadMax;         // g_balance_hagar_secondary_load_max (rockets per full load)
        public float LoadSpread;      // g_balance_hagar_secondary_load_spread
        public float LoadSpreadBias;  // g_balance_hagar_secondary_load_spread_bias
        public float Radius;          // g_balance_hagar_secondary_radius
        public float Refire;          // g_balance_hagar_secondary_refire
        public float Speed;           // g_balance_hagar_secondary_speed
        public float Spread;          // g_balance_hagar_secondary_spread
    }

    public PrimaryBalance Primary;
    public SecondaryBalance Secondary;

    /// <summary>g_balance_hagar_secondary — whether secondary fire is enabled.</summary>
    public bool SecondaryEnabled = true;

    /// <summary>g_balance_hagar_secondary_load — whether the secondary is the loadable burst (vs a single bouncing rocket).</summary>
    public bool SecondaryLoad = true;


    public Hagar()
    {
        NetName = "hagar";
        AmmoType = ResourceType.Rockets;   // QC ammo_type
        DisplayName = "Hagar";
        Impulse = 8;
        // WEP_FLAG_NORMAL | WEP_FLAG_RELOADABLE | WEP_FLAG_CANCLIMB | WEP_TYPE_SPLASH
        SpawnFlags = WeaponFlags.Normal | WeaponFlags.Reloadable | WeaponFlags.CanClimb | WeaponFlags.TypeSplash;
        Color = new Vector3(0.886f, 0.545f, 0.345f);
        ViewModel = "h_hagar.iqm";  // MDL_HAGAR_VIEW
        WorldModel = "v_hagar.md3"; // MDL_HAGAR_WORLD
        ItemModel = "g_hagar.md3";  // MDL_HAGAR_ITEM
    }

    public override void Configure()
    {
        Primary.Ammo = Bal("g_balance_hagar_primary_ammo", 1f);
        Primary.Damage = Bal("g_balance_hagar_primary_damage", 25f);
        Primary.EdgeDamage = Bal("g_balance_hagar_primary_edgedamage", 12.5f);
        Primary.Force = Bal("g_balance_hagar_primary_force", 100f);
        Primary.Health = Bal("g_balance_hagar_primary_health", 15f);
        Primary.Lifetime = Bal("g_balance_hagar_primary_lifetime", 5f);
        Primary.Radius = Bal("g_balance_hagar_primary_radius", 65f);
        Primary.Refire = Bal("g_balance_hagar_primary_refire", 0.16667f);
        Primary.Speed = Bal("g_balance_hagar_primary_speed", 2200f);

        Secondary.Ammo = Bal("g_balance_hagar_secondary_ammo", 1f);
        Secondary.Damage = Bal("g_balance_hagar_secondary_damage", 35f);
        Secondary.EdgeDamage = Bal("g_balance_hagar_secondary_edgedamage", 17.5f);
        Secondary.Force = Bal("g_balance_hagar_secondary_force", 75f);
        Secondary.Health = Bal("g_balance_hagar_secondary_health", 15f);
        Secondary.LifetimeMin = Bal("g_balance_hagar_secondary_lifetime_min", 10f);
        Secondary.LifetimeRand = Bal("g_balance_hagar_secondary_lifetime_rand", 0f);
        Secondary.LoadMax = Bal("g_balance_hagar_secondary_load_max", 4f);
        Secondary.LoadSpread = Bal("g_balance_hagar_secondary_load_spread", 0.075f);
        Secondary.LoadSpreadBias = Bal("g_balance_hagar_secondary_load_spread_bias", 0.5f);
        Secondary.Radius = Bal("g_balance_hagar_secondary_radius", 80f);
        Secondary.Refire = Bal("g_balance_hagar_secondary_refire", 0.5f);
        Secondary.Speed = Bal("g_balance_hagar_secondary_speed", 2000f);
        Secondary.Spread = Bal("g_balance_hagar_secondary_spread", 0f);

        SecondaryEnabled = BalBool("g_balance_hagar_secondary", true);
    }

    // METHOD(Hagar, wr_think) — common/weapons/weapon/hagar.qc
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        if (fire == FireMode.Primary)
        {
            // W_Hagar_Attack_Auto: one rocket per refire tick (QC weapon_prepareattack, primary refire).
            if (PrepareAttack(actor, slot, fire))
                Attack(actor, slot);
        }
        else if (fire == FireMode.Secondary && SecondaryEnabled)
        {
            if (SecondaryLoad)
            {
                // W_Hagar_Attack2_Load is an incremental hold-to-charge state machine (load one rocket per
                // load_speed tick while ATCK2 is held, release the loaded salvo on button-up, with warning
                // beeps and give-back-on-abort). Driving that needs per-tick held-button input; without it
                // we fire the full load_max salvo on each secondary press (the released-salvo behaviour),
                // gated by the secondary refire.
                if (PrepareAttack(actor, slot, fire))
                    AttackLoadRelease(actor, slot);
            }
            else
            {
                // W_Hagar_Attack2: a single bouncing rocket (used when g_balance_hagar_secondary_load is off).
                if (PrepareAttack(actor, slot, fire))
                    AttackBounce(actor, slot);
            }
        }
    }

    // Refire from the (cvar-seeded) balance blocks; the Hagar has no separate animtime cvar, so the refire
    // doubles as the fire-anim length (return 0 animtime so only the refire timer gates).
    public override float RefireFor(FireMode fire) => fire == FireMode.Secondary ? Secondary.Refire : Primary.Refire;
    public override float AnimtimeFor(FireMode fire) => 0f;

    // W_Hagar_Attack — a single straight rapid rocket that bursts on impact. hagar.qc
    private void Attack(Entity actor, WeaponSlot slot)
    {
        actor.TakeResource(AmmoType, Primary.Ammo);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out Vector3 up);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward);

        Entity missile = SpawnRocket(actor, shot.Origin, "missile", Primary.Health, MoveType.Fly);
        missile.Velocity = WeaponFiring.ProjectileVelocity(shot.Dir, up, Primary.Speed);
        missile.Angles = QMath.VecToAngles(missile.Velocity);

        float deathTime = Api.Clock.Time + Primary.Lifetime;
        missile.Touch = (self, other) => Explode(self, Primary.Damage, Primary.EdgeDamage, Primary.Radius, Primary.Force);
        missile.Think = self => Explode(self, Primary.Damage, Primary.EdgeDamage, Primary.Radius, Primary.Force);
        missile.NextThink = deathTime;
        // W_Hagar_Damage: shot down -> burst.
        missile.ProjectileDamage = (self, attacker) => Explode(self, Primary.Damage, Primary.EdgeDamage, Primary.Radius, Primary.Force);

        // MUTATOR_CALLHOOK(EditProjectile, actor, missile) (hagar.qc).
        var ep = new MutatorHooks.EditProjectileArgs(actor, missile);
        MutatorHooks.EditProjectile.Call(ref ep);

        Api.Sound.Play(actor, SoundChannel.WeaponAuto, "weapons/hagar_fire.wav");
        EffectEmitter.Emit("HAGAR_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);
    }

    // W_Hagar_Attack2 — a single bouncing rocket (the non-loaded secondary). hagar.qc
    private void AttackBounce(Entity actor, WeaponSlot slot)
    {
        actor.TakeResource(AmmoType, Secondary.Ammo);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out Vector3 up);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward);

        Entity missile = SpawnRocket(actor, shot.Origin, "missile", Secondary.Health, MoveType.BounceMissile);
        missile.Velocity = WeaponFiring.ProjectileVelocity(shot.Dir, up, Secondary.Speed, 0f, 0f, Secondary.Spread);
        missile.Angles = QMath.VecToAngles(missile.Velocity);
        missile.Count = 0; // bounce counter

        float deathTime = Api.Clock.Time + Secondary.LifetimeMin + Prandom.Float() * Secondary.LifetimeRand;
        missile.Touch = (self, other) => BounceTouch(self, other, deathTime);
        missile.Think = self => Explode(self, Secondary.Damage, Secondary.EdgeDamage, Secondary.Radius, Secondary.Force);
        missile.NextThink = deathTime;
        missile.ProjectileDamage = (self, attacker) => Explode(self, Secondary.Damage, Secondary.EdgeDamage, Secondary.Radius, Secondary.Force);

        // MUTATOR_CALLHOOK(EditProjectile, actor, missile) (hagar.qc W_Hagar_Attack2).
        var ep = new MutatorHooks.EditProjectileArgs(actor, missile);
        MutatorHooks.EditProjectile.Call(ref ep);

        Api.Sound.Play(actor, SoundChannel.WeaponAuto, "weapons/hagar_fire.wav");
        EffectEmitter.Emit("HAGAR_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);
    }

    // W_Hagar_Touch2 — bounce once then explode on the next contact / on a player. hagar.qc
    private void BounceTouch(Entity self, Entity other, float deathTime)
    {
        bool hitPlayer = other.TakeDamage == DamageMode.Aim || (other.Flags & EntFlags.Client) != 0;
        if (self.Count > 0 || hitPlayer)
        {
            Explode(self, Secondary.Damage, Secondary.EdgeDamage, Secondary.Radius, Secondary.Force);
            return;
        }
        ++self.Count; // first bounce: keep going (engine MOVETYPE_BOUNCEMISSILE reflects the velocity)
        self.Angles = QMath.VecToAngles(self.Velocity);
    }

    // W_Hagar_Attack2_Load_Release — fire a full load of `load_max` rockets at once in a spread. hagar.qc
    private void AttackLoadRelease(Entity actor, WeaponSlot slot)
    {
        int shots = (int)Secondary.LoadMax;
        if (shots < 1) shots = 1;

        actor.TakeResource(AmmoType, Secondary.Ammo * shots);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out Vector3 right, out Vector3 up);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward);

        // per-shot base spread: more shots -> less spread, biased by load_spread_bias.
        float perShot = (Secondary.LoadMax > 1f) ? (shots - 1) / (Secondary.LoadMax - 1f) : 0f;
        perShot = 1f - perShot * Secondary.LoadSpreadBias;
        perShot = Secondary.Spread * perShot;

        for (int i = 0; i < shots; ++i)
        {
            Entity missile = SpawnRocket(actor, shot.Origin, "missile", Secondary.Health, MoveType.Fly);

            // s = W_CalculateSpreadPattern(1, …) * load_spread; dir = w_shotdir + right*s.y + up*s.z, then
            // W_SetupProjVelocity adds the per-shot random jitter (perShot, which shrinks as shots grow).
            Vector3 s = WeaponFiring.CalculateSpreadPattern(i, shots) * (Secondary.LoadSpread * WeaponFiring.WeaponSpreadFactor);
            Vector3 dir = QMath.Normalize(shot.Dir + right * s.Y + up * s.Z);
            missile.Velocity = WeaponFiring.ProjectileVelocity(dir, up, Secondary.Speed, 0f, 0f, perShot);
            missile.Angles = QMath.VecToAngles(missile.Velocity);

            float deathTime = Api.Clock.Time + Secondary.LifetimeMin + Prandom.Float() * Secondary.LifetimeRand;
            missile.Touch = (self, other) => Explode(self, Secondary.Damage, Secondary.EdgeDamage, Secondary.Radius, Secondary.Force);
            missile.Think = self => Explode(self, Secondary.Damage, Secondary.EdgeDamage, Secondary.Radius, Secondary.Force);
            missile.NextThink = deathTime;
            missile.ProjectileDamage = (self, attacker) => Explode(self, Secondary.Damage, Secondary.EdgeDamage, Secondary.Radius, Secondary.Force);

            // MUTATOR_CALLHOOK(EditProjectile, actor, missile) — fired per loaded rocket (hagar.qc load-release loop).
            var ep = new MutatorHooks.EditProjectileArgs(actor, missile);
            MutatorHooks.EditProjectile.Call(ref ep);
        }

        Api.Sound.Play(actor, SoundChannel.WeaponAuto, "weapons/hagar_fire.wav");
        EffectEmitter.Emit("HAGAR_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);
    }

    /// <summary>Spawn a shootable Hagar rocket with the common fields set (shared by primary + load volley).</summary>
    private Entity SpawnRocket(Entity actor, Vector3 origin, string className, float health, MoveType moveType)
    {
        Entity missile = Api.Entities.Spawn();
        missile.ClassName = className;
        missile.Owner = actor;
        missile.NetName = NetName;
        missile.MoveType = moveType;
        Projectiles.MakeTrigger(missile); // QC PROJECTILE_MAKETRIGGER (SOLID_CORPSE): transparent to the firer's movement
        missile.Flags = EntFlags.Item; // QC FL_PROJECTILE
        missile.TakeDamage = DamageMode.Yes; // shootable
        missile.Health = health;
        Api.Entities.SetSize(missile, Vector3.Zero, Vector3.Zero);
        Api.Entities.SetOrigin(missile, origin);
        return missile;
    }

    // W_Hagar_Explode / Explode2 — radius damage + knockback, then remove. hagar.qc
    private void Explode(Entity self, float damage, float edge, float radius, float force)
    {
        self.Touch = null;
        self.Think = null;
        self.TakeDamage = DamageMode.No;

        WeaponSplash.RadiusDamage(self, self.Origin, damage, edge, radius, self.Owner, RegistryId, force);

        EffectEmitter.Emit("HAGAR_EXPLODE", self.Origin);
        Api.Entities.Remove(self);
    }

    // METHOD(Hagar, wr_checkammo1) — hagar.qc
    public bool CheckAmmoPrimary(Entity actor) => actor.GetResource(AmmoType) >= Primary.Ammo;

    // METHOD(Hagar, wr_checkammo2) — hagar.qc
    public bool CheckAmmoSecondary(Entity actor) => actor.GetResource(AmmoType) >= Secondary.Ammo;
}
