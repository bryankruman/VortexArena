using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Arc — port of common/weapons/weapon/arc.{qh,qc}. A hitscan weapon whose primary is a continuous
/// lightning beam that sweeps to follow the player's aim (curving toward the crosshair, limited by a max
/// angle) and deals damage-per-second to whatever it touches, while heating up toward an overheat limit.
/// Its secondary depends on the <c>bolt</c> cvar: with bolt enabled (the default) it fires a short burst of
/// bouncing energy bolts that explode with radius damage; with bolt disabled, secondary is a higher-damage
/// "burst" variant of the beam.
///
/// Identity/attributes from arc.qh; balance from bal-wep-xonotic.cfg (g_balance_arc_*).
/// This port covers the per-frame beam DPS, the curving beam direction (beam_dir blended toward the aim,
/// limited by beam_maxangle/returnspeed), the heat / overheat-jam / cooldown model, exponential distance
/// falloff, teammate healing (health + armor), the burst-beam variant, the bouncing bolt secondary with
/// shoot-down, and the splash damage. Only the bezier multi-segment rendering and CSQC beam networking are
/// left out (the single trace along beam_dir is the gameplay-equivalent of the segmented sweep).
/// </summary>
[Weapon]
public sealed class Arc : Weapon
{
    /// <summary>Beam (primary) balance — QC WEP_CVAR(WEP_ARC, beam_* / burst_* / heat/cooldown).</summary>
    public struct BeamBalance
    {
        public float Ammo;             // beam_ammo (cells per second)
        public float Animtime;         // beam_animtime
        public float Damage;           // beam_damage (per second vs players)
        public float DegreesPerSegment;// beam_degreespersegment
        public float DistancePerSegment;// beam_distancepersegment
        public float FalloffHalflifeDist;// beam_falloff_halflifedist
        public float FalloffMaxDist;   // beam_falloff_maxdist
        public float FalloffMinDist;   // beam_falloff_mindist
        public float Force;            // beam_force (per second)
        public float HealingAmax;      // beam_healing_amax
        public float HealingAps;       // beam_healing_aps
        public float HealingHmax;      // beam_healing_hmax
        public float HealingHps;       // beam_healing_hps
        public float Heat;             // beam_heat (heat/sec)
        public float MaxAngle;         // beam_maxangle
        public float NonPlayerDamage;  // beam_nonplayerdamage (per second vs non-players)
        public float Range;            // beam_range
        public float Refire;           // beam_refire
        public float ReturnSpeed;      // beam_returnspeed
        public float Tightness;        // beam_tightness

        public float BurstAmmo;        // burst_ammo
        public float BurstDamage;      // burst_damage (per second, secondary beam mode)
        public float BurstHealingAps;  // burst_healing_aps
        public float BurstHealingHps;  // burst_healing_hps
        public float BurstHeat;        // burst_heat

        public float Cooldown;         // cooldown
        public float CooldownRelease;  // cooldown_release
        public float OverheatMax;      // overheat_max
        public float OverheatMin;      // overheat_min
    }

    /// <summary>Bolt (secondary, when bolt=1) balance — QC WEP_CVAR(WEP_ARC, bolt_*).</summary>
    public struct BoltBalance
    {
        public int   BounceCount;      // bolt_bounce_count
        public float BounceExplode;    // bolt_bounce_explode
        public float BounceLifetime;   // bolt_bounce_lifetime
        public int   Count;            // bolt_count (bolts per burst)
        public float DamageForceScale; // bolt_damageforcescale
        public float Damage;           // bolt_damage
        public float EdgeDamage;       // bolt_edgedamage
        public float Force;            // bolt_force
        public float Health;           // bolt_health (shootable bolt hp)
        public float Lifetime;         // bolt_lifetime
        public float Radius;           // bolt_radius
        public float Refire;           // bolt_refire
        public float Refire2;          // bolt_refire2
        public float Speed;            // bolt_speed
        public float Spread;           // bolt_spread
        public float Ammo;             // bolt_ammo (cells per burst)
    }

