using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Physics;

/// <summary>
/// The movement tunables read from cvars once per tick, ported from the Xonotic <c>STAT(MOVEVARS_*)</c>
/// set that <c>Physics_UpdateStats</c> (qcsrc/common/physics/player.qc) fills from the <c>autocvar_sv_*</c>
/// movement variables. This is the C# equivalent of the per-player movevars block: the ported movement
/// functions read everything they need from one of these structs instead of from the global cvar table,
/// which keeps the hot path allocation-free and the math easy to unit-test.
///
/// Defaults below are the stock <b>Xonotic</b> physics set (physicsX.cfg, mirrored by the
/// <c>g_physics_xonotic_*</c> block of physics.cfg). <see cref="FromCvars"/> reads live values via
/// <see cref="Api.Cvars"/> and falls back to these when a cvar is unset (GetFloat returns 0).
/// </summary>
public struct MovementParameters
{
    // --- ground movement ---
    public float MaxSpeed;        // sv_maxspeed            (360)
    public float Accelerate;      // sv_accelerate          (15)
    public float Friction;        // sv_friction            (6)
    public float FrictionSlick;   // sv_friction_slick      (0.5)
    public float StopSpeed;       // sv_stopspeed           (100)
    public float SlickAccelerate; // sv_slickaccelerate     (15)
    public float FrictionOnLand;  // sv_friction_on_land    (0)  -> PHYS_FRICTION_ONLAND

    // --- air movement (Xonotic QW air-accel) ---
    public float MaxAirSpeed;            // sv_maxairspeed              (360)
    public float AirAccelerate;          // sv_airaccelerate            (2)
    public float AirAccelQW;             // sv_airaccel_qw              (-0.8)
    public float AirStrafeAccelQW;       // sv_airstrafeaccel_qw        (-0.95)
    public float AirAccelQWStretchFactor;// sv_airaccel_qw_stretchfactor(2)
    public float AirSpeedLimitNonQW;     // sv_airspeedlimit_nonqw      (900)
    public float AirAccelSidewaysFriction;// sv_airaccel_sideways_friction (0)
    public float MaxAirStrafeSpeed;      // sv_maxairstrafespeed        (100)
    public float AirStrafeAccelerate;    // sv_airstrafeaccelerate      (18)
    public float AirStopAccelerate;      // sv_airstopaccelerate        (3)
    public bool  AirStopAccelerateFull;  // sv_airstopaccelerate_full   (0)

    // --- CPM-style air control ---
    public float AirControl;        // sv_aircontrol         (100)
    public int   AirControlFlags;   // sv_aircontrol_flags   (0)
    public float AirControlPower;   // sv_aircontrol_power   (2)
    public float AirControlPenalty; // sv_aircontrol_penalty (0)

    // --- Warsow bunny (the alternative air-accel selected by warsowbunny_turnaccel != 0) ---
    public float WarsowBunnyTurnAccel;       // sv_warsowbunny_turnaccel        (0 -> disabled)
    public float WarsowBunnyAirForwardAccel; // sv_warsowbunny_airforwardaccel  (1.00001)
    public float WarsowBunnyTopSpeed;        // sv_warsowbunny_topspeed         (925)
    public float WarsowBunnyAccel;           // sv_warsowbunny_accel            (0.1593)
    public float WarsowBunnyBackToSideRatio; // sv_warsowbunny_backtosideratio  (0.8)

    // --- jumping ---
    public float JumpVelocity;       // sv_jumpvelocity        (260)
    public float JumpVelocityCrouch; // sv_jumpvelocity_crouch (0 -> use JumpVelocity)
    public float JumpSpeedCapMin;    // sv_jumpspeedcap_min    (NaN -> disabled)
    public float JumpSpeedCapMax;    // sv_jumpspeedcap_max    (NaN -> disabled)
    public bool  JumpSpeedCapMaxDisableOnRamps; // sv_jumpspeedcap_max_disable_on_ramps (1)
    public bool  TrackCanJump;       // sv_track_canjump       (0)
    public bool  DoubleJump;         // sv_doublejump          (0 -> no air re-jump in stock Xonotic)
    public bool  JumpStep;           // sv_jumpstep            (1 -> allow stepping while jumping)

