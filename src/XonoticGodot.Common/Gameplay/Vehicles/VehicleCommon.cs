// Port: qcsrc/common/vehicles/sv_vehicles.qc (+ sv_vehicles.qh, vehicle.qh, vehicles.qc)
//
// Shared, vehicle-agnostic infrastructure for the C# vehicle port: the [Vehicle] registry attribute,
// the per-entity vehicle fields (promoted from QC's flat .vehicle_* edict fields), the VHF_* flag enum,
// and the Godot-free core of the shared enter/exit/spawn/think/regen/damage routines that every concrete
// vehicle (Racer/Raptor/Spiderbot/Bumblebee) leans on.
//
// NAMESPACE NOTE: everything is in the flat `XonoticGodot.Common.Gameplay` namespace on purpose — a nested
// `.Vehicles` namespace would collide with the `Vehicles` catalog type in EntityClasses.cs. The folder is
// named Vehicles/ but the namespace is not.
//
// This is a NEW file; it only ADDS members to the existing `partial class Entity` (same technique as
// Gameplay/Items/EntityResources.cs) and never edits an existing file.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Framework
{
    public partial class Entity
    {
        // ---- QC vehicle linkage (sv_vehicles.qh: .vehicle / .owner) ----

        /// <summary>QC <c>.vehicle</c> — on a player, the vehicle entity they currently pilot (NULL when on foot).</summary>
        public Entity? Vehicle;

        /// <summary>QC <c>.vehicledef</c> — the <see cref="Gameplay.Vehicle"/> descriptor driving this vehicle entity.</summary>
        public Gameplay.Vehicle? VehicleDef;

        /// <summary>QC <c>.tur_head</c> — the turret-head sub-entity (turret/aim model) attached to the vehicle body.</summary>
        public Entity? TurHead;

        // ---- QC vehicle stat fields (sv_vehicles.qh _STAT(VEHICLESTAT_*)) ----
        // On the vehicle entity these hold the REAL values; the per-frame code mirrors a 0..100 percentage
        // onto the owning player. We keep the real values here; the percentage mirroring is a HUD/network
        // concern (NOTE: client/net) handled where the vehicle stats are transmitted.

        /// <summary>QC <c>.vehicle_energy</c> — boost/weapon energy pool (real value on the vehicle).</summary>
        public float VehicleEnergy;

        /// <summary>QC <c>.vehicle_shield</c> — regenerating shield that absorbs damage before health.</summary>
        public float VehicleShield;

        /// <summary>QC <c>.vehicle_ammo1</c> — primary weapon ammo/heat pool (real value on the vehicle).</summary>
        public float VehicleAmmo1;

        /// <summary>QC <c>.vehicle_ammo2</c> — secondary weapon ammo pool.</summary>
        public float VehicleAmmo2;

        // ---- QC vehicle bookkeeping fields ----

        /// <summary>QC <c>.vehicle_flags</c> — the VHF_* capability bitfield (<see cref="Gameplay.VehicleFlags"/>).</summary>
        public Gameplay.VehicleFlags VehicleFlags;

        /// <summary>QC <c>.old_vehicle_flags</c> — backup of the flags while an enemy is "stealing" the vehicle.</summary>
        public Gameplay.VehicleFlags OldVehicleFlags;

        /// <summary>QC <c>.dmg_time</c> — last time the vehicle took damage (gates shield/health regen pause).</summary>
        public float DmgTime;

        /// <summary>QC <c>.vehicle_enter_delay</c> — players cannot enter/leave a vehicle before this time.</summary>
        public float VehicleEnterDelay;

        /// <summary>QC <c>.respawntime</c> — seconds before a destroyed vehicle returns.</summary>
        public float RespawnTime;

        /// <summary>QC <c>.pos1</c> / <c>.pos2</c> — the vehicle's spawn origin and spawn angles (return point).</summary>
        public Vector3 SpawnPos, SpawnAngles;
    }
}

