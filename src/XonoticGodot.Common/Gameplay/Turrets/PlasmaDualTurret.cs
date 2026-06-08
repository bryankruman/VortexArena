using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Dual Plasma Cannon Turret — port of common/turrets/turret/plasma_dual.{qh,qc}. The stronger sibling of the
/// <see cref="PlasmaTurret"/> (QC <c>CLASS(DualPlasmaTurret, PlasmaTurret)</c>): same electric-ball splash
/// projectile and damage, but two cannons firing at nearly double the rate (refire 0.35 vs 0.6). Identity from
/// plasma_dual.qh; balance from turrets.cfg (<c>g_turrets_unit_plasma_dual_*</c>). Inherits the plasma weapon
/// (<c>SUPER(PlasmaTurret).tr_attack</c>).
/// </summary>
[Turret]
public sealed class PlasmaDualTurret : PlasmaTurret
{
    // --- balance overrides (turrets.cfg g_turrets_unit_plasma_dual_*) ---
    // Same shot_dmg/radius/speed/force as Plasma (reuse the base consts); these differ:
    private const float DualRefire = 0.35f;
    private const float DualTargetRange = 3000f;
    private const float DualTargetRangeMin = 80f;
    private const float DualAmmoMax = 640f;
    private const float DualAmmoRecharge = 40f;
    private const float DualAimSpeed = 100f;
    private const float DualFireTolerance = 200f;

    public PlasmaDualTurret()
    {
        NetName = "plasma_dual";
        DisplayName = "Dual Plasma Cannon";
        Model = "models/turrets/base.md3";
        StartHealth = 500f;
        Range = DualTargetRange;
    }

    public override void Spawn(Entity e)
        => TurretSpawn.Init(this, e, new Vector3(-32f, -32f, 0f), new Vector3(32f, 32f, 64f),
            DualAmmoMax, DualAmmoRecharge, shotVolly: 0);

    public override void Think(Entity e)
    {
        var p = MakeParams(DualTargetRangeMin, DualTargetRange, DualRefire, DualAimSpeed, DualFireTolerance);
        TurretAI.RunCombat(e, in p, Attack);
    }

    public override bool ValidTarget(Entity self, Entity target)
        => TurretAI.ValidTarget(self, target, Select, DualTargetRangeMin, DualTargetRange);

    // SUPER(PlasmaTurret).tr_attack — identical plasma ball (same shot_dmg/radius/speed/spread). The "two
    // cannons" is purely a model/tag detail; the faster DualRefire already doubles the effective rate, matching
    // QC balance. Alternating the two barrel tags + the dual head-frame anim are client-render only.
    protected override void Attack(Entity turret, Entity enemy)
    {
        base.Attack(turret, enemy);
        // NOTE (client-render): alternate the two barrel tags (tag_fire on plasmad.md3) per shot + the dual
        // head-frame animation. The fire logic (incl. the inherited instagib railgun variant) is in base.Attack.
    }
}
