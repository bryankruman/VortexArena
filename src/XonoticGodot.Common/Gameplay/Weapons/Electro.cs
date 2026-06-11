using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Electro — port of common/weapons/weapon/electro.{qh,qc}. A splash/combo weapon. Primary fires a
/// fast straight bolt (MOVETYPE_FLY) that bursts on impact; secondary lobs gravity-affected bouncing orbs
/// (MOVETYPE_BOUNCE) that detonate on a timer or contact. The signature mechanic is the "combo": when a
/// blast happens near a live orb the orb is triggered too, chaining a bigger explosion.
///
/// Identity/attributes from electro.qh; balance from bal-wep-xonotic.cfg (g_balance_electro_*).
/// This port covers the bolt and orb projectiles, their contact/lifetime detonation, the multi-orb
/// secondary burst (secondary_count), the in-flight midair-combo, the staggered combo chain (combo_speed
/// delay + recursive re-trigger), orb bounce factor/stop, shoot-down-into-combo, and the splash damage.
/// Only sticky orbs (MOVETYPE_FOLLOW), the combo explode-over-time variant, and orb networking are omitted.
/// </summary>
[Weapon]
public sealed class Electro : Weapon
{
    /// <summary>Primary-fire (bolt) balance — QC WEP_CVAR_PRI(WEP_ELECTRO, *).</summary>
    public struct PrimaryBalance
    {
        public float Ammo;        // g_balance_electro_primary_ammo (cells)
        public float Animtime;    // g_balance_electro_primary_animtime
        public float ComboRadius; // g_balance_electro_primary_comboradius (orb-trigger radius on bolt blast)
        public float Damage;      // g_balance_electro_primary_damage
        public float EdgeDamage;  // g_balance_electro_primary_edgedamage
        public float Force;       // g_balance_electro_primary_force
        public float Lifetime;    // g_balance_electro_primary_lifetime
        public float Radius;      // g_balance_electro_primary_radius
        public float Refire;      // g_balance_electro_primary_refire
        public float Speed;       // g_balance_electro_primary_speed
        public float MidairComboRadius; // g_balance_electro_primary_midaircombo_radius (orb trigger while in flight)
        public bool  MidairComboExplode;// g_balance_electro_primary_midaircombo_explode
    }

    /// <summary>Secondary-fire (orb) balance — QC WEP_CVAR_SEC(WEP_ELECTRO, *).</summary>
    public struct SecondaryBalance
    {
        public float Ammo;             // g_balance_electro_secondary_ammo
        public float Animtime;         // g_balance_electro_secondary_animtime
        public float BounceFactor;     // g_balance_electro_secondary_bouncefactor
        public float BounceStop;       // g_balance_electro_secondary_bouncestop
        public int   Count;            // g_balance_electro_secondary_count (orbs per burst)
        public float Damage;           // g_balance_electro_secondary_damage
        public float DamageForceScale; // g_balance_electro_secondary_damageforcescale
        public float EdgeDamage;       // g_balance_electro_secondary_edgedamage
        public float Force;            // g_balance_electro_secondary_force
        public float Health;           // g_balance_electro_secondary_health (shootable orb hp)
        public float Lifetime;         // g_balance_electro_secondary_lifetime
        public float Radius;           // g_balance_electro_secondary_radius
        public float Refire;           // g_balance_electro_secondary_refire (delay between secondary BURSTS)
        public float Refire2;          // g_balance_electro_secondary_refire2 (primary lockout after an orb)
        public float Speed;            // g_balance_electro_secondary_speed
        public float SpeedUp;          // g_balance_electro_secondary_speed_up
        public bool  TouchExplode;     // g_balance_electro_secondary_touchexplode
    }

    /// <summary>Combo balance — QC WEP_CVAR(WEP_ELECTRO, combo_*).</summary>
    public struct ComboBalance
    {
        public float ComboRadius; // g_balance_electro_combo_comboradius (chain reach from a triggered orb)
        public float Damage;      // g_balance_electro_combo_damage
        public float EdgeDamage;  // g_balance_electro_combo_edgedamage
        public float Force;       // g_balance_electro_combo_force
        public float Radius;      // g_balance_electro_combo_radius (blast radius of a combo)
    }

    public PrimaryBalance Primary;
    public SecondaryBalance Secondary;
    public ComboBalance Combo;