namespace XonoticGodot.Common.Gameplay
{
    /// <summary>
    /// Client-render flags a vehicle entity packs into its networked <see cref="Framework.Entity.Effects"/>
    /// bitfield — the transient presentation state the CSQC vehicle visuals need but that isn't a transform
    /// (whether the primary gun is firing, whether boost is engaged). High bits, above the engine's EF_*
    /// range, so they ride the existing delta-compressed Effects field (costing nothing while unchanged) and
    /// the client (<c>VehicleVisuals</c>) reads them back to drive barrel spin / muzzle flash / boost sound.
    /// The server vehicle frame sets/clears them each tick from the pilot's input.
    /// </summary>
    public static class VehicleEffects
    {
        /// <summary>Primary gun firing this tick (spins the barrels + pops the muzzle flash).</summary>
        public const int Firing = 1 << 24;
        /// <summary>Boost/afterburner engaged (the Racer boost engine-sound overlay).</summary>
        public const int Boosting = 1 << 25;
    }

    /// <summary>
    /// Extension of the <see cref="Vehicle"/> descriptor base (declared <c>partial</c> in EntityClasses.cs).
    /// Adds the death hook QC exposes as <c>vr_death</c> (common/vehicles/vehicle.qh) without editing that
    /// file. The base already declares Spawn / Enter(vehicle,player) / Exit / Think.
    /// </summary>
    public abstract partial class Vehicle
    {
        /// <summary>(SERVER) QC <c>vr_death</c> — run when the vehicle is destroyed (death FX, corpse, respawn).</summary>
        public virtual void Death(Entity vehicle) { }
    }

    /// <summary>
    /// Registry attribute for vehicles — the C# successor to QC's <c>REGISTER_VEHICLE</c> (common/vehicles/all.qh).
    /// Derives from <see cref="GameRegistryAttribute"/> (which already carries the AttributeUsage), so the
    /// existing reflection bootstrap in <c>GameRegistries.Bootstrap</c> (Gameplay/Registries.cs) discovers and
    /// enrols each <see cref="Vehicle"/> into <see cref="Vehicles"/> with no edit to Framework/Registry.cs.
    /// Mirrors the existing <c>WeaponAttribute</c>/<c>MutatorAttribute</c> declarations.
    /// </summary>
    public sealed class VehicleAttribute : GameRegistryAttribute { }

    /// <summary>
    /// Vehicle capability flags — port of the VHF_* constants (common/vehicles/vehicle.qh). Stored in
    /// <see cref="Entity.VehicleFlags"/>. The movement-style bits (GROUND/HOVER/FLY) and the damage-shake
    /// cosmetic bits are carried for fidelity even where this headless phase doesn't act on them yet.
    /// </summary>
    [System.Flags]
    public enum VehicleFlags
    {
        None = 0,
        IsVehicle = 1 << 1,    // VHF_ISVEHICLE
        HasShield = 1 << 2,    // VHF_HASSHIELD
        ShieldRegen = 1 << 3,  // VHF_SHIELDREGEN
        HealthRegen = 1 << 4,  // VHF_HEALTHREGEN
        EnergyRegen = 1 << 5,  // VHF_ENERGYREGEN
        DeathEject = 1 << 6,   // VHF_DEATHEJECT
        MoveGround = 1 << 7,   // VHF_MOVE_GROUND
        MoveHover = 1 << 8,    // VHF_MOVE_HOVER
        MoveFly = 1 << 9,      // VHF_MOVE_FLY
        DmgShake = 1 << 10,    // VHF_DMGSHAKE
        DmgRoll = 1 << 11,     // VHF_DMGROLL
        DmgHeadRoll = 1 << 12, // VHF_DMGHEADROLL
        MultiSlot = 1 << 13,   // VHF_MULTISLOT
        PlayerSlot = 1 << 14,  // VHF_PLAYERSLOT
    }

