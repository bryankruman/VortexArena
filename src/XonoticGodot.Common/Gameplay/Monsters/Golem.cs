using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;
using XonoticGodot.Common.Gameplay.Damage;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Golem — port of common/monsters/monster/golem.{qh,qc}. A powerful supermonster: a series of melee
/// punches up close, an occasional leaping ground-smash AoE, and a thrown electrified lightning chunk that
/// zaps nearby targets at range. MON_FLAG_SUPERMONSTER | MON_FLAG_MELEE | MON_FLAG_RANGED.
///
/// Registered (per task) under the NetName "golem"; this descriptor also covers the legacy
/// <c>monster_shambler</c> / <c>monster_dog</c> map spawns. Identity/size from golem.qh; health/damage/speeds
/// from monsters.cfg (g_monster_golem_*). Fully ported: the multi-swing melee combo, the delayed ground
/// smash (radius damage + knockback), and the thrown lightning chunk — which bounces, is itself shootable
/// (50 hp; destroying it detonates it early), fuses after 5s, and on detonation chains lightning zaps to
/// every target in a wide radius. Only client frame playback + the lightning arc beams are CSQC.
/// </summary>
[Monster]
public sealed class Golem : Monster
{
    // Balance — g_monster_golem_* (monsters.cfg).
    public float ClawDamage = 60f;          // g_monster_golem_attack_claw_damage
    public float SmashDamage = 50f;         // g_monster_golem_attack_smash_damage
    public float SmashForce = 100f;         // g_monster_golem_attack_smash_force
    public float SmashRange = 200f;         // g_monster_golem_attack_smash_range
    public float LightningDamage = 25f;     // g_monster_golem_attack_lightning_damage
    public float LightningDamageZap = 15f;  // g_monster_golem_attack_lightning_damage_zap
    public float LightningForce = 100f;     // g_monster_golem_attack_lightning_force
    public float LightningRadius = 50f;     // g_monster_golem_attack_lightning_radius
    public float LightningRadiusZap = 250f; // g_monster_golem_attack_lightning_radius_zap
    public float LightningSpeed = 1000f;    // g_monster_golem_attack_lightning_speed
    public float LightningSpeedUp = 150f;   // g_monster_golem_attack_lightning_speed_up
    public float SpeedWalk = 150f;          // g_monster_golem_speed_walk
    public float SpeedRun = 320f;           // g_monster_golem_speed_run
    public float SpeedStop = 300f;          // g_monster_golem_speed_stop

    public Golem()
    {
        NetName = "golem";
        DisplayName = "Golem";
        Model = "models/monsters/golem.dpm";
        StartHealth = 650f;                 // g_monster_golem_health
        Damage = 60f;                       // claw damage
        Speed = 320f;                       // run speed
    }

    // Monster_Spawn + METHOD(Golem, mr_setup) — golem.qc
    public override void Spawn(Entity e)
    {
        var st = MonsterAI.Setup(this, e);
        Api.Entities.SetSize(e, new Vector3(-24, -24, -20), new Vector3(24, 24, 88));

        st.AttackRange = 150f; // golem.qc mr_setup: attack_range = 150
        st.WalkSpeed = MonsterAI.Cvar("g_monster_golem_speed_walk", SpeedWalk);
        st.RunSpeed = MonsterAI.Cvar("g_monster_golem_speed_run", SpeedRun);
        st.StopSpeed = MonsterAI.Cvar("g_monster_golem_speed_stop", SpeedStop);
        st.DamageForceScale = MonsterAI.Cvar("g_monster_golem_damageforcescale", 0.1f);
        st.MonsterLoot = MonsterAI.CvarString("g_monster_golem_loot", "health_mega electro");

        // golem.qc mr_setup: spawn animation gating + MON_FLAG_SUPERMONSTER spawn-shield. anim_spawn '12 1 5'
        // ≈ 0.2s; gate the brain and apply a matching spawn shield (Setup already applied the default one).
        float spawnAnim = 0.2f;
        st.SpawnTime = MonsterAI.Now + spawnAnim;
        MonsterFramework.ApplyFor(MonsterFramework.SpawnShield, e, spawnAnim);
        st.Anim = MonsterAI.MonsterAnim.Idle;
    }

    // METHOD(Golem, mr_think) — golem.qc
    public override void Think(Entity e)
    {
        var st = MonsterAI.StateOf(e);
        if (st is null) return;
        MonsterAI.RunThink(e, st);
    }

