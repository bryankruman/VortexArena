// Port: qcsrc/common/vehicles/vehicle/racer.{qh,qc} + racer_weapon.{qh,qc}
//
// The Racer ("wakizashi") — a fast single-seat hovercraft. Primary fire is a rapid energy laser cannon,
// secondary fires a pair of guided/ground-hugging rockets; jump afterburns (drains energy). Balance values
// are the bal/server cfg defaults inlined below (g_vehicle_racer_*).
//
// FULL deep behavior is implemented here: the 4-point engine-spring hover (racer_align4point via
// VehiclePhysics.ForceFromTag), the racer_frame avelocity-free angle controller (yaw/pitch/roll toward the
// pilot's view, friction, downforce, water handling), the afterburner energy drain on the real jump button,
// and the homing/ground-hugging rocket guidance (racer_rocket_tracker / racer_rocket_groundhugger via
// VehiclePhysics.GuideRocket). NOTE (client-render): only genuinely client-side items — muzzle FX, aux
// crosshair color, HUD % mirroring — remain.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Racer vehicle (QC <c>CLASS(Racer, Vehicle)</c>). Registered into <see cref="Vehicles"/> via
/// <see cref="VehicleAttribute"/>.
/// </summary>
[Vehicle]
public sealed class Racer : Vehicle
{
    // ---- chassis (racer.qc autocvars) ----
    public float SpeedForward = 650f;     // g_vehicle_racer_speed_forward
    public float SpeedStrafe = 650f;      // g_vehicle_racer_speed_strafe
    public float SpeedAfterburn = 3000f;  // g_vehicle_racer_speed_afterburn
    public float AfterburnCost = 130f;    // g_vehicle_racer_afterburn_cost (energy/sec)
    public float WaterburnCost = 5f;      // g_vehicle_racer_waterburn_cost
    public float WaterburnSpeed = 750f;   // g_vehicle_racer_waterburn_speed
    public float WaterSpeedForward = 600f;// g_vehicle_racer_water_speed_forward
    public float WaterSpeedStrafe = 600f; // g_vehicle_racer_water_speed_strafe
    public float Friction = 0.45f;        // g_vehicle_racer_friction
    public float TurnSpeed = 220f;        // g_vehicle_racer_turnspeed
    public float TurnRoll = 30f;          // g_vehicle_racer_turnroll
    public float PitchSpeed = 125f;       // g_vehicle_racer_pitchspeed
    public float PitchLimit = 30f;        // g_vehicle_racer_pitchlimit
    public float HoverPower = 8000f;      // g_vehicle_racer_hoverpower (x4 engines)
    public float SpringLength = 90f;      // g_vehicle_racer_springlength
    public float UpForceDamper = 2f;      // g_vehicle_racer_upforcedamper
    public float WaterUpForceDamper = 15f;// g_vehicle_racer_water_upforcedamper
    public float AngleStabilizer = 1.75f; // g_vehicle_racer_anglestabilizer
    public float DownForce = 0.01f;       // g_vehicle_racer_downforce
    public float WaterDownForce = 0.03f;  // g_vehicle_racer_water_downforce
    public int HoverType = 0;             // g_vehicle_racer_hovertype (0=hover, !=0=maglev)
    public float ThinkRate = 0.05f;       // g_vehicle_racer_thinkrate

    // ---- resources ----
    public float MaxEnergy = 100f;            // g_vehicle_racer_energy
    public float EnergyRegen = 90f;           // g_vehicle_racer_energy_regen
    public float EnergyRegenPause = 0.35f;    // g_vehicle_racer_energy_regen_pause
    public float MaxShield = 100f;            // g_vehicle_racer_shield
    public float ShieldRegen = 30f;           // g_vehicle_racer_shield_regen
    public float ShieldRegenPause = 1f;       // g_vehicle_racer_shield_regen_pause
    public float HealthRegen = 0f;            // g_vehicle_racer_health_regen
    public float RespawnTime = 35f;           // g_vehicle_racer_respawntime

