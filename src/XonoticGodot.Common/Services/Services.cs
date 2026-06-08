using System.Numerics;
using XonoticGodot.Common.Framework;

namespace XonoticGodot.Common.Services;

/// <summary>
/// The engine-services facade — the C# reimplementation of the QuakeC builtins / dpdefs binding
/// (planning/decisions/ADR-0009, specs/engine-services-facade.md). Gameplay in <c>XonoticGodot.Common</c>
/// depends only on these interfaces, never on Godot, so it stays headless-testable. The deterministic
/// simulation (<c>XonoticGodot.Engine</c>) and the Godot host provide the implementations.
/// </summary>

/// <summary>Result of a traceline/tracebox sweep (QC trace_* globals).</summary>
public struct TraceResult
{
    public bool AllSolid;
    public bool StartSolid;
    public float Fraction;            // 1.0 = no collision
    public Vector3 EndPos;
    public Vector3 PlaneNormal;       // '0 0 0' if no collision
    public float PlaneDist;
    public Entity? Ent;               // entity hit, if any
    public bool InOpen;
    public bool InWater;
    public int DpHitContents;         // SUPERCONTENTS bitmask of the surface hit
    public int DpHitQ3SurfaceFlags;   // Q3SURFACEFLAG_* of the surface hit
    public string? DpHitTextureName;  // texture name of the surface hit

    public static TraceResult Miss(Vector3 end) => new()
    {
        Fraction = 1f,
        EndPos = end,
        PlaneNormal = Vector3.Zero,
    };
}

/// <summary>traceline/tracebox/pointcontents (fidelity-critical — specs/determinism-and-physics.md).</summary>
public interface ITraceService
{
    TraceResult Trace(Vector3 start, Vector3 mins, Vector3 maxs, Vector3 end, MoveFilter filter, Entity? ignore);
    int PointContents(Vector3 point);

    /// <summary>
    /// QC <c>checkpvs(viewpoint, target)</c>: is <paramref name="target"/> potentially visible from
    /// <paramref name="viewpoint"/> per the map's compiled PVS? A conservative superset of true visibility
    /// (never a false negative), so callers use it as a cheap pre-filter before an exact <see cref="Trace"/>
    /// line-of-sight test, or to cull networking/sound. Returns true on an unvised map (no PVS data).
    /// </summary>
    bool CheckPvs(Vector3 viewpoint, Vector3 target);
}

/// <summary>spawn/remove/find/setorigin/setmodel/setsize (QC entity-management builtins).</summary>
public interface IEntityService
{
    Entity Spawn();
    void Remove(Entity e);
    void SetOrigin(Entity e, Vector3 origin);
    void SetSize(Entity e, Vector3 mins, Vector3 maxs);
    void SetModel(Entity e, string model);
    IEnumerable<Entity> FindByClass(string className);
    IEnumerable<Entity> FindInRadius(Vector3 origin, float radius);
}

[Flags]
public enum CvarFlags { None = 0, Save = 1, Notify = 2, ReadOnly = 4 }

/// <summary>cvar/cvar_set/registercvar (QC cvar builtins). Honors Xonotic cvar names (OPEN Q5).</summary>
public interface ICvarService
{
    float GetFloat(string name);
    string GetString(string name);
    void Set(string name, string value);
    void Register(string name, string defaultValue, CvarFlags flags = CvarFlags.None);
}

