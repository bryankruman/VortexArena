using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Grappling Hook — port of common/weapons/weapon/hook.{qh,qc}. Primary fires a chain that latches onto
/// geometry and reels the player in (a movement tool, consuming fuel); secondary lobs a gravity bomb
/// (MOVETYPE_TOSS, falling under its own gravity) that, on contact or at end of lifetime, pulls victims
/// inward with a strong negative-force radius blast spread out over a short duration. The bomb is shootable.
///
/// Identity/attributes from hook.qh; balance from bal-wep-xonotic.cfg (g_balance_hook_*).
/// This port covers the secondary gravity bomb (toss launch, contact/lifetime detonation, duration-spread
/// power-curve pull blast, shoot-down) AND the primary grapple: FireGrapplingHook launches the chain, it
/// latches on contact (GrapplingHook_Stop), and reels the owner in (the simple non-tarzan pull) with
/// hook_state (FIRING/REMOVING/PULLING/WAITING_FOR_RELEASE) and hooked-fuel drain. Only the CSQC rope
/// rendering and the elastic "tarzan" rubber-band pull variant are left out.
/// </summary>
[Weapon]
public sealed class Hook : Weapon
{
    /// <summary>Primary-fire (grapple) balance — QC WEP_CVAR_PRI(WEP_HOOK, *).</summary>
    public struct PrimaryBalance
    {
        public float Ammo;            // g_balance_hook_primary_ammo (fuel to fire the hook)
        public float Animtime;        // g_balance_hook_primary_animtime
        public float HookedAmmo;      // g_balance_hook_primary_hooked_ammo (fuel/sec while reeling)
        public float HookedTimeFree;  // g_balance_hook_primary_hooked_time_free
        public float HookedTimeMax;   // g_balance_hook_primary_hooked_time_max
        public float Refire;          // g_balance_hook_primary_refire
    }

    /// <summary>Secondary-fire (gravity bomb) balance — QC WEP_CVAR_SEC(WEP_HOOK, *).</summary>
    public struct SecondaryBalance
    {
        public float Animtime;        // g_balance_hook_secondary_animtime
        public float Damage;          // g_balance_hook_secondary_damage
        public float DamageForceScale;// g_balance_hook_secondary_damageforcescale
        public float Duration;        // g_balance_hook_secondary_duration (blast spread over this time)
        public float EdgeDamage;      // g_balance_hook_secondary_edgedamage
        public float Force;           // g_balance_hook_secondary_force (negative = pull inward)
        public float Gravity;         // g_balance_hook_secondary_gravity
        public float Health;          // g_balance_hook_secondary_health (shootable bomb hp)
        public float Lifetime;        // g_balance_hook_secondary_lifetime
        public float Power;           // g_balance_hook_secondary_power (falloff exponent over duration)
        public float Radius;          // g_balance_hook_secondary_radius
        public float Refire;          // g_balance_hook_secondary_refire
        public float Speed;           // g_balance_hook_secondary_speed (initial upward speed)
    }

    public PrimaryBalance Primary;
    public SecondaryBalance Secondary;


    public Hook()
    {
        NetName = "hook";
        AmmoType = ResourceType.Fuel;   // QC ammo_type
        DisplayName = "Grappling Hook";
        Impulse = 0;
        // WEP_FLAG_CANCLIMB | WEP_TYPE_SPLASH | WEP_FLAG_NOTRUEAIM
        SpawnFlags = WeaponFlags.CanClimb | WeaponFlags.TypeSplash | WeaponFlags.NoTrueAim;
        Color = new Vector3(0.471f, 0.817f, 0.392f);
        ViewModel = "h_hookgun.iqm";  // MDL_HOOK_VIEW
        WorldModel = "v_hookgun.md3"; // MDL_HOOK_WORLD
        ItemModel = "g_hookgun.md3";  // MDL_HOOK_ITEM
    }

