using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Devastator (Nexuiz "Rocket Launcher") — port of common/weapons/weapon/devastator.{qh,qc}. A splash
/// weapon: primary fire launches a single rocket that accelerates from speedstart toward speed, detonates
/// on contact (or at end of lifetime) dealing radius damage + knockback; secondary fire remote-detonates
/// rockets already in flight. The rocket is damageable (can be shot down).
///
/// Identity/attributes from devastator.qh; balance from bal-wep-xonotic.cfg (g_balance_devastator_*).
/// This port covers the rocket projectile, its acceleration, laser guiding (W_Devastator_SteerTo steering
/// toward the owner's aim, gated by guidedelay/guiderate + the rl_release latch), the spawnshield/proximity
/// remote-detonation gate, shoot-down, and the splash damage. Only the rocket-jump remote variant, the
/// airshot achievement and CSQC fly-sound networking are left out (render/online).
/// </summary>
[Weapon]
public sealed class Devastator : Weapon
{
    /// <summary>Balance block — QC WEP_CVAR(WEP_DEVASTATOR, *) (single fire mode, no PRI/SEC split).</summary>
    public struct Balance
    {
        public float Ammo;            // g_balance_devastator_ammo (rockets per shot)
        public float Animtime;        // g_balance_devastator_animtime
        public float Damage;          // g_balance_devastator_damage
        public float DamageForceScale;// g_balance_devastator_damageforcescale
        public float DetonateDelay;   // g_balance_devastator_detonatedelay (>=0 timer, <0 proximity)
        public float EdgeDamage;      // g_balance_devastator_edgedamage
        public float Force;           // g_balance_devastator_force
        public float ForceXyScale;    // g_balance_devastator_force_xyscale
        public float GuideDelay;      // g_balance_devastator_guidedelay (delay before steering starts)
        public float GuideGoal;       // g_balance_devastator_guidegoal (goal point distance along the aim ray)
        public float GuideRate;       // g_balance_devastator_guiderate
        public float GuideRateDelay;  // g_balance_devastator_guideratedelay (ramp-in duration for the turn rate)
        public float Health;          // g_balance_devastator_health (shootable rocket hp)
        public float Lifetime;        // g_balance_devastator_lifetime
        public float Radius;          // g_balance_devastator_radius
        public float Refire;          // g_balance_devastator_refire
        public float RemoteDamage;    // g_balance_devastator_remote_damage
        public float RemoteEdgeDamage;// g_balance_devastator_remote_edgedamage
        public float RemoteForce;     // g_balance_devastator_remote_force
        public float RemoteRadius;    // g_balance_devastator_remote_radius
        public float Speed;           // g_balance_devastator_speed (target/top speed)
        public float SpeedAccel;      // g_balance_devastator_speedaccel
        public float SpeedStart;      // g_balance_devastator_speedstart (launch speed)
    }

    public Balance Cvars;


    public Devastator()
    {
        NetName = "devastator";
        AmmoType = ResourceType.Rockets;   // QC ammo_type
        DisplayName = "Devastator";
        Impulse = 9;
        // WEP_FLAG_NORMAL | WEP_FLAG_RELOADABLE | WEP_FLAG_CANCLIMB | WEP_TYPE_SPLASH
        SpawnFlags = WeaponFlags.Normal | WeaponFlags.Reloadable | WeaponFlags.CanClimb | WeaponFlags.TypeSplash;
        Color = new Vector3(0.914f, 0.745f, 0.341f);
        ViewModel = "h_rl.iqm";  // MDL_DEVASTATOR_VIEW
        WorldModel = "v_rl.md3"; // MDL_DEVASTATOR_WORLD
        ItemModel = "g_rl.md3";  // MDL_DEVASTATOR_ITEM
    }