    // --- gravity / integration ---
    public float Gravity;            // sv_gravity             (800)

    // --- stair stepping / hull ---
    public float StepHeight;         // sv_stepheight          (31 in Xonotic; spec/task note 34)
    public int   StepDown;           // sv_gameplayfix_stepdown(2)  0=off 1=on 2=on+set-onground
    public float StepDownMaxSpeed;   // sv_gameplayfix_stepdown_maxspeed (400)
    public int   WallFriction;       // sv_wallfriction        (1 in stock: DP engine default, no .cfg sets it; gate is live but the QC _Movetype_WallFriction body is commented out, so the term is a no-op — see PlayerPhysics.WallFriction)

    // --- the high-speed modifier + remaining MOVEVARS breadth (T54) ---
    /// <summary>QC <c>STAT(MOVEVARS_HIGHSPEED) = autocvar_g_movement_highspeed</c> (player.qc:47) — the BASE
    /// per-player top-speed multiplier the PlayerPhysics mutator hooks (Speed powerup, entrap nade, buffs)
    /// multiply onto. Unset reads 1 (xonotic-server.cfg:586 ships 1). PlayerPhysics seeds
    /// <c>player.SpeedMultiplier</c> from this each tick; the fold happens at <see cref="ApplyHighSpeed"/>.</summary>
    public float HighSpeed;          // g_movement_highspeed   (1)
    /// <summary>QC <c>autocvar_g_movement_highspeed_q3_compat</c> (player.qc:52-65, xonotic-server.cfg:587
    /// ships 0): selects WHICH stats the maxspd_mod fold scales — see <see cref="ApplyHighSpeed"/>.</summary>
    public bool  HighSpeedQ3Compat;  // g_movement_highspeed_q3_compat (0)
    /// <summary>DP <c>sv_gameplayfix_nudgeoutofsolid</c> (default ON) — replicated so a server that disables the
    /// embedded-in-solid recovery doesn't desync prediction. PlayerPhysics still reads the cvar ambiently
    /// (stamped by MoveVarsBlock.Apply on the client), so this field is informational.</summary>
    public bool  NudgeOutOfSolid;    // sv_gameplayfix_nudgeoutofsolid (1)
    /// <summary>QC <c>STAT(MOVEVARS_WALLCLIP)</c> (stats.qh:448, autocvar_sv_wallclip — no cfg sets it, 0).
    /// Carried for replication completeness; no consumer in the port yet (doc-only, like QC where only the
    /// movetypes wallclip experiment reads it).</summary>
    public int   WallClip;           // sv_wallclip            (0)
    /// <summary>QC <c>STAT(NOSTEP)</c> (stats.qh:235, cvar sv_nostep, default 0). Doc-only until the
    /// movetypes step path consults it.</summary>
    public bool  NoStep;             // sv_nostep              (0)
    /// <summary>QC <c>STAT(SLICK_APPLYGRAVITY)</c> (stats.qh:365, default 0). PlayerPhysics currently pins the
    /// stock value as a const (PlayerPhysics.SlickApplyGravity) — this field carries the replicated value for a
    /// future live consumer.</summary>
    public bool  SlickApplyGravity;  // sv_slick_applygravity  (0)

    /// <summary>Player bounding box mins. QC <c>autocvar_sv_player_mins</c> = '-16 -16 -24'.</summary>
    public Vector3 PlayerMins;
    /// <summary>Player bounding box maxs. QC <c>autocvar_sv_player_maxs</c> = '16 16 45'.</summary>
    public Vector3 PlayerMaxs;