    // ---- cannon (racer_weapon.qh) ----
    public float CannonCost = 1.5f;       // g_vehicle_racer_cannon_cost (energy/shot)
    public float CannonDamage = 15f;      // g_vehicle_racer_cannon_damage
    public float CannonRadius = 100f;     // g_vehicle_racer_cannon_radius
    public float CannonForce = 50f;       // g_vehicle_racer_cannon_force
    public float CannonSpeed = 15000f;    // g_vehicle_racer_cannon_speed
    public float CannonSpread = 0.0125f;  // g_vehicle_racer_cannon_spread
    public float CannonRefire = 0.05f;    // g_vehicle_racer_cannon_refire

    // ---- rocket (racer_weapon.qh) ----
    public float RocketDamage = 100f;     // g_vehicle_racer_rocket_damage
    public float RocketRadius = 125f;     // g_vehicle_racer_rocket_radius
    public float RocketForce = 350f;      // g_vehicle_racer_rocket_force
    public float RocketSpeed = 900f;      // g_vehicle_racer_rocket_speed
    public float RocketAccel = 1600f;     // g_vehicle_racer_rocket_accel
    public float RocketTurnRate = 0.2f;   // g_vehicle_racer_rocket_turnrate
    public float RocketRefire = 3f;       // g_vehicle_racer_rocket_refire
    public bool RocketLockTarget = true;  // g_vehicle_racer_rocket_locktarget
    public float RocketLockingTime = 0.35f;      // g_vehicle_racer_rocket_locking_time
    public float RocketLockingReleaseTime = 0.5f;// g_vehicle_racer_rocket_locking_releasetime
    public float RocketLockedTime = 4f;          // g_vehicle_racer_rocket_locked_time

    // ---- death blast (racer.qc) ----
    public float BlowupRadius = 250f;     // g_vehicle_racer_blowup_radius
    public float BlowupCoreDamage = 250f; // g_vehicle_racer_blowup_coredamage
    public float BlowupEdgeDamage = 15f;  // g_vehicle_racer_blowup_edgedamage
    public float BlowupForce = 250f;      // g_vehicle_racer_blowup_forceintensity

    public Racer()
    {
        NetName = "racer";
        DisplayName = "Racer";
        Model = "models/vehicles/wakizashi.dpm";
        StartHealth = 200f;               // g_vehicle_racer_health
    }

    // METHOD(Racer, vr_spawn) — racer.qc
    public override void Spawn(Entity vehicle)
    {
        VehicleCommon.SpawnVehicle(vehicle, this);

        if (Model is not null && Api.Services is not null)
            Api.Entities.SetModel(vehicle, Model);

        vehicle.MaxHealth = StartHealth;
        vehicle.SetResourceExplicit(ResourceType.Health, StartHealth);
        vehicle.VehicleShield = MaxShield;
        vehicle.VehicleEnergy = MaxEnergy;
        vehicle.RespawnTime = RespawnTime;
        vehicle.MoveType = MoveType.Toss;     // QC: MOVETYPE_TOSS on spawn (becomes BOUNCE on enter)
        vehicle.Solid = Solid.SlideBox;
        vehicle.DeadState = DeadFlag.No;
        vehicle.DamageForceScale = 0.5f;      // QC vr_spawn: instance.damageforcescale = 0.5
        vehicle.Touch = null;
        vehicle.VehWeaponDelay = Time;

        // Hitbox: '-120 -120 -40' * 0.5 .. '120 120 40' * 0.5 (racer.qh).
        if (Api.Services is not null)
            Api.Entities.SetSize(vehicle, new Vector3(-60f, -60f, -20f), new Vector3(60f, 60f, 20f));

        // QC vr_setup: enable the regen capability flags this vehicle supports.
        vehicle.VehicleFlags |= VehicleFlags.HasShield | VehicleFlags.MoveHover | VehicleFlags.DmgShake | VehicleFlags.DmgRoll;
        if (EnergyRegen > 0f) vehicle.VehicleFlags |= VehicleFlags.EnergyRegen;
        if (ShieldRegen > 0f) vehicle.VehicleFlags |= VehicleFlags.ShieldRegen;
        if (HealthRegen > 0f) vehicle.VehicleFlags |= VehicleFlags.HealthRegen;

        vehicle.Think = self => Think(self);
        vehicle.NextThink = Time;

        // TODO(port,client): qcsrc/common/vehicles/vehicle/racer.qc vr_spawn — scale-0.5 model hack,
        //                    hud/viewport tag attachment (cosmetic only; physics uses the real hitbox above).
    }

