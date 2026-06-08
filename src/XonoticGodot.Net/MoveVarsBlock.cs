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
    /// order. MUST stay in sync with <c>XonoticGodot.Common.Physics.MovementParameters.FromCvars</c>. Sent
    /// positionally as floats (covers ints/bools, and NaN for the uncapped jumpspeedcaps).
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
    };

    /// <summary>The number of replicated movement cvars.</summary>
    public static int Count => MovementCvars.Length;

    /// <summary>Read the live movement-cvar values from the server's store, in <see cref="MovementCvars"/> order.</summary>
    public static float[] Capture(ICvarService cvars)
    {
        var v = new float[MovementCvars.Length];
        for (int i = 0; i < v.Length; i++)
            v[i] = cvars.GetFloat(MovementCvars[i]);
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
