using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The @!#%'n Tuba — port of common/weapons/weapon/tuba.{qh,qc}. A hidden splash weapon that "plays notes"
/// which damage and knock back nearby enemies. Each press (primary or secondary) plays a note, which
/// applies a small radius blast centered on the player. The note's pitch is chosen from the movement keys
/// held; secondary plays a higher pitch. Reload cycles through three instruments (Tuba/Accordion/Klein
/// Bottle). It uses no ammo.
///
/// Identity/attributes from tuba.qh; balance from bal-wep-xonotic.cfg (g_balance_tuba_*).
/// This port covers the per-note radius blast, the sustained note entity (held -> refresh, release ->
/// note-off), the W_Tuba_GetNote pitch selection (derived from movement direction since key input is
/// headless), and the instrument cycling on reload (Tuba/Accordion/Klein Bottle). Only the note-on/off
/// sound networking and the melody-recognition chat triggers (W_Tuba_HasPlayed) are render/online-only.
/// </summary>
[Weapon]
public sealed class Tuba : Weapon
{
    /// <summary>Balance block — QC WEP_CVAR(WEP_TUBA, *) (single block, no PRI/SEC split).</summary>
    public struct Balance
    {
        public float Animtime;    // g_balance_tuba_animtime
        public float Attenuation; // g_balance_tuba_attenuation (sound falloff; affects networking only)
        public float Damage;      // g_balance_tuba_damage (per note, at the blast core)
        public float EdgeDamage;  // g_balance_tuba_edgedamage
        public float Force;       // g_balance_tuba_force
        public float Radius;      // g_balance_tuba_radius
        public float Refire;      // g_balance_tuba_refire
    }

    public Balance Cvars;

    public Tuba()
    {
        NetName = "tuba";
        DisplayName = "@!#%'n Tuba";
        Impulse = 1;
        // WEP_FLAG_HIDDEN | WEP_TYPE_SPLASH | WEP_FLAG_NODUAL | WEP_FLAG_NOTRUEAIM
        SpawnFlags = WeaponFlags.Hidden | WeaponFlags.TypeSplash | WeaponFlags.NoDual | WeaponFlags.NoTrueAim;
        Color = new Vector3(0.909f, 0.816f, 0.345f);
        ViewModel = "h_tuba.iqm";  // MDL_TUBA_VIEW
        WorldModel = "v_tuba.md3"; // MDL_TUBA_WORLD
        ItemModel = "g_tuba.md3";  // MDL_TUBA_ITEM
    }

    public override void Configure()
    {
        Cvars.Animtime = Bal("g_balance_tuba_animtime", 0.05f);
        Cvars.Attenuation = Bal("g_balance_tuba_attenuation", 0.5f);
        Cvars.Damage = Bal("g_balance_tuba_damage", 5f);
        Cvars.EdgeDamage = Bal("g_balance_tuba_edgedamage", 0f);
        Cvars.Force = Bal("g_balance_tuba_force", 40f);
        Cvars.Radius = Bal("g_balance_tuba_radius", 200f);
        Cvars.Refire = Bal("g_balance_tuba_refire", 0.05f);
    }

    // METHOD(Tuba, wr_think) — common/weapons/weapon/tuba.qc
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        var st = actor.WeaponState(slot);

        // The Tuba is a CONTINUOUS weapon — it sustains a note while a fire button is held. The note's blast
        // is refire-gated (tuba_refire ~0.05s) via PrepareAttack; the note entity's keep-alive (refire*2)
        // bridges the gap between blasts so a held note doesn't gap. The driver calls WrThink(Primary) every
        // tick, so we read the held buttons directly (st.ButtonAttack/2) and stop the note when neither is up.
        if (fire == FireMode.Secondary)
        {
            // Secondary blast (raises the pitch by +7); the Primary call already handled the primary blast.
            if (PrepareAttack(actor, slot, fire))
                NoteOn(actor, slot, secondary: true);
            return;
        }