    // METHOD(Racer, vr_enter) — racer.qc
    public override void Enter(Entity vehicle, Entity player)
    {
        VehicleCommon.EnterVehicle(vehicle, player);
        vehicle.MoveType = MoveType.Bounce; // QC: set_movetype(instance, MOVETYPE_BOUNCE)

        // The Racer is driven by the player's per-frame physics plug (Frame), not its own think, while
        // occupied. Think still runs for regen/glue via the owner branch.
        vehicle.NextThink = Time;
        vehicle.Think = self => Think(self);
    }

    // void racer_exit(entity this, int eject) — racer.qc
    public override void Exit(Entity vehicle, Entity player)
    {
        QMath.AngleVectors(vehicle.Angles, out Vector3 forward, out _, out Vector3 up);
        Vector3 spot;

        bool dying = VehicleCommon.IsDead(vehicle);
        if (dying)
        {
            // Eject hard, up and forward (QC eject branch).
            spot = vehicle.Origin + forward * 100f + new Vector3(0, 0, 64f);
            spot = VehicleCommon.FindGoodExit(vehicle, player, spot);
            player.Velocity = (up + forward * 0.25f) * 750f;
        }
        else
        {
            float maxAir = MaxAirSpeed();
            if (QMath.VLen(vehicle.Velocity) > 2f * maxAir)
            {
                player.Velocity = QMath.Normalize(vehicle.Velocity) * maxAir * 2f;
                player.Velocity += new Vector3(0, 0, 200f);
                spot = vehicle.Origin + forward * 32f + new Vector3(0, 0, 32f);
            }
            else
            {
                player.Velocity = vehicle.Velocity * 0.5f + new Vector3(0, 0, 10f);
                spot = vehicle.Origin - forward * 200f + new Vector3(0, 0, 32f);
            }
            spot = VehicleCommon.FindGoodExit(vehicle, player, spot);
        }
        // QC also sets owner.oldvelocity = owner.velocity (fall-damage negation); OldVelocity isn't modeled.

        VehicleCommon.ExitVehicle(vehicle, player, dying ? VehicleExitFlag.Eject : VehicleExitFlag.Normal);
        if (Api.Services is not null)
            Api.Entities.SetOrigin(player, spot);

        // QC: the empty racer resumes its idle hover think.
        vehicle.MoveType = MoveType.Bounce;
        vehicle.Think = self => Think(self);
        vehicle.NextThink = Time;
    }

    // void racer_think(entity this) — racer.qc: the idle/empty hover. Also the regen + glue dispatcher.
    public override void Think(Entity vehicle)
    {
        float dt = ThinkRate;
        vehicle.NextThink = Time + dt;

        if (VehicleCommon.FreezeIfGameStopped(vehicle))
            return;

        Entity? player = vehicle.Owner;

        if (player is null)
        {
            // QC racer_think: a light spring + idle-bob keeps the empty craft hovering near the ground,
            // and the stabilizer eases pitch/roll back toward level.
            HoverIdle(vehicle, dt);
        }
        else if (!VehicleCommon.IsDead(vehicle))
        {
            // Piloted: run the full per-frame controller from the seated player's input.
            Frame(vehicle, player, vehicle.VehInput, dt);
        }

        // --- regen (shared) ---
        if ((vehicle.VehicleFlags & VehicleFlags.ShieldRegen) != 0)
            vehicle.VehicleShield = VehicleCommon.Regen(vehicle, vehicle.VehicleShield, vehicle.DmgTime,
                MaxShield, ShieldRegenPause, ShieldRegen, dt, healthScale: true);
        if ((vehicle.VehicleFlags & VehicleFlags.EnergyRegen) != 0)
            vehicle.VehicleEnergy = VehicleCommon.Regen(vehicle, vehicle.VehicleEnergy, vehicle.VehWait,
                MaxEnergy, EnergyRegenPause, EnergyRegen, dt, healthScale: false);
        if ((vehicle.VehicleFlags & VehicleFlags.HealthRegen) != 0)
            VehicleCommon.RegenResource(vehicle, vehicle.DmgTime, StartHealth, 0f, HealthRegen, dt, false, ResourceType.Health);

        if (player is not null)
        {
            // QC: keep the seated player glued to the vehicle (origin + '0 0 32') and matching its velocity.
            if (Api.Services is not null)
                Api.Entities.SetOrigin(player, vehicle.Origin + new Vector3(0, 0, 32f));
            player.OldOrigin = player.Origin; // negate fall damage
            player.Velocity = vehicle.Velocity;
        }
    }

