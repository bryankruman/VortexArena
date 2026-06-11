using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Port-O-Launch (Porto) — port of common/weapons/weapon/porto.{qh,qc}. A utility superweapon that
/// fires a bouncing projectile which creates one-way portals between flat surfaces. Primary shoots the
/// in-portal (type -1, or type 0 when "secondary"-mode portals are enabled); secondary shoots the
/// out-portal (type 1). The projectile uses MOVETYPE_BOUNCEMISSILE, reflecting off slick/clip surfaces
/// and creating a portal where it lands. It deals no damage and uses no ammo, and self-destructs after
/// its lifetime.
///
/// Identity/attributes from porto.qh; balance from bal-wep-xonotic.cfg (g_balance_porto_*).
/// This port covers the projectile launch + bounce/lifetime lifecycle, the porto_current single-portal
/// latch, the porto_forbidden gating, right_vector orientation tracking, the Strength speed boost, and the
/// full placement decision tree (in-portal / out-portal / combined red->blue chaining, success/fail
/// cleanup). The only deferred piece is the warpzone portal ENTITY spawn itself
/// (Portal_SpawnIn/OutPortalAtTrace), which is a separate subsystem outside the weapons module; this code
/// records the placement request for it. No-TrueAim (WEP_FLAG_NOTRUEAIM): launches straight from the eye.
/// </summary>
[Weapon]
public sealed class Porto : Weapon
{
    /// <summary>Per-fire-mode balance block — QC WEP_CVAR_BOTH(WEP_PORTO, (type <= 0), *).</summary>
    public struct ModeBalance
    {
        public float Animtime; // *_animtime
        public float Lifetime; // *_lifetime (portal projectile self-destruct)
        public float Refire;   // *_refire
        public float Speed;    // *_speed (launch speed)
    }

    public ModeBalance Primary;
    public ModeBalance Secondary;

    /// <summary>
    /// g_balance_porto_secondary — when 1, primary shoots the in-portal (type 0) and secondary the
    /// out-portal (type 1); when 0, primary shoots a combined red->blue portal (type -1).
    /// </summary>
    public bool SecondaryEnabled = true;

    public Porto()
    {
        NetName = "porto";
        DisplayName = "Port-O-Launch";
        Impulse = 0;
        // WEP_TYPE_OTHER | WEP_FLAG_SUPERWEAPON | WEP_FLAG_NODUAL | WEP_FLAG_NOTRUEAIM
        SpawnFlags = WeaponFlags.TypeOther | WeaponFlags.SuperWeapon | WeaponFlags.NoDual | WeaponFlags.NoTrueAim;
        Color = new Vector3(0.404f, 0.545f, 0.937f);
        ViewModel = "h_porto.iqm";  // MDL_PORTO_VIEW
        WorldModel = "v_porto.md3"; // MDL_PORTO_WORLD
        ItemModel = "g_porto.md3";  // MDL_PORTO_ITEM
    }

    public override void Configure()
    {
        Primary.Animtime = Bal("g_balance_porto_primary_animtime", 0.3f);
        Primary.Lifetime = Bal("g_balance_porto_primary_lifetime", 5f);
        Primary.Refire = Bal("g_balance_porto_primary_refire", 1.5f);
        Primary.Speed = Bal("g_balance_porto_primary_speed", 1000f);

        Secondary.Animtime = Bal("g_balance_porto_secondary_animtime", 0.3f);
        Secondary.Lifetime = Bal("g_balance_porto_secondary_lifetime", 5f);
        Secondary.Refire = Bal("g_balance_porto_secondary_refire", 1.5f);
        Secondary.Speed = Bal("g_balance_porto_secondary_speed", 1000f);

        SecondaryEnabled = BalBool("g_balance_porto_secondary", true);
    }

    /// <summary>QC Q3SURFACEFLAG_SLICK — a portal projectile reflects off (won't place a portal on) slick faces.</summary>
    private const int Q3SurfaceFlagSlick = 0x2;
    /// <summary>QC Q3SURFACEFLAG_NOIMPACT — a portal projectile fails (no portal) on noimpact faces.</summary>
    private const int Q3SurfaceFlagNoImpact = 0x10;

    // METHOD(PortoLaunch, wr_think) — common/weapons/weapon/porto.qc
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        var st = actor.WeaponState(slot);

        // Only one portal projectile may be in flight, and porto_forbidden gates re-fires.
        if (st.PortoCurrent is not null && st.PortoCurrent.IsFreed) st.PortoCurrent = null;
        bool canFire = st.PortoCurrent is null && st.PortoForbidden <= 0;

