// Port of qcsrc/common/mapobjects/models.qc — the decoration-prop / static-wall family:
//   misc_gamemodel / misc_clientmodel / misc_models  (non-solid external-model props; pitch FLIPPED first)
//   func_illusionary / func_clientillusionary        (non-solid brush entities)
//   func_wall / func_clientwall / func_static        (SOLID brush entities)
//
// Port notes:
//  * SetBrushEntityModel == Api.Entities.SetModel, called AFTER solid exactly as QC (models.qc:180) — an
//    inline "*N" model gives the wall its real brush bounds AND, with Solid.Bsp, real collision through
//    TraceService.ClipToEntities (this is what makes stormkeep's 17 func_walls actually block).
//  * The g_clientmodel_* variants are SVQC-identical here (their extra Net_LinkEntity ENT_CLIENT_WALL
//    stream is unnecessary — the listen-server/demo client reads the shared entity; the fade keys
//    fade_start/fade_end/alpha_max/alpha_min/fade_vertical_offset ride the entity for it). The antiwall
//    relay protocol (g_clientmodel_use antiwall_flag) and bgmscript ADSR animation are out of scope (no
//    shipped stock map depends on them).
//  * External-model props keep their full "models/…" path on Entity.Model — that's the networked-render
//    contract (ServerNet networks any non-'*' model as a Generic entity and the client loads it by path).
//    No ResolveModelPath prefixing here: map keys carry full paths, unlike the bare item-def names (T35).
//  * INITPRIO_DROPTOFLOOR runs inline at spawn: the static world the MOVE_NOMONSTERS drop traces against
//    is already built before the entity lump spawns in both hosts.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>The models.qc decoration/wall spawnfuncs. Registered by <see cref="MapObjectsRegistry"/>.</summary>
public static class MapModels
{
    // ---- the eight classname spawnfuncs (models.qc:207-218) ----

    /// <summary><c>spawnfunc(misc_gamemodel)</c> — non-solid model entity (pitch flipped first).</summary>
    public static void GameModelSetup(Entity e) { e.ClassName = "misc_gamemodel"; FlipPitch(e); GModelInit(e, Solid.Not); }

    /// <summary><c>spawnfunc(misc_clientmodel)</c> — client-networked model entity (pitch flipped first).</summary>
    public static void ClientModelSetup(Entity e) { e.ClassName = "misc_clientmodel"; FlipPitch(e); GModelInit(e, Solid.Not); }

    /// <summary><c>spawnfunc(misc_models)</c> — DEPRECATED compat alias of misc_gamemodel.</summary>
    public static void ModelsSetup(Entity e) { e.ClassName = "misc_models"; FlipPitch(e); GModelInit(e, Solid.Not); }

    /// <summary><c>spawnfunc(func_illusionary)</c> — non-solid brush entity (Q1 name).</summary>
    public static void IllusionarySetup(Entity e) { e.ClassName = "func_illusionary"; GModelInit(e, Solid.Not); }

    /// <summary><c>spawnfunc(func_clientillusionary)</c> — non-solid client-networked brush entity.</summary>
    public static void ClientIllusionarySetup(Entity e) { e.ClassName = "func_clientillusionary"; GModelInit(e, Solid.Not); }

    /// <summary><c>spawnfunc(func_wall)</c> — SOLID brush entity (Q1 name).</summary>
    public static void WallSetup(Entity e) { e.ClassName = "func_wall"; GModelInit(e, Solid.Bsp); }

    /// <summary><c>spawnfunc(func_clientwall)</c> — SOLID client-networked brush entity.</summary>
    public static void ClientWallSetup(Entity e) { e.ClassName = "func_clientwall"; GModelInit(e, Solid.Bsp); }

    /// <summary><c>spawnfunc(func_static)</c> — DEPRECATED alias of func_wall.</summary>
    public static void StaticSetup(Entity e) { e.ClassName = "func_static"; GModelInit(e, Solid.Bsp); }

