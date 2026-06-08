// Port: the per-edict vehicle bookkeeping fields QuakeC scattered across the flat field namespace and
// reused inside the *_frame PlayerPhysplug functions (common/vehicles/vehicle/*.qc) plus sv_vehicles.qc.
//
// VehicleCommon.cs (a NEW file, same technique) already promoted the headline vehicle fields
// (.vehicle / .vehicle_energy / .vehicle_shield / .vehicle_flags / .dmg_time / ...). This file adds the
// REMAINING per-entity state the deep per-frame behavior needs — the lock-on tracker, the per-weapon
// refire/counter/sound timers, the seated player's current input snapshot, the multi-seat gunner links,
// and the projectile-guidance scratch fields — WITHOUT editing VehicleCommon.cs (so the two files never
// collide). Every member is prefixed `Veh*` to stay clear of the existing names.
//
// Entity is declared partial and this is a NEW file, so extending it here is allowed by the task
// constraints (no existing file is modified). Same pattern as Gameplay/Items/EntityResources.cs.

using System.Numerics;
using XonoticGodot.Common.Physics;

namespace XonoticGodot.Common.Framework
{
    public partial class Entity
    {
        // =====================================================================================
        // Seated-player input snapshot — the C# stand-in for the QC PHYS_INPUT_* / CS(player).movement /
        // player.v_angle that every *_frame reads. The server stores the player's current move command
        // here each tick (set by the player-physics step before it hands off to the vehicle frame); the
        // vehicle frame consumes it. Defaults to "no input" so a headless/idle vehicle behaves.
        // =====================================================================================

        /// <summary>QC <c>CS(player).movement</c> + <c>v_angle</c> + <c>PHYS_INPUT_BUTTON_*</c> for the seated pilot/gunner.</summary>
        public MovementInput VehInput;

        // =====================================================================================
        // Lock-on tracker — port of the .lock_* edict fields (sv_vehicles.qh) driven by
        // vehicles_locktarget() (sv_vehicles.qc). Used by the racer rocket and raptor cannon lock.
        // =====================================================================================

        /// <summary>QC <c>.lock_target</c> — the entity currently being locked onto.</summary>
        public Entity? VehLockTarget;

        /// <summary>QC <c>.lock_strength</c> — 0..1 lock progress (1 == full lock).</summary>
        public float VehLockStrength;

        /// <summary>QC <c>.lock_time</c> — absolute time the achieved lock holds until.</summary>
        public float VehLockTime;

        /// <summary>QC <c>.lock_soundtime</c> — gate for the lock/locking ping sounds.</summary>
        public float VehLockSoundTime;

        // =====================================================================================
        // Per-weapon refire / fire-tag / counter / sound timers — the grab-bag of generic edict fields
        // (.delay/.cnt/.wait/.lip/.misc_bulletcounter/.sound_nexttime/.attack_finished_single[0])
        // the QC *_frame and *_weapons.qc reused as weapon state. Promoted here with descriptive names.
        // =====================================================================================

        /// <summary>QC <c>.misc_bulletcounter</c> — alternates fire tags / counts a salvo (racer rockets, raptor/spider/minigun).</summary>
        public int VehBulletCounter;

        /// <summary>QC <c>.delay</c> reused as a weapon next-attack gate (racer rocket pair, raptor bombs).</summary>
        public float VehWeaponDelay;

        /// <summary>QC <c>.lip</c> reused as the reload-bar start time for the secondary weapon.</summary>
        public float VehReloadStart;

        /// <summary>QC <c>.attack_finished_single[0]</c> — next-shot gate for the head/turret weapon (spider minigun, bumblebee cannon).</summary>
        public float VehAttackFinished;

        /// <summary>QC <c>.cnt</c> reused as the energy/ammo regen-pause timer (raptor energy, spider minigun belt).</summary>
        public float VehRegenPauseTime;

        /// <summary>QC <c>.wait</c> reused as the energy regen-pause / afterburn-FX gate (racer/bumblebee).</summary>
        public float VehWait;

        /// <summary>QC <c>.sound_nexttime</c> — gate for the looping engine sound.</summary>
        public float VehSoundNextTime;