    /// <summary>QC exit flags passed to <c>vehicles_exit</c> / <c>vr_*</c> (sv_vehicles.qh VHEF_*).</summary>
    public enum VehicleExitFlag
    {
        Normal = 0,   // VHEF_NORMAL  — user pressed the exit key
        Eject = 1,    // VHEF_EJECT   — fast triple-tap exit or the vehicle is dying
        Release = 2,  // VHEF_RELEASE — release ownership (disconnect / spectate / kill)
    }

    /// <summary>
    /// Shared, vehicle-agnostic helpers — the Godot-free core of common/vehicles/sv_vehicles.qc. Concrete
    /// vehicles call into these from their <see cref="Vehicle.Spawn"/>/<see cref="Vehicle.Enter"/>/
    /// <see cref="Vehicle.Exit"/>/<see cref="Vehicle.Think"/> overrides so the enter/exit handshake, the
    /// resource regen, the shield-then-health damage split and the shared projectile spawner live in one
    /// place (exactly as QC kept them shared in sv_vehicles.qc).
    /// </summary>
    public static class VehicleCommon
    {
        /// <summary>QC autocvar_g_vehicles_thinkrate (sv_vehicles.qh) — default vehicle think cadence.</summary>
        public const float DefaultThinkRate = 0.1f;

        private static float Time => Api.Services is not null ? Api.Clock.Time : 0f;

        /// <summary>
        /// QC global <c>game_stopped</c> — set true by the match loop while the round is frozen (warmup over /
        /// match ended / intermission), parking every vehicle. The host (MatchController / RoundHandler) drives
        /// this; <see cref="FreezeIfGameStopped"/> reads it. Defaults false (the round is live).
        /// </summary>
        public static bool GameStopped { get; set; }

        // =====================================================================================
        // ENTER  — port of vehicles_enter() (sv_vehicles.qc ~931): attach the player to the vehicle.
        // =====================================================================================

        /// <summary>
        /// The shared half of <c>vehicles_enter()</c>: bind <paramref name="player"/> to <paramref name="vehic"/>,
        /// freeze the player's own physics (MOVETYPE_NONE / SOLID_NOT / no self-damage) and snapshot its team.
        /// The concrete vehicle's <see cref="Vehicle.Enter"/> override calls this first, then applies the
        /// per-vehicle setup (movetype, takeoff, HUD seeding).
        ///
        /// NOTE — cross-boundary (client/net/bot/mutator): CSQCVehicleSetup view-port/HUD networking, weapon-slot
        /// swap to the vehicle's temp weapon entities, jetpack/hook removal, steal-waypoint,
        /// MUTATOR_CALLHOOK(VehicleEnter), and the bot-driver guard.
        /// </summary>
        public static void EnterVehicle(Entity vehic, Entity player)
        {
            // QC: veh.owner = pl; pl.vehicle = veh;
            vehic.Owner = player;
            player.Vehicle = vehic;

            // The vehicle now stops self-thinking; the player's PlayerPhysplug drives it each frame.
            vehic.NextThink = 0f;

            // QC: pl.takedamage = DAMAGE_NO; pl.solid = SOLID_NOT; MOVETYPE_NOCLIP; alpha = -1; view_ofs = 0.
            // The spec asks for the player's movetype set to NONE while seated — the player is now a passenger
            // whose origin is rewritten from the vehicle each frame, so it must not integrate its own physics.
            player.TakeDamage = DamageMode.No;
            player.Solid = Solid.Not;
            player.MoveType = MoveType.None;
            player.Velocity = Vector3.Zero;
            player.ViewOfs = Vector3.Zero;
            player.Flags &= ~EntFlags.OnGround;
            player.Angles = vehic.Angles;

            // QC: veh.team = pl.team; veh.flags -= FL_NOTARGET; (vehicle becomes shootable as the player's team)
            vehic.Team = player.Team;
            vehic.Flags &= ~EntFlags.OnGround;

            // QC seeds the per-weapon ammo/energy mirror to 0 on entry.
            vehic.VehicleAmmo1 = 0f;
            vehic.VehicleAmmo2 = 0f;
            vehic.VehicleEnergy = 0f;

            // NOTE — cross-boundary (other systems/agents): the rest of vehicles_enter is CSQCVehicleSetup
            // viewport/HUD networking (net), the weaponentities[] temp_wepent swap (weapon view-entities), the
            // steal waypoint (CSQC), MUTATOR_CALLHOOK(VehicleEnter) (no VehicleEnter hook in MutatorHooks yet),
            // antilag_clear (antilag system), and the bot-driver gate (bot AI). The server-authoritative core
            // — ownership link, solidity/movetype/damage state, team, ammo-mirror reset — is done above.
        }

