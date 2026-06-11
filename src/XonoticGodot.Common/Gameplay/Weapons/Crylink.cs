using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Crylink — port of common/weapons/weapon/crylink.{qh,qc}. A splash weapon firing a burst of
/// fast energy spikes that spread out and deflect off walls. Each spike does radius damage on contact
/// (and a reduced amount on each bounce), can bounce a limited number of times, and fades after its
/// lifetime. Primary spreads wide (6 shots); secondary fires a tighter group (5 shots) with negative
/// force (pulls victims toward impact).
///
/// Identity/attributes from crylink.qh; balance from bal-wep-xonotic.cfg (g_balance_crylink_*).
/// This port covers the multi-spike fan-out (circular pattern + secondary linear fan), per-spike
/// bounce/lifetime, the over-lifetime fade-damage scaling, the link-join "converge on release" mechanic,
/// and the link-explode chain detonation. Only client projectile networking/effects are left out.
/// </summary>
[Weapon]
public sealed class Crylink : Weapon
{
    /// <summary>Per-fire-mode balance block — QC WEP_CVAR_PRI/SEC(WEP_CRYLINK, *).</summary>
    public struct ModeBalance
    {
        public float Ammo;                // *_ammo (cells per shot)
        public float Animtime;            // *_animtime
        public float BounceDamageFactor;  // *_bouncedamagefactor
        public float Bounces;             // *_bounces (max bounce count)
        public float Damage;              // *_damage (per spike)
        public float EdgeDamage;          // *_edgedamage
        public float Force;               // *_force (negative = pull)
        public float MiddleLifetime;      // *_middle_lifetime
        public float OtherLifetime;       // *_other_lifetime
        public float Radius;              // *_radius
        public float Refire;              // *_refire
        public int   Shots;               // *_shots (spike count)
        public float Speed;               // *_speed
        public float Spread;              // *_spread
    }

    public ModeBalance Primary;
    public ModeBalance Secondary;

    /// <summary>g_balance_crylink_secondary — whether secondary fire is enabled.</summary>
    public bool SecondaryEnabled = true;


    public Crylink()
    {
        NetName = "crylink";
        AmmoType = ResourceType.Cells;   // QC ammo_type
        DisplayName = "Crylink";
        Impulse = 6;
        // WEP_FLAG_NORMAL | WEP_FLAG_RELOADABLE | WEP_TYPE_SPLASH | WEP_FLAG_CANCLIMB
        SpawnFlags = WeaponFlags.Normal | WeaponFlags.Reloadable | WeaponFlags.TypeSplash | WeaponFlags.CanClimb;
        Color = new Vector3(0.918f, 0.435f, 0.976f);
        ViewModel = "h_crylink.iqm";  // MDL_CRYLINK_VIEW
        WorldModel = "v_crylink.md3"; // MDL_CRYLINK_WORLD
        ItemModel = "g_crylink.md3";  // MDL_CRYLINK_ITEM
    }