    /// <summary>
    /// Build the parameter block from live cvars (<paramref name="prefix"/> defaults to <c>"sv_"</c>),
    /// substituting the stock Xonotic defaults whenever a cvar is unset. Mirrors the assignments in
    /// <c>Physics_UpdateStats</c> but without the per-client overrides / high-speed modifier, which the
    /// caller can apply on top (see <see cref="ApplyHighSpeed"/>).
    /// </summary>
    public static MovementParameters FromCvars(string prefix = "sv_")
    {
        MovementParameters p = Defaults;

        // Floats: read the cvar, keep the Xonotic default if the cvar is unset (GetFloat -> 0).
        // We treat 0 as "unset" only for variables whose Xonotic value is non-zero; the genuinely-zero
        // Xonotic defaults (e.g. aircontrol_penalty) are simply overwritten by GetFloat's 0 anyway.
        p.MaxSpeed                  = Cvar(prefix + "maxspeed",                 p.MaxSpeed);
        p.Accelerate                = Cvar(prefix + "accelerate",              p.Accelerate);
        p.Friction                  = Cvar(prefix + "friction",                p.Friction);
        p.FrictionSlick             = Cvar(prefix + "friction_slick",          p.FrictionSlick);
        p.StopSpeed                 = Cvar(prefix + "stopspeed",               p.StopSpeed);
        p.SlickAccelerate           = Cvar(prefix + "slickaccelerate",         p.SlickAccelerate);
        p.FrictionOnLand            = CvarRaw(prefix + "friction_on_land",      p.FrictionOnLand);

        p.MaxAirSpeed               = Cvar(prefix + "maxairspeed",             p.MaxAirSpeed);
        p.AirAccelerate             = Cvar(prefix + "airaccelerate",           p.AirAccelerate);
        p.AirAccelQW                = CvarRaw(prefix + "airaccel_qw",           p.AirAccelQW);
        p.AirStrafeAccelQW          = CvarRaw(prefix + "airstrafeaccel_qw",     p.AirStrafeAccelQW);
        p.AirAccelQWStretchFactor   = Cvar(prefix + "airaccel_qw_stretchfactor", p.AirAccelQWStretchFactor);
        p.AirSpeedLimitNonQW        = Cvar(prefix + "airspeedlimit_nonqw",     p.AirSpeedLimitNonQW);
        p.AirAccelSidewaysFriction  = CvarRaw(prefix + "airaccel_sideways_friction", p.AirAccelSidewaysFriction);
        p.MaxAirStrafeSpeed         = Cvar(prefix + "maxairstrafespeed",       p.MaxAirStrafeSpeed);
        p.AirStrafeAccelerate       = Cvar(prefix + "airstrafeaccelerate",     p.AirStrafeAccelerate);
        p.AirStopAccelerate         = Cvar(prefix + "airstopaccelerate",       p.AirStopAccelerate);
        p.AirStopAccelerateFull     = CvarBool(prefix + "airstopaccelerate_full", p.AirStopAccelerateFull);

        p.AirControl                = Cvar(prefix + "aircontrol",              p.AirControl);
        p.AirControlFlags           = (int)CvarRaw(prefix + "aircontrol_flags", p.AirControlFlags);
        p.AirControlPower           = Cvar(prefix + "aircontrol_power",        p.AirControlPower);
        p.AirControlPenalty         = CvarRaw(prefix + "aircontrol_penalty",    p.AirControlPenalty);

        p.WarsowBunnyTurnAccel      = CvarRaw(prefix + "warsowbunny_turnaccel", p.WarsowBunnyTurnAccel);
        p.WarsowBunnyAirForwardAccel= Cvar(prefix + "warsowbunny_airforwardaccel", p.WarsowBunnyAirForwardAccel);
        p.WarsowBunnyTopSpeed       = Cvar(prefix + "warsowbunny_topspeed",    p.WarsowBunnyTopSpeed);
        p.WarsowBunnyAccel          = Cvar(prefix + "warsowbunny_accel",       p.WarsowBunnyAccel);
        p.WarsowBunnyBackToSideRatio= Cvar(prefix + "warsowbunny_backtosideratio", p.WarsowBunnyBackToSideRatio);

        p.JumpVelocity              = Cvar(prefix + "jumpvelocity",            p.JumpVelocity);
        p.JumpVelocityCrouch        = CvarRaw(prefix + "jumpvelocity_crouch",   p.JumpVelocityCrouch);
        p.JumpSpeedCapMin           = CvarNan(prefix + "jumpspeedcap_min",      p.JumpSpeedCapMin);
        p.JumpSpeedCapMax           = CvarNan(prefix + "jumpspeedcap_max",      p.JumpSpeedCapMax);
        p.JumpSpeedCapMaxDisableOnRamps = CvarBool(prefix + "jumpspeedcap_max_disable_on_ramps", p.JumpSpeedCapMaxDisableOnRamps);
        p.TrackCanJump              = CvarBool(prefix + "track_canjump",        p.TrackCanJump);
        p.DoubleJump                = CvarBool(prefix + "doublejump",           p.DoubleJump);
        p.JumpStep                  = CvarBool(prefix + "jumpstep",             p.JumpStep);

        p.Gravity                   = Cvar(prefix + "gravity",                 p.Gravity);

        p.StepHeight                = Cvar(prefix + "stepheight",              p.StepHeight);
        p.StepDown                  = (int)CvarRaw(prefix + "gameplayfix_stepdown", p.StepDown);
        p.StepDownMaxSpeed          = Cvar(prefix + "gameplayfix_stepdown_maxspeed", p.StepDownMaxSpeed);
        p.WallFriction              = (int)CvarRaw(prefix + "wallfriction",     p.WallFriction);

        // T54 breadth. g_movement_highspeed: unset must read 1, and a deliberate 0 must be honored (it would
        // freeze movement, but that's what the cvar says) — so gate on the STRING being present, not the float.
        p.HighSpeed                 = Api.Cvars.GetString("g_movement_highspeed").Length == 0
                                        ? p.HighSpeed
                                        : Api.Cvars.GetFloat("g_movement_highspeed");
        p.HighSpeedQ3Compat         = CvarBool("g_movement_highspeed_q3_compat", p.HighSpeedQ3Compat);
        p.NudgeOutOfSolid           = Api.Cvars.GetString(prefix + "gameplayfix_nudgeoutofsolid") != "0"; // DP default ON
        p.WallClip                  = (int)CvarRaw(prefix + "wallclip",          p.WallClip);
        p.NoStep                    = CvarBool(prefix + "nostep",                p.NoStep);
        p.SlickApplyGravity         = CvarBool(prefix + "slick_applygravity",    p.SlickApplyGravity);

        // Hull: QC reads these from autocvar_sv_player_mins/maxs (vector cvars). We don't have a vector
        // cvar accessor on the facade, so the Xonotic hull is taken from Defaults. A host that wants to
        // override the hull can set the fields after FromCvars().
        // (PlayerMins/PlayerMaxs already carry the Xonotic defaults via `Defaults`.)

        return p;
    }