        // =====================================================================================
        // EXIT  — port of vehicles_exit() (sv_vehicles.qc ~775): detach + eject the player.
        // =====================================================================================

        /// <summary>
        /// The shared half of <c>vehicles_exit()</c>: restore the ejected <paramref name="player"/> to a normal
        /// walking entity (MOVETYPE_WALK / SOLID_SLIDEBOX / takes damage again), drop the vehicle↔player link,
        /// and re-flag the (living) vehicle as untargetable until it is re-entered. The concrete vehicle's
        /// <see cref="Vehicle.Exit"/> override computes the ejection origin/velocity and then calls this.
        ///
        /// NOTE — cross-boundary (client/net/mutator): SVC_SETVIEWPORT/SETVIEWANGLES + CSQCVehicleSetup
        /// networking, weapon slot restore, MUTATOR_CALLHOOK(VehicleExit), notifications, return-helper/waypoint spawning.
        /// </summary>
        public static void ExitVehicle(Entity vehic, Entity player, VehicleExitFlag eject)
        {
            if (player is not null)
            {
                // QC: player.takedamage = DAMAGE_AIM; solid = SOLID_SLIDEBOX; MOVETYPE_WALK; can be hurt again.
                player.TakeDamage = DamageMode.Aim;
                player.Solid = Solid.SlideBox;
                player.MoveType = MoveType.Walk;
                player.Vehicle = null;
                player.VehicleEnterDelay = Time + 2f; // QC: prevent instant re-entry

                // NOTE — cross-boundary (other systems/agents): the rest of vehicles_exit is SVC_SETVIEWPORT/
                // SETVIEWANGLES + CSQCVehicleSetup (net), the weaponentities[] switch-weapon restore (weapon
                // view-entities), the view_ofs = STAT(PL_VIEW_OFS) restore + setsize(PL_MIN/PL_MAX) resize (both
                // owned by the player-physics layer, which sets PL_VIEW_OFS from sv_player_viewoffset and owns
                // the crouch hull), and MUTATOR_CALLHOOK(VehicleExit) (no VehicleExit hook in MutatorHooks yet).
                // The server core — damage/solid/movetype restore, link drop, re-entry delay — is done above.
            }

            // QC: vehic.flags |= FL_NOTARGET; (no longer a valid target with no pilot). We approximate
            // FL_NOTARGET with NoTarget; living vehicles also stop spinning.
            vehic.Flags |= EntFlags.NoTarget;
            if (!IsDead(vehic))
                vehic.AVelocity = Vector3.Zero;

            vehic.Team = vehic.TurHead?.Team ?? 0f;

            // QC: restore the shield-regen flag that "steal" temporarily stripped.
            if ((vehic.OldVehicleFlags & VehicleFlags.ShieldRegen) != 0)
                vehic.VehicleFlags |= VehicleFlags.ShieldRegen;
            vehic.OldVehicleFlags = VehicleFlags.None;

            vehic.Owner = null;
        }

