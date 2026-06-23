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
/// rigid-body extension) AND hooks <c>Item_Spawn</c> on a map item-entity. Neither exists in the port:
///   1. No ODE rigid-body integrator: <see cref="XonoticGodot.Engine.Simulation.MoveTypePhysics"/> ports the DP
///      MOVETYPE_TOSS/BOUNCE/FLY/etc. integrators faithfully, but there is no MOVETYPE_PHYSICS (the QC ghost's
///      movetype) — its <c>default:</c> case merely runs the entity's think. Wiring an ODE-equivalent rigid body
///      to gameplay edicts is a large engine task, out of scope here.
///   2. No <c>Item_Spawn</c> mutator-hook chain: the world-item driver explicitly skips it
///      (StartItem.cs: "MUTATOR_CALLHOOK(Item_Spawn, this) — no hook chain; skip."), so there is nothing for this
///      mutator to subscribe in <see cref="Hook"/>. Reviving it needs a new <c>Item_Spawn</c> HookChain in
///      MutatorHooks.cs plus a <c>.Call</c> site in StartItem.cs — both outside this file.
/// So this mutator faithfully follows QC's own "no physics engine ⇒ revert" path: it self-disables on add (logging
/// the byte-identical revert trace) and does nothing, exactly as the reference game does when DP_PHYSICS_ODE is
/// unavailable. The <c>physical_item_*</c> think/touch/damage bodies and the Item_Spawn ghost-entity attach are
/// flagged for the day both an ODE-equivalent and an Item_Spawn hook land (see todos / registry shard
/// mutator-physical_items.item_spawn.ghost_entity + .item_callbacks.think_touch_damage).
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
    /// trace QC does and stays inert.
    /// </summary>
    /// <remarks>
    /// QC's MUTATOR_ONADD runs the engine check exactly once (on add). The port's activation gate evaluates
    /// <see cref="IsEnabled"/> more than once (the registry verifies the predicate IS reached on the live boot
    /// path, and later code can re-query it), so we latch the revert trace behind <see cref="_revertLogged"/> to
    /// keep QC's once-per-add log semantics — otherwise `developer 1` would show the warning repeatedly.
    /// </remarks>
    private static bool _revertLogged;

    private static bool HasPhysicsEngine()
    {
        // QC LOG_TRACE on the unavailable path. Mirror the message so the revert is auditable in `developer 1`,
        // but only once (matching QC's single MUTATOR_ONADD evaluation), not once per IsEnabled query.
        if (!_revertLogged)
        {
            _revertLogged = true;
            Log.Trace("Warning: Physical items are enabled but no physics engine can be used. Reverting to old items.");
        }
        return false;
    }
}