    public Electro()
    {
        NetName = "electro";
        AmmoType = ResourceType.Cells;   // QC ammo_type
        DisplayName = "Electro";
        Impulse = 5;
        // WEP_FLAG_NORMAL | WEP_FLAG_RELOADABLE | WEP_TYPE_SPLASH
        SpawnFlags = WeaponFlags.Normal | WeaponFlags.Reloadable | WeaponFlags.TypeSplash;
        Color = new Vector3(0.408f, 0.600f, 0.949f);
        ViewModel = "h_electro.iqm";  // MDL_ELECTRO_VIEW
        WorldModel = "v_electro.md3"; // MDL_ELECTRO_WORLD
        ItemModel = "g_electro.md3";  // MDL_ELECTRO_ITEM
    }

    public override void Configure()
    {
        Primary.Ammo = Bal("g_balance_electro_primary_ammo", 4f);
        Primary.Animtime = Bal("g_balance_electro_primary_animtime", 0.3f);
        Primary.ComboRadius = Bal("g_balance_electro_primary_comboradius", 300f);
        Primary.Damage = Bal("g_balance_electro_primary_damage", 40f);
        Primary.EdgeDamage = Bal("g_balance_electro_primary_edgedamage", 20f);
        Primary.Force = Bal("g_balance_electro_primary_force", 200f);
        Primary.Lifetime = Bal("g_balance_electro_primary_lifetime", 5f);
        Primary.Radius = Bal("g_balance_electro_primary_radius", 100f);
        Primary.Refire = Bal("g_balance_electro_primary_refire", 0.6f);
        Primary.Speed = Bal("g_balance_electro_primary_speed", 2500f);
        Primary.MidairComboRadius = Bal("g_balance_electro_primary_midaircombo_radius", 0f);   // off by default in xonotic balance (on in some balances)
        Primary.MidairComboExplode = BalBool("g_balance_electro_primary_midaircombo_explode", true);

        Secondary.Ammo = Bal("g_balance_electro_secondary_ammo", 2f);
        Secondary.Animtime = Bal("g_balance_electro_secondary_animtime", 0.2f);
        Secondary.BounceFactor = Bal("g_balance_electro_secondary_bouncefactor", 0.3f);
        Secondary.BounceStop = Bal("g_balance_electro_secondary_bouncestop", 0.05f);
        Secondary.Count = BalInt("g_balance_electro_secondary_count", 3);
        Secondary.Damage = Bal("g_balance_electro_secondary_damage", 30f);
        Secondary.DamageForceScale = Bal("g_balance_electro_secondary_damageforcescale", 4f);
        Secondary.EdgeDamage = Bal("g_balance_electro_secondary_edgedamage", 15f);
        Secondary.Force = Bal("g_balance_electro_secondary_force", 50f);
        Secondary.Health = Bal("g_balance_electro_secondary_health", 5f);
        Secondary.Lifetime = Bal("g_balance_electro_secondary_lifetime", 4f);
        Secondary.Radius = Bal("g_balance_electro_secondary_radius", 150f);
        Secondary.Refire = Bal("g_balance_electro_secondary_refire", 1.2f);
        Secondary.Refire2 = Bal("g_balance_electro_secondary_refire2", 0.2f);
        Secondary.Speed = Bal("g_balance_electro_secondary_speed", 1000f);
        Secondary.SpeedUp = Bal("g_balance_electro_secondary_speed_up", 200f);
        Secondary.TouchExplode = BalBool("g_balance_electro_secondary_touchexplode", true);

        Combo.ComboRadius = Bal("g_balance_electro_combo_comboradius", 300f);
        Combo.Damage = Bal("g_balance_electro_combo_damage", 50f);
        Combo.EdgeDamage = Bal("g_balance_electro_combo_edgedamage", 25f);
        Combo.Force = Bal("g_balance_electro_combo_force", 120f);
        Combo.Radius = Bal("g_balance_electro_combo_radius", 150f);
    }

    // METHOD(Electro, wr_think) — common/weapons/weapon/electro.qc
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        var st = actor.WeaponState(slot);
        float rate = WeaponRateFactor();