    /// <summary>
    /// The stock Xonotic movement set (physicsX.cfg / g_physics_xonotic_*). Also serves as the fallback
    /// table for <see cref="FromCvars"/>.
    /// </summary>
    public static readonly MovementParameters Defaults = new()
    {
        MaxSpeed = 360f,
        Accelerate = 15f,
        Friction = 6f,
        FrictionSlick = 0.5f,
        StopSpeed = 100f,
        SlickAccelerate = 15f,
        FrictionOnLand = 0f,

        MaxAirSpeed = 360f,
        AirAccelerate = 2f,
        AirAccelQW = -0.8f,
        AirStrafeAccelQW = -0.95f,
        AirAccelQWStretchFactor = 2f,
        AirSpeedLimitNonQW = 900f,
        AirAccelSidewaysFriction = 0f,
        MaxAirStrafeSpeed = 100f,
        AirStrafeAccelerate = 18f,
        AirStopAccelerate = 3f,
        AirStopAccelerateFull = false,

        AirControl = 100f,
        AirControlFlags = 0,
        AirControlPower = 2f,
        AirControlPenalty = 0f,

        WarsowBunnyTurnAccel = 0f,
        WarsowBunnyAirForwardAccel = 1.00001f,
        WarsowBunnyTopSpeed = 925f,
        WarsowBunnyAccel = 0.1593f,
        WarsowBunnyBackToSideRatio = 0.8f,

        JumpVelocity = 260f,
        JumpVelocityCrouch = 0f,
        JumpSpeedCapMin = float.NaN,
        JumpSpeedCapMax = float.NaN,
        JumpSpeedCapMaxDisableOnRamps = true,
        TrackCanJump = false,
        DoubleJump = false,  // sv_doublejump 0 — no air re-jump in stock Xonotic
        JumpStep = true,     // sv_jumpstep 1 — allow the up/forward step while airborne

        Gravity = 800f,

        StepHeight = 31f, // Xonotic physicsX.cfg value; task brief mentions 34 (the Nexuiz/Vecxis value).
        StepDown = 2,
        StepDownMaxSpeed = 400f,
        WallFriction = 1, // sv_wallfriction 1 — Darkplaces engine default (QC autocvar_sv_wallfriction has no
                          // initializer and no Xonotic .cfg sets it; a live engine dump confirms 1). The net effect
                          // is still NO wall friction: the stock QC _Movetype_WallFriction body is commented out, and
                          // PlayerPhysics.WallFriction mirrors that no-op — so the corpus stays byte-identical.

        HighSpeed = 1f,            // g_movement_highspeed 1 (xonotic-server.cfg:586)
        HighSpeedQ3Compat = false, // g_movement_highspeed_q3_compat 0 (:587)
        NudgeOutOfSolid = true,    // sv_gameplayfix_nudgeoutofsolid — DP default ON
        WallClip = 0,              // sv_wallclip — no cfg sets it
        NoStep = false,            // sv_nostep 0
        SlickApplyGravity = false, // sv_slick_applygravity 0

        PlayerMins = new Vector3(-16f, -16f, -24f),
        PlayerMaxs = new Vector3(16f, 16f, 45f),
    };