    public override void Configure()
    {
        Cvars.Ammo = Bal("g_balance_devastator_ammo", 4f);
        Cvars.Animtime = Bal("g_balance_devastator_animtime", 0.4f);
        Cvars.Damage = Bal("g_balance_devastator_damage", 80f);
        Cvars.DamageForceScale = Bal("g_balance_devastator_damageforcescale", 0f);
        Cvars.DetonateDelay = Bal("g_balance_devastator_detonatedelay", 0.02f);
        Cvars.EdgeDamage = Bal("g_balance_devastator_edgedamage", 40f);
        Cvars.Force = Bal("g_balance_devastator_force", 400f);
        Cvars.ForceXyScale = Bal("g_balance_devastator_force_xyscale", 1f);
        Cvars.GuideDelay = Bal("g_balance_devastator_guidedelay", 0.2f);
        Cvars.GuideGoal = Bal("g_balance_devastator_guidegoal", 512f);
        Cvars.GuideRate = Bal("g_balance_devastator_guiderate", 90f);
        Cvars.GuideRateDelay = Bal("g_balance_devastator_guideratedelay", 0.01f);
        Cvars.Health = Bal("g_balance_devastator_health", 30f);
        Cvars.Lifetime = Bal("g_balance_devastator_lifetime", 10f);
        Cvars.Radius = Bal("g_balance_devastator_radius", 110f);
        Cvars.Refire = Bal("g_balance_devastator_refire", 1.1f);
        Cvars.RemoteDamage = Bal("g_balance_devastator_remote_damage", 70f);
        Cvars.RemoteEdgeDamage = Bal("g_balance_devastator_remote_edgedamage", 35f);
        Cvars.RemoteForce = Bal("g_balance_devastator_remote_force", 300f);
        Cvars.RemoteRadius = Bal("g_balance_devastator_remote_radius", 110f);
        Cvars.Speed = Bal("g_balance_devastator_speed", 1300f);
        Cvars.SpeedAccel = Bal("g_balance_devastator_speedaccel", 1300f);
        Cvars.SpeedStart = Bal("g_balance_devastator_speedstart", 1000f);
    }

    // METHOD(Devastator, wr_think) — common/weapons/weapon/devastator.qc
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        var st = actor.WeaponState(slot);

        if (fire == FireMode.Primary)
        {
            // rl_release latch: the primary must be re-pressed between launches (otherwise holding fire
            // would just guide the rocket). guidestop disables guiding and so disables the latch too. The
            // latch is reset whenever the primary button is NOT held (QC the !PHYS_INPUT_BUTTON_ATCK branch).
            if (!st.ButtonAttack)
            {
                st.RlRelease = true; // primary released -> next press may launch again
            }
            else
            {
                bool guidestop = Api.Services is not null && Api.Cvars.GetFloat("g_balance_devastator_guidestop") != 0f;
                // Launch only on a fresh press (latch set) or when guiding is disabled, AND when the refire
                // gate + ammo allow (QC weapon_prepareattack(..., refire)).
                if ((st.RlRelease || guidestop) && PrepareAttack(actor, slot, fire))
                {
                    Attack(actor, slot, st, secondary: false);
                    st.RlRelease = false;
                }
            }
        }

