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
                // QC vaporizer.qc:340: jump_interval = time + WEP_CVAR_PRI(WEP_BLASTER, refire) *
                // W_WeaponRateFactor(actor) — the sv_weaponrate (+ Speed powerup/buff) scaling.
                st.JumpInterval = Api.Clock.Time + 0.7f * WeaponRateFactor(actor); // WEP_CVAR_PRI(WEP_BLASTER, refire)
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
        // QC W_SetupShot(..., vaporizer_damage, thiswep.m_id): pass wep+maxDamage so the accuracy "fired"
        // denominator is credited (the sister Vortex does this; vaporizer.qc:136).
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, wep: this, maxDamage: damage);

        // FireRailgunBullet: pierces targets, applies the configured knockback force (+ falloff if set).
        // headshotNotify: true — the Vaporizer announces headshots (QC vaporizer.qc:145).
        Vector3 end = shot.Origin + shot.Dir * WeaponFiring.MaxShotDistance;
        Entity? hit = WeaponFiring.FireRailgunBullet(actor, shot.Origin, end, damage, RegistryId, Primary.Force,
            headshotNotify: true);

        // QC vaporizer.qc:139: sound played separately at VOL_BASE * 0.8 (and bypasses the strength sound).
        Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/minstanexfire.wav", SoundLevels.VolBase * 0.8f);

        // Rail-endpoint trace (world-only) → the impact point + surface flags, used for the beam visual, the
        // impact effect/sound, and the Rocket-Minsta sky/noimpact explosion gate (QC trace_endpos / trace_
        // dphitq3surfaceflags after FireRailgunBullet). FireRailgunBullet pierces players, so a fresh world
        // trace gives the wall endpoint the beam actually stopped at.
        TraceResult impTr = Api.Trace.Trace(shot.Origin, Vector3.Zero, Vector3.Zero, end, MoveFilter.WorldOnly, actor);
        Vector3 endpos = impTr.EndPos;

        // QC vaporizer.qc:156-157: the cylindric rail beam (gauntletbeam on a hit, lgbeam otherwise) + muzzle
        // flash (EFFECT_VORTEX_MUZZLEFLASH). The port nets a sweep effect (VAPORIZER_BEAM_HIT when a player was
        // pierced, VAPORIZER_BEAM otherwise) and reuses the shared Vortex muzzle flash.
        EffectEmitter.Emit(hit is not null ? "VAPORIZER_BEAM_HIT" : "VAPORIZER_BEAM", shot.Origin, endpos, 0);
        EffectEmitter.Emit("VORTEX_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);

        // QC vaporizer.qc:407-413 wr_impacteffect: EFFECT_VORTEX_IMPACT + SND_VAPORIZER_IMPACT (neximpact) at
        // the beam endpoint. The port emits impacts server-side like the Vortex.
        EffectEmitter.Emit("VORTEX_IMPACT", endpos);
        WeaponSplash.ImpactSoundAt(endpos, "weapons/neximpact.wav"); // SND_VAPORIZER_IMPACT

        // Rocket-Minsta: a Devastator-style explosion at the beam endpoint — but NOT when the beam hit sky or a
        // noimpact surface (QC vaporizer.qc:169-171: trace_dphitq3surfaceflags & (SKY | NOIMPACT)).
        if (Api.Services is not null && Api.Cvars.GetFloat("g_rm") != 0f
            && (impTr.DpHitQ3SurfaceFlags & (WeaponFiring.Q3SurfaceFlagSky | Q3SurfaceFlagNoImpact)) == 0)
            RocketMinstaExplosion(actor, endpos);

        // W_DecreaseAmmo (vaporizer.qc:173): (autocvar_g_instagib) ? 1 : primary_ammo. Base keys this on the
        // LIVE cvar only — a non-instagib Vaporizer pickup spends the full primary_ammo per shot.
        actor.TakeResource(AmmoType, InstagibActive ? 1f : Primary.Ammo);

        // Deferred (client render): yoda/impressive achievements, the CSQC beam team-color/colorboost.
    }

    // W_RocketMinsta_Explosion — radius blast at the rail endpoint (g_rm_*). vaporizer.qc:108.
    private void RocketMinstaExplosion(Entity actor, Vector3 loc)
    {
        // QC tags the blast WEP_DEVASTATOR.m_id | HITTYPE_SPLASH and credits WEP_DEVASTATOR accuracy — so the
        // RocketMinsta mutator's devastator-keyed no-self-damage / force-gib hooks match, and the obituary/
        // accuracy attribute to the Devastator, not the Vaporizer. Resolve the Devastator's death tag and pass
        // it as the SPECIAL deathTag (RadiusDamage routes it through the string pipeline + ORs HITTYPE_SPLASH on
        // the indirect victims). accuracyWeapon = the Devastator credits "fired" damage (QC vaporizer.qc:110-111).
        var devastator = Weapons.ByName("devastator");
        string? devTag = devastator is not null ? Damage.DeathTypes.FromWeapon(devastator.NetName) : null;
        WeaponSplash.RadiusDamage(actor, loc,
            Cvar("g_rm_damage", 70f), Cvar("g_rm_edgedamage", 38f), Cvar("g_rm_radius", 140f),
            actor, RegistryId, Cvar("g_rm_force", 400f),
            accuracyWeapon: devastator, deathTag: devTag);
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

    // W_RocketMinsta_Attack(mode 0) — a fan of short-lived bouncing laser bolts (g_rm_laser_*). vaporizer.qc:222.
    private void RocketMinstaLaserBarrage(Entity actor, WeaponSlot slot)
    {
        // Cvar fallbacks are the Base mutators.cfg:447-460 defaults (the cfg ships these; the fallbacks fire
        // only on a bare config). count 3, damage 80, radius 150, force 400, speed 6000, lifetime 30, spread 0.05.
        int count = (int)MathF.Max(1f, Cvar("g_rm_laser_count", 3f));
        float damage = Cvar("g_rm_laser_damage", 80f);
        float radius = Cvar("g_rm_laser_radius", 150f);
        float force = Cvar("g_rm_laser_force", 400f);
        float speed = Cvar("g_rm_laser_speed", 6000f);
        float spread = Cvar("g_rm_laser_spread", 0.05f);
        float zspread = Cvar("g_rm_laser_zspread", 0f);
        float lifetime = Cvar("g_rm_laser_lifetime", 30f);

        // QC vaporizer.qc:230,245: the bolts set up + tag as WEP_ELECTRO (proj.projectiledeathtype = WEP_ELECTRO
        // .m_id) — so the RocketMinsta mutator's electro-keyed no-self-damage hook matches and the kill feed /
        // accuracy attribute to the Electro, not the Vaporizer. Fall back to the Vaporizer id if Electro is
        // somehow unregistered.
        var electro = Weapons.ByName("electro");
        int laserDeathType = electro?.RegistryId ?? RegistryId;

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out Vector3 right, out _);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, new Vector3(0, 0, -3), new Vector3(0, 0, -3));

        // QC vaporizer.qc:233: W_MuzzleFlash(WEP_ELECTRO) — the barrage uses electro muzzle effects.
        EffectEmitter.Emit("ELECTRO_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);

        for (int i = 0; i < count; ++i)
        {
            Entity proj = Api.Entities.Spawn();
            proj.ClassName = "plasma_prim";
            proj.Owner = actor;
            // QC nets each bolt as PROJECTILE_ROCKETMINSTA_LASER (vaporizer.qc:272) — a DISTINCT visual from the
            // generic plasma/vaporizer bolt: models/elaser.mdl + the EFFECT_ROCKETMINSTA_LASER trail. The port keys
            // the client's ProjectileCatalog off classname+netname, so tag the bolt's netname "rocketminsta" to pick
            // the RocketMinstaLaser descriptor (red laser, elaser model) instead of falling through to "plasma".
            proj.NetName = "rocketminsta";
            // QC CSQCProjectile_SendEntity nets realowner.team as the bolt's colormap; projectile.qc tints the
            // PROJECTILE_ROCKETMINSTA_LASER body to that team palette (colormod = colormapPaletteColor(colormap&0x0F)).
            // Carry the firer's team so the colormap networks (ServerNet nets Colormap = (int)Team) and the client
            // can apply the team colormod.
            proj.Team = actor.Team;
            proj.MoveType = MoveType.BounceMissile;
            Projectiles.MakeTrigger(proj); // QC PROJECTILE_MAKETRIGGER (SOLID_CORPSE): transparent to the firer's movement
            proj.Flags = EntFlags.Item;
            Api.Entities.SetSize(proj, new Vector3(0, 0, -3), new Vector3(0, 0, -3));
            Api.Entities.SetOrigin(proj, shot.Origin);

            // velocity = (w_shotdir + (((i+0.5)/count)*2 - 1) * right * spread) * speed; z += zspread*(random-0.5).
            // QC uses (random() - 0.5) → [-0.5,0.5] (not a full signed [-1,1]); dormant at the default zspread 0.
            float lat = ((i + 0.5f) / count) * 2f - 1f;
            Vector3 v = (shot.Dir + lat * right * spread) * speed;
            v.Z += zspread * (Prandom.Float() - 0.5f);
            proj.Velocity = v;
            proj.Angles = QMath.VecToAngles(proj.Velocity);

            proj.Touch = (self, other) =>
            {
                WeaponSplash.RadiusDamage(self, self.Origin, damage / count, damage / count, radius,
                    self.Owner, laserDeathType, force / count, directHit: other);
                Api.Entities.Remove(self);
            };
            proj.Think = self => { WeaponSplash.RadiusDamage(self, self.Origin, damage / count, damage / count,
                radius, self.Owner, laserDeathType, force / count); Api.Entities.Remove(self); };
            proj.NextThink = Api.Clock.Time + lifetime;

            // MUTATOR_CALLHOOK(EditProjectile, actor, proj) — fired per laser bolt (vaporizer.qc RM laser barrage).
            var ep = new MutatorHooks.EditProjectileArgs(actor, proj);
            MutatorHooks.EditProjectile.Call(ref ep);
        }

        // QC vaporizer.qc:229: mode-0 fan plays SND_CRYLINK_FIRE (electro_fire2 is only the mode-1 rapid stream).
        Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/crylink_fire.wav");
    }

    private static float Cvar(string name, float fallback)
    {
        if (Api.Services is null) return fallback;
        float v = Api.Cvars.GetFloat(name);
        return v != 0f ? v : fallback;
    }

    // METHOD(Vaporizer, wr_checkammo1) — vaporizer.qc:362 (instagib needs 1 cell, else primary_ammo). QC re-reads
    // autocvar_g_instagib here, so the non-instagib loadout correctly requires the full primary_ammo per shot.
    public bool CheckAmmoPrimary(Entity actor)
        => actor.GetResource(AmmoType) >= (InstagibActive ? 1f : Primary.Ammo);

    // METHOD(Vaporizer, wr_checkammo2) — vaporizer.qc:370: the Blaster-laser secondary is free at the default
    // Blaster ammo 0 (return true); otherwise it needs WEP_CVAR_PRI(WEP_BLASTER, ammo) cells.
    public bool CheckAmmoSecondary(Entity actor)
    {
        float blasterAmmo = Cvar("g_balance_blaster_primary_ammo", 0f);
        return blasterAmmo <= 0f || actor.GetResource(AmmoType) >= blasterAmmo;
    }

    /// <summary>QC <c>autocvar_g_instagib</c>: the live instagib gate. <see cref="Instagib"/> is the class default
    /// (true — the Vaporizer is the instagib loadout weapon), but the per-shot/per-check cost must re-read the
    /// cvar so a non-instagib Vaporizer pickup spends/needs the full <c>primary_ammo</c> (vaporizer.qc reads
    /// <c>autocvar_g_instagib</c> at every W_DecreaseAmmo / wr_checkammo).</summary>
    private bool InstagibActive => Instagib && (Api.Services is null || Api.Cvars.GetFloat("g_instagib") != 0f);

    /// <summary>QC Q3SURFACEFLAG_NOIMPACT (BIT(0)) — a surface nothing impacts; the RM explosion is suppressed
    /// when the rail endpoint lands on it (or on sky), matching vaporizer.qc:170.</summary>
    private const int Q3SurfaceFlagNoImpact = 0x1;
}