    public override void Configure()
    {
        Primary.Ammo = Bal("g_balance_hook_primary_ammo", 5f);
        Primary.Animtime = Bal("g_balance_hook_primary_animtime", 0.3f);
        Primary.HookedAmmo = Bal("g_balance_hook_primary_hooked_ammo", 5f);
        Primary.HookedTimeFree = Bal("g_balance_hook_primary_hooked_time_free", 2f);
        Primary.HookedTimeMax = Bal("g_balance_hook_primary_hooked_time_max", 0f);
        Primary.Refire = Bal("g_balance_hook_primary_refire", 0.2f);

        Secondary.Animtime = Bal("g_balance_hook_secondary_animtime", 0.3f);
        Secondary.Damage = Bal("g_balance_hook_secondary_damage", 25f);
        Secondary.DamageForceScale = Bal("g_balance_hook_secondary_damageforcescale", 0f);
        Secondary.Duration = Bal("g_balance_hook_secondary_duration", 1.5f);
        Secondary.EdgeDamage = Bal("g_balance_hook_secondary_edgedamage", 5f);
        Secondary.Force = Bal("g_balance_hook_secondary_force", -2000f);
        Secondary.Gravity = Bal("g_balance_hook_secondary_gravity", 5f);
        Secondary.Health = Bal("g_balance_hook_secondary_health", 15f);
        Secondary.Lifetime = Bal("g_balance_hook_secondary_lifetime", 5f);
        Secondary.Power = Bal("g_balance_hook_secondary_power", 3f);
        Secondary.Radius = Bal("g_balance_hook_secondary_radius", 500f);
        Secondary.Refire = Bal("g_balance_hook_secondary_refire", 3f);
        Secondary.Speed = Bal("g_balance_hook_secondary_speed", 0f);
    }

    /// <summary>g_balance_grapplehook_speed_fly — the launched chain's flight speed.</summary>
    public float GrappleSpeedFly = 1800f;
    /// <summary>g_balance_grapplehook_speed_pull — the reel-in pull speed.</summary>
    public float GrappleSpeedPull = 2000f;
    /// <summary>g_balance_grapplehook_length_min — minimal rope length (stops pulling below this).</summary>
    public float GrappleLengthMin = 50f;
    /// <summary>g_balance_grapplehook_health — the chain's HP (shootable).</summary>
    public float GrappleHealth = 50f;

    // The Hook's primary is continuous (reels every tick, rolls its own HookRefire), so only the secondary
    // gravity bomb uses the refire gate. Provide the gate's refire/animtime explicitly from the balance struct
    // (the base BalanceTiming cvar read would fall back to 1s if the balance cfg hasn't been loaded).
    public override float RefireFor(FireMode fire) => fire == FireMode.Secondary ? Secondary.Refire : Primary.Refire;
    public override float AnimtimeFor(FireMode fire) => fire == FireMode.Secondary ? Secondary.Animtime : 0f;

    // METHOD(Hook, wr_think) — common/weapons/weapon/hook.qc + server/hook.qc grapple lifecycle.
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        var st = actor.WeaponState(slot);

        // The hook is a CONTINUOUS weapon (it reels every tick while primary is held), so it reads the held
        // button (st.ButtonAttack) directly rather than using the refire gate. The driver calls WrThink(Primary)
        // every tick — the lifecycle/reel state machine below therefore runs in this Primary call. The
        // secondary gravity bomb is the only thing the (ATK2-held) Secondary call does.
        if (fire == FireMode.Secondary)
        {
            // QC hook.qc wr_think: the secondary gravity bomb is gated by weapon_prepareattack on the secondary
            // refire (g_balance_hook_secondary_refire = 3s). Because the bomb is ammo-free, WITHOUT this gate
            // holding ATK2 spawns one bomb per server tick (~72/s) — unbounded projectile/knockback spam. Route
            // it through the refire gate like every other discrete secondary.
            if (PrepareAttack(actor, slot, fire))
                AttackGravityBomb(actor, slot);
            return;
        }

