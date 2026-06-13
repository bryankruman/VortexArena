using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;
using XonoticGodot.Common.Gameplay.Damage;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Spider — port of common/monsters/monster/spider.{qh,qc}. A mechanical arachnid that slows fast prey
/// with a web projectile, then closes in for high-damage bites. MON_FLAG_MELEE | MON_FLAG_RANGED | MON_FLAG_RIDE.
///
/// Identity/size from spider.qh; health/damage/speeds from monsters.cfg (g_monster_spider_*). Fully ported:
/// the bite (melee), the bouncing web projectile (ranged, within web range) and the Webbed status it applies
/// in radius on impact (halving the victim's move speed — and any webbed monster's, mirrored in MonsterAI's
/// move-speed scaling). Only client frame playback and the web projectile model are CSQC.
/// </summary>
[Monster]
public sealed class Spider : Monster
{
    // Balance — g_monster_spider_* (monsters.cfg).
    public float BiteDamage = 35f;      // g_monster_spider_attack_bite_damage
    public float BiteDelay = 1.5f;      // g_monster_spider_attack_bite_delay
    public float WebDelay = 3f;         // g_monster_spider_attack_web_delay
    public float WebSpeed = 1300f;      // g_monster_spider_attack_web_speed
    public float WebSpeedUp = 150f;     // g_monster_spider_attack_web_speed_up
    public float WebRange = 800f;       // g_monster_spider_attack_web_range
    public float WebDamageTime = 7f;    // g_monster_spider_attack_web_damagetime (Webbed duration)
    public float SpeedWalk = 400f;      // g_monster_spider_speed_walk
    public float SpeedRun = 500f;       // g_monster_spider_speed_run
    public float SpeedStop = 100f;      // g_monster_spider_speed_stop

    public Spider()
    {
        NetName = "spider";
        DisplayName = "Spider";
        Model = "models/monsters/spider.dpm";
        StartHealth = 180f;             // g_monster_spider_health
        Damage = 35f;                   // bite damage
        Speed = 500f;                   // run speed
    }

    // Monster_Spawn + METHOD(Spider, mr_setup) — spider.qc
    public override void Spawn(Entity e)
    {
        var st = MonsterAI.Setup(this, e);
        Api.Entities.SetSize(e, new Vector3(-30, -30, -25), new Vector3(30, 30, 30));

        st.WalkSpeed = MonsterAI.Cvar("g_monster_spider_speed_walk", SpeedWalk);
        st.RunSpeed = MonsterAI.Cvar("g_monster_spider_speed_run", SpeedRun);
        st.StopSpeed = MonsterAI.Cvar("g_monster_spider_speed_stop", SpeedStop);
        st.DamageForceScale = MonsterAI.Cvar("g_monster_spider_damageforcescale", 0.6f);
        st.MonsterLoot = MonsterAI.CvarString("g_monster_spider_loot", "health_medium");
    }

    // METHOD(Spider, mr_think) — spider.qc
    public override void Think(Entity e)
    {
        var st = MonsterAI.StateOf(e);
        if (st is null) return;
        MonsterAI.RunThink(e, st);
    }

    // M_Spider_Attack (spider.qc): melee bite when close, web projectile when within web range.
    public override void Attack(Entity e, Entity target)
    {
        var st = MonsterAI.StateOf(e);
        if (st is null) return;

        float dist = (target.Origin - e.Origin).Length();
        if (dist <= st.AttackRange)
        {
            // MONSTER_ATTACK_MELEE -> bite.
            st.Anim = MonsterAI.MonsterAnim.Attack;
            // spider.qc M_Spider_Attack melee: the bite is DEATH_MONSTER_SPIDER.
            MonsterAI.MeleeAttack(e, st, BiteDamage, st.AttackRange, BiteDelay,
                DeathTypes.MonsterSpider);
        }
        else if (dist <= WebRange && MonsterAI.Now >= st.AttackDelay)
        {
            // MONSTER_ATTACK_RANGED -> launch a web (SpiderAttack wr_think, fire 1).
            FireWeb(e, st, target);
        }
    }

    // M_Spider_Attack_Web (spider.qc): spawn the bouncing web projectile aimed at the enemy. On impact it
    // applies STATUSEFFECT_Webbed in a small radius (no direct damage), halving move speed for web_damagetime.
    private void FireWeb(Entity e, MonsterAI.MonsterState st, Entity target)
    {
        st.AttackDelay = MonsterAI.Now + WebDelay;
        st.AttackFinished = MonsterAI.Now + WebDelay;
        st.Anim = MonsterAI.MonsterAnim.Attack;

        MonsterAI.FaceTarget(e, target);
        Vector3 dir = QMath.Normalize((target.Origin + new Vector3(0, 0, 10)) - e.Origin);

        float webDuration = MonsterAI.Cvar("g_monster_spider_attack_web_damagetime", WebDamageTime);

        // The web does no direct impact damage (radius 0 damage); its effect is the Webbed slow on contact.
        // It bounces (bouncefactor 0.3 / bouncestop 0.05) until it hits something and pops.
        Entity web = MonsterAI.SpawnProjectile(e, st, dir, WebSpeed, damage: 0f, edgeDamage: 0f,
            radius: 25f, force: 0f, deathType: DeathTypes.MonsterSpider, // spider.qc: DEATH_MONSTER_SPIDER
            moveType: MoveType.Bounce, lifetime: 5f,
            bounceFactor: 0.3f, bounceStop: 0.05f,
            makeTrigger: true,              // spider.qc:134 PROJECTILE_MAKETRIGGER
            onExplode: p =>
            {
                // STATUSEFFECT_Webbed to everything alive in radius except other spiders (QC web explode).
                foreach (Entity it in Api.Entities.FindInRadius(p.Origin, 25f))
                {
                    if (it == p) continue;
                    if (it.TakeDamage == DamageMode.No) continue;
                    if (it.DeadState != DeadFlag.No || it.Health <= 0f) continue;
                    if (it.NetName == "spider") continue; // spiders are immune to their own web
                    MonsterFramework.ApplyFor(MonsterFramework.Webbed, it, webDuration, 1f, p.Owner);
                }
            });
        // QC W_SetupProjVelocity_Explicit(..., web_speed, web_speed_up, ...) adds an upward launch component.
        web.Velocity += new Vector3(0, 0, MonsterAI.Cvar("g_monster_spider_attack_web_speed_up", WebSpeedUp));

        Api.Sound.Play(e, SoundChannel.Weapon, "weapons/electro_fire2.wav");
    }
}