        /// <summary>
        /// Port of <c>vehicles_findgoodexit()</c> (sv_vehicles.qc ~751): pick a clear spot to drop the pilot.
        /// Tries the preferred spot, then samples points on a ring around the vehicle until a player-sized box
        /// fits. The concrete vehicle's exit code uses this so a pilot never spawns inside a wall.
        /// </summary>
        public static Vector3 FindGoodExit(Entity vehic, Entity player, Vector3 preferSpot)
        {
            Vector3 plMin = player.Mins == Vector3.Zero ? new Vector3(-16f, -16f, -24f) : player.Mins;
            Vector3 plMax = player.Maxs == Vector3.Zero ? new Vector3(16f, 16f, 45f) : player.Maxs;

            if (Api.Services is null)
                return preferSpot;

            TraceResult tr = Api.Trace.Trace(vehic.Origin + new Vector3(0, 0, 32f), plMin, plMax, preferSpot, MoveFilter.Normal, player);
            if (tr.Fraction == 1f && !tr.StartSolid && !tr.AllSolid)
                return preferSpot;

            float mySize = 1.5f * QMath.VLen(vehic.Maxs - vehic.Mins);
            Vector3 center = 0.5f * (vehic.AbsMin + vehic.AbsMax);
            // QC vehicles_findgoodexit: autocvar_g_vehicles_exit_attempts (default 25) tries of
            // `v = normalize(randomvec() with z=0) * mysize` around the vehicle centre, via the deterministic
            // shared PRNG (ADR-0010: server seeds + broadcasts so the prediction reproduces the same draws).
            int attempts = (int)Cvar("g_vehicles_exit_attempts", 25f);
            for (int i = 0; i < attempts; ++i)
            {
                Vector3 v = Prandom.Vec();
                v.Z = 0f;                       // QC: v.z = 0
                v = center + QMath.Normalize(v) * mySize;
                TraceResult t = Api.Trace.Trace(center, plMin, plMax, v, MoveFilter.Normal, player);
                if (t.Fraction == 1f && !t.StartSolid && !t.AllSolid)
                    return v;
            }
            return vehic.Origin;
        }

        // =====================================================================================
        // SPAWN  — port of vehicles_spawn() (sv_vehicles.qc ~1107): (re)place the vehicle in the world.
        // =====================================================================================

        /// <summary>
        /// The shared half of <c>vehicles_spawn()</c>: reset the vehicle entity to its idle, ownerless, shootable
        /// state at its spawn point. Concrete vehicles call this from <see cref="Vehicle.Spawn"/> before applying
        /// their per-vehicle model/movetype/health, then schedule their own think.
        /// </summary>
        public static void SpawnVehicle(Entity vehic, Vehicle info)
        {
            vehic.Owner = null;
            vehic.VehicleDef = info;
            vehic.MoveType = MoveType.Step;     // QC: MOVETYPE_STEP
            vehic.Solid = Solid.SlideBox;       // QC: SOLID_SLIDEBOX
            vehic.TakeDamage = DamageMode.Aim;  // QC: DAMAGE_AIM
            vehic.DeadState = DeadFlag.No;      // QC: DEAD_NO
            vehic.Flags |= EntFlags.NoTarget;   // QC: FL_NOTARGET until entered
            vehic.AVelocity = Vector3.Zero;
            vehic.Velocity = Vector3.Zero;
            vehic.VehicleFlags |= VehicleFlags.IsVehicle;

            // QC: return to spawn (this.angles = pos2; setorigin(this, pos1)).
            vehic.Angles = vehic.SpawnAngles;
            if (Api.Services is not null)
                Api.Entities.SetOrigin(vehic, vehic.SpawnPos);

            // NOTE — cross-boundary: EFFECT_TELEPORT (client-render), the bot-target list (bot AI), the
            // hud/viewport model reset + CSQCMODEL_AUTOINIT (client/net) and lock reset. The server spawn
            // state — owner, movetype/solid/damage, FL_NOTARGET, angles, placement at the spawn point — is set above.
        }

        // =====================================================================================
        // THINK helpers — regen (vehicles_regen / vehicles_regen_resource, sv_vehicles.qc ~549).
        // =====================================================================================

