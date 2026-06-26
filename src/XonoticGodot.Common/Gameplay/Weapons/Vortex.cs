using System.Numerics;
using System.Runtime.CompilerServices;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Vortex (Nexuiz "Nex") — port of common/weapons/weapon/vortex.{qh,qc}. A hitscan rail weapon:
/// primary fire is an instant beam dealing a large fixed chunk of damage, consuming cells. The hold-to-
/// overcharge mechanic is modeled in full (T57): per-tick charge regen (gated by <c>charge_always</c>),
/// the secondary charge ladder — chargepool / secondary-ammo-eating / free — with the chargepool regen +
/// player-health-regen pause interaction, the forced reload, the velocity-charge mutator, and the optional
/// real secondary fire mode (<c>g_balance_vortex_secondary</c>).
///
/// Identity/attributes from vortex.qh; balance from bal-wep-xonotic.cfg (g_balance_vortex_*).
/// </summary>
[Weapon]
public sealed class Vortex : Weapon
{
    /// <summary>Balance block — QC WEP_CVAR(WEP_VORTEX, *) (primary + secondary + charge cvars).</summary>
    public struct Balance
    {
        public float Damage;             // g_balance_vortex_primary_damage
        public float Force;              // g_balance_vortex_primary_force
        public float Refire;             // g_balance_vortex_primary_refire
        public float Animtime;           // g_balance_vortex_primary_animtime
        public float Ammo;               // g_balance_vortex_primary_ammo (cells per shot)

        public bool  Charge;             // g_balance_vortex_charge
        public bool  ChargeAlways;       // g_balance_vortex_charge_always
        public float ChargeStart;        // g_balance_vortex_charge_start
        public float ChargeMinDmg;       // g_balance_vortex_charge_mindmg
        public float ChargeLimit;        // g_balance_vortex_charge_limit
        public float ChargeRate;         // g_balance_vortex_charge_rate
        public float ChargeAnimLimit;    // g_balance_vortex_charge_animlimit
        public float ChargeShotMul;      // g_balance_vortex_charge_shot_multiplier
        public float ChargeRotPause;     // g_balance_vortex_charge_rot_pause
        public float ChargeVelocityRate; // g_balance_vortex_charge_velocity_rate
        public float ChargeMinSpeed;     // g_balance_vortex_charge_minspeed
        public float ChargeMaxSpeed;     // g_balance_vortex_charge_maxspeed

        public bool  Secondary;          // g_balance_vortex_secondary (0 = zoom, not a fire mode)
        public float SecondaryDamage;    // g_balance_vortex_secondary_damage
        public float SecondaryForce;     // g_balance_vortex_secondary_force
        public float SecondaryRefire;    // g_balance_vortex_secondary_refire
        public float SecondaryAnimtime;  // g_balance_vortex_secondary_animtime
        public float SecondaryAmmo;      // g_balance_vortex_secondary_ammo (cells/sec while charging)
        public bool  SecondaryChargePool;       // g_balance_vortex_secondary_chargepool
        public float ChargePoolRegen;           // g_balance_vortex_secondary_chargepool_regen
        public float ChargePoolPauseRegen;      // g_balance_vortex_secondary_chargepool_pause_regen
        public float ReloadAmmo;         // g_balance_vortex_reload_ammo (clip size; 0 = not reloadable)
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

    // QC vortex.qh w_reticle + vortex.qc wr_zoom/wr_zoomdir: while g_balance_vortex_secondary is 0 (the stock
    // default — secondary is NOT a separate fire mode) holding ATTACK2 is the ZOOM ("the secondary fire zooms in
    // when held, allowing for ease of aiming"), and the scope overlay is gfx/reticle_nex. With secondary enabled,
    // ATTACK2 becomes a real (weaker) fire mode and the zoom is disabled (wr_zoomdir → false).
    public override string? Reticle => "gfx/reticle_nex";
    public override bool ZoomOnSecondary => !Cvars.Secondary;

