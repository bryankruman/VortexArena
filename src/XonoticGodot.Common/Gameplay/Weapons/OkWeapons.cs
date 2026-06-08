// Port of the shared Overkill-weapon secondary "blaster jump" (the common wr_think tail of
// common/mutators/mutator/overkill/ok{machinegun,shotgun,nex,hmg,rpc}.qc).

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Shared helper for the five Overkill weapons. Every ok* weapon's <c>wr_think</c> opens with the same
/// secondary-fire branch: <c>if (refire_type == 1 &amp;&amp; (fire &amp; 2) &amp;&amp; time &gt;= actor.jump_interval)
/// { actor.jump_interval = time + WEP_CVAR_PRI(WEP_BLASTER, refire) * W_WeaponRateFactor(actor);
/// W_Blaster_Attack(actor, weaponentity); ... }</c> — the "Overkill blaster jump": a damage/force-less
/// Blaster shot fired on a dedicated per-PLAYER refire timer (<c>actor.jump_interval</c>), used to launch
/// yourself around. (The Blaster's damage/force are nullified by the Overkill mutator's Damage_Calculate.)
///
/// This factors that shared tail into one faithful place so the five weapon files stay 1:1 with their .qc and
/// don't each re-implement it. The blaster is fired ungated via <see cref="Blaster.FirePrimaryDirect"/> (it
/// owns its own timer here, exactly like the OK weapons own <c>jump_interval</c>); the gate is
/// <c>actor.JumpInterval</c> (QC's <c>actor.jump_interval</c>, the player edict field) advanced by the
/// blaster's primary refire scaled by the weapon rate factor.
/// </summary>
internal static class OkWeapons
{
    /// <summary>
    /// Run the shared Overkill secondary blaster-jump for <paramref name="actor"/> on this tick. No-op unless
    /// <paramref name="secondaryRefireType"/> is 1, the secondary button is held, and the player's
    /// <see cref="Entity.JumpInterval"/> gate has elapsed.
    /// </summary>
    public static void FireSecondaryBlasterJump(Entity actor, WeaponSlot slot, FireMode fire, int secondaryRefireType)
    {
        if (Api.Services is null) return;
        if (secondaryRefireType != 1 || fire != FireMode.Secondary) return;

        // QC: (fire & 2) is decided by the driver calling WrThink(Secondary) only when ATCK2 is held; mirror by
        // checking the per-slot secondary button so a non-pressing tick (the per-frame upkeep call) doesn't fire.
        if (!actor.WeaponState(slot).ButtonAttack2) return;

        float now = Api.Clock.Time;
        if (now < actor.JumpInterval) return;

        if (Weapons.ByName("blaster") is not Blaster blaster) return;

        // actor.jump_interval = time + WEP_CVAR_PRI(WEP_BLASTER, refire) * W_WeaponRateFactor(actor);
        float blasterRefire = BlasterRefire(blaster);
        actor.JumpInterval = now + blasterRefire * WeaponRate();

        // makevectors(actor.v_angle); W_Blaster_Attack(actor, weaponentity);
        blaster.FirePrimaryDirect(actor, slot);
    }

    private static float BlasterRefire(Blaster blaster)
    {
        float r = blaster.Primary.Refire;
        return r > 0f ? r : 0.7f; // g_balance_blaster_primary_refire default
    }

    private static float WeaponRate()
    {
        if (Api.Services is null) return 1f;
        float f = Api.Cvars.GetFloat("g_weaponratefactor");
        return f > 0f ? 1f / f : 1f;
    }
}