    // M_Golem_Attack (golem.qc): melee combo up close; at range, either a ground smash (if within smash
    // range) or, less often, a thrown lightning chunk. Ranged is gated by golem_lastattack + grounded.
    public override void Attack(Entity e, Entity target)
    {
        var st = MonsterAI.StateOf(e);
        if (st is null) return;

        float dist = (target.Origin - e.Origin).Length();
        if (dist <= st.AttackRange)
        {
            // MONSTER_ATTACK_MELEE -> a combo of 1..3 claw swings, 0.5s apart (QC Monster_Delay swing_cnt).
            if (MonsterAI.Now < st.AttackFinished) return;
            int swings = System.Math.Clamp((int)MathF.Floor(MonsterRandom.Next() * 4f), 1, 3);
            MonsterAI.QueueCombo(e, st, swings, ClawDamage);
        }
        else
        {
            // MONSTER_ATTACK_RANGED: gated by golem_lastattack (AttackDelay) + grounded (golem.qc).
            if (MonsterAI.Now < st.AttackDelay || (e.Flags & EntFlags.OnGround) == 0) return;

            float roll = MonsterRandom.Next();
            if (roll <= 0.5f && dist <= SmashRange)
            {
                Smash(e, st, target);
            }
            else if (roll <= 0.1f && dist >= SmashRange * 1.5f)
            {
                ThrowLightning(e, st, target);
            }
        }
    }

    // M_Golem_Attack_Smash (golem.qc): leap up and ground-pound, dealing radius damage in front of the golem.
    // QC defers the blast 1.1s behind the jump animation via Monster_Delay; we do the same.
    private void Smash(Entity e, MonsterAI.MonsterState st, Entity target)
    {
        st.AttackDelay = MonsterAI.Now + 3f + MonsterRandom.Next() * 1.5f; // golem_lastattack
        float skill = MonsterAI.SkillMod(st);

        MonsterAI.QueueDelayedAttack(e, st, windUp: 1.1f, totalLock: 1.4f, action: self =>
        {
            Vector3 forward = QMath.Forward(self.Angles);
            Vector3 loc = self.Origin + forward * 50f;
            WeaponSplash.RadiusDamage(self, loc, SmashDamage * skill, SmashDamage * skill * 0.5f, SmashRange,
                self, 0, SmashForce);
            Api.Sound.Play(self, SoundChannel.Weapon, "weapons/rocket_impact.wav");
        });
    }

    // M_Golem_Attack_Lightning (golem.qc): lob a bouncing electrified chunk. It is shootable (50 hp): if a
    // player destroys it first it detonates early (W_PrepareExplosionByDamage); otherwise it fuses after 5s
    // or on contact. On detonation it does a small radius blast AND chains lightning zaps to every target in
    // a wide radius. QC defers the throw 0.6s behind the animation via Monster_Delay.
    private void ThrowLightning(Entity e, MonsterAI.MonsterState st, Entity target)
    {
        st.AttackDelay = MonsterAI.Now + 3f + MonsterRandom.Next() * 1.5f; // golem_lastattack
        float skill = MonsterAI.SkillMod(st);
        string zapDeath = DeathTypes.FromWeapon(NetName);

        MonsterAI.QueueDelayedAttack(e, st, windUp: 0.6f, totalLock: 1.1f, action: self =>
        {
            if (self.Enemy is null) return;
            MonsterAI.FaceTarget(self, self.Enemy);
            Vector3 dir = QMath.Normalize((self.Enemy.Origin + new Vector3(0, 0, 10)) - self.Origin);

            Entity gren = MonsterAI.SpawnProjectile(self, st, dir, LightningSpeed,
                damage: LightningDamage, edgeDamage: LightningDamage, radius: LightningRadius,
                force: LightningForce, deathType: zapDeath,
                moveType: MoveType.Bounce, lifetime: 5f,
                sizeMin: new Vector3(-8, -8, -8), sizeMax: new Vector3(8, 8, 8),
                shootableHealth: 50f,           // golem.qc: gren takes damage (50 hp), explodes when destroyed
                bounceFactor: 0.5f, bounceStop: 0.075f,
                onExplode: p =>
                {
                    // Chained zaps: arc to every damageable target in the wide zap radius (te_csqc_lightningarc
                    // beam is CSQC), each taking lightning_damage_zap * skillmod (QC FOREACH_ENTITY_RADIUS).
                    MonsterFramework.ChainedZaps(p, p.Owner, p.Origin, LightningRadiusZap,
                        LightningDamageZap, skill, zapDeath);
                    Api.Sound.Play(p, SoundChannel.Weapon, "weapons/electro_impact.wav");
                });
            gren.Velocity += new Vector3(0, 0, LightningSpeedUp); // QC speed_up launch component

            Api.Sound.Play(self, SoundChannel.Weapon, "weapons/electro_fire2.wav");
        });
    }
}
