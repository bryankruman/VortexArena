using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Machinegun Turret — port of common/turrets/turret/machinegun.{qh,qc} (+ machinegun_weapon.qc). A hitscan
/// bullet emplacement: targets players in line of sight, leads them, and rapid-fires bullets in bursts. The
/// cheapest, weakest turret. Identity/hitbox from machinegun.qh; balance from turrets.cfg
/// (<c>g_turrets_unit_machinegun_*</c>).
///
/// Mechanic: hitscan (TUR_FLAG_HITSCAN) — each shot is an instant <see cref="Api.Trace"/> + damage, no
/// projectile (machinegun_weapon.qc fireBullet). Volley of 5 with a 0.5s pause between bursts.
/// </summary>
[Turret]
public sealed class MachinegunTurret : Turret
{
    // --- balance (turrets.cfg g_turrets_unit_machinegun_*) ---
    private const float ShotDamage = 10f;
    private const float ShotForce = 20f;
    private const float ShotSpread = 0.015f;
    private const float ShotSpeed = 34920f;      // near-hitscan; drives lead compensation
    private const float ShotRefire = 0.1f;
    private const int ShotVolly = 5;
    private const float ShotVollyRefire = 0.5f;
    private const float TargetRange = 4500f;
    private const float TargetRangeMin = 2f;
    private const float TargetRangeOptimal = 1000f;
    private const float AmmoMax = 1500f;
    private const float AmmoRecharge = 75f;
    private const float AimSpeed = 120f;
    private const float AimMaxPitch = 25f;
    private const float AimMaxRot = 360f;
    private const float FireTolerance = 25f;

    // QC machinegun scoring biases (turrets.cfg): range 0.25, same 0.25, angle 0.5, player 1, missile 0.
    private const float RangeBias = 0.25f, SameBias = 0.25f, AngleBias = 0.5f, PlayerBias = 1f, MissileBias = 0f;

    // QC machinegun.qc tr_setup: players, range-limited, team-checked, angle-limited. Hitscan, lead aim.
    private const int Select = TurretAI.SelectPlayers | TurretAI.SelectRangeLimits
                             | TurretAI.SelectTeamCheck | TurretAI.SelectAngleLimits;

    public MachinegunTurret()
    {
        NetName = "machinegun";
        DisplayName = "Machinegun Turret";
        Model = "models/turrets/base.md3";
        StartHealth = 256f;
        Range = TargetRange;
    }

    public override void Spawn(Entity e)
        => TurretSpawn.Init(this, e, new Vector3(-32f, -32f, 0f), new Vector3(32f, 32f, 64f),
            AmmoMax, AmmoRecharge, ShotVolly);

    public override void Think(Entity e)
    {
        var p = new TurretParams(Select, TargetRangeMin, TargetRange, ShotDamage, ShotRefire,
            AimSpeed, FireTolerance, lead: true, ShotVolly, ShotVollyRefire,
            rangeOptimal: TargetRangeOptimal, shotSpeed: ShotSpeed, aimMaxPitch: AimMaxPitch, aimMaxRot: AimMaxRot,
            shotTimeCompensate: true, zPredict: true,
            rangeBias: RangeBias, sameBias: SameBias, angleBias: AngleBias,
            missileBias: MissileBias, playerBias: PlayerBias,
            trackType: TurretAI.TrackFluidInertia, trackAccelPitch: 0.4f, trackAccelRot: 0.9f, trackBlendRate: 0.2f);
        TurretAI.RunCombat(e, in p, Attack);
    }

    public override bool ValidTarget(Entity self, Entity target)
        => TurretAI.ValidTarget(self, target, Select, TargetRangeMin, TargetRange);

    // METHOD(MachineGunTurretAttack, wr_think) — machinegun_weapon.qc: fireBullet along the muzzle dir with
    // deterministic spread + knockback force. Only the muzzle flash / tracer / headshake are client-render.
    private void Attack(Entity turret, Entity enemy)
    {
        TurretState st = TurretAI.State(turret);
        Vector3 dir = QMath.Normalize(st.AimPos - st.ShotOrg);
        if (dir == Vector3.Zero) dir = QMath.Forward(TurretAI.HeadWorldAngles(turret));

        // machinegun_weapon.qc: fireBullet with DEATH_TURRET_MACHINEGUN.
        TurretCombat.FireBullet(turret, st.ShotOrg, dir, ShotSpread, ShotDamage, ShotForce, DeathTypes.TurretMachinegun);

        if (Api.Services is not null)
            Api.Sound.Play(turret, SoundChannel.Weapon, "weapons/uzi_fire.wav");

        // NOTE (client-render): EFFECT_BULLET tracer, MDL_MACHINEGUN_MUZZLEFLASH at tag_fire, head frame anim.
        // The server-side fire (machinegun_weapon.qc) is done above.
    }
}
