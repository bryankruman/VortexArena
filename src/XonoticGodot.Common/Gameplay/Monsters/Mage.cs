using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;
using XonoticGodot.Common.Gameplay.Damage;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Mage — port of common/monsters/monster/mage.{qh,qc}. A nanotech support-caster: it throws homing
/// electric spikes, shoves nearby foes with an explosive push, shields itself, heals allies, and teleports
/// behind its target. MON_FLAG_MELEE | MON_FLAG_RANGED.
///
/// Identity/size from mage.qh; health/damage/speeds + ability chances from monsters.cfg (g_monster_mage_*).
/// Fully ported: the homing spike (with the Seeker-style guidance, one-in-flight at a time), the close-range
/// explosive push, the teleport-behind-target, the self+ally Shield status, and the radius heal with its
/// skin variants (health / ammo / armor). Only client frame playback and the spike's CSQC model are CSQC.
/// </summary>
[Monster]
public sealed class Mage : Monster
{
    // Balance — g_monster_mage_* (monsters.cfg).
    public float SpikeDamage = 45f;     // g_monster_mage_attack_spike_damage
    public float SpikeRadius = 60f;     // g_monster_mage_attack_spike_radius
    public float SpikeDelay = 2f;       // g_monster_mage_attack_spike_delay
    public float SpikeSpeedMax = 370f;  // g_monster_mage_attack_spike_speed_max
    public float SpikeAccel = 480f;     // g_monster_mage_attack_spike_accel
    public float SpikeDecel = 480f;     // g_monster_mage_attack_spike_decel
    public float SpikeTurnrate = 0.65f; // g_monster_mage_attack_spike_turnrate
    public float SpikeChance = 0.45f;   // g_monster_mage_attack_spike_chance
    public float PushChance = 0.7f;     // g_monster_mage_attack_push_chance
    public float PushDamage = 25f;      // g_monster_mage_attack_push_damage
    public float PushRadius = 150f;     // g_monster_mage_attack_push_radius
    public float PushForce = 300f;      // g_monster_mage_attack_push_force
    public float PushDelay = 1f;        // g_monster_mage_attack_push_delay
    public float TeleportChance = 0.1f; // g_monster_mage_attack_teleport_chance
    public float TeleportDelay = 5f;    // g_monster_mage_attack_teleport_delay
    public float TeleportRandom = 0.4f; // g_monster_mage_attack_teleport_random (chance of the random-relocate branch)
    public float TeleportRange = 1200f; // g_monster_mage_attack_teleport_random_range
    public float HealAllies = 20f;      // g_monster_mage_heal_allies
    public float HealRange = 250f;      // g_monster_mage_heal_range
    public float HealMinHealth = 250f;  // g_monster_mage_heal_minhealth
    public float HealDelay = 1.5f;      // g_monster_mage_heal_delay
    public float ShieldTime = 3f;       // g_monster_mage_shield_time
    public float ShieldDelay = 7f;      // g_monster_mage_shield_delay
    public float ShieldBlock = 0.8f;    // g_monster_mage_shield_blockpercent
    public float SpeedWalk = 250f;      // g_monster_mage_speed_walk
    public float SpeedRun = 400f;       // g_monster_mage_speed_run
    public float SpeedStop = 50f;       // g_monster_mage_speed_stop

    public Mage()
    {
        NetName = "mage";
        DisplayName = "Mage";
        Model = "models/monsters/nanomage.dpm";
        StartHealth = 400f;             // g_monster_mage_health
        Damage = 45f;                   // spike damage
        Speed = 400f;                   // run speed
    }

    // Monster_Spawn + METHOD(Mage, mr_setup) — mage.qc
    public override void Spawn(Entity e)
    {
        var st = MonsterAI.Setup(this, e);
        Api.Entities.SetSize(e, new Vector3(-16, -16, -24), new Vector3(16, 16, 55));

        st.WalkSpeed = MonsterAI.Cvar("g_monster_mage_speed_walk", SpeedWalk);
        st.RunSpeed = MonsterAI.Cvar("g_monster_mage_speed_run", SpeedRun);
        st.StopSpeed = MonsterAI.Cvar("g_monster_mage_speed_stop", SpeedStop);
        st.DamageForceScale = MonsterAI.Cvar("g_monster_mage_damageforcescale", 0.5f);
        st.MonsterLoot = MonsterAI.CvarString("g_monster_mage_loot", "health_big");
    }