        /// <summary>
        /// Port of <c>vehicles_regen()</c> (sv_vehicles.qc): top up an arbitrary scalar pool (shield/energy)
        /// toward <paramref name="fieldMax"/> at <paramref name="regen"/> per second, but only once
        /// <paramref name="lastTouchTime"/> + <paramref name="pause"/> has elapsed. When
        /// <paramref name="healthScale"/> is set the rate is scaled by the vehicle's current health fraction.
        /// </summary>
        public static float Regen(Entity vehic, float current, float lastTouchTime, float fieldMax,
            float pause, float regen, float dt, bool healthScale)
        {
            if (current < fieldMax && lastTouchTime + pause < Time)
            {
                if (healthScale && vehic.MaxHealth > 0f)
                    regen *= vehic.GetResource(ResourceType.Health) / vehic.MaxHealth;
                current = MathF.Min(current + regen * dt, fieldMax);
            }
            return current;
        }

        /// <summary>
        /// Port of <c>vehicles_regen_resource()</c> (sv_vehicles.qc): the <see cref="Regen"/> variant that tops
        /// up a real <see cref="ResourceType"/> (i.e. RES_HEALTH) on the vehicle instead of a scalar pool.
        /// </summary>
        public static void RegenResource(Entity vehic, float lastTouchTime, float fieldMax,
            float pause, float regen, float dt, bool healthScale, ResourceType resource)
        {
            float amount = vehic.GetResource(resource);
            if (amount < fieldMax && lastTouchTime + pause < Time)
            {
                if (healthScale && vehic.MaxHealth > 0f)
                    regen *= amount / vehic.MaxHealth;
                vehic.SetResource(resource, MathF.Min(amount + regen * dt, fieldMax));
            }
        }

        // =====================================================================================
        // DAMAGE — port of vehicles_damage() (sv_vehicles.qc ~625): shield-then-health, death eject.
        // =====================================================================================

        /// <summary>
        /// Port of the core of <c>vehicles_damage()</c> (sv_vehicles.qc): route incoming damage through the
        /// vehicle's shield first (if it has one and any is left), spilling the remainder into health, then
        /// apply knockback (scaled by the vehicle's <c>.damageforcescale</c>) and — on death — eject/release the
        /// pilot and let the descriptor run its death FX + respawn. The per-weapon vehicle damage-rate table is
        /// applied here (<see cref="VehicleDamageRate"/>).
        ///
        /// Deferred vs QC (client-only): the shield-hit cosmetic entity + SND_ONS_* hit sounds, antilag.
        /// </summary>
        public static void DamageVehicle(Entity vehic, Entity? inflictor, Entity? attacker,
            float damage, string deathType, Vector3 hitLoc, Vector3 force)
        {
            if (vehic.TakeDamage == DamageMode.No || IsDead(vehic))
                return;

            vehic.DmgTime = Time;
            vehic.Enemy = attacker;

            // Per-weapon vehicle damage-rate (QC vehicles_damage WEAPONTODO): some weapons hit vehicles for a
            // fraction (vortex/machinegun/rifle/vaporizer at <1) and some for a multiple (any other weapon at
            // 2x, the tag/seeker at 5x), so a vehicle "feels" different against each weapon. Keyed off the
            // weapon NetName the deathtype carries (DeathTypes.FromWeapon).
            damage *= VehicleDamageRate(deathType);

            if ((vehic.VehicleFlags & VehicleFlags.HasShield) != 0 && vehic.VehicleShield > 0f)
            {
                vehic.VehicleShield -= damage;
                if (vehic.VehicleShield < 0f)
                {
                    // Shield depleted: spill the overflow into health (QC TakeResource(RES_HEALTH, |shield|)),
                    // and play the heavy hit (SND_ONS_HIT2) at CH_PAIN, VOL_BASE, ATTEN_NORM.
                    vehic.TakeResource(ResourceType.Health, MathF.Abs(vehic.VehicleShield));
                    vehic.VehicleShield = 0f;
                    if (Api.Services is not null)
                        Api.Sound.Play(vehic, SoundChannel.Body, "onslaught/ons_hit2.wav", 0.7f, 0.5f);
                    // NOTE (client-render): the vehicle_shieldent colormod+alpha hit flash is a CSQC visual.
                }
                else
                {
                    // Shield absorbed it: the electricity sparkle (SND_ONS_ELECTRICITY_EXPLODE), VOL_BASE, ATTEN_NORM.
                    if (Api.Services is not null)
                        Api.Sound.Play(vehic, SoundChannel.Body, "onslaught/electricity_explode.wav", 0.7f, 0.5f);
                }
            }
            else
            {
                vehic.TakeResource(ResourceType.Health, damage);
                // QC vehicles_damage: spamsound(this, CH_PAIN, SND_ONS_HIT2, VOL_BASE, ATTEN_NORM) on a raw hit.
                if (Api.Services is not null)
                    Api.Sound.Play(vehic, SoundChannel.Body, "onslaught/ons_hit2.wav", 0.7f, 0.5f);
            }

            // QC applies knockback scaled by .damageforcescale (set per vehicle in vr_spawn), else raw force.
            float fscale = vehic.DamageForceScale;
            vehic.Velocity += (fscale > 0f && fscale < 1f) ? force * fscale : force;
            _ = hitLoc;

            if (vehic.GetResource(ResourceType.Health) <= 0f)
            {
                // Mark dying BEFORE ejecting so the descriptor's Exit() takes its death-eject branch (QC passes
                // VHEF_EJECT to vehicles_exit before vr_death; the Exit override keys off IsDead here).
                if (vehic.DeadState == DeadFlag.No)
                    vehic.DeadState = DeadFlag.Dying;

                Entity? pilot = vehic.Owner;
                if (pilot is not null)
                    vehic.VehicleDef?.Exit(vehic, pilot); // descriptor computes eject vector + calls ExitVehicle

                vehic.VehicleDef?.Death(vehic);
                // NOTE — cross-boundary: antilag_clear (antilag system) and the vehicles_setreturn respawn
                // WAYPOINT (CSQC) are all that remain of vehicles_damage's death path; the respawn itself is
                // already scheduled by the descriptor's Death() above.
            }
        }