    /// <summary>
    /// Apply the <c>maxspd_mod</c> fold the way <c>Physics_UpdateStats</c> does (player.qc:50-65,116-119):
    /// MaxSpeed always scales ("also slow walking", :51); then — when NOT q3-compat (the stock path, :58-64) —
    /// AirSpeedLimitNonQW scales and the two QW accel fractions are re-stretched via
    /// <see cref="PMAccelerate.AdjustAirAccelQW"/> (AirStrafeAccelQW only when non-zero, the QC ternary :61-63);
    /// when q3-compat (:54-57,:116-117) the QW vars stay RAW and MaxAirSpeed scales instead. NOTE
    /// MaxAirStrafeSpeed is scaled in NEITHER branch (player.qc:115 has no ×mod — an earlier port revision
    /// wrongly scaled it here). Call after <see cref="FromCvars"/>/<see cref="FromValues"/> when the
    /// effective multiplier (HighSpeed × mutator SpeedMultiplier) differs from 1.
    /// </summary>
    public void ApplyHighSpeed(float maxspeedMod)
    {
        if (maxspeedMod == 1f) return;
        MaxSpeed *= maxspeedMod;
        if (HighSpeedQ3Compat)
        {
            MaxAirSpeed *= maxspeedMod;     // player.qc:117 — q3compat scales maxairspeed, leaves the QW vars raw
        }
        else
        {
            AirSpeedLimitNonQW *= maxspeedMod;
            AirAccelQW = PMAccelerate.AdjustAirAccelQW(AirAccelQW, maxspeedMod);
            if (AirStrafeAccelQW != 0f)
                AirStrafeAccelQW = PMAccelerate.AdjustAirAccelQW(AirStrafeAccelQW, maxspeedMod);
        }
    }

    // =================================================================================================
    //  Wire-vector decode + the per-player resolution seams (T54)
    // =================================================================================================

    // CLR gotcha: a struct may not declare a static FIELD of Nullable<itself> (generic layout cycle →
    // TypeLoadException at runtime), so the override storage lives in a nested class behind properties.
    private static class OverrideStore
    {
        internal static MovementParameters? Prediction;
        internal static System.Func<Entity, float[]?>? Provider;
    }