    public override void Configure()
    {
        Primary.Ammo = Bal("g_balance_crylink_primary_ammo", 3f);
        Primary.Animtime = Bal("g_balance_crylink_primary_animtime", 0.3f);
        Primary.BounceDamageFactor = Bal("g_balance_crylink_primary_bouncedamagefactor", 1f);
        Primary.Bounces = Bal("g_balance_crylink_primary_bounces", 1f);
        Primary.Damage = Bal("g_balance_crylink_primary_damage", 10f);
        Primary.EdgeDamage = Bal("g_balance_crylink_primary_edgedamage", 5f);
        Primary.Force = Bal("g_balance_crylink_primary_force", -50f);
        Primary.MiddleLifetime = Bal("g_balance_crylink_primary_middle_lifetime", 5f);
        Primary.OtherLifetime = Bal("g_balance_crylink_primary_other_lifetime", 5f);
        Primary.Radius = Bal("g_balance_crylink_primary_radius", 80f);
        Primary.Refire = Bal("g_balance_crylink_primary_refire", 0.7f);
        Primary.Shots = BalInt("g_balance_crylink_primary_shots", 6);
        Primary.Speed = Bal("g_balance_crylink_primary_speed", 2000f);
        Primary.Spread = Bal("g_balance_crylink_primary_spread", 0.08f);

        Secondary.Ammo = Bal("g_balance_crylink_secondary_ammo", 3f);
        Secondary.Animtime = Bal("g_balance_crylink_secondary_animtime", 0.2f);
        Secondary.BounceDamageFactor = Bal("g_balance_crylink_secondary_bouncedamagefactor", 0.5f);
        Secondary.Bounces = Bal("g_balance_crylink_secondary_bounces", 0f);
        Secondary.Damage = Bal("g_balance_crylink_secondary_damage", 8f);
        Secondary.EdgeDamage = Bal("g_balance_crylink_secondary_edgedamage", 4f);
        Secondary.Force = Bal("g_balance_crylink_secondary_force", -200f);
        Secondary.MiddleLifetime = Bal("g_balance_crylink_secondary_middle_lifetime", 5f);
        Secondary.OtherLifetime = Bal("g_balance_crylink_secondary_other_lifetime", 5f);
        Secondary.Radius = Bal("g_balance_crylink_secondary_radius", 100f);
        Secondary.Refire = Bal("g_balance_crylink_secondary_refire", 0.7f);
        Secondary.Shots = BalInt("g_balance_crylink_secondary_shots", 5);
        Secondary.Speed = Bal("g_balance_crylink_secondary_speed", 3000f);
        Secondary.Spread = Bal("g_balance_crylink_secondary_spread", 0.01f);

        SecondaryEnabled = BalBool("g_balance_crylink_secondary", true);

        // join/link/fade balance (bal-wep-xonotic.cfg g_balance_crylink_*).
        PrimaryJoinSpread = Bal("g_balance_crylink_primary_joinspread", 0.2f);
        PrimaryJoinDelay = Bal("g_balance_crylink_primary_joindelay", 0.1f);
        PrimaryLinkExplode = BalInt("g_balance_crylink_primary_linkexplode", 0);
        PrimaryFadeTime = 5f;
        SecondaryJoinSpread = Bal("g_balance_crylink_secondary_joinspread", 0f);
        SecondaryJoinDelay = Bal("g_balance_crylink_secondary_joindelay", 0f);
        SecondaryLinkExplode = BalInt("g_balance_crylink_secondary_linkexplode", 1);
        SecondaryFadeTime = 5f;
        SecondarySpreadType = BalInt("g_balance_crylink_secondary_spreadtype", 1);
    }

    /// <summary>g_balance_crylink_*_joinspread — converge speed factor on release (0 = parallel, no convergence).</summary>
    public float PrimaryJoinSpread = 0.5f;
    public float SecondaryJoinSpread = 0f;
    /// <summary>g_balance_crylink_*_linkexplode — 1: chain-detonate the group on a friendly-safe hit; 2: always.</summary>
    public int PrimaryLinkExplode = 0;
    public int SecondaryLinkExplode = 0;
    /// <summary>g_balance_crylink_secondary_spreadtype — 1: circular pattern; 0: linear horizontal fan.</summary>
    public int SecondarySpreadType = 1;
    /// <summary>g_balance_crylink_*_joindelay — min time the group must persist before it can link-join.</summary>
    public float PrimaryJoinDelay = 0.1f;
    public float SecondaryJoinDelay = 0.1f;
    /// <summary>g_balance_crylink_*_fadetime — fade duration over which a spike's damage decays to 0.</summary>
    public float PrimaryFadeTime = 0.1f;
    public float SecondaryFadeTime = 0.1f;

    // METHOD(Crylink, wr_think) — common/weapons/weapon/crylink.qc
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        var st = actor.WeaponState(slot);

