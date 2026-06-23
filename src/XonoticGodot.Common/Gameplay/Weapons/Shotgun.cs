using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Shotgun — port of common/weapons/weapon/shotgun.{qh,qc}. A hitscan weapon: primary fire sprays a
/// fan of pellets (each an independent <see cref="WeaponFiring.FireBullet"/> trace) that fall off with
/// distance; secondary is a short-range melee "slap" that sweeps a few traces in front of the actor.
///
/// Identity/attributes from shotgun.qh; balance from bal-wep-xonotic.cfg (g_balance_shotgun_*).
/// This port covers the full pellet fan (spread, solid penetration, distance falloff, knockback force),
/// the melee swing (swing-arc damage scaling + multi-hit dedupe, HITTYPE_SECONDARY slap obituary, per-trace
/// woosh fx), shell-casing eject, the impact ricochet ping, and the out-of-ammo auto-melee fallback.
/// Left out (weapon-frame/online concerns): the multi-frame melee sweep + melee_delay wind-up timing, the
/// staged triple-shot (secondary==2) frame sequence, and antilag take-back on the melee/setup traces.
/// </summary>
[Weapon]
public sealed class Shotgun : Weapon
{
    /// <summary>Primary-fire balance block — QC WEP_CVAR_PRI(WEP_SHOTGUN, *).</summary>
    public struct PrimaryBalance
    {
        public float Ammo;              // g_balance_shotgun_primary_ammo (shells per shot)
        public float Animtime;          // g_balance_shotgun_primary_animtime
        public float Bullets;           // g_balance_shotgun_primary_bullets (pellet count)
        public float Damage;            // g_balance_shotgun_primary_damage (per pellet)
        public float Force;             // g_balance_shotgun_primary_force
        public float Refire;            // g_balance_shotgun_primary_refire
        public float SolidPenetration;  // g_balance_shotgun_primary_solidpenetration
        public float Spread;            // g_balance_shotgun_primary_spread
    }

    /// <summary>Secondary-fire (melee) balance block — QC WEP_CVAR_SEC(WEP_SHOTGUN, *).</summary>
    public struct SecondaryBalance
    {
        public int   Secondary;            // g_balance_shotgun_secondary (0=off, 1=melee, 2=triple-shot)
        public float Animtime;             // g_balance_shotgun_secondary_animtime
        public float Damage;               // g_balance_shotgun_secondary_damage (vs players)
        public float Force;                // g_balance_shotgun_secondary_force
        public float Refire;               // g_balance_shotgun_secondary_refire
        public bool  MeleeBlockedByFiring; // g_balance_shotgun_secondary_melee_blockedbyfiring (default 0)
        public float MeleeDelay;           // g_balance_shotgun_secondary_melee_delay
        public float MeleeMultihit;        // g_balance_shotgun_secondary_melee_multihit
        public float MeleeNonplayerDamage; // g_balance_shotgun_secondary_melee_nonplayerdamage
        public float MeleeRange;           // g_balance_shotgun_secondary_melee_range
        public float MeleeSwingSide;       // g_balance_shotgun_secondary_melee_swing_side
        public float MeleeSwingUp;         // g_balance_shotgun_secondary_melee_swing_up
        public float MeleeTime;            // g_balance_shotgun_secondary_melee_time
        public float MeleeTraces;          // g_balance_shotgun_secondary_melee_traces
    }

    public PrimaryBalance Primary;
    public SecondaryBalance Secondary;

    /// <summary>QC IT_UNLIMITED_AMMO item bit (mirrors the local const used by WeaponFireGate/Vortex).</summary>
    private const int ItUnlimitedAmmo = 1 << 0;


    public Shotgun()
    {
        NetName = "shotgun";
        AmmoType = ResourceType.Shells;   // QC ammo_type
        DisplayName = "Shotgun";
        Impulse = 2;
        // WEP_FLAG_NORMAL | WEP_FLAG_RELOADABLE | WEP_TYPE_HITSCAN | WEP_TYPE_MELEE_SEC | WEP_FLAG_BLEED
        SpawnFlags = WeaponFlags.Normal | WeaponFlags.Reloadable | WeaponFlags.TypeHitscan
                   | WeaponFlags.TypeMeleeSec | WeaponFlags.Bleed;
        Color = new Vector3(0.518f, 0.608f, 0.659f);
        ViewModel = "h_shotgun.iqm";  // MDL_SHOTGUN_VIEW
        WorldModel = "v_shotgun.md3"; // MDL_SHOTGUN_WORLD
        ItemModel = "g_shotgun.md3";  // MDL_SHOTGUN_ITEM
    }

