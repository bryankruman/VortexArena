using System.Numerics;
using XonoticGodot.Common.Framework;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Additional gameplay hook chains used by the ported mutators — the C# successor to the QuakeC
/// MUTATOR_HOOKABLE events declared in server/mutators/events.qh and common/mutators/events.qh.
///
/// <see cref="GameHooks"/> already owns the sample chain (PlayerDamage_SplitHealthArmor) used by the
/// vampire mutator. This file adds the chains the rest of the batch needs, each a typed
/// <see cref="HookChain{TArgs}"/> whose <c>ref</c> args struct replaces QC's global M_ARGV(i, ...)
/// in/out slots (ADR-0003, specs/entity-model.md). Field naming follows the QC slot comments so the
/// port stays auditable.
///
/// NOTE: handlers return <c>bool</c>. For "forbid"/"return-true" QC hooks (ForbidThrowCurrentWeapon,
/// PlayerRegen, PlayerJump...) a <c>true</c> return models QC's <c>return true</c> (the hook bus ORs
/// returns together, mirroring CALLHOOK's "any handler returned true" semantics).
/// </summary>
public static class MutatorHooks
{
    // ----------------------------------------------------------------------------------------------
    // Lifecycle / per-frame
    // ----------------------------------------------------------------------------------------------

    /// <summary>EV_PlayerSpawn — player spawned as a player (after shared setup). Slot0 player, slot1 spawn spot.</summary>
    public struct PlayerSpawnArgs
    {
        public readonly Entity Player;   // MUTATOR_ARGV_0_entity
        public readonly Entity? Spot;    // MUTATOR_ARGV_1_entity (may be null)
        public PlayerSpawnArgs(Entity player, Entity? spot) { Player = player; Spot = spot; }
    }
    public static readonly HookChain<PlayerSpawnArgs> PlayerSpawn = new();

    /// <summary>
    /// EV_Spawn_Score (server/mutators/events.qh) — fired from QC <c>Spawn_Score</c> (server/spawnpoints.qc)
    /// for EACH candidate spawn spot while selecting a spawnpoint, so a mutator can bias the spot's priority.
    /// Slot0 the player being spawned, slot1 the spawn spot entity, slot2 the spot's spawn-score vector
    /// (in/out — QC packs the spot's prio in <c>.x</c> and the distance weight in <c>.y</c>; handlers rewrite
    /// <see cref="Priority"/>). spawn_unique drops a repeat spot's priority to 0.1; spawn_near_teammate raises
    /// the priority of spots near a teammate. The port's scorer keeps (prio, weight) as floats, so the chain
    /// exposes both — <see cref="Priority"/> is the QC <c>spawn_score.x</c>, <see cref="Weight"/> the <c>.y</c>.
    /// </summary>
    public struct SpawnScoreArgs
    {
        public readonly Entity Player;   // MUTATOR_ARGV_0_entity
        public readonly Entity Spot;     // MUTATOR_ARGV_1_entity
        public float Priority;           // MUTATOR_ARGV_2_vector.x (in/out)
        public float Weight;             // MUTATOR_ARGV_2_vector.y (in/out)
        public SpawnScoreArgs(Entity player, Entity spot, float priority, float weight)
        {
            Player = player; Spot = spot; Priority = priority; Weight = weight;
        }
    }
    public static readonly HookChain<SpawnScoreArgs> SpawnScore = new();

    /// <summary>
    /// EV_PlayerPreThink — runs each frame for every player entity (also bots/dead/spectators).
    /// QC guards on IS_PLAYER/IS_DEAD/game_stopped inside the handler.
    /// </summary>
    public struct PlayerPreThinkArgs
    {
        public readonly Entity Player;   // MUTATOR_ARGV_0_entity
        public PlayerPreThinkArgs(Entity player) { Player = player; }
    }
    public static readonly HookChain<PlayerPreThinkArgs> PlayerPreThink = new();

