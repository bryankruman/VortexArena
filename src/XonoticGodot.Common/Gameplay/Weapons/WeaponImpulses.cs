using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The client-impulse → weapon-selection router — the C# successor to the weapon half of QuakeC's
/// <c>server/impulse.qc</c> (the <c>weapon_group_handle</c> / <c>weapon_priority_handle</c> /
/// <c>weapon_byid_handle</c> handlers and the <c>weapon_next/prev_*</c>, <c>weapon_last</c>, <c>weapon_best</c>,
/// <c>weapon_drop</c>, <c>weapon_reload</c> impulses). The server feeds it a client's impulse number (the
/// <c>impulse N</c> console command) and it drives the <see cref="Inventory"/> selection API
/// (server/weapons/selection.qc), exactly as <c>ImpulseCommands</c> dispatches through the IMPULSES registry.
///
/// <para>This is a NEW selection/reload helper alongside <see cref="WeaponFireDriver"/> (per the T6 task scope);
/// the host (Commands.cs) routes the raw impulse number here via <see cref="Handle"/>.</para>
///
/// <para><b>Impulse numbers</b> are the literal Xonotic values from <c>common/impulses/all.qh</c> (kept in sync
/// with xonotic-client.cfg's weapon binds): group 1-9/14, priority prev/best/next 200-229, by-id 230-253, and
/// the next/prev/last/best/drop/reload singletons 10-20.</para>
/// </summary>
public static class WeaponImpulses
{
    // ---- impulse numbers (common/impulses/all.qh) ----
    private const int GroupFirst = 1;   // weapon_group_1..9 = 1..9
    private const int GroupLast = 9;
    private const int Group0 = 14;      // weapon_group_0 = 14

    private const int PriorityPrevFirst = 200; // weapon_priority_<0..9>_prev = 200..209
    private const int PriorityBestFirst = 210; // weapon_priority_<0..9>_best = 210..219
    private const int PriorityNextFirst = 220; // weapon_priority_<0..9>_next = 220..229

    private const int ByIdFirst = 230;  // weapon_byid_<0..23> = 230..253
    private const int ByIdLast = 253;

    private const int NextById = 10;
    private const int Last = 11;
    private const int PrevById = 12;
    private const int Best = 13;
    private const int NextByPriority = 15;
    private const int PrevByPriority = 16;
    private const int Drop = 17;
    private const int NextByGroup = 18;
    private const int PrevByGroup = 19;
    private const int Reload = 20;

    /// <summary>
    /// Route a raw client impulse (QC <c>ImpulseCommands</c> → the IMPULSES registry handler) onto the weapon
    /// selection/reload API. Returns true if the impulse was a weapon impulse and was handled (so the host can
    /// stop dispatch), false to let the host try its other impulse handlers (waypoints/cheats/etc.).
    ///
    /// <para>Mirrors the QC guards: a dead player has weapon-switch impulses *latched* (re-applied on respawn) by
    /// QC's <c>weapon_*_handle</c> via <c>this.impulse = imp</c>; this port simply ignores them while dead (the
    /// re-latch is a networking nicety not modeled here). Reload/drop are no-ops while dead.</para>
    /// </summary>
    public static bool Handle(Entity actor, int imp)
    {
        if (imp <= 0) return false;

        bool dead = IsDead(actor);

        // ---- weapon group (legacy shared-impulse cycling, 1-9 + 0) ----
        if (imp >= GroupFirst && imp <= GroupLast)
        {
            if (!dead) Inventory.NextWeaponOnImpulse(actor, imp);
            return true;
        }
        if (imp == Group0)
        {
            if (!dead) Inventory.NextWeaponOnImpulse(actor, 0);
            return true;
        }

        // ---- custom-priority cycling (prev/best/next, groups 0-9) ----
        if (imp >= PriorityPrevFirst && imp <= PriorityPrevFirst + 9)
        {
            if (!dead) PriorityHandle(actor, dir: -1);
            return true;
        }
        if (imp >= PriorityBestFirst && imp <= PriorityBestFirst + 9)
        {
            if (!dead) PriorityHandle(actor, dir: 0); // "best" within the priority group
            return true;
        }
        if (imp >= PriorityNextFirst && imp <= PriorityNextFirst + 9)
        {
            if (!dead) PriorityHandle(actor, dir: +1);
            return true;
        }

        // ---- direct weapon by unique id (230..253) ----
        if (imp >= ByIdFirst && imp <= ByIdLast)
        {
            if (!dead) ByIdHandle(actor, imp - ByIdFirst);
            return true;
        }

        switch (imp)
        {
            case NextById:       if (!dead) Inventory.NextWeapon(actor, 0); return true;
            case PrevById:       if (!dead) Inventory.PreviousWeapon(actor, 0); return true;
            case NextByGroup:    if (!dead) Inventory.NextWeapon(actor, 1); return true;
            case PrevByGroup:    if (!dead) Inventory.PreviousWeapon(actor, 1); return true;
            case NextByPriority: if (!dead) Inventory.NextWeapon(actor, 2); return true;
            case PrevByPriority: if (!dead) Inventory.PreviousWeapon(actor, 2); return true;
            case Last:           if (!dead) Inventory.LastWeapon(actor); return true;
            case Best:
                if (!dead && Inventory.GetBestWeapon(actor) is { } b)
                    Inventory.SwitchWeaponWithComplain(actor, b);
                return true;
            case Reload:
                if (!dead) ReloadHandle(actor);
                return true;
            case Drop:
                // QC IMPULSE(weapon_drop) (server/impulse.qc:334-351): per slot, throw the current weapon.
                // (The vehicle gate is N/A on this path — no vehicle occupancy field reaches the impulse router.)
                if (!dead) DropHandle(actor);
                return true;
        }

        return false; // not a weapon impulse
    }