// Mirrors QuakeC's CH_* constants (common/sounds/sound.qh) + Darkplaces' channel model (sound.h): a NEGATIVE
// (auto) channel auto-allocates a fresh slot so overlapping plays STACK; a POSITIVE (single) channel is matched
// per (entity, channel) so a new play REPLACES the one already on it (SND_PickChannel, snd_main.c). Most
// gameplay one-shots (weapon fire, impacts, footsteps, pain, voice, pickups) are AUTO in stock Xonotic — single
// channels are reserved for continuous/looping sources (tuba, bgm, projectile fly-loops) that must not stack.
public enum SoundChannel
{
    PlayerAuto = -7,  // CH_PLAYER  — footsteps, landing, body sounds (stack)
    PainAuto = -6,    // CH_PAIN    — pain (stack)
    ShotsAuto = -4,   // CH_SHOTS   — projectile impacts (stack)
    TriggerAuto = -3, // CH_TRIGGER — item pickups, world triggers (stack)
    VoiceAuto = -2,   // CH_VOICE   — voice / taunts (stack)
    WeaponAuto = -1,  // CH_WEAPON_A
    Auto = 0,         // CH_INFO
    WeaponSingle = 1,
    Weapon = 1,
    Voice = 2,
    Item = 3,
    Body = 4,
    Tuba = 5,
    Pain = 6,
    Player = 7,
    Bgm = 8,
}

/// <summary>sound()/precache_sound (QC sound builtins), with DP's SV_StartSound entity+channel model.</summary>
public interface ISoundService
{
    /// <summary>
    /// QC <c>sound(e, channel, sample, volume, attenuation)</c> / DP <c>SV_StartSound</c>. When
    /// <paramref name="loop"/> is true the sound is a PERSISTENT loop keyed by <c>(e, channel)</c> (QC
    /// <c>loopsound</c>): a later loop on the same entity+channel REPLACES it, and <see cref="Stop"/> ends it.
    /// A one-shot (the default) just plays once. Re-emitting the same looping sample on its (e, channel) each
    /// tick is idempotent — the client keeps the existing loop rather than restarting it — so a continuous
    /// emitter (the Arc beam) can call this every weapon-think without stacking.
    /// </summary>
    void Play(Entity e, SoundChannel channel, string sample, float volume = 1f, float attenuation = 1f, bool loop = false, float pitch = 1f);

    /// <summary>
    /// Stop the sound on <c>(e, channel)</c> — DP <c>sound(e, channel, SND_Null)</c>. Ends a looping sound
    /// started with <c>Play(..., loop: true)</c> (e.g. the Arc beam loop when the trigger is released). A no-op
    /// if nothing is playing on that entity+channel.
    /// </summary>
    void Stop(Entity e, SoundChannel channel);
}

/// <summary>gettaginfo/setattachment (QC model/tag builtins — drives weapon/effect attachment).</summary>
public interface IModelService
{
    bool TryGetTag(Entity e, string tagName, out Vector3 origin, out Vector3 forward, out Vector3 right, out Vector3 up);
    void SetAttachment(Entity e, Entity parent, string tagName);
}

/// <summary>
/// The <c>getsurface*</c> model/BSP mesh-query builtins (DP <c>VM_getsurface*</c> #434-#439/#486/#628-#629).
/// They read the render surfaces of an entity's model — a BSP inline model (<c>"*N"</c>) or a loaded
/// md3/iqm/dpm — and return per-surface point/triangle/normal/texture/attribute data in WORLD space
/// (the entity's current origin+angles applied). Used by <c>lib/warpzone</c> to auto-derive a portal's plane
/// from its brush, and by the decal/surface-query code. <see cref="SurfaceAttribute"/> selects which per-point
/// channel <see cref="GetSurfacePointAttribute"/> returns.
/// </summary>
public interface ISurfaceService
{
    /// <summary>getsurfacenumpoints — vertex count of surface <paramref name="surface"/> (0 if none).</summary>
    int GetSurfaceNumPoints(Entity e, int surface);

    /// <summary>getsurfacepoint — world-space position of vertex <paramref name="point"/> on the surface.</summary>
    Vector3 GetSurfacePoint(Entity e, int surface, int point);

    /// <summary>getsurfacenormal — world-space geometric normal of the surface's plane.</summary>
    Vector3 GetSurfaceNormal(Entity e, int surface);

    /// <summary>getsurfacetexture — the surface's shader/texture name ("" if none).</summary>
    string GetSurfaceTexture(Entity e, int surface);

    /// <summary>getsurfacenearpoint — index of the surface nearest <paramref name="point"/> (-1 if none).</summary>
    int GetSurfaceNearPoint(Entity e, Vector3 point);