    /// <summary>
    /// EV_SV_StartFrame (server/mutators/events.qh) — fired once per server frame from QC <c>StartFrame()</c>
    /// (server/main.qc), after the bot/anticheat frame and before the per-client PostThink pass. The Godot-free
    /// gameplay layer can't reach the server-core <c>ServerHooks.SvStartFrame</c> (it lives in XonoticGodot.Server,
    /// which references Common, not the other way round), so per-frame mutators that live in Common — random_gravity
    /// here — subscribe to THIS chain instead. The server's per-frame loop (<c>GameWorld.StartFrame</c> /
    /// <c>ServerHooks.FireStartFrame</c>) must also pump this chain so the handlers actually tick (see the T19
    /// report's crossTaskNeeds). Slot0 the current sim time (QC <c>time</c>).
    /// </summary>
    public struct SvStartFrameArgs
    {
        public readonly float Time;   // QC time at the top of the frame
        public SvStartFrameArgs(float time) { Time = time; }
    }
    public static readonly HookChain<SvStartFrameArgs> SvStartFrame = new();

    /// <summary>Fire <see cref="SvStartFrame"/> for the given sim time (returns true if any handler did).</summary>
    public static bool FireStartFrame(float time)
    {
        var a = new SvStartFrameArgs(time);
        return SvStartFrame.Call(ref a);
    }

    /// <summary>
    /// EV_PlayerPowerups — end of player_powerups(); lets mutators tweak values set by powerup items.
    /// Slot0 player, slot1 old items bitmask.
    /// </summary>
    public struct PlayerPowerupsArgs
    {
        public readonly Entity Player;   // MUTATOR_ARGV_0_entity
        public readonly int OldItems;    // MUTATOR_ARGV_1_int
        public PlayerPowerupsArgs(Entity player, int oldItems) { Player = player; OldItems = oldItems; }
    }
    public static readonly HookChain<PlayerPowerupsArgs> PlayerPowerups = new();

    /// <summary>
    /// EV_PlayerRegen — called each think frame; a handler returning true disables regen (QC instagib).
    /// The full in/out regen-tuning slots are deferred; only the "disable" return is modeled for now.
    /// </summary>
    public struct PlayerRegenArgs
    {
        public readonly Entity Player;   // MUTATOR_ARGV_0_entity
        public PlayerRegenArgs(Entity player) { Player = player; }
    }
    public static readonly HookChain<PlayerRegenArgs> PlayerRegen = new();

    // ----------------------------------------------------------------------------------------------
    // Movement
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// EV_PlayerPhysics — before player physics, may adjust movement vars. Slot0 player, slot1 ticrate.
    /// Used by dodging / multijump to drive their per-frame movement state machines.
    /// </summary>
    public struct PlayerPhysicsArgs
    {
        public readonly Entity Player;   // MUTATOR_ARGV_0_entity
        public readonly float TicRate;   // MUTATOR_ARGV_1_float
        public PlayerPhysicsArgs(Entity player, float ticRate) { Player = player; TicRate = ticRate; }
    }
    public static readonly HookChain<PlayerPhysicsArgs> PlayerPhysics = new();

    /// <summary>
    /// EV_WeaponRateFactor (server/mutators/events.qh) — fired from QC <c>W_WeaponRateFactor</c>
    /// (common/weapons/weapon.qh) so a mutator can scale the weapon refire/animtime factor. Slot0 the factor
    /// (in/out — the handler multiplies <see cref="Factor"/> and QC reads it back via <c>M_ARGV(0, float)</c>),
    /// slot1 the firing player. The Speed powerup multiplies by
    /// <c>g_balance_powerup_speed_attack_time_multiplier</c> (0.8 → faster) and the Speed buff by
    /// <c>g_buffs_speed_rate</c>. The base factor passed in is QC's <c>1/g_weaponratefactor</c>.
    /// </summary>
    public struct WeaponRateFactorArgs
    {
        public float Factor;            // MUTATOR_ARGV_0_float (in/out)
        public readonly Entity Player;  // MUTATOR_ARGV_1_entity
        public WeaponRateFactorArgs(float factor, Entity player) { Factor = factor; Player = player; }
    }
    public static readonly HookChain<WeaponRateFactorArgs> WeaponRateFactor = new();