        if (fire == FireMode.Primary)
        {
            // QC: if (time >= electro_secondarytime + refire2*rate && weapon_prepareattack(..., refire)) {...}
            // The refire2 interlock keeps the primary briefly locked out after lobbing an orb so the two modes
            // don't overlap; primary itself uses the shared ATTACK_FINISHED (positive attacktime).
            if (Api.Clock.Time >= st.ElectroSecondaryTime + Secondary.Refire2 * rate
                && PrepareAttack(actor, slot, fire, attackTime: Primary.Refire))
                AttackBolt(actor, slot);
        }
        else if (fire == FireMode.Secondary)
        {
            // QC: if (time >= electro_secondarytime + refire*rate && weapon_prepareattack(..., true, -1)) fire
            // the FIRST orb, set electro_count = count, electro_secondarytime = time, and schedule
            // W_Electro_CheckAttack after animtime to stream the rest one-per-tick while ATCK2 stays held.
            // attacktime = -1: the secondary runs on its private electro_secondarytime, not ATTACK_FINISHED.
            if (Api.Clock.Time >= st.ElectroSecondaryTime + Secondary.Refire * rate
                && PrepareAttack(actor, slot, fire, attackTime: -1f))
            {
                AttackOrb(actor, slot);
                st.ElectroCount = (int)MathF.Max(1f, Secondary.Count);
                st.ElectroSecondaryTime = Api.Clock.Time;
                ScheduleCheckAttack(actor, slot);
            }
        }
    }

    // W_Electro_CheckAttack — fires one more orb per animtime tick while electro_count > 1 and ATCK2 is held,
    // decrementing electro_count and rescheduling itself; otherwise the slot becomes READY. electro.qc
    private void CheckAttack(Entity actor, WeaponSlot slot)
    {
        var st = actor.WeaponState(slot);
        if (st.ElectroCount > 1 && st.ButtonAttack2
            && PrepareAttack(actor, slot, FireMode.Secondary, attackTime: -1f))
        {
            AttackOrb(actor, slot);
            --st.ElectroCount;
            st.ElectroSecondaryTime = Api.Clock.Time;
            ScheduleCheckAttack(actor, slot);
            return;
        }
        // QC w_ready(...): the streaming burst is done; return the slot to READY.
        if (st.State == WeaponFireState.InUse)
            st.State = WeaponFireState.Ready;
    }

    // weapon_thinkf(actor, weaponentity, WFRAME_FIRE2, secondary animtime, W_Electro_CheckAttack)
    private void ScheduleCheckAttack(Entity actor, WeaponSlot slot)
    {
        var st = actor.WeaponState(slot);
        float animtime = MathF.Max(0f, Secondary.Animtime) * WeaponRateFactor();
        WeaponFireDriver.ScheduleThink(st, animtime, (pl, sl) => CheckAttack(pl, sl));
    }

    // Refire/animtime from the (cvar-seeded) per-mode balance blocks.
    public override float RefireFor(FireMode fire) => fire == FireMode.Secondary ? Secondary.Refire : Primary.Refire;
    public override float AnimtimeFor(FireMode fire) => fire == FireMode.Secondary ? Secondary.Animtime : Primary.Animtime;

    // W_Electro_Attack_Bolt — fast straight bolt that bursts on impact. electro.qc
    private void AttackBolt(Entity actor, WeaponSlot slot)
    {
        actor.TakeResource(AmmoType, Primary.Ammo);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, new Vector3(0, 0, -3), new Vector3(0, 0, -3), recoil: 2f);

        Entity proj = Api.Entities.Spawn();
        proj.ClassName = "electro_bolt";
        proj.Owner = actor;
        proj.NetName = NetName;
        proj.MoveType = MoveType.Fly;
        Projectiles.MakeTrigger(proj); // QC PROJECTILE_MAKETRIGGER (SOLID_CORPSE): transparent to the firer's movement
        proj.Flags = EntFlags.Item; // QC FL_PROJECTILE
        Api.Entities.SetSize(proj, new Vector3(0, 0, -3), new Vector3(0, 0, -3));
        Api.Entities.SetOrigin(proj, shot.Origin);

        // W_SetupProjVelocity_PRI: velocity = w_shotdir * speed.
        proj.Velocity = WeaponFiring.ProjectileVelocity(shot.Dir, Vector3.UnitZ, Primary.Speed);
        proj.Angles = QMath.VecToAngles(proj.Velocity);

        float deathTime = Api.Clock.Time + Primary.Lifetime;
        proj.Touch = (self, other) => ExplodeBolt(self, other);
        proj.Think = self => BoltThink(self, deathTime); // W_Electro_Bolt_Think: midair combo + lifetime
        proj.NextThink = Api.Clock.Time;

        // MUTATOR_CALLHOOK(EditProjectile, actor, proj) (electro.qc).
        var ep = new MutatorHooks.EditProjectileArgs(actor, proj);
        MutatorHooks.EditProjectile.Call(ref ep);

        Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/electro_fire.wav");
        EffectEmitter.Emit("ELECTRO_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);
    }

    // W_Electro_Bolt_Think — while in flight, trigger nearby orbs into a combo (midaircombo); explode at end
    // of lifetime. electro.qc
    private void BoltThink(Entity self, float deathTime)
    {
        if (Api.Clock.Time >= deathTime)
        {
            ExplodeBolt(self, null);
            return;
        }

        // midair combo: detonate orbs within midaircombo_radius (defaults on in xonotic balance), and if any
        // were found and midaircombo_explode is set, blow up the bolt too.
        if (Primary.MidairComboRadius > 0f)
        {
            bool found = false;
            var orbs = Api.Entities.FindInRadius(self.Origin, Primary.MidairComboRadius)
                .Where(e => e.ClassName == "electro_orb" && !e.IsFreed).ToList();
            foreach (Entity e in orbs)
            {
                // own/teammate/enemy gating is the midaircombo_own/_teammate/_enemy cvars; default enemy-only,
                // but without team data here we trigger any non-self orb (the common FFA case).
                e.Owner = self.Owner;
                e.Touch = null; e.Think = null; e.TakeDamage = DamageMode.No;
                WeaponSplash.RadiusDamage(e, e.Origin, Combo.Damage, Combo.EdgeDamage, Combo.Radius,
                    self.Owner, RegistryId, Combo.Force);
                EffectEmitter.Emit("ELECTRO_COMBO", e.Origin);
                Api.Entities.Remove(e);
                found = true;
            }
            if (found && Primary.MidairComboExplode)
            {
                ExplodeBolt(self, null);
                return;
            }
        }
        self.NextThink = MathF.Min(Api.Clock.Time + 0.05f, deathTime);
    }

    // W_Electro_Explode (bolt path) — bolt blast + trigger nearby orbs into a combo. electro.qc
    private void ExplodeBolt(Entity self, Entity? directHit)
    {
        self.Touch = null;
        self.Think = null;
        self.TakeDamage = DamageMode.No;

        // Trigger orbs within comboradius FIRST (so they chain off this blast).
        TriggerCombo(self.Origin, Primary.ComboRadius, self.Owner);

        WeaponSplash.RadiusDamage(self, self.Origin, Primary.Damage, Primary.EdgeDamage, Primary.Radius,
            self.Owner, RegistryId, Primary.Force);

        EffectEmitter.Emit("ELECTRO_BALLEXPLODE", self.Origin);
        Api.Entities.Remove(self);
    }

    // W_Electro_Attack_Orb — gravity-affected bouncing orb that detonates on timer/contact. electro.qc
    private void AttackOrb(Entity actor, WeaponSlot slot)
    {
        actor.TakeResource(AmmoType, Secondary.Ammo);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out Vector3 up);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, new Vector3(-4, -4, -4), new Vector3(4, 4, 4), recoil: 2f);
        // w_shotdir = v_forward — no TrueAim for orbs.
        Vector3 dir = forward;

        Entity orb = Api.Entities.Spawn();
        orb.ClassName = "electro_orb";
        orb.Owner = actor;
        orb.NetName = NetName;
        orb.MoveType = MoveType.Bounce;
        Projectiles.MakeTrigger(orb); // QC PROJECTILE_MAKETRIGGER (SOLID_CORPSE): transparent to the firer's movement
        orb.Flags = EntFlags.Item; // QC FL_PROJECTILE
        orb.Gravity = 1f;
        Api.Entities.SetSize(orb, new Vector3(-4, -4, -4), new Vector3(4, 4, 4));
        Api.Entities.SetOrigin(orb, shot.Origin);

        orb.TakeDamage = DamageMode.Yes; // shootable
        orb.Health = Secondary.Health;
        orb.DamageForceScale = Secondary.DamageForceScale;
        orb.BounceFactor = Secondary.BounceFactor; // engine MOVETYPE_BOUNCE uses these
        orb.BounceStop = Secondary.BounceStop;

        // W_SetupProjVelocity_UP_SEC: velocity = normalize(dir + up*(speed_up/speed)) * speed.
        orb.Velocity = WeaponFiring.ProjectileVelocity(dir, up, Secondary.Speed, Secondary.SpeedUp);
        orb.Angles = QMath.VecToAngles(orb.Velocity);

        orb.Touch = (self, other) => OrbTouch(self, other);
        orb.Think = self => ExplodeOrb(self); // adaptor_think2use_hittype_splash at lifetime
        orb.NextThink = Api.Clock.Time + Secondary.Lifetime;
        // W_Electro_Orb_Damage: a shot-down orb detonates as a combo (crediting whoever shot it).
        orb.ProjectileDamage = (self, attacker) =>
        {
            self.Owner = attacker ?? self.Owner;
            self.Touch = null; self.Think = null; self.TakeDamage = DamageMode.No;
            WeaponSplash.RadiusDamage(self, self.Origin, Combo.Damage, Combo.EdgeDamage, Combo.Radius,
                self.Owner, RegistryId, Combo.Force);
            EffectEmitter.Emit("ELECTRO_COMBO", self.Origin);
            Api.Entities.Remove(self);
        };

        // MUTATOR_CALLHOOK(EditProjectile, actor, proj) (electro.qc). invincibleproj zeroes the orb's health
        // here, so a shot-down combo can't be triggered — matching QC.
        var ep = new MutatorHooks.EditProjectileArgs(actor, orb);
        MutatorHooks.EditProjectile.Call(ref ep);

        Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/electro_fire2.wav");
        EffectEmitter.Emit("ELECTRO_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);
    }

    // W_Electro_Orb_Touch — burst on a player (touchexplode), otherwise bounce. electro.qc
    private void OrbTouch(Entity self, Entity other)
    {
        bool hitPlayer = other.TakeDamage == DamageMode.Aim || (other.Flags & EntFlags.Client) != 0;
        if (hitPlayer && Secondary.TouchExplode)
        {
            ExplodeOrb(self);
            return;
        }
        // bounce off the world (engine MOVETYPE_BOUNCE handles the reflection).
        Api.Sound.Play(self, SoundChannel.Body, "weapons/electro_bounce.wav");
        self.Angles = QMath.VecToAngles(self.Velocity);
    }

    // W_Electro_Explode (orb path) — orb blast (secondary balance). electro.qc
    private void ExplodeOrb(Entity self)
    {
        self.Touch = null;
        self.Think = null;
        self.TakeDamage = DamageMode.No;

        WeaponSplash.RadiusDamage(self, self.Origin, Secondary.Damage, Secondary.EdgeDamage, Secondary.Radius,
            self.Owner, RegistryId, Secondary.Force);

        EffectEmitter.Emit("ELECTRO_BALLEXPLODE", self.Origin);
        Api.Entities.Remove(self);
    }

    /// <summary>g_balance_electro_combo_speed — chain delay = distance/speed (0 = instant). Default 1000.</summary>
    public float ComboSpeed = 1000f;

    /// <summary>
    /// Port of W_Electro_TriggerCombo (electro.qc): every live electro_orb within <paramref name="rad"/> of
    /// <paramref name="org"/> is converted to a combo orb (classname "electro_orb_chain"), credited to
    /// <paramref name="own"/>, and scheduled to detonate after a combo_speed-based delay (distance/speed).
    /// Each detonation re-triggers orbs within combo_comboradius, so the chain ripples outward — exactly
    /// like QC's recursive setthink(W_Electro_ExplodeCombo) chaining.
    /// </summary>
    private void TriggerCombo(Vector3 org, float rad, Entity? own)
    {
        if (Api.Services is null) return;
        // Snapshot first: we mutate orbs while RadiusDamage re-enumerates the world.
        var orbs = Api.Entities.FindInRadius(org, rad)
            .Where(e => e.ClassName == "electro_orb" && !e.IsFreed).ToList();
        foreach (Entity e in orbs)
        {
            e.Owner = own;
            e.ClassName = "electro_orb_chain"; // so a re-trigger doesn't pick it up again
            e.Touch = null;
            e.TakeDamage = DamageMode.No;
            e.ProjectileDamage = null;

            float delay = (ComboSpeed > 0f) ? (e.Origin - org).Length() / ComboSpeed : 0f;
            e.Think = self => ExplodeCombo(self);
            e.NextThink = Api.Clock.Time + delay;
            if (delay <= 0f) ExplodeCombo(e);
        }
    }

    // W_Electro_ExplodeCombo — a combo orb's blast; it first re-triggers nearby orbs (chain), then explodes.
    private void ExplodeCombo(Entity self)
    {
        // chain to orbs within combo_comboradius before blasting.
        TriggerCombo(self.Origin, Combo.ComboRadius, self.Owner);
        self.Think = null;
        WeaponSplash.RadiusDamage(self, self.Origin, Combo.Damage, Combo.EdgeDamage, Combo.Radius,
            self.Owner, RegistryId, Combo.Force);
        EffectEmitter.Emit("ELECTRO_COMBO", self.Origin);
        Api.Entities.Remove(self);
    }

    // METHOD(Electro, wr_checkammo1) — electro.qc
    public bool CheckAmmoPrimary(Entity actor) => actor.GetResource(AmmoType) >= Primary.Ammo;

    // METHOD(Electro, wr_checkammo2) — electro.qc
    public bool CheckAmmoSecondary(Entity actor) => actor.GetResource(AmmoType) >= Secondary.Ammo;
}
