using XonoticGodot.Common.Services;

namespace XonoticGodot.Net;

/// <summary>
/// Server→client replication of the movement cvars (DP's <c>movevars_*</c> / Xonotic's <c>sv_*</c> physics
/// set). The client predicts with the SAME deterministic movement sim as the server
/// (<c>MovementParameters.FromCvars</c>), so its prediction only matches authority if it reads the same
/// <c>sv_*</c> values. Stock defaults already agree, but a mid-match physics/mutator change (a different
/// <c>physics*.cfg</c>, <c>g_movement_highspeed</c>, an XPM ruleset vote) would silently desync prediction —
/// this block carries the server's live values so the client stamps them into its own cvar store.
///
/// Sent owner-only and <strong>only when changed</strong> (gated by a content <see cref="Hash"/>), so the
/// steady-state cost is a single bool per snapshot. The value list is exactly the cvar set
/// <c>MovementParameters.FromCvars</c> consumes (keep <see cref="MovementCvars"/> in sync with it), serialized
/// positionally — no names on the wire.
/// </summary>
public static class MoveVarsBlock
{
    /// <summary>
    /// The <c>sv_*</c> (and a couple of <c>g_*</c>) movement cvars the deterministic predictor reads, in wire
    /// order. MUST stay in sync with <c>XonoticGodot.Common.Physics.MovementParameters.FromCvars</c> AND with
    /// <c>MovementParameters.FromValues</c> (which decodes this vector positionally). Sent positionally as
    /// floats (covers ints/bools, and NaN for the uncapped jumpspeedcaps). APPEND-ONLY — never reorder
    /// (Apply/FromValues are prefix-stable across versions).
    /// </summary>
    public static readonly string[] MovementCvars =
    {
        "sv_maxspeed", "sv_accelerate", "sv_friction", "sv_friction_slick", "sv_stopspeed",
        "sv_slickaccelerate", "sv_friction_on_land",
        "sv_maxairspeed", "sv_airaccelerate", "sv_airaccel_qw", "sv_airstrafeaccel_qw",
        "sv_airaccel_qw_stretchfactor", "sv_airspeedlimit_nonqw", "sv_airaccel_sideways_friction",
        "sv_maxairstrafespeed", "sv_airstrafeaccelerate", "sv_airstopaccelerate", "sv_airstopaccelerate_full",
        "sv_aircontrol", "sv_aircontrol_flags", "sv_aircontrol_power", "sv_aircontrol_penalty",
        "sv_warsowbunny_turnaccel", "sv_warsowbunny_airforwardaccel", "sv_warsowbunny_topspeed",
        "sv_warsowbunny_accel", "sv_warsowbunny_backtosideratio",
        "sv_jumpvelocity", "sv_jumpvelocity_crouch", "sv_jumpspeedcap_min", "sv_jumpspeedcap_max",
        "sv_jumpspeedcap_max_disable_on_ramps", "sv_track_canjump", "sv_doublejump", "sv_jumpstep",
        "sv_gravity", "sv_stepheight", "sv_gameplayfix_stepdown", "sv_gameplayfix_stepdown_maxspeed",
        "sv_wallfriction",
        // ---- v7 additions (T54): the rest of the stats.qh MOVEVARS breadth the shared sim consults. ----
        "g_movement_highspeed",            // STAT(MOVEVARS_HIGHSPEED), player.qc:47 — unset reads 1 (see Capture)
        "g_movement_highspeed_q3_compat",  // the q3-compat maxspd_mod fold selector (player.qc:52-65), stock 0
        "sv_gameplayfix_nudgeoutofsolid",  // read ambiently by PlayerPhysics.NudgeOutOfSolid — unset reads 1 (DP default ON)
        "sv_wallclip",                     // STAT(MOVEVARS_WALLCLIP) (stats.qh:448) — no cfg sets it, default 0
        "sv_nostep",                       // STAT(NOSTEP) (stats.qh:235), default 0
        "sv_slick_applygravity",           // STAT(SLICK_APPLYGRAVITY) (stats.qh:365), default 0
        // Deliberately NOT replicated: MOVEVARS_TICRATE/TIMESCALE (the port advertises the tick rate at
        // handshake and ships dt per InputCommand), MOVEVARS_ENTGRAVITY (per-entity, not a cvar),
        // MOVEVARS_CL_TRACK_CANJUMP (a client→server cvar loopback — rides the sentcvar channel instead).
    };

    /// <summary>The number of replicated movement cvars.</summary>
    public static int Count => MovementCvars.Length;

    /// <summary>Read the live movement-cvar values from the server's store, in <see cref="MovementCvars"/> order.</summary>
    public static float[] Capture(ICvarService cvars)
    {
        var v = new float[MovementCvars.Length];
        for (int i = 0; i < v.Length; i++)
            v[i] = CaptureOne(cvars, MovementCvars[i]);
        return v;
    }