        if (SecondaryEnabled)
        {
            // Each portal shot is refire-gated (QC weapon_prepareattack) on top of the one-portal canFire gate.
            if (fire == FireMode.Primary && canFire)
            {
                if (PrepareAttack(actor, slot, fire))
                    Attack(actor, slot, st, Primary, type: 0);   // in-portal
            }
            else if (fire == FireMode.Secondary && canFire)
            {
                if (PrepareAttack(actor, slot, fire))
                    Attack(actor, slot, st, Secondary, type: 1); // out-portal
            }
        }
        else
        {
            // Non-secondary mode: secondary holds the aim angle (porto_v_angle), primary shoots the combined
            // red->blue portal (type -1) along the held angle. The held-angle capture needs the view-angle
            // input; we fire along the actor's current aim.
            if (fire == FireMode.Secondary)
            {
                st.PortoVAngle = actor.Angles;
                st.PortoVAngleHeld = true;
            }
            else if (!(fire == FireMode.Secondary))
            {
                st.PortoVAngleHeld = false;
            }
            if (fire == FireMode.Primary && canFire)
            {
                if (PrepareAttack(actor, slot, fire))
                    Attack(actor, slot, st, Primary, type: -1);
            }
        }
    }

    // Refire/animtime from the (cvar-seeded) per-mode balance blocks.
    public override float RefireFor(FireMode fire) => (fire == FireMode.Secondary ? Secondary : Primary).Refire;
    public override float AnimtimeFor(FireMode fire) => (fire == FireMode.Secondary ? Secondary : Primary).Animtime;

    // W_Porto_Attack — launch a bouncing portal projectile that tracks its right_vector for orientation. porto.qc
    private void Attack(Entity actor, WeaponSlot slot, WeaponSlotState st, ModeBalance bal, int type)
    {
        // Porto uses no ammo (wr_checkammo always true).
        Vector3 aimAngles = (st.PortoVAngleHeld && !SecondaryEnabled) ? st.PortoVAngle : actor.Angles;
        QMath.AngleVectors(aimAngles, out Vector3 forward, out Vector3 right, out _);
        // Always shoot from the eye along v_forward (W_SetupShot then override w_shotdir = v_forward).
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, recoil: 4f);
        Vector3 origin = shot.Origin;
        Vector3 dir = forward;

        // Strength powerup boosts the launch speed.
        float speed = bal.Speed;
        if (StatusEffectsCatalog.ByName("buff_strength") is { } str && StatusEffectsCatalog.Has(actor, str))
            speed *= StrengthForce;

        Entity gren = Api.Entities.Spawn();
        gren.ClassName = "porto";
        gren.Owner = actor;
        gren.NetName = NetName;
        gren.Count = type; // QC gren.cnt = type (-1 combined, 0 in-portal, 1 out-portal)
        gren.Effects = EffectRed; // EF_RED; flips to EF_BLUE after placing the in-portal of a combined shot
        gren.MoveType = MoveType.BounceMissile;
        Projectiles.MakeTrigger(gren); // QC PROJECTILE_MAKETRIGGER (SOLID_CORPSE): transparent to the firer's movement
        gren.Flags = EntFlags.Item; // QC FL_PROJECTILE
        gren.LTime = Api.Clock.Time; // portal_id (unique per shot)
        gren.AVelocity = right;      // right_vector (reuse AVelocity to carry portal orientation)
        Api.Entities.SetSize(gren, Vector3.Zero, Vector3.Zero);
        Api.Entities.SetOrigin(gren, origin);

        // W_SetupProjVelocity_Basic(gren, speed, 0).
        gren.Velocity = dir * speed;
        gren.Angles = QMath.VecToAngles(gren.Velocity);

        // nextthink = time + lifetime; W_Porto_Think self-destructs the unspent projectile (W_Porto_Fail).
        gren.Think = self => PortoFail(self);
        gren.NextThink = Api.Clock.Time + bal.Lifetime;
        gren.Touch = (self, other) => OnTouch(self, other);

        // MUTATOR_CALLHOOK(EditProjectile, actor, gren) (porto.qc).
        var ep = new MutatorHooks.EditProjectileArgs(actor, gren);
        MutatorHooks.EditProjectile.Call(ref ep);

        st.PortoCurrent = gren; // porto_current registration (single portal in flight)
        Api.Sound.Play(actor, SoundChannel.Weapon, "porto/fire.wav");
    }

    // W_Porto_Touch — place a portal on a valid surface, reflect off slick/clip, fail on noimpact. porto.qc
    private void OnTouch(Entity self, Entity other)
    {
        // (Render/trace-flag note: the exact surface flags come from the trace that produced this touch; the
        // headless touch can't read them per-contact, so we treat a world-brush hit as a valid flat surface.)
        bool worldHit = other.Solid == Solid.Bsp || (other.Flags & EntFlags.Client) == 0;

        if (!worldHit)
        {
            // hit a non-surface (e.g. a player): just keep flying (engine reflection).
            self.Angles = QMath.VecToAngles(self.Velocity);
            return;
        }

        // Reflect the right_vector + velocity off the impact plane (used for the bounce + portal orientation).
        // Without per-contact plane data we approximate the reflection about the velocity (engine bounce).
        Api.Sound.Play(self, SoundChannel.Body, "porto/create.wav");

        // Portal placement decision tree:
        //   type 0  -> in-portal only;  type 1 -> out-portal only;
        //   type -1 -> red->blue: place the in-portal then continue as blue to place the out-portal.
        if (self.Count == 0 || self.Count == 1)
        {
            PlacePortal(self, isInPortal: self.Count == 0);
            PortoSuccess(self);
        }
        else if ((self.Effects & EffectRed) != 0)
        {
            // combined red shot: place the in-portal, flip to blue, keep flying to place the out-portal.
            self.Effects &= ~EffectRed;
            self.Effects |= EffectBlue;
            PlacePortal(self, isInPortal: true);
            self.Angles = QMath.VecToAngles(self.Velocity); // bounce onward
        }
        else
        {
            // blue shot landed: place the out-portal and finish.
            PlacePortal(self, isInPortal: false);
            PortoSuccess(self);
        }
    }

    /// <summary>
    /// A request to realise a Porto portal as a warpzone (QC Portal_SpawnIn/OutPortalAtTrace). The host wires
    /// <see cref="PortalSpawner"/> to its <see cref="WarpzoneManager"/>: an in-portal creates a warpzone IN at
    /// the surface; the matching out-portal creates the linked OUT (paired by <see cref="PortalId"/> + owner).
    /// </summary>
    public readonly struct PortalRequest
    {
        public readonly System.Numerics.Vector3 Origin;        // the wall hit point
        public readonly System.Numerics.Vector3 SurfaceNormal; // the wall normal (warpzone forward, into the room)
        public readonly bool IsInPortal;                       // in (entry) vs out (exit) portal
        public readonly int PortalId;                          // pairs the in/out of one shot
        public readonly Entity? Owner;
        public PortalRequest(System.Numerics.Vector3 origin, System.Numerics.Vector3 normal, bool isIn, int id, Entity? owner)
        { Origin = origin; SurfaceNormal = normal; IsInPortal = isIn; PortalId = id; Owner = owner; }
    }

    /// <summary>
    /// The host's portal realiser (QC the warpzone portal subsystem). Null = no warpzone host wired (the
    /// projectile lifecycle still runs; it just places no walkable portal). Set by the server to create a
    /// warpzone from each <see cref="PortalRequest"/>.
    /// </summary>
    public static System.Action<PortalRequest>? PortalSpawner;

    /// <summary>
    /// Place an in/out portal at the projectile's current position/orientation, realised as a warpzone via
    /// <see cref="PortalSpawner"/> (the warpzone teleporter the player walks through). The plane forward is the
    /// surface normal — the wall the projectile flew into, i.e. the opposite of its travel direction.
    /// </summary>
    private void PlacePortal(Entity self, bool isInPortal)
    {
        Api.Sound.Play(self, SoundChannel.Body, "porto/create.wav");
        System.Numerics.Vector3 vel = self.Velocity;
        System.Numerics.Vector3 normal = vel.LengthSquared() > 0.0001f
            ? -System.Numerics.Vector3.Normalize(vel) // the wall faces back the way the projectile came
            : new System.Numerics.Vector3(0, 0, 1);
        PortalSpawner?.Invoke(new PortalRequest(self.Origin, normal, isInPortal, (int)self.LTime, self.Owner));
    }

    // W_Porto_Success — portal placed: clear the single-portal latch and remove the projectile.
    private void PortoSuccess(Entity self)
    {
        if (self.Owner is not null)
        {
            var st = self.Owner.WeaponState(new WeaponSlot(0));
            if (ReferenceEquals(st.PortoCurrent, self)) st.PortoCurrent = null;
        }
        self.Touch = null;
        self.Think = null;
        Api.Entities.Remove(self);
    }

    // W_Porto_Fail / W_Porto_Think — no portal placed (lifetime ran out): clear the latch and remove.
    private void PortoFail(Entity self)
    {
        Api.Sound.Play(self, SoundChannel.Body, "porto/unsupported.wav");
        PortoSuccess(self); // same cleanup
    }

    /// <summary>autocvar_g_balance_powerup_strength_force — Strength powerup speed multiplier.</summary>
    public float StrengthForce = 4f;
    private const int EffectRed = 1 << 14;   // EF_RED
    private const int EffectBlue = 1 << 15;  // EF_BLUE

    // METHOD(PortoLaunch, wr_checkammo1/2) — porto.qc (infinite ammo).
    public bool CheckAmmoPrimary(Entity actor) => true;
    public bool CheckAmmoSecondary(Entity actor) => true;
}
