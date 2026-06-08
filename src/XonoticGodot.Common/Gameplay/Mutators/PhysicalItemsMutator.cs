// Port of common/mutators/mutator/physical_items/sv_physical_items.qc

using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Physical Items mutator — port of common/mutators/mutator/physical_items/sv_physical_items.qc. Makes map
/// items physical (MOVETYPE_PHYSICS rigid bodies) so they fall, slide and can be knocked around by damage, by
/// attaching a second physics "ghost" entity to each item. Enabled by the <c>g_physical_items</c> cvar
/// (1 = only dropped items, 2 = all map items).
///
/// Ported here: the enable gate + the cvars (<c>g_physical_items</c>, <c>g_physical_items_damageforcescale</c>,
/// <c>g_physical_items_reset</c>) and QC's MUTATOR_ONADD physics-engine availability check, which logs the exact
/// "no physics engine can be used, reverting to old items" trace and disables the mutator when no rigid-body
/// engine is present.
///
/// BLOCKER (documented partial — DOUBLE-BLOCKED): QC requires <c>DP_PHYSICS_ODE</c> (the Open Dynamics Engine
/// rigid-body extension) AND hooks <c>Item_Spawn</c> on a map item-entity. Neither exists in the port: there is
/// no <c>autocvar_physics_ode</c> / ODE rigid-body integrator wired to gameplay entities, and no map item-entity
/// spawn pipeline (MapObjectsRegistry registers no <c>item_*</c> spawnfuncs). So this mutator follows QC's own
/// "no physics engine ⇒ revert" path: it self-disables on add and does nothing, exactly as the reference game
/// does when DP_PHYSICS_ODE is unavailable. The <c>physical_item_*</c> think/touch/damage bodies and the
/// Item_Spawn ghost-entity attach are flagged for the day both an ODE-equivalent and an item pipeline land
/// (crossTaskNeeds); modelling MOVETYPE_PHYSICS items is out of scope for this task.
/// </summary>
[Mutator]
public sealed class PhysicalItemsMutator : MutatorBase
{
    /// <summary>QC autocvar_g_physical_items (0 off, 1 dropped-only, 2 all map items).</summary>
    public int Mode;

    /// <summary>QC autocvar_g_physical_items_damageforcescale — how much damage knocks an item around.</summary>
    public float DamageForceScale;

    /// <summary>QC autocvar_g_physical_items_reset — return an un-spawned item to its origin each think.</summary>
    public bool Reset;

    public PhysicalItemsMutator() => NetName = "physical_items";

    // QC: REGISTER_MUTATOR(physical_items, autocvar_g_physical_items) — with a MUTATOR_ONADD physics-engine check
    // that returns -1 (rolls the mutator back) when no rigid-body engine is available. The port has no such engine
    // wired to gameplay entities, so IsEnabled also requires HasPhysicsEngine() — which is false here, mirroring
    // QC reverting to old items. (Kept as a single gate so the mutator simply never activates, no half state.)
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_physical_items") != 0f && HasPhysicsEngine();

    public override void Hook()
    {
        if (Api.Services is not null)
        {
            Mode = (int)Api.Cvars.GetFloat("g_physical_items");
            DamageForceScale = Api.Cvars.GetFloat("g_physical_items_damageforcescale");
            Reset = Api.Cvars.GetFloat("g_physical_items_reset") != 0f;
        }
        // NOTE (deferred): QC subscribes Item_Spawn (physical_item_think/touch/damage + the ghost-entity attach).
        // No Item_Spawn pipeline + no ODE rigid-body engine exists in the port, so there is nothing to subscribe;
        // see the class doc / crossTaskNeeds. (Hook() only runs when IsEnabled, which HasPhysicsEngine() blocks.)
    }

    public override void Unhook()
    {
        // Nothing subscribed (see Hook). QC's MUTATOR_ONROLLBACK_OR_REMOVE is likewise a no-op ("nothing to roll
        // back"); MUTATOR_ONREMOVE logs "cannot be removed at runtime" — the port has no live-remove guard.
    }

    /// <summary>
    /// QC MUTATOR_ONADD: <c>autocvar_physics_ode &amp;&amp; checkextension("DP_PHYSICS_ODE")</c>. The port has no ODE
    /// rigid-body engine wired to gameplay entities, so this is always false and the mutator logs the same revert
    /// trace QC does and stays inert. (Logged once per check; the activation loop calls IsEnabled a bounded number
    /// of times.)
    /// </summary>
    private static bool HasPhysicsEngine()
    {
        // QC LOG_TRACE on the unavailable path. Mirror the message so the revert is auditable in `developer 1`.
        Log.Trace("Warning: Physical items are enabled but no physics engine can be used. Reverting to old items.");
        return false;
    }
}
