using System.Numerics;

namespace XonoticGodot.Common.Framework;

public delegate void EntityThink(Entity self);
public delegate void EntityTouch(Entity self, Entity other);
public delegate void EntityUse(Entity self, Entity activator);

/// <summary>
/// Marker for the presentation-side binding (a Godot node wrapper, set on the client only).
/// Defined as an interface so <c>XonoticGodot.Common</c> stays Godot-free (ADR-0008).
/// </summary>
public interface IEntityPresence { }

/// <summary>
/// The C# successor to the QuakeC edict. Engine fields live here; gameplay state lives on subclasses
/// or components (resolving QC's flat field namespace — see planning/specs/entity-model.md and ADR-0007).
/// The simulation operates on <see cref="Entity"/> with no Godot dependency.
///
/// Declared <c>partial</c> so feature areas can extend it in their own files without contention.
/// </summary>
public partial class Entity
{
    // --- identity / lifecycle ---
    public int Index;                  // engine-assigned slot
    public bool IsFreed;
    public string ClassName = "";
    public IEntityPresence? Presence;  // link to the Godot node (client-side only)

    // --- spatial state (engine-maintained) ---
    // (QC <c>.oldvelocity</c> = Entity.OldVelocity lives on the vehicles partial, Vehicles/VehicleCommon.cs;
    // it is the shared engine last-frame velocity snapshot, distinct from OldOrigin the interpolation anchor.)
    public Vector3 Origin, OldOrigin, Velocity, Angles, AVelocity;
    public Vector3 Mins, Maxs, Size, AbsMin, AbsMax, ViewOfs, PunchAngle, PunchVector;

    // --- movement / physics ---
    public MoveType MoveType;
    public Solid Solid;

    /// <summary>
    /// DP <c>.dphitcontentsmask</c> (Base/darkplaces/sv_phys.c SV_GenericHitSuperContentsMask): when nonzero,
    /// this SUPERCONTENTS mask OVERRIDES the solid-derived default for THIS entity's own movement trace. A
    /// projectile set up by <c>PROJECTILE_MAKETRIGGER</c> (SOLID_CORPSE) sets it to
    /// <c>SOLID|BODY|CORPSE</c> so the projectile still clips corpses even though SOLID_CORPSE alone would
    /// drop the CORPSE bit. 0 = unset (fall back to the solid-derived mask). See <c>Projectiles.MakeTrigger</c>.
    /// </summary>
    public int DpHitContentsMask;

    /// <summary>
    /// [sv-antilag.clear.on_spawn] Sticky one-shot request to wipe this entity's lag-comp position ring
    /// (port of the explicit <c>antilag_clear</c> call Base fires from <c>PutClientInServer</c>
    /// (client.qc:858) and on vehicle enter/exit). Set by (re)spawn / teleport / vehicle-boarding; the net
    /// driver clears the ring and resets the flag on its next antilag record pass. This catches the case the
    /// per-tick origin-jump heuristic misses — a respawn/teleport that lands WITHIN the jump threshold of the
    /// previous origin would otherwise leave stale history, letting a shot rewind toward the old position.
    /// </summary>
    public bool AntilagNeedsClear;

    public EntFlags Flags;
    public Entity? GroundEntity;
    public int WaterLevel;
    public int WaterType;
    public float Gravity = 1f;

    // --- scheduling / callbacks ---
    public float NextThink;
    public float LTime;               // local time for moving brush entities (PUSH)
    public EntityThink? Think;
    public EntityTouch? Touch;
    public EntityUse? Use;
    public EntityTouch? Blocked;

    // --- commonly shared gameplay fields (kept on base where QC used them generically) ---
    public float Health, MaxHealth;
    public float Frame, Skin;
    public int Effects, ModelIndex;
    public DamageMode TakeDamage;
    public DeadFlag DeadState;
    public Entity? Owner, Enemy, GoalEntity, Aiment, Chain, DmgInflictor;
    public string Model = "", NetName = "", Target = "", Target2 = "", TargetName = "", Message = "";
    public int SpawnFlags, Items;
    public float Team, Frags;

    public Entity() { }

    public bool OnGround => (Flags & EntFlags.OnGround) != 0;

    public override string ToString() => $"{(string.IsNullOrEmpty(ClassName) ? "entity" : ClassName)}#{Index}{(IsFreed ? " (freed)" : "")}";
}