    /// <summary>
    /// EV_PlayerJump — player pressed jump. Slot0 player, slot1 jump height (in/out), slot2 multijump
    /// flag (in/out). Bloodloss returns true to forbid the jump; multijump/walljump set
    /// <see cref="Multijump"/> = true to grant an extra jump.
    /// </summary>
    public struct PlayerJumpArgs
    {
        public readonly Entity Player;   // MUTATOR_ARGV_0_entity
        public float JumpHeight;         // MUTATOR_ARGV_1_float (in/out)
        public bool Multijump;           // MUTATOR_ARGV_2_bool  (in/out)
        public PlayerJumpArgs(Entity player, float jumpHeight, bool multijump)
        {
            Player = player; JumpHeight = jumpHeight; Multijump = multijump;
        }
    }
    public static readonly HookChain<PlayerJumpArgs> PlayerJump = new();

    /// <summary>
    /// EV_PlayerCanCrouch — decides whether a player can crouch. Slot0 player, slot1 do_crouch (in/out).
    /// Bloodloss forces do_crouch=true when the player is below the bloodloss threshold.
    /// </summary>
    public struct PlayerCanCrouchArgs
    {
        public readonly Entity Player;   // MUTATOR_ARGV_0_entity
        public bool DoCrouch;            // MUTATOR_ARGV_1_bool (in/out)
        public PlayerCanCrouchArgs(Entity player, bool doCrouch) { Player = player; DoCrouch = doCrouch; }
    }
    public static readonly HookChain<PlayerCanCrouchArgs> PlayerCanCrouch = new();

    // Port of qcsrc/common/mutators/events.qh (EV_PM_Physics, EV_IsFlying).

    /// <summary>
    /// EV_PM_Physics (events.qh:93-98) — "called during player physics, allows adjusting the movement type
    /// used". Fired in <c>sys_phys_update</c> (ecs/systems/physics.qc:108) as the SECOND branch of the
    /// movement-type selection (right after the FL_WATERJUMP branch): a handler that returns <c>true</c>
    /// has FULLY REPLACED the move, so the rest of the branch chain (noclip/fly/swim/ladder/jetpack/ground/
    /// air) is skipped and only the post-update bookkeeping runs. This is the hook bugrigs (vehicle-style
    /// drive physics) hangs off. Slot0 player, slot1 maxspeed_mod, slot2 tick rate (dt) — all <c>in</c>,
    /// no <c>out</c> slots (the handler mutates the player's velocity/origin directly).
    /// </summary>
    public struct PMPhysicsArgs
    {
        public readonly Entity Player;     // MUTATOR_ARGV_0_entity
        public readonly float MaxspeedMod; // MUTATOR_ARGV_1_float
        public readonly float TicRate;     // MUTATOR_ARGV_2_float
        public PMPhysicsArgs(Entity player, float maxspeedMod, float ticRate)
        {
            Player = player; MaxspeedMod = maxspeedMod; TicRate = ticRate;
        }
    }
    public static readonly HookChain<PMPhysicsArgs> PMPhysics = new();

    /// <summary>
    /// EV_IsFlying (events.qh:58-61) — the LAST term of the <c>||</c> that selects the noclip/fly branch in
    /// <c>sys_phys_update</c> (physics.qc:113: <c>MOVETYPE_NOCLIP || MOVETYPE_FLY || MOVETYPE_FLY_WORLDONLY
    /// || MUTATOR_CALLHOOK(IsFlying, this)</c>). A handler returning <c>true</c> FORCES the fly branch (air-
    /// friction half-step + full-3D PM_Accelerate, no gravity). Slot0 player only, no <c>out</c> slots.
    /// NOTE: this MUTATOR_HOOKABLE(IsFlying) is DISTINCT from the plain <c>bool IsFlying(entity)</c> function
    /// (common/physics/player.qc:843, the onground/swimming/24u airshot test used for .wasFlying and by
    /// weapons) — only the hook is ported here.
    /// </summary>
    public struct IsFlyingArgs
    {
        public readonly Entity Player;   // MUTATOR_ARGV_0_entity
        public IsFlyingArgs(Entity player) { Player = player; }
    }
    public static readonly HookChain<IsFlyingArgs> IsFlying = new();