    // METHOD(Mage, mr_think) — mage.qc: heal/shield decision pass, THEN the shared chase/attack loop.
    public override void Think(Entity e)
    {
        var st = MonsterAI.StateOf(e);
        if (st is null) return;

        // Heal trigger: when our own health is low OR a nearby ally needs help, pulse a heal (QC mr_think).
        if (MonsterRandom.Next() < 0.5f && MonsterAI.Now >= st.AttackFinished
            && (e.Health < HealMinHealth || NeedsHelpNearby(e, st)))
        {
            HealPulse(e, st);
        }

        // Shield trigger: in combat, hurt, off cooldown, not already shielded -> raise the shield.
        if (MonsterRandom.Next() < 0.5f && e.Enemy is not null && MonsterAI.Now >= st.ShieldDelay
            && e.Health < st.MaxHealth && !StatusEffectsCatalog.Has(e, MonsterFramework.Shield))
        {
            RaiseShield(e, st);
        }

        MonsterAI.RunThink(e, st);
    }

    // M_Mage_Attack (mage.qc): close range -> push (chance); longer range -> teleport (chance) or homing spike.
    public override void Attack(Entity e, Entity target)
    {
        var st = MonsterAI.StateOf(e);
        if (st is null) return;
        if (MonsterAI.Now < st.AttackFinished) return;

        float dist = (target.Origin - e.Origin).Length();
        if (dist <= st.AttackRange)
        {
            // MONSTER_ATTACK_MELEE -> explosive push.
            if (MonsterRandom.Next() <= PushChance)
                Push(e, st, target);
        }
        else
        {
            // MONSTER_ATTACK_RANGED -> teleport behind the target, or a homing spike (one in flight at a time).
            if (MonsterRandom.Next() <= TeleportChance)
                Teleport(e, st, target);
            else if (st.ActiveSpike is null && MonsterRandom.Next() <= SpikeChance)
                FireSpike(e, st, target);
        }
    }

    // M_Mage_Attack_Spike (mage.qc): a homing electric sphere. Only one may be in flight at a time
    // (QC .mage_spike). The Seeker-style guidance is driven each frame in the projectile's think.
    private void FireSpike(Entity e, MonsterAI.MonsterState st, Entity target)
    {
        st.AttackFinished = MonsterAI.Now + SpikeDelay;
        st.State = MonsterAI.MonsterState_AttackMelee; // QC freezes movement while firing the spike
        st.Anim = MonsterAI.MonsterAnim.Attack;

        MonsterAI.FaceTarget(e, target);
        Vector3 dir = QMath.Normalize((target.Origin + new Vector3(0, 0, 10)) - e.Origin);

        float turn = MonsterAI.Cvar("g_monster_mage_attack_spike_turnrate", SpikeTurnrate);
        float accel = MonsterAI.Cvar("g_monster_mage_attack_spike_accel", SpikeAccel);
        float decel = MonsterAI.Cvar("g_monster_mage_attack_spike_decel", SpikeDecel);
        float speedMax = MonsterAI.Cvar("g_monster_mage_attack_spike_speed_max", SpikeSpeedMax);
        bool smart = MonsterAI.Cvar("g_monster_mage_attack_spike_smart", 1f) != 0f;
        float smartMin = MonsterAI.Cvar("g_monster_mage_attack_spike_smart_mindist", 600f);
        float traceMin = MonsterAI.Cvar("g_monster_mage_attack_spike_smart_trace_min", 1000f);
        float traceMax = MonsterAI.Cvar("g_monster_mage_attack_spike_smart_trace_max", 2500f);

        float deathTime = MonsterAI.Now + 7f; // QC missile.ltime = time + 7
        Entity spike = MonsterAI.SpawnProjectile(e, st, dir, 400f, // QC spike launches at 400, accelerates to max
            damage: SpikeDamage, edgeDamage: SpikeDamage * 0.5f, radius: SpikeRadius,
            force: 0f, deathType: DeathTypes.MonsterMage, // mage.qc M_Mage_Attack_Spike: DEATH_MONSTER_MAGE
            moveType: MoveType.FlyMissile, lifetime: 7f,
            sizeMin: Vector3.Zero, sizeMax: Vector3.Zero,
            // M_Mage_Attack_Spike_Explode: grenade_impact on detonation (SND_MageSpike_IMPACT).
            onExplode: p => Api.Sound.Play(p, SoundChannel.ShotsAuto, "weapons/grenade_impact.wav"),
            onThink: p =>
            {
                if (!MonsterFramework.HomeProjectile(p, deathTime, turn, accel, decel, speedMax,
                        smart, smartMin, traceMin, traceMax))
                {
                    // QC: explode (with HITTYPE_SPLASH) when guidance gives up.
                    p.Health = 0f; p.TakeDamage = DamageMode.Yes; // signal SpawnProjectile to detonate
                }
            });
        // QC muzzle: makevectors(this.angles); origin + v_forward*14 + '0 0 30' + v_right*-14 (the shared spawner
        // placed it at origin+dir*14, missing the up/right offsets). Re-derive from the mage's facing.
        QMath.AngleVectors(e.Angles, out Vector3 fwd, out Vector3 right, out _);
        Api.Entities.SetOrigin(spike, e.Origin + fwd * 14f + new Vector3(0, 0, 30) + right * -14f);
        spike.Enemy = target;
        spike.AVelocity = new Vector3(300, 300, 300);
        st.ActiveSpike = spike;

        Api.Sound.Play(e, SoundChannel.Weapon, "weapons/electro_fire.wav");
    }

