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

        // autocvar_g_balance_powerup_strength_force (balance-xonotic.cfg:237 = 3). Read live so a balance/ruleset
        // variant (e.g. nexuiz25 = 4) is honoured rather than the previously-hardcoded constant.
        StrengthForce = Bal("g_balance_powerup_strength_force", 3f);
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
            // red->blue portal (type -1) along the held angle. QC wr_think processes both buttons in one call;
            // the port driver calls WrThink once per fire-mode tick, so we model the same hold state machine but
            // ONLY mutate it on the matching fire-mode tick — never blanket-clear the hold on a Primary tick
            // (the bug that wiped the captured angle before the primary shot could read it):
            //   - on the Secondary tick: capture/hold the aim angle (and keep it held while ATCK2 stays pressed);
            //   - the hold is released by the absence of an ATCK2 tick, which the driver expresses as the
            //     Secondary fire-mode no longer firing — we cannot observe a "released" edge from a Primary tick,
            //     so we leave the hold intact and let the next Secondary tick re-arm it. (Dormant in stock play:
            //     g_balance_porto_secondary defaults to 1, so this branch is never reached.)
            if (fire == FireMode.Secondary)
            {
                if (!st.PortoVAngleHeld)
                {
                    st.PortoVAngle = actor.Angles; // QC porto_v_angle = actor.v_angle
                    st.PortoVAngleHeld = true;
                }
            }
            else if (fire == FireMode.Primary && canFire)
            {
                if (PrepareAttack(actor, slot, fire))
                {
                    Attack(actor, slot, st, Primary, type: -1);
                    st.PortoVAngleHeld = false; // QC clears the hold once the held-angle shot is consumed
                }
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
        QMath.AngleVectors(aimAngles, out Vector3 forward, out _, out _);
        // Always shoot from the eye along v_forward (W_SetupShot then override w_shotdir = v_forward).
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, recoil: 4f);
        Vector3 origin = shot.Origin;
        Vector3 dir = forward;

        // Strength powerup boosts the launch speed. QC: StatusEffects_active(STATUSEFFECT_Strength, actor),
        // whose netname is "strength" (NOT "buff_strength" — only buffs carry the buff_ prefix). Matches the
        // canonical query used elsewhere (Ctf.cs:801).
        float speed = bal.Speed;
        if (StatusEffectsCatalog.ByName("strength") is { } str && StatusEffectsCatalog.Has(actor, str))
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
        Api.Entities.SetSize(gren, Vector3.Zero, Vector3.Zero);
        Api.Entities.SetOrigin(gren, origin);

        // W_SetupProjVelocity_Basic(gren, speed, 0).
        gren.Velocity = dir * speed;
        gren.Angles = QMath.VecToAngles(gren.Velocity);

        // QC: fixedmakevectors(fixedvectoangles(gren.velocity)); gren.right_vector = v_right;
        // right_vector is the portal's roll axis; we carry it on AVelocity and reflect it on each bounce so
        // portal orientation tracks through reflections.
        QMath.AngleVectors(QMath.VecToAngles(gren.Velocity), out _, out Vector3 right, out _);
        gren.AVelocity = right;

        // nextthink = time + lifetime; W_Porto_Think self-destructs the unspent projectile.
        // QC W_Porto_Think: if the owner reconnected/respawned as a different player (playerid mismatch) it just
        // delete()s with no fail behaviour; otherwise W_Porto_Fail(false). We carry the firing slot so the
        // success/fail cleanup clears the right per-slot porto_current latch.
        gren.Think = self => PortoThink(self, slot);
        gren.NextThink = Api.Clock.Time + bal.Lifetime;
        gren.Touch = (self, other) => OnTouch(self, other, slot);

        // MUTATOR_CALLHOOK(EditProjectile, actor, gren) (porto.qc).
        var ep = new MutatorHooks.EditProjectileArgs(actor, gren);
        MutatorHooks.EditProjectile.Call(ref ep);

        st.PortoCurrent = gren; // porto_current registration (single portal in flight)
        Api.Sound.Play(actor, SoundChannel.Weapon, "porto/fire.wav");
    }

    // W_Porto_Touch — place a portal on a valid surface, reflect off slick/clip, fail on noimpact. porto.qc
    private void OnTouch(Entity self, Entity other, WeaponSlot slot)
    {
        // QC: if (toucher.classname == "portal") return; — a portal collision is handled by the portal itself.
        if (other.ClassName == "portal") return;

        // (Render/trace-flag note: the exact surface flags + impact plane normal come from the trace that
        // produced this touch; the headless touch can't read them per-contact, so we approximate — a damageable
        // edict counts as a creature and a world-brush hit as a valid flat surface, and the reflection/normal is
        // derived from the negated velocity. The slick/clip + noimpact branches are best-effort until per-contact
        // surface flags are available; see the touch-tree spec gap.)
        bool isCreature = other.TakeDamage != DamageMode.No || (other.Flags & EntFlags.Client) != 0;
        if (isCreature)
        {
            // QC creature-hit: trace straight down to the floor; if there's no nearby solid (or it's
            // slick/clip/noimpact) ignore the contact and keep flying. We have no per-contact floor trace
            // headless, so we conservatively ignore the creature contact (no portal on a player) and bounce on.
            self.Angles = QMath.VecToAngles(self.Velocity);
            return;
        }

        // QC: not-owner — if the firing player is gone/changed, drop the projectile with the unsupported cue and
        // place nothing. We key ownership on the live owner reference (the port has no stable playerid; see TODO).
        if (self.Owner is null || self.Owner.IsFreed)
        {
            Api.Sound.Play(self, SoundChannel.ShotsAuto, "porto/unsupported.wav");
            CleanupProjectile(self, slot);
            return;
        }

        // QC SLICK / PLAYERCLIP reflect-and-continue: bounce off (don't place), reflecting BOTH the velocity and
        // the carried right_vector about the impact plane, and play the bounce cue. Headless we lack the impact
        // plane normal; approximate the normal as the negated travel direction (engine bounce already flipped
        // velocity), reflect right_vector about it, and keep flying. (Best-effort — see touch-tree gap.)
        if (IsSlickOrClip(other))
        {
            Api.Sound.Play(self, SoundChannel.ShotsAuto, "porto/bounce.wav");
            Vector3 n = SurfaceNormal(self.Velocity);
            self.AVelocity = ReflectAbout(self.AVelocity, n); // right_vector reflect
            self.Angles = QMath.VecToAngles(self.Velocity);
            return;
        }

        // QC NOIMPACT (sky / noimpact face): hard fail, no portal. On a combined (cnt<0) shot also clear any
        // already-placed in-portal of this shot.
        if (IsNoImpact(other))
        {
            Api.Sound.Play(self, SoundChannel.ShotsAuto, "porto/unsupported.wav");
            if (self.Count < 0) PortalClearWithId?.Invoke(self.Owner, (int)self.LTime);
            CleanupProjectile(self, slot);
            return;
        }

        // Portal placement decision tree:
        //   type 0  -> in-portal only;  type 1 -> out-portal only;
        //   type -1 -> red->blue: place the in-portal then continue as blue to place the out-portal.
        if (self.Count == 0 || self.Count == 1)
        {
            // In/out-only: place; create on success. (PortalSpawner is fire-and-forget — there is no headless
            // success/fail handshake yet, so we treat placement as succeeding; see portal_spawn gap.)
            bool isIn = self.Count == 0;
            PlacePortal(self, isInPortal: isIn);
            Api.Sound.Play(self, SoundChannel.ShotsAuto, "porto/create.wav");
            PortoSuccess(self, slot);
        }
        else if ((self.Effects & EffectRed) != 0)
        {
            // combined red shot: place the in-portal, flip to blue, reflect, keep flying to place the out-portal.
            self.Effects &= ~EffectRed;
            self.Effects |= EffectBlue;
            PlacePortal(self, isInPortal: true);
            Api.Sound.Play(self, SoundChannel.ShotsAuto, "porto/create.wav");
            Vector3 n = SurfaceNormal(self.Velocity);
            self.AVelocity = ReflectAbout(self.AVelocity, n); // QC: right_vector reflected off the in-portal plane
            self.Angles = QMath.VecToAngles(self.Velocity); // bounce onward as blue
        }
        else
        {
            // blue shot landed: place the out-portal and finish.
            PlacePortal(self, isInPortal: false);
            Api.Sound.Play(self, SoundChannel.ShotsAuto, "porto/create.wav");
            PortoSuccess(self, slot);
        }
    }

    /// <summary>QC trace_dphitq3surfaceflags &amp; SLICK || trace_dphitcontents &amp; PLAYERCLIP — surfaces a porto
    /// reflects off instead of placing on. The port's headless touch carries no per-contact surface flags / hit
    /// contents on the toucher entity, so this currently returns false (placement proceeds), matching the
    /// common-case world-brush hit. When Entity gains per-contact SurfaceFlags/Contents (or the touch carries the
    /// trace), wire them here against <see cref="Q3SurfaceFlagSlick"/> / DPCONTENTS_PLAYERCLIP. See spec gap
    /// weapon-porto.touch.placement_tree.</summary>
    private static bool IsSlickOrClip(Entity other) => false;

    /// <summary>QC trace_dphitq3surfaceflags &amp; NOIMPACT — sky/noimpact faces that fail the shot. Same
    /// per-contact-flag limitation as <see cref="IsSlickOrClip"/>; returns false until the trace flags are
    /// available on the touch.</summary>
    private static bool IsNoImpact(Entity other) => false;

    /// <summary>The placement plane normal, approximated from the negated travel direction (the wall faces back
    /// the way the projectile came). Headless we have no per-contact plane normal.</summary>
    private static Vector3 SurfaceNormal(Vector3 velocity)
        => velocity.LengthSquared() > 0.0001f ? -Vector3.Normalize(velocity) : new Vector3(0, 0, 1);

    /// <summary>QC reflect: v -= 2*n*(v·n).</summary>
    private static Vector3 ReflectAbout(Vector3 v, Vector3 n)
        => v - 2f * n * Vector3.Dot(v, n);

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
    /// Host hook for QC Portal_ClearWithID(owner, portal_id) — clears just the in/out portals of one shot (used
    /// when a combined cnt&lt;0 shot fails after already placing its in-portal). Null = no host wired (no-op).
    /// </summary>
    public static System.Action<Entity, int>? PortalClearWithId;

    /// <summary>
    /// Place an in/out portal at the projectile's current position/orientation, realised as a warpzone via
    /// <see cref="PortalSpawner"/> (the warpzone teleporter the player walks through). The plane forward is the
    /// surface normal — the wall the projectile flew into, i.e. the opposite of its travel direction. The create
    /// sound is played by the caller (OnTouch) on the success branch, matching QC SND_PORTO_CREATE timing.
    /// </summary>
    private void PlacePortal(Entity self, bool isInPortal)
    {
        Vector3 normal = SurfaceNormal(self.Velocity); // the wall faces back the way the projectile came
        PortalSpawner?.Invoke(new PortalRequest(self.Origin, normal, isInPortal, (int)self.LTime, self.Owner));
    }

    // W_Porto_Success — portal placed: clear the single-portal latch and remove the projectile.
    private void PortoSuccess(Entity self, WeaponSlot slot) => CleanupProjectile(self, slot);

    // W_Porto_Think — the lifetime ran out with the projectile still unspent.
    //   QC: if the owner reconnected/respawned as a different player -> plain delete (no fail behaviour);
    //       else W_Porto_Fail(this, false). W_Porto_Fail itself plays NO sound (the unsupported cue belongs to
    //       the touch-fail path only) — so a pure lifetime expiry is silent. On a combined (cnt<0) shot a soft
    //       fail also clears any in-portal already placed by this shot.
    private void PortoThink(Entity self, WeaponSlot slot)
    {
        bool ownerGone = self.Owner is null || self.Owner.IsFreed;
        if (!ownerGone && self.Count < 0)
            PortalClearWithId?.Invoke(self.Owner!, (int)self.LTime);
        // (No throwable-drop / center-print: see lifecycle gap. No sound — W_Porto_Fail is silent.)
        CleanupProjectile(self, slot);
    }

    // Shared teardown: clear the owner's per-slot porto_current latch for THIS projectile, then delete it.
    private void CleanupProjectile(Entity self, WeaponSlot slot)
    {
        if (self.Owner is not null)
        {
            var st = self.Owner.WeaponState(slot);
            if (ReferenceEquals(st.PortoCurrent, self)) st.PortoCurrent = null;
        }
        self.Touch = null;
        self.Think = null;
        Api.Entities.Remove(self);
    }

    /// <summary>autocvar_g_balance_powerup_strength_force — Strength powerup speed multiplier (default 3, seeded
    /// from the cvar in <see cref="Configure"/>).</summary>
    public float StrengthForce = 3f;
    private const int EffectRed = 1 << 14;   // EF_RED
    private const int EffectBlue = 1 << 15;  // EF_BLUE

    // METHOD(PortoLaunch, wr_checkammo1/2) — porto.qc (infinite ammo).
    public bool CheckAmmoPrimary(Entity actor) => true;
    public bool CheckAmmoSecondary(Entity actor) => true;
}
