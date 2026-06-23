using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Map-placement spawnfuncs for the vehicle family — the thin <c>spawnfunc(vehicle_X)</c> entries each per-type
/// .qc defines (common/vehicles/vehicle/*.qc). Every one is
/// <c>spawnfunc(vehicle_X){ if (!autocvar_g_vehicle_X || !vehicle_initialize(this, VEH_X, false)) delete(this); }</c>,
/// so they all funnel through the shared setup: gate on the per-vehicle master cvar, resolve the
/// <see cref="Vehicle"/> from the <see cref="Vehicles"/> registry by net name, record the spawn point (QC
/// <c>pos1</c>/<c>pos2</c>), then run the descriptor's <see cref="Vehicle.Spawn"/> (= QC
/// <c>vehicle_initialize</c> + the deferred <c>vehicles_spawn</c>→<c>vr_spawn</c>, which itself wires the
/// per-frame think). <see cref="MapObjectsRegistry"/> registers these into <see cref="SpawnFuncs"/> so the BSP
/// entity-lump loader instantiates hand-placed vehicles on stock maps.
/// </summary>
public static class VehicleSpawnFuncs
{
    /// <summary>
    /// The shared body of every <c>spawnfunc(vehicle_X)</c>: gate on <c>g_vehicle_&lt;netName&gt;</c> (QC
    /// <c>autocvar_g_vehicle_X</c>) and the resolved type; on failure delete the edict (QC
    /// <c>delete(this); return;</c>). Otherwise record the spawn return point (QC <c>vehicle_initialize</c>:
    /// <c>pos1 = origin; pos2 = angles</c>) and run the descriptor's Spawn, which (via
    /// <see cref="VehicleCommon.SpawnVehicle"/>) reads those back and arms the per-frame think.
    /// <paramref name="netName"/> is the QC <c>info.netname</c> (after <c>vehicle_</c>).
    /// </summary>
    private static void Spawn(Entity e, string netName)
    {
        Vehicle? def = Vehicles.ByName(netName);
        // QC: if (!autocvar_g_vehicle_X || !vehicle_initialize(...)) { delete(this); return; }
        // g_vehicle_X is a boolean master switch (one per vehicle): an explicit 0 disables it, unset defaults
        // on (vehicles.cfg sets each to 1). Use the absent-vs-0 gate, not a 0-as-unset fallback.
        if (def is null || !MasterSwitchEnabled("g_vehicle_" + netName))
        {
            if (Api.Services is not null)
                Api.Entities.Remove(e);
            return;
        }

        // QC vehicle_initialize: pos1 = this.origin; pos2 = this.angles — the return point a destroyed vehicle
        // respawns at. SpawnVehicle (called by the descriptor's Spawn) reads these, so set them first.
        e.SpawnPos = e.Origin;
        e.SpawnAngles = e.Angles;

        // QC vehicle_initialize + the deferred vehicles_spawn → vr_spawn: stamp the vehicle (model/hitbox/health/
        // shield/energy + capability flags) and arm its think. The port folds all of that into the descriptor's
        // Spawn (which calls VehicleCommon.SpawnVehicle and sets e.Think/e.NextThink itself).
        def.Spawn(e);

        // QC vehicle_initialize tail (sv_vehicles.qc:1283): if (MUTATOR_CALLHOOK(VehicleInit, this)) return false;
        // — a mutator may veto this vehicle's one-time init, and the spawnfunc deletes the edict on a false return.
        if (VehicleCommon.InitVehicle(e) && Api.Services is not null)
            Api.Entities.Remove(e);
    }

    // Per-type spawnfuncs (each per-vehicle .qc: spawnfunc(vehicle_X){ ... vehicle_initialize(this, VEH_X, false); }).
    public static void Racer(Entity e) => Spawn(e, "racer");             // vehicle/racer.qc:520
    public static void Raptor(Entity e) => Spawn(e, "raptor");           // vehicle/raptor.qc:587
    public static void Spiderbot(Entity e) => Spawn(e, "spiderbot");     // vehicle/spiderbot.qc:529
    public static void Bumblebee(Entity e) => Spawn(e, "bumblebee");     // vehicle/bumblebee.qc:740

    /// <summary>
    /// Evaluate a <c>g_vehicle_X</c> boolean master switch the QC way (<c>if (!autocvar_g_vehicle_X)</c>):
    /// DISABLED only when the cvar is explicitly 0, ENABLED when unset/absent (vehicles.cfg defaults each to
    /// 1). Distinguishes "absent" from "present-and-0" via the raw string (an unset cvar reads as ""), so a
    /// server that sets <c>g_vehicle_racer 0</c> actually suppresses that vehicle instead of falling back to 1.
    /// </summary>
    private static bool MasterSwitchEnabled(string name)
    {
        if (Api.Services is null) return true; // no cvar store (headless): default-on like the cfg
        string s = Api.Cvars.GetString(name);
        if (string.IsNullOrEmpty(s)) return true; // unset → enabled (cfg default is 1)
        return Api.Cvars.GetFloat(name) != 0f;     // present → 0 disables (QC !autocvar)
    }
}