        if (st.ButtonAttack)
        {
            if (PrepareAttack(actor, slot, fire))
                NoteOn(actor, slot, secondary: false);
        }
        else if (!st.ButtonAttack2)
        {
            NoteOff(actor, slot); // neither fire button down -> stop the sustained note
        }
    }

    // Refire/animtime from the (cvar-seeded) balance block (a single tuba_refire/animtime for both pitches).
    public override float RefireFor(FireMode fire) => Cvars.Refire;
    public override float AnimtimeFor(FireMode fire) => Cvars.Animtime;

    // W_Tuba_NoteOn — play a note: pick the pitch, spawn/refresh the sustained note entity, and blast nearby
    // enemies with a small radius hit centered on the player. tuba.qc
    private void NoteOn(Entity actor, WeaponSlot slot, bool secondary)
    {
        var st = actor.WeaponState(slot);
        int note = GetNote(actor, secondary);

        // The deathtype carries the instrument as HITTYPE flags (bit0 -> secondary/accordion, bit1 -> klein
        // bottle) so obituaries name the right instrument.
        int deathType = RegistryId;

        // Sustained note entity: refresh if the pitch/instrument is unchanged, else restart.
        Entity? cur = st.TubaNote;
        if (cur is not null && !cur.IsFreed && (cur.Count != note || cur.Frame != st.TubaInstrument))
        {
            NoteOff(actor, slot);
            cur = null;
        }
        if (cur is null || cur.IsFreed)
        {
            Entity n = Api.Entities.Spawn();
            n.ClassName = "tuba_note";
            n.Owner = actor;
            n.NetName = NetName;
            n.Count = note;                 // .cnt = note pitch
            n.Frame = st.TubaInstrument;    // which instrument
            Api.Entities.SetOrigin(n, actor.Origin);
            st.TubaNote = n;
        }
        // teleport_time: keep the note alive a little past the refire so a held note doesn't gap.
        if (st.TubaNote is not null)
            st.TubaNote.MaxHealth = Api.Clock.Time + Cvars.Refire * 2f;

        WeaponSplash.RadiusDamage(actor, actor.Origin, Cvars.Damage, Cvars.EdgeDamage, Cvars.Radius,
            actor, deathType, Cvars.Force);

        // QC plays a per-note loop sample chosen by pitch/instrument; the single base note stands in here.
        Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/tuba_loopnote.wav");
    }

    // W_Tuba_NoteOff — stop the sustained note when the player lets go of fire. tuba.qc
    private void NoteOff(Entity actor, WeaponSlot slot)
    {
        var st = actor.WeaponState(slot);
        if (st.TubaNote is not null)
        {
            if (!st.TubaNote.IsFreed) Api.Entities.Remove(st.TubaNote);
            st.TubaNote = null;
        }
    }

    /// <summary>
    /// Port of W_Tuba_GetNote (tuba.qc): in QC the pitch is chosen from the movement keys held (a 3x3 grid),
    /// shifted by crouch (-12), jump (+12), secondary (+7) and the team/player tuning (+3). The movement-key
    /// and crouch/jump inputs aren't carried by the headless entity, so we derive a movement-direction note
    /// from the player's velocity (a faithful stand-in) plus the +7 secondary shift; pitch only affects the
    /// sound sample, so this is purely cosmetic.
    /// </summary>
    private int GetNote(Entity actor, bool secondary)
    {
        // Map the horizontal velocity direction (relative to facing) to the same 1..9 movestate grid QC uses.
        QMath.AngleVectors(actor.Angles, out Vector3 fwd, out Vector3 right, out _);
        float vf = QMath.Dot(actor.Velocity, fwd);
        float vr = QMath.Dot(actor.Velocity, right);
        int movestate = 5;
        if (vf < -16f) movestate -= 3; else if (vf > 16f) movestate += 3;
        if (vr < -16f) movestate -= 1; else if (vr > 16f) movestate += 1;

        int note = movestate switch
        {
            1 => -6, 2 => -5, 3 => -4, 4 => +5,
            6 => +2, 7 => +3, 8 => +4, 9 => -1,
            _ => 0,
        };
        if (secondary) note += 7;
        return note;
    }

    // METHOD(Tuba, wr_reload) — cycle through the three instruments (Tuba -> Accordion -> Klein Bottle). tuba.qc
    public void Reload(Entity actor, WeaponSlot slot)
    {
        var st = actor.WeaponState(slot);
        st.TubaInstrument = (st.TubaInstrument + 1) % 3;
        Api.Sound.Play(actor, SoundChannel.Body, "misc/teleport.wav");
    }

    // METHOD(Tuba, wr_checkammo1/2) — tuba.qc (infinite ammo).
    public bool CheckAmmoPrimary(Entity actor) => true;
    public bool CheckAmmoSecondary(Entity actor) => true;
}
