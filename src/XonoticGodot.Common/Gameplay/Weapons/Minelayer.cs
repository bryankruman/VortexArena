using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Mine Layer — port of common/weapons/weapon/minelayer.{qh,qc}. A splash weapon: primary lobs a mine
/// (MOVETYPE_TOSS) that sticks where it lands and arms; it detonates when an enemy comes within proximity
/// range (sooner the closer they get), at the end of its lifetime, or when remote-detonated. Secondary
/// remote-detonates all of this player's placed mines. Mines are shootable and limited in number.
///
/// Identity/attributes from minelayer.qh; balance from bal-wep-xonotic.cfg (g_balance_minelayer_*).
/// This port covers the mine projectile, surface-sticking (W_MineLayer_Stick lock-in-place), the lifetime
/// countdown, proximity-trigger detonation with the team-safety protection delay, owner-death auto-detonate,
/// mine shoot-down (damageforcescale knock-loose + destroy), the spawnshield/team-safety gated remote
/// detonate, and the splash damage. Only the airshot achievement, the exact surface-normal facing and CSQC
/// mine networking are left out (render/online).
/// </summary>
[Weapon]
public sealed class Minelayer : Weapon
{
    /// <summary>Balance block — QC WEP_CVAR(WEP_MINE_LAYER, *) (single block, no PRI/SEC split).</summary>
    public struct Balance
    {
        public float Ammo;               // g_balance_minelayer_ammo (rockets per mine)
        public float Animtime;           // g_balance_minelayer_animtime
        public float DamageForceScale;   // g_balance_minelayer_damageforcescale
        public float Damage;             // g_balance_minelayer_damage
        public float DetonateDelay;      // g_balance_minelayer_detonatedelay (<0 = no remote-detonate gate)
        public float EdgeDamage;         // g_balance_minelayer_edgedamage
        public float Force;              // g_balance_minelayer_force
        public float Health;             // g_balance_minelayer_health (shootable mine hp)
        public float Lifetime;           // g_balance_minelayer_lifetime
        public float LifetimeCountdown;  // g_balance_minelayer_lifetime_countdown
        public int   Limit;              // g_balance_minelayer_limit (max simultaneous mines)
        public float ProximityRadius;    // g_balance_minelayer_proximity_radius
        public float ProximityTimeCore;  // g_balance_minelayer_proximity_time_core
        public float ProximityTimeEdge;  // g_balance_minelayer_proximity_time_edge
        public float Radius;             // g_balance_minelayer_radius
        public float Refire;             // g_balance_minelayer_refire
        public float RemoteDamage;       // g_balance_minelayer_remote_damage
        public float RemoteEdgeDamage;   // g_balance_minelayer_remote_edgedamage
        public float RemoteForce;        // g_balance_minelayer_remote_force
        public float RemoteRadius;       // g_balance_minelayer_remote_radius
        public float Speed;              // g_balance_minelayer_speed (launch speed)
    }

    public Balance Cvars;


    public Minelayer()
    {
        NetName = "minelayer";
        AmmoType = ResourceType.Rockets;   // QC ammo_type
        DisplayName = "Mine Layer";
        Impulse = 4;
        // WEP_FLAG_MUTATORBLOCKED | WEP_FLAG_RELOADABLE | WEP_TYPE_SPLASH
        SpawnFlags = WeaponFlags.MutatorBlocked | WeaponFlags.Reloadable | WeaponFlags.TypeSplash;
        Color = new Vector3(0.988f, 0.514f, 0.392f);
        ViewModel = "h_minelayer.iqm";  // MDL_MINELAYER_VIEW
        WorldModel = "v_minelayer.md3"; // MDL_MINELAYER_WORLD
        ItemModel = "g_minelayer.md3";  // MDL_MINELAYER_ITEM
    }