    // M_Mage_Attack_Push (mage.qc): explosive AoE shove around the mage.
    private void Push(Entity e, MonsterAI.MonsterState st, Entity target)
    {
        st.AttackFinished = MonsterAI.Now + PushDelay;
        st.State = MonsterAI.MonsterState_AttackMelee;
        st.Anim = MonsterAI.MonsterAnim.Attack;

        // mage.qc M_Mage_Attack_Push: the explosive shove is DEATH_MONSTER_MAGE.
        WeaponSplash.RadiusDamage(e, e.Origin, PushDamage, PushDamage, PushRadius,
            e, 0, PushForce, deathTag: DeathTypes.MonsterMage);

        Api.Sound.Play(e, SoundChannel.Weapon, "weapons/tagexp1.wav");
    }

    // M_Mage_Attack_Teleport (mage.qc): two variants. With teleport_random chance the mage blinks to a random
    // clear spot within a +/-teleport_random_range box (escape); otherwise it blinks just behind the target's
    // facing for a sneak attack. Both set the teleport cooldown.
    private void Teleport(Entity e, MonsterAI.MonsterState st, Entity target)
    {
        if ((target.Origin - e.Origin).Length() > TeleportRange) return;

        // QC: if (teleport_random && random() <= teleport_random) MoveToRandomLocationWithinBounds(...).
        float randChance = MonsterAI.Cvar("g_monster_mage_attack_teleport_random", TeleportRandom);
        if (randChance != 0f && MonsterRandom.Next() <= randChance && RandomRelocate(e))
        {
            // Face the target (yaw only) and set the cooldown — the random branch is self-contained.
            Vector3 ra = QMath.VecToAngles(target.Origin - e.Origin);
            e.Angles = new Vector3(0f, ra.Y, 0f);
            st.AttackFinished = MonsterAI.Now + TeleportDelay;
            return;
        }

        if ((target.Flags & EntFlags.OnGround) == 0) return; // QC: target must be grounded for the behind-blink

        // Trace from the target's center backwards along its facing by 200u; teleport there if clear.
        Vector3 back = -QMath.Forward(target.Angles);
        Vector3 center = target.Origin + (target.Mins + target.Maxs) * 0.5f;
        TraceResult tr = Api.Trace.Trace(center, e.Mins, e.Maxs, center + back * 200f, MoveFilter.NoMonsters, e);
        if (tr.Fraction < 1f) return;

        Api.Entities.SetOrigin(e, tr.EndPos);
        Vector3 a = QMath.VecToAngles(target.Origin - e.Origin);
        e.Angles = new Vector3(-a.X, a.Y, 0f);
        e.Velocity *= 0.5f;
        st.AttackFinished = MonsterAI.Now + TeleportDelay;
        // QC M_Mage_Attack_Teleport plays no sound (only Send_Effect(EFFECT_SPAWN) particles, a CSQC concern).
    }

