using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Vaporizer (Nexuiz "MinstaNex") — port of common/weapons/weapon/vaporizer.{qh,qc}. A hitscan rail
/// superweapon firing an instant beam of energy for a huge fixed chunk of damage (in instagib it one-shots
/// players); secondary fires a Blaster-style knockback laser. The signature instagib behaviour — a 0-damage
/// cvar means "10000 damage" (instant kill) and each shot costs 1 cell — is modeled here.
///
/// Identity/attributes from vaporizer.qh; balance from bal-wep-xonotic.cfg (g_balance_vaporizer_*).
/// This port covers the primary railgun beam (piercing + knockback force), the Blaster-laser secondary
/// (driven through the Blaster weapon), and the full Rocket-Minsta mode (g_rm: explosion at the beam
/// endpoint + the secondary laser barrage). Only the achievements and CSQC beam particle are render-only.
/// </summary>
[Weapon]
public sealed class Vaporizer : Weapon
{
    /// <summary>Primary-fire (rail beam) balance — QC WEP_CVAR_PRI(WEP_VAPORIZER, *).</summary>
    public struct PrimaryBalance
    {
        public float Ammo;     // g_balance_vaporizer_primary_ammo (cells per shot, non-instagib)
        public float Animtime; // g_balance_vaporizer_primary_animtime
        public float Damage;   // g_balance_vaporizer_primary_damage (<=0 means instant-kill 10000)
        public float Force;    // g_balance_vaporizer_primary_force
        public float Refire;   // g_balance_vaporizer_primary_refire
    }

    public PrimaryBalance Primary;

    /// <summary>
    /// Whether the instagib mutator is active. In QC this is autocvar_g_instagib: when set, each shot costs
    /// exactly 1 cell and the beam deals its instant-kill 10000 damage. The vaporizer is the instagib
    /// default loadout weapon.
    /// </summary>
    public bool Instagib = true;


    public Vaporizer()
    {
        NetName = "vaporizer";
        AmmoType = ResourceType.Cells;   // QC ammo_type
        DisplayName = "Vaporizer";
        Impulse = 7;
        // WEP_FLAG_RELOADABLE | WEP_FLAG_CANCLIMB | WEP_FLAG_SUPERWEAPON | WEP_TYPE_HITSCAN | WEP_FLAG_NODUAL
        SpawnFlags = WeaponFlags.Reloadable | WeaponFlags.CanClimb | WeaponFlags.SuperWeapon
                   | WeaponFlags.TypeHitscan | WeaponFlags.NoDual;
        Color = new Vector3(0.592f, 0.557f, 0.824f);
        ViewModel = "h_minstanex.iqm";  // MDL_VAPORIZER_VIEW
        WorldModel = "v_minstanex.md3"; // MDL_VAPORIZER_WORLD
        ItemModel = "g_minstanex.md3";  // MDL_VAPORIZER_ITEM
    }

    public override void Configure()
    {
        Primary.Ammo = Bal("g_balance_vaporizer_primary_ammo", 10f);
        Primary.Animtime = Bal("g_balance_vaporizer_primary_animtime", 0.3f);
        Primary.Damage = Bal("g_balance_vaporizer_primary_damage", 150f);
        Primary.Force = Bal("g_balance_vaporizer_primary_force", 800f);
        Primary.Refire = Bal("g_balance_vaporizer_primary_refire", 1f);

        Instagib = true;
    }

    // METHOD(Vaporizer, wr_think) — common/weapons/weapon/vaporizer.qc
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        var st = actor.WeaponState(slot);
        bool rm = Api.Services is not null && Api.Cvars.GetFloat("g_rm") != 0f;

        // Primary: rail beam — but in Rocket-Minsta when out of cells, primary falls back to the laser.
        // Gated by the primary refire (QC weapon_prepareattack); the out-of-cells RM fallthrough below uses
        // its own jump_interval gate so it can fire even when PrepareAttack would block the rail.
        if (fire == FireMode.Primary && (actor.GetResource(ResourceType.Cells) > 0f || !rm))
        {
            if (PrepareAttack(actor, slot, fire))
                Attack(actor, slot);
            return;
        }

