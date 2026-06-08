using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The T.A.G. Seeker — port of common/weapons/weapon/seeker.{qh,qc}. A splash weapon with two layouts
/// (g_balance_seeker_type). In the default type 0: primary fires a "tag" dart that, on striking a player,
/// spawns a controller which launches a barrage of homing missiles at them; secondary fires FLAC — a rapid
/// spray of short-lived scattered explosives. (In type 1 the roles invert: primary fires homing missiles at
/// already-tagged targets, secondary fires the tag.) Missiles, tags and FLAC bolts are all shootable.
///
/// Identity/attributes from seeker.qh; balance from bal-wep-xonotic.cfg (g_balance_seeker_*).
/// This port covers the homing missile (turnrate steering toward the tagged enemy + accel/decel speed
/// clamp), the FLAC spray (f_diff muzzle cycle + random spread), the tag dart, the tag-tracker volley
/// controller (firing missile_count missiles missile_delay apart), the type-1 tracker/closest-target
/// selection, shoot-down, and the splash damage. Only smart-trace world avoidance, proxy detonation and
/// waypoint sprites are left out.
/// </summary>
[Weapon]
public sealed class Seeker : Weapon
{
    /// <summary>Homing-missile balance — QC WEP_CVAR(WEP_SEEKER, missile_*).</summary>
    public struct MissileBalance
    {
        public float Accel;            // missile_accel
        public float Ammo;             // missile_ammo (rockets per missile)
        public float Animtime;         // missile_animtime
        public int   Count;            // missile_count (missiles launched per tag)
        public float DamageForceScale; // missile_damageforcescale
        public float Damage;           // missile_damage
        public float Decel;            // missile_decel
        public float Delay;            // missile_delay (between volley shots)
        public float EdgeDamage;       // missile_edgedamage
        public float Force;            // missile_force
        public float Health;           // missile_health (shootable missile hp)
        public float Lifetime;         // missile_lifetime
        public float Radius;           // missile_radius
        public float Refire;           // missile_refire
        public float Speed;            // missile_speed (launch speed)
        public float SpeedMax;         // missile_speed_max
        public float SpeedUp;          // missile_speed_up
        public float TurnRate;         // missile_turnrate
    }

    /// <summary>FLAC (secondary in type 0) balance — QC WEP_CVAR(WEP_SEEKER, flac_*).</summary>
    public struct FlacBalance
    {
        public float Ammo;         // flac_ammo
        public float Animtime;     // flac_animtime
        public float Damage;       // flac_damage
        public float EdgeDamage;   // flac_edgedamage
        public float Force;        // flac_force
        public float Lifetime;     // flac_lifetime
        public float LifetimeRand; // flac_lifetime_rand
        public float Radius;       // flac_radius
        public float Refire;       // flac_refire
        public float Speed;        // flac_speed (forward launch speed)
        public float SpeedUp;      // flac_speed_up
        public float Spread;       // flac_spread
    }

    /// <summary>Tag-dart (primary in type 0) balance — QC WEP_CVAR(WEP_SEEKER, tag_*).</summary>
    public struct TagBalance
    {
        public float Ammo;            // tag_ammo
        public float Animtime;        // tag_animtime
        public float DamageForceScale;// tag_damageforcescale
        public float Health;          // tag_health (shootable dart hp)
        public float Lifetime;        // tag_lifetime
        public float Refire;          // tag_refire
        public float Speed;           // tag_speed (launch speed)
        public float Spread;          // tag_spread
        public float TrackerLifetime; // tag_tracker_lifetime
    }

    public MissileBalance Missile;
    public FlacBalance Flac;
    public TagBalance Tag;

    /// <summary>g_balance_seeker_type — 0: tag(primary)/flac(secondary); 1: missiles(primary)/tag(secondary).</summary>
    public int Type;