        // crylink_waitrelease: after firing a join-enabled group, the spikes stay spread while fire is HELD;
        // they converge (W_Crylink_LinkJoin) only once the player RELEASES — the weapon's defining "time the
        // convergence onto a target" mechanic (crylink.qc describe()). QC gates the join on BOTH the
        // button-RELEASE level check (!(fire & 1) / !(fire & 2)) AND a joindelay floor
        // (time > teleport_time == firetime + joindelay) — the joindelay is only a minimum floor, the release
        // is the trigger. The live held-button state is supplied each tick by WeaponFireDriver (st.ButtonAttack
        // /2); for the secondary group too, since this block runs in the every-tick WrThink(Primary) call where
        // SetButtons has already recorded this tick's ATK2 (even on the ticks WrThink(Secondary) isn't called).
        if (st.CrylinkWaitRelease != 0)
        {
            bool isPrimary = st.CrylinkWaitRelease == 1;
            bool released = isPrimary ? !st.ButtonAttack : !st.ButtonAttack2; // QC !(fire & 1) / !(fire & 2)
            if (released)
            {
                Entity? group = st.CrylinkLastGroup;
                bool groupAlive = group is not null && !group.IsFreed;
                float joinDelay = isPrimary ? PrimaryJoinDelay : SecondaryJoinDelay;
                // QC: !crylink_lastgroup || time > teleport_time. A live group converges once the joindelay
                // floor passes; if its head spike already died (this port's CrylinkLastGroup can dangle where
                // QC re-heads the queue to a live spike) there's nothing to converge, but we still drop out of
                // wait-release so a dead group can't strand the player unable to fire (QC clears it regardless).
                if (!groupAlive || Api.Clock.Time > group!.LTime + joinDelay)
                {
                    if (groupAlive)
                        LinkJoin(group!, (isPrimary ? PrimaryJoinSpread : SecondaryJoinSpread)
                            * (isPrimary ? Primary.Speed : Secondary.Speed));
                    st.CrylinkLastGroup = null;
                    st.CrylinkWaitRelease = 0;
                }
            }
        }

