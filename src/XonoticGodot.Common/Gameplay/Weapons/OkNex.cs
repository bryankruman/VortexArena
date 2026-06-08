// Port of common/mutators/mutator/overkill/oknex.{qh,qc}

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Overkill Nex — port of common/mutators/mutator/overkill/oknex.{qh,qc}. The Overkill loadout's
/// long-range rail: primary fires an instant piercing beam (W_OverkillNex_Attack → FireRailgunBullet,
/// damage 100, force 500); the secondary is the shared Overkill blaster jump (on <c>actor.jump_interval</c>).
/// Only granted by the Overkill mutator (WEP_FLAG_HIDDEN | WEP_FLAG_MUTATORBLOCKED); resolved by NetName
/// "oknex".
///
/// Identity/attributes from oknex.qh; balance from bal-wep-xonotic.cfg (g_balance_oknex_*). Ported: the rail
/// beam (no headshot announce, like the Vortex), the forced reload, and the secondary blaster-jump.
///
/// CHARGE IS OFF BY DEFAULT (g_balance_oknex_charge 0) — so this is "Vortex without charge": <c>charge = 1</c>
/// and no charge math runs. The velocity-charge model (the oknex_charge GetPressedKeys hook,
/// charge_velocity_rate, wr_glow) and the chargepool are all charge-gated and therefore inert at the shipped
/// balance; they're documented as deferred (parity risk §11: don't port the charge path unless the cvar is set).
/// </summary>
[Weapon]
public sealed class OkNex : Weapon
{
    /// <summary>Primary-fire balance block — QC WEP_CVAR_PRI(WEP_OVERKILL_NEX, *) + the (default-off) charge cvars.</summary>
    public struct Balance
    {
        public float Ammo;              // g_balance_oknex_primary_ammo
        public float Animtime;          // g_balance_oknex_primary_animtime
        public float Damage;            // g_balance_oknex_primary_damage
        public float Force;             // g_balance_oknex_primary_force
        public float Refire;            // g_balance_oknex_primary_refire
        public bool  Charge;            // g_balance_oknex_charge (default 0 → no charge)
        public float ChargeMinDmg;      // g_balance_oknex_charge_mindmg
        public float ChargeShotMul;     // g_balance_oknex_charge_shot_multiplier
        public float ChargeStart;       // g_balance_oknex_charge_start
        public float ChargeLimit;       // g_balance_oknex_charge_limit
        public float ChargeRate;        // g_balance_oknex_charge_rate
        public int   SecondaryRefireType; // g_balance_oknex_secondary_refire_type (1 = own jump_interval timer)
        public float ReloadAmmo;        // g_balance_oknex_reload_ammo
        public float ReloadTime;        // g_balance_oknex_reload_time
    }

    public Balance Cvars;

    public OkNex()
    {
        NetName = "oknex";
        AmmoType = ResourceType.Cells;   // QC ammo_type RES_CELLS
        DisplayName = "Overkill Nex";
        Impulse = 7;
        // WEP_FLAG_HIDDEN | WEP_FLAG_RELOADABLE | WEP_TYPE_HITSCAN | WEP_FLAG_MUTATORBLOCKED
        SpawnFlags = WeaponFlags.Hidden | WeaponFlags.Reloadable | WeaponFlags.TypeHitscan
                   | WeaponFlags.MutatorBlocked;
        Color = new Vector3(0.459f, 0.765f, 0.835f);
        ViewModel = "h_ok_sniper.iqm";   // MDL_OK_NEX_VIEW
        WorldModel = "v_ok_sniper.md3";  // MDL_OK_NEX_WORLD
        ItemModel = "g_ok_sniper.md3";   // MDL_OK_NEX_ITEM
    }

