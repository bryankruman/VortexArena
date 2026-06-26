using System.Numerics;

namespace XonoticGodot.Common.Math;

/// <summary>
/// Deterministic pseudo-random generator (QC prandom/psrandom, lib/random.qc). The server seeds it and
/// broadcasts the seed so the predicting client reproduces the same spread/effects (ADR-0010). NOT
/// <c>System.Random</c> (whose algorithm isn't guaranteed stable). PCG-XSH-RR style.
/// </summary>
public static class Prandom
{
    private static ulong _state = 0x853c49e6748fea9bUL;
    private const ulong Inc = 0xda3e39cb94b95bdbUL;

    public static void Seed(uint seed)
    {
        _state = 0UL;
        Next();
        _state += seed;
        Next();
    }

    private static uint Next()
    {
        ulong old = _state;
        _state = old * 6364136223846793005UL + Inc;
        uint xorshifted = (uint)(((old >> 18) ^ old) >> 27);
        int rot = (int)(old >> 59);
        return (xorshifted >> rot) | (xorshifted << ((-rot) & 31));
    }

    /// <summary>Uniform float in [0,1) (QC random()).</summary>
    public static float Float() => (Next() >> 8) * (1.0f / 16777216.0f);

    /// <summary>Uniform float in [-1,1).</summary>
    public static float Signed() => Float() * 2f - 1f;

    private static float _gaussLast;
    private static bool _gaussLastSet;

    /// <summary>
    /// A Gaussian (normal) random variate with mean 0 and standard deviation 1 (QC
    /// <c>gsl_ran_ugaussian</c>, lib/random.qc:87) — the Box-Muller transform, caching the second variate
    /// of each pair exactly as the GSL/QC code does. Uses the deterministic <see cref="Float"/> stream so
    /// server and predicting client agree (ADR-0010). Used by the gauss spread styles (W_CalculateSpread 3/4).
    /// </summary>
    public static float Gaussian()
    {
        if (_gaussLastSet)
        {
            _gaussLastSet = false;
            return _gaussLast;
        }

        float a = Float() * (2f * QMath.Pi);
        float b = MathF.Sqrt(-2f * MathF.Log(Float()));
        _gaussLast = MathF.Cos(a) * b;
        _gaussLastSet = true;
        return MathF.Sin(a) * b;
    }

    public static float Range(float min, float max) => min + (max - min) * Float();

    public static int RangeInt(int minInclusive, int maxExclusive)
        => maxExclusive <= minInclusive ? minInclusive : minInclusive + (int)(Next() % (uint)(maxExclusive - minInclusive));

    /// <summary>
    /// A random vector uniformly distributed inside the UNIT BALL (|v| &lt;= 1), matching the DarkPlaces
    /// engine <c>randomvec()</c> builtin (#91) and the QC <c>prandomvec</c> fallback (lib/random.qc:120,
    /// <c>while (v*v &gt; 1)</c>): rejection-sample a point in the cube [-1,1)^3, retry while it lies
    /// outside the unit ball. This is the distribution every QC <c>randomvec()</c> caller actually sees
    /// (weapon spread density <c>sqrt(1-r^2)</c>, loot/buff/pinata scatter, etc.) — NOT a raw cube, which
    /// would bias toward the 8 corner diagonals and overshoot |v| up to ~1.73. A degenerate
    /// <c>(0,0,-1)</c> fallback after too many rejects mirrors the engine builtin and bounds the loop.
    /// </summary>
    public static Vector3 Vec()
    {
        for (int tries = 0; tries < 16; tries++)
        {
            float x = Signed(), y = Signed(), z = Signed();
            if (x * x + y * y + z * z <= 1f)
                return new Vector3(x, y, z);
        }
        return new Vector3(0f, 0f, -1f);
    }

    /// <summary>A spread offset cone around a forward direction (QC W_CalculateSpread core).</summary>
    public static Vector3 Spread(Vector3 forward, Vector3 right, Vector3 up, float spread)
    {
        // uniform-in-disc spread, scaled, projected onto the right/up plane
        float r = spread * MathF.Sqrt(Float());
        float a = Float() * 2f * QMath.Pi;
        return QMath.Normalize(forward + right * (r * MathF.Cos(a)) + up * (r * MathF.Sin(a)));
    }
}
