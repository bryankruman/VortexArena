using XonoticGodot.Common.Framework;
using XonoticGodot.Net; // WepentViewState — its DATA struct lives in THIS (.Common) assembly under the
                        // XonoticGodot.Net namespace; only the wire codec (WepentViewCodec) lives in .Net.

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The single shared resolver of <see cref="WepentViewState"/> off an AUTHORITATIVE <see cref="Player"/>.
///
/// <para>This is the one source of truth for the per-weapon HUD-ring scratch (QC the networked
/// <c>wepent.*</c> fields: <c>vortex_charge</c> / <c>vortex_chargepool_ammo</c> / <c>clip_load</c> /
/// <c>clip_size</c> / <c>hagar_load</c> / <c>minelayer_mines</c> / <c>arc_heat_percent</c>), replacing the
/// per-weapon <c>if/else</c> ladder that was duplicated three ways:</para>
/// <list type="bullet">
///   <item><c>ServerNet.ResolveOwnerWeaponRings</c> — resolves the OWNER block scalars for a remote client.</item>
///   <item><c>NetGame.UpdateCrosshairWeaponRings</c> (host branch) — feeds the local crosshair panel.</item>
///   <item>the new per-player <c>WepentView</c> entity-state block — feeds OTHER players' / spectatee rings.</item>
/// </list>
/// <para>Those three drifting (the registry's noted host/owner/per-player divergence) is exactly the bug this
/// kills: all three now delegate here, so they read one identical truth off the authoritative player.</para>
///
/// <para><b>Engine-free (ADR-0008):</b> this lives in <c>.Common</c>, which must not touch Godot. The two
/// pieces of state that would otherwise require the engine — the live-mine count (a world scan) and the
/// current sim time — are supplied by the caller as a <paramref name="mineCounter"/> delegate and a
/// <paramref name="now"/> value, so the resolver itself makes no Godot/engine/world calls.</para>
/// </summary>
public static class WepentResolver
{
    /// <summary>
    /// Resolve the per-weapon view-state for the held weapon of an authoritative <paramref name="p"/>.
    /// Returns <see cref="WepentViewState.None"/> (the "no live weapon → no ring" sentinel) when the player
    /// has no current weapon, is dead, or is observing — matching every existing feeder's early-out.
    /// </summary>
    /// <param name="p">The authoritative player whose held-weapon scratch is read.</param>
    /// <param name="mineCounter">
    /// Counts <paramref name="p"/>'s live mines (QC <c>W_MineLayer_Count</c> — the per-frame <c>g_mines</c>
    /// world scan). Supplied by the caller because the count isn't cached on the slot and the scan needs the
    /// world service (engine-side); pass a delegate that walks the mine entities owned by the player.
    /// </param>
    /// <param name="now">The current sim time (engine clock), used by the Arc overheat-decay expression.</param>
    public static WepentViewState Resolve(Player p, System.Func<Player, int> mineCounter, float now)
    {
        Weapon? active = Inventory.CurrentWeapon(p);
        if (active is null || p.IsDead || p.IsObserver)
            return WepentViewState.None; // no live weapon → every field stays at its no-data default

        WeaponSlotState wst = p.WeaponState(new WeaponSlot(0));
        string net = active.NetName; // non-null (defaults to "")

        WepentViewState v = WepentViewState.None;

        // Vortex / Vaporizer / Overkill-Nex charge ring (QC 482-496): vortex_charge / vortex_chargepool_ammo,
        // both [0,1]. The Oknex charge reads the SAME slot fields (per the registry note), so when this is a
        // *nex variant we stamp the Oknex twins identically to keep the wire symmetric for the consumer.
        if (net is "vortex" or "vaporizer" || net.Contains("nex"))
        {
            v.VortexCharge = wst.VortexCharge;
            v.VortexChargePool = wst.VortexChargePoolAmmo;
            if (net.Contains("nex"))
            {
                v.OknexCharge = wst.VortexCharge;
                v.OknexChargePool = wst.VortexChargePoolAmmo;
            }
        }

        // Reload / ammo ring (QC 536-548): clip_load / clip_size, for any weapon with a clip. clip_size == 0
        // means the weapon has no clip → the consumer hides the ring. clip_load == -1 is the "needs reload"
        // sentinel and is preserved on the wire (signed short).
        if (wst.ClipSize > 0)
        {
            v.ClipLoad = wst.ClipLoad;
            v.ClipSize = wst.ClipSize;
        }

        // Hagar burst-load ring (QC 529-535): hagar_load. The load_max cap is NOT on this per-player wire
        // (unlike OwnerWeaponRings); the consumer clamps against the CrosshairPanel default.
        if (net == "hagar")
            v.HagarLoad = wst.HagarLoad;

        // Mine Layer count ring (QC 522-528): minelayer_mines (current live-mine count). The count isn't
        // cached on the slot, so the caller's delegate runs the g_mines scan (engine-side).
        if (net == "minelayer")
            v.MinelayerMines = mineCounter(p);

        // Arc overheat ring (QC 474, 550-556 + Arc_GetHeat_Percent arc.qc:55-68). While a beam is live the
        // ring is beam_heat/overheat_max; after release the latched arc_overheat timestamp decays the ring,
        // SCALED by arc_cooldown (the cooldown_speed captured when the beam stopped). overheatMax is read off
        // the active Arc descriptor instance — exactly as ResolveOwnerWeaponRings does — keeping .Common
        // engine-free (Arc lives here too, so no descriptor accessor is needed).
        if (net == "arc" && active is Arc arc && arc.Beam.OverheatMax > 0f)
        {
            float pct = wst.BeamHeat > 0f
                ? wst.BeamHeat / arc.Beam.OverheatMax
                : (wst.ArcOverheat > now ? (wst.ArcOverheat - now) / arc.Beam.OverheatMax * wst.ArcCooldown : 0f);
            v.ArcHeat = System.Math.Clamp(pct, 0f, 1f);
        }

        // BeamState (Phase-2 consumer): bit0 = arc beam active. bit2 (electro secondary beam active) has no
        // dedicated slot flag yet — left 0, to be wired in Phase-2 when the electro beam state is tracked.
        int beamState = 0;
        if (net == "arc" && wst.BeamHeat > 0f) beamState |= 1 << 0;
        // bit2 (electro beam): no WeaponSlotState flag exists yet → 0 (Phase-2).
        v.BeamState = beamState;

        // ViewmodelFrame: not resolved this phase (Phase-2 consumer) → left 0 (idle/none) via None default.

        return v;
    }
}