    /// <summary>
    /// Port of <c>racer_frame</c> (the SVQC controller half): 4-point engine-spring hover + align, yaw/roll/
    /// pitch toward the pilot's view, friction + downforce, afterburn, and both weapons. This is the deep
    /// behavior — invoked from <see cref="Think"/> with the seated player's input each tick.
    /// </summary>
    public void Frame(Entity vehicle, Entity player, in MovementInput input, float dt)
    {
        if (VehicleCommon.IsDead(vehicle)) return;

        bool inLiquid = vehicle.WaterLevel > 0 || VehiclePhysics.InLiquid(vehicle.Origin);

        // --- 4-point engine spring (racer_align4point) -------------------------------------------------
        Align4Point(vehicle, input, dt, inLiquid);

        // --- angle controller: yaw/roll/pitch toward the pilot's aim (racer_frame) ---------------------
        Vector3 vAngle = input.ViewAngles;
        Vector3 ang = vehicle.Angles;
        ang.X = -ang.X; // QC flips pitch sign for the controller, flips back after

        // Yaw toward view yaw.
        float ftmp = TurnSpeed * dt;
        ftmp = QMath.Bound(-ftmp, VehiclePhysics.ShortAngle(vAngle.Y - ang.Y), ftmp);
        ang.Y = VehiclePhysics.AngleMods(ang.Y + ftmp);
        // Roll proportional to the yaw rate (QC turnroll).
        ang.Z += -ftmp * TurnRoll * dt;
        // Pitch toward view pitch, clamped.
        float pf = PitchSpeed * dt;
        pf = QMath.Bound(-pf, VehiclePhysics.ShortAngle(vAngle.X - ang.X), pf);
        ang.X = QMath.Bound(-PitchLimit, VehiclePhysics.AngleMods(ang.X + pf), PitchLimit);

        ang.X = -ang.X;
        vehicle.Angles = ang;

        // --- thrust (friction + wishmove + afterburn + downforce) --------------------------------------
        QMath.AngleVectors(vehicle.Angles, out Vector3 forward, out Vector3 right, out Vector3 up);
        Vector3 df = vehicle.Velocity * -Friction;

        Vector3 move = input.MoveValues;
        if (move.X != 0f)
        {
            float spd = inLiquid ? WaterSpeedForward : SpeedForward;
            df += forward * (move.X > 0f ? spd : -spd);
        }
        if (move.Y != 0f)
        {
            float spd = inLiquid ? WaterSpeedStrafe : SpeedStrafe;
            df += right * (move.Y > 0f ? spd : -spd);
        }

        // Afterburn on jump (energy-gated). QC drains afterburn_cost/sec (or waterburn in liquid).
        if (input.ButtonJump && vehicle.VehicleEnergy >= AfterburnCost * dt)
        {
            vehicle.VehWait = Time; // reset the energy regen-pause
            if (inLiquid)
            {
                vehicle.VehicleEnergy -= WaterburnCost * dt;
                df += forward * WaterburnSpeed;
            }
            else
            {
                vehicle.VehicleEnergy -= AfterburnCost * dt;
                df += forward * SpeedAfterburn;
            }
            if (Api.Services is not null)
                Api.Sound.Play(vehicle, SoundChannel.Auto, "vehicles/racer_boost.wav");
            vehicle.Effects |= VehicleEffects.Boosting; // networked: the client overlays the boost engine sound
        }
        else
        {
            vehicle.Effects &= ~VehicleEffects.Boosting;
        }

        // Networked client-render flag: cannon firing → the client pops the muzzle flash.
        if (input.ButtonAttack1) vehicle.Effects |= VehicleEffects.Firing;
        else vehicle.Effects &= ~VehicleEffects.Firing;

        // Downforce (QC: stronger right after leaving water).
        float dforce = inLiquid ? WaterDownForce : DownForce;
        df -= up * (QMath.VLen(vehicle.Velocity) * dforce);

        vehicle.Velocity += df * dt;

        // --- weapons -----------------------------------------------------------------------------------
        // Primary: rapid energy laser (refire-gated, energy-gated).
        if (input.ButtonAttack1 && vehicle.VehicleEnergy >= CannonCost && Time >= vehicle.VehAttackFinished)
        {
            vehicle.VehAttackFinished = Time + CannonRefire;
            FireCannon(vehicle, player);
        }

        // Rocket lock-on (racer_frame): trace the crosshair and build/decay the lock.
        if (RocketLockTarget)
        {
            TraceResult tr = VehiclePhysics.CrosshairTrace(vehicle, input.ViewAngles, vehicle);
            VehiclePhysics.LockTarget(vehicle, tr.Ent,
                dt / RocketLockingTime, dt / RocketLockingReleaseTime, RocketLockedTime);
            // TODO(port,client): UpdateAuxiliaryXhair lock-color (red/green/blue) by lock_strength.
        }

        // Secondary: a pair of rockets (right then left), with the long refire after the pair.
        if (input.ButtonAttack2 && Time > vehicle.VehWeaponDelay)
        {
            ++vehicle.VehBulletCounter;
            vehicle.VehWeaponDelay = Time + 0.3f;
            bool locked = vehicle.VehLockStrength == 1f && vehicle.VehLockTarget is not null;
            Entity? targ = locked ? vehicle.VehLockTarget : null;

            if (vehicle.VehBulletCounter == 1)
            {
                FireRocket(vehicle, player, "tag_rocket_r", targ);
            }
            else if (vehicle.VehBulletCounter >= 2)
            {
                FireRocket(vehicle, player, "tag_rocket_l", targ);
                vehicle.VehLockStrength = 0f;
                vehicle.VehLockTarget = null;
                vehicle.VehBulletCounter = 0;
                vehicle.VehWeaponDelay = Time + RocketRefire;
                vehicle.VehReloadStart = Time;
            }
        }

        // TODO(port,client): qcsrc/common/vehicles/vehicle/racer.qc racer_frame — engine move/idle/boost sound
        //                    selection, EFFECT_RACER_BOOSTER / smoke trails, vehicle_ammo2/reload2 HUD %.
    }

