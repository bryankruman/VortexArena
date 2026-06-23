using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
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
/// This port covers the per-note radius blast (a HITTYPE_SOUND attack that never hurts teammates), the
/// sustained note entity (held -> refresh, release -> note-off), the W_Tuba_GetNote pitch selection
/// (derived from movement direction since key input is headless), the instrument cycling on reload
/// (Tuba/Accordion/Klein Bottle, which carries the HITTYPE_SECONDARY/BOUNCE obituary bits), the per-note
/// smoke ring, and the wr_setup instrument reset on equip. Only the per-pitch note-sound networking
/// (ENT_CLIENT_TUBANOTE loop/fade/pitch-step) and the melody-recognition chat triggers (W_Tuba_HasPlayed)
/// remain render/online-only.
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

    // METHOD(Tuba, wr_setup) — reset the instrument to Tuba on (re)equip. tuba.qc:355-358
    // The driver calls WrSetup on switch-in (WeaponFireDriver), matching QC's wr_setup on equip.
    public override void WrSetup(Entity actor, WeaponSlot slot)
    {
        actor.WeaponState(slot).TubaInstrument = 0;
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
    // enemies with a small radius hit centered on the player. tuba.qc:262-319
    private void NoteOn(Entity actor, WeaponSlot slot, bool secondary)
    {
        var st = actor.WeaponState(slot);
        int note = GetNote(actor, secondary);

        // Build the deathtype tag exactly as QC does (tuba.qc:266-272):
        //   hittype = HITTYPE_SOUND
        //   if (instrument & 1) hittype |= HITTYPE_SECONDARY   (accordion)
        //   if (instrument & 2) hittype |= HITTYPE_BOUNCE       (klein bottle)
        // HITTYPE_SOUND makes the blast a sound attack: DamageSystem.Apply NEVER hurts teammates (it honors
        // the same-team HITTYPE_SOUND rule). The SECONDARY/BOUNCE bits drive the per-instrument obituary
        // (DeathMessages: BOUNCE -> Klein Bottle, SECONDARY -> Accordeon, else Tuba). The int weapon-id path
        // (ApplyDamage(int)) cannot carry any of these, so we form the STRING deathtype here and pass it via
        // the deathTag override on WeaponSplash.RadiusDamage (the Wave-1 string damage channel).
        // Note: the secondary FIRE MODE itself does not set HITTYPE_SECONDARY (QC's wr_think OR's it, but only
        // the note pitch +7 is observable; the obituary instrument bit comes from the instrument, faithful to
        // W_Tuba_NoteOn which recomputes hittype from the instrument alone before RadiusDamage).
        string deathTag = DeathTypes.WithHitType(DeathTypes.FromWeapon(NetName), DeathTypes.Sound);
        if ((st.TubaInstrument & 1) != 0)
            deathTag = DeathTypes.WithHitType(deathTag, DeathTypes.Secondary);
        if ((st.TubaInstrument & 2) != 0)
            deathTag = DeathTypes.WithHitType(deathTag, DeathTypes.Bounce);

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
            actor, RegistryId, Cvars.Force, deathTag: deathTag);

        // QC plays a per-note pitched loop sample (TUBA_STARTNOTE) chosen by pitch+instrument, sustained and
        // faded client-side via ENT_CLIENT_TUBANOTE. The note-sound networking is render-only; here we cue the
        // per-pitch/instrument sample so the lookup resolves to a real asset (tubaN_loopnoteM.ogg) instead of
        // the non-existent flat "tuba_loopnote.wav". The sustained loop/fade/pitch-step remains a client gap.
        Api.Sound.Play(actor, SoundChannel.Weapon, NoteSample(st.TubaInstrument, note));

        // Per-note smoke ring (EFFECT_SMOKE_RING), throttled to 0.25s, with a per-instrument vertical offset.
        // tuba.qc:307-318. QC reads the weapon-tag origin (gettaginfo); headless, we approximate from the
        // player's eye + view vectors, which is what the tag sits relative to.
        if (Api.Clock.Time > st.TubaSmokeTime)
        {
            QMath.AngleVectors(actor.Angles, out Vector3 fwd, out Vector3 right, out Vector3 up);
            Vector3 org = actor.Origin + actor.ViewOfs;
            Vector3 ringOrg = st.TubaInstrument switch
            {
                1 => org + up * 25f + right * 10f + fwd * 14f,
                2 => org + up * 50f + right * 10f + fwd * 45f,
                _ => org + up * 40f + right * 10f + fwd * 14f,
            };
            EffectEmitter.Emit("SMOKE_RING", ringOrg, up * 100f, 1);
            st.TubaSmokeTime = Api.Clock.Time + 0.25f;
        }
    }

    // W_Tuba_NoteOff — stop the sustained note when the player lets go of fire. tuba.qc:99-134
    private void NoteOff(Entity actor, WeaponSlot slot)
    {
        var st = actor.WeaponState(slot);
        if (st.TubaNote is not null)
        {
            if (!st.TubaNote.IsFreed) Api.Entities.Remove(st.TubaNote);
            st.TubaNote = null;
        }
        // NOTE: QC also records this note into tuba_lastnotes (the melody-recognition ring buffer used by map
        // magic-ear triggers / W_Tuba_HasPlayed) here. That history + chat is not modeled (see todos).
    }

    /// <summary>
    /// Port of W_Tuba_GetNote (tuba.qc): in QC the pitch is chosen from the movement keys held (a 3x3 grid),
    /// shifted by crouch (-12), jump (+12), secondary (+7) and the team/player tuning (+3). The movement-key
    /// and crouch/jump inputs aren't carried by the headless entity, so we derive a movement-direction note
    /// from the player's velocity (a faithful stand-in) plus the +7 secondary shift; pitch only affects the
    /// sound sample, so this divergence is purely cosmetic (registry-flagged intended divergence).
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

    // CSQC TUBA_STARTNOTE(i, n): _Sound_fixpath(W_Sound("tuba" + (i?ftos(i):"") + "_loopnote" + ftos(n))).
    // tuba.qc:430. The instrument prefix is "" for Tuba, "1"/"2" for Accordion/Klein Bottle; the suffix is the
    // signed note pitch. This reproduces the real per-pitch/instrument asset name (e.g. "weapons/tuba_loopnote0",
    // "weapons/tuba1_loopnote-6") that exists in data, instead of the flat "tuba_loopnote.wav" that resolved to
    // nothing. (The client-side pitch-step crossfade / fade-out / sustain loop remain unported.)
    private static string NoteSample(int instrument, int note)
    {
        string i = instrument != 0 ? instrument.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        return "weapons/tuba" + i + "_loopnote"
            + note.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".wav";
    }

    // METHOD(Tuba, wr_reload) — cycle through the three instruments (Tuba -> Accordion -> Klein Bottle).
    // tuba.qc:360-390. Wired from WeaponImpulses.ReloadHandle (Wave-1 seam: a non-Reloadable weapon whose
    // wr_reload is repurposed dispatches here instead of the no-op base WrReload).
    public void Reload(Entity actor, WeaponSlot slot)
    {
        var st = actor.WeaponState(slot);

        // QC: early-return if the slot is not READY (can't cycle mid raise/fire/drop). tuba.qc:363
        if (st.State != WeaponFireState.Ready)
            return;

        // Cycle 0 -> 1 -> 2 -> 0 (Tuba -> Accordion -> Klein Bottle). tuba.qc:366-380
        st.TubaInstrument = (st.TubaInstrument + 1) % 3;

        // QC plays Send_Effect(EFFECT_TELEPORT, w_shotorg, ...) on the instrument switch (tuba.qc:387).
        EffectEmitter.Emit("TELEPORT", actor.Origin + actor.ViewOfs, Vector3.Zero, 1);

        // QC: state = WS_INUSE; weapon_thinkf(WFRAME_RELOAD, 0.5, w_ready) — lock the slot for the 0.5s reload
        // anim then settle back to READY. tuba.qc:388-389.
        st.State = WeaponFireState.InUse;
        WeaponFireDriver.ScheduleThink(st, 0.5f, static (pl, sl) =>
        {
            var s2 = pl.WeaponState(sl);
            if (s2.State == WeaponFireState.InUse)
                s2.State = WeaponFireState.Ready;
        });
    }

    // METHOD(Tuba, wr_checkammo1/2) — tuba.qc (infinite ammo).
    public bool CheckAmmoPrimary(Entity actor) => true;
    public bool CheckAmmoSecondary(Entity actor) => true;
}