    // QC MoveToRandomLocationWithinBounds(this, absmin-extra, absmax+extra, ...): sample a random spot inside a
    // box that extends teleport_random_range past the mage's bbox, drop it to solid ground, and place the mage
    // there if the destination is open (not embedded in solid). Returns false if no clear spot was found.
    private bool RandomRelocate(Entity e)
    {
        float range = MonsterAI.Cvar("g_monster_mage_attack_teleport_random_range", TeleportRange);
        Vector3 absmin = e.Origin + e.Mins, absmax = e.Origin + e.Maxs;
        Vector3 lo = absmin - new Vector3(range, range, range);
        Vector3 hi = absmax + new Vector3(range, range, range);

        // QC's builtin retries internally (maxtries 256); a handful of biased samples suffices for the port.
        for (int attempt = 0; attempt < 32; attempt++)
        {
            float rx = lo.X + MonsterRandom.Next() * (hi.X - lo.X);
            float ry = lo.Y + MonsterRandom.Next() * (hi.Y - lo.Y);
            float rz = lo.Z + MonsterRandom.Next() * (hi.Z - lo.Z);

            // Drop straight down to find ground (trace the full box height of the search region).
            Vector3 from = new Vector3(rx, ry, rz);
            Vector3 to = new Vector3(rx, ry, lo.Z);
            TraceResult down = Api.Trace.Trace(from, e.Mins, e.Maxs, to, MoveFilter.NoMonsters, e);
            if (down.StartSolid || down.AllSolid) continue; // sample began inside a wall
            if (down.Fraction >= 1f) continue;              // never hit ground — bottomless

            // Verify the mage's bbox actually fits at the landing spot (settle test).
            Vector3 spot = down.EndPos;
            TraceResult fit = Api.Trace.Trace(spot, e.Mins, e.Maxs, spot, MoveFilter.NoMonsters, e);
            if (fit.StartSolid || fit.AllSolid) continue;

            Api.Entities.SetOrigin(e, spot);
            // QC M_Mage_Attack_Teleport plays no sound here either (only Send_Effect(EFFECT_SPAWN) particles).
            return true;
        }
        return false;
    }

    // M_Mage_Defend_Heal (mage.qc): heal pulse over a radius. Heals players by skin variant (0 health,
    // 1 ammo, 2 armor) and always heals injured ally monsters. Self-heal is the always-available case.
    private void HealPulse(Entity e, MonsterAI.MonsterState st)
    {
        int skin = (int)e.Skin;
        bool washealed = false;
        float regenStableHp = MonsterAI.Cvar("g_balance_health_regenstable", 100f);
        float regenStableArmor = MonsterAI.Cvar("g_balance_armor_regenstable", 50f);

        foreach (Entity it in Api.Entities.FindInRadius(e.Origin, HealRange))
        {
            if (!HealCheck(e, it, skin)) continue;
            washealed = true;

            if ((it.Flags & EntFlags.Client) != 0)
            {
                switch (skin)
                {
                    case 0:
                        it.GiveResourceWithLimit(ResourceType.Health, HealAllies, regenStableHp);
                        break;
                    case 1: // ammo top-up
                        if (it.AmmoCells > 0f) it.GiveResourceWithLimit(ResourceType.Cells, 1, MonsterAI.Cvar("g_pickup_cells_max", 180f));
                        if (it.AmmoRockets > 0f) it.GiveResourceWithLimit(ResourceType.Rockets, 1, MonsterAI.Cvar("g_pickup_rockets_max", 160f));
                        if (it.AmmoShells > 0f) it.GiveResourceWithLimit(ResourceType.Shells, 2, MonsterAI.Cvar("g_pickup_shells_max", 60f));
                        if (it.AmmoBullets > 0f) it.GiveResourceWithLimit(ResourceType.Bullets, 5, MonsterAI.Cvar("g_pickup_nails_max", 320f));
                        break;
                    case 2: // armor repair
                        if (it.ArmorValue < regenStableArmor)
                            it.GiveResourceWithLimit(ResourceType.Armor, HealAllies, regenStableArmor);
                        break;
                }
            }
            else
            {
                // Ally monster: heal toward its own max (no resource limit on non-players).
                it.GiveResourceWithLimit(ResourceType.Health, HealAllies, it.MaxHealth);
            }
        }

        if (washealed)
        {
            st.AttackFinished = MonsterAI.Now + HealDelay;
            st.AnimFinished = MonsterAI.Now + 1.5f;
            st.State = MonsterAI.MonsterState_AttackMelee;
            st.Anim = MonsterAI.MonsterAnim.Attack;
            // QC M_Mage_Defend_Heal plays no dedicated sound (only Send_Effect HEALING/AMMO_REGEN/ARMOR_REPAIR, CSQC).
        }
    }