    /// <summary>Port of <c>racer_align4point</c>: four engine springs + the resulting upward push and pitch/roll torque.</summary>
    private void Align4Point(Entity vehicle, in MovementInput input, float dt, bool inLiquid)
    {
        bool maglev = HoverType != 0;
        var (frF, frP) = VehiclePhysics.ForceFromTag(vehicle, "tag_engine_fr", SpringLength, HoverPower, maglev);
        var (flF, flP) = VehiclePhysics.ForceFromTag(vehicle, "tag_engine_fl", SpringLength, HoverPower, maglev);
        var (brF, brP) = VehiclePhysics.ForceFromTag(vehicle, "tag_engine_br", SpringLength, HoverPower, maglev);
        var (blF, blP) = VehiclePhysics.ForceFromTag(vehicle, "tag_engine_bl", SpringLength, HoverPower, maglev);

        Vector3 push = frF + flF + brF + blF;
        vehicle.Velocity += push * dt;

        float uforce = inLiquid ? WaterUpForceDamper : UpForceDamper;
        if (inLiquid)
        {
            Vector3 v = vehicle.Velocity;
            v.Z += input.ButtonCrouch ? 30f : 200f;
            vehicle.Velocity = v;
        }

        // Anti-oscillation damper on the upward velocity.
        if (vehicle.Velocity.Z > 0f)
        {
            Vector3 v = vehicle.Velocity; v.Z *= 1f - uforce * dt; vehicle.Velocity = v;
        }

        // Differential of the four engine powers -> pitch (x) and roll (z) torque (QC * 360).
        float torqueX = ((flP - blP) + (frP - brP)) * 360f;
        float torqueZ = ((frP - flP) + (brP - blP)) * 360f;

        Vector3 ang = vehicle.Angles;
        ang.Z += torqueZ * dt;
        ang.X += torqueX * dt;
        // Stabilizer eases pitch/roll back toward level.
        ang.X *= 1f - AngleStabilizer * dt;
        ang.Z *= 1f - AngleStabilizer * dt;
        vehicle.Angles = ang;
    }