    // ----------------------------------------------------------------------------------------------
    // Damage / death
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// EV_Damage_Calculate — adjusts the damage/force/mirror-damage applied to a target (QC the place
    /// strength multipliers, instagib lives, midair multipliers, overkill blaster nullification etc.
    /// live). All of <see cref="Damage"/>, <see cref="MirrorDamage"/>, <see cref="Force"/> are in/out.
    /// </summary>
    public struct DamageCalculateArgs
    {
        public readonly Entity? Inflictor;   // MUTATOR_ARGV_0_entity
        public readonly Entity? Attacker;    // MUTATOR_ARGV_1_entity
        public readonly Entity Target;       // MUTATOR_ARGV_2_entity
        public readonly string DeathType;    // MUTATOR_ARGV_3_float (deathtype tag — string in this port)
        public float Damage;                 // MUTATOR_ARGV_4_float (in/out)
        public float MirrorDamage;           // MUTATOR_ARGV_5_float (in/out)
        public Vector3 Force;                // MUTATOR_ARGV_6_vector (in/out)
        public readonly Entity? WeaponEntity; // MUTATOR_ARGV_7_entity

        public DamageCalculateArgs(Entity? inflictor, Entity? attacker, Entity target, string deathType,
            float damage, float mirrorDamage, Vector3 force, Entity? weaponEntity)
        {
            Inflictor = inflictor;
            Attacker = attacker;
            Target = target;
            DeathType = deathType;
            Damage = damage;
            MirrorDamage = mirrorDamage;
            Force = force;
            WeaponEntity = weaponEntity;
        }
    }
    public static readonly HookChain<DamageCalculateArgs> DamageCalculate = new();

    /// <summary>
    /// EV_PlayerDies — a player died; lets mutators drop carried stuff / force gibbing. Slot0 inflictor,
    /// slot1 attacker, slot2 target, slot3 deathtype, slot4 damage (in/out — instagib bumps it to 1000
    /// to always gib on a vaporizer kill). Pinata/overkill throw weapons here.
    /// </summary>
    public struct PlayerDiesArgs
    {
        public readonly Entity? Inflictor;  // MUTATOR_ARGV_0_entity
        public readonly Entity? Attacker;   // MUTATOR_ARGV_1_entity
        public readonly Entity Target;      // MUTATOR_ARGV_2_entity
        public readonly string DeathType;   // MUTATOR_ARGV_3_float (deathtype tag — string in this port)
        public float Damage;                // MUTATOR_ARGV_4_float (in/out)

        public PlayerDiesArgs(Entity? inflictor, Entity? attacker, Entity target, string deathType, float damage)
        {
            Inflictor = inflictor;
            Attacker = attacker;
            Target = target;
            DeathType = deathType;
            Damage = damage;
        }
    }
    public static readonly HookChain<PlayerDiesArgs> PlayerDies = new();

    /// <summary>
    /// EV_GiveFragsForKill (server/mutators/events.qh) — fired from QC <c>GiveFrags</c> (server/damage.qc:72)
    /// when "self" fragged someone, so a mutator can change the frag score awarded for the kill. Slot0
    /// attacker, slot1 target, slot2 frag score (in/out — the handler rewrites <see cref="FragScore"/> and QC
    /// reads it back via <c>f = M_ARGV(2, float)</c>), slot3 deathtype, slot4 the attacker's weapon entity.
    /// weaponarena_random/instagib adjust the score here.
    /// </summary>
    public struct GiveFragsForKillArgs
    {
        public readonly Entity Attacker;      // MUTATOR_ARGV_0_entity
        public readonly Entity Target;        // MUTATOR_ARGV_1_entity
        public float FragScore;               // MUTATOR_ARGV_2_float (in/out)
        public readonly string DeathType;     // MUTATOR_ARGV_3_float (deathtype tag — string in this port)
        public readonly Entity? WeaponEntity; // MUTATOR_ARGV_4_entity