    public Seeker()
    {
        NetName = "seeker";
        AmmoType = ResourceType.Rockets;   // QC ammo_type
        DisplayName = "T.A.G. Seeker";
        Impulse = 8;
        // WEP_FLAG_MUTATORBLOCKED | WEP_FLAG_RELOADABLE | WEP_TYPE_SPLASH
        SpawnFlags = WeaponFlags.MutatorBlocked | WeaponFlags.Reloadable | WeaponFlags.TypeSplash;
        Color = new Vector3(0.957f, 0.439f, 0.533f);
        ViewModel = "h_seeker.iqm";  // MDL_SEEKER_VIEW
        WorldModel = "v_seeker.md3"; // MDL_SEEKER_WORLD
        ItemModel = "g_seeker.md3";  // MDL_SEEKER_ITEM
    }

    public override void Configure()
    {
        Missile.Accel = Bal("g_balance_seeker_missile_accel", 1400f);
        Missile.Ammo = Bal("g_balance_seeker_missile_ammo", 2f);
        Missile.Animtime = Bal("g_balance_seeker_missile_animtime", 0.2f);
        Missile.Count = BalInt("g_balance_seeker_missile_count", 3);
        Missile.DamageForceScale = Bal("g_balance_seeker_missile_damageforcescale", 4f);
        Missile.Damage = Bal("g_balance_seeker_missile_damage", 30f);
        Missile.Decel = Bal("g_balance_seeker_missile_decel", 1400f);
        Missile.Delay = Bal("g_balance_seeker_missile_delay", 0.25f);
        Missile.EdgeDamage = Bal("g_balance_seeker_missile_edgedamage", 10f);
        Missile.Force = Bal("g_balance_seeker_missile_force", 150f);
        Missile.Health = Bal("g_balance_seeker_missile_health", 5f);
        Missile.Lifetime = Bal("g_balance_seeker_missile_lifetime", 15f);
        Missile.Radius = Bal("g_balance_seeker_missile_radius", 80f);
        Missile.Refire = Bal("g_balance_seeker_missile_refire", 0.5f);
        Missile.Speed = Bal("g_balance_seeker_missile_speed", 700f);
        Missile.SpeedMax = Bal("g_balance_seeker_missile_speed_max", 1300f);
        Missile.SpeedUp = Bal("g_balance_seeker_missile_speed_up", 300f);
        Missile.TurnRate = Bal("g_balance_seeker_missile_turnrate", 0.65f);

        Flac.Ammo = Bal("g_balance_seeker_flac_ammo", 1f);
        Flac.Animtime = Bal("g_balance_seeker_flac_animtime", 0.1f);
        Flac.Damage = Bal("g_balance_seeker_flac_damage", 15f);
        Flac.EdgeDamage = Bal("g_balance_seeker_flac_edgedamage", 10f);
        Flac.Force = Bal("g_balance_seeker_flac_force", 50f);
        Flac.Lifetime = Bal("g_balance_seeker_flac_lifetime", 0.1f);
        Flac.LifetimeRand = Bal("g_balance_seeker_flac_lifetime_rand", 0.05f);
        Flac.Radius = Bal("g_balance_seeker_flac_radius", 100f);
        Flac.Refire = Bal("g_balance_seeker_flac_refire", 0.1f);
        Flac.Speed = Bal("g_balance_seeker_flac_speed", 3000f);
        Flac.SpeedUp = Bal("g_balance_seeker_flac_speed_up", 1000f);
        Flac.Spread = Bal("g_balance_seeker_flac_spread", 0.4f);

        Tag.Ammo = Bal("g_balance_seeker_tag_ammo", 1f);
        Tag.Animtime = Bal("g_balance_seeker_tag_animtime", 0.2f);
        Tag.DamageForceScale = Bal("g_balance_seeker_tag_damageforcescale", 4f);
        Tag.Health = Bal("g_balance_seeker_tag_health", 5f);
        Tag.Lifetime = Bal("g_balance_seeker_tag_lifetime", 15f);
        Tag.Refire = Bal("g_balance_seeker_tag_refire", 0.75f);
        Tag.Speed = Bal("g_balance_seeker_tag_speed", 5000f);
        Tag.Spread = Bal("g_balance_seeker_tag_spread", 0f);
        Tag.TrackerLifetime = Bal("g_balance_seeker_tag_tracker_lifetime", 10f);

        Type = BalInt("g_balance_seeker_type", 0);
    }