    /// <summary>QC <c>weapon_priority_handle</c>: cycle by the active custom priority list (cl_weaponpriority).</summary>
    private static void PriorityHandle(Entity actor, int dir)
        => Inventory.CycleWeapon(actor, Inventory.WeaponPriority(actor), dir);

    /// <summary>
    /// QC <c>weapon_byid_handle</c> (server/impulse.qc:150): switch directly to the weapon whose
    /// <c>m_unique_impulse</c> is <c>WEP_IMPULSE_BEGIN + idx</c> — i.e. <c>Weapon_from_impulse(230 + idx)</c>
    /// (all.qh:150). That impulse is allocated in QC weapon DEFINITION order (the 19 hardcoded-impulse core
    /// weapons first, then the strcmp-sorted tail), NOT in the port's alphabetical
    /// <see cref="Registry{T}.Sort"/> id order — so <c>weapon_byid_0</c> is blaster, not the alphabetically-first
    /// weapon (arc). <see cref="WeaponOrder.WeaponByIdIndex"/> reproduces that QC unique-impulse order
    /// (weapon-local, leaving the global registry sort untouched). Uses the try-others fallback (QC
    /// <c>W_SwitchWeapon_TryOthers</c>).
    /// </summary>
    private static void ByIdHandle(Entity actor, int idx)
    {
        Weapon? w = WeaponOrder.WeaponByIdIndex(idx);
        if (w is null) return; // out of range / impulse-unreachable (QC Weapon_from_impulse → WEP_Null → no-op)
        // QC W_SwitchWeapon_TryOthers: switch; if not owned and cl_weapon_switch_fallback_to_impulse, fall back to
        // cycling that weapon's impulse group. The fallback cvar defaults off, so a plain switch matches stock.
        if (!Inventory.SwitchWeaponWithComplain(actor, w)
            && Api.Services is not null && Api.Cvars.GetFloat("cl_weapon_switch_fallback_to_impulse") != 0f)
        {
            Inventory.NextWeaponOnImpulse(actor, w.Impulse);
        }
    }

    /// <summary>
    /// QC <c>IMPULSE(weapon_drop)</c> (server/impulse.qc:334-351): throw the current weapon forward.
    /// Velocity = <c>W_CalculateProjectileVelocity(this, this.velocity, v_forward*750, false)</c>
    /// (tracing.qc:174-183) — with stock <c>g_projectiles_newton_style 0</c> this is ABSOLUTE
    /// <c>v_forward * 750 * W_WeaponSpeedFactor</c>, no player-velocity inheritance. The dual-wield
    /// <c>dv = v_right * -movedir.y</c> sideways offset collapses to <c>'0 0 0'</c> (the port drives one slot).
    /// </summary>
    private static void DropHandle(Entity actor)
    {
        XonoticGodot.Common.Math.QMath.AngleVectors(actor.Angles, out System.Numerics.Vector3 forward, out _, out _);
        // QC W_WeaponSpeedFactor: g_weaponspeedfactor (default 1) — the only live term of
        // W_CalculateProjectileVelocity with newton_style 0.
        float factor = 1f;
        if (Api.Services is not null)
        {
            float f = Api.Cvars.GetFloat("g_weaponspeedfactor");
            if (f > 0f) factor = f;
        }
        WeaponThrowing.ThrowWeapon(actor, new WeaponSlot(0), forward * (750f * factor),
            System.Numerics.Vector3.Zero, doreduce: true);
    }

    /// <summary>
    /// QC <c>weapon_reload</c> impulse: reload the weapon(s) in the active slot(s). weaponLocked gates it (a
    /// frozen/blocked player can't reload); the reload itself runs the weapon's <c>wr_reload</c> → W_Reload.
    /// </summary>
    private static void ReloadHandle(Entity actor)
    {
        Weapon? w = Inventory.CurrentWeapon(actor);
        w?.WrReload(actor, new WeaponSlot(0));
    }

    private static bool IsDead(Entity e) => e.DeadState != DeadFlag.No;
}
