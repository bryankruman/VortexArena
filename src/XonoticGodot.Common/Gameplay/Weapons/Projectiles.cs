using XonoticGodot.Common.Framework;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Shared projectile setup helpers — the Godot-free successor to the QC projectile macros in
/// <c>server/weapons/common.qh</c>.
/// </summary>
public static class Projectiles
{
    // SUPERCONTENTS bits the projectile hit mask uses (DP collision.h). Common can't reference the Engine's
    // SuperContents class (Engine depends on Common, not the reverse), so mirror the bit values here — exactly
    // as PlayerPhysics already does for the liquid bits. These MUST match XonoticGodot.Engine.Collision.SuperContents.
    private const int SuperContentsSolid  = 0x00000001;
    private const int SuperContentsBody   = 0x02000000;
    private const int SuperContentsCorpse = 0x20000000;

    /// <summary>
    /// Port of <c>PROJECTILE_MAKETRIGGER</c> (server/weapons/common.qh:33): make <paramref name="e"/> a
    /// projectile that is TRANSPARENT to player movement but still collides with the world, player/monster
    /// bodies and corpses.
    ///
    /// In Darkplaces a player's own movement trace masks <c>SOLID|BODY|PLAYERCLIP</c> — deliberately WITHOUT
    /// <c>CORPSE</c>. By giving the projectile <see cref="Solid.Corpse"/> (so its body brush sits in the CORPSE
    /// content channel) the player's hull passes straight through it instead of being blocked by it. The
    /// explicit <see cref="Entity.DpHitContentsMask"/> = <c>SOLID|BODY|CORPSE</c> keeps the projectile's OWN
    /// trace hitting bodies and corpses (SOLID_CORPSE alone would drop the CORPSE bit per
    /// SV_GenericHitSuperContentsMask). This is what stops a rocket/grenade colliding with — and detonating on —
    /// its firer (notably the predicting local player on a listen server, whose carrier entity is a DISTINCT
    /// instance from the projectile's server-side <see cref="Entity.Owner"/>, so the owner trace-exception
    /// cannot protect it).
    ///
    /// Deferred vs QC: <c>clipgroup</c> (so a burst of same-owner projectiles don't collide with each other) and
    /// the <c>g_projectiles_interact</c> variants — neither affects the firer-self-hit this fixes.
    /// </summary>
    public static void MakeTrigger(Entity e)
    {
        e.Solid = Solid.Corpse;
        e.DpHitContentsMask = SuperContentsSolid | SuperContentsBody | SuperContentsCorpse;
    }
}