        /// <summary>QC <c>.sounds</c> — which looping engine sound is currently playing (idle vs move).</summary>
        public int VehSoundState = -1;

        /// <summary>QC <c>.frame</c> — the model animation frame the SV side drives (takeoff %, walk pose, rocket belt).</summary>
        public float VehAnimFrame;

        // =====================================================================================
        // Secondary-weapon mode — the W2MODE stat (raptor RSM_*, spiderbot SBRM_*). On the vehicle entity
        // this holds the real value; QC mirrors it to the player as STAT(VEHICLESTAT_W2MODE).
        // =====================================================================================

        /// <summary>QC <c>STAT(VEHICLESTAT_W2MODE)</c> — secondary fire mode index (raptor bomb/flare, spiderbot rocket mode).</summary>
        public int VehW2Mode;

        // =====================================================================================
        // Spiderbot leg/jump state.
        // =====================================================================================

        /// <summary>QC spiderbot <c>.jump_delay</c> — gate that re-enables movement after a jump.</summary>
        public float VehJumpDelay;

        /// <summary>QC spiderbot <c>.button2</c> — latches the in-progress jump so it fires once per press.</summary>
        public bool VehJumpLatched;

        /// <summary>QC spiderbot <c>.tur_head.wait</c> — landing-recovery gate after a jump.</summary>
        public float VehLandTime;

        /// <summary>QC spiderbot <c>.tur_head.frame</c> / <c>.gun2.cnt</c> — rocket belt position and per-shot gate.</summary>
        public int VehRocketBelt;
        public float VehRocketGate;

        /// <summary>QC spiderbot rocket <c>.wait</c> latch: 0 idle, 1 holding-guide, -10 volley auto-empty.</summary>
        public int VehRocketWaitLatch;

        // =====================================================================================
        // Multi-seat (Bumblebee) — the gunner-slot links. On the BODY: gunner1/gunner2 (the seated
        // players) and gun1/gun2/gun3 (the slot entities). On a SLOT entity: VehSlotOwner -> body,
        // VehSlotIndex (1/2), VehSlotPlayer (the seated player).
        // =====================================================================================

        /// <summary>QC body <c>.gunner1</c> / <c>.gunner2</c> — the players seated in the side-gun slots.</summary>
        public Entity? VehGunner1, VehGunner2;

        /// <summary>QC body <c>.gun1</c> / <c>.gun2</c> / <c>.gun3</c> — the side-gun slot entities + the center raygun.</summary>
        public Entity? VehGun1, VehGun2, VehGun3;

        /// <summary>On a gun-slot entity: the body vehicle that owns it (QC slot.owner).</summary>
        public Entity? VehSlotOwner;

        /// <summary>On a gun-slot entity: 1 or 2 (right/left); 0 if not a slot.</summary>
        public int VehSlotIndex;

        /// <summary>On a gun-slot entity: the player currently occupying it (mirrors VehGunner1/2 on the body).</summary>
        public Entity? VehSlotPlayer;

        /// <summary>QC slot/turret <c>.phase</c> — re-entry delay after a gunner leaves a slot.</summary>
        public float VehPhase;

        // =====================================================================================
        // Projectile-guidance scratch (vehicle homing rockets) — the fields the per-rocket think reads.
        // =====================================================================================

        /// <summary>QC rocket <c>.pos1</c> — the guidance target point (crosshair trace endpoint / artillery impact).</summary>
        public Vector3 VehGuideTarget;

        /// <summary>QC rocket <c>.lip</c> — per-tick acceleration (racer rocket accel).</summary>
        public float VehProjAccel;

        /// <summary>QC rocket <c>.wait</c> — turn rate toward the guidance target.</summary>
        public float VehProjTurnRate;

        /// <summary>QC rocket <c>.cnt</c> — absolute lifetime expiry.</summary>
        public float VehProjExpire;

        /// <summary>The guidance mode this projectile flies under (homing/groundhug/guided/artillery), -1 = dumb.</summary>
        public int VehGuideMode = -1;
    }
}