    public override void Configure()
    {
        Primary.Ammo = Bal("g_balance_shotgun_primary_ammo", 1f);
        Primary.Animtime = Bal("g_balance_shotgun_primary_animtime", 0.2f);
        Primary.Bullets = Bal("g_balance_shotgun_primary_bullets", 12f);
        Primary.Damage = Bal("g_balance_shotgun_primary_damage", 4f);
        Primary.Force = Bal("g_balance_shotgun_primary_force", 15f);
        Primary.Refire = Bal("g_balance_shotgun_primary_refire", 0.75f);
        Primary.SolidPenetration = Bal("g_balance_shotgun_primary_solidpenetration", 3.8f);
        Primary.Spread = Bal("g_balance_shotgun_primary_spread", 0.12f);

        Secondary.Secondary = BalInt("g_balance_shotgun_secondary", 1);
        Secondary.Animtime = Bal("g_balance_shotgun_secondary_animtime", 1.15f);
        Secondary.Damage = Bal("g_balance_shotgun_secondary_damage", 70f);
        Secondary.Force = Bal("g_balance_shotgun_secondary_force", 200f);
        Secondary.Refire = Bal("g_balance_shotgun_secondary_refire", 1.25f);
        Secondary.MeleeBlockedByFiring = BalBool("g_balance_shotgun_secondary_melee_blockedbyfiring", false);
        Secondary.MeleeDelay = Bal("g_balance_shotgun_secondary_melee_delay", 0.25f);
        Secondary.MeleeMultihit = Bal("g_balance_shotgun_secondary_melee_multihit", 1f);
        Secondary.MeleeNonplayerDamage = Bal("g_balance_shotgun_secondary_melee_nonplayerdamage", 40f);
        Secondary.MeleeRange = Bal("g_balance_shotgun_secondary_melee_range", 120f);
        Secondary.MeleeSwingSide = Bal("g_balance_shotgun_secondary_melee_swing_side", 120f);
        Secondary.MeleeSwingUp = Bal("g_balance_shotgun_secondary_melee_swing_up", 30f);
        Secondary.MeleeTime = Bal("g_balance_shotgun_secondary_melee_time", 0.15f);
        Secondary.MeleeTraces = Bal("g_balance_shotgun_secondary_melee_traces", 10f);
    }

    // METHOD(Shotgun, wr_think) — common/weapons/weapon/shotgun.qc
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        var st = actor.WeaponState(slot);
        float rate = WeaponRateFactor();

        if (fire == FireMode.Primary)
        {
            // QC handles the refire SEPARATELY from the shared ATTACK_FINISHED so a melee can follow a primary
            // blast straight away: the primary passes ANIMTIME (not refire) into weapon_prepareattack — so the
            // shared ATTACK_FINISHED is held for only ~0.2s — and parks the REAL 0.75 refire in
            // shotgun_primarytime, a private gate it checks first.
            //   if (time >= shotgun_primarytime)
            //   if (weapon_prepareattack(..., false, WEP_CVAR_PRI(animtime))) { ...; shotgun_primarytime = ...; }
            if (Api.Clock.Time >= st.ShotgunPrimaryTime
                && PrepareAttack(actor, slot, fire, attackTime: Primary.Animtime))
            {
                Attack(actor, slot);
                st.ShotgunPrimaryTime = Api.Clock.Time + Primary.Refire * rate;
            }
        }
        else if (fire == FireMode.Secondary)
        {
            if (Secondary.Secondary == 2)
            {
                // secondary==2 triple-shot: same private-timer pattern as primary (QC passes alt_animtime into
                // weapon_prepareattack and parks the real refire in shotgun_primarytime).
                if (Api.Clock.Time >= st.ShotgunPrimaryTime
                    && PrepareAttack(actor, slot, fire, attackTime: Secondary.Animtime))
                {
                    Attack(actor, slot);
                    st.ShotgunPrimaryTime = Api.Clock.Time + Secondary.Refire * rate;
                }
            }
        }