        /// <summary>
        /// Port of the per-weapon vehicle damage-rate table at the top of <c>vehicles_damage()</c>
        /// (sv_vehicles.qh autocvar_g_vehicles_*_damagerate). Returns the multiplier applied to incoming
        /// damage based on the attacking weapon (carried by <paramref name="deathType"/>). Non-weapon damage
        /// (falls, blasts, drowning) is unscaled (1).
        /// </summary>
        public static float VehicleDamageRate(string deathType)
        {
            string wep = Damage.DeathTypes.WeaponNetNameOf(deathType);
            if (wep.Length == 0)
                return 1f; // non-weapon (environment / vehicle blast) damage is unscaled

            // Defaults mirror sv_vehicles.qh; read the cvar by name so a server config can retune them.
            return wep switch
            {
                "vortex"    => Cvar("g_vehicles_vortex_damagerate", 0.75f),
                "machinegun" => Cvar("g_vehicles_machinegun_damagerate", 0.75f),
                "rifle"     => Cvar("g_vehicles_rifle_damagerate", 0.75f),
                "vaporizer" => Cvar("g_vehicles_vaporizer_damagerate", 0.5f),
                "seeker"    => Cvar("g_vehicles_tag_damagerate", 5f),     // WEP_SEEKER == the tag weapon
                _           => Cvar("g_vehicles_weapon_damagerate", 2f),  // every other weapon hits 2x
            };
        }

        private static float Cvar(string name, float fallback)
        {
            if (Api.Services is null) return fallback;
            float v = Api.Cvars.GetFloat(name);
            return v != 0f ? v : fallback;
        }

        // =====================================================================================
        // PROJECTILE — port of vehicles_projectile() (sv_vehicles.qc ~221): the shared vehicle bolt/rocket.
        // =====================================================================================

