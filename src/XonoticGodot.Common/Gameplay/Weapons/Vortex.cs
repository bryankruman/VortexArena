using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Vortex (Nexuiz "Nex") — port of common/weapons/weapon/vortex.{qh,qc}. A hitscan rail weapon:
/// primary fire is an instant beam dealing a large fixed chunk of damage, consuming cells. The full
/// charge mechanic (hold-zoom to overcharge for more damage) is modeled in the balance block and the
/// charge math; per-actor charge *state* tracking is deferred to the weapon-entity component phase.
///
/// Identity/attributes from vortex.qh; balance from bal-wep-xonotic.cfg (g_balance_vortex_*).
/// </summary>
[Weapon]
public sealed class Vortex : Weapon
{
    /// <summary>Balance block — QC WEP_CVAR(WEP_VORTEX, *) (primary + charge cvars).</summary>
    public struct Balance
    {
        public float Damage;             // g_balance_vortex_primary_damage
        public float Force;              // g_balance_vortex_primary_force
        public float Refire;             // g_balance_vortex_primary_refire
        public float Animtime;           // g_balance_vortex_primary_animtime
        public float Ammo;               // g_balance_vortex_primary_ammo (cells per shot)

        public bool  Charge;             // g_balance_vortex_charge
        public float ChargeStart;        // g_balance_vortex_charge_start
        public float ChargeMinDmg;       // g_balance_vortex_charge_mindmg
        public float ChargeLimit;        // g_balance_vortex_charge_limit
        public float ChargeRate;         // g_balance_vortex_charge_rate
        public float ChargeAnimLimit;    // g_balance_vortex_charge_animlimit
        public float ChargeShotMul;      // g_balance_vortex_charge_shot_multiplier
        public bool  Secondary;          // g_balance_vortex_secondary (0 = zoom, not a fire mode)
    }

    public Balance Cvars;


    public Vortex()
    {
        NetName = "vortex";
        AmmoType = ResourceType.Cells;   // QC ammo_type
        DisplayName = "Vortex";
        Impulse = 7;
        // WEP_FLAG_NORMAL | WEP_FLAG_RELOADABLE | WEP_TYPE_HITSCAN
        SpawnFlags = WeaponFlags.Normal | WeaponFlags.Reloadable | WeaponFlags.TypeHitscan;
        Color = new Vector3(0.459f, 0.765f, 0.835f);
        ViewModel = "h_nex.iqm";   // MDL_VORTEX_VIEW
        WorldModel = "v_nex.md3";  // MDL_VORTEX_WORLD
        ItemModel = "g_nex.md3";   // MDL_VORTEX_ITEM
    }

    public override void Configure()
    {
        Cvars.Damage = Bal("g_balance_vortex_primary_damage", 80f);
        Cvars.Force = Bal("g_balance_vortex_primary_force", 200f);
        Cvars.Refire = Bal("g_balance_vortex_primary_refire", 1.5f);
        Cvars.Animtime = Bal("g_balance_vortex_primary_animtime", 0.4f);
        Cvars.Ammo = Bal("g_balance_vortex_primary_ammo", 6f);

        Cvars.Charge = BalBool("g_balance_vortex_charge", true);
        Cvars.ChargeStart = Bal("g_balance_vortex_charge_start", 0.5f);
        Cvars.ChargeMinDmg = Bal("g_balance_vortex_charge_mindmg", 40f);
        Cvars.ChargeLimit = Bal("g_balance_vortex_charge_limit", 1f);
        Cvars.ChargeRate = Bal("g_balance_vortex_charge_rate", 0.6f);
        Cvars.ChargeAnimLimit = Bal("g_balance_vortex_charge_animlimit", 0.5f);
        Cvars.ChargeShotMul = Bal("g_balance_vortex_charge_shot_multiplier", 0f);
        Cvars.Secondary = BalBool("g_balance_vortex_secondary", false);
    }

    // METHOD(Vortex, wr_think) — common/weapons/weapon/vortex.qc (primary path).
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        var st = actor.WeaponState(slot);

        // W_Vortex_Charge: charge regenerates toward charge_limit over time (charge_rate/tick). This per-tick
        // upkeep runs in the Primary call (the driver calls WrThink(Primary) every tick); skipping it on the
        // Secondary call avoids double-charging while ATK2 is held.
        if (fire == FireMode.Primary && Cvars.Charge && st.VortexCharge < Cvars.ChargeLimit)
            st.VortexCharge = MathF.Min(1f, st.VortexCharge + Cvars.ChargeRate * Api.Clock.FrameTime);