        public GiveFragsForKillArgs(Entity attacker, Entity target, float fragScore, string deathType, Entity? weaponEntity)
        {
            Attacker = attacker;
            Target = target;
            FragScore = fragScore;
            DeathType = deathType;
            WeaponEntity = weaponEntity;
        }
    }
    public static readonly HookChain<GiveFragsForKillArgs> GiveFragsForKill = new();

    // ----------------------------------------------------------------------------------------------
    // Items / loadout / arena
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// EV_SetStartItems (no QC args) — adjusts the {warmup_}start_* loadout globals. Modeled as a
    /// mutable <see cref="StartLoadout"/> the handlers write (the C# successor to QC's start_weapons /
    /// start_ammo_* / start_health globals).
    /// </summary>
    public struct SetStartItemsArgs
    {
        public StartLoadout Loadout;
        public SetStartItemsArgs(StartLoadout loadout) { Loadout = loadout; }
    }
    public static readonly HookChain<SetStartItemsArgs> SetStartItems = new();

    /// <summary>
    /// EV_SetWeaponArena — string in/out. Mutators set it to "off" to disable any configured weapon
    /// arena (instagib/overkill/melee/nix all do this).
    /// </summary>
    public struct SetWeaponArenaArgs
    {
        public string Arena;   // MUTATOR_ARGV_0_string (in/out)
        public SetWeaponArenaArgs(string arena) { Arena = arena; }
    }
    public static readonly HookChain<SetWeaponArenaArgs> SetWeaponArena = new();

    /// <summary>
    /// EV_ForbidRandomStartWeapons — return true to forbid giving random start weapons (slot0 player).
    /// </summary>
    public struct ForbidRandomStartWeaponsArgs
    {
        public readonly Entity Player;   // MUTATOR_ARGV_0_entity
        public ForbidRandomStartWeaponsArgs(Entity player) { Player = player; }
    }
    public static readonly HookChain<ForbidRandomStartWeaponsArgs> ForbidRandomStartWeapons = new();

    /// <summary>
    /// EV_ForbidThrowCurrentWeapon — return true to forbid dropping the current weapon. Slot0 player,
    /// slot1 weapon entity (deferred). Many arena-style mutators forbid throwing.
    /// </summary>
    public struct ForbidThrowCurrentWeaponArgs
    {
        public readonly Entity Player;   // MUTATOR_ARGV_0_entity
        public ForbidThrowCurrentWeaponArgs(Entity player) { Player = player; }
    }
    public static readonly HookChain<ForbidThrowCurrentWeaponArgs> ForbidThrowCurrentWeapon = new();

    /// <summary>
    /// EV_FilterItemDefinition — return true to disallow items of the given definition from spawning.
    /// Slot0 the item definition entity (modeled loosely by ClassName / NetName until the item registry
    /// lands). NIX/melee filter health+armor here.
    /// </summary>
    public struct FilterItemDefinitionArgs
    {
        public readonly Entity Definition;   // MUTATOR_ARGV_0_entity
        public FilterItemDefinitionArgs(Entity definition) { Definition = definition; }
    }
    public static readonly HookChain<FilterItemDefinitionArgs> FilterItemDefinition = new();

    /// <summary>
    /// EV_EditProjectile — lets mutators edit a just-fired projectile. Slot0 owner, slot1 projectile.
    /// (invincibleproj zeroes the projectile's health; rocketflying clears detonate delays.)
    /// </summary>
    public struct EditProjectileArgs
    {
        public readonly Entity? Owner;       // MUTATOR_ARGV_0_entity
        public readonly Entity Projectile;   // MUTATOR_ARGV_1_entity
        public EditProjectileArgs(Entity? owner, Entity projectile) { Owner = owner; Projectile = projectile; }
    }
    public static readonly HookChain<EditProjectileArgs> EditProjectile = new();