        /// <summary>
        /// Port of <c>vehicles_projectile()</c> (sv_vehicles.qc): spawn the generic vehicle projectile used by
        /// every vehicle weapon (racer laser/rocket, raptor/bumblebee cannon, spiderbot rocket). It flies as a
        /// MOVETYPE_FLYMISSILE bbox owned by the vehicle but credited to the pilot, explodes on touch via
        /// <see cref="WeaponSplash.RadiusDamage"/>, and self-removes after its lifetime.
        ///
        /// <paramref name="health"/> &gt; 0 makes the projectile shootable (QC DAMAGE_AIM). The CSQCProjectile
        /// visual networking and muzzle effect are out of scope (NOTE: client/net).
        /// </summary>
        public static Entity SpawnProjectile(Entity owner, Entity pilot, Vector3 origin, Vector3 velocity,
            float damage, float radius, float force, float size, string deathType, int registryId,
            float health, float lifetime, string? fireSound = null)
        {
            Entity proj = Api.Entities.Spawn();
            proj.ClassName = "vehicles_projectile";
            proj.Owner = owner;       // QC proj.owner = this (the vehicle/turret)
            proj.DmgInflictor = pilot; // QC proj.realowner = the crediting pilot
            proj.MoveType = MoveType.FlyMissile;
            proj.Solid = Solid.BBox;
            proj.Flags |= EntFlags.Item; // QC FL_PROJECTILE
            proj.Velocity = velocity;
            proj.Angles = QMath.VecToAngles(velocity);

            float s = MathF.Max(size, 1f);
            Api.Entities.SetSize(proj, new Vector3(-s, -s, -s), new Vector3(s, s, s));
            Api.Entities.SetOrigin(proj, origin);

            if (health > 0f)
            {
                proj.TakeDamage = DamageMode.Aim;
                proj.Health = health;
            }
            else
            {
                proj.Flags |= EntFlags.NoTarget;
            }

            void Explode(Entity self)
            {
                self.Touch = null;
                self.Think = null;
                self.TakeDamage = DamageMode.No;
                WeaponSplash.RadiusDamage(self, self.Origin, damage, 0f, radius, self.DmgInflictor, registryId, force);
                Api.Entities.Remove(self);
            }

            proj.Touch = (self, _) => Explode(self);
            proj.Think = Explode;
            proj.NextThink = Time + (lifetime > 0f ? lifetime : 30f); // QC default nextthink = time + 30

            if (fireSound is not null)
                Api.Sound.Play(owner, SoundChannel.Weapon, fireSound);

            // NOTE — cross-boundary: CSQCProjectile networking + muzzle EFFECT_* (client/net), the bot-dodge
            // list (bot AI), and the owner==owner friendly-projectile pass-through (a RadiusDamage owner-filter
            // detail in the damage system). The server projectile — movetype, ownership, splash, lifetime,
            // fire sound — is wired above.
            return proj;
        }

        /// <summary>QC IS_DEAD(e).</summary>
        public static bool IsDead(Entity e) => e.DeadState != DeadFlag.No;

        /// <summary>
        /// QC <c>game_stopped</c> freeze used at the top of every <c>*_frame</c>: a stopped round parks the
        /// vehicle (SOLID_NOT / DAMAGE_NO / MOVETYPE_NONE). Concrete <see cref="Vehicle.Think"/> overrides call
        /// this and bail when it returns true.
        /// </summary>
        public static bool FreezeIfGameStopped(Entity vehic)
        {
            // Read the host-driven flag (set by the match loop), falling back to the cvar mirror so the gate
            // works even before a host wires GameStopped (QC keeps game_stopped as a plain global).
            bool stopped = GameStopped || (Api.Services is not null && Api.Cvars.GetFloat("g_game_stopped") != 0f);
            if (!stopped)
                return false;

            // QC racer_frame/raptor_frame/etc.: park the vehicle while the round is frozen.
            vehic.Solid = Solid.Not;
            vehic.TakeDamage = DamageMode.No;
            vehic.MoveType = MoveType.None;
            return true;
        }
    }
}