        if (fire == FireMode.Primary)
        {
            // QC: if (weapon_prepareattack(..., refire)) { W_Vortex_Attack(...); weapon_thinkf(..., animtime); }
            if (PrepareAttack(actor, slot, fire))
                Attack(actor, slot, st, isSecondary: false);
        }
        else if (fire == FireMode.Secondary)
        {
            // Secondary is the zoom/charge button: when charge is on and the plain zoom secondary is in
            // use (not g_balance_vortex_secondary), holding it overcharges the beam toward full.
            if (Cvars.Charge && !Cvars.Secondary)
            {
                st.VortexChargeRotTime = Api.Clock.Time; // pause charge rot while actively charging
                if (st.VortexCharge < 1f)
                {
                    float dt = MathF.Min(Api.Clock.FrameTime, (1f - st.VortexCharge) / Cvars.ChargeRate);
                    st.VortexCharge += dt * Cvars.ChargeRate;
                }
            }
            else if (Cvars.Secondary)
            {
                // g_balance_vortex_secondary: secondary is a real (weaker) fire mode — refire-gated.
                if (PrepareAttack(actor, slot, fire))
                    Attack(actor, slot, st, isSecondary: true);
            }
        }
    }

    // W_Vortex_Attack — common/weapons/weapon/vortex.qc
    private void Attack(Entity actor, WeaponSlot slot, WeaponSlotState st, bool isSecondary)
    {
        float mydmg = Cvars.Damage;
        float myforce = Cvars.Force;

        // charge = chargeMinDmg/dmg + (1 - chargeMinDmg/dmg) * vortex_charge, then the shot consumes charge
        // via charge_shot_multiplier (a fast-shot penalty). Uses the per-slot accumulated charge.
        float charge = 1f;
        if (Cvars.Charge && mydmg > 0f)
        {
            float baseFrac = Cvars.ChargeMinDmg / mydmg;
            charge = baseFrac + (1f - baseFrac) * st.VortexCharge;
            st.VortexCharge *= Cvars.ChargeShotMul; // AFTER computing damage/force
        }
        mydmg *= charge;
        myforce *= charge;

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward);

        Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/nexfire.wav");
        // Overcharge sound when charged past the anim limit (a louder zap the more overcharged it is).
        if (Cvars.ChargeAnimLimit > 0f && charge > Cvars.ChargeAnimLimit)
            Api.Sound.Play(actor, SoundChannel.Body, "weapons/nexcharge.wav");

        // FireRailgunBullet: pierces targets, applies knockback `myforce` (+ falloff cvars when set).
        // headshotNotify: false — the Vortex does NOT announce headshots (QC vortex.qc:144).
        Vector3 end = shot.Origin + shot.Dir * WeaponFiring.MaxShotDistance;
        WeaponFiring.FireRailgunBullet(actor, shot.Origin, end, mydmg, RegistryId, myforce,
            headshotNotify: false);

        // W_DecreaseAmmo(thiswep, actor, ammo) — subtract cells (unless unlimited ammo).
        actor.TakeResource(AmmoType, Cvars.Ammo);

        TraceResult impTr = Api.Trace.Trace(shot.Origin, Vector3.Zero, Vector3.Zero, end, MoveFilter.WorldOnly, actor);
        EffectEmitter.Emit("VORTEX_BEAM", shot.Origin, impTr.EndPos, 0);
        WeaponSplash.ImpactSoundAt(impTr.EndPos, "weapons/neximpact.wav"); // QC SND_VORTEX_IMPACT (wr_impacteffect)
        EffectEmitter.Emit("VORTEX_IMPACT", impTr.EndPos, -shot.Dir * 1000f);
        EffectEmitter.Emit("VORTEX_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);
    }

    // METHOD(Vortex, wr_setup / wr_resetplayer) — seed the per-slot charge to charge_start.
    public override void WrSetup(Entity actor, WeaponSlot slot)
    {
        if (Cvars.Charge) actor.WeaponState(slot).VortexCharge = Cvars.ChargeStart;
    }

    // QC the Vortex has a single refire/animtime block (primary); the optional g_balance_vortex_secondary
    // fire mode reuses it, so both modes report the primary timing.
    public override float RefireFor(FireMode fire) => Cvars.Refire;
    public override float AnimtimeFor(FireMode fire) => Cvars.Animtime;

    // METHOD(Vortex, wr_checkammo1) — common/weapons/weapon/vortex.qc
    public bool CheckAmmoPrimary(Entity actor) => actor.GetResource(AmmoType) >= Cvars.Ammo;
}