        if (fire == FireMode.Secondary)
        {
            // Flag every live rocket this actor owns to remote-detonate (gated by spawnshield/proximity in
            // the rocket's think, W_Devastator_RemoteExplode). This is a press action, not a refire-gated shot.
            RemoteDetonate(actor);
        }
    }

    // Refire/animtime from the (cvar-seeded) balance block — the Devastator has a single fire mode (the
    // secondary remote-detonate is a press action, not a refire-gated shot).
    public override float RefireFor(FireMode fire) => Cvars.Refire;
    public override float AnimtimeFor(FireMode fire) => Cvars.Animtime;

    // W_Devastator_Attack — spawn an accelerating, guidable, detonatable rocket. devastator.qc
    private void Attack(Entity actor, WeaponSlot slot, WeaponSlotState st, bool secondary)
    {
        actor.TakeResource(AmmoType, Cvars.Ammo);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, new Vector3(-3, -3, -3), new Vector3(3, 3, 3));

        Entity missile = Api.Entities.Spawn();
        missile.ClassName = "rocket";
        missile.Owner = actor;
        missile.NetName = NetName;
        missile.MoveType = MoveType.Fly;
        // QC PROJECTILE_MAKETRIGGER: SOLID_CORPSE + dphitcontentsmask SOLID|BODY|CORPSE so the rocket is
        // transparent to player movement (no CORPSE in a player's move mask) — this is what stops the rocket
        // colliding with / detonating on its firer — yet still hits the world, bodies and corpses.
        Projectiles.MakeTrigger(missile);
        missile.Flags = EntFlags.Item; // QC FL_PROJECTILE
        missile.DamageForceScale = Cvars.DamageForceScale;

        // setsize '-3 -3 -3'..'3 3 3' so the rocket can be shot down.
        Api.Entities.SetSize(missile, new Vector3(-3, -3, -3), new Vector3(3, 3, 3));
        // setorigin(missile, w_shotorg - v_forward * 3) — back it off so it hits the wall at the right point.
        Api.Entities.SetOrigin(missile, shot.Origin - shot.Dir * 3f);

        // Shootable rocket (event_damage -> W_Devastator_Damage): give it health + DAMAGE_YES.
        missile.TakeDamage = DamageMode.Yes;
        missile.Health = Cvars.Health;

        // W_SetupProjVelocity_Basic(missile, speedstart, 0) — launches at speedstart, the think accelerates it.
        missile.Velocity = shot.Dir * Cvars.SpeedStart;
        missile.Angles = QMath.VecToAngles(missile.Velocity);

        float deathTime = Api.Clock.Time + Cvars.Lifetime;
        // QC missile.spawnshieldtime (devastator.qc:295-298): detonatedelay >= 0 sets a remote-detonation TIMER
        // gate; < 0 = proximity-safety based. Stored on the entity (Entity.ProjectileDetonateTime) — NOT a
        // closure local — so the Rocket Flying mutator's EditProjectile write (proj.spawnshieldtime = time)
        // can reach the gate W_Devastator_RemoteExplode reads. Seeded BEFORE the EditProjectile call below so
        // the mutator's clear wins (QC ordering: seed at :295, MUTATOR_CALLHOOK at :333).
        missile.ProjectileDetonateTime = Cvars.DetonateDelay >= 0f ? Api.Clock.Time + Cvars.DetonateDelay : -1f;
        // pushltime: the guide-delay before steering kicks in (reuse LTime).
        missile.LTime = Api.Clock.Time + Cvars.GuideDelay;
        missile.Count = 0; // guide-active sound guard

        // Register this as the actor's guided rocket; rl_detonate_later from secondary edge at launch.
        st.LastRocket = missile;
        if (secondary) missile.DeadState = DeadFlag.Dying; // rl_detonate_later latch (reuse DeadState)

        missile.Touch = (self, other) => OnTouch(self, other);
        missile.Think = self => OnThink(self, actor, slot, deathTime);
        missile.ProjectileDamage = (self, attacker) => Explode(self, null);
        missile.NextThink = Api.Clock.Time;

        // MUTATOR_CALLHOOK(EditProjectile, actor, missile) (devastator.qc) — before the rocket's first think,
        // so invincibleproj zeroes the rocket's health before it can be shot down.
        var ep = new MutatorHooks.EditProjectileArgs(actor, missile);
        MutatorHooks.EditProjectile.Call(ref ep);

        Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/rocket_fire.wav");

        if (Api.Clock.Time >= missile.NextThink)
            missile.Think(missile);
    }

    // W_Devastator_Think — accelerate toward top speed, laser-guide toward the owner's aim, and handle
    // remote detonation; detonate at end of lifetime. devastator.qc
    private void OnThink(Entity self, Entity owner, WeaponSlot slot, float deathTime)
    {
        self.NextThink = Api.Clock.Time;
        if (Api.Clock.Time > deathTime)
        {
            Explode(self, null);
            return;
        }

        // accelerate: velspeed = speed - (velocity . forward); add up to speedaccel*frametime along forward.
        Vector3 forward = QMath.Normalize(self.Velocity);
        if (forward == Vector3.Zero) forward = QMath.Forward(self.Angles);
        float velAlong = QMath.Dot(self.Velocity, forward);
        float velSpeed = Cvars.Speed - velAlong;
        if (velSpeed > 0f)
            self.Velocity += forward * MathF.Min(Cvars.SpeedAccel * Api.Clock.FrameTime, velSpeed);

        // Laser guiding (W_Devastator_Think): while this is the owner's active rocket, the primary is still HELD
        // (rl_release latch clear — releasing fire stops guiding and re-arms a launch), the guide-delay has passed,
        // and the owner is alive, steer the rocket toward the owner's aim, capped at guiderate deg/s.
        var st = owner.WeaponState(slot);
        if (ReferenceEquals(st.LastRocket, self) && !st.RlRelease && Cvars.GuideRate > 0f
            && Api.Clock.Time > self.LTime && owner.DeadState == DeadFlag.No)
        {
            // guideratedelay ramp: the turn rate eases in over guideratedelay after steering starts.
            float f = Cvars.GuideRateDelay > 0f
                ? QMath.Clamp((Api.Clock.Time - self.LTime) / Cvars.GuideRateDelay, 0f, 1f) : 1f;

            float curSpeed = self.Velocity.Length();
            QMath.AngleVectors(owner.Angles, out Vector3 desiredDir, out _, out _); // owner view forward (v_angle)
            Vector3 desiredOrigin = owner.Origin + owner.ViewOfs;                   // aim-ray origin (no dual-wield offset)
            Vector3 olddir = QMath.Normalize(self.Velocity);

            // QC goal-point steering: project the rocket onto the aim ray and target a point guidegoal units
            // further along it, then steer toward that goal — so the rocket curves ONTO the aim line (lead),
            // not merely parallel to it (the old pure-direction approximation never converged to the crosshair).
            Vector3 goal = desiredOrigin
                + (QMath.Dot(self.Origin - desiredOrigin, desiredDir) + Cvars.GuideGoal) * desiredDir;
            Vector3 newdir = SteerTo(olddir, QMath.Normalize(goal - self.Origin),
                MathF.Cos(Cvars.GuideRate * f * Api.Clock.FrameTime * QMath.Deg2Rad));

            self.Velocity = newdir * curSpeed;
            self.Angles = QMath.VecToAngles(self.Velocity);

            if (self.Count == 0)
            {
                Api.Sound.Play(owner, SoundChannel.Body, "weapons/rocket_mode.wav");
                self.Count = 1;
            }
        }
        else
        {
            self.Angles = QMath.VecToAngles(self.Velocity);
        }

        // rl_detonate_later: explode once the spawnshield timer (or proximity safety) clears.
        if (self.DeadState == DeadFlag.Dying && ReferenceEquals(st.LastRocket, self))
            RemoteExplode(self, owner);
    }

    /// <summary>
    /// Port of W_Devastator_SteerTo (devastator.qc): rotate <paramref name="thisdir"/> toward
    /// <paramref name="goaldir"/> by at most the angle whose cosine is <paramref name="maxTurnCos"/>. Solves
    /// the same quadratic QC does for the blend factor; refuses to guide when nearly antiparallel.
    /// </summary>
    private static Vector3 SteerTo(Vector3 thisdir, Vector3 goaldir, float maxTurnCos)
    {
        float f = QMath.Dot(thisdir, goaldir);
        if (f > maxTurnCos) return goaldir;       // already within the cone
        if (f < -0.9998f) return thisdir;          // ~antiparallel: refuse (avoid numerical blow-up)

        float m2 = maxTurnCos * maxTurnCos;
        // 0 = (m2 - f^2) x^2 + (2 f (m2 - 1)) x + (m2 - 1) ; take the larger root.
        float a = m2 - f * f, b = 2f * f * (m2 - 1f), c = m2 - 1f;
        float x;
        if (MathF.Abs(a) < 1e-6f)
        {
            x = (MathF.Abs(b) < 1e-6f) ? 0f : -c / b;
        }
        else
        {
            float disc = b * b - 4f * a * c;
            if (disc < 0f) return goaldir;
            float sd = MathF.Sqrt(disc);
            x = (-b + sd) / (2f * a); // larger solution
        }
        return QMath.Normalize(thisdir + goaldir * x);
    }

    // W_Devastator_RemoteExplode — only detonate once the spawnshield timer / proximity safety allows. The gate
    // is the rocket's own Entity.ProjectileDetonateTime (QC this.spawnshieldtime), so the Rocket Flying mutator's
    // EditProjectile write (= time) reaches it: time >= the just-set timer => detonation opens immediately.
    private void RemoteExplode(Entity self, Entity owner)
    {
        if (owner.DeadState != DeadFlag.No) return;
        bool allowed;
        if (self.ProjectileDetonateTime >= 0f)
            allowed = Api.Clock.Time >= self.ProjectileDetonateTime; // timer gate (QC time >= spawnshieldtime)
        else
            allowed = (self.Origin - (owner.Origin + (owner.Mins + owner.Maxs) * 0.5f)).Length() > Cvars.RemoteRadius; // safety
        if (allowed)
            DoRemoteExplode(self, owner);
    }

    // W_Devastator_DoRemoteExplode — the actual remote (secondary) blast: remote_* balance.
    private void DoRemoteExplode(Entity self, Entity owner)
    {
        self.Touch = null;
        self.Think = null;
        self.TakeDamage = DamageMode.No;
        self.ProjectileDamage = null;
        if (owner.WeaponState(new WeaponSlot(0)) is { } s0 && ReferenceEquals(s0.LastRocket, self)) s0.LastRocket = null;

        WeaponSplash.RadiusDamage(self, self.Origin, Cvars.RemoteDamage, Cvars.RemoteEdgeDamage,
            Cvars.RemoteRadius, owner, RegistryId, Cvars.RemoteForce, forceZScale: Cvars.ForceXyScale);
        Api.Entities.Remove(self);
    }

    // W_Devastator_Touch — explode on contact with the world or an entity. devastator.qc
    private void OnTouch(Entity self, Entity other) => Explode(self, other);

    // W_Devastator_Explode — radius damage + knockback (force_xyscale shaping) at impact, then remove. devastator.qc
    private void Explode(Entity self, Entity? directHit)
    {
        self.Touch = null;
        self.Think = null;
        self.TakeDamage = DamageMode.No;
        self.ProjectileDamage = null;

        // QC scales the horizontal knockback by force_xyscale (defaults to 1 in xonotic balance, so the
        // blast is the unmodified RadiusDamage); the direct-hit entity skips the LOS reduction.
        WeaponSplash.RadiusDamage(self, self.Origin, Cvars.Damage, Cvars.EdgeDamage, Cvars.Radius,
            self.Owner, RegistryId, Cvars.Force, directHit: directHit);

        Api.Entities.Remove(self);
    }

    // wr_think secondary — flag every live rocket this actor owns to remote-detonate (rl_detonate_later).
    // The rocket's own think then blasts it once its spawnshield/proximity safety gate allows (so you can't
    // instantly detonate a rocket that's still inside your own blast radius). devastator.qc
    private void RemoteDetonate(Entity actor)
    {
        bool any = false;
        foreach (Entity e in Api.Entities.FindByClass("rocket"))
        {
            if (!ReferenceEquals(e.Owner, actor) || e.IsFreed) continue;
            if (e.NetName != NetName) continue;
            if (e.DeadState != DeadFlag.Dying) // not yet flagged
            {
                e.DeadState = DeadFlag.Dying; // rl_detonate_later latch
                any = true;
            }
        }
        if (any)
            Api.Sound.Play(actor, SoundChannel.Body, "weapons/rocket_det.wav");
    }

    // METHOD(Devastator, wr_checkammo1) — devastator.qc
    public bool CheckAmmoPrimary(Entity actor) => actor.GetResource(AmmoType) >= Cvars.Ammo;
}