    public override void Configure()
    {
        Cvars.Ammo = Bal("g_balance_minelayer_ammo", 4f);
        Cvars.Animtime = Bal("g_balance_minelayer_animtime", 0.4f);
        Cvars.DamageForceScale = Bal("g_balance_minelayer_damageforcescale", 1.25f);
        Cvars.Damage = Bal("g_balance_minelayer_damage", 40f);
        Cvars.DetonateDelay = Bal("g_balance_minelayer_detonatedelay", -1f);
        Cvars.EdgeDamage = Bal("g_balance_minelayer_edgedamage", 20f);
        Cvars.Force = Bal("g_balance_minelayer_force", 250f);
        Cvars.Health = Bal("g_balance_minelayer_health", 15f);
        Cvars.Lifetime = Bal("g_balance_minelayer_lifetime", 30f);
        Cvars.LifetimeCountdown = Bal("g_balance_minelayer_lifetime_countdown", 0.5f);
        Cvars.Limit = BalInt("g_balance_minelayer_limit", 4);
        Cvars.ProximityRadius = Bal("g_balance_minelayer_proximity_radius", 125f);
        Cvars.ProximityTimeCore = Bal("g_balance_minelayer_proximity_time_core", 0.1f);
        Cvars.ProximityTimeEdge = Bal("g_balance_minelayer_proximity_time_edge", 0.3f);
        Cvars.Radius = Bal("g_balance_minelayer_radius", 175f);
        Cvars.Refire = Bal("g_balance_minelayer_refire", 1f);
        Cvars.RemoteDamage = Bal("g_balance_minelayer_remote_damage", 40f);
        Cvars.RemoteEdgeDamage = Bal("g_balance_minelayer_remote_edgedamage", 20f);
        Cvars.RemoteForce = Bal("g_balance_minelayer_remote_force", 300f);
        Cvars.RemoteRadius = Bal("g_balance_minelayer_remote_radius", 200f);
        Cvars.Speed = Bal("g_balance_minelayer_speed", 800f);
    }