        // Melee (secondary == 1): QC routes it after the primary/triple-shot block, gated by its OWN
        // weapon_prepareattack(..., true, WEP_CVAR_SEC(refire)) on the shared ATTACK_FINISHED — which the
        // primary only held for animtime — plus the melee_blockedbyfiring guard (default 0, so NOT gated by
        // shotgun_primarytime). That is what lets the slap land immediately after a blast.
        //
        // QC also auto-melees on an EMPTY primary press (shotgun.qc:343): a non-bot whose shells AND clip are
        // exhausted (and has no IT_UNLIMITED_AMMO) slaps instead of dry-firing. So the gate triggers on EITHER
        // a secondary press OR an out-of-ammo primary press.
        bool unlimited = actor.UnlimitedAmmo || (actor.Items & ItUnlimitedAmmo) != 0;
        bool emptyPrimaryAutoMelee = fire == FireMode.Primary
            && actor is not Player { IsBot: true }
            && actor.GetResource(AmmoType) <= 0f
            && st.ClipLoad == 0
            && !unlimited;
        if (Secondary.Secondary == 1
            && (fire == FireMode.Secondary || emptyPrimaryAutoMelee)
            && (!Secondary.MeleeBlockedByFiring || Api.Clock.Time >= st.ShotgunPrimaryTime)
            && PrepareAttack(actor, slot, FireMode.Secondary))
        {
            Melee(actor, slot);
        }
        // Multi-frame scheduling of the triple-shot (W_Shotgun_Attack3_Frame1/2) is a weapon-frame loop concern
        // driven by the weapon-system tick, not the blast.
    }

    // Refire/animtime from the (cvar-seeded) balance blocks (primary fan vs secondary melee/triple).
    public override float RefireFor(FireMode fire) => fire == FireMode.Secondary ? Secondary.Refire : Primary.Refire;
    public override float AnimtimeFor(FireMode fire) => fire == FireMode.Secondary ? Secondary.Animtime : Primary.Animtime;

    // W_Shotgun_Attack — fire `bullets` hitscan pellets in a spread pattern. shotgun.qc
    private void Attack(Entity actor, WeaponSlot slot)
    {
        // W_DecreaseAmmo(thiswep, actor, ammo)
        actor.TakeResource(AmmoType, Primary.Ammo);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out Vector3 right, out Vector3 up);
        // fired credit: damage * bullets — the whole volley's potential (QC shotgun.qc:19-20).
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward,
            wep: this, maxDamage: Primary.Damage * Primary.Bullets, recoil: 5f);

        int deathType = RegistryId;
        int pellets = (int)Primary.Bullets;

        // QC: when spread_pattern_scale > 0, pellets lay out via W_CalculateSpreadPattern (a deterministic
        // fan); otherwise each pellet gets independent W_CalculateSpread random spread. We use the random
        // per-pellet path (g_balance_shotgun_primary_spread_pattern defaults off in xonotic balance).
        for (int i = 0; i < pellets; ++i)
        {
            WeaponFiring.FireBullet(actor, shot.Origin, shot.Dir, WeaponFiring.MaxShotDistance, Primary.Damage,
                deathType, Primary.Spread, Primary.SolidPenetration, force: Primary.Force);
            Vector3 impEnd = shot.Origin + shot.Dir * WeaponFiring.MaxShotDistance;
            TraceResult impTr = Api.Trace.Trace(shot.Origin, Vector3.Zero, Vector3.Zero, impEnd, MoveFilter.WorldOnly, actor);
            // w_backoff = the impact surface normal (trace_plane_normal), -force_dir fallback when no hit.
            Vector3 backoff = impTr.PlaneNormal.LengthSquared() > 1e-6f ? impTr.PlaneNormal : -shot.Dir;
            // QC wr_impacteffect (CSQC shotgun.qc:400-410): EFFECT_SHOTGUN_IMPACT at w_org + w_backoff*2 plus
            // the 5%-chance / 0.25s-throttled SND_RIC_RANDOM ricochet — both routed through the shared seam now
            // (BulletImpactFx owns the impact puff + the previously-dead SoundSystem.PlayRic).
            WeaponFiring.BulletImpactFx(actor, impTr.EndPos, backoff, "SHOTGUN_IMPACT");
        }

        Api.Sound.Play(actor, SoundChannel.WeaponAuto, "weapons/shotgun_fire.wav");
        EffectEmitter.Emit("SHOTGUN_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);

        // Casing eject — QC W_Shotgun_Attack (shotgun.qc:78-83): SpawnCasing when g_casings>=1 (default 2). The
        // shared seam gates on g_casings>=2 and computes the QC view-frame eject velocity; a Shell casing.
        WeaponFiring.EjectCasing(actor, shot.Origin, WeaponFiring.CasingType.Shell);
    }

    // W_Shotgun_Attack2 + W_Shotgun_Melee_Think — a single melee swing in front of the actor. shotgun.qc
    private void Melee(Entity actor, WeaponSlot slot)
    {
        Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/shotgun_melee.wav");

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out Vector3 right, out Vector3 up);
        Vector3 eye = actor.Origin + actor.ViewOfs;

        // QC's W_Shotgun_Melee_Think spreads the melee_traces sweep across the swing arc over melee_time
        // (one batch of traces per server frame, with swing_alreadyhit dedupe persisting across frames). We
        // collapse that to a single pass over the whole arc — gameplay-equivalent for a one-shot swing:
        // swing_factor runs +1..-1 across the arc and scales the damage by min(1, swing_factor + 1).
        int traces = (int)Secondary.MeleeTraces;
        if (traces < 1) traces = 1;

        // QC tags every melee Damage() with WEP_SHOTGUN.m_id | HITTYPE_SECONDARY (shotgun.qc:152), which is what
        // selects WEAPON_SHOTGUN_MURDER_SLAP over WEAPON_SHOTGUN_MURDER in wr_killmessage. The int-deathtype
        // ApplyDamage seam can't carry the HITTYPE bit (it resolves the id to a bare weapon tag), so the slap
        // routes through Combat.Damage directly with the secondary-tagged deathtype, mirroring how Crylink's
        // secondary threads HITTYPE_SECONDARY into its damage tag.
        string slapDeathType = DeathTypes.WithHitType(DeathTypes.FromWeapon(NetName), DeathTypes.Secondary);

        Entity? lastHit = null;
        for (int i = 0; i < traces; ++i)
        {
            float swingFactor = (1f - (i / Secondary.MeleeTraces)) * 2f - 1f;
            Vector3 meleePath = up * Secondary.MeleeSwingUp + right * Secondary.MeleeSwingSide;
            Vector3 targPos = eye + meleePath * swingFactor + forward * Secondary.MeleeRange;

            TraceResult tr = Api.Trace.Trace(eye, Vector3.Zero, Vector3.Zero, targPos, MoveFilter.Normal, actor);
            // QC Send_Effect(EFFECT_SHOTGUN_WOOSH, trace_endpos, -melee_path, 1) per swing trace (shotgun.qc:126),
            // plus the swing sound is already played once above; the woosh fx is emitted at each sweep endpoint.
            WeaponFiring.MeleeWoosh(actor, tr.EndPos, -meleePath, "SHOTGUN_WOOSH", swingSound: null);

            Entity? victim = tr.Ent;
            if (tr.Fraction < 1f && victim is not null && victim.TakeDamage != DamageMode.No && victim != lastHit)
            {
                // is_player ? damage : melee_nonplayerdamage, scaled by min(1, swing_factor + 1).
                bool isPlayer = (victim.Flags & EntFlags.Client) != 0;
                float baseDmg = isPlayer ? Secondary.Damage : Secondary.MeleeNonplayerDamage;
                float swingDamage = baseDmg * MathF.Min(1f, swingFactor + 1f);

                Vector3 force = forward * Secondary.Force;
                Combat.Damage(victim, actor, actor, swingDamage, slapDeathType, eye, force);

                if (Secondary.MeleeMultihit != 0f)
                {
                    lastHit = victim; // allow multiple hits per swing, but not the same target twice
                    continue;
                }
                break; // single hit per swing
            }
        }
    }

    // METHOD(Shotgun, wr_checkammo1) — shotgun.qc
    public bool CheckAmmoPrimary(Entity actor) => actor.GetResource(AmmoType) >= Primary.Ammo;

    // METHOD(Shotgun, wr_checkammo2) — shotgun.qc. 1 = melee (no ammo), 2 = triple-shot (uses primary ammo),
    // else secondary unavailable.
    public bool CheckAmmoSecondary(Entity actor) => Secondary.Secondary switch
    {
        1 => true,
        2 => actor.GetResource(AmmoType) >= Primary.Ammo,
        _ => false,
    };
}