    /// <summary>
    /// CLIENT-side per-player physics override: when the server replicates a preset-RESOLVED movevar vector
    /// (g_physics_clientselect — the per-peer block after the global MoveVarsBlock in the snapshot), the net
    /// layer parks <c>FromValues(resolved)</c> here and the PREDICTED move (<c>input.Predicted</c>) reads it
    /// instead of the cvar store. Null (the default) = stock behavior, byte-identical to <see cref="FromCvars"/>.
    /// Cleared on disconnect (ClientNet.Dispose) — reset in tests that set it.
    /// </summary>
    public static MovementParameters? PredictionOverride
    {
        get => OverrideStore.Prediction;
        set => OverrideStore.Prediction = value;
    }

    /// <summary>
    /// SERVER-side per-player physics resolver: returns the entity's preset-resolved movevar vector (in
    /// MoveVarsBlock order) or null for "no per-player physics" (the stock path). Installed by ServerNet when
    /// hosting; null (the default) = every player moves on the global cvars. Cleared in ServerNet.Dispose.
    /// </summary>
    public static System.Func<Entity, float[]?>? PresetProvider
    {
        get => OverrideStore.Provider;
        set => OverrideStore.Provider = value;
    }

    /// <summary>
    /// The per-tick parameter read for <see cref="PlayerPhysics.Move"/> — QC's per-player stat read. The
    /// PREDICTED leg (client prediction replays) takes <see cref="PredictionOverride"/> when set; the
    /// authoritative leg consults <see cref="PresetProvider"/>; both default to the ambient
    /// <see cref="FromCvars"/> — the pre-T54 path, so a host with clientselect off is byte-identical.
    /// </summary>
    public static MovementParameters Resolve(Entity player, bool predicted)
    {
        if (predicted)
            return PredictionOverride ?? FromCvars();
        return PresetProvider?.Invoke(player) is { } v ? FromValues(v) : FromCvars();
    }

