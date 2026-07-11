using XonoticGodot.Common.Framework;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>The canonical float-vector string parser (VecParse) — the shared replacement for the ~9 private
/// per-file copies. Pins the semantics new call sites rely on: invariant culture, mixed space/comma/tab
/// separators, all-or-nothing parsing, and the min-arity gate.</summary>
public class VecParseTests
{
    [Theory]
    [InlineData("1 2 3", 3, new[] { 1f, 2f, 3f })]
    [InlineData("1,2,3", 3, new[] { 1f, 2f, 3f })]
    [InlineData(" 456 1288 220  45 10 ", 3, new[] { 456f, 1288f, 220f, 45f, 10f })] // optional tail kept
    [InlineData("-1 -1 -1", 3, new[] { -1f, -1f, -1f })]
    [InlineData("0.5,\t2", 2, new[] { 0.5f, 2f })]
    public void Parses_Valid_Vectors(string s, int min, float[] expected)
    {
        Assert.True(VecParse.TryParseFloats(s, min, out float[] vals));
        Assert.Equal(expected, vals);
    }

    [Theory]
    [InlineData(null, 3)]
    [InlineData("", 3)]
    [InlineData("   ", 3)]
    [InlineData("1 2", 3)]        // too few
    [InlineData("1 2 x", 3)]      // bad token → whole parse fails (no zero-fill)
    [InlineData("1;2;3", 3)]      // unsupported separator
    public void Rejects_Invalid_Input(string? s, int min)
    {
        Assert.False(VecParse.TryParseFloats(s, min, out float[] vals));
        Assert.Empty(vals);
    }
}
