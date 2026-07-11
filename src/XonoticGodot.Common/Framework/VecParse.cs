using System;
using System.Globalization;

namespace XonoticGodot.Common.Framework;

/// <summary>
/// The canonical "parse a float vector from a string" helper. The codebase had accumulated ~9 private
/// copies (MapLoader, NetGame, ViewModel, HudPanel, ViewEffects, …), each with subtly different separator
/// and error semantics — new code should call this instead of adding copy N+1; existing copies migrate
/// opportunistically when their files are next touched.
///
/// <para>Semantics: invariant culture; space, comma, and tab separators (any mix); ALL tokens must parse
/// and at least <paramref name="min"/> values must be present, else the whole parse fails (no zero-fill,
/// no partial results — a malformed config value should be loud at the call site, not silently munged).</para>
/// </summary>
public static class VecParse
{
    private static readonly char[] Separators = { ' ', ',', '\t' };

    /// <summary>
    /// Parse <paramref name="s"/> as a list of floats. Returns false (and <paramref name="vals"/> = empty)
    /// when the string is null/empty, any token fails to parse, or fewer than <paramref name="min"/> values
    /// are present. On success <paramref name="vals"/> holds every parsed value (callers may accept more
    /// than <paramref name="min"/> — e.g. an optional yaw/pitch tail after an x y z position).
    /// </summary>
    public static bool TryParseFloats(string? s, int min, out float[] vals)
    {
        vals = Array.Empty<float>();
        if (string.IsNullOrWhiteSpace(s))
            return false;
        string[] parts = s.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < min)
            return false;
        var parsed = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[i]))
                return false;
        }
        vals = parsed;
        return true;
    }
}