    /// <summary>
    /// Build the parameter block positionally from a replicated <c>MoveVarsBlock</c> vector (the wire twin of
    /// <see cref="FromCvars"/> — keep the assignment order in lockstep with <c>MoveVarsBlock.MovementCvars</c>).
    /// Same unset-fallback semantics as the cvar reads (a 0 where the stock default is non-zero means "unset"),
    /// EXCEPT the jumpspeedcaps: the wire already encodes "disabled" as NaN (MoveVarsBlock.CaptureOne), so a
    /// real 0 (the xdf/quake3/cpma presets) passes through as a genuine 0 cap. A short vector (an older peer)
    /// leaves the tail at <see cref="Defaults"/>.
    /// </summary>
    public static MovementParameters FromValues(float[] v)
    {
        MovementParameters p = Defaults;
        int i = 0;
        float F(float fallback) { float x = i < v.Length ? v[i] : fallback; i++; return x != 0f ? x : fallback; }
        float Raw(float fallback) { float x = i < v.Length ? v[i] : fallback; i++; return (x == 0f && fallback != 0f) ? fallback : x; }
        bool B(bool fallback) { float x = i < v.Length ? v[i] : (fallback ? 1f : 0f); i++; return x != 0f || fallback; }
        float Nan() { float x = i < v.Length ? v[i] : float.NaN; i++; return x; } // NaN sentinel already on the wire

        p.MaxSpeed = F(p.MaxSpeed); p.Accelerate = F(p.Accelerate); p.Friction = F(p.Friction);
        p.FrictionSlick = F(p.FrictionSlick); p.StopSpeed = F(p.StopSpeed);
        p.SlickAccelerate = F(p.SlickAccelerate); p.FrictionOnLand = Raw(p.FrictionOnLand);

        p.MaxAirSpeed = F(p.MaxAirSpeed); p.AirAccelerate = F(p.AirAccelerate);
        p.AirAccelQW = Raw(p.AirAccelQW); p.AirStrafeAccelQW = Raw(p.AirStrafeAccelQW);
        p.AirAccelQWStretchFactor = F(p.AirAccelQWStretchFactor); p.AirSpeedLimitNonQW = F(p.AirSpeedLimitNonQW);
        p.AirAccelSidewaysFriction = Raw(p.AirAccelSidewaysFriction);
        p.MaxAirStrafeSpeed = F(p.MaxAirStrafeSpeed); p.AirStrafeAccelerate = F(p.AirStrafeAccelerate);
        p.AirStopAccelerate = F(p.AirStopAccelerate); p.AirStopAccelerateFull = B(p.AirStopAccelerateFull);

        p.AirControl = F(p.AirControl); p.AirControlFlags = (int)Raw(p.AirControlFlags);
        p.AirControlPower = F(p.AirControlPower); p.AirControlPenalty = Raw(p.AirControlPenalty);

        p.WarsowBunnyTurnAccel = Raw(p.WarsowBunnyTurnAccel);
        p.WarsowBunnyAirForwardAccel = F(p.WarsowBunnyAirForwardAccel);
        p.WarsowBunnyTopSpeed = F(p.WarsowBunnyTopSpeed); p.WarsowBunnyAccel = F(p.WarsowBunnyAccel);
        p.WarsowBunnyBackToSideRatio = F(p.WarsowBunnyBackToSideRatio);

        p.JumpVelocity = F(p.JumpVelocity); p.JumpVelocityCrouch = Raw(p.JumpVelocityCrouch);
        p.JumpSpeedCapMin = Nan(); p.JumpSpeedCapMax = Nan();
        p.JumpSpeedCapMaxDisableOnRamps = B(p.JumpSpeedCapMaxDisableOnRamps);
        p.TrackCanJump = B(p.TrackCanJump); p.DoubleJump = B(p.DoubleJump); p.JumpStep = B(p.JumpStep);

        p.Gravity = F(p.Gravity);

        p.StepHeight = F(p.StepHeight); p.StepDown = (int)Raw(p.StepDown);
        p.StepDownMaxSpeed = F(p.StepDownMaxSpeed); p.WallFriction = (int)Raw(p.WallFriction);

        // v7 tail: HighSpeed rides the wire with unset→1 already applied at Capture, so honor a genuine 0.
        p.HighSpeed = i < v.Length ? v[i] : p.HighSpeed; i++;
        p.HighSpeedQ3Compat = B(p.HighSpeedQ3Compat);
        p.NudgeOutOfSolid = i < v.Length ? v[i] != 0f : p.NudgeOutOfSolid; i++; // Capture sends unset→1
        p.WallClip = (int)Raw(p.WallClip);
        p.NoStep = B(p.NoStep);
        p.SlickApplyGravity = B(p.SlickApplyGravity);

        return p;
    }

    // --- cvar read helpers -------------------------------------------------------------------------

    // For "magnitude" cvars whose Xonotic default is non-zero: treat a 0 read (cvar absent) as "use default".
    private static float Cvar(string name, float fallback)
    {
        float v = Api.Cvars.GetFloat(name);
        return v != 0f ? v : fallback;
    }

    // For cvars that can legitimately be 0 or negative (qw fractions, penalties, flags, on_land):
    // we cannot distinguish "unset" from "0", so the host is expected to register these. If truly
    // unset GetFloat returns 0, which matches several Xonotic defaults anyway.
    private static float CvarRaw(string name, float fallback)
    {
        float v = Api.Cvars.GetFloat(name);
        // Only keep the fallback when the value is exactly 0 AND the fallback is non-zero (cvar likely
        // missing); otherwise honor the live value (including a deliberate 0).
        return (v == 0f && fallback != 0f) ? fallback : v;
    }

    private static bool CvarBool(string name, bool fallback)
    {
        float v = Api.Cvars.GetFloat(name);
        // GetFloat of an unset cvar is 0; can't distinguish from a real "0", so default to fallback at 0.
        return v != 0f || fallback;
    }

    // jumpspeedcap_min/max default to "nan" in Xonotic; a missing cvar reads 0 which would wrongly enable
    // the cap, so we keep NaN unless the host registered a finite value.
    private static float CvarNan(string name, float fallback)
    {
        float v = Api.Cvars.GetFloat(name);
        return v != 0f ? v : fallback;
    }
}