        if (st.ButtonAttack)
        {
            // Fire the grappling hook (FireGrapplingHook) on the press edge, gated by hook_refire and the
            // WAITING_FOR_RELEASE latch (so holding fire reels in rather than re-firing).
            if (st.Hook is null && (st.HookState & HookState.WaitingForRelease) == 0
                && st.HookRefire < Api.Clock.Time)
            {
                actor.TakeResource(AmmoType, Primary.Ammo);
                st.HookState |= HookState.Firing | HookState.WaitingForRelease;
            }
        }
        else
        {
            // primary released: drop the hook and clear the latch.
            st.HookState |= HookState.Removing;
            st.HookState &= ~HookState.WaitingForRelease;
        }

        // While hooked, drain fuel over time (after the free grace period) and stop if it runs out.
        if (st.Hook is not null)
        {
            st.HookRefire = MathF.Max(st.HookRefire, Api.Clock.Time + Primary.Refire);
            if (st.Hook.Health > 0f) // .state == 1 means latched (reuse Health>0 as the latched flag)
            {
                float hookedTimeMax = Primary.HookedTimeMax;
                if (hookedTimeMax > 0f && Api.Clock.Time > st.HookTimeHooked + hookedTimeMax)
                    st.HookState |= HookState.Removing;

                float hookedFuel = Primary.HookedAmmo;
                if (hookedFuel > 0f && Api.Clock.Time > st.HookTimeFuelDecrease)
                {
                    float need = (Api.Clock.Time - st.HookTimeFuelDecrease) * hookedFuel;
                    if (actor.GetResource(AmmoType) >= need)
                    {
                        actor.TakeResource(AmmoType, need);
                        st.HookTimeFuelDecrease = Api.Clock.Time;
                    }
                    else
                    {
                        actor.SetResource(AmmoType, 0f);
                        st.HookState |= HookState.Removing;
                    }
                }
            }
            else
            {
                st.HookTimeHooked = Api.Clock.Time;
                st.HookTimeFuelDecrease = Api.Clock.Time + Primary.HookedTimeFree;
            }
        }

        // Crouch-slide releases the pull (HOOK_PULLING); without the crouch input flag we keep pulling.
        st.HookState |= HookState.Pulling;