    // M_Mage_Defend_Heal_Check (mage.qc): is targ a valid heal target for this mage's current skin?
    private bool HealCheck(Entity self, Entity targ, int skin)
    {
        // QC M_Mage_Defend_Heal_Check has NO self special-case: the mage (a monster) flows through the !IS_PLAYER
        // branch below, so it self-heals toward max_health (400) on EVERY skin, not just skin 0 below 100 HP.
        if (targ.IsFreed) return false;
        if (MonsterAI.IsTeamplay && self.Team != 0f && targ.Team != self.Team && targ != self.GoalEntity) return false;
        if (targ.Health <= 0f) return false;
        if (StatusEffectsCatalog.Frozen != null && StatusEffectsCatalog.Has(targ, StatusEffectsCatalog.Frozen)) return false;

        if ((targ.Flags & EntFlags.Client) == 0)
            // ally monster: heal if injured
            return (targ.Flags & EntFlags.Monster) != 0 && targ.Health < targ.MaxHealth;

        // player target: don't heal a shielded player; otherwise depends on skin.
        if (StatusEffectsCatalog.Has(targ, MonsterFramework.Shield)) return false;
        return skin switch
        {
            0 => targ.Health < MonsterAI.Cvar("g_balance_health_regenstable", 100f),
            1 => (targ.AmmoCells > 0f && targ.AmmoCells < MonsterAI.Cvar("g_pickup_cells_max", 180f))
                 || (targ.AmmoRockets > 0f && targ.AmmoRockets < MonsterAI.Cvar("g_pickup_rockets_max", 160f))
                 || (targ.AmmoBullets > 0f && targ.AmmoBullets < MonsterAI.Cvar("g_pickup_nails_max", 320f))
                 || (targ.AmmoShells > 0f && targ.AmmoShells < MonsterAI.Cvar("g_pickup_shells_max", 60f)),
            2 => targ.ArmorValue < MonsterAI.Cvar("g_balance_armor_regenstable", 50f),
            _ => false,
        };
    }

    // Whether any player or ally monster in heal range currently needs help (QC mr_think need_help scan).
    private bool NeedsHelpNearby(Entity e, MonsterAI.MonsterState st)
    {
        int skin = (int)e.Skin;
        foreach (Entity it in Api.Entities.FindInRadius(e.Origin, HealRange))
        {
            if (it == e) continue;
            if (HealCheck(e, it, skin)) return true;
        }
        return false;
    }

    // M_Mage_Defend_Shield (mage.qc): brief damage-blocking shield on self. STATUSEFFECT_Shield does the
    // actual damage reduction (read in MonsterAI.MarkPain); armor is bumped while up and restored on expiry.
    private void RaiseShield(Entity e, MonsterAI.MonsterState st)
    {
        float time = MonsterAI.Cvar("g_monster_mage_shield_time", ShieldTime);
        MonsterFramework.ApplyFor(MonsterFramework.Shield, e, time, 1f, e);
        st.ShieldDelay = MonsterAI.Now + MonsterAI.Cvar("g_monster_mage_shield_delay", ShieldDelay);

        // Bump armor while shielded and schedule its restore (QC SetResourceExplicit(RES_ARMOR, blockpercent)).
        st.ShieldRestoreArmor = e.ArmorValue;
        e.ArmorValue = MonsterAI.Cvar("g_monster_mage_shield_blockpercent", ShieldBlock);
        st.ShieldExpire = MonsterAI.Now + time;

        st.AttackFinished = MonsterAI.Now + 1f; // short cooldown after shielding
        st.Anim = MonsterAI.MonsterAnim.Attack;
        // QC M_Mage_Defend_Shield plays no sound.
    }
}