    public override void Configure()
    {
        Cvars.Damage = Bal("g_balance_vortex_primary_damage", 80f);
        Cvars.Force = Bal("g_balance_vortex_primary_force", 200f);
        Cvars.Refire = Bal("g_balance_vortex_primary_refire", 1.5f);
        Cvars.Animtime = Bal("g_balance_vortex_primary_animtime", 0.4f);
        Cvars.Ammo = Bal("g_balance_vortex_primary_ammo", 6f);

        Cvars.Charge = BalBool("g_balance_vortex_charge", true);
        Cvars.ChargeAlways = BalBool("g_balance_vortex_charge_always", false);
        Cvars.ChargeStart = Bal("g_balance_vortex_charge_start", 0.5f);
        Cvars.ChargeMinDmg = Bal("g_balance_vortex_charge_mindmg", 40f);
        Cvars.ChargeLimit = Bal("g_balance_vortex_charge_limit", 1f);
        Cvars.ChargeRate = Bal("g_balance_vortex_charge_rate", 0.6f);
        Cvars.ChargeAnimLimit = Bal("g_balance_vortex_charge_animlimit", 0.5f);
        Cvars.ChargeShotMul = Bal("g_balance_vortex_charge_shot_multiplier", 0f);
        Cvars.ChargeRotPause = Bal("g_balance_vortex_charge_rot_pause", 0f);
        Cvars.ChargeVelocityRate = Bal("g_balance_vortex_charge_velocity_rate", 0f);
        Cvars.ChargeMinSpeed = Bal("g_balance_vortex_charge_minspeed", 400f);
        Cvars.ChargeMaxSpeed = Bal("g_balance_vortex_charge_maxspeed", 800f);

        Cvars.Secondary = BalBool("g_balance_vortex_secondary", false);
        Cvars.SecondaryDamage = Bal("g_balance_vortex_secondary_damage", 0f);
        Cvars.SecondaryForce = Bal("g_balance_vortex_secondary_force", 0f);
        Cvars.SecondaryRefire = Bal("g_balance_vortex_secondary_refire", 0f);
        Cvars.SecondaryAnimtime = Bal("g_balance_vortex_secondary_animtime", 0f);
        Cvars.SecondaryAmmo = Bal("g_balance_vortex_secondary_ammo", 2f);
        Cvars.SecondaryChargePool = BalBool("g_balance_vortex_secondary_chargepool", false);
        Cvars.ChargePoolRegen = Bal("g_balance_vortex_secondary_chargepool_regen", 0.15f);
        Cvars.ChargePoolPauseRegen = Bal("g_balance_vortex_secondary_chargepool_pause_regen", 1f);
        Cvars.ReloadAmmo = Bal("g_balance_vortex_reload_ammo", 0f);
    }

    // METHOD(Vortex, wr_think) — common/weapons/weapon/vortex.qc:185-276.
    //
    // dt: QC runs wr_think TWICE per server frame with frametime/W_TICSPERFRAME (weaponsystem.qc:595,
    // W_TICSPERFRAME=2); the port's driver runs WrThink once per tick with the full frametime — the same
    // per-second charge/regen/deplete rates.
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        var st = actor.WeaponState(slot);
        float dt = Api.Clock.FrameTime;