    // METHOD(MineLayer, wr_think) — common/weapons/weapon/minelayer.qc
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        if (fire == FireMode.Primary)
        {
            // QC checks the per-player mine limit before prepareattack, so an over-limit press doesn't burn
            // the refire timer; then gates the lay on the refire (weapon_prepareattack).
            if (Cvars.Limit > 0 && CountMines(actor) >= Cvars.Limit)
                return;
            if (PrepareAttack(actor, slot, fire))
                Attack(actor, slot);
        }
        else if (fire == FireMode.Secondary)
        {
            // Remote-detonate the actor's mines on the secondary press (not a refire-gated shot).
            RemoteDetonate(actor);
        }
    }

    // Refire/animtime from the (cvar-seeded) balance block — the Mine Layer has a single lay-mine fire mode
    // (the secondary remote-detonate is a press action, not a refire-gated shot).
    public override float RefireFor(FireMode fire) => Cvars.Refire;
    public override float AnimtimeFor(FireMode fire) => Cvars.Animtime;

    // W_MineLayer_Attack — lay a mine that arms and proximity-detonates. minelayer.qc
    private void Attack(Entity actor, WeaponSlot slot)
    {
        // QC enforces the per-player mine limit before firing.
        if (Cvars.Limit > 0 && CountMines(actor) >= Cvars.Limit)
            return;

        actor.TakeResource(AmmoType, Cvars.Ammo);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, new Vector3(-4, -4, -4), new Vector3(4, 4, 4));

        Entity mine = Api.Entities.Spawn();
        mine.ClassName = "mine";
        mine.Owner = actor;
        mine.NetName = NetName;
        mine.MoveType = MoveType.Toss;
        mine.Solid = Solid.BBox;
        mine.Flags = EntFlags.Item; // QC FL_PROJECTILE
        Api.Entities.SetSize(mine, new Vector3(-4, -4, -4), new Vector3(4, 4, 4));
        // setorigin(mine, w_shotorg - v_forward * 4) so it hits the wall at the right point.
        Api.Entities.SetOrigin(mine, shot.Origin - shot.Dir * 4f);

        mine.TakeDamage = DamageMode.Yes; // shootable
        mine.Health = Cvars.Health;
        mine.DamageForceScale = Cvars.DamageForceScale;

        // QC mine.spawnshieldtime (minelayer.qc:316-319): detonatedelay >= 0 sets a spawnshield TIMER gating
        // remote detonation; < 0 = proximity-safety based (mines default to -1, so proximity). Stored on
        // Entity.ProjectileDetonateTime (the shared rocket/mine gate field) — NOT mine.LTime — so the Rocket
        // Flying mutator's EditProjectile write reaches the gate W_MineLayer_RemoteExplode reads. Seeded BEFORE
        // the EditProjectile call below so the mutator's clear wins (QC: seed at :316, MUTATOR_CALLHOOK at :357).
        mine.ProjectileDetonateTime = Cvars.DetonateDelay >= 0f ? Api.Clock.Time + Cvars.DetonateDelay : -1f;

        // W_SetupProjVelocity_Basic(mine, speed, 0).
        mine.Velocity = shot.Dir * Cvars.Speed;
        mine.Angles = QMath.VecToAngles(mine.Velocity);

        // cnt = (lifetime - lifetime_countdown) + time is the forced-detonation deadline.
        float deathTime = Api.Clock.Time + (Cvars.Lifetime - Cvars.LifetimeCountdown);
        mine.Count = 0;       // bounce counter for re-grounding after a damage knock-off
        mine.MaxHealth = 0f;  // QC .mine_time: scheduled proximity-explosion time (0 = not yet armed)

        mine.Think = self => OnThink(self, deathTime);
        mine.NextThink = Api.Clock.Time;
        mine.Touch = (self, other) => OnTouch(self, other);
        // W_MineLayer_Damage: knock the mine loose (damageforcescale) and/or destroy it when its HP is gone.
        mine.ProjectileDamage = (self, attacker) => OnMineDamage(self, attacker);

        // MUTATOR_CALLHOOK(EditProjectile, actor, mine) (minelayer.qc).
        var ep = new MutatorHooks.EditProjectileArgs(actor, mine);
        MutatorHooks.EditProjectile.Call(ref ep);

        Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/mine_fire.wav");
    }

    // W_MineLayer_Damage — a shot mine: if HP remains, the force can knock it loose to re-stick; at 0 HP it
    // detonates. (The damage pipeline calls this after subtracting the mine's health.) minelayer.qc
    private void OnMineDamage(Entity self, Entity? attacker)
    {
        if (self.Health > 0f)
        {
            // Knocked loose: bounce briefly then re-attach on the next surface touch.
            if (Cvars.DamageForceScale > 0f && self.MoveType == MoveType.None)
            {
                self.MoveType = MoveType.Bounce;
                self.MaxHealth = 0f;                 // disarm proximity until it re-grounds
                self.Touch = (s, o) => OnTouch(s, o);
            }
            return;
        }
        Explode(self, null);
    }

    // W_MineLayer_Think — lifetime countdown, owner-death auto-detonate, and proximity-trigger logic. minelayer.qc
    private void OnThink(Entity self, float deathTime)
    {
        self.NextThink = Api.Clock.Time;

        // Lifetime reached its countdown point: arm the final countdown (a warning beep + super-aggressive).
        if (Api.Clock.Time > deathTime && self.MaxHealth == 0f && self.Frame == 0f)
        {
            if (Cvars.LifetimeCountdown > 0f)
                Api.Sound.Play(self, SoundChannel.Body, "weapons/mine_trigger.wav");
            self.MaxHealth = Api.Clock.Time + Cvars.LifetimeCountdown;
            self.Frame = 1f; // mine_explodeanyway: ignore team-safety once the countdown is running
        }

        // A player's mines detonate if the owner disconnects or dies.
        Entity? owner = self.Owner;
        if (owner is null || owner.IsFreed || owner.DeadState != DeadFlag.No
            || (owner.Flags & EntFlags.Client) == 0)
        {
            Explode(self, null);
            return;
        }

        // Proximity: if an enemy is within proximity_radius, schedule an explosion (sooner the closer they
        // are — scaled from time_edge at the rim to time_core at the center). MaxHealth holds that time.
        if (Api.Services is not null)
        {
            float proxRad = Cvars.ProximityRadius;
            foreach (Entity head in Api.Entities.FindInRadius(self.Origin, proxRad))
            {
                if (head.TakeDamage == DamageMode.No) continue;
                if ((head.Flags & EntFlags.Client) == 0) continue;   // players only
                if (head.DeadState != DeadFlag.No) continue;
                if (ReferenceEquals(head, self.Owner)) continue;     // not the owner
                if (self.Team != 0f && head.Team == self.Team) continue; // don't trigger for team mates

                if (self.MaxHealth == 0f)
                    Api.Sound.Play(self, SoundChannel.Body, "weapons/mine_trigger.wav");
                float dist = (head.Origin - self.Origin).Length();
                float newTime = Api.Clock.Time + Cvars.ProximityTimeCore;
                if (Cvars.ProximityTimeEdge != Cvars.ProximityTimeCore && proxRad > 0f)
                    newTime += (Cvars.ProximityTimeEdge - Cvars.ProximityTimeCore) * (dist / proxRad);
                if (self.MaxHealth == 0f || newTime < self.MaxHealth)
                    self.MaxHealth = newTime; // choose the earliest explosion time
            }
        }

        if (self.MaxHealth != 0f && Api.Clock.Time >= self.MaxHealth)
            ProximityExplode(self);

        // re-ground after a damage knock-off: a bouncing mine that lands re-sticks on the next touch.
    }

    // W_MineLayer_ProximityExplode — team-safety: if a friend is in the blast radius, delay until clear
    // (unless the lifetime countdown made the mine super-aggressive). minelayer.qc
    private void ProximityExplode(Entity self)
    {
        if (self.Frame == 0f && self.Team != 0f && Api.Services is not null) // mine_explodeanyway == 0
        {
            foreach (Entity head in Api.Entities.FindInRadius(self.Origin, Cvars.Radius))
                if (head.Team == self.Team && (head.Flags & EntFlags.Client) != 0 && !ReferenceEquals(head, self.Owner))
                    return; // a friend is too close — hold
        }
        self.MaxHealth = 0f;
        Explode(self, null);
    }

    // W_MineLayer_Touch / W_MineLayer_Stick — stick to the BSP surface it lands on, facing against the
    // surface normal, and lock in place. minelayer.qc
    private void OnTouch(Entity self, Entity other)
    {
        if (self.MoveType == MoveType.None) return; // already stuck

        // Stick: freeze the mine in place on the surface (QC W_MineLayer_Stick orients to -trace_plane_normal
        // and uses MOVETYPE_NONE; the surface-normal facing is a render detail we approximate by zeroing the
        // velocity here — the headless toss landed it on the ground already).
        if (other.Solid == Solid.Bsp || (other.Flags & EntFlags.Client) == 0)
        {
            Api.Sound.Play(self, SoundChannel.Body, "weapons/mine_stick.wav");
            self.Velocity = Vector3.Zero;
            self.MoveType = MoveType.None; // lock in place (disables gravity)
            self.Gravity = 0f;
        }
    }

    // W_MineLayer_Explode — radius damage + knockback, then remove. minelayer.qc
    private void Explode(Entity self, Entity? directHit)
    {
        self.Touch = null;
        self.Think = null;
        self.TakeDamage = DamageMode.No;

        WeaponSplash.RadiusDamage(self, self.Origin, Cvars.Damage, Cvars.EdgeDamage, Cvars.Radius,
            self.Owner, RegistryId, Cvars.Force, directHit: directHit);

        Api.Entities.Remove(self);
        // (The airshot achievement + out-of-ammo weapon switch are gametype/HUD concerns layered elsewhere.)
    }

    // W_MineLayer_RemoteExplode (secondary) — remote-detonate this actor's mines, gated by the spawnshield
    // timer (detonatedelay) and a team-safety radius check (don't blow up near a friend). minelayer.qc
    private void RemoteDetonate(Entity actor)
    {
        bool any = false;
        // Snapshot first: the blast removes mines and RadiusDamage re-enumerates the world.
        var mines = Api.Entities.FindByClass("mine")
            .Where(e => ReferenceEquals(e.Owner, actor) && !e.IsFreed).ToList();
        foreach (Entity e in mines)
        {
            // spawnshield gate (QC W_MineLayer_RemoteExplode, minelayer.qc:142-145): detonatedelay >= 0 requires
            // the timer to elapse; < 0 is a proximity safety. Reads the mine's Entity.ProjectileDetonateTime so
            // the Rocket Flying mutator's clear (= time) opens remote detonation immediately.
            if (e.ProjectileDetonateTime >= 0f)
            {
                if (Api.Clock.Time < e.ProjectileDetonateTime) continue; // timer not yet elapsed
            }
            else
            {
                // proximity safety: don't remote-detonate while still inside our own blast radius.
                float d = (e.Origin - (actor.Origin + (actor.Mins + actor.Maxs) * 0.5f)).Length();
                if (d <= Cvars.RemoteRadius) continue;
            }

            // team-safety: skip if a friend is within the remote blast radius.
            if (actor.Team != 0f && Api.Services is not null)
            {
                bool friendNear = false;
                foreach (Entity head in Api.Entities.FindInRadius(e.Origin, Cvars.RemoteRadius))
                    if (head.Team == actor.Team && (head.Flags & EntFlags.Client) != 0) { friendNear = true; break; }
                if (friendNear) continue;
            }

            any = true;
            e.Touch = null;
            e.Think = null;
            e.TakeDamage = DamageMode.No;
            e.ProjectileDamage = null;
            WeaponSplash.RadiusDamage(e, e.Origin, Cvars.RemoteDamage, Cvars.RemoteEdgeDamage,
                Cvars.RemoteRadius, actor, RegistryId, Cvars.RemoteForce);
            Api.Entities.Remove(e);
        }
        if (any)
            Api.Sound.Play(actor, SoundChannel.Body, "weapons/mine_det.wav");
    }

    /// <summary>Count this actor's live mines (QC W_MineLayer_Count over the g_mines list).</summary>
    private static int CountMines(Entity actor)
    {
        if (Api.Services is null) return 0;
        int count = 0;
        foreach (Entity e in Api.Entities.FindByClass("mine"))
            if (ReferenceEquals(e.Owner, actor) && !e.IsFreed)
                ++count;
        return count;
    }

    // METHOD(MineLayer, wr_checkammo1) — minelayer.qc
    public bool CheckAmmoPrimary(Entity actor) => actor.GetResource(AmmoType) >= Cvars.Ammo;

    // METHOD(MineLayer, wr_checkammo2) — minelayer.qc (secondary available when mines are placed).
    public bool CheckAmmoSecondary(Entity actor) => CountMines(actor) > 0;
}