    /// <summary>QC racer_think idle hover spring (sin-bob altitude hold) + stabilizer for the empty craft.</summary>
    private void HoverIdle(Entity vehicle, float dt)
    {
        if (Api.Services is null) return;
        TraceResult tr = Api.Trace.Trace(vehicle.Origin, vehicle.Mins, vehicle.Maxs,
            vehicle.Origin - new Vector3(0, 0, SpringLength), MoveFilter.NoMonsters, vehicle);

        Vector3 df = vehicle.Velocity * -Friction;
        df.Z += (1f - tr.Fraction) * HoverPower + MathF.Sin(Time * 2f) * SpringLength * 2f;

        float forced = UpForceDamper;
        bool inLiquid = VehiclePhysics.InLiquid(vehicle.Origin - new Vector3(0, 0, 64f));
        if (inLiquid)
        {
            forced = WaterUpForceDamper;
            Vector3 vv = vehicle.Velocity; vv.Z += 200f; vehicle.Velocity = vv;
        }

        vehicle.Velocity += df * dt;
        if (vehicle.Velocity.Z > 0f)
        {
            Vector3 vv = vehicle.Velocity; vv.Z *= 1f - forced * dt; vehicle.Velocity = vv;
        }

        Vector3 ang = vehicle.Angles;
        ang.X *= 1f - AngleStabilizer * dt;
        ang.Z *= 1f - AngleStabilizer * dt;
        vehicle.Angles = ang;
    }

    // METHOD(Racer, vr_death) — racer.qc
    public override void Death(Entity vehicle)
    {
        vehicle.SetResourceExplicit(ResourceType.Health, 0f);
        vehicle.TakeDamage = DamageMode.No;
        vehicle.Solid = Solid.Corpse;
        vehicle.DeadState = DeadFlag.Dying;
        vehicle.MoveType = MoveType.Bounce;

        // QC racer_diethink: tumble (avelocity), then after a random delay racer_blowup detonates.
        float delay = Time + 2f + Prandom.Range(0f, 3f);
        vehicle.VehBulletCounter = 1 + (int)Prandom.Range(0f, 2f); // .cnt = deadtouch detonation countdown
        Vector3 av = vehicle.AVelocity;
        av.Z = Prandom.Float() < 0.5f ? 32f : -32f;
        av.X = -QMath.VLen(vehicle.Velocity) * 0.2f;
        vehicle.AVelocity = av;
        vehicle.Velocity += new Vector3(0, 0, 700f); // QC: pop up before the blast

        // racer_deadtouch: each bounce decrements the counter; hitting 0 detonates early.
        vehicle.Touch = (self, _) =>
        {
            Vector3 a = self.AVelocity; a.X *= 0.7f; self.AVelocity = a;
            if (--self.VehBulletCounter <= 0) Blowup(self);
        };

        vehicle.Think = self =>
        {
            self.NextThink = Time;
            if (Time >= delay) Blowup(self);
        };
        vehicle.NextThink = Time;
        // TODO(port,client): EFFECT_EXPLOSION_MEDIUM, colormod darken, stop-networking (setSendEntity null).
    }

    /// <summary>Port of <c>racer_blowup</c>: the delayed radius blast, then schedule respawn at the spawn point.</summary>
    private void Blowup(Entity vehicle)
    {
        vehicle.DeadState = DeadFlag.Dead;
        // The pilot was already ejected when health hit 0 (vehicles_damage). Eject any straggler defensively.
        if (vehicle.Owner is not null)
            VehicleCommon.ExitVehicle(vehicle, vehicle.Owner, VehicleExitFlag.Normal);

        WeaponSplash.RadiusDamage(vehicle, vehicle.Origin, BlowupCoreDamage, BlowupEdgeDamage,
            BlowupRadius, vehicle.Enemy, RegistryId, BlowupForce);

        vehicle.MoveType = MoveType.None;
        vehicle.Solid = Solid.Not;
        vehicle.Touch = null;
        vehicle.AVelocity = Vector3.Zero;
        vehicle.Velocity = Vector3.Zero;
        if (Api.Services is not null)
            Api.Entities.SetOrigin(vehicle, vehicle.SpawnPos);

        vehicle.NextThink = Time + RespawnTime;
        vehicle.Think = self => Spawn(self); // QC: setthink(vehicles_spawn) after respawntime
    }