        if (fire == FireMode.Primary)
        {
            // QC vortex.qc:187-188 — W_Vortex_Charge regen (only toward charge_limit) unless charge_always
            // (whose every-PlayerFrame path, server/client.qc:2461, is gated off stock: charge_always 0).
            if (!Cvars.ChargeAlways)
                Charge(st, dt);

            // Velocity-charge (vortex.qc:89-111, the vortex_charge GetPressedKeys mutator hook): while
            // HOLDING the vortex and moving faster than minspeed, charge rises by velocity_rate scaled by
            // how close xyspeed is to maxspeed. The hook requires m_weapon == WEP_VORTEX, and the driver
            // only calls WrThink for the held weapon, so the per-tick condition is identical. Stock
            // charge_velocity_rate is 0 (off).
            if (Cvars.Charge && Cvars.ChargeVelocityRate > 0f && Cvars.ChargeMaxSpeed > Cvars.ChargeMinSpeed)
            {
                float xyspeed = new Vector2(actor.Velocity.X, actor.Velocity.Y).Length();
                if (xyspeed > Cvars.ChargeMinSpeed)
                {
                    xyspeed = MathF.Min(xyspeed, Cvars.ChargeMaxSpeed);
                    float f = (xyspeed - Cvars.ChargeMinSpeed) / (Cvars.ChargeMaxSpeed - Cvars.ChargeMinSpeed);
                    st.VortexCharge = MathF.Min(1f, st.VortexCharge + Cvars.ChargeVelocityRate * f * dt);
                }
            }

            // Chargepool regen (vortex.qc:190-196): regen only after the pause window; while the pool is
            // below full, the PLAYER's health-regen pause is continuously pushed out.
            if (Cvars.SecondaryChargePool && st.VortexChargePoolAmmo < 1f)
            {
                if (actor.VortexChargepoolPauseRegenFinished < Api.Clock.Time)
                    st.VortexChargePoolAmmo = MathF.Min(1f, st.VortexChargePoolAmmo + Cvars.ChargePoolRegen * dt);
                actor.PauseRegenFinished = MathF.Max(actor.PauseRegenFinished,
                    Api.Clock.Time + Cvars.ChargePoolPauseRegen);
            }

            // Forced reload (vortex.qc:198-202): clip below the cheaper mode's cost -> reload and bail.
            if (ForcedReload(actor, slot, st))
                return;

            // QC: if (weapon_prepareattack(..., refire)) { W_Vortex_Attack(...); weapon_thinkf(..., animtime); }
            if (PrepareAttack(actor, slot, fire))
                Attack(actor, slot, st, isSecondary: false);
        }
        else if (fire == FireMode.Secondary)
        {
            // The driver invokes the Secondary call only while ATK2 is held — the port's stand-in for QC's
            // charge key selection (vortex.qc:212): ZOOM when (charge && !secondary), else fire&2. With no
            // separate zoom button, ATK2 serves both roles, so the inner "only eat ammo when the button is
            // pressed (fire & 2)" check (vortex.qc:237) is implied by reaching this branch.
            if (ForcedReload(actor, slot, st))
                return;

            if (Cvars.Charge)
            {
                // ---- the charging ladder (vortex.qc:214-265) ----
                st.VortexChargeRotTime = Api.Clock.Time + Cvars.ChargeRotPause;
                if (st.VortexCharge < 1f)
                {
                    if (Cvars.SecondaryChargePool)
                    {
                        if (Cvars.SecondaryAmmo > 0f)
                        {
                            // always deplete while the key is held (vortex.qc:226)
                            st.VortexChargePoolAmmo =
                                MathF.Max(0f, st.VortexChargePoolAmmo - Cvars.SecondaryAmmo * dt);

                            float cdt = MathF.Min(dt, (1f - st.VortexCharge) / Cvars.ChargeRate);
                            actor.VortexChargepoolPauseRegenFinished =
                                Api.Clock.Time + Cvars.ChargePoolPauseRegen;
                            cdt = QMath.Clamp(cdt, 0f, st.VortexChargePoolAmmo);

                            st.VortexCharge += cdt * Cvars.ChargeRate;
                        }
                    }
                    else if (Cvars.SecondaryAmmo > 0f)
                    {
                        // sec-ammo path (vortex.qc:235-259): eat cells while charging, but never let the
                        // reserve (or the clip, with reload_ammo) drop below the PRIMARY shot cost.
                        float cdt = MathF.Min(dt, (1f - st.VortexCharge) / Cvars.ChargeRate);
                        bool unlimited = actor.UnlimitedAmmo || (actor.Items & ItUnlimitedAmmoBit) != 0;
                        if (!unlimited)
                        {
                            if (Cvars.ReloadAmmo > 0f)
                            {
                                cdt = QMath.Clamp(cdt, 0f, (st.ClipLoad - Cvars.Ammo) / Cvars.SecondaryAmmo);
                                if (cdt > 0f)
                                    st.ClipLoad = (int)MathF.Max(Cvars.SecondaryAmmo,
                                        st.ClipLoad - Cvars.SecondaryAmmo * cdt);
                                SetWeaponLoad(st, RegistryId, st.ClipLoad);
                            }
                            else
                            {
                                float res = actor.GetResource(AmmoType);
                                cdt = QMath.Clamp(cdt, 0f, (res - Cvars.Ammo) / Cvars.SecondaryAmmo);
                                if (cdt > 0f)
                                    actor.SetResource(AmmoType,
                                        MathF.Max(Cvars.SecondaryAmmo, res - Cvars.SecondaryAmmo * cdt));
                            }
                        }
                        st.VortexCharge += cdt * Cvars.ChargeRate;
                    }
                    else
                    {
                        // free path (vortex.qc:260-264)
                        float cdt = MathF.Min(dt, (1f - st.VortexCharge) / Cvars.ChargeRate);
                        st.VortexCharge += cdt * Cvars.ChargeRate;
                    }
                }
            }
            else if (Cvars.Secondary)
            {
                // g_balance_vortex_secondary without charge: a real (weaker) fire mode — refire-gated.
                if (PrepareAttack(actor, slot, fire))
                    Attack(actor, slot, st, isSecondary: true);
            }
        }
    }

