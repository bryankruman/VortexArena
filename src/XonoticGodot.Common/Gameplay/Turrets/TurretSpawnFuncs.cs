using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Map-placement spawnfuncs for the turret family — the thin <c>spawnfunc(turret_X)</c> entries each per-type
/// .qc defines (common/turrets/turret/*.qc). Every one is
/// <c>spawnfunc(turret_X){ if(!turret_initialize(this, TUR_X)) delete(this); }</c>, so they all funnel through
/// the shared setup: resolve the <see cref="Turret"/> from the <see cref="Turrets"/> registry by net name, run
/// its <see cref="Turret.Spawn"/> (= QC <c>tr_setup</c> + <c>turret_initialize</c>'s field stamping, done by
/// <see cref="TurretSpawn.Init"/>), then wire the per-frame think (QC <c>turret_link</c> →
/// <c>setthink(turret_think)</c>). <see cref="MapObjectsRegistry"/> registers these into <see cref="SpawnFuncs"/>
/// so the BSP entity-lump loader instantiates hand-placed turrets on stock maps.
/// </summary>
public static class TurretSpawnFuncs
{
    /// <summary>
    /// The shared body of every <c>spawnfunc(turret_X)</c> (QC
    /// <c>if (!turret_initialize(this, TUR_X)) delete(this);</c>): resolve the turret type by net name, run its
    /// <see cref="Turret.Spawn"/>, then hand the brain to the descriptor's <see cref="Turret.Think"/> re-armed
    /// every frame (QC <c>turret_think</c> keeps <c>nextthink = time</c>). An unknown type / disabled turrets
    /// deletes the edict. <paramref name="netName"/> is the QC <c>tur.netname</c> (after <c>turret_</c>).
    /// </summary>
    private static void Spawn(Entity e, string netName)
    {
        Turret? def = Turrets.ByName(netName);
        // QC turret_initialize returns false for a null/invalid turret (or !autocvar_g_turrets) → delete.
        // g_turrets is a boolean master switch: an explicit 0 disables it; unset defaults on (turrets.cfg
        // sets it to 1) — see MasterSwitchEnabled, which distinguishes absent from present-and-0.
        if (def is null || !MasterSwitchEnabled("g_turrets"))
        {
            if (Api.Services is not null)
                Api.Entities.Remove(e);
            return;
        }

        // QC turret_initialize: stamp the turret edict (model/hitbox/health/solid/team + ammo/volley + the
        // use/damage/death lifecycle). The port folds that into the descriptor's Spawn (which calls
        // TurretSpawn.Init).
        def.Spawn(e);

        // QC turret_link: setthink(this, turret_think); this.nextthink = time. This is what makes a hand-placed
        // turret actually run each frame — the simulation loop's SV_RunThink fires it. Re-arm nextthink inside
        // the think so it keeps ticking (turret_think sets nextthink = time every frame).
        e.Think = self => { self.NextThink = Now; def.Think(self); };
        e.NextThink = Now;
    }

    // Per-type spawnfuncs (each per-turret .qc: spawnfunc(turret_X){ if(!turret_initialize(this,TUR_X)) delete(this); }).
    public static void Machinegun(Entity e) => Spawn(e, "machinegun");     // turret/machinegun.qc:5
    public static void Plasma(Entity e) => Spawn(e, "plasma");             // turret/plasma.qc:5
    public static void PlasmaDual(Entity e) => Spawn(e, "plasma_dual");    // turret/plasma_dual.qc:5
    public static void Mlrs(Entity e) => Spawn(e, "mlrs");                 // turret/mlrs.qc:5
    public static void Flac(Entity e) => Spawn(e, "flac");                 // turret/flac.qc:5
    public static void Hellion(Entity e) => Spawn(e, "hellion");           // turret/hellion.qc:5
    public static void Hk(Entity e) => Spawn(e, "hk");                     // turret/hk.qc:9
    public static void Phaser(Entity e) => Spawn(e, "phaser");             // turret/phaser.qc:5
    public static void Tesla(Entity e) => Spawn(e, "tesla");              // turret/tesla.qc:5
    public static void Walker(Entity e) => Spawn(e, "walker");             // turret/walker.qc:350
    public static void EWheel(Entity e) => Spawn(e, "ewheel");             // turret/ewheel.qc:134
    public static void FusionReactor(Entity e) => Spawn(e, "fusreac");     // turret/fusionreactor.qc:26

    /// <summary>
    /// QC <c>spawnfunc(turret_checkpoint)</c> + <c>turret_checkpoint_init</c> (common/turrets/checkpoint.qc): a
    /// stationary waypoint node the mobile turrets (ewheel/walker) roam between. On spawn it drops the node to
    /// the floor (a 1024u down-trace, then origin = floor + 32u up) and indexes itself by <c>targetname</c> so a
    /// turret's <c>find(targetname)</c> resolves it. The chain link (<c>this.enemy = find(targetname, target)</c>)
    /// is resolved lazily by <see cref="EWheelTurret"/> when it advances the path, so the spawn order of the
    /// checkpoints on the map does not matter (QC defers the link to INITPRIO_FINDTARGET for the same reason).
    /// </summary>
    public static void Checkpoint(Entity e)
    {
        e.ClassName = "turret_checkpoint";

        if (Api.Services is not null)
        {
            // QC turret_checkpoint_init: traceline(origin+'0 0 16', origin-'0 0 1024', MOVE_WORLDONLY); then
            // setorigin(trace_endpos + '0 0 32') — snap the node to the floor it sits above.
            Vector3 from = e.Origin + new Vector3(0f, 0f, 16f);
            Vector3 to = e.Origin - new Vector3(0f, 0f, 1024f);
            TraceResult tr = Api.Trace.Trace(from, Vector3.Zero, Vector3.Zero, to, MoveFilter.WorldOnly, e);
            Api.Entities.SetOrigin(e, tr.EndPos + new Vector3(0f, 0f, 32f));
        }

        // Findable by the turret's find(targetname) chase (QC find(NULL, targetname, this.target)).
        MapMover.IndexRegister(e);
    }

    private static float Now => Api.Services is null ? 0f : Api.Clock.Time;

    /// <summary>
    /// Evaluate the <c>g_turrets</c> boolean master switch the QC way (<c>if (!autocvar_g_turrets)</c>):
    /// DISABLED only when the cvar is explicitly 0, ENABLED when unset/absent (turrets.cfg defaults it to 1).
    /// Distinguishes "absent" from "present-and-0" via the raw string (an unset cvar reads as ""), so a server
    /// that sets <c>g_turrets 0</c> actually suppresses the turret instead of falling back to 1.
    /// </summary>
    private static bool MasterSwitchEnabled(string name)
    {
        if (Api.Services is null) return true; // no cvar store (headless): default-on like the cfg
        string s = Api.Cvars.GetString(name);
        if (string.IsNullOrEmpty(s)) return true; // unset → enabled (cfg default is 1)
        return Api.Cvars.GetFloat(name) != 0f;     // present → 0 disables (QC !autocvar)
    }
}