    // ============================ WEAPONS ============================

    // METHOD(RacerAttack, wr_think) fire&1 — racer_weapon.qc: the rapid energy laser cannon.
    /// <summary>Fire the primary laser cannon (one energy bolt; QC W_SetupShot + vehicles_projectile, alternating muzzle tags).</summary>
    public void FireCannon(Entity vehicle, Entity player)
    {
        if (vehicle.VehicleEnergy < CannonCost) return; // wr_checkammo1
        vehicle.VehicleEnergy -= CannonCost;
        vehicle.VehWait = Time; // reset energy regen-pause

        QMath.AngleVectors(vehicle.Angles, out Vector3 forward, out Vector3 right, out Vector3 up);
        // Alternate the two fire tags (QC veh.cnt toggles tag_fire1/tag_fire2).
        vehicle.VehSoundState = vehicle.VehSoundState == 1 ? 0 : 1;
        string tag = vehicle.VehSoundState == 1 ? "tag_fire1" : "tag_fire2";
        Vector3 org = VehiclePhysics.TagOrigin(vehicle, tag, new Vector3(80f, vehicle.VehSoundState == 1 ? 16f : -16f, 0f));

        // Spread cone around forward (deterministic PRNG, ADR-0010).
        Vector3 vel = Prandom.Spread(forward, right, up, CannonSpread) * CannonSpeed;

        VehicleCommon.SpawnProjectile(vehicle, player, org, vel,
            CannonDamage, CannonRadius, CannonForce, size: 0f,
            DeathTypes.FromWeapon("racercannon"), RegistryId, health: 0f, lifetime: 0f,
            fireSound: "vehicles/lasergun_fire.wav");
        // TODO(port,client): EFFECT_RACER_MUZZLEFLASH muzzle effect + CSQCProjectile visual.
    }

    // void racer_fire_rocket() — racer_weapon.qc: the secondary rocket (guided when locked, else ground-hugging).
    /// <summary>Fire one secondary rocket from <paramref name="tag"/>; homes on <paramref name="targ"/> when locked, else hugs terrain.</summary>
    public void FireRocket(Entity vehicle, Entity player, string tag, Entity? targ)
    {
        var (org, fwd) = VehiclePhysics.TagOriginForward(vehicle, tag);
        Vector3 vel = fwd * RocketSpeed;

        Entity rocket = VehicleCommon.SpawnProjectile(vehicle, player, org, vel,
            RocketDamage, RocketRadius, RocketForce, size: 3f,
            DeathTypes.FromWeapon("racercannon"), RegistryId, health: 20f, lifetime: 15f,
            fireSound: "vehicles/rocket_fire.wav");
        rocket.ClassName = "racer_rocket";

        // Guidance parameters (racer_fire_rocket): per-tick accel, turn rate, lifetime, target.
        rocket.VehProjAccel = RocketAccel * FrameTime;
        rocket.VehProjTurnRate = RocketTurnRate;
        rocket.VehProjExpire = Time + 15f;
        rocket.Enemy = targ;
        rocket.VehGuideMode = (int)(targ is not null
            ? VehiclePhysics.GuideMode.RacerHoming
            : VehiclePhysics.GuideMode.RacerGroundHug);

        // Drive the per-tick guidance off the projectile's own think (replacing the dumb fly-straight think).
        rocket.Think = self =>
        {
            self.NextThink = Time;
            var mode = (VehiclePhysics.GuideMode)self.VehGuideMode;
            if (!VehiclePhysics.GuideRocket(self, mode, FrameTime, crosshair: null))
            {
                // QC use() -> explode: detonate via the shared projectile blast.
                WeaponSplash.RadiusDamage(self, self.Origin, RocketDamage, 0f, RocketRadius,
                    self.DmgInflictor, RegistryId, RocketForce);
                Api.Entities.Remove(self);
            }
        };
        rocket.NextThink = Time;
    }

    private static float Time => Api.Services is not null ? Api.Clock.Time : 0f;
    private static float FrameTime => Api.Services is not null ? Api.Clock.FrameTime : 0.05f;
    private static float MaxAirSpeed()
    {
        if (Api.Services is null) return 400f;
        float v = Api.Cvars.GetFloat("sv_maxairspeed");
        return v != 0f ? v : 400f;
    }
}