    // W_Vortex_Charge (vortex.qc:174-178): regen toward charge_limit (a charge above the limit — e.g. from
    // velocity charging — is left alone, it only decays via the rot path, stock-disabled).
    private void Charge(WeaponSlotState st, float dt)
    {
        if (Cvars.Charge && st.VortexCharge < Cvars.ChargeLimit)
            st.VortexCharge = MathF.Min(1f, st.VortexCharge + Cvars.ChargeRate * dt);
    }

    // vortex.qc:198-202: autocvar_g_balance_vortex_reload_ammo && clip_load < min(pri_ammo, sec_ammo).
    private bool ForcedReload(Entity actor, WeaponSlot slot, WeaponSlotState st)
    {
        if (Cvars.ReloadAmmo <= 0f || st.ClipLoad >= MathF.Min(Cvars.Ammo, Cvars.SecondaryAmmo))
            return false;
        WrReload(actor, slot);
        return true;
    }

    // W_Vortex_Attack — common/weapons/weapon/vortex.qc:113-170.
    private void Attack(Entity actor, WeaponSlot slot, WeaponSlotState st, bool isSecondary)
    {
        float mydmg = isSecondary ? Cvars.SecondaryDamage : Cvars.Damage;
        float myforce = isSecondary ? Cvars.SecondaryForce : Cvars.Force;

        // charge = chargeMinDmg/dmg + (1 - chargeMinDmg/dmg) * vortex_charge, then the shot consumes charge
        // via charge_shot_multiplier (a fast-shot penalty). Uses the per-slot accumulated charge.
        float charge = 1f;
        if (Cvars.Charge && mydmg > 0f)
        {
            float baseFrac = Cvars.ChargeMinDmg / mydmg;
            charge = baseFrac + (1f - baseFrac) * st.VortexCharge;
            st.VortexCharge *= Cvars.ChargeShotMul; // AFTER setting mydmg/myforce
        }
        mydmg *= charge;
        myforce *= charge;

        // QC vortex.qc:122: capture IsFlying(actor) BEFORE the trace — the rail trace overwrites the global
        // trace state FireRailgunBullet's yoda check reads, so the shooter's airborne status (for the yoda
        // mid-air-kill announce) must be sampled here, before firing.
        bool flying = IsFlying(actor);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        // QC vortex.qc:137: W_SetupShot(..., mydmg, dtype) — the fired credit is the POST-charge damage.
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, wep: this, maxDamage: mydmg, recoil: 5f);

        Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/nexfire.wav");
        // Overcharge sound when charged past the anim limit — a LOUDER zap the more overcharged it is. QC
        // vortex.qc:139: VOL_BASE * (charge - 0.5*animlimit) / (1 - 0.5*animlimit). VOL_BASE = 0.7 (sound.qh).
        if (Cvars.ChargeAnimLimit > 0f && charge > Cvars.ChargeAnimLimit)
        {
            const float volBase = 0.7f; // QC VOL_BASE
            float vol = volBase * (charge - 0.5f * Cvars.ChargeAnimLimit) / (1f - 0.5f * Cvars.ChargeAnimLimit);
            Api.Sound.Play(actor, SoundChannel.Body, "weapons/nexcharge.wav", volume: vol);
        }

        // FireRailgunBullet: pierces targets, applies knockback `myforce` (+ falloff cvars when set).
        // headshotNotify: false — the Vortex does NOT announce headshots (QC vortex.qc:144).
        Vector3 end = shot.Origin + shot.Dir * WeaponFiring.CurrentMaxShotDistance;
        Entity? hit = WeaponFiring.FireRailgunBullet(actor, shot.Origin, end, mydmg, RegistryId, myforce,
            headshotNotify: false);