    /// <summary>
    /// EV_GrappleHookThink (server/mutators/events.qh) — fired each think of an in-flight/latched grappling
    /// hook chain from QC <c>GrapplingHookThink</c> (server/hook.qc). Slot0 the hook entity (its
    /// <c>.realowner</c> is the firing player; QC's tarzan variant also tracks a hooked-player <c>.aiment</c>).
    /// vampirehook subscribes here to drain health from the hooked player each damagerate. NOTE: the port's
    /// grapple latches onto GEOMETRY (no hooked-player aiment), so the vampirehook drain is a documented
    /// partial until the hooked-player ("tarzan") mechanic exists.
    /// </summary>
    public struct GrappleHookThinkArgs
    {
        public readonly Entity Hook;   // MUTATOR_ARGV_0_entity (the grappling hook entity)
        public GrappleHookThinkArgs(Entity hook) { Hook = hook; }
    }
    public static readonly HookChain<GrappleHookThinkArgs> GrappleHookThink = new();

    // ----------------------------------------------------------------------------------------------
    // Presentation
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// EV_SetDefaultAlpha (no QC args) — sets the default player/weapon alpha. Modeled as mutable fields
    /// the cloaked mutator writes. A handler returning true mirrors QC's "I set the alpha" return.
    /// </summary>
    public struct SetDefaultAlphaArgs
    {
        public float PlayerAlpha;   // QC default_player_alpha
        public float WeaponAlpha;   // QC default_weapon_alpha
        public SetDefaultAlphaArgs(float playerAlpha, float weaponAlpha)
        {
            PlayerAlpha = playerAlpha; WeaponAlpha = weaponAlpha;
        }
    }
    public static readonly HookChain<SetDefaultAlphaArgs> SetDefaultAlpha = new();

    // ----------------------------------------------------------------------------------------------
    // Vehicles (common/vehicles/sv_vehicles.qc MUTATOR_CALLHOOK) — added by the Wave-A2 orchestrator
    // as the stable interface T37 (vehicle seam) fires; no stock mutator subscribes them yet.
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// EV_VehicleInit (sv_vehicles.qc:1283) — fired at the end of <c>vehicle_initialize</c>; a handler
    /// returning <c>true</c> ABORTS init (QC: <c>if (MUTATOR_CALLHOOK(VehicleInit, this)) return false;</c>).
    /// Slot0 the vehicle entity.
    /// </summary>
    public struct VehicleInitArgs
    {
        public readonly Entity Vehicle;   // MUTATOR_ARGV_0_entity
        public VehicleInitArgs(Entity vehicle) { Vehicle = vehicle; }
    }
    public static readonly HookChain<VehicleInitArgs> VehicleInit = new();

    /// <summary>
    /// EV_VehicleEnter (sv_vehicles.qc:1072) — a player boarded a vehicle. Slot0 player, slot1 vehicle.
    /// Notify-style (the QC return is unused at this call site).
    /// </summary>
    public struct VehicleEnterArgs
    {
        public readonly Entity Player;    // MUTATOR_ARGV_0_entity
        public readonly Entity Vehicle;   // MUTATOR_ARGV_1_entity
        public VehicleEnterArgs(Entity player, Entity vehicle) { Player = player; Vehicle = vehicle; }
    }
    public static readonly HookChain<VehicleEnterArgs> VehicleEnter = new();

    /// <summary>
    /// EV_VehicleExit (sv_vehicles.qc:848) — a player left a vehicle. Slot0 player (may be null on a
    /// death-eject), slot1 vehicle.
    /// </summary>
    public struct VehicleExitArgs
    {
        public readonly Entity? Player;   // MUTATOR_ARGV_0_entity
        public readonly Entity Vehicle;   // MUTATOR_ARGV_1_entity
        public VehicleExitArgs(Entity? player, Entity vehicle) { Player = player; Vehicle = vehicle; }
    }
    public static readonly HookChain<VehicleExitArgs> VehicleExit = new();

