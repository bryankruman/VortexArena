using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;
using XonoticGodot.Common.Gameplay.Damage;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Wyvern — port of common/monsters/monster/wyvern.{qh,qc}. A fragile flying reptile that glides after
/// prey and lobs explosive, burning fireballs from a distance. MONSTER_TYPE_FLY | MON_FLAG_RANGED | MON_FLAG_RIDE.
///
/// Identity/size from wyvern.qh; health/damage/speeds from monsters.cfg (g_monster_wyvern_*). Fully ported:
/// the flying movement (FL_FLY + MOVETYPE_FLY, vertical chase), the fireball (radius damage + knockback) for
/// both the melee and ranged branches with its 1s wind-up, and the post-impact burning (Fire_AddDamage) in
/// radius. Only client frame playback (incl. the falling deadthink anim) is CSQC.
/// </summary>
[Monster]
public sealed class Wyvern : Monster
{
    // Balance — g_monster_wyvern_* (monsters.cfg).
    public float FireballDamage = 50f;      // g_monster_wyvern_attack_fireball_damage
    public float FireballEdgeDamage = 20f;  // g_monster_wyvern_attack_fireball_edgedamage
    public float FireballForce = 50f;       // g_monster_wyvern_attack_fireball_force
    public float FireballRadius = 120f;     // g_monster_wyvern_attack_fireball_radius
    public float FireballSpeed = 1200f;     // g_monster_wyvern_attack_fireball_speed
    public float FireballDamageTime = 2f;   // g_monster_wyvern_attack_fireball_damagetime (burn duration)
    public float SpeedWalk = 120f;          // g_monster_wyvern_speed_walk
    public float SpeedRun = 250f;           // g_monster_wyvern_speed_run
    public float SpeedStop = 300f;          // g_monster_wyvern_speed_stop

    public Wyvern()
    {
        NetName = "wyvern";
        DisplayName = "Wyvern";
        Model = "models/monsters/wyvern.dpm";
        StartHealth = 150f;                 // g_monster_wyvern_health
        Damage = 50f;                       // fireball damage
        Speed = 250f;                       // run speed
    }

    // Monster_Spawn + METHOD(Wyvern, mr_setup) — wyvern.qc. MONSTER_TYPE_FLY: fly movetype + FL_FLY.
    public override void Spawn(Entity e)
    {
        var st = MonsterAI.Setup(this, e);
        Api.Entities.SetSize(e, new Vector3(-30, -30, -48), new Vector3(30, 30, 30));

        // MONSTER_TYPE_FLY (wyvern.qh spawnflags): override the base STEP movetype with flight, and allow
        // vertical chase/wander (MONSTERFLAG_FLY_VERTICAL) so it can climb/dive toward prey.
        e.Flags |= EntFlags.Fly;
        e.MoveType = MoveType.Fly;
        e.Gravity = 0f;
        e.SpawnFlags |= MonsterAI.MonsterFlag_FlyVertical;

        st.WalkSpeed = MonsterAI.Cvar("g_monster_wyvern_speed_walk", SpeedWalk);
        st.RunSpeed = MonsterAI.Cvar("g_monster_wyvern_speed_run", SpeedRun);
        st.StopSpeed = MonsterAI.Cvar("g_monster_wyvern_speed_stop", SpeedStop);
        st.DamageForceScale = MonsterAI.Cvar("g_monster_wyvern_damageforcescale", 0.6f);
        st.MonsterLoot = MonsterAI.CvarString("g_monster_wyvern_loot", "cells");
    }

    // METHOD(Wyvern, mr_think) — wyvern.qc
    public override void Think(Entity e)
    {
        var st = MonsterAI.StateOf(e);
        if (st is null) return;
        MonsterAI.RunThink(e, st);
    }

    // M_Wyvern_Attack (wyvern.qc): both melee and ranged answer with a fireball, after a 1s wind-up delay.
    public override void Attack(Entity e, Entity target)
    {
        var st = MonsterAI.StateOf(e);
        if (st is null) return;
        if (MonsterAI.Now < st.AttackFinished) return;

        // QC: Monster_Delay(actor, 0, 1, M_Wyvern_Attack_Fireball) — wind up for 1s, then fire; the attack
        // itself is gated for anim_finished (1.2) + 0.2.
        st.Anim = MonsterAI.MonsterAnim.Attack;
        MonsterAI.QueueDelayedAttack(e, st, windUp: 1f, totalLock: 1.4f, action: self =>
        {
            if (self.Enemy is not null)
                FireFireball(self, MonsterAI.StateOf(self)!, self.Enemy);
        });
    }

    // M_Wyvern_Attack_Fireball (wyvern.qc): a flying missile that explodes (radius damage) on contact, then
    // ignites everything in its blast radius (Fire_AddDamage) for a burning damage-over-time.
    private void FireFireball(Entity e, MonsterAI.MonsterState st, Entity target)
    {
        MonsterAI.FaceTarget(e, target);
        Vector3 dir = QMath.Normalize((target.Origin + new Vector3(0, 0, 10)) - e.Origin);

        float burnTime = MonsterAI.Cvar("g_monster_wyvern_attack_fireball_damagetime", FireballDamageTime);
        float skill = MonsterAI.SkillMod(st);
        Entity owner = e;
        string deathType = DeathTypes.FromWeapon(NetName);

        Entity missile = MonsterAI.SpawnProjectile(e, st, dir, FireballSpeed,
            damage: FireballDamage, edgeDamage: FireballEdgeDamage, radius: FireballRadius,
            force: FireballForce, deathType: deathType,
            moveType: MoveType.FlyMissile, lifetime: 5f,
            sizeMin: new Vector3(-6, -6, -6), sizeMax: new Vector3(6, 6, 6),
            onExplode: p =>
            {
                // QC: Fire_AddDamage(it, own, 5*skillmod, fireball_damagetime, deathtype) in radius — ignite.
                foreach (Entity it in Api.Entities.FindInRadius(p.Origin, FireballRadius))
                {
                    if (it == owner) continue;
                    if (it.TakeDamage != DamageMode.Aim) continue; // QC: only DAMAGE_AIM targets burn
                    MonsterFramework.AddFireDamage(it, owner, 5f * skill, burnTime, deathType);
                }
            });
        missile.AVelocity = new Vector3(300, 300, 300);

        Api.Sound.Play(e, SoundChannel.Weapon, "weapons/electro_fire.wav");
    }
}