        // Yoda (mid-air rail kill) + Impressive (every-other cross-team rail hit) announcements — QC
        // vortex.qc:141-162. QC sets the `yoda` / `impressive_hits` globals inside the Damage path
        // (server/damage.qc:646-651) on a cross-team damaging hit, and yoda additionally requires the victim
        // to be a flying PLAYER. The port's FireRailgunBullet doesn't surface those globals, so we re-derive
        // them locally from the first pierced victim (the rail's primary target): a live, damageable,
        // cross-team player counts as an impressive hit; if that victim is also airborne AND the shooter is
        // airborne, it's a yoda.
        Announce(actor, hit, flying);

        // W_DecreaseAmmo(thiswep, actor, WEP_CVAR_BOTH(ammo)) — clip/resource via the shared helper.
        DecreaseAmmo(actor, slot, isSecondary ? Cvars.SecondaryAmmo : Cvars.Ammo);

        TraceResult impTr = Api.Trace.Trace(shot.Origin, Vector3.Zero, Vector3.Zero, end, MoveFilter.WorldOnly, actor);
        EmitBeam(actor, shot.Origin, impTr.EndPos, charge);
        WeaponSplash.ImpactSoundAt(impTr.EndPos, "weapons/neximpact.wav"); // QC SND_VORTEX_IMPACT (wr_impacteffect)
        // QC vortex wr_impacteffect: boxparticles(EFFECT_VORTEX_IMPACT, .., '0 0 0', '0 0 0', 1, ..) — the impact
        // burst carries NO inherited velocity (its own velocityjitter/sizeincrease do the work).
        EffectEmitter.Emit("VORTEX_IMPACT", impTr.EndPos);
        EffectEmitter.Emit("VORTEX_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);
    }

    // Charged beam particle — port of vortex.qc:37-82 (SendCSQCVortexBeamParticle + the
    // TE_CSQC_VORTEXBEAMPARTICLE NET_HANDLE). QC nets shotorg/endpos/charge(byte)/owner and the CLIENT draws the
    // EFFECT_VORTEX_BEAM trail, with charge=sqrt(charge) divided across trail spacing and alpha, and — in team
    // mode with cl_tracers_teamcolor — tinted via vortex_glowcolor(owner_colors, charge). The port emits the beam
    // server-side and broadcasts the colour, so the team tint is computed here from the FIRER's charge + team.
    //
    // Limitations (charge is server-only, not networked cross-client): the sqrt(charge) ALPHA/FADE/trail-spacing
    // scaling has no port-side hook (EffectSystem.Spawn carries no per-emission alpha for trails), so the beam's
    // brightness is unmodulated — only the colour tint is reproduced. cl_tracers_teamcolor / cl_particles_oldvortexbeam
    // are read here as the single broadcast value (the per-recipient client cvar nuance is not reproducible from a
    // single broadcast emit).
    private void EmitBeam(Entity actor, Vector3 shotorg, Vector3 endpos, float charge)
    {
        // QC vortex.qc:61: charge = sqrt(charge) (the value the client would receive as a 0..255 byte, /255).
        float beamCharge = MathF.Sqrt(QMath.Clamp(charge, 0f, 1f));

        // QC vortex.qc:76-79: cl_particles_oldvortexbeam selects the legacy EFFECT_VORTEX_BEAM_OLD beam.
        bool oldBeam = Api.Services is not null && Api.Cvars.GetFloat("cl_particles_oldvortexbeam") != 0f;
        string beamEffect = oldBeam ? "VORTEX_BEAM_OLD" : "VORTEX_BEAM";

        // QC vortex.qc:63: (teamplay && cl_tracers_teamcolor == 1) || cl_tracers_teamcolor == 2. cl_tracers_teamcolor
        // is an unregistered client cvar (default 0 → no tint even in team games), so the team tint is opt-in.
        int tracers = Api.Services is null ? 0 : (int)Api.Cvars.GetFloat("cl_tracers_teamcolor");
        bool teamplay = Api.Services is not null && Api.Cvars.GetFloat("teamplay") != 0f;
        bool useColor = (teamplay && tracers == 1) || tracers == 2;

        if (useColor)
        {
            // QC vortex.qc:65-69: vortex_glowcolor(owner_colors, max(0.25, charge)); fall back to the plain player
            // colour if charging is off (rgb == 0). Entity.Team carries the NUM_TEAM_* palette code (== the low
            // colormap nibble for a teamplay player), so it stands in for entcs_GetClientColors(owner)&0x0F.
            Vector3 rgb = VortexGlowColor((int)actor.Team, MathF.Max(0.25f, beamCharge));
            if (rgb == Vector3.Zero)
                rgb = ColormapPaletteColor(((int)actor.Team) & 0x0F);
            var beam = Effects.ByName(beamEffect);
            EffectEmitter.Emit(beam, shotorg, endpos, 0, rgb, rgb, except: null);
            return;
        }

        EffectEmitter.Emit(beamEffect, shotorg, endpos, 0);
    }