        // Process the state machine: (re)fire or remove.
        if ((st.HookState & HookState.Firing) != 0)
        {
            if (st.Hook is not null) RemoveHook(st);
            FireGrapplingHook(actor, slot, st);
            st.HookState &= ~HookState.Firing;
            st.HookRefire = MathF.Max(st.HookRefire, Api.Clock.Time + Primary.Refire);
        }
        else if ((st.HookState & HookState.Removing) != 0)
        {
            if (st.Hook is not null) RemoveHook(st);
            st.HookState &= ~HookState.Removing;
        }
    }

    // FireGrapplingHook (server/hook.qc) — launch the chain projectile that latches onto geometry.
    private void FireGrapplingHook(Entity actor, WeaponSlot slot, WeaponSlotState st)
    {
        Vector3 forward = QMath.Forward(actor.Angles);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward);

        Entity hook = Api.Entities.Spawn();
        hook.ClassName = "grapplinghook";
        hook.Owner = actor;
        hook.NetName = NetName;
        hook.MoveType = MoveType.Fly;
        Projectiles.MakeTrigger(hook); // QC PROJECTILE_MAKETRIGGER (SOLID_CORPSE): transparent to the firer's movement
        hook.Flags = EntFlags.Item;
        hook.TakeDamage = DamageMode.Aim; // shootable
        hook.Health = 0f;                  // 0 = in flight; set >0 (latched flag) on GrapplingHook_Stop
        hook.MaxHealth = GrappleHealth;    // actual HP for shoot-down
        Api.Entities.SetSize(hook, new Vector3(-3, -3, -3), new Vector3(3, 3, 3));
        Api.Entities.SetOrigin(hook, shot.Origin);
        hook.Velocity = shot.Dir * GrappleSpeedFly;
        hook.Angles = QMath.VecToAngles(hook.Velocity);

        hook.Touch = (self, other) => GrapplingHookTouch(self, other, actor, slot);
        hook.Think = self => GrapplingHookThink(self, actor, slot);
        hook.NextThink = Api.Clock.Time;
        hook.ProjectileDamage = (self, attacker) => RemoveHook(actor.WeaponState(slot));

        // MUTATOR_CALLHOOK(EditProjectile, actor, missile) (server/hook.qc FireGrapplingHook).
        var ep = new MutatorHooks.EditProjectileArgs(actor, hook);
        MutatorHooks.EditProjectile.Call(ref ep);

        st.Hook = hook;
        Api.Sound.Play(actor, SoundChannel.Body, "weapons/hook_fire.wav");
    }

    // GrapplingHookTouch + GrapplingHook_Stop — latch the chain where it lands and start reeling.
    private void GrapplingHookTouch(Entity self, Entity other, Entity actor, WeaponSlot slot)
    {
        if (other.MoveType == MoveType.Follow) return;
        Api.Sound.Play(self, SoundChannel.Body, "weapons/hook_impact.wav");
        self.Health = 1f;                  // latched flag (.state = 1)
        self.Velocity = Vector3.Zero;
        self.MoveType = MoveType.None;
        self.Touch = null;
        // keep thinking so the owner gets reeled in.
        self.Think = s => GrapplingHookThink(s, actor, slot);
        self.NextThink = Api.Clock.Time;
    }

    // GrapplingHookThink — reel the owner toward the latched chain end (the simple, non-"tarzan" pull). server/hook.qc
    private void GrapplingHookThink(Entity hook, Entity actor, WeaponSlot slot)
    {
        var st = actor.WeaponState(slot);
        if (!ReferenceEquals(st.Hook, hook)) { Api.Entities.Remove(hook); return; }
        hook.NextThink = Api.Clock.Time;

        // MUTATOR_CALLHOOK(GrappleHookThink, this) — server/hook.qc GrapplingHookThink. vampirehook drains
        // health from the hooked player here. The hook's .realowner is the firing actor; QC's tarzan variant
        // also tracks a hooked-player .aiment, which the port's geometry-latching grapple doesn't set.
        var gh = new MutatorHooks.GrappleHookThinkArgs(hook);
        MutatorHooks.GrappleHookThink.Call(ref gh);

        if (hook.Health <= 0f) return; // still in flight, not latched yet

        // Pull: move the player toward a point just short of the hook; speed ramps down near the end.
        Vector3 myorg = actor.Origin + actor.ViewOfs;
        Vector3 dir = QMath.Normalize(hook.Origin - myorg);
        Vector3 end = hook.Origin - dir * GrappleLengthMin;
        float dist = (end - myorg).Length();

        float spd;
        if (dist < 200f) spd = dist * (GrappleSpeedPull / 200f);
        else spd = GrappleSpeedPull;
        if (spd < 50f) spd = 0f;

        actor.Velocity = dir * spd;
        actor.MoveType = MoveType.Fly;          // QC sets the owner to MOVETYPE_FLY while reeling
        actor.Flags &= ~EntFlags.OnGround;
    }

    // RemoveHook / GrapplingHookReset — drop the chain and restore the owner's movement.
    private void RemoveHook(WeaponSlotState st)
    {
        if (st.Hook is not null)
        {
            Api.Entities.Remove(st.Hook);
            st.Hook = null;
        }
        // the player's movetype is restored to walk by the movement system once velocity is reapplied.
    }

    // W_Hook_Attack2 — lob a gravity bomb that falls under its own gravity and pulls victims in. hook.qc
    private void AttackGravityBomb(Entity actor, WeaponSlot slot)
    {
        // QC leaves secondary ammo-free for now (WEAPONTODO), so no TakeResource here.
        Vector3 forward = QMath.Forward(actor.Angles);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward);

        Entity gren = Api.Entities.Spawn();
        gren.ClassName = "hookbomb";
        gren.Owner = actor;
        gren.NetName = NetName;
        gren.MoveType = MoveType.Toss;
        Projectiles.MakeTrigger(gren); // QC PROJECTILE_MAKETRIGGER (SOLID_CORPSE): transparent to the firer's movement
        gren.Flags = EntFlags.Item; // QC FL_PROJECTILE
        gren.Gravity = Secondary.Gravity;
        Api.Entities.SetSize(gren, Vector3.Zero, Vector3.Zero);
        Api.Entities.SetOrigin(gren, shot.Origin);

        gren.TakeDamage = DamageMode.Yes; // shootable
        gren.Health = Secondary.Health;

        // QC: gren.velocity = '0 0 1' * speed (just an initial vertical nudge; then it falls).
        gren.Velocity = new Vector3(0f, 0f, Secondary.Speed);
        gren.Angles = Vector3.Zero;

        // nextthink = time + lifetime; the lifetime think starts the blast (adaptor_think2use_hittype_splash).
        gren.Touch = (self, other) => StartBlast(self);
        gren.Think = self => StartBlast(self);
        gren.NextThink = Api.Clock.Time + Secondary.Lifetime;
        // W_Hook_Damage: a shot-down bomb starts its pull blast early.
        gren.ProjectileDamage = (self, attacker) => StartBlast(self);

        // MUTATOR_CALLHOOK(EditProjectile, actor, gren) (hook.qc W_Hook_Attack2).
        var ep = new MutatorHooks.EditProjectileArgs(actor, gren);
        MutatorHooks.EditProjectile.Call(ref ep);

        Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/hookbomb_fire.wav");
    }

    // W_Hook_Explode2 — begin the duration-spread gravity blast (pulls victims in over `duration`). hook.qc
    private void StartBlast(Entity self)
    {
        self.Touch = null;
        self.TakeDamage = DamageMode.No;
        self.MoveType = MoveType.None;

        // QC ticks W_Hook_ExplodeThink every 0.05s for `duration`, applying the *delta* of a power-curve
        // falloff each tick so the total blast == one full RadiusDamage. teleport_time marks the blast start.
        float startTime = Api.Clock.Time;
        self.NextThink = Api.Clock.Time;
        self.Think = s => ExplodeThink(s, startTime, lastFrac: 1f);
        self.Think(self);
    }

    // W_Hook_ExplodeThink — apply this tick's share of the pull blast; repeat until duration elapses. hook.qc
    private void ExplodeThink(Entity self, float startTime, float lastFrac)
    {
        float dt = Api.Clock.Time - startTime;
        // dmg_remaining = clamp(1 - dt/duration, 0, 1) ^ power; f = previous_remaining - this_remaining.
        float remaining = MathF.Pow(QMath.Clamp(1f - dt / Secondary.Duration, 0f, 1f), Secondary.Power);
        float f = lastFrac - remaining;

        if (f > 0f)
        {
            WeaponSplash.RadiusDamage(self, self.Origin, f * Secondary.Damage, f * Secondary.EdgeDamage,
                Secondary.Radius, self.Owner, RegistryId, f * Secondary.Force);
        }

        if (dt < Secondary.Duration)
        {
            self.NextThink = Api.Clock.Time + 0.05f;
            self.Think = s => ExplodeThink(s, startTime, remaining);
        }
        else
        {
            Api.Entities.Remove(self);
        }
    }

    // METHOD(Hook, wr_checkammo1) — hook.qc (needs fuel to fire the hook).
    public bool CheckAmmoPrimary(Entity actor) => actor.GetResource(AmmoType) >= Primary.Ammo;

    // METHOD(Hook, wr_checkammo2) — hook.qc (secondary is ammo-free for now).
    public bool CheckAmmoSecondary(Entity actor) => true;
}
