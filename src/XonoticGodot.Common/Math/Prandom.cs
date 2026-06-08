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

    public static float Range(float min, float max) => min + (max - min) * Float();

    public static int RangeInt(int minInclusive, int maxExclusive)
        => maxExclusive <= minInclusive ? minInclusive : minInclusive + (int)(Next() % (uint)(maxExclusive - minInclusive));

    /// <summary>A random vector with components in [-1,1) (QC randomvec()).</summary>
    public static Vector3 Vec() => new(Signed(), Signed(), Signed());

    /// <summary>A spread offset cone around a forward direction (QC W_CalculateSpread core).</summary>
    public static Vector3 Spread(Vector3 forward, Vector3 right, Vector3 up, float spread)
    {
        // uniform-in-disc spread, scaled, projected onto the right/up plane
        float r = spread * MathF.Sqrt(Float());
        float a = Float() * 2f * QMath.Pi;
        return QMath.Normalize(forward + right * (r * MathF.Cos(a)) + up * (r * MathF.Sin(a)));
    }
}