    // vector vortex_glowcolor(int actor_colors, float charge) — vortex.qc:7-26. Builds the charge-blended player
    // glow colour: f = min(1, charge/animlimit) of 0.3*mycolors, plus (above animlimit) an extra
    // (charge-animlimit)/(1-animlimit) of 0.7*mycolors. mycolors = colormapPaletteColor(actor_colors & 0x0F, pants).
    // A zero result is nudged off pure black (the engine treats '0 0 0' as "use the model's own glow").
    private Vector3 VortexGlowColor(int actorColors, float charge)
    {
        if (!Cvars.Charge)
            return Vector3.Zero;

        float animlimit = Cvars.ChargeAnimLimit;
        Vector3 mycolors = ColormapPaletteColor(actorColors & 0x0F);

        float f = MathF.Min(1f, animlimit > 0f ? charge / animlimit : 1f);
        Vector3 g = f * (mycolors * 0.3f);
        if (charge > animlimit)
        {
            f = (charge - animlimit) / (1f - animlimit);
            g += f * (mycolors * 0.7f);
        }
        // transition color can't be '0 0 0' as it defaults to player model glow color (vortex.qc:22-23)
        if (g == Vector3.Zero)
            g = new Vector3(0f, 0f, 0.000001f);
        return g;
    }

    // colormapPaletteColor(c, isPants=true) over the team palette (lib/color.qh). Entity.Team only ever holds the
    // standard NUM_TEAM_* codes (4/13/12/9), so just the team colours are needed here; the shirt/pants phase is
    // irrelevant for the solid team codes. Matches CsqcModelAppearance.ColormapPaletteColor for these values.
    private static Vector3 ColormapPaletteColor(int c) => c switch
    {
        4  => new Vector3(1f, 0f, 0f),           // red    (NUM_TEAM_1)
        13 => new Vector3(0f, 0.333333f, 1f),    // blue   (NUM_TEAM_2)
        12 => new Vector3(1f, 1f, 0f),           // yellow (NUM_TEAM_3)
        9  => new Vector3(1f, 0f, 1f),           // pink/magenta (NUM_TEAM_4, palette 9)
        _  => Vector3.Zero,
    };

    // Per-actor `vortex_lasthit` (QC .float vortex_lasthit, vortex.qc:156-162). QC stores it on the player
    // edict; the port has no Vortex field on Entity (cross-file), so we track it in a weak side-table keyed
    // by actor — same observable "only every second consecutive hit" cadence, no Entity.cs edit.
    private static readonly ConditionalWeakTable<Entity, StrongBox<int>> _vortexLastHit = new();

    private static int GetLastHit(Entity actor) => _vortexLastHit.TryGetValue(actor, out var b) ? b.Value : 0;
    private static void SetLastHit(Entity actor, int v) => _vortexLastHit.GetOrCreateValue(actor).Value = v;