    public BeamBalance Beam;
    public BoltBalance Bolt;

    /// <summary>g_balance_arc_bolt — when 1 (default), secondary fires bolts; when 0, secondary is burst beam.</summary>
    public bool BoltEnabled = true;


    public Arc()
    {
        NetName = "arc";
        AmmoType = ResourceType.Cells;   // QC ammo_type
        DisplayName = "Arc";
        Impulse = 3;
        // WEP_FLAG_MUTATORBLOCKED | WEP_TYPE_HITSCAN
        SpawnFlags = WeaponFlags.MutatorBlocked | WeaponFlags.TypeHitscan;
        Color = new Vector3(0.463f, 0.612f, 0.886f);
        ViewModel = "h_arc.iqm";  // MDL_ARC_VIEW
        WorldModel = "v_arc.md3"; // MDL_ARC_WORLD
        ItemModel = "g_arc.md3";  // MDL_ARC_ITEM
    }

    public override void Configure()
    {
        Beam.Ammo = Bal("g_balance_arc_beam_ammo", 6f);
        Beam.Animtime = Bal("g_balance_arc_beam_animtime", 0.1f);
        Beam.Damage = Bal("g_balance_arc_beam_damage", 100f);
        Beam.DegreesPerSegment = Bal("g_balance_arc_beam_degreespersegment", 1f);
        Beam.DistancePerSegment = Bal("g_balance_arc_beam_distancepersegment", 0f);
        Beam.FalloffHalflifeDist = Bal("g_balance_arc_beam_falloff_halflifedist", 0f);
        Beam.FalloffMaxDist = Bal("g_balance_arc_beam_falloff_maxdist", 0f);
        Beam.FalloffMinDist = Bal("g_balance_arc_beam_falloff_mindist", 0f);
        Beam.Force = Bal("g_balance_arc_beam_force", 600f);
        Beam.HealingAmax = Bal("g_balance_arc_beam_healing_amax", 0f);
        Beam.HealingAps = Bal("g_balance_arc_beam_healing_aps", 50f);
        Beam.HealingHmax = Bal("g_balance_arc_beam_healing_hmax", 150f);
        Beam.HealingHps = Bal("g_balance_arc_beam_healing_hps", 50f);
        Beam.Heat = Bal("g_balance_arc_beam_heat", 0f);
        Beam.MaxAngle = Bal("g_balance_arc_beam_maxangle", 10f);
        Beam.NonPlayerDamage = Bal("g_balance_arc_beam_nonplayerdamage", 80f);
        Beam.Range = Bal("g_balance_arc_beam_range", 1500f);
        Beam.Refire = Bal("g_balance_arc_beam_refire", 0.25f);
        Beam.ReturnSpeed = Bal("g_balance_arc_beam_returnspeed", 8f);
        Beam.Tightness = Bal("g_balance_arc_beam_tightness", 0.6f);

        Beam.BurstAmmo = Bal("g_balance_arc_burst_ammo", 15f);
        Beam.BurstDamage = Bal("g_balance_arc_burst_damage", 250f);
        Beam.BurstHealingAps = Bal("g_balance_arc_burst_healing_aps", 100f);
        Beam.BurstHealingHps = Bal("g_balance_arc_burst_healing_hps", 100f);
        Beam.BurstHeat = Bal("g_balance_arc_burst_heat", 5f);

        Beam.Cooldown = Bal("g_balance_arc_cooldown", 2.5f);
        Beam.CooldownRelease = Bal("g_balance_arc_cooldown_release", 0f);
        Beam.OverheatMax = Bal("g_balance_arc_overheat_max", 5f);
        Beam.OverheatMin = Bal("g_balance_arc_overheat_min", 3f);

        Bolt.BounceCount = BalInt("g_balance_arc_bolt_bounce_count", 0);
        Bolt.BounceExplode = Bal("g_balance_arc_bolt_bounce_explode", 0f);
        Bolt.BounceLifetime = Bal("g_balance_arc_bolt_bounce_lifetime", 0f);
        Bolt.Count = BalInt("g_balance_arc_bolt_count", 1);
        Bolt.DamageForceScale = Bal("g_balance_arc_bolt_damageforcescale", 0f);
        Bolt.Damage = Bal("g_balance_arc_bolt_damage", 25f);
        Bolt.EdgeDamage = Bal("g_balance_arc_bolt_edgedamage", 12.5f);
        Bolt.Force = Bal("g_balance_arc_bolt_force", 120f);
        Bolt.Health = Bal("g_balance_arc_bolt_health", 15f);
        Bolt.Lifetime = Bal("g_balance_arc_bolt_lifetime", 5f);
        Bolt.Radius = Bal("g_balance_arc_bolt_radius", 65f);
        Bolt.Refire = Bal("g_balance_arc_bolt_refire", 0.16667f);
        Bolt.Refire2 = Bal("g_balance_arc_bolt_refire2", 0.16667f);
        Bolt.Speed = Bal("g_balance_arc_bolt_speed", 2300f);
        Bolt.Spread = Bal("g_balance_arc_bolt_spread", 0f);
        Bolt.Ammo = Bal("g_balance_arc_bolt_ammo", 1f);

        BoltEnabled = BalBool("g_balance_arc_bolt", true);
    }