    /// <summary>
    /// EV_VehicleTouch (sv_vehicles.qc:876) — a vehicle touched an entity; a handler returning <c>true</c>
    /// SUPPRESSES the touch (QC: <c>if (MUTATOR_CALLHOOK(VehicleTouch, this, toucher)) return;</c>).
    /// Slot0 vehicle, slot1 toucher.
    /// </summary>
    public struct VehicleTouchArgs
    {
        public readonly Entity Vehicle;   // MUTATOR_ARGV_0_entity
        public readonly Entity Toucher;   // MUTATOR_ARGV_1_entity
        public VehicleTouchArgs(Entity vehicle, Entity toucher) { Vehicle = vehicle; Toucher = toucher; }
    }
    public static readonly HookChain<VehicleTouchArgs> VehicleTouch = new();

    // ----------------------------------------------------------------------------------------------
    // PlayerDamaged (server/mutators/events.qh:478 EV_PlayerDamaged) — added by the Wave-A2 orchestrator
    // as the stable interface for damagetext (T51). Fired from DamageSystem.PlayerDamage AFTER the
    // health/armor subtract, carrying the ACTUAL amounts removed (dh/da) + the pre-split potential damage.
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// EV_PlayerDamaged — fired on every damage tick once health/armor have been applied. Slot0 attacker
    /// (may be null), slot1 target, slot2 <see cref="Health"/> = health actually removed (QC <c>dh =
    /// initial_health - max(GetResource(this,HEALTH),0)</c>), slot3 <see cref="Armor"/> = armor actually
    /// removed (<c>da</c>), slot4 hit location, slot5 deathtype, slot6 <see cref="PotentialDamage"/> = the
    /// pre-split potential damage. QC returns "forbid logging" (a stats concern the port ignores). damagetext
    /// reads these to build the floating number.
    /// </summary>
    public struct PlayerDamagedArgs
    {
        public readonly Entity? Attacker;       // MUTATOR_ARGV_0_entity
        public readonly Entity Target;          // MUTATOR_ARGV_1_entity
        public readonly float Health;           // MUTATOR_ARGV_2_float (dh — health removed)
        public readonly float Armor;            // MUTATOR_ARGV_3_float (da — armor removed)
        public readonly Vector3 HitLocation;    // MUTATOR_ARGV_4_vector
        public readonly string DeathType;       // MUTATOR_ARGV_5_int (deathtype tag — string in this port)
        public readonly float PotentialDamage;  // MUTATOR_ARGV_6_float

        public PlayerDamagedArgs(Entity? attacker, Entity target, float health, float armor,
            Vector3 hitLocation, string deathType, float potentialDamage)
        {
            Attacker = attacker;
            Target = target;
            Health = health;
            Armor = armor;
            HitLocation = hitLocation;
            DeathType = deathType;
            PotentialDamage = potentialDamage;
        }
    }
    public static readonly HookChain<PlayerDamagedArgs> PlayerDamaged = new();
}

/// <summary>
/// The mutable start-of-life loadout — the C# stand-in for QuakeC's start_health / start_armorvalue /
/// start_weapons / start_ammo_* globals (and their warmup_* twins) that SetStartItems hooks mutate
/// (server/world.qc, common/items). This is the spawn-config layer: weapons are tracked as a set of
/// weapon NetNames (readable, registry-order-independent); the arena mutators additionally apply the
/// concrete owned-weapon <see cref="WepSet"/> per spawn through <see cref="Inventory"/> in their PlayerSpawn
/// hooks. Warmup mirrors the live values (StartLoadout has no separate warmup_* twins).
/// </summary>
public sealed class StartLoadout
{
    public float Health = 100f;
    public float Armor = 0f;

    public float AmmoShells;
    public float AmmoBullets;   // QC start_ammo_nails
    public float AmmoRockets;
    public float AmmoCells;
    public float AmmoFuel;

    /// <summary>Weapon NetNames granted at spawn (QC start_weapons bitset). e.g. "vaporizer", "shotgun".</summary>
    public readonly HashSet<string> Weapons = new(StringComparer.Ordinal);

    /// <summary>QC start_items bitmask flags (IT_UNLIMITED_AMMO etc.) collected as string tags for now.</summary>
    public readonly HashSet<string> ItemFlags = new(StringComparer.Ordinal);

    public void SetWeapons(params string[] netNames)
    {
        Weapons.Clear();
        foreach (string n in netNames) Weapons.Add(n);
    }
}