        // Each spike volley is refire-gated (QC weapon_prepareattack with the primary/secondary refire). The
        // CrylinkWaitRelease guard additionally blocks a new volley while the previous group waits to join.
        if (fire == FireMode.Primary && st.CrylinkWaitRelease != 1)
        {
            if (PrepareAttack(actor, slot, fire))
                Attack(actor, slot, st, Primary, secondary: false);
        }
        else if (fire == FireMode.Secondary && SecondaryEnabled && st.CrylinkWaitRelease != 2)
        {
            if (PrepareAttack(actor, slot, fire))
                Attack(actor, slot, st, Secondary, secondary: true);
        }
    }

    // Refire/animtime from the (cvar-seeded) per-mode balance blocks.
    public override float RefireFor(FireMode fire) => (fire == FireMode.Secondary ? Secondary : Primary).Refire;
    public override float AnimtimeFor(FireMode fire) => (fire == FireMode.Secondary ? Secondary : Primary).Animtime;

    // W_Crylink_Attack / W_Crylink_Attack2 — spray `shots` spikes in a spread, linked into a converge group.
    private void Attack(Entity actor, WeaponSlot slot, WeaponSlotState st, ModeBalance bal, bool secondary)
    {
        actor.TakeResource(AmmoType, bal.Ammo);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out Vector3 right, out Vector3 up);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, recoil: 2f);

        int shots = bal.Shots;
        float damage = bal.Damage, edge = bal.EdgeDamage, radius = bal.Radius, force = bal.Force;
        float bounceFactor = bal.BounceDamageFactor;
        int maxBounces = (int)bal.Bounces;
        float fadeTime = secondary ? SecondaryFadeTime : PrimaryFadeTime;
        int linkExplode = secondary ? SecondaryLinkExplode : PrimaryLinkExplode;
        int deathType = RegistryId;

        var group = new List<Entity>(shots);
        for (int i = 0; i < shots; ++i)
        {
            Entity proj = Api.Entities.Spawn();
            proj.ClassName = "spike";
            proj.Owner = actor;
            proj.NetName = NetName;
            proj.MoveType = MoveType.BounceMissile;
            Projectiles.MakeTrigger(proj); // QC PROJECTILE_MAKETRIGGER (SOLID_CORPSE): transparent to the firer's movement
            proj.Flags = EntFlags.Item; // QC FL_PROJECTILE
            Api.Entities.SetSize(proj, Vector3.Zero, Vector3.Zero);
            Api.Entities.SetOrigin(proj, shot.Origin);

            // Spread: primary + secondary spreadtype 1 use the circular pattern; secondary spreadtype 0 is a
            // linear horizontal fan (s = w_shotdir + ((i+0.5)/shots*2 - 1) * right * spread).
            Vector3 dir;
            if (secondary && SecondarySpreadType == 0)
            {
                float lat = ((i + 0.5f) / shots) * 2f - 1f;
                dir = QMath.Normalize(shot.Dir + lat * right * (bal.Spread * WeaponFiring.WeaponSpreadFactor));
            }
            else
            {
                Vector3 s = WeaponFiring.CalculateSpreadPattern(i, shots) * (bal.Spread * WeaponFiring.WeaponSpreadFactor);
                dir = QMath.Normalize(shot.Dir + right * s.Y + up * s.Z);
            }
            proj.Velocity = WeaponFiring.ProjectileVelocity(dir, up, bal.Speed);
            proj.Angles = QMath.VecToAngles(proj.Velocity);

            // First spike uses middle_lifetime, the rest other_lifetime. fade_time/fade_rate scale the
            // spike's damage down to 0 over fadetime once its lifetime starts running out.
            float lifetime = (i == 0) ? bal.MiddleLifetime : bal.OtherLifetime;
            proj.MaxHealth = Api.Clock.Time + lifetime;         // QC .fade_time
            proj.Health = (fadeTime > 0f) ? 1f / fadeTime : 0f; // QC .fade_rate (reused .health field)
            proj.Count = maxBounces;                            // QC .cnt = remaining bounces
            proj.LTime = Api.Clock.Time;                        // group spawn time (for joindelay)

            proj.Think = self => Api.Entities.Remove(self);     // W_Crylink_Fadethink
            proj.NextThink = proj.MaxHealth + fadeTime;
            proj.Touch = (self, other) => OnTouch(self, other, damage, edge, radius, force, bounceFactor,
                linkExplode, deathType, group, secondary);

            // MUTATOR_CALLHOOK(EditProjectile, actor, proj) — fired per spike (crylink.qc).
            var ep = new MutatorHooks.EditProjectileArgs(actor, proj);
            MutatorHooks.EditProjectile.Call(ref ep);

            group.Add(proj);
        }

        Api.Sound.Play(actor, SoundChannel.Weapon, secondary ? "weapons/crylink_fire2.wav" : "weapons/crylink_fire.wav");
        EffectEmitter.Emit("CRYLINK_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);

        // Register the group for the converge-on-release link-join (only when joinspread is enabled).
        float joinSpread = secondary ? SecondaryJoinSpread : PrimaryJoinSpread;
        if (joinSpread != 0f && shots > 1)
        {
            st.CrylinkLastGroup = group[0];
            st.CrylinkWaitRelease = secondary ? 2 : 1;
            _groups[group[0]] = group;
        }
    }

    // Active link groups keyed by their head spike (the C# successor to the QC queuenext/queueprev ring).
    private readonly Dictionary<Entity, List<Entity>> _groups = new();

    /// <summary>
    /// Port of W_Crylink_LinkJoin (crylink.qc): retarget every live spike in the group so they converge on
    /// the group's average position/velocity, producing the signature "snap together then spread" pattern.
    /// jspeed = joinspread * initial speed controls how fast they converge.
    /// </summary>
    private void LinkJoin(Entity head, float jspeed)
    {
        if (!_groups.TryGetValue(head, out var group)) return;
        _groups.Remove(head);
        group.RemoveAll(e => e.IsFreed);
        if (group.Count < 2) return;

        Vector3 avgOrg = Vector3.Zero, avgVel = Vector3.Zero;
        foreach (var p in group) { avgOrg += p.Origin; avgVel += p.Velocity; }
        avgOrg /= group.Count;
        avgVel /= group.Count;

        if (jspeed == 0f)
        {
            foreach (var p in group) p.Velocity = avgVel;
            return;
        }

        // avg distance from center -> time to meet; aim each spike at the meeting point.
        float avgDist = 0f;
        foreach (var p in group) avgDist += (p.Origin - avgOrg).LengthSquared();
        avgDist = MathF.Sqrt(avgDist / group.Count);
        if (avgDist == 0f) return;

        float meetTime = avgDist / jspeed;
        Vector3 targ = avgOrg + meetTime * avgVel;
        foreach (var p in group)
        {
            p.Velocity = (targ - p.Origin) * (1f / meetTime);
            p.Angles = QMath.VecToAngles(p.Velocity);
        }
    }

    // W_Crylink_Touch — radius damage on contact (faded over lifetime); reduced damage on a bounce, until
    // bounces run out; chain-detonate the whole group when linkexplode says so. crylink.qc
    private void OnTouch(Entity self, Entity other, float damage, float edge, float radius, float force,
        float bounceFactor, int linkExplode, int deathType, List<Entity> group, bool secondary)
    {
        // QC wr_impacteffect keys the impact sprite off HITTYPE_SECONDARY: secondary spikes use the smaller
        // CRYLINK_IMPACT2, primary the bigger CRYLINK_IMPACT (both with '0 0 0' velocity).
        string impact = secondary ? "CRYLINK_IMPACT2" : "CRYLINK_IMPACT";
        // a = fade scalar in [0,1]: 1 - (time - fade_time) * fade_rate.
        float a = QMath.Clamp(1f - (Api.Clock.Time - self.MaxHealth) * self.Health, 0f, 1f);
        bool finalHit = self.Count <= 0 || other.TakeDamage != DamageMode.No;
        float f = (finalHit ? 1f : bounceFactor) * a;

        WeaponSplash.RadiusDamage(self, self.Origin, f * damage, f * edge, radius, self.Owner, deathType,
            f * force, directHit: other);

        if (finalHit)
        {
            // Chain-detonate the rest of the linked group (W_Crylink_LinkExplode) if enabled.
            if (linkExplode != 0)
                LinkExplode(self, group, damage, edge, radius, force, deathType, secondary);
            WeaponSplash.ImpactSound(self, "weapons/crylink_impact2.wav"); // QC SND_CRYLINK_IMPACT2 (wr_impacteffect)
            EffectEmitter.Emit(impact, self.Origin);
            RemoveFromGroup(self, group);
            Api.Entities.Remove(self);
            return;
        }

        // Survived a bounce: spend one bounce. MOVETYPE_BOUNCEMISSILE reflects the velocity in the engine.
        --self.Count;
        self.Angles = QMath.VecToAngles(self.Velocity);
    }

    // W_Crylink_LinkExplode — detonate every other spike in the group at its current position.
    private void LinkExplode(Entity except, List<Entity> group, float damage, float edge, float radius,
        float force, int deathType, bool secondary)
    {
        string impact = secondary ? "CRYLINK_IMPACT2" : "CRYLINK_IMPACT";
        Vector3 avgOrg = Vector3.Zero;
        int linkCount = 0;
        foreach (var e in group.ToArray())
        {
            if (ReferenceEquals(e, except) || e.IsFreed) continue;
            float a = QMath.Clamp(1f - (Api.Clock.Time - e.MaxHealth) * e.Health, 0f, 1f);
            WeaponSplash.RadiusDamage(e, e.Origin, a * damage, a * edge, radius, e.Owner, deathType, a * force);
            WeaponSplash.ImpactSound(e, "weapons/crylink_impact2.wav"); // QC SND_CRYLINK_IMPACT2 (wr_impacteffect)
            EffectEmitter.Emit(impact, e.Origin);
            avgOrg += e.Origin; ++linkCount;
            e.Touch = null; e.Think = null;
            Api.Entities.Remove(e);
        }
        if (linkCount > 0) EffectEmitter.Emit("CRYLINK_JOINEXPLODE", avgOrg / linkCount);
        group.Clear();
    }

    private void RemoveFromGroup(Entity e, List<Entity> group)
    {
        group.Remove(e);
        // If this was a registered group head, drop the registration.
        if (_groups.ContainsKey(e)) _groups.Remove(e);
    }

    // METHOD(Crylink, wr_checkammo1) — crylink.qc
    public bool CheckAmmoPrimary(Entity actor) => actor.GetResource(AmmoType) >= Primary.Ammo;

    // METHOD(Crylink, wr_checkammo2) — crylink.qc
    public bool CheckAmmoSecondary(Entity actor) => actor.GetResource(AmmoType) >= Secondary.Ammo;
}