    // METHOD(Arc, wr_think) — common/weapons/weapon/arc.qc
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        var st = actor.WeaponState(slot);

        // The Arc beam is CONTINUOUS (fires/heats every tick while the button is held), so it does not use the
        // refire-gate (PrepareAttack); it reads the held button (st.ButtonAttack/2) directly, exactly as QC's
        // W_Arc_Beam keys off PHYS_INPUT_BUTTON_ATCK. The bolt secondary, by contrast, is a discrete shot and
        // IS refire-gated.
        //
        // The driver calls WrThink(Primary) every tick (for upkeep). We do the beam/cooldown decision in that
        // Primary call based on which fire button is down:
        if (fire == FireMode.Primary)
        {
            bool beamPrimary = st.ButtonAttack;                       // primary held -> normal beam
            bool beamBurst = st.ButtonAttack2 && !BoltEnabled;        // secondary held + no bolt -> burst beam
            if (beamPrimary || beamBurst)
            {
                BeamTick(actor, slot, st, burst: beamBurst && !beamPrimary);
            }
            else
            {
                // Neither beam button held: cool the barrel down (cooldown/sec toward 0) and end the beam. On the
                // firing→release EDGE (the beam was live last tick, st.ArcBeam set) stop its loop and play the
                // release cue — QC arc.qc:632 sound(actor, CH_WEAPON_A, SND_ARC_STOP). Gated on the edge so idle
                // ticks stay silent (and don't re-stop a loop that's already gone).
                if (st.ArcBeam is not null)
                {
                    Api.Sound.Stop(actor, SoundChannel.Weapon);
                    Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/arc_stop.wav");
                }
                BeamCooldown(st, burst: false);
                st.ArcBeam = null;
            }
        }
        else if (fire == FireMode.Secondary && BoltEnabled)
        {
            // Discrete bolt burst, refire-gated (QC weapon_prepareattack with bolt_refire).
            if (PrepareAttack(actor, slot, fire))
                AttackBoltBurst(actor, slot, st);
        }
    }

    // Cool the barrel when the beam isn't active (W_Arc_Beam_Think's release branch, arc.qc:223-258). QC picks a
    // per-state cooldown_speed: full `cooldown` while still hot (heat > overheat_min), a heat-proportional
    // `heat/beam_refire` once below overheat_min (so the last sliver bleeds off in ~beam_refire), or 0 while the
    // burst beam is held; that speed is then drained from the heat each frame.
    private void BeamCooldown(WeaponSlotState st, bool burst)
    {
        if (Beam.Cooldown <= 0f)
        {
            st.BeamInitialized = false;
            return;
        }
        float cooldownSpeed = CooldownSpeed(st, burst);
        if (cooldownSpeed > 0f && st.BeamHeat > 0f)
            st.BeamHeat = MathF.Max(0f, st.BeamHeat - cooldownSpeed * Api.Clock.FrameTime);
        st.BeamInitialized = false;
    }

    // QC arc.qc:225-231 cooldown_speed selection.
    private float CooldownSpeed(WeaponSlotState st, bool burst)
    {
        if (st.BeamHeat > Beam.OverheatMin && Beam.Cooldown > 0f)
            return Beam.Cooldown;
        if (!burst)
            return Beam.Refire > 0f ? st.BeamHeat / Beam.Refire : 0f;
        return 0f;
    }

    // W_Arc_Beam_Think (full DPS core) — one frame of the beam: accumulate heat (jamming on overheat), curve
    // the beam direction toward the aim (limited by beam_maxangle/returnspeed), trace, and apply
    // distance-falloff damage to enemies / healing to teammates. arc.qc
    private void BeamTick(Entity actor, WeaponSlot slot, WeaponSlotState st, bool burst)
    {
        // Overheat jam: while overheated (heat at max), the beam can't fire and just cools down.
        if (Beam.OverheatMax > 0f && st.BeamHeat >= Beam.OverheatMax)
        {
            if (st.ArcOverheat == 0f)
            {
                // QC arc.qc:240-244: while overheated, cooldown_speed == `cooldown` (heat is at max, above
                // overheat_min) and the jam lasts `time + heat / cooldown_speed` — i.e. as long as it takes to bleed
                // the accumulated heat back off at the cooldown rate (~overheat_max/cooldown), NOT a fixed
                // overheat_max seconds. Falls back to overheat_max only if cooldown is disabled (no bleed rate).
                float cooldownSpeed = CooldownSpeed(st, burst);
                st.ArcOverheat = Api.Clock.Time + (cooldownSpeed > 0f ? st.BeamHeat / cooldownSpeed : Beam.OverheatMax);
                Api.Sound.Stop(actor, SoundChannel.Weapon);          // end the beam loop before the stop cue
                Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/arc_stop.wav"); // QC arc.qc:237 SND_ARC_STOP
            }
            BeamCooldown(st, burst);
            st.ArcBeam = null;
            return;
        }
        st.ArcOverheat = 0f;

        // coefficient = frametime, clamped by remaining ammo (rootammo per second).
        float coefficient = Api.Clock.FrameTime;
        float rootAmmo = burst ? Beam.BurstAmmo : Beam.Ammo;
        if (rootAmmo > 0f)
        {
            float cur = actor.GetResource(AmmoType);
            coefficient = MathF.Min(coefficient, cur / rootAmmo);
            actor.SetResource(AmmoType, MathF.Max(0f, cur - rootAmmo * Api.Clock.FrameTime));
        }

        // Heat builds while firing.
        float heatSpeed = burst ? Beam.BurstHeat : Beam.Heat;
        st.BeamHeat = MathF.Min(Beam.OverheatMax > 0f ? Beam.OverheatMax : float.MaxValue,
            st.BeamHeat + heatSpeed * Api.Clock.FrameTime);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, Beam.Range);
        Vector3 wantDir = shot.Dir;

        // Curving beam: beam_dir lags wantDir, blending toward it but turning no faster than beam_maxangle
        // per the returnspeed rate (so whipping the aim drags the beam around smoothly).
        if (!st.BeamInitialized)
        {
            st.BeamDir = wantDir;
            st.BeamInitialized = true;
        }
        else if (st.BeamDir != wantDir)
        {
            float angle = (wantDir - st.BeamDir).Length() * QMath.Rad2Deg;
            if (angle < 0.01f)
            {
                st.BeamDir = wantDir;
            }
            else
            {
                float maxBlend = 1f;
                if (angle > Beam.MaxAngle) maxBlend = Beam.MaxAngle / angle;
                float blend = QMath.Clamp(1f - Beam.ReturnSpeed * Api.Clock.FrameTime, 0f, maxBlend);
                st.BeamDir = QMath.Normalize(wantDir * (1f - blend) + st.BeamDir * blend);
            }
        }

        // Single straight segment along the (curved) beam_dir — the QC bezier segmentation is a render-side
        // smoothing of this same trace.
        Vector3 end = shot.Origin + st.BeamDir * Beam.Range;
        // QC arc.qc:375 traces through WarpZone_traceline_antilag at ANTILAG_LATENCY: rewind other players to the
        // shooter's view-time so a high-ping player's beam connects on a strafing target. Bracket the damage trace
        // (no-op on a client / bot-only server / test where no lag-comp provider is installed).
        LagComp.Begin(actor);
        TraceResult tr;
        try
        {
            tr = Api.Trace.Trace(shot.Origin, Vector3.Zero, Vector3.Zero, end, MoveFilter.Normal, actor);
        }
        finally { LagComp.End(); }
        EffectEmitter.Emit("ARC_BEAM", shot.Origin, tr.EndPos, 0);

        Entity? hit = tr.Ent;
        if (hit is not null && hit.TakeDamage != DamageMode.No)
        {
            bool isPlayer = (hit.Flags & EntFlags.Client) != 0
                || hit.ClassName == "body" || (hit.Flags & EntFlags.Monster) != 0;

            // Teammates get healed (health + armor) instead of damaged.
            if (!ReferenceEquals(hit, actor) && actor.Team != 0f && hit.Team == actor.Team)
            {
                float hps = burst ? Beam.BurstHealingHps : Beam.HealingHps;
                float aps = burst ? Beam.BurstHealingAps : Beam.HealingAps;
                if (hps > 0f)
                {
                    // QC Heal(trace_ent, own, hps*coef, hplimit) — GiveResourceWithLimit(Health) which CLAMPS the
                    // post-give total to the limit (players: healing_hmax; non-players: RES_LIMIT_NONE = uncapped).
                    // Previously this DROPPED the heal entirely once it would push past hmax instead of topping up
                    // to it — a real divergence right at the cap.
                    float hpLimit = isPlayer ? Beam.HealingHmax : Resources.LimitNone;
                    hit.GiveResourceWithLimit(ResourceType.Health, hps * coefficient, hpLimit);
                }
                if (isPlayer && aps > 0f && hit.GetResource(ResourceType.Armor) <= Beam.HealingAmax)
                {
                    hit.GiveResourceWithLimit(ResourceType.Armor, aps * coefficient, Beam.HealingAmax);
                    // QC arc.qc:417 — refresh the armor-rot pause so the just-given armor doesn't immediately
                    // start rotting back off.
                    hit.PauseRotArmorFinished = MathF.Max(hit.PauseRotArmorFinished,
                        Api.Clock.Time + Bal("g_balance_pause_armor_rot", 1f));
                }
            }
            else
            {
                float perSec = isPlayer ? (burst ? Beam.BurstDamage : Beam.Damage) : Beam.NonPlayerDamage;
                // Exponential distance falloff (beam_falloff_*).
                float falloff = (Beam.FalloffHalflifeDist > 0f)
                    ? WeaponFiring.ExponentialFalloff(Beam.FalloffMinDist, Beam.FalloffMaxDist,
                        Beam.FalloffHalflifeDist, (tr.EndPos - shot.Origin).Length())
                    : 1f;
                float damage = perSec * coefficient * falloff;
                Vector3 dir = QMath.Normalize(end - shot.Origin);
                Vector3 force = dir * (Beam.Force * coefficient * falloff);
                WeaponFiring.ApplyDamage(hit, actor, damage, RegistryId, force: force, hitLoc: tr.EndPos);
            }
        }

        st.ArcBeam = actor; // mark the beam as live this frame
        // Looping beam sound on (actor, CH_WEAPON) — DP loopsound(beam, CH_SHOTS_SINGLE, SND_ARC_LOOP). Emitted
        // every think while firing, but loop:true makes the client KEEP the one existing loop (no stacking) and
        // follow the player; Stop() on release/overheat ends it (below / in WrThink).
        Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/arc_loop.wav", loop: true);
    }

    // W_Arc_Attack_Bolt — fire bouncing energy bolt(s) that explode with radius damage. The refire2 cadence
    // (every bolt_count-th bolt enforces the longer refire2) is tracked via misc_bulletcounter. arc.qc
    private void AttackBoltBurst(Entity actor, WeaponSlot slot, WeaponSlotState st)
    {
        actor.TakeResource(AmmoType, Bolt.Ammo);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, recoil: 2f); // QC arc.qc bolt recoil 2

        int count = Bolt.Count;
        if (count < 1) count = 1;
        for (int i = 0; i < count; ++i)
        {
            Entity missile = Api.Entities.Spawn();
            missile.ClassName = "missile";
            missile.Owner = actor;
            missile.NetName = NetName;
            missile.MoveType = MoveType.BounceMissile;
            Projectiles.MakeTrigger(missile); // QC PROJECTILE_MAKETRIGGER (SOLID_CORPSE): transparent to the firer's movement
            missile.Flags = EntFlags.Item; // QC FL_PROJECTILE
            missile.DamageForceScale = Bolt.DamageForceScale;
            Api.Entities.SetSize(missile, Vector3.Zero, Vector3.Zero);
            Api.Entities.SetOrigin(missile, shot.Origin);

            missile.TakeDamage = DamageMode.Yes; // shootable
            missile.Health = Bolt.Health;

            // W_SetupProjVelocity_PRE(bolt_): velocity = w_shotdir * speed (with bolt_spread, normally 0).
            missile.Velocity = WeaponFiring.ProjectileVelocity(shot.Dir, Vector3.UnitZ, Bolt.Speed, 0f, 0f, Bolt.Spread);
            missile.Angles = QMath.VecToAngles(missile.Velocity);

            missile.Count = 0; // QC .cnt = bounce counter
            missile.Touch = (self, other) => BoltTouch(self, other);
            missile.Think = self => ExplodeBolt(self); // adaptor_think2use_hittype_splash at lifetime
            missile.ProjectileDamage = (self, _) => ExplodeBolt(self); // W_Arc_Bolt_Damage -> W_PrepareExplosionByDamage
            missile.NextThink = Api.Clock.Time + Bolt.Lifetime;

            // MUTATOR_CALLHOOK(EditProjectile, actor, missile) — fired per bolt (arc.qc W_Arc_Attack_Bolt).
            var ep = new MutatorHooks.EditProjectileArgs(actor, missile);
            MutatorHooks.EditProjectile.Call(ref ep);

            // [W1-projectile-net] Route incoming damage through the shoot-down shim (QC W_Arc_Bolt_Damage):
            // it runs the g_projectiles_damage gate, subtracts the damage from the bolt's RES_HEALTH, and only
            // fires ProjectileDamage (ExplodeBolt) once HP <= 0 — so a partial-damage graze leaves the bolt
            // alive instead of detonating it on any hit. The Arc bolt is not combo-able (exception -1, default).
            Projectiles.MakeShootable(missile);
        }

        ++st.MiscBulletCounter; // refire2 cadence (every bolt_count-th shot waits refire2 in the weapon loop)
        Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/electro_fire2.wav");
    }

    // W_Arc_Bolt_Touch — explode on a damageable target or when bounces run out; otherwise bounce. arc.qc
    private void BoltTouch(Entity self, Entity other)
    {
        // QC arc.qc:116 keys solely on toucher.takedamage == DAMAGE_AIM (anything that aim-takes damage —
        // players, bodies, monsters, shootable projectiles), not on the Client flag specifically.
        bool hitAimTarget = other.TakeDamage == DamageMode.Aim;
        if (self.Count >= Bolt.BounceCount || Bolt.BounceCount == 0 || hitAimTarget)
        {
            ExplodeBolt(self);
            return;
        }
        // Survived a bounce (engine MOVETYPE_BOUNCEMISSILE reflects velocity): count it and re-aim.
        ++self.Count;
        self.Angles = QMath.VecToAngles(self.Velocity);
        if (Bolt.BounceExplode != 0f)
        {
            WeaponSplash.RadiusDamage(self, self.Origin, Bolt.Damage, Bolt.EdgeDamage, Bolt.Radius,
                self.Owner, RegistryId, Bolt.Force);
        }
        if (self.Count == 1 && Bolt.BounceLifetime != 0f)
            self.NextThink = Api.Clock.Time + Bolt.BounceLifetime;
    }

    // W_Arc_Bolt_Explode — radius damage + knockback, then remove. arc.qc
    private void ExplodeBolt(Entity self)
    {
        self.Touch = null;
        self.Think = null;
        self.TakeDamage = DamageMode.No;
        WeaponSplash.RadiusDamage(self, self.Origin, Bolt.Damage, Bolt.EdgeDamage, Bolt.Radius,
            self.Owner, RegistryId, Bolt.Force);
        WeaponSplash.ImpactSound(self, "weapons/electro_impact.wav"); // QC SND_ARC_BOLT_IMPACT (wr_impacteffect)
        // arc.qc: pointparticles(EFFECT_ELECTRO_IMPACT, org2, w_backoff * 1000, 1) — spray back along the impact
        // normal; the reversed bolt flight direction is DP's w_backoff fallback (-force_dir).
        Vector3 backoff = self.Velocity.LengthSquared() > 1e-6f ? -QMath.Normalize(self.Velocity) : Vector3.Zero;
        EffectEmitter.Emit("ELECTRO_IMPACT", self.Origin, backoff * 1000f);
        Api.Entities.Remove(self);
    }

    // QC the Arc has no _primary_/_secondary_ refire cvars: the primary is the continuous beam (beam_refire,
    // only used if the beam ever went through the gate — it does not), the secondary bolt uses bolt_refire.
    // No separate bolt animtime cvar, so the refire doubles as the fire-anim length.
    public override float RefireFor(FireMode fire)
        => fire == FireMode.Secondary ? Bolt.Refire : Beam.Refire;
    public override float AnimtimeFor(FireMode fire)
        => fire == FireMode.Secondary ? Bolt.Refire : Beam.Animtime;

    // METHOD(Arc, wr_checkammo1) — arc.qc:664-667. The continuous beam only needs ANY cells to start
    // (`!beam_ammo || cells > 0`), NOT a full per-second `beam_ammo` worth — the per-tick drain in BeamTick
    // already scales by whatever fraction of a tick's ammo remains. Gating on `cells >= beam_ammo (6)` was a
    // real bug: a player with 1-5 cells should fire the beam (draining what's left) but was auto-switched away.
    public bool CheckAmmoPrimary(Entity actor)
        => Beam.Ammo == 0f || actor.GetResource(AmmoType) > 0f;

    // METHOD(Arc, wr_checkammo2) — arc.qc:668-679. Bolt secondary needs `cells >= bolt_ammo (1)`; the burst-beam
    // secondary (bolt disabled) needs only `overheat_max > 0 && (!burst_ammo || cells > 0)` — same "any cells"
    // rule as the primary beam, not a full `burst_ammo (15)` worth.
    public bool CheckAmmoSecondary(Entity actor)
    {
        float cells = actor.GetResource(AmmoType);
        if (BoltEnabled)
            return cells >= Bolt.Ammo;
        return Beam.OverheatMax > 0f && (Beam.BurstAmmo == 0f || cells > 0f);
    }
}