        // Secondary (and the out-of-cells RM primary) fires the Blaster knockback laser, or the RM laser
        // barrage when g_rm + g_rm_laser are enabled. A manual jump_interval gate handles its refire so it
        // can fire independently of the rail beam (important for instagib). Each path requires its own button
        // be held — the driver calls WrThink(Primary) every tick for upkeep, so the Primary fallback must
        // check st.ButtonAttack itself (the Secondary call only happens while ATK2 is held).
        bool primaryFallback = fire == FireMode.Primary && rm && actor.GetResource(ResourceType.Cells) <= 0f && st.ButtonAttack;
        if (fire == FireMode.Secondary || primaryFallback)
        {
            bool rmLaser = rm && Api.Cvars.GetFloat("g_rm_laser") != 0f;
            if (rmLaser)
            {
                if (st.JumpInterval <= Api.Clock.Time)
                {
                    st.JumpInterval = Api.Clock.Time + Cvar("g_rm_laser_refire", 0.7f);
                    RocketMinstaLaserBarrage(actor, slot);
                }
            }
            else if (st.JumpInterval <= Api.Clock.Time)
            {
                st.JumpInterval = Api.Clock.Time + 0.7f; // WEP_CVAR_PRI(WEP_BLASTER, refire)
                BlasterSecondary(actor, slot);
            }
        }
    }

    // Refire/animtime from the (cvar-seeded) primary balance block (the rail beam). The secondary laser uses
    // its own jump_interval gate, so only the primary mode consults these.
    public override float RefireFor(FireMode fire) => Primary.Refire;
    public override float AnimtimeFor(FireMode fire) => Primary.Animtime;

    // W_Vaporizer_Attack — instant rail beam dealing a huge fixed chunk of damage. vaporizer.qc
    private void Attack(Entity actor, WeaponSlot slot)
    {
        // damage = (primary_damage > 0) ? primary_damage : 10000 (instant kill).
        float damage = Primary.Damage > 0f ? Primary.Damage : 10000f;

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward);

        // FireRailgunBullet: pierces targets, applies the configured knockback force (+ falloff if set).
        // headshotNotify: true — the Vaporizer announces headshots (QC vaporizer.qc:145).
        Vector3 end = shot.Origin + shot.Dir * WeaponFiring.MaxShotDistance;
        Entity? hit = WeaponFiring.FireRailgunBullet(actor, shot.Origin, end, damage, RegistryId, Primary.Force,
            headshotNotify: true);

        Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/minstanexfire.wav");

        // Rocket-Minsta: a Devastator-style explosion at the beam endpoint.
        if (Api.Services is not null && Api.Cvars.GetFloat("g_rm") != 0f)
            RocketMinstaExplosion(actor, end);

        // W_DecreaseAmmo: instagib spends exactly 1 cell, otherwise primary_ammo.
        bool instagib = Instagib || (Api.Services is not null && Api.Cvars.GetFloat("g_instagib") != 0f);
        actor.TakeResource(AmmoType, instagib ? 1f : Primary.Ammo);

        // Deferred (client render): yoda/impressive achievements, SendCSQCVaporizerBeamParticle, muzzle flash.
    }

    // W_RocketMinsta_Explosion — radius blast at the rail endpoint (g_rm_*). vaporizer.qc
    private void RocketMinstaExplosion(Entity actor, Vector3 loc)
    {
        WeaponSplash.RadiusDamage(actor, loc,
            Cvar("g_rm_damage", 35f), Cvar("g_rm_edgedamage", 15f), Cvar("g_rm_radius", 90f),
            actor, RegistryId, Cvar("g_rm_force", 200f));
    }

    // W_Blaster_Attack via the Blaster weapon — the secondary knockback laser.
    private void BlasterSecondary(Entity actor, WeaponSlot slot)
    {
        var blaster = Registry<Weapon>.ByName("blaster") as Blaster;
        if (blaster is null) return;
        // Fire the Blaster's primary bolt directly from the actor's aim (QC makevectors(v_angle) +
        // W_Blaster_Attack). This is the Vaporizer's own refire-gated secondary (jump_interval), so it must
        // bypass the Blaster's refire gate — go through FirePrimaryDirect, not the gated WrThink.
        blaster.FirePrimaryDirect(actor, slot);
    }

    // W_RocketMinsta_Attack(mode 0) — a fan of short-lived bouncing laser bolts (g_rm_laser_*). vaporizer.qc
    private void RocketMinstaLaserBarrage(Entity actor, WeaponSlot slot)
    {
        int count = (int)MathF.Max(1f, Cvar("g_rm_laser_count", 1f));
        float damage = Cvar("g_rm_laser_damage", 25f);
        float radius = Cvar("g_rm_laser_radius", 80f);
        float force = Cvar("g_rm_laser_force", 300f);
        float speed = Cvar("g_rm_laser_speed", 5000f);
        float spread = Cvar("g_rm_laser_spread", 0f);
        float zspread = Cvar("g_rm_laser_zspread", 0f);
        float lifetime = Cvar("g_rm_laser_lifetime", 0.3f);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out Vector3 right, out _);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, new Vector3(0, 0, -3), new Vector3(0, 0, -3));

        for (int i = 0; i < count; ++i)
        {
            Entity proj = Api.Entities.Spawn();
            proj.ClassName = "plasma_prim";
            proj.Owner = actor;
            proj.NetName = NetName;
            proj.MoveType = MoveType.BounceMissile;
            Projectiles.MakeTrigger(proj); // QC PROJECTILE_MAKETRIGGER (SOLID_CORPSE): transparent to the firer's movement
            proj.Flags = EntFlags.Item;
            Api.Entities.SetSize(proj, new Vector3(0, 0, -3), new Vector3(0, 0, -3));
            Api.Entities.SetOrigin(proj, shot.Origin);

            // velocity = (w_shotdir + (((i+0.5)/count)*2 - 1) * right * spread) * speed; z += zspread*signed.
            float lat = ((i + 0.5f) / count) * 2f - 1f;
            Vector3 v = (shot.Dir + lat * right * spread) * speed;
            v.Z += zspread * Prandom.Signed();
            proj.Velocity = v;
            proj.Angles = QMath.VecToAngles(proj.Velocity);

            proj.Touch = (self, other) =>
            {
                WeaponSplash.RadiusDamage(self, self.Origin, damage / count, damage / count, radius,
                    self.Owner, RegistryId, force / count, directHit: other);
                Api.Entities.Remove(self);
            };
            proj.Think = self => { WeaponSplash.RadiusDamage(self, self.Origin, damage / count, damage / count,
                radius, self.Owner, RegistryId, force / count); Api.Entities.Remove(self); };
            proj.NextThink = Api.Clock.Time + lifetime;

            // MUTATOR_CALLHOOK(EditProjectile, actor, proj) — fired per laser bolt (vaporizer.qc RM laser barrage).
            var ep = new MutatorHooks.EditProjectileArgs(actor, proj);
            MutatorHooks.EditProjectile.Call(ref ep);
        }
        Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/electro_fire2.wav");
    }

    private static float Cvar(string name, float fallback)
    {
        if (Api.Services is null) return fallback;
        float v = Api.Cvars.GetFloat(name);
        return v != 0f ? v : fallback;
    }

    // METHOD(Vaporizer, wr_checkammo1) — vaporizer.qc (instagib needs 1 cell).
    public bool CheckAmmoPrimary(Entity actor) => actor.GetResource(AmmoType) >= (Instagib ? 1f : Primary.Ammo);
}