    /// <summary>The three misc_* model entities flip mapper pitch into the engine model convention
    /// (models.qc:207-209 <c>this.angles_x = -this.angles.x</c>).</summary>
    private static void FlipPitch(Entity e) => e.Angles = new Vector3(-e.Angles.X, e.Angles.Y, e.Angles.Z);

    /// <summary>Port of <c>g_model_init</c> / <c>g_clientmodel_init</c> (models.qc:173-204; the client variant
    /// differs only by networking we don't need).</summary>
    internal static void GModelInit(Entity ent, Solid sol)
    {
        // (QC: geomtype + ODE physics — not ported, no stock map uses it.)
        if (ent.ScaleFactor == 0f)
            ent.ScaleFactor = ent.ModelScale;

        // solid: unset -> the family default; a mapper 'solid -1' key forces non-solid; any other explicit
        // value is kept raw (the port Solid enum carries the QC SOLID_* values 1:1).
        if (ent.SolidOverride == 0f)
            ent.Solid = sol;
        else if (ent.SolidOverride < 0f)
            ent.Solid = Solid.Not;
        else
            ent.Solid = (Solid)(int)ent.SolidOverride;

        ent.MoveType = MoveType.None;

        // SetBrushEntityModel — AFTER solid, as QC notes, for correct area linking.
        if (Api.Services is not null && !string.IsNullOrEmpty(ent.Model))
            Api.Entities.SetModel(ent, ent.Model);

        ent.Use = SetColormapToActivator;

        // INITPRIO_DROPTOFLOOR (models.qc:65-82) — run inline, see header.
        DropBySpawnflags(ent);

        MapMover.IndexRegister(ent);
    }

    /// <summary>Port of <c>g_model_setcolormaptoactivator</c> (models.qc:17-29). The colormap value is stored
    /// on <see cref="Entity.ColorMapOverride"/> (render consumption is a follow-up seam).</summary>
    internal static void SetColormapToActivator(Entity this_, Entity actor)
    {
        // QC checks the global `teamplay`; the established Common-side read is the cvar (MonsterAI.IsTeamplay).
        bool teamplay = Api.Services is not null && Api.Cvars.GetFloat("teamplay") != 0f;
        if (teamplay)
        {
            if (actor.Team != 0f)
                this_.ColorMapOverride = (int)(actor.Team - 1f) * 0x11;
            else
                this_.ColorMapOverride = 0x00;
        }
        else
        {
            this_.ColorMapOverride = (int)System.MathF.Floor(Prandom.Float() * 256f);
        }
        this_.ColorMapOverride |= 1 << 10; // BIT(10) RENDER_COLORMAPPED
    }

    /// <summary>Port of <c>g_model_dropbyspawnflags</c> (models.qc:65-82): spawnflags&amp;3 —
    /// 1 = ALIGN_ORIGIN (line trace), 2 = ALIGN_BOTTOM (box trace), 3 = origin trace lifted by -mins.z.</summary>
    internal static void DropBySpawnflags(Entity this_)
    {
        if (Api.Services is null)
            return;

        Vector3 down = this_.Origin - new Vector3(0f, 0f, 4096f);
        switch (this_.SpawnFlags & 3)
        {
            case 1: // ALIGN_ORIGIN
            {
                TraceResult tr = Api.Trace.Trace(this_.Origin, Vector3.Zero, Vector3.Zero, down, MoveFilter.NoMonsters, this_);
                MapMover.SetOrigin(this_, tr.EndPos);
                break;
            }
            case 2: // ALIGN_BOTTOM
            {
                TraceResult tr = Api.Trace.Trace(this_.Origin, this_.Mins, this_.Maxs, down, MoveFilter.NoMonsters, this_);
                MapMover.SetOrigin(this_, tr.EndPos);
                break;
            }
            case 3: // ALIGN_ORIGIN | ALIGN_BOTTOM
            {
                TraceResult tr = Api.Trace.Trace(this_.Origin, Vector3.Zero, Vector3.Zero, down, MoveFilter.NoMonsters, this_);
                MapMover.SetOrigin(this_, tr.EndPos - new Vector3(0f, 0f, 1f) * this_.Mins.Z);
                break;
            }
        }
    }
}