    /// <summary>
    /// Read one movement cvar with the wire's value semantics. Three names need more than a bare GetFloat:
    /// <list type="bullet">
    ///   <item><c>g_movement_highspeed</c> / <c>sv_gameplayfix_nudgeoutofsolid</c>: their engine defaults are 1
    ///         (xonotic-server.cfg:586 / DP sv_gameplayfix default), but an UNSET cvar reads 0 via GetFloat —
    ///         so an empty store must capture 1, not silently turn the feature off for remote clients.</item>
    ///   <item><c>sv_jumpspeedcap_min/_max</c>: the stock value is the literal string <c>"nan"</c> (= cap
    ///         disabled). The wire is float, so NaN IS the disabled sentinel — map "nan"/unset → NaN and keep a
    ///         REAL 0 (the xdf/quake3/cpma presets) as 0. NaN survives serialization.</item>
    /// </list>
    /// </summary>
    public static float CaptureOne(ICvarService cvars, string name)
    {
        switch (name)
        {
            case "g_movement_highspeed":
            case "sv_gameplayfix_nudgeoutofsolid":
            {
                string s = cvars.GetString(name);
                return s.Length == 0 ? 1f : cvars.GetFloat(name);
            }
            case "sv_jumpspeedcap_min":
            case "sv_jumpspeedcap_max":
                return ParseNanFloat(cvars.GetString(name));
            default:
                return cvars.GetFloat(name);
        }
    }

    /// <summary>DP <c>atof("nan")</c> semantics for the jumpspeedcap cvars: ""/unset or "nan" → NaN (cap
    /// disabled), anything else parses as a float (a real 0 stays 0 — the xdf/quake3/cpma presets).</summary>
    public static float ParseNanFloat(string s)
    {
        if (s.Length == 0 || s.Equals("nan", System.StringComparison.OrdinalIgnoreCase))
            return float.NaN;
        return float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : float.NaN;
    }

    /// <summary>
    /// Build a client's preset-RESOLVED movevar vector — the per-player half of QC
    /// <c>Physics_UpdateStats</c> (common/physics/player.qc:44-151): every preset-resolvable entry goes through
    /// <c>Physics_ClientOption</c> (<see cref="XonoticGodot.Common.Physics.PhysicsPreset.Resolve"/>) with the
    /// already-captured global value as the <c>autocvar_sv_*</c> fallback; global-only entries (the stats.qh
    /// names WITH a cvar expression — jumpstep, friction_on_land, wallfriction, stepdown_maxspeed, …) are copied
    /// from <paramref name="globalValues"/> verbatim. The maxspd_mod fold is NOT applied here — both sides fold
    /// it at move time via <c>MovementParameters.ApplyHighSpeed</c> (the replicated <c>g_movement_highspeed</c>
    /// entry), so folding it into the vector would double-apply.
    /// </summary>
    /// <param name="cvars">The server's cvar store (holds the <c>g_physics_&lt;set&gt;_*</c> table).</param>
    /// <param name="clPhysics">The client's replicated <c>cl_physics</c> ("" for a bot / not sent).</param>
    /// <param name="globalValues">This tick's <see cref="Capture"/> result (the sv_* fallbacks).</param>
    /// <param name="exists">CVAR_TYPEFLAG_EXISTS check (pass <c>CvarService.Has</c>); null → GetString != "".</param>
    public static float[] CaptureResolved(ICvarService cvars, string clPhysics, float[] globalValues,
        System.Func<string, bool>? exists = null)
    {
        var v = new float[MovementCvars.Length];
        int n = System.Math.Min(globalValues.Length, v.Length);
        for (int i = 0; i < n; i++)
        {
            string? option = XonoticGodot.Common.Physics.PhysicsPreset.OptionFor(MovementCvars[i]);
            v[i] = option is null
                ? globalValues[i]
                : XonoticGodot.Common.Physics.PhysicsPreset.Resolve(cvars, clPhysics, option, globalValues[i], exists);
        }
        return v;
    }

    /// <summary>Stamp received values back into the client's cvar store so <c>FromCvars</c> reads the server's physics.</summary>
    public static void Apply(ICvarService cvars, float[] values)
    {
        int n = System.Math.Min(values.Length, MovementCvars.Length);
        for (int i = 0; i < n; i++)
            cvars.Set(MovementCvars[i], values[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Write the positional float block.</summary>
    public static void Serialize(BitWriter w, float[] values)
    {
        w.WriteByte(values.Length);
        for (int i = 0; i < values.Length; i++)
            w.WriteFloat(values[i]);
    }

    /// <summary>Read the positional float block.</summary>
    public static float[] Deserialize(ref BitReader r)
    {
        int n = r.ReadByte();
        if (n < 0 || n > 255) return System.Array.Empty<float>();
        var v = new float[n];
        for (int i = 0; i < n; i++)
            v[i] = r.ReadFloat();
        return v;
    }

    /// <summary>A content hash of the values for cheap "did the physics change?" change-detection (FNV-1a over the bits).</summary>
    public static uint Hash(float[] values)
    {
        uint h = 2166136261u;
        for (int i = 0; i < values.Length; i++)
        {
            uint bits = (uint)System.BitConverter.SingleToInt32Bits(values[i]);
            for (int b = 0; b < 4; b++) { h ^= bits & 0xFF; h *= 16777619u; bits >>= 8; }
        }
        return h;
    }
}