    /// <summary>getsurfaceclippedpoint — <paramref name="point"/> clamped to the nearest spot on the surface.</summary>
    Vector3 GetSurfaceClippedPoint(Entity e, int surface, Vector3 point);

    /// <summary>getsurfacepointattribute — a per-vertex channel (see <see cref="SurfaceAttribute"/>).</summary>
    Vector3 GetSurfacePointAttribute(Entity e, int surface, int point, int attribute);

    /// <summary>getsurfacenumtriangles — triangle count of the surface (0 if none).</summary>
    int GetSurfaceNumTriangles(Entity e, int surface);

    /// <summary>getsurfacetriangle — the three vertex indices of triangle <paramref name="triangle"/> as (x,y,z).</summary>
    Vector3 GetSurfaceTriangle(Entity e, int surface, int triangle);
}

/// <summary>SPA_* codes for <see cref="ISurfaceService.GetSurfacePointAttribute"/> (csprogsdefs.qc).</summary>
public enum SurfaceAttribute
{
    Position = 0,    // SPA_POSITION
    SAxis = 1,       // SPA_S_AXIS    (tangent)
    TAxis = 2,       // SPA_T_AXIS    (bitangent)
    Normal = 3,      // SPA_R_AXIS    (vertex normal)
    TexCoords = 4,   // SPA_TEXCOORDS0
    LightmapTexCoords = 5, // SPA_LIGHTMAP0_TEXCOORDS
    LightmapColor = 6,     // SPA_LIGHTMAP_COLOR (vertex color/light)
}

/// <summary>A null <see cref="ISurfaceService"/> (no model geometry registered): every query is empty.</summary>
public sealed class NullSurfaceService : ISurfaceService
{
    public static readonly NullSurfaceService Instance = new();
    public int GetSurfaceNumPoints(Entity e, int surface) => 0;
    public Vector3 GetSurfacePoint(Entity e, int surface, int point) => Vector3.Zero;
    public Vector3 GetSurfaceNormal(Entity e, int surface) => Vector3.Zero;
    public string GetSurfaceTexture(Entity e, int surface) => "";
    public int GetSurfaceNearPoint(Entity e, Vector3 point) => -1;
    public Vector3 GetSurfaceClippedPoint(Entity e, int surface, Vector3 point) => point;
    public Vector3 GetSurfacePointAttribute(Entity e, int surface, int point, int attribute) => Vector3.Zero;
    public int GetSurfaceNumTriangles(Entity e, int surface) => 0;
    public Vector3 GetSurfaceTriangle(Entity e, int surface, int triangle) => Vector3.Zero;
}

/// <summary>The current simulation clock (QC time/frametime globals).</summary>
public interface IGameClock
{
    float Time { get; }
    float FrameTime { get; }
}

/// <summary>Aggregates the facade so gameplay can reach all services through one ambient handle.</summary>
public interface IEngineServices
{
    ITraceService Trace { get; }
    IEntityService Entities { get; }
    ICvarService Cvars { get; }
    ISoundService Sound { get; }
    IModelService Models { get; }
    IGameClock Clock { get; }

    /// <summary>getsurface* mesh queries (default: a no-geometry null service, overridden by the engine host).</summary>
    ISurfaceService Surfaces => NullSurfaceService.Instance;
}

/// <summary>
/// Ambient access to the engine facade — the C# stand-in for QuakeC's global builtins. The simulation
/// sets <see cref="Services"/> once at startup. (One world per process for now; a per-world context can
/// replace this later if a single process hosts multiple simulations.)
/// </summary>
public static class Api
{
    public static IEngineServices Services { get; set; } = null!;

    public static ITraceService Trace => Services.Trace;
    public static IEntityService Entities => Services.Entities;
    public static ICvarService Cvars => Services.Cvars;
    public static ISoundService Sound => Services.Sound;
    public static IModelService Models => Services.Models;
    public static IGameClock Clock => Services.Clock;
    public static ISurfaceService Surfaces => Services.Surfaces;
}