    // Yoda / Impressive — port of vortex.qc:141-162. `flying` is the shooter's airborne status captured before
    // the trace. `hit` is FireRailgunBullet's first pierced victim (the rail's primary target).
    private void Announce(Entity actor, Entity? hit, bool flying)
    {
        if ((actor.Flags & EntFlags.Client) == 0) return; // only real clients get announces

        // QC ++impressive_hits (damage.qc:646): a cross-team damaging hit on a hittable victim. The rail always
        // deals damage > 0, so any live, damageable, cross-team target counts.
        bool impressiveHit = hit is not null
            && hit.TakeDamage != DamageMode.No
            && hit.DeadState == DeadFlag.No
            && !ReferenceEquals(hit, actor)
            && !Teams.SameTeam(hit, actor);

        // QC yoda (damage.qc:648-651): the victim is a non-special-death PLAYER and IsFlying(victim); plus the
        // vortex block also gates on the SHOOTER flying (vortex.qc:154 `if (yoda && flying)`).
        if (impressiveHit && flying
            && (hit!.Flags & EntFlags.Client) != 0 && IsFlying(hit))
            NotificationSystem.Announce(actor, "ACHIEVEMENT_YODA");

        // QC vortex.qc:156-162: impressive fires only when THIS shot AND the previous one both landed a hit
        // (actor.vortex_lasthit), then resets so it's every-other. vortex_lasthit is then set to this shot's
        // hit state.
        int impressive = impressiveHit ? 1 : 0;
        if (impressive != 0 && GetLastHit(actor) != 0)
        {
            NotificationSystem.Announce(actor, "ACHIEVEMENT_IMPRESSIVE");
            impressive = 0; // only every second time
        }
        SetLastHit(actor, impressive);
    }

    // bool IsFlying(entity) — common/physics/player.qc, the airshot test: airborne, not swimming, and at least
    // 24u of clearance below (so a player skimming the ground doesn't count). Mirrors Devastator/Mortar.
    private static bool IsFlying(Entity e)
    {
        if (e.OnGround) return false;
        if (e.WaterLevel >= 2) return false; // WATERLEVEL_SWIMMING
        TraceResult tr = Api.Trace.Trace(e.Origin, e.Mins, e.Maxs,
            e.Origin - new Vector3(0f, 0f, 24f), MoveFilter.Normal, e);
        return tr.Fraction >= 1f;
    }

    // METHOD(Vortex, wr_setup / wr_resetplayer) — seed the per-slot charge + chargepool.
    // NOTE: QC seeds these in wr_resetplayer (on respawn, vortex.qc:299-311); the port has no respawn-reset
    // weapon hook, so the seed runs on switch-in (wr_setup) — a switch away+back also re-seeds (deviation,
    // pre-existing for the charge; the pool seed follows the same convention).
    public override void WrSetup(Entity actor, WeaponSlot slot)
    {
        SetLastHit(actor, 0); // QC wr_setup/wr_resetplayer: actor.vortex_lasthit = 0 (impressive streak reset)
        if (!Cvars.Charge) return;
        var st = actor.WeaponState(slot);
        st.VortexCharge = Cvars.ChargeStart;
        if (Cvars.SecondaryChargePool)
            st.VortexChargePoolAmmo = 1f;
    }

    // QC WEP_CVAR_BOTH refire/animtime: the real secondary fire mode has its own timing block; the
    // zoom-charge secondary never reaches PrepareAttack, so primary timing covers everything else.
    public override float RefireFor(FireMode fire)
        => fire == FireMode.Secondary && Cvars.Secondary ? Cvars.SecondaryRefire : Cvars.Refire;
    public override float AnimtimeFor(FireMode fire)
        => fire == FireMode.Secondary && Cvars.Secondary ? Cvars.SecondaryAnimtime : Cvars.Animtime;

    // METHOD(Vortex, wr_checkammo1) — vortex.qc:281-286 (resource OR the persistent clip when reloadable).
    public bool CheckAmmoPrimary(Entity actor)
        => actor.GetResource(AmmoType) >= Cvars.Ammo
        || (Cvars.ReloadAmmo > 0f
            && GetWeaponLoad(actor.WeaponState(new WeaponSlot(0)), RegistryId) >= Cvars.Ammo);

    // METHOD(Vortex, wr_checkammo2) — vortex.qc:287-298: with g_balance_vortex_secondary, don't allow
    // charging without enough ammo; otherwise "zoom is not a fire mode" (false).
    public bool CheckAmmoSecondary(Entity actor)
    {
        if (!Cvars.Secondary)
            return false;
        return actor.GetResource(AmmoType) >= Cvars.SecondaryAmmo
            || GetWeaponLoad(actor.WeaponState(new WeaponSlot(0)), RegistryId) >= Cvars.SecondaryAmmo;
    }

    /// <summary>QC <c>IT_UNLIMITED_AMMO</c> = BIT(0) (common/items/item.qh).</summary>
    private const int ItUnlimitedAmmoBit = 1 << 0;
}