    // METHOD(Seeker, wr_think) — common/weapons/weapon/seeker.qc
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        // Each mode is refire-gated (QC weapon_prepareattack with the missile/tag/flac refire per type).
        if (Type == 1)
        {
            // type 1: primary launches missiles at the closest tagged target, secondary fires the tag.
            if (fire == FireMode.Primary) { if (PrepareAttack(actor, slot, fire)) SeekerAttack(actor, slot); }
            else if (fire == FireMode.Secondary) { if (PrepareAttack(actor, slot, fire)) FireTag(actor, slot); }
        }
        else
        {
            // type 0 (default): primary fires the tag, secondary fires FLAC.
            if (fire == FireMode.Primary) { if (PrepareAttack(actor, slot, fire)) FireTag(actor, slot); }
            else if (fire == FireMode.Secondary) { if (PrepareAttack(actor, slot, fire)) FireFlac(actor, slot); }
        }
    }

    // W_Seeker_Attack (type 1) — fire a homing missile at the nearest tagged target with line of sight. seeker.qc
    private void SeekerAttack(Entity actor, WeaponSlot slot)
    {
        Entity? closest = null;
        foreach (var tag in _trackers)
        {
            if (!ReferenceEquals(tag.Owner, actor) || tag.IsFreed || tag.Enemy is null) continue;
            if (closest is null
                || (actor.Origin - tag.Enemy.Origin).LengthSquared() < (actor.Origin - closest.Origin).LengthSquared())
                closest = tag.Enemy;
        }
        if (closest is not null)
        {
            // LOS check: if a wall blocks the target, fire un-targeted.
            Vector3 eye = actor.Origin + actor.ViewOfs;
            TraceResult tr = Api.Trace.Trace(eye, Vector3.Zero, Vector3.Zero, closest.Origin, MoveFilter.NoMonsters, actor);
            if (tr.Fraction < 1f && !ReferenceEquals(tr.Ent, closest)) closest = null;
        }
        FireMissile(actor, slot, Vector3.Zero, closest);
    }

    // W_Seeker_Fire_Missile — a homing missile that steers toward its tagged target. seeker.qc
    private void FireMissile(Entity actor, WeaponSlot slot, Vector3 offset, Entity? target)
    {
        actor.TakeResource(AmmoType, Missile.Ammo);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out Vector3 up);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, new Vector3(-4, -4, -4), new Vector3(4, 4, 4));

        Entity missile = Api.Entities.Spawn();
        missile.ClassName = "seeker_missile";
        missile.Owner = actor;
        missile.NetName = NetName;
        missile.Enemy = target; // homing target (steered toward in W_Seeker_Missile_Think)
        missile.MoveType = MoveType.FlyMissile;
        missile.Solid = Solid.BBox;
        missile.Flags = EntFlags.Item; // QC FL_PROJECTILE
        Api.Entities.SetSize(missile, new Vector3(-4, -4, -4), new Vector3(4, 4, 4));
        Api.Entities.SetOrigin(missile, shot.Origin + offset);

        missile.TakeDamage = DamageMode.Yes; // shootable
        missile.Health = Missile.Health;
        missile.DamageForceScale = Missile.DamageForceScale;

        // W_SetupProjVelocity_UP_PRE: velocity = normalize(w_shotdir + up*(speed_up/speed)) * speed.
        missile.Velocity = WeaponFiring.ProjectileVelocity(shot.Dir, up, Missile.Speed, Missile.SpeedUp);
        missile.Angles = QMath.VecToAngles(missile.Velocity);

        float deathTime = Api.Clock.Time + Missile.Lifetime;
        missile.Touch = (self, other) => ExplodeMissile(self);
        missile.Think = self => MissileThink(self, deathTime);
        missile.ProjectileDamage = (self, attacker) => ExplodeMissile(self);
        missile.NextThink = Api.Clock.Time;

        // MUTATOR_CALLHOOK(EditProjectile, actor, missile) (seeker.qc W_Seeker_Fire_Missile).
        var ep = new MutatorHooks.EditProjectileArgs(actor, missile);
        MutatorHooks.EditProjectile.Call(ref ep);

        Api.Sound.Play(actor, SoundChannel.WeaponAuto, "weapons/seeker_fire.wav");
    }

    // W_Seeker_Missile_Think — accel/decel speed clamp + turnrate homing toward the tagged enemy. seeker.qc
    private void MissileThink(Entity self, float deathTime)
    {
        if (Api.Clock.Time > deathTime)
        {
            ExplodeMissile(self);
            return;
        }
        self.NextThink = Api.Clock.Time;

        float ft = Api.Clock.FrameTime;
        // spd = bound(spd - decel*ft, speed_max, spd + accel*ft) — accelerate toward speed_max.
        float spd = self.Velocity.Length();
        spd = QMath.Bound(spd - Missile.Decel * ft, Missile.SpeedMax, spd + Missile.Accel * ft);

        // Drop the target if it's gone/dead.
        Entity? enemy = self.Enemy;
        if (enemy is not null && (enemy.TakeDamage != DamageMode.Aim || enemy.DeadState != DeadFlag.No))
            enemy = self.Enemy = null;

        if (enemy is not null)
        {
            // newdir = normalize(olddir + desireddir * turnrate); velocity = newdir * spd.
            Vector3 eorg = 0.5f * (enemy.AbsMin + enemy.AbsMax);
            if (eorg == Vector3.Zero) eorg = enemy.Origin + (enemy.Mins + enemy.Maxs) * 0.5f;
            Vector3 desiredDir = QMath.Normalize(eorg - self.Origin);
            Vector3 oldDir = QMath.Normalize(self.Velocity);
            Vector3 newDir = QMath.Normalize(oldDir + desiredDir * Missile.TurnRate);
            self.Velocity = newDir * spd;
        }
        else
        {
            self.Velocity = QMath.Normalize(self.Velocity) * spd;
        }
        self.Angles = QMath.VecToAngles(self.Velocity);
    }

    // W_Seeker_Missile_Explode — radius damage + knockback, then remove. seeker.qc
    private void ExplodeMissile(Entity self)
    {
        self.Touch = null;
        self.Think = null;
        self.TakeDamage = DamageMode.No;
        WeaponSplash.RadiusDamage(self, self.Origin, Missile.Damage, Missile.EdgeDamage, Missile.Radius,
            self.Owner, RegistryId, Missile.Force);
        Api.Entities.Remove(self);
    }

    // W_Seeker_Fire_Flac — a single short-lived scattered explosive (the burst comes from rapid refire). seeker.qc
    private void FireFlac(Entity actor, WeaponSlot slot)
    {
        actor.TakeResource(AmmoType, Flac.Ammo);
        var st = actor.WeaponState(slot);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out Vector3 right, out Vector3 up);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, new Vector3(-2, -2, -2), new Vector3(2, 2, 2));

        // f_diff: a 4-position muzzle offset cycle (bulletcounter % 4) so the spray fans out side-to-side.
        Vector3 fdiff = (st.MiscBulletCounter & 3) switch
        {
            0 => new Vector3(-1.25f, -3.75f, 0f),
            1 => new Vector3(1.25f, -3.75f, 0f),
            2 => new Vector3(-1.25f, 3.75f, 0f),
            _ => new Vector3(1.25f, 3.75f, 0f),
        };
        ++st.MiscBulletCounter;
        Vector3 origin = shot.Origin + right * fdiff.X + forward * fdiff.Y + up * fdiff.Z;

        Entity missile = Api.Entities.Spawn();
        missile.ClassName = "missile";
        missile.Owner = actor;
        missile.NetName = NetName;
        missile.MoveType = MoveType.Fly;
        missile.Solid = Solid.BBox;
        missile.Flags = EntFlags.Item; // QC FL_PROJECTILE
        Api.Entities.SetSize(missile, new Vector3(-2, -2, -2), new Vector3(2, 2, 2));
        Api.Entities.SetOrigin(missile, origin);

        // W_SetupProjVelocity_UP_PRE(flac_): w_shotdir*speed + up*speed_up, with random per-shot spread.
        missile.Velocity = WeaponFiring.ProjectileVelocity(shot.Dir, up, Flac.Speed, Flac.SpeedUp, 0f, Flac.Spread);
        missile.Angles = QMath.VecToAngles(missile.Velocity);

        float deathTime = Api.Clock.Time + Flac.Lifetime + Prandom.Float() * Flac.LifetimeRand;
        missile.Touch = (self, other) => ExplodeFlac(self);
        missile.Think = self => ExplodeFlac(self); // adaptor_think2use_hittype_splash at lifetime
        missile.NextThink = deathTime;

        // MUTATOR_CALLHOOK(EditProjectile, actor, missile) (seeker.qc W_Seeker_Fire_Flac).
        var ep = new MutatorHooks.EditProjectileArgs(actor, missile);
        MutatorHooks.EditProjectile.Call(ref ep);

        Api.Sound.Play(actor, SoundChannel.WeaponAuto, "weapons/flac_fire.wav");
    }

    // W_Seeker_Flac_Explode — radius damage + knockback, then remove. seeker.qc
    private void ExplodeFlac(Entity self)
    {
        self.Touch = null;
        self.Think = null;
        self.TakeDamage = DamageMode.No;
        WeaponSplash.RadiusDamage(self, self.Origin, Flac.Damage, Flac.EdgeDamage, Flac.Radius,
            self.Owner, RegistryId, Flac.Force);
        Api.Entities.Remove(self);
    }

    // Active tag trackers (the C# successor to the QC g_seeker_trackers intrusive list).
    private readonly List<Entity> _trackers = new();

    // W_Seeker_Fire_Tag — fire a tag dart that marks the player it hits for a homing-missile volley. seeker.qc
    private void FireTag(Entity actor, WeaponSlot slot)
    {
        actor.TakeResource(AmmoType, Tag.Ammo);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, new Vector3(-2, -2, -2), new Vector3(2, 2, 2));

        Entity missile = Api.Entities.Spawn();
        missile.ClassName = "seeker_tag";
        missile.Owner = actor;
        missile.NetName = NetName;
        missile.MoveType = MoveType.Fly;
        missile.Solid = Solid.BBox;
        missile.Flags = EntFlags.Item; // QC FL_PROJECTILE
        Api.Entities.SetSize(missile, new Vector3(-2, -2, -2), new Vector3(2, 2, 2));
        Api.Entities.SetOrigin(missile, shot.Origin);

        missile.TakeDamage = DamageMode.Yes; // shootable
        missile.Health = Tag.Health;
        missile.DamageForceScale = Tag.DamageForceScale;

        // W_SetupProjVelocity_PRE(tag_): velocity = w_shotdir * speed (with tag_spread, normally 0).
        missile.Velocity = WeaponFiring.ProjectileVelocity(shot.Dir, Vector3.UnitZ, Tag.Speed, 0f, 0f, Tag.Spread);
        missile.Angles = QMath.VecToAngles(missile.Velocity);

        missile.Touch = (self, other) => TagTouch(self, other, slot);
        missile.Think = self => Api.Entities.Remove(self);     // SUB_Remove at lifetime
        missile.ProjectileDamage = (self, attacker) => Api.Entities.Remove(self); // W_Seeker_Tag_Damage shoot-down
        missile.NextThink = Api.Clock.Time + Tag.Lifetime;

        // MUTATOR_CALLHOOK(EditProjectile, actor, missile) (seeker.qc W_Seeker_Fire_Tag).
        var ep = new MutatorHooks.EditProjectileArgs(actor, missile);
        MutatorHooks.EditProjectile.Call(ref ep);

        Api.Sound.Play(actor, SoundChannel.WeaponAuto, "weapons/tag_fire.wav");
    }

    // W_Seeker_Tag_Touch — on a player, spawn a tag tracker whose volley controller fires missile_count
    // homing missiles at the tagged target over time (missile_delay between shots). seeker.qc
    private void TagTouch(Entity self, Entity other, WeaponSlot slot)
    {
        bool hitPlayer = other.TakeDamage == DamageMode.Aim || (other.Flags & EntFlags.Client) != 0;
        if (hitPlayer && self.Owner is not null && other.DeadState == DeadFlag.No)
        {
            Entity owner = self.Owner;

            if (Type == 1)
            {
                // type 1: the tag just registers a tracker the primary fires missiles at (no auto-volley).
                Entity tracker = Api.Entities.Spawn();
                tracker.ClassName = "tag_tracker";
                tracker.Owner = owner;
                tracker.Enemy = other;
                tracker.MaxHealth = Api.Clock.Time + Tag.TrackerLifetime; // tracker expiry
                _trackers.Add(tracker);
                tracker.Think = t =>
                {
                    if (t.Enemy is null || t.Enemy.DeadState != DeadFlag.No || Api.Clock.Time > t.MaxHealth)
                    {
                        _trackers.Remove(t);
                        Api.Entities.Remove(t);
                        return;
                    }
                    t.NextThink = Api.Clock.Time;
                };
                tracker.NextThink = Api.Clock.Time;
            }
            else
            {
                // type 0: spawn a volley controller that fires missile_count missiles, missile_delay apart.
                Entity ctrl = Api.Entities.Spawn();
                ctrl.ClassName = "tag_tracker";
                ctrl.Owner = owner;
                ctrl.Enemy = other;
                ctrl.Count = (int)MathF.Max(1f, Missile.Count); // shots remaining
                _trackers.Add(ctrl);
                ctrl.Think = c => VolleyControllerThink(c, slot);
                ctrl.NextThink = Api.Clock.Time;
            }
        }
        Api.Entities.Remove(self);
    }

    // W_Seeker_Vollycontroller_Think — fire one missile per tick at the tagged target until the count runs out.
    private void VolleyControllerThink(Entity ctrl, WeaponSlot slot)
    {
        --ctrl.Count;
        if (ctrl.Owner is null || ctrl.Owner.DeadState != DeadFlag.No || ctrl.Count < 0
            || ctrl.Enemy is null || ctrl.Enemy.DeadState != DeadFlag.No)
        {
            _trackers.Remove(ctrl);
            Api.Entities.Remove(ctrl);
            return;
        }
        // 4-position muzzle offset cycle, like FLAC's f_diff.
        Vector3 fdiff = (ctrl.Count & 3) switch
        {
            0 => new Vector3(-1.25f, -3.75f, 0f),
            1 => new Vector3(1.25f, -3.75f, 0f),
            2 => new Vector3(-1.25f, 3.75f, 0f),
            _ => new Vector3(1.25f, 3.75f, 0f),
        };
        FireMissile(ctrl.Owner, slot, fdiff, ctrl.Enemy);
        ctrl.NextThink = Api.Clock.Time + Missile.Delay;
    }

    // QC the Seeker refire/animtime are sub-weapon-specific (missile/tag/flac), and which sub-weapon a mode
    // uses depends on g_balance_seeker_type: type 1 -> primary=missile, secondary=tag; type 0 -> primary=tag,
    // secondary=flac. Mirror that selection here (the default _primary_/_secondary_ cvar convention misses it).
    public override float RefireFor(FireMode fire)
    {
        bool secondary = fire == FireMode.Secondary;
        if (Type == 1) return secondary ? Tag.Refire : Missile.Refire;
        return secondary ? Flac.Refire : Tag.Refire;
    }
    public override float AnimtimeFor(FireMode fire)
    {
        bool secondary = fire == FireMode.Secondary;
        if (Type == 1) return secondary ? Tag.Animtime : Missile.Animtime;
        return secondary ? Flac.Animtime : Tag.Animtime;
    }

    // METHOD(Seeker, wr_checkammo1) — seeker.qc (type 0: tag; type 1: missile).
    public bool CheckAmmoPrimary(Entity actor)
        => actor.GetResource(AmmoType) >= (Type == 1 ? Missile.Ammo : Tag.Ammo);

    // METHOD(Seeker, wr_checkammo2) — seeker.qc (type 0: flac; type 1: tag).
    public bool CheckAmmoSecondary(Entity actor)
        => actor.GetResource(AmmoType) >= (Type == 1 ? Tag.Ammo : Flac.Ammo);
}
