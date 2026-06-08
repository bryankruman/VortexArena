using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Phaser Cannon Turret — port of common/turrets/turret/phaser.{qh,qc} (+ phaser_weapon.qc). A sniper-class
/// hitscan turret (TUR_FLAG_SNIPER | TUR_FLAG_HITSCAN) that fires a continuous energy beam which deals damage
/// over its duration and slows players caught in it. Heavy per-burst damage (100), slow refire. Identity/hitbox
/// from phaser.qh; balance from turrets.cfg (<c>g_turrets_unit_phaser_*</c>).
///
/// Ported faithfully: the SUSTAINED beam — firing spawns a beam that re-traces along the head's aim every frame
/// for <c>shot_speed</c> (4) seconds, applying <c>shot_dmg / (shot_speed/frametime)</c> per tick and SLOWING
/// the target each tick (QC FireImoBeam <c>velocity *= 0.75</c>, util.qc). The turret keeps tracking while the
/// beam runs and only resets its refire clock when the beam ends (phaser_weapon.qc beam_think). A short
/// disability status effect is layered on the slow if the catalog provides one. Only the beam model visual +
/// charge/discharge head frames are client render.
/// </summary>
[Turret]
public sealed class PhaserTurret : Turret
{
    // --- balance (turrets.cfg g_turrets_unit_phaser_*) ---
    private const float ShotDamage = 100f;       // total damage spread over the beam duration
    private const float ShotRadius = 8f;          // beam thickness
    private const float ShotSpeed = 4f;           // beam DURATION in seconds (QC overloads shot_speed)
    private const float ShotForce = 5f;
    private const float ShotRefire = 4f;
    private const float VelFactor = 0.75f;        // per-tick slow (QC FireImoBeam f_velfactor)
    private const float TargetRange = 3000f;
    private const float TargetRangeMin = 0f;
    private const float TargetRangeOptimal = 1500f;
    private const float AmmoMax = 2000f;
    private const float AmmoRecharge = 25f;
    private const float AimSpeed = 300f;
    private const float AimMaxPitch = 30f;
    private const float AimMaxRot = 360f;
    private const float FireTolerance = 100f;

    // QC phaser.qc tr_setup keeps the sv_turrets default select: LOS, players, range, team, angle. Lead aim.
    private const int Select = TurretAI.SelectLos | TurretAI.SelectPlayers | TurretAI.SelectRangeLimits
                             | TurretAI.SelectTeamCheck | TurretAI.SelectAngleLimits;

    public PhaserTurret()
    {
        NetName = "phaser";
        DisplayName = "Phaser Cannon";
        Model = "models/turrets/base.md3";
        StartHealth = 500f;
        Range = TargetRange;
    }

    public override void Spawn(Entity e)
        => TurretSpawn.Init(this, e, new Vector3(-32f, -32f, 0f), new Vector3(32f, 32f, 64f),
            AmmoMax, AmmoRecharge, shotVolly: 0);

    public override void Think(Entity e)
    {
        var p = new TurretParams(Select, TargetRangeMin, TargetRange, ShotDamage, ShotRefire,
            AimSpeed, FireTolerance, lead: true,
            rangeOptimal: TargetRangeOptimal, shotSpeed: 35000f /*beam is hitscan for lead*/,
            aimMaxPitch: AimMaxPitch, aimMaxRot: AimMaxRot,
            shotTimeCompensate: true, zPredict: true, trackType: TurretAI.TrackFluidInertia);
        TurretAI.RunCombat(e, in p, Attack);
    }

    public override bool ValidTarget(Entity self, Entity target)
        => TurretAI.ValidTarget(self, target, Select, TargetRangeMin, TargetRange);

    // phaser_weapon.qc wr_think: spawn the sustained beam. It thinks each frame for shot_speed seconds, ticking
    // beam damage + slow, and holds the turret's refire until it ends.
    private void Attack(Entity turret, Entity enemy)
    {
        if (Api.Services is null) return;
        float now = Api.Clock.Time;

        // The beam owns the refire: it re-parks attack_finished each frame while running, then sets the real
        // shot_refire when it ends (QC beam_think). The now + p.Refire that RunCombat.Fire sets right after this
        // is harmlessly overwritten by the first BeamThink tick this same frame.

        Entity beam = Api.Entities.Spawn();
        beam.ClassName = "PhaserTurret_beam";
        beam.Owner = turret;
        beam.Enemy = enemy;
        beam.Solid = Solid.Not;
        beam.MoveType = MoveType.None;
        float endTime = now + ShotSpeed;
        float perTickDamage = ShotDamage / (ShotSpeed / System.Math.Max(Api.Clock.FrameTime, 0.0001f));

        beam.Think = self => BeamThink(self, endTime, perTickDamage);
        beam.NextThink = now;

        Api.Sound.Play(turret, SoundChannel.Weapon, "weapons/electro_fire.wav");

        // NOTE (client-render): the MDL_TUR_PHASER_BEAM visual scaled to the hit distance + the fireflag
        // charge/discharge head frames. The server-side beam (per-tick damage, phaser_weapon.qc) is done above.
    }

    // beam_think (phaser_weapon.qc): re-trace along the head's aim each frame, damage + slow the first hit, and
    // tear down + reset the turret refire when the duration elapses.
    private void BeamThink(Entity beam, float endTime, float perTickDamage)
    {
        Entity turret = beam.Owner!;
        float now = Api.Services is not null ? Api.Clock.Time : 0f;

        if (now > endTime || turret.IsFreed || turret.DeadState != DeadFlag.No)
        {
            // End of beam: set the real refire and remove (QC beam_think termination).
            if (!turret.IsFreed) TurretAI.State(turret).AttackFinished = now + ShotRefire;
            if (Api.Services is not null) Api.Entities.Remove(beam);
            return;
        }

        TurretState st = TurretAI.State(turret);
        st.AttackFinished = endTime + 0.001f;   // hold refire past the beam while it runs (QC attack_finished = time + frametime)
        st.ShotOrg = TurretAI.ShotOrigin(turret);
        Vector3 dir = QMath.Forward(TurretAI.HeadWorldAngles(turret));
        Vector3 start = st.ShotOrg;
        Vector3 end = start + dir * TargetRange;

        // FireImoBeam (util.qc): a thick beam to the first hit; damage + slow it (velocity *= velfactor).
        if (Api.Services is not null)
        {
            Vector3 half = new Vector3(ShotRadius, ShotRadius, ShotRadius);
            TraceResult tr = Api.Trace.Trace(start, -half, half, end, MoveFilter.Normal, turret);
            Entity? hit = tr.Ent;
            if (hit is not null && hit.TakeDamage != DamageMode.No)
            {
                Combat.Damage(hit, turret, turret, perTickDamage, DeathTypes.FromWeapon(NetName),
                    tr.EndPos, dir * ShotForce);
                hit.Velocity *= VelFactor;     // QC FireImoBeam slow
                ApplySlow(hit);                // optional status-effect layer
            }
        }

        beam.NextThink = now;
    }

    /// <summary>Layer a brief slow/disability status on the beam target if the catalog offers one (QC slow is the velocity scale; this just augments it).</summary>
    private static void ApplySlow(Entity target)
    {
        StatusEffectDef? slow = StatusEffectsCatalog.ByName("buff_disability") ?? StatusEffectsCatalog.Frozen;
        if (slow is not null && slow != StatusEffectsCatalog.Frozen)
            StatusEffectsCatalog.Apply(target, slow, duration: 0.2f, strength: 1f);
    }
}