    public override void Configure()
    {
        Cvars.Ammo = Bal("g_balance_oknex_primary_ammo", 10f);
        Cvars.Animtime = Bal("g_balance_oknex_primary_animtime", 0.65f);
        Cvars.Damage = Bal("g_balance_oknex_primary_damage", 100f);
        Cvars.Force = Bal("g_balance_oknex_primary_force", 500f);
        Cvars.Refire = Bal("g_balance_oknex_primary_refire", 1f);
        Cvars.Charge = BalBool("g_balance_oknex_charge", false);
        Cvars.ChargeMinDmg = Bal("g_balance_oknex_charge_mindmg", 40f);
        Cvars.ChargeShotMul = Bal("g_balance_oknex_charge_shot_multiplier", 0f);
        Cvars.ChargeStart = Bal("g_balance_oknex_charge_start", 0.5f);
        Cvars.ChargeLimit = Bal("g_balance_oknex_charge_limit", 1f);
        Cvars.ChargeRate = Bal("g_balance_oknex_charge_rate", 0.6f);
        Cvars.SecondaryRefireType = BalInt("g_balance_oknex_secondary_refire_type", 1);
        Cvars.ReloadAmmo = Bal("g_balance_oknex_reload_ammo", 50f);
        Cvars.ReloadTime = Bal("g_balance_oknex_reload_time", 2f);
    }

    // METHOD(OverkillNex, wr_think)
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        var st = actor.WeaponState(slot);

        // Charge regenerates toward the limit (only when charge is enabled — default OFF, so this is inert).
        if (Cvars.Charge && st.VortexCharge < Cvars.ChargeLimit)
            st.VortexCharge = MathF.Min(1f, st.VortexCharge + Cvars.ChargeRate * Api.Clock.FrameTime);

        // Secondary blaster-jump on the dedicated jump_interval timer (refire_type 1).
        OkWeapons.FireSecondaryBlasterJump(actor, slot, fire, Cvars.SecondaryRefireType);

        // forced reload
        if (Cvars.ReloadAmmo != 0f && st.ClipLoad < Cvars.Ammo)
        {
            WrReload(actor, slot);
            return;
        }

        if (fire == FireMode.Primary)
        {
            // weapon_prepareattack(thiswep, actor, weaponentity, false, refire)
            if (PrepareAttack(actor, slot, fire))
                Attack(actor, slot, st);
        }
    }

    public override float RefireFor(FireMode fire) => Cvars.Refire;
    public override float AnimtimeFor(FireMode fire) => Cvars.Animtime;

    // METHOD(OverkillNex, wr_setup / wr_resetplayer) — seed the per-slot charge (no-op when charge off).
    public override void WrSetup(Entity actor, WeaponSlot slot)
    {
        if (Cvars.Charge) actor.WeaponState(slot).VortexCharge = Cvars.ChargeStart;
    }

    // W_OverkillNex_Attack(thiswep, actor, weaponentity, false)
    private void Attack(Entity actor, WeaponSlot slot, WeaponSlotState st)
    {
        float mydmg = Cvars.Damage;
        float myforce = Cvars.Force;

        // charge = charge_mindmg/dmg + (1 - charge_mindmg/dmg) * oknex_charge; then consume via shot_multiplier.
        // With charge OFF (default) charge == 1 (Vortex-without-charge).
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

        // FireRailgunBullet(actor, weaponentity, w_shotorg, w_shotorg + w_shotdir * max_shot_distance, mydmg, true, myforce, ...)
        // headshotNotify mirrors the Vortex (false): the OK Nex does not announce headshots.
        Vector3 end = shot.Origin + shot.Dir * WeaponFiring.MaxShotDistance;
        WeaponFiring.FireRailgunBullet(actor, shot.Origin, end, mydmg, RegistryId, myforce,
            headshotNotify: false);

        // W_DecreaseAmmo(thiswep, actor, ammo) — clip-aware (WEP_FLAG_RELOADABLE): drains the magazine so the
        // wr_think forced-reload branch (clip_load < ammo) engages. oknex.qc:108 (ammo) + :157-162 (reload).
        DecreaseAmmo(actor, slot, Cvars.Ammo);
    }

    // METHOD(OverkillNex, wr_checkammo1)
    public bool CheckAmmoPrimary(Entity actor) => actor.GetResource(AmmoType) >= Cvars.Ammo;

    // METHOD(OverkillNex, wr_checkammo2) — Blaster secondary is unlimited (secondary == 2 = blaster).
    public bool CheckAmmoSecondary(Entity actor) => true;

    public override float ReloadingAmmo() => Cvars.ReloadAmmo;
    public override float ReloadingTime() => Cvars.ReloadTime;
}
